using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Contracts.Plugin;

[DataContract]
public sealed class PluginServerConfiguration
{
    [DataMember(Order = 1)]
    public string ServerId { get; set; } = string.Empty;

    [DataMember(Order = 2)]
    public string ClusterId { get; set; } = string.Empty;

    [DataMember(Order = 3)]
    public string DisplayName { get; set; } = string.Empty;

    [DataMember(Order = 4)]
    public ClusterNodeRole NodeRole { get; set; }

    [DataMember(Order = 5)]
    public string? LobbyServerId { get; set; }

    [DataMember(Order = 6)]
    public bool KickUnauthorizedPlayers { get; set; }

    [DataMember(Order = 7)]
    public bool RedirectUnauthorizedToLobby { get; set; }

    [DataMember(Order = 8)]
    public int AuthorizationGraceSeconds { get; set; }

    [DataMember(Order = 9)]
    public List<AuthorizedPlayerDefinition> Players { get; set; } = new List<AuthorizedPlayerDefinition>();

    [DataMember(Order = 10)]
    public List<PluginRoleMapping> RoleMappings { get; set; } = new List<PluginRoleMapping>();

    [DataMember(Order = 11)]
    public string? ClusterSecret { get; set; }

    [DataMember(Order = 12)]
    public bool EnablePluginLogForwarding { get; set; }

    [DataMember(Order = 13)]
    public int PluginLogRetentionHours { get; set; }
}
