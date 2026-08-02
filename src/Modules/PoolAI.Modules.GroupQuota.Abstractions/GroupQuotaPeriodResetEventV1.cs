namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record GroupQuotaPeriodResetEventV1(GroupQuotaEventV1Data Data)
    : GroupQuotaEventV1(Data)
{
    public override string EventType => "period_reset";

    public override GroupQuotaUsageProjectionDisposition UsageProjection =>
        GroupQuotaUsageProjectionDisposition.None;
}
