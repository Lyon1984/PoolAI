namespace PoolAI.Modules.Identity.Abstractions;

public sealed record EmailOutboxMessage(
    EmailOutboxDeliveryLease Lease,
    string MessageId,
    JsonElement RecipientEnvelope,
    string TemplateCode,
    JsonElement TemplatePayload,
    JsonElement DeliverySecretEnvelope);
