using PoolAI.Modules.GroupQuota.Abstractions;

namespace PoolAI.Modules.Gateway.Application;

public interface IReservationFinalizationPort
{
    ValueTask SettleKnownUsageAsync(
        DispatchedReservationHandle reservation,
        ReservationSettlementEvidence.KnownUsage usage,
        CancellationToken cancellationToken);

    ValueTask SettleConservativelyAsync(
        DispatchedReservationHandle reservation,
        ConservativeReservationSettlement settlement,
        CancellationToken cancellationToken);
}
