using System.Text.Json;
using System.Text.Json.Nodes;
using PoolAI.Adapters.OpenAI;
using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.ContractTests;

public sealed class ResponsesProtocolContractTests
{
    [Theory]
    [InlineData("{\"model\":\"test\",\"input\":\"text\"}", false)]
    [InlineData("{\"model\":\"test\",\"input\":\"text\",\"stream\":true}", true)]
    [InlineData("{\"model\":\"test\",\"input\":[{\"role\":\"user\",\"content\":\"text\"}]}", false)]
    [InlineData("{\"model\":\"test\",\"input\":[{\"type\":\"message\",\"role\":\"developer\",\"content\":[{\"type\":\"input_text\",\"text\":\"text\"}]}]}", false)]
    [InlineData("{\"model\":\"test\",\"input\":[{\"type\":\"function_call\",\"call_id\":\"call_1\",\"name\":\"function\",\"arguments\":\"{}\"},{\"type\":\"function_call_output\",\"call_id\":\"call_1\",\"output\":\"done\"}]}", false)]
    public async Task TextMessagesAndFunctionHistoryProduceImmutableNormalizedRequests(string json, bool stream)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        ResponsesProtocolAdapter adapter = new();
        var result = await adapter.NormalizeAsync(document.RootElement, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.Equal(InboundProtocol.Responses, adapter.Capability.Protocol);
        Assert.Equal("test", result.Value.Model);
        Assert.Equal(stream, result.Value.Stream);
        Assert.Equal(7, result.Value.RequestId.Value.Version);
        Assert.Equal(document.RootElement.GetRawText(), result.Value.Payload.GetRawText());
    }

    [Theory]
    [InlineData("null", "invalid_request")]
    [InlineData("[]", "invalid_request")]
    [InlineData("{\"model\":\"test\",\"input\":\"x\",\"input\":\"y\"}", "invalid_request")]
    [InlineData("{\"model\":\"test\",\"input\":\"x\",\"metadata\":{\"a\":\"1\",\"a\":\"2\"}}", "invalid_request")]
    [InlineData("{\"model\":\"test\",\"input\":\"x\",\"store\":true}", "unsupported_feature")]
    [InlineData("{\"model\":\"test\",\"input\":\"x\",\"tools\":[{\"type\":\"web_search\"}]}", "unsupported_feature")]
    [InlineData("{\"model\":\"test\",\"input\":\"x\",\"tools\":null}", "unsupported_feature")]
    [InlineData("{\"model\":\"test\",\"input\":\"x\",\"include\":[\"unsupported\"]}", "unsupported_feature")]
    [InlineData("{\"model\":\"test\",\"input\":\"x\",\"include\":true}", "unsupported_feature")]
    public async Task DuplicateNamesAndUnfrozenFeaturesDoNotReachTheProcess(string json, string code)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        var result = await new ResponsesProtocolAdapter().NormalizeAsync(document.RootElement, TestContext.Current.CancellationToken);
        Assert.True(result.IsFailure);
        Assert.Equal(code, result.Error.Code);
    }

    [Theory]
    [InlineData("model", "null")]
    [InlineData("model", "\"\"")]
    [InlineData("model", "\" test\"")]
    [InlineData("model", "3")]
    [InlineData("input", "null")]
    [InlineData("input", "[]")]
    [InlineData("input", "[null]")]
    [InlineData("input", "[{\"role\":\"invalid\",\"content\":\"x\"}]")]
    [InlineData("input", "[{\"role\":\"user\",\"content\":[]}]")]
    [InlineData("input", "[{\"role\":\"user\",\"content\":[{\"type\":\"input_image\",\"image_url\":\"x\"}]}]")]
    [InlineData("input", "[{\"type\":null,\"role\":\"user\",\"content\":\"x\"}]")]
    [InlineData("input", "[{\"type\":\"function_call_output\",\"call_id\":1,\"output\":\"x\"}]")]
    [InlineData("input", "[{\"type\":\"function_call\",\"call_id\":\"call\",\"name\":\"function\",\"arguments\":\"{}\",\"id\":1}]")]
    [InlineData("input", "[{\"type\":\"function_call\",\"call_id\":\"call\",\"name\":\"function\",\"arguments\":\"{}\",\"status\":\"failed\"}]")]
    [InlineData("stream", "0")]
    [InlineData("instructions", "1")]
    [InlineData("previous_response_id", "1")]
    [InlineData("previous_response_id", "\"\"")]
    [InlineData("previous_response_id", "\"contains space\"")]
    [InlineData("temperature", "null")]
    [InlineData("temperature", "-0.1")]
    [InlineData("temperature", "2.1")]
    [InlineData("top_p", "1.1")]
    [InlineData("top_p", "1e999")]
    [InlineData("tool_choice", "\"invalid\"")]
    [InlineData("tool_choice", "{\"type\":\"function\",\"name\":\"\"}")]
    [InlineData("metadata", "{\"key\":1}")]
    [InlineData("metadata", "[]")]
    public async Task SchemaFailuresUseFrozenFieldPointers(string name, string value)
    {
        JsonObject request = new() { ["model"] = "test", ["input"] = "text" };
        request[name] = JsonNode.Parse(value);
        using JsonDocument document = JsonDocument.Parse(request.ToJsonString());
        var result = await new ResponsesProtocolAdapter().NormalizeAsync(document.RootElement, TestContext.Current.CancellationToken);
        Assert.True(result.IsFailure);
        Assert.Equal("validation_failed", result.Error.Code);
        Assert.Equal(422, result.Error.Presentation!.Status);
        Assert.Contains("/" + name, result.Error.Presentation.Errors!.Keys);
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
    [InlineData("0.5", false)]
    [InlineData("1e99999999999999999999", false)]
    [InlineData("1e-100", false)]
    [InlineData("1e100", false)]
    [InlineData("\"1\"", false)]
    public async Task OutputLimitIsValidatedLosslesslyWithoutFloatingPointRounding(string number, bool valid)
    {
        using JsonDocument document = JsonDocument.Parse("{\"model\":\"test\",\"input\":\"text\",\"max_output_tokens\":" + number + "}");
        var result = await new ResponsesProtocolAdapter().NormalizeAsync(document.RootElement, TestContext.Current.CancellationToken);
        Assert.Equal(valid, result.IsSuccess);
        if (!valid) { Assert.Equal("validation_failed", result.Error.Code); }
    }

    [Theory]
    [InlineData("\"none\"")]
    [InlineData("\"auto\"")]
    [InlineData("\"required\"")]
    [InlineData("{\"type\":\"function\",\"name\":\"function\"}")]
    public async Task FunctionToolMetadataAndNullableHistoryFieldsMatchTheFrozenRequest(string choice)
    {
        string request = "{\"model\":\"test\",\"input\":\"text\",\"stream\":false,\"instructions\":null,\"previous_response_id\":null,"
            + "\"temperature\":0.5,\"top_p\":1,\"include\":[],\"metadata\":{\"tag\":\"test\"},"
            + "\"tools\":[{\"type\":\"function\",\"name\":\"function\",\"description\":\"test\",\"parameters\":{\"type\":\"object\"},\"strict\":true}],\"tool_choice\":" + choice + "}";
        using JsonDocument document = JsonDocument.Parse(request);
        var result = await new ResponsesProtocolAdapter().NormalizeAsync(document.RootElement, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
    }
}
