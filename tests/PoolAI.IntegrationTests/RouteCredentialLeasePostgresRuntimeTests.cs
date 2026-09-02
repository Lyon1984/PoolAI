#pragma warning disable MA0051 // One integration test keeps the full credential fence sequence visible.
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Application.Ports;
using PoolAI.Modules.Supply.Infrastructure.Persistence;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class RouteCredentialLeasePostgresRuntimeTests(
    PostgresRuntimeFixture fixture)
{
    private readonly PostgresRuntimeFixture _fixture =
        fixture ?? throw new ArgumentNullException(nameof(fixture));

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task CurrentRouteFencesBeforeDecryptAndReturnsOneUseZeroizingLease()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        EntityId accountId = EntityId.New();
        await SeedAccountAsync(accountId, cancellationToken).ConfigureAwait(true);
        RecordingProtector protector = new();
        PostgresRouteCredentialLeaseSource source = new(
            _fixture.ApiServices.GetRequiredService<NpgsqlDataSource>(),
            protector);
        RouteCredentialLeaseRequest current = new(
            accountId,
            AccountVersion: 7,
            CredentialRevision: 11,
            UpstreamProvider.OpenAiCompatible,
            new Uri("https://route.example.test/alternate-path"));

        RouteCredentialLeaseRequest[] mismatches =
        [
            current with { AccountId = EntityId.New() },
            current with { AccountVersion = 8 },
            current with { CredentialRevision = 12 },
            current with { Provider = UpstreamProvider.OpenAi },
            current with
            {
                UpstreamBaseUri = new Uri("https://other.example.test/v1"),
            },
        ];
        foreach (RouteCredentialLeaseRequest mismatch in mismatches)
        {
            Result<IRouteCredentialLease> rejected = await source.AcquireAsync(
                mismatch,
                cancellationToken).ConfigureAwait(true);
            Assert.True(rejected.IsFailure);
            Assert.Equal("no_available_account", rejected.Error.Code);
        }

        Assert.Equal(0, protector.UnprotectCalls);

        Result<IRouteCredentialLease> acquired = await source.AcquireAsync(
            current,
            cancellationToken).ConfigureAwait(true);

        Assert.True(acquired.IsSuccess);
        Assert.Equal(1, protector.UnprotectCalls);
        Assert.Equal(accountId, protector.LastAccountId);
        Assert.NotNull(protector.LastPlaintext);
        using IRouteCredentialLease lease = acquired.Value;
        byte[]? transferredDigest = null;
        lease.TransferOnce(credential =>
            transferredDigest = SHA256.HashData(credential));
        byte[] digest = Assert.IsType<byte[]>(transferredDigest);
        try
        {
            Assert.NotEqual(new byte[SHA256.HashSizeInBytes], digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }

        byte[] firstPlaintext = Assert.IsType<byte[]>(protector.LastPlaintext);
        Assert.All(firstPlaintext, static value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() =>
            lease.TransferOnce(static _ => { }));

        Result<IRouteCredentialLease> disposable = await source.AcquireAsync(
            current,
            cancellationToken).ConfigureAwait(true);
        byte[] secondPlaintext = Assert.IsType<byte[]>(protector.LastPlaintext);
        disposable.Value.Dispose();
        Assert.All(secondPlaintext, static value => Assert.Equal(0, value));

        await RetireAccountAsync(accountId, cancellationToken).ConfigureAwait(true);
        Result<IRouteCredentialLease> retired = await source.AcquireAsync(
            current with { AccountVersion = 8 },
            cancellationToken).ConfigureAwait(true);
        Assert.True(retired.IsFailure);
        Assert.Equal("no_available_account", retired.Error.Code);
        Assert.Equal(2, protector.UnprotectCalls);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task InvalidRequestIsRejectedBeforeDatabaseOrDecrypt()
    {
        RecordingProtector protector = new();
        PostgresRouteCredentialLeaseSource source = new(
            _fixture.ApiServices.GetRequiredService<NpgsqlDataSource>(),
            protector);
        RouteCredentialLeaseRequest invalid = new(
            EntityId.New(),
            AccountVersion: 0,
            CredentialRevision: 1,
            UpstreamProvider.OpenAi,
            new Uri("https://route.example.test/v1"));

        Result<IRouteCredentialLease> result = await source.AcquireAsync(
            invalid,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsFailure);
        Assert.Equal("invalid_request", result.Error.Code);
        Assert.Equal(0, protector.UnprotectCalls);
    }

    private async ValueTask SeedAccountAsync(
        EntityId accountId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            INSERT INTO public.accounts (
                id,
                provider,
                name,
                auth_type,
                upstream_base_url,
                credential_envelope,
                credential_prefix,
                status,
                priority,
                weight,
                max_concurrency,
                last_health_at,
                last_health_status,
                version,
                credential_revision
            ) VALUES (
                $1,
                'openai_compatible',
                $2,
                'api_key',
                'https://route.example.test/v1',
                '{}'::jsonb,
                'route-fixture',
                'active',
                1,
                1,
                1,
                clock_timestamp(),
                'healthy',
                7,
                11
            );
            """);
        command.Parameters.AddWithValue(accountId.Value);
        command.Parameters.AddWithValue($"route-credential-{accountId.Value:N}");
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false));
    }

    private async ValueTask RetireAccountAsync(
        EntityId accountId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            UPDATE public.accounts
            SET status = 'retired',
                deleted_at = clock_timestamp(),
                version = version + 1,
                updated_at = clock_timestamp()
            WHERE id = $1;
            """);
        command.Parameters.AddWithValue(accountId.Value);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false));
    }

    private sealed class RecordingProtector : IAccountCredentialProtector
    {
        internal int UnprotectCalls { get; private set; }

        internal EntityId? LastAccountId { get; private set; }

        internal byte[]? LastPlaintext { get; private set; }

        public AccountCredentialProtection Protect(
            string credential,
            EntityId accountId) => throw Unexpected();

        public ValueTask<AccountCredentialLease> UnprotectAsync(
            JsonElement envelope,
            EntityId accountId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UnprotectCalls++;
            LastAccountId = accountId;
            LastPlaintext = Enumerable.Repeat((byte)0x4c, 64).ToArray();
            return ValueTask.FromResult(new AccountCredentialLease(LastPlaintext));
        }

        public ValueTask<AccountCredentialRewrap> RewrapAsync(
            JsonElement envelope,
            EntityId accountId,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<AccountCredentialRewrap>(Unexpected());

        private static InvalidOperationException Unexpected() =>
            new("Unexpected credential protector operation.");
    }
}
#pragma warning restore MA0051
