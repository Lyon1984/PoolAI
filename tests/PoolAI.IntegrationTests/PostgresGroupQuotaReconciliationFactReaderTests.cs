#pragma warning disable MA0051 // The signed reconciliation scenario is intentionally explicit.
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class PostgresGroupQuotaReconciliationFactReaderTests(
    PostgresRuntimeFixture fixture)
{
    private const int PlanHistoryCount = 50_000;
    private const int ForeignGroupHistoryCount = PlanHistoryCount / 5;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ReaderReturnsOneCheckpointPinnedAuthoritativeSnapshot()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SeededScenario scenario = await SeedAsync(cancellationToken).ConfigureAwait(true);

        Assert.Equal(
            scenario.PeriodId,
            await ResolvePeriodAsync(
                scenario.GroupId,
                periodId: null,
                cancellationToken).ConfigureAwait(true));
        Assert.Equal(
            scenario.ClosedPeriodId,
            await ResolvePeriodAsync(
                scenario.GroupId,
                scenario.ClosedPeriodId,
                cancellationToken).ConfigureAwait(true));
        Assert.Null(await ResolvePeriodAsync(
            scenario.GroupId,
            new EntityId(Guid.NewGuid()),
            cancellationToken).ConfigureAwait(true));
        Assert.Null(await ResolvePeriodAsync(
            new EntityId(Guid.NewGuid()),
            periodId: null,
            cancellationToken).ConfigureAwait(true));

        GroupQuotaReconciliationFactSnapshot snapshot = Assert.IsType<
            GroupQuotaReconciliationFactSnapshot>(await ReadAsync(
                scenario.GroupId,
                periodId: null,
                checkpointSourceEventSequence: scenario.CheckpointSequence,
                cancellationToken: cancellationToken).ConfigureAwait(true));

        Assert.Equal(scenario.GroupId, snapshot.GroupId);
        Assert.Equal(scenario.PeriodId, snapshot.PeriodId);
        Assert.Equal(scenario.CheckpointSequence, snapshot.CheckpointSourceEventSequence);
        Assert.Equal(new BigInteger(1000), snapshot.LedgerTotalTokens);
        Assert.Equal(new BigInteger(12), snapshot.LedgerConsumedTokens);
        Assert.Equal(new BigInteger(5), snapshot.LedgerReservedTokens);
        Assert.Equal(new BigInteger(12), snapshot.FactConsumedTokens);
        Assert.Equal(new BigInteger(5), snapshot.PendingReservationTokens);
        Assert.Equal(1, snapshot.PendingReservationCount);
        Assert.Equal(1, snapshot.OverdueReservationCount);
        Assert.Equal(scenario.PendingLeaseExpiresAt, snapshot.OldestOverdueAt);
        Assert.Equal(new BigInteger(15), snapshot.ExpectedConsumedAtCheckpoint);
        Assert.True(snapshot.CheckpointBelongsToGroup);
        Assert.Equal(scenario.LatestSequence, snapshot.LatestPeriodEventSequence);
        Assert.Equal(scenario.LatestOccurredAt, snapshot.LatestPeriodEventOccurredAt);
        Assert.True(snapshot.IsCurrentPeriod);
        Assert.Equal(scenario.FirstSequence, snapshot.FirstPeriodEventSequence);
        Assert.Equal(scenario.LatestSequence, snapshot.LatestGroupEventSequence);
        Assert.Equal(5, snapshot.PeriodEventCount);
        Assert.True(snapshot.EventChainConsistent);
        Assert.True(snapshot.FactEventCoverageConsistent);
        Assert.True(snapshot.LatestEventMatchesLedger);
        Assert.Equal(BigInteger.Zero, snapshot.OverageTokens);
        Assert.True(snapshot.CheckedAt >= snapshot.LatestPeriodEventOccurredAt);

        IReadOnlyList<long> firstSequencePage = await ListPeriodSequencesAsync(
            scenario.GroupId,
            scenario.PeriodId,
            scenario.LatestSequence,
            afterSourceEventSequence: 0,
            maximumCount: 2,
            cancellationToken).ConfigureAwait(true);
        IReadOnlyList<long> secondSequencePage = await ListPeriodSequencesAsync(
            scenario.GroupId,
            scenario.PeriodId,
            scenario.LatestSequence,
            firstSequencePage[^1],
            maximumCount: 2,
            cancellationToken).ConfigureAwait(true);
        IReadOnlyList<long> thirdSequencePage = await ListPeriodSequencesAsync(
            scenario.GroupId,
            scenario.PeriodId,
            scenario.LatestSequence,
            secondSequencePage[^1],
            maximumCount: 2,
            cancellationToken).ConfigureAwait(true);
        long[] exactPeriodSequences =
            [.. firstSequencePage, .. secondSequencePage, .. thirdSequencePage];
        Assert.Equal(5, exactPeriodSequences.Length);
        Assert.Equal(scenario.FirstSequence, exactPeriodSequences[0]);
        Assert.Equal(scenario.LatestSequence, exactPeriodSequences[^1]);
        Assert.DoesNotContain(scenario.ClosedEventSequence, exactPeriodSequences);
        Assert.DoesNotContain(scenario.ForeignGroupEventSequence, exactPeriodSequences);
        Assert.Equal(
            exactPeriodSequences.Order(),
            exactPeriodSequences);

        GroupQuotaReconciliationFactSnapshot explicitPeriod = Assert.IsType<
            GroupQuotaReconciliationFactSnapshot>(await ReadAsync(
                scenario.GroupId,
                scenario.PeriodId,
                checkpointSourceEventSequence: 0,
                cancellationToken: cancellationToken).ConfigureAwait(true));
        Assert.Equal(BigInteger.Zero, explicitPeriod.ExpectedConsumedAtCheckpoint);
        GroupQuotaReconciliationFactSnapshot closedPeriod = Assert.IsType<
            GroupQuotaReconciliationFactSnapshot>(await ReadAsync(
                scenario.GroupId,
                scenario.ClosedPeriodId,
                scenario.LatestSequence,
                cancellationToken: cancellationToken).ConfigureAwait(true));
        Assert.False(closedPeriod.IsCurrentPeriod);
        Assert.Equal(new BigInteger(7), closedPeriod.LedgerConsumedTokens);
        Assert.Equal(new BigInteger(7), closedPeriod.ExpectedConsumedAtCheckpoint);
        Assert.True(closedPeriod.CheckpointBelongsToGroup);
        Assert.Equal(scenario.ClosedEventSequence, closedPeriod.FirstPeriodEventSequence);
        Assert.Equal(scenario.ClosedEventSequence, closedPeriod.LatestPeriodEventSequence);
        Assert.Equal(scenario.LatestSequence, closedPeriod.LatestGroupEventSequence);
        Assert.Equal(1, closedPeriod.PeriodEventCount);
        Assert.True(closedPeriod.EventChainConsistent);
        Assert.True(closedPeriod.FactEventCoverageConsistent);
        Assert.True(closedPeriod.LatestEventMatchesLedger);
        Assert.Null(await ReadAsync(
            scenario.GroupId,
            new EntityId(Guid.NewGuid()),
            scenario.CheckpointSequence,
            cancellationToken: cancellationToken).ConfigureAwait(true));
        Assert.Null(await ReadAsync(
            new EntityId(Guid.NewGuid()),
            periodId: null,
            checkpointSourceEventSequence: scenario.CheckpointSequence,
            cancellationToken: cancellationToken).ConfigureAwait(true));

        GroupQuotaReconciliationFactSnapshot foreignCheckpoint = Assert.IsType<
            GroupQuotaReconciliationFactSnapshot>(await ReadAsync(
                scenario.GroupId,
                scenario.PeriodId,
                scenario.ForeignGroupEventSequence,
                cancellationToken).ConfigureAwait(true));
        Assert.False(foreignCheckpoint.CheckpointBelongsToGroup);

        IReadOnlyList<GroupQuotaReconciliationCandidate> candidates =
            await ListCandidatesAsync(
                scenario.GroupId,
                maximumCount: 1000,
                cancellationToken: cancellationToken).ConfigureAwait(true);
        Assert.Contains(
            candidates,
            candidate => candidate.GroupId == scenario.OtherGroupId
                && candidate.PeriodId == scenario.OtherPeriodId);
        Assert.All(
            candidates,
            candidate => Assert.True(Compare(candidate.GroupId, scenario.GroupId) > 0));
        Assert.True(candidates.Count <= 1000);
        for (int index = 1; index < candidates.Count; index++)
        {
            Assert.True(Compare(
                candidates[index - 1].GroupId,
                candidates[index].GroupId) < 0);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task EventContinuityAndLatestLedgerChecksRemainIndependent()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SeededScenario scenario = await SeedAsync(cancellationToken).ConfigureAwait(true);
        await CorruptMiddleDeltaAsync(scenario, cancellationToken).ConfigureAwait(true);

        GroupQuotaReconciliationFactSnapshot snapshot = Assert.IsType<
            GroupQuotaReconciliationFactSnapshot>(await ReadAsync(
                scenario.GroupId,
                scenario.PeriodId,
                scenario.CheckpointSequence,
                cancellationToken).ConfigureAwait(true));

        Assert.False(snapshot.EventChainConsistent);
        Assert.True(snapshot.LatestEventMatchesLedger);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task LatestLedgerMismatchDoesNotRewriteEventContinuity()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SeededScenario scenario = await SeedAsync(cancellationToken).ConfigureAwait(true);
        await CorruptLedgerAsync(scenario, cancellationToken).ConfigureAwait(true);

        GroupQuotaReconciliationFactSnapshot snapshot = Assert.IsType<
            GroupQuotaReconciliationFactSnapshot>(await ReadAsync(
                scenario.GroupId,
                scenario.PeriodId,
                scenario.CheckpointSequence,
                cancellationToken).ConfigureAwait(true));

        Assert.True(snapshot.EventChainConsistent);
        Assert.False(snapshot.LatestEventMatchesLedger);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task MissingTerminalFactEventIsAnIndependentIntegrityFailure()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SeededScenario scenario = await SeedAsync(cancellationToken).ConfigureAwait(true);
        await CorruptTerminalEventTypeAsync(scenario, cancellationToken)
            .ConfigureAwait(true);

        GroupQuotaReconciliationFactSnapshot snapshot = Assert.IsType<
            GroupQuotaReconciliationFactSnapshot>(await ReadAsync(
                scenario.GroupId,
                scenario.PeriodId,
                scenario.CheckpointSequence,
                cancellationToken).ConfigureAwait(true));

        Assert.True(snapshot.EventChainConsistent);
        Assert.False(snapshot.FactEventCoverageConsistent);
        Assert.True(snapshot.LatestEventMatchesLedger);
        Assert.Equal(snapshot.LedgerConsumedTokens, snapshot.FactConsumedTokens);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ExactPeriodSequencePageUsesTheExistingBoundedIndex()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using NpgsqlConnection connection = await fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(true);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(true);
        EntityId groupId = EntityId.New();
        EntityId periodId = EntityId.New();
        long latestSourceSequence = await SeedPlanHistoryAsync(
            connection,
            transaction,
            groupId,
            periodId,
            cancellationToken).ConfigureAwait(true);

        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) "
            + ReadSqlConstant(
                typeof(PoolAI.Modules.GroupQuota.Infrastructure.Persistence
                    .PostgresGroupQuotaReconciliationFactReader),
                "ListPeriodSourceEventSequencesSql");
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, groupId.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, periodId.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, latestSourceSequence);
        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, 0L);
        command.Parameters.AddWithValue(100);
        IReadOnlyList<PlanNode> nodes = await ReadPlanAsync(
            command,
            cancellationToken).ConfigureAwait(true);

        Assert.Contains(nodes, static node => string.Equals(
            node.IndexName,
            "ix_group_quota_events_period_sequence",
            StringComparison.Ordinal));
        Assert.DoesNotContain(nodes, static node =>
            node.TouchesQuotaEvents && node.RowsExamined >= PlanHistoryCount / 10);
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ExactPointReconciliationUsesIndexesWithoutScanningUnrelatedHistory()
    {
        // Governing contract: ADR 0013 requires the point reconciliation read
        // to use existing indexes and avoid unbounded global event history.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using NpgsqlConnection connection = await fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(true);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(true);
        EntityId groupId = EntityId.New();
        EntityId periodId = EntityId.New();
        long latestSourceSequence = await SeedPlanHistoryAsync(
            connection,
            transaction,
            groupId,
            periodId,
            cancellationToken).ConfigureAwait(true);

        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) "
            + ReadSqlConstant(
                typeof(PoolAI.Modules.GroupQuota.Infrastructure.Persistence
                    .PostgresGroupQuotaReconciliationFactReader),
                "ReadSql");
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, groupId.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, periodId.Value);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Bigint,
            latestSourceSequence);
        IReadOnlyList<PlanNode> nodes = await ReadPlanAsync(
            command,
            cancellationToken).ConfigureAwait(true);

        Assert.True(
            nodes.Any(static node => string.Equals(
                node.IndexName,
                "ix_group_quota_events_period_sequence",
                StringComparison.Ordinal)),
            string.Join(
                Environment.NewLine,
                nodes.Select(static node => node.ToString())));
        Assert.True(
            nodes.Any(static node => string.Equals(
                node.IndexName,
                "ix_group_quota_events_group_sequence",
                StringComparison.Ordinal)),
            string.Join(
                Environment.NewLine,
                nodes.Select(static node => node.ToString())));
        Assert.DoesNotContain(nodes, static node =>
            node.TouchesQuotaEvents && node.RowsExamined >= PlanHistoryCount / 10);
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(true);
    }

    private static async ValueTask<long> SeedPlanHistoryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EntityId groupId,
        EntityId periodId,
        CancellationToken cancellationToken)
    {
        EntityId historicalPeriodId = EntityId.New();
        EntityId foreignGroupId = EntityId.New();
        EntityId foreignPeriodId = EntityId.New();
        using (NpgsqlCommand constraints = new(
            "SET CONSTRAINTS ALL DEFERRED;",
            connection,
            transaction))
        {
            _ = await constraints.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        using (NpgsqlCommand group = new(
            "INSERT INTO public.groups (id, name, status) "
                + "VALUES ($1, 'm3e5-plan-' || $1::text, 'disabled'), "
                + "($2, 'm3e5-plan-' || $2::text, 'disabled');",
            connection,
            transaction))
        {
            group.Parameters.AddWithValue(NpgsqlDbType.Uuid, groupId.Value);
            group.Parameters.AddWithValue(
                NpgsqlDbType.Uuid,
                foreignGroupId.Value);
            Assert.Equal(
                2,
                await group.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false));
        }

        using (NpgsqlCommand quota = new(
            "INSERT INTO public.group_token_quotas "
                + "(group_id, current_period_id, enabled, version) "
                + "VALUES ($1, $2, true, 1), ($3, $4, true, 1);",
            connection,
            transaction))
        {
            quota.Parameters.AddWithValue(NpgsqlDbType.Uuid, groupId.Value);
            quota.Parameters.AddWithValue(NpgsqlDbType.Uuid, periodId.Value);
            quota.Parameters.AddWithValue(
                NpgsqlDbType.Uuid,
                foreignGroupId.Value);
            quota.Parameters.AddWithValue(
                NpgsqlDbType.Uuid,
                foreignPeriodId.Value);
            Assert.Equal(
                2,
                await quota.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false));
        }

        using (NpgsqlCommand period = new(
            """
            INSERT INTO public.group_quota_periods (
                id, group_id, period_number, total_tokens, consumed_tokens,
                reserved_tokens, status, opened_at, closed_at, reset_reason, version)
            VALUES
                ($1, $2, 1, 1, 0, 0, 'closed',
                    clock_timestamp() - interval '2 days',
                    clock_timestamp() - interval '1 day', 'plan rollover', 1),
                ($3, $2, 2, 1, 0, 0, 'current',
                    clock_timestamp(), NULL, NULL, 1),
                ($4, $5, 1, 1, 0, 0, 'current',
                    clock_timestamp(), NULL, NULL, 1);
            """,
            connection,
            transaction))
        {
            period.Parameters.AddWithValue(
                NpgsqlDbType.Uuid,
                historicalPeriodId.Value);
            period.Parameters.AddWithValue(NpgsqlDbType.Uuid, groupId.Value);
            period.Parameters.AddWithValue(NpgsqlDbType.Uuid, periodId.Value);
            period.Parameters.AddWithValue(
                NpgsqlDbType.Uuid,
                foreignPeriodId.Value);
            period.Parameters.AddWithValue(
                NpgsqlDbType.Uuid,
                foreignGroupId.Value);
            Assert.Equal(
                3,
                await period.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false));
        }

        using (NpgsqlCommand events = new(
            """
            INSERT INTO public.group_quota_events (
                id, group_id, period_id, event_type,
                delta_total_tokens, delta_consumed_tokens, delta_reserved_tokens,
                total_tokens_after, consumed_tokens_after, reserved_tokens_after,
                actor_type, idempotency_key, metadata, occurred_at)
            SELECT gen_random_uuid(), $1,
                   CASE WHEN source % 500 = 0 THEN $2 ELSE $3 END,
                   'total_adjusted',
                   0, 0, 0, 1, 0, 0,
                   'system', $4 || source::text, '{}'::jsonb, clock_timestamp()
            FROM generate_series(1, $5) AS source;
            """,
            connection,
            transaction))
        {
            events.Parameters.AddWithValue(NpgsqlDbType.Uuid, groupId.Value);
            events.Parameters.AddWithValue(NpgsqlDbType.Uuid, periodId.Value);
            events.Parameters.AddWithValue(
                NpgsqlDbType.Uuid,
                historicalPeriodId.Value);
            events.Parameters.AddWithValue(
                NpgsqlDbType.Text,
                $"m3e5-plan:{groupId.Value:N}:");
            events.Parameters.AddWithValue(PlanHistoryCount);
            Assert.Equal(
                PlanHistoryCount,
                await events.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false));
        }

        using (NpgsqlCommand foreignEvents = new(
            """
            INSERT INTO public.group_quota_events (
                id, group_id, period_id, event_type,
                delta_total_tokens, delta_consumed_tokens, delta_reserved_tokens,
                total_tokens_after, consumed_tokens_after, reserved_tokens_after,
                actor_type, idempotency_key, metadata, occurred_at)
            SELECT gen_random_uuid(), $1, $2, 'total_adjusted',
                   0, 0, 0, 1, 0, 0,
                   'system', $3 || source::text, '{}'::jsonb, clock_timestamp()
            FROM generate_series(1, $4) AS source;
            """,
            connection,
            transaction))
        {
            foreignEvents.Parameters.AddWithValue(
                NpgsqlDbType.Uuid,
                foreignGroupId.Value);
            foreignEvents.Parameters.AddWithValue(
                NpgsqlDbType.Uuid,
                foreignPeriodId.Value);
            foreignEvents.Parameters.AddWithValue(
                NpgsqlDbType.Text,
                $"m3e5-plan:{foreignGroupId.Value:N}:");
            foreignEvents.Parameters.AddWithValue(ForeignGroupHistoryCount);
            Assert.Equal(
                ForeignGroupHistoryCount,
                await foreignEvents.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false));
        }

        using (NpgsqlCommand analyze = new(
            "ANALYZE public.group_quota_events;",
            connection,
            transaction))
        {
            _ = await analyze.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        using NpgsqlCommand latest = new(
            "SELECT max(event_sequence) FROM public.group_quota_events "
                + "WHERE period_id = $1;",
            connection,
            transaction);
        latest.Parameters.AddWithValue(NpgsqlDbType.Uuid, periodId.Value);
        object? value = await latest.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return Assert.IsType<long>(value);
    }

    private static async ValueTask<IReadOnlyList<PlanNode>> ReadPlanAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        object? raw = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        string json = Convert.ToString(
            raw,
            System.Globalization.CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException("PostgreSQL returned no JSON plan.");
        using JsonDocument document = JsonDocument.Parse(json);
        List<PlanNode> nodes = [];
        CollectPlanNodes(document.RootElement[0].GetProperty("Plan"), nodes);
        return nodes;
    }

    private static void CollectPlanNodes(
        JsonElement plan,
        ICollection<PlanNode> nodes)
    {
        nodes.Add(new PlanNode(
            ReadString(plan, "Relation Name"),
            ReadString(plan, "Index Name"),
            ReadRows(plan, "Actual Rows"),
            ReadRows(plan, "Rows Removed by Filter")
                + ReadRows(plan, "Rows Removed by Index Recheck"),
            ReadRows(plan, "Actual Loops")));
        if (plan.TryGetProperty("Plans", out JsonElement children))
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                CollectPlanNodes(child, nodes);
            }
        }
    }

    private static string ReadSqlConstant(Type owner, string name) =>
        owner.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?
            .GetRawConstantValue() as string
        ?? throw new InvalidOperationException($"Could not read {owner.Name}.{name}.");

    private static string? ReadString(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement property)
            ? property.GetString()
            : null;

    private static long ReadRows(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement property)
            ? (long)Math.Ceiling(property.GetDouble())
            : 0;

    private sealed record PlanNode(
        string? RelationName,
        string? IndexName,
        long ActualRows,
        long RemovedRows,
        long ActualLoops)
    {
        internal bool TouchesQuotaEvents => string.Equals(
            RelationName,
            "group_quota_events",
            StringComparison.Ordinal);

        internal long RowsExamined => (ActualRows + RemovedRows) * ActualLoops;
    }

    private async ValueTask<GroupQuotaReconciliationFactSnapshot?> ReadAsync(
        EntityId groupId,
        EntityId? periodId,
        long checkpointSourceEventSequence,
        CancellationToken cancellationToken)
    {
        IGroupQuotaReconciliationFactReader reader = fixture.ApiServices
            .GetRequiredService<IGroupQuotaReconciliationFactReader>();
        IUnitOfWorkFactory factory = fixture.ApiServices
            .GetRequiredService<IUnitOfWorkFactory>();
        IUnitOfWork unitOfWork = await factory.BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        GroupQuotaReconciliationFactSnapshot? snapshot = await reader.ReadAsync(
            groupId,
            periodId,
            checkpointSourceEventSequence,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    private async ValueTask<EntityId?> ResolvePeriodAsync(
        EntityId groupId,
        EntityId? periodId,
        CancellationToken cancellationToken)
    {
        IGroupQuotaReconciliationFactReader reader = fixture.ApiServices
            .GetRequiredService<IGroupQuotaReconciliationFactReader>();
        IUnitOfWorkFactory factory = fixture.ApiServices
            .GetRequiredService<IUnitOfWorkFactory>();
        IUnitOfWork unitOfWork = await factory.BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        EntityId? resolved = await reader.ResolvePeriodAsync(
            groupId,
            periodId,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return resolved;
    }

    private async ValueTask<IReadOnlyList<GroupQuotaReconciliationCandidate>>
        ListCandidatesAsync(
            EntityId afterGroupId,
            int maximumCount,
            CancellationToken cancellationToken)
    {
        IGroupQuotaReconciliationFactReader reader = fixture.ApiServices
            .GetRequiredService<IGroupQuotaReconciliationFactReader>();
        IUnitOfWorkFactory factory = fixture.ApiServices
            .GetRequiredService<IUnitOfWorkFactory>();
        IUnitOfWork unitOfWork = await factory.BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        IReadOnlyList<GroupQuotaReconciliationCandidate> candidates =
            await reader.ListCurrentCandidatesAsync(
                afterGroupId,
                maximumCount,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return candidates;
    }

    private async ValueTask<IReadOnlyList<long>> ListPeriodSequencesAsync(
        EntityId groupId,
        EntityId periodId,
        long throughSourceEventSequence,
        long afterSourceEventSequence,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        IGroupQuotaReconciliationFactReader reader = fixture.ApiServices
            .GetRequiredService<IGroupQuotaReconciliationFactReader>();
        IUnitOfWorkFactory factory = fixture.ApiServices
            .GetRequiredService<IUnitOfWorkFactory>();
        IUnitOfWork unitOfWork = await factory.BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        IReadOnlyList<long> sourceEventSequences = await reader
            .ListPeriodSourceEventSequencesAsync(
                groupId,
                periodId,
                throughSourceEventSequence,
                afterSourceEventSequence,
                maximumCount,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return sourceEventSequences;
    }

    private async ValueTask<SeededScenario> SeedAsync(
        CancellationToken cancellationToken)
    {
        ReconciliationScenario scenario = ReconciliationScenario.Create();
        using NpgsqlConnection connection = await fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetReplicaRoleAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        await InsertBaseFactsAsync(connection, transaction, scenario, cancellationToken)
            .ConfigureAwait(false);

        long closedEventSequence = await InsertEventAsync(
            connection,
            transaction,
            scenario.ClosedEventId,
            scenario.GroupId,
            scenario.ClosedPeriodId,
            reservationId: null,
            attemptId: null,
            eventType: "period_reset",
            deltaTotal: 500,
            deltaConsumed: 7,
            deltaReserved: 0,
            totalAfter: 500,
            consumedAfter: 7,
            reservedAfter: 0,
            occurredAt: scenario.OpenedAt.AddMinutes(-1),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        long firstSequence = await InsertEventAsync(
            connection,
            transaction,
            scenario.InitializedEventId,
            scenario.GroupId,
            scenario.PeriodId,
            reservationId: null,
            attemptId: null,
            eventType: "initialized",
            deltaTotal: 1000,
            deltaConsumed: 0,
            deltaReserved: 0,
            totalAfter: 1000,
            consumedAfter: 0,
            reservedAfter: 0,
            occurredAt: scenario.OpenedAt,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        _ = await InsertEventAsync(
            connection,
            transaction,
            scenario.ReservedEventId,
            scenario.GroupId,
            scenario.PeriodId,
            scenario.SettledReservationId,
            scenario.SettledAttemptId,
            eventType: "reserved",
            deltaTotal: 0,
            deltaConsumed: 0,
            deltaReserved: 15,
            totalAfter: 1000,
            consumedAfter: 0,
            reservedAfter: 15,
            occurredAt: scenario.OpenedAt.AddMinutes(5),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        long checkpointSequence = await InsertEventAsync(
            connection,
            transaction,
            scenario.SettledEventId,
            scenario.GroupId,
            scenario.PeriodId,
            scenario.SettledReservationId,
            scenario.SettledAttemptId,
            eventType: "settled",
            deltaTotal: 0,
            deltaConsumed: 15,
            deltaReserved: -15,
            totalAfter: 1000,
            consumedAfter: 15,
            reservedAfter: 0,
            occurredAt: scenario.OpenedAt.AddHours(1),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        _ = await InsertEventAsync(
            connection,
            transaction,
            scenario.AdjustedEventId,
            scenario.GroupId,
            scenario.PeriodId,
            scenario.SettledReservationId,
            scenario.SettledAttemptId,
            eventType: "usage_adjusted",
            deltaTotal: 0,
            deltaConsumed: -3,
            deltaReserved: 0,
            totalAfter: 1000,
            consumedAfter: 12,
            reservedAfter: 0,
            occurredAt: scenario.OpenedAt.AddHours(2),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        DateTimeOffset latestOccurredAt = scenario.OpenedAt.AddHours(2).AddMinutes(1);
        long latestSequence = await InsertEventAsync(
            connection,
            transaction,
            scenario.PendingEventId,
            scenario.GroupId,
            scenario.PeriodId,
            scenario.PendingReservationId,
            scenario.PendingAttemptId,
            eventType: "reserved",
            deltaTotal: 0,
            deltaConsumed: 0,
            deltaReserved: 5,
            totalAfter: 1000,
            consumedAfter: 12,
            reservedAfter: 5,
            occurredAt: latestOccurredAt,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        long foreignGroupEventSequence = await InsertEventAsync(
            connection,
            transaction,
            scenario.OtherInitializedEventId,
            scenario.OtherGroupId,
            scenario.OtherPeriodId,
            reservationId: null,
            attemptId: null,
            eventType: "initialized",
            deltaTotal: 100,
            deltaConsumed: 0,
            deltaReserved: 0,
            totalAfter: 100,
            consumedAfter: 0,
            reservedAfter: 0,
            occurredAt: scenario.OpenedAt,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await InsertAdjustmentAsync(connection, transaction, scenario, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SeededScenario(
            scenario.GroupId,
            scenario.PeriodId,
            scenario.OtherGroupId,
            scenario.OtherPeriodId,
            scenario.ClosedPeriodId,
            scenario.ReservedEventId,
            scenario.SettledEventId,
            scenario.PendingLeaseExpiresAt,
            closedEventSequence,
            firstSequence,
            checkpointSequence,
            latestSequence,
            foreignGroupEventSequence,
            latestOccurredAt);
    }

    private static async ValueTask InsertBaseFactsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ReconciliationScenario scenario,
        CancellationToken cancellationToken)
    {
        const string seedSql = """
            INSERT INTO public.groups (id, name, status, created_at, updated_at)
            VALUES
                ($1, 'reconciliation-' || $1::text, 'disabled', $16, $16),
                ($3, 'reconciliation-' || $3::text, 'disabled', $16, $16);

            INSERT INTO public.group_token_quotas (
                group_id, current_period_id, enabled, version, created_at, updated_at)
            VALUES
                ($1, $2, true, 1, $16, $16),
                ($3, $4, true, 1, $16, $16);

            INSERT INTO public.group_quota_periods (
                id, group_id, period_number, total_tokens, consumed_tokens,
                reserved_tokens, status, opened_at, closed_at, reset_reason,
                version, created_at, updated_at)
            VALUES
                ($24, $1, 1, 500, 7, 0, 'closed', $16 - interval '1 day',
                    $16 - interval '1 minute', 'reconciliation test reset',
                    1, $16 - interval '1 day', $16 - interval '1 minute'),
                ($2, $1, 2, 1000, 12, 5, 'current', $16, NULL, NULL,
                    1, $16, $16),
                ($4, $3, 1, 100, 0, 0, 'current', $16, NULL, NULL,
                    1, $16, $16);

            INSERT INTO public.usage_requests (
                request_id, user_id, api_key_id, subscription_id,
                quota_group_id, routing_group_id, endpoint, requested_model,
                effective_model, is_streaming, status, attempt_count,
                final_attempt_id, received_at, completed_at)
            VALUES
                ($5, $13, $14, $15, $1, $1, '/v1/responses', 'test-model',
                    'test-model', false, 'succeeded', 1, $7, $16, $17),
                ($8, $13, $14, $15, $1, $1, '/v1/responses', 'test-model',
                    'test-model', false, 'in_progress', 0, NULL, $18, NULL);

            INSERT INTO public.group_token_reservations (
                id, period_id, group_id, request_id, attempt_id, attempt_index,
                account_id, channel_id, estimated_tokens, actual_tokens, status,
                is_streaming, lease_owner, lease_expires_at, max_expires_at,
                dispatch_started_at, dispatch_provider, dispatch_model,
                estimated_input_tokens, estimated_output_tokens, usage_source,
                settled_at, created_at, updated_at)
            VALUES
                ($6, $2, $1, $5, $7, 0, $11, $12, 15, 15, 'settled', false,
                    'reconciliation-test', $19, $20, $21, 'openai', 'test-model',
                    10, 5, 'upstream', $17, $16, $17),
                ($9, $2, $1, $8, $10, 0, $11, $12, 5, NULL, 'pending', false,
                    'reconciliation-test', $22, $23, NULL, NULL, NULL,
                    NULL, NULL, NULL, NULL, $18, $18);

            INSERT INTO public.usage_attempts (
                attempt_id, request_id, attempt_index, reservation_id,
                quota_group_id, routing_group_id, account_id, channel_id,
                provider, model, status, upstream_http_status,
                input_tokens, output_tokens, cache_read_tokens,
                cache_creation_tokens, thinking_tokens, usage_source,
                is_estimated, dispatch_started_at, first_token_at,
                completed_at, created_at)
            VALUES (
                $7, $5, 0, $6, $1, $1, $11, $12,
                'openai', 'test-model', 'succeeded', 200,
                10, 5, 0, 0, 0, 'upstream', false,
                $21, $21, $17, $17);
            """;
        int affected = 0;
        foreach (string statement in seedSql.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using NpgsqlCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = statement;
            AddBaseParameters(command, scenario, ParameterCount(statement));
            affected += await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        Assert.Equal(12, affected);
    }

    private static void AddBaseParameters(
        NpgsqlCommand command,
        ReconciliationScenario scenario,
        int parameterCount)
    {
        object[] values =
        [
            scenario.GroupId.Value,
            scenario.PeriodId.Value,
            scenario.OtherGroupId.Value,
            scenario.OtherPeriodId.Value,
            scenario.SettledRequestId.Value,
            scenario.SettledReservationId.Value,
            scenario.SettledAttemptId.Value,
            scenario.PendingRequestId.Value,
            scenario.PendingReservationId.Value,
            scenario.PendingAttemptId.Value,
            scenario.AccountId.Value,
            scenario.ChannelId.Value,
            scenario.UserId.Value,
            scenario.ApiKeyId.Value,
            scenario.SubscriptionId.Value,
            scenario.OpenedAt,
            scenario.OpenedAt.AddHours(1),
            scenario.OpenedAt.AddHours(2),
            scenario.OpenedAt.AddMinutes(30),
            scenario.OpenedAt.AddHours(3),
            scenario.OpenedAt.AddMinutes(10),
            scenario.PendingLeaseExpiresAt,
            scenario.OpenedAt.AddHours(6),
            scenario.ClosedPeriodId.Value,
        ];
        foreach (object value in values.Take(parameterCount))
        {
            command.Parameters.AddWithValue(value);
        }
    }

    private static int ParameterCount(string statement)
    {
        for (int candidate = 24; candidate > 0; candidate--)
        {
            if (statement.Contains($"${candidate}", StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return 0;
    }

    private static async ValueTask<long> InsertEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EntityId eventId,
        EntityId groupId,
        EntityId periodId,
        EntityId? reservationId,
        EntityId? attemptId,
        string eventType,
        long deltaTotal,
        long deltaConsumed,
        long deltaReserved,
        long totalAfter,
        long consumedAfter,
        long reservedAfter,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO public.group_quota_events (
                id, group_id, period_id, reservation_id, attempt_id,
                event_type, delta_total_tokens, delta_consumed_tokens,
                delta_reserved_tokens, total_tokens_after,
                consumed_tokens_after, reserved_tokens_after, actor_type,
                idempotency_key, metadata, occurred_at)
            VALUES (
                $1, $2, $3, $4, $5,
                $6, $7, $8, $9, $10, $11, $12,
                'system', $13, '{}'::jsonb, $14)
            RETURNING event_sequence;
            """;
        command.Parameters.AddWithValue(eventId.Value);
        command.Parameters.AddWithValue(groupId.Value);
        command.Parameters.AddWithValue(periodId.Value);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Uuid,
            Value = reservationId is { } reservation
                ? reservation.Value
                : DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Uuid,
            Value = attemptId is { } attempt ? attempt.Value : DBNull.Value,
        });
        command.Parameters.AddWithValue(eventType);
        command.Parameters.AddWithValue(deltaTotal);
        command.Parameters.AddWithValue(deltaConsumed);
        command.Parameters.AddWithValue(deltaReserved);
        command.Parameters.AddWithValue(totalAfter);
        command.Parameters.AddWithValue(consumedAfter);
        command.Parameters.AddWithValue(reservedAfter);
        command.Parameters.AddWithValue($"reconciliation:{eventId.Value:N}");
        command.Parameters.AddWithValue(occurredAt);
        return Assert.IsType<long>(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static async ValueTask InsertAdjustmentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ReconciliationScenario scenario,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO public.usage_attempt_adjustments (
                attempt_id, quota_event_id, previous_total_tokens,
                corrected_input_tokens, corrected_output_tokens,
                corrected_cache_read_tokens, corrected_cache_creation_tokens,
                corrected_thinking_tokens, usage_source, reason, adjusted_at)
            VALUES ($1, $2, 15, 8, 4, 0, 0, 0, 'upstream',
                'reconciliation test correction', $3);
            """;
        command.Parameters.AddWithValue(scenario.SettledAttemptId.Value);
        command.Parameters.AddWithValue(scenario.AdjustedEventId.Value);
        command.Parameters.AddWithValue(scenario.OpenedAt.AddHours(2));
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask CorruptMiddleDeltaAsync(
        SeededScenario scenario,
        CancellationToken cancellationToken)
    {
        using NpgsqlConnection connection = await fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetReplicaRoleAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE public.group_quota_events
            SET delta_reserved_tokens = 14
            WHERE id = $1;
            """;
        command.Parameters.AddWithValue(scenario.ReservedEventId.Value);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask CorruptLedgerAsync(
        SeededScenario scenario,
        CancellationToken cancellationToken)
    {
        using NpgsqlConnection connection = await fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetReplicaRoleAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE public.group_quota_periods
            SET reserved_tokens = 6
            WHERE id = $1;
            """;
        command.Parameters.AddWithValue(scenario.PeriodId.Value);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask CorruptTerminalEventTypeAsync(
        SeededScenario scenario,
        CancellationToken cancellationToken)
    {
        using NpgsqlConnection connection = await fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetReplicaRoleAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE public.group_quota_events
            SET event_type = 'renewed'
            WHERE id = $1;
            """;
        command.Parameters.AddWithValue(scenario.SettledEventId.Value);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask SetReplicaRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SET LOCAL session_replication_role = replica;";
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static int Compare(EntityId left, EntityId right) =>
        StringComparer.Ordinal.Compare(
            left.Value.ToString("N"),
            right.Value.ToString("N"));

    private sealed record ReconciliationScenario(
        EntityId GroupId,
        EntityId PeriodId,
        EntityId OtherGroupId,
        EntityId OtherPeriodId,
        EntityId SettledRequestId,
        EntityId SettledReservationId,
        EntityId SettledAttemptId,
        EntityId PendingRequestId,
        EntityId PendingReservationId,
        EntityId PendingAttemptId,
        EntityId AccountId,
        EntityId ChannelId,
        EntityId UserId,
        EntityId ApiKeyId,
        EntityId SubscriptionId,
        EntityId InitializedEventId,
        EntityId ReservedEventId,
        EntityId SettledEventId,
        EntityId AdjustedEventId,
        EntityId PendingEventId,
        EntityId OtherInitializedEventId,
        DateTimeOffset OpenedAt,
        DateTimeOffset PendingLeaseExpiresAt,
        EntityId ClosedPeriodId,
        EntityId ClosedEventId)
    {
        public static ReconciliationScenario Create()
        {
            EntityId firstGroup = EntityId.New();
            EntityId secondGroup = EntityId.New();
            if (Compare(firstGroup, secondGroup) > 0)
            {
                (firstGroup, secondGroup) = (secondGroup, firstGroup);
            }

            DateTimeOffset openedAt = TimeProvider.System.GetUtcNow().AddHours(-4);
            openedAt = new DateTimeOffset(
                openedAt.Ticks - (openedAt.Ticks % 10),
                TimeSpan.Zero);
            return new ReconciliationScenario(
                firstGroup,
                EntityId.New(),
                secondGroup,
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                openedAt,
                openedAt.AddHours(3),
                EntityId.New(),
                EntityId.New());
        }
    }

    private sealed record SeededScenario(
        EntityId GroupId,
        EntityId PeriodId,
        EntityId OtherGroupId,
        EntityId OtherPeriodId,
        EntityId ClosedPeriodId,
        EntityId ReservedEventId,
        EntityId SettledEventId,
        DateTimeOffset PendingLeaseExpiresAt,
        long ClosedEventSequence,
        long FirstSequence,
        long CheckpointSequence,
        long LatestSequence,
        long ForeignGroupEventSequence,
        DateTimeOffset LatestOccurredAt);
}
#pragma warning restore MA0051
