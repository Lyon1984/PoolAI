using System.Text.Json;
using Microsoft.Net.Http.Headers;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Timeouts;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Abstractions;
using PoolAI.Modules.Gateway.Application;

namespace PoolAI.Api;

internal static class ResponsesEndpoint
{
    private static readonly Action<ILogger, Exception?> AdmissionInvariantFailed = LoggerMessage.Define(
        LogLevel.Critical, new EventId(1701, "ModelAdmissionInvariant"),
        "Gateway model admission consistency invariant failed; execution was fenced.");
    internal static async Task HandleAsync(
        HttpContext context,
        GatewayModelDiscriminator discriminator,
        GatewayAdmissionController admission,
        GatewayResponsesRequestParser parser,
        GatewayRequestProcess process,
        IConfiguration configuration,
        TimeProvider timeProvider,
        ILogger<GatewayRequestProcess> logger)
    {
        CancellationToken clientAbort = context.RequestAborted;
        CancellationToken serverDeadline = context.Features.Get<IHttpRequestTimeoutFeature>()?.RequestTimeoutToken ?? default;
        Result<GatewayModelDiscriminatorLease> acquired = discriminator.TryAcquire(clientAbort, serverDeadline);
        if (acquired.IsFailure)
        {
            await GatewayProblemWriter.WriteFailureAsync(context, acquired.Error).ConfigureAwait(false);
            return;
        }

        using GatewayModelDiscriminatorLease guard = acquired.Value;
        Result<PreparedAdmission> prepared = await PrepareWithinDeadlineAsync(context, guard,
            admission, parser, process, configuration, logger).ConfigureAwait(false);
        if (prepared.IsFailure)
        {
            guard.Dispose();
            if (!clientAbort.IsCancellationRequested && !context.Response.HasStarted)
            {
                await GatewayProblemWriter.WriteFailureAsync(context, prepared.Error).ConfigureAwait(false);
            }

            return;
        }

        using GatewayAdmissionLease selected = prepared.Value.Lease;
        await ExecuteAsync(context, process, prepared.Value.Request, timeProvider).ConfigureAwait(false);
    }

    private static async ValueTask<Result<PreparedAdmission>> PrepareWithinDeadlineAsync(
        HttpContext context, GatewayModelDiscriminatorLease guard, GatewayAdmissionController admission,
        GatewayResponsesRequestParser parser, GatewayRequestProcess process, IConfiguration configuration,
        ILogger<GatewayRequestProcess> logger)
    {
        try
        {
            // The task only returns data. It cannot write HTTP, publish a result,
            // or enter the Process Manager after this cancellation-aware fence.
            Result<NormalizedGatewayRequest> prepared = await PrepareAsync(context, guard, admission, parser, process,
                    configuration.GetValue("Gateway:MaxRequestBodyBytes", 16_777_216L))
                .AsTask().WaitAsync(guard.CancellationToken).ConfigureAwait(false);
            guard.CancellationToken.ThrowIfCancellationRequested();
            return prepared.IsFailure ? CopyFailure<PreparedAdmission>(prepared.Error)
                : Result.Success(new PreparedAdmission(prepared.Value, guard.CompletePreparation(prepared.Value.Stream)));
        }
        catch (OperationCanceledException)
        {
            if (!context.RequestAborted.IsCancellationRequested)
            {
                guard.RecordRejection("deadline");
            }

            return Result.Failure<PreparedAdmission>("gateway_overloaded",
                "The model admission preparation deadline expired.", 1);
        }
        catch (GatewayReplayStorageException)
        {
            return Result.Failure<PreparedAdmission>("gateway_overloaded",
                "Private model admission replay storage is unavailable.", 1);
        }
        catch (InvalidOperationException)
        {
            AdmissionInvariantFailed(logger, null);
            if (context.Response.HasStarted) { context.Abort(); }
            return Result.Failure<PreparedAdmission>("internal_error", "The model admission invariant failed.");
        }
    }

