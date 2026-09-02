using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Application.Ports;
using PoolAI.Modules.Supply.Domain;

namespace PoolAI.Modules.Supply.Infrastructure.Persistence;

internal sealed class PostgresRouteCredentialLeaseSource(
    NpgsqlDataSource dataSource,
    IAccountCredentialProtector protector) : IRouteCredentialLeaseSource
{
    private const int MaximumCredentialBytes = 16 * 1024;
    private const string CurrentSnapshotSql = """
        SELECT account.version,
               account.credential_revision,
               account.provider,
               account.upstream_base_url,
               account.status,
               account.deleted_at,
               account.credential_envelope::text
        FROM public.accounts AS account
        WHERE account.id = $1;
        """;

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    private readonly IAccountCredentialProtector _protector =
        protector ?? throw new ArgumentNullException(nameof(protector));

    public async ValueTask<Result<IRouteCredentialLease>> AcquireAsync(
        RouteCredentialLeaseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsValidRequest(request))
        {
            return InvalidRequest();
        }

        CurrentAccountSnapshot? snapshot = await ReadCurrentSnapshotAsync(
            request.AccountId,
            cancellationToken).ConfigureAwait(false);
        if (snapshot is null || !Matches(request, snapshot))
        {
            return StaleRoute();
        }

        AccountCredentialLease credential = await _protector
            .UnprotectAsync(
                snapshot.Envelope,
                request.AccountId,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            bool isBounded = credential.Use(static value =>
                value.Length is > 0 and <= MaximumCredentialBytes);
            if (!isBounded)
            {
                return Result.Failure<IRouteCredentialLease>(
                    "dependency_unavailable",
                    "The Account credential is invalid.",
                    retryAfterSeconds: 1);
            }

            IRouteCredentialLease lease = new SingleUseRouteCredentialLease(
                credential);
            credential = null!;
            return Result.Success(lease);
        }
        finally
        {
            credential?.Dispose();
        }
    }

    private async ValueTask<CurrentAccountSnapshot?> ReadCurrentSnapshotAsync(
        EntityId accountId,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease =
            connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = CurrentSnapshotSql;
        command.Parameters.AddWithValue(accountId.Value);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        CurrentAccountSnapshot snapshot = ReadSnapshot(reader);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The Account route credential lookup returned multiple rows.");
        }

        return snapshot;
    }

    private static CurrentAccountSnapshot ReadSnapshot(NpgsqlDataReader reader)
    {
        string baseUrl = AccountInput.BaseUrl(reader.GetString(3));
        using JsonDocument document = JsonDocument.Parse(reader.GetString(6));
        return new CurrentAccountSnapshot(
            reader.GetInt64(0),
            reader.GetInt64(1),
            ParseProvider(reader.GetString(2)),
            new Uri(baseUrl, UriKind.Absolute),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
            document.RootElement.Clone());
    }

    private static bool Matches(
        RouteCredentialLeaseRequest request,
        CurrentAccountSnapshot snapshot) =>
        snapshot.AccountVersion == request.AccountVersion
        && snapshot.CredentialRevision == request.CredentialRevision
        && snapshot.Provider == request.Provider
        && string.Equals(snapshot.Status, "active", StringComparison.Ordinal)
        && snapshot.DeletedAt is null
        && SameAuthority(snapshot.UpstreamBaseUri, request.UpstreamBaseUri);

    private static bool IsValidRequest(RouteCredentialLeaseRequest request) =>
        request.AccountId.Value != Guid.Empty
        && request.AccountVersion > 0
        && request.CredentialRevision > 0
        && request.Provider is UpstreamProvider.OpenAi
            or UpstreamProvider.OpenAiCompatible
        && IsValidBaseUri(request.UpstreamBaseUri);

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

    private static bool SameAuthority(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            CanonicalHost(left),
            CanonicalHost(right),
            StringComparison.Ordinal)
        && left.Port == right.Port;

    private static string CanonicalHost(Uri value) =>
        (value.HostNameType == UriHostNameType.Dns
            ? value.IdnHost
            : value.DnsSafeHost).ToLowerInvariant();

    private static UpstreamProvider ParseProvider(string value) => value switch
    {
        "openai" => UpstreamProvider.OpenAi,
        "openai_compatible" => UpstreamProvider.OpenAiCompatible,
        _ => throw new InvalidOperationException(
            "The Account credential provider is invalid."),
    };

    private static Result<IRouteCredentialLease> InvalidRequest() =>
        Result.Failure<IRouteCredentialLease>(
            "invalid_request",
            "The Account route credential request is invalid.");

    private static Result<IRouteCredentialLease> StaleRoute() =>
        Result.Failure<IRouteCredentialLease>(
            "no_available_account",
            "The selected Account route is no longer current.",
            retryAfterSeconds: 1);

    private sealed record CurrentAccountSnapshot(
        long AccountVersion,
        long CredentialRevision,
        UpstreamProvider Provider,
        Uri UpstreamBaseUri,
        string Status,
        DateTimeOffset? DeletedAt,
        JsonElement Envelope);

    private sealed class SingleUseRouteCredentialLease(
        AccountCredentialLease credential) : IRouteCredentialLease
    {
        private readonly Lock _gate = new();
        private AccountCredentialLease? _credential = credential
            ?? throw new ArgumentNullException(nameof(credential));

        public void TransferOnce(RouteCredentialReader reader)
        {
            ArgumentNullException.ThrowIfNull(reader);
            AccountCredentialLease acquiredCredential;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_credential is null, this);
                acquiredCredential = _credential;
                _credential = null;
            }

            using (acquiredCredential)
            {
                acquiredCredential.Use(value =>
                {
                    reader(value);
                    return true;
                });
            }
        }

        public void Dispose()
        {
            AccountCredentialLease? acquiredCredential;
            lock (_gate)
            {
                acquiredCredential = _credential;
                _credential = null;
            }

            acquiredCredential?.Dispose();
        }

        public override string ToString() => nameof(IRouteCredentialLease);
    }
}
