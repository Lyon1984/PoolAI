#pragma warning disable MA0048 // The authenticated actor is local to the shared HTTP adapter.
#pragma warning disable MA0051 // Stable HTTP error projection remains explicit in one switch.
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using PoolAI.BuildingBlocks;
using PoolAI.Contracts.Generated;

namespace PoolAI.Modules.Supply.Endpoints;

internal sealed record AuthenticatedSupplyActor(
    EntityId UserId,
    string Role,
    long TokenVersion);

internal static class SupplyHttp
{
    internal static EntityId RequestId(HttpContext context) =>
        Guid.TryParse(context.TraceIdentifier, out Guid requestId)
            ? new EntityId(requestId)
            : throw new InvalidOperationException("The API request identifier is invalid.");

    internal static AuthenticatedSupplyActor RequireActor(HttpContext context) =>
        TryGetActor(context, out AuthenticatedSupplyActor? actor)
            ? actor!
            : throw new InvalidOperationException(
                "The authenticated principal is missing required identity claims.");

    internal static bool TryGetActor(
        HttpContext context,
        out AuthenticatedSupplyActor? actor)
    {
        ClaimsPrincipal principal = context.User;
        string? userIdValue = principal.FindFirstValue("sub")
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        string? role = principal.FindFirstValue("role")
            ?? principal.FindFirstValue(ClaimTypes.Role);
        string? tokenVersionValue = principal.FindFirstValue("token_version");
        if (!Guid.TryParse(userIdValue, out Guid userId)
            || userId == Guid.Empty
            || role is not ("admin" or "operator" or "auditor" or "user")
            || !long.TryParse(
                tokenVersionValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long tokenVersion)
            || tokenVersion <= 0)
        {
            actor = null;
            return false;
        }

        actor = new AuthenticatedSupplyActor(new EntityId(userId), role, tokenVersion);
        return true;
    }

    internal static bool TryGetEntityId(
        HttpContext context,
        Guid value,
        string pointer,
        string resourceName,
        out EntityId entityId,
        out IResult? failure)
    {
        if (value == Guid.Empty)
        {
            entityId = default;
            failure = InvalidRequestProblem(
                context,
                FieldError(
                    pointer,
                    $"The {resourceName} path identifier must be a non-empty UUID."));
            return false;
        }

        entityId = new EntityId(value);
        failure = null;
        return true;
    }

    internal static bool TryGetPagination(
        HttpContext context,
        string? limit,
        out int parsedLimit,
        out IResult? failure)
    {
        parsedLimit = 50;
        if (limit is not null
            && (!int.TryParse(
                    limit,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out parsedLimit)
                || parsedLimit is < 1 or > 100))
        {
            failure = InvalidRequestProblem(
                context,
                FieldError("/limit", "The pagination limit must be between 1 and 100."));
            return false;
        }

        failure = null;
        return true;
    }

    internal static bool TryGetIdempotencyKey(
        HttpContext context,
        out string? idempotencyKey,
        out IResult? failure)
    {
        idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrEmpty(idempotencyKey))
        {
            failure = Problem(
                context,
                StatusCodes.Status428PreconditionRequired,
                "idempotency_key_required",
                "Idempotency key required",
                "The Idempotency-Key header is required.",
                retryable: false);
            return false;
        }

        if (idempotencyKey.Length > 128
            || idempotencyKey.Any(static character =>
                character is < (char)0x21 or > (char)0x7e))
        {
            failure = InvalidRequestProblem(
                context,
                FieldError(
                    "/headers/Idempotency-Key",
                    "The idempotency key is invalid."));
            return false;
        }

