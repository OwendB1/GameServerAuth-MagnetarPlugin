using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Contracts.Plugin;
using PluginSdk.Commands;
using Sandbox.Game;
using Shared.Config;
using VRage.Game.ModAPI;

namespace ServerPlugin;

public abstract class GameServerAuthCommandModule : CommandModule
{
    protected void Respond(string message, string fallbackMessage = "GameServerAuth update.")
    {
        var effectiveMessage = string.IsNullOrWhiteSpace(message)
            ? fallbackMessage
            : message.Trim();

        Context?.Respond(effectiveMessage);
    }

    protected void RespondWithInfo()
    {
        var config = Plugin.Instance.PluginConfig;
        Respond($"{Plugin.PluginName} plugin is enabled: {Format(config.Enabled)}");
        Respond($"instance_id: {Format(config.ServerId)}");
        Respond($"discord_guild_id: {Format(config.DiscordGuildId)}");
        Respond($"cluster_id: {Format(config.ClusterId)}");
        Respond($"node_name: {Format(config.NodeName)}");
        Respond($"node_role: {config.NodeRole}");
    }

    protected void EnablePlugin()
    {
        Plugin.Instance.SetEnabled(true);
        RespondWithInfo();
    }

    protected void DisablePlugin()
    {
        Plugin.Instance.SetEnabled(false);
        RespondWithInfo();
    }

    protected void RespondWithStatus()
    {
        Respond($"runtime_enabled: {Format(Plugin.Instance.IsRuntimeEnabled)}");
        Respond($"socket_connected: {Format(Plugin.Instance.SocketClient?.IsConnected ?? false)}");
        Respond($"plugin_version: {Format(Plugin.Instance.SocketClient?.CurrentPluginVersion)}");
        Respond($"last_socket_error: {Format(Plugin.Instance.SocketClient?.LastError)}");
        Respond($"last_socket_connect_utc: {Format(Plugin.Instance.SocketClient?.LastSocketConnectUtc?.ToString("O"))}");
        Respond($"last_socket_frame_utc: {Format(Plugin.Instance.SocketClient?.LastFrameReceivedUtc?.ToString("O"))}");
        Respond($"last_hello_accepted_utc: {Format(Plugin.Instance.SocketClient?.LastHelloAcceptedUtc?.ToString("O"))}");
        Respond($"session_loaded: {Format(Plugin.Instance.IsSessionLoaded)}");
        Respond($"game_ready: {Format(Plugin.Instance.IsGameplayReady)}");
        Respond($"remote_config_loaded: {Format(Plugin.Instance.AuthorizationRuntime?.Configuration != null)}");
        Respond($"config_revision: {Plugin.Instance.AuthorizationRuntime?.ConfigurationRevision ?? 0}");
        Respond($"last_config_utc: {Format(Plugin.Instance.SocketClient?.LastConfigurationUtc?.ToString("O"))}");
        Respond($"monitor_initialized: {Format(Plugin.Instance.PlayerAuthorizationMonitor != null)}");
        Respond($"authorized_players: {Plugin.Instance.AuthorizationRuntime?.AuthorizedPlayerCount ?? 0}");
        Respond($"online_players: {Plugin.Instance.PlayerAuthorizationMonitor?.OnlinePlayerCount ?? 0}");
        Respond($"ready_players: {Plugin.Instance.PlayerAuthorizationMonitor?.ReadyPlayerCount ?? 0}");
        Respond($"pending_players: {Plugin.Instance.PlayerAuthorizationMonitor?.PendingPlayerCount ?? 0}");
    }

