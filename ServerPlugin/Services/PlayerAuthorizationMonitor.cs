using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Contracts.Plugin;
using Sandbox;
using Sandbox.Game;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Shared.Logging;
using VRage.Game.ModAPI;

namespace ServerPlugin.Services;

public sealed class PlayerAuthorizationMonitor : IDisposable
{
    private static readonly TimeSpan MinimumAcceptSuppression = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumAcceptSuppression = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan PendingPlayerResolutionTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan JoinActivityDeduplicationWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan OfflinePlayerPruneDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan VisualScriptResolutionDelay = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan AccessStatusCacheDuration = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan AccessStatusPromptDelay = TimeSpan.FromSeconds(2);

    private readonly IPluginLogger _log;
    private readonly AuthorizationRuntimeState _runtimeState;
    private readonly NexusClusterRedirectService _nexusClusterRedirectService;
    private readonly Func<bool> _isServiceConnected;
    private readonly ConcurrentDictionary<long, DateTimeOffset> _pendingPlayers = new ConcurrentDictionary<long, DateTimeOffset>();
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _detectedPlayers = new ConcurrentDictionary<ulong, DateTimeOffset>();
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _readyPlayers = new ConcurrentDictionary<ulong, DateTimeOffset>();
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _promptedPlayers = new ConcurrentDictionary<ulong, DateTimeOffset>();
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _acceptSuppressionExpirations = new ConcurrentDictionary<ulong, DateTimeOffset>();
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _lastJoinActivityReports = new ConcurrentDictionary<ulong, DateTimeOffset>();
    private readonly ConcurrentDictionary<ulong, string> _knownPlayerNames = new ConcurrentDictionary<ulong, string>();
    private readonly ConcurrentDictionary<ulong, byte> _managedReservedPlayers = new ConcurrentDictionary<ulong, byte>();
    private readonly ConcurrentDictionary<ulong, CachedPlayerAccessStatus> _cachedAccessStatuses = new ConcurrentDictionary<ulong, CachedPlayerAccessStatus>();
    private readonly ConcurrentDictionary<ulong, byte> _accessStatusRefreshInFlight = new ConcurrentDictionary<ulong, byte>();

    private long _lastSweepTick;
    private int _lastConfigurationRevision = -1;
    private bool _hasObservedServiceConnectionState;
    private bool _lastObservedServiceConnected;
    private DateTimeOffset? _serviceDisconnectedSinceUtc;

    public int OnlinePlayerCount => _detectedPlayers.Count;
    public int ReadyPlayerCount => _readyPlayers.Count;
    public int PendingPlayerCount => _pendingPlayers.Count;

    public PlayerAuthorizationMonitor(
        IPluginLogger log,
        AuthorizationRuntimeState runtimeState,
        NexusClusterRedirectService nexusClusterRedirectService,
        Func<bool> isServiceConnected)
    {
        this._log = log ?? throw new ArgumentNullException(nameof(log));
        this._runtimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
        this._nexusClusterRedirectService = nexusClusterRedirectService ?? throw new ArgumentNullException(nameof(nexusClusterRedirectService));
        this._isServiceConnected = isServiceConnected ?? throw new ArgumentNullException(nameof(isServiceConnected));

        MyVisualScriptLogicProvider.PlayerConnected += OnPlayerConnected;
        MyVisualScriptLogicProvider.PlayerDisconnected += OnPlayerDisconnected;
    }

    public void Dispose()
    {
        MyVisualScriptLogicProvider.PlayerConnected -= OnPlayerConnected;
        MyVisualScriptLogicProvider.PlayerDisconnected -= OnPlayerDisconnected;
        _pendingPlayers.Clear();
        _detectedPlayers.Clear();
        _readyPlayers.Clear();
        _promptedPlayers.Clear();
        _acceptSuppressionExpirations.Clear();
        _lastJoinActivityReports.Clear();
        _knownPlayerNames.Clear();
        _managedReservedPlayers.Clear();
        _cachedAccessStatuses.Clear();
        _accessStatusRefreshInFlight.Clear();
    }

    public void Update(long tick)
    {
        if (tick - _lastSweepTick < 60)
        {
            return;
        }

        _lastSweepTick = tick;
        ApplyConfigurationRevision();

        var players = GetCurrentPlayers();
        PruneOfflinePlayers(players);
        ResolvePendingPlayers(players);
        DiscoverReadyPlayers(players);
        UpdateServiceConnectionState();

        foreach (var steamId in _readyPlayers.Keys)
        {
            EvaluatePlayer(steamId);
        }
    }

    public void SuppressEnforcementAfterAccept(ulong steamId)
    {
        var suppressionDuration = GetAcceptSuppressionDuration();
        var suppressionExpiresUtc = DateTimeOffset.UtcNow.Add(suppressionDuration);
        _acceptSuppressionExpirations[steamId] = suppressionExpiresUtc;
        _promptedPlayers.TryRemove(steamId, out _);
        _log.Info($"Temporarily suppressing unauthorized enforcement for {steamId} until {suppressionExpiresUtc:O} after accept command");
    }

