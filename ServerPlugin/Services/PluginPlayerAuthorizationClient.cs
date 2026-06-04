using System;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;
using Contracts.Plugin;
using Shared.Logging;
using Shared.Plugin;

namespace ServerPlugin.Services;

public sealed class PluginPlayerAuthorizationClient
{
    private static readonly Uri AccessStatusServiceUri = new Uri("https://auth.odb-tech.com/api/plugin/access-status");
    private static readonly Uri AuthorizationServiceUri = new Uri("https://auth.odb-tech.com/api/plugin/authorize-player");
    private static readonly Uri PlayerJoinServiceUri = new Uri("https://auth.odb-tech.com/api/plugin/player-join");
    private static readonly Uri PlayerKickServiceUri = new Uri("https://auth.odb-tech.com/api/plugin/player-kick");

    private readonly IPluginLogger _log;

    public PluginPlayerAuthorizationClient(IPluginLogger log)
    {
        this._log = log;
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
    }

    public Task<PluginPlayerAuthorizationResponse> AuthorizeAsync(ulong steamId, string steamDisplayName, CancellationToken cancellationToken)
    {
        return SendAsync(AuthorizationServiceUri, steamId, steamDisplayName, cancellationToken);
    }

    public Task<PluginPlayerAuthorizationResponse> GetAccessStatusAsync(ulong steamId, string steamDisplayName, CancellationToken cancellationToken)
    {
        return SendAsync(AccessStatusServiceUri, steamId, steamDisplayName, cancellationToken);
    }

    public async Task ReportJoinAsync(ulong steamId, string steamDisplayName, CancellationToken cancellationToken)
    {
        await SendEventAsync(PlayerJoinServiceUri, steamId, steamDisplayName, "Player join activity", cancellationToken).ConfigureAwait(false);
    }

    public async Task ReportKickAsync(ulong steamId, string steamDisplayName, CancellationToken cancellationToken)
    {
        await SendEventAsync(PlayerKickServiceUri, steamId, steamDisplayName, "Player kick activity", cancellationToken).ConfigureAwait(false);
    }

    private async Task SendEventAsync(Uri endpoint, ulong steamId, string steamDisplayName, string eventName, CancellationToken cancellationToken)
    {
        PluginPlayerAuthorizationRequest requestPayload;
        string errorMessage;
        if (!TryBuildRequest(steamId, steamDisplayName, out requestPayload, out errorMessage))
        {
            _log.Warning($"{eventName} request skipped: {errorMessage}");
            return;
        }

        try
        {
            var request = WebRequest.CreateHttp(endpoint);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;

            using (var requestStream = await request.GetRequestStreamAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var serializer = new DataContractJsonSerializer(typeof(PluginPlayerAuthorizationRequest));
                serializer.WriteObject(requestStream, requestPayload);
            }

            using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
            {
                _log.Info($"{eventName} recorded for {steamId} ({(int)response.StatusCode} {response.StatusDescription})");
            }
        }
        catch (WebException exception) when (exception.Response is HttpWebResponse response)
        {
            using (response)
            using (var responseStream = response.GetResponseStream())
            using (var reader = responseStream == null ? null : new StreamReader(responseStream))
            {
                var responseText = reader == null ? string.Empty : (await reader.ReadToEndAsync().ConfigureAwait(false)).Trim();
                _log.Warning(
                    $"{eventName} request failed for {steamId}: {(int)response.StatusCode} {response.StatusDescription}. {responseText}");
            }
        }
        catch (WebException exception)
        {
            _log.Warning($"{eventName} request failed for {steamId}: {exception.Message}");
        }
        catch (Exception exception)
        {
            _log.Warning($"{eventName} request failed for {steamId}: {exception.Message}");
        }
    }

    private Task<PluginPlayerAuthorizationResponse> SendAsync(Uri endpoint, ulong steamId, string steamDisplayName, CancellationToken cancellationToken)
    {
        PluginPlayerAuthorizationRequest requestPayload;
        string errorMessage;
        if (!TryBuildRequest(steamId, steamDisplayName, out requestPayload, out errorMessage))
        {
            return Task.FromResult(new PluginPlayerAuthorizationResponse
            {
                Status = PluginPlayerAuthorizationStatus.Error,
                Message = errorMessage
            });
        }

        return SendAsync(endpoint, requestPayload, cancellationToken);
    }

