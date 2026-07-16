#pragma warning disable MA0048 // The envelope contract and its AAD constants form one atomic contract.
namespace PoolAI.Modules.Identity.Application;

public sealed record EmailSecretEnvelopePlaintext(
    string Recipient,
    string ResetUrl);

public sealed record PasswordResetEmailEnvelopes(
    JsonElement RecipientEnvelope,
    JsonElement DeliverySecretEnvelope);

public interface IEmailSecretEnvelope
{
    PasswordResetEmailEnvelopes Encrypt(
        EmailSecretEnvelopePlaintext plaintext,
        EntityId emailOutboxId);

    EmailSecretEnvelopePlaintext Decrypt(
        JsonElement recipientEnvelope,
        JsonElement deliverySecretEnvelope,
        EntityId emailOutboxId);
}

public static class PasswordResetEmailEnvelope
{
    public const string Purpose = "email-delivery-secret";
    public const string EntityType = "email_outbox";
    public const string RecipientField = "recipient_envelope";
    public const string DeliverySecretField = "delivery_secret_envelope";

    public static string BuildAad(EntityId emailOutboxId, string fieldName)
    {
        if (fieldName is not RecipientField and not DeliverySecretField)
        {
            throw new ArgumentOutOfRangeException(nameof(fieldName));
        }

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"poolai|v1|{Purpose}|{EntityType}|{emailOutboxId.Value:D}|{fieldName}");
    }
}
#pragma warning restore MA0048
