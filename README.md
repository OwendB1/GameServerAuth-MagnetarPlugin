# GameServerAuth Magnetar Plugin

Server-only Space Engineers plugin for Magnetar using the Magnetar Plugin SDK.

GameServerAuth connects a dedicated server to the Game Server Auth control plane. Players link Discord and Steam accounts, then authorize in-game. Server owners can manage role-based join access, promotion levels, and reserved slots from the web dashboard.

## Projects

- `ServerPlugin` - Magnetar Plugin SDK plugin entry point, configuration, commands, and server runtime services.
- `Contracts` - shared GameServerAuth API payloads and signing helpers.
- `Shared` - common plugin helpers and interfaces.

The old client plugin is intentionally removed. This repository builds only the Magnetar server plugin.

## Build

Install the Space Engineers Dedicated Server build references and the .NET SDK required by the project, then build:

```sh
dotnet build GameServerAuth-MagnetarPlugin.sln -c Debug
```

The plugin output is:

```text
Linux:   ServerPlugin/bin/Debug/net10.0/GameServerAuth.dll
Windows: ServerPlugin/bin/Debug/net48/GameServerAuth.dll
         ServerPlugin/bin/Debug/net10.0/GameServerAuth.dll
```

`ServerPlugin/ServerPlugin.csproj` references `PluginSdk.dll` from `$(MagnetarBin)`,
which is auto-detected from the Magnetar install location (`$(Magnetar)`) in
`Directory.Build.props`. By default this resolves to:

```text
Windows: %AppData%\Magnetar\Libraries\MagnetarLegacy   (net4x)
         %AppData%\Magnetar\Libraries\MagnetarInterim  (net5+)
Linux:   ~/.local/share/Magnetar/Bin
```

Native cluster support requires Magnetar PluginSdk 2.4.2.3 or newer.

Override the Magnetar location when needed by setting `<Magnetar>` (or `<MagnetarBin>`
directly) in your local `Directory.Build.props`, or on the command line:

```sh
dotnet build GameServerAuth-MagnetarPlugin.sln -c Debug -p:Magnetar=/path/to/Magnetar
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
Native cluster nodes derive a stable GSA instance ID from the GSA registration
ID, `CLUSTER_ID`, and `CLUSTER_NODE_ID`. The dashboard name comes from
`CLUSTER_NODE_ID`. They report as cluster members, including the
World Authority. The node role is selected automatically from the runtime.
The plugin refuses a cluster connection when either launcher identity is missing.
Standalone processes generate an instance ID per process; cloned
configuration does not require a unique `ServerId` setting.
Important options:

- `Enabled` - turns the plugin runtime on or off.
- `DiscordGuildId` - Discord guild identifier claimed for this server.
- `ClusterId`, `ClusterSecret` - GSA cluster identity and signing configuration.
- `NodeName` - standalone display name; native clusters use launcher identity instead.

Nexus is unsupported by this Magnetar plugin. The node role is always standalone
or native cluster member; unauthorized players are kicked when GSA requests it.
The wire payload keeps legacy Nexus fields empty for compatibility with the
current GSA service protocol.

GSA management limits concurrent plugin connections per registered cluster.
Its connection cap must cover every node that connects, including the World
Authority; additional nodes receive HTTP 409 until the cap is raised in GSA.

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
ServerPlugin/Deploy.sh GameServerAuth.dll ServerPlugin/bin/Debug/net10.0 net10.0
```

On Windows:

```bat
ServerPlugin\Deploy.bat GameServerAuth.dll ServerPlugin\bin\Debug\net48 net48
```

The optional third argument is the target framework moniker. It routes the deploy to the
matching Magnetar edition: `net4x` builds go to `Magnetar/Legacy/Local`, newer ones to
`Magnetar/Interim/Local` (when the Interim edition is installed).

`GameServerAuthServer.xml` is the MagnetarHub metadata file for server-side publication.
