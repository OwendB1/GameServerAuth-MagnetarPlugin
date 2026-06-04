using System;
using System.IO;
using System.Reflection;
using System.Xml;

namespace ServerPlugin;

internal static class PluginVersionResolver
{
    private static string _cachedVersion;
    private static readonly object Gate = new object();

    public static string GetVersion()
    {
        if (!string.IsNullOrWhiteSpace(_cachedVersion))
        {
            return _cachedVersion;
        }

        lock (Gate)
        {
            if (!string.IsNullOrWhiteSpace(_cachedVersion))
            {
                return _cachedVersion;
            }

            _cachedVersion = ReadManifestVersion() ?? ReadAssemblyVersion();
            return _cachedVersion;
        }
    }

    private static string ReadManifestVersion()
    {
        try
        {
            var manifestPath = ResolveManifestPath();
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            {
                return null;
            }

            var document = new XmlDocument();
            document.Load(manifestPath);
            var versionNode = document.SelectSingleNode("/PluginManifest/Version");
            var version = versionNode?.InnerText?.Trim();
            return string.IsNullOrWhiteSpace(version) ? null : version;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveManifestPath()
    {
        try
        {
            var assemblyDirectory = Path.GetDirectoryName(typeof(PluginVersionResolver).Assembly.Location);
            if (!string.IsNullOrWhiteSpace(assemblyDirectory))
            {
                return Path.Combine(assemblyDirectory, "manifest.xml");
            }
        }
        catch
        {
        }

        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "manifest.xml");
    }

    private static string ReadAssemblyVersion()
    {
        if (!string.IsNullOrWhiteSpace(GeneratedPluginVersion.Value))
        {
            return GeneratedPluginVersion.Value;
        }

        try
        {
            var assembly = typeof(PluginVersionResolver).Assembly;
            var informationalVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informationalVersion))
            {
                return informationalVersion;
            }

            var version = assembly.GetName().Version;
            if (version != null)
            {
                if (version.Revision > 0)
                {
                    return version.ToString();
                }

                if (version.Build >= 0)
                {
                    return string.Format("{0}.{1}.{2}", version.Major, version.Minor, version.Build);
                }

                return string.Format("{0}.{1}", version.Major, version.Minor);
            }
        }
        catch
        {
        }

        return "0.0.0";
    }
}
