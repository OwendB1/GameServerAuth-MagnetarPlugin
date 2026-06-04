using System.Runtime.Serialization;

namespace Contracts.Plugin;

[DataContract]
public sealed class PluginLogEntryPayload
{
    [DataMember(Order = 1)]
    public long OccurredAtUnixTimeMilliseconds { get; set; }

    [DataMember(Order = 2)]
    public string LogLevel { get; set; } = string.Empty;

    [DataMember(Order = 3)]
    public string Message { get; set; } = string.Empty;
}
