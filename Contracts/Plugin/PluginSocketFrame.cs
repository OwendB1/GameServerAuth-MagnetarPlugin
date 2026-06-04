using System.Runtime.Serialization;

namespace Contracts.Plugin;

[DataContract]
public sealed class PluginSocketFrame
{
    [DataMember(Order = 1)]
    public string Type { get; set; } = string.Empty;

    [DataMember(Order = 2)]
    public PluginHelloPayload? Hello { get; set; }

    [DataMember(Order = 3)]
    public PluginHeartbeatPayload? Heartbeat { get; set; }

    [DataMember(Order = 4)]
    public PluginServerConfiguration? Configuration { get; set; }

    [DataMember(Order = 5)]
    public PluginLogEntryPayload? LogEntry { get; set; }
}
