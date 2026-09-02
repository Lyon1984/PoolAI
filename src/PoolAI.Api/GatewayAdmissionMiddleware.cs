using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Routing;
using PoolAI.BuildingBlocks;
using PoolAI.Contracts.Generated;
using PoolAI.Modules.Gateway.Application;

namespace PoolAI.Api;

internal sealed class GatewayAdmissionMiddleware(
    RequestDelegate next,
    GatewayAdmissionController admission,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan QueueWaitBudget = TimeSpan.FromSeconds(1);
    private readonly RequestDelegate _next =
        next ?? throw new ArgumentNullException(nameof(next));
    private readonly GatewayAdmissionController _admission =
        admission ?? throw new ArgumentNullException(nameof(admission));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!TryClassify(context, out GatewayAdmissionKind kind))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        Result<GatewayAdmissionLease> acquired;
        try
        {
            acquired = await AcquireWithinServerBudgetAsync(context, kind)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        if (acquired.IsFailure)
        {
            // A disconnect observed after the limiter completed still takes
            // precedence over writing an overload body into an abandoned request.
            if (context.RequestAborted.IsCancellationRequested)
            {
                return;
            }

            await WriteOverloadedIfConnectedAsync(context, kind).ConfigureAwait(false);
            return;
        }

        using GatewayAdmissionLease lease = acquired.Value;
        if (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (context.RequestAborted.IsCancellationRequested)
        {
            // Client cancellation is not an overload or an internal failure.
        }
    }

    private async ValueTask<Result<GatewayAdmissionLease>>
        AcquireWithinServerBudgetAsync(
            HttpContext context,
            GatewayAdmissionKind kind)
    {
        using CancellationTokenSource waitBudget = new(
            QueueWaitBudget,
            _timeProvider);
        CancellationToken requestDeadline = context.Features
            .Get<IHttpRequestTimeoutFeature>()?
            .RequestTimeoutToken ?? CancellationToken.None;
        using CancellationTokenSource serverWait = CancellationTokenSource
            .CreateLinkedTokenSource(waitBudget.Token, requestDeadline);
        return await _admission.AcquireAsync(
                kind,
                context.RequestAborted,
                serverWait.Token)
            .ConfigureAwait(false);
    }

    private static async Task WriteOverloadedIfConnectedAsync(
        HttpContext context,
        GatewayAdmissionKind kind)
    {
        try
        {
            await WriteOverloadedAsync(context, kind).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (context.RequestAborted.IsCancellationRequested)
        {
            // The response became abandoned while it was being serialized.
        }
    }

    private static bool TryClassify(
        HttpContext context,
        out GatewayAdmissionKind kind)
    {
        if (context.GetEndpoint() is not RouteEndpoint endpoint)
        {
            kind = default;
            return false;
        }

        PathString path = context.Request.Path;
        if (path.StartsWithSegments("/api/v1", StringComparison.Ordinal))
        {
            kind = GatewayAdmissionKind.Control;
            return true;
        }

        if (HttpMethods.IsGet(context.Request.Method)
            && string.Equals(path.Value, "/v1/models", StringComparison.Ordinal))
        {
            kind = GatewayAdmissionKind.NonStream;
            return true;
        }

        if (HttpMethods.IsGet(context.Request.Method)
            && string.Equals(path.Value, "/v1/usage", StringComparison.Ordinal))
        {
            kind = GatewayAdmissionKind.Usage;
            return true;
        }

        GatewayAdmissionEndpointMetadata? metadata = endpoint.Metadata
            .GetMetadata<GatewayAdmissionEndpointMetadata>();
        if (metadata is not null
            && path.StartsWithSegments("/v1", StringComparison.Ordinal))
        {
            kind = metadata.Kind;
            return true;
        }

        kind = default;
        return false;
    }

    private static Task WriteOverloadedAsync(
        HttpContext context,
        GatewayAdmissionKind kind) => kind switch
        {
            GatewayAdmissionKind.Control => ControlPlaneProblemWriter.WriteAsync(
                context,
                StatusCodes.Status429TooManyRequests,
                ErrorCodesV1.GatewayOverloaded,
                "Gateway overloaded",
                "The control-plane admission partition is saturated.",
                retryable: true,
                retryAfterSeconds: 1),
            GatewayAdmissionKind.NonStream or
                GatewayAdmissionKind.Sse or
                GatewayAdmissionKind.Usage => GatewayProblemWriter.WriteOverloadedAsync(
                    context,
                    kind == GatewayAdmissionKind.Usage
                        ? "The query admission partition is saturated."
                        : "The data-plane admission partition is saturated."),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
}
