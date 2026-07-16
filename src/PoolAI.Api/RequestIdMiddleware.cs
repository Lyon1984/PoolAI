namespace PoolAI.Api;

internal sealed class RequestIdMiddleware(RequestDelegate next)
{
    private static readonly object RequestIdKey = new();

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Guid requestId = Guid.CreateVersion7();
        context.Items[RequestIdKey] = requestId;
        context.TraceIdentifier = requestId.ToString();
        context.Response.OnStarting(static state =>
        {
            (HttpContext httpContext, Guid id) = ((HttpContext, Guid))state;
            httpContext.Response.Headers["X-Request-Id"] = id.ToString();
            return Task.CompletedTask;
        }, (context, requestId));
        await next(context).ConfigureAwait(false);
    }

    public static Guid GetRequestId(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(RequestIdKey, out object? value) && value is Guid requestId
            ? requestId
            : throw new InvalidOperationException("The request ID middleware has not initialized this request.");
    }
}
