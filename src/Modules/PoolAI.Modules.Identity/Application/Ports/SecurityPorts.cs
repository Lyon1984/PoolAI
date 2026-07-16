#pragma warning disable MA0048 // Security port DTOs are intentionally collocated with their ports.
namespace PoolAI.Modules.Identity.Application.Ports;

internal interface IVersionedPasswordHasher
{
    string Hash(string password);

    bool Verify(string encodedHash, string password);
}

internal sealed record PasswordResetTokenSecret(
    string Token,
    byte[] Hash,
    short PepperVersion);

internal sealed record PasswordResetTokenCandidate(
    byte[] Hash,
    short PepperVersion);

internal interface IPasswordResetTokenHasher
{
    PasswordResetTokenSecret Create();

    IReadOnlyList<PasswordResetTokenCandidate> HashCandidates(string token);
}
#pragma warning restore MA0048
