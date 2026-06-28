# GameServerAuth Magnetar Plugin

Server-only Space Engineers plugin for Magnetar using the Magnetar Plugin SDK.

GameServerAuth connects a dedicated server to the Game Server Auth control plane. Players link Discord and Steam accounts, then authorize in-game. Server owners can manage role-based join access, promotion levels, reserved slots, and optional cluster lobby redirects from the web dashboard.

## Projects

- `ServerPlugin` - Magnetar Plugin SDK plugin entry point, configuration, commands, and server runtime services.
- `Contracts` - shared GameServerAuth API payloads and signing helpers.
- `Shared` - common plugin helpers and interfaces.

The old client plugin is intentionally removed. This repository builds only the Magnetar server plugin.

## Build

Install the Space Engineers Dedicated Server build references and the .NET SDK required by the project, then build:

```sh
dotnet build GameServerAuth.sln -c Debug
```

The plugin output is:

```text
ServerPlugin/bin/Debug/net48/GameServerAuth.dll
```

`ServerPlugin/ServerPlugin.csproj` references `PluginSdk.dll` from `$(MagnetarBin)`,
which is auto-detected from the Magnetar install location (`$(Magnetar)`) in
`Directory.Build.props`. By default this resolves to:

```text
Windows: %AppData%\Magnetar\Libraries\MagnetarLegacy   (net4x)
         %AppData%\Magnetar\Libraries\MagnetarInterim  (net5+)
Linux:   ~/.local/share/Magnetar/Bin
```

Override the Magnetar location when needed by setting `<Magnetar>` (or `<MagnetarBin>`
directly) in your local `Directory.Build.props`, or on the command line:

```sh
dotnet build GameServerAuth.sln -c Debug -p:Magnetar=/path/to/Magnetar
```

`Directory.Build.props.template` is the template for `Directory.Build.props`, a **local,
uncommitted** file holding the reference folder path overrides (`Bin64`, `Dedicated64`).
`setup.py` copies the template to `Directory.Build.props` when it is missing, then fills in
the auto-detected paths, so every contributor keeps their own local paths. Leaving a path
empty falls back to the platform-specific auto-detection in the same file.

## Configuration

Magnetar stores the XML configuration through the Plugin SDK config system
(`ConfigStorage.LoadXml` / `ConfigStorage.SaveXml`). Runtime edits from Quasar
or the GameServerAuth control plane are applied to the live `PluginConfig`,
debounced to one XML write per change burst, and flushed when the plugin unloads.
Important options:

- `Enabled` - turns the plugin runtime on or off.
- `ServerId` - GameServerAuth server identifier.
- `DiscordGuildId` - Discord guild identifier claimed for this server.
- `ClusterId`, `ClusterSecret`, `NodeName`, `NodeRole` - cluster identity and signing configuration.
- `LobbyServerId` - target lobby server for redirecting unauthorized cluster players.

The plugin creates and saves default config on first load.

## Commands

Chat command roots:

```text
!gsa
```

Commands:

```text
!gsa info
!gsa status
!gsa enable
!gsa disable
!gsa accept
!gsa authorize
!accept
!authorize
```

`!accept` and `!authorize` are the player-facing authorization aliases.

## Deployment

Use the Magnetar local plugin folder or the included deploy scripts after a build:

```sh
ServerPlugin/Deploy.sh GameServerAuth.dll ServerPlugin/bin/Debug/net48 net48
```

On Windows:

```bat
ServerPlugin\Deploy.bat GameServerAuth.dll ServerPlugin\bin\Debug\net48 net48
```

The optional third argument is the target framework moniker. It routes the deploy to the
matching Magnetar edition: `net4x` builds go to `Magnetar/Legacy/Local`, newer ones to
`Magnetar/Interim/Local` (when the Interim edition is installed).

`GameServerAuthServer.xml` is the MagnetarHub metadata file for server-side publication.
