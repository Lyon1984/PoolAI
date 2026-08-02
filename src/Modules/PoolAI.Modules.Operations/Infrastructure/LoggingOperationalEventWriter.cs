using Microsoft.Extensions.Logging;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.Operations.Infrastructure;

internal sealed partial class LoggingOperationalEventWriter(
    ILogger<LoggingOperationalEventWriter> logger) : IOperationalEventWriter
{
    private readonly ILogger<LoggingOperationalEventWriter> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public ValueTask WriteAsync(
        string eventName,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        cancellationToken.ThrowIfCancellationRequested();
        JsonElement normalizedPayload = OutboxTelemetryClassifier
            .NormalizeOperationalPayload(payload);
        WriteOperationalEvent(_logger, eventName, normalizedPayload.GetRawText());
        return ValueTask.CompletedTask;
    }

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Warning,
        Message = "Operational event {EventName}: {Payload}")]
    private static partial void WriteOperationalEvent(
        ILogger logger,
        string eventName,
        string payload);
}
