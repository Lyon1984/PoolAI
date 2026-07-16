using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Database.Migrations;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Identity;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application;
using PoolAI.Modules.Identity.Application.Ports;
using PoolAI.Modules.Identity.Infrastructure.Security;
using PoolAI.Modules.Operations;
using PoolAI.Modules.Operations.Abstractions;
using Testcontainers.PostgreSql;

namespace PoolAI.IntegrationTests;

public sealed class IdentityVerticalSliceTests
{
    private const string CreatedEmail = "created-user@poolai.test";
    private const string ActiveEmail = "active-reset@poolai.test";
    private const string TemporaryPassword = "CreateOnly-Password-123!";
    private const string NewPassword = "Reset-Password-456!";
    private static readonly Guid AdminRoleId = Guid.Parse(
        "01900000-0000-7000-8000-000000000001");
    private static readonly Guid UserRoleId = Guid.Parse(
        "01900000-0000-7000-8000-000000000004");

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task IdentityCommandsAreAtomicIdempotentNonEnumeratingAndSecretSafe()
    {
        // Governing contracts: DEC-003/018/033, AC-001/035/040 and the M1-E1
        // password-reset rules in docs/开发执行规格-v1.0.md sections 7.3/7.4.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        IdentityTestRuntime runtime = await CreateRuntimeAsync(cancellationToken)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable runtimeLease = runtime.ConfigureAwait(true);

        UserView disabledUser = await AssertCreateReplayAndUpdatesAsync(
            runtime.Services,
            runtime.Administrator,
            runtime.Actor,
            cancellationToken).ConfigureAwait(true);
        await AssertLastAdminAndDisabledResetAreRejectedAsync(
            runtime.Services,
            runtime.Administrator,
            runtime.Actor,
            disabledUser,
            cancellationToken).ConfigureAwait(true);
        await AssertAdminResetForActiveUserAsync(
            runtime.Services,
            runtime.Administrator,
            runtime.Actor,
            runtime.ActiveUserId,
            cancellationToken).ConfigureAwait(true);
        await AssertForgotAndCompletionAsync(
            runtime.Services,
            runtime.Administrator,
            runtime.ActiveUserId,
            disabledUser,
            runtime.PasswordHasher,
            cancellationToken).ConfigureAwait(true);
    }

