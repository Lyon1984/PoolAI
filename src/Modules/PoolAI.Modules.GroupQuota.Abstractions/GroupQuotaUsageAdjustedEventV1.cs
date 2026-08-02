namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record GroupQuotaUsageAdjustedEventV1(GroupQuotaEventV1Data Data)
    : GroupQuotaEventV1(Data)
{
    public override string EventType => "usage_adjusted";

    public override GroupQuotaUsageProjectionDisposition UsageProjection =>
        GroupQuotaUsageProjectionDisposition.RebuildAttemptHour;
}
