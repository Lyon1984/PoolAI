using System.Text.Json;
using System.Text.Json.Nodes;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.Adapters.OpenAI;

public sealed class ResponsesUpstreamAdapter(AdapterCapability capability) : IUpstreamAdapter
{
    public AdapterCapability Capability { get; } = capability ?? throw new ArgumentNullException(nameof(capability));

    public ValueTask<Result<IPreparedUpstreamAttempt>> PrepareAsync(
        AdapterAttemptContext attempt,
        NormalizedGatewayRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (Capability.Protocol != InboundProtocol.Responses
            || request.Stream != (Capability.Operation == AdapterOperation.Stream)
            || !string.Equals(attempt.Route.ClientModel, request.Model, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(Result.Failure<IPreparedUpstreamAttempt>(
                "invalid_request", "The Responses Adapter capability does not match the request."));
        }

        JsonObject payload = JsonNode.Parse(request.Payload.GetRawText())!.AsObject();
        payload["model"] = attempt.Route.UpstreamModel;
        Uri baseUri = attempt.Route.UpstreamBaseUri;
        Uri target = new(baseUri.AbsoluteUri.TrimEnd('/') + "/responses", UriKind.Absolute);
        IPreparedUpstreamAttempt prepared = new PreparedResponsesAttempt(
            target, payload, request.Model, request.Stream, request.Output);
        return ValueTask.FromResult(Result.Success(prepared));
    }

    private sealed class PreparedResponsesAttempt(
        Uri target,
        JsonObject body,
        string clientModel,
        bool stream,
        IGatewayResponseOutput? output) : IPreparedUpstreamAttempt
    {
        private JsonObject? _body = body;
        private int _created;
        private int _parsed;

        public ValueTask<Result<PreparedUpstreamRequest>> CreateRequestAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _created, 1) != 0 || _body is null)
            {
                return ValueTask.FromResult(Result.Failure<PreparedUpstreamRequest>(
                    "upstream_protocol_error", "The prepared request is no longer available."));
            }

            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(_body);
            try
            {
                return ValueTask.FromResult(Result.Success(new PreparedUpstreamRequest(
                    HttpMethod.Post, target, bytes,
                    new[] { new PreparedUpstreamHeader("Accept", stream ? "text/event-stream" : "application/json") })));
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
                _body = null;
            }
        }

        public ValueTask<Result<NormalizedUpstreamResult>> ParseResponseAsync(
            AdapterUpstreamResponse response,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(response);
            if (Interlocked.Exchange(ref _parsed, 1) != 0)
            {
                return ValueTask.FromResult(Result.Failure<NormalizedUpstreamResult>(
                    "upstream_protocol_error", "The upstream response is single-use."));
            }

            return ResponsesResponseParser.ParseAsync(response, clientModel, stream, output, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            _body = null;
            return ValueTask.CompletedTask;
        }
    }
}
