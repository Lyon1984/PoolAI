using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PoolAI.Contracts.Generated;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application;

namespace PoolAI.Modules.GroupQuota.Endpoints;

public static class GroupQuotaEndpointMappings
{
    public static IEndpointRouteBuilder MapGroupQuotaEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        RouteGroupBuilder groups = endpoints.MapGroup("/api/v1/admin/groups");
        groups.AddEndpointFilter(static async (invocation, next) =>
        {
            HttpContext context = invocation.HttpContext;
            return GroupQuotaHttp.TryGetActor(context, out _)
                ? await next(invocation).ConfigureAwait(false)
                : GroupQuotaHttp.InvalidUserToken(context);
        });
        groups.MapGet("/", ListGroupsAsync)
            .RequireAuthorization(RequireAnyReadRole)
            .WithName("adminListGroups");
        groups.MapPost("/", CreateGroupAsync)
            .RequireAuthorization(RequireAdmin)
            .WithName("adminCreateGroup");
        groups.MapGet("/{groupId:guid}", GetGroupAsync)
            .RequireAuthorization(RequireAnyReadRole)
            .WithName("adminGetGroup");
        groups.MapMethods("/{groupId:guid}", [HttpMethods.Patch], UpdateGroupAsync)
            .RequireAuthorization(RequireAdmin)
            .WithName("adminUpdateGroup");
        return endpoints;
    }

    private static void RequireAdmin(AuthorizationPolicyBuilder policy) =>
        policy.RequireAuthenticatedUser().RequireRole("admin");

    private static void RequireAnyReadRole(AuthorizationPolicyBuilder policy) =>
        policy.RequireAuthenticatedUser().RequireRole("admin", "operator", "auditor");

    private static async Task<IResult> ListGroupsAsync(
        HttpContext context,
        IListGroupsUseCase useCase,
        string? cursor,
        string? limit)
    {
        int parsedLimit = 50;
        if (limit is not null
            && (!int.TryParse(
                    limit,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out parsedLimit)
                || parsedLimit is < 1 or > 100))
        {
            return GroupQuotaHttp.InvalidRequestProblem(
                context,
                GroupQuotaHttp.FieldError(
                    "/limit",
                    "The pagination limit must be between 1 and 100."));
        }

        Result<PoolAI.Modules.GroupQuota.Application.GroupPage> result =
            await useCase.ExecuteAsync(
                new ListGroupsQuery(
                    GroupQuotaHttp.RequireActor(context),
                    cursor,
                    parsedLimit),
                context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return GroupQuotaHttp.FromError(context, result.Error);
        }

        PoolAI.Modules.GroupQuota.Application.GroupPage page = result.Value;
        return Results.Ok(new PoolAI.Contracts.Generated.GroupPage
        {
            Data = page.Data.Select(GroupQuotaHttp.ToContract).ToArray(),
            Page = new PageInfo
            {
                HasMore = page.HasMore,
                NextCursor = page.NextCursor is null ? default : page.NextCursor,
            },
        });
    }

    private static async Task<IResult> GetGroupAsync(
        HttpContext context,
        IGetGroupUseCase useCase,
        Guid groupId)
    {
        if (!GroupQuotaHttp.TryGetEntityId(
                context,
                groupId,
                out EntityId entityId,
                out IResult? failure))
        {
            return failure!;
        }

        Result<GroupView> result = await useCase.ExecuteAsync(
            new GetGroupQuery(GroupQuotaHttp.RequireActor(context), entityId),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return GroupQuotaHttp.FromError(context, result.Error);
        }

        context.Response.Headers.ETag = GroupQuotaHttp.ETag(result.Value.Version);
        return Results.Ok(GroupQuotaHttp.ToContract(result.Value));
    }

    private static async Task<IResult> CreateGroupAsync(
        HttpContext context,
        ICreateGroupUseCase useCase,
        GroupCreateRequest request)
    {
        IResult? failure = GroupQuotaHttp.RequireContentType(context, "application/json");
        if (failure is not null)
        {
            return failure;
        }

        IReadOnlyDictionary<string, IReadOnlyList<string>> errors =
            GroupQuotaHttp.Validate(request);
        if (errors.Count != 0)
        {
            return GroupQuotaHttp.ValidationProblem(context, errors);
        }

        if (!GroupQuotaHttp.TryGetIdempotencyKey(
                context,
                out string? idempotencyKey,
                out failure))
        {
            return failure!;
        }

        Result<GroupCommandOutcome> result = await useCase.ExecuteAsync(
            new CreateGroupCommand(
                GroupQuotaHttp.RequestId(context),
                GroupQuotaHttp.RequireActor(context),
                idempotencyKey!,
                request.Name,
                request.Description.HasValue ? request.Description.Value : null,
                request.TotalTokens,
                GroupQuotaHttp.RemoteIp(context),
                GroupQuotaHttp.UserAgent(context)),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return GroupQuotaHttp.FromError(context, result.Error);
        }

        GroupCommandOutcome outcome = result.Value;
        context.Response.Headers.ETag = outcome.ETag;
        context.Response.Headers.Location = outcome.Location
            ?? $"/api/v1/admin/groups/{outcome.Value.Id.Value:D}";
        return Results.Json(
            GroupQuotaHttp.ToContract(outcome.Value),
            statusCode: outcome.StatusCode);
    }

    private static async Task<IResult> UpdateGroupAsync(
        HttpContext context,
        IUpdateGroupUseCase updateUseCase,
        IGroupActivationOrchestrator activationOrchestrator,
        Guid groupId,
        GroupUpdateRequest request)
    {
        IResult? failure = GroupQuotaHttp.RequireContentType(
            context,
            "application/merge-patch+json");
        if (failure is not null)
        {
            return failure;
        }

        if (!GroupQuotaHttp.TryGetEntityId(
                context,
                groupId,
                out EntityId entityId,
                out failure))
        {
            return failure!;
        }

        IReadOnlyDictionary<string, IReadOnlyList<string>> errors =
            GroupQuotaHttp.Validate(request);
        if (errors.Count != 0)
        {
            return GroupQuotaHttp.ValidationProblem(context, errors);
        }

        if (!GroupQuotaHttp.TryGetIdempotencyKey(
                context,
                out string? idempotencyKey,
                out failure)
            || !GroupQuotaHttp.TryGetExpectedVersion(
                context,
                out long expectedVersion,
                out failure))
        {
            return failure!;
        }

        GroupActor actor = GroupQuotaHttp.RequireActor(context);
        if (request.Status.HasValue && request.Status.Value == GroupStatus.Active)
        {
            return await ActivateGroupAsync(
                context,
                activationOrchestrator,
                entityId,
                expectedVersion,
                idempotencyKey!,
                actor,
                request).ConfigureAwait(false);
        }

        return await UpdateNonActiveGroupAsync(
            context,
            updateUseCase,
            entityId,
            expectedVersion,
            idempotencyKey!,
            actor,
            request).ConfigureAwait(false);
    }

    private static async Task<IResult> ActivateGroupAsync(
        HttpContext context,
        IGroupActivationOrchestrator activationOrchestrator,
        EntityId entityId,
        long expectedVersion,
        string idempotencyKey,
        GroupActor actor,
        GroupUpdateRequest request)
    {
        Result<GroupActivationResult> activation = await activationOrchestrator.ActivateAsync(
            new GroupActivationOrchestrationCommand(
                new ActorContext(actor.UserId, actor.TokenVersion),
                entityId,
                expectedVersion,
                idempotencyKey,
                request.Reason.Value!,
                new GroupMetadataPatch(
                    request.Name.HasValue,
                    request.Name.HasValue ? request.Name.Value : null,
                    request.Description.HasValue,
                    request.Description.HasValue ? request.Description.Value : null),
                GroupQuotaHttp.RequestId(context),
                GroupQuotaHttp.RemoteIp(context),
                GroupQuotaHttp.UserAgent(context)),
            context.RequestAborted).ConfigureAwait(false);
        if (activation.IsFailure)
        {
            return GroupQuotaHttp.FromError(context, activation.Error);
        }

        GroupResourceSnapshot resource = activation.Value.Resource
            ?? throw new InvalidOperationException(
                "The Group activation command did not return its resource snapshot.");
        context.Response.Headers.ETag = GroupQuotaHttp.ETag(resource.Version);
        return Results.Ok(GroupQuotaHttp.ToContract(resource));
    }

    private static async Task<IResult> UpdateNonActiveGroupAsync(
        HttpContext context,
        IUpdateGroupUseCase updateUseCase,
        EntityId entityId,
        long expectedVersion,
        string idempotencyKey,
        GroupActor actor,
        GroupUpdateRequest request)
    {
        Result<GroupCommandOutcome> result = await updateUseCase.ExecuteAsync(
            new UpdateGroupCommand(
                GroupQuotaHttp.RequestId(context),
                actor,
                idempotencyKey,
                entityId,
                expectedVersion,
                request.Name.HasValue,
                request.Name.HasValue ? request.Name.Value : null,
                request.Description.HasValue,
                request.Description.HasValue ? request.Description.Value : null,
                request.Status.HasValue,
                request.Status.HasValue
                    ? GroupQuotaHttp.ToLifecycle(request.Status.Value)
                    : null,
                request.Reason.HasValue ? request.Reason.Value : null,
                GroupQuotaHttp.RemoteIp(context),
                GroupQuotaHttp.UserAgent(context)),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return GroupQuotaHttp.FromError(context, result.Error);
        }

        GroupCommandOutcome outcome = result.Value;
        context.Response.Headers.ETag = outcome.ETag;
        return Results.Json(
            GroupQuotaHttp.ToContract(outcome.Value),
            statusCode: outcome.StatusCode);
    }
}