    protected void HandleAccept()
    {
        var caller = Context.Caller;
        if (caller.IsConsole || caller.SteamId == 0)
        {
            Respond("This command can only be used by a player.");
            return;
        }

        if (TryRespondAlreadyAuthorized(caller.SteamId))
        {
            return;
        }

        var accessStatus = Plugin.Instance.PlayerAuthorizationClient
            ?.GetAccessStatusAsync(caller.SteamId, caller.Name, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        if (accessStatus == null)
        {
            Respond("Authorization service unavailable.");
            return;
        }

        switch (accessStatus.Status)
        {
            case PluginPlayerAuthorizationStatus.Authorized:
            case PluginPlayerAuthorizationStatus.AlreadyAuthorized:
                Respond(accessStatus.Message, "Already authorized for this cluster.");
                break;

            case PluginPlayerAuthorizationStatus.RequiresAccept:
                var result = Plugin.Instance.PlayerAuthorizationClient
                    ?.AuthorizeAsync(caller.SteamId, caller.Name, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                if (result == null)
                {
                    Respond("Authorization service unavailable.");
                    return;
                }

                HandleAuthorizationResult(caller, result);
                break;

            case PluginPlayerAuthorizationStatus.RequiresLink:
                Respond(accessStatus.Message, "Discord and Steam are not linked yet.");
                Respond("Run !gsa authorize to open the GameServerAuth link flow.");
                break;

            case PluginPlayerAuthorizationStatus.RequiresGuildJoin:
                OpenAuthorizationOverlay(
                    caller,
                    accessStatus,
                    delaySeconds: 5,
                    openingMessage: "Discord guild join required. Steam overlay will open in 5 seconds with GameServerAuth join flow.",
                    followupMessage: "After joining the guild, return to the game and run !accept again.",
                    showInstructionDialog: true);
                break;

            case PluginPlayerAuthorizationStatus.RequiresRole:
                OpenAuthorizationOverlay(
                    caller,
                    accessStatus,
                    delaySeconds: 5,
                    openingMessage: "Mapped Discord role missing. Steam overlay will open in 5 seconds with GameServerAuth instructions.",
                    followupMessage: "After you get one of the required roles, return to the game and run !accept again.",
                    showInstructionDialog: true);
                break;

            case PluginPlayerAuthorizationStatus.AccessBlocked:
                Respond(accessStatus.Message, "Access blocked by cluster owner.");
                break;

            default:
                Respond(accessStatus.Message, "Authorization request failed. Tell server admin to check GameServerAuth cluster config.");
                break;
        }
    }

    protected void HandleAuthorize()
    {
        var caller = Context.Caller;
        if (caller.IsConsole || caller.SteamId == 0)
        {
            Respond("This command can only be used by a player.");
            return;
        }

        if (TryRespondAlreadyAuthorized(caller.SteamId))
        {
            return;
        }

        var accessStatus = Plugin.Instance.PlayerAuthorizationClient
            ?.GetAccessStatusAsync(caller.SteamId, caller.Name, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        if (accessStatus == null)
        {
            Respond("Authorization service unavailable.");
            return;
        }

        switch (accessStatus.Status)
        {
            case PluginPlayerAuthorizationStatus.RequiresLink:
                OpenAuthorizationOverlay(
                    caller,
                    accessStatus,
                    delaySeconds: 0,
                    openingMessage: "Account link required. Opening GameServerAuth link in Steam overlay.");
                break;

            case PluginPlayerAuthorizationStatus.RequiresGuildJoin:
                OpenAuthorizationOverlay(
                    caller,
                    accessStatus,
                    delaySeconds: 0,
                    openingMessage: "Discord guild join required. Opening GameServerAuth link in Steam overlay.");
                break;

            case PluginPlayerAuthorizationStatus.RequiresRole:
                OpenAuthorizationOverlay(
                    caller,
                    accessStatus,
                    delaySeconds: 0,
                    openingMessage: "Cluster requirements are not met yet. Opening GameServerAuth instructions in Steam overlay.");
                break;

            case PluginPlayerAuthorizationStatus.RequiresAccept:
                if (!string.IsNullOrWhiteSpace(accessStatus.AuthorizationUrl))
                {
                    OpenAuthorizationOverlay(
                        caller,
                        accessStatus,
                        delaySeconds: 0,
                        openingMessage: "Opening GameServerAuth cluster instructions in Steam overlay.",
                        followupMessage: "When ready, return to the game and run !accept.");
                    break;
                }

                Respond(accessStatus.Message, "Discord and Steam are linked. Run !accept to authorize this cluster.");
                break;

            case PluginPlayerAuthorizationStatus.Authorized:
            case PluginPlayerAuthorizationStatus.AlreadyAuthorized:
                Respond(accessStatus.Message, "Already authorized for this cluster.");
                break;

            case PluginPlayerAuthorizationStatus.AccessBlocked:
                Respond(accessStatus.Message, "Access blocked by cluster owner.");
                break;

            default:
                Respond(accessStatus.Message, "Authorization request failed. Tell server admin to check GameServerAuth cluster config.");
                break;
        }
    }

    private bool TryRespondAlreadyAuthorized(ulong steamUserId)
    {
        var runtime = Plugin.Instance.AuthorizationRuntime;
        AuthorizedPlayerDefinition player;
        if (runtime == null || !runtime.TryGetPlayer(steamUserId, out player) || !player.IsAuthorized)
        {
            return false;
        }

        Respond("Already authorized for this cluster.");
        return true;
    }

    private void HandleAuthorizationResult(CommandCaller caller, PluginPlayerAuthorizationResponse result)
    {
        switch (result.Status)
        {
            case PluginPlayerAuthorizationStatus.Authorized:
            case PluginPlayerAuthorizationStatus.AlreadyAuthorized:
                Plugin.Instance.PlayerAuthorizationMonitor?.SuppressEnforcementAfterAccept(caller.SteamId);
                Respond(result.Message, result.Status == PluginPlayerAuthorizationStatus.Authorized
                    ? "Cluster authorization stored."
                    : "Already authorized for this cluster.");
                Respond("Config update pushed. Wait a few seconds if your permissions do not change immediately.");
                break;

            case PluginPlayerAuthorizationStatus.RequiresLink:
                Respond(result.Message, "Discord and Steam are not linked yet.");
                Respond("Run !gsa authorize to open the GameServerAuth link flow.");
                break;

            case PluginPlayerAuthorizationStatus.RequiresGuildJoin:
                OpenAuthorizationOverlay(
                    caller,
                    result,
                    delaySeconds: 5,
                    openingMessage: "Discord guild join required. Steam overlay will open in 5 seconds with GameServerAuth join flow.",
                    followupMessage: "After joining the guild, return to the game and run !accept again.",
                    showInstructionDialog: true);
                break;

            case PluginPlayerAuthorizationStatus.RequiresRole:
                OpenAuthorizationOverlay(
                    caller,
                    result,
                    delaySeconds: 5,
                    openingMessage: "Mapped Discord role missing. Steam overlay will open in 5 seconds with GameServerAuth instructions.",
                    followupMessage: "After you get one of the required roles, return to the game and run !accept again.",
                    showInstructionDialog: true);
                break;

            case PluginPlayerAuthorizationStatus.AccessBlocked:
                Respond(result.Message, "Access blocked by cluster owner.");
                break;

            default:
                Respond(result.Message, "Authorization request failed. Tell server admin to check GameServerAuth cluster config.");
                break;
        }
    }

    private void OpenAuthorizationOverlay(
        CommandCaller caller,
        PluginPlayerAuthorizationResponse accessStatus,
        int delaySeconds,
        string openingMessage,
        string followupMessage = null,
        bool showInstructionDialog = false)
    {
        var authorizationUrl = accessStatus.AuthorizationUrl?.Trim();
        if (string.IsNullOrWhiteSpace(authorizationUrl))
        {
            Respond("Authorization URL missing. Tell server admin to check GameServerAuth web config.");
            return;
        }

        if (caller.IdentityId == 0)
        {
            Respond("Unable to open Steam overlay for this player.");
            return;
        }

        if (showInstructionDialog)
        {
            ShowInstructionDialog(caller, accessStatus);
        }

        Respond(openingMessage);
        if (!string.IsNullOrWhiteSpace(followupMessage))
        {
            Respond(followupMessage);
        }

        var overlayUrl = BuildSteamOverlayAuthorizationUrl(authorizationUrl);
        if (delaySeconds <= 0)
        {
            MyVisualScriptLogicProvider.OpenSteamOverlay(overlayUrl, caller.IdentityId);
            return;
        }

        var identityId = caller.IdentityId;
        Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ConfigureAwait(false);
            Plugin.Instance?.InvokeOnGameThread(
                () => MyVisualScriptLogicProvider.OpenSteamOverlay(overlayUrl, identityId),
                "GameServerAuth.OpenAuthorizationOverlay");
        });
    }

    private static void ShowInstructionDialog(CommandCaller caller, PluginPlayerAuthorizationResponse response)
    {
        var body = BuildInstructionDialogBody(response);
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        Plugin.Instance.TryShowPlayerDialog(
            caller.SteamId,
            "Auth instructions",
            string.IsNullOrWhiteSpace(response.ClusterName) ? "GameServerAuth" : response.ClusterName,
            body,
            "Press this when done");
    }

    private static string BuildInstructionDialogBody(PluginPlayerAuthorizationResponse response)
    {
        var builder = new StringBuilder();
        AppendDialogLine(builder, response.Message);

        if (!string.IsNullOrWhiteSpace(response.RequiredDiscordRoleName))
        {
            AppendDialogLine(builder, $"Required Discord roles: {response.RequiredDiscordRoleName.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(response.Instructions))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(response.Instructions.Trim());
        }

        return builder.ToString();
    }

    private static void AppendDialogLine(StringBuilder builder, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        builder.Append(text.Trim());
    }

    private static string BuildSteamOverlayAuthorizationUrl(string authorizationUrl)
    {
        return "https://steamcommunity.com/linkfilter/?u=" + Uri.EscapeDataString(authorizationUrl);
    }

    private static string Format(bool value) => value ? "Yes" : "No";
    private static string Format(string value) => string.IsNullOrWhiteSpace(value) ? "<empty>" : value;
}

[CommandRoot("gsa", "GameServerAuth", "Discord-linked server authorization")]
public sealed class GsaCommands : GameServerAuthCommandModule
{
    [Command("", "Prints the current settings")]
    [Permission(MyPromoteLevel.None)]
    public void Info() => RespondWithInfo();

    [Command("info", "Prints the current settings")]
    [Permission(MyPromoteLevel.None)]
    public void InfoCommand() => RespondWithInfo();

    [Command("enable", "Enables the plugin")]
    [Permission(MyPromoteLevel.Admin)]
    public void Enable() => EnablePlugin();

    [Command("disable", "Disables the plugin")]
    [Permission(MyPromoteLevel.Admin)]
    public void Disable() => DisablePlugin();

    [Command("status", "Prints auth runtime state")]
    [Permission(MyPromoteLevel.Admin)]
    public void Status() => RespondWithStatus();

    [Command("accept", "Authorizes this cluster with your linked Discord and Steam account")]
    [Permission(MyPromoteLevel.None)]
    public void Accept() => HandleAccept();

    [Command("authorize", "Opens the account-link and guild-join flow")]
    [Permission(MyPromoteLevel.None)]
    public void Authorize() => HandleAuthorize();
}

[CommandRoot("accept", "GameServerAuth", "Authorize this cluster")]
public sealed class AcceptCommand : GameServerAuthCommandModule
{
    [Command("", "Authorizes this cluster with your linked Discord and Steam account")]
    [Permission(MyPromoteLevel.None)]
    public void Accept() => HandleAccept();
}

[CommandRoot("authorize", "GameServerAuth", "Open account-link and guild-join flow")]
public sealed class AuthorizeCommand : GameServerAuthCommandModule
{
    [Command("", "Opens the account-link and guild-join flow")]
    [Permission(MyPromoteLevel.None)]
    public void Authorize() => HandleAuthorize();
}
