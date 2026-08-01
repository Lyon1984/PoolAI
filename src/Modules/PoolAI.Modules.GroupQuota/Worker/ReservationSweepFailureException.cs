using PoolAI.Modules.GroupQuota.Application.Ports;

namespace PoolAI.Modules.GroupQuota.Worker;

internal sealed class ReservationSweepFailureException(
    QuotaLedgerFailure failure) : Exception(
        $"The reservation sweeper failed safely with {failure}.")
{
    internal QuotaLedgerFailure Failure { get; } = failure;
}
