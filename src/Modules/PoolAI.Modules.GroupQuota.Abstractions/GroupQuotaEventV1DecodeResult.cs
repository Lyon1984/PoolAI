namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record GroupQuotaEventV1DecodeResult(
    GroupQuotaEventEnvelopeV1? Envelope,
    GroupQuotaEventV1DecodeFailure? Failure)
{
    public bool IsSuccess => Envelope is not null && Failure is null;
}
