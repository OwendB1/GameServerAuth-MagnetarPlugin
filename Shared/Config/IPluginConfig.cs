using System.ComponentModel;
using Contracts.Plugin;

namespace Shared.Config;

public interface IPluginConfig : INotifyPropertyChanged
{
    // Enables the plugin
    bool Enabled { get; set; }

    // Enables checking for changes in patched game code (disable this on Proton/Linux)
    bool DetectCodeChanges { get; set; }

    string ServerId { get; set; }
    string DiscordGuildId { get; set; }
    string ClusterId { get; set; }
    string ClusterSecret { get; set; }
    string NodeName { get; set; }
    ClusterNodeRole NodeRole { get; set; }
    string LobbyServerId { get; set; }
}
