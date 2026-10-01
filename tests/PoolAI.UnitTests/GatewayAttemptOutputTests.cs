using System.Text.Json;
using PoolAI.Modules.Gateway.Abstractions;
using PoolAI.Modules.Gateway.Application;

namespace PoolAI.UnitTests;

public sealed class GatewayAttemptOutputTests
{
    private static readonly string[] WriteEvents = ["start", "headers", "business", "write", "write"];
    private static readonly string[] StartEvents = ["start"];
    [Fact]
    public async Task BusinessOutputIsFencedBeforeAnyWriteAndHeadersOnlyAdvanceAfterStart()
    {
        List<string> events = [];
        Evidence evidence = new(events);
        Output output = new(events);
        GatewayAttemptOutput writer = new(output, evidence);
        JsonElement payload = JsonSerializer.SerializeToElement(new { type = "response.created", sequence_number = 0 });
        await writer.StartAsync(TestContext.Current.CancellationToken);
        await writer.StartAsync(TestContext.Current.CancellationToken);
        await writer.WriteEventAsync("response.created", payload, TestContext.Current.CancellationToken);
        await writer.WriteEventAsync("response.created", payload, TestContext.Current.CancellationToken);
        Assert.Equal(WriteEvents, events);
        output.FailWrite = true;
        await Assert.ThrowsAsync<IOException>(async () =>
            await writer.WriteEventAsync("response.created", payload, TestContext.Current.CancellationToken).ConfigureAwait(false));
        Assert.Equal(1, events.Count(static value => value is "business"));
    }

    [Fact]
    public async Task AFailedHeaderStartDoesNotInventExternalOutputEvidence()
    {
        List<string> events = [];
        GatewayAttemptOutput writer = new(new Output(events) { FailStart = true }, new Evidence(events));
        await Assert.ThrowsAsync<IOException>(async () => await writer.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(false));
        Assert.Equal(StartEvents, events);
    }

    private sealed class Evidence(List<string> events) : IGatewayAttemptOutputEvidenceSink
    {
        public void MarkDownstreamHeadersCommitted() => events.Add("headers");
        public void MarkBusinessOutputStarted() => events.Add("business");
    }
    private sealed class Output(List<string> events) : IGatewayResponseOutput
    {
        internal bool FailStart { get; init; }
        internal bool FailWrite { get; set; }
        public ValueTask StartAsync(CancellationToken cancellationToken)
        {
            events.Add("start");
            return FailStart ? ValueTask.FromException(new IOException()) : ValueTask.CompletedTask;
        }
        public ValueTask WriteEventAsync(string eventName, JsonElement payload, CancellationToken cancellationToken)
        {
            events.Add("write");
            return FailWrite ? ValueTask.FromException(new IOException()) : ValueTask.CompletedTask;
        }
    }
}