    private static async ValueTask<Result<NormalizedGatewayRequest>> PrepareAsync(
        HttpContext context, GatewayModelDiscriminatorLease guard,
        GatewayAdmissionController admission, GatewayResponsesRequestParser parser,
        GatewayRequestProcess process, long maximumBytes)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } maximum)
        {
            maximum.MaxRequestBodySize = maximumBytes + 1;
        }

        bool identityEncoding = context.Request.Headers.ContentEncoding.Count == 0
            || context.Request.Headers.ContentEncoding.Count == 1
                && string.Equals(context.Request.Headers.ContentEncoding[0], "identity", StringComparison.OrdinalIgnoreCase);
        await guard.SpoolAsync(context.Request.Body, context.Request.ContentLength, maximumBytes, identityEncoding).ConfigureAwait(false);
        Result<GatewayAdmissionLease> selected = await admission.AcquireAsync(guard.SelectedKind,
            context.RequestAborted, guard.CancellationToken).ConfigureAwait(false);
        if (selected.IsFailure)
        {
            return CopyFailure<NormalizedGatewayRequest>(selected.Error);
        }

        if (!guard.BindSelected(selected.Value))
        {
            throw new OperationCanceledException(guard.CancellationToken);
        }

        string? key = ApiKey(context);
        if (key is null)
        {
            return Result.Failure<NormalizedGatewayRequest>("authentication_required", "A Bearer API Key is required.");
        }

        Result<bool> authenticated = await process.AuthenticateIngressAsync(key,
            context.Connection.RemoteIpAddress, context.Request.Headers["X-Forwarded-For"].ToArray()!,
            guard.CancellationToken).ConfigureAwait(false);
        if (authenticated.IsFailure)
        {
            return CopyFailure<NormalizedGatewayRequest>(authenticated.Error);
        }

        if (!IsUtf8Json(context.Request.ContentType) || !identityEncoding)
        {
            return Result.Failure<NormalizedGatewayRequest>("unsupported_media_type", "UTF-8 JSON without content encoding is required.");
        }

        if (guard.Oversized)
        {
            return Result.Failure<NormalizedGatewayRequest>("payload_too_large", "The request body exceeds the Gateway limit.");
        }

        return await parser.ParseAsync(guard.Replay,
            new EntityId(RequestIdMiddleware.GetRequestId(context)), guard.CancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(HttpContext context, GatewayRequestProcess process,
        NormalizedGatewayRequest request, TimeProvider timeProvider)
    {
        Result<GatewayAuthorizedRequest> authorized = await process.AuthorizeAsync(ApiKey(context)!,
            context.Connection.RemoteIpAddress, context.Request.Headers["X-Forwarded-For"].ToArray()!,
            context.RequestAborted).ConfigureAwait(false);
        if (authorized.IsFailure)
        {
            await GatewayProblemWriter.WriteFailureAsync(context, authorized.Error).ConfigureAwait(false);
            return;
        }

        ResponsesHttpOutput output = new(context);
        request = request with { Output = request.Stream ? output : null };
        Result<GatewaySingleAttemptOutcome> result = await process.ExecuteInitialAttemptAsync(
            authorized.Value, InboundProtocol.Responses, request, clientRequestId: null,
            timeProvider.GetUtcNow().Add(request.Stream ? TimeSpan.FromHours(2) : TimeSpan.FromSeconds(600)),
            sessionAffinityHash: null, context.RequestAborted).ConfigureAwait(false);
        if (context.RequestAborted.IsCancellationRequested) { return; }

        string? error = result.IsFailure ? result.Error.Code
            : result.Value.ErrorCode ?? result.Value.UpstreamResult?.ErrorCode;
        NormalizedUpstreamResult? upstream = result.IsSuccess ? result.Value.UpstreamResult : null;
        if (error is not null || upstream is null)
        {
            await WriteTerminalFailureAsync(context, output,
                result.IsFailure ? result.Error : new ResultError(error ?? "upstream_dispatch_ambiguous",
                    "The upstream request could not be completed safely.")).ConfigureAwait(false);
        }
        else if (request.Stream && upstream.TerminalEvent is JsonElement terminal)
        {
            await output.WriteEventAsync("response.completed", terminal, context.RequestAborted).ConfigureAwait(false);
        }
        else if (request.Stream)
        {
            await WriteTerminalFailureAsync(context, output,
                new ResultError("upstream_stream_error", "The upstream stream did not complete.")).ConfigureAwait(false);
        }
        else
        {
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.Body, upstream.Payload,
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static Task WriteTerminalFailureAsync(HttpContext context, ResponsesHttpOutput output, ResultError failure) =>
        context.Response.HasStarted
            ? output.WriteErrorAsync(failure.Code, context.RequestAborted).AsTask()
            : GatewayProblemWriter.WriteFailureAsync(context, failure);

    private static string? ApiKey(HttpContext context) => context.Request.Headers.Authorization.Count == 1
        && context.Request.Headers.Authorization[0] is { } value
        && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        && value.Length > 7 ? value[7..] : null;

    private static bool IsUtf8Json(string? value) => MediaTypeHeaderValue.TryParse(value, out var media)
        && media.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
        && (!media.Charset.HasValue || media.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase));

    private static Result<T> CopyFailure<T>(ResultError error) => Result.Failure<T>(
        error.Code, error.Description, error.RetryAfterSeconds, error.ETag, error.Presentation);

    private sealed record PreparedAdmission(NormalizedGatewayRequest Request, GatewayAdmissionLease Lease);
}