    private void OnPlayerConnected(long identityId)
    {
        if (TryIgnoreNonHumanVisualScriptIdentity(identityId, "connect"))
        {
            return;
        }

        _pendingPlayers[identityId] = DateTimeOffset.UtcNow;
        _log.Info($"Visual-script connect detected. Pending identity={identityId}");
        _ = ResolveConnectedPlayerAsync(identityId);
    }

    private void OnPlayerDisconnected(long identityId)
    {
        if (!_pendingPlayers.ContainsKey(identityId))
        {
            return;
        }

        if (TryIgnoreNonHumanVisualScriptIdentity(identityId, "disconnect"))
        {
            return;
        }

        _pendingPlayers.TryRemove(identityId, out _);
        _log.Info($"Visual-script disconnect detected. Identity={identityId}");
    }

    private async Task ResolveConnectedPlayerAsync(long identityId)
    {
        try
        {
            await Task.Delay(VisualScriptResolutionDelay).ConfigureAwait(false);
            DateTimeOffset pendingSinceUtc;
            if (!_pendingPlayers.TryGetValue(identityId, out pendingSinceUtc))
            {
                return;
            }

            if (TryIgnoreNonHumanVisualScriptIdentity(identityId, "delayed-resolve"))
            {
                return;
            }

            var players = GetCurrentPlayers();
            if (players == null)
            {
                _log.Info($"Delayed visual-script resolve skipped for identity {identityId}: player list unavailable.");
                return;
            }

            foreach (var candidate in players)
            {
                if (candidate?.IdentityId != identityId || !IsHumanPlayer(candidate))
                {
                    continue;
                }

                _pendingPlayers.TryRemove(identityId, out _);
                RegisterReadyPlayer(candidate.SteamUserId, candidate.DisplayName, pendingSinceUtc, "visual-script-delay");
                return;
            }

            _log.Info($"Delayed visual-script resolve did not find identity {identityId}; sweep fallback remains active.");
        }
        catch (Exception exception)
        {
            _log.Warning(exception, $"Delayed visual-script resolve failed for identity {identityId}.");
        }
    }

    private void ApplyConfigurationRevision()
    {
        var revision = _runtimeState.ConfigurationRevision;
        if (revision == _lastConfigurationRevision)
        {
            return;
        }

        _lastConfigurationRevision = revision;
        var configuration = _runtimeState.Configuration;
        if (configuration == null)
        {
            _log.Warning("Auth configuration not loaded yet. Promote, reserved-slot sync, and enforcement checks remain deferred.");
            return;
        }

        var authorizedPlayers = 0;
        var reservedPlayers = 0;
        foreach (var player in configuration.Players)
        {
            if (player.IsAuthorized)
            {
                authorizedPlayers++;
            }

            if (player.HasReservedSlot)
            {
                reservedPlayers++;
            }
        }

        _log.Info(
            $"Applying auth configuration revision {revision}: players={configuration.Players.Count}, authorized={authorizedPlayers}, reserved={reservedPlayers}");

        SyncDedicatedReservedPlayers(configuration);

        // Join/connect notifications can arrive before the game has fully finalized
        // player creation. Restrict promote changes to the ready set to avoid touching
        // player state during the new-player request path.
        foreach (var steamId in _readyPlayers.Keys)
        {
            ApplyImmediatePlayerState(steamId, "config-revision");
        }
    }

