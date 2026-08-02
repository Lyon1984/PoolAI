using Npgsql;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Operations.Application.Ports;

namespace PoolAI.Modules.Operations.Infrastructure.Persistence;

internal sealed class PostgresOutboxReplayRepository : IOutboxReplayRepository
{
    private const string ReplaySql = """
        SELECT disposition, new_message_id, event_sequence
        FROM public.poolai_operations_replay_dead_outbox($1, $2, $3);
        """;

    public async ValueTask<OutboxReplayWriteResult> ReplayDeadAsync(
        OutboxReplayWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(unitOfWorkContext);
        if (write.SourceMessageId.Value == Guid.Empty
            || write.NewMessageId.Value == Guid.Empty
            || write.SourceMessageId == write.NewMessageId)
        {
            throw new ArgumentException(
                "The Outbox replay identifiers are invalid.",
                nameof(write));
        }

        PostgresPersistenceGuard.NotBlank(
            write.NewDeduplicationKey,
            nameof(write.NewDeduplicationKey));
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(
            unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(ReplaySql);
        command.Parameters.AddWithValue(write.SourceMessageId.Value);
        command.Parameters.AddWithValue(write.NewMessageId.Value);
        command.Parameters.AddWithValue(write.NewDeduplicationKey);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (reader.FieldCount != 3
            || !string.Equals(reader.GetName(0), "disposition", StringComparison.Ordinal)
            || !string.Equals(reader.GetName(1), "new_message_id", StringComparison.Ordinal)
            || !string.Equals(reader.GetName(2), "event_sequence", StringComparison.Ordinal)
            || !await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The signed Outbox replay function returned an invalid shape.");
        }

        OutboxReplayPersistenceDisposition disposition = ParseDisposition(reader.GetString(0));
        EntityId? messageId = reader.IsDBNull(1)
            ? null
            : new EntityId(reader.GetGuid(1));
        long? eventSequence = reader.IsDBNull(2) ? null : reader.GetInt64(2);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The signed Outbox replay function returned more than one row.");
        }

        ValidateResult(write, disposition, messageId, eventSequence);
        return new OutboxReplayWriteResult(disposition, messageId, eventSequence);
    }

    private static OutboxReplayPersistenceDisposition ParseDisposition(string value) => value switch
    {
        "created" => OutboxReplayPersistenceDisposition.Created,
        "replayed" => OutboxReplayPersistenceDisposition.Replayed,
        "source_not_found" => OutboxReplayPersistenceDisposition.SourceNotFound,
        "source_not_dead" => OutboxReplayPersistenceDisposition.SourceNotDead,
        "replay_conflict" => OutboxReplayPersistenceDisposition.ReplayConflict,
        "validation_failed" => OutboxReplayPersistenceDisposition.ValidationFailed,
        _ => throw new InvalidOperationException(
            "The signed Outbox replay function returned an unknown disposition."),
    };

    private static void ValidateResult(
        OutboxReplayWrite write,
        OutboxReplayPersistenceDisposition disposition,
        EntityId? messageId,
        long? eventSequence)
    {
        bool succeeded = disposition is OutboxReplayPersistenceDisposition.Created
            or OutboxReplayPersistenceDisposition.Replayed;
        if ((succeeded
                && (messageId != write.NewMessageId || eventSequence is null or <= 0))
            || (!succeeded && (messageId is not null || eventSequence is not null)))
        {
            throw new InvalidOperationException(
                "The signed Outbox replay function returned an inconsistent receipt.");
        }
    }
}
