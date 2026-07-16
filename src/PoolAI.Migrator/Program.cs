using PoolAI.Database.Migrations;
using Npgsql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PoolAI.Migrator;
using System.Text;

MigratorInvocation invocation = MigratorCommandParser.Parse(args);
HostApplicationBuilder builder = Host.CreateApplicationBuilder([]);
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
builder.Services.AddSingleton<AdminBootstrapWriter>();
builder.Services.AddPoolAiObservability(builder.Configuration);

using IHost host = builder.Build();
PostgresMigrator migrator = host.Services.GetRequiredService<PostgresMigrator>();
await migrator
    .ApplyAsync(connectionString, "PoolAI.Migrator", CancellationToken.None)
    .ConfigureAwait(false);

if (invocation.ShouldBootstrapAdmin)
{
    if (!Console.IsInputRedirected)
    {
        throw new InvalidOperationException(
            "Admin bootstrap requires redirected standard input.");
    }

    Console.InputEncoding = new UTF8Encoding(false, true);
    AdminBootstrapSecrets secrets = await BootstrapAdminSecretReader
        .ReadAsync(Console.In, CancellationToken.None)
        .ConfigureAwait(false);
    AdminBootstrapRequest request = new(
        invocation.Email!,
        invocation.DisplayName!,
        secrets);
    AdminBootstrapWriter bootstrapWriter = host.Services
        .GetRequiredService<AdminBootstrapWriter>();
    _ = await bootstrapWriter
        .CreateAsync(connectionString, request, CancellationToken.None)
        .ConfigureAwait(false);
}

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
