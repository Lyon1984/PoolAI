using System.Security.Cryptography;
using System.Text;
using PoolAI.Modules.GroupQuota.Application.Ports;

namespace PoolAI.Modules.GroupQuota.Application;

internal static class QuotaMutationIdentityFactory
{
    internal static EntityId ReservationId(EntityId attemptId) =>
        DeriveVersion7(attemptId, "reservation");

    internal static QuotaMutationIdentity For(
        EntityId attemptId,
        string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        return new QuotaMutationIdentity(
            DeriveVersion7(attemptId, $"event:{operation}"),
            DeriveVersion7(attemptId, $"outbox:{operation}"),
            $"quota:{operation}:v1:{attemptId.Value:N}");
    }

    private static EntityId DeriveVersion7(EntityId attemptId, string purpose)
    {
        Span<byte> source = stackalloc byte[16];
        attemptId.Value.TryWriteBytes(source, bigEndian: true, out _);

        byte[] purposeBytes = Encoding.UTF8.GetBytes(purpose);
        byte[] input = new byte[source.Length + purposeBytes.Length];
        source.CopyTo(input);
        purposeBytes.CopyTo(input, source.Length);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        _ = SHA256.HashData(input, hash);

        Span<byte> derived = stackalloc byte[16];
        hash[..derived.Length].CopyTo(derived);
        source[..6].CopyTo(derived);
        derived[6] = (byte)((derived[6] & 0x0f) | 0x70);
        derived[8] = (byte)((derived[8] & 0x3f) | 0x80);
        return new EntityId(new Guid(derived, bigEndian: true));
    }
}
