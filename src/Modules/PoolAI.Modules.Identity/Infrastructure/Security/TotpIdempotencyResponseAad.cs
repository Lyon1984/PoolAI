using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using PoolAI.Modules.Identity.Application.Ports;

namespace PoolAI.Modules.Identity.Infrastructure.Security;

internal static class TotpIdempotencyResponseAad
{
    private const string Purpose = "idempotency-response";
    private const string Entity = "idempotency-request-binding";

    internal static string Build(
        string field,
        EntityId oneTimeTokenId,
        IdempotencySecretBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (string.IsNullOrWhiteSpace(field)
            || string.IsNullOrWhiteSpace(binding.Scope)
            || string.IsNullOrWhiteSpace(binding.IdempotencyKey)
            || binding.RequestHash.Length != 32)
        {
            throw new ArgumentException(
                "The idempotency secret binding is invalid.",
                nameof(binding));
        }

        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("actor_user_id", binding.ActorUserId.Value);
            writer.WriteString("scope", binding.Scope);
            writer.WriteString("idempotency_key", binding.IdempotencyKey);
            writer.WriteString(
                "request_hash",
                Convert.ToHexStringLower(binding.RequestHash.Span));
            writer.WriteString("response_resource_id", oneTimeTokenId.Value);
            writer.WriteString("response_field", field);
            writer.WriteEndObject();
        }

        byte[] canonicalBinding = buffer.WrittenSpan.ToArray();
        byte[] digest = SHA256.HashData(canonicalBinding);
        try
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"poolai|v1|{Purpose}|{Entity}|{Convert.ToHexStringLower(digest)}|{field}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonicalBinding);
            CryptographicOperations.ZeroMemory(digest);
            buffer.Clear();
        }
    }
}
