using System.Data.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PoolAI.Api;

internal sealed partial class ControlPlaneExceptionHandler(
    ILogger<ControlPlaneExceptionHandler> logger) : IExceptionHandler
{
    private readonly ILogger<ControlPlaneExceptionHandler> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);
        if (!httpContext.Request.Path.StartsWithSegments(
                "/api/v1",
                StringComparison.Ordinal))
        {
            return false;
        }

        if (exception is BadHttpRequestException badRequest)
        {
            (int status, string code, string title, string detail) = badRequest.StatusCode switch
            {
                StatusCodes.Status413PayloadTooLarge =>
                    (StatusCodes.Status413PayloadTooLarge, "payload_too_large", "Payload too large", "The request payload exceeds the allowed size."),
                StatusCodes.Status415UnsupportedMediaType =>
                    (StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type", "Unsupported media type", "The request media type is not supported."),
                _ =>
                    (StatusCodes.Status400BadRequest, "invalid_request", "Invalid request", "The request body or parameters are invalid."),
            };
            await ControlPlaneProblemWriter.WriteAsync(
                httpContext,
                status,
                code,
                title,
                detail,
                retryable: false).ConfigureAwait(false);
            return true;
        }

        if (exception is DbException { IsTransient: true } or TimeoutException)
        {
            await ControlPlaneProblemWriter.WriteAsync(
                httpContext,
                StatusCodes.Status503ServiceUnavailable,
                "dependency_unavailable",
                "Dependency unavailable",
                "A required dependency is temporarily unavailable.",
                retryable: true,
                retryAfterSeconds: 1).ConfigureAwait(false);
            return true;
        }

        LogUnhandledException(
            _logger,
            httpContext.TraceIdentifier,
            exception.GetType().FullName ?? exception.GetType().Name);
        await ControlPlaneProblemWriter.WriteAsync(
            httpContext,
            StatusCodes.Status500InternalServerError,
            "internal_error",
            "Internal error",
            "The request could not be completed safely.",
            retryable: false).ConfigureAwait(false);
        return true;
    }

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Error,
        Message = "Unhandled control-plane exception for request {RequestId}; exception type {ExceptionType}.")]
    private static partial void LogUnhandledException(
        ILogger logger,
        string requestId,
        string exceptionType);
}
