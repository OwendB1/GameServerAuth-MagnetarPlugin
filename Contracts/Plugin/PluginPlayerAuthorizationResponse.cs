using System.Runtime.Serialization;

namespace Contracts.Plugin;

[DataContract]
public sealed class PluginPlayerAuthorizationResponse
{
    [DataMember(Order = 1)]
    public PluginPlayerAuthorizationStatus Status { get; set; }

    [DataMember(Order = 2)]
    public string Message { get; set; } = string.Empty;

    [DataMember(Order = 3)]
    public string? AuthorizationUrl { get; set; }

    [DataMember(Order = 4)]
    public string? ClusterName { get; set; }

    [DataMember(Order = 5)]
    public string? Instructions { get; set; }

    [DataMember(Order = 6)]
    public string? RequiredDiscordRoleName { get; set; }
}
