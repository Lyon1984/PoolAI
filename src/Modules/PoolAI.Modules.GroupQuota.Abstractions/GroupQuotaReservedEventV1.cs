namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record GroupQuotaReservedEventV1(GroupQuotaEventV1Data Data)
    : GroupQuotaEventV1(Data)
{
    public override string EventType => "reserved";

    public override GroupQuotaUsageProjectionDisposition UsageProjection =>
        GroupQuotaUsageProjectionDisposition.None;
}
