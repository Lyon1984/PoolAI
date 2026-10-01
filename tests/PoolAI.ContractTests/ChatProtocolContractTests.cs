using System.Text.Json;
using System.Text.Json.Nodes;
using PoolAI.Adapters.OpenAI;
using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.ContractTests;

// Governing assets: ChatCompletionRequest, ChatMessage and canonical tool-followup fixture.
public sealed class ChatProtocolContractTests
{
    [Theory]
    [InlineData("system")]
    [InlineData("developer")]
    [InlineData("user")]
    [InlineData("assistant")]
    public async Task TextRolesAndTextPartsFollowTheFrozenSchema(string role)
    {
        foreach (bool parts in new[] { false, true })
        {
            JsonObject request = JsonNode.Parse(ChatTestAssets.Request().GetRawText())!.AsObject();
            request["messages"]![0]!["role"] = role;
            request["messages"]![0]!["content"] = parts ? JsonNode.Parse("[{\"type\":\"text\",\"text\":\"文字\"}]") : JsonValue.Create("文字");
            var result = await NormalizeAsync(request.ToJsonString());
            Assert.True(result.IsSuccess);
            Assert.Equal("client-alias", result.Value.Model);
            Assert.False(result.Value.Stream);
            Assert.Equal(7, result.Value.RequestId.Value.Version);
            Assert.Equal(InboundProtocol.ChatCompletions, new ChatProtocolAdapter().Capability.Protocol);
        }
    }

    [Fact]
    public async Task CanonicalFunctionHistoryAndToolResultsAreNotExecutedOrRewritten()
    {
        string fixture = ResponsesTestAssets.Fixture("chat-completions-tool-followup.json");
        var result = await NormalizeAsync(fixture);
        Assert.True(result.IsSuccess);
        using JsonDocument source = JsonDocument.Parse(fixture);
        Assert.Equal(source.RootElement.GetRawText(), result.Value.Payload.GetRawText());
    }

    [Theory]
    [InlineData("null", "invalid_request")]
    [InlineData("[]", "invalid_request")]
    [InlineData("{\"model\":\"x\",\"messages\":[],\"model\":\"y\"}", "invalid_request")]
    [InlineData("{\"model\":\"x\",\"messages\":[{\"role\":\"user\",\"content\":\"x\",\"content\":\"y\"}]}", "invalid_request")]
    [InlineData("{\"model\":\"x\",\"messages\":[],\"n\":2}", "unsupported_feature")]
    [InlineData("{\"model\":\"x\",\"messages\":[],\"max_tokens\":10}", "unsupported_feature")]
    public async Task DuplicateNamesAndUnfrozenFeaturesFailBeforeBusinessWork(string json, string code)
    {
        var result = await NormalizeAsync(json);
        Assert.True(result.IsFailure);
        Assert.Equal(code, result.Error.Code);
    }

