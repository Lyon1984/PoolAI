using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Supply.Application.Ports;

namespace PoolAI.Modules.Supply.Infrastructure.Persistence;

internal sealed partial class PostgresAccountCredentialStore : IAccountCredentialStore
{
    private const string CreateSql = """
        SELECT disposition,
               current_version,
               current_credential_revision
        FROM public.poolai_supply_create_account(
            $1, $2, $3, $4, $5::jsonb, $6, $7, $8, $9, $10
        );
        """;

    private const string ReplaceSql = """
        SELECT disposition,
               current_version,
               current_credential_revision
        FROM public.poolai_supply_replace_account_credential(
            $1, $2, $3::jsonb, $4, $5
        );
        """;

    private const string RewrapSql = """
        SELECT disposition,
               current_credential_revision
        FROM public.poolai_supply_rewrap_account_credential(
            $1, $2, $3::jsonb
        );
        """;

    private readonly NpgsqlDataSource _dataSource;

    public PostgresAccountCredentialStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async ValueTask<AccountCredentialCreateResult> CreateAsync(
        AccountCredentialCreate account,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ValidateEnvelope(account.Envelope, nameof(account));
        PostgresTransactionSession session =
            PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(CreateSql);
        command.Parameters.AddWithValue(account.AccountId.Value);
        command.Parameters.AddWithValue(account.Provider);
        command.Parameters.AddWithValue(account.Name);
        command.Parameters.AddWithValue(account.UpstreamBaseUrl);
        AddJson(command, account.Envelope);
        command.Parameters.AddWithValue(account.CredentialPrefix);
        AddNullableText(command, account.CredentialHint);
        command.Parameters.AddWithValue(account.MaxConcurrency);
        command.Parameters.AddWithValue(account.Priority);
        command.Parameters.AddWithValue(account.Weight);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The Account credential create entry point returned no result.");
        }

