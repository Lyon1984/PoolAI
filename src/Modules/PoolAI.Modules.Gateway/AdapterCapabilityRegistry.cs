using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.Modules.Gateway.Application;

public sealed class AdapterCapabilityRegistry
{
    private readonly Dictionary<CapabilityKey, AdapterCapability> capabilities;

    public AdapterCapabilityRegistry(IEnumerable<AdapterCapability> registeredCapabilities)
    {
        ArgumentNullException.ThrowIfNull(registeredCapabilities);

        Dictionary<CapabilityKey, AdapterCapability> unique = [];
        foreach (AdapterCapability capability in registeredCapabilities)
        {
            CapabilityKey key = new(capability.Protocol, capability.Upstream, capability.Operation);
            if (!unique.TryAdd(key, capability))
            {
                throw new InvalidOperationException($"Duplicate adapter capability: {key}.");
            }
        }

        capabilities = unique;
    }

    public AdapterCapability Get(
        InboundProtocol protocol,
        UpstreamType upstream,
        AdapterOperation operation)
    {
        CapabilityKey key = new(protocol, upstream, operation);
        return capabilities.TryGetValue(key, out AdapterCapability? capability)
            ? capability
            : throw new KeyNotFoundException($"Adapter capability is not registered: {key}.");
    }

    private sealed record CapabilityKey(
        InboundProtocol Protocol,
        UpstreamType Upstream,
        AdapterOperation Operation);
}
