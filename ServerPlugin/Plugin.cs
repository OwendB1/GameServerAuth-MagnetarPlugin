using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Threading;
using Contracts.Plugin;
using HarmonyLib;
using PluginSdk.Commands;
using PluginSdk.Config;
using Sandbox;
using Sandbox.Game;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using ServerPlugin.Services;
using Shared.Config;
using Shared.Logging;
using Shared.Patches;
using Shared.Plugin;
using VRage.FileSystem;
using VRage.Game;
using VRage.Plugins;

namespace ServerPlugin;

// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin, ICommonPlugin
{
    public const string PluginName = "GameServerAuth";
    public static Plugin Instance { get; private set; }

    public long Tick { get; private set; }
    private bool _failed;
    private bool _initialized;

    public IPluginLogger Log => Logger;
    private static readonly IPluginLogger Logger = new SdkPluginLogger(PluginName);

    public IPluginConfig Config => _config;
    public PluginConfig PluginConfig => _config;
    private PluginConfig _config;
    private string _configPath;
    private static readonly string ConfigFileName = $"{PluginName}.xml";

    public PluginSocketClient SocketClient { get; private set; }
    public PluginPlayerAuthorizationClient PlayerAuthorizationClient { get; private set; }
    public AuthorizationRuntimeState AuthorizationRuntime { get; private set; }
    public PlayerAuthorizationMonitor PlayerAuthorizationMonitor { get; private set; }
    public NexusClusterRedirectService NexusClusterRedirectService { get; private set; }
    public bool IsRuntimeEnabled => _runtimeEnabled;
    public bool IsSessionLoaded => MySession.Static != null;
    public bool IsGameplayReady => MySession.Static?.Ready == true || MySandboxGame.IsGameReady;

    private string _lastManagerStateMessage;
    private DateTimeOffset _lastManagerStateLoggedUtc;
    private bool _runtimeEnabled;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void Init(object gameInstance)
    {
#if DEBUG
        // Allow the debugger some time to connect once the plugin assembly is loaded
        Thread.Sleep(100);
#endif

        Instance = this;

        Log.Info("Loading");

        _configPath = Path.Combine(MyFileSystem.UserDataPath, ConfigFileName);
        _config = ConfigStorage.LoadXml<PluginConfig>(_configPath);
        if (string.IsNullOrWhiteSpace(_config.ServerId))
        {
            _config.ServerId = Guid.NewGuid().ToString("N");
            SaveConfig();
        }

        var gameVersion = MyFinalBuildConstants.APP_VERSION_STRING.ToString();
        Common.SetPlugin(this, gameVersion, MyFileSystem.UserDataPath);

        if (!PatchHelpers.HarmonyPatchAll(Log, new Harmony(PluginName)))
        {
            _failed = true;
            return;
        }

        try
        {
            ServerCommands.Register(Assembly.GetExecutingAssembly(),
                typeof(GsaCommands), typeof(AcceptCommand), typeof(AuthorizeCommand));
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Failed to register GameServerAuth chat commands.");
        }

        AuthorizationRuntime = new AuthorizationRuntimeState();
        NexusClusterRedirectService = new NexusClusterRedirectService(Log);
        PlayerAuthorizationClient = new PluginPlayerAuthorizationClient(Log);
        SocketClient = new PluginSocketClient(Log, AuthorizationRuntime, BuildHeartbeatPayload);
        _config.PropertyChanged += ConfigChanged;
        ApplyConfiguredRuntimeState();

        _initialized = true;
        Log.Debug("Successfully loaded");
    }

    public void Dispose()
    {
        if (_initialized)
        {
            Log.Debug("Disposing");
            ResetGameplaySessionState();
            PlayerAuthorizationClient = null;
            SocketClient?.Dispose();
            SocketClient = null;
            AuthorizationRuntime = null;
            NexusClusterRedirectService = null;

            if (_config != null)
            {
                _config.PropertyChanged -= ConfigChanged;
                SaveConfig();
            }

            Log.Debug("Disposed");
        }

        Instance = null;
    }

    public void Update()
    {
        if (_failed)
            return;
        
#if DEBUG
        CustomUpdate();
        Tick++;
#else        
        try
        {
            CustomUpdate();
            Tick++;
        }
        catch (Exception e)
        {
            Log.Critical(e, "Update failed");
            _failed = true;
        }
#endif       
    }

    private void CustomUpdate()
    {
        ApplyConfiguredRuntimeState();
        if (_runtimeEnabled)
        {
            TryInitializeGameplayManagers();
            PlayerAuthorizationMonitor?.Update(Tick);
        }

        PatchHelpers.PatchUpdates();
    }

    public void SetEnabled(bool enabled)
    {
        if (PluginConfig == null)
        {
            return;
        }

        PluginConfig.Enabled = enabled;
        ApplyConfiguredRuntimeState();
    }

    public bool TrySendPlayerMessage(ulong steamId, string message, string fallbackMessage = "GameServerAuth update.")
    {
        var effectiveMessage = string.IsNullOrWhiteSpace(message)
            ? fallbackMessage
            : message.Trim();

        try
        {
            var identityId = MySession.Static?.Players?.TryGetIdentityId(steamId) ?? 0;
            if (identityId == 0)
            {
                return false;
            }

            MyVisualScriptLogicProvider.SendChatMessage(effectiveMessage, PluginName, identityId);
            return true;
        }
        catch (Exception exception)
        {
            Log.Warning($"Failed to send chat message to {steamId}: {exception.Message}");
            return false;
        }
    }

    public void InvokeOnGameThread(Action action, string caller = PluginName)
    {
        if (action == null)
        {
            return;
        }

        if (MySandboxGame.Static != null)
        {
            MySandboxGame.Static.Invoke(action, caller);
            return;
        }

        action();
    }

    public bool TryShowPlayerDialog(
        ulong steamId,
        string title,
        string subtitle,
        string content,
        string buttonText = "OK")
    {
        if (steamId == 0 || string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var heading = string.IsNullOrWhiteSpace(subtitle)
            ? title
            : $"{title}: {subtitle}";
        return TrySendPlayerMessage(steamId, $"{heading}\n{content}", content);
    }

    private void TryInitializeGameplayManagers()
    {
        if (!_runtimeEnabled)
        {
            return;
        }

        if (!IsSessionLoaded || MyAPIGateway.Multiplayer?.Players == null)
        {
            LogManagerState("Game session or multiplayer player API unavailable. Player authorization monitor is waiting for session load.");
            return;
        }

        if (AuthorizationRuntime == null || NexusClusterRedirectService == null)
        {
            LogManagerState("Runtime services unavailable. Player authorization monitor is waiting for plugin initialization.");
            return;
        }

        if (PlayerAuthorizationMonitor == null)
        {
            PlayerAuthorizationMonitor = new PlayerAuthorizationMonitor(
                Log,
                AuthorizationRuntime,
                NexusClusterRedirectService,
                () => SocketClient?.IsConnected == true);
            Log.Info("Player authorization monitor initialized.");
        }

        ClearManagerState();
    }

    private PluginHeartbeatPayload BuildHeartbeatPayload()
    {
        var configuredNodeName = string.IsNullOrWhiteSpace(Common.Config.NodeName)
            ? Environment.MachineName
            : Common.Config.NodeName.Trim();

        return new PluginHeartbeatPayload
        {
            OnlinePlayers = PlayerAuthorizationMonitor?.OnlinePlayerCount ?? 0,
            AuthorizedPlayers = AuthorizationRuntime?.AuthorizedPlayerCount ?? 0,
            StatusMessage = SocketClient?.LastError,
            NexusServerId = NexusClusterRedirectService?.GetCurrentServerId(),
            NodeName = configuredNodeName
        };
    }

    private void ResetGameplaySessionState()
    {
        ClearManagerState();
        if (PlayerAuthorizationMonitor != null)
        {
            Log.Info("Disposing player authorization monitor.");
            PlayerAuthorizationMonitor.Dispose();
            PlayerAuthorizationMonitor = null;
        }
    }

    private void ApplyConfiguredRuntimeState()
    {
        var shouldEnable = PluginConfig?.Enabled == true;
        if (shouldEnable == _runtimeEnabled)
        {
            return;
        }

        if (shouldEnable)
        {
            _runtimeEnabled = true;
            SocketClient?.Start();
            Log.Info("GameServerAuth runtime enabled.");
            TryInitializeGameplayManagers();
            return;
        }

        _runtimeEnabled = false;
        ResetGameplaySessionState();
        SocketClient?.Stop();
        AuthorizationRuntime?.Reset();
        Log.Info("GameServerAuth runtime disabled.");
    }

    private void LogManagerState(string message)
    {
        var now = DateTimeOffset.UtcNow;
        if (string.Equals(_lastManagerStateMessage, message, StringComparison.Ordinal) &&
            now - _lastManagerStateLoggedUtc < TimeSpan.FromSeconds(30))
        {
            return;
        }

        _lastManagerStateMessage = message;
        _lastManagerStateLoggedUtc = now;
        Log.Info(message);
    }

    private void ClearManagerState()
    {
        _lastManagerStateMessage = null;
        _lastManagerStateLoggedUtc = default;
    }

    private void ConfigChanged(object sender, PropertyChangedEventArgs e)
    {
        SaveConfig();
        ApplyConfiguredRuntimeState();
    }

    private void SaveConfig()
    {
        if (_config == null || string.IsNullOrWhiteSpace(_configPath))
        {
            return;
        }

        try
        {
            ConfigStorage.SaveXml(_config, _configPath);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Failed to save plugin config.");
        }
    }
}
