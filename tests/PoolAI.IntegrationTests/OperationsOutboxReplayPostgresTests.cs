#pragma warning disable MA0051 // The transactional evidence is clearest in one acceptance flow.
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Application;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class OperationsOutboxReplayPostgresTests(PostgresRuntimeFixture fixture)
{
    private readonly PostgresRuntimeFixture _fixture = fixture
        ?? throw new ArgumentNullException(nameof(fixture));

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task AdminReplayApplicationCreatesAuditedReplacementAndReplaysIdempotently()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        EntityId actorId = EntityId.New();
        EntityId sourceMessageId = EntityId.New();
        EntityId requestId = EntityId.New();
        string key = $"m3-e4-integration-{Guid.CreateVersion7():N}";
        const string reason = "poison message remediated and replay reviewed";
        await SeedDeadSourceAsync(
            actorId,
            sourceMessageId,
            cancellationToken).ConfigureAwait(true);
        IReplayDeadOutboxUseCase useCase = _fixture.ApiServices
            .GetRequiredService<IReplayDeadOutboxUseCase>();
        ReplayDeadOutboxCommand command = new(
            requestId,
            new OutboxReplayActor(
                actorId,
                OperationsControlRole.Admin,
                TokenVersion: 7),
            key,
            sourceMessageId,
            reason,
            "192.0.2.30",
            "sha256:integration-test");

        Result<OutboxReplayOutcome> created = await useCase.ExecuteAsync(
            command,
            cancellationToken).ConfigureAwait(true);
        Result<OutboxReplayOutcome> replayed = await useCase.ExecuteAsync(
            command,
            cancellationToken).ConfigureAwait(true);

        Assert.True(created.IsSuccess);
        Assert.False(created.Value.IsReplay);
        Assert.True(created.Value.EventSequence > 0);
        Assert.Equal(sourceMessageId, created.Value.ReplayOf);
        Assert.True(replayed.IsSuccess);
        Assert.True(replayed.Value.IsReplay);
        Assert.Equal(created.Value.MessageId, replayed.Value.MessageId);
        Assert.Equal(created.Value.EventSequence, replayed.Value.EventSequence);
        Assert.Equal(created.Value.ReplayOf, replayed.Value.ReplayOf);

        ReplayEvidence evidence = await ReadEvidenceAsync(
            created.Value,
            sourceMessageId,
            requestId,
            key,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(1, evidence.ReplacementCount);
        Assert.Equal("pending", evidence.ReplacementStatus);
        Assert.Equal(sourceMessageId.Value, evidence.ReplayOf);
        Assert.Equal("dead", evidence.SourceStatus);
        Assert.Equal("m3-e4 integration poison probe", evidence.SourceLastError);
        Assert.True(evidence.EnvelopePreserved);
        Assert.Equal(1, evidence.AuditCount);
        Assert.Equal("outbox.dead.replayed", evidence.AuditAction);
        Assert.Equal("outbox_message", evidence.AuditTargetType);
        Assert.Equal(reason, evidence.AuditReason);
        Assert.False(evidence.AuditMetadataContainsPayload);
        Assert.Equal(sourceMessageId.Value.ToString("D"), evidence.BeforeMessageId);
        Assert.Equal(created.Value.MessageId.Value.ToString("D"), evidence.AfterMessageId);
        Assert.Equal(
            created.Value.EventSequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            evidence.AfterEventSequence);
        Assert.Equal(1, evidence.IdempotencyCount);
        Assert.Equal("completed", evidence.IdempotencyStatus);
        Assert.Equal(202, evidence.IdempotencyResponseStatus);
        Assert.Equal(created.Value.MessageId.Value, evidence.IdempotencyResourceId);
        Assert.False(evidence.IdempotencyContainsSensitiveFields);
        Assert.True(evidence.EmptyResponseHeaders);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ReplayFunctionIsApiOnlyAndSensitiveOutboxColumnsRemainUnreadable()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        NpgsqlDataSource api = _fixture.ApiServices.GetRequiredService<NpgsqlDataSource>();
        NpgsqlDataSource worker = _fixture.WorkerServices.GetRequiredService<NpgsqlDataSource>();
        using (NpgsqlCommand allowed = api.CreateCommand("""
                   SELECT disposition
                   FROM public.poolai_operations_replay_dead_outbox($1, $2, $3);
                   """))
        {
            allowed.Parameters.AddWithValue(Guid.CreateVersion7());
            allowed.Parameters.AddWithValue(Guid.CreateVersion7());
            allowed.Parameters.AddWithValue("m3-e4-api-permission-probe");
            Assert.Equal(
                "source_not_found",
                Assert.IsType<string>(await allowed
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(true)));
        }

        using (NpgsqlCommand denied = worker.CreateCommand("""
                   SELECT disposition
                   FROM public.poolai_operations_replay_dead_outbox($1, $2, $3);
                   """))
        {
            denied.Parameters.AddWithValue(Guid.CreateVersion7());
            denied.Parameters.AddWithValue(Guid.CreateVersion7());
            denied.Parameters.AddWithValue("m3-e4-worker-permission-probe");
            PostgresException error = await Assert.ThrowsAsync<PostgresException>(
                () => denied.ExecuteScalarAsync(cancellationToken))
                .ConfigureAwait(true);
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }

        using (NpgsqlCommand denied = api.CreateCommand("""
                   SELECT payload, last_error
                   FROM public.outbox_messages
                   WHERE false;
                   """))
        {
            PostgresException error = await Assert.ThrowsAsync<PostgresException>(
                () => denied.ExecuteReaderAsync(cancellationToken))
                .ConfigureAwait(true);
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
    }

    private async ValueTask SeedDeadSourceAsync(
        EntityId actorId,
        EntityId sourceMessageId,
        CancellationToken cancellationToken)
    {
        using (NpgsqlCommand actor = _fixture.AdministratorDataSource.CreateCommand("""
                   INSERT INTO public.users (
                       id, email, normalized_email, display_name,
                       password_hash, security_stamp
                   ) VALUES (
                       $1, $2, $2, 'M3-E4 replay actor',
                       'poolai-password-v1:integration', $3
                   );
                   """))
        {
            actor.Parameters.AddWithValue(actorId.Value);
            actor.Parameters.AddWithValue($"m3-e4-{actorId.Value:N}@example.test");
            actor.Parameters.AddWithValue(Guid.CreateVersion7());
            Assert.Equal(
                1,
                await actor.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
        }

        using NpgsqlCommand source = _fixture.AdministratorDataSource.CreateCommand("""
            INSERT INTO public.outbox_messages (
                id, deduplication_key, topic, schema_version,
                aggregate_type, aggregate_id, aggregate_version,
                event_type, source_event_sequence,
                correlation_id, causation_id, payload, occurred_at,
                status, next_attempt_at, publish_attempts,
                locked_by, lock_generation, locked_until,
                published_at, dead_at, replay_of, last_error
            ) VALUES (
                $1, $2, 'poolai.group-quota.v1', 1,
                'group_quota', $3, 17,
                'poolai.group-quota.settled', 811,
                $4, $5, '{"event":"settled","tokens":"42","secret_probe":"opaque"}'::jsonb,
                '2026-08-02 04:05:06.789+00'::timestamptz,
                'dead', NULL, 3, NULL, 2, NULL, NULL,
                clock_timestamp(), NULL, 'm3-e4 integration poison probe'
            );
            """);
        source.Parameters.AddWithValue(sourceMessageId.Value);
        source.Parameters.AddWithValue($"m3-e4:dead-source:{sourceMessageId.Value:N}");
        source.Parameters.AddWithValue(Guid.CreateVersion7());
        source.Parameters.AddWithValue(Guid.CreateVersion7());
        source.Parameters.AddWithValue(Guid.CreateVersion7());
        Assert.Equal(
            1,
            await source.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<ReplayEvidence> ReadEvidenceAsync(
        OutboxReplayOutcome outcome,
        EntityId sourceMessageId,
        EntityId requestId,
        string key,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT
                (SELECT count(*)::integer FROM public.outbox_messages
                 WHERE replay_of = $2),
                replacement.status,
                replacement.replay_of,
                source.status,
                source.last_error,
                replacement.topic = source.topic
                    AND replacement.schema_version = source.schema_version
                    AND replacement.aggregate_type = source.aggregate_type
                    AND replacement.aggregate_id = source.aggregate_id
                    AND replacement.aggregate_version IS NOT DISTINCT FROM source.aggregate_version
                    AND replacement.event_type = source.event_type
                    AND replacement.source_event_sequence IS NOT DISTINCT FROM source.source_event_sequence
                    AND replacement.correlation_id = source.correlation_id
                    AND replacement.causation_id IS NOT DISTINCT FROM source.causation_id
                    AND replacement.payload = source.payload
                    AND replacement.occurred_at = source.occurred_at,
                (SELECT count(*)::integer FROM public.audit_logs WHERE request_id = $3),
                audit.action,
                audit.target_type,
                audit.reason,
                audit.metadata ? 'payload',
                audit.before_state ->> 'message_id',
                audit.after_state ->> 'message_id',
                audit.after_state ->> 'event_sequence',
                (SELECT count(*)::integer FROM public.idempotency_records
                 WHERE idempotency_key = $4),
                idempotency.status,
                idempotency.response_status,
                idempotency.resource_id,
                idempotency.response_body ? 'payload'
                    OR idempotency.response_body ? 'last_error',
                idempotency.response_headers = '{}'::jsonb
            FROM public.outbox_messages AS replacement
            JOIN public.outbox_messages AS source ON source.id = $2
            JOIN public.audit_logs AS audit
              ON audit.request_id = $3 AND audit.target_id = replacement.id
            JOIN public.idempotency_records AS idempotency
              ON idempotency.idempotency_key = $4
            WHERE replacement.id = $1;
            """);
        command.Parameters.AddWithValue(outcome.MessageId.Value);
        command.Parameters.AddWithValue(sourceMessageId.Value);
        command.Parameters.AddWithValue(requestId.Value);
        command.Parameters.AddWithValue(key);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        ReplayEvidence evidence = new(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetGuid(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetBoolean(5),
            reader.GetInt32(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetBoolean(10),
            reader.GetString(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetInt32(14),
            reader.GetString(15),
            reader.GetInt32(16),
            reader.GetGuid(17),
            reader.GetBoolean(18),
            reader.GetBoolean(19));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return evidence;
    }

    private sealed record ReplayEvidence(
        int ReplacementCount,
        string ReplacementStatus,
        Guid ReplayOf,
        string SourceStatus,
        string SourceLastError,
        bool EnvelopePreserved,
        int AuditCount,
        string AuditAction,
        string AuditTargetType,
        string AuditReason,
        bool AuditMetadataContainsPayload,
        string BeforeMessageId,
        string AfterMessageId,
        string AfterEventSequence,
        int IdempotencyCount,
        string IdempotencyStatus,
        int IdempotencyResponseStatus,
        Guid IdempotencyResourceId,
        bool IdempotencyContainsSensitiveFields,
        bool EmptyResponseHeaders);
}
#pragma warning restore MA0051
