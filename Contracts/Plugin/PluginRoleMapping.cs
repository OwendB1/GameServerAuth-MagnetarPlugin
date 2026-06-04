using System.Runtime.Serialization;

namespace Contracts.Plugin;

[DataContract]
public sealed class PluginRoleMapping
{
    [DataMember(Order = 1)]
    public ulong DiscordRoleId { get; set; }

    [DataMember(Order = 2)]
    public string? DiscordRoleName { get; set; }

    [DataMember(Order = 3)]
    public GameAuthorizationLevel AuthorizationLevel { get; set; }

    [DataMember(Order = 4)]
    public bool GrantsReservedSlot { get; set; }
}
