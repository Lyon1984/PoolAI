using PoolAI.Database.Migrations;
using Npgsql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PoolAI.Migrator;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
string connectionString = ValidateConnectionString(
    builder.Configuration,
    builder.Environment.IsProduction());

MigrationCatalog catalog = await MigrationCatalog
    .LoadAsync(CancellationToken.None)
    .ConfigureAwait(false);
builder.Services.AddSingleton(catalog);
builder.Services.AddSingleton<PostgresMigrator>();
builder.Services.AddPoolAiObservability(builder.Configuration);

using IHost host = builder.Build();
PostgresMigrator migrator = host.Services.GetRequiredService<PostgresMigrator>();
await migrator
    .ApplyAsync(connectionString, "PoolAI.Migrator", CancellationToken.None)
    .ConfigureAwait(false);

static string ValidateConnectionString(IConfiguration configuration, bool isProduction)
{
    const string Key = "Data:Postgres:ConnectionString";
    string? connectionString = configuration[Key];
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException($"Invalid or missing configuration key: {Key}.");
    }

    try
    {
        NpgsqlConnectionStringBuilder parsed = new(connectionString);
        if (string.IsNullOrWhiteSpace(parsed.Host)
            || string.IsNullOrWhiteSpace(parsed.Username)
            || (isProduction
                && (parsed.SslMode is not SslMode.Require
                    and not SslMode.VerifyCA
                    and not SslMode.VerifyFull
                    || string.IsNullOrWhiteSpace(parsed.Password))))
        {
            throw new InvalidOperationException($"Invalid or missing configuration key: {Key}.");
        }
    }
    catch (ArgumentException)
    {
        throw new InvalidOperationException($"Invalid or missing configuration key: {Key}.");
    }

    return connectionString;
}