    private async Task<PluginPlayerAuthorizationResponse> SendAsync(
        Uri endpoint,
        PluginPlayerAuthorizationRequest payload,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = WebRequest.CreateHttp(endpoint);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;

            using (var requestStream = await request.GetRequestStreamAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var serializer = new DataContractJsonSerializer(typeof(PluginPlayerAuthorizationRequest));
                serializer.WriteObject(requestStream, payload);
            }

            using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
            using (var responseStream = response.GetResponseStream())
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await ReadAuthorizationResponseAsync(
                        responseStream,
                        "Authorization service returned an empty response.",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (WebException exception) when (exception.Response is HttpWebResponse response)
        {
            var fallbackMessage = $"Authorization service rejected the request ({(int)response.StatusCode} {response.StatusDescription}).";
            using (response)
            using (var responseStream = response.GetResponseStream())
            {
                var failedResponse = await ReadAuthorizationResponseAsync(responseStream, fallbackMessage, cancellationToken).ConfigureAwait(false);
                _log.Warning($"Player authorization request failed: {failedResponse.Message}");
                return failedResponse;
            }
        }
        catch (WebException exception)
        {
            _log.Warning($"Player authorization request failed: {exception.Message}");
            return new PluginPlayerAuthorizationResponse
            {
                Status = PluginPlayerAuthorizationStatus.Error,
                Message = "Authorization service request failed."
            };
        }
        catch (Exception exception)
        {
            _log.Warning($"Player authorization request failed: {exception.Message}");
            return new PluginPlayerAuthorizationResponse
            {
                Status = PluginPlayerAuthorizationStatus.Error,
                Message = exception.Message
            };
        }
    }

    private static async Task<PluginPlayerAuthorizationResponse> ReadAuthorizationResponseAsync(
        Stream responseStream,
        string fallbackMessage,
        CancellationToken cancellationToken)
    {
        if (responseStream == null)
        {
            return new PluginPlayerAuthorizationResponse
            {
                Status = PluginPlayerAuthorizationStatus.Error,
                Message = fallbackMessage
            };
        }

        using (var buffer = new MemoryStream())
        {
            await responseStream.CopyToAsync(buffer).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.Length == 0)
            {
                return new PluginPlayerAuthorizationResponse
                {
                    Status = PluginPlayerAuthorizationStatus.Error,
                    Message = fallbackMessage
                };
            }

            var parsed = TryReadAuthorizationResponse(buffer) ?? TryReadCamelCaseAuthorizationResponse(buffer);
            if (parsed != null)
            {
                NormalizeAuthorizationResponse(parsed, fallbackMessage);
                return parsed;
            }

            buffer.Position = 0;
            using (var reader = new StreamReader(buffer))
            {
                var text = (await reader.ReadToEndAsync().ConfigureAwait(false)).Trim();
                return new PluginPlayerAuthorizationResponse
                {
                    Status = PluginPlayerAuthorizationStatus.Error,
                    Message = string.IsNullOrWhiteSpace(text) || LooksLikeHtml(text)
                        ? fallbackMessage
                        : text
                };
            }
        }
    }

    private static PluginPlayerAuthorizationResponse TryReadAuthorizationResponse(MemoryStream buffer)
    {
        buffer.Position = 0;
        try
        {
            var serializer = new DataContractJsonSerializer(typeof(PluginPlayerAuthorizationResponse));
            var parsed = serializer.ReadObject(buffer) as PluginPlayerAuthorizationResponse;
            return HasMeaningfulAuthorizationResponse(parsed) ? parsed : null;
        }
        catch (SerializationException)
        {
            return null;
        }
    }

    private static PluginPlayerAuthorizationResponse TryReadCamelCaseAuthorizationResponse(MemoryStream buffer)
    {
        buffer.Position = 0;
        try
        {
            var serializer = new DataContractJsonSerializer(typeof(CamelCasePluginPlayerAuthorizationResponse));
            var parsed = serializer.ReadObject(buffer) as CamelCasePluginPlayerAuthorizationResponse;
            return parsed != null && HasMeaningfulAuthorizationResponse(parsed.ToContracts())
                ? parsed.ToContracts()
                : null;
        }
        catch (SerializationException)
        {
            return null;
        }
    }

    private static bool HasMeaningfulAuthorizationResponse(PluginPlayerAuthorizationResponse response)
    {
        return response != null &&
               (response.Status != PluginPlayerAuthorizationStatus.Error ||
                !string.IsNullOrWhiteSpace(response.Message) ||
                !string.IsNullOrWhiteSpace(response.AuthorizationUrl) ||
                !string.IsNullOrWhiteSpace(response.ClusterName) ||
                !string.IsNullOrWhiteSpace(response.Instructions) ||
                !string.IsNullOrWhiteSpace(response.RequiredDiscordRoleName));
    }

