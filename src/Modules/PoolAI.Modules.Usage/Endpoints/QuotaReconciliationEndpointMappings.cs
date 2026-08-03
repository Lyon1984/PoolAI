using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PoolAI.BuildingBlocks;
using PoolAI.Contracts.Generated;
using PoolAI.Modules.Usage.Application;

namespace PoolAI.Modules.Usage.Endpoints;

public static class QuotaReconciliationEndpointMappings
{
    public static IEndpointRouteBuilder MapQuotaReconciliationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet(
                "/api/v1/admin/groups/{groupId:guid}/quota/reconciliation",
                GetAsync)
            .RequireAuthorization(static policy => policy
                .RequireAuthenticatedUser()
                .RequireRole("admin", "operator", "auditor"))
            .WithName("adminGetGroupQuotaReconciliation");
        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        HttpContext context,
        IGetGroupQuotaReconciliationUseCase useCase,
        Guid groupId,
        string? period_id)
    {
        if (groupId == Guid.Empty)
        {
            return Problem(
                context,
                StatusCodes.Status400BadRequest,
                "invalid_request",
                "Invalid request",
                "The Group path identifier must be a non-empty UUID.",
                errors: FieldError(
                    "/groupId",
                    "The Group path identifier must be a non-empty UUID."));
        }

        EntityId? periodId = null;
        if (period_id is not null)
        {
            if (!Guid.TryParse(period_id, out Guid parsedPeriodId)
                || parsedPeriodId == Guid.Empty)
            {
                return Problem(
                    context,
                    StatusCodes.Status400BadRequest,
                    "invalid_request",
                    "Invalid request",
                    "The period_id query value must be a non-empty UUID.",
                    errors: FieldError(
                        "/period_id",
                        "The period_id query value must be a non-empty UUID."));
            }

            periodId = new EntityId(parsedPeriodId);
        }

        Result<QuotaReconciliationView> result = await useCase.ExecuteAsync(
            new EntityId(groupId),
            periodId,
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return string.Equals(
                result.Error.Code,
                "resource_not_found",
                StringComparison.Ordinal)
                ? Problem(
                    context,
                    StatusCodes.Status404NotFound,
                    "resource_not_found",
                    "Resource not found",
                    "The requested resource was not found.")
                : Problem(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "internal_error",
                    "Internal error",
                    "The request could not be completed safely.");
        }

        return Results.Ok(ToContract(result.Value));
    }

    private static GroupQuotaReconciliation ToContract(QuotaReconciliationView view)
    {
        var fact = view.Authoritative;
        var projection = view.UsageProjection;
        return new GroupQuotaReconciliation
        {
            GroupId = fact.GroupId.Value,
            PeriodId = fact.PeriodId.Value,
            LedgerTotalTokens = Token(fact.LedgerTotalTokens),
            LedgerConsumedTokens = Token(fact.LedgerConsumedTokens),
            LedgerReservedTokens = Token(fact.LedgerReservedTokens),
            FactConsumedTokens = Token(fact.FactConsumedTokens),
            PendingReservationTokens = Token(fact.PendingReservationTokens),
            ConsumedVariance = Token(view.ConsumedVariance),
            ReservedVariance = Token(view.ReservedVariance),
            IsReconciled = view.ConsumedVariance.IsZero
                && view.ReservedVariance.IsZero,
            UsageProjection = new GroupQuotaUsageProjectionReconciliation
            {
                Status = Status(projection.Status),
                ExpectedConsumedTokens = Token(projection.ExpectedConsumedTokens),
                ProjectedConsumedTokens = Token(projection.ProjectedConsumedTokens),
                ConsumedVariance = Token(projection.ConsumedVariance),
                CheckpointSourceEventSequence = projection
                    .CheckpointSourceEventSequence
                    .ToString(CultureInfo.InvariantCulture),
                LatestSourceEventSequence = projection
                    .LatestSourceEventSequence
                    .ToString(CultureInfo.InvariantCulture),
                DataThrough = projection.DataThrough,
            },
            CheckedAt = fact.CheckedAt,
            DataWatermark = fact.LatestPeriodEventOccurredAt,
        };
    }

    private static string Token(System.Numerics.BigInteger value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static string Status(UsageProjectionReconciliationStatus status) => status switch
    {
        UsageProjectionReconciliationStatus.Blocked => "blocked",
        UsageProjectionReconciliationStatus.NotStarted => "not_started",
        UsageProjectionReconciliationStatus.Mismatched => "mismatched",
        UsageProjectionReconciliationStatus.Lagging => "lagging",
        UsageProjectionReconciliationStatus.Reconciled => "reconciled",
        _ => throw new InvalidOperationException(
            "The Usage projection reconciliation status is invalid."),
    };

    private static IResult Problem(
        HttpContext context,
        int status,
        string code,
        string title,
        string detail,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? errors = null)
    {
        ControlPlaneProblem problem = new()
        {
            Type = new Uri(
                $"https://poolai.example/problems/{code.Replace('_', '-')}",
                UriKind.Absolute),
            Title = title,
            Status = status,
            Detail = detail,
            Instance = context.Request.Path.Value ?? "/",
            Code = code,
            RequestId = RequestId(context),
            Retryable = false,
            Errors = errors is null
                ? default
                : new Optional<IReadOnlyDictionary<string, IReadOnlyList<string>>>(errors),
        };
        return Results.Json(
            problem,
            statusCode: status,
            contentType: "application/problem+json");
    }

    private static Guid RequestId(HttpContext context) =>
        Guid.TryParse(context.TraceIdentifier, out Guid requestId)
            && requestId != Guid.Empty
            ? requestId
            : throw new InvalidOperationException("The API request identifier is invalid.");

    private static Dictionary<string, IReadOnlyList<string>> FieldError(
        string pointer,
        string message) => new Dictionary<string, IReadOnlyList<string>>(
            StringComparer.Ordinal)
        {
            [pointer] = [message],
        };
}
