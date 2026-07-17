#pragma warning disable MA0051 // Endpoint adapters keep contract validation and mapping visible.

using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using PoolAI.Contracts.Generated;
using PoolAI.Modules.SubscriptionAccess.Application;

namespace PoolAI.Modules.SubscriptionAccess.Endpoints;

public static class SubscriptionEndpointMappings
{
    public static IEndpointRouteBuilder MapSubscriptionAccessEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        RouteGroupBuilder api = endpoints.MapGroup("/api/v1");

        RouteGroupBuilder me = api.MapGroup("/me");
        AddActorFilter(me);
        me.MapGet("/subscriptions", ListMySubscriptionsAsync)
            .RequireAuthorization(RequireAnyUserRole)
            .WithName("listMySubscriptions");

        RouteGroupBuilder templates = api.MapGroup("/admin/subscription-templates");
        AddActorFilter(templates);
        templates.MapGet("/", ListTemplatesAsync)
            .RequireAuthorization(RequireAdminRead)
            .WithName("adminListSubscriptionTemplates");
        templates.MapPost("/", CreateTemplateAsync)
            .RequireAuthorization(RequireAdminOrOperator)
            .WithName("adminCreateSubscriptionTemplate");
        templates.MapGet("/{templateId:guid}", GetTemplateAsync)
            .RequireAuthorization(RequireAdminRead)
            .WithName("adminGetSubscriptionTemplate");
        templates.MapMethods("/{templateId:guid}", [HttpMethods.Patch], UpdateTemplateAsync)
            .RequireAuthorization(RequireAdminOrOperator)
            .WithName("adminUpdateSubscriptionTemplate");
        templates.MapDelete("/{templateId:guid}", RetireTemplateAsync)
            .RequireAuthorization(RequireAdminOrOperator)
            .WithName("adminRetireSubscriptionTemplate");

