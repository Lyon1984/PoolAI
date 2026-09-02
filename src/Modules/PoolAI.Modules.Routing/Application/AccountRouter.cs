using System.Security.Cryptography;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Routing.Abstractions;
using PoolAI.Modules.Routing.Infrastructure;
using PoolAI.Modules.Supply.Abstractions;

namespace PoolAI.Modules.Routing.Application;

internal sealed class AccountRouter(
    IAccountCandidateReader candidates,
    ICoordinationLeaseSet leases,
    IRouteAffinityStore affinities,
    IAccountCircuitBreaker breakers) : IAccountRouter
{
    private readonly IAccountCandidateReader _candidates =
        candidates ?? throw new ArgumentNullException(nameof(candidates));
    private readonly ICoordinationLeaseSet _leases =
        leases ?? throw new ArgumentNullException(nameof(leases));
    private readonly IRouteAffinityStore _affinities =
        affinities ?? throw new ArgumentNullException(nameof(affinities));
    private readonly IAccountCircuitBreaker _breakers =
        breakers ?? throw new ArgumentNullException(nameof(breakers));

    public async ValueTask<Result<IAccountLease>> RouteAsync(
        RouteAccountCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        string? validationError = Validate(command, out string model);
        if (validationError is not null)
        {
            return Result.Failure<IAccountLease>("invalid_request", validationError);
        }

        Result<IReadOnlyList<AccountCandidate>> candidateResult =
            await LoadCandidatesAsync(command.GroupId, model, cancellationToken)
            .ConfigureAwait(false);
        if (candidateResult.IsFailure)
        {
            return CopyFailure<IAccountLease>(candidateResult.Error);
        }

        Result<IReadOnlyList<AccountCandidate>> breakerResult =
            await FilterClosedBreakersAsync(
                candidateResult.Value,
                cancellationToken).ConfigureAwait(false);
        if (breakerResult.IsFailure)
        {
            return CopyFailure<IAccountLease>(breakerResult.Error);
        }

        IReadOnlyList<AccountCandidate> available = breakerResult.Value;
        if (available.Count == 0)
        {
            return Result.Failure<IAccountLease>(
                "no_available_account",
                "No Account has a closed shared circuit breaker.",
                retryAfterSeconds: 1);
        }

        EntityId? stickyAccountId = await FindStickyAccountAsync(
            command,
            available,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<AccountCandidate> ordered = AccountSelectionStrategy.Order(
            available,
            command with { Model = model },
            stickyAccountId);
        return await AcquireAsync(command, ordered, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<Result<IReadOnlyList<AccountCandidate>>>
        FilterClosedBreakersAsync(
            IReadOnlyList<AccountCandidate> candidates,
            CancellationToken cancellationToken)
    {
        List<AccountCandidate> closed = new(candidates.Count);
        foreach (AccountCandidate candidate in candidates)
        {
            Result<AccountBreakerSnapshot> breaker = await _breakers
                .ReadAsync(candidate.AccountId, cancellationToken)
                .ConfigureAwait(false);
            if (breaker.IsFailure)
            {
                return CopyFailure<IReadOnlyList<AccountCandidate>>(
                    breaker.Error);
            }

            if (breaker.Value.State == AccountBreakerState.Closed)
            {
                closed.Add(candidate);
            }
        }

        return Result.Success<IReadOnlyList<AccountCandidate>>(closed);
    }

    private async ValueTask<Result<IAccountLease>> AcquireAsync(
        RouteAccountCommand command,
        IReadOnlyList<AccountCandidate> ordered,
        CancellationToken cancellationToken)
    {
        string owner = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        TimeSpan? shortestRetry = null;
        foreach (AccountCandidate candidate in ordered)
        {
            CoordinationLeaseAcquireResult acquired = await _leases
                .AcquireAsync(
                    new CoordinationLeaseAcquireRequest(
                        LeaseKey(candidate.AccountId),
                        owner,
                        candidate.ConcurrencyLimit),
                    cancellationToken)
                .ConfigureAwait(false);
            if (acquired.Disposition == CoordinationLeaseAcquireDisposition.Unavailable)
            {
                return CoordinationUnavailable<IAccountLease>();
            }

            if (acquired.Disposition == CoordinationLeaseAcquireDisposition.CapacityExceeded)
            {
                shortestRetry = shortestRetry is null || acquired.RetryAfter < shortestRetry
                    ? acquired.RetryAfter
                    : shortestRetry;
                continue;
            }

            if (acquired.Disposition is not (
                CoordinationLeaseAcquireDisposition.Acquired
                or CoordinationLeaseAcquireDisposition.Renewed))
            {
                return CoordinationUnavailable<IAccountLease>();
            }

            return await CreateLeaseAsync(
                command,
                candidate,
                acquired.ExpiresAt,
                owner,
                cancellationToken).ConfigureAwait(false);
        }

        return Result.Failure<IAccountLease>(
            "account_capacity_unavailable",
            "All schedulable Accounts are at their concurrency limit.",
            RetrySeconds(shortestRetry ?? TimeSpan.FromSeconds(1)));
    }

    private async ValueTask<Result<IAccountLease>> CreateLeaseAsync(
        RouteAccountCommand command,
        AccountCandidate candidate,
        DateTimeOffset expiresAt,
        string owner,
        CancellationToken cancellationToken)
    {
        AccountRoute route = new(
            candidate.GroupId,
            candidate.ChannelId,
            candidate.AccountId,
            MapProvider(candidate.Provider),
            candidate.ClientModel,
            candidate.UpstreamModel,
            new Uri(candidate.UpstreamBaseUrl, UriKind.Absolute),
            new AccountRouteCapabilities(
                candidate.Capabilities.Responses,
                candidate.Capabilities.ChatCompletions,
                candidate.Capabilities.FunctionTools,
                candidate.Capabilities.Streaming),
            expiresAt,
            candidate.ConfigurationVersion,
            candidate.ChannelVersion,
            candidate.AccountVersion,
            candidate.CredentialRevision);
        AccountLease lease = new(_leases, route, owner);
        try
        {
            if (command.SessionAffinityHash is { } sessionHash)
            {
                await _affinities.SetAsync(
                    command.GroupId,
                    sessionHash,
                    new RouteAffinity(
                        candidate.AccountId,
                        command.GroupPolicyVersion,
                        candidate.ConfigurationVersion),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseLeaseQuietlyAsync(lease).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await ReleaseLeaseQuietlyAsync(lease).ConfigureAwait(false);
            return CoordinationUnavailable<IAccountLease>();
        }

        return Result.Success<IAccountLease>(lease);
    }

    private static async ValueTask ReleaseLeaseQuietlyAsync(AccountLease lease)
    {
        try
        {
            _ = await lease.ReleaseAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The Redis lease remains bounded by its TTL. The original
            // affinity-write failure or caller cancellation stays authoritative.
        }
    }

    private async ValueTask<Result<IReadOnlyList<AccountCandidate>>> LoadCandidatesAsync(
        EntityId groupId,
        string model,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<AccountCandidate>> result = await _candidates
            .GetCandidatesAsync(groupId, model, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure || result.Value.Count > 0)
        {
            return result.IsFailure
                ? CopyFailure<IReadOnlyList<AccountCandidate>>(result.Error)
                : HasValidCanonicalCandidates(result.Value, groupId, model)
                    ? result
                    : Result.Failure<IReadOnlyList<AccountCandidate>>(
                        "dependency_unavailable",
                        "The canonical Account candidate set is inconsistent.",
                        retryAfterSeconds: 1);
        }

        return Result.Failure<IReadOnlyList<AccountCandidate>>(
            "no_available_account",
            "No schedulable Account is available for the Group.",
            retryAfterSeconds: 1);
    }

    private async ValueTask<EntityId?> FindStickyAccountAsync(
        RouteAccountCommand command,
        IReadOnlyList<AccountCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (command.SessionAffinityHash is not { } sessionHash)
        {
            return null;
        }

        RouteAffinity? affinity = await _affinities
            .GetAsync(command.GroupId, sessionHash, cancellationToken)
            .ConfigureAwait(false);
        if (affinity is null
            || affinity.GroupPolicyVersion != command.GroupPolicyVersion)
        {
            return null;
        }

        return candidates.Any(candidate =>
                candidate.AccountId == affinity.AccountId
                && candidate.ConfigurationVersion == affinity.SupplyConfigurationVersion)
            ? affinity.AccountId
            : null;
    }

    private static bool HasValidCanonicalCandidates(
        IReadOnlyList<AccountCandidate> candidates,
        EntityId groupId,
        string model)
    {
        HashSet<EntityId> accountIds = [];
        return candidates.All(candidate =>
            candidate.GroupId == groupId
            && candidate.ChannelId.Value != Guid.Empty
            && candidate.AccountId.Value != Guid.Empty
            && candidate.Provider is UpstreamProvider.OpenAi
                or UpstreamProvider.OpenAiCompatible
            && string.Equals(candidate.ClientModel, model, StringComparison.Ordinal)
            && IsValidModel(candidate.UpstreamModel)
            && IsValidCanonicalBaseUri(candidate.UpstreamBaseUrl)
            && candidate.Capabilities is not null
            && candidate.Health is AccountHealth.Healthy or AccountHealth.Degraded
            && candidate.ConcurrencyLimit is >= 1 and <= 10_000
            && candidate.Priority is >= -100_000 and <= 100_000
            && candidate.Weight is >= 1 and <= 100_000
            && candidate.ConfigurationVersion > 0
            && candidate.ChannelVersion > 0
            && candidate.AccountVersion > 0
            && candidate.CredentialRevision > 0
            && accountIds.Add(candidate.AccountId));
    }

    private static bool IsValidModel(string value) =>
        value is { Length: >= 1 and <= 200 }
        && !value.Any(char.IsControl)
        && string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static bool IsValidCanonicalBaseUri(string value)
    {
        if (string.IsNullOrEmpty(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.HostNameType is UriHostNameType.Unknown or UriHostNameType.Basic
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.Port is < 1 or > 65_535)
        {
            return false;
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            && (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                || uri.IsLoopback);
    }

    private static AccountRouteProvider MapProvider(UpstreamProvider provider) =>
        provider switch
        {
            UpstreamProvider.OpenAi => AccountRouteProvider.OpenAi,
            UpstreamProvider.OpenAiCompatible =>
                AccountRouteProvider.OpenAiCompatible,
            _ => throw new InvalidOperationException(
                "The candidate Account provider is invalid."),
        };

    private static string? Validate(
        RouteAccountCommand command,
        out string normalizedModel)
    {
        normalizedModel = command.Model?.Trim() ?? string.Empty;
        if (normalizedModel.Length is < 1 or > 200
            || normalizedModel.Any(char.IsControl))
        {
            return "The model must contain between 1 and 200 non-control characters.";
        }

        if (command.GroupPolicyVersion <= 0)
        {
            return "The Group policy version must be positive.";
        }

        if (command.SessionAffinityHash is { } hash
            && (hash.Length != 32
                || !hash.All(static character => character is
                    >= '0' and <= '9'
                    or >= 'a' and <= 'f')))
        {
            return "The session affinity hash must be 32 lowercase hexadecimal characters.";
        }

        return null;
    }

    internal static string LeaseKey(EntityId accountId) =>
        $"lease:account:v1:{{{accountId.Value:D}}}";

    private static long RetrySeconds(TimeSpan retryAfter) =>
        Math.Max(1, checked((long)Math.Ceiling(retryAfter.TotalSeconds)));

    private static Result<T> CoordinationUnavailable<T>() =>
        Result.Failure<T>(
            "coordination_unavailable",
            "Redis coordination is temporarily unavailable.",
            retryAfterSeconds: 1);

    private static Result<T> CopyFailure<T>(ResultError error) =>
        Result.Failure<T>(
            error.Code,
            error.Description,
            error.RetryAfterSeconds,
            error.ETag,
            error.Presentation);
}
