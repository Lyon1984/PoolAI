namespace PoolAI.Modules.GroupQuota.Abstractions;

public interface IGroupQuotaLedger
{
    ValueTask<Result<ReservationHandle>> ReserveAsync(
        ReserveQuotaCommand command,
        CancellationToken cancellationToken);
}
