#nullable enable

using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Shared.Logging;

namespace ServerPlugin.Services;

public sealed class NexusClusterRedirectService
{
    private static readonly string[] PlayerTransportTypeNames =
    [
        "NGPlugin.BoundarySystem.PlayerTransportSync",
        "BoundarySystem.PlayerTransportSync"
    ];

    private static readonly string[] RegionHandlerTypeNames =
    [
        "NGPlugin.BoundarySystem.RegionHandler",
        "BoundarySystem.RegionHandler"
    ];

    private static readonly string[] CurrentServerIdMemberNames =
    [
        "ServerID",
        "ServerId",
        "ID",
        "Id"
    ];

    private static readonly string[] LobbyServerIdMemberNames =
    [
        "LobbyServerID",
        "LobbyServerId"
    ];

    private readonly IPluginLogger _log;

    public NexusClusterRedirectService(IPluginLogger log)
    {
        this._log = log;
    }

    public string? GetCurrentServerId()
    {
        return TryReadThisServerId(CurrentServerIdMemberNames);
    }

    public string? GetRedirectTargetServerId(string? configuredTargetServerId)
    {
        if (TryParseServerId(configuredTargetServerId, out var serverId))
        {
            return serverId.ToString(CultureInfo.InvariantCulture);
        }

        var fallback = TryReadThisServerId(LobbyServerIdMemberNames);
        return TryParseServerId(fallback, out serverId)
            ? serverId.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    public bool TryRedirectPlayer(ulong steamId, string? configuredTargetServerId, out string failureReason)
    {
        failureReason = string.Empty;
        if (!TryParseServerId(GetRedirectTargetServerId(configuredTargetServerId), out var targetServerId))
        {
            failureReason = "Lobby Nexus server id unavailable.";
            return false;
        }

        var method = FindPlayerTransportMethod();
        if (method is null)
        {
            failureReason = "Nexus transport API not loaded.";
            return false;
        }

        var parameters = method.GetParameters();
        if (parameters.Length != 2)
        {
            failureReason = "Nexus transport signature not supported.";
            return false;
        }

        try
        {
            var args = new[]
            {
                ConvertArgument(steamId, parameters[0].ParameterType),
                ConvertArgument(targetServerId, parameters[1].ParameterType)
            };

            method.Invoke(null, args);
            return true;
        }
        catch (Exception exception)
        {
            failureReason = exception.GetBaseException().Message;
            _log.Warning($"Nexus redirect invocation failed: {failureReason}");
            return false;
        }
    }

    private static bool TryParseServerId(string? rawValue, out byte serverId)
    {
        return byte.TryParse(rawValue?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out serverId) && serverId > 0;
    }

    private string? TryReadThisServerId(string[] memberNames)
    {
        if (!TryGetThisServerObject(out var thisServer) || thisServer is null)
        {
            return null;
        }

        foreach (var memberName in memberNames)
        {
            if (!TryReadMemberValue(thisServer, memberName, out var value))
            {
                continue;
            }

            if (TryConvertToByte(value, out var serverId))
            {
                return serverId.ToString(CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    private static bool TryGetThisServerObject(out object? thisServer)
    {
        foreach (var typeName in RegionHandlerTypeNames)
        {
            var type = FindType(typeName);
            if (type is null)
            {
                continue;
            }

            var property = type.GetProperty("ThisServer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (property is null)
            {
                continue;
            }

            thisServer = property.GetValue(null);
            if (thisServer is not null)
            {
                return true;
            }
        }

        thisServer = null;
        return false;
    }

    private static MethodInfo? FindPlayerTransportMethod()
    {
        foreach (var typeName in PlayerTransportTypeNames)
        {
            var type = FindType(typeName);
            if (type is null)
            {
                continue;
            }

            var method = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(x => string.Equals(x.Name, "SendPlayerTo", StringComparison.Ordinal) && x.GetParameters().Length == 2);
            if (method is not null)
            {
                return method;
            }
        }

        return null;
    }

    private static Type? FindType(string fullName)
    {
        return AppDomain.CurrentDomain
            .GetAssemblies()
            .Select(assembly => assembly.GetType(fullName, throwOnError: false, ignoreCase: false))
            .FirstOrDefault(type => type is not null);
    }

    private static bool TryReadMemberValue(object instance, string memberName, out object? value)
    {
        var type = instance.GetType();
        var property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (property is not null)
        {
            value = property.GetValue(instance);
            return true;
        }

        var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field is not null)
        {
            value = field.GetValue(instance);
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryConvertToByte(object? value, out byte serverId)
    {
        switch (value)
        {
            case byte byteValue when byteValue > 0:
                serverId = byteValue;
                return true;
            case sbyte sbyteValue when sbyteValue > 0:
                serverId = (byte)sbyteValue;
                return true;
            case short shortValue when shortValue > 0 && shortValue <= byte.MaxValue:
                serverId = (byte)shortValue;
                return true;
            case ushort ushortValue when ushortValue > 0 && ushortValue <= byte.MaxValue:
                serverId = (byte)ushortValue;
                return true;
            case int intValue when intValue > 0 && intValue <= byte.MaxValue:
                serverId = (byte)intValue;
                return true;
            case uint uintValue when uintValue > 0 && uintValue <= byte.MaxValue:
                serverId = (byte)uintValue;
                return true;
            case string stringValue when byte.TryParse(stringValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0:
                serverId = parsed;
                return true;
            default:
                serverId = 0;
                return false;
        }
    }

    private static object ConvertArgument(object value, Type targetType)
    {
        var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (underlyingType.IsInstanceOfType(value))
        {
            return value;
        }

        return Convert.ChangeType(value, underlyingType, CultureInfo.InvariantCulture);
    }
}
