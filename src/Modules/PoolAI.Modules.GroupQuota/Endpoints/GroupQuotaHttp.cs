using System.Buffers;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using PoolAI.Contracts.Generated;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application;

namespace PoolAI.Modules.GroupQuota.Endpoints;

internal static class GroupQuotaHttp
{
    internal static EntityId RequestId(HttpContext context) =>
        Guid.TryParse(context.TraceIdentifier, out Guid requestId)
            ? new EntityId(requestId)
            : throw new InvalidOperationException("The API request identifier is invalid.");

    internal static GroupActor RequireActor(HttpContext context) =>
        TryGetActor(context, out GroupActor? actor)
            ? actor!
            : throw new InvalidOperationException(
                "The authenticated principal is missing required identity claims.");

    internal static bool TryGetActor(HttpContext context, out GroupActor? actor)
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
            || !TryParseRole(roleValue, out GroupControlRole role))
        {
            actor = null;
            return false;
        }

        actor = new GroupActor(new EntityId(userId), role, tokenVersion);
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
                FieldError("/groupId", "The Group path identifier must be a non-empty UUID."));
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
                FieldError("/headers/Idempotency-Key", "The idempotency key is invalid."));
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

        expectedVersion = 0;
        long parsedVersion = 0;
        bool valid = ifMatch.Length >= 4
            && ifMatch.StartsWith("\"v", StringComparison.Ordinal)
            && ifMatch[2] is >= '1' and <= '9'
            && ifMatch.EndsWith('"')
            && long.TryParse(
                ifMatch.AsSpan(2, ifMatch.Length - 3),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out parsedVersion)
            && parsedVersion > 0;
        if (!valid)
        {
            failure = InvalidRequestProblem(
                context,
                FieldError("/headers/If-Match", "If-Match must be a strong ETag such as \"v7\"."));
            return false;
        }

        expectedVersion = parsedVersion;
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

    internal static async ValueTask<JsonBodyReadResult> ReadJsonBodyAsync(
        HttpContext context)
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

    internal static async ValueTask<QuotaMutationRequestReadResult>
        ReadQuotaMutationRequestAsync(
            HttpContext context,
            string tokenPropertyName)
    {
        IResult? failure = RequireContentType(context, "application/json");
        if (failure is not null)
        {
            return new QuotaMutationRequestReadResult(null, failure);
        }

        JsonBodyReadResult body = await ReadJsonBodyAsync(context).ConfigureAwait(false);
        if (body.Failure is not null)
        {
            return new QuotaMutationRequestReadResult(null, body.Failure);
        }

        if (!TryParseQuotaMutationRequest(
                body.Body,
                tokenPropertyName,
                out ParsedQuotaMutationRequest? parsed,
                out IReadOnlyDictionary<string, IReadOnlyList<string>> errors))
        {
            return new QuotaMutationRequestReadResult(
                null,
                ValidationProblem(context, errors));
        }

        return new QuotaMutationRequestReadResult(parsed, Failure: null);
    }

    internal static bool TryParseGroupCreateRequest(
        JsonElement request,
        out ParsedGroupCreateRequest? parsed,
        out IReadOnlyDictionary<string, IReadOnlyList<string>> errors)
    {
        Dictionary<string, IReadOnlyList<string>> failures = new(StringComparer.Ordinal);
        parsed = null;
        if (request.ValueKind != JsonValueKind.Object)
        {
            failures["/"] = ["The request body must be a JSON object."];
            errors = failures;
            return false;
        }

        if (!HasDecodablePropertyNames(request))
        {
            failures["/"] =
                ["Request property names must contain valid Unicode scalar values."];
            errors = failures;
            return false;
        }

        AddGroupCreateAdditionalPropertyErrors(request, failures);
        ReadGroupCreateTextFields(
            request,
            failures,
            out string name,
            out string? description);

        long totalTokens = 0;
        if (!request.TryGetProperty("total_tokens", out JsonElement totalTokensElement)
            || !TryReadPositiveSafeInteger(totalTokensElement, out totalTokens))
        {
            failures["/total_tokens"] =
                ["total_tokens must be an integer between 1 and 9007199254740991."];
        }

        errors = failures;
        if (failures.Count != 0)
        {
            return false;
        }

        parsed = new ParsedGroupCreateRequest(name, description, totalTokens);
        return true;
    }

    internal static bool TryParseQuotaMutationRequest(
        JsonElement request,
        string tokenPropertyName,
        out ParsedQuotaMutationRequest? parsed,
        out IReadOnlyDictionary<string, IReadOnlyList<string>> errors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenPropertyName);
        Dictionary<string, IReadOnlyList<string>> failures = new(StringComparer.Ordinal);
        parsed = null;
        if (request.ValueKind != JsonValueKind.Object)
        {
            failures["/"] = ["The request body must be a JSON object."];
            errors = failures;
            return false;
        }

        if (!HasDecodablePropertyNames(request))
        {
            failures["/"] =
                ["Request property names must contain valid Unicode scalar values."];
            errors = failures;
            return false;
        }

        foreach (JsonProperty property in request.EnumerateObject())
        {
            if (!string.Equals(property.Name, tokenPropertyName, StringComparison.Ordinal)
                && !string.Equals(property.Name, "reason", StringComparison.Ordinal))
            {
                failures[JsonPointer(property.Name)] =
                    ["The request field is not defined by this operation."];
            }
        }

        long totalTokens = 0;
        if (!request.TryGetProperty(tokenPropertyName, out JsonElement totalTokensElement)
            || !TryReadPositiveSafeInteger(totalTokensElement, out totalTokens))
        {
            failures[JsonPointer(tokenPropertyName)] =
                [$"{tokenPropertyName} must be an integer between 1 and 9007199254740991."];
        }

        string reason = string.Empty;
        if (!request.TryGetProperty("reason", out JsonElement reasonElement)
            || !TryGetJsonString(reasonElement, out reason)
            || !SatisfiesQuotaReasonContract(reason))
        {
            failures["/reason"] =
                ["The reason must be non-blank and at most 500 characters."];
        }

        errors = failures;
        if (failures.Count != 0)
        {
            return false;
        }

        parsed = new ParsedQuotaMutationRequest(totalTokens, reason!);
        return true;
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> Validate(
        GroupUpdateRequest request)
    {
        Dictionary<string, IReadOnlyList<string>> errors = new(StringComparer.Ordinal);
        if (!request.Name.HasValue
            && !request.Description.HasValue
            && !request.Status.HasValue)
        {
            errors["/"] = ["At least one mutable Group field is required."];
        }

        if (request.Name.HasValue
            && (string.IsNullOrWhiteSpace(request.Name.Value)
                || request.Name.Value!.Trim().Length > 100
                || request.Name.Value.Any(char.IsControl)))
        {
            errors["/name"] = ["A Group name of at most 100 characters is required."];
        }

        if (request.Description.HasValue
            && request.Description.Value is { Length: > 1000 })
        {
            errors["/description"] = ["A Group description cannot exceed 1000 characters."];
        }

        if (request.Status.HasValue && !Enum.IsDefined(request.Status.Value))
        {
            errors["/status"] = ["The Group status is invalid."];
        }

        if (request.Status.HasValue
            && (!request.Reason.HasValue
                || string.IsNullOrWhiteSpace(request.Reason.Value)
                || request.Reason.Value!.Length > 500
                || request.Reason.Value.Any(static character => character is '\r' or '\n')))
        {
            errors["/reason"] = ["Status changes require a non-blank reason of at most 500 characters."];
        }

        if (request.Reason.HasValue
            && (string.IsNullOrWhiteSpace(request.Reason.Value)
                || request.Reason.Value!.Length > 500
                || request.Reason.Value.Any(static character => character is '\r' or '\n')))
        {
            errors["/reason"] = ["The reason must be non-blank and at most 500 characters."];
        }

        return errors;
    }

    internal static GroupLifecycle ToLifecycle(GroupStatus status) => status switch
    {
        GroupStatus.Active => GroupLifecycle.Active,
        GroupStatus.Disabled => GroupLifecycle.Disabled,
        GroupStatus.Archived => GroupLifecycle.Archived,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    internal static GroupStatus ToContractLifecycle(GroupLifecycle status) => status switch
    {
        GroupLifecycle.Active => GroupStatus.Active,
        GroupLifecycle.Disabled => GroupStatus.Disabled,
        GroupLifecycle.Archived => GroupStatus.Archived,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    internal static PoolAI.Contracts.Generated.Group ToContract(GroupView view) => new()
    {
        Id = view.Id.Value,
        Name = view.Name,
        Platform = "openai",
        Status = ToContractLifecycle(view.Status),
        Description = new Optional<string?>(view.Description),
        Version = view.Version,
        CreatedAt = view.CreatedAt,
        UpdatedAt = view.UpdatedAt,
    };

    internal static PoolAI.Contracts.Generated.Group ToContract(GroupResourceSnapshot view) => new()
    {
        Id = view.GroupId.Value,
        Name = view.Name,
        Platform = "openai",
        Status = ToContractLifecycle(view.Lifecycle),
        Description = new Optional<string?>(view.Description),
        Version = view.Version,
        CreatedAt = view.CreatedAt,
        UpdatedAt = view.UpdatedAt,
    };

    internal static PoolAI.Contracts.Generated.GroupQuota ToContract(GroupQuotaView view) => new()
    {
        GroupId = view.GroupId.Value,
        PeriodId = view.PeriodId.Value,
        Status = view.Status switch
        {
            GroupPoolQuotaStatus.Active => "active",
            GroupPoolQuotaStatus.Exhausted => "exhausted",
            GroupPoolQuotaStatus.Disabled => "disabled",
            _ => throw new ArgumentOutOfRangeException(nameof(view)),
        },
        TotalTokens = view.TotalTokens.ToString(CultureInfo.InvariantCulture),
        ConsumedTokens = view.ConsumedTokens.ToString(CultureInfo.InvariantCulture),
        ReservedTokens = view.ReservedTokens.ToString(CultureInfo.InvariantCulture),
        RemainingTokens = view.RemainingTokens.ToString(CultureInfo.InvariantCulture),
        OverageTokens = view.OverageTokens.ToString(CultureInfo.InvariantCulture),
        PeriodStartedAt = view.PeriodStartedAt,
        PeriodEndedAt = new Optional<DateTimeOffset?>(view.PeriodEndedAt),
        Version = view.Version,
        UpdatedAt = view.UpdatedAt,
    };

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
                throw new InvalidOperationException("The frozen Group error presentation is invalid.");
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

    private static bool TryReadPositiveSafeInteger(
        JsonElement element,
        out long value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        ReadOnlySpan<char> raw = element.GetRawText().AsSpan();
        if (raw.IsEmpty
            || raw.ContainsAnyExceptInRange('0', '9')
            || !element.TryGetInt64(out value))
        {
            value = 0;
            return false;
        }

        return value is >= 1 and <= 9_007_199_254_740_991;
    }

    internal static bool SatisfiesQuotaReasonContract(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        int scalarCount = 0;
        ReadOnlySpan<char> remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(
                    remaining,
                    out _,
                    out int consumed) != OperationStatus.Done)
            {
                return false;
            }

            scalarCount++;
            if (scalarCount > 500)
            {
                return false;
            }

            remaining = remaining[consumed..];
        }

        return true;
    }

    private static bool TryGetJsonString(
        JsonElement element,
        out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        try
        {
            // JsonElement.GetString() is non-null for String; malformed
            // escaped surrogates are reported by the exception path below.
            value = element.GetString()!;
            return true;
        }
        catch (InvalidOperationException)
        {
            // System.Text.Json defers decoding escaped isolated UTF-16 surrogates
            // until GetString(). Treat them as field validation failures.
            return false;
        }
    }

    private static bool HasDecodablePropertyNames(JsonElement element)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            try
            {
                _ = property.Name;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        return true;
    }

    private static void ReadGroupCreateTextFields(
        JsonElement request,
        Dictionary<string, IReadOnlyList<string>> failures,
        out string name,
        out string? description)
    {
        name = string.Empty;
        if (!request.TryGetProperty("name", out JsonElement nameElement)
            || !TryGetJsonString(nameElement, out name)
            || string.IsNullOrWhiteSpace(name)
            || name.Trim().Length > 100
            || name.Any(char.IsControl))
        {
            failures["/name"] = ["A Group name of at most 100 characters is required."];
        }

        description = null;
        if (!request.TryGetProperty("description", out JsonElement descriptionElement))
        {
            return;
        }

        if (descriptionElement.ValueKind == JsonValueKind.String)
        {
            if (TryGetJsonString(descriptionElement, out string decodedDescription))
            {
                description = decodedDescription;
            }
            else
            {
                failures["/description"] =
                    ["A Group description must be a string or null."];
            }
        }
        else if (descriptionElement.ValueKind != JsonValueKind.Null)
        {
            failures["/description"] = ["A Group description must be a string or null."];
        }

        if (description is { Length: > 1000 })
        {
            failures["/description"] = ["A Group description cannot exceed 1000 characters."];
        }
    }

    private static void AddGroupCreateAdditionalPropertyErrors(
        JsonElement request,
        Dictionary<string, IReadOnlyList<string>> failures)
    {
        foreach (JsonProperty property in request.EnumerateObject())
        {
            if (!string.Equals(property.Name, "name", StringComparison.Ordinal)
                && !string.Equals(property.Name, "description", StringComparison.Ordinal)
                && !string.Equals(property.Name, "total_tokens", StringComparison.Ordinal))
            {
                failures[JsonPointer(property.Name)] =
                    ["The request field is not defined by this operation."];
            }
        }
    }

    private static string JsonPointer(string propertyName) =>
        string.Concat(
            "/",
            propertyName
                .Replace("~", "~0", StringComparison.Ordinal)
                .Replace("/", "~1", StringComparison.Ordinal));

    private static HttpError MapError(ResultError error) => error.Code switch
    {
        "role_required" or "forbidden" => new(
            "role_required", 403, "Required role missing", "The required role is missing.", false),
        "resource_not_found" => new(
            error.Code, 404, "Resource not found", "The requested resource was not found.", false),
        "idempotency_conflict" => new(
            error.Code, 409, "Idempotency conflict", "The idempotency key was used for a different request.", false),
        "resource_conflict" => new(
            error.Code, 409, "Resource conflict", "The requested state conflicts with the current resource state.", false),
        "group_activation_not_ready" => new(
            error.Code, 409, "Group activation not ready", "The Group does not satisfy its activation preconditions.", false),
        "version_conflict" => new(
            error.Code, 412, "Version conflict", "The resource version no longer matches.", true),
        "validation_failed" => new(
            error.Code, 422, "Validation failed", "One or more request fields failed validation.", false),
        "invalid_request" => new(
            error.Code, 400, "Invalid request", "The request is invalid.", false),
        "coordination_unavailable" => new(
            error.Code, 503, "Coordination unavailable", "Required coordination is temporarily unavailable.", true, 1),
        "dependency_unavailable" or "service_unavailable" => new(
            "dependency_unavailable", 503, "Dependency unavailable", "A required dependency is temporarily unavailable.", true, 1),
        _ => new(
            error.Code, 500, "Internal error", "The request could not be completed safely.", false),
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

    private static bool TryParseRole(string? value, out GroupControlRole role)
    {
        role = value switch
        {
            "admin" => GroupControlRole.Admin,
            "operator" => GroupControlRole.Operator,
            "auditor" => GroupControlRole.Auditor,
            "user" => GroupControlRole.User,
            _ => default,
        };
        return value is "admin" or "operator" or "auditor" or "user";
    }

    private sealed record HttpError(
        string Code,
        int Status,
        string Title,
        string Detail,
        bool Retryable,
        long? RetryAfterSeconds = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? Errors = null);

    internal sealed record ParsedGroupCreateRequest(
        string Name,
        string? Description,
        long TotalTokens);

    internal sealed record ParsedQuotaMutationRequest(
        long TotalTokens,
        string Reason);

    internal readonly record struct JsonBodyReadResult(
        JsonElement Body,
        IResult? Failure);

    internal readonly record struct QuotaMutationRequestReadResult(
        ParsedQuotaMutationRequest? Request,
        IResult? Failure);
}
