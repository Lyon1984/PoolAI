#pragma warning disable MA0051 // The Account function-call protocol remains visible in one adapter.
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Application.Ports;
using PoolAI.Modules.Supply.Domain;

namespace PoolAI.Modules.Supply.Infrastructure.Persistence;

internal sealed partial class PostgresAccountControlPlaneRepository
    : IAccountControlPlaneRepository
{
    internal const string UpdateFunctionName = "public.poolai_supply_update_account";
    internal const string RetireFunctionName = "public.poolai_supply_retire_account";

    private const string UpdateSavepoint = "supply_account_update_call";
    private const string RetireSavepoint = "supply_account_retire_call";

    private static readonly string UpdateSql = $"""
        SELECT disposition,
               was_changed,
               before_state::text,
               current_version
        FROM {UpdateFunctionName}(
            $1, $2, $3, $4, $5, $6, $7, $8::jsonb, $9, $10,
            $11, $12, $13, $14, $15, $16, $17, $18, $19
        );
        """;

    private static readonly string RetireSql = $"""
        SELECT disposition,
               was_changed,
               before_state::text,
               current_version
        FROM {RetireFunctionName}($1, $2, $3);
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly IAccountCredentialStore _credentialStore;

    public PostgresAccountControlPlaneRepository(
        NpgsqlDataSource dataSource,
        IAccountCredentialStore credentialStore)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _credentialStore = credentialStore
            ?? throw new ArgumentNullException(nameof(credentialStore));
    }

    public async ValueTask<AccountMutationResult> CreateAsync(
        AccountCreateWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        AccountCredentialCreateResult created = await _credentialStore.CreateAsync(
            new AccountCredentialCreate(
                write.AccountId,
                ProviderCode(write.Provider),
                write.Name,
                write.UpstreamBaseUrl,
                write.CredentialEnvelope,
                write.CredentialPrefix,
                CredentialHint: null,
                write.MaxConcurrency,
                write.Priority,
                write.Weight),
            unitOfWorkContext,
            cancellationToken).ConfigureAwait(false);
        AccountMutationDisposition disposition = created.Disposition switch
        {
            AccountCredentialCreateDisposition.Created =>
                AccountMutationDisposition.Written,
            AccountCredentialCreateDisposition.ValidationFailed =>
                AccountMutationDisposition.ValidationFailed,
            AccountCredentialCreateDisposition.Conflict =>
                AccountMutationDisposition.Conflict,
            _ => throw new InvalidOperationException(
                "The Account credential create result is unknown."),
        };
        if (disposition != AccountMutationDisposition.Written)
        {
            return new AccountMutationResult(
                disposition,
                WasChanged: false,
                Value: null,
                Before: null,
                created.CurrentVersion);
        }

        PostgresTransactionSession session =
            PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        AccountResource value = await GetRequiredAsync(
            write.AccountId,
            session,
            cancellationToken).ConfigureAwait(false);
        return new AccountMutationResult(
            AccountMutationDisposition.Written,
            WasChanged: true,
            value,
            Before: null,
            value.Version);
    }

    public async ValueTask<AccountMutationResult> UpdateAsync(
        AccountUpdateWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        PostgresTransactionSession session =
            PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        await BeginSavepointAsync(
            session,
            UpdateSavepoint,
            cancellationToken).ConfigureAwait(false);
        try
        {
            AccountFunctionResult functionResult;
            using (NpgsqlCommand command = session.CreateCommand(UpdateSql))
            {
                command.Parameters.AddWithValue(write.AccountId.Value);
                command.Parameters.AddWithValue(write.ExpectedVersion);
                command.Parameters.AddWithValue(write.NameSpecified);
                AddNullableText(command.Parameters, write.Name);
                command.Parameters.AddWithValue(write.BaseUrlSpecified);
                AddNullableText(command.Parameters, write.UpstreamBaseUrl);
                command.Parameters.AddWithValue(write.CredentialSpecified);
                AddNullableJson(command.Parameters, write.CredentialEnvelope);
                AddNullableText(command.Parameters, write.CredentialPrefix);
                AddNullableText(command.Parameters, value: null);
                command.Parameters.AddWithValue(write.StatusSpecified);
                AddNullableText(
                    command.Parameters,
                    write.Status is null ? null : StatusCode(write.Status.Value));
                command.Parameters.AddWithValue(write.MaxConcurrencySpecified);
                AddNullableInteger(command.Parameters, write.MaxConcurrency);
                command.Parameters.AddWithValue(write.PrioritySpecified);
                AddNullableInteger(command.Parameters, write.Priority);
                command.Parameters.AddWithValue(write.WeightSpecified);
                AddNullableInteger(command.Parameters, write.Weight);
                AddNullableText(command.Parameters, write.Reason);
                functionResult = await ReadFunctionResultAsync(
                    command,
                    cancellationToken).ConfigureAwait(false);
            }

            AccountMutationResult result = await ToMutationResultAsync(
                functionResult,
                write.AccountId,
                isRetire: false,
                session,
                cancellationToken).ConfigureAwait(false);
            await ReleaseSavepointAsync(
                session,
                UpdateSavepoint,
                cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (PostgresException exception) when (IsKnownMutationFailure(exception))
        {
            await RollbackAndReleaseSavepointAsync(
                session,
                UpdateSavepoint,
                cancellationToken).ConfigureAwait(false);
            return new AccountMutationResult(
                MapException(exception, isRetire: false),
                WasChanged: false,
                Value: null,
                Before: null);
        }
    }

    public async ValueTask<AccountMutationResult> RetireAsync(
        AccountRetireWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        PostgresTransactionSession session =
            PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        await BeginSavepointAsync(
            session,
            RetireSavepoint,
            cancellationToken).ConfigureAwait(false);
        try
        {
            AccountFunctionResult functionResult;
            using (NpgsqlCommand command = session.CreateCommand(RetireSql))
            {
                command.Parameters.AddWithValue(write.AccountId.Value);
                command.Parameters.AddWithValue(write.ExpectedVersion);
                command.Parameters.AddWithValue(write.Reason);
                functionResult = await ReadFunctionResultAsync(
                    command,
                    cancellationToken).ConfigureAwait(false);
            }

            AccountMutationResult result = await ToMutationResultAsync(
                functionResult,
                write.AccountId,
                isRetire: true,
                session,
                cancellationToken).ConfigureAwait(false);
            await ReleaseSavepointAsync(
                session,
                RetireSavepoint,
                cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (PostgresException exception) when (IsKnownMutationFailure(exception))
        {
            await RollbackAndReleaseSavepointAsync(
                session,
                RetireSavepoint,
                cancellationToken).ConfigureAwait(false);
            return new AccountMutationResult(
                MapException(exception, isRetire: true),
                WasChanged: false,
                Value: null,
                Before: null);
        }
    }

    private static async ValueTask<AccountFunctionResult> ReadFunctionResultAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The Account database function returned no result.");
        }

        AccountFunctionResult result = new(
            reader.GetString(0),
            reader.GetBoolean(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3));
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The Account database function returned multiple results.");
        }

        return result;
    }

    private static async ValueTask<AccountMutationResult> ToMutationResultAsync(
        AccountFunctionResult result,
        EntityId accountId,
        bool isRetire,
        PostgresTransactionSession session,
        CancellationToken cancellationToken)
    {
        AccountMutationDisposition disposition = MapDisposition(
            result.Disposition,
            isRetire);
        AccountResource? before = ParseBeforeState(result.BeforeState);
        if (disposition != AccountMutationDisposition.Written)
        {
            return new AccountMutationResult(
                disposition,
                result.WasChanged,
                Value: null,
                before,
                result.CurrentVersion);
        }

        AccountResource value = await GetRequiredAsync(
            accountId,
            session,
            cancellationToken).ConfigureAwait(false);
        if (result.CurrentVersion != value.Version || before is null)
        {
            throw new InvalidOperationException(
                "The Account mutation result is internally inconsistent.");
        }

        if (isRetire
            && (!result.WasChanged
                || value.Status != AccountResourceStatus.Retired))
        {
            throw new InvalidOperationException(
                "The Account retirement result is invalid.");
        }

        return new AccountMutationResult(
            AccountMutationDisposition.Written,
            result.WasChanged,
            value,
            before,
            result.CurrentVersion);
    }

    private static AccountResource? ParseBeforeState(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        return ReadAccount(root);
    }

    private static AccountMutationDisposition MapDisposition(
        string disposition,
        bool isRetire) => disposition switch
        {
            "updated" when !isRetire => AccountMutationDisposition.Written,
            "retired" when isRetire => AccountMutationDisposition.Written,
            "validation_failed" => AccountMutationDisposition.ValidationFailed,
            "conflict" => AccountMutationDisposition.Conflict,
            "not_found" => AccountMutationDisposition.NotFound,
            "version_conflict" => AccountMutationDisposition.VersionConflict,
            "account_retired" or "invalid_transition" =>
                AccountMutationDisposition.LifecycleConflict,
            "account_in_use" when isRetire => AccountMutationDisposition.AccountInUse,
            _ => throw new InvalidOperationException(
                "The Account database function returned an unknown disposition."),
        };

    private static AccountMutationDisposition MapException(
        PostgresException exception,
        bool isRetire)
    {
        if (isRetire
            && string.Equals(
                exception.SqlState,
                "P0001",
                StringComparison.Ordinal)
            && string.Equals(
                exception.MessageText,
                "account_in_use",
                StringComparison.Ordinal))
        {
            return AccountMutationDisposition.AccountInUse;
        }

        return string.Equals(
            exception.SqlState,
            PostgresErrorCodes.UniqueViolation,
            StringComparison.Ordinal)
            ? AccountMutationDisposition.Conflict
            : AccountMutationDisposition.ValidationFailed;
    }

    private static bool IsKnownMutationFailure(PostgresException exception) =>
        string.Equals(exception.SqlState, "P0001", StringComparison.Ordinal)
        || string.Equals(
            exception.SqlState,
            PostgresErrorCodes.CheckViolation,
            StringComparison.Ordinal)
        || string.Equals(
            exception.SqlState,
            PostgresErrorCodes.UniqueViolation,
            StringComparison.Ordinal);

    private static string ProviderCode(
        UpstreamProvider provider) => provider switch
        {
            UpstreamProvider.OpenAi => "openai",
            UpstreamProvider.OpenAiCompatible => "openai_compatible",
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };

    private static string StatusCode(AccountResourceStatus status) => status switch
    {
        AccountResourceStatus.Active => "active",
        AccountResourceStatus.Disabled => "disabled",
        AccountResourceStatus.Retired => "retired",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static void AddNullableText(
        NpgsqlParameterCollection parameters,
        string? value) => parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Text,
            Value = value ?? (object)DBNull.Value,
        });

    private static void AddNullableInteger(
        NpgsqlParameterCollection parameters,
        int? value) => parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Integer,
            Value = value is null ? DBNull.Value : value.Value,
        });

    private static void AddNullableJson(
        NpgsqlParameterCollection parameters,
        JsonElement? value) => parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = value is null
                ? DBNull.Value
                : value.Value.GetRawText(),
        });

    private static async ValueTask BeginSavepointAsync(
        PostgresTransactionSession session,
        string name,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand($"SAVEPOINT {name};");
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ReleaseSavepointAsync(
        PostgresTransactionSession session,
        string name,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand($"RELEASE SAVEPOINT {name};");
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask RollbackAndReleaseSavepointAsync(
        PostgresTransactionSession session,
        string name,
        CancellationToken cancellationToken)
    {
        using (NpgsqlCommand rollback = session.CreateCommand(
            $"ROLLBACK TO SAVEPOINT {name};"))
        {
            _ = await rollback.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ReleaseSavepointAsync(session, name, cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed record AccountFunctionResult(
        string Disposition,
        bool WasChanged,
        string? BeforeState,
        long? CurrentVersion);
}
#pragma warning restore MA0051
