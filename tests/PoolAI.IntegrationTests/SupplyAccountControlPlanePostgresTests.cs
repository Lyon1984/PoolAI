#pragma warning disable MA0051 // The PostgreSQL Account lifecycle proof is intentionally end-to-end.
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Secrets;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Application.Ports;
using PoolAI.Modules.Supply.Domain;
using PoolAI.Modules.Supply.Infrastructure.Persistence;
using PoolAI.Modules.Supply.Infrastructure.Security;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class SupplyAccountControlPlanePostgresTests(
    PostgresRuntimeFixture fixture)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ProviderIsExplicitImmutableAndCredentialsAreNeverReadable()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        NpgsqlDataSource dataSource =
            fixture.ApiServices.GetRequiredService<NpgsqlDataSource>();
        PostgresAccountControlPlaneRepository repository = new(
            dataSource,
            new PostgresAccountCredentialStore(dataSource));
        AccountCredentialProtector protector = CreateProtector();
        EntityId accountId = EntityId.New();
        const string originalCredential = "integration-account-secret-0001";
        const string replacementCredential = "integration-account-secret-0002";
        const string originalUrl = "https://EXAMPLE.com/v1";
        const string replacementUrl = "https://SECOND.example/v2";

        AccountCredentialProtection original = protector.Protect(
            originalCredential,
            accountId);
        AccountMutationResult created = await InUnitOfWorkAsync(
            context => repository.CreateAsync(
                new AccountCreateWrite(
                    accountId,
                    UpstreamProvider.OpenAiCompatible,
                    "Integration Account",
                    originalUrl,
                    original.Envelope,
                    AccountInput.CredentialPrefix(originalCredential),
                    MaxConcurrency: 4,
                    Priority: 2,
                    Weight: 100),
                context,
                cancellationToken),
            cancellationToken);
        Assert.Equal(AccountMutationDisposition.Written, created.Disposition);
        Assert.Equal(originalUrl, created.Value!.UpstreamBaseUrl);
        Assert.Equal(UpstreamProvider.OpenAiCompatible, created.Value.Provider);
        Assert.Equal(1, created.Value.Version);

        AccountCredentialProtection replacement = protector.Protect(
            replacementCredential,
            accountId);
        AccountMutationResult updated = await InUnitOfWorkAsync(
            context => repository.UpdateAsync(
                new AccountUpdateWrite(
                    accountId,
                    ExpectedVersion: 1,
                    NameSpecified: true,
                    Name: "Updated Integration Account",
                    BaseUrlSpecified: true,
                    UpstreamBaseUrl: replacementUrl,
                    CredentialSpecified: true,
                    CredentialEnvelope: replacement.Envelope,
                    CredentialPrefix:
                        AccountInput.CredentialPrefix(replacementCredential),
                    StatusSpecified: false,
                    Status: null,
                    MaxConcurrencySpecified: false,
                    MaxConcurrency: null,
                    PrioritySpecified: false,
                    Priority: null,
                    WeightSpecified: false,
                    Weight: null,
                    Reason: "rotate integration credential"),
                context,
                cancellationToken),
            cancellationToken);
        Assert.Equal(AccountMutationDisposition.Written, updated.Disposition);
        Assert.True(updated.WasChanged);
        Assert.Equal(replacementUrl, updated.Value!.UpstreamBaseUrl);
        Assert.Equal(UpstreamProvider.OpenAiCompatible, updated.Value.Provider);
        Assert.Equal(2, updated.Value.Version);
        Assert.Equal(AccountHealth.Unknown, updated.Value.Health);
        Assert.Null(updated.Value.LastHealthAt);
        Assert.Null(updated.Value.UpstreamRateLimitedUntil);

        string persistedEnvelope = await ReadEnvelopeAsync(
            accountId,
            cancellationToken);
        Assert.DoesNotContain(
            originalCredential,
            persistedEnvelope,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            replacementCredential,
            persistedEnvelope,
            StringComparison.Ordinal);

        AccountMutationResult retired = await InUnitOfWorkAsync(
            context => repository.RetireAsync(
                new AccountRetireWrite(
                    accountId,
                    ExpectedVersion: 2,
                    Reason: "integration cleanup"),
                context,
                cancellationToken),
            cancellationToken);
        Assert.Equal(AccountMutationDisposition.Written, retired.Disposition);
        Assert.Equal(AccountResourceStatus.Retired, retired.Value!.Status);
        Assert.Equal(3, retired.Value.Version);
        Assert.Equal(UpstreamProvider.OpenAiCompatible, retired.Value.Provider);
    }

    private async ValueTask<AccountMutationResult> InUnitOfWorkAsync(
        Func<IUnitOfWorkContext, ValueTask<AccountMutationResult>> action,
        CancellationToken cancellationToken)
    {
        IUnitOfWorkFactory factory =
            fixture.ApiServices.GetRequiredService<IUnitOfWorkFactory>();
        IUnitOfWork unitOfWork = await factory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lease =
            unitOfWork.ConfigureAwait(false);
        AccountMutationResult result = await action(unitOfWork.Context)
            .ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<string> ReadEnvelopeAsync(
        EntityId accountId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            SELECT credential_envelope::text
            FROM public.accounts
            WHERE id = $1;
            """);
        command.Parameters.AddWithValue(accountId.Value);
        return (string?)await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The integration Account envelope was not persisted.");
    }

    private static AccountCredentialProtector CreateProtector()
    {
        const string keyId = "m2-e2-integration-key";
        byte[] key = Enumerable
            .Repeat((byte)0x74, SecretEnvelopeKeyRing.KeySize)
            .ToArray();
        try
        {
            SecretEnvelopeKeyRing keyRing = new(
                keyId,
                new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    [keyId] = key,
                });
            return new AccountCredentialProtector(
                new AccountCredentialEnvelopeOptions(keyRing),
                new NoOpOperationalEventWriter());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private sealed class NoOpOperationalEventWriter : IOperationalEventWriter
    {
        public ValueTask WriteAsync(
            string eventName,
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}
#pragma warning restore MA0051
