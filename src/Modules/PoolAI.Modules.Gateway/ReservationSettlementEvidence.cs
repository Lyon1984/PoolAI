using PoolAI.Modules.GroupQuota.Abstractions;

namespace PoolAI.Modules.Gateway.Application;

public abstract record ReservationSettlementEvidence
{
    private ReservationSettlementEvidence()
    {
    }

    public sealed record KnownUsage : ReservationSettlementEvidence
    {
        public KnownUsage(
            TokenUsage usage,
            SettlementUsageSource usageSource)
        {
            ArgumentNullException.ThrowIfNull(usage);
            if (usageSource is not (SettlementUsageSource.Upstream
                or SettlementUsageSource.LocalTokenizer
                or SettlementUsageSource.ConfirmedNoExecution))
            {
                throw new ArgumentOutOfRangeException(nameof(usageSource));
            }

            Usage = usage;
            UsageSource = usageSource;
        }

        public TokenUsage Usage { get; }

        public SettlementUsageSource UsageSource { get; }
    }

    public sealed record NoKnownUsage : ReservationSettlementEvidence
    {
        public static NoKnownUsage Instance { get; } = new();

        private NoKnownUsage()
        {
        }
    }
}
