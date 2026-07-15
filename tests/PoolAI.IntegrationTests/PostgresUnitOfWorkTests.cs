using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Postgres;
using Testcontainers.PostgreSql;

namespace PoolAI.IntegrationTests;

public sealed class PostgresUnitOfWorkTests
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task UnitOfWorkUsesReadCommittedAndOwnsCommitAndRollback()
    {
        // Governing contract: design-pattern-baseline section 6.3 requires one
        // explicit PostgreSQL transaction whose owner alone can commit it.
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        PostgreSqlContainer container = new PostgreSqlBuilder(ReadPostgresImage())
            .WithDatabase("poolai")
            .WithUsername("postgres")
            .WithPassword(password)
            .Build();
        await using ConfiguredAsyncDisposable containerLease = container.ConfigureAwait(true);

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await container.StartAsync(cancellationToken).ConfigureAwait(true);
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(
            container.GetConnectionString());
        await CreateProbeTableAsync(dataSource, cancellationToken).ConfigureAwait(true);

        PostgresUnitOfWorkFactory factory = new(dataSource);
        await AssertRollbackAsync(factory, dataSource, cancellationToken).ConfigureAwait(true);
        await AssertCommitAsync(factory, dataSource, cancellationToken).ConfigureAwait(true);
    }

    private static async ValueTask CreateProbeTableAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand create = dataSource.CreateCommand(
            "CREATE TABLE public.poolai_uow_probe (id integer PRIMARY KEY);");
        _ = await create
            .ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask AssertRollbackAsync(
        PostgresUnitOfWorkFactory factory,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        IUnitOfWork rolledBack = await factory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        PostgresTransactionSession rollbackSession =
            PostgresUnitOfWorkAccessor.Require(rolledBack.Context);
        Assert.Same(rollbackSession, rolledBack.Context);
        using (NpgsqlCommand isolation = rollbackSession.CreateCommand(
                   "SHOW transaction_isolation;"))
        {
            object? isolationLevel = await isolation
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            Assert.Equal("read committed", Assert.IsType<string>(isolationLevel));
        }

        using (NpgsqlCommand insert = rollbackSession.CreateCommand(
                   "INSERT INTO public.poolai_uow_probe (id) VALUES (1);"))
        {
            Assert.Equal(
                1,
                await insert
                    .ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false));
        }

        await rolledBack.DisposeAsync().ConfigureAwait(false);
        await rolledBack.DisposeAsync().ConfigureAwait(false);
        Assert.Throws<ObjectDisposedException>(
            () => PostgresUnitOfWorkAccessor.Require(rolledBack.Context));
        Assert.Equal(
            0L,
            await CountProbeRowsAsync(dataSource, cancellationToken).ConfigureAwait(false));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => rolledBack.CommitAsync(cancellationToken).AsTask()).ConfigureAwait(false);
    }

    private static async ValueTask AssertCommitAsync(
        PostgresUnitOfWorkFactory factory,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        IUnitOfWork committed = await factory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable committedLease =
            committed.ConfigureAwait(false);
        PostgresTransactionSession commitSession =
            PostgresUnitOfWorkAccessor.Require(committed.Context);
        using (NpgsqlCommand insert = commitSession.CreateCommand(
                   "INSERT INTO public.poolai_uow_probe (id) VALUES (2);"))
        {
            Assert.Equal(
                1,
                await insert
                    .ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false));
        }

        await committed.CommitAsync(cancellationToken).ConfigureAwait(false);
        Assert.Throws<ObjectDisposedException>(
            () => PostgresUnitOfWorkAccessor.Require(committed.Context));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => committed.CommitAsync(cancellationToken).AsTask()).ConfigureAwait(false);
        Assert.Equal(
            1L,
            await CountProbeRowsAsync(dataSource, cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask<long> CountProbeRowsAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand count = dataSource.CreateCommand(
            "SELECT count(*) FROM public.poolai_uow_probe;");
        object? result = await count
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return Assert.IsType<long>(result);
    }

    private static string ReadPostgresImage()
    {
        string root = MigrationCatalogTests.FindRepositoryRoot();
        using JsonDocument versions = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "eng", "versions.json")));
        string image = versions.RootElement
            .GetProperty("containers")
            .GetProperty("postgresql")
            .GetString()
            ?? throw new InvalidOperationException("The PostgreSQL image lock is missing.");
        string digest = versions.RootElement
            .GetProperty("containerDigests")
            .GetProperty("postgresql")
            .GetString()
            ?? throw new InvalidOperationException("The PostgreSQL digest lock is missing.");
        return $"{image}@{digest}";
    }
}
