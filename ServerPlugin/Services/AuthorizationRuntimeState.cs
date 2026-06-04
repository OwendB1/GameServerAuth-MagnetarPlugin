using System.Collections.Generic;
using System.Linq;
using Contracts.Plugin;

namespace ServerPlugin.Services;

public sealed class AuthorizationRuntimeState
{
    private readonly object _syncRoot = new();
    private PluginServerConfiguration _configuration;
    private Dictionary<ulong, AuthorizedPlayerDefinition> _players = new();
    private int _configurationRevision;

    public PluginServerConfiguration Configuration
    {
        get
        {
            lock (_syncRoot)
            {
                return _configuration;
            }
        }
    }

    public int AuthorizedPlayerCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _players.Count(x => x.Value.IsAuthorized);
            }
        }
    }

    public int ConfigurationRevision
    {
        get
        {
            lock (_syncRoot)
            {
                return _configurationRevision;
            }
        }
    }

    public bool IsPluginLogForwardingEnabled
    {
        get
        {
            lock (_syncRoot)
            {
                return _configuration != null && _configuration.EnablePluginLogForwarding;
            }
        }
    }

    public int PluginLogRetentionHours
    {
        get
        {
            lock (_syncRoot)
            {
                return _configuration?.PluginLogRetentionHours ?? 0;
            }
        }
    }

    public void ApplyConfiguration(PluginServerConfiguration newConfiguration)
    {
        lock (_syncRoot)
        {
            _configuration = newConfiguration;
            _players = newConfiguration.Players.ToDictionary(x => x.SteamId, x => x);
            _configurationRevision++;
        }
    }

    public void Reset()
    {
        lock (_syncRoot)
        {
            _configuration = null;
            _players = new Dictionary<ulong, AuthorizedPlayerDefinition>();
            _configurationRevision++;
        }
    }

    public bool TryGetPlayer(ulong steamId, out AuthorizedPlayerDefinition player)
    {
        lock (_syncRoot)
        {
            return _players.TryGetValue(steamId, out player);
        }
    }
}
