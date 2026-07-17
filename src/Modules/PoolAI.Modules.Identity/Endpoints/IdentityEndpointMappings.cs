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
#pragma warning disable MA0051 // The endpoint Composition Root keeps the complete Identity route surface visible.
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        RouteGroupBuilder api = endpoints.MapGroup("/api/v1");

        api.MapPost("/auth/login", LoginAsync)
            .AllowAnonymous()
            .WithName("login");
        api.MapPost("/auth/refresh", RefreshSessionAsync)
            .AllowAnonymous()
            .WithName("refreshSession");
        api.MapPost("/auth/totp/verify", VerifyLoginTotpAsync)
            .AllowAnonymous()
            .WithName("verifyLoginTotp");
        api.MapPost("/auth/logout", LogoutAsync)
            .AddEndpointFilter(static async (invocation, next) =>
            {
                HttpContext context = invocation.HttpContext;
                return IdentityHttp.TryGetSessionActor(context, out _)
                    ? await next(invocation).ConfigureAwait(false)
                    : IdentityHttp.InvalidUserToken(context);
            })
            .RequireAuthorization(RequireAnyUserRole)
            .WithName("logout");
        api.MapPost("/auth/forgot-password", RequestPasswordResetAsync)
            .AllowAnonymous()
            .WithName("requestPasswordReset");
        api.MapPost("/auth/reset-password", CompletePasswordResetAsync)
            .AllowAnonymous()
            .WithName("resetPassword");

        RouteGroupBuilder me = api.MapGroup("/me");
        me.AddEndpointFilter(static async (invocation, next) =>
        {
            HttpContext context = invocation.HttpContext;
            return IdentityHttp.TryGetSessionActor(context, out _)
                ? await next(invocation).ConfigureAwait(false)
                : IdentityHttp.InvalidUserToken(context);
        });
        me.MapGet("/", GetCurrentUserAsync)
            .RequireAuthorization(RequireAnyUserRole)
            .WithName("getMyProfile");
        me.MapPost("/password", ChangePasswordAsync)
            .RequireAuthorization(RequireAnyUserRole)
            .WithName("changeMyPassword");
        me.MapPost("/totp/setup", SetupTotpAsync)
            .RequireAuthorization(RequireAnyUserRole)
            .WithName("beginMyTotpSetup");
        me.MapPost("/totp/confirm", ConfirmTotpAsync)
            .RequireAuthorization(RequireAnyUserRole)
            .WithName("confirmMyTotpSetup");
        me.MapPost("/totp/disable", DisableTotpAsync)
            .RequireAuthorization(RequireAnyUserRole)
            .WithName("disableMyTotp");

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
#pragma warning restore MA0051

    private static void RequireAdmin(AuthorizationPolicyBuilder policy) =>
        policy.RequireAuthenticatedUser().RequireRole("admin");

    private static void RequireAnyReadRole(AuthorizationPolicyBuilder policy) =>
        policy.RequireAuthenticatedUser().RequireRole("admin", "operator", "auditor");

    private static void RequireAnyUserRole(AuthorizationPolicyBuilder policy) =>
        policy.RequireAuthenticatedUser().RequireRole("admin", "operator", "auditor", "user");

    private static async Task<IResult> LoginAsync(
        HttpContext context,
        ILoginUseCase useCase,
        LoginRequest request)
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
            throw new InvalidOperationException("The validated login email could not be normalized.");
        }

        Result<LoginResultView> result = await useCase.ExecuteAsync(
            new LoginCommand(
                IdentityHttp.RequestId(context),
                email,
                request.Password,
                IdentityHttp.RemoteIp(context),
                IdentityHttp.UserAgent(context)),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return IdentityHttp.FromError(context, result.Error);
        }

        IdentityHttp.NoStore(context);
        return result.Value switch
        {
            LoginTokenResultView token => Results.Ok(IdentityHttp.ToContract(token.Tokens)),
            LoginMfaResultView mfa => Results.Ok(IdentityHttp.ToContract(mfa)),
            _ => throw new InvalidOperationException("Unknown login result."),
        };
    }

    private static async Task<IResult> VerifyLoginTotpAsync(
        HttpContext context,
        IVerifyLoginTotpUseCase useCase,
        TotpVerifyRequest request)
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

        Result<TokenPairView> result = await useCase.ExecuteAsync(
            new VerifyLoginTotpCommand(
                IdentityHttp.RequestId(context),
                new EntityId(request.ChallengeId),
                request.TotpCode,
                IdentityHttp.RemoteIp(context),
                IdentityHttp.UserAgent(context)),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return IdentityHttp.FromError(context, result.Error);
        }

        IdentityHttp.NoStore(context);
        return Results.Ok(IdentityHttp.ToContract(result.Value));
    }

    private static async Task<IResult> RefreshSessionAsync(
        HttpContext context,
        IRefreshSessionUseCase useCase,
        RefreshRequest request)
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

        Result<TokenPairView> result = await useCase.ExecuteAsync(
            new RefreshSessionCommand(
                IdentityHttp.RequestId(context),
                request.RefreshToken,
                IdentityHttp.RemoteIp(context),
                IdentityHttp.UserAgent(context)),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return IdentityHttp.FromError(context, result.Error);
        }

        IdentityHttp.NoStore(context);
        return Results.Ok(IdentityHttp.ToContract(result.Value));
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        ILogoutUseCase useCase,
        LogoutRequest? request)
    {
        if (request is not null)
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
        }

        string? refreshToken = request?.RefreshToken.HasValue is true
            ? request.RefreshToken.Value
            : null;
        bool allSessions = request?.AllSessions.HasValue is true
            && request.AllSessions.Value;
        Result<IdentityCommandOutcome> result = await useCase.ExecuteAsync(
            new LogoutCommand(
                IdentityHttp.RequestId(context),
                IdentityHttp.RequireSessionActor(context),
                refreshToken,
                allSessions,
                IdentityHttp.RemoteIp(context),
                IdentityHttp.UserAgent(context)),
            context.RequestAborted).ConfigureAwait(false);
        return result.IsFailure
            ? IdentityHttp.FromError(context, result.Error)
            : Results.NoContent();
    }

    private static async Task<IResult> GetCurrentUserAsync(
        HttpContext context,
        IGetCurrentUserUseCase useCase)
    {
        Result<CurrentUserView> result = await useCase.ExecuteAsync(
            new GetCurrentUserQuery(IdentityHttp.RequireSessionActor(context)),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return IdentityHttp.FromError(context, result.Error);
        }

        context.Response.Headers.ETag = IdentityHttp.ETag(result.Value.Version);
        IdentityHttp.NoStore(context);
        return Results.Ok(IdentityHttp.ToContract(result.Value));
    }

    private static async Task<IResult> ChangePasswordAsync(
        HttpContext context,
        IChangePasswordUseCase useCase,
        PasswordChangeRequest request)
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

        if (!IdentityHttp.TryGetIdempotencyKey(context, out string? idempotencyKey, out IResult? failure)
            || !IdentityHttp.TryGetExpectedVersion(context, out long expectedVersion, out failure))
        {
            return failure!;
        }

        Result<IdentityCommandOutcome> result = await useCase.ExecuteAsync(
            new ChangePasswordCommand(
                IdentityHttp.RequestId(context),
                IdentityHttp.RequireSessionActor(context),
                idempotencyKey!,
                expectedVersion,
                request.CurrentPassword,
                request.NewPassword,
                request.Reason,
                IdentityHttp.RemoteIp(context),
                IdentityHttp.UserAgent(context)),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return IdentityHttp.FromError(context, result.Error);
        }

        if (result.Value.ETag is not null)
        {
            context.Response.Headers.ETag = result.Value.ETag;
        }

        IdentityHttp.NoStore(context);
        return Results.StatusCode(result.Value.StatusCode);
    }

    private static async Task<IResult> SetupTotpAsync(
        HttpContext context,
        ISetupTotpUseCase useCase,
        TotpSetupRequest request)
    {
        IResult? failure = IdentityHttp.RequireContentType(context, "application/json");
        if (failure is not null)
        {
            return failure;
        }

        IReadOnlyDictionary<string, IReadOnlyList<string>> errors = IdentityHttp.Validate(request);
        if (errors.Count != 0)
        {
            return IdentityHttp.ValidationProblem(context, errors);
        }

        if (!IdentityHttp.TryGetIdempotencyKey(context, out string? idempotencyKey, out failure))
        {
            return failure!;
        }

        Result<IdentityCommandOutcome<TotpSetupView>> result = await useCase.ExecuteAsync(
            new SetupTotpCommand(
                IdentityHttp.RequestId(context),
                IdentityHttp.RequireSessionActor(context),
                idempotencyKey!,
                request.CurrentPassword,
                IdentityHttp.RemoteIp(context),
                IdentityHttp.UserAgent(context)),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return IdentityHttp.FromError(context, result.Error);
        }

        IdentityHttp.NoStore(context);
        return Results.Json(
            IdentityHttp.ToContract(result.Value.Value),
            statusCode: result.Value.StatusCode);
    }

    private static async Task<IResult> ConfirmTotpAsync(
        HttpContext context,
        IConfirmTotpUseCase useCase,
        TotpConfirmRequest request)
    {
        IResult? failure = IdentityHttp.RequireContentType(context, "application/json");
        if (failure is not null)
        {
            return failure;
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

        Result<IdentityCommandOutcome<TotpConfirmView>> result = await useCase.ExecuteAsync(
            new ConfirmTotpCommand(
                IdentityHttp.RequestId(context),
                IdentityHttp.RequireSessionActor(context),
                idempotencyKey!,
                expectedVersion,
                new EntityId(request.ChallengeId),
                request.TotpCode,
                IdentityHttp.RemoteIp(context),
                IdentityHttp.UserAgent(context)),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return IdentityHttp.FromError(context, result.Error);
        }

        if (result.Value.ETag is not null)
        {
            context.Response.Headers.ETag = result.Value.ETag;
        }

        IdentityHttp.NoStore(context);
        return Results.Json(
            IdentityHttp.ToContract(result.Value.Value),
            statusCode: result.Value.StatusCode);
    }

    private static async Task<IResult> DisableTotpAsync(
        HttpContext context,
        IDisableTotpUseCase useCase,
        TotpDisableRequest request)
    {
        IResult? failure = IdentityHttp.RequireContentType(context, "application/json");
        if (failure is not null)
        {
            return failure;
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

        Result<IdentityCommandOutcome> result = await useCase.ExecuteAsync(
            new DisableTotpCommand(
                IdentityHttp.RequestId(context),
                IdentityHttp.RequireSessionActor(context),
                idempotencyKey!,
                expectedVersion,
                request.CurrentPassword,
                request.TotpCode,
                IdentityHttp.RemoteIp(context),
                IdentityHttp.UserAgent(context)),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return IdentityHttp.FromError(context, result.Error);
        }

        if (result.Value.ETag is not null)
        {
            context.Response.Headers.ETag = result.Value.ETag;
        }

        return Results.StatusCode(result.Value.StatusCode);
    }

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
