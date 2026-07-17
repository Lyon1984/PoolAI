using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using PoolAI.Application.Orchestration;
using PoolAI.BuildingBlocks;
using PoolAI.Contracts.Generated;

namespace PoolAI.Api;

internal static class UserGroupPoolEndpointMappings
{
    internal static IEndpointRouteBuilder MapUserGroupPoolEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/api/v1/me/group-pools", ListAsync)
            .RequireAuthorization(RequireAnyUserRole)
            .WithName("listMyGroupPools");
        return endpoints;
    }

    private static void RequireAnyUserRole(AuthorizationPolicyBuilder policy) =>
        policy.RequireAuthenticatedUser().RequireRole("admin", "operator", "auditor", "user");

    private static async Task<IResult> ListAsync(
        HttpContext context,
        IListUserGroupPoolsUseCase useCase)
    {
        string? subject = context.User.FindFirstValue("sub")
            ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(subject, out Guid userId) || userId == Guid.Empty)
        {
            return new ProblemResult(
                401,
                "invalid_user_token",
                "Authentication required",
                "The user access token is invalid or expired.",
                Retryable: false);
        }

        Result<IReadOnlyList<UserGroupPoolView>> result = await useCase.ExecuteAsync(
            new ListUserGroupPoolsQuery(new EntityId(userId)),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return Failure(result.Error);
        }

        return Results.Ok(new
        {
            data = result.Value.Select(ToContract).ToArray(),
        });
    }

    private static GroupPoolSummary ToContract(UserGroupPoolView view) => new()
    {
        GroupId = view.GroupId.Value,
        GroupName = view.GroupName,
        SubscriptionId = view.SubscriptionId.Value,
        PlanName = view.PlanName,
        AccessExpiresAt = view.AccessExpiresAt,
        QuotaStatus = view.QuotaStatus,
        TotalTokens = view.TotalTokens.ToString(CultureInfo.InvariantCulture),
        ConsumedTokens = view.ConsumedTokens.ToString(CultureInfo.InvariantCulture),
        ReservedTokens = view.ReservedTokens.ToString(CultureInfo.InvariantCulture),
        RemainingTokens = view.RemainingTokens.ToString(CultureInfo.InvariantCulture),
        UpdatedAt = view.UpdatedAt,
    };

    private static ProblemResult Failure(ResultError error) => error.Code switch
    {
        "coordination_unavailable" => new ProblemResult(
            503,
            error.Code,
            "Coordination unavailable",
            "A required coordination dependency is temporarily unavailable.",
            Retryable: true,
            error.RetryAfterSeconds ?? 1),
        "dependency_unavailable" or "service_unavailable" => new ProblemResult(
            503,
            "dependency_unavailable",
            "Dependency unavailable",
            "A required dependency is temporarily unavailable.",
            Retryable: true,
            error.RetryAfterSeconds ?? 1),
        _ => new ProblemResult(
            500,
            "internal_error",
            "Internal error",
            "The request could not be completed.",
            Retryable: false),
    };

    private sealed record ProblemResult(
        int Status,
        string Code,
        string Title,
        string Detail,
        bool Retryable,
        long? RetryAfterSeconds = null) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => ControlPlaneProblemWriter.WriteAsync(
            httpContext,
            Status,
            Code,
            Title,
            Detail,
            Retryable,
            RetryAfterSeconds);
    }
}
