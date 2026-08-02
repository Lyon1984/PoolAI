namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record GroupQuotaDispatchStartedEventV1(GroupQuotaEventV1Data Data)
    : GroupQuotaEventV1(Data)
{
    public override string EventType => "dispatch_started";

    public override GroupQuotaUsageProjectionDisposition UsageProjection =>
        GroupQuotaUsageProjectionDisposition.None;
}
