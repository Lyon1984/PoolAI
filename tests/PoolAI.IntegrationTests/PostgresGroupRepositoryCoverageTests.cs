#pragma warning disable MA0051 // The contract scenarios keep their complete PostgreSQL transaction flow visible.
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.GroupQuota;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application;
using PoolAI.Modules.GroupQuota.Application.Ports;
using PoolAI.Modules.GroupQuota.Domain;
using PoolAI.Modules.GroupQuota.Infrastructure.Persistence;
using PoolAI.Modules.Operations;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class PostgresGroupRepositoryCoverageTests(PostgresRuntimeFixture fixture)
{
    private const string AdminRoleId = "01900000-0000-7000-8000-000000000001";
    private const string RequestHashPepper =
        "ZmFrZS1pZGVtcG90ZW5jeS1wZXBwZXItMzItYnl0ZXM=";

    [Fact]
    public async Task CrudConflictsAndKeysetQueriesUseTheCanonicalPostgresRepository()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        EntityId actorId = await SeedAdminActorAsync("group-crud", cancellationToken)
            .ConfigureAwait(true);
        GroupActor actor = new(actorId, GroupControlRole.Admin, TokenVersion: 1);
        await using ServiceProvider services = BuildGroupServices();
        ICreateGroupUseCase create = services.GetRequiredService<ICreateGroupUseCase>();
        IUpdateGroupUseCase update = services.GetRequiredService<IUpdateGroupUseCase>();
        IGetGroupUseCase get = services.GetRequiredService<IGetGroupUseCase>();
        IListGroupsUseCase list = services.GetRequiredService<IListGroupsUseCase>();
        IGroupStatusReader statusReader = services.GetRequiredService<IGroupStatusReader>();

        string suffix = Guid.NewGuid().ToString("N")[..12];
        string firstName = $"Repository first {suffix}";
        string secondName = $"Repository second {suffix}";
        GroupCommandOutcome first = await CreateGroupAsync(
            create,
            actor,
            firstName,
            "PostgreSQL repository coverage",
            totalTokens: 12_345,
            cancellationToken).ConfigureAwait(true);
        GroupCommandOutcome second = await CreateGroupAsync(
            create,
            actor,
            secondName,
            description: null,
            totalTokens: 54_321,
            cancellationToken).ConfigureAwait(true);

        string duplicateKey = Key("create-duplicate");
        Result<GroupCommandOutcome> duplicate = await create.ExecuteAsync(
            new CreateGroupCommand(
                EntityId.New(),
                actor,
                duplicateKey,
                firstName,
                "must not create a second Group",
                100,
                "192.0.2.31",
                "group-repository-coverage"),
            cancellationToken).ConfigureAwait(true);
        AssertFailure(duplicate, GroupErrorCodes.ResourceConflict, 409);
        Assert.Equal(
            1L,
            await CountGroupsByNameAsync(firstName, cancellationToken).ConfigureAwait(true));

        Result<GroupCommandOutcome> noOp = await update.ExecuteAsync(
            Update(
                actor,
                second.Value.Id,
                expectedVersion: 1,
                key: Key("noop"),
                hasName: true,
                name: secondName),
            cancellationToken).ConfigureAwait(true);
        Assert.True(noOp.IsSuccess, noOp.Error.Description);
        Assert.Equal(1, noOp.Value.Value.Version);
        Assert.Equal(secondName, noOp.Value.Value.Name);

        Result<GroupCommandOutcome> setDescription = await update.ExecuteAsync(
            Update(
                actor,
                second.Value.Id,
                expectedVersion: 1,
                key: Key("description-set"),
                hasDescription: true,
                description: "temporary description"),
            cancellationToken).ConfigureAwait(true);
        Assert.True(setDescription.IsSuccess, setDescription.Error.Description);
        Assert.Equal(2, setDescription.Value.Value.Version);
        Assert.Equal("temporary description", setDescription.Value.Value.Description);

        Result<GroupCommandOutcome> clearDescription = await update.ExecuteAsync(
            Update(
                actor,
                second.Value.Id,
                expectedVersion: 2,
                key: Key("description-clear"),
                hasDescription: true,
                description: null),
            cancellationToken).ConfigureAwait(true);
        Assert.True(clearDescription.IsSuccess, clearDescription.Error.Description);
        Assert.Equal(3, clearDescription.Value.Value.Version);
        Assert.Null(clearDescription.Value.Value.Description);

        Result<GroupCommandOutcome> stale = await update.ExecuteAsync(
            Update(
                actor,
                second.Value.Id,
                expectedVersion: 1,
                key: Key("stale"),
                hasName: true,
                name: $"Stale {suffix}"),
            cancellationToken).ConfigureAwait(true);
        AssertFailure(stale, GroupErrorCodes.VersionConflict, 412);
        Assert.Equal("\"v3\"", stale.Error.ETag);

        Result<GroupCommandOutcome> missingUpdate = await update.ExecuteAsync(
            Update(
                actor,
                EntityId.New(),
                expectedVersion: 1,
                key: Key("missing-update"),
                hasDescription: true,
                description: null),
            cancellationToken).ConfigureAwait(true);
        AssertFailure(missingUpdate, GroupErrorCodes.ResourceNotFound, 404);

        string conflictKey = Key("rename-conflict");
        Result<GroupCommandOutcome> renameConflict = await update.ExecuteAsync(
            Update(
                actor,
                second.Value.Id,
                expectedVersion: 3,
                key: conflictKey,
                hasName: true,
                name: firstName),
            cancellationToken).ConfigureAwait(true);
        AssertFailure(renameConflict, GroupErrorCodes.ResourceConflict, 409);
        Assert.Equal(
            "failed:409",
            await ReadIdempotencyTerminalAsync(conflictKey, cancellationToken)
                .ConfigureAwait(true));

        Result<GroupView> afterConflict = await get.ExecuteAsync(
            new GetGroupQuery(actor, second.Value.Id),
            cancellationToken).ConfigureAwait(true);
        Assert.True(afterConflict.IsSuccess, afterConflict.Error.Description);
        Assert.Equal(secondName, afterConflict.Value.Name);
        Assert.Equal(3, afterConflict.Value.Version);

        Result<GroupCommandOutcome> renamed = await update.ExecuteAsync(
            Update(
                actor,
                second.Value.Id,
                expectedVersion: 3,
                key: Key("rename-success"),
                hasName: true,
                name: $"Repository renamed {suffix}"),
            cancellationToken).ConfigureAwait(true);
        Assert.True(renamed.IsSuccess, renamed.Error.Description);
        Assert.Equal(4, renamed.Value.Value.Version);

        Result<GroupView> existing = await get.ExecuteAsync(
            new GetGroupQuery(actor, first.Value.Id),
            cancellationToken).ConfigureAwait(true);
        Assert.True(existing.IsSuccess, existing.Error.Description);
        Assert.Equal(firstName, existing.Value.Name);
        Assert.Equal("PostgreSQL repository coverage", existing.Value.Description);

        Result<GroupView> missing = await get.ExecuteAsync(
            new GetGroupQuery(actor, EntityId.New()),
            cancellationToken).ConfigureAwait(true);
        AssertFailure(missing, GroupErrorCodes.ResourceNotFound);

        EntityId groupWithoutQuota = await InsertGroupWithoutQuotaAsync(
            $"Repository no quota {suffix}",
            cancellationToken).ConfigureAwait(true);
        Result<GroupSnapshot> noQuota = await statusReader.GetAsync(
            groupWithoutQuota,
            cancellationToken).ConfigureAwait(true);
        Assert.True(noQuota.IsSuccess, noQuota.Error.Description);
        Assert.False(noQuota.Value.HasCurrentQuotaPeriod);
        Assert.Equal(GroupLifecycle.Disabled, noQuota.Value.Lifecycle);

        HashSet<Guid> seen = [];
        string? cursor = null;
        bool sawContinuation = false;
        for (int pageNumber = 0; pageNumber < 100; pageNumber++)
        {
            Result<GroupPage> pageResult = await list.ExecuteAsync(
                new ListGroupsQuery(actor, cursor, Limit: 2),
                cancellationToken).ConfigureAwait(true);
            Assert.True(pageResult.IsSuccess, pageResult.Error.Description);
            GroupPage page = pageResult.Value;
            foreach (GroupView group in page.Data)
            {
                Assert.True(seen.Add(group.Id.Value), "Keyset pagination returned a duplicate Group.");
            }

            if (!page.HasMore)
            {
                Assert.Null(page.NextCursor);
                break;
            }

            sawContinuation = true;
            cursor = Assert.IsType<string>(page.NextCursor);
            Assert.Equal(2, page.Data.Count);
        }

        Assert.True(sawContinuation);
        Assert.Contains(first.Value.Id.Value, seen);
        Assert.Contains(second.Value.Id.Value, seen);
        Assert.Contains(groupWithoutQuota.Value, seen);
    }

    [Fact]
    public async Task ActivationArchiveAndPoolSummaryUseCanonicalQuotaAndLifecycleState()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        EntityId actorId = await SeedAdminActorAsync("group-lifecycle", cancellationToken)
            .ConfigureAwait(true);
        GroupActor actor = new(actorId, GroupControlRole.Admin, TokenVersion: 1);
        ActorContext activationActor = new(actorId, TokenVersion: 1);
        await using ServiceProvider services = BuildGroupServices();
        ICreateGroupUseCase create = services.GetRequiredService<ICreateGroupUseCase>();
        IUpdateGroupUseCase update = services.GetRequiredService<IUpdateGroupUseCase>();
        IGetGroupUseCase get = services.GetRequiredService<IGetGroupUseCase>();
        IGroupActivationCommand activation = services.GetRequiredService<IGroupActivationCommand>();
        IGroupPoolSummaryReader summaries = services.GetRequiredService<IGroupPoolSummaryReader>();

        string suffix = Guid.NewGuid().ToString("N")[..12];
        GroupCommandOutcome created = await CreateGroupAsync(
            create,
            actor,
            $"Lifecycle {suffix}",
            "activation and archive coverage",
            totalTokens: 1_000,
            cancellationToken).ConfigureAwait(true);
        DateTimeOffset observedAt = await ReadDatabaseClockAsync(cancellationToken)
            .ConfigureAwait(true);
        SupplyReadinessEvidence evidence = new("v1.RepositoryCoverage", observedAt);

        Result<GroupActivationResult> notReady = await activation.ActivateAsync(
            new ActivateGroupCommand(
                activationActor,
                created.Value.Id,
                ExpectedVersion: 1,
                IdempotencyKey: Key("activation-not-ready"),
                Reason: "prove the database activation guard",
                SupplyEvidence: evidence,
                RequestId: EntityId.New(),
                IpAddress: "192.0.2.32",
                UserAgent: "group-repository-coverage"),
            cancellationToken).ConfigureAwait(true);
        AssertFailure(notReady, GroupErrorCodes.GroupActivationNotReady, 409);
        Result<GroupView> unchanged = await get.ExecuteAsync(
            new GetGroupQuery(actor, created.Value.Id),
            cancellationToken).ConfigureAwait(true);
        Assert.True(unchanged.IsSuccess, unchanged.Error.Description);
        Assert.Equal(GroupLifecycle.Disabled, unchanged.Value.Status);
        Assert.Equal(1, unchanged.Value.Version);

        await SeedReadySupplyAsync(created.Value.Id, suffix, cancellationToken)
            .ConfigureAwait(true);
        Result<GroupActivationResult> activated = await activation.ActivateAsync(
            new ActivateGroupCommand(
                activationActor,
                created.Value.Id,
                ExpectedVersion: 1,
                IdempotencyKey: Key("activation-success"),
                Reason: "supply is ready",
                SupplyEvidence: evidence,
                RequestId: EntityId.New(),
                IpAddress: "192.0.2.32",
                UserAgent: "group-repository-coverage"),
            cancellationToken).ConfigureAwait(true);
        Assert.True(activated.IsSuccess, activated.Error.Description);
        Assert.Equal(GroupLifecycle.Active, activated.Value.Lifecycle);
        Assert.Equal(2, activated.Value.Version);
        Assert.NotNull(activated.Value.Resource);

        EntityId[] duplicatedIds =
        [
            created.Value.Id,
            created.Value.Id,
            EntityId.New(),
        ];
        Result<IReadOnlyList<GroupPoolSummarySnapshot>> activeSummaries =
            await summaries.GetByGroupIdsAsync(duplicatedIds, cancellationToken)
                .ConfigureAwait(true);
        Assert.True(activeSummaries.IsSuccess, activeSummaries.Error.Description);
        GroupPoolSummarySnapshot active = Assert.Single(activeSummaries.Value);
        Assert.Equal(created.Value.Id, active.GroupId);
        Assert.Equal(GroupLifecycle.Active, active.Lifecycle);
        Assert.Equal((BigInteger)1_000, active.TotalTokens);
        Assert.Equal(BigInteger.Zero, active.ConsumedTokens);
        Assert.Equal(BigInteger.Zero, active.ReservedTokens);
        Assert.Equal(GroupPoolQuotaStatus.Active, active.QuotaStatus);

        await SetCurrentPeriodConsumedAsync(
            created.Value.Id,
            consumedTokens: 1_000,
            cancellationToken).ConfigureAwait(true);
        Result<IReadOnlyList<GroupPoolSummarySnapshot>> exhaustedSummaries =
            await summaries.GetByGroupIdsAsync(
                [created.Value.Id],
                cancellationToken).ConfigureAwait(true);
        Assert.True(exhaustedSummaries.IsSuccess, exhaustedSummaries.Error.Description);
        GroupPoolSummarySnapshot exhausted = Assert.Single(exhaustedSummaries.Value);
        Assert.Equal(GroupPoolQuotaStatus.Exhausted, exhausted.QuotaStatus);
        Assert.Equal((BigInteger)1_000, exhausted.ConsumedTokens);

        Result<GroupCommandOutcome> disabled = await update.ExecuteAsync(
            Update(
                actor,
                created.Value.Id,
                expectedVersion: 2,
                key: Key("disable"),
                hasStatus: true,
                status: GroupLifecycle.Disabled,
                reason: "prepare archive"),
            cancellationToken).ConfigureAwait(true);
        Assert.True(disabled.IsSuccess, disabled.Error.Description);
        Assert.Equal(GroupLifecycle.Disabled, disabled.Value.Value.Status);
        Assert.Equal(3, disabled.Value.Value.Version);

        Result<IReadOnlyList<GroupPoolSummarySnapshot>> disabledSummaries =
            await summaries.GetByGroupIdsAsync(
                [created.Value.Id],
                cancellationToken).ConfigureAwait(true);
        Assert.True(disabledSummaries.IsSuccess, disabledSummaries.Error.Description);
        GroupPoolSummarySnapshot disabledSummary = Assert.Single(disabledSummaries.Value);
        Assert.Equal(GroupLifecycle.Disabled, disabledSummary.Lifecycle);
        Assert.Equal(GroupPoolQuotaStatus.Disabled, disabledSummary.QuotaStatus);

        await SeedArchiveBlockingSubscriptionAsync(
            created.Value.Id,
            actorId,
            suffix,
            cancellationToken).ConfigureAwait(true);
        string blockedKey = Key("archive-blocked");
        Result<GroupCommandOutcome> blocked = await update.ExecuteAsync(
            Update(
                actor,
                created.Value.Id,
                expectedVersion: 3,
                key: blockedKey,
                hasStatus: true,
                status: GroupLifecycle.Archived,
                reason: "archive with active access"),
            cancellationToken).ConfigureAwait(true);
        AssertFailure(blocked, GroupErrorCodes.ResourceConflict, 409);
        Assert.Equal(
            "failed:409",
            await ReadIdempotencyTerminalAsync(blockedKey, cancellationToken)
                .ConfigureAwait(true));

        await RevokeArchiveBlockingSubscriptionAsync(suffix, cancellationToken)
            .ConfigureAwait(true);
        Result<GroupCommandOutcome> archived = await update.ExecuteAsync(
            Update(
                actor,
                created.Value.Id,
                expectedVersion: 3,
                key: Key("archive-success"),
                hasStatus: true,
                status: GroupLifecycle.Archived,
                reason: "access has been revoked"),
            cancellationToken).ConfigureAwait(true);
        Assert.True(archived.IsSuccess, archived.Error.Description);
        Assert.Equal(GroupLifecycle.Archived, archived.Value.Value.Status);
        Assert.Equal(4, archived.Value.Value.Version);

        Result<GroupCommandOutcome> archivedMutation = await update.ExecuteAsync(
            Update(
                actor,
                created.Value.Id,
                expectedVersion: 4,
                key: Key("archived-mutation"),
                hasName: true,
                name: $"Archived rename {suffix}"),
            cancellationToken).ConfigureAwait(true);
        AssertFailure(archivedMutation, GroupErrorCodes.ResourceConflict, 409);

        Result<IReadOnlyList<GroupPoolSummarySnapshot>> archivedSummaries =
            await summaries.GetByGroupIdsAsync(
                [created.Value.Id],
                cancellationToken).ConfigureAwait(true);
        Assert.True(archivedSummaries.IsSuccess, archivedSummaries.Error.Description);
        GroupPoolSummarySnapshot archivedSummary = Assert.Single(archivedSummaries.Value);
        Assert.Equal(GroupLifecycle.Archived, archivedSummary.Lifecycle);
        Assert.Equal(GroupPoolQuotaStatus.Disabled, archivedSummary.QuotaStatus);

        Result<IReadOnlyList<GroupPoolSummarySnapshot>> empty =
            await summaries.GetByGroupIdsAsync(
                Array.Empty<EntityId>(),
                cancellationToken).ConfigureAwait(true);
        Assert.True(empty.IsSuccess, empty.Error.Description);
        Assert.Empty(empty.Value);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task FunctionFailuresRollbackSavepointsAndContractDriftFailsClosed()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        EntityId actorId = await SeedAdminActorAsync("group-function-failures", cancellationToken)
            .ConfigureAwait(true);
        await using ServiceProvider services = BuildGroupServices();
        NpgsqlDataSource dataSource = services.GetRequiredService<NpgsqlDataSource>();
        PostgresGroupRepository repository = new(dataSource);
        IUnitOfWorkFactory unitOfWorkFactory = services
            .GetRequiredService<IUnitOfWorkFactory>();
        string suffix = Guid.NewGuid().ToString("N")[..12];
        EntityId validGroupId = EntityId.New();
        EntityId validationGroupId = EntityId.New();
        EntityId businessErrorGroupId = EntityId.New();
        EntityId duplicateEventGroupId = EntityId.New();
        EntityId sharedQuotaEventId = EntityId.New();

        CreateGroupWrite CreateWrite(
            EntityId groupId,
            string name,
            long totalTokens,
            EntityId? quotaEventId = null) => new(
                groupId,
                EntityId.New(),
                quotaEventId ?? EntityId.New(),
                EntityId.New(),
                name,
                "direct PostgreSQL function coverage",
                totalTokens,
                actorId,
                Key("direct-create"),
                "exercise the Group create database contract");

        IUnitOfWork unitOfWork = await unitOfWorkFactory
            .BeginAsync(cancellationToken).ConfigureAwait(true);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);

        GroupWriteResult validation = await repository.CreateAsync(
            CreateWrite(validationGroupId, " ", totalTokens: 100),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(GroupWriteDisposition.ValidationFailed, validation.Disposition);
        Assert.Null(validation.Group);

        GroupWriteResult created = await repository.CreateAsync(
            CreateWrite(
                validGroupId,
                $"Direct repository valid {suffix}",
                totalTokens: 100,
                sharedQuotaEventId),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(GroupWriteDisposition.Written, created.Disposition);
        Assert.Equal(validGroupId, Assert.IsType<GroupResource>(created.Group).Id);

        GroupWriteResult businessError = await repository.CreateAsync(
            CreateWrite(
                businessErrorGroupId,
                $"Direct repository invalid quota {suffix}",
                totalTokens: 0),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(GroupWriteDisposition.LifecycleConflict, businessError.Disposition);
        Assert.Null(businessError.Group);

        GroupWriteResult duplicateEvent = await repository.CreateAsync(
            CreateWrite(
                duplicateEventGroupId,
                $"Direct repository duplicate event {suffix}",
                totalTokens: 100,
                sharedQuotaEventId),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(GroupWriteDisposition.LifecycleConflict, duplicateEvent.Disposition);
        Assert.Null(duplicateEvent.Group);

        GroupWriteResult invalidUpdate = await repository.UpdateAsync(
            new UpdateGroupWrite(
                EntityId.New(),
                ExpectedVersion: 0,
                HasName: false,
                Name: null,
                HasDescription: false,
                Description: null,
                Lifecycle: null,
                Reason: null,
                SupplyEvidence: null),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(GroupWriteDisposition.ValidationFailed, invalidUpdate.Disposition);

        GroupWriteResult invalidActivation = await repository.UpdateAsync(
            new UpdateGroupWrite(
                EntityId.New(),
                ExpectedVersion: 0,
                HasName: false,
                Name: null,
                HasDescription: false,
                Description: null,
                Lifecycle: null,
                Reason: null,
                new SupplyReadinessEvidence(
                    "v1.invalid",
                    TimeProvider.System.GetUtcNow())),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(GroupWriteDisposition.ActivationNotReady, invalidActivation.Disposition);

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(true);
        Assert.True(await GroupExistsAsync(validGroupId, cancellationToken).ConfigureAwait(true));
        Assert.False(await GroupExistsAsync(validationGroupId, cancellationToken)
            .ConfigureAwait(true));
        Assert.False(await GroupExistsAsync(businessErrorGroupId, cancellationToken)
            .ConfigureAwait(true));
        Assert.False(await GroupExistsAsync(duplicateEventGroupId, cancellationToken)
            .ConfigureAwait(true));

        Assert.Equal(
            GroupWriteDisposition.NameConflict,
            PostgresGroupRepository.MapCreateDisposition("conflict"));
        Assert.Equal(
            GroupWriteDisposition.ValidationFailed,
            PostgresGroupRepository.MapCreateDisposition("validation_failed"));
        Assert.Throws<InvalidOperationException>(
            static () => PostgresGroupRepository.MapCreateDisposition("contract_drift"));

        Assert.IsType<InvalidOperationException>(InvokePrivateStaticFailure(
            typeof(PostgresGroupRepository),
            "MapDisposition",
            "contract_drift",
            false));
        Assert.IsType<InvalidOperationException>(InvokePrivateStaticFailure(
            typeof(PostgresGroupRepository),
            "ParseLifecycle",
            "contract_drift"));
        Assert.IsType<ArgumentOutOfRangeException>(InvokePrivateStaticFailure(
            typeof(PostgresGroupRepository),
            "LifecycleCode",
            (GroupLifecycle)int.MaxValue));
        Assert.IsType<InvalidOperationException>(InvokePrivateStaticFailure(
            typeof(PostgresGroupPoolSummaryReader),
            "ParseLifecycle",
            "contract_drift"));
        Assert.IsType<InvalidOperationException>(InvokePrivateStaticFailure(
            typeof(PostgresGroupPoolSummaryReader),
            "ParseQuotaStatus",
            "contract_drift"));

        NpgsqlConnection connection = await dataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(true);
        await using ConfiguredAsyncDisposable connectionLease =
            connection.ConfigureAwait(false);
        using NpgsqlCommand noResult = connection.CreateCommand();
        noResult.CommandText = """
            SELECT
                'updated'::text,
                false,
                NULL::text,
                NULL::bigint
            WHERE false;
            """;
        Task noResultTask = InvokePrivateValueTaskAsTask(
            typeof(PostgresGroupRepository),
            "ReadFunctionResultAsync",
            noResult,
            cancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => noResultTask)
            .ConfigureAwait(true);
    }

    private ServiceProvider BuildGroupServices()
    {
        string connectionString = fixture.ApiServices
            .GetRequiredService<IConfiguration>()["Data:Postgres:ConnectionString"]
            ?? throw new InvalidOperationException(
                "The PostgreSQL runtime fixture did not expose its API connection string.");
        ConfigurationManager configuration = new();
        configuration["Data:Postgres:ConnectionString"] = connectionString;
        configuration["Data:Redis:ConnectionString"] = fixture.RedisConnectionString;
        configuration["Data:Redis:KeyPrefix"] = "poolai:r1:group-repository-coverage:";
        configuration["Health:Ntp:Server"] = "127.0.0.1";
        configuration["Health:Ntp:Port"] = "123";
        configuration["Idempotency:RequestHashPepper"] = RequestHashPepper;

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddPoolAiPostgresRuntime(connectionString);
        services.AddOperationsModule(configuration, "Integration");
        services.AddGroupQuotaModule();
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private async ValueTask<EntityId> SeedAdminActorAsync(
        string prefix,
        CancellationToken cancellationToken)
    {
        EntityId actorId = EntityId.New();
        string email = $"{prefix}-{Guid.NewGuid():N}@example.test";
        using (NpgsqlCommand user = fixture.AdministratorDataSource.CreateCommand("""
                   INSERT INTO public.users (
                       id, email, normalized_email, display_name, password_hash,
                       security_stamp
                   ) VALUES ($1, $2, $2, $3, 'poolai-password-v1:test', $4);
                   """))
        {
            user.Parameters.AddWithValue(actorId.Value);
            user.Parameters.AddWithValue(email);
            user.Parameters.AddWithValue($"{prefix} actor");
            user.Parameters.AddWithValue(Guid.CreateVersion7());
            Assert.Equal(
                1,
                await user.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true));
        }

        using NpgsqlCommand role = fixture.AdministratorDataSource.CreateCommand("""
            INSERT INTO public.user_roles (user_id, role_id, assigned_by)
            VALUES ($1, $2, $1);
            """);
        role.Parameters.AddWithValue(actorId.Value);
        role.Parameters.AddWithValue(Guid.Parse(AdminRoleId));
        Assert.Equal(
            1,
            await role.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true));
        return actorId;
    }

    private static async ValueTask<GroupCommandOutcome> CreateGroupAsync(
        ICreateGroupUseCase create,
        GroupActor actor,
        string name,
        string? description,
        long totalTokens,
        CancellationToken cancellationToken)
    {
        Result<GroupCommandOutcome> result = await create.ExecuteAsync(
            new CreateGroupCommand(
                EntityId.New(),
                actor,
                Key("create"),
                name,
                description,
                totalTokens,
                "192.0.2.30",
                "group-repository-coverage"),
            cancellationToken).ConfigureAwait(true);
        Assert.True(result.IsSuccess, result.Error.Description);
        Assert.Equal(201, result.Value.StatusCode);
        Assert.Equal(GroupLifecycle.Disabled, result.Value.Value.Status);
        Assert.Equal(1, result.Value.Value.Version);
        return result.Value;
    }

    private static UpdateGroupCommand Update(
        GroupActor actor,
        EntityId groupId,
        long expectedVersion,
        string key,
        bool hasName = false,
        string? name = null,
        bool hasDescription = false,
        string? description = null,
        bool hasStatus = false,
        GroupLifecycle? status = null,
        string? reason = null) => new(
            EntityId.New(),
            actor,
            key,
            groupId,
            expectedVersion,
            hasName,
            name,
            hasDescription,
            description,
            hasStatus,
            status,
            reason,
            "192.0.2.30",
            "group-repository-coverage");

    private static string Key(string purpose) =>
        $"group-repository-{purpose}-{Guid.NewGuid():N}";

    private static void AssertFailure<T>(
        Result<T> result,
        string code,
        int? status = null)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(code, result.Error.Code);
        if (status is not null)
        {
            Assert.Equal(status.Value, result.Error.Presentation?.Status);
        }
    }

    private async ValueTask<long> CountGroupsByNameAsync(
        string name,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            SELECT count(*)
            FROM public.groups
            WHERE name = $1;
            """);
        command.Parameters.AddWithValue(name);
        return Assert.IsType<long>(await command
            .ExecuteScalarAsync(cancellationToken).ConfigureAwait(true));
    }

    private async ValueTask<bool> GroupExistsAsync(
        EntityId groupId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            SELECT EXISTS (
                SELECT 1
                FROM public.groups
                WHERE id = $1
            );
            """);
        command.Parameters.AddWithValue(groupId.Value);
        return Assert.IsType<bool>(await command
            .ExecuteScalarAsync(cancellationToken).ConfigureAwait(true));
    }

    private static Exception InvokePrivateStaticFailure(
        Type declaringType,
        string methodName,
        params object?[] arguments)
    {
        MethodInfo? method = declaringType.GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        TargetInvocationException invocation = Assert.Throws<TargetInvocationException>(
            () => method.Invoke(null, arguments));
        return Assert.IsAssignableFrom<Exception>(invocation.InnerException);
    }

    private static Task InvokePrivateValueTaskAsTask(
        Type declaringType,
        string methodName,
        params object?[] arguments)
    {
        MethodInfo? method = declaringType.GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        object? awaitable = method.Invoke(null, arguments);
        Assert.NotNull(awaitable);
        MethodInfo? asTask = awaitable.GetType().GetMethod(
            nameof(ValueTask.AsTask),
            BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(asTask);
        return Assert.IsAssignableFrom<Task>(asTask.Invoke(awaitable, null));
    }

    private async ValueTask<string> ReadIdempotencyTerminalAsync(
        string key,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            SELECT status || ':' || response_status::text
            FROM public.idempotency_records
            WHERE idempotency_key = $1;
            """);
        command.Parameters.AddWithValue(key);
        return Assert.IsType<string>(await command
            .ExecuteScalarAsync(cancellationToken).ConfigureAwait(true));
    }

    private async ValueTask<EntityId> InsertGroupWithoutQuotaAsync(
        string name,
        CancellationToken cancellationToken)
    {
        EntityId groupId = EntityId.New();
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            INSERT INTO public.groups (id, name, description, status)
            VALUES ($1, $2, NULL, 'disabled');
            """);
        command.Parameters.AddWithValue(groupId.Value);
        command.Parameters.AddWithValue(name);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true));
        return groupId;
    }

    private async ValueTask<DateTimeOffset> ReadDatabaseClockAsync(
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand(
            "SELECT clock_timestamp();");
        object? scalar = await command
            .ExecuteScalarAsync(cancellationToken).ConfigureAwait(true);
        Assert.NotNull(scalar);
        object value = scalar;
        return value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime => new DateTimeOffset(
                DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException(
                "PostgreSQL clock_timestamp() returned an unsupported value."),
        };
    }

    private async ValueTask SeedReadySupplyAsync(
        EntityId groupId,
        string suffix,
        CancellationToken cancellationToken)
    {
        EntityId channelId = EntityId.New();
        EntityId accountId = EntityId.New();
        NpgsqlConnection connection = await fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(true);
        await using ConfiguredAsyncDisposable connectionLease =
            connection.ConfigureAwait(false);
        NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(true);
        await using ConfiguredAsyncDisposable transactionLease =
            transaction.ConfigureAwait(false);
        using (NpgsqlCommand channel = connection.CreateCommand())
        {
            channel.Transaction = transaction;
            channel.CommandText = """
            INSERT INTO public.channels (
                id, provider, name, model_rules, status
            ) VALUES (
                $1, 'openai', $2, '{"gpt-test":"gpt-test"}'::jsonb, 'active'
            );
            """;
            channel.Parameters.AddWithValue(channelId.Value);
            channel.Parameters.AddWithValue($"Coverage channel {suffix}");
            Assert.Equal(
                1,
                await channel.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true));
        }

        using (NpgsqlCommand account = connection.CreateCommand())
        {
            account.Transaction = transaction;
            account.CommandText = """
            INSERT INTO public.accounts (
                id, provider, name, auth_type, upstream_base_url,
                credential_envelope, credential_prefix,
                status, last_health_at, last_health_status
            ) VALUES (
                $1, 'openai', $2, 'api_key', 'https://example.test/v1',
                '{}'::jsonb, 'sk-coverage',
                'active', clock_timestamp(), 'healthy'
            );
            """;
            account.Parameters.AddWithValue(accountId.Value);
            account.Parameters.AddWithValue($"Coverage account {suffix}");
            Assert.Equal(
                1,
                await account.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true));
        }

        using (NpgsqlCommand configuration = connection.CreateCommand())
        {
            configuration.Transaction = transaction;
            configuration.CommandText = """
                INSERT INTO public.group_supply_configurations (group_id, channel_id)
                VALUES ($1, $2);
                """;
            configuration.Parameters.AddWithValue(groupId.Value);
            configuration.Parameters.AddWithValue(channelId.Value);
            Assert.Equal(
                1,
                await configuration.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(true));
        }

        using (NpgsqlCommand binding = connection.CreateCommand())
        {
            binding.Transaction = transaction;
            binding.CommandText = """
                INSERT INTO public.group_accounts (group_id, account_id, is_enabled)
                VALUES ($1, $2, true);
                """;
            binding.Parameters.AddWithValue(groupId.Value);
            binding.Parameters.AddWithValue(accountId.Value);
            Assert.Equal(
                1,
                await binding.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(true);
    }

    private async ValueTask SetCurrentPeriodConsumedAsync(
        EntityId groupId,
        long consumedTokens,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            UPDATE public.group_quota_periods AS period
            SET consumed_tokens = $2,
                updated_at = clock_timestamp()
            FROM public.group_token_quotas AS quota
            WHERE quota.group_id = $1
              AND period.id = quota.current_period_id
              AND period.group_id = quota.group_id;
            """);
        command.Parameters.AddWithValue(groupId.Value);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Numeric,
            Value = (BigInteger)consumedTokens,
        });
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true));
    }

    private async ValueTask SeedArchiveBlockingSubscriptionAsync(
        EntityId groupId,
        EntityId actorId,
        string suffix,
        CancellationToken cancellationToken)
    {
        EntityId templateId = EntityId.New();
        EntityId subscriptionId = EntityId.New();
        EntityId targetUserId = EntityId.New();
        string email = $"archive-target-{Guid.NewGuid():N}@example.test";
        NpgsqlConnection connection = await fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(true);
        await using ConfiguredAsyncDisposable connectionLease =
            connection.ConfigureAwait(false);
        NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(true);
        await using ConfiguredAsyncDisposable transactionLease =
            transaction.ConfigureAwait(false);
        using (NpgsqlCommand user = connection.CreateCommand())
        {
            user.Transaction = transaction;
            user.CommandText = """
            INSERT INTO public.users (
                id, email, normalized_email, display_name, password_hash,
                security_stamp
            ) VALUES (
                $1, $2, $2, 'Archive blocker', 'poolai-password-v1:test', $3
            );
            """;
            user.Parameters.AddWithValue(targetUserId.Value);
            user.Parameters.AddWithValue(email);
            user.Parameters.AddWithValue(Guid.CreateVersion7());
            Assert.Equal(
                1,
                await user.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true));
        }

        using (NpgsqlCommand template = connection.CreateCommand())
        {
            template.Transaction = transaction;
            template.CommandText = """
            INSERT INTO public.subscription_templates (
                id, group_id, name, description, default_duration_days, status
            ) VALUES ($1, $2, $3, 'archive fence coverage', 30, 'active');
            """;
            template.Parameters.AddWithValue(templateId.Value);
            template.Parameters.AddWithValue(groupId.Value);
            template.Parameters.AddWithValue($"Archive template {suffix}");
            Assert.Equal(
                1,
                await template.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true));
        }

        using (NpgsqlCommand subscription = connection.CreateCommand())
        {
            subscription.Transaction = transaction;
            subscription.CommandText = """
            INSERT INTO public.subscriptions (
                id, user_id, group_id, template_id, template_name_snapshot,
                starts_at, expires_at, status, assigned_by, change_reason
            ) VALUES (
                $1, $2, $3, $4, $5,
                clock_timestamp() - interval '1 hour',
                clock_timestamp() + interval '1 day',
                'active', $6, 'archive fence coverage'
            );
            """;
            subscription.Parameters.AddWithValue(subscriptionId.Value);
            subscription.Parameters.AddWithValue(targetUserId.Value);
            subscription.Parameters.AddWithValue(groupId.Value);
            subscription.Parameters.AddWithValue(templateId.Value);
            subscription.Parameters.AddWithValue($"Archive template {suffix}");
            subscription.Parameters.AddWithValue(actorId.Value);
            Assert.Equal(
                1,
                await subscription.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(true));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(true);
    }

    private async ValueTask RevokeArchiveBlockingSubscriptionAsync(
        string suffix,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            UPDATE public.subscriptions AS subscription
            SET status = 'revoked',
                version = subscription.version + 1,
                updated_at = clock_timestamp(),
                change_reason = 'archive fence released'
            FROM public.subscription_templates AS template
            WHERE template.id = subscription.template_id
              AND template.name = $1
              AND subscription.status = 'active';
            """);
        command.Parameters.AddWithValue($"Archive template {suffix}");
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true));
    }
}
#pragma warning restore MA0051
