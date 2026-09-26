using System;
using System.IO;
using Contracts.Plugin;
using PluginSdk.Clustering;
using Shared.Config;
using Shared.Logging;
using Shared.Patches;

namespace Shared.Plugin;

public static class Common
{
    private static readonly string StandaloneInstanceId = Guid.NewGuid().ToString("N");
    public static string InstanceId
    {
        get
        {
            if (!PluginCluster.IsClusterProcess) return StandaloneInstanceId;

            var registrationId = Config?.ClusterId?.Trim();
            var clusterId = Environment.GetEnvironmentVariable("CLUSTER_ID")?.Trim();
            var nodeId = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID")?.Trim();
            // GSA instance IDs are globally unique across registered clusters.
            return string.IsNullOrEmpty(registrationId) || string.IsNullOrEmpty(clusterId) || string.IsNullOrEmpty(nodeId)
                ? string.Empty
                : $"gsa:{registrationId}:cluster:{clusterId}:{nodeId}";
        }
    }
    public static string NodeName => PluginCluster.IsClusterProcess
        ? Environment.GetEnvironmentVariable("CLUSTER_NODE_ID")?.Trim()
        : string.IsNullOrWhiteSpace(Config?.NodeName) ? Environment.MachineName : Config.NodeName.Trim();
    public static ClusterNodeRole NodeRole => PluginCluster.IsClusterProcess
        ? ClusterNodeRole.ClusterMember
        : Config?.NodeRole ?? ClusterNodeRole.Standalone;

    public static ICommonPlugin Plugin { get; private set; }
    public static IPluginLogger Logger { get; private set; }
    public static IPluginConfig Config { get; private set; }

    public static string GameVersion;
    public static string DataDir;

    public static void SetPlugin(ICommonPlugin plugin, string gameVersion, string storageDir)
    {
        Plugin = plugin;
        Logger = plugin.Log;
        Config = plugin.Config;

        GameVersion = gameVersion;
        DataDir = Path.Combine(storageDir, "GameServerAuth");

        PatchHelpers.Configure();
    }
}