        AccountCredentialCreateResult result = new(
            CreateDisposition(reader.GetString(0)),
            ReadNullableInt64(reader, 1),
            ReadNullableInt64(reader, 2));
        await EnsureNoMoreRowsAsync(
            reader,
            "create",
            cancellationToken).ConfigureAwait(false);
        ValidateCreateResult(result);
        return result;
    }

    public async ValueTask<AccountCredentialReplacementResult> ReplaceAsync(
        AccountCredentialReplacement replacement,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            replacement.ExpectedVersion,
            1,
            nameof(replacement));
        ValidateEnvelope(replacement.Envelope, nameof(replacement));
        PostgresTransactionSession session =
            PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(ReplaceSql);
        command.Parameters.AddWithValue(replacement.AccountId.Value);
        command.Parameters.AddWithValue(replacement.ExpectedVersion);
        AddJson(command, replacement.Envelope);
        command.Parameters.AddWithValue(replacement.CredentialPrefix);
        AddNullableText(command, replacement.CredentialHint);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The Account credential replacement entry point returned no result.");
        }

        AccountCredentialReplacementResult result = new(
            ReplacementDisposition(reader.GetString(0)),
            ReadNullableInt64(reader, 1),
            ReadNullableInt64(reader, 2));
        await EnsureNoMoreRowsAsync(
            reader,
            "replacement",
            cancellationToken).ConfigureAwait(false);
        ValidateReplacementResult(result, replacement.ExpectedVersion);
        return result;
    }

    public async ValueTask<AccountCredentialRewrapWriteResult> TryRewrapAsync(
        AccountCredentialRewrapWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            write.ExpectedCredentialRevision,
            1,
            nameof(write));
        ValidateEnvelope(write.Envelope, nameof(write));
        PostgresTransactionSession session =
            PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(RewrapSql);
        command.Parameters.AddWithValue(write.AccountId.Value);
        command.Parameters.AddWithValue(write.ExpectedCredentialRevision);
        AddJson(command, write.Envelope);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The Account credential rewrap entry point returned no result.");
        }

        AccountCredentialRewrapWriteResult result = new(
            RewrapDisposition(reader.GetString(0)),
            ReadNullableInt64(reader, 1));
        await EnsureNoMoreRowsAsync(
            reader,
            "rewrap",
            cancellationToken).ConfigureAwait(false);
        ValidateRewrapResult(result, write.ExpectedCredentialRevision);
        return result;
    }

    private static void ValidateEnvelope(JsonElement envelope, string parameterName)
    {
        if (envelope.ValueKind is not JsonValueKind.Object)
        {
            throw new ArgumentException(
                "The Account credential envelope must be a JSON object.",
                parameterName);
        }
    }

    private static void AddJson(NpgsqlCommand command, JsonElement value) =>
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = value.GetRawText(),
        });

    private static void AddNullableText(NpgsqlCommand command, string? value) =>
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Text,
            Value = value is null ? DBNull.Value : value,
        });

    private static long? ReadNullableInt64(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static async ValueTask EnsureNoMoreRowsAsync(
        NpgsqlDataReader reader,
        string operation,
        CancellationToken cancellationToken)
    {
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"The Account credential {operation} entry point returned multiple results.");
        }
    }

    private static void ValidateCreateResult(
        AccountCredentialCreateResult result)
    {
        bool valid = result.Disposition switch
        {
            AccountCredentialCreateDisposition.Created =>
                result.CurrentVersion == 1
                && result.CurrentCredentialRevision == 1,
            AccountCredentialCreateDisposition.ValidationFailed
                or AccountCredentialCreateDisposition.Conflict =>
                result.CurrentVersion is null
                && result.CurrentCredentialRevision is null,
            _ => false,
        };
        if (!valid)
        {
            throw InvalidResultShape("create");
        }
    }

    private static void ValidateReplacementResult(
        AccountCredentialReplacementResult result,
        long expectedVersion)
    {
        bool valid = result.Disposition switch
        {
            AccountCredentialReplacementDisposition.Replaced =>
                result.CurrentVersion == checked(expectedVersion + 1)
                && IsPositive(result.CurrentCredentialRevision),
            AccountCredentialReplacementDisposition.AccountRetired
                or AccountCredentialReplacementDisposition.VersionConflict =>
                IsPositive(result.CurrentVersion)
                && IsPositive(result.CurrentCredentialRevision),
            AccountCredentialReplacementDisposition.ValidationFailed
                or AccountCredentialReplacementDisposition.NotFound =>
                result.CurrentVersion is null
                && result.CurrentCredentialRevision is null,
            _ => false,
        };
        if (!valid)
        {
            throw InvalidResultShape("replacement");
        }
    }

    private static void ValidateRewrapResult(
        AccountCredentialRewrapWriteResult result,
        long expectedCredentialRevision)
    {
        bool valid = result.Disposition switch
        {
            AccountCredentialRewrapWriteDisposition.Rewrapped =>
                result.CurrentCredentialRevision
                    == checked(expectedCredentialRevision + 1),
            AccountCredentialRewrapWriteDisposition.CredentialRevisionConflict
                or AccountCredentialRewrapWriteDisposition.ContentMismatch =>
                IsPositive(result.CurrentCredentialRevision),
            AccountCredentialRewrapWriteDisposition.ValidationFailed
                or AccountCredentialRewrapWriteDisposition.NotFound =>
                result.CurrentCredentialRevision is null,
            _ => false,
        };
        if (!valid)
        {
            throw InvalidResultShape("rewrap");
        }
    }

    private static bool IsPositive(long? value) => value is > 0;

    private static InvalidOperationException InvalidResultShape(
        string operation) =>
        new($"The Account credential {operation} entry point returned an invalid result.");

    private static AccountCredentialCreateDisposition CreateDisposition(string value) =>
        value switch
        {
            "created" => AccountCredentialCreateDisposition.Created,
            "validation_failed" => AccountCredentialCreateDisposition.ValidationFailed,
            "conflict" => AccountCredentialCreateDisposition.Conflict,
            _ => throw UnknownDisposition(),
        };

    private static AccountCredentialReplacementDisposition ReplacementDisposition(
        string value) =>
        value switch
        {
            "replaced" => AccountCredentialReplacementDisposition.Replaced,
            "validation_failed" =>
                AccountCredentialReplacementDisposition.ValidationFailed,
            "not_found" => AccountCredentialReplacementDisposition.NotFound,
            "account_retired" =>
                AccountCredentialReplacementDisposition.AccountRetired,
            "version_conflict" =>
                AccountCredentialReplacementDisposition.VersionConflict,
            _ => throw UnknownDisposition(),
        };

    private static AccountCredentialRewrapWriteDisposition RewrapDisposition(
        string value) =>
        value switch
        {
            "rewrapped" => AccountCredentialRewrapWriteDisposition.Rewrapped,
            "validation_failed" =>
                AccountCredentialRewrapWriteDisposition.ValidationFailed,
            "not_found" => AccountCredentialRewrapWriteDisposition.NotFound,
            "credential_revision_conflict" =>
                AccountCredentialRewrapWriteDisposition.CredentialRevisionConflict,
            "content_mismatch" =>
                AccountCredentialRewrapWriteDisposition.ContentMismatch,
            _ => throw UnknownDisposition(),
        };

    private static InvalidOperationException UnknownDisposition() =>
        new("The Account credential entry point returned an unknown disposition.");
}
