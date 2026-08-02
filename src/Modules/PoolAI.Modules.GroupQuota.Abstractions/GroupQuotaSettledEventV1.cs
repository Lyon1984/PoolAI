namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record GroupQuotaSettledEventV1(GroupQuotaEventV1Data Data)
    : GroupQuotaEventV1(Data)
{
    public override string EventType => "settled";

    public override GroupQuotaUsageProjectionDisposition UsageProjection =>
        GroupQuotaUsageProjectionDisposition.RebuildAttemptHour;
}
