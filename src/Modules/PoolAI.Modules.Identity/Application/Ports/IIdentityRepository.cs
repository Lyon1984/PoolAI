#pragma warning disable MA0048 // Repository request/result types are intentionally collocated with the port.
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Domain;

namespace PoolAI.Modules.Identity.Application.Ports;

internal sealed record UserCursor(DateTimeOffset CreatedAt, EntityId Id);

internal sealed record UserSlice(
    IReadOnlyList<IdentityUser> Users,
    bool HasMore);

internal enum UpdateUserDisposition
{
    Updated,
    NotFound,
    VersionConflict,
    LastActiveAdminConflict,
}

internal sealed record UpdateUserPersistenceResult(
    UpdateUserDisposition Disposition,
    IdentityUser? User,
    IdentityUser? Before,
    bool Changed);

internal sealed record PasswordResetOutboxWrite(
    EntityId TokenId,
    EntityId EmailOutboxId,
    EntityId UserId,
    byte[] TokenHash,
    short PepperVersion,
    TimeSpan Lifetime,
    string IdempotencyKey,
    string MessageId,
    JsonElement RecipientEnvelope,
    JsonElement TemplatePayload,
    JsonElement DeliverySecretEnvelope);

internal sealed record PasswordResetConsumeResult(
    IdentityUser User,
    EntityId PasswordResetId);

internal interface IIdentityRepository
{
    ValueTask<UserSlice> ListAsync(
        UserCursor? cursor,
        int limit,
        CancellationToken cancellationToken);

    ValueTask<IdentityUser?> GetAsync(
        EntityId userId,
        CancellationToken cancellationToken);

    ValueTask<IdentityUser?> GetAsync(
        EntityId userId,
        IUnitOfWorkContext unitOfWorkContext,
        bool forUpdate,
        CancellationToken cancellationToken);

    ValueTask<IdentityUser?> FindByNormalizedEmailAsync(
        string normalizedEmail,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<IdentityUser?> CreateAsync(
        EntityId userId,
        string email,
        string normalizedEmail,
        string displayName,
        string passwordHash,
        SystemRole role,
        EntityId assignedBy,
        EntityId securityStamp,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<UpdateUserPersistenceResult> UpdateAsync(
        EntityId userId,
        long expectedVersion,
        string? displayName,
        SystemRole? role,
        UserLifecycle? status,
        EntityId assignedBy,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask InsertPasswordResetAsync(
        PasswordResetOutboxWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<bool> HasConsumablePasswordResetAsync(
        IReadOnlyList<PasswordResetTokenCandidate> candidates,
        CancellationToken cancellationToken);

    ValueTask<PasswordResetConsumeResult?> ConsumePasswordResetAsync(
        IReadOnlyList<PasswordResetTokenCandidate> candidates,
        string passwordHash,
        EntityId securityStamp,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}
#pragma warning restore MA0048