    private void EvaluatePlayer(ulong steamId)
    {
        if (!IsServiceConnected())
        {
            EvaluatePlayerWhileServiceDisconnected(steamId);
            return;
        }

        var configuration = _runtimeState.Configuration;
        if (configuration == null)
        {
            return;
        }

        AuthorizedPlayerDefinition player;
        if (_runtimeState.TryGetPlayer(steamId, out player) && player.IsAuthorized)
        {
            _promptedPlayers.TryRemove(steamId, out _);
            _acceptSuppressionExpirations.TryRemove(steamId, out _);
            EnsurePromoteLevel(steamId, MapPromoteLevel(player.AuthorizationLevel));
            return;
        }

        if (IsAcceptSuppressionActive(steamId))
        {
            _promptedPlayers.TryRemove(steamId, out _);
            return;
        }

        DateTimeOffset firstReadyUtc;
        if (!_readyPlayers.TryGetValue(steamId, out firstReadyUtc))
        {
            return;
        }

        var cachedAccessStatus = GetCachedAccessStatus(steamId);
        if (cachedAccessStatus == null)
        {
            EnsureAccessStatusRefresh(steamId);
            if (DateTimeOffset.UtcNow - firstReadyUtc < AccessStatusPromptDelay)
            {
                return;
            }
        }

        EnsurePromoteLevel(steamId, MyPromoteLevel.None);
        PromptUnauthorizedPlayer(steamId, configuration, cachedAccessStatus);

        if (DateTimeOffset.UtcNow - firstReadyUtc < TimeSpan.FromSeconds(Math.Max(5, configuration.AuthorizationGraceSeconds)))
        {
            return;
        }

        var redirectEnabled = configuration.RedirectUnauthorizedToLobby && !IsLobbyNode(configuration);
        var currentNexusServerId = _nexusClusterRedirectService.GetCurrentServerId();
        var lobbyNexusServerId = _nexusClusterRedirectService.GetRedirectTargetServerId(configuration.LobbyServerId);
        if (redirectEnabled &&
            !string.IsNullOrWhiteSpace(lobbyNexusServerId) &&
            !string.Equals(currentNexusServerId, lobbyNexusServerId, StringComparison.Ordinal))
        {
            string redirectFailureReason;
            if (_nexusClusterRedirectService.TryRedirectPlayer(steamId, lobbyNexusServerId, out redirectFailureReason))
            {
                _log.Info($"Redirected unauthorized player {steamId} to Nexus server {lobbyNexusServerId}");
                _detectedPlayers.TryRemove(steamId, out _);
                _readyPlayers.TryRemove(steamId, out _);
                _promptedPlayers.TryRemove(steamId, out _);
                _cachedAccessStatuses.TryRemove(steamId, out _);
                _accessStatusRefreshInFlight.TryRemove(steamId, out _);
                return;
            }

            _log.Warning($"Failed to redirect unauthorized player {steamId} to lobby {lobbyNexusServerId}: {redirectFailureReason}");
        }

        if (!ShouldKickUnauthorizedPlayers(configuration))
        {
            return;
        }

        _log.Info($"Kicking unauthorized player {steamId}");
        KickPlayer(steamId);
        _ = Plugin.Instance.PlayerAuthorizationClient?.ReportKickAsync(steamId, ResolvePlayerName(steamId), CancellationToken.None);
        _detectedPlayers.TryRemove(steamId, out _);
        _readyPlayers.TryRemove(steamId, out _);
        _promptedPlayers.TryRemove(steamId, out _);
        _cachedAccessStatuses.TryRemove(steamId, out _);
        _accessStatusRefreshInFlight.TryRemove(steamId, out _);
    }

    private void EvaluatePlayerWhileServiceDisconnected(ulong steamId)
    {
        EnsureDisconnectedSnapshotInitialized();
        if (IsExistingPlayerFromBeforeServiceOutage(steamId))
        {
            _promptedPlayers.TryRemove(steamId, out _);
            return;
        }

        EnsurePromoteLevel(steamId, MyPromoteLevel.None);
        PromptServiceUnavailablePlayer(steamId);

        DateTimeOffset firstReadyUtc;
        if (!_readyPlayers.TryGetValue(steamId, out firstReadyUtc))
        {
            return;
        }

        var graceSeconds = Math.Max(5, _runtimeState.Configuration?.AuthorizationGraceSeconds ?? 5);
        if (DateTimeOffset.UtcNow - firstReadyUtc < TimeSpan.FromSeconds(graceSeconds))
        {
            return;
        }

        if (IsLobbyNode(_runtimeState.Configuration))
        {
            _log.Info($"Leaving player {steamId} connected because this node is configured as Lobby while auth service is disconnected.");
            return;
        }

        _log.Info($"Kicking player {steamId} because auth service is disconnected and the player joined after the outage started.");
        KickPlayer(steamId);
        _detectedPlayers.TryRemove(steamId, out _);
        _readyPlayers.TryRemove(steamId, out _);
        _promptedPlayers.TryRemove(steamId, out _);
        _acceptSuppressionExpirations.TryRemove(steamId, out _);
        _lastJoinActivityReports.TryRemove(steamId, out _);
        _cachedAccessStatuses.TryRemove(steamId, out _);
        _accessStatusRefreshInFlight.TryRemove(steamId, out _);
    }

    private List<IMyPlayer> GetCurrentPlayers()
    {
        if (MyAPIGateway.Multiplayer?.Players == null)
        {
            return null;
        }

        var players = new List<IMyPlayer>();
        MyAPIGateway.Multiplayer.Players.GetPlayers(players);
        return players;
    }

    private void ResolvePendingPlayers(List<IMyPlayer> players)
    {
        if (_pendingPlayers.IsEmpty || players == null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var pendingPlayer in _pendingPlayers)
        {
            if (TryIgnoreNonHumanVisualScriptIdentity(pendingPlayer.Key, "sweep-resolve"))
            {
                continue;
            }

            if (now - pendingPlayer.Value > PendingPlayerResolutionTimeout)
            {
                _pendingPlayers.TryRemove(pendingPlayer.Key, out _);
                _log.Warning($"Dropping stale pending auth identity {pendingPlayer.Key}");
                continue;
            }

            IMyPlayer resolvedPlayer = null;
            foreach (var candidate in players)
            {
                if (candidate?.IdentityId == pendingPlayer.Key && IsHumanPlayer(candidate))
                {
                    resolvedPlayer = candidate;
                    break;
                }
            }

            if (resolvedPlayer == null)
            {
                continue;
            }

            _pendingPlayers.TryRemove(pendingPlayer.Key, out _);
            RegisterReadyPlayer(resolvedPlayer.SteamUserId, resolvedPlayer.DisplayName, now, "visual-script");
        }
    }

