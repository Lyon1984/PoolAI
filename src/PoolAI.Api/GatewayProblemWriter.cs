using System.Globalization;
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Contracts.Generated;

namespace PoolAI.Api;

internal static class GatewayProblemWriter
{
    public static Task WriteFailureAsync(HttpContext context, ResultError failure)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(failure);
        int status = failure.Presentation?.Status ?? failure.Code switch
        {
            "invalid_request" or "unsupported_feature" => 400,
            "authentication_required" or "invalid_api_key" => 401,
            "user_disabled" or "subscription_required" or "subscription_inactive" or "group_disabled" or "model_not_allowed" => 403,
            "model_not_found" => 404,
            "group_quota_exhausted" or "group_quota_insufficient" => 429,
            "group_activation_not_ready" => 409,
            "payload_too_large" => 413,
            "unsupported_media_type" => 415,
            "validation_failed" => 422,
            "gateway_overloaded" or "group_rate_limited" or "group_quota_reserved" or "rate_limit_exceeded" => 429,
            "no_available_account" or "account_capacity_unavailable" or "coordination_unavailable" or "dependency_unavailable" or "reservation_lease_lost" => 503,
            "upstream_connect_timeout" or "upstream_first_byte_timeout" => 504,
            "upstream_auth_failed" or "upstream_rejected" or "upstream_protocol_error" or "upstream_usage_out_of_range"
                or "upstream_dispatch_ambiguous" or "upstream_stream_error" or "upstream_unavailable" => 502,
            _ => 500,
        };
        string code = status == 500 && failure.Code is not "token_numeric_overflow" ? "internal_error" : failure.Code;
        string detail = status == 500 ? "The Gateway could not complete the request safely." : failure.Description;
        bool retryable = failure.Presentation?.Retryable ?? (status is 429 or 503
            && code is not ("group_quota_exhausted" or "group_quota_insufficient")
            || code is "upstream_unavailable" or "upstream_connect_timeout");
        long? retry = retryable ? failure.RetryAfterSeconds ?? (status is 429 or 503 ? 1L : null) : null;
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        if (retry is not null)
        {
            context.Response.Headers.RetryAfter = retry.Value.ToString(CultureInfo.InvariantCulture);
        }

        GatewayProblem problem = new()
        {
            Type = new Uri($"https://poolai.example/problems/{code.Replace('_', '-')}", UriKind.Absolute),
            Title = failure.Presentation?.Title ?? "Gateway request failed", Status = status, Detail = detail,
            Instance = context.Request.Path.Value ?? "/", Code = code,
            RequestId = RequestIdMiddleware.GetRequestId(context), Retryable = retryable,
            RetryAfterSeconds = retry is null ? default : new Optional<long>(retry.Value),
            Errors = failure.Presentation?.Errors is { } errors
                ? new Optional<IReadOnlyDictionary<string, IReadOnlyList<string>>>(errors) : default,
            Error = new OpenAIErrorProjection
            {
                Message = detail, Type = status == 429 ? "rate_limit_error" : status < 500 ? "invalid_request_error" : "server_error",
                Param = null, Code = code,
            },
        };
        return JsonSerializer.SerializeAsync(context.Response.Body, problem, cancellationToken: context.RequestAborted);
    }

    public static Task WriteOverloadedAsync(
        HttpContext context,
        string detail)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        const int retryAfterSeconds = 1;
        const string code = ErrorCodesV1.GatewayOverloaded;
        Guid requestId = RequestIdMiddleware.GetRequestId(context);

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.ContentType = "application/json";
        context.Response.Headers.RetryAfter = retryAfterSeconds.ToString(
            CultureInfo.InvariantCulture);
        GatewayProblem problem = new()
        {
            Type = new Uri(
                "https://poolai.example/problems/gateway-overloaded",
                UriKind.Absolute),
            Title = "Gateway overloaded",
            Status = StatusCodes.Status429TooManyRequests,
            Detail = detail,
            Instance = context.Request.Path.Value ?? "/",
            Code = code,
            RequestId = requestId,
            Retryable = true,
            RetryAfterSeconds = new Optional<long>(retryAfterSeconds),
            Error = new OpenAIErrorProjection
            {
                Message = detail,
                Type = "rate_limit_error",
                Param = null,
                Code = code,
            },
        };
        return JsonSerializer.SerializeAsync(
            context.Response.Body,
            problem,
            cancellationToken: context.RequestAborted);
    }
}
