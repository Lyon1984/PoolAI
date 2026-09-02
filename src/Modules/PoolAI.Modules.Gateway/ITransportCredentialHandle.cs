using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.Modules.Gateway.Application;

internal interface ITransportCredentialHandle : IUpstreamCredentialHandle
{
    ITransportCredentialAttachment AttachAuthorizationOnce(
        Uri vettedDestination,
        HttpRequestMessage transportOwnedRequest);
}