    private void PruneOfflinePlayers(List<IMyPlayer> players)
    {
        if (players == null)
        {
            return;
        }

        var onlineSteamIds = new HashSet<ulong>();
        foreach (var player in players)
        {
            if (IsHumanPlayer(player))
            {
                onlineSteamIds.Add(player.SteamUserId);
            }
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var trackedPlayer in _detectedPlayers)
        {
            if (onlineSteamIds.Contains(trackedPlayer.Key) || now - trackedPlayer.Value < OfflinePlayerPruneDelay)
            {
                continue;
            }

            if (_detectedPlayers.TryRemove(trackedPlayer.Key, out _))
            {
                _readyPlayers.TryRemove(trackedPlayer.Key, out _);
                _promptedPlayers.TryRemove(trackedPlayer.Key, out _);
                _acceptSuppressionExpirations.TryRemove(trackedPlayer.Key, out _);
                _lastJoinActivityReports.TryRemove(trackedPlayer.Key, out _);
                _cachedAccessStatuses.TryRemove(trackedPlayer.Key, out _);
                _accessStatusRefreshInFlight.TryRemove(trackedPlayer.Key, out _);
                _log.Info($"Pruned stale tracked player {trackedPlayer.Key} after missing from current player list.");
            }
        }
    }

    private void DiscoverReadyPlayers(List<IMyPlayer> players)
    {
        if (players == null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var player in players)
        {
            if (!IsHumanPlayer(player))
            {
                continue;
            }

            RegisterReadyPlayer(player.SteamUserId, player.DisplayName, now, "player-sweep");
        }
    }

    private void RegisterDetectedPlayer(ulong steamId, string displayName, DateTimeOffset now, string source)
    {
        RememberPlayerName(steamId, displayName);
        if (!_detectedPlayers.TryAdd(steamId, now))
        {
            return;
        }

        _promptedPlayers.TryRemove(steamId, out _);
        _acceptSuppressionExpirations.TryRemove(steamId, out _);
        _log.Info($"Player detected via {source}: {DescribePlayer(displayName, steamId)}");
        TryReportJoinActivity(steamId, displayName, source);
        EnsureAccessStatusRefresh(steamId, displayName);
    }

    private void RegisterReadyPlayer(ulong steamId, string displayName, DateTimeOffset now, string source)
    {
        RegisterDetectedPlayer(steamId, displayName, now, source);
        if (!_readyPlayers.TryAdd(steamId, now))
        {
            return;
        }

        _log.Info($"Player ready for full auth enforcement via {source}: {DescribePlayer(displayName, steamId)}");

        if (_runtimeState.Configuration == null && IsServiceConnected())
        {
            if (Plugin.Instance.TrySendPlayerMessage(
                    steamId,
                    "Cluster access uses GameServerAuth. Run !accept if your accounts are linked, or !gsa authorize if they are not.",
                    "Run !accept if linked, or !gsa authorize if not."))
            {
                _log.Info($"Sent fallback access hint to {DescribePlayer(displayName, steamId)} because auth configuration is not loaded yet.");
            }

            return;
        }

        EvaluatePlayer(steamId);
    }

    private void ApplyImmediatePlayerState(ulong steamId, string source)
    {
        var configuration = _runtimeState.Configuration;
        if (configuration == null)
        {
            _log.Info($"Deferred auth access check via {source} for {steamId}: configuration not loaded yet.");
            return;
        }

        AuthorizedPlayerDefinition player;
        if (_runtimeState.TryGetPlayer(steamId, out player) && player.IsAuthorized)
        {
            var targetLevel = MapPromoteLevel(player.AuthorizationLevel);
            _log.Info(
                $"Authorized player via {source}: {DescribePlayer(player.DisplayName, steamId)} promote={targetLevel} reserved={player.HasReservedSlot}");
            EnsurePromoteLevel(steamId, targetLevel);
            return;
        }

        _log.Info($"Unauthorized or unknown player via {source}: {steamId}. Promote target=None");
        EnsurePromoteLevel(steamId, MyPromoteLevel.None);
    }

