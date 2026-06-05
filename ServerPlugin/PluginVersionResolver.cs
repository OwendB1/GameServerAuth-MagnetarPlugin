using System;
using System.Reflection;

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

            _cachedVersion = ReadAssemblyVersion();
            return _cachedVersion;
        }
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
