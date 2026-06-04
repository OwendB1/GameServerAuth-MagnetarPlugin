using System.Runtime.Serialization;

namespace Contracts.Plugin;

[DataContract]
public sealed class PluginHeartbeatPayload
{
    [DataMember(Order = 1)]
    public int OnlinePlayers { get; set; }

    [DataMember(Order = 2)]
    public int AuthorizedPlayers { get; set; }

    [DataMember(Order = 3)]
    public string? StatusMessage { get; set; }

    [DataMember(Order = 4)]
    public string? NexusServerId { get; set; }

    [DataMember(Order = 5)]
    public string? NodeName { get; set; }
}
