using System.ComponentModel;

namespace Shared.Config;

public interface IPluginConfig : INotifyPropertyChanged
{
    // Enables the plugin
    bool Enabled { get; set; }

    string DiscordGuildId { get; set; }
    string ClusterId { get; set; }
    string ClusterSecret { get; set; }
    string NodeName { get; set; }
}
