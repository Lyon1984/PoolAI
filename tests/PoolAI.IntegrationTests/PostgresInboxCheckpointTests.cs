using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Usage.Abstractions;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class PostgresInboxCheckpointTests
{
    private readonly PostgresRuntimeFixture _fixture;

    public PostgresInboxCheckpointTests(PostgresRuntimeFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task InboxReceiptAndCheckpointAdvanceCommitOrRollBackTogether()
    {
        // Governing contract: docs/database/README.md section 8 and AC-041.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        InboxDependencies dependencies = ResolveDependencies();
        InboxScenario scenario = InboxScenario.Create();
        UsageAggregationLease lease = await ClaimCheckpointAsync(
            dependencies,
            scenario,
            cancellationToken).ConfigureAwait(true);
        await AssertRollbackAsync(dependencies, scenario, lease, cancellationToken)
            .ConfigureAwait(true);
        UsageAggregationLease committedLease = await CommitReceiptAndCheckpointAsync(
            dependencies,
            scenario,
            lease,
            cancellationToken).ConfigureAwait(true);
        await AssertCommittedStateAsync(scenario, lease, committedLease, cancellationToken)
            .ConfigureAwait(true);
        await AssertInboxConflictSemanticsAsync(scenario, cancellationToken)
            .ConfigureAwait(true);
    }

    private InboxDependencies ResolveDependencies() => new(
        _fixture.WorkerServices.GetRequiredService<IUnitOfWorkFactory>(),
        _fixture.WorkerServices.GetRequiredService<IInboxReceiptAppender>(),
        _fixture.WorkerServices.GetRequiredService<IUsageAggregationCheckpoint>());

    private static async ValueTask<UsageAggregationLease> ClaimCheckpointAsync(
        InboxDependencies dependencies,
        InboxScenario scenario,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await dependencies.Factory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        UsageAggregationClaimResult claimed = await dependencies.Checkpoint.ClaimAsync(
            scenario.ClaimRequest,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(UsageAggregationClaimDisposition.Acquired, claimed.Disposition);
        UsageAggregationLease lease = Assert.IsType<UsageAggregationLease>(claimed.Lease);
        Assert.Equal(0, lease.LastEventSequence);
        Assert.Equal(1, lease.Version);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return lease;
    }

    private async ValueTask AssertRollbackAsync(
        InboxDependencies dependencies,
        InboxScenario scenario,
        UsageAggregationLease lease,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await dependencies.Factory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (unitOfWork.ConfigureAwait(false))
        {
            InboxReceiptAppendResult inserted = await dependencies.Inbox.AppendAsync(
                scenario.Receipt,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            Assert.Equal(InboxReceiptDisposition.Inserted, inserted.Disposition);
            UsageAggregationLease? advanced = await dependencies.Checkpoint.AdvanceAsync(
                new UsageAggregationAdvanceRequest(
                    lease,
                    scenario.EventSequence,
                    scenario.CompletedThrough),
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            Assert.NotNull(advanced);
        }

        Assert.False(await InboxReceiptExistsAsync(scenario.Receipt, cancellationToken)
            .ConfigureAwait(false));
        WatermarkState state = await ReadWatermarkAsync(
            scenario.Projector,
            scenario.Partition,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(new WatermarkState(0, null, lease.Version), state);
    }

    private static async ValueTask<UsageAggregationLease> CommitReceiptAndCheckpointAsync(
        InboxDependencies dependencies,
        InboxScenario scenario,
        UsageAggregationLease lease,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await dependencies.Factory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        InboxReceiptAppendResult inserted = await dependencies.Inbox.AppendAsync(
            scenario.Receipt,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(InboxReceiptDisposition.Inserted, inserted.Disposition);
        UsageAggregationLease committed = Assert.IsType<UsageAggregationLease>(
            await dependencies.Checkpoint.AdvanceAsync(
                new UsageAggregationAdvanceRequest(
                    lease,
                    scenario.EventSequence,
                    scenario.CompletedThrough),
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false));
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return committed;
    }

    private async ValueTask AssertCommittedStateAsync(
        InboxScenario scenario,
        UsageAggregationLease lease,
        UsageAggregationLease committedLease,
        CancellationToken cancellationToken)
    {
        Assert.True(await InboxReceiptExistsAsync(scenario.Receipt, cancellationToken)
            .ConfigureAwait(false));
        WatermarkState state = await ReadWatermarkAsync(
            scenario.Projector,
            scenario.Partition,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(scenario.EventSequence, state.LastEventSequence);
        Assert.Equal(lease.Version + 1, state.Version);
        Assert.Equal(committedLease.Version, state.Version);
        Assert.Equal(scenario.CompletedThrough, state.CompletedThrough);
    }

    private async ValueTask AssertInboxConflictSemanticsAsync(
        InboxScenario scenario,
        CancellationToken cancellationToken)
    {
        Assert.Equal(
            InboxReceiptDisposition.Duplicate,
            await AppendAndCommitAsync(scenario.Receipt, cancellationToken).ConfigureAwait(false));
        Assert.Equal(
            InboxReceiptDisposition.MessageConflict,
            await AppendAndCommitAsync(
                scenario.Receipt with { PayloadHash = SHA256.HashData("different-event"u8) },
                cancellationToken).ConfigureAwait(false));
        Assert.Equal(
            InboxReceiptDisposition.SequenceConflict,
            await AppendAndCommitAsync(
                scenario.Receipt with { MessageId = EntityId.New() },
                cancellationToken).ConfigureAwait(false));
        WatermarkState state = await ReadWatermarkAsync(
            scenario.Projector,
            scenario.Partition,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(scenario.EventSequence, state.LastEventSequence);
    }

    private async ValueTask<InboxReceiptDisposition> AppendAndCommitAsync(
        InboxReceipt receipt,
        CancellationToken cancellationToken)
    {
        IUnitOfWorkFactory factory = _fixture.WorkerServices
            .GetRequiredService<IUnitOfWorkFactory>();
        IInboxReceiptAppender inbox = _fixture.WorkerServices
            .GetRequiredService<IInboxReceiptAppender>();
        IUnitOfWork unitOfWork = await factory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        InboxReceiptAppendResult result = await inbox.AppendAsync(
            receipt,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result.Disposition;
    }

    private async ValueTask<bool> InboxReceiptExistsAsync(
        InboxReceipt receipt,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT EXISTS (
                SELECT 1
                FROM public.inbox_messages
                WHERE consumer_name = $1 AND message_id = $2
            );
            """);
        command.Parameters.AddWithValue(receipt.ConsumerName);
        command.Parameters.AddWithValue(receipt.MessageId.Value);
        return Assert.IsType<bool>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private async ValueTask<WatermarkState> ReadWatermarkAsync(
        string projector,
        string partition,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT last_event_sequence, completed_through, version
            FROM public.aggregation_watermarks
            WHERE projector_name = $1 AND partition_key = $2;
            """);
        command.Parameters.AddWithValue(projector);
        command.Parameters.AddWithValue(partition);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return new WatermarkState(
            reader.GetInt64(0),
            reader.IsDBNull(1)
                ? null
                : new DateTimeOffset(reader.GetFieldValue<DateTime>(1).ToUniversalTime()),
            reader.GetInt64(2));
    }

    private sealed record WatermarkState(
        long LastEventSequence,
        DateTimeOffset? CompletedThrough,
        long Version);

    private sealed record InboxDependencies(
        IUnitOfWorkFactory Factory,
        IInboxReceiptAppender Inbox,
        IUsageAggregationCheckpoint Checkpoint);

    private sealed record InboxScenario(
        string Projector,
        string Partition,
        long EventSequence,
        DateTimeOffset CompletedThrough,
        UsageAggregationClaimRequest ClaimRequest,
        InboxReceipt Receipt)
    {
        public static InboxScenario Create()
        {
            string projector = $"integration-usage-{EntityId.New()}";
            string partition = $"partition-{EntityId.New()}";
            // Outbox event_sequence is global across topics and is not transactional,
            // so a Usage projector must accept a strictly increasing sequence with gaps.
            const long eventSequence = 42;
            DateTimeOffset completedThrough = DateTimeOffset.FromUnixTimeSeconds(
                TimeProvider.System.GetUtcNow().ToUnixTimeSeconds() - 60);
            return new InboxScenario(
                projector,
                partition,
                eventSequence,
                completedThrough,
                new UsageAggregationClaimRequest(
                    projector,
                    partition,
                    $"worker-{EntityId.New()}",
                    TimeSpan.FromMinutes(5)),
                new InboxReceipt(
                    $"{projector}:v1",
                    EntityId.New(),
                    "usage.attempt.completed.v1",
                    eventSequence,
                    1,
                    SHA256.HashData("canonical-usage-event"u8)));
        }
    }
}
