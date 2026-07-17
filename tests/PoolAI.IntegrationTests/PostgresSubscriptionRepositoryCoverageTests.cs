#pragma warning disable MA0051 // PostgreSQL contract scenarios keep the complete lifecycle visible.
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.SubscriptionAccess.Application;
using PoolAI.Modules.SubscriptionAccess.Application.Ports;
using PoolAI.Modules.SubscriptionAccess.Domain;
using PoolAI.Modules.SubscriptionAccess.Infrastructure.Persistence;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class PostgresSubscriptionRepositoryCoverageTests(PostgresRuntimeFixture fixture)
{
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
}
#pragma warning restore MA0051
