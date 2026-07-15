using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.Adapters.OpenAI;

public static class OpenAiCapabilityDescriptor
{
    public static IReadOnlyList<AdapterCapability> R1Capabilities { get; } =
    [
        new(InboundProtocol.Responses, UpstreamType.OpenAi, AdapterOperation.NonStream, true, false),
        new(InboundProtocol.Responses, UpstreamType.OpenAi, AdapterOperation.Stream, true, false),
        new(InboundProtocol.ChatCompletions, UpstreamType.OpenAi, AdapterOperation.NonStream, true, false),
        new(InboundProtocol.ChatCompletions, UpstreamType.OpenAi, AdapterOperation.Stream, true, false),
        new(InboundProtocol.Models, UpstreamType.OpenAi, AdapterOperation.ListModels, true, true),
        new(InboundProtocol.Responses, UpstreamType.OpenAiCompatible, AdapterOperation.NonStream, true, false),
        new(InboundProtocol.Responses, UpstreamType.OpenAiCompatible, AdapterOperation.Stream, true, false),
        new(InboundProtocol.ChatCompletions, UpstreamType.OpenAiCompatible, AdapterOperation.NonStream, true, false),
        new(InboundProtocol.ChatCompletions, UpstreamType.OpenAiCompatible, AdapterOperation.Stream, true, false),
        new(InboundProtocol.Models, UpstreamType.OpenAiCompatible, AdapterOperation.ListModels, true, true),
    ];
}
