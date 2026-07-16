using System.Globalization;
using System.Text.Json;
using PoolAI.Contracts.Generated;

namespace PoolAI.Api;

internal static class ControlPlaneProblemWriter
{
    public static Task WriteAsync(
        HttpContext context,
        int status,
        string code,
        string title,
        string detail,
        bool retryable,
        long? retryAfterSeconds = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? errors = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        Guid requestId = RequestIdMiddleware.GetRequestId(context);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
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
            RequestId = requestId,
            Retryable = retryable,
            RetryAfterSeconds = retryAfterSeconds is null
                ? default
                : new Optional<long>(retryAfterSeconds.Value),
            Errors = errors is null
                ? default
                : new Optional<IReadOnlyDictionary<string, IReadOnlyList<string>>>(errors),
        };
        return JsonSerializer.SerializeAsync(
            context.Response.Body,
            problem,
            cancellationToken: context.RequestAborted);
    }
}
