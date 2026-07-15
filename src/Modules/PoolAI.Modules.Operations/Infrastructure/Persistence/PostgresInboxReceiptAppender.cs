using System.Security.Cryptography;
using Npgsql;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.Operations.Infrastructure.Persistence;

internal sealed class PostgresInboxReceiptAppender : IInboxReceiptAppender
{
    private const string InsertSql = """
        INSERT INTO public.inbox_messages (
            consumer_name, message_id, topic, event_sequence,
            schema_version, payload_hash
        ) VALUES ($1, $2, $3, $4, $5, $6)
        ON CONFLICT DO NOTHING;
        """;

    private const string ReadConflictSql = """
        SELECT message_id, topic, event_sequence, schema_version, payload_hash
        FROM public.inbox_messages
        WHERE (consumer_name = $1 AND message_id = $2)
           OR (consumer_name = $1 AND topic = $3 AND event_sequence = $4)
        ORDER BY CASE WHEN message_id = $2 THEN 0 ELSE 1 END
        LIMIT 1;
        """;

    public async ValueTask<InboxReceiptAppendResult> AppendAsync(
        InboxReceipt receipt,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        Validate(receipt);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        using (NpgsqlCommand insert = session.CreateCommand(InsertSql))
        {
            AddParameters(insert, receipt);
            int inserted = await insert
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            if (inserted == 1)
            {
                return new InboxReceiptAppendResult(InboxReceiptDisposition.Inserted);
            }
        }

        using NpgsqlCommand read = session.CreateCommand(ReadConflictSql);
        read.Parameters.AddWithValue(receipt.ConsumerName);
        read.Parameters.AddWithValue(receipt.MessageId.Value);
        read.Parameters.AddWithValue(receipt.Topic);
        read.Parameters.AddWithValue(receipt.EventSequence);
        using NpgsqlDataReader reader = await read
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The conflicting inbox receipt disappeared.");
        }

        Guid existingMessageId = reader.GetGuid(0);
        if (existingMessageId != receipt.MessageId.Value)
        {
            return new InboxReceiptAppendResult(InboxReceiptDisposition.SequenceConflict);
        }

        bool exact = string.Equals(reader.GetString(1), receipt.Topic, StringComparison.Ordinal)
            && reader.GetInt64(2) == receipt.EventSequence
            && reader.GetInt32(3) == receipt.SchemaVersion
            && CryptographicOperations.FixedTimeEquals(
                reader.GetFieldValue<byte[]>(4),
                receipt.PayloadHash.Span);
        return new InboxReceiptAppendResult(
            exact ? InboxReceiptDisposition.Duplicate : InboxReceiptDisposition.MessageConflict);
    }

    private static void AddParameters(NpgsqlCommand command, InboxReceipt receipt)
    {
        command.Parameters.AddWithValue(receipt.ConsumerName);
        command.Parameters.AddWithValue(receipt.MessageId.Value);
        command.Parameters.AddWithValue(receipt.Topic);
        command.Parameters.AddWithValue(receipt.EventSequence);
        command.Parameters.AddWithValue(receipt.SchemaVersion);
        command.Parameters.AddWithValue(receipt.PayloadHash.ToArray());
    }

    private static void Validate(InboxReceipt receipt)
    {
        PostgresPersistenceGuard.NotBlank(receipt.ConsumerName, nameof(receipt.ConsumerName));
        PostgresPersistenceGuard.NotBlank(receipt.Topic, nameof(receipt.Topic));
        PostgresPersistenceGuard.Hash32(receipt.PayloadHash, nameof(receipt.PayloadHash));
        if (receipt.EventSequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(receipt), "Event sequence must be positive.");
        }

        if (receipt.SchemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(receipt), "Schema version must be positive.");
        }
    }
}
