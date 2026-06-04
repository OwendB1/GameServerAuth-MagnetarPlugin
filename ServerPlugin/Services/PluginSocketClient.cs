using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Contracts.Plugin;
using Shared.Logging;
using Shared.Plugin;

namespace ServerPlugin.Services;

public sealed class PluginSocketClient : IDisposable
{
    private static readonly Uri ServiceUri = new Uri("wss://auth.odb-tech.com/ws/plugin");
    private const int MaxIncomingFrameBytes = 1024 * 1024;
    private static readonly TimeSpan InitialAuthRetryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReconnectRetryInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LogFlushInterval = TimeSpan.FromSeconds(2);

    private readonly IPluginLogger _log;
    private readonly Func<PluginHeartbeatPayload> _heartbeatFactory;
    private readonly AuthorizationRuntimeState _runtimeState;
    private readonly ConcurrentQueue<PluginLogEntryPayload> _pendingLogEntries = new();
    private readonly object _lifecycleSync = new();

    private CancellationTokenSource _cancellationTokenSource;
    private Task _workerTask;
    private ClientWebSocket _activeSocket;
    private bool _hasCompletedInitialAuth;
    private int _initialAuthAttempt;

    public bool IsConnected { get; private set; }

    public string LastError { get; private set; }

    public DateTimeOffset? LastSocketConnectUtc { get; private set; }

    public DateTimeOffset? LastHelloAcceptedUtc { get; private set; }

    public DateTimeOffset? LastFrameReceivedUtc { get; private set; }

    public DateTimeOffset? LastConfigurationUtc { get; private set; }

    public string CurrentPluginVersion { get; private set; }

    public PluginSocketClient(
        IPluginLogger log,
        AuthorizationRuntimeState runtimeState,
        Func<PluginHeartbeatPayload> heartbeatFactory)
    {
        this._log = log;
        this._runtimeState = runtimeState;
        this._heartbeatFactory = heartbeatFactory;
        CurrentPluginVersion = PluginVersionResolver.GetVersion();
    }

    public void Start()
    {
        lock (_lifecycleSync)
        {
            if (_workerTask != null)
            {
                return;
            }

            LastError = null;
            _cancellationTokenSource = new CancellationTokenSource();
            _workerTask = Task.Run(() => RunAsync(_cancellationTokenSource.Token));
        }
    }

