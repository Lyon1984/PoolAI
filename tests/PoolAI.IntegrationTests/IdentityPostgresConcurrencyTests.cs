#pragma warning disable MA0051 // The two races intentionally share one isolated PostgreSQL fixture.
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Database.Migrations;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application.Ports;
using PoolAI.Modules.Identity.Domain;
using PoolAI.Modules.Identity.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace PoolAI.IntegrationTests;

public sealed class IdentityPostgresConcurrencyTests
{
    private static readonly Guid AdminRoleId = Guid.Parse(
        "01900000-0000-7000-8000-000000000001");
    private static readonly Guid UserRoleId = Guid.Parse(
        "01900000-0000-7000-8000-000000000004");

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task LastAdminAndPasswordResetUseDatabaseLinearization()
    {
        // Governing contracts: AC-035/040/041 and docs/database/README.md.
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        PostgreSqlContainer container = new PostgreSqlBuilder(
            PostgresMigrationTests.ReadPostgresImage())
            .WithDatabase("poolai")
            .WithUsername("postgres")
            .WithPassword(password)
            .Build();
        await using ConfiguredAsyncDisposable containerLease = container.ConfigureAwait(true);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await container.StartAsync(cancellationToken).ConfigureAwait(true);
        string connectionString = container.GetConnectionString();
        await PostgresMigrationTests.ProvisionRuntimeRolesAsync(
            connectionString,
            cancellationToken).ConfigureAwait(true);
        MigrationCatalog catalog = await MigrationCatalog.LoadAsync(cancellationToken)
            .ConfigureAwait(true);
        await new PostgresMigrator(catalog).ApplyAsync(
            connectionString,
            "PoolAI.IntegrationTests.identity-concurrency",
            cancellationToken).ConfigureAwait(true);

        ServiceCollection services = new();
        services.AddPoolAiPostgresRuntime(connectionString);
        ServiceProvider serviceProvider = services.BuildServiceProvider();
        await using ConfiguredAsyncDisposable serviceProviderLease = serviceProvider.ConfigureAwait(false);
        NpgsqlDataSource dataSource = serviceProvider.GetRequiredService<NpgsqlDataSource>();
        IUnitOfWorkFactory unitOfWorkFactory = serviceProvider
            .GetRequiredService<IUnitOfWorkFactory>();
        PostgresIdentityRepository repository = new(dataSource);

        EntityId firstAdmin = EntityId.New();
        EntityId secondAdmin = EntityId.New();
        long firstVersion = await InsertUserAsync(
            dataSource,
            firstAdmin,
            AdminRoleId,
            cancellationToken).ConfigureAwait(true);
        long secondVersion = await InsertUserAsync(
            dataSource,
            secondAdmin,
            AdminRoleId,
            cancellationToken).ConfigureAwait(true);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository
            .ListAsync(cursor: null, limit: 0, cancellationToken)
            .AsTask()).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository
            .ListAsync(cursor: null, limit: 101, cancellationToken)
            .AsTask()).ConfigureAwait(true);
        UserSlice firstPage = await repository.ListAsync(
            cursor: null,
            limit: 1,
            cancellationToken).ConfigureAwait(true);
        IdentityUser firstListed = Assert.Single(firstPage.Users);
        UserSlice secondPage = await repository.ListAsync(
            new UserCursor(firstListed.CreatedAt, firstListed.Id),
            limit: 1,
            cancellationToken).ConfigureAwait(true);
        IdentityUser secondListed = Assert.Single(secondPage.Users);
        Assert.True(firstPage.HasMore);
        Assert.False(secondPage.HasMore);
        Assert.NotEqual(firstListed.Id, secondListed.Id);

        Task<UpdateUserDisposition> firstDisable = DisableAsync(
            repository,
            unitOfWorkFactory,
            firstAdmin,
            firstVersion,
            cancellationToken);
        Task<UpdateUserDisposition> secondDisable = DisableAsync(
            repository,
            unitOfWorkFactory,
            secondAdmin,
            secondVersion,
            cancellationToken);
        UpdateUserDisposition[] dispositions = await Task.WhenAll(
            firstDisable,
            secondDisable).ConfigureAwait(true);

        Assert.Single(dispositions, static value => value == UpdateUserDisposition.Updated);
        Assert.Single(
            dispositions,
            static value => value == UpdateUserDisposition.LastActiveAdminConflict);
        Assert.Equal(
            1L,
            await CountActiveAdminsAsync(dataSource, cancellationToken).ConfigureAwait(true));

        EntityId resetUser = EntityId.New();
        _ = await InsertUserAsync(
            dataSource,
            resetUser,
            UserRoleId,
            cancellationToken).ConfigureAwait(true);
        EntityId passwordResetId = EntityId.New();
        byte[] tokenHash = RandomNumberGenerator.GetBytes(32);
        await InsertPasswordResetAsync(
            dataSource,
            passwordResetId,
            resetUser,
            tokenHash,
            cancellationToken).ConfigureAwait(true);
        PasswordResetTokenCandidate[] candidates = [new(tokenHash, 7)];

        Task<PasswordResetConsumeResult?> firstConsume = ConsumeAsync(
            repository,
            unitOfWorkFactory,
            candidates,
            "poolai-password-v1:first",
            cancellationToken);
        Task<PasswordResetConsumeResult?> secondConsume = ConsumeAsync(
            repository,
            unitOfWorkFactory,
            candidates,
            "poolai-password-v1:second",
            cancellationToken);
        PasswordResetConsumeResult?[] consumed = await Task.WhenAll(
            firstConsume,
            secondConsume).ConfigureAwait(true);

        PasswordResetConsumeResult single = Assert.Single(consumed.OfType<PasswordResetConsumeResult>());
        Assert.Equal(passwordResetId, single.PasswordResetId);
        Assert.Equal(
            1L,
            await CountConsumedTokenAsync(
                dataSource,
                passwordResetId,
                cancellationToken).ConfigureAwait(true));
    }

    private static async Task<UpdateUserDisposition> DisableAsync(
        PostgresIdentityRepository repository,
        IUnitOfWorkFactory unitOfWorkFactory,
        EntityId userId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        UpdateUserPersistenceResult result = await repository.UpdateAsync(
            userId,
            expectedVersion,
            displayName: null,
            role: null,
            status: UserLifecycle.Disabled,
            assignedBy: userId,
            unitOfWorkContext: unitOfWork.Context,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result.Disposition;
    }

    private static async Task<PasswordResetConsumeResult?> ConsumeAsync(
        PostgresIdentityRepository repository,
        IUnitOfWorkFactory unitOfWorkFactory,
        IReadOnlyList<PasswordResetTokenCandidate> candidates,
        string passwordHash,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        PasswordResetConsumeResult? result = await repository.ConsumePasswordResetAsync(
            candidates,
            passwordHash,
            EntityId.New(),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async ValueTask<long> InsertUserAsync(
        NpgsqlDataSource dataSource,
        EntityId userId,
        Guid roleId,
        CancellationToken cancellationToken)
    {
        string email = $"identity-{userId.Value:N}@poolai.test";
        using (NpgsqlCommand user = dataSource.CreateCommand("""
                   INSERT INTO public.users (
                       id, email, normalized_email, display_name, password_hash, security_stamp
                   ) VALUES ($1, $2, $2, 'Identity test', 'poolai-password-v1:test', $3);
                   """))
        {
            user.Parameters.AddWithValue(userId.Value);
            user.Parameters.AddWithValue(email);
            user.Parameters.AddWithValue(Guid.CreateVersion7());
            Assert.Equal(
                1,
                await user.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
        }

        using (NpgsqlCommand role = dataSource.CreateCommand("""
                   INSERT INTO public.user_roles (user_id, role_id, assigned_by)
                   VALUES ($1, $2, $1);
                   """))
        {
            role.Parameters.AddWithValue(userId.Value);
            role.Parameters.AddWithValue(roleId);
            Assert.Equal(
                1,
                await role.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
        }

        using NpgsqlCommand version = dataSource.CreateCommand(
            "SELECT version FROM public.users WHERE id = $1;");
        version.Parameters.AddWithValue(userId.Value);
        return Assert.IsType<long>(await version
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static async ValueTask InsertPasswordResetAsync(
        NpgsqlDataSource dataSource,
        EntityId passwordResetId,
        EntityId userId,
        byte[] tokenHash,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = dataSource.CreateCommand("""
            INSERT INTO public.one_time_tokens (
                id, user_id, purpose, token_hash, pepper_version, expires_at
            ) VALUES (
                $1, $2, 'password_reset', $3, 7,
                clock_timestamp() + interval '30 minutes'
            );
            """);
        command.Parameters.AddWithValue(passwordResetId.Value);
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(tokenHash);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask<long> CountActiveAdminsAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = dataSource.CreateCommand("""
            SELECT count(*)
            FROM public.users AS users
            JOIN public.user_roles AS user_roles ON user_roles.user_id = users.id
            WHERE users.status = 'active' AND user_roles.role_id = $1;
            """);
        command.Parameters.AddWithValue(AdminRoleId);
        return Assert.IsType<long>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static async ValueTask<long> CountConsumedTokenAsync(
        NpgsqlDataSource dataSource,
        EntityId passwordResetId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = dataSource.CreateCommand("""
            SELECT count(*)
            FROM public.one_time_tokens
            WHERE id = $1 AND used_at IS NOT NULL;
            """);
        command.Parameters.AddWithValue(passwordResetId.Value);
        return Assert.IsType<long>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }
}
#pragma warning restore MA0051
