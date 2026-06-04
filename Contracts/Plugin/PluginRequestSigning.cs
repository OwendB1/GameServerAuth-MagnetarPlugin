using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Contracts.Plugin;

public static class PluginRequestSigning
{
    public static string CreateAuthorizationSignature(string sharedSecret, PluginPlayerAuthorizationRequest request)
    {
        return ComputeSignature(sharedSecret, string.Join(
            "\n",
            "authorize-v1",
            request.ServerId ?? string.Empty,
            request.DiscordGuildId.ToString(CultureInfo.InvariantCulture),
            request.ClusterId ?? string.Empty,
            request.SteamId.ToString(CultureInfo.InvariantCulture),
            request.IssuedAtUnixTimeSeconds.ToString(CultureInfo.InvariantCulture),
            request.Nonce ?? string.Empty));
    }

    public static string CreateHelloSignature(string sharedSecret, PluginHelloPayload hello)
    {
        return ComputeSignature(sharedSecret, string.Join(
            "\n",
            "hello-v2",
            hello.ServerId ?? string.Empty,
            hello.DiscordGuildId.ToString(CultureInfo.InvariantCulture),
            hello.ClusterId ?? string.Empty,
            hello.NodeName ?? string.Empty,
            ((int)hello.NodeRole).ToString(CultureInfo.InvariantCulture),
            hello.PluginVersion ?? string.Empty,
            hello.GameVersion ?? string.Empty,
            hello.NexusServerId ?? string.Empty,
            hello.IssuedAtUnixTimeSeconds.ToString(CultureInfo.InvariantCulture),
            hello.Nonce ?? string.Empty));
    }

    public static bool VerifyAuthorizationSignature(string sharedSecret, PluginPlayerAuthorizationRequest request)
    {
        return FixedTimeEquals(CreateAuthorizationSignature(sharedSecret, request), request.Signature);
    }

    public static bool VerifyHelloSignature(string sharedSecret, PluginHelloPayload hello)
    {
        return FixedTimeEquals(CreateHelloSignature(sharedSecret, hello), hello.Signature);
    }

    private static string ComputeSignature(string sharedSecret, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(sharedSecret ?? string.Empty));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    private static bool FixedTimeEquals(string left, string? right)
    {
        if (right is null)
        {
            return false;
        }

        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        if (leftBytes.Length != rightBytes.Length)
        {
            return false;
        }

        var diff = 0;
        for (var index = 0; index < leftBytes.Length; index++)
        {
            diff |= leftBytes[index] ^ rightBytes[index];
        }

        return diff == 0;
    }
}
