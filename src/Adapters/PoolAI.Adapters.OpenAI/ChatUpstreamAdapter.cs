using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.Adapters.OpenAI;

public sealed class ChatUpstreamAdapter(AdapterCapability capability) : IUpstreamAdapter
{
    public AdapterCapability Capability { get; } = capability ?? throw new ArgumentNullException(nameof(capability));

    public ValueTask<Result<IPreparedUpstreamAttempt>> PrepareAsync(
        AdapterAttemptContext attempt, NormalizedGatewayRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (Capability.Protocol != InboundProtocol.ChatCompletions
            || Capability.Operation is not (AdapterOperation.NonStream or AdapterOperation.Stream)
            || request.Stream != (Capability.Operation == AdapterOperation.Stream)
            || !string.Equals(attempt.Route.ClientModel, request.Model, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(Result.Failure<IPreparedUpstreamAttempt>(
                "invalid_request", "The Chat Adapter capability does not match the request."));
        }

        JsonObject payload = JsonNode.Parse(request.Payload.GetRawText())!.AsObject();
        payload["model"] = attempt.Route.UpstreamModel;
        bool includeUsage = payload["stream_options"]?["include_usage"]?.GetValue<bool>() ?? false;
        bool includeObfuscation = payload["stream_options"]?["include_obfuscation"]?.GetValue<bool>() ?? true;
        if (request.Stream)
        {
            // Ask for authoritative accounting evidence regardless of the public
            // projection. include_usage=false still emits no public usage chunk.
            JsonObject options = payload["stream_options"] as JsonObject ?? new JsonObject();
            options["include_usage"] = true;
            payload["stream_options"] = options;
        }
        Uri target = new(attempt.Route.UpstreamBaseUri.AbsoluteUri.TrimEnd('/') + "/chat/completions", UriKind.Absolute);
        IPreparedUpstreamAttempt prepared = new PreparedChatAttempt(target, payload,
            request.Model, request.Stream, includeUsage, includeObfuscation, request.Output);
        return ValueTask.FromResult(Result.Success(prepared));
    }

    private sealed class PreparedChatAttempt(Uri target, JsonObject body, string clientModel,
        bool stream, bool includeUsage, bool includeObfuscation, IGatewayResponseOutput? output) : IPreparedUpstreamAttempt
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
                return ValueTask.FromResult(Result.Success(new PreparedUpstreamRequest(HttpMethod.Post, target,
                    bytes, new[] { new PreparedUpstreamHeader("Accept", stream ? "text/event-stream" : "application/json") })));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
                _body = null;
            }
        }

        public ValueTask<Result<NormalizedUpstreamResult>> ParseResponseAsync(
            AdapterUpstreamResponse response, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(response);
            if (Interlocked.Exchange(ref _parsed, 1) != 0)
            {
                return ValueTask.FromResult(Result.Failure<NormalizedUpstreamResult>(
                    "upstream_protocol_error", "The upstream response is single-use."));
            }
            return ChatResponseParser.ParseAsync(response, clientModel, stream,
                includeUsage, includeObfuscation, output, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            _body = null;
            return ValueTask.CompletedTask;
        }
    }
}
