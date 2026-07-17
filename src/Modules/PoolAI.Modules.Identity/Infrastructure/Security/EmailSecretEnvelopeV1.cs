using System.Security.Cryptography;
using System.Text;
using PoolAI.Modules.Identity.Application;

namespace PoolAI.Modules.Identity.Infrastructure.Security;

internal sealed class EmailSecretEnvelopeV1 : IEmailSecretEnvelope
{
    private readonly AeadEnvelopeV1 _envelope;

    internal EmailSecretEnvelopeV1(EnvelopeKeyRingOptions keyRing)
    {
        _envelope = new AeadEnvelopeV1(keyRing);
    }

    public PasswordResetEmailEnvelopes Encrypt(
        EmailSecretEnvelopePlaintext plaintext,
        EntityId emailOutboxId)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (string.IsNullOrWhiteSpace(plaintext.Recipient)
            || string.IsNullOrWhiteSpace(plaintext.ResetUrl))
        {
            throw new ArgumentException(
                "The email secret plaintext fields are required.",
                nameof(plaintext));
        }

        return new PasswordResetEmailEnvelopes(
            EncryptField(
                plaintext.Recipient,
                PasswordResetEmailEnvelope.BuildAad(
                    emailOutboxId,
                    PasswordResetEmailEnvelope.RecipientField)),
            EncryptField(
                plaintext.ResetUrl,
                PasswordResetEmailEnvelope.BuildAad(
                    emailOutboxId,
                    PasswordResetEmailEnvelope.DeliverySecretField)));
    }

    public EmailSecretEnvelopePlaintext Decrypt(
        JsonElement recipientEnvelope,
        JsonElement deliverySecretEnvelope,
        EntityId emailOutboxId) => new(
        DecryptField(
            recipientEnvelope,
            PasswordResetEmailEnvelope.BuildAad(
                emailOutboxId,
                PasswordResetEmailEnvelope.RecipientField)),
        DecryptField(
            deliverySecretEnvelope,
            PasswordResetEmailEnvelope.BuildAad(
                emailOutboxId,
                PasswordResetEmailEnvelope.DeliverySecretField)));

    private JsonElement EncryptField(string plaintext, string aad)
    {
        byte[] value = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            return _envelope.Encrypt(value, aad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    private string DecryptField(JsonElement envelope, string aad)
    {
        byte[] plaintext;
        try
        {
            plaintext = _envelope.Decrypt(envelope, aad);
        }
        catch (CryptographicException exception)
        {
            throw new CryptographicException(
                "Email secret envelope validation failed.",
                exception);
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(plaintext);
        }
        catch (DecoderFallbackException exception)
        {
            throw new CryptographicException(
                "Email secret envelope validation failed.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
