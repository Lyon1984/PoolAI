#pragma warning disable MA0051 // PostgreSQL contract scenarios keep the complete lifecycle visible.
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Infrastructure.Persistence;
using PoolAI.Modules.Operations;
using PoolAI.Modules.SubscriptionAccess;
using PoolAI.Modules.SubscriptionAccess.Abstractions;
using PoolAI.Modules.SubscriptionAccess.Application;
using PoolAI.Modules.SubscriptionAccess.Application.Ports;
using PoolAI.Modules.SubscriptionAccess.Domain;
using PoolAI.Modules.SubscriptionAccess.Infrastructure.Persistence;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class PostgresSubscriptionRepositoryCoverageTests(PostgresRuntimeFixture fixture)
{
    private const string AdminRoleId = "01900000-0000-7000-8000-000000000001";
    private const string UserRoleId = "01900000-0000-7000-8000-000000000004";
    private const string RequestHashPepper =
        "ZmFrZS1pZGVtcG90ZW5jeS1wZXBwZXItMzItYnl0ZXM=";

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task AssignmentAndLifecycleMutationsPersistCanonicalAuditChainAtomically()
    {
        // Governing acceptance: DEC-008 and AC-008 require the production command
        // path to keep one canonical user+Group row through assign, extend,
        // suspend, resume and revoke while every command atomically advances its
        // version and appends durable idempotency, audit and outbox facts.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        EntityId actorId = await SeedUserAsync("ac008-actor", cancellationToken)
            .ConfigureAwait(true);
        await AssignRoleAsync(actorId, AdminRoleId, actorId, cancellationToken)
            .ConfigureAwait(true);
        EntityId userId = await SeedUserAsync("ac008-user", cancellationToken)
            .ConfigureAwait(true);
        await AssignRoleAsync(userId, UserRoleId, actorId, cancellationToken)
            .ConfigureAwait(true);
        EntityId groupId = await SeedGroupAsync("active", actorId, cancellationToken)
            .ConfigureAwait(true);

        await using ServiceProvider services = BuildSubscriptionServices();
        ICreateSubscriptionTemplateUseCase createTemplate = services
            .GetRequiredService<ICreateSubscriptionTemplateUseCase>();
        IAssignSubscriptionUseCase assignSubscription = services
            .GetRequiredService<IAssignSubscriptionUseCase>();
        IUpdateSubscriptionUseCase updateSubscription = services
            .GetRequiredService<IUpdateSubscriptionUseCase>();
        SubscriptionActor actor = new(actorId, SystemRole.Admin, TokenVersion: 2);
        string suffix = Guid.NewGuid().ToString("N");

        Result<SubscriptionCommandOutcome<SubscriptionTemplateView>> templateResult =
            await createTemplate.ExecuteAsync(
                new CreateSubscriptionTemplateCommand(
                    EntityId.New(),
                    actor,
                    $"ac008-template-{suffix}",
                    groupId,
                    $"AC-008 Template {suffix}",
                    "Canonical Subscription acceptance fixture",
                    30,
                    "192.0.2.80",
                    "ac008-integration"),
                cancellationToken).ConfigureAwait(true);
        Assert.True(templateResult.IsSuccess, templateResult.Error.Description);
        EntityId templateId = templateResult.Value.Value.Id;

        string assignKey = $"ac008-assign-{suffix}";
        string extendKey = $"ac008-extend-{suffix}";
        string suspendKey = $"ac008-suspend-{suffix}";
        string resumeKey = $"ac008-resume-{suffix}";
        string revokeKey = $"ac008-revoke-{suffix}";
        EntityId assignRequestId = EntityId.New();
        EntityId extendRequestId = EntityId.New();
        EntityId suspendRequestId = EntityId.New();
        EntityId resumeRequestId = EntityId.New();
        EntityId revokeRequestId = EntityId.New();
        DateTimeOffset startsAt = TimeProvider.System.GetUtcNow().AddMinutes(-1);
        DateTimeOffset expiresAt = startsAt.AddDays(30);
        Result<SubscriptionCommandOutcome<SubscriptionView>> assigned =
            await assignSubscription.ExecuteAsync(
                new AssignSubscriptionCommand(
                    assignRequestId,
                    actor,
                    assignKey,
                    userId,
                    templateId,
                    startsAt,
                    expiresAt,
                    "initial canonical assignment",
                    "192.0.2.80",
                    "ac008-integration"),
                cancellationToken).ConfigureAwait(true);
        Assert.True(assigned.IsSuccess, assigned.Error.Description);
        Assert.False(assigned.Value.IsReplay);
        Assert.Equal(1, assigned.Value.Value.Version);
        Assert.Equal("\"v1\"", assigned.Value.ETag);

        DateTimeOffset extendedExpiry = assigned.Value.Value.ExpiresAt.AddDays(7);
        Result<SubscriptionCommandOutcome<SubscriptionView>> extended =
            await updateSubscription.ExecuteAsync(
                new UpdateSubscriptionCommand(
                    extendRequestId,
                    actor,
                    extendKey,
                    assigned.Value.Value.Id,
                    ExpectedVersion: 1,
                    StartsAtSpecified: false,
                    StartsAt: null,
                    ExpiresAtSpecified: true,
                    ExpiresAt: extendedExpiry,
                    StatusSpecified: false,
                    Status: null,
                    Reason: "extend canonical assignment",
                    IpAddress: "192.0.2.80",
                    UserAgent: "ac008-integration"),
                cancellationToken).ConfigureAwait(true);
        Assert.True(extended.IsSuccess, extended.Error.Description);
        Assert.False(extended.Value.IsReplay);
        Assert.Equal(assigned.Value.Value.Id, extended.Value.Value.Id);
        Assert.Equal(2, extended.Value.Value.Version);
        Assert.Equal("\"v2\"", extended.Value.ETag);
        Assert.Equal(extendedExpiry, extended.Value.Value.ExpiresAt);

        Result<SubscriptionCommandOutcome<SubscriptionView>> suspended =
            await updateSubscription.ExecuteAsync(
                new UpdateSubscriptionCommand(
                    suspendRequestId,
                    actor,
                    suspendKey,
                    assigned.Value.Value.Id,
                    ExpectedVersion: 2,
                    StartsAtSpecified: false,
                    StartsAt: null,
                    ExpiresAtSpecified: false,
                    ExpiresAt: null,
                    StatusSpecified: true,
                    Status: SubscriptionLifecycle.Suspended,
                    Reason: "suspend canonical assignment",
                    IpAddress: "192.0.2.80",
                    UserAgent: "ac008-integration"),
                cancellationToken).ConfigureAwait(true);
        Assert.True(suspended.IsSuccess, suspended.Error.Description);
        Assert.Equal(3, suspended.Value.Value.Version);
        Assert.Equal(
            SubscriptionEffectiveLifecycle.Suspended,
            suspended.Value.Value.EffectiveStatus);

        Result<SubscriptionCommandOutcome<SubscriptionView>> resumed =
            await updateSubscription.ExecuteAsync(
                new UpdateSubscriptionCommand(
                    resumeRequestId,
                    actor,
                    resumeKey,
                    assigned.Value.Value.Id,
                    ExpectedVersion: 3,
                    StartsAtSpecified: false,
                    StartsAt: null,
                    ExpiresAtSpecified: false,
                    ExpiresAt: null,
                    StatusSpecified: true,
                    Status: SubscriptionLifecycle.Active,
                    Reason: "resume canonical assignment",
                    IpAddress: "192.0.2.80",
                    UserAgent: "ac008-integration"),
                cancellationToken).ConfigureAwait(true);
        Assert.True(resumed.IsSuccess, resumed.Error.Description);
        Assert.Equal(4, resumed.Value.Value.Version);
        Assert.Equal(
            SubscriptionEffectiveLifecycle.Active,
            resumed.Value.Value.EffectiveStatus);

        EntityId rolledBackRequestId = EntityId.New();
        string rolledBackKey = $"ac008-before-commit-{suffix}";
        FailBeforeCommitUnitOfWorkFactory faultFactory = new(
            new PostgresUnitOfWorkFactory(
                fixture.ApiServices.GetRequiredService<NpgsqlDataSource>()));
        await using (ServiceProvider faultServices = BuildSubscriptionServices(faultFactory))
        {
            IUpdateSubscriptionUseCase faultedUpdate = faultServices
                .GetRequiredService<IUpdateSubscriptionUseCase>();
            faultFactory.FailNextCommit();
            await Assert.ThrowsAsync<InjectedBeforeCommitFaultException>(
                () => faultedUpdate.ExecuteAsync(
                    new UpdateSubscriptionCommand(
                        rolledBackRequestId,
                        actor,
                        rolledBackKey,
                        assigned.Value.Value.Id,
                        ExpectedVersion: 4,
                        StartsAtSpecified: false,
                        StartsAt: null,
                        ExpiresAtSpecified: true,
                        ExpiresAt: extendedExpiry.AddDays(1),
                        StatusSpecified: false,
                        Status: null,
                        Reason: "must roll back before commit",
                        IpAddress: "192.0.2.80",
                        UserAgent: "ac008-integration"),
                    cancellationToken).AsTask()).ConfigureAwait(true);
        }
        await AssertSubscriptionFaultRolledBackAsync(
            assigned.Value.Value.Id,
            rolledBackRequestId,
            rolledBackKey,
            expectedVersion: 4,
            expectedExpiry: extendedExpiry,
            cancellationToken).ConfigureAwait(true);

        Result<SubscriptionCommandOutcome<SubscriptionView>> revoked =
            await updateSubscription.ExecuteAsync(
                new UpdateSubscriptionCommand(
                    revokeRequestId,
                    actor,
                    revokeKey,
                    assigned.Value.Value.Id,
                    ExpectedVersion: 4,
                    StartsAtSpecified: false,
                    StartsAt: null,
                    ExpiresAtSpecified: false,
                    ExpiresAt: null,
                    StatusSpecified: true,
                    Status: SubscriptionLifecycle.Revoked,
                    Reason: "revoke canonical assignment",
                    IpAddress: "192.0.2.80",
                    UserAgent: "ac008-integration"),
                cancellationToken).ConfigureAwait(true);
        Assert.True(revoked.IsSuccess, revoked.Error.Description);
        Assert.Equal(5, revoked.Value.Value.Version);
        Assert.Equal(
            SubscriptionEffectiveLifecycle.Revoked,
            revoked.Value.Value.EffectiveStatus);

        using (NpgsqlCommand canonical = fixture.AdministratorDataSource.CreateCommand())
        {
            canonical.CommandText = """
                SELECT id, version, expires_at, status,
                       template_id, template_name_snapshot, assigned_by
                FROM public.subscriptions
                WHERE user_id = $1 AND group_id = $2;
                """;
            canonical.Parameters.AddWithValue(userId.Value);
            canonical.Parameters.AddWithValue(groupId.Value);
            using NpgsqlDataReader reader = await canonical
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(true);
            Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
            Assert.Equal(assigned.Value.Value.Id.Value, reader.GetGuid(0));
            Assert.Equal(5L, reader.GetInt64(1));
            Assert.Equal(extendedExpiry, reader.GetFieldValue<DateTimeOffset>(2));
            Assert.Equal("revoked", reader.GetString(3));
            Assert.Equal(templateId.Value, reader.GetGuid(4));
            Assert.Equal(templateResult.Value.Value.Name, reader.GetString(5));
            Assert.Equal(actorId.Value, reader.GetGuid(6));
            Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        }

        List<SubscriptionAuditSnapshot> audits = [];
        using (NpgsqlCommand audit = fixture.AdministratorDataSource.CreateCommand())
        {
            audit.CommandText = """
                SELECT action,
                       request_id,
                       actor_user_id,
                       reason,
                       (before_state ->> 'version')::bigint,
                       (after_state ->> 'version')::bigint,
                       before_state ->> 'status',
                       after_state ->> 'status'
                FROM public.audit_logs
                WHERE target_type = 'subscription' AND target_id = $1
                ORDER BY (after_state ->> 'version')::bigint;
                """;
            audit.Parameters.AddWithValue(assigned.Value.Value.Id.Value);
            using NpgsqlDataReader reader = await audit
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(true);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(true))
            {
                audits.Add(new SubscriptionAuditSnapshot(
                    reader.GetString(0),
                    reader.GetGuid(1),
                    reader.GetGuid(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetString(7)));
            }
        }
        Assert.Equal(5, audits.Count);
        Assert.Equal(
            new SubscriptionAuditSnapshot(
                "subscription_access.subscription.assigned",
                assignRequestId.Value,
                actorId.Value,
                "initial canonical assignment",
                null,
                1,
                null,
                "active"),
            audits[0]);
        Assert.Equal(
            new SubscriptionAuditSnapshot(
                "subscription_access.subscription.updated",
                extendRequestId.Value,
                actorId.Value,
                "extend canonical assignment",
                1,
                2,
                "active",
                "active"),
            audits[1]);
        Assert.Equal(
            new SubscriptionAuditSnapshot(
                "subscription_access.subscription.updated",
                suspendRequestId.Value,
                actorId.Value,
                "suspend canonical assignment",
                2,
                3,
                "active",
                "suspended"),
            audits[2]);
        Assert.Equal(
            new SubscriptionAuditSnapshot(
                "subscription_access.subscription.updated",
                resumeRequestId.Value,
                actorId.Value,
                "resume canonical assignment",
                3,
                4,
                "suspended",
                "active"),
            audits[3]);
        Assert.Equal(
            new SubscriptionAuditSnapshot(
                "subscription_access.subscription.updated",
                revokeRequestId.Value,
                actorId.Value,
                "revoke canonical assignment",
                4,
                5,
                "active",
                "revoked"),
            audits[4]);

        Dictionary<string, (string Status, int ResponseStatus, Guid ResourceId)> idempotency = [];
        using (NpgsqlCommand records = fixture.AdministratorDataSource.CreateCommand())
        {
            records.CommandText = """
                SELECT idempotency_key, status, response_status, resource_id
                FROM public.idempotency_records
                WHERE idempotency_key IN ($1, $2, $3, $4, $5);
                """;
            records.Parameters.AddWithValue(assignKey);
            records.Parameters.AddWithValue(extendKey);
            records.Parameters.AddWithValue(suspendKey);
            records.Parameters.AddWithValue(resumeKey);
            records.Parameters.AddWithValue(revokeKey);
            using NpgsqlDataReader reader = await records
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(true);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(true))
            {
                Assert.True(idempotency.TryAdd(
                    reader.GetString(0),
                    (reader.GetString(1), reader.GetInt32(2), reader.GetGuid(3))));
            }
        }
        Assert.Equal(5, idempotency.Count);
        Assert.Equal(("completed", 201, assigned.Value.Value.Id.Value), idempotency[assignKey]);
        Assert.Equal(("completed", 200, assigned.Value.Value.Id.Value), idempotency[extendKey]);
        Assert.Equal(("completed", 200, assigned.Value.Value.Id.Value), idempotency[suspendKey]);
        Assert.Equal(("completed", 200, assigned.Value.Value.Id.Value), idempotency[resumeKey]);
        Assert.Equal(("completed", 200, assigned.Value.Value.Id.Value), idempotency[revokeKey]);

        using (NpgsqlCommand outbox = fixture.AdministratorDataSource.CreateCommand())
        {
            outbox.CommandText = """
                SELECT event_type, aggregate_version, correlation_id
                FROM public.outbox_messages
                WHERE aggregate_type = 'subscription' AND aggregate_id = $1
                ORDER BY aggregate_version;
                """;
            outbox.Parameters.AddWithValue(assigned.Value.Value.Id.Value);
            using NpgsqlDataReader reader = await outbox
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(true);
            Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
            Assert.Equal("subscription_assigned", reader.GetString(0));
            Assert.Equal(1L, reader.GetInt64(1));
            Assert.Equal(assignRequestId.Value, reader.GetGuid(2));
            Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
            Assert.Equal("subscription_updated", reader.GetString(0));
            Assert.Equal(2L, reader.GetInt64(1));
            Assert.Equal(extendRequestId.Value, reader.GetGuid(2));
            Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
            Assert.Equal("subscription_updated", reader.GetString(0));
            Assert.Equal(3L, reader.GetInt64(1));
            Assert.Equal(suspendRequestId.Value, reader.GetGuid(2));
            Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
            Assert.Equal("subscription_updated", reader.GetString(0));
            Assert.Equal(4L, reader.GetInt64(1));
            Assert.Equal(resumeRequestId.Value, reader.GetGuid(2));
            Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
            Assert.Equal("subscription_updated", reader.GetString(0));
            Assert.Equal(5L, reader.GetInt64(1));
            Assert.Equal(revokeRequestId.Value, reader.GetGuid(2));
            Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ExpiredSubscriptionIsRejectedByCanonicalGrantReaderWithoutWriteback()
    {
        // ADR 0007/error-catalog section 3.3 requires a missing canonical row
        // to return subscription_required and an existing expired row to
        // return subscription_inactive. AC-009 requires DB-time expiration
        // without lifecycle writeback.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        EntityId actorId = await SeedUserAsync("ac009-actor", cancellationToken)
            .ConfigureAwait(true);
        EntityId userId = await SeedUserAsync("ac009-user", cancellationToken)
            .ConfigureAwait(true);
        EntityId groupId = await SeedGroupAsync("active", actorId, cancellationToken)
            .ConfigureAwait(true);
        ServiceProvider services = BuildSubscriptionServices();
        await using ConfiguredAsyncDisposable servicesLease = services.ConfigureAwait(false);
        ISubscriptionAccessReader reader = services
            .GetRequiredService<ISubscriptionAccessReader>();
        PostgresSubscriptionRepository repository = new(
            fixture.ApiServices.GetRequiredService<NpgsqlDataSource>());

        Result<SubscriptionAccessSnapshot> missing = await reader.GetEffectiveAccessAsync(
            userId,
            groupId,
            cancellationToken).ConfigureAwait(true);
        Assert.True(missing.IsFailure);
        Assert.Equal("subscription_required", missing.Error.Code);
        Assert.Null(await repository.GetEffectiveAccessAsync(
            userId,
            groupId,
            cancellationToken).ConfigureAwait(true));

        EntityId templateId = EntityId.New();
        TemplateMutationResult template = await ExecuteAsync(
            (context, token) => repository.CreateTemplateAsync(
                templateId,
                groupId,
                $"AC-009 Template {Guid.NewGuid():N}",
                "Expiry boundary fixture",
                30,
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.True(template.WasChanged);

        DateTimeOffset startsAt;
        DateTimeOffset expiresAt;
        using (NpgsqlCommand clock = fixture.AdministratorDataSource.CreateCommand(
                   """
                   WITH observed AS MATERIALIZED (
                       SELECT clock_timestamp() AS at
                   )
                   SELECT observed.at - interval '1 minute',
                          observed.at + interval '1 day'
                   FROM observed;
                   """))
        {
            using NpgsqlDataReader data = await clock
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(true);
            Assert.True(await data.ReadAsync(cancellationToken).ConfigureAwait(true));
            startsAt = data.GetFieldValue<DateTimeOffset>(0);
            expiresAt = data.GetFieldValue<DateTimeOffset>(1);
            Assert.False(await data.ReadAsync(cancellationToken).ConfigureAwait(true));
        }

        EntityId subscriptionId = EntityId.New();
        SubscriptionMutationResult assigned = await ExecuteAsync(
            (context, token) => repository.AssignSubscriptionAsync(
                subscriptionId,
                userId,
                templateId,
                startsAt,
                expiresAt,
                actorId,
                "expiry boundary assignment",
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.True(assigned.WasChanged);

        Result<SubscriptionAccessSnapshot> beforeExpiry = await reader.GetEffectiveAccessAsync(
            userId,
            groupId,
            cancellationToken).ConfigureAwait(true);
        Assert.True(beforeExpiry.IsSuccess, beforeExpiry.Error.Description);
        Assert.Equal(SubscriptionEffectiveStatus.Active, beforeExpiry.Value.EffectiveStatus);
        Assert.NotNull(await repository.GetEffectiveAccessAsync(
            userId,
            groupId,
            cancellationToken).ConfigureAwait(true));

        DateTimeOffset expiryBoundary;
        using (NpgsqlCommand expire = fixture.AdministratorDataSource.CreateCommand("""
                   UPDATE public.subscriptions
                   SET expires_at = clock_timestamp()
                   WHERE id = $1
                   RETURNING expires_at;
                   """))
        {
            expire.Parameters.AddWithValue(subscriptionId.Value);
            using NpgsqlDataReader data = await expire
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(true);
            Assert.True(await data.ReadAsync(cancellationToken).ConfigureAwait(true));
            expiryBoundary = data.GetFieldValue<DateTimeOffset>(0);
            Assert.False(await data.ReadAsync(cancellationToken).ConfigureAwait(true));
        }

        SubscriptionPersistenceSnapshot before = await ReadSubscriptionPersistenceAsync(
            subscriptionId,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(expiryBoundary, before.ExpiresAt);

        Result<SubscriptionAccessSnapshot> expired = await reader.GetEffectiveAccessAsync(
            userId,
            groupId,
            cancellationToken).ConfigureAwait(true);
        Assert.True(expired.IsFailure);
        Assert.Equal("subscription_inactive", expired.Error.Code);
        SubscriptionRecord expiredRecord = Assert.IsType<SubscriptionRecord>(
            await repository.GetEffectiveAccessAsync(
                userId,
                groupId,
                cancellationToken).ConfigureAwait(true));
        Assert.Equal(SubscriptionEffectiveLifecycle.Expired, expiredRecord.EffectiveStatus);
        Assert.True(expiredRecord.ObservedAt >= expiredRecord.ExpiresAt);
        SubscriptionPersistenceSnapshot after = await ReadSubscriptionPersistenceAsync(
            subscriptionId,
            cancellationToken).ConfigureAwait(true);

        Assert.Equal(before, after);
        Assert.Equal("active", after.Status);
        Assert.Equal(1, after.Version);
        Assert.Equal(0, after.AuditCount);
        Assert.Equal(0, after.OutboxCount);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task CrudListsAndLifecycleProjectionsUseTheCanonicalRepository()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        NpgsqlDataSource dataSource = fixture.ApiServices.GetRequiredService<NpgsqlDataSource>();
        Assert.Throws<ArgumentNullException>(
            static () => new PostgresSubscriptionRepository(null!));
        PostgresSubscriptionRepository repository = new(dataSource);
        EntityId actorId = await SeedUserAsync("subscription-actor", cancellationToken)
            .ConfigureAwait(true);
        EntityId groupId = await SeedGroupAsync("active", actorId, cancellationToken)
            .ConfigureAwait(true);
        EntityId firstUserId = await SeedUserAsync("subscription-first", cancellationToken)
            .ConfigureAwait(true);
        EntityId secondUserId = await SeedUserAsync("subscription-second", cancellationToken)
            .ConfigureAwait(true);
        string suffix = Guid.NewGuid().ToString("N")[..12];
        EntityId firstTemplateId = EntityId.New();
        EntityId secondTemplateId = EntityId.New();

        TemplateMutationResult firstTemplate = await ExecuteAsync(
            (context, token) => repository.CreateTemplateAsync(
                firstTemplateId,
                groupId,
                $"Repository plan one {suffix}",
                "canonical template",
                30,
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(SubscriptionMutationDisposition.Updated, firstTemplate.Disposition);
        Assert.True(firstTemplate.WasChanged);
        Assert.Equal(SubscriptionTemplateLifecycle.Active, firstTemplate.Value!.Status);
        Assert.Equal(1, firstTemplate.Value.Version);

        TemplateMutationResult secondTemplate = await ExecuteAsync(
            (context, token) => repository.CreateTemplateAsync(
                secondTemplateId,
                groupId,
                $"Repository plan two {suffix}",
                null,
                60,
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(SubscriptionMutationDisposition.Updated, secondTemplate.Disposition);

        TemplateMutationResult noOp = await ExecuteAsync(
            (context, token) => repository.UpdateTemplateAsync(
                firstTemplateId,
                expectedVersion: 1,
                nameSpecified: false,
                name: null,
                descriptionSpecified: false,
                description: null,
                durationSpecified: false,
                durationDays: null,
                statusSpecified: false,
                status: null,
                retire: false,
                reason: null,
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(SubscriptionMutationDisposition.Updated, noOp.Disposition);
        Assert.False(noOp.WasChanged);
        Assert.Equal(1, noOp.Value!.Version);
        Assert.Null(noOp.BeforeState);

        string renamed = $"Repository renamed {suffix}";
        TemplateMutationResult disabled = await ExecuteAsync(
            (context, token) => repository.UpdateTemplateAsync(
                firstTemplateId,
                expectedVersion: 1,
                nameSpecified: true,
                name: renamed,
                descriptionSpecified: true,
                description: null,
                durationSpecified: true,
                durationDays: 45,
                statusSpecified: true,
                status: SubscriptionTemplateLifecycle.Disabled,
                retire: false,
                reason: "disable for repository coverage",
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(SubscriptionMutationDisposition.Updated, disabled.Disposition);
        Assert.True(disabled.WasChanged);
        Assert.Equal(SubscriptionTemplateLifecycle.Disabled, disabled.Value!.Status);
        Assert.Equal(2, disabled.Value.Version);
        Assert.Equal(1, disabled.BeforeState!.Value.GetProperty("version").GetInt64());

        TemplateMutationResult stale = await ExecuteAsync(
            (context, token) => repository.UpdateTemplateAsync(
                firstTemplateId,
                expectedVersion: 1,
                nameSpecified: false,
                name: null,
                descriptionSpecified: false,
                description: null,
                durationSpecified: false,
                durationDays: null,
                statusSpecified: true,
                status: SubscriptionTemplateLifecycle.Active,
                retire: false,
                reason: "stale version",
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(SubscriptionMutationDisposition.VersionConflict, stale.Disposition);
        Assert.False(stale.WasChanged);
        Assert.Null(stale.Value);
        Assert.Equal(2, stale.CurrentVersion);

        TemplateMutationResult enabled = await ExecuteAsync(
            (context, token) => repository.UpdateTemplateAsync(
                firstTemplateId,
                expectedVersion: 2,
                nameSpecified: false,
                name: null,
                descriptionSpecified: false,
                description: null,
                durationSpecified: false,
                durationDays: null,
                statusSpecified: true,
                status: SubscriptionTemplateLifecycle.Active,
                retire: false,
                reason: "enable for assignment",
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(3, enabled.Value!.Version);

        TemplateMutationResult nameConflict = await ExecuteAsync(
            (context, token) => repository.UpdateTemplateAsync(
                firstTemplateId,
                expectedVersion: 3,
                nameSpecified: true,
                name: secondTemplate.Value!.Name,
                descriptionSpecified: false,
                description: null,
                durationSpecified: false,
                durationDays: null,
                statusSpecified: false,
                status: null,
                retire: false,
                reason: null,
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(SubscriptionMutationDisposition.ResourceConflict, nameConflict.Disposition);
        Assert.Null(nameConflict.Value);

        SubscriptionTemplateRecord existingTemplate = Assert.IsType<SubscriptionTemplateRecord>(
            await repository.GetTemplateAsync(firstTemplateId, cancellationToken)
                .ConfigureAwait(true));
        Assert.Equal(renamed, existingTemplate.Name);
        Assert.Null(existingTemplate.Description);
        Assert.Null(await repository.GetTemplateAsync(EntityId.New(), cancellationToken)
            .ConfigureAwait(true));

        SubscriptionTemplateSlice templatePage = await repository.ListTemplatesAsync(
            cursor: null,
            limit: 1,
            cancellationToken).ConfigureAwait(true);
        Assert.True(templatePage.HasMore);
        SubscriptionTemplateRecord templateCursor = Assert.Single(templatePage.Items);
        SubscriptionTemplateSlice nextTemplatePage = await repository.ListTemplatesAsync(
            new SubscriptionCursor(templateCursor.CreatedAt, templateCursor.Id),
            limit: 100,
            cancellationToken).ConfigureAwait(true);
        Assert.NotEmpty(nextTemplatePage.Items);

        EntityId firstSubscriptionId = EntityId.New();
        SubscriptionMutationResult assigned = await ExecuteAsync(
            (context, token) => repository.AssignSubscriptionAsync(
                firstSubscriptionId,
                firstUserId,
                firstTemplateId,
                startsAt: null,
                expiresAt: null,
                actorId,
                "initial assignment",
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(SubscriptionMutationDisposition.Updated, assigned.Disposition);
        Assert.True(assigned.WasChanged);
        Assert.Equal(SubscriptionEffectiveLifecycle.Active, assigned.Value!.EffectiveStatus);
        Assert.Equal(1, assigned.Value.Version);

        DateTimeOffset scheduledStart = TimeProvider.System.GetUtcNow().AddDays(2);
        EntityId secondSubscriptionId = EntityId.New();
        SubscriptionMutationResult scheduled = await ExecuteAsync(
            (context, token) => repository.AssignSubscriptionAsync(
                secondSubscriptionId,
                secondUserId,
                secondTemplateId,
                scheduledStart,
                scheduledStart.AddDays(10),
                actorId,
                "scheduled assignment",
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(SubscriptionEffectiveLifecycle.Scheduled, scheduled.Value!.EffectiveStatus);

        SubscriptionRecord existing = Assert.IsType<SubscriptionRecord>(
            await repository.GetSubscriptionAsync(
                firstSubscriptionId,
                visibleToUserId: null,
                cancellationToken).ConfigureAwait(true));
        Assert.Equal(firstUserId, existing.UserId);
        Assert.NotNull(await repository.GetSubscriptionAsync(
            firstSubscriptionId,
            firstUserId,
            cancellationToken).ConfigureAwait(true));
        Assert.Null(await repository.GetSubscriptionAsync(
            firstSubscriptionId,
            secondUserId,
            cancellationToken).ConfigureAwait(true));
        Assert.NotNull(await repository.GetEffectiveAccessAsync(
            firstUserId,
            groupId,
            cancellationToken).ConfigureAwait(true));
        Assert.Null(await repository.GetEffectiveAccessAsync(
            firstUserId,
            EntityId.New(),
            cancellationToken).ConfigureAwait(true));

        SubscriptionSlice subscriptionPage = await repository.ListSubscriptionsAsync(
            cursor: null,
            limit: 1,
            userId: null,
            groupId: null,
            cancellationToken).ConfigureAwait(true);
        Assert.True(subscriptionPage.HasMore);
        SubscriptionRecord subscriptionCursor = Assert.Single(subscriptionPage.Items);
        SubscriptionSlice nextSubscriptionPage = await repository.ListSubscriptionsAsync(
            new SubscriptionCursor(subscriptionCursor.CreatedAt, subscriptionCursor.Id),
            limit: 100,
            userId: null,
            groupId,
            cancellationToken).ConfigureAwait(true);
        Assert.NotEmpty(nextSubscriptionPage.Items);
        SubscriptionSlice firstUser = await repository.ListSubscriptionsAsync(
            cursor: null,
            limit: 100,
            firstUserId,
            groupId: null,
            cancellationToken).ConfigureAwait(true);
        Assert.Contains(firstUser.Items, item => item.Id == firstSubscriptionId);
        Assert.Single(await repository.ListForUserAsync(firstUserId, cancellationToken)
            .ConfigureAwait(true));
        Assert.Single(await repository.ListActiveForUserAsync(firstUserId, cancellationToken)
            .ConfigureAwait(true));

        SubscriptionMutationResult suspended = await UpdateAsync(
            repository,
            firstSubscriptionId,
            expectedVersion: 1,
            status: SubscriptionLifecycle.Suspended,
            actorId,
            "suspend",
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(SubscriptionLifecycle.Suspended, suspended.Value!.Status);
        Assert.Equal(SubscriptionEffectiveLifecycle.Suspended, suspended.Value.EffectiveStatus);
        Assert.Empty(await repository.ListActiveForUserAsync(firstUserId, cancellationToken)
            .ConfigureAwait(true));

        SubscriptionMutationResult resumed = await UpdateAsync(
            repository,
            firstSubscriptionId,
            expectedVersion: 2,
            status: SubscriptionLifecycle.Active,
            actorId,
            "resume",
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(3, resumed.Value!.Version);

        SubscriptionMutationResult extended = await ExecuteAsync(
            (context, token) => repository.UpdateSubscriptionAsync(
                firstSubscriptionId,
                expectedVersion: 3,
                startsAtSpecified: false,
                startsAt: null,
                expiresAtSpecified: true,
                expiresAt: resumed.Value!.ExpiresAt.AddDays(1),
                statusSpecified: false,
                status: null,
                allowRevokedRegrant: false,
                actorId,
                "extend",
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(4, extended.Value!.Version);

        SubscriptionMutationResult revoked = await UpdateAsync(
            repository,
            firstSubscriptionId,
            expectedVersion: 4,
            status: SubscriptionLifecycle.Revoked,
            actorId,
            "revoke",
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(SubscriptionEffectiveLifecycle.Revoked, revoked.Value!.EffectiveStatus);

        SubscriptionMutationResult revokedNoOp = await UpdateAsync(
            repository,
            firstSubscriptionId,
            expectedVersion: 5,
            status: SubscriptionLifecycle.Revoked,
            actorId,
            "revoked no-op",
            cancellationToken).ConfigureAwait(true);
        Assert.False(revokedNoOp.WasChanged);
        Assert.Equal(5, revokedNoOp.Value!.Version);

        DateTimeOffset regrantStart = TimeProvider.System.GetUtcNow().AddMinutes(-5);
        SubscriptionMutationResult regranted = await ExecuteAsync(
            (context, token) => repository.UpdateSubscriptionAsync(
                firstSubscriptionId,
                expectedVersion: 5,
                startsAtSpecified: true,
                startsAt: regrantStart,
                expiresAtSpecified: true,
                expiresAt: regrantStart.AddDays(7),
                statusSpecified: true,
                status: SubscriptionLifecycle.Active,
                allowRevokedRegrant: true,
                actorId,
                "authorized regrant",
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.True(regranted.WasChanged);
        Assert.Equal(6, regranted.Value!.Version);
        Assert.Equal(SubscriptionEffectiveLifecycle.Active, regranted.Value.EffectiveStatus);

        TemplateMutationResult retired = await ExecuteAsync(
            (context, token) => repository.UpdateTemplateAsync(
                secondTemplateId,
                expectedVersion: 1,
                nameSpecified: false,
                name: null,
                descriptionSpecified: false,
                description: null,
                durationSpecified: false,
                durationDays: null,
                statusSpecified: false,
                status: null,
                retire: true,
                reason: "retire the plan",
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(SubscriptionTemplateLifecycle.Retired, retired.Value!.Status);
        Assert.Equal(2, retired.Value.Version);

        TemplateMutationResult retiredAgain = await ExecuteAsync(
            (context, token) => repository.UpdateTemplateAsync(
                secondTemplateId,
                expectedVersion: 2,
                nameSpecified: false,
                name: null,
                descriptionSpecified: false,
                description: null,
                durationSpecified: false,
                durationDays: null,
                statusSpecified: false,
                status: null,
                retire: true,
                reason: "cannot retire twice",
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(SubscriptionMutationDisposition.InvalidTransition, retiredAgain.Disposition);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task FailureDispositionsLimitsAndContractDriftFailClosed()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        PostgresSubscriptionRepository repository = new(
            fixture.ApiServices.GetRequiredService<NpgsqlDataSource>());
        EntityId actorId = await SeedUserAsync("subscription-failure-actor", cancellationToken)
            .ConfigureAwait(true);
        EntityId targetUserId = await SeedUserAsync("subscription-failure-target", cancellationToken)
            .ConfigureAwait(true);
        string suffix = Guid.NewGuid().ToString("N")[..12];
        EntityId activeGroupId = await SeedGroupAsync("active", actorId, cancellationToken)
            .ConfigureAwait(true);
        EntityId disabledGroupId = await SeedGroupAsync("disabled", actorId, cancellationToken)
            .ConfigureAwait(true);
        EntityId archivedGroupId = await SeedGroupAsync("archived", actorId, cancellationToken)
            .ConfigureAwait(true);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository
            .ListTemplatesAsync(null, 0, cancellationToken).AsTask()).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository
            .ListTemplatesAsync(null, 101, cancellationToken).AsTask()).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository
            .ListSubscriptionsAsync(null, 0, null, null, cancellationToken).AsTask())
            .ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository
            .ListSubscriptionsAsync(null, 101, null, null, cancellationToken).AsTask())
            .ConfigureAwait(true);

        TemplateMutationResult invalidTemplate = await ExecuteAsync(
            (context, token) => repository.CreateTemplateAsync(
                EntityId.New(),
                activeGroupId,
                " ",
                null,
                0,
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(SubscriptionMutationDisposition.InvalidTransition, invalidTemplate.Disposition);

        TemplateMutationResult missingGroup = await ExecuteAsync(
            (context, token) => repository.CreateTemplateAsync(
                EntityId.New(),
                EntityId.New(),
                $"Missing {suffix}",
                null,
                30,
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(SubscriptionMutationDisposition.NotFound, missingGroup.Disposition);

        TemplateMutationResult archivedGroup = await ExecuteAsync(
            (context, token) => repository.CreateTemplateAsync(
                EntityId.New(),
                archivedGroupId,
                $"Archived {suffix}",
                null,
                30,
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(SubscriptionMutationDisposition.GroupArchived, archivedGroup.Disposition);

        EntityId activeTemplateId = EntityId.New();
        TemplateMutationResult activeTemplate = await ExecuteAsync(
            (context, token) => repository.CreateTemplateAsync(
                activeTemplateId,
                activeGroupId,
                $"Active failure plan {suffix}",
                null,
                30,
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.True(activeTemplate.WasChanged);

        TemplateMutationResult duplicate = await ExecuteAsync(
            (context, token) => repository.CreateTemplateAsync(
                EntityId.New(),
                activeGroupId,
                activeTemplate.Value!.Name,
                null,
                30,
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(SubscriptionMutationDisposition.ResourceConflict, duplicate.Disposition);

        TemplateMutationResult missingUpdate = await ExecuteAsync(
            (context, token) => repository.UpdateTemplateAsync(
                EntityId.New(),
                expectedVersion: 1,
                nameSpecified: false,
                name: null,
                descriptionSpecified: false,
                description: null,
                durationSpecified: false,
                durationDays: null,
                statusSpecified: false,
                status: null,
                retire: false,
                reason: null,
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(SubscriptionMutationDisposition.NotFound, missingUpdate.Disposition);

        EntityId disabledTemplateId = EntityId.New();
        await ExecuteAsync(
            (context, token) => repository.CreateTemplateAsync(
                disabledTemplateId,
                disabledGroupId,
                $"Disabled group plan {suffix}",
                null,
                30,
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        SubscriptionMutationResult disabledGroup = await ExecuteAsync(
            (context, token) => repository.AssignSubscriptionAsync(
                EntityId.New(),
                targetUserId,
                disabledTemplateId,
                null,
                null,
                actorId,
                "disabled Group",
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(SubscriptionMutationDisposition.GroupDisabled, disabledGroup.Disposition);

        TemplateMutationResult disabledTemplate = await ExecuteAsync(
            (context, token) => repository.UpdateTemplateAsync(
                activeTemplateId,
                expectedVersion: 1,
                nameSpecified: false,
                name: null,
                descriptionSpecified: false,
                description: null,
                durationSpecified: false,
                durationDays: null,
                statusSpecified: true,
                status: SubscriptionTemplateLifecycle.Disabled,
                retire: false,
                reason: "disable Template",
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(2, disabledTemplate.Value!.Version);
        SubscriptionMutationResult templateDisabled = await ExecuteAsync(
            (context, token) => repository.AssignSubscriptionAsync(
                EntityId.New(),
                targetUserId,
                activeTemplateId,
                null,
                null,
                actorId,
                "disabled Template",
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(SubscriptionMutationDisposition.TemplateDisabled, templateDisabled.Disposition);

        await ExecuteAsync(
            (context, token) => repository.UpdateTemplateAsync(
                activeTemplateId,
                expectedVersion: 2,
                nameSpecified: false,
                name: null,
                descriptionSpecified: false,
                description: null,
                durationSpecified: false,
                durationDays: null,
                statusSpecified: true,
                status: SubscriptionTemplateLifecycle.Active,
                retire: false,
                reason: "enable Template",
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        EntityId subscriptionId = EntityId.New();
        SubscriptionMutationResult created = await ExecuteAsync(
            (context, token) => repository.AssignSubscriptionAsync(
                subscriptionId,
                targetUserId,
                activeTemplateId,
                null,
                null,
                actorId,
                "canonical assignment",
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.True(created.WasChanged);

        SubscriptionMutationResult canonicalConflict = await ExecuteAsync(
            (context, token) => repository.AssignSubscriptionAsync(
                EntityId.New(),
                targetUserId,
                activeTemplateId,
                null,
                null,
                actorId,
                "duplicate canonical assignment",
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            SubscriptionMutationDisposition.CanonicalConflict,
            canonicalConflict.Disposition);

        SubscriptionMutationResult missingTemplate = await ExecuteAsync(
            (context, token) => repository.AssignSubscriptionAsync(
                EntityId.New(),
                targetUserId,
                EntityId.New(),
                null,
                null,
                actorId,
                "missing Template",
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(SubscriptionMutationDisposition.NotFound, missingTemplate.Disposition);

        SubscriptionMutationResult foreignKeyConflict = await ExecuteAsync(
            (context, token) => repository.AssignSubscriptionAsync(
                EntityId.New(),
                EntityId.New(),
                activeTemplateId,
                null,
                null,
                actorId,
                "unknown User",
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            SubscriptionMutationDisposition.ResourceConflict,
            foreignKeyConflict.Disposition);

        SubscriptionMutationResult missingSubscription = await UpdateAsync(
            repository,
            EntityId.New(),
            expectedVersion: 1,
            status: SubscriptionLifecycle.Suspended,
            actorId,
            "missing Subscription",
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(SubscriptionMutationDisposition.NotFound, missingSubscription.Disposition);

        SubscriptionMutationResult staleSubscription = await UpdateAsync(
            repository,
            subscriptionId,
            expectedVersion: 99,
            status: SubscriptionLifecycle.Suspended,
            actorId,
            "stale Subscription",
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            SubscriptionMutationDisposition.VersionConflict,
            staleSubscription.Disposition);

        SubscriptionMutationResult invalidTransition = await ExecuteAsync(
            (context, token) => repository.UpdateSubscriptionAsync(
                subscriptionId,
                expectedVersion: 1,
                startsAtSpecified: false,
                startsAt: null,
                expiresAtSpecified: true,
                expiresAt: created.Value!.StartsAt.AddMinutes(-1),
                statusSpecified: false,
                status: null,
                allowRevokedRegrant: false,
                actorId,
                "invalid expiry",
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            SubscriptionMutationDisposition.InvalidTransition,
            invalidTransition.Disposition);

        Assert.IsType<InvalidOperationException>(InvokePrivateStaticFailure(
            "ParseDisposition",
            "contract_drift"));
        Assert.IsType<InvalidOperationException>(InvokePrivateStaticFailure(
            "ParseTemplateStatus",
            "contract_drift"));
        Assert.IsType<InvalidOperationException>(InvokePrivateStaticFailure(
            "ParseSubscriptionStatus",
            "contract_drift"));
        Assert.IsType<InvalidOperationException>(InvokePrivateStaticFailure(
            "ParseEffectiveStatus",
            "contract_drift"));
        Assert.IsType<ArgumentOutOfRangeException>(InvokePrivateStaticFailure(
            "TemplateStatusCode",
            (SubscriptionTemplateLifecycle)int.MaxValue));
        Assert.IsType<ArgumentOutOfRangeException>(InvokePrivateStaticFailure(
            "SubscriptionStatusCode",
            (SubscriptionLifecycle)int.MaxValue));
    }

    private async ValueTask AssertSubscriptionFaultRolledBackAsync(
        EntityId subscriptionId,
        EntityId requestId,
        string idempotencyKey,
        long expectedVersion,
        DateTimeOffset expectedExpiry,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            SELECT
                (SELECT count(*)
                 FROM public.subscriptions
                 WHERE id = $1
                   AND status = 'active'
                   AND version = $2
                   AND expires_at = $3),
                (SELECT count(*)
                 FROM public.audit_logs
                 WHERE request_id = $4),
                (SELECT count(*)
                 FROM public.outbox_messages
                 WHERE correlation_id = $4),
                (SELECT count(*)
                 FROM public.idempotency_records
                 WHERE idempotency_key = $5);
            """);
        command.Parameters.AddWithValue(subscriptionId.Value);
        command.Parameters.AddWithValue(expectedVersion);
        command.Parameters.AddWithValue(expectedExpiry);
        command.Parameters.AddWithValue(requestId.Value);
        command.Parameters.AddWithValue(idempotencyKey);
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(true);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal(0, reader.GetInt64(1));
        Assert.Equal(0, reader.GetInt64(2));
        Assert.Equal(0, reader.GetInt64(3));
    }

    private ServiceProvider BuildSubscriptionServices(
        IUnitOfWorkFactory? unitOfWorkFactory = null)
    {
        string connectionString = fixture.ApiServices
            .GetRequiredService<IConfiguration>()["Data:Postgres:ConnectionString"]
            ?? throw new InvalidOperationException(
                "The PostgreSQL runtime fixture did not expose its API connection string.");
        ConfigurationManager configuration = new();
        configuration["Data:Postgres:ConnectionString"] = connectionString;
        configuration["Data:Redis:ConnectionString"] = fixture.RedisConnectionString;
        configuration["Data:Redis:KeyPrefix"] =
            $"poolai:r1:subscription-ac008:{Guid.NewGuid():N}:";
        configuration["Health:Ntp:Server"] = "127.0.0.1";
        configuration["Health:Ntp:Port"] = "123";
        configuration["Idempotency:RequestHashPepper"] = RequestHashPepper;

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddPoolAiPostgresRuntime(connectionString);
        services.AddOperationsModule(configuration, "Integration");
        services.AddSingleton<IUserStatusReader>(static provider =>
            new PostgresIdentitySessionReader(
                provider.GetRequiredService<NpgsqlDataSource>()));
        services.AddSubscriptionAccessModule(configuration);
        if (unitOfWorkFactory is not null)
        {
            services.AddSingleton(unitOfWorkFactory);
        }

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private async ValueTask AssignRoleAsync(
        EntityId userId,
        string roleId,
        EntityId assignedBy,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            INSERT INTO public.user_roles (user_id, role_id, assigned_by)
            VALUES ($1, $2, $3);
            """);
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(Guid.Parse(roleId));
        command.Parameters.AddWithValue(assignedBy.Value);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true));
    }

    private async ValueTask<SubscriptionPersistenceSnapshot> ReadSubscriptionPersistenceAsync(
        EntityId subscriptionId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            SELECT subscription.status,
                   subscription.version,
                   subscription.starts_at,
                   subscription.expires_at,
                   subscription.created_at,
                   subscription.updated_at,
                   subscription.xmin::text,
                   (
                       SELECT count(*)
                       FROM public.audit_logs AS audit
                       WHERE audit.target_type = 'subscription'
                         AND audit.target_id = subscription.id
                   ),
                   (
                       SELECT count(*)
                       FROM public.outbox_messages AS message
                       WHERE message.aggregate_type = 'subscription'
                         AND message.aggregate_id = subscription.id
                   )
            FROM public.subscriptions AS subscription
            WHERE subscription.id = $1;
            """);
        command.Parameters.AddWithValue(subscriptionId.Value);
        using NpgsqlDataReader data = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(true);
        Assert.True(await data.ReadAsync(cancellationToken).ConfigureAwait(true));
        SubscriptionPersistenceSnapshot value = new(
            data.GetString(0),
            data.GetInt64(1),
            data.GetFieldValue<DateTimeOffset>(2),
            data.GetFieldValue<DateTimeOffset>(3),
            data.GetFieldValue<DateTimeOffset>(4),
            data.GetFieldValue<DateTimeOffset>(5),
            data.GetString(6),
            data.GetInt64(7),
            data.GetInt64(8));
        Assert.False(await data.ReadAsync(cancellationToken).ConfigureAwait(true));
        return value;
    }

    private async ValueTask<SubscriptionMutationResult> UpdateAsync(
        PostgresSubscriptionRepository repository,
        EntityId subscriptionId,
        long expectedVersion,
        SubscriptionLifecycle status,
        EntityId actorId,
        string reason,
        CancellationToken cancellationToken) => await ExecuteAsync(
            (context, token) => repository.UpdateSubscriptionAsync(
                subscriptionId,
                expectedVersion,
                startsAtSpecified: false,
                startsAt: null,
                expiresAtSpecified: false,
                expiresAt: null,
                statusSpecified: true,
                status,
                allowRevokedRegrant: false,
                actorId,
                reason,
                context,
                token),
            cancellationToken).ConfigureAwait(true);

    private async ValueTask<T> ExecuteAsync<T>(
        Func<IUnitOfWorkContext, CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken)
    {
        IUnitOfWorkFactory factory = fixture.ApiServices
            .GetRequiredService<IUnitOfWorkFactory>();
        IUnitOfWork unitOfWork = await factory.BeginAsync(cancellationToken)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable lease = unitOfWork.ConfigureAwait(false);
        T result = await action(unitOfWork.Context, cancellationToken).ConfigureAwait(true);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(true);
        return result;
    }

    private async ValueTask<EntityId> SeedGroupAsync(
        string status,
        EntityId actorId,
        CancellationToken cancellationToken)
    {
        EntityId groupId = EntityId.New();
        using NpgsqlCommand create = fixture.AdministratorDataSource.CreateCommand("""
            SELECT disposition
            FROM public.poolai_group_create(
                $1, $2, 'Subscription repository coverage',
                $3, 100000, $4, $5, $6, $7, 'repository coverage seed');
            """);
        create.Parameters.AddWithValue(groupId.Value);
        create.Parameters.AddWithValue($"Subscription repository {Guid.NewGuid():N}");
        create.Parameters.AddWithValue(EntityId.New().Value);
        create.Parameters.AddWithValue(actorId.Value);
        create.Parameters.AddWithValue(EntityId.New().Value);
        create.Parameters.AddWithValue(EntityId.New().Value);
        create.Parameters.AddWithValue($"repository-group-{Guid.NewGuid():N}");
        Assert.Equal(
            "created",
            Assert.IsType<string>(await create
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(true)));

        if (string.Equals(status, "disabled", StringComparison.Ordinal))
        {
            return groupId;
        }

        if (string.Equals(status, "active", StringComparison.Ordinal))
        {
            EntityId channelId = EntityId.New();
            EntityId accountId = EntityId.New();
            using NpgsqlCommand supply = fixture.AdministratorDataSource.CreateCommand("""
                WITH inserted_channel AS (
                    INSERT INTO public.channels (
                        id, provider, name, model_rules, status
                    ) VALUES (
                        $1, 'openai', $2,
                        '{"gpt-test":"gpt-test"}'::jsonb, 'active'
                    )
                    RETURNING id
                ),
                inserted_account AS (
                    INSERT INTO public.accounts (
                        id, provider, name, auth_type, upstream_base_url,
                        credential_envelope, credential_prefix,
                        status, last_health_at, last_health_status
                    ) VALUES (
                        $3, 'openai', $4, 'api_key',
                        'https://example.test/v1', '{}'::jsonb,
                        'sk-repository', 'active', clock_timestamp(), 'healthy'
                    )
                    RETURNING id
                ),
                inserted_configuration AS (
                    INSERT INTO public.group_supply_configurations (
                        group_id, channel_id
                    )
                    SELECT $5, id FROM inserted_channel
                    RETURNING group_id
                )
                INSERT INTO public.group_accounts (group_id, account_id, is_enabled)
                SELECT inserted_configuration.group_id, inserted_account.id, true
                FROM inserted_configuration
                CROSS JOIN inserted_account;
                """);
            supply.Parameters.AddWithValue(channelId.Value);
            supply.Parameters.AddWithValue($"Repository channel {Guid.NewGuid():N}");
            supply.Parameters.AddWithValue(accountId.Value);
            supply.Parameters.AddWithValue($"Repository account {Guid.NewGuid():N}");
            supply.Parameters.AddWithValue(groupId.Value);
            Assert.Equal(
                1,
                await supply.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true));
        }
        else
        {
            Assert.Equal("archived", status);
        }

        using NpgsqlCommand transition = fixture.AdministratorDataSource.CreateCommand("""
            SELECT disposition
            FROM public.poolai_group_update(
                $1, 1, false, NULL, false, NULL, $2,
                'repository coverage transition',
                CASE WHEN $2 = 'active' THEN 'v1.repository-ready' ELSE NULL END,
                CASE WHEN $2 = 'active' THEN clock_timestamp() ELSE NULL END);
            """);
        transition.Parameters.AddWithValue(groupId.Value);
        transition.Parameters.AddWithValue(status);
        Assert.Equal(
            "updated",
            Assert.IsType<string>(await transition
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(true)));
        return groupId;
    }

    private async ValueTask<EntityId> SeedUserAsync(
        string prefix,
        CancellationToken cancellationToken)
    {
        EntityId userId = EntityId.New();
        string email = $"{prefix}-{Guid.NewGuid():N}@example.test";
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            INSERT INTO public.users (
                id, email, normalized_email, display_name, password_hash,
                security_stamp
            ) VALUES ($1, $2, $2, $3, 'poolai-password-v1:test', $4);
            """);
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(email);
        command.Parameters.AddWithValue(prefix);
        command.Parameters.AddWithValue(Guid.CreateVersion7());
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true));
        return userId;
    }

    private static Exception InvokePrivateStaticFailure(
        string methodName,
        params object?[] arguments)
    {
        MethodInfo? method = typeof(PostgresSubscriptionRepository).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        TargetInvocationException invocation = Assert.Throws<TargetInvocationException>(
            () => method.Invoke(null, arguments));
        return Assert.IsAssignableFrom<Exception>(invocation.InnerException);
    }

    private sealed class FailBeforeCommitUnitOfWorkFactory(IUnitOfWorkFactory inner)
        : IUnitOfWorkFactory
    {
        private int failNextCommit;

        internal void FailNextCommit() => Interlocked.Exchange(ref failNextCommit, 1);

        public async ValueTask<IUnitOfWork> BeginAsync(CancellationToken cancellationToken) =>
            new FailBeforeCommitUnitOfWork(
                await inner.BeginAsync(cancellationToken).ConfigureAwait(false),
                this);

        private bool ConsumeFailure() =>
            Interlocked.Exchange(ref failNextCommit, 0) == 1;

        private sealed class FailBeforeCommitUnitOfWork(
            IUnitOfWork innerUnitOfWork,
            FailBeforeCommitUnitOfWorkFactory owner) : IUnitOfWork
        {
            public IUnitOfWorkContext Context => innerUnitOfWork.Context;

            public ValueTask CommitAsync(CancellationToken cancellationToken)
            {
                if (owner.ConsumeFailure())
                {
                    throw new InjectedBeforeCommitFaultException();
                }

                return innerUnitOfWork.CommitAsync(cancellationToken);
            }

            public ValueTask DisposeAsync() => innerUnitOfWork.DisposeAsync();
        }
    }

    private sealed class InjectedBeforeCommitFaultException : Exception;

    private sealed record SubscriptionAuditSnapshot(
        string Action,
        Guid RequestId,
        Guid ActorUserId,
        string Reason,
        long? BeforeVersion,
        long AfterVersion,
        string? BeforeStatus,
        string AfterStatus);

    private sealed record SubscriptionPersistenceSnapshot(
        string Status,
        long Version,
        DateTimeOffset StartsAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        string TransactionId,
        long AuditCount,
        long OutboxCount);
}
#pragma warning restore MA0051
