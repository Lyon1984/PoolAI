using System.Text;
using System.Text.Json;
using PoolAI.Adapters.OpenAI;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.ContractTests;

internal static class ResponsesTestAssets
{
    private static readonly string[] RequestIdHeader = ["req_responses_test"];

    internal static string Fixture(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PoolAI.sln")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllText(Path.Combine(directory!.FullName, "docs", "contracts", "fixtures", name));
    }

    // Canonical fixture files use a single final newline. The wire stream needs
    // the blank line delimiter; an EOF without it remains an incomplete frame.
    internal static string WireFixture(string name) => Fixture(name).TrimEnd('\n') + "\n\n";

    internal static JsonElement CompletedResponse()
    {
        string line = Fixture("responses-stream-completed.sse").Split('\n')
            .Last(static line => line.StartsWith("data: ", StringComparison.Ordinal));
        using JsonDocument document = JsonDocument.Parse(line[6..]);
        return document.RootElement.GetProperty("response").Clone();
    }

    internal static AdapterAttemptContext Context() => new(EntityId.New(), EntityId.New(), 0,
        new AdapterRouteSnapshot(EntityId.New(), EntityId.New(), EntityId.New(), UpstreamType.OpenAi,
            "client-alias", "upstream-model", new Uri("https://upstream.example/v1/"),
            true, true, true, true, 1, 1, 1, 1), DateTimeOffset.MaxValue, 0);

    internal static async ValueTask<IPreparedUpstreamAttempt> PrepareAsync(bool stream,
        IGatewayResponseOutput? output = null, UpstreamType upstream = UpstreamType.OpenAi)
    {
        AdapterCapability capability = OpenAiCapabilityDescriptor.R1Capabilities.Single(c =>
            c.Protocol == InboundProtocol.Responses && c.Upstream == upstream
            && c.Operation == (stream ? AdapterOperation.Stream : AdapterOperation.NonStream));
        ResponsesUpstreamAdapter adapter = new(capability);
        var result = await adapter.PrepareAsync(Context(), new NormalizedGatewayRequest(EntityId.New(),
            "client-alias", stream, JsonSerializer.SerializeToElement(new { model = "client-alias", input = "text", stream }))
            { Output = output }, TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    internal static AdapterUpstreamResponse Response(Stream content, bool stream, int status = 200,
        string? mediaType = null) => new(status, content,
            new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = new[] { mediaType ?? (stream ? "text/event-stream" : "application/json") },
                ["x-request-id"] = RequestIdHeader,
            });

    internal sealed class RecordingOutput : IGatewayResponseOutput
    {
        internal List<JsonElement> Events { get; } = [];
        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask WriteEventAsync(string eventName, JsonElement payload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(eventName, payload.GetProperty("type").GetString());
            Events.Add(payload.Clone());
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class ChunkedStream(string value, int chunkSize) : MemoryStream(Encoding.UTF8.GetBytes(value))
    {
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
    }
}
