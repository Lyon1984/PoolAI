using PoolAI.Modules.GroupQuota;
using PoolAI.Modules.Identity;
using PoolAI.Modules.Operations;
using PoolAI.Modules.Operations.Infrastructure.Configuration;
using PoolAI.Modules.Routing;
using PoolAI.Modules.Supply;
using PoolAI.Modules.Usage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Worker;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
PoolAiRuntimeConfigurationValidator.Validate(
    builder.Configuration,
    builder.Environment.EnvironmentName,
    PoolAiRuntimeConfigurationValidator.HostProfile.Worker);

IServiceCollection services = builder.Services
    .AddPoolAiPostgresRuntime(
        builder.Configuration["Data:Postgres:ConnectionString"]!,
        builder.Configuration.GetValue("Data:Postgres:CommandTimeoutSeconds", 30),
        builder.Configuration.GetValue("Data:Postgres:MaxPoolSize", 100))
    .AddIdentityModule()
    .AddGroupQuotaModule()
    .AddSupplyModule(
        builder.Configuration,
        builder.Environment.EnvironmentName)
    .AddUsageModule()
    .AddOperationsModule(builder.Configuration, builder.Environment.EnvironmentName);
if (builder.Configuration.GetValue("WorkerJobs:UsageRebuild:Enabled", false))
{
    services.AddUsageProjectionRebuildWorker(builder.Configuration);
}
else
{
    services
        .AddRoutingHealthModule(builder.Configuration)
        .AddIdentityEmailOutboxWorker(builder.Configuration)
        .AddGroupQuotaReservationSweeper(builder.Configuration)
        .AddSupplyCredentialRewrapWorker(builder.Configuration)
        .AddOperationsOutboxPublisher(builder.Configuration)
        .AddUsageQuotaReconciliationWorker();
}

builder.Services.AddPoolAiObservability(builder.Configuration);

using IHost host = builder.Build();
IRuntimeDependencyReadiness runtimeReadiness = host.Services
    .GetRequiredService<IRuntimeDependencyReadiness>();
RuntimeDependencyReadiness readinessResult = await runtimeReadiness
    .CheckAsync(CancellationToken.None)
    .ConfigureAwait(false);
if (!readinessResult.IsReady)
{
    throw new InvalidOperationException(
        $"Worker runtime readiness gate failed: {readinessResult.FailureCode ?? "dependency_unavailable"}.");
}

await host.RunAsync().ConfigureAwait(false);
