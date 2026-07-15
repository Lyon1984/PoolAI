using Npgsql;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Identity.Abstractions;

namespace PoolAI.Modules.Identity.Infrastructure.Persistence;

internal sealed class PostgresEmailOutboxDeliveryStore : IEmailOutboxDeliveryStore
{
    private const string ClaimSql = """
        WITH candidates AS MATERIALIZED (
            SELECT id
            FROM public.email_outbox
            WHERE (status = 'pending' AND next_attempt_at <= clock_timestamp())
               OR (status = 'processing'
                   AND locked_until <= clock_timestamp()
                   AND lock_owner IS DISTINCT FROM $1)
            ORDER BY
                CASE WHEN status = 'pending' THEN next_attempt_at ELSE locked_until END,
                created_at,
                id
            FOR UPDATE SKIP LOCKED
            LIMIT $2
        )
        UPDATE public.email_outbox AS message
        SET status = 'processing',
            lock_owner = $1,
            lock_generation = message.lock_generation + 1,
            attempts = message.attempts + 1,
            locked_until = clock_timestamp() + $3,
            updated_at = clock_timestamp()
        FROM candidates
        WHERE message.id = candidates.id
        RETURNING message.id,
                  message.message_id,
                  message.recipient_envelope::text,
                  message.template_code,
                  message.template_payload::text,
                  message.delivery_secret_envelope::text,
                  message.lock_generation,
                  message.attempts;
        """;

    private const string HeartbeatSql = """
        UPDATE public.email_outbox
        SET locked_until = clock_timestamp() + $5,
            updated_at = clock_timestamp()
        WHERE id = $1
          AND status = 'processing'
          AND lock_owner = $2
          AND lock_generation = $3
          AND attempts = $4
          AND locked_until > clock_timestamp();
        """;

    private const string MarkSentSql = """
        UPDATE public.email_outbox
        SET status = 'sent',
            next_attempt_at = NULL,
            lock_owner = NULL,
            locked_until = NULL,
            recipient_envelope = NULL,
            delivery_secret_envelope = NULL,
            sent_at = clock_timestamp(),
            last_error = NULL,
            updated_at = clock_timestamp()
        WHERE id = $1
          AND status = 'processing'
          AND lock_owner = $2
          AND lock_generation = $3
          AND attempts = $4
          AND locked_until > clock_timestamp();
        """;

    private const string RetrySql = """
        UPDATE public.email_outbox
        SET status = 'pending',
            next_attempt_at = clock_timestamp() + $5,
            lock_owner = NULL,
            locked_until = NULL,
            last_error = $6,
            updated_at = clock_timestamp()
        WHERE id = $1
          AND status = 'processing'
          AND lock_owner = $2
          AND lock_generation = $3
          AND attempts = $4
          AND locked_until > clock_timestamp();
        """;

    private const string MarkDeadSql = """
        UPDATE public.email_outbox
        SET status = 'dead',
            next_attempt_at = NULL,
            lock_owner = NULL,
            locked_until = NULL,
            recipient_envelope = NULL,
            delivery_secret_envelope = NULL,
            dead_at = clock_timestamp(),
            last_error = $5,
            updated_at = clock_timestamp()
        WHERE id = $1
          AND status = 'processing'
          AND lock_owner = $2
          AND lock_generation = $3
          AND attempts = $4
          AND locked_until > clock_timestamp();
        """;

    public async ValueTask<IReadOnlyList<EmailOutboxMessage>> ClaimDueAsync(
        EntityId owner,
        int maximumCount,
        TimeSpan leaseDuration,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        if (maximumCount is <= 0 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        Positive(leaseDuration, nameof(leaseDuration));
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(ClaimSql);
        command.Parameters.AddWithValue(owner.Value);
        command.Parameters.AddWithValue(maximumCount);
        command.Parameters.AddWithValue(leaseDuration);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        List<EmailOutboxMessage> messages = new(maximumCount);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            EntityId emailId = new(reader.GetGuid(0));
            messages.Add(new EmailOutboxMessage(
                new EmailOutboxDeliveryLease(
                    emailId,
                    owner,
                    reader.GetInt64(6),
                    reader.GetInt32(7)),
                reader.GetString(1),
                ParseJson(reader.GetString(2)),
                reader.GetString(3),
                ParseJson(reader.GetString(4)),
                ParseJson(reader.GetString(5))));
        }

        return messages;
    }

    public async ValueTask<bool> HeartbeatAsync(
        EmailOutboxDeliveryLease lease,
        TimeSpan leaseDuration,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        Validate(lease);
        Positive(leaseDuration, nameof(leaseDuration));
        return await ExecuteAsync(
            HeartbeatSql,
            lease,
            unitOfWorkContext,
            command => command.Parameters.AddWithValue(leaseDuration),
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<bool> MarkSentAsync(
        EmailOutboxDeliveryLease lease,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        Validate(lease);
        return ExecuteAsync(MarkSentSql, lease, unitOfWorkContext, null, cancellationToken);
    }

    public async ValueTask<bool> ReleaseForRetryAsync(
        EmailOutboxDeliveryLease lease,
        TimeSpan retryDelay,
        string errorSummary,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        Validate(lease);
        Positive(retryDelay, nameof(retryDelay));
        ValidateError(errorSummary);
        return await ExecuteAsync(
            RetrySql,
            lease,
            unitOfWorkContext,
            command =>
            {
                command.Parameters.AddWithValue(retryDelay);
                command.Parameters.AddWithValue(errorSummary);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> MarkDeadAsync(
        EmailOutboxDeliveryLease lease,
        string errorSummary,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        Validate(lease);
        ValidateError(errorSummary);
        return await ExecuteAsync(
            MarkDeadSql,
            lease,
            unitOfWorkContext,
            command => command.Parameters.AddWithValue(errorSummary),
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> ExecuteAsync(
        string sql,
        EmailOutboxDeliveryLease lease,
        IUnitOfWorkContext unitOfWorkContext,
        Action<NpgsqlCommand>? addParameters,
        CancellationToken cancellationToken)
    {
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(sql);
        command.Parameters.AddWithValue(lease.EmailId.Value);
        command.Parameters.AddWithValue(lease.Owner.Value);
        command.Parameters.AddWithValue(lease.Generation);
        command.Parameters.AddWithValue(lease.Attempt);
        addParameters?.Invoke(command);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static JsonElement ParseJson(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void Validate(EmailOutboxDeliveryLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Generation <= 0 || lease.Attempt <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lease));
        }
    }

    private static void Positive(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateError(string errorSummary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorSummary);
        if (!string.Equals(errorSummary, errorSummary.Trim(), StringComparison.Ordinal)
            || errorSummary.Length > 2048)
        {
            throw new ArgumentException("The non-secret error summary is invalid.", nameof(errorSummary));
        }
    }
}
