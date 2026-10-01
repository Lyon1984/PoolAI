using System.Text.Json;
using System.Text.Json.Nodes;

namespace PoolAI.ContractTests;

// The canonical SSE validator defines these identity, order and delta invariants.
public sealed class ResponsesStreamLifecycleContractTests
{
    [Theory]
    [InlineData(false, 4, "item_id", "\"unknown\"")]
    [InlineData(false, 4, "output_index", "1")]
    [InlineData(false, 4, "content_index", "1")]
    [InlineData(false, 5, "text", "\"changed\"")]
    [InlineData(false, 6, "part/text", "\"changed\"")]
    [InlineData(false, 7, "item/id", "\"changed\"")]
    [InlineData(false, 7, "item/content", "[]")]
    [InlineData(false, 8, "response/output", "[]")]
    [InlineData(false, 8, "response/output/0/content/0/text", "\"changed\"")]
    [InlineData(true, 2, "item/arguments", "\"seed\"")]
    [InlineData(true, 3, "item_id", "\"unknown\"")]
    [InlineData(true, 5, "name", "\"changed\"")]
    [InlineData(true, 5, "arguments", "\"{}\"")]
    [InlineData(true, 6, "item/call_id", "\"changed\"")]
    [InlineData(true, 6, "item/name", "\"changed\"")]
    [InlineData(true, 7, "response/output", "[]")]
    public async Task IdentitiesAndCompletedPayloadsMustMatchTheActualStream(bool function, int index, string path, string json)
    {
        List<JsonObject> frames = Frames(function);
        Set(frames[index], path, JsonNode.Parse(json));
        await AssertProtocolFailureAsync(frames);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(false, 5)]
    [InlineData(false, 6)]
    [InlineData(false, 7)]
    [InlineData(true, 2)]
    [InlineData(true, 5)]
    [InlineData(true, 6)]
    public async Task LifecycleTransitionsCannotRepeatEvenWithContiguousSequenceNumbers(bool function, int index)
    {
        List<JsonObject> frames = Frames(function);
        frames.Insert(index + 1, frames[index].DeepClone().AsObject());
        await AssertProtocolFailureAsync(frames);
    }

    [Theory]
    [InlineData("missing-progress")]
    [InlineData("delta-after-done")]
    [InlineData("unfinished-item")]
    [InlineData("duplicate-id")]
    [InlineData("function-on-message")]
    [InlineData("text-on-function")]
    [InlineData("part-before-text-done")]
    [InlineData("arguments-after-done")]
    public async Task CrossItemAndPrematureTerminalTransitionsAreRejected(string scenario)
    {
        bool function = scenario is "text-on-function" or "arguments-after-done";
        List<JsonObject> frames = Frames(function);
        switch (scenario)
        {
            case "missing-progress": frames.RemoveAt(1); break;
            case "delta-after-done": frames.Insert(6, frames[4].DeepClone().AsObject()); break;
            case "unfinished-item": frames.RemoveAt(7); break;
            case "duplicate-id":
                JsonObject duplicate = frames[2].DeepClone().AsObject();
                duplicate["output_index"] = 1;
                frames.Insert(3, duplicate);
                break;
            case "function-on-message":
                frames.Insert(3, JsonNode.Parse("{\"type\":\"response.function_call_arguments.delta\",\"item_id\":\"msg_0190f902\",\"output_index\":0,\"delta\":\"{}\"}")!.AsObject());
                break;
            case "text-on-function":
                frames.Insert(3, JsonNode.Parse("{\"type\":\"response.content_part.added\",\"item_id\":\"fc_0190f932\",\"output_index\":0,\"content_index\":0,\"part\":{\"type\":\"output_text\",\"text\":\"\",\"annotations\":[],\"logprobs\":[]}}")!.AsObject());
                break;
            case "part-before-text-done": frames.RemoveAt(5); break;
            case "arguments-after-done": frames.Insert(6, frames[3].DeepClone().AsObject()); break;
        }
        await AssertProtocolFailureAsync(frames);
    }

    [Fact]
    public async Task LifecycleStateIsBoundedIndependentlyOfIndividualFrameSize()
    {
        List<JsonObject> frames = Frames(false).Take(4).ToList();
        JsonObject delta = Frames(false)[4];
        for (int index = 0; index < 10; index++)
        {
            JsonObject value = delta.DeepClone().AsObject();
            value["delta"] = new string('x', 900_000);
            frames.Add(value);
        }
        await AssertProtocolFailureAsync(frames, chunkSize: 16_384);

        foreach (bool parts in new[] { false, true })
        {
            List<JsonObject> bounded = Frames(false).Take(parts ? 3 : 2).ToList();
            JsonObject seed = Frames(false)[parts ? 3 : 2];
            for (int index = 0; index <= 4096; index++)
            {
                JsonObject value = seed.DeepClone().AsObject();
                value[parts ? "content_index" : "output_index"] = index;
                if (!parts) { value["item"]!["id"] = "msg_" + index; }
                bounded.Add(value);
            }
            await AssertProtocolFailureAsync(bounded, chunkSize: 16_384);
        }
    }

    private static List<JsonObject> Frames(bool function) => ResponsesTestAssets.WireFixture(function
            ? "responses-stream-function-call.sse" : "responses-stream-completed.sse")
        .Split('\n').Where(static line => line.StartsWith("data: ", StringComparison.Ordinal))
        .Select(static line => JsonNode.Parse(line[6..])!.AsObject()).ToList();

    private static void Set(JsonNode value, string path, JsonNode? replacement)
    {
        string[] parts = path.Split('/');
        foreach (string part in parts[..^1])
        {
            value = value is JsonArray array ? array[int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)]! : value[part]!;
        }
        value[parts[^1]] = replacement;
    }

    private static async Task AssertProtocolFailureAsync(List<JsonObject> frames, int chunkSize = 3)
    {
        int sequence = 0;
        string wire = string.Concat(frames.Select(value =>
        {
            value["sequence_number"] = sequence++;
            return "event: " + value["type"]!.GetValue<string>() + "\ndata: " + value.ToJsonString() + "\n\n";
        }));
        var prepared = await ResponsesTestAssets.PrepareAsync(true).ConfigureAwait(false);
        await using (prepared.ConfigureAwait(false))
        {
            using var content = new ResponsesTestAssets.ChunkedStream(wire, chunkSize);
            var result = await prepared.ParseResponseAsync(ResponsesTestAssets.Response(content, true), TestContext.Current.CancellationToken).ConfigureAwait(false);
            Assert.True(result.IsFailure);
            Assert.Equal("upstream_protocol_error", result.Error.Code);
        }
    }
}
