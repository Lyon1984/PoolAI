#pragma warning disable MA0051 // The six route adapters keep their contract mapping visible.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PoolAI.Application.Orchestration;
using PoolAI.BuildingBlocks;
using PoolAI.Contracts.Generated;
using PoolAI.Modules.Identity.Abstractions;

namespace PoolAI.Api;

internal static class ApiKeyEndpointMappings
{
    internal static IEndpointRouteBuilder MapApiKeyEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder self = endpoints.MapGroup("/api/v1/me/api-keys");
        AddActorFilter(self);
        self.MapGet("/", ListMyApiKeysAsync)
            .RequireAuthorization(RequireAnyUserRole)
            .WithName("listMyApiKeys");
        self.MapPost("/", CreateMyApiKeyAsync)
            .RequireAuthorization(RequireAnyUserRole)
            .WithName("createMyApiKey");
        self.MapGet("/{apiKeyId:guid}", GetMyApiKeyAsync)
            .RequireAuthorization(RequireAnyUserRole)
            .WithName("getMyApiKey");

        RouteGroupBuilder admin = endpoints.MapGroup(
            "/api/v1/admin/users/{userId:guid}/api-keys");
        AddActorFilter(admin);
        admin.MapGet("/", AdminListUserApiKeysAsync)
            .RequireAuthorization(RequireAdmin)
            .WithName("adminListUserApiKeys");
        admin.MapPost("/", AdminCreateUserApiKeyAsync)
            .RequireAuthorization(RequireAdmin)
            .WithName("adminCreateUserApiKey");
        admin.MapGet("/{apiKeyId:guid}", AdminGetUserApiKeyAsync)
            .RequireAuthorization(RequireAdmin)
            .WithName("adminGetUserApiKey");

