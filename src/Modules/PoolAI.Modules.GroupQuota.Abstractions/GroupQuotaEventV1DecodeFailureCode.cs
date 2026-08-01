namespace PoolAI.Modules.GroupQuota.Abstractions;

public enum GroupQuotaEventV1DecodeFailureCode
{
    MalformedJson,
    InvalidEnvelope,
    UnsupportedTopic,
    UnsupportedSchemaVersion,
    UnsupportedEventType,
    MissingIdentity,
    InvalidSequence,
    EnvelopePayloadMismatch,
    InvalidPayload,
    InvalidEventSemantics,
}
