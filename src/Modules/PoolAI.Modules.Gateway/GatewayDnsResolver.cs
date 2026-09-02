using System.Net;

namespace PoolAI.Modules.Gateway.Application;

internal sealed class GatewayDnsResolver : IGatewayDnsResolver
{
    public async ValueTask<IPAddress[]> ResolveAsync(
        string host,
        CancellationToken cancellationToken) =>
        await Dns.GetHostAddressesAsync(host, cancellationToken)
            .ConfigureAwait(false);
}