    [Theory]
    [InlineData("model", "null")]
    [InlineData("model", "\"\"")]
    [InlineData("messages", "null")]
    [InlineData("messages", "[]")]
    [InlineData("messages", "[null]")]
    [InlineData("messages", "[{\"role\":\"other\",\"content\":\"x\"}]")]
    [InlineData("messages", "[{\"role\":\"user\",\"content\":null}]")]
    [InlineData("messages", "[{\"role\":\"user\",\"content\":[]}]")]
    [InlineData("messages", "[{\"role\":\"user\",\"content\":[{\"type\":\"image_url\",\"text\":\"x\"}]}]")]
    [InlineData("messages", "[{\"role\":\"assistant\",\"content\":null}]")]
    [InlineData("messages", "[{\"role\":\"assistant\",\"tool_calls\":[]}]")]
    [InlineData("messages", "[{\"role\":\"tool\",\"content\":\"x\"}]")]
    [InlineData("messages", "[{\"role\":\"user\",\"content\":\"x\",\"name\":1}]")]
    [InlineData("stream", "null")]
    [InlineData("temperature", "2.1")]
    [InlineData("temperature", "-1")]
    [InlineData("top_p", "1.1")]
    [InlineData("top_p", "null")]
    [InlineData("tool_choice", "\"other\"")]
    [InlineData("tool_choice", "{\"type\":\"function\",\"function\":{}}")]
    [InlineData("stream_options", "null")]
    [InlineData("stream_options", "{\"include_usage\":1}")]
    [InlineData("stream_options", "{\"include_obfuscation\":null}")]
    [InlineData("stream_options", "{\"unknown\":false}")]
    public async Task InvalidFieldsUseTheCanonicalJsonPointer(string field, string value)
    {
        JsonObject request = JsonNode.Parse(ChatTestAssets.Request().GetRawText())!.AsObject();
        request[field] = JsonNode.Parse(value);
        var result = await NormalizeAsync(request.ToJsonString());
        Assert.True(result.IsFailure);
        Assert.Equal("validation_failed", result.Error.Code);
        Assert.Equal(422, result.Error.Presentation!.Status);
        Assert.Contains("/" + field, result.Error.Presentation.Errors!.Keys);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("1.0", true)]
    [InlineData("1e1", true)]
    [InlineData("9007199254740991", true)]
    [InlineData("9007199254740991.0", true)]
    [InlineData("9007199254740992", false)]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("1e100", false)]
    [InlineData("1e-100", false)]
    [InlineData("0.5", false)]
    [InlineData("\"1\"", false)]
    public async Task CompletionLimitNeverRoundsThroughFloatingPoint(string number, bool valid)
    {
        string request = "{\"model\":\"x\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"max_completion_tokens\":" + number + "}";
        var result = await NormalizeAsync(request);
        Assert.Equal(valid, result.IsSuccess);
    }

    [Theory]
    [InlineData("\"none\"")]
    [InlineData("\"auto\"")]
    [InlineData("\"required\"")]
    [InlineData("{\"type\":\"function\",\"function\":{\"name\":\"function\"}}")]
    public async Task FunctionParametersAndOptionalStreamSettingsRemainContractCompatible(string choice)
    {
        JsonObject request = JsonNode.Parse(ChatTestAssets.Request(true).GetRawText())!.AsObject();
        request["tool_choice"] = JsonNode.Parse(choice);
        request["tools"] = JsonNode.Parse("[{\"type\":\"function\",\"function\":{\"name\":\"function\",\"description\":\"test\",\"strict\":true,\"parameters\":{\"arbitrary\":[1,null,{\"nested\":true}]}}}]");
        request["stream_options"]!["include_obfuscation"] = false;
        var result = await NormalizeAsync(request.ToJsonString());
        Assert.True(result.IsSuccess);
        Assert.True(result.Value.Stream);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[{\"type\":\"web_search\"}]")]
    [InlineData("[{\"type\":\"function\",\"function\":{\"name\":\"x\",\"parameters\":[],\"strict\":true}}]")]
    public async Task NonFunctionToolsAreOutsideReleaseOne(string value)
    {
        JsonObject request = JsonNode.Parse(ChatTestAssets.Request().GetRawText())!.AsObject();
        request["tools"] = JsonNode.Parse(value);
        var result = await NormalizeAsync(request.ToJsonString());
        Assert.True(result.IsFailure);
        Assert.Equal("unsupported_feature", result.Error.Code);
    }

    [Theory]
    [InlineData(200, 64, true)]
    [InlineData(201, 64, false)]
    [InlineData(200, 65, false)]
    public async Task JsonSchemaLengthsCountUnicodeScalarsNotUtf16Units(int modelLength, int nameLength, bool valid)
    {
        JsonObject request = JsonNode.Parse(ChatTestAssets.Request().GetRawText())!.AsObject();
        request["model"] = string.Concat(Enumerable.Repeat("𠀀", modelLength));
        string name = string.Concat(Enumerable.Repeat("𠀀", nameLength));
        request["tools"] = JsonSerializer.SerializeToNode(new[] { new { type = "function",
            function = new { name, parameters = new { } } } });
        var result = await NormalizeAsync(request.ToJsonString());
        Assert.Equal(valid, result.IsSuccess);
    }

    private static async ValueTask<PoolAI.BuildingBlocks.Result<NormalizedGatewayRequest>> NormalizeAsync(string value)
    {
        using JsonDocument document = JsonDocument.Parse(value);
        return await new ChatProtocolAdapter().NormalizeAsync(document.RootElement, TestContext.Current.CancellationToken).ConfigureAwait(false);
    }
}
