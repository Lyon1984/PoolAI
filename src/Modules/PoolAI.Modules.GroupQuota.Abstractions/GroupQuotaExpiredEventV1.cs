namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record GroupQuotaExpiredEventV1(
    GroupQuotaEventV1Data Data,
    bool ConservativeExpiry)
    : GroupQuotaEventV1(Data)
{
    public override string EventType => "expired";

    public override GroupQuotaUsageProjectionDisposition UsageProjection =>
        ConservativeExpiry
            ? GroupQuotaUsageProjectionDisposition.RebuildAttemptHour
            : GroupQuotaUsageProjectionDisposition.None;
}
