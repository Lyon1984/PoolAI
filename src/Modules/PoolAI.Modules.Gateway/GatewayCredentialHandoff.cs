using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Abstractions;
using PoolAI.Modules.Routing.Abstractions;
using PoolAI.Modules.Supply.Abstractions;

namespace PoolAI.Modules.Gateway.Application;

internal sealed class GatewayCredentialHandoff(
    IRouteCredentialLeaseSource credentialLeases)
{
    private readonly IRouteCredentialLeaseSource _credentialLeases =
        credentialLeases ?? throw new ArgumentNullException(nameof(credentialLeases));

    internal async ValueTask<Result<IUpstreamCredentialHandle>> AcquireAsync(
        AccountRoute route,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (!IsValidRoute(route))
        {
            return Result.Failure<IUpstreamCredentialHandle>(
                "invalid_request",
                "The selected Account route is invalid.");
        }

        Result<IRouteCredentialLease> acquired = await _credentialLeases
            .AcquireAsync(
                new RouteCredentialLeaseRequest(
                    route.AccountId,
                    route.AccountVersion,
                    route.CredentialRevision,
                    MapProvider(route.Provider),
                    route.UpstreamBaseUri),
                cancellationToken)
            .ConfigureAwait(false);
        if (acquired.IsFailure)
        {
            return Result.Failure<IUpstreamCredentialHandle>(
                acquired.Error.Code,
                acquired.Error.Description,
                acquired.Error.RetryAfterSeconds,
                acquired.Error.ETag,
                acquired.Error.Presentation);
        }

        return Result.Success<IUpstreamCredentialHandle>(
            new RouteBoundUpstreamCredentialHandle(
                route.UpstreamBaseUri,
                acquired.Value));
    }

    private static bool IsValidRoute(AccountRoute route) =>
        route.GroupId.Value != Guid.Empty
        && route.ChannelId.Value != Guid.Empty
        && route.AccountId.Value != Guid.Empty
        && route.Provider is AccountRouteProvider.OpenAi
            or AccountRouteProvider.OpenAiCompatible
        && IsValidModel(route.ClientModel)
        && IsValidModel(route.UpstreamModel)
        && IsValidBaseUri(route.UpstreamBaseUri)
        && route.Capabilities is not null
        && route.LeaseExpiresAt != default
        && route.SupplyConfigurationVersion > 0
        && route.ChannelVersion > 0
        && route.AccountVersion > 0
        && route.CredentialRevision > 0;

    private static bool IsValidModel(string value) =>
        value is { Length: >= 1 and <= 200 }
        && !value.Any(char.IsControl)
        && string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static bool IsValidBaseUri(Uri? value)
    {
        if (value is null
            || !value.IsAbsoluteUri
            || value.HostNameType is UriHostNameType.Unknown or UriHostNameType.Basic
            || !string.IsNullOrEmpty(value.UserInfo)
            || !string.IsNullOrEmpty(value.Query)
            || !string.IsNullOrEmpty(value.Fragment)
            || value.Port is < 1 or > 65_535)
        {
            return false;
        }

        if (string.Equals(value.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(value.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            && (string.Equals(value.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                || value.IsLoopback);
    }

    private static UpstreamProvider MapProvider(AccountRouteProvider provider) =>
        provider switch
        {
            AccountRouteProvider.OpenAi => UpstreamProvider.OpenAi,
            AccountRouteProvider.OpenAiCompatible =>
                UpstreamProvider.OpenAiCompatible,
            _ => throw new InvalidOperationException(
                "The selected Account route provider is invalid."),
        };

    private sealed class RouteBoundUpstreamCredentialHandle :
        ITransportCredentialHandle
    {
        private static readonly UTF8Encoding StrictUtf8 = new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);
        private readonly Lock _gate = new();
        private readonly string _authority;
        private IRouteCredentialLease? _credentialLease;

        internal RouteBoundUpstreamCredentialHandle(
            Uri upstreamBaseUri,
            IRouteCredentialLease credentialLease)
        {
            _authority = CanonicalAuthority(upstreamBaseUri);
            _credentialLease = credentialLease
                ?? throw new ArgumentNullException(nameof(credentialLease));
        }

        public ITransportCredentialAttachment AttachAuthorizationOnce(
            Uri vettedDestination,
            HttpRequestMessage transportOwnedRequest)
        {
            ArgumentNullException.ThrowIfNull(vettedDestination);
            ArgumentNullException.ThrowIfNull(transportOwnedRequest);
            IRouteCredentialLease lease;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_credentialLease is null, this);
                lease = _credentialLease;
                _credentialLease = null;
            }

            using (lease)
            {
                if (!IsValidBaseUri(vettedDestination)
                    || !string.Equals(
                        _authority,
                        CanonicalAuthority(vettedDestination),
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The vetted upstream destination does not match the selected route authority.");
                }

                if (transportOwnedRequest.Headers.Authorization is not null)
                {
                    throw new InvalidOperationException(
                        "The transport-owned request already contains Authorization.");
                }

                TransportCredentialAttachment? attachment = null;
                try
                {
                    lease.TransferOnce(credential =>
                    {
                        string value = StrictUtf8.GetString(credential);
                        transportOwnedRequest.Headers.Authorization =
                            new AuthenticationHeaderValue("Bearer", value);
                        attachment = new TransportCredentialAttachment(
                            transportOwnedRequest);
                    });
                    return attachment
                        ?? throw new InvalidOperationException(
                            "The credential lease did not attach Authorization.");
                }
                catch
                {
                    attachment?.Dispose();
                    transportOwnedRequest.Headers.Authorization = null;
                    throw;
                }
            }
        }

        public void Dispose()
        {
            IRouteCredentialLease? lease;
            lock (_gate)
            {
                lease = _credentialLease;
                _credentialLease = null;
            }

            lease?.Dispose();
        }

        public override string ToString() => nameof(IUpstreamCredentialHandle);

        private static string CanonicalAuthority(Uri value)
        {
            string host = (value.HostNameType == UriHostNameType.Dns
                    ? value.IdnHost
                    : value.DnsSafeHost)
                .ToLowerInvariant();
            string authorityHost = value.HostNameType == UriHostNameType.IPv6
                ? $"[{host}]"
                : host;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{value.Scheme.ToLowerInvariant()}://{authorityHost}:{value.Port}");
        }

        private sealed class TransportCredentialAttachment(
            HttpRequestMessage request) : ITransportCredentialAttachment
        {
            private HttpRequestMessage? _request = request
                ?? throw new ArgumentNullException(nameof(request));

            public void Dispose()
            {
                HttpRequestMessage? current = Interlocked.Exchange(
                    ref _request,
                    null);
                if (current is not null)
                {
                    current.Headers.Authorization = null;
                }
            }
        }
    }
}