        failure = null;
        return true;
    }

    internal static bool TryGetExpectedVersion(
        HttpContext context,
        out long expectedVersion,
        out IResult? failure)
    {
        expectedVersion = 0;
        string ifMatch = context.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrEmpty(ifMatch))
        {
            expectedVersion = 0;
            failure = Problem(
                context,
                StatusCodes.Status428PreconditionRequired,
                "if_match_required",
                "Precondition required",
                "The If-Match header is required.",
                retryable: false);
            return false;
        }

        bool valid = ifMatch.Length >= 4
            && ifMatch.StartsWith("\"v", StringComparison.Ordinal)
            && ifMatch[2] is >= '1' and <= '9'
            && ifMatch.EndsWith('"')
            && long.TryParse(
                ifMatch.AsSpan(2, ifMatch.Length - 3),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out expectedVersion)
            && expectedVersion > 0;
        if (!valid)
        {
            expectedVersion = 0;
            failure = InvalidRequestProblem(
                context,
                FieldError(
                    "/headers/If-Match",
                    "If-Match must be a strong ETag such as \"v7\"."));
            return false;
        }

        failure = null;
        return true;
    }

    internal static bool TryGetChangeReason(
        HttpContext context,
        out string? reason,
        out IResult? failure)
    {
        reason = context.Request.Headers["X-Change-Reason"].ToString();
        if (string.IsNullOrWhiteSpace(reason)
            || reason.Length > 500
            || reason.Any(static character => character is '\r' or '\n'))
        {
            failure = InvalidRequestProblem(
                context,
                FieldError(
                    "/headers/X-Change-Reason",
                    "X-Change-Reason must be non-blank and at most 500 characters."));
            return false;
        }

        failure = null;
        return true;
    }

    internal static IResult? RequireContentType(HttpContext context, string expected)
    {
        string contentType = context.Request.ContentType ?? string.Empty;
        int separator = contentType.IndexOf(';', StringComparison.Ordinal);
        string mediaType = (separator < 0 ? contentType : contentType[..separator]).Trim();
        return string.Equals(mediaType, expected, StringComparison.OrdinalIgnoreCase)
            ? null
            : Problem(
                context,
                StatusCodes.Status415UnsupportedMediaType,
                "unsupported_media_type",
                "Unsupported media type",
                $"This operation requires {expected}.",
                retryable: false);
    }

    internal static IResult ValidationProblem(
        HttpContext context,
        IReadOnlyDictionary<string, IReadOnlyList<string>> errors) => Problem(
        context,
        StatusCodes.Status422UnprocessableEntity,
        "validation_failed",
        "Validation failed",
        "One or more request fields failed validation.",
        retryable: false,
        errors: errors);

    internal static IResult InvalidRequestProblem(
        HttpContext context,
        IReadOnlyDictionary<string, IReadOnlyList<string>> errors) => Problem(
        context,
        StatusCodes.Status400BadRequest,
        "invalid_request",
        "Invalid request",
        "One or more request parameters are invalid.",
        retryable: false,
        errors: errors);

    internal static IResult InvalidUserToken(HttpContext context)
    {
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return Problem(
            context,
            StatusCodes.Status401Unauthorized,
            "invalid_user_token",
            "Invalid user token",
            "The user access token is missing required identity claims.",
            retryable: false);
    }

    internal static IResult FromError(HttpContext context, ResultError error)
    {
        HttpError mapped = MapError(error);
        if (error.Presentation is ResultErrorPresentation presentation)
        {
            if (!string.Equals(presentation.Code, error.Code, StringComparison.Ordinal)
                || presentation.RetryAfterSeconds != error.RetryAfterSeconds)
            {
                throw new InvalidOperationException(
                    "The frozen Supply error presentation is invalid.");
            }

            mapped = new HttpError(
                presentation.Code,
                presentation.Status,
                presentation.Title,
                presentation.Detail,
                presentation.Retryable,
                presentation.RetryAfterSeconds,
                presentation.Errors);
        }

        if (error.ETag is not null)
        {
            context.Response.Headers.ETag = error.ETag;
        }

        return Problem(
            context,
            mapped.Status,
            mapped.Code,
            mapped.Title,
            mapped.Detail,
            mapped.Retryable,
            mapped.RetryAfterSeconds,
            mapped.Errors);
    }

    internal static string ETag(long version) => $"\"v{version}\"";

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

    private static HttpError MapError(ResultError error) => error.Code switch
    {
        "role_required" or "forbidden" => new(
            "role_required",
            403,
            "Required role missing",
            "The required role is missing.",
            false),
        "resource_not_found" => new(
            error.Code,
            404,
            "Resource not found",
            "The requested resource was not found.",
            false),
        "idempotency_conflict" => new(
            error.Code,
            409,
            "Idempotency conflict",
            "The idempotency key was used for a different request.",
            false),
        "resource_conflict" => new(
            error.Code,
            409,
            "Resource conflict",
            "The requested state conflicts with the current resource state.",
            false),
        "account_in_use" => new(
            error.Code,
            409,
            "Account in use",
            "The Account still has an enabled Supply binding.",
            false),
        "channel_in_use" => new(
            error.Code,
            409,
            "Channel in use",
            "The Channel is still referenced by a Supply configuration.",
            false),
        "version_conflict" => new(
            error.Code,
            412,
            "Version conflict",
            "The resource version no longer matches.",
            true),
        "validation_failed" or "group_account_binding_invalid" => new(
            error.Code,
            422,
            "Validation failed",
            "One or more request fields failed validation.",
            false),
        "invalid_request" => new(
            error.Code,
            400,
            "Invalid request",
            "The request is invalid.",
            false),
        "coordination_unavailable" => new(
            error.Code,
            503,
            "Coordination unavailable",
            "Required coordination is temporarily unavailable.",
            true,
            1),
        "dependency_unavailable" or "service_unavailable" => new(
            "dependency_unavailable",
            503,
            "Dependency unavailable",
            "A required dependency is temporarily unavailable.",
            true,
            1),
        _ => new(
            error.Code,
            500,
            "Internal error",
            "The request could not be completed safely.",
            false),
    };

    private static IResult Problem(
        HttpContext context,
        int status,
        string code,
        string title,
        string detail,
        bool retryable,
        long? retryAfterSeconds = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? errors = null)
    {
        if (retryAfterSeconds is not null)
        {
            context.Response.Headers.RetryAfter = retryAfterSeconds.Value.ToString(
                CultureInfo.InvariantCulture);
        }

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
            RequestId = RequestId(context).Value,
            Retryable = retryable,
            RetryAfterSeconds = retryAfterSeconds is null ? default : retryAfterSeconds.Value,
            Errors = errors is null
                ? default
                : new Optional<IReadOnlyDictionary<string, IReadOnlyList<string>>>(errors),
        };
        return Results.Json(
            problem,
            statusCode: status,
            contentType: "application/problem+json");
    }

    private sealed record HttpError(
        string Code,
        int Status,
        string Title,
        string Detail,
        bool Retryable,
        long? RetryAfterSeconds = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? Errors = null);
}
#pragma warning restore MA0051
#pragma warning restore MA0048
