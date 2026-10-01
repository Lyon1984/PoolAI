using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Gateway.Abstractions;
using System.Text.Json;

namespace PoolAI.EndToEndTests;

internal sealed class ResponsesAdmissionApiFactory : PoolAiApiFactory
{
    internal AdmissionKeyAuthenticator Keys { get; } = new();
    internal StoppedUserReader Users { get; } = new();
    internal ProbeProtocolAdapter? ProtocolOverride { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Admission:DataNonStreamPermits"] = "1", ["Admission:DataStreamPermits"] = "1",
            ["Gateway:MaxRequestBodyBytes"] = "1048576",
        };
        foreach ((string key, string? value) in values) { builder.UseSetting(key, value); }
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(values));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IApiKeyAuthenticator>();
            services.AddSingleton<IApiKeyAuthenticator>(Keys);
            services.RemoveAll<IUserStatusReader>();
            services.AddSingleton<IUserStatusReader>(Users);
            services.AddSingleton<IStartupFilter, SocketPeerFilter>();
            if (ProtocolOverride is not null)
            {
                services.RemoveAll<IProtocolAdapter>();
                services.AddSingleton<IProtocolAdapter>(ProtocolOverride);
            }
        });
    }

    private sealed class SocketPeerFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, continuation) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                return continuation(context);
            });
            next(app);
        };
    }

    internal sealed class AdmissionKeyAuthenticator : IApiKeyAuthenticator
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Valid { get; set; }
        internal bool Block { get; set; }
        internal int Calls { get; private set; }
        internal Task Entered => _entered.Task;
        internal void Release() => _release.TrySetResult();

        public async ValueTask<Result<ApiKeyAccessSnapshot>> AuthenticateAsync(string presentedKey, CancellationToken cancellationToken)
        {
            Calls++;
            _entered.TrySetResult();
            // Intentionally ignores cancellation: prove that a late external
            // dependency cannot restore the guard or open business execution.
            if (Block) { await _release.Task.ConfigureAwait(false); }
            return Valid ? Result.Success(new ApiKeyAccessSnapshot(EntityId.New(), EntityId.New(), EntityId.New(),
                true, [], 1, TimeProvider.System.GetUtcNow()))
                : Result.Failure<ApiKeyAccessSnapshot>("invalid_api_key", "The API Key is invalid.");
        }
    }

    internal sealed class StoppedUserReader : IUserStatusReader
    {
        internal int Calls { get; private set; }
        public ValueTask<Result<UserStatusSnapshot>> GetCurrentAsync(EntityId userId, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(Result.Failure<UserStatusSnapshot>("user_disabled", "The fixture stops before further business dependencies."));
        }
    }

    internal sealed class ProbeProtocolAdapter : IProtocolAdapter
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Block { get; set; }
        internal bool EffectiveStream { get; set; }
        internal Task Entered => _entered.Task;
        internal void Release() => _release.TrySetResult();
        public AdapterCapability Capability { get; } = new(InboundProtocol.Responses, UpstreamType.OpenAi,
            AdapterOperation.NonStream, true, false);
        public async ValueTask<Result<NormalizedGatewayRequest>> NormalizeAsync(JsonElement request, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            if (Block) { await _release.Task.ConfigureAwait(false); }
            return Result.Success(new NormalizedGatewayRequest(EntityId.New(), "test", EffectiveStream, request.Clone()));
        }
    }
}