    private static async ValueTask<UserView> AssertCreateReplayAndUpdatesAsync(
        IServiceProvider services,
        NpgsqlDataSource administrator,
        IdentityActor actor,
        CancellationToken cancellationToken)
    {
        UserView created = await AssertCreateReplayAsync(
            services,
            administrator,
            actor,
            cancellationToken).ConfigureAwait(false);
        return await AssertRoleAndStatusUpdatesAsync(
            services,
            administrator,
            actor,
            created,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<UserView> AssertCreateReplayAsync(
        IServiceProvider services,
        NpgsqlDataSource administrator,
        IdentityActor actor,
        CancellationToken cancellationToken)
    {
        ICreateUserUseCase create = services.GetRequiredService<ICreateUserUseCase>();
        EntityId createRequestId = EntityId.New();
        CreateUserCommand createCommand = new(
            createRequestId,
            actor,
            "create-user-vertical-1",
            CreatedEmail,
            "Created user",
            SystemRole.User,
            TemporaryPassword,
            "192.0.2.10",
            "identity-integration-test");

        Result<IdentityCommandOutcome<UserView>> created = await create.ExecuteAsync(
            createCommand,
            cancellationToken).ConfigureAwait(true);
        Result<IdentityCommandOutcome<UserView>> replay = await create.ExecuteAsync(
            createCommand,
            cancellationToken).ConfigureAwait(true);

        Assert.True(created.IsSuccess);
        Assert.Equal(201, created.Value.StatusCode);
        Assert.False(created.Value.IsReplay);
        Assert.True(replay.IsSuccess);
        Assert.Equal(201, replay.Value.StatusCode);
        Assert.True(replay.Value.IsReplay);
        Assert.Equal(created.Value.Value, replay.Value.Value);
        Assert.Equal(1L, await CountAsync(
            administrator,
            "SELECT count(*) FROM public.users WHERE normalized_email = $1;",
            CreatedEmail,
            cancellationToken).ConfigureAwait(true));
        await AssertIdentityFactCardinalityAsync(
            administrator,
            created.Value.Value.Id,
            createRequestId,
            "identity.user.created",
            "user_created",
            expected: 1,
            cancellationToken).ConfigureAwait(true);
        await AssertAuditIdempotencyHashAsync(
            administrator,
            createRequestId,
            createCommand.IdempotencyKey,
            cancellationToken).ConfigureAwait(true);
        return created.Value.Value;
    }

    private static async ValueTask<UserView> AssertRoleAndStatusUpdatesAsync(
        IServiceProvider services,
        NpgsqlDataSource administrator,
        IdentityActor actor,
        UserView created,
        CancellationToken cancellationToken)
    {
        IUpdateUserUseCase update = services.GetRequiredService<IUpdateUserUseCase>();
        Result<IdentityCommandOutcome<UserView>> roleChanged = await update.ExecuteAsync(
            new UpdateUserCommand(
                EntityId.New(),
                actor,
                "update-user-role-vertical-1",
                created.Id,
                created.Version,
                DisplayName: "Role-updated user",
                Role: SystemRole.Operator,
                Status: null,
                "role assignment test",
                "192.0.2.10",
                "identity-integration-test"),
            cancellationToken).ConfigureAwait(true);
        Assert.True(roleChanged.IsSuccess);
        Assert.Equal(SystemRole.Operator, roleChanged.Value.Value.Role);
        Assert.Equal("Role-updated user", roleChanged.Value.Value.DisplayName);
        Assert.Equal(created.Version + 1, roleChanged.Value.Value.Version);
        UserSecurityFacts roleChangedFacts = await ReadUserSecurityFactsAsync(
            administrator,
            created.Id,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(roleChanged.Value.Value.Version, roleChangedFacts.TokenVersion);
        Assert.Equal(roleChanged.Value.Value.Version, roleChangedFacts.Version);

        await AssertStaleUpdateReplayAsync(
            update,
            actor,
            created,
            roleChanged.Value.Value,
            cancellationToken).ConfigureAwait(true);
        return await AssertDisableRoleAndStatusAsync(
            update,
            administrator,
            actor,
            roleChanged.Value.Value,
            cancellationToken).ConfigureAwait(true);
    }

    private static async ValueTask AssertStaleUpdateReplayAsync(
        IUpdateUserUseCase update,
        IdentityActor actor,
        UserView created,
        UserView roleChanged,
        CancellationToken cancellationToken)
    {
        const string StaleUpdateKey = "update-user-stale-vertical-1";
        UpdateUserCommand staleUpdate = new(
            EntityId.New(),
            actor,
            StaleUpdateKey,
            created.Id,
            created.Version,
            "Stale display name",
            Role: null,
            Status: null,
            Reason: null,
            "192.0.2.10",
            "identity-integration-test");
        Result<IdentityCommandOutcome<UserView>> stale = await update.ExecuteAsync(
            staleUpdate,
            cancellationToken).ConfigureAwait(true);
        Result<IdentityCommandOutcome<UserView>> staleReplay = await update.ExecuteAsync(
            staleUpdate,
            cancellationToken).ConfigureAwait(true);
        Assert.True(stale.IsFailure);
        Assert.True(staleReplay.IsFailure);
        Assert.Equal(IdentityErrorCodes.VersionConflict, stale.Error.Code);
        Assert.Equal($"\"v{roleChanged.Version}\"", stale.Error.ETag);
        Assert.Equal(stale.Error.ETag, staleReplay.Error.ETag);
        Assert.NotNull(stale.Error.Presentation);
        Assert.Equal(stale.Error.Presentation, staleReplay.Error.Presentation);
        Assert.Equal(412, stale.Error.Presentation.Status);

        Result<IdentityCommandOutcome<UserView>> changedPrecondition = await update.ExecuteAsync(
            staleUpdate with
            {
                RequestId = EntityId.New(),
                ExpectedVersion = roleChanged.Version,
            },
            cancellationToken).ConfigureAwait(true);
        Assert.True(changedPrecondition.IsFailure);
        Assert.Equal(IdentityErrorCodes.IdempotencyConflict, changedPrecondition.Error.Code);
    }

    private static async ValueTask<UserView> AssertDisableRoleAndStatusAsync(
        IUpdateUserUseCase update,
        NpgsqlDataSource administrator,
        IdentityActor actor,
        UserView roleChanged,
        CancellationToken cancellationToken)
    {
        EntityId disableRequestId = EntityId.New();
        Result<IdentityCommandOutcome<UserView>> disabled = await update.ExecuteAsync(
            new UpdateUserCommand(
                disableRequestId,
                actor,
                "disable-user-vertical-1",
                roleChanged.Id,
                roleChanged.Version,
                DisplayName: null,
                Role: SystemRole.User,
                Status: UserLifecycle.Disabled,
                "disable test account",
                "192.0.2.10",
                "identity-integration-test"),
            cancellationToken).ConfigureAwait(true);
        Assert.True(disabled.IsSuccess);
        Assert.Equal(UserLifecycle.Disabled, disabled.Value.Value.Status);
        Assert.Equal(roleChanged.Version + 1, disabled.Value.Value.Version);
        UserSecurityFacts facts = await ReadUserSecurityFactsAsync(
            administrator,
            disabled.Value.Value.Id,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal("user", facts.Role);
        Assert.Equal("disabled", facts.Status);
        Assert.Equal(disabled.Value.Value.Version, facts.Version);
        Assert.Equal(disabled.Value.Value.Version, facts.TokenVersion);
        await AssertAuditIdempotencyHashAsync(
            administrator,
            disableRequestId,
            "disable-user-vertical-1",
            cancellationToken).ConfigureAwait(true);
        return disabled.Value.Value;
    }

    private static async ValueTask AssertLastAdminAndDisabledResetAreRejectedAsync(
        IServiceProvider services,
        NpgsqlDataSource administrator,
        IdentityActor actor,
        UserView disabledUser,
        CancellationToken cancellationToken)
    {
        UserSecurityFacts adminBefore = await ReadUserSecurityFactsAsync(
            administrator,
            actor.UserId,
            cancellationToken).ConfigureAwait(true);
        IUpdateUserUseCase update = services.GetRequiredService<IUpdateUserUseCase>();
        Result<IdentityCommandOutcome<UserView>> lastAdmin = await update.ExecuteAsync(
            new UpdateUserCommand(
                EntityId.New(),
                actor,
                "disable-last-admin-vertical-1",
                actor.UserId,
                adminBefore.Version,
                DisplayName: null,
                Role: null,
                Status: UserLifecycle.Disabled,
                "last admin guard test",
                "192.0.2.10",
                "identity-integration-test"),
            cancellationToken).ConfigureAwait(true);
        Assert.True(lastAdmin.IsFailure);
        Assert.Equal(IdentityErrorCodes.ResourceConflict, lastAdmin.Error.Code);
        UserSecurityFacts adminAfter = await ReadUserSecurityFactsAsync(
            administrator,
            actor.UserId,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(adminBefore, adminAfter);

        IRequestAdminPasswordResetUseCase adminReset = services
            .GetRequiredService<IRequestAdminPasswordResetUseCase>();
        Result<IdentityCommandOutcome> reset = await adminReset.ExecuteAsync(
            new AdminPasswordResetCommand(
                EntityId.New(),
                actor,
                "reset-disabled-user-vertical-1",
                disabledUser.Id,
                "disabled reset rejection test",
                "192.0.2.10",
                "identity-integration-test"),
            cancellationToken).ConfigureAwait(true);
        Assert.True(reset.IsFailure);
        Assert.Equal(IdentityErrorCodes.ResourceConflict, reset.Error.Code);
        Assert.Equal(0L, await CountResetTokensAsync(
            administrator,
            disabledUser.Id,
            cancellationToken).ConfigureAwait(true));
    }

    private static async ValueTask AssertForgotAndCompletionAsync(
        IServiceProvider services,
        NpgsqlDataSource administrator,
        EntityId activeUserId,
        UserView disabledUser,
        VersionedPasswordHasher passwordHasher,
        CancellationToken cancellationToken)
    {
        FirstResetRequest first = await AssertFirstForgotRequestsAsync(
            services,
            administrator,
            activeUserId,
            disabledUser,
            cancellationToken).ConfigureAwait(false);
        PasswordResetSecrets secrets = await AssertSupersedingForgotAsync(
            services,
            administrator,
            activeUserId,
            first,
            cancellationToken).ConfigureAwait(false);
        await AssertPasswordResetCompletionAsync(
            services,
            administrator,
            activeUserId,
            disabledUser,
            passwordHasher,
            secrets,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AssertAdminResetForActiveUserAsync(
        IServiceProvider services,
        NpgsqlDataSource administrator,
        IdentityActor actor,
        EntityId activeUserId,
        CancellationToken cancellationToken)
    {
        IRequestAdminPasswordResetUseCase adminReset = services
            .GetRequiredService<IRequestAdminPasswordResetUseCase>();
        EntityId requestId = EntityId.New();
        const string IdempotencyKey = "reset-active-user-vertical-1";
        AdminPasswordResetCommand command = new(
            requestId,
            actor,
            IdempotencyKey,
            activeUserId,
            "active reset request test",
            "192.0.2.10",
            "identity-integration-test");
        Result<IdentityCommandOutcome> accepted = await adminReset.ExecuteAsync(
            command,
            cancellationToken).ConfigureAwait(false);
        Result<IdentityCommandOutcome> replay = await adminReset.ExecuteAsync(
            command,
            cancellationToken).ConfigureAwait(false);

        Assert.True(accepted.IsSuccess);
        Assert.False(accepted.Value.IsReplay);
        Assert.True(replay.IsSuccess);
        Assert.True(replay.Value.IsReplay);
        Assert.Equal(1L, await CountResetTokensAsync(
            administrator,
            activeUserId,
            cancellationToken).ConfigureAwait(false));
        await AssertIdentityFactCardinalityAsync(
            administrator,
            activeUserId,
            requestId,
            "identity.password_reset.requested",
            "password_reset_requested",
            expected: 1,
            cancellationToken).ConfigureAwait(false);
        await AssertAuditIdempotencyHashAsync(
            administrator,
            requestId,
            IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<FirstResetRequest> AssertFirstForgotRequestsAsync(
        IServiceProvider services,
        NpgsqlDataSource administrator,
        EntityId activeUserId,
        UserView disabledUser,
        CancellationToken cancellationToken)
    {
        IRequestPasswordResetUseCase forgot = services
            .GetRequiredService<IRequestPasswordResetUseCase>();
        EntityId missingRequestId = EntityId.New();
        EntityId disabledRequestId = EntityId.New();
        EntityId activeRequestId = EntityId.New();
        Result<IdentityCommandOutcome> missing = await forgot.ExecuteAsync(
            Forgot(
                missingRequestId,
                "missing-user@poolai.test"),
            cancellationToken).ConfigureAwait(true);
        Result<IdentityCommandOutcome> disabled = await forgot.ExecuteAsync(
            Forgot(disabledRequestId, CreatedEmail),
            cancellationToken).ConfigureAwait(true);
        Result<IdentityCommandOutcome> active = await forgot.ExecuteAsync(
            Forgot(activeRequestId, ActiveEmail),
            cancellationToken).ConfigureAwait(true);

        AssertSameAcceptedResponse(missing, disabled, active);
        Assert.Equal(0L, await CountAnonymousAuthIdempotencyRecordsAsync(
            administrator,
            cancellationToken).ConfigureAwait(true));
        Assert.Equal(0L, await CountFactsForRequestsAsync(
            administrator,
            [missingRequestId, disabledRequestId],
            cancellationToken).ConfigureAwait(true));
        Assert.Equal(0L, await CountResetTokensAsync(
            administrator,
            disabledUser.Id,
            cancellationToken).ConfigureAwait(true));
        Assert.Equal(2L, await CountResetTokensAsync(
            administrator,
            activeUserId,
            cancellationToken).ConfigureAwait(true));
        await AssertIdentityFactCardinalityAsync(
            administrator,
            activeUserId,
            activeRequestId,
            "identity.password_reset.requested",
            "password_reset_requested",
            expected: 1,
            cancellationToken).ConfigureAwait(true);

        EmailFacts firstEmail = await ReadNewestEmailAsync(
            administrator,
            activeUserId,
            cancellationToken).ConfigureAwait(true);
        EmailSecretEnvelopePlaintext firstPlaintext = services
            .GetRequiredService<IEmailSecretEnvelope>()
            .Decrypt(
                firstEmail.RecipientEnvelope,
                firstEmail.DeliverySecretEnvelope,
                firstEmail.Id);
        string firstToken = ExtractToken(firstPlaintext.ResetUrl);
        return new FirstResetRequest(firstEmail, firstPlaintext, firstToken);
    }

    private static async ValueTask<PasswordResetSecrets> AssertSupersedingForgotAsync(
        IServiceProvider services,
        NpgsqlDataSource administrator,
        EntityId activeUserId,
        FirstResetRequest first,
        CancellationToken cancellationToken)
    {
        IRequestPasswordResetUseCase forgot = services
            .GetRequiredService<IRequestPasswordResetUseCase>();
        EntityId repeatedRequestId = EntityId.New();
        Result<IdentityCommandOutcome> repeated = await forgot.ExecuteAsync(
            Forgot(repeatedRequestId, ActiveEmail),
            cancellationToken).ConfigureAwait(true);
        Assert.True(repeated.IsSuccess);
        Assert.Equal(202, repeated.Value.StatusCode);
        Assert.Equal(3L, await CountResetTokensAsync(
            administrator,
            activeUserId,
            cancellationToken).ConfigureAwait(true));
        TokenFacts firstTokenFacts = await ReadTokenAsync(
            administrator,
            first.Email.TokenId,
            cancellationToken).ConfigureAwait(true);
        Assert.NotNull(firstTokenFacts.RevokedAt);
        Assert.Equal("superseded", firstTokenFacts.RevokeReason);
        Assert.Null(firstTokenFacts.UsedAt);

        EmailFacts newestEmail = await ReadNewestEmailAsync(
            administrator,
            activeUserId,
            cancellationToken).ConfigureAwait(true);
        Assert.NotEqual(first.Email.Id, newestEmail.Id);
        EmailSecretEnvelopePlaintext plaintext = services
            .GetRequiredService<IEmailSecretEnvelope>()
            .Decrypt(
                newestEmail.RecipientEnvelope,
                newestEmail.DeliverySecretEnvelope,
                newestEmail.Id);
        Assert.Equal(ActiveEmail, plaintext.Recipient);
        string token = ExtractToken(plaintext.ResetUrl);
        Assert.Equal(43, token.Length);
        Assert.Equal("password-reset-v1", newestEmail.TemplateCode);
        Assert.Equal(30, newestEmail.TemplatePayload.GetProperty(
            "expires_in_minutes").GetInt32());
        return new PasswordResetSecrets(
            first.Plaintext,
            first.Token,
            newestEmail,
            plaintext,
            token);
    }

    private static async ValueTask AssertPasswordResetCompletionAsync(
        IServiceProvider services,
        NpgsqlDataSource administrator,
        EntityId activeUserId,
        UserView disabledUser,
        VersionedPasswordHasher passwordHasher,
        PasswordResetSecrets secrets,
        CancellationToken cancellationToken)
    {
        UserSecurityFacts beforeReset = await ReadUserSecurityFactsAsync(
            administrator,
            activeUserId,
            cancellationToken).ConfigureAwait(true);
        EntityId refreshSessionId = await InsertRefreshSessionAsync(
            administrator,
            activeUserId,
            cancellationToken).ConfigureAwait(true);
        ICompletePasswordResetUseCase complete = services
            .GetRequiredService<ICompletePasswordResetUseCase>();
        EntityId completeRequestId = EntityId.New();
        Result<IdentityCommandOutcome> completed = await complete.ExecuteAsync(
            new CompletePasswordResetCommand(
                completeRequestId,
                secrets.Token,
                NewPassword,
                "192.0.2.10",
                "identity-integration-test"),
            cancellationToken).ConfigureAwait(true);
        EntityId reusedRequestId = EntityId.New();
        Result<IdentityCommandOutcome> reused = await complete.ExecuteAsync(
            new CompletePasswordResetCommand(
                reusedRequestId,
                secrets.Token,
                NewPassword,
                "192.0.2.10",
                "identity-integration-test"),
            cancellationToken).ConfigureAwait(true);

        Assert.True(completed.IsSuccess);
        Assert.Equal(204, completed.Value.StatusCode);
        Assert.True(reused.IsFailure);
        Assert.Equal(IdentityErrorCodes.PasswordResetTokenInvalid, reused.Error.Code);
        Assert.Equal(0L, await CountAnonymousAuthIdempotencyRecordsAsync(
            administrator,
            cancellationToken).ConfigureAwait(true));
        UserSecurityFacts afterReset = await ReadUserSecurityFactsAsync(
            administrator,
            activeUserId,
            cancellationToken).ConfigureAwait(true);
        await AssertPasswordAndSessionFactsAsync(
            administrator,
            activeUserId,
            refreshSessionId,
            completeRequestId,
            beforeReset,
            afterReset,
            passwordHasher,
            secrets,
            cancellationToken).ConfigureAwait(false);
        await AssertSecretTextAbsentAsync(
            administrator,
            activeUserId,
            disabledUser.Id,
            secrets,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AssertPasswordAndSessionFactsAsync(
        NpgsqlDataSource administrator,
        EntityId activeUserId,
        EntityId refreshSessionId,
        EntityId completeRequestId,
        UserSecurityFacts beforeReset,
        UserSecurityFacts afterReset,
        VersionedPasswordHasher passwordHasher,
        PasswordResetSecrets secrets,
        CancellationToken cancellationToken)
    {
        Assert.StartsWith(VersionedPasswordHasher.Prefix, afterReset.PasswordHash, StringComparison.Ordinal);
        Assert.True(passwordHasher.Verify(afterReset.PasswordHash, NewPassword));
        Assert.NotEqual(beforeReset.SecurityStamp, afterReset.SecurityStamp);
        Assert.Equal(beforeReset.TokenVersion + 1, afterReset.TokenVersion);
        Assert.Equal(beforeReset.Version + 1, afterReset.Version);
        await AssertRefreshSessionRevokedAsync(
            administrator,
            refreshSessionId,
            cancellationToken).ConfigureAwait(true);
        TokenFacts newestToken = await ReadTokenAsync(
            administrator,
            secrets.Email.TokenId,
            cancellationToken).ConfigureAwait(true);
        Assert.NotNull(newestToken.UsedAt);
        Assert.Null(newestToken.RevokedAt);
        Assert.Equal((short)7, newestToken.PepperVersion);
        Assert.Equal(32, newestToken.Hash.Length);
        Assert.DoesNotContain(
            secrets.Token,
            Convert.ToBase64String(newestToken.Hash),
            StringComparison.Ordinal);
        await AssertIdentityFactCardinalityAsync(
            administrator,
            activeUserId,
            completeRequestId,
            "identity.password_reset.completed",
            "password_reset_completed",
            expected: 1,
            cancellationToken).ConfigureAwait(true);
    }

    private static async ValueTask AssertSecretTextAbsentAsync(
        NpgsqlDataSource administrator,
        EntityId activeUserId,
        EntityId disabledUserId,
        PasswordResetSecrets secrets,
        CancellationToken cancellationToken)
    {
        string persistedNonSecretText = await ReadAuditEventAndTemplateTextAsync(
            administrator,
            [activeUserId, disabledUserId],
            cancellationToken).ConfigureAwait(true);
        foreach (string forbidden in new[]
                 {
                     TemporaryPassword,
                     NewPassword,
                     secrets.FirstToken,
                     secrets.Token,
                     secrets.FirstPlaintext.ResetUrl,
                     secrets.Plaintext.ResetUrl,
                     ActiveEmail,
                 })
        {
            Assert.DoesNotContain(forbidden, persistedNonSecretText, StringComparison.Ordinal);
        }
    }

    private static ForgotPasswordCommand Forgot(
        EntityId requestId,
        string email) => new(
        requestId,
        email,
        "192.0.2.10",
        "identity-integration-test");

    private static void AssertSameAcceptedResponse(
        params Result<IdentityCommandOutcome>[] results)
    {
        Assert.All(results, static result =>
        {
            Assert.True(result.IsSuccess);
            Assert.Equal(202, result.Value.StatusCode);
            Assert.False(result.Value.IsReplay);
        });
    }

    private static async ValueTask<IdentityTestRuntime> CreateRuntimeAsync(
        CancellationToken cancellationToken)
    {
        string administratorPassword = Secret();
        string apiPassword = Secret();
        PostgreSqlContainer container = new PostgreSqlBuilder(
            PostgresMigrationTests.ReadPostgresImage())
            .WithDatabase("poolai")
            .WithUsername("postgres")
            .WithPassword(administratorPassword)
            .Build();
        await container.StartAsync(cancellationToken).ConfigureAwait(false);
        string administratorConnectionString = container.GetConnectionString();
        await PostgresMigrationTests.ProvisionRuntimeRolesAsync(
            administratorConnectionString,
            cancellationToken).ConfigureAwait(false);
        MigrationCatalog catalog = await MigrationCatalog.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        await new PostgresMigrator(catalog).ApplyAsync(
            administratorConnectionString,
            "PoolAI.IntegrationTests.identity-vertical",
            cancellationToken).ConfigureAwait(false);
        await SetApiPasswordAsync(
            administratorConnectionString,
            apiPassword,
            cancellationToken).ConfigureAwait(false);

        string apiConnectionString = WithApiRole(
            administratorConnectionString,
            apiPassword);
        NpgsqlDataSource administrator = NpgsqlDataSource.Create(
            administratorConnectionString);
        VersionedPasswordHasher passwordHasher = new();
        EntityId adminId = EntityId.New();
        EntityId activeUserId = EntityId.New();
        await SeedUsersAsync(
            administrator,
            passwordHasher,
            adminId,
            activeUserId,
            cancellationToken).ConfigureAwait(false);
        ServiceProvider services = BuildServices(
            apiConnectionString,
            TestConfiguration(apiConnectionString));
        return new IdentityTestRuntime(
            container,
            administrator,
            services,
            new IdentityActor(adminId, SystemRole.Admin, TokenVersion: 2),
            activeUserId,
            passwordHasher);
    }

    private static async ValueTask SeedUsersAsync(
        NpgsqlDataSource administrator,
        VersionedPasswordHasher passwordHasher,
        EntityId adminId,
        EntityId activeUserId,
        CancellationToken cancellationToken)
    {
        await InsertUserAsync(
            administrator,
            adminId,
            "admin@poolai.test",
            "Bootstrap Admin",
            passwordHasher.Hash("Admin-Seed-Password-123!"),
            AdminRoleId,
            cancellationToken).ConfigureAwait(false);
        await InsertUserAsync(
            administrator,
            activeUserId,
            ActiveEmail,
            "Active reset user",
            passwordHasher.Hash("Active-Seed-Password-123!"),
            UserRoleId,
            cancellationToken).ConfigureAwait(false);
    }

    private static ServiceProvider BuildServices(
        string connectionString,
        IConfiguration configuration)
    {
        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddPoolAiPostgresRuntime(connectionString);
        services.AddOperationsModule(configuration, "Integration");
        services.Replace(ServiceDescriptor.Singleton<IOperationalEventWriter>(
            new NoOpOperationalEventWriter()));
        services.AddIdentityModule(configuration);
        services.Replace(ServiceDescriptor.Singleton<IPasswordResetRateLimiter>(
            new AllowPasswordResetRateLimiter()));
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private static ConfigurationManager TestConfiguration(string connectionString)
    {
        string envelopeKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        ConfigurationManager configuration = new();
        configuration["Data:Postgres:ConnectionString"] = connectionString;
        configuration["Data:Redis:ConnectionString"] = "127.0.0.1:1,abortConnect=false";
        configuration["Data:Redis:KeyPrefix"] = "poolai:r1:identity-integration:";
        configuration["Health:Ntp:Server"] = "127.0.0.1";
        configuration["Health:Ntp:Port"] = "123";
        configuration["App:PublicBaseUrl"] = "https://app.poolai.test/base/";
        configuration["Email:FromAddress"] = "no-reply@poolai.test";
        configuration["Auth:Password:MinLength"] = "12";
        configuration["Auth:PasswordReset:TokenMinutes"] = "30";
        configuration["Auth:TokenHash:CurrentPepperVersion"] = "7";
        configuration["Auth:TokenHash:CurrentPepper"] = SecretBase64();
        configuration["Auth:PasswordReset:RateLimitScopePepper"] = SecretBase64();
        configuration["Auth:PasswordReset:IpRequestsPerMinute"] = "5";
        configuration["Auth:PasswordReset:AccountRequestsPerMinute"] = "3";
        configuration["Idempotency:RequestHashPepper"] = SecretBase64();
        configuration["Secrets:Envelope:CurrentKeyId"] = "email-k1";
        configuration["Secrets:Envelope:CurrentKey"] = envelopeKey;
        configuration["Secrets:Envelope:DecryptKeyRing:email-k1"] = envelopeKey;
        return configuration;
    }

    private static async ValueTask SetApiPasswordAsync(
        string connectionString,
        string password,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        using (NpgsqlCommand setting = new(
                   "SELECT pg_catalog.set_config('poolai.test_api_password', $1, false);",
                   connection))
        {
            setting.Parameters.AddWithValue(password);
            _ = await setting.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            using NpgsqlCommand alter = new(
                """
                DO $password$
                BEGIN
                    EXECUTE pg_catalog.format(
                        'ALTER ROLE poolai_api PASSWORD %L',
                        pg_catalog.current_setting('poolai.test_api_password'));
                END;
                $password$;
                """,
                connection);
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            using NpgsqlCommand clear = new(
                "SELECT pg_catalog.set_config('poolai.test_api_password', '', false);",
                connection);
            _ = await clear.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static string WithApiRole(string connectionString, string password) =>
        new NpgsqlConnectionStringBuilder(connectionString)
        {
            Username = "poolai_api",
            Password = password,
            ApplicationName = "PoolAI.IntegrationTests.identity-vertical",
        }.ConnectionString;

    private static async ValueTask InsertUserAsync(
        NpgsqlDataSource dataSource,
        EntityId userId,
        string email,
        string displayName,
        string passwordHash,
        Guid roleId,
        CancellationToken cancellationToken)
    {
        using (NpgsqlCommand user = dataSource.CreateCommand("""
                   INSERT INTO public.users (
                       id, email, normalized_email, display_name, password_hash, security_stamp
                   ) VALUES ($1, $2, $2, $3, $4, $5);
                   """))
        {
            user.Parameters.AddWithValue(userId.Value);
            user.Parameters.AddWithValue(email);
            user.Parameters.AddWithValue(displayName);
            user.Parameters.AddWithValue(passwordHash);
            user.Parameters.AddWithValue(Guid.CreateVersion7());
            Assert.Equal(
                1,
                await user.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
        }

        using NpgsqlCommand role = dataSource.CreateCommand("""
            INSERT INTO public.user_roles (user_id, role_id, assigned_by)
            VALUES ($1, $2, $1);
            """);
        role.Parameters.AddWithValue(userId.Value);
        role.Parameters.AddWithValue(roleId);
        Assert.Equal(
            1,
            await role.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask<EntityId> InsertRefreshSessionAsync(
        NpgsqlDataSource dataSource,
        EntityId userId,
        CancellationToken cancellationToken)
    {
        EntityId sessionId = EntityId.New();
        using NpgsqlCommand command = dataSource.CreateCommand("""
            INSERT INTO public.refresh_sessions (
                id, family_id, user_id, token_hash, pepper_version, expires_at
            ) VALUES ($1, $2, $3, $4, 1, clock_timestamp() + interval '1 day');
            """);
        command.Parameters.AddWithValue(sessionId.Value);
        command.Parameters.AddWithValue(Guid.CreateVersion7());
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(RandomNumberGenerator.GetBytes(32));
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
        return sessionId;
    }

    private static async ValueTask AssertRefreshSessionRevokedAsync(
        NpgsqlDataSource dataSource,
        EntityId sessionId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = dataSource.CreateCommand("""
            SELECT status, revoked_at IS NOT NULL, revoke_reason
            FROM public.refresh_sessions
            WHERE id = $1;
            """);
        command.Parameters.AddWithValue(sessionId.Value);
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        Assert.Equal("revoked", reader.GetString(0));
        Assert.True(reader.GetBoolean(1));
        Assert.Equal("password_reset", reader.GetString(2));
    }

    private static async ValueTask AssertIdentityFactCardinalityAsync(
        NpgsqlDataSource dataSource,
        EntityId targetId,
        EntityId requestId,
        string auditAction,
        string eventType,
        long expected,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = dataSource.CreateCommand("""
            SELECT
                (SELECT count(*) FROM public.audit_logs
                 WHERE target_id = $1 AND request_id = $2 AND action = $3),
                (SELECT count(*) FROM public.outbox_messages
                 WHERE aggregate_id = $1 AND correlation_id = $2
                   AND topic = 'poolai.identity.v1' AND event_type = $4);
            """);
        command.Parameters.AddWithValue(targetId.Value);
        command.Parameters.AddWithValue(requestId.Value);
        command.Parameters.AddWithValue(auditAction);
        command.Parameters.AddWithValue(eventType);
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        Assert.Equal(expected, reader.GetInt64(0));
        Assert.Equal(expected, reader.GetInt64(1));
    }

    private static async ValueTask<long> CountFactsForRequestsAsync(
        NpgsqlDataSource dataSource,
        EntityId[] requestIds,
        CancellationToken cancellationToken)
    {
        Guid[] ids = requestIds.Select(static id => id.Value).ToArray();
        using NpgsqlCommand command = dataSource.CreateCommand("""
            SELECT
                (SELECT count(*) FROM public.audit_logs
                 WHERE request_id = ANY($1)
                   AND action = 'identity.password_reset.requested')
              + (SELECT count(*) FROM public.outbox_messages
                 WHERE correlation_id = ANY($1)
                   AND topic = 'poolai.identity.v1'
                   AND event_type = 'password_reset_requested');
            """);
        command.Parameters.AddWithValue(ids);
        return Assert.IsType<long>(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static async ValueTask<long> CountAnonymousAuthIdempotencyRecordsAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = dataSource.CreateCommand("""
            SELECT count(*)
            FROM public.idempotency_records
            WHERE scope LIKE '%:/api/v1/auth/forgot-password'
               OR scope LIKE '%:/api/v1/auth/reset-password';
            """);
        return Assert.IsType<long>(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask AssertAuditIdempotencyHashAsync(
        NpgsqlDataSource dataSource,
        EntityId requestId,
        string rawIdempotencyKey,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = dataSource.CreateCommand("""
            SELECT metadata::text
            FROM public.audit_logs
            WHERE request_id = $1;
            """);
        command.Parameters.AddWithValue(requestId.Value);
        string metadataText = Assert.IsType<string>(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        using JsonDocument metadata = JsonDocument.Parse(metadataText);
        string digest = Assert.IsType<string>(
            metadata.RootElement.GetProperty("idempotency_key_hash").GetString());
        Assert.Equal(64, digest.Length);
        Assert.All(digest, static character => Assert.True(
            character is >= '0' and <= '9' or >= 'a' and <= 'f'));
        Assert.DoesNotContain(rawIdempotencyKey, metadataText, StringComparison.Ordinal);
    }

    private static async ValueTask<long> CountResetTokensAsync(
        NpgsqlDataSource dataSource,
        EntityId userId,
        CancellationToken cancellationToken) => await CountAsync(
        dataSource,
        "SELECT count(*) FROM public.one_time_tokens WHERE user_id = $1 AND purpose = 'password_reset';",
        userId.Value,
        cancellationToken).ConfigureAwait(false);

    private static async ValueTask<long> CountAsync(
        NpgsqlDataSource dataSource,
        string sql,
        object parameter,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(parameter);
        return Assert.IsType<long>(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static async ValueTask<UserSecurityFacts> ReadUserSecurityFactsAsync(
        NpgsqlDataSource dataSource,
        EntityId userId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = dataSource.CreateCommand("""
            SELECT u.password_hash, u.security_stamp, u.token_version, u.version,
                   u.status, r.code
            FROM public.users AS u
            JOIN public.user_roles AS ur ON ur.user_id = u.id
            JOIN public.roles AS r ON r.id = ur.role_id
            WHERE u.id = $1;
            """);
        command.Parameters.AddWithValue(userId.Value);
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return new UserSecurityFacts(
            reader.GetString(0),
            reader.GetGuid(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetString(5));
    }

    private static async ValueTask<EmailFacts> ReadNewestEmailAsync(
        NpgsqlDataSource dataSource,
        EntityId userId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = dataSource.CreateCommand("""
            SELECT id, one_time_token_id, recipient_envelope::text,
                   template_code, template_payload::text,
                   delivery_secret_envelope::text
            FROM public.email_outbox
            WHERE user_id = $1
            ORDER BY created_at DESC, id DESC
            LIMIT 1;
            """);
        command.Parameters.AddWithValue(userId.Value);
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return new EmailFacts(
            new EntityId(reader.GetGuid(0)),
            new EntityId(reader.GetGuid(1)),
            JsonSerializer.Deserialize<JsonElement>(reader.GetString(2)),
            reader.GetString(3),
            JsonSerializer.Deserialize<JsonElement>(reader.GetString(4)),
            JsonSerializer.Deserialize<JsonElement>(reader.GetString(5)));
    }

    private static async ValueTask<TokenFacts> ReadTokenAsync(
        NpgsqlDataSource dataSource,
        EntityId tokenId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = dataSource.CreateCommand("""
            SELECT pepper_version, token_hash, used_at, revoked_at, revoke_reason
            FROM public.one_time_tokens
            WHERE id = $1;
            """);
        command.Parameters.AddWithValue(tokenId.Value);
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return new TokenFacts(
            reader.GetInt16(0),
            reader.GetFieldValue<byte[]>(1),
            reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private static async ValueTask<string> ReadAuditEventAndTemplateTextAsync(
        NpgsqlDataSource dataSource,
        EntityId[] userIds,
        CancellationToken cancellationToken)
    {
        Guid[] ids = userIds.Select(static id => id.Value).ToArray();
        using NpgsqlCommand command = dataSource.CreateCommand("""
            SELECT concat_ws('|',
                COALESCE((SELECT string_agg(concat_ws('|',
                    before_state::text, after_state::text, metadata::text), '|')
                    FROM public.audit_logs WHERE target_id = ANY($1)), ''),
                COALESCE((SELECT string_agg(payload::text, '|')
                    FROM public.outbox_messages
                    WHERE aggregate_id = ANY($1) AND topic = 'poolai.identity.v1'), ''),
                COALESCE((SELECT string_agg(template_payload::text, '|')
                    FROM public.email_outbox WHERE user_id = ANY($1)), ''));
            """);
        command.Parameters.AddWithValue(ids);
        return Assert.IsType<string>(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static string ExtractToken(string resetUrl)
    {
        Uri uri = new(resetUrl, UriKind.Absolute);
        const string Prefix = "?token=";
        Assert.StartsWith(Prefix, uri.Query, StringComparison.Ordinal);
        return Uri.UnescapeDataString(uri.Query[Prefix.Length..]);
    }

    private static string Secret() => Convert.ToHexString(
        RandomNumberGenerator.GetBytes(24));

    private static string SecretBase64() => Convert.ToBase64String(
        RandomNumberGenerator.GetBytes(32));

    private sealed class AllowPasswordResetRateLimiter : IPasswordResetRateLimiter
    {
        private static readonly PasswordResetRateLimitDecision Allowed = new(
            PasswordResetRateLimitDisposition.Allowed,
            RetryAfterSeconds: null);

        public ValueTask<PasswordResetRateLimitDecision> CheckForgotAsync(
            string ipAddress,
            string normalizedAccount,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Allowed);
        }

        public ValueTask<PasswordResetRateLimitDecision> CheckAdminAsync(
            string normalizedAccount,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Allowed);
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

    private sealed record UserSecurityFacts(
        string PasswordHash,
        Guid SecurityStamp,
        long TokenVersion,
        long Version,
        string Status,
        string Role);

    private sealed record EmailFacts(
        EntityId Id,
        EntityId TokenId,
        JsonElement RecipientEnvelope,
        string TemplateCode,
        JsonElement TemplatePayload,
        JsonElement DeliverySecretEnvelope);

    private sealed record TokenFacts(
        short PepperVersion,
        byte[] Hash,
        DateTimeOffset? UsedAt,
        DateTimeOffset? RevokedAt,
        string? RevokeReason);

    private sealed record FirstResetRequest(
        EmailFacts Email,
        EmailSecretEnvelopePlaintext Plaintext,
        string Token);

    private sealed record PasswordResetSecrets(
        EmailSecretEnvelopePlaintext FirstPlaintext,
        string FirstToken,
        EmailFacts Email,
        EmailSecretEnvelopePlaintext Plaintext,
        string Token);

    private sealed class IdentityTestRuntime : IAsyncDisposable
    {
        private readonly PostgreSqlContainer _container;

        internal IdentityTestRuntime(
            PostgreSqlContainer container,
            NpgsqlDataSource administrator,
            ServiceProvider services,
            IdentityActor actor,
            EntityId activeUserId,
            VersionedPasswordHasher passwordHasher)
        {
            _container = container;
            Administrator = administrator;
            Services = services;
            Actor = actor;
            ActiveUserId = activeUserId;
            PasswordHasher = passwordHasher;
        }

        internal NpgsqlDataSource Administrator { get; }

        internal ServiceProvider Services { get; }

        internal IdentityActor Actor { get; }

        internal EntityId ActiveUserId { get; }

        internal VersionedPasswordHasher PasswordHasher { get; }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync().ConfigureAwait(false);
            await Administrator.DisposeAsync().ConfigureAwait(false);
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }
}
