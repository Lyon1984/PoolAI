using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using PoolAI.Contracts.Generated;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.SubscriptionAccess.Application;

namespace PoolAI.Modules.SubscriptionAccess.Endpoints;

internal static class SubscriptionHttp
{
    internal static EntityId RequestId(HttpContext context) =>
        Guid.TryParse(context.TraceIdentifier, out Guid value)
            ? new EntityId(value)
            : throw new InvalidOperationException("The API request identifier is invalid.");

    internal static bool TryGetActor(HttpContext context, out SubscriptionActor? actor)
    {
        string? userIdValue = context.User.FindFirstValue("sub")
            ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        string? roleValue = context.User.FindFirstValue("role")
            ?? context.User.FindFirstValue(ClaimTypes.Role);
        string? tokenVersionValue = context.User.FindFirstValue("token_version");
        if (!Guid.TryParse(userIdValue, out Guid userId)
            || userId == Guid.Empty
            || !long.TryParse(
                tokenVersionValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long tokenVersion)
            || tokenVersion <= 0
            || !TryParseRole(roleValue, out SystemRole role))
        {
            actor = null;
            return false;
        }

        actor = new SubscriptionActor(new EntityId(userId), role, tokenVersion);
        return true;
    }

    internal static SubscriptionActor RequireActor(HttpContext context) =>
        TryGetActor(context, out SubscriptionActor? actor)
            ? actor!
            : throw new InvalidOperationException("Required identity claims are missing.");

    internal static IResult InvalidUserToken(HttpContext context)
    {
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return Problem(
            context,
            401,
            "invalid_user_token",
            "Invalid user token",
            "The user access token is missing required identity claims.",
            retryable: false);
    }

    internal static bool TryGetEntityId(
        HttpContext context,
        Guid value,
        string parameterName,
        out EntityId entityId,
        out IResult? failure)
    {
        if (value == Guid.Empty)
        {
            entityId = default;
            failure = Problem(
                context,
                400,
                SubscriptionErrorCodes.InvalidRequest,
                "Invalid request",
                $"The {parameterName} path parameter must be a non-empty UUID.",
                retryable: false);
            return false;
        }

        entityId = new EntityId(value);
        failure = null;
        return true;
    }

    internal static bool TryGetIdempotencyKey(
        HttpContext context,
        out string? key,
        out IResult? failure)
    {
        key = context.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrEmpty(key))
        {
            failure = Problem(
                context,
                428,
                "idempotency_key_required",
                "Idempotency key required",
                "The Idempotency-Key header is required.",
                retryable: false);
            return false;
        }

        if (key.Length > 128
            || key.Any(static character => character is < (char)0x21 or > (char)0x7e))
        {
            failure = Problem(
                context,
                400,
                SubscriptionErrorCodes.InvalidRequest,
                "Invalid request",
                "The Idempotency-Key header is invalid.",
                retryable: false);
            return false;
        }

