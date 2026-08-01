namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record GroupQuotaTotalAdjustedEventV1(GroupQuotaEventV1Data Data)
    : GroupQuotaEventV1(Data)
{
    public override string EventType => "total_adjusted";

    public override GroupQuotaUsageProjectionDisposition UsageProjection =>
        GroupQuotaUsageProjectionDisposition.None;
}
