using System.Globalization;
using System.Text.Json;
using PoolAI.Contracts.Generated;

namespace PoolAI.Api;

internal static class GatewayProblemWriter
{
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
