#pragma warning disable MA0051 // Stable HTTP error mapping is intentionally explicit.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using PoolAI.BuildingBlocks;
using PoolAI.Contracts.Generated;
using PoolAI.Modules.Identity.Abstractions;

namespace PoolAI.Api;

internal static class ApiKeyHttp
{
    internal static EntityId RequestId(HttpContext context) =>
        new(RequestIdMiddleware.GetRequestId(context));

    internal static bool TryGetActor(HttpContext context, out ApiKeyActor? actor)
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

        actor = new ApiKeyActor(new EntityId(userId), role, tokenVersion);
        return true;
    }

    internal static ApiKeyActor RequireActor(HttpContext context) =>
        TryGetActor(context, out ApiKeyActor? actor)
            ? actor!
            : throw new InvalidOperationException(
                "The authenticated principal is missing required identity claims.");

    internal static IResult InvalidUserToken(HttpContext context)
    {
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return Problem(
            StatusCodes.Status401Unauthorized,
            "invalid_user_token",
            "Invalid user token",
            "The user access token is missing required identity claims.",
            retryable: false);
    }

    internal static bool TryGetEntityId(
        Guid value,
        string parameterName,
        out EntityId entityId,
        out IResult? failure)
    {
        if (value == Guid.Empty)
        {
            entityId = default;
            failure = InvalidRequest(
                $"/{parameterName}",
                $"The {parameterName} path parameter must be a non-empty UUID.");
            return false;
        }

        entityId = new EntityId(value);
        failure = null;
        return true;
    }

    internal static bool TryGetPagination(
        HttpContext context,
        out string? cursor,
        out int limit,
        out IResult? failure)
    {
        Microsoft.Extensions.Primitives.StringValues cursorValues =
            context.Request.Query["cursor"];
        if (cursorValues.Count > 1)
        {
            cursor = null;
            limit = 0;
            failure = InvalidRequest("/cursor", "cursor must be supplied at most once.");
            return false;
        }

        cursor = cursorValues.Count == 0 ? null : cursorValues[0];
        if (cursor is not null && cursor.Length is < 1 or > 512)
        {
            limit = 0;
            failure = InvalidRequest(
                "/cursor",
                "cursor must contain between 1 and 512 characters.");
            return false;
        }

        Microsoft.Extensions.Primitives.StringValues limitValues =
            context.Request.Query["limit"];
        if (limitValues.Count > 1)
        {
            limit = 0;
            failure = InvalidRequest("/limit", "limit must be supplied at most once.");
            return false;
        }

        limit = 50;
        if (limitValues.Count == 1
            && (!int.TryParse(
                    limitValues[0],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out limit)
                || limit is < 1 or > 100))
        {
            failure = InvalidRequest("/limit", "limit must be between 1 and 100.");
            return false;
        }

        failure = null;
        return true;
    }

    internal static bool TryGetIdempotencyKey(
        HttpContext context,
        out string? key,
        out IResult? failure)
    {
        Microsoft.Extensions.Primitives.StringValues values =
            context.Request.Headers["Idempotency-Key"];
        key = values.ToString();
        if (values.Count == 0 || string.IsNullOrEmpty(key))
        {
            failure = Problem(
                StatusCodes.Status428PreconditionRequired,
                "idempotency_key_required",
                "Idempotency key required",
                "The Idempotency-Key header is required.",
                retryable: false);
            return false;
        }

        if (values.Count != 1
            || key.Length > 128
            || key.Any(static character =>
                character is < (char)0x21 or > (char)0x7e))
        {
            failure = InvalidRequest(
                "/headers/Idempotency-Key",
                "The Idempotency-Key header must contain 1 to 128 visible ASCII characters.");
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
        Microsoft.Extensions.Primitives.StringValues values =
            context.Request.Headers.IfMatch;
        string value = values.ToString();
        if (values.Count == 0 || string.IsNullOrEmpty(value))
        {
            version = 0;
            failure = Problem(
                StatusCodes.Status428PreconditionRequired,
                "if_match_required",
                "Precondition required",
                "The If-Match header is required.",
                retryable: false);
            return false;
        }

        version = 0;
        bool valid = values.Count == 1
            && value.Length >= 4
            && value.StartsWith("\"v", StringComparison.Ordinal)
            && value.EndsWith('"')
            && value[2] is >= '1' and <= '9'
            && long.TryParse(
                value.AsSpan(2, value.Length - 3),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out version)
            && version > 0
            && string.Equals(value, ETag(version), StringComparison.Ordinal);
        if (!valid)
        {
            version = 0;
            failure = InvalidRequest(
                "/headers/If-Match",
                "If-Match must contain one strong API Key ETag such as \"v7\".");
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
        Microsoft.Extensions.Primitives.StringValues values =
            context.Request.Headers["X-Change-Reason"];
        reason = values.ToString();
        if (values.Count != 1 || !ApiKeyTextRules.IsValidAdminReason(reason))
        {
            failure = InvalidRequest(
                "/headers/X-Change-Reason",
                "X-Change-Reason must contain one valid audit reason of at most 500 Unicode scalar values.");
            return false;
        }

        failure = null;
        return true;
    }

    internal static IResult? RequireJsonContentType(HttpContext context) =>
        RequireContentType(context, "application/json");

    internal static IResult? RequireContentType(
        HttpContext context,
        string expected)
    {
        string value = context.Request.ContentType ?? string.Empty;
        int separator = value.IndexOf(';', StringComparison.Ordinal);
        string mediaType = (separator < 0 ? value : value[..separator]).Trim();
        return string.Equals(mediaType, expected, StringComparison.OrdinalIgnoreCase)
            ? null
            : Problem(
                StatusCodes.Status415UnsupportedMediaType,
                "unsupported_media_type",
                "Unsupported media type",
                $"This operation requires {expected}.",
                retryable: false);
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> ValidateCreate(
        string? name,
        Guid groupId,
        IReadOnlyList<string>? allowedCidrs,
        bool allowedCidrsSpecified,
        string? reason,
        bool reasonRequired)
    {
        Dictionary<string, IReadOnlyList<string>> errors = new(StringComparer.Ordinal);
        if (!ApiKeyTextRules.IsValidName(name))
        {
            errors["/name"] = ["A non-blank API Key name of at most 100 characters is required."];
        }

        if (groupId == Guid.Empty)
        {
            errors["/group_id"] = ["group_id must be a non-empty UUID."];
        }

        if (allowedCidrsSpecified && allowedCidrs is null)
        {
            errors["/allowed_cidrs"] = ["allowed_cidrs must be an array when supplied."];
        }
        else if (allowedCidrs is { Count: > 50 }
            || allowedCidrs is not null && allowedCidrs.Any(static cidr => !IsValidCidr(cidr)))
        {
            errors["/allowed_cidrs"] =
            [
                "allowed_cidrs must contain at most 50 valid IPv4 or IPv6 CIDR values.",
            ];
        }

        if (reasonRequired
            && !ApiKeyTextRules.IsValidAdminReason(reason))
        {
            errors["/reason"] =
            [
                "A non-blank audit reason of at most 500 characters is required.",
            ];
        }

        return errors;
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> ValidateUpdate(
        bool setName,
        string? name,
        bool setStatus,
        string? status,
        bool setExpiresAt,
        bool setAllowedCidrs,
        IReadOnlyList<string>? allowedCidrs,
        string? reason,
        bool reasonRequired)
    {
        Dictionary<string, IReadOnlyList<string>> errors = new(StringComparer.Ordinal);
        if (!setName && !setStatus && !setExpiresAt && !setAllowedCidrs)
        {
            errors["/"] = ["At least one mutable API Key field is required."];
        }

        if (setName && !ApiKeyTextRules.IsValidName(name))
        {
            errors["/name"] =
            [
                "A non-blank API Key name of at most 100 Unicode scalar values is required.",
            ];
        }

        if (setStatus && status is not ("active" or "disabled"))
        {
            errors["/status"] = ["status must be active or disabled."];
        }

        if (setAllowedCidrs && allowedCidrs is null)
        {
            errors["/allowed_cidrs"] = ["allowed_cidrs must be an array when supplied."];
        }
        else if (setAllowedCidrs
            && (allowedCidrs is { Count: > 50 }
                || allowedCidrs is not null
                && allowedCidrs.Any(static cidr => !IsValidCidr(cidr))))
        {
            errors["/allowed_cidrs"] =
            [
                "allowed_cidrs must contain at most 50 valid IPv4 or IPv6 CIDR values.",
            ];
        }

        if (reasonRequired && !ApiKeyTextRules.IsValidAdminReason(reason))
        {
            errors["/reason"] =
            [
                "A non-blank audit reason of at most 500 Unicode scalar values is required.",
            ];
        }

        return errors;
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> ValidateRotate(
        string? reason)
    {
        if (ApiKeyTextRules.IsValidAdminReason(reason))
        {
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        }

        return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["/reason"] =
            [
                "A non-blank audit reason of at most 500 Unicode scalar values is required.",
            ],
        };
    }

    internal static ApiKeyPersistentStatus? ParseMutableStatus(string? value) => value switch
    {
        "active" => ApiKeyPersistentStatus.Active,
        "disabled" => ApiKeyPersistentStatus.Disabled,
        _ => null,
    };

    internal static PoolAI.Contracts.Generated.ApiKey ToContract(
        ApiKeyControlPlaneSnapshot value) => new()
    {
        Id = value.ApiKeyId.Value,
        Name = value.Name,
        Prefix = value.Prefix,
        GroupId = value.GroupId.Value,
        Status = value.Status switch
        {
            ApiKeyPersistentStatus.Active => "active",
            ApiKeyPersistentStatus.Disabled => "disabled",
            ApiKeyPersistentStatus.Revoked => "revoked",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        },
        EffectiveStatus = value.EffectiveStatus switch
        {
            ApiKeyEffectiveStatus.Active => "active",
            ApiKeyEffectiveStatus.Disabled => "disabled",
            ApiKeyEffectiveStatus.Expired => "expired",
            ApiKeyEffectiveStatus.Revoked => "revoked",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        },
        Version = value.Version,
        ExpiresAt = new Optional<DateTimeOffset?>(value.ExpiresAt),
        AllowedCidrs = value.AllowedCidrs,
        CreatedAt = value.CreatedAt,
        LastUsedAt = new Optional<DateTimeOffset?>(value.LastUsedAt),
    };

    internal static PageInfo Page(string? nextCursor, bool hasMore) => new()
    {
        HasMore = hasMore,
        NextCursor = nextCursor is null
            ? default
            : new Optional<string?>(nextCursor),
    };

    internal static string ETag(long version)
    {
        if (version <= 0)
        {
            throw new InvalidOperationException("An API Key version must be positive.");
        }

        return $"\"v{version}\"";
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

    internal static IResult ValidationProblem(
        IReadOnlyDictionary<string, IReadOnlyList<string>> errors) => Problem(
        StatusCodes.Status422UnprocessableEntity,
        "validation_failed",
        "Validation failed",
        "One or more request fields failed validation.",
        retryable: false,
        errors: errors);

    internal static IResult InvalidRequest(string pointer, string message) => Problem(
        StatusCodes.Status400BadRequest,
        "invalid_request",
        "Invalid request",
        "One or more request parameters are invalid.",
        retryable: false,
        errors: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [pointer] = [message],
        });

    internal static IResult FromError(HttpContext context, ResultError error)
    {
        HttpError mapped = MapError(error);
        if (error.Presentation is ResultErrorPresentation presentation)
        {
            if (!string.Equals(error.Code, presentation.Code, StringComparison.Ordinal)
                || presentation.RetryAfterSeconds != error.RetryAfterSeconds)
            {
                throw new InvalidOperationException(
                    "The frozen API Key error presentation is inconsistent.");
            }

            mapped = new HttpError(
                presentation.Status,
                presentation.Code,
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
            mapped.Status,
            mapped.Code,
            mapped.Title,
            mapped.Detail,
            mapped.Retryable,
            mapped.RetryAfterSeconds,
            mapped.Errors);
    }

    private static bool IsValidCidr(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 64
            || value.Contains('%', StringComparison.Ordinal))
        {
            return false;
        }

        int separator = value.IndexOf('/');
        if (separator <= 0
            || separator != value.LastIndexOf('/')
            || separator == value.Length - 1)
        {
            return false;
        }

        string addressValue = value[..separator];
        string prefixValue = value[(separator + 1)..];
        if (prefixValue.Length > 1 && prefixValue[0] == '0'
            || !int.TryParse(
                prefixValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int prefix)
            || !IPAddress.TryParse(addressValue, out IPAddress? address)
            || address.IsIPv4MappedToIPv6)
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            string[] octets = addressValue.Split('.');
            return octets.Length == 4
                && octets.All(static octet => octet.Length == 1 || octet[0] != '0')
                && prefix is >= 0 and <= 32;
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
            && prefix is >= 0 and <= 128;
    }

    private static HttpError MapError(ResultError error) => error.Code switch
    {
        "forbidden" => new(
            403, error.Code, "Forbidden", "The authenticated user cannot perform this operation.", false),
        "role_required" => new(
            403, error.Code, "Required role missing", "The required role is missing.", false),
        "subscription_required" => new(
            403, error.Code, "Subscription required", "An active Subscription is required for this Group.", false),
        "subscription_inactive" => new(
            403, error.Code, "Subscription inactive", "The Subscription for this Group is not active.", false),
        "group_disabled" => new(
            403, error.Code, "Group disabled", "The Group is disabled.", false),
        "resource_not_found" => new(
            404, error.Code, "Resource not found", "The requested resource was not found.", false),
        "idempotency_conflict" => new(
            409, error.Code, "Idempotency conflict", "The idempotency key was used for a different request.", false),
        "resource_conflict" => new(
            409, error.Code, "Resource conflict", "The request conflicts with the current resource state.", false),
        "api_key_group_immutable" => new(
            409, error.Code, "API Key Group immutable", "An API Key cannot be moved to another Group.", false),
        "api_key_revoked" => new(
            409, error.Code, "API Key revoked", "A revoked API Key cannot be restored.", false),
        "version_conflict" => new(
            412,
            error.Code,
            "Version conflict",
            "The resource version no longer matches; retrieve it again before retrying.",
            true),
        "validation_failed" => new(
            422,
            error.Code,
            "Validation failed",
            "One or more request fields failed validation.",
            false,
            Errors: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["/"] = ["The request failed application validation."],
            }),
        "invalid_request" => new(
            400, error.Code, "Invalid request", "The request is invalid.", false),
        "rate_limit_exceeded" => new(
            429,
            error.Code,
            "Rate limit exceeded",
            "Too many requests were received for this operation.",
            true,
            error.RetryAfterSeconds ?? 1),
        "coordination_unavailable" => new(
            503,
            error.Code,
            "Coordination unavailable",
            "The required coordination service is temporarily unavailable.",
            true,
            error.RetryAfterSeconds ?? 1),
        "dependency_unavailable" or "service_unavailable" => new(
            503,
            "dependency_unavailable",
            "Dependency unavailable",
            "A required dependency is temporarily unavailable.",
            true,
            1),
        _ => new(
            500,
            "internal_error",
            "Internal error",
            "The request could not be completed safely.",
            false),
    };

    private static ProblemResult Problem(
        int status,
        string code,
        string title,
        string detail,
        bool retryable,
        long? retryAfterSeconds = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? errors = null) =>
        new ProblemResult(
            status,
            code,
            title,
            detail,
            retryable,
            retryAfterSeconds,
            errors);

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

    private sealed record ProblemResult(
        int Status,
        string Code,
        string Title,
        string Detail,
        bool Retryable,
        long? RetryAfterSeconds,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? Errors) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) =>
            ControlPlaneProblemWriter.WriteAsync(
                httpContext,
                Status,
                Code,
                Title,
                Detail,
                Retryable,
                RetryAfterSeconds,
                Errors);
    }

    private sealed record HttpError(
        int Status,
        string Code,
        string Title,
        string Detail,
        bool Retryable,
        long? RetryAfterSeconds = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? Errors = null);
}

#pragma warning restore MA0051
