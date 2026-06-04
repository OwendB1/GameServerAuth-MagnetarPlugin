namespace Contracts.Plugin;

public enum PluginPlayerAuthorizationStatus
{
    Error = 0,
    Authorized = 1,
    AlreadyAuthorized = 2,
    RequiresLink = 3,
    RequiresAccept = 4,
    AccessBlocked = 5,
    RequiresGuildJoin = 6,
    RequiresRole = 7
}
