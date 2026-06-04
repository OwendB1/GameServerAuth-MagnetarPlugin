using System.Runtime.Serialization;

namespace Contracts.Plugin;

[DataContract]
public sealed class PluginHelloPayload
{
    [DataMember(Order = 1)]
    public string? ServerId { get; set; }

    [DataMember(Order = 2)]
    public ulong DiscordGuildId { get; set; }

    [DataMember(Order = 3)]
    public string? ClusterId { get; set; }

    [DataMember(Order = 4)]
    public string? NodeName { get; set; }

    [DataMember(Order = 5)]
    public ClusterNodeRole NodeRole { get; set; }

    [DataMember(Order = 6)]
    public string? PluginVersion { get; set; }

    [DataMember(Order = 7)]
    public string? GameVersion { get; set; }

    [DataMember(Order = 8)]
    public string? NexusServerId { get; set; }

    [DataMember(Order = 9)]
    public long IssuedAtUnixTimeSeconds { get; set; }

    [DataMember(Order = 10)]
    public string Nonce { get; set; } = string.Empty;

    [DataMember(Order = 11)]
    public string Signature { get; set; } = string.Empty;
}
