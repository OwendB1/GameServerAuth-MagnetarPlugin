using System.ComponentModel;
using Contracts.Plugin;

namespace Shared.Config;

public interface IPluginConfig : INotifyPropertyChanged
{
    // Enables the plugin
    bool Enabled { get; set; }

    string ServerId { get; set; }
    string DiscordGuildId { get; set; }
    string ClusterId { get; set; }
    string ClusterSecret { get; set; }
    string NodeName { get; set; }
    ClusterNodeRole NodeRole { get; set; }
    string LobbyServerId { get; set; }
}