    private static void NormalizeAuthorizationResponse(PluginPlayerAuthorizationResponse response, string fallbackMessage)
    {
        if (!string.IsNullOrWhiteSpace(response.Instructions))
        {
            response.Instructions = response.Instructions.Trim();
        }

        if (!string.IsNullOrWhiteSpace(response.RequiredDiscordRoleName))
        {
            response.RequiredDiscordRoleName = response.RequiredDiscordRoleName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(response.Message))
        {
            response.Message = response.Message.Trim();
            return;
        }

        switch (response.Status)
        {
            case PluginPlayerAuthorizationStatus.Authorized:
                response.Message = "Cluster authorization stored.";
                break;
            case PluginPlayerAuthorizationStatus.AlreadyAuthorized:
                response.Message = "Already authorized for this cluster.";
                break;
            case PluginPlayerAuthorizationStatus.RequiresLink:
                response.Message = "Discord and Steam are not linked yet.";
                break;
            case PluginPlayerAuthorizationStatus.RequiresAccept:
                response.Message = "Discord and Steam are linked. Run !accept to authorize this cluster.";
                break;
            case PluginPlayerAuthorizationStatus.AccessBlocked:
                response.Message = "Access blocked by cluster owner.";
                break;
            case PluginPlayerAuthorizationStatus.RequiresGuildJoin:
                response.Message = "Join the Discord guild that owns this cluster, then return to the game and run !accept again.";
                break;
            case PluginPlayerAuthorizationStatus.RequiresRole:
                response.Message = "Acquire the required Discord role in the owning guild, then return to the game and run !accept again.";
                break;
            default:
                response.Message = fallbackMessage;
                break;
        }
    }

    private static bool LooksLikeHtml(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.TrimStart();
        return trimmed.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<body", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<head", StringComparison.OrdinalIgnoreCase);
    }

    [DataContract]
    private sealed class CamelCasePluginPlayerAuthorizationResponse
    {
        [DataMember(Name = "status", Order = 1)]
        public PluginPlayerAuthorizationStatus Status { get; set; }

        [DataMember(Name = "message", Order = 2)]
        public string Message { get; set; } = string.Empty;

        [DataMember(Name = "authorizationUrl", Order = 3)]
        public string AuthorizationUrl { get; set; } = string.Empty;

        [DataMember(Name = "clusterName", Order = 4)]
        public string ClusterName { get; set; } = string.Empty;

        [DataMember(Name = "instructions", Order = 5)]
        public string Instructions { get; set; } = string.Empty;

        [DataMember(Name = "requiredDiscordRoleName", Order = 6)]
        public string RequiredDiscordRoleName { get; set; } = string.Empty;

        public PluginPlayerAuthorizationResponse ToContracts()
        {
            return new PluginPlayerAuthorizationResponse
            {
                Status = Status,
                Message = Message,
                AuthorizationUrl = string.IsNullOrWhiteSpace(AuthorizationUrl) ? null : AuthorizationUrl,
                ClusterName = string.IsNullOrWhiteSpace(ClusterName) ? null : ClusterName,
                Instructions = string.IsNullOrWhiteSpace(Instructions) ? null : Instructions,
                RequiredDiscordRoleName = string.IsNullOrWhiteSpace(RequiredDiscordRoleName) ? null : RequiredDiscordRoleName
            };
        }
    }

    private static bool TryBuildRequest(ulong steamId, string steamDisplayName, out PluginPlayerAuthorizationRequest request, out string errorMessage)
    {
        request = null;
        errorMessage = string.Empty;

        ulong guildId;
        if (!ulong.TryParse(Common.Config.DiscordGuildId?.Trim(), out guildId) || guildId == 0)
        {
            errorMessage = "Discord guild ID missing in plugin config.";
            return false;
        }

        var clusterId = Common.Config.ClusterId?.Trim();
        if (string.IsNullOrWhiteSpace(clusterId))
        {
            errorMessage = "Cluster GUID missing in plugin config.";
            return false;
        }

        request = new PluginPlayerAuthorizationRequest
        {
            ServerId = Common.Config.ServerId.Trim(),
            DiscordGuildId = guildId,
            ClusterId = clusterId,
            SteamId = steamId,
            SteamDisplayName = string.IsNullOrWhiteSpace(steamDisplayName) ? null : steamDisplayName.Trim(),
            IssuedAtUnixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Nonce = Guid.NewGuid().ToString("N")
        };

        var clusterSecret = Common.Config.ClusterSecret?.Trim();
        if (string.IsNullOrWhiteSpace(request.ServerId))
        {
            errorMessage = "Server ID missing in plugin config.";
            request = null;
            return false;
        }

        if (string.IsNullOrWhiteSpace(clusterSecret))
        {
            errorMessage = "Cluster secret missing in plugin config.";
            request = null;
            return false;
        }

        request.Signature = PluginRequestSigning.CreateAuthorizationSignature(clusterSecret, request);
        return true;
    }
}
