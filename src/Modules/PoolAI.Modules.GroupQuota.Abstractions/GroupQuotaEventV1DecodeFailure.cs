namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record GroupQuotaEventV1DecodeFailure(
    GroupQuotaEventV1DecodeFailureCode Code,
    string Location);
