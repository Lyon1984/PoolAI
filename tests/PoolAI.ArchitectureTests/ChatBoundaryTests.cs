namespace PoolAI.ArchitectureTests;

public sealed class ChatBoundaryTests
{
    [Fact]
    public void ChatSharesOneFencedAdmissionOwnerAndKeepsHttpOutsideBusinessExecution()
    {
        string root = RepositoryRoot.Find();
        string endpoint = File.ReadAllText(Path.Combine(root, "src", "PoolAI.Api", "ResponsesEndpoint.cs"));
        string program = File.ReadAllText(Path.Combine(root, "src", "PoolAI.Api", "Program.cs"));
        Assert.Contains("MapPost(\"/v1/chat/completions\", ResponsesEndpoint.HandleChatAsync)", program, StringComparison.Ordinal);
        Assert.Contains("parser.ParseAsync, process", endpoint, StringComparison.Ordinal);
        Assert.Equal(2, endpoint.Split("discriminator.TryAcquire(", StringSplitOptions.None).Length);
        Assert.Equal(2, endpoint.Split("admission.AcquireAsync(", StringSplitOptions.None).Length);
        Assert.Contains("InboundProtocol.ChatCompletions", endpoint, StringComparison.Ordinal);
        Assert.Contains("process.ExecuteInitialAttemptAsync", endpoint, StringComparison.Ordinal);
        string parser = File.ReadAllText(Path.Combine(root, "src", "Modules", "PoolAI.Modules.Gateway", "GatewayChatRequestParser.cs"));
        foreach (string source in new[] { endpoint, parser })
        {
            Assert.DoesNotContain("DbContext", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Npgsql", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Redis", source, StringComparison.Ordinal);
            Assert.DoesNotContain("HttpClient", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ChatAdapterKeepsFunctionCallsAsDataAndUsesVendorNeutralPorts()
    {
        string root = RepositoryRoot.Find();
        string adapter = File.ReadAllText(Path.Combine(root, "src", "Adapters", "PoolAI.Adapters.OpenAI", "ChatUpstreamAdapter.cs"));
        string output = File.ReadAllText(Path.Combine(root, "src", "PoolAI.Api", "ChatHttpOutput.cs"));
        Assert.Contains("IUpstreamAdapter", adapter, StringComparison.Ordinal);
        Assert.Contains("IPreparedUpstreamAttempt", adapter, StringComparison.Ordinal);
        Assert.Contains("IGatewayResponseOutput", output, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("response.completed", output, StringComparison.Ordinal);
        Assert.DoesNotContain("event:", output, StringComparison.Ordinal);
    }
}
