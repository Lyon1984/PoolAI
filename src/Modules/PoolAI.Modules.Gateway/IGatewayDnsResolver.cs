using System.Net;

namespace PoolAI.Modules.Gateway.Application;

internal interface IGatewayDnsResolver
{
    ValueTask<IPAddress[]> ResolveAsync(
        string host,
        CancellationToken cancellationToken);
}
