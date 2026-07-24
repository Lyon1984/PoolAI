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
        self.MapPatch("/{apiKeyId:guid}", UpdateMyApiKeyAsync)
            .RequireAuthorization(RequireAnyUserRole)
            .WithName("updateMyApiKey");
        self.MapDelete("/{apiKeyId:guid}", RevokeMyApiKeyAsync)
            .RequireAuthorization(RequireAnyUserRole)
            .WithName("revokeMyApiKey");
        self.MapPost("/{apiKeyId:guid}/rotate", RotateMyApiKeyAsync)
            .RequireAuthorization(RequireAnyUserRole)
            .WithName("rotateMyApiKey");

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
        admin.MapPatch("/{apiKeyId:guid}", AdminUpdateUserApiKeyAsync)
            .RequireAuthorization(RequireAdmin)
            .WithName("adminUpdateUserApiKey");
        admin.MapDelete("/{apiKeyId:guid}", AdminRevokeUserApiKeyAsync)
            .RequireAuthorization(RequireAdmin)
            .WithName("adminRevokeUserApiKey");
        admin.MapPost("/{apiKeyId:guid}/rotate", AdminRotateUserApiKeyAsync)
            .RequireAuthorization(RequireAdmin)
            .WithName("adminRotateUserApiKey");

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

    private static async Task<IResult> UpdateMyApiKeyAsync(
        HttpContext context,
        [FromServices] IApiKeyMutationUseCase useCase,
        [FromBody] ApiKeyUpdateRequest request,
        Guid apiKeyId)
    {
        ApiKeyActor actor = ApiKeyHttp.RequireActor(context);
        return await UpdateAsync(
            context,
            useCase,
            actor,
            ApiKeyAccessMode.Self,
            actor.UserId,
            apiKeyId,
            request.Name.HasValue,
            request.Name.HasValue ? request.Name.Value : null,
            request.Status.HasValue,
            request.Status.HasValue ? request.Status.Value : null,
            request.ExpiresAt.HasValue,
            request.ExpiresAt.HasValue ? request.ExpiresAt.Value : null,
            request.AllowedCidrs.HasValue,
            request.AllowedCidrs.HasValue ? request.AllowedCidrs.Value : null,
            reason: null).ConfigureAwait(false);
    }

    private static async Task<IResult> AdminUpdateUserApiKeyAsync(
        HttpContext context,
        [FromServices] IApiKeyMutationUseCase useCase,
        [FromBody] AdminUserApiKeyUpdateRequest request,
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

        return await UpdateAsync(
            context,
            useCase,
            ApiKeyHttp.RequireActor(context),
            ApiKeyAccessMode.AdminProxy,
            targetUserId,
            apiKeyId,
            request.Name.HasValue,
            request.Name.HasValue ? request.Name.Value : null,
            request.Status.HasValue,
            request.Status.HasValue ? request.Status.Value : null,
            request.ExpiresAt.HasValue,
            request.ExpiresAt.HasValue ? request.ExpiresAt.Value : null,
            request.AllowedCidrs.HasValue,
            request.AllowedCidrs.HasValue ? request.AllowedCidrs.Value : null,
            request.Reason).ConfigureAwait(false);
    }

    private static async Task<IResult> UpdateAsync(
        HttpContext context,
        IApiKeyMutationUseCase useCase,
        ApiKeyActor actor,
        ApiKeyAccessMode accessMode,
        EntityId targetUserId,
        Guid apiKeyId,
        bool setName,
        string? name,
        bool setStatus,
        string? status,
        bool setExpiresAt,
        DateTimeOffset? expiresAt,
        bool setAllowedCidrs,
        IReadOnlyList<string>? allowedCidrs,
        string? reason)
    {
        if (!ApiKeyHttp.TryGetEntityId(
                apiKeyId,
                "apiKeyId",
                out EntityId keyId,
                out IResult? failure))
        {
            return failure!;
        }

        failure = ApiKeyHttp.RequireContentType(
            context,
            "application/merge-patch+json");
        if (failure is not null)
        {
            return failure;
        }

        IReadOnlyDictionary<string, IReadOnlyList<string>> errors =
            ApiKeyHttp.ValidateUpdate(
                setName,
                name,
                setStatus,
                status,
                setExpiresAt,
                setAllowedCidrs,
                allowedCidrs,
                reason,
                accessMode == ApiKeyAccessMode.AdminProxy);
        if (errors.Count != 0)
        {
            return ApiKeyHttp.ValidationProblem(errors);
        }

        if (!ApiKeyHttp.TryGetIdempotencyKey(
                context,
                out string? idempotencyKey,
                out failure)
            || !ApiKeyHttp.TryGetExpectedVersion(
                context,
                out long expectedVersion,
                out failure))
        {
            return failure!;
        }

        Result<ApiKeyUpdatedOutcome> result = await useCase.UpdateAsync(
            new UpdateApiKeyCommand(
                ApiKeyHttp.RequestId(context),
                actor,
                accessMode,
                targetUserId,
                keyId,
                idempotencyKey!,
                expectedVersion,
                setName,
                name,
                setStatus,
                setStatus ? ApiKeyHttp.ParseMutableStatus(status) : null,
                setExpiresAt,
                expiresAt,
                setAllowedCidrs,
                setAllowedCidrs ? allowedCidrs : null,
                reason,
                ApiKeyHttp.RemoteIp(context),
                ApiKeyHttp.UserAgent(context)),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return ApiKeyHttp.FromError(context, result.Error);
        }

        ApiKeyUpdatedOutcome outcome = result.Value;
        context.Response.Headers.ETag = outcome.ETag;
        return Results.Json(
            ApiKeyHttp.ToContract(outcome.ApiKey),
            statusCode: outcome.StatusCode);
    }

    private static async Task<IResult> RevokeMyApiKeyAsync(
        HttpContext context,
        [FromServices] IApiKeyMutationUseCase useCase,
        Guid apiKeyId)
    {
        ApiKeyActor actor = ApiKeyHttp.RequireActor(context);
        return await RevokeAsync(
            context,
            useCase,
            actor,
            ApiKeyAccessMode.Self,
            actor.UserId,
            apiKeyId).ConfigureAwait(false);
    }

    private static async Task<IResult> AdminRevokeUserApiKeyAsync(
        HttpContext context,
        [FromServices] IApiKeyMutationUseCase useCase,
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

        return await RevokeAsync(
            context,
            useCase,
            ApiKeyHttp.RequireActor(context),
            ApiKeyAccessMode.AdminProxy,
            targetUserId,
            apiKeyId).ConfigureAwait(false);
    }

    private static async Task<IResult> RevokeAsync(
        HttpContext context,
        IApiKeyMutationUseCase useCase,
        ApiKeyActor actor,
        ApiKeyAccessMode accessMode,
        EntityId targetUserId,
        Guid apiKeyId)
    {
        if (!ApiKeyHttp.TryGetEntityId(
                apiKeyId,
                "apiKeyId",
                out EntityId keyId,
                out IResult? failure)
            || !ApiKeyHttp.TryGetIdempotencyKey(
                context,
                out string? idempotencyKey,
                out failure)
            || !ApiKeyHttp.TryGetExpectedVersion(
                context,
                out long expectedVersion,
                out failure)
            || !ApiKeyHttp.TryGetChangeReason(
                context,
                out string? reason,
                out failure))
        {
            return failure!;
        }

        Result<ApiKeyRevokedOutcome> result = await useCase.RevokeAsync(
            new RevokeApiKeyCommand(
                ApiKeyHttp.RequestId(context),
                actor,
                accessMode,
                targetUserId,
                keyId,
                idempotencyKey!,
                expectedVersion,
                reason!,
                ApiKeyHttp.RemoteIp(context),
                ApiKeyHttp.UserAgent(context)),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return ApiKeyHttp.FromError(context, result.Error);
        }

        context.Response.Headers.ETag = result.Value.ETag;
        return Results.StatusCode(result.Value.StatusCode);
    }

    private static async Task<IResult> RotateMyApiKeyAsync(
        HttpContext context,
        [FromServices] IApiKeyMutationUseCase useCase,
        [FromServices] IApiKeyCreatedOutcomeValidator outcomeValidator,
        [FromBody] ApiKeyRotateRequest request,
        Guid apiKeyId)
    {
        ApiKeyActor actor = ApiKeyHttp.RequireActor(context);
        return await RotateAsync(
            context,
            useCase,
            outcomeValidator,
            actor,
            ApiKeyAccessMode.Self,
            actor.UserId,
            apiKeyId,
            request.Reason).ConfigureAwait(false);
    }

    private static async Task<IResult> AdminRotateUserApiKeyAsync(
        HttpContext context,
        [FromServices] IApiKeyMutationUseCase useCase,
        [FromServices] IApiKeyCreatedOutcomeValidator outcomeValidator,
        [FromBody] ApiKeyRotateRequest request,
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

        return await RotateAsync(
            context,
            useCase,
            outcomeValidator,
            ApiKeyHttp.RequireActor(context),
            ApiKeyAccessMode.AdminProxy,
            targetUserId,
            apiKeyId,
            request.Reason).ConfigureAwait(false);
    }

    private static async Task<IResult> RotateAsync(
        HttpContext context,
        IApiKeyMutationUseCase useCase,
        IApiKeyCreatedOutcomeValidator outcomeValidator,
        ApiKeyActor actor,
        ApiKeyAccessMode accessMode,
        EntityId targetUserId,
        Guid apiKeyId,
        string? reason)
    {
        if (!ApiKeyHttp.TryGetEntityId(
                apiKeyId,
                "apiKeyId",
                out EntityId keyId,
                out IResult? failure))
        {
            return failure!;
        }

        failure = ApiKeyHttp.RequireJsonContentType(context);
        if (failure is not null)
        {
            return failure;
        }

        IReadOnlyDictionary<string, IReadOnlyList<string>> errors =
            ApiKeyHttp.ValidateRotate(reason);
        if (errors.Count != 0)
        {
            return ApiKeyHttp.ValidationProblem(errors);
        }

        if (!ApiKeyHttp.TryGetIdempotencyKey(
                context,
                out string? idempotencyKey,
                out failure)
            || !ApiKeyHttp.TryGetExpectedVersion(
                context,
                out long expectedVersion,
                out failure))
        {
            return failure!;
        }

        RotateApiKeyCommand command = new(
            ApiKeyHttp.RequestId(context),
            actor,
            accessMode,
            targetUserId,
            keyId,
            idempotencyKey!,
            expectedVersion,
            reason!,
            ApiKeyHttp.RemoteIp(context),
            ApiKeyHttp.UserAgent(context));
        Result<ApiKeyCreatedOutcome> result = await useCase.RotateAsync(
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
            statusCode: outcome.StatusCode);
    }
}

#pragma warning restore MA0051
