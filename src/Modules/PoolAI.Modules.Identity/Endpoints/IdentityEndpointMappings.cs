using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PoolAI.Contracts.Generated;
using PoolAI.Modules.Identity.Application;

namespace PoolAI.Modules.Identity.Endpoints;

public static class IdentityEndpointMappings
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        RouteGroupBuilder api = endpoints.MapGroup("/api/v1");

        api.MapPost("/auth/forgot-password", RequestPasswordResetAsync)
            .AllowAnonymous()
            .WithName("requestPasswordReset");
        api.MapPost("/auth/reset-password", CompletePasswordResetAsync)
            .AllowAnonymous()
            .WithName("resetPassword");

        RouteGroupBuilder users = api.MapGroup("/admin/users");
        users.AddEndpointFilter(static async (invocation, next) =>
        {
            HttpContext context = invocation.HttpContext;
            return IdentityHttp.TryGetActor(context, out _)
                ? await next(invocation).ConfigureAwait(false)
                : IdentityHttp.InvalidUserToken(context);
        });
        users.MapGet("/", ListUsersAsync)
            .RequireAuthorization(RequireAnyReadRole)
            .WithName("adminListUsers");
        users.MapPost("/", CreateUserAsync)
            .RequireAuthorization(RequireAdmin)
            .WithName("adminCreateUser");
        users.MapGet("/{userId:guid}", GetUserAsync)
            .RequireAuthorization(RequireAnyReadRole)
            .WithName("adminGetUser");
        users.MapMethods("/{userId:guid}", [HttpMethods.Patch], UpdateUserAsync)
            .RequireAuthorization(RequireAdmin)
            .WithName("adminUpdateUser");
        users.MapPost("/{userId:guid}/password-reset", RequestAdminPasswordResetAsync)
            .RequireAuthorization(RequireAdmin)
            .WithName("adminRequestUserPasswordReset");
        return endpoints;
    }

    private static void RequireAdmin(AuthorizationPolicyBuilder policy) =>
        policy.RequireRole("admin");

    private static void RequireAnyReadRole(AuthorizationPolicyBuilder policy) =>
        policy.RequireRole("admin", "operator", "auditor");

    private static async Task<IResult> ListUsersAsync(
        HttpContext context,
        IListUsersUseCase useCase,
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
            return IdentityHttp.InvalidRequestProblem(
                context,
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["/limit"] =
                        ["The pagination parameter is outside the allowed range."],
                });
        }

        Result<PoolAI.Modules.Identity.Application.UserPage> result = await useCase.ExecuteAsync(
            new ListUsersQuery(IdentityHttp.RequireActor(context), cursor, parsedLimit),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return IdentityHttp.FromError(context, result.Error);
        }

        PoolAI.Modules.Identity.Application.UserPage page = result.Value;
        return Results.Ok(new PoolAI.Contracts.Generated.UserPage
        {
            Data = page.Data.Select(IdentityHttp.ToContract).ToArray(),
            Page = new PageInfo
            {
                HasMore = page.HasMore,
                NextCursor = page.NextCursor is null ? default : page.NextCursor,
            },
        });
    }

    private static async Task<IResult> GetUserAsync(
        HttpContext context,
        IGetUserUseCase useCase,
        Guid userId)
    {
        if (!IdentityHttp.TryGetEntityId(
                context,
                userId,
                "userId",
                out EntityId entityId,
                out IResult? failure))
        {
            return failure!;
        }

        Result<UserView> result = await useCase.ExecuteAsync(
            new GetUserQuery(IdentityHttp.RequireActor(context), entityId),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return IdentityHttp.FromError(context, result.Error);
        }

        context.Response.Headers.ETag = IdentityHttp.ETag(result.Value.Version);
        return Results.Ok(IdentityHttp.ToContract(result.Value));
    }

    private static async Task<IResult> CreateUserAsync(
        HttpContext context,
        ICreateUserUseCase useCase,
        UserCreateRequest request)
    {
        IResult? contentTypeFailure = IdentityHttp.RequireContentType(context, "application/json");
        if (contentTypeFailure is not null)
        {
            return contentTypeFailure;
        }

        IReadOnlyDictionary<string, IReadOnlyList<string>> errors = IdentityHttp.Validate(request);
        if (errors.Count != 0)
        {
            return IdentityHttp.ValidationProblem(context, errors);
        }

        if (!IdentityHttp.TryNormalizeEmail(request.Email, out string email))
        {
            throw new InvalidOperationException(
                "The validated user email could not be normalized.");
        }

        if (!IdentityHttp.TryGetIdempotencyKey(context, out string? idempotencyKey, out IResult? failure))
        {
            return failure!;
        }

        Result<IdentityCommandOutcome<UserView>> result = await useCase.ExecuteAsync(
            new CreateUserCommand(
                IdentityHttp.RequestId(context),
                IdentityHttp.RequireActor(context),
                idempotencyKey!,
                email,
                request.DisplayName,
                IdentityHttp.ToSystemRole(request.Role),
                request.TemporaryPassword,
                IdentityHttp.RemoteIp(context),
                IdentityHttp.UserAgent(context)),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return IdentityHttp.FromError(context, result.Error);
        }

        IdentityCommandOutcome<UserView> outcome = result.Value;
        context.Response.Headers.ETag = outcome.ETag ?? IdentityHttp.ETag(outcome.Value.Version);
        context.Response.Headers.Location = outcome.Location
            ?? $"/api/v1/admin/users/{outcome.Value.Id.Value:D}";
        return Results.Json(IdentityHttp.ToContract(outcome.Value), statusCode: outcome.StatusCode);
    }

    private static async Task<IResult> UpdateUserAsync(
        HttpContext context,
        IUpdateUserUseCase useCase,
        Guid userId,
        UserUpdateRequest request)
    {
        IResult? contentTypeFailure = IdentityHttp.RequireContentType(
            context,
            "application/merge-patch+json");
        if (contentTypeFailure is not null)
        {
            return contentTypeFailure;
        }

        if (!IdentityHttp.TryGetEntityId(
                context,
                userId,
                "userId",
                out EntityId entityId,
                out IResult? failure))
        {
            return failure!;
        }

        IReadOnlyDictionary<string, IReadOnlyList<string>> errors = IdentityHttp.Validate(request);
        if (errors.Count != 0)
        {
            return IdentityHttp.ValidationProblem(context, errors);
        }

        if (!IdentityHttp.TryGetIdempotencyKey(context, out string? idempotencyKey, out failure)
            || !IdentityHttp.TryGetExpectedVersion(context, out long expectedVersion, out failure))
        {
            return failure!;
        }

        Result<IdentityCommandOutcome<UserView>> result = await useCase.ExecuteAsync(
            new UpdateUserCommand(
                IdentityHttp.RequestId(context),
                IdentityHttp.RequireActor(context),
                idempotencyKey!,
                entityId,
                expectedVersion,
                request.DisplayName.HasValue ? request.DisplayName.Value : null,
                request.Role.HasValue ? IdentityHttp.ToSystemRole(request.Role.Value) : null,
                request.Status.HasValue ? IdentityHttp.ToLifecycle(request.Status.Value) : null,
                request.Reason.HasValue ? request.Reason.Value : null,
                IdentityHttp.RemoteIp(context),
                IdentityHttp.UserAgent(context)),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return IdentityHttp.FromError(context, result.Error);
        }

        IdentityCommandOutcome<UserView> outcome = result.Value;
        context.Response.Headers.ETag = outcome.ETag ?? IdentityHttp.ETag(outcome.Value.Version);
        return Results.Json(IdentityHttp.ToContract(outcome.Value), statusCode: outcome.StatusCode);
    }

    private static async Task<IResult> RequestAdminPasswordResetAsync(
        HttpContext context,
        IRequestAdminPasswordResetUseCase useCase,
        Guid userId,
        AdminPasswordResetRequest request)
    {
        IResult? contentTypeFailure = IdentityHttp.RequireContentType(context, "application/json");
        if (contentTypeFailure is not null)
        {
            return contentTypeFailure;
        }

        if (!IdentityHttp.TryGetEntityId(
                context,
                userId,
                "userId",
                out EntityId entityId,
                out IResult? failure))
        {
            return failure!;
        }

        if (string.IsNullOrWhiteSpace(request.Reason)
            || request.Reason.Length > 500
            || request.Reason.Any(static character => character is '\r' or '\n'))
        {
            return IdentityHttp.ValidationProblem(
                context,
                IdentityHttp.FieldError("/reason", "A non-blank reason of at most 500 characters is required."));
        }

        if (!IdentityHttp.TryGetIdempotencyKey(context, out string? idempotencyKey, out failure))
        {
            return failure!;
        }

        Result<IdentityCommandOutcome> result = await useCase.ExecuteAsync(
            new AdminPasswordResetCommand(
                IdentityHttp.RequestId(context),
                IdentityHttp.RequireActor(context),
                idempotencyKey!,
                entityId,
                request.Reason,
                IdentityHttp.RemoteIp(context),
                IdentityHttp.UserAgent(context)),
            context.RequestAborted).ConfigureAwait(false);
        return result.IsFailure
            ? IdentityHttp.FromError(context, result.Error)
            : Results.StatusCode(result.Value.StatusCode);
    }

    private static async Task<IResult> RequestPasswordResetAsync(
        HttpContext context,
        IRequestPasswordResetUseCase useCase,
        ForgotPasswordRequest request)
    {
        IResult? contentTypeFailure = IdentityHttp.RequireContentType(context, "application/json");
        if (contentTypeFailure is not null)
        {
            return contentTypeFailure;
        }

        if (!IdentityHttp.IsEmail(request.Email))
        {
            return IdentityHttp.ValidationProblem(
                context,
                IdentityHttp.FieldError(
                    "/email",
                    "A supported email mailbox of at most 254 characters is required."));
        }

        if (!IdentityHttp.TryNormalizeEmail(request.Email, out string email))
        {
            throw new InvalidOperationException(
                "The validated password-reset email could not be normalized.");
        }

        EntityId requestId = IdentityHttp.RequestId(context);
        Result<IdentityCommandOutcome> result = await useCase.ExecuteAsync(
            new ForgotPasswordCommand(
                requestId,
                email,
                IdentityHttp.RemoteIp(context),
                IdentityHttp.UserAgent(context)),
            context.RequestAborted).ConfigureAwait(false);
        return result.IsFailure
            ? IdentityHttp.FromError(context, result.Error)
            : Results.StatusCode(result.Value.StatusCode);
    }

    private static async Task<IResult> CompletePasswordResetAsync(
        HttpContext context,
        ICompletePasswordResetUseCase useCase,
        ResetPasswordRequest request)
    {
        IResult? contentTypeFailure = IdentityHttp.RequireContentType(context, "application/json");
        if (contentTypeFailure is not null)
        {
            return contentTypeFailure;
        }

        Dictionary<string, IReadOnlyList<string>> errors = new(StringComparer.Ordinal);
        if (request.Token is null || request.Token.Length is < 32 or > 4096)
        {
            errors["/token"] = ["The password-reset token length is invalid."];
        }

        if (request.NewPassword is null || request.NewPassword.Length is < 12 or > 1024)
        {
            errors["/new_password"] = ["The new password must contain between 12 and 1024 characters."];
        }

        if (errors.Count != 0)
        {
            return IdentityHttp.ValidationProblem(context, errors);
        }

        EntityId requestId = IdentityHttp.RequestId(context);
        Result<IdentityCommandOutcome> result = await useCase.ExecuteAsync(
            new CompletePasswordResetCommand(
                requestId,
                request.Token!,
                request.NewPassword!,
                IdentityHttp.RemoteIp(context),
                IdentityHttp.UserAgent(context)),
            context.RequestAborted).ConfigureAwait(false);
        return result.IsFailure
            ? IdentityHttp.FromError(context, result.Error)
            : Results.StatusCode(result.Value.StatusCode);
    }
}
