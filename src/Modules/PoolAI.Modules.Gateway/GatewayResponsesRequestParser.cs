using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.Modules.Gateway.Application;

public sealed class GatewayResponsesRequestParser(IEnumerable<IProtocolAdapter> adapters)
{
    private readonly IProtocolAdapter _adapter = adapters.Single(adapter =>
        adapter.Capability.Protocol == InboundProtocol.Responses);

    public async ValueTask<Result<NormalizedGatewayRequest>> ParseAsync(
        Stream replay, EntityId requestId, CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(replay,
                new JsonDocumentOptions { MaxDepth = 64 }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Result<NormalizedGatewayRequest> result = await _adapter.NormalizeAsync(document.RootElement,
                cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? Result.Success(result.Value with { RequestId = requestId }) : result;
        }
        catch (JsonException)
        {
            return Result.Failure<NormalizedGatewayRequest>("invalid_request", "The request JSON is invalid.");
        }
    }
}
