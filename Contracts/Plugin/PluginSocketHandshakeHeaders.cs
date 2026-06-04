namespace Contracts.Plugin;

public static class PluginSocketHandshakeHeaders
{
    public const string Prefix = "X-GSA-";
    public const string ServerId = Prefix + "ServerId";
    public const string DiscordGuildId = Prefix + "DiscordGuildId";
    public const string ClusterId = Prefix + "ClusterId";
    public const string NodeName = Prefix + "NodeName";
    public const string NodeRole = Prefix + "NodeRole";
    public const string PluginVersion = Prefix + "PluginVersion";
    public const string GameVersion = Prefix + "GameVersion";
    public const string NexusServerId = Prefix + "NexusServerId";
    public const string IssuedAtUnixTimeSeconds = Prefix + "IssuedAtUnixTimeSeconds";
    public const string Nonce = Prefix + "Nonce";
    public const string Signature = Prefix + "Signature";
}
