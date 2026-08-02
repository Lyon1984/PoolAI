using Npgsql;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.GroupQuota.Abstractions;

namespace PoolAI.Modules.GroupQuota.Infrastructure.Persistence;

internal sealed class PostgresAttemptSettlementFactExistenceReader :
    IAttemptSettlementFactExistenceReader
{
    private const string ExistsSql = """
        SELECT EXISTS (
            SELECT 1
            FROM public.usage_attempts AS attempt
            JOIN public.group_token_reservations AS reservation
              ON reservation.id = attempt.reservation_id
             AND reservation.attempt_id = attempt.attempt_id
             AND reservation.group_id = attempt.quota_group_id
            WHERE reservation.id = $3
              AND reservation.group_id = $1
              AND reservation.period_id = $2
        );
        """;

    public async ValueTask<bool> ExistsForReservationAsync(
        EntityId groupId,
        EntityId periodId,
        EntityId reservationId,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(
            unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(ExistsSql);
        command.Parameters.AddWithValue(groupId.Value);
        command.Parameters.AddWithValue(periodId.Value);
        command.Parameters.AddWithValue(reservationId.Value);
        return AssertBoolean(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static bool AssertBoolean(object? value) => value is bool result
        ? result
        : throw new InvalidOperationException(
            "The PostgreSQL settlement-fact existence result violated its ABI.");
}
