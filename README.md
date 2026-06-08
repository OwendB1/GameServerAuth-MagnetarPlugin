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

`ServerPlugin/ServerPlugin.csproj` references `PluginSdk.dll` from `$(PluginSdkDll)`. If that property is not set, it checks:

```text
/home/owendb/.local/share/Magnetar/Bin/PluginSdk.dll
/home/owendb/Documents/GitHub/Magnetar/PluginSdk/bin/Debug/netstandard2.0/PluginSdk.dll
```

Override it when needed:

```sh
dotnet build GameServerAuth.sln -c Debug -p:PluginSdkDll=/path/to/PluginSdk.dll
```

## Configuration

Magnetar stores the XML configuration through the Plugin SDK config system. Important options:

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
ServerPlugin/Deploy.sh GameServerAuth.dll ServerPlugin/bin/Debug/net48
```

On Windows:

```bat
ServerPlugin\Deploy.bat GameServerAuth.dll ServerPlugin\bin\Debug\net48
```

`GameServerAuthServer.xml` is the MagnetarHub metadata file for server-side publication.
