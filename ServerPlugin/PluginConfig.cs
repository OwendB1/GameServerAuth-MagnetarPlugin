using System;
using Contracts.Plugin;
using PluginSdk.Config;
using Shared.Config;

namespace ServerPlugin;

[Serializable]
[Section("general", caption: "General")]
[Section("identity", caption: "Identity")]
public class PluginConfig : PluginSdk.Config.PluginConfig, IPluginConfig
{
    private bool _enabled = true;
    private string _discordGuildId = string.Empty;
    private string _clusterId = string.Empty;
    private string _clusterSecret = string.Empty;
    private string _nodeName = Environment.MachineName;
    private ClusterNodeRole _nodeRole = ClusterNodeRole.Standalone;
    private string _lobbyServerId = string.Empty;

    [BoolOption("Enable the plugin", Parent = "general")]
    public bool Enabled
    {
        get => _enabled;
        set => SetField(ref _enabled, value);
    }

    [StringOption(description: "Discord guild id copied from the owner dashboard", Parent = "identity")]
    public string DiscordGuildId
    {
        get => _discordGuildId;
        set => SetField(ref _discordGuildId, value);
    }

    [StringOption(description: "Cluster GUID copied from the owner dashboard", Parent = "identity")]
    public string ClusterId
    {
        get => _clusterId;
        set => SetField(ref _clusterId, value);
    }

    [StringOption(description: "Shared secret copied from the owner dashboard", Parent = "identity")]
    public string ClusterSecret
    {
        get => _clusterSecret;
        set => SetField(ref _clusterSecret, value);
    }

    [StringOption(description: "Standalone or Nexus node name for logs and dashboard. Native clusters use CLUSTER_NODE_ID automatically.", Parent = "identity")]
    public string NodeName
    {
        get => _nodeName;
        set => SetField(ref _nodeName, value);
    }

    [EnumOption("Standalone or Nexus role. Native clusters report cluster member automatically.", Parent = "identity")]
    public ClusterNodeRole NodeRole
    {
        get => _nodeRole;
        set => SetField(ref _nodeRole, value);
    }

    [StringOption(description: "Optional target node id for cluster redirects", Parent = "identity")]
    public string LobbyServerId
    {
        get => _lobbyServerId;
        set => SetField(ref _lobbyServerId, value);
    }
}