    public void Stop()
    {
        CancellationTokenSource tokenSource;
        Task task;
        var stopped = false;

        lock (_lifecycleSync)
        {
            tokenSource = _cancellationTokenSource;
            task = _workerTask;
        }

        if (tokenSource == null)
        {
            return;
        }

        tokenSource.Cancel();
        AbortActiveSocket();
        try
        {
            stopped = task == null || task.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Ignore cancellation/transport errors during shutdown.
            stopped = true;
        }
        finally
        {
            lock (_lifecycleSync)
            {
                if (stopped && ReferenceEquals(_cancellationTokenSource, tokenSource))
                {
                    _cancellationTokenSource.Dispose();
                    _cancellationTokenSource = null;
                }

                if (stopped && ReferenceEquals(_workerTask, task))
                {
                    _workerTask = null;
                }

                if (stopped)
                {
                    _activeSocket = null;
                }
            }

            IsConnected = false;
            LastError = null;
            ClearPendingLogs();

            if (!stopped)
            {
                _log.Warning("Timed out waiting for auth socket worker to stop.");
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }

    public void RequestConfiguration()
    {
        // Receive loop requests config on next reconnect already.
        // Method kept for command/UI symmetry.
    }

    public void QueueLog(string logLevel, string message)
    {
        if (string.IsNullOrWhiteSpace(message) || !_runtimeState.IsPluginLogForwardingEnabled || !IsConnected)
        {
            return;
        }

        _pendingLogEntries.Enqueue(new PluginLogEntryPayload
        {
            OccurredAtUnixTimeMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            LogLevel = string.IsNullOrWhiteSpace(logLevel) ? "Info" : logLevel.Trim(),
            Message = message.Trim()
        });
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var isInitialAuthPhase = !_hasCompletedInitialAuth;
            try
            {
                var hello = BuildHelloPayload();
                if (hello == null)
                {
                    if (isInitialAuthPhase)
                    {
                        _initialAuthAttempt++;
                    }

                    LogRetry("Plugin auth handshake could not be built", LastError ?? "unknown reason", isInitialAuthPhase);
                    await DelayBeforeRetryAsync(isInitialAuthPhase, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (isInitialAuthPhase)
                {
                    _initialAuthAttempt++;
                    _log.Info(
                        $"Starting initial plugin auth attempt {_initialAuthAttempt} for instance {hello.ServerId} in cluster {hello.ClusterId}.");
                }

                using (var socket = new ClientWebSocket())
                {
                    SetActiveSocket(socket);
                    try
                    {
                        ApplyHandshakeHeaders(socket, hello);
                        await socket.ConnectAsync(ServiceUri, cancellationToken).ConfigureAwait(false);
                        _hasCompletedInitialAuth = true;
                        _initialAuthAttempt = 0;
                        IsConnected = true;
                        LastError = null;
                        LastSocketConnectUtc = DateTimeOffset.UtcNow;
                        _log.Info("Authenticated socket connected to auth.odb-tech.com");

                        using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            var heartbeatTask = HeartbeatLoopAsync(socket, linkedCts.Token);
                            try
                            {
                                await ReceiveLoopAsync(socket, linkedCts.Token).ConfigureAwait(false);
                            }
                            finally
                            {
                                linkedCts.Cancel();
                                try
                                {
                                    await heartbeatTask.ConfigureAwait(false);
                                }
                                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                                {
                                }
                            }
                        }
                    }
                    finally
                    {
                        ClearActiveSocket(socket);
                    }
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    LastError = "Auth socket closed.";
                    _log.Warning($"Auth socket closed. Retrying in {(int)ReconnectRetryInterval.TotalSeconds} seconds.");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                LastError = exception.Message;
                LogRetry("Auth socket error", exception.Message, isInitialAuthPhase);
            }
            finally
            {
                IsConnected = false;
                ClearPendingLogs();
            }

            await DelayBeforeRetryAsync(!_hasCompletedInitialAuth, cancellationToken).ConfigureAwait(false);
        }
    }

    private PluginHelloPayload BuildHelloPayload()
    {
        var config = Common.Config;
        ulong guildId;
        if (!ulong.TryParse(config.DiscordGuildId?.Trim(), out guildId) || guildId == 0)
        {
            LastError = "DiscordGuildId missing or invalid.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(config.ServerId) || string.IsNullOrWhiteSpace(config.ClusterId))
        {
            LastError = "ServerId or ClusterId missing.";
            return null;
        }

        var clusterSecret = config.ClusterSecret?.Trim();
        if (string.IsNullOrWhiteSpace(clusterSecret))
        {
            LastError = "ClusterSecret missing.";
            return null;
        }

        var pluginVersion = PluginVersionResolver.GetVersion();

        var hello = new PluginHelloPayload
        {
            ServerId = config.ServerId.Trim(),
            DiscordGuildId = guildId,
            ClusterId = config.ClusterId.Trim(),
            NodeName = string.IsNullOrWhiteSpace(config.NodeName) ? Environment.MachineName : config.NodeName.Trim(),
            NodeRole = config.NodeRole,
            PluginVersion = pluginVersion,
            GameVersion = Common.GameVersion,
            NexusServerId = Plugin.Instance?.NexusClusterRedirectService?.GetCurrentServerId(),
            IssuedAtUnixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Nonce = Guid.NewGuid().ToString("N")
        };

        CurrentPluginVersion = pluginVersion;
        hello.Signature = PluginRequestSigning.CreateHelloSignature(clusterSecret, hello);
        return hello;
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var frame = await ReceiveFrameAsync(socket, buffer, cancellationToken).ConfigureAwait(false);
            if (frame == null)
            {
                return;
            }

            LastFrameReceivedUtc = DateTimeOffset.UtcNow;
            LastError = null;

            switch (frame.Type)
            {
                case PluginSocketMessageTypes.HelloAccepted:
                    IsConnected = true;
                    LastHelloAcceptedUtc = LastFrameReceivedUtc;
                    _log.Info("Auth hello accepted");
                    break;

                case PluginSocketMessageTypes.Configuration:
                    if (frame.Configuration != null)
                    {
                        if (!IsConnected)
                        {
                            IsConnected = true;
                            _log.Info("Auth session confirmed by configuration frame.");
                        }

                        if (!string.IsNullOrWhiteSpace(frame.Configuration.ClusterSecret) &&
                            !string.Equals(Common.Config.ClusterSecret, frame.Configuration.ClusterSecret, StringComparison.Ordinal))
                        {
                            Common.Config.ClusterSecret = frame.Configuration.ClusterSecret;
                            _log.Info("Updated plugin cluster secret from server configuration.");
                        }

                        _runtimeState.ApplyConfiguration(frame.Configuration);
                        if (!frame.Configuration.EnablePluginLogForwarding)
                        {
                            ClearPendingLogs();
                        }

                        LastConfigurationUtc = DateTimeOffset.UtcNow;
                        var authorizedPlayers = frame.Configuration.Players.Count(x => x.IsAuthorized);
                        var reservedPlayers = frame.Configuration.Players.Count(x => x.HasReservedSlot);
                        _log.Info(
                            $"Received config for {frame.Configuration.ServerId}: players={frame.Configuration.Players.Count}, authorized={authorizedPlayers}, reserved={reservedPlayers}, mappings={frame.Configuration.RoleMappings.Count}, plugin_logs={(frame.Configuration.EnablePluginLogForwarding ? "on" : "off")}, log_retention_h={frame.Configuration.PluginLogRetentionHours}");
                    }
                    break;
            }
        }
    }

    private async Task HeartbeatLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var nextHeartbeatUtc = DateTimeOffset.UtcNow.Add(HeartbeatInterval);
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            await FlushPendingLogsAsync(socket, cancellationToken).ConfigureAwait(false);

            if (DateTimeOffset.UtcNow >= nextHeartbeatUtc)
            {
                var payload = _heartbeatFactory();
                await SendFrameAsync(socket, new PluginSocketFrame
                {
                    Type = PluginSocketMessageTypes.Heartbeat,
                    Heartbeat = payload
                }, cancellationToken).ConfigureAwait(false);
                nextHeartbeatUtc = DateTimeOffset.UtcNow.Add(HeartbeatInterval);
            }

            await Task.Delay(LogFlushInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ApplyHandshakeHeaders(ClientWebSocket socket, PluginHelloPayload hello)
    {
        SetHandshakeHeader(socket, PluginSocketHandshakeHeaders.ServerId, hello.ServerId);
        SetHandshakeHeader(socket, PluginSocketHandshakeHeaders.DiscordGuildId, hello.DiscordGuildId.ToString());
        SetHandshakeHeader(socket, PluginSocketHandshakeHeaders.ClusterId, hello.ClusterId);
        SetHandshakeHeader(socket, PluginSocketHandshakeHeaders.NodeName, hello.NodeName);
        SetHandshakeHeader(socket, PluginSocketHandshakeHeaders.NodeRole, ((int)hello.NodeRole).ToString());
        SetHandshakeHeader(socket, PluginSocketHandshakeHeaders.PluginVersion, hello.PluginVersion);
        SetHandshakeHeader(socket, PluginSocketHandshakeHeaders.GameVersion, hello.GameVersion);
        SetHandshakeHeader(socket, PluginSocketHandshakeHeaders.NexusServerId, hello.NexusServerId);
        SetHandshakeHeader(socket, PluginSocketHandshakeHeaders.IssuedAtUnixTimeSeconds, hello.IssuedAtUnixTimeSeconds.ToString());
        SetHandshakeHeader(socket, PluginSocketHandshakeHeaders.Nonce, hello.Nonce);
        SetHandshakeHeader(socket, PluginSocketHandshakeHeaders.Signature, hello.Signature);
    }

    private static void SetHandshakeHeader(ClientWebSocket socket, string name, string value)
    {
        socket.Options.SetRequestHeader(name, Uri.EscapeDataString(value ?? string.Empty));
    }

    private async Task DelayBeforeRetryAsync(bool isInitialAuthPhase, CancellationToken cancellationToken)
    {
        var retryDelay = isInitialAuthPhase ? InitialAuthRetryInterval : ReconnectRetryInterval;
        await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
    }

    private void LogRetry(string prefix, string reason, bool isInitialAuthPhase)
    {
        var retryDelay = isInitialAuthPhase ? InitialAuthRetryInterval : ReconnectRetryInterval;
        if (isInitialAuthPhase)
        {
            _log.Warning(
                $"{prefix}: {reason}. Initial auth will retry in {(int)retryDelay.TotalSeconds} seconds.");
            return;
        }

        _log.Warning(
            $"{prefix}: {reason}. Reconnect will retry in {(int)retryDelay.TotalSeconds} seconds.");
    }

    private async Task FlushPendingLogsAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        if (!_runtimeState.IsPluginLogForwardingEnabled)
        {
            ClearPendingLogs();
            return;
        }

        while (socket.State == WebSocketState.Open &&
               !cancellationToken.IsCancellationRequested &&
               _pendingLogEntries.TryDequeue(out var logEntry))
        {
            await SendFrameAsync(socket, new PluginSocketFrame
            {
                Type = PluginSocketMessageTypes.LogEntry,
                LogEntry = logEntry
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ClearPendingLogs()
    {
        while (_pendingLogEntries.TryDequeue(out _))
        {
        }
    }

    private void SetActiveSocket(ClientWebSocket socket)
    {
        lock (_lifecycleSync)
        {
            _activeSocket = socket;
        }
    }

    private void ClearActiveSocket(ClientWebSocket socket)
    {
        lock (_lifecycleSync)
        {
            if (ReferenceEquals(_activeSocket, socket))
            {
                _activeSocket = null;
            }
        }
    }

    private void AbortActiveSocket()
    {
        ClientWebSocket socket;
        lock (_lifecycleSync)
        {
            socket = _activeSocket;
        }

        if (socket == null)
        {
            return;
        }

        try
        {
            socket.Abort();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            socket.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task<PluginSocketFrame> ReceiveFrameAsync(ClientWebSocket socket, byte[] buffer, CancellationToken cancellationToken)
    {
        using (var stream = new MemoryStream())
        {
            var totalBytes = 0;
            while (true)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new InvalidOperationException("Auth service sent a non-text websocket frame.");
                }

                totalBytes += result.Count;
                if (totalBytes > MaxIncomingFrameBytes)
                {
                    throw new InvalidOperationException("Auth service frame exceeded configured size limit.");
                }

                stream.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    break;
                }
            }

            stream.Position = 0;
            var serializer = new DataContractJsonSerializer(typeof(PluginSocketFrame));
            return serializer.ReadObject(stream) as PluginSocketFrame;
        }
    }

    private static async Task SendFrameAsync(ClientWebSocket socket, PluginSocketFrame frame, CancellationToken cancellationToken)
    {
        using (var stream = new MemoryStream())
        {
            var serializer = new DataContractJsonSerializer(typeof(PluginSocketFrame));
            serializer.WriteObject(stream, frame);
            var payload = stream.ToArray();
            await socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
    }
}
