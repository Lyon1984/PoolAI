#pragma warning disable MA0048 // Security port DTOs are intentionally collocated with their ports.
using PoolAI.Modules.Identity.Abstractions;

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

internal sealed record RefreshCredentialSecret(
    string Token,
    byte[] Hash,
    short PepperVersion);

internal sealed record RefreshCredentialCandidate(
    byte[] Hash,
    short PepperVersion);

internal interface IRefreshCredentialHasher
{
    RefreshCredentialSecret Create();

    IReadOnlyList<RefreshCredentialCandidate> HashCandidates(string token);

    bool Verify(string token, byte[] expectedHash, short pepperVersion);
}

internal sealed record OneTimeChallengeSecret(
    EntityId Challenge,
    byte[] Hash,
    short PepperVersion);

internal sealed record OneTimeChallengeCandidate(
    byte[] Hash,
    short PepperVersion);

internal interface IOneTimeChallengeHasher
{
    OneTimeChallengeSecret Create();

    IReadOnlyList<OneTimeChallengeCandidate> HashCandidates(EntityId challenge);

    bool Verify(EntityId challenge, byte[] expectedHash, short pepperVersion);
}

internal sealed record TotpProvisioningSecret(
    string Base32Secret,
    string OtpAuthUri);

internal interface ITotpAuthenticator
{
    TotpProvisioningSecret CreateProvisioningSecret(string accountName);

    string BuildProvisioningUri(string base32Secret, string accountName);

    bool TryMatchStep(
        string base32Secret,
        string code,
        DateTimeOffset timestamp,
        out long matchedStep);
}

internal enum TotpSecretEnvelopeTarget
{
    SetupChallenge,
    User,
}

internal interface ITotpSecretEnvelope
{
    JsonElement Encrypt(
        string base32Secret,
        TotpSecretEnvelopeTarget target,
        EntityId targetId);

    string Decrypt(
        JsonElement envelope,
        TotpSecretEnvelopeTarget target,
        EntityId targetId);
}

internal interface ITotpRecoveryCodeEnvelope
{
    JsonElement Encrypt(
        IReadOnlyList<string> recoveryCodes,
        EntityId oneTimeTokenId,
        IdempotencySecretBinding binding);

    IReadOnlyList<string> Decrypt(
        JsonElement envelope,
        EntityId oneTimeTokenId,
        IdempotencySecretBinding binding);
}

internal sealed record IdempotencySecretBinding(
    EntityId ActorUserId,
    string Scope,
    string IdempotencyKey,
    ReadOnlyMemory<byte> RequestHash);

internal sealed record TotpSetupResponseSecret(
    EntityId Challenge,
    string Base32Secret,
    string OtpAuthUri,
    int ExpiresInSeconds);

internal interface ITotpSetupResponseEnvelope
{
    JsonElement Encrypt(
        TotpSetupResponseSecret response,
        EntityId oneTimeTokenId,
        IdempotencySecretBinding binding);

    TotpSetupResponseSecret Decrypt(
        JsonElement envelope,
        EntityId oneTimeTokenId,
        IdempotencySecretBinding binding);
}

internal sealed record TotpRecoveryCodeSecret(
    string Code,
    byte[] Hash,
    short PepperVersion);

internal interface ITotpRecoveryCodeGenerator
{
    IReadOnlyList<TotpRecoveryCodeSecret> CreateBatch();
}

internal sealed record AccessTokenSubject(
    EntityId UserId,
    SystemRole Role,
    long TokenVersion,
    EntityId SessionFamilyId);

internal sealed record AccessTokenSecret(
    string Token,
    DateTimeOffset ExpiresAt);

internal interface IAccessTokenIssuer
{
    AccessTokenSecret Issue(AccessTokenSubject subject);
}
#pragma warning restore MA0048