        failure = null;
        return true;
    }

    internal static bool TryGetExpectedVersion(
        HttpContext context,
        out long version,
        out IResult? failure)
    {
        version = 0;
        string value = context.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrEmpty(value))
        {
            failure = Problem(
                context,
                428,
                "if_match_required",
                "Precondition required",
                "The If-Match header is required.",
                retryable: false);
            return false;
        }

        bool valid = value.Length >= 4
            && value.StartsWith("\"v", StringComparison.Ordinal)
            && value.EndsWith('"')
            && value[2] is >= '1' and <= '9'
            && long.TryParse(
                value.AsSpan(2, value.Length - 3),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out version)
            && version > 0;
        if (!valid)
        {
            version = 0;
            failure = Problem(
                context,
                400,
                SubscriptionErrorCodes.InvalidRequest,
                "Invalid request",
                "If-Match must be a single strong resource ETag such as \"v7\".",
                retryable: false);
            return false;
        }

        failure = null;
        return true;
    }

    internal static IResult? RequireContentType(HttpContext context, string expected)
    {
        string value = context.Request.ContentType ?? string.Empty;
        int separator = value.IndexOf(';', StringComparison.Ordinal);
        string mediaType = (separator < 0 ? value : value[..separator]).Trim();
        return string.Equals(mediaType, expected, StringComparison.OrdinalIgnoreCase)
            ? null
            : Problem(
                context,
                415,
                "unsupported_media_type",
                "Unsupported media type",
                $"This operation requires {expected}.",
                retryable: false);
    }

    internal static IResult ValidationProblem(
        HttpContext context,
        IReadOnlyDictionary<string, IReadOnlyList<string>> errors) => Problem(
            context,
            422,
            SubscriptionErrorCodes.ValidationFailed,
            "Validation failed",
            "One or more request fields failed validation.",
            retryable: false,
            errors: errors);

    internal static IResult InvalidRequest(HttpContext context, string pointer, string message) =>
        Problem(
            context,
            400,
            SubscriptionErrorCodes.InvalidRequest,
            "Invalid request",
            "One or more request parameters are invalid.",
            retryable: false,
            errors: FieldError(pointer, message));

    internal static IResult FromError(HttpContext context, ResultError error)
    {
        if (error.ETag is not null)
        {
            context.Response.Headers.ETag = error.ETag;
        }

        ResultErrorPresentation? presentation = error.Presentation;
        (int status, string title, string detail, bool retryable, long? retryAfter) =
            presentation is not null
                ? (presentation.Status,
                    presentation.Title,
                    presentation.Detail,
                    presentation.Retryable,
                    presentation.RetryAfterSeconds)
                : MapError(error);
        IReadOnlyDictionary<string, IReadOnlyList<string>>? errors = presentation?.Errors;
        if (presentation is null
            && string.Equals(
                error.Code,
                SubscriptionErrorCodes.ValidationFailed,
                StringComparison.Ordinal))
        {
            errors = FieldError("/", "The request failed application validation.");
        }

        return Problem(
            context,
            status,
            error.Code,
            title,
            detail,
            retryable,
            retryAfter,
            errors);
    }

    internal static string? RemoteIp(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString();

    internal static string? UserAgent(HttpContext context)
    {
        string value = context.Request.Headers.UserAgent.ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        byte[] input = Encoding.UTF8.GetBytes(value[..Math.Min(value.Length, 512)]);
        byte[] digest = SHA256.HashData(input);
        try
        {
            return string.Concat("sha256:", Convert.ToHexStringLower(digest));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> FieldError(
        string pointer,
        string message) => new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [pointer] = [message],
        };

    internal static PoolAI.Contracts.Generated.SubscriptionTemplate ToContract(
        SubscriptionTemplateView value) => new()
    {
        Id = value.Id.Value,
        GroupId = value.GroupId.Value,
        Name = value.Name,
        Description = value.Description is null ? default : new Optional<string?>(value.Description),
        DefaultDurationDays = value.DefaultDurationDays,
        Status = value.Status switch
        {
            SubscriptionTemplateLifecycle.Active => SubscriptionTemplateStatus.Active,
            SubscriptionTemplateLifecycle.Disabled => SubscriptionTemplateStatus.Disabled,
            SubscriptionTemplateLifecycle.Retired => SubscriptionTemplateStatus.Retired,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        },
        Version = value.Version,
        CreatedAt = value.CreatedAt,
        UpdatedAt = value.UpdatedAt,
    };

    internal static PoolAI.Contracts.Generated.Subscription ToContract(
        SubscriptionView value) => new()
    {
        Id = value.Id.Value,
        UserId = value.UserId.Value,
        GroupId = value.GroupId.Value,
        TemplateId = value.TemplateId.Value,
        PlanName = value.PlanName,
        StartsAt = value.StartsAt,
        ExpiresAt = value.ExpiresAt,
        Status = value.Status switch
        {
            SubscriptionLifecycle.Active => SubscriptionStatus.Active,
            SubscriptionLifecycle.Suspended => SubscriptionStatus.Suspended,
            SubscriptionLifecycle.Revoked => SubscriptionStatus.Revoked,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        },
        EffectiveStatus = value.EffectiveStatus switch
        {
            SubscriptionEffectiveLifecycle.Scheduled => "scheduled",
            SubscriptionEffectiveLifecycle.Active => "active",
            SubscriptionEffectiveLifecycle.Expired => "expired",
            SubscriptionEffectiveLifecycle.Suspended => "suspended",
            SubscriptionEffectiveLifecycle.Revoked => "revoked",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        },
        AssignedBy = value.AssignedBy.Value,
        Version = value.Version,
        CreatedAt = value.CreatedAt,
        UpdatedAt = value.UpdatedAt,
    };

    internal static PageInfo Page(string? nextCursor, bool hasMore) => new()
    {
        HasMore = hasMore,
        NextCursor = nextCursor is null ? default : new Optional<string?>(nextCursor),
    };

    internal static bool IsValidName(string? value) => value is not null
        && !string.IsNullOrWhiteSpace(value)
        && value.Length <= 100
        && !value.Any(char.IsControl);

    internal static bool IsValidDescription(string? value) =>
        value is null || value.Length <= 1000 && !value.Any(char.IsControl);

    internal static bool IsValidReason(string? value) => value is not null
        && !string.IsNullOrWhiteSpace(value)
        && value.Length <= 500
        && !value.Any(char.IsControl);

    private static (int, string, string, bool, long?) MapError(ResultError error) =>
        error.Code switch
        {
            SubscriptionErrorCodes.RoleRequired =>
                (403, "Required role missing", "The required role is missing.", false, null),
            SubscriptionErrorCodes.GroupDisabled =>
                (403, "Group disabled", "The Group is disabled.", false, null),
            SubscriptionErrorCodes.ResourceNotFound =>
                (404, "Resource not found", "The requested resource was not found.", false, null),
            SubscriptionErrorCodes.IdempotencyConflict =>
                (409, "Idempotency conflict", "The idempotency key was already used for a different request.", false, null),
            SubscriptionErrorCodes.ResourceConflict =>
                (409, "Resource conflict", "The requested state conflicts with the current resource state.", false, null),
            SubscriptionErrorCodes.SubscriptionConflict =>
                (409, "Subscription conflict", "A canonical Subscription already exists.", false, null),
            SubscriptionErrorCodes.SubscriptionTemplateDisabled =>
                (409, "Subscription Template disabled", "The Subscription Template is not active.", false, null),
            SubscriptionErrorCodes.VersionConflict =>
                (412, "Version conflict", "The resource version no longer matches; retrieve it again before retrying.", true, null),
            SubscriptionErrorCodes.ValidationFailed =>
                (422, "Validation failed", "One or more request fields failed validation.", false, null),
            SubscriptionErrorCodes.InvalidRequest =>
                (400, "Invalid request", "The request is invalid.", false, null),
            SubscriptionErrorCodes.CoordinationUnavailable =>
                (503, "Coordination unavailable", "The required coordination service is temporarily unavailable.", true, 1),
            _ => (500, "Internal error", "The request could not be completed safely.", false, null),
        };

    private static IResult Problem(
        HttpContext context,
        int status,
        string code,
        string title,
        string detail,
        bool retryable,
        long? retryAfter = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? errors = null)
    {
        if (retryAfter is not null)
        {
            context.Response.Headers.RetryAfter = retryAfter.Value.ToString(CultureInfo.InvariantCulture);
        }

        ControlPlaneProblem body = new()
        {
            Type = new Uri($"https://poolai.example/problems/{code.Replace('_', '-')}", UriKind.Absolute),
            Title = title,
            Status = status,
            Detail = detail,
            Instance = context.Request.Path.Value ?? "/",
            Code = code,
            RequestId = RequestId(context).Value,
            Retryable = retryable,
            RetryAfterSeconds = retryAfter is null ? default : retryAfter.Value,
            Errors = errors is null
                ? default
                : new Optional<IReadOnlyDictionary<string, IReadOnlyList<string>>>(errors),
        };
        return Results.Json(body, statusCode: status, contentType: "application/problem+json");
    }

    private static bool TryParseRole(string? value, out SystemRole role)
    {
        role = value switch
        {
            "admin" => SystemRole.Admin,
            "operator" => SystemRole.Operator,
            "auditor" => SystemRole.Auditor,
            "user" => SystemRole.User,
            _ => default,
        };
        return value is "admin" or "operator" or "auditor" or "user";
    }
}