        RouteGroupBuilder subscriptions = api.MapGroup("/admin/subscriptions");
        AddActorFilter(subscriptions);
        subscriptions.MapGet("/", ListSubscriptionsAsync)
            .RequireAuthorization(RequireAdminRead)
            .WithName("adminListSubscriptions");
        subscriptions.MapPost("/", AssignSubscriptionAsync)
            .RequireAuthorization(RequireAdminOrOperator)
            .WithName("adminAssignSubscription");
        subscriptions.MapGet("/{subscriptionId:guid}", GetSubscriptionAsync)
            .RequireAuthorization(RequireAdminRead)
            .WithName("adminGetSubscription");
        subscriptions.MapMethods("/{subscriptionId:guid}", [HttpMethods.Patch], UpdateSubscriptionAsync)
            .RequireAuthorization(RequireAdminOrOperator)
            .WithName("adminUpdateSubscription");
        return endpoints;
    }

    private static void AddActorFilter(RouteGroupBuilder group) => group.AddEndpointFilter(
        static async (invocation, next) => SubscriptionHttp.TryGetActor(
            invocation.HttpContext,
            out _)
            ? await next(invocation).ConfigureAwait(false)
            : SubscriptionHttp.InvalidUserToken(invocation.HttpContext));

    private static void RequireAdminRead(AuthorizationPolicyBuilder policy) =>
        policy.RequireAuthenticatedUser().RequireRole("admin", "operator", "auditor");

    private static void RequireAdminOrOperator(AuthorizationPolicyBuilder policy) =>
        policy.RequireAuthenticatedUser().RequireRole("admin", "operator");

    private static void RequireAnyUserRole(AuthorizationPolicyBuilder policy) =>
        policy.RequireAuthenticatedUser().RequireRole("admin", "operator", "auditor", "user");

    private static async Task<IResult> ListMySubscriptionsAsync(
        HttpContext context,
        IListSubscriptionsUseCase useCase)
    {
        Result<PoolAI.Modules.SubscriptionAccess.Application.SubscriptionPage> result =
            await useCase.ExecuteAsync(
            new ListSubscriptionsQuery(
                SubscriptionHttp.RequireActor(context),
                Cursor: null,
                Limit: 100,
                UserId: null,
                GroupId: null,
                IsSelfQuery: true),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return SubscriptionHttp.FromError(context, result.Error);
        }

        return Results.Ok(new
        {
            data = result.Value.Data.Select(SubscriptionHttp.ToContract).ToArray(),
        });
    }

    private static async Task<IResult> ListTemplatesAsync(
        HttpContext context,
        IListSubscriptionTemplatesUseCase useCase,
        string? cursor,
        string? limit)
    {
        int pageSize = 50;
        if (limit is not null
            && (!int.TryParse(
                    limit,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out pageSize)
                || pageSize is < 1 or > 100))
        {
            return SubscriptionHttp.InvalidRequest(
                context,
                "/limit",
                "limit must be between 1 and 100.");
        }

        Result<PoolAI.Modules.SubscriptionAccess.Application.SubscriptionTemplatePage> result =
            await useCase.ExecuteAsync(
            new ListSubscriptionTemplatesQuery(
                SubscriptionHttp.RequireActor(context),
                cursor,
                pageSize),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return SubscriptionHttp.FromError(context, result.Error);
        }

        PoolAI.Modules.SubscriptionAccess.Application.SubscriptionTemplatePage page = result.Value;
        return Results.Ok(new PoolAI.Contracts.Generated.SubscriptionTemplatePage
        {
            Data = page.Data.Select(SubscriptionHttp.ToContract).ToArray(),
            Page = SubscriptionHttp.Page(page.NextCursor, page.HasMore),
        });
    }

    private static async Task<IResult> GetTemplateAsync(
        HttpContext context,
        IGetSubscriptionTemplateUseCase useCase,
        Guid templateId)
    {
        if (!SubscriptionHttp.TryGetEntityId(
                context,
                templateId,
                "templateId",
                out EntityId id,
                out IResult? failure))
        {
            return failure!;
        }

        Result<SubscriptionTemplateView> result = await useCase.ExecuteAsync(
            new GetSubscriptionTemplateQuery(SubscriptionHttp.RequireActor(context), id),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return SubscriptionHttp.FromError(context, result.Error);
        }

        context.Response.Headers.ETag = ETag(result.Value.Version);
        return Results.Ok(SubscriptionHttp.ToContract(result.Value));
    }

    private static async Task<IResult> CreateTemplateAsync(
        HttpContext context,
        ICreateSubscriptionTemplateUseCase useCase,
        SubscriptionTemplateCreateRequest request)
    {
        IResult? failure = SubscriptionHttp.RequireContentType(context, "application/json");
        if (failure is not null)
        {
            return failure;
        }

        Dictionary<string, IReadOnlyList<string>> errors = Validate(request);
        if (errors.Count != 0)
        {
            return SubscriptionHttp.ValidationProblem(context, errors);
        }

        if (!SubscriptionHttp.TryGetIdempotencyKey(context, out string? key, out failure))
        {
            return failure!;
        }

        Result<SubscriptionCommandOutcome<SubscriptionTemplateView>> result =
            await useCase.ExecuteAsync(
                new CreateSubscriptionTemplateCommand(
                    SubscriptionHttp.RequestId(context),
                    SubscriptionHttp.RequireActor(context),
                    key!,
                    new EntityId(request.GroupId),
                    request.Name,
                    request.Description.HasValue ? request.Description.Value : null,
                    checked((int)request.DefaultDurationDays),
                    SubscriptionHttp.RemoteIp(context),
                    SubscriptionHttp.UserAgent(context)),
                context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return SubscriptionHttp.FromError(context, result.Error);
        }

        SubscriptionCommandOutcome<SubscriptionTemplateView> outcome = result.Value;
        context.Response.Headers.ETag = outcome.ETag;
        context.Response.Headers.Location = outcome.Location!;
        return Results.Json(
            SubscriptionHttp.ToContract(outcome.Value),
            statusCode: outcome.StatusCode);
    }

    private static async Task<IResult> UpdateTemplateAsync(
        HttpContext context,
        IUpdateSubscriptionTemplateUseCase useCase,
        Guid templateId,
        SubscriptionTemplateUpdateRequest request)
    {
        IResult? failure = SubscriptionHttp.RequireContentType(
            context,
            "application/merge-patch+json");
        if (failure is not null)
        {
            return failure;
        }

        if (!SubscriptionHttp.TryGetEntityId(
                context,
                templateId,
                "templateId",
                out EntityId id,
                out failure))
        {
            return failure!;
        }

        Dictionary<string, IReadOnlyList<string>> errors = Validate(request);
        if (errors.Count != 0)
        {
            return SubscriptionHttp.ValidationProblem(context, errors);
        }

        if (!SubscriptionHttp.TryGetIdempotencyKey(context, out string? key, out failure)
            || !SubscriptionHttp.TryGetExpectedVersion(context, out long version, out failure))
        {
            return failure!;
        }

        SubscriptionTemplateLifecycle? status = request.Status.HasValue
            ? request.Status.Value switch
            {
                "active" => SubscriptionTemplateLifecycle.Active,
                "disabled" => SubscriptionTemplateLifecycle.Disabled,
                _ => null,
            }
            : null;
        Result<SubscriptionCommandOutcome<SubscriptionTemplateView>> result =
            await useCase.ExecuteAsync(
                new UpdateSubscriptionTemplateCommand(
                    SubscriptionHttp.RequestId(context),
                    SubscriptionHttp.RequireActor(context),
                    key!,
                    id,
                    version,
                    request.Name.HasValue,
                    request.Name.Value,
                    request.Description.HasValue,
                    request.Description.Value,
                    request.DefaultDurationDays.HasValue,
                    request.DefaultDurationDays.HasValue
                        ? checked((int)request.DefaultDurationDays.Value)
                        : null,
                    request.Status.HasValue,
                    status,
                    request.Reason.HasValue ? request.Reason.Value : null,
                    SubscriptionHttp.RemoteIp(context),
                    SubscriptionHttp.UserAgent(context)),
                context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return SubscriptionHttp.FromError(context, result.Error);
        }

        context.Response.Headers.ETag = result.Value.ETag;
        return Results.Json(
            SubscriptionHttp.ToContract(result.Value.Value),
            statusCode: result.Value.StatusCode);
    }

    private static async Task<IResult> RetireTemplateAsync(
        HttpContext context,
        IRetireSubscriptionTemplateUseCase useCase,
        Guid templateId)
    {
        if (!SubscriptionHttp.TryGetEntityId(
                context,
                templateId,
                "templateId",
                out EntityId id,
                out IResult? failure)
            || !SubscriptionHttp.TryGetIdempotencyKey(context, out string? key, out failure)
            || !SubscriptionHttp.TryGetExpectedVersion(context, out long version, out failure))
        {
            return failure!;
        }

        string reason = context.Request.Headers["X-Change-Reason"].ToString();
        if (!SubscriptionHttp.IsValidReason(reason))
        {
            return SubscriptionHttp.ValidationProblem(
                context,
                SubscriptionHttp.FieldError(
                    "/headers/X-Change-Reason",
                    "A non-blank change reason of at most 500 characters is required."));
        }

        Result<SubscriptionCommandOutcome> result = await useCase.ExecuteAsync(
            new RetireSubscriptionTemplateCommand(
                SubscriptionHttp.RequestId(context),
                SubscriptionHttp.RequireActor(context),
                key!,
                id,
                version,
                reason,
                SubscriptionHttp.RemoteIp(context),
                SubscriptionHttp.UserAgent(context)),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return SubscriptionHttp.FromError(context, result.Error);
        }

        context.Response.Headers.ETag = result.Value.ETag;
        return Results.StatusCode(result.Value.StatusCode);
    }

    private static async Task<IResult> ListSubscriptionsAsync(
        HttpContext context,
        IListSubscriptionsUseCase useCase,
        string? cursor,
        string? limit,
        [FromQuery(Name = "user_id")] string? userId,
        [FromQuery(Name = "group_id")] string? groupId)
    {
        int pageSize = 50;
        if (limit is not null
            && (!int.TryParse(
                    limit,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out pageSize)
                || pageSize is < 1 or > 100))
        {
            return SubscriptionHttp.InvalidRequest(
                context,
                "/limit",
                "limit must be between 1 and 100.");
        }

        if (!TryParseOptionalEntityId(userId, out EntityId? parsedUserId))
        {
            return SubscriptionHttp.InvalidRequest(
                context,
                "/user_id",
                "user_id must be a non-empty UUID when supplied.");
        }

        if (!TryParseOptionalEntityId(groupId, out EntityId? parsedGroupId))
        {
            return SubscriptionHttp.InvalidRequest(
                context,
                "/group_id",
                "group_id must be a non-empty UUID when supplied.");
        }

        Result<PoolAI.Modules.SubscriptionAccess.Application.SubscriptionPage> result =
            await useCase.ExecuteAsync(
            new ListSubscriptionsQuery(
                SubscriptionHttp.RequireActor(context),
                cursor,
                pageSize,
                parsedUserId,
                parsedGroupId,
                IsSelfQuery: false),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return SubscriptionHttp.FromError(context, result.Error);
        }

        PoolAI.Modules.SubscriptionAccess.Application.SubscriptionPage page = result.Value;
        return Results.Ok(new PoolAI.Contracts.Generated.SubscriptionPage
        {
            Data = page.Data.Select(SubscriptionHttp.ToContract).ToArray(),
            Page = SubscriptionHttp.Page(page.NextCursor, page.HasMore),
        });
    }

    private static bool TryParseOptionalEntityId(string? value, out EntityId? entityId)
    {
        entityId = null;
        if (value is null)
        {
            return true;
        }

        if (!Guid.TryParseExact(value, "D", out Guid parsed) || parsed == Guid.Empty)
        {
            return false;
        }

        entityId = new EntityId(parsed);
        return true;
    }

    private static async Task<IResult> GetSubscriptionAsync(
        HttpContext context,
        IGetSubscriptionUseCase useCase,
        Guid subscriptionId)
    {
        if (!SubscriptionHttp.TryGetEntityId(
                context,
                subscriptionId,
                "subscriptionId",
                out EntityId id,
                out IResult? failure))
        {
            return failure!;
        }

        Result<SubscriptionView> result = await useCase.ExecuteAsync(
            new GetSubscriptionQuery(SubscriptionHttp.RequireActor(context), id),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return SubscriptionHttp.FromError(context, result.Error);
        }

        context.Response.Headers.ETag = ETag(result.Value.Version);
        return Results.Ok(SubscriptionHttp.ToContract(result.Value));
    }

    private static async Task<IResult> AssignSubscriptionAsync(
        HttpContext context,
        IAssignSubscriptionUseCase useCase,
        SubscriptionCreateRequest request)
    {
        IResult? failure = SubscriptionHttp.RequireContentType(context, "application/json");
        if (failure is not null)
        {
            return failure;
        }

        Dictionary<string, IReadOnlyList<string>> errors = Validate(request);
        if (errors.Count != 0)
        {
            return SubscriptionHttp.ValidationProblem(context, errors);
        }

        if (!SubscriptionHttp.TryGetIdempotencyKey(context, out string? key, out failure))
        {
            return failure!;
        }

        Result<SubscriptionCommandOutcome<SubscriptionView>> result = await useCase.ExecuteAsync(
            new AssignSubscriptionCommand(
                SubscriptionHttp.RequestId(context),
                SubscriptionHttp.RequireActor(context),
                key!,
                new EntityId(request.UserId),
                new EntityId(request.TemplateId),
                request.StartsAt.HasValue ? request.StartsAt.Value : null,
                request.ExpiresAt.HasValue ? request.ExpiresAt.Value : null,
                request.Reason,
                SubscriptionHttp.RemoteIp(context),
                SubscriptionHttp.UserAgent(context)),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return SubscriptionHttp.FromError(context, result.Error);
        }

        context.Response.Headers.ETag = result.Value.ETag;
        context.Response.Headers.Location = result.Value.Location!;
        return Results.Json(
            SubscriptionHttp.ToContract(result.Value.Value),
            statusCode: result.Value.StatusCode);
    }

    private static async Task<IResult> UpdateSubscriptionAsync(
        HttpContext context,
        IUpdateSubscriptionUseCase useCase,
        Guid subscriptionId,
        SubscriptionUpdateRequest request)
    {
        IResult? failure = SubscriptionHttp.RequireContentType(
            context,
            "application/merge-patch+json");
        if (failure is not null)
        {
            return failure;
        }

        if (!SubscriptionHttp.TryGetEntityId(
                context,
                subscriptionId,
                "subscriptionId",
                out EntityId id,
                out failure))
        {
            return failure!;
        }

        Dictionary<string, IReadOnlyList<string>> errors = Validate(request);
        if (errors.Count != 0)
        {
            return SubscriptionHttp.ValidationProblem(context, errors);
        }

        if (!SubscriptionHttp.TryGetIdempotencyKey(context, out string? key, out failure)
            || !SubscriptionHttp.TryGetExpectedVersion(context, out long version, out failure))
        {
            return failure!;
        }

        SubscriptionLifecycle? status = request.Status.HasValue
            ? request.Status.Value switch
            {
                SubscriptionStatus.Active => SubscriptionLifecycle.Active,
                SubscriptionStatus.Suspended => SubscriptionLifecycle.Suspended,
                SubscriptionStatus.Revoked => SubscriptionLifecycle.Revoked,
                _ => null,
            }
            : null;
        Result<SubscriptionCommandOutcome<SubscriptionView>> result = await useCase.ExecuteAsync(
            new UpdateSubscriptionCommand(
                SubscriptionHttp.RequestId(context),
                SubscriptionHttp.RequireActor(context),
                key!,
                id,
                version,
                request.StartsAt.HasValue,
                request.StartsAt.HasValue ? request.StartsAt.Value : null,
                request.ExpiresAt.HasValue,
                request.ExpiresAt.HasValue ? request.ExpiresAt.Value : null,
                request.Status.HasValue,
                status,
                request.Reason,
                SubscriptionHttp.RemoteIp(context),
                SubscriptionHttp.UserAgent(context)),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return SubscriptionHttp.FromError(context, result.Error);
        }

        context.Response.Headers.ETag = result.Value.ETag;
        return Results.Json(
            SubscriptionHttp.ToContract(result.Value.Value),
            statusCode: result.Value.StatusCode);
    }

    private static Dictionary<string, IReadOnlyList<string>> Validate(
        SubscriptionTemplateCreateRequest request)
    {
        Dictionary<string, IReadOnlyList<string>> errors = new(StringComparer.Ordinal);
        if (request.GroupId == Guid.Empty)
        {
            errors["/group_id"] = ["A non-empty Group UUID is required."];
        }

        if (!SubscriptionHttp.IsValidName(request.Name))
        {
            errors["/name"] = ["A name of at most 100 characters is required."];
        }

        if (request.Description.HasValue
            && !SubscriptionHttp.IsValidDescription(request.Description.Value))
        {
            errors["/description"] = ["description must contain at most 1000 characters."];
        }

        if (request.DefaultDurationDays is < 1 or > 3650)
        {
            errors["/default_duration_days"] = ["default_duration_days must be between 1 and 3650."];
        }

        return errors;
    }

    private static Dictionary<string, IReadOnlyList<string>> Validate(
        SubscriptionTemplateUpdateRequest request)
    {
        Dictionary<string, IReadOnlyList<string>> errors = new(StringComparer.Ordinal);
        if (!request.Name.HasValue
            && !request.Description.HasValue
            && !request.DefaultDurationDays.HasValue
            && !request.Status.HasValue)
        {
            errors["/"] = ["At least one mutable Template field is required."];
        }

        if (request.Name.HasValue && !SubscriptionHttp.IsValidName(request.Name.Value))
        {
            errors["/name"] = ["A name of at most 100 characters is required."];
        }

        if (request.Description.HasValue
            && !SubscriptionHttp.IsValidDescription(request.Description.Value))
        {
            errors["/description"] = ["description must contain at most 1000 characters."];
        }

        if (request.DefaultDurationDays.HasValue
            && request.DefaultDurationDays.Value is < 1 or > 3650)
        {
            errors["/default_duration_days"] = ["default_duration_days must be between 1 and 3650."];
        }

        if (request.Status.HasValue && request.Status.Value is not ("active" or "disabled"))
        {
            errors["/status"] = ["status must be active or disabled; use DELETE to retire."];
        }

        if (request.Status.HasValue && !SubscriptionHttp.IsValidReason(request.Reason.Value))
        {
            errors["/reason"] = ["A non-blank reason of at most 500 characters is required."];
        }
        else if (request.Reason.HasValue && !SubscriptionHttp.IsValidReason(request.Reason.Value))
        {
            errors["/reason"] = ["reason must be non-blank and at most 500 characters."];
        }

        return errors;
    }

    private static Dictionary<string, IReadOnlyList<string>> Validate(
        SubscriptionCreateRequest request)
    {
        Dictionary<string, IReadOnlyList<string>> errors = new(StringComparer.Ordinal);
        if (request.UserId == Guid.Empty)
        {
            errors["/user_id"] = ["A non-empty User UUID is required."];
        }

        if (request.TemplateId == Guid.Empty)
        {
            errors["/template_id"] = ["A non-empty Template UUID is required."];
        }

        if (!SubscriptionHttp.IsValidReason(request.Reason))
        {
            errors["/reason"] = ["A non-blank reason of at most 500 characters is required."];
        }

        if (request.StartsAt.HasValue
            && request.ExpiresAt.HasValue
            && request.ExpiresAt.Value is not null
            && request.ExpiresAt.Value <= request.StartsAt.Value)
        {
            errors["/expires_at"] = ["expires_at must be after starts_at."];
        }

        return errors;
    }

    private static Dictionary<string, IReadOnlyList<string>> Validate(
        SubscriptionUpdateRequest request)
    {
        Dictionary<string, IReadOnlyList<string>> errors = new(StringComparer.Ordinal);
        if (!request.StartsAt.HasValue && !request.ExpiresAt.HasValue && !request.Status.HasValue)
        {
            errors["/"] = ["At least one mutable Subscription field is required."];
        }

        if (!SubscriptionHttp.IsValidReason(request.Reason))
        {
            errors["/reason"] = ["A non-blank reason of at most 500 characters is required."];
        }

        if (request.StartsAt.HasValue
            && request.ExpiresAt.HasValue
            && request.ExpiresAt.Value <= request.StartsAt.Value)
        {
            errors["/expires_at"] = ["expires_at must be after starts_at."];
        }

        return errors;
    }

    private static string ETag(long version) => $"\"v{version}\"";
}

#pragma warning restore MA0051