        return endpoints;
    }

    private static void AddActorFilter(RouteGroupBuilder group) => group.AddEndpointFilter(
        static async (invocation, next) =>
            ApiKeyHttp.TryGetActor(invocation.HttpContext, out _)
                ? await next(invocation).ConfigureAwait(false)
                : ApiKeyHttp.InvalidUserToken(invocation.HttpContext));

    private static void RequireAdmin(AuthorizationPolicyBuilder policy) =>
        policy.RequireAuthenticatedUser().RequireRole("admin");

    private static void RequireAnyUserRole(AuthorizationPolicyBuilder policy) =>
        policy.RequireAuthenticatedUser().RequireRole("admin", "operator", "auditor", "user");

    private static async Task<IResult> ListMyApiKeysAsync(
        HttpContext context,
        [FromServices] IApiKeyControlPlaneReader reader)
    {
        ApiKeyActor actor = ApiKeyHttp.RequireActor(context);
        if (!ApiKeyHttp.TryGetPagination(
                context,
                out string? cursor,
                out int limit,
                out IResult? failure))
        {
            return failure!;
        }

        Result<PoolAI.Modules.Identity.Abstractions.ApiKeyPage> result =
            await reader.ListAsync(
                new ListApiKeysQuery(
                    actor,
                    ApiKeyAccessMode.Self,
                    actor.UserId,
                    cursor,
                    limit),
                context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return ApiKeyHttp.FromError(context, result.Error);
        }

        PoolAI.Modules.Identity.Abstractions.ApiKeyPage page = result.Value;
        return Results.Ok(new PoolAI.Contracts.Generated.ApiKeyPage
        {
            Data = page.Data.Select(ApiKeyHttp.ToContract).ToArray(),
            Page = ApiKeyHttp.Page(page.NextCursor, page.HasMore),
        });
    }

    private static async Task<IResult> AdminListUserApiKeysAsync(
        HttpContext context,
        [FromServices] IApiKeyControlPlaneReader reader,
        Guid userId)
    {
        if (!ApiKeyHttp.TryGetEntityId(
                userId,
                "userId",
                out EntityId targetUserId,
                out IResult? failure)
            || !ApiKeyHttp.TryGetPagination(
                context,
                out string? cursor,
                out int limit,
                out failure))
        {
            return failure!;
        }

        Result<PoolAI.Modules.Identity.Abstractions.ApiKeyPage> result =
            await reader.ListAsync(
                new ListApiKeysQuery(
                    ApiKeyHttp.RequireActor(context),
                    ApiKeyAccessMode.AdminProxy,
                    targetUserId,
                    cursor,
                    limit),
                context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return ApiKeyHttp.FromError(context, result.Error);
        }

        PoolAI.Modules.Identity.Abstractions.ApiKeyPage page = result.Value;
        return Results.Ok(new AdminUserApiKeyPage
        {
            Data = page.Data.Select(ApiKeyHttp.ToContract).ToArray(),
            Page = ApiKeyHttp.Page(page.NextCursor, page.HasMore),
        });
    }

    private static async Task<IResult> GetMyApiKeyAsync(
        HttpContext context,
        [FromServices] IApiKeyControlPlaneReader reader,
        Guid apiKeyId)
    {
        ApiKeyActor actor = ApiKeyHttp.RequireActor(context);
        return await GetAsync(
            context,
            reader,
            actor,
            ApiKeyAccessMode.Self,
            actor.UserId,
            apiKeyId).ConfigureAwait(false);
    }

    private static async Task<IResult> AdminGetUserApiKeyAsync(
        HttpContext context,
        [FromServices] IApiKeyControlPlaneReader reader,
        Guid userId,
        Guid apiKeyId)
    {
        if (!ApiKeyHttp.TryGetEntityId(
                userId,
                "userId",
                out EntityId targetUserId,
                out IResult? failure))
        {
            return failure!;
        }

        return await GetAsync(
            context,
            reader,
            ApiKeyHttp.RequireActor(context),
            ApiKeyAccessMode.AdminProxy,
            targetUserId,
            apiKeyId).ConfigureAwait(false);
    }

    private static async Task<IResult> GetAsync(
        HttpContext context,
        IApiKeyControlPlaneReader reader,
        ApiKeyActor actor,
        ApiKeyAccessMode accessMode,
        EntityId targetUserId,
        Guid apiKeyId)
    {
        if (!ApiKeyHttp.TryGetEntityId(
                apiKeyId,
                "apiKeyId",
                out EntityId keyId,
                out IResult? failure))
        {
            return failure!;
        }

        Result<ApiKeyControlPlaneSnapshot> result = await reader.GetAsync(
            new GetApiKeyQuery(actor, accessMode, targetUserId, keyId),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return ApiKeyHttp.FromError(context, result.Error);
        }

        context.Response.Headers.ETag = ApiKeyHttp.ETag(result.Value.Version);
        return Results.Ok(ApiKeyHttp.ToContract(result.Value));
    }

    private static async Task<IResult> CreateMyApiKeyAsync(
        HttpContext context,
        [FromServices] IApiKeyCreateUseCase useCase,
        [FromServices] IApiKeyCreatedOutcomeValidator outcomeValidator,
        [FromBody] ApiKeyCreateRequest request)
    {
        ApiKeyActor actor = ApiKeyHttp.RequireActor(context);
        return await CreateAsync(
            context,
            useCase,
            outcomeValidator,
            actor,
            ApiKeyAccessMode.Self,
            actor.UserId,
            request.Name,
            request.GroupId,
            request.ExpiresAt.HasValue ? request.ExpiresAt.Value : null,
            request.AllowedCidrs.HasValue ? request.AllowedCidrs.Value : [],
            request.AllowedCidrs.HasValue,
            reason: null).ConfigureAwait(false);
    }

    private static async Task<IResult> AdminCreateUserApiKeyAsync(
        HttpContext context,
        [FromServices] IApiKeyCreateUseCase useCase,
        [FromServices] IApiKeyCreatedOutcomeValidator outcomeValidator,
        [FromBody] AdminUserApiKeyCreateRequest request,
        Guid userId)
    {
        if (!ApiKeyHttp.TryGetEntityId(
                userId,
                "userId",
                out EntityId targetUserId,
                out IResult? failure))
        {
            return failure!;
        }

        return await CreateAsync(
            context,
            useCase,
            outcomeValidator,
            ApiKeyHttp.RequireActor(context),
            ApiKeyAccessMode.AdminProxy,
            targetUserId,
            request.Name,
            request.GroupId,
            request.ExpiresAt.HasValue ? request.ExpiresAt.Value : null,
            request.AllowedCidrs.HasValue ? request.AllowedCidrs.Value : [],
            request.AllowedCidrs.HasValue,
            request.Reason).ConfigureAwait(false);
    }

    private static async Task<IResult> CreateAsync(
        HttpContext context,
        IApiKeyCreateUseCase useCase,
        IApiKeyCreatedOutcomeValidator outcomeValidator,
        ApiKeyActor actor,
        ApiKeyAccessMode accessMode,
        EntityId targetUserId,
        string name,
        Guid groupId,
        DateTimeOffset? expiresAt,
        IReadOnlyList<string>? allowedCidrs,
        bool allowedCidrsSpecified,
        string? reason)
    {
        IResult? failure = ApiKeyHttp.RequireJsonContentType(context);
        if (failure is not null)
        {
            return failure;
        }

        IReadOnlyDictionary<string, IReadOnlyList<string>> errors =
            ApiKeyHttp.ValidateCreate(
                name,
                groupId,
                allowedCidrs,
                allowedCidrsSpecified,
                reason,
                accessMode == ApiKeyAccessMode.AdminProxy);
        if (errors.Count != 0)
        {
            return ApiKeyHttp.ValidationProblem(errors);
        }

        if (!ApiKeyHttp.TryGetIdempotencyKey(
                context,
                out string? idempotencyKey,
                out failure))
        {
            return failure!;
        }

        CreateApiKeyCommand command = new(
            ApiKeyHttp.RequestId(context),
            actor,
            accessMode,
            targetUserId,
            new EntityId(groupId),
            idempotencyKey!,
            name,
            expiresAt,
            allowedCidrs ?? [],
            reason,
            ApiKeyHttp.RemoteIp(context),
            ApiKeyHttp.UserAgent(context));
        Result<ApiKeyCreatedOutcome> result = await useCase.CreateAsync(
            command,
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return ApiKeyHttp.FromError(context, result.Error);
        }

        ApiKeyCreatedOutcome outcome = result.Value;
        outcomeValidator.EnsureValid(command, outcome);

        context.Response.Headers.ETag = outcome.ETag;
        context.Response.Headers.Location = outcome.Location;
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(
            new ApiKeyCreated
            {
                ApiKey = ApiKeyHttp.ToContract(outcome.ApiKey),
                Secret = outcome.Secret,
            },
            statusCode: StatusCodes.Status201Created);
    }
}

#pragma warning restore MA0051
