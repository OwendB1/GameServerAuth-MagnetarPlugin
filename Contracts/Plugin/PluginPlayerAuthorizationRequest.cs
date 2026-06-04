using System.Runtime.Serialization;

namespace Contracts.Plugin;

[DataContract]
public sealed class PluginPlayerAuthorizationRequest
{
    [DataMember(Order = 1)]
    public string ServerId { get; set; } = string.Empty;

    [DataMember(Order = 2)]
    public ulong DiscordGuildId { get; set; }

    [DataMember(Order = 3)]
    public string ClusterId { get; set; } = string.Empty;

    [DataMember(Order = 4)]
    public ulong SteamId { get; set; }

    [DataMember(Order = 5)]
    public long IssuedAtUnixTimeSeconds { get; set; }

    [DataMember(Order = 6)]
    public string Nonce { get; set; } = string.Empty;

    [DataMember(Order = 7)]
    public string Signature { get; set; } = string.Empty;

    [DataMember(Order = 8, EmitDefaultValue = false)]
    public string? SteamDisplayName { get; set; }
}
