using System.Globalization;
using System.Net.Mail;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using PoolAI.Contracts.Generated;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application;

namespace PoolAI.Modules.Identity.Endpoints;

internal static class IdentityHttp
{
    public static EntityId RequestId(HttpContext context) =>
        Guid.TryParse(context.TraceIdentifier, out Guid requestId)
            ? new EntityId(requestId)
            : throw new InvalidOperationException("The API request identifier is invalid.");

    public static IdentityActor RequireActor(HttpContext context)
    {
        return TryGetActor(context, out IdentityActor? actor)
            ? actor!
            : throw new InvalidOperationException("The authenticated principal is missing required identity claims.");
    }

    public static bool TryGetActor(HttpContext context, out IdentityActor? actor)
    {
        ClaimsPrincipal principal = context.User;
        string? userIdValue = principal.FindFirstValue("sub")
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        string? roleValue = principal.FindFirstValue("role")
            ?? principal.FindFirstValue(ClaimTypes.Role);
        string? tokenVersionValue = principal.FindFirstValue("token_version");
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

        actor = new IdentityActor(new EntityId(userId), role, tokenVersion);
        return true;
    }

    public static bool TryGetEntityId(
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
                StatusCodes.Status400BadRequest,
                "invalid_request",
                "Invalid request",
                $"The {parameterName} path parameter must be a non-empty UUID.",
                retryable: false);
            return false;
        }

        entityId = new EntityId(value);
        failure = null;
        return true;
    }

    public static bool TryGetIdempotencyKey(
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
            || idempotencyKey.Any(static character => character is < (char)0x21 or > (char)0x7e))
        {
            failure = Problem(
                context,
                StatusCodes.Status400BadRequest,
                "invalid_request",
                "Invalid request",
                "The Idempotency-Key header is invalid.",
                retryable: false);
            return false;
        }

        failure = null;
        return true;
    }

    public static bool TryGetExpectedVersion(
        HttpContext context,
        out long expectedVersion,
        out IResult? failure)
    {
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

        long parsedVersion = 0;
        bool valid = ifMatch.Length >= 4
            && ifMatch.StartsWith("\"v", StringComparison.Ordinal)
            && ifMatch.EndsWith('"')
            && long.TryParse(
                ifMatch.AsSpan(2, ifMatch.Length - 3),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out parsedVersion)
            && parsedVersion > 0;
        if (!valid)
        {
            expectedVersion = 0;
            failure = Problem(
                context,
                StatusCodes.Status400BadRequest,
                "invalid_request",
                "Invalid request",
                "If-Match must be a single strong resource ETag such as \"v7\".",
                retryable: false);
            return false;
        }

        expectedVersion = parsedVersion;
        failure = null;
        return true;
    }

    public static IResult? RequireContentType(HttpContext context, string expected)
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

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Validate(UserCreateRequest request)
    {
        Dictionary<string, IReadOnlyList<string>> errors = new(StringComparer.Ordinal);
        if (!IsEmail(request.Email))
        {
            errors["/email"] =
                ["A supported email mailbox of at most 254 characters is required."];
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName)
            || request.DisplayName.Length > 100
            || request.DisplayName.Any(char.IsControl))
        {
            errors["/display_name"] = ["The display name must contain between 1 and 100 characters."];
        }

        if (!Enum.IsDefined(request.Role))
        {
            errors["/role"] = ["The role is invalid."];
        }

        if (request.TemporaryPassword is null
            || request.TemporaryPassword.Length is < 12 or > 1024)
        {
            errors["/temporary_password"] = ["The temporary password must contain between 12 and 1024 characters."];
        }

        return errors;
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Validate(UserUpdateRequest request)
    {
        Dictionary<string, IReadOnlyList<string>> errors = new(StringComparer.Ordinal);
        if (!request.DisplayName.HasValue && !request.Role.HasValue && !request.Status.HasValue)
        {
            errors["/"] = ["At least one mutable user field is required."];
        }

        if (request.DisplayName.HasValue
            && (string.IsNullOrWhiteSpace(request.DisplayName.Value)
                || request.DisplayName.Value!.Length > 100
                || request.DisplayName.Value!.Any(char.IsControl)))
        {
            errors["/display_name"] = ["The display name must contain between 1 and 100 characters."];
        }

        if (request.Role.HasValue && !Enum.IsDefined(request.Role.Value))
        {
            errors["/role"] = ["The role is invalid."];
        }

        if (request.Status.HasValue && !Enum.IsDefined(request.Status.Value))
        {
            errors["/status"] = ["The status is invalid."];
        }

        if ((request.Role.HasValue || request.Status.HasValue)
            && (!request.Reason.HasValue
                || string.IsNullOrWhiteSpace(request.Reason.Value)
                || request.Reason.Value!.Length > 500
                || request.Reason.Value!.Any(static character => character is '\r' or '\n')))
        {
            errors["/reason"] = ["Role and status changes require a non-blank reason of at most 500 characters."];
        }

        return errors;
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> FieldError(
        string pointer,
        string message) =>
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [pointer] = [message],
        };

    public static bool IsEmail(string? value) => TryNormalizeEmail(value, out _);

    public static bool TryNormalizeEmail(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null)
        {
            return false;
        }

        try
        {
            normalized = NormalizeMailbox(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string NormalizeMailbox(string value)
    {
        MailAddress address = ParseMailbox(value);
        int separator = address.Address.LastIndexOf('@');
        if (separator <= 0 || separator == address.Address.Length - 1)
        {
            throw new ArgumentException("The email mailbox is invalid.", nameof(value));
        }

        string localPart = address.Address[..separator];
        ValidateLocalPart(localPart);
        string asciiDomain = NormalizeDomain(address.Address[(separator + 1)..]);
        string canonical = string.Concat(localPart, "@", asciiDomain);
        if (canonical.Length > 254)
        {
            throw new ArgumentException("The email mailbox is too long.", nameof(value));
        }

        return canonical;
    }

    private static MailAddress ParseMailbox(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 320
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Any(static character => character is '\r' or '\n' or '\0'
                || char.IsControl(character)))
        {
            throw new ArgumentException("The email mailbox is invalid.", nameof(value));
        }

        MailAddress address = new(value);
        return string.Equals(address.Address, value, StringComparison.Ordinal)
            ? address
            : throw new ArgumentException("The email mailbox is invalid.", nameof(value));
    }

    private static void ValidateLocalPart(string localPart)
    {
        if (localPart.Length is < 1 or > 64
            || localPart[0] == '.'
            || localPart[^1] == '.'
            || localPart.Contains("..", StringComparison.Ordinal)
            || !localPart.All(IsMailboxLocalPartCharacter))
        {
            throw new ArgumentException("The email local part is not supported.", nameof(localPart));
        }
    }

    private static string NormalizeDomain(string domain)
    {
        string asciiDomain;
        try
        {
            asciiDomain = new IdnMapping
            {
                UseStd3AsciiRules = true,
            }.GetAscii(domain).ToLowerInvariant();
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("The email domain is invalid.", nameof(domain), exception);
        }

        if (!IsCanonicalDnsDomain(asciiDomain))
        {
            throw new ArgumentException("The email domain is invalid.", nameof(domain));
        }

        return asciiDomain;
    }

    public static string ETag(long version) => $"\"v{version.ToString(CultureInfo.InvariantCulture)}\"";

    public static PoolAI.Contracts.Generated.User ToContract(UserView value) => new()
    {
        Id = value.Id.Value,
        Email = value.Email,
        DisplayName = value.DisplayName,
        Role = ToContractRole(value.Role),
        Status = value.Status is UserLifecycle.Active ? UserStatus.Active : UserStatus.Disabled,
        Version = value.Version,
        CreatedAt = value.CreatedAt,
        UpdatedAt = value.UpdatedAt,
    };

    public static SystemRole ToSystemRole(Role value) => value switch
    {
        Role.Admin => SystemRole.Admin,
        Role.Operator => SystemRole.Operator,
        Role.Auditor => SystemRole.Auditor,
        Role.User => SystemRole.User,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static UserLifecycle ToLifecycle(UserStatus value) => value switch
    {
        UserStatus.Active => UserLifecycle.Active,
        UserStatus.Disabled => UserLifecycle.Disabled,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static IResult ValidationProblem(
        HttpContext context,
        IReadOnlyDictionary<string, IReadOnlyList<string>> errors) =>
        Problem(
            context,
            StatusCodes.Status422UnprocessableEntity,
            "validation_failed",
            "Validation failed",
            "One or more request fields failed validation.",
            retryable: false,
            errors: errors);

    public static IResult InvalidRequestProblem(
        HttpContext context,
        IReadOnlyDictionary<string, IReadOnlyList<string>> errors) =>
        Problem(
            context,
            StatusCodes.Status400BadRequest,
            "invalid_request",
            "Invalid request",
            "One or more request parameters are invalid.",
            retryable: false,
            errors: errors);

    public static IResult InvalidUserToken(HttpContext context)
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

    public static IResult FromError(HttpContext context, ResultError error)
    {
        HttpError mapped = MapError(error);
        mapped = ApplyFrozenPresentation(context, error, mapped);
        IReadOnlyDictionary<string, IReadOnlyList<string>>? errors = mapped.Errors;
        errors ??= mapped.Code switch
        {
            "password_policy_failed" => FieldError(
                PasswordFieldPointer(context),
                "The password does not satisfy the configured policy."),
            "validation_failed" => FieldError(
                "/",
                "The request failed application validation."),
            _ => null,
        };

        if (error.ETag is not null)
        {
            context.Response.Headers.ETag = error.ETag;
        }

        if (mapped.Status == StatusCodes.Status401Unauthorized)
        {
            context.Response.Headers.WWWAuthenticate =
                string.Equals(
                    mapped.Code,
                    "password_reset_token_invalid",
                    StringComparison.Ordinal)
                    ? "PasswordReset"
                    : "Bearer";
        }

        return Problem(
            context,
            mapped.Status,
            mapped.Code,
            mapped.Title,
            mapped.Detail,
            mapped.Retryable,
            mapped.RetryAfterSeconds,
            errors);
    }

    private static HttpError MapError(ResultError error) => error.Code switch
    {
        "role_required" or "forbidden" => new(
            "role_required", 403, "Required role missing", "The required role is missing.", false),
        "resource_not_found" => new(
            error.Code, 404, "Resource not found", "The requested resource was not found.", false),
        "idempotency_conflict" => new(
            error.Code, 409, "Idempotency conflict", "The idempotency key was already used for a different request.", false),
        "resource_conflict" => new(
            error.Code, 409, "Resource conflict", "The requested state conflicts with the current resource state.", false),
        "version_conflict" => new(
            error.Code, 412, "Version conflict", "The resource version no longer matches; retrieve it again before retrying.", true),
        "password_reset_token_invalid" => new(
            error.Code, 401, "Invalid password-reset token", "The password-reset token is invalid, expired, or already used.", false),
        "password_policy_failed" => new(
            error.Code, 422, "Password policy failed", "The password does not meet the configured policy.", false),
        "validation_failed" => new(
            error.Code, 422, "Validation failed", "One or more request fields failed validation.", false),
        "invalid_request" => new(
            error.Code, 400, "Invalid request", "The request is invalid.", false),
        "rate_limit_exceeded" => new(
            error.Code, 429, "Rate limit exceeded", "Too many requests were received for this operation.", true, error.RetryAfterSeconds),
        "coordination_unavailable" => new(
            error.Code, 503, "Coordination unavailable", "The required coordination service is temporarily unavailable.", true, 1),
        "dependency_unavailable" or "service_unavailable" => new(
            "dependency_unavailable", 503, "Dependency unavailable", "A required dependency is temporarily unavailable.", true, 1),
        _ => new(
            error.Code, 500, "Internal error", "The request could not be completed safely.", false),
    };

    private static HttpError ApplyFrozenPresentation(
        HttpContext context,
        ResultError error,
        HttpError mapped)
    {
        if (error.Presentation is not ResultErrorPresentation presentation)
        {
            return mapped;
        }

        if (!string.Equals(mapped.Code, presentation.Code, StringComparison.Ordinal)
            || presentation.RetryAfterSeconds != error.RetryAfterSeconds)
        {
            throw new InvalidOperationException(
                "The frozen error presentation does not match its result error.");
        }

        return new HttpError(
            presentation.Code,
            presentation.Status,
            presentation.Title,
            presentation.Detail,
            presentation.Retryable,
            presentation.RetryAfterSeconds,
            presentation.Errors);
    }

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

        ControlPlaneProblem problem = new()
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
        return Results.Json(problem, statusCode: status, contentType: "application/problem+json");
    }

    public static string? RemoteIp(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString();

    public static string? UserAgent(HttpContext context)
    {
        string value = context.Request.Headers.UserAgent.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value[..Math.Min(value.Length, 512)];
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

    private static string PasswordFieldPointer(HttpContext context) =>
        context.Request.Path.Equals(
            "/api/v1/auth/reset-password",
            StringComparison.Ordinal)
            ? "/new_password"
            : "/temporary_password";

    private static bool IsCanonicalDnsDomain(string domain)
    {
        if (domain.Length is < 1 or > 253
            || domain[0] == '.'
            || domain[^1] == '.')
        {
            return false;
        }

        foreach (string label in domain.Split('.'))
        {
            if (label.Length is < 1 or > 63
                || !char.IsAsciiLetterOrDigit(label[0])
                || !char.IsAsciiLetterOrDigit(label[^1])
                || label.Any(static character =>
                    !char.IsAsciiLetterOrDigit(character) && character != '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsMailboxLocalPartCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character)
            || character is '.' or '!' or '#' or '$' or '%' or '&' or '\'' or '*'
                or '+' or '-' or '/' or '=' or '?' or '^' or '_' or '`' or '{' or '|'
                or '}' or '~';

    private sealed record HttpError(
        string Code,
        int Status,
        string Title,
        string Detail,
        bool Retryable,
        long? RetryAfterSeconds = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? Errors = null);

    private static Role ToContractRole(SystemRole value) => value switch
    {
        SystemRole.Admin => Role.Admin,
        SystemRole.Operator => Role.Operator,
        SystemRole.Auditor => Role.Auditor,
        SystemRole.User => Role.User,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}
