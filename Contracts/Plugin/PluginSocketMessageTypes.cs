namespace Contracts.Plugin;

public static class PluginSocketMessageTypes
{
    public const string Hello = "hello";
    public const string HelloAccepted = "hello-accepted";
    public const string Heartbeat = "heartbeat";
    public const string Configuration = "configuration";
    public const string ConfigurationRequested = "configuration-requested";
    public const string LogEntry = "log-entry";
    public const string Error = "error";
}
