#pragma warning disable MA0048 // Persistence request/result types stay with the internal port.
using PoolAI.Modules.SubscriptionAccess.Domain;

namespace PoolAI.Modules.SubscriptionAccess.Application.Ports;

internal sealed record SubscriptionCursor(DateTimeOffset CreatedAt, EntityId Id);

internal sealed record SubscriptionTemplateSlice(
    IReadOnlyList<SubscriptionTemplateRecord> Items,
    bool HasMore);

internal sealed record SubscriptionSlice(
    IReadOnlyList<SubscriptionRecord> Items,
    bool HasMore);

internal enum SubscriptionMutationDisposition
{
    Updated,
    NotFound,
    VersionConflict,
    ResourceConflict,
    GroupArchived,
    GroupDisabled,
    TemplateDisabled,
    CanonicalConflict,
    InvalidTransition,
}

internal sealed record TemplateMutationResult(
    SubscriptionMutationDisposition Disposition,
    bool WasChanged,
    SubscriptionTemplateRecord? Value,
    JsonElement? BeforeState,
    long? CurrentVersion = null);

internal sealed record SubscriptionMutationResult(
    SubscriptionMutationDisposition Disposition,
    bool WasChanged,
    SubscriptionRecord? Value,
    JsonElement? BeforeState,
    long? CurrentVersion = null);

internal interface ISubscriptionRepository
{
    ValueTask<SubscriptionTemplateSlice> ListTemplatesAsync(
        SubscriptionCursor? cursor,
        int limit,
        CancellationToken cancellationToken);

    ValueTask<SubscriptionTemplateRecord?> GetTemplateAsync(
        EntityId templateId,
        CancellationToken cancellationToken);

    ValueTask<TemplateMutationResult> CreateTemplateAsync(
        EntityId templateId,
        EntityId groupId,
        string name,
        string? description,
        int defaultDurationDays,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<TemplateMutationResult> UpdateTemplateAsync(
        EntityId templateId,
        long expectedVersion,
        bool nameSpecified,
        string? name,
        bool descriptionSpecified,
        string? description,
        bool durationSpecified,
        int? durationDays,
        bool statusSpecified,
        SubscriptionTemplateLifecycle? status,
        bool retire,
        string? reason,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<SubscriptionSlice> ListSubscriptionsAsync(
        SubscriptionCursor? cursor,
        int limit,
        EntityId? userId,
        EntityId? groupId,
        CancellationToken cancellationToken);

    ValueTask<SubscriptionRecord?> GetSubscriptionAsync(
        EntityId subscriptionId,
        EntityId? visibleToUserId,
        CancellationToken cancellationToken);

    ValueTask<SubscriptionRecord?> GetEffectiveAccessAsync(
        EntityId userId,
        EntityId groupId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<SubscriptionRecord>> ListForUserAsync(
        EntityId userId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<SubscriptionRecord>> ListActiveForUserAsync(
        EntityId userId,
        CancellationToken cancellationToken);

    ValueTask<SubscriptionMutationResult> AssignSubscriptionAsync(
        EntityId subscriptionId,
        EntityId userId,
        EntityId templateId,
        DateTimeOffset? startsAt,
        DateTimeOffset? expiresAt,
        EntityId assignedBy,
        string reason,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<SubscriptionMutationResult> UpdateSubscriptionAsync(
        EntityId subscriptionId,
        long expectedVersion,
        bool startsAtSpecified,
        DateTimeOffset? startsAt,
        bool expiresAtSpecified,
        DateTimeOffset? expiresAt,
        bool statusSpecified,
        SubscriptionLifecycle? status,
        bool allowRevokedRegrant,
        EntityId actorId,
        string reason,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}
#pragma warning restore MA0048
