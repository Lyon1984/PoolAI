using System.Text.Json;
using PoolAI.Adapters.OpenAI;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.ContractTests;

internal static class ChatTestAssets
{
    internal static JsonElement Request(bool stream = false, bool includeUsage = false) => JsonSerializer.SerializeToElement(new
    {
        model = "client-alias", messages = new[] { new { role = "user", content = "text" } }, stream,
        stream_options = new { include_usage = includeUsage },
    });

    internal static async ValueTask<IPreparedUpstreamAttempt> PrepareAsync(bool stream,
        bool includeUsage = false, IGatewayResponseOutput? output = null, UpstreamType upstream = UpstreamType.OpenAi,
        JsonElement? payload = null)
    {
        AdapterCapability capability = OpenAiCapabilityDescriptor.R1Capabilities.Single(capability =>
            capability.Protocol == InboundProtocol.ChatCompletions && capability.Upstream == upstream
            && capability.Operation == (stream ? AdapterOperation.Stream : AdapterOperation.NonStream));
        var result = await new ChatUpstreamAdapter(capability).PrepareAsync(ResponsesTestAssets.Context(),
            new NormalizedGatewayRequest(EntityId.New(), "client-alias", stream, payload ?? Request(stream, includeUsage))
            { Output = output }, TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    internal static JsonElement Completion() => JsonSerializer.SerializeToElement(new
    {
        id = "chat_test", @object = "chat.completion", created = 1783987200, model = "provider-model",
        choices = new[] { new { index = 0, message = new { role = "assistant", content = "text" }, finish_reason = "stop" } },
        usage = new { prompt_tokens = 8, completion_tokens = 2, total_tokens = 10 },
    });

    internal sealed class RecordingOutput : IGatewayResponseOutput
    {
        internal List<JsonElement> Events { get; } = [];
        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask WriteEventAsync(string eventName, JsonElement payload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("chat.completion.chunk", eventName);
            Assert.Equal(eventName, payload.GetProperty("object").GetString());
            Events.Add(payload.Clone());
            return ValueTask.CompletedTask;
        }
    }
}
