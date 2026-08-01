namespace PoolAI.Modules.GroupQuota.Abstractions;

public interface IGroupQuotaLedger
{
    ValueTask<Result<ReserveQuotaResult>> ReserveAsync(
        ReserveQuotaCommand command,
        CancellationToken cancellationToken);

    ValueTask<Result<DispatchedReservationHandle>> MarkDispatchedAsync(
        MarkReservationDispatchedCommand command,
        CancellationToken cancellationToken);

    ValueTask<Result<QuotaTransitionResult>> SettleAsync(
        SettleReservationCommand command,
        CancellationToken cancellationToken);

    ValueTask<Result<QuotaTransitionResult>> ReleaseAsync(
        ReleaseReservationCommand command,
        CancellationToken cancellationToken);
}
