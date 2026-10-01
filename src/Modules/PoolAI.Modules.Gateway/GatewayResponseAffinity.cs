using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Abstractions;
using PoolAI.Modules.Routing.Abstractions;

namespace PoolAI.Modules.Gateway.Application;

internal sealed class GatewayResponseAffinity(byte[] pepper, IRouteAffinityRecorder recorder) : IDisposable
{
    private readonly byte[] _pepper = pepper.ToArray();
    private readonly IRouteAffinityRecorder _recorder = recorder;

    internal string? ForRequest(GatewayCanonicalAccess access, NormalizedGatewayRequest request) =>
        request.Payload.ValueKind == JsonValueKind.Object
        && request.Payload.TryGetProperty("previous_response_id", out JsonElement previous)
        && previous.ValueKind == JsonValueKind.String && previous.GetString() is { Length: >= 1 and <= 200 } id
            ? Hash(access.Group.GroupId, access.ApiKey.ApiKeyId, id) : null;

    internal async ValueTask RememberAsync(GatewayCanonicalAccess access, AccountRoute route,
        NormalizedUpstreamResult? result, CancellationToken cancellationToken)
    {
        if (result is not { ErrorCode: null } || result.Payload.ValueKind != JsonValueKind.Object
            || !result.Payload.TryGetProperty("id", out var responseId)
            || responseId.ValueKind != JsonValueKind.String || responseId.GetString() is not { Length: >= 1 and <= 200 } id)
        {
            return;
        }

        try
        {
            await _recorder.StoreAsync(access.Group.GroupId,
                Hash(access.Group.GroupId, access.ApiKey.ApiKeyId, id), route.AccountId,
                access.Group.Version, route.SupplyConfigurationVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Affinity is advisory. An immutable settlement is never undone by
            // a failed Redis write; the next request still rechecks Supply.
        }
    }

    internal string Hash(EntityId groupId, EntityId apiKeyId, string responseId)
    {
        byte[] input = Encoding.UTF8.GetBytes(string.Concat("PoolAI:ResponsesAffinity:v1\0",
            groupId.Value.ToString("D"), "\0", apiKeyId.Value.ToString("D"), "\0", responseId));
        try
        {
            return Convert.ToHexStringLower(HMACSHA256.HashData(_pepper, input).AsSpan(0, 16));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_pepper);
}
