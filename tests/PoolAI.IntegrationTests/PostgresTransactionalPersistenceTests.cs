using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class PostgresTransactionalPersistenceTests
{
    private static readonly Guid GroupCreateActorId =
        Guid.Parse("01900000-0000-7000-8000-0000000007f0");
    private static readonly Guid GroupCreateActorStamp =
        Guid.Parse("01900000-0000-7000-8000-0000000007f1");
    private readonly PostgresRuntimeFixture _fixture;

    public PostgresTransactionalPersistenceTests(PostgresRuntimeFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ApiRuntimeUnitOfWorkRollsBackWithoutCommitAndCommitsOnlyOnce()
    {
        // Governing contract: docs/architecture/design-pattern-baseline.md section 6.3.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        IUnitOfWorkFactory factory = _fixture.ApiServices
            .GetRequiredService<IUnitOfWorkFactory>();
        EntityId rolledBackGroupId = EntityId.New();
        IUnitOfWork rolledBack = await factory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(true);
        PostgresTransactionSession rollbackSession = PostgresUnitOfWorkAccessor.Require(
            rolledBack.Context);
        using (NpgsqlCommand role = rollbackSession.CreateCommand("SELECT current_user;"))
        {
            Assert.Equal(
                "poolai_api",
                Assert.IsType<string>(await role
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(true)));
        }

        await InsertDisabledGroupAsync(
            rolledBackGroupId,
            rollbackSession,
            cancellationToken).ConfigureAwait(true);
        await rolledBack.DisposeAsync().ConfigureAwait(true);
        await rolledBack.DisposeAsync().ConfigureAwait(true);
        Assert.False(await GroupExistsAsync(
            rolledBackGroupId,
            cancellationToken).ConfigureAwait(true));
        Assert.Throws<ObjectDisposedException>(
            () => PostgresUnitOfWorkAccessor.Require(rolledBack.Context));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => rolledBack.CommitAsync(cancellationToken).AsTask()).ConfigureAwait(true);

        EntityId committedGroupId = EntityId.New();
        IUnitOfWork committed = await factory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable committedLease =
            committed.ConfigureAwait(true);
        await InsertDisabledGroupAsync(
            committedGroupId,
            PostgresUnitOfWorkAccessor.Require(committed.Context),
            cancellationToken).ConfigureAwait(true);
        await committed.CommitAsync(cancellationToken).ConfigureAwait(true);

        Assert.True(await GroupExistsAsync(
            committedGroupId,
            cancellationToken).ConfigureAwait(true));
        Assert.Throws<ObjectDisposedException>(
            () => PostgresUnitOfWorkAccessor.Require(committed.Context));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => committed.CommitAsync(cancellationToken).AsTask()).ConfigureAwait(true);
    }

    [Theory]
    [InlineData(TransactionFaultPoint.AfterAcquire)]
    [InlineData(TransactionFaultPoint.AfterBusinessWrite)]
    [InlineData(TransactionFaultPoint.AfterAudit)]
    [InlineData(TransactionFaultPoint.AfterOutbox)]
    [InlineData(TransactionFaultPoint.AfterComplete)]
    [InlineData(TransactionFaultPoint.BeforeCommit)]
    [Trait("Category", "PostgreSQL")]
    public async Task EveryPreCommitFaultRollsBackBusinessIdempotencyAuditAndOutbox(
        TransactionFaultPoint faultPoint)
    {
        // Governing contract: AC-040 requires all four facts to share one commit point.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CommandScenario scenario = CommandScenario.Create();

        await Assert.ThrowsAsync<InjectedTransactionFaultException>(() => ExecuteCommandAsync(
            scenario,
            faultPoint,
            cancellationToken).AsTask()).ConfigureAwait(true);

        CommandCounts counts = await ReadCommandCountsAsync(
            scenario,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(CommandCounts.Empty, counts);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task CommitThenResponseLossReplaysOriginalResponseAndDifferentHashConflicts()
    {
        // Governing contract: AC-040 and docs/database/README.md section 5.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CommandScenario scenario = CommandScenario.Create();
        CommandIdempotencyResponse original = await ExecuteCommandAsync(
            scenario,
            null,
            cancellationToken).ConfigureAwait(true);

        CommandCounts committed = await ReadCommandCountsAsync(
            scenario,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(new CommandCounts(1, 1, 1, 1, "completed"), committed);

        CommandIdempotencyAcquireResult replay = await AcquireAndCommitAsync(
            scenario.Request(EntityId.New(), scenario.RequestHash),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(CommandIdempotencyDisposition.Replay, replay.Disposition);
        CommandIdempotencyResponse replayed = Assert.IsType<CommandIdempotencyResponse>(
            replay.Response);
        Assert.Equal(original.TerminalStatus, replayed.TerminalStatus);
        Assert.Equal(original.Status, replayed.Status);
        Assert.Equal(
            original.Body?.GetProperty("group_id").GetString(),
            replayed.Body?.GetProperty("group_id").GetString());
        Assert.Equal(
            original.Headers.GetProperty("Location").GetString(),
            replayed.Headers.GetProperty("Location").GetString());
        Assert.Equal(
            original.Headers.GetProperty("ETag").GetString(),
            replayed.Headers.GetProperty("ETag").GetString());

        byte[] conflictingHash = SHA256.HashData("different-command"u8);
        CommandIdempotencyAcquireResult conflict = await AcquireAndCommitAsync(
            scenario.Request(EntityId.New(), conflictingHash),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(CommandIdempotencyDisposition.Conflict, conflict.Disposition);
        Assert.Equal(
            committed,
            await ReadCommandCountsAsync(scenario, cancellationToken).ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task PersistenceStoresRejectForeignUnitOfWorkContexts()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ICommandIdempotencyStore store = _fixture.ApiServices
            .GetRequiredService<ICommandIdempotencyStore>();
        CommandScenario scenario = CommandScenario.Create();

        await Assert.ThrowsAsync<ArgumentException>(() => store.AcquireAsync(
            scenario.Request(EntityId.New(), scenario.RequestHash),
            new ForeignUnitOfWorkContext(),
            cancellationToken).AsTask()).ConfigureAwait(true);
    }

    private async ValueTask<CommandIdempotencyResponse> ExecuteCommandAsync(
        CommandScenario scenario,
        TransactionFaultPoint? faultPoint,
        CancellationToken cancellationToken)
    {
        IUnitOfWorkFactory factory = _fixture.ApiServices
            .GetRequiredService<IUnitOfWorkFactory>();
        ICommandIdempotencyStore idempotency = _fixture.ApiServices
            .GetRequiredService<ICommandIdempotencyStore>();
        IAuditAppender audit = _fixture.ApiServices.GetRequiredService<IAuditAppender>();
        IOutboxAppender outbox = _fixture.ApiServices.GetRequiredService<IOutboxAppender>();

        IUnitOfWork unitOfWork = await factory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        CommandIdempotencyAcquireResult acquired = await idempotency.AcquireAsync(
            scenario.Request(scenario.OwnerId, scenario.RequestHash),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(CommandIdempotencyDisposition.Acquired, acquired.Disposition);
        CommandIdempotencyLease lease = Assert.IsType<CommandIdempotencyLease>(acquired.Lease);
        ThrowAt(TransactionFaultPoint.AfterAcquire, faultPoint);

        await InsertDisabledGroupAsync(
            scenario.GroupId,
            PostgresUnitOfWorkAccessor.Require(unitOfWork.Context),
            cancellationToken).ConfigureAwait(false);
        ThrowAt(TransactionFaultPoint.AfterBusinessWrite, faultPoint);

        await audit.AppendAsync(
            scenario.Audit,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        ThrowAt(TransactionFaultPoint.AfterAudit, faultPoint);

        await outbox.AppendAsync(
            scenario.Event,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        ThrowAt(TransactionFaultPoint.AfterOutbox, faultPoint);

        CommandIdempotencyResponse response = scenario.Response;
        bool completed = await idempotency.CompleteAsync(
            new CommandIdempotencyCompletion(
                lease,
                response.TerminalStatus,
                response.Status,
                response.Body,
                response.BodyEnvelope,
                response.Headers,
                response.ResourceType,
                response.ResourceId),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Assert.True(completed);
        ThrowAt(TransactionFaultPoint.AfterComplete, faultPoint);
        ThrowAt(TransactionFaultPoint.BeforeCommit, faultPoint);

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return response;
    }

    private async ValueTask<CommandIdempotencyAcquireResult> AcquireAndCommitAsync(
        CommandIdempotencyRequest request,
        CancellationToken cancellationToken)
    {
        IUnitOfWorkFactory factory = _fixture.ApiServices
            .GetRequiredService<IUnitOfWorkFactory>();
        ICommandIdempotencyStore store = _fixture.ApiServices
            .GetRequiredService<ICommandIdempotencyStore>();
        IUnitOfWork unitOfWork = await factory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        CommandIdempotencyAcquireResult result = await store.AcquireAsync(
            request,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async ValueTask InsertDisabledGroupAsync(
        EntityId groupId,
        PostgresTransactionSession session,
        CancellationToken cancellationToken)
    {
        using (NpgsqlCommand actor = session.CreateCommand("""
                   INSERT INTO public.users (
                       id, email, normalized_email, display_name,
                       password_hash, security_stamp
                   ) VALUES (
                       $1, 'm1e4-transaction-probe@example.test',
                       'm1e4-transaction-probe@example.test',
                       'M1-E4 Transaction Probe', 'poolai-password-v1:test', $2
                   )
                   ON CONFLICT DO NOTHING;
                   """))
        {
            actor.Parameters.AddWithValue(GroupCreateActorId);
            actor.Parameters.AddWithValue(GroupCreateActorStamp);
            _ = await actor.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using NpgsqlCommand command = session.CreateCommand("""
            SELECT disposition
            FROM public.poolai_group_create(
                $1, $2, NULL, $3, 1000, $4, $5, $6, $7,
                'integration transaction probe');
            """);
        command.Parameters.AddWithValue(groupId.Value);
        command.Parameters.AddWithValue($"integration-{groupId.Value:N}");
        command.Parameters.AddWithValue(Guid.CreateVersion7());
        command.Parameters.AddWithValue(GroupCreateActorId);
        command.Parameters.AddWithValue(Guid.CreateVersion7());
        command.Parameters.AddWithValue(Guid.CreateVersion7());
        command.Parameters.AddWithValue($"integration:group:create:{groupId.Value:N}");
        Assert.Equal(
            "created",
            Assert.IsType<string>(await command
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));
    }

    private async ValueTask<bool> GroupExistsAsync(
        EntityId groupId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM public.groups WHERE id = $1);");
        command.Parameters.AddWithValue(groupId.Value);
        return Assert.IsType<bool>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private async ValueTask<CommandCounts> ReadCommandCountsAsync(
        CommandScenario scenario,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT
                (SELECT count(*)::integer FROM public.groups WHERE id = $1),
                (SELECT count(*)::integer FROM public.audit_logs WHERE id = $2),
                (SELECT count(*)::integer FROM public.outbox_messages WHERE id = $3),
                (SELECT count(*)::integer FROM public.idempotency_records
                    WHERE scope = $4 AND idempotency_key = $5),
                (SELECT status FROM public.idempotency_records
                    WHERE scope = $4 AND idempotency_key = $5);
            """);
        command.Parameters.AddWithValue(scenario.GroupId.Value);
        command.Parameters.AddWithValue(scenario.AuditId.Value);
        command.Parameters.AddWithValue(scenario.MessageId.Value);
        command.Parameters.AddWithValue(scenario.Scope);
        command.Parameters.AddWithValue(scenario.Key);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return new CommandCounts(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private static void ThrowAt(
        TransactionFaultPoint expected,
        TransactionFaultPoint? actual)
    {
        if (actual == expected)
        {
            throw new InjectedTransactionFaultException(expected);
        }
    }

    public enum TransactionFaultPoint
    {
        AfterAcquire,
        AfterBusinessWrite,
        AfterAudit,
        AfterOutbox,
        AfterComplete,
        BeforeCommit,
    }

    private sealed class InjectedTransactionFaultException(TransactionFaultPoint faultPoint)
        : Exception($"Injected transaction fault at {faultPoint}.");

    private sealed class ForeignUnitOfWorkContext : IUnitOfWorkContext;

    private sealed record CommandScenario(
        string Scope,
        string Key,
        byte[] RequestHash,
        EntityId RecordId,
        EntityId OwnerId,
        EntityId GroupId,
        EntityId AuditId,
        EntityId MessageId,
        AuditEntry Audit,
        IntegrationEvent Event,
        CommandIdempotencyResponse Response)
    {
        public static CommandScenario Create()
        {
            EntityId groupId = EntityId.New();
            EntityId auditId = EntityId.New();
            EntityId messageId = EntityId.New();
            string scope = "integration.group.create";
            JsonElement payload = CreatePayload(groupId);
            return new CommandScenario(
                scope,
                EntityId.New().ToString(),
                SHA256.HashData(Encoding.UTF8.GetBytes($"{scope}:{groupId}")),
                EntityId.New(),
                EntityId.New(),
                groupId,
                auditId,
                messageId,
                CreateAuditEntry(auditId, groupId, scope, payload),
                CreateIntegrationEvent(messageId, groupId, payload),
                CreateResponse(groupId, payload));
        }

        private static JsonElement CreatePayload(EntityId groupId) =>
            JsonSerializer.SerializeToElement(new
            {
                group_id = groupId.ToString(),
                status = "disabled",
            });

        private static AuditEntry CreateAuditEntry(
            EntityId auditId,
            EntityId groupId,
            string scope,
            JsonElement payload) => new(
                auditId,
                AuditActorType.Service,
                null,
                "group.created",
                "group",
                groupId,
                null,
                "integration transaction probe",
                null,
                "PoolAI.IntegrationTests",
                null,
                payload,
                JsonSerializer.SerializeToElement(new { idempotency_scope = scope }));

        private static IntegrationEvent CreateIntegrationEvent(
            EntityId messageId,
            EntityId groupId,
            JsonElement payload) => new(
                messageId,
                $"integration:group:{groupId}",
                "integration.persistence.v1",
                1,
                "group",
                groupId,
                1,
                "group.created",
                null,
                EntityId.New(),
                null,
                payload,
                TimeProvider.System.GetUtcNow());

        private static CommandIdempotencyResponse CreateResponse(
            EntityId groupId,
            JsonElement payload) => new(
                CommandIdempotencyTerminalStatus.Completed,
                201,
                payload,
                null,
                CreateResponseHeaders(groupId),
                "group",
                groupId);

        private static JsonElement CreateResponseHeaders(EntityId groupId) =>
            JsonSerializer.SerializeToElement(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Location"] = $"/v1/admin/groups/{groupId}",
                    ["ETag"] = "\"1\"",
                });

        public CommandIdempotencyRequest Request(EntityId owner, ReadOnlyMemory<byte> hash) => new(
            Scope,
            Key,
            RecordId,
            "service:integration-tests",
            hash,
            owner,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromHours(1));
    }

    private sealed record CommandCounts(
        int Groups,
        int Audits,
        int OutboxMessages,
        int IdempotencyRecords,
        string? IdempotencyStatus)
    {
        public static CommandCounts Empty { get; } = new(0, 0, 0, 0, null);
    }
}