    private void SyncDedicatedReservedPlayers(PluginServerConfiguration configuration)
    {
        var dedicatedConfig = MySandboxGame.ConfigDedicated;
        if (dedicatedConfig == null)
        {
            _log.Warning("Dedicated config unavailable. Skipping reserved-slot sync.");
            return;
        }

        var desiredReservedPlayers = new Dictionary<ulong, AuthorizedPlayerDefinition>();
        foreach (var player in configuration.Players)
        {
            if (dedicatedConfig.Reserved.Contains(player.SteamId))
            {
                _managedReservedPlayers[player.SteamId] = 0;
            }

            if (!ShouldBeDedicatedReserved(player))
            {
                continue;
            }

            desiredReservedPlayers[player.SteamId] = player;
        }

        var changed = false;
        foreach (var desiredPlayer in desiredReservedPlayers)
        {
            if (dedicatedConfig.Reserved.Contains(desiredPlayer.Key))
            {
                continue;
            }

            dedicatedConfig.Reserved.Add(desiredPlayer.Key);
            _managedReservedPlayers[desiredPlayer.Key] = 0;
            changed = true;
            _log.Info(
                $"Added {DescribePlayer(desiredPlayer.Value.DisplayName, desiredPlayer.Key)} to dedicated Reserved list. reserved={desiredPlayer.Value.HasReservedSlot}");
        }

        foreach (var managedPlayer in _managedReservedPlayers.Keys)
        {
            if (desiredReservedPlayers.ContainsKey(managedPlayer))
            {
                continue;
            }

            if (dedicatedConfig.Reserved.Remove(managedPlayer))
            {
                changed = true;
                _log.Info($"Removed {managedPlayer} from dedicated Reserved list.");
            }

            _managedReservedPlayers.TryRemove(managedPlayer, out _);
        }

        if (!changed)
        {
            return;
        }

        try
        {
            dedicatedConfig.Save();
            _log.Info($"Saved dedicated config after reserved-slot sync. reserved_entries={dedicatedConfig.Reserved.Count}");
        }
        catch (Exception exception)
        {
            _log.Warning(exception, "Failed to save dedicated config after reserved-slot sync.");
        }
    }

    private void TryReportJoinActivity(ulong steamId, string displayName, string source)
    {
        var resolvedDisplayName = ResolvePlayerName(steamId, displayName);
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset lastReportUtc;
        if (_lastJoinActivityReports.TryGetValue(steamId, out lastReportUtc) &&
            now - lastReportUtc < JoinActivityDeduplicationWindow)
        {
            return;
        }

        _lastJoinActivityReports[steamId] = now;
        _log.Info($"Reporting player join activity via {source}: {DescribePlayer(resolvedDisplayName, steamId)}");
        _ = Plugin.Instance.PlayerAuthorizationClient?.ReportJoinAsync(steamId, resolvedDisplayName, CancellationToken.None);
    }

