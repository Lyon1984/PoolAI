using System.Text.Json;
using PoolAI.Contracts.Generated;

namespace PoolAI.ContractTests;

public sealed class GeneratedContractRoundTripTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    [Fact]
    public void AuthoritativeControlProblemRoundTripsThroughGeneratedDto()
    {
        string root = FindRepositoryRoot();
        string payload = File.ReadAllText(Path.Combine(
            root,
            "docs",
            "contracts",
            "fixtures",
            "control-plane-validation-error.json"));

        (ControlPlaneProblem first, string serialized, ControlPlaneProblem second) =
            RoundTrip<ControlPlaneProblem>(payload);

        Assert.Equal("validation_failed", first.Code);
        Assert.Equal(422, first.Status);
        Assert.False(first.Retryable);
        Assert.Equal("0190f8bf-a040-7444-a2ca-c4bc32e48b47", first.RequestId.ToString());
        Assert.Equal(
            "https://poolai.example/problems/validation-failed",
            first.Type.OriginalString);
        Assert.Equal("请求字段未通过校验。", first.Detail);
        Assert.True(first.Errors.HasValue);
        Assert.Equal(
            "必须是有效的电子邮件地址。",
            Assert.Single(first.Errors.Value!["/email"]));

        Assert.Equal(first.Code, second.Code);
        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.Retryable, second.Retryable);
        Assert.Equal(first.RequestId, second.RequestId);
        Assert.Equal(first.Type, second.Type);
        Assert.Equal(first.Detail, second.Detail);
        Assert.True(second.Errors.HasValue);
        Assert.Equal(
            "必须是有效的电子邮件地址。",
            Assert.Single(second.Errors.Value!["/email"]));

        using JsonDocument document = JsonDocument.Parse(serialized);
        Assert.False(document.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void AuthoritativeGatewayProblemRoundTripsThroughGeneratedDto()
    {
        string root = FindRepositoryRoot();
        string payload = File.ReadAllText(Path.Combine(
            root,
            "docs",
            "contracts",
            "fixtures",
            "gateway-upstream-usage-out-of-range.json"));

        (GatewayProblem first, string serialized, GatewayProblem second) =
            RoundTrip<GatewayProblem>(payload);

        Assert.Equal("upstream_usage_out_of_range", first.Code);
        Assert.Equal(502, first.Status);
        Assert.False(first.Retryable);
        Assert.Equal("0190f8bf-a040-7444-a2ca-c4bc32e48b47", first.RequestId.ToString());
        Assert.Equal(
            "https://poolai.example/problems/upstream-usage-out-of-range",
            first.Type.OriginalString);
        Assert.Equal(first.Code, first.Error.Code);
        Assert.Equal(first.Detail, first.Error.Message);
        Assert.Null(first.Error.Param);

        Assert.Equal(first.Code, second.Code);
        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.Retryable, second.Retryable);
        Assert.Equal(first.RequestId, second.RequestId);
        Assert.Equal(first.Type, second.Type);
        Assert.Equal(first.Detail, second.Detail);
        Assert.Equal(second.Code, second.Error.Code);
        Assert.Equal(second.Detail, second.Error.Message);
        Assert.Null(second.Error.Param);

        using JsonDocument document = JsonDocument.Parse(serialized);
        Assert.Equal(
            second.Code,
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void AuthoritativeResponsesSseEventsRoundTripWithoutDroppingProtocolFields()
    {
        string root = FindRepositoryRoot();
        string[] completedFrames = ReadSseJsonData(root, "responses-stream-completed.sse");

        var (created, createdJson, _) = RoundTrip<ResponseCreatedEvent>(completedFrames[0]);
        Assert.Equal("response.created", created.Type);
        Assert.Equal(0, created.SequenceNumber);
        Assert.Equal("in_progress", created.Response.Status);
        using (JsonDocument document = JsonDocument.Parse(createdJson))
        {
            Assert.Equal(
                JsonValueKind.Null,
                document.RootElement.GetProperty("response").GetProperty("usage").ValueKind);
        }

        var (completed, completedJson, _) =
            RoundTrip<ResponseCompletedEvent>(completedFrames[^1]);
        Assert.Equal("response.completed", completed.Type);
        Assert.Equal("completed", completed.Response.Status);
        using (JsonDocument document = JsonDocument.Parse(completedJson))
        {
            JsonElement usage = document.RootElement.GetProperty("response").GetProperty("usage");
            Assert.Equal(10, usage.GetProperty("total_tokens").GetInt64());
            Assert.Equal(
                0,
                usage.GetProperty("input_tokens_details")
                    .GetProperty("cache_write_tokens")
                    .GetInt64());
        }

        string[] errorFrames = ReadSseJsonData(root, "responses-stream-error.sse");
        var (terminal, terminalJson, _) = RoundTrip<PoolResponseErrorEvent>(errorFrames[^1]);
        Assert.Equal("error", terminal.Type);
        Assert.Equal("upstream_stream_error", terminal.Code);
        Assert.NotEmpty(terminal.Message);
        using JsonDocument terminalDocument = JsonDocument.Parse(terminalJson);
        Assert.Equal(
            terminal.Code,
            terminalDocument.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void AuthoritativeChatSseUsageModesRoundTripWithoutCollapsingPresence()
    {
        string root = FindRepositoryRoot();
        string[] withoutUsage = ReadSseJsonData(root, "chat-completions-text-no-usage.sse");
        var (_, omittedJson, _) = RoundTrip<ChatCompletionChunk>(withoutUsage[0]);
        using (JsonDocument document = JsonDocument.Parse(omittedJson))
        {
            Assert.False(document.RootElement.TryGetProperty("usage", out _));
        }

        string[] withUsage = ReadSseJsonData(root, "chat-completions-text.sse");
        var (_, nullUsageJson, _) = RoundTrip<ChatCompletionChunk>(withUsage[0]);
        using (JsonDocument document = JsonDocument.Parse(nullUsageJson))
        {
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("usage").ValueKind);
        }

        var (_, usageJson, _) = RoundTrip<ChatCompletionChunk>(withUsage[^1]);
        using JsonDocument usageDocument = JsonDocument.Parse(usageJson);
        Assert.Empty(usageDocument.RootElement.GetProperty("choices").EnumerateArray());
        Assert.Equal(
            10,
            usageDocument.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt64());
    }

    private static (T First, string Serialized, T Second) RoundTrip<T>(string payload)
        where T : class
    {
        T first = JsonSerializer.Deserialize<T>(payload, SerializerOptions)
            ?? throw new JsonException($"Authoritative payload did not deserialize as {typeof(T).Name}.");
        string serialized = JsonSerializer.Serialize(first, SerializerOptions);
        T second = JsonSerializer.Deserialize<T>(serialized, SerializerOptions)
            ?? throw new JsonException($"Serialized {typeof(T).Name} did not deserialize again.");
        return (first, serialized, second);
    }

    private static string[] ReadSseJsonData(string root, string fixtureName)
    {
        string source = File.ReadAllText(Path.Combine(
            root,
            "docs",
            "contracts",
            "fixtures",
            fixtureName));

        return source
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(frame => frame
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..])
            .Where(data => !string.Equals(data, "[DONE]", StringComparison.Ordinal))
            .ToArray();
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PoolAI.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the PoolAI repository root.");
    }
}
