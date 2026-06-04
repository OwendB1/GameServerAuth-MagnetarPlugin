using System.Runtime.Serialization;

namespace Contracts.Plugin;

[DataContract]
public sealed class AuthorizedPlayerDefinition
{
    [DataMember(Order = 1)]
    public ulong SteamId { get; set; }

    [DataMember(Order = 2)]
    public ulong DiscordUserId { get; set; }

    [DataMember(Order = 3)]
    public bool IsAuthorized { get; set; }

    [DataMember(Order = 4)]
    public bool HasReservedSlot { get; set; }

    [DataMember(Order = 5)]
    public GameAuthorizationLevel AuthorizationLevel { get; set; }

    [DataMember(Order = 6)]
    public string? DisplayName { get; set; }
}