    private void RememberPlayerName(ulong steamId, string displayName)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            _knownPlayerNames[steamId] = displayName.Trim();
        }
    }

    private string ResolvePlayerName(ulong steamId, string fallbackDisplayName = null)
    {
        if (!string.IsNullOrWhiteSpace(fallbackDisplayName))
        {
            var resolvedFallback = fallbackDisplayName.Trim();
            _knownPlayerNames[steamId] = resolvedFallback;
            return resolvedFallback;
        }

        string knownDisplayName;
        return _knownPlayerNames.TryGetValue(steamId, out knownDisplayName) ? knownDisplayName : string.Empty;
    }

    private void EnsurePromoteLevel(ulong steamId, MyPromoteLevel targetLevel)
    {
        if (MySession.Static == null)
        {
            return;
        }

        var currentLevel = MySession.Static.GetUserPromoteLevel(steamId);

        // SE reserves MyPromoteLevel.Owner for the server owner and restores it; never demote them.
        if (currentLevel == MyPromoteLevel.Owner)
        {
            return;
        }

        // Owner cannot be granted to a normal player; never set it via the plugin.
        if (targetLevel == MyPromoteLevel.Owner)
        {
            targetLevel = MyPromoteLevel.Admin;
        }

        if (currentLevel == targetLevel)
        {
            return;
        }

        var originalLevel = currentLevel;
        MySession.Static.SetUserPromoteLevel(steamId, targetLevel);
        currentLevel = MySession.Static.GetUserPromoteLevel(steamId);

        if (currentLevel == targetLevel)
        {
            _log.Info($"Applied promote level for {steamId}: {originalLevel} -> {targetLevel}");
            return;
        }

        _log.Warning($"Failed to apply promote level for {steamId}: current={currentLevel}, target={targetLevel}");
    }

    private void UpdateServiceConnectionState()
    {
        var connected = _isServiceConnected();
        var now = DateTimeOffset.UtcNow;
        if (!_hasObservedServiceConnectionState)
        {
            _hasObservedServiceConnectionState = true;
            _lastObservedServiceConnected = connected;
            if (!connected)
            {
                _serviceDisconnectedSinceUtc = now;
                _log.Warning("Auth service is disconnected. Existing connected players will stay, and new joins will be refused until reconnect.");
            }

            return;
        }

        if (connected == _lastObservedServiceConnected)
        {
            return;
        }

        _lastObservedServiceConnected = connected;
        if (connected)
        {
            _serviceDisconnectedSinceUtc = null;
            _log.Info("Auth service reconnected. Standard authorization enforcement resumed.");
            return;
        }

        _serviceDisconnectedSinceUtc = now;
        _log.Warning("Auth service disconnected. Existing connected players will stay, and new joins will be refused until reconnect.");
    }

    private bool IsServiceConnected()
    {
        return _hasObservedServiceConnectionState
            ? _lastObservedServiceConnected
            : _isServiceConnected();
    }

    private bool IsExistingPlayerFromBeforeServiceOutage(ulong steamId)
    {
        DateTimeOffset detectedSinceUtc;
        return _serviceDisconnectedSinceUtc.HasValue &&
               _detectedPlayers.TryGetValue(steamId, out detectedSinceUtc) &&
               detectedSinceUtc <= _serviceDisconnectedSinceUtc.Value;
    }

    private void EnsureDisconnectedSnapshotInitialized()
    {
        if (_serviceDisconnectedSinceUtc.HasValue)
        {
            return;
        }

        _hasObservedServiceConnectionState = true;
        _lastObservedServiceConnected = false;
        _serviceDisconnectedSinceUtc = DateTimeOffset.UtcNow;
    }

    private static MyPromoteLevel MapPromoteLevel(GameAuthorizationLevel level)
    {
        switch (level)
        {
            case GameAuthorizationLevel.Scripter:
                return MyPromoteLevel.Scripter;
            case GameAuthorizationLevel.Moderator:
                return MyPromoteLevel.Moderator;
            case GameAuthorizationLevel.SpaceMaster:
                return MyPromoteLevel.SpaceMaster;
            case GameAuthorizationLevel.Admin:
            case GameAuthorizationLevel.Owner:
                return MyPromoteLevel.Admin;
            default:
                return MyPromoteLevel.None;
        }
    }

    private void PromptUnauthorizedPlayer(
        ulong steamId,
        PluginServerConfiguration configuration,
        CachedPlayerAccessStatus cachedAccessStatus)
    {
        var isLobbyNode = IsLobbyNode(configuration);
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset lastPromptUtc;
        if (_promptedPlayers.TryGetValue(steamId, out lastPromptUtc) &&
            (isLobbyNode || now - lastPromptUtc < TimeSpan.FromSeconds(30)))
        {
            return;
        }

        _promptedPlayers[steamId] = now;
        var graceSeconds = Math.Max(5, configuration.AuthorizationGraceSeconds);
        var redirectTarget = _nexusClusterRedirectService.GetRedirectTargetServerId(configuration.LobbyServerId);
        var actionText = configuration.RedirectUnauthorizedToLobby &&
                         !isLobbyNode &&
                         !string.IsNullOrWhiteSpace(redirectTarget)
            ? "you will be redirected to the lobby"
            : ShouldKickUnauthorizedPlayers(configuration)
                ? "you will be kicked"
                : "access will remain blocked";
        string message;
        string fallbackMessage;
        string logLabel;
        switch (cachedAccessStatus?.Status)
        {
            case PluginPlayerAuthorizationStatus.RequiresAccept:
            case PluginPlayerAuthorizationStatus.Authorized:
            case PluginPlayerAuthorizationStatus.AlreadyAuthorized:
                message = isLobbyNode
                    ? "This lobby requires authorization. Run !accept."
                    : $"This cluster requires authorization. Run !accept within {graceSeconds} seconds or {actionText}.";
                fallbackMessage = isLobbyNode
                    ? "This lobby requires authorization. Run !accept."
                    : "This cluster requires authorization. Run !accept.";
                logLabel = "!accept";
                break;

            case PluginPlayerAuthorizationStatus.RequiresLink:
                message = isLobbyNode
                    ? "This lobby requires account linking. Run !gsa authorize."
                    : $"This cluster requires account linking. Run !gsa authorize within {graceSeconds} seconds or {actionText}.";
                fallbackMessage = isLobbyNode
                    ? "This lobby requires account linking. Run !gsa authorize."
                    : "This cluster requires account linking. Run !gsa authorize.";
                logLabel = "!gsa authorize";
                break;

            case PluginPlayerAuthorizationStatus.RequiresGuildJoin:
                message = isLobbyNode
                    ? "Join owning Discord guild first, then run !accept."
                    : $"Join owning Discord guild first. Run !accept within {graceSeconds} seconds to open guild join flow or {actionText}.";
                fallbackMessage = isLobbyNode
                    ? "Join owning Discord guild first, then run !accept."
                    : "Join owning Discord guild first. Run !accept.";
                logLabel = "guild-join";
                break;

            case PluginPlayerAuthorizationStatus.RequiresRole:
                message = isLobbyNode
                    ? "Acquire one of the required Discord roles, then run !accept."
                    : $"Acquire one of the required Discord roles. Run !accept within {graceSeconds} seconds to open cluster instructions or {actionText}.";
                fallbackMessage = isLobbyNode
                    ? "Acquire one of the required Discord roles, then run !accept."
                    : "Acquire one of the required Discord roles. Run !accept.";
                logLabel = "required-role";
                break;

            case PluginPlayerAuthorizationStatus.AccessBlocked:
                var blockedMessage = string.IsNullOrWhiteSpace(cachedAccessStatus.Message)
                    ? (isLobbyNode
                        ? "Access to this lobby is blocked by the cluster owner."
                        : "Access to this cluster is blocked by the cluster owner.")
                    : cachedAccessStatus.Message.Trim();
                message = isLobbyNode
                    ? blockedMessage
                    : $"{blockedMessage} You cannot authorize here and {actionText}.";
                fallbackMessage = blockedMessage;
                logLabel = "blocked";
                break;

            default:
                message = isLobbyNode
                    ? "This lobby requires authorization. Run !accept if your accounts are linked, or !gsa authorize if they are not."
                    : $"This cluster requires authorization. Run !accept if your accounts are linked, or !gsa authorize within {graceSeconds} seconds if they are not, or {actionText}.";
                fallbackMessage = isLobbyNode
                    ? "This lobby requires authorization. Run !accept if linked, or !gsa authorize if not."
                    : "This cluster requires authorization. Run !accept if linked, or !gsa authorize if not.";
                logLabel = "mixed";
                break;
        }

        if (Plugin.Instance.TrySendPlayerMessage(steamId, message, fallbackMessage))
        {
            _log.Info($"Prompted unauthorized player {steamId} with {logLabel} instructions.");
        }
    }

    private CachedPlayerAccessStatus GetCachedAccessStatus(ulong steamId)
    {
        CachedPlayerAccessStatus cachedAccessStatus;
        if (!_cachedAccessStatuses.TryGetValue(steamId, out cachedAccessStatus))
        {
            return null;
        }

        if (DateTimeOffset.UtcNow - cachedAccessStatus.CheckedUtc <= AccessStatusCacheDuration)
        {
            return cachedAccessStatus;
        }

        _cachedAccessStatuses.TryRemove(steamId, out _);
        return null;
    }

    private void EnsureAccessStatusRefresh(ulong steamId, string displayName = null)
    {
        if (Plugin.Instance.PlayerAuthorizationClient == null || GetCachedAccessStatus(steamId) != null)
        {
            return;
        }

        if (!_accessStatusRefreshInFlight.TryAdd(steamId, 0))
        {
            return;
        }

        _ = RefreshAccessStatusAsync(steamId, ResolvePlayerName(steamId, displayName));
    }

    private async Task RefreshAccessStatusAsync(ulong steamId, string displayName)
    {
        try
        {
            var response = await Plugin.Instance.PlayerAuthorizationClient
                .GetAccessStatusAsync(steamId, displayName, CancellationToken.None)
                .ConfigureAwait(false);
            if (response == null)
            {
                return;
            }

            _cachedAccessStatuses[steamId] = new CachedPlayerAccessStatus(response.Status, response.Message, DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            _log.Warning(exception, $"Failed to refresh player access status for {steamId}.");
        }
        finally
        {
            _accessStatusRefreshInFlight.TryRemove(steamId, out _);
        }
    }

    private void PromptServiceUnavailablePlayer(ulong steamId)
    {
        var configuration = _runtimeState.Configuration;
        var isLobbyNode = IsLobbyNode(configuration);
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset lastPromptUtc;
        if (_promptedPlayers.TryGetValue(steamId, out lastPromptUtc) &&
            (isLobbyNode || now - lastPromptUtc < TimeSpan.FromSeconds(30)))
        {
            return;
        }

        _promptedPlayers[steamId] = now;
        var graceSeconds = Math.Max(5, configuration?.AuthorizationGraceSeconds ?? 5);
        var message = isLobbyNode
            ? "Cluster authorization service is currently unavailable. New joins are blocked until the service reconnects."
            : $"Cluster authorization service is currently unavailable. New joins are blocked. You will be removed in {graceSeconds} seconds unless the service reconnects.";
        var fallbackMessage = isLobbyNode
            ? "Cluster authorization service unavailable. New joins are blocked until it reconnects."
            : "Cluster authorization service unavailable. New joins are blocked.";
        if (Plugin.Instance.TrySendPlayerMessage(
                steamId,
                message,
                fallbackMessage))
        {
            _log.Info($"Prompted player {steamId} that auth service is unavailable and new joins are blocked.");
        }
    }

    private static bool IsLobbyNode(PluginServerConfiguration configuration)
    {
        return (configuration?.NodeRole ?? Plugin.Instance.Config.NodeRole) == ClusterNodeRole.Lobby;
    }

    private static bool ShouldKickUnauthorizedPlayers(PluginServerConfiguration configuration)
    {
        return configuration != null && configuration.KickUnauthorizedPlayers && !IsLobbyNode(configuration);
    }

    private bool IsAcceptSuppressionActive(ulong steamId)
    {
        DateTimeOffset suppressionExpiresUtc;
        if (!_acceptSuppressionExpirations.TryGetValue(steamId, out suppressionExpiresUtc))
        {
            return false;
        }

        if (suppressionExpiresUtc > DateTimeOffset.UtcNow)
        {
            return true;
        }

        _acceptSuppressionExpirations.TryRemove(steamId, out _);
        return false;
    }

    private bool TryIgnoreNonHumanVisualScriptIdentity(long identityId, string source)
    {
        if (identityId == 0)
        {
            return false;
        }

        var playerCollection = MySession.Static?.Players;
        var isNpcIdentity = playerCollection != null && playerCollection.IdentityIsNpc(identityId);
        IMyPlayer currentPlayer;
        if (!isNpcIdentity && (!TryGetCurrentPlayerByIdentity(identityId, out currentPlayer) || IsHumanPlayer(currentPlayer)))
        {
            return false;
        }

        _pendingPlayers.TryRemove(identityId, out _);

        if (_log.IsDebugEnabled)
        {
            var identity = playerCollection?.TryGetIdentity(identityId);
            var label = string.IsNullOrWhiteSpace(identity?.DisplayName)
                ? identityId.ToString()
                : $"{identity.DisplayName} ({identityId})";
            _log.Debug($"Ignoring visual-script {source} for non-human identity {label}");
        }

        return true;
    }

    private bool IsHumanPlayer(IMyPlayer player)
    {
        return player != null &&
               player.SteamUserId != 0 &&
               !player.IsBot &&
               !IsNpcIdentity(player.IdentityId);
    }

    private bool IsNpcIdentity(long identityId)
    {
        return identityId != 0 &&
               MySession.Static?.Players != null &&
               MySession.Static.Players.IdentityIsNpc(identityId);
    }

    private bool ShouldIgnoreSteamPlayer(ulong steamId, string source)
    {
        if (steamId == 0)
        {
            return true;
        }

        IMyPlayer currentPlayer;
        if (TryGetCurrentPlayerBySteamId(steamId, out currentPlayer))
        {
            if (IsHumanPlayer(currentPlayer))
            {
                return false;
            }

            if (_log.IsDebugEnabled)
            {
                _log.Debug($"Ignoring {source} for non-human SteamId {steamId}");
            }

            return true;
        }

        var identityId = MyAPIGateway.Multiplayer?.Players?.TryGetIdentityId(steamId) ?? 0;
        if (!IsNpcIdentity(identityId))
        {
            return false;
        }

        if (_log.IsDebugEnabled)
        {
            _log.Debug($"Ignoring {source} for NPC SteamId {steamId} identity={identityId}");
        }

        return true;
    }

    private bool TryGetCurrentPlayerByIdentity(long identityId, out IMyPlayer player)
    {
        return TryGetCurrentPlayer(candidate => candidate.IdentityId == identityId, out player);
    }

    private bool TryGetCurrentPlayerBySteamId(ulong steamId, out IMyPlayer player)
    {
        return TryGetCurrentPlayer(candidate => candidate.SteamUserId == steamId, out player);
    }

    private bool TryGetCurrentPlayer(Func<IMyPlayer, bool> predicate, out IMyPlayer player)
    {
        player = null;
        var players = GetCurrentPlayers();
        if (players == null)
        {
            return false;
        }

        foreach (var candidate in players)
        {
            if (candidate != null && predicate(candidate))
            {
                player = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool ShouldBeDedicatedReserved(AuthorizedPlayerDefinition player)
    {
        return player.IsAuthorized && player.HasReservedSlot;
    }

    private void KickPlayer(ulong steamId)
    {
        try
        {
            MyVisualScriptLogicProvider.KickPlayer(steamId);
        }
        catch (Exception exception)
        {
            _log.Warning(exception, $"Failed to kick player {steamId}.");
        }
    }

    private static string DescribePlayer(string displayName, ulong steamId)
    {
        return string.IsNullOrWhiteSpace(displayName)
            ? steamId.ToString()
            : $"{displayName} ({steamId})";
    }

    private TimeSpan GetAcceptSuppressionDuration()
    {
        var configuredGraceSeconds = _runtimeState.Configuration?.AuthorizationGraceSeconds ?? (int)MinimumAcceptSuppression.TotalSeconds;
        var targetSeconds = Math.Max(
            MinimumAcceptSuppression.TotalSeconds,
            Math.Min(MaximumAcceptSuppression.TotalSeconds, configuredGraceSeconds + 15));
        return TimeSpan.FromSeconds(targetSeconds);
    }

    private sealed class CachedPlayerAccessStatus
    {
        public CachedPlayerAccessStatus(PluginPlayerAuthorizationStatus status, string message, DateTimeOffset checkedUtc)
        {
            Status = status;
            Message = string.IsNullOrWhiteSpace(message) ? string.Empty : message.Trim();
            CheckedUtc = checkedUtc;
        }

        public PluginPlayerAuthorizationStatus Status { get; }
        public string Message { get; }
        public DateTimeOffset CheckedUtc { get; }
    }
}
