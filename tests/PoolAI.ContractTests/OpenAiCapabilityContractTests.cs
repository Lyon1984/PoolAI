using PoolAI.Adapters.OpenAI;
using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.ContractTests;

public sealed class OpenAiCapabilityContractTests
{
    [Fact]
    public void R1AdapterCapabilityKeysAreCompleteAndUnique()
    {
        IReadOnlyList<AdapterCapability> capabilities = OpenAiCapabilityDescriptor.R1Capabilities;
        var keys = capabilities
            .Select(capability => new
            {
                capability.Protocol,
                capability.Upstream,
                capability.Operation,
            })
            .ToArray();

        Assert.Equal(10, capabilities.Count);
        Assert.Equal(keys.Length, keys.Distinct().Count());
        Assert.All(Enum.GetValues<UpstreamType>(), upstream =>
        {
            Assert.Contains(capabilities, capability =>
                capability.Protocol == InboundProtocol.Responses
                && capability.Upstream == upstream
                && capability.Operation == AdapterOperation.NonStream);
            Assert.Contains(capabilities, capability =>
                capability.Protocol == InboundProtocol.Responses
                && capability.Upstream == upstream
                && capability.Operation == AdapterOperation.Stream);
            Assert.Contains(capabilities, capability =>
                capability.Protocol == InboundProtocol.ChatCompletions
                && capability.Upstream == upstream
                && capability.Operation == AdapterOperation.NonStream);
            Assert.Contains(capabilities, capability =>
                capability.Protocol == InboundProtocol.ChatCompletions
                && capability.Upstream == upstream
                && capability.Operation == AdapterOperation.Stream);
            Assert.Contains(capabilities, capability =>
                capability.Protocol == InboundProtocol.Models
                && capability.Upstream == upstream
                && capability.Operation == AdapterOperation.ListModels);
        });
    }

    [Fact]
    public void ModelPostCapabilitiesDoNotClaimVerifiedIdempotentReplay()
    {
        AdapterCapability[] modelPostCapabilities = OpenAiCapabilityDescriptor.R1Capabilities
            .Where(capability => capability.Operation is AdapterOperation.NonStream or AdapterOperation.Stream)
            .ToArray();

        Assert.NotEmpty(modelPostCapabilities);
        Assert.All(modelPostCapabilities, capability =>
            Assert.False(capability.SupportsVerifiedIdempotentReplay));
    }
}
