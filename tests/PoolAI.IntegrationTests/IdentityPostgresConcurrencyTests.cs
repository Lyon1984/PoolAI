#pragma warning disable MA0051 // The database-linearization regressions share one isolated PostgreSQL fixture.
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
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
    public async Task IdentityMutationsUseDatabaseLinearization()
    {
        // Governing contracts: AC-002/003/035/040/041 and docs/database/README.md.
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

        await AssertSessionLinearizationAsync(
            new PostgresIdentitySessionRepository(dataSource),
            unitOfWorkFactory,
            dataSource,
            cancellationToken).ConfigureAwait(true);
    }

    private static async ValueTask AssertSessionLinearizationAsync(
        PostgresIdentitySessionRepository repository,
        IUnitOfWorkFactory unitOfWorkFactory,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await InstallChallengeBoundaryTriggerAsync(dataSource, cancellationToken)
            .ConfigureAwait(false);

        EntityId loginUserId = EntityId.New();
        _ = await InsertUserAsync(
            dataSource,
            loginUserId,
            UserRoleId,
            cancellationToken).ConfigureAwait(false);
        await EnableTotpForTestAsync(dataSource, loginUserId, cancellationToken)
            .ConfigureAwait(false);
        long loginTokenVersion = await ReadTokenVersionAsync(
            dataSource,
            loginUserId,
            cancellationToken).ConfigureAwait(false);
        EntityId loginChallengeId = EntityId.New();
        byte[] loginChallengeHash = RandomNumberGenerator.GetBytes(32);
        await InsertTotpChallengeAsync(
            dataSource,
            loginChallengeId,
            loginUserId,
            loginChallengeHash,
            "login",
            secretEnvelope: null,
            cancellationToken).ConfigureAwait(false);

        byte[] initialRefreshHash = RandomNumberGenerator.GetBytes(32);
        EntityId initialSessionId = EntityId.New();
        IUnitOfWork loginUnitOfWork = await unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (loginUnitOfWork.ConfigureAwait(false))
        {
            MfaLoginPersistenceResult result = await repository.CompleteMfaLoginAsync(
                [new CredentialHashCandidate(loginChallengeHash, 7)],
                acceptedStep: 100,
                new RefreshSessionWrite(
                    initialSessionId,
                    initialRefreshHash,
                    7,
                    TimeSpan.FromDays(30),
                    "::ffff:127.0.0.1",
                    "PoolAI expiry-boundary test"),
                loginUnitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            Assert.Equal(MfaLoginDisposition.SessionCreated, result.Disposition);
            Assert.Equal(initialSessionId, result.SessionFamilyId);
            await loginUnitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        Assert.True(await WasConsumedBeforeExpiryAsync(
            dataSource,
            loginChallengeId,
            cancellationToken).ConfigureAwait(false));

        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<RefreshRotationDisposition> firstRotation = RotateRefreshAsync(
            repository,
            unitOfWorkFactory,
            initialRefreshHash,
            start.Task,
            cancellationToken);
        Task<RefreshRotationDisposition> secondRotation = RotateRefreshAsync(
            repository,
            unitOfWorkFactory,
            initialRefreshHash,
            start.Task,
            cancellationToken);
        start.SetResult();
        RefreshRotationDisposition[] rotations = await Task.WhenAll(
            firstRotation,
            secondRotation).ConfigureAwait(false);
        Assert.Single(rotations, static value => value == RefreshRotationDisposition.Rotated);
        Assert.Single(rotations, static value => value == RefreshRotationDisposition.Reused);
        Assert.False(await repository.IsSessionFamilyActiveAsync(
            loginUserId,
            initialSessionId,
            loginTokenVersion,
            cancellationToken).ConfigureAwait(false));
        Assert.Equal(
            (2L, 1L, 1L, 0L),
            await ReadRefreshFamilyStateAsync(
                dataSource,
                initialSessionId,
                cancellationToken).ConfigureAwait(false));

        EntityId setupUserId = EntityId.New();
        long setupExpectedVersion = await InsertUserAsync(
            dataSource,
            setupUserId,
            UserRoleId,
            cancellationToken).ConfigureAwait(false);
        long setupTokenVersion = await ReadTokenVersionAsync(
            dataSource,
            setupUserId,
            cancellationToken).ConfigureAwait(false);
        EntityId setupChallengeId = EntityId.New();
        byte[] setupChallengeHash = RandomNumberGenerator.GetBytes(32);
        JsonElement setupEnvelope = JsonSerializer.SerializeToElement(new { v = 1 });
        await InsertTotpChallengeAsync(
            dataSource,
            setupChallengeId,
            setupUserId,
            setupChallengeHash,
            "setup",
            setupEnvelope,
            cancellationToken).ConfigureAwait(false);
        JsonElement recoveryEnvelope = JsonSerializer.SerializeToElement(new { v = 1, count = 8 });
        TotpRecoveryCodeWrite[] recoveryCodes = Enumerable.Range(0, 8)
            .Select(static _ => new TotpRecoveryCodeWrite(
                EntityId.New(),
                RandomNumberGenerator.GetBytes(32),
                PepperVersion: 7))
            .ToArray();

        IUnitOfWork setupUnitOfWork = await unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (setupUnitOfWork.ConfigureAwait(false))
        {
            SecurityMutationPersistenceResult result = await repository.ConfirmTotpAsync(
                new TotpConfirmWrite(
                    setupUserId,
                    setupExpectedVersion,
                    [new CredentialHashCandidate(setupChallengeHash, 7)],
                    200,
                    setupEnvelope,
                    recoveryEnvelope,
                    recoveryCodes),
                setupUnitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            Assert.Equal(SecurityMutationDisposition.Updated, result.Disposition);
            Assert.Equal(setupExpectedVersion + 1, result.User!.Version);
            Assert.Equal(setupTokenVersion + 1, result.User.TokenVersion);
            await setupUnitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        Assert.True(await WasConsumedBeforeExpiryAsync(
            dataSource,
            setupChallengeId,
            cancellationToken).ConfigureAwait(false));
        Assert.Equal(
            8L,
            await CountRecoveryCodesAsync(
                dataSource,
                setupUserId,
                cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask InstallChallengeBoundaryTriggerAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = dataSource.CreateCommand("""
            CREATE OR REPLACE FUNCTION public.poolai_test_shorten_totp_challenge()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            BEGIN
                UPDATE public.one_time_tokens
                SET expires_at = clock_timestamp() + interval '50 milliseconds'
                WHERE user_id = NEW.id
                  AND purpose = 'totp_challenge';
                PERFORM pg_catalog.pg_sleep(0.25);
                RETURN NEW;
            END;
            $function$;

            CREATE TRIGGER tr_poolai_test_shorten_totp_challenge
            BEFORE UPDATE OF totp_last_accepted_step ON public.users
            FOR EACH ROW EXECUTE FUNCTION public.poolai_test_shorten_totp_challenge();
            """);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask EnableTotpForTestAsync(
        NpgsqlDataSource dataSource,
        EntityId userId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = dataSource.CreateCommand("""
            UPDATE public.users
            SET totp_secret_envelope = '{"v":1}'::jsonb
            WHERE id = $1;
            """);
        command.Parameters.AddWithValue(userId.Value);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask InsertTotpChallengeAsync(
        NpgsqlDataSource dataSource,
        EntityId challengeId,
        EntityId userId,
        byte[] tokenHash,
        string kind,
        JsonElement? secretEnvelope,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = dataSource.CreateCommand("""
            INSERT INTO public.one_time_tokens (
                id, user_id, purpose, token_hash, pepper_version, expires_at,
                challenge_kind, secret_envelope, security_stamp, token_version
            )
            SELECT $1, users.id, 'totp_challenge', $3, 7,
                   clock_timestamp() + interval '1 hour', $4, $5::jsonb,
                   users.security_stamp, users.token_version
            FROM public.users AS users
            WHERE users.id = $2;
            """);
        command.Parameters.AddWithValue(challengeId.Value);
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(tokenHash);
        command.Parameters.AddWithValue(kind);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = secretEnvelope is null
                ? DBNull.Value
                : secretEnvelope.Value.GetRawText(),
        });
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task<RefreshRotationDisposition> RotateRefreshAsync(
        PostgresIdentitySessionRepository repository,
        IUnitOfWorkFactory unitOfWorkFactory,
        byte[] refreshHash,
        Task start,
        CancellationToken cancellationToken)
    {
        await start.ConfigureAwait(false);
        IUnitOfWork unitOfWork = await unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        RefreshRotationPersistenceResult result = await repository.RotateRefreshSessionAsync(
            [new CredentialHashCandidate(refreshHash, 7)],
            new RefreshSessionWrite(
                EntityId.New(),
                RandomNumberGenerator.GetBytes(32),
                7,
                TimeSpan.FromDays(30),
                IpAddress: null,
                UserAgent: null),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result.Disposition;
    }

    private static async ValueTask<bool> WasConsumedBeforeExpiryAsync(
        NpgsqlDataSource dataSource,
        EntityId challengeId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = dataSource.CreateCommand("""
            SELECT used_at IS NOT NULL AND used_at < expires_at
            FROM public.one_time_tokens
            WHERE id = $1;
            """);
        command.Parameters.AddWithValue(challengeId.Value);
        return Assert.IsType<bool>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static async ValueTask<(long Rows, long Rotated, long Revoked, long Active)>
        ReadRefreshFamilyStateAsync(
            NpgsqlDataSource dataSource,
            EntityId familyId,
            CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = dataSource.CreateCommand("""
            SELECT count(*),
                   count(*) FILTER (WHERE status = 'rotated'),
                   count(*) FILTER (WHERE status = 'revoked'),
                   count(*) FILTER (WHERE status = 'active')
            FROM public.refresh_sessions
            WHERE family_id = $1;
            """);
        command.Parameters.AddWithValue(familyId.Value);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return (
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3));
    }

    private static async ValueTask<long> ReadTokenVersionAsync(
        NpgsqlDataSource dataSource,
        EntityId userId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = dataSource.CreateCommand(
            "SELECT token_version FROM public.users WHERE id = $1;");
        command.Parameters.AddWithValue(userId.Value);
        return Assert.IsType<long>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static async ValueTask<long> CountRecoveryCodesAsync(
        NpgsqlDataSource dataSource,
        EntityId userId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = dataSource.CreateCommand("""
            SELECT count(*)
            FROM public.totp_recovery_codes
            WHERE user_id = $1
              AND used_at IS NULL
              AND revoked_at IS NULL;
            """);
        command.Parameters.AddWithValue(userId.Value);
        return Assert.IsType<long>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
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
