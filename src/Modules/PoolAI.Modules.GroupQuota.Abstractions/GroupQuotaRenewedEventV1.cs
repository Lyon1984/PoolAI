namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record GroupQuotaRenewedEventV1(GroupQuotaEventV1Data Data)
    : GroupQuotaEventV1(Data)
{
    public override string EventType => "renewed";

    public override GroupQuotaUsageProjectionDisposition UsageProjection =>
        GroupQuotaUsageProjectionDisposition.None;
}
