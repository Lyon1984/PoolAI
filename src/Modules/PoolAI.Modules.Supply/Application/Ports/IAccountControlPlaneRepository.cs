#pragma warning disable MA0048 // Account persistence request/result types stay with the internal port.
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Domain;

namespace PoolAI.Modules.Supply.Application.Ports;

internal sealed record AccountCursor(DateTimeOffset CreatedAt, EntityId Id);

internal sealed record AccountSlice(
    IReadOnlyList<AccountResource> Items,
    bool HasMore);

internal sealed record AccountCreateWrite(
    EntityId AccountId,
    UpstreamProvider Provider,
    string Name,
    string UpstreamBaseUrl,
    JsonElement CredentialEnvelope,
    string CredentialPrefix,
    int MaxConcurrency,
    int Priority,
    int Weight)
{
    public override string ToString() => nameof(AccountCreateWrite);
}

internal sealed record AccountUpdateWrite(
    EntityId AccountId,
    long ExpectedVersion,
    bool NameSpecified,
    string? Name,
    bool BaseUrlSpecified,
    string? UpstreamBaseUrl,
    bool CredentialSpecified,
    JsonElement? CredentialEnvelope,
    string? CredentialPrefix,
    bool StatusSpecified,
    AccountResourceStatus? Status,
    bool MaxConcurrencySpecified,
    int? MaxConcurrency,
    bool PrioritySpecified,
    int? Priority,
    bool WeightSpecified,
    int? Weight,
    string? Reason)
{
    public override string ToString() => nameof(AccountUpdateWrite);
}

internal sealed record AccountRetireWrite(
    EntityId AccountId,
    long ExpectedVersion,
    string Reason);

internal enum AccountMutationDisposition
{
    Written,
    ValidationFailed,
    Conflict,
    NotFound,
    VersionConflict,
    LifecycleConflict,
    AccountInUse,
}

internal sealed record AccountMutationResult(
    AccountMutationDisposition Disposition,
    bool WasChanged,
    AccountResource? Value,
    AccountResource? Before,
    long? CurrentVersion = null);

internal interface IAccountControlPlaneRepository
{
    ValueTask<AccountSlice> ListAsync(
        AccountCursor? cursor,
        int limit,
        CancellationToken cancellationToken);

    ValueTask<AccountResource?> GetAsync(
        EntityId accountId,
        CancellationToken cancellationToken);

    ValueTask<AccountMutationResult> CreateAsync(
        AccountCreateWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<AccountMutationResult> UpdateAsync(
        AccountUpdateWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<AccountMutationResult> RetireAsync(
        AccountRetireWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}
#pragma warning restore MA0048
