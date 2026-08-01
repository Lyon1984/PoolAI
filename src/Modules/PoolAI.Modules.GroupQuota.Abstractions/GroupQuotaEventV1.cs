namespace PoolAI.Modules.GroupQuota.Abstractions;

/// <summary>
/// Closed v1 union for GroupQuota integration-event payloads.
/// </summary>
public abstract record GroupQuotaEventV1(GroupQuotaEventV1Data Data)
{
    public abstract string EventType { get; }

    public abstract GroupQuotaUsageProjectionDisposition UsageProjection { get; }
}
