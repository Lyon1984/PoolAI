namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record GroupQuotaInitializedEventV1(GroupQuotaEventV1Data Data)
    : GroupQuotaEventV1(Data)
{
    public override string EventType => "initialized";

    public override GroupQuotaUsageProjectionDisposition UsageProjection =>
        GroupQuotaUsageProjectionDisposition.None;
}
