using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using PoolAI.Contracts.Generated;
using PoolAI.Modules.Operations.Application;

namespace PoolAI.Modules.Operations.Endpoints;

internal static class OutboxReplayHttp
{
    internal static EntityId RequestId(HttpContext context) =>
        Guid.TryParse(context.TraceIdentifier, out Guid requestId)
            ? new EntityId(requestId)
            : throw new InvalidOperationException("The API request identifier is invalid.");

    internal static OutboxReplayActor RequireActor(HttpContext context) =>
        TryGetActor(context, out OutboxReplayActor? actor)
            ? actor!
            : throw new InvalidOperationException(
                "The authenticated principal is missing required identity claims.");

    internal static bool TryGetActor(HttpContext context, out OutboxReplayActor? actor)
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
            || !TryParseRole(roleValue, out OperationsControlRole role))
        {
            actor = null;
            return false;
        }

        actor = new OutboxReplayActor(new EntityId(userId), role, tokenVersion);
        return true;
    }

    internal static bool TryGetEntityId(
        HttpContext context,
        Guid value,
        out EntityId entityId,
        out IResult? failure)
    {
        if (value == Guid.Empty)
        {
            entityId = default;
            failure = InvalidRequestProblem(
                context,
                FieldError(
                    "/messageId",
                    "The Outbox message path identifier must be a non-empty UUID."));
            return false;
        }

        entityId = new EntityId(value);
        failure = null;
        return true;
    }

    internal static bool TryGetIdempotencyKey(
        HttpContext context,
        out string? idempotencyKey,
        out IResult? failure)
    {
        var values = context.Request.Headers["Idempotency-Key"];
        if (values.Count == 0)
        {
            idempotencyKey = null;
            failure = Problem(
                context,
                StatusCodes.Status428PreconditionRequired,
                "idempotency_key_required",
                "Idempotency key required",
                "The Idempotency-Key header is required.",
                retryable: false);
            return false;
        }

        if (values.Count != 1
            || !OutboxReplayInput.IsValidIdempotencyKey(values[0]))
        {
            idempotencyKey = null;
            failure = InvalidRequestProblem(
                context,
                FieldError(
                    "/headers/Idempotency-Key",
                    "Idempotency-Key must contain 1 to 128 visible ASCII characters."));
            return false;
        }

        idempotencyKey = values[0];
        failure = null;
        return true;
    }

    internal static async ValueTask<ReplayRequestReadResult> ReadReplayRequestAsync(
        HttpContext context)
    {
        IResult? contentTypeFailure = RequireContentType(context, "application/json");
        if (contentTypeFailure is not null)
        {
            return new ReplayRequestReadResult(null, contentTypeFailure);
        }

        JsonBodyReadResult bodyRead = await ReadJsonBodyAsync(context).ConfigureAwait(false);
        if (bodyRead.Failure is not null)
        {
            return new ReplayRequestReadResult(null, bodyRead.Failure);
        }

        Dictionary<string, IReadOnlyList<string>> errors = new(StringComparer.Ordinal);
        JsonElement body = bodyRead.Body;
        if (body.ValueKind != JsonValueKind.Object)
        {
            errors["/"] = ["The request body must be a JSON object."];
            return new ReplayRequestReadResult(null, ValidationProblem(context, errors));
        }

        string? reason = null;
        bool reasonSeen = false;
        foreach (JsonProperty property in body.EnumerateObject())
        {
            if (!string.Equals(property.Name, "reason", StringComparison.Ordinal))
            {
                errors[JsonPointer(property.Name)] = ["The property is not allowed."];
                continue;
            }

            if (reasonSeen || property.Value.ValueKind != JsonValueKind.String)
            {
                errors["/reason"] = ["A single string audit reason is required."];
                continue;
            }

            reasonSeen = true;
            reason = property.Value.GetString();
        }

        string normalizedReason = string.Empty;
        if (!reasonSeen
            || !OutboxReplayInput.TryNormalizeReason(reason, out normalizedReason))
        {
            errors["/reason"] = ["A non-blank audit reason of at most 500 characters is required."];
        }

        return errors.Count == 0
            ? new ReplayRequestReadResult(normalizedReason, Failure: null)
            : new ReplayRequestReadResult(null, ValidationProblem(context, errors));
    }

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
            mapped = ApplyPresentation(error, presentation);
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

    private static HttpError MapError(ResultError error) => error.Code switch
    {
        OperationsErrorCodes.RoleRequired => new(
            error.Code,
            StatusCodes.Status403Forbidden,
            "Required role missing",
            "The required role is missing.",
            Retryable: false),
        OperationsErrorCodes.ResourceNotFound => new(
            error.Code,
            StatusCodes.Status404NotFound,
            "Resource not found",
            "The requested resource was not found.",
            Retryable: false),
        OperationsErrorCodes.ResourceConflict => new(
            error.Code,
            StatusCodes.Status409Conflict,
            "Resource conflict",
            "The requested state conflicts with the current resource state.",
            Retryable: false),
        OperationsErrorCodes.IdempotencyConflict => new(
            error.Code,
            StatusCodes.Status409Conflict,
            "Idempotency conflict",
            "The idempotency key was used for a different request.",
            Retryable: false),
        OperationsErrorCodes.ValidationFailed => new(
            error.Code,
            StatusCodes.Status422UnprocessableEntity,
            "Validation failed",
            "One or more request fields failed validation.",
            Retryable: false),
        OperationsErrorCodes.InvalidRequest => new(
            error.Code,
            StatusCodes.Status400BadRequest,
            "Invalid request",
            "The request is invalid.",
            Retryable: false),
        OperationsErrorCodes.ServiceUnavailable => new(
            error.Code,
            StatusCodes.Status503ServiceUnavailable,
            "Service unavailable",
            "The request cannot be completed safely at this time.",
            Retryable: true,
            error.RetryAfterSeconds),
        _ => new(
            "internal_error",
            StatusCodes.Status500InternalServerError,
            "Internal error",
            "The request could not be completed safely.",
            Retryable: false),
    };

    private static HttpError ApplyPresentation(
        ResultError error,
        ResultErrorPresentation presentation)
    {
        if (!string.Equals(presentation.Code, error.Code, StringComparison.Ordinal)
            || presentation.RetryAfterSeconds != error.RetryAfterSeconds)
        {
            throw new InvalidOperationException(
                "The frozen Outbox replay error presentation is invalid.");
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

    private static async ValueTask<JsonBodyReadResult> ReadJsonBodyAsync(HttpContext context)
    {
        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(
                context.Request.Body,
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return new JsonBodyReadResult(document.RootElement.Clone(), Failure: null);
        }
        catch (JsonException)
        {
            return new JsonBodyReadResult(
                default,
                InvalidRequestProblem(
                    context,
                    FieldError("/", "The request body must contain valid JSON.")));
        }
    }

    private static IResult? RequireContentType(HttpContext context, string expected)
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

    private static IResult ValidationProblem(
        HttpContext context,
        IReadOnlyDictionary<string, IReadOnlyList<string>> errors) => Problem(
        context,
        StatusCodes.Status422UnprocessableEntity,
        "validation_failed",
        "Validation failed",
        "One or more request fields failed validation.",
        retryable: false,
        errors: errors);

    private static IResult InvalidRequestProblem(
        HttpContext context,
        IReadOnlyDictionary<string, IReadOnlyList<string>> errors) => Problem(
        context,
        StatusCodes.Status400BadRequest,
        "invalid_request",
        "Invalid request",
        "One or more request parameters are invalid.",
        retryable: false,
        errors: errors);

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

    private static Dictionary<string, IReadOnlyList<string>> FieldError(
        string pointer,
        string message) => new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [pointer] = [message],
        };

    private static string JsonPointer(string propertyName) =>
        string.Concat(
            "/",
            propertyName
                .Replace("~", "~0", StringComparison.Ordinal)
                .Replace("/", "~1", StringComparison.Ordinal));

    private static bool TryParseRole(string? value, out OperationsControlRole role)
    {
        role = value switch
        {
            "admin" => OperationsControlRole.Admin,
            "operator" => OperationsControlRole.Operator,
            "auditor" => OperationsControlRole.Auditor,
            "user" => OperationsControlRole.User,
            _ => default,
        };
        return value is "admin" or "operator" or "auditor" or "user";
    }

    internal sealed record ReplayRequestReadResult(string? Reason, IResult? Failure);

    private sealed record JsonBodyReadResult(JsonElement Body, IResult? Failure);

    private sealed record HttpError(
        string Code,
        int Status,
        string Title,
        string Detail,
        bool Retryable,
        long? RetryAfterSeconds = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? Errors = null);
}
