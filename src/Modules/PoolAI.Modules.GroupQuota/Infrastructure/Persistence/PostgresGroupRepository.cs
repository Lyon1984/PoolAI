#pragma warning disable MA0051 // SQL adapters keep the complete function-call and savepoint mapping visible.
using System.Runtime.CompilerServices;
using Npgsql;
using NpgsqlTypes;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application.Ports;
using PoolAI.Modules.GroupQuota.Domain;

namespace PoolAI.Modules.GroupQuota.Infrastructure.Persistence;

internal sealed partial class PostgresGroupRepository(NpgsqlDataSource dataSource) : IGroupRepository
{
    private const string CreateSavepoint = "group_create_call";
    private const string UpdateSavepoint = "group_update_call";

    private const string SelectColumns = """
        g.id,
        g.name,
        g.description,
        g.status,
        g.version,
        g.created_at,
        g.updated_at,
        EXISTS (
            SELECT 1
            FROM public.group_token_quotas AS quota
            JOIN public.group_quota_periods AS period
              ON period.id = quota.current_period_id
             AND period.group_id = quota.group_id
            WHERE quota.group_id = g.id
              AND quota.enabled = true
              AND period.status = 'current'
              AND period.total_tokens > 0
        ) AS has_current_quota_period,
        clock_timestamp() AS observed_at
        """;

    private static readonly string GetSql = $"""
        SELECT {SelectColumns}
        FROM public.groups AS g
        WHERE g.id = $1;
        """;

    // This preflight only selects an early error presentation. The subsequent
    // poolai_group_update call owns the row lock, CAS, and final activation guard.
    private static readonly string GetForActivationSql = $"""
        SELECT {SelectColumns}
        FROM public.groups AS g
        WHERE g.id = $1;
        """;

    private static readonly string ListFirstSql = $"""
        SELECT {SelectColumns}
        FROM public.groups AS g
        ORDER BY g.created_at DESC, g.id DESC
        LIMIT $1;
        """;

    private static readonly string ListAfterSql = $"""
        SELECT {SelectColumns}
        FROM public.groups AS g
        WHERE g.created_at < $1
           OR (g.created_at = $1 AND g.id < $2)
        ORDER BY g.created_at DESC, g.id DESC
        LIMIT $3;
        """;

    private const string CreateSql = """
        SELECT disposition, was_changed, before_state::text, current_version
        FROM public.poolai_group_create(
            $1, $2, $3, $4, $5, $6, $7, $8, $9, $10
        );
        """;

    private const string UpdateSql = """
        SELECT disposition, was_changed, before_state::text, current_version
        FROM public.poolai_group_update(
            $1, $2, $3, $4, $5, $6, $7, $8, $9, $10
        );
        """;

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async ValueTask<GroupWriteResult> CreateAsync(
        CreateGroupWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        await BeginSavepointAsync(session, CreateSavepoint, cancellationToken).ConfigureAwait(false);
        try
        {
            FunctionResult functionResult;
            using (NpgsqlCommand command = session.CreateCommand(CreateSql))
            {
                command.Parameters.AddWithValue(write.GroupId.Value);
                command.Parameters.AddWithValue(write.Name);
                AddNullableText(command.Parameters, write.Description);
                command.Parameters.AddWithValue(write.PeriodId.Value);
                command.Parameters.Add(new NpgsqlParameter
                {
                    NpgsqlDbType = NpgsqlDbType.Numeric,
                    Value = write.TotalTokens,
                });
                command.Parameters.AddWithValue(write.ActorUserId.Value);
                command.Parameters.AddWithValue(write.QuotaEventId.Value);
                command.Parameters.AddWithValue(write.QuotaOutboxId.Value);
                command.Parameters.AddWithValue(write.QuotaIdempotencyKey);
                command.Parameters.AddWithValue(write.Reason);
                functionResult = await ReadFunctionResultAsync(command, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!string.Equals(functionResult.Disposition, "created", StringComparison.Ordinal))
            {
                await ReleaseSavepointAsync(session, CreateSavepoint, cancellationToken)
                    .ConfigureAwait(false);
                return new GroupWriteResult(
                    MapCreateDisposition(functionResult.Disposition),
                    Group: null,
                    CurrentVersion: functionResult.CurrentVersion);
            }

            GroupResource group = await GetRequiredAsync(
                write.GroupId,
                session,
                cancellationToken).ConfigureAwait(false);
            await ReleaseSavepointAsync(session, CreateSavepoint, cancellationToken)
                .ConfigureAwait(false);
            return new GroupWriteResult(
                GroupWriteDisposition.Written,
                group,
                WasChanged: functionResult.WasChanged,
                CurrentVersion: functionResult.CurrentVersion);
        }
        catch (PostgresException exception) when (IsKnownCreateFailure(exception))
        {
            await RollbackAndReleaseSavepointAsync(
                session,
                CreateSavepoint,
                cancellationToken).ConfigureAwait(false);
            return new GroupWriteResult(
                GroupWriteDisposition.LifecycleConflict,
                Group: null);
        }
    }

    public async ValueTask<GroupWriteResult> UpdateAsync(
        UpdateGroupWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        await BeginSavepointAsync(session, UpdateSavepoint, cancellationToken).ConfigureAwait(false);
        try
        {
            FunctionResult functionResult;
            using (NpgsqlCommand command = session.CreateCommand(UpdateSql))
            {
                command.Parameters.AddWithValue(write.GroupId.Value);
                command.Parameters.AddWithValue(write.ExpectedVersion);
                command.Parameters.AddWithValue(write.HasName);
                AddNullableText(command.Parameters, write.HasName ? write.Name : null);
                command.Parameters.AddWithValue(write.HasDescription);
                AddNullableText(command.Parameters, write.HasDescription ? write.Description : null);
                AddNullableText(
                    command.Parameters,
                    write.Lifecycle is null ? null : LifecycleCode(write.Lifecycle.Value));
                AddNullableText(command.Parameters, write.Lifecycle is null ? null : write.Reason);
                AddNullableText(command.Parameters, write.SupplyEvidence?.OpaqueToken);
                AddNullableTimestamp(
                    command.Parameters,
                    write.SupplyEvidence?.ObservedAt.ToUniversalTime());
                functionResult = await ReadFunctionResultAsync(command, cancellationToken)
                    .ConfigureAwait(false);
            }

            GroupWriteDisposition disposition = MapDisposition(
                functionResult.Disposition,
                write.SupplyEvidence is not null);
            if (disposition != GroupWriteDisposition.Written)
            {
                GroupResource? before = ParseBeforeState(
                    functionResult.BeforeState,
                    hasCurrentQuotaPeriod: false);
                await ReleaseSavepointAsync(session, UpdateSavepoint, cancellationToken)
                    .ConfigureAwait(false);
                return new GroupWriteResult(
                    disposition,
                    Group: null,
                    before,
                    functionResult.WasChanged,
                    functionResult.CurrentVersion);
            }

            GroupResource updated = await GetRequiredAsync(
                write.GroupId,
                session,
                cancellationToken).ConfigureAwait(false);
            GroupResource? canonicalBefore = ParseBeforeState(
                functionResult.BeforeState,
                updated.HasCurrentQuotaPeriod);
            await ReleaseSavepointAsync(session, UpdateSavepoint, cancellationToken)
                .ConfigureAwait(false);
            return new GroupWriteResult(
                GroupWriteDisposition.Written,
                updated,
                canonicalBefore,
                functionResult.WasChanged,
                functionResult.CurrentVersion);
        }
        catch (PostgresException exception) when (
            string.Equals(
                exception.SqlState,
                PostgresErrorCodes.UniqueViolation,
                StringComparison.Ordinal))
        {
            await RollbackAndReleaseSavepointAsync(
                session,
                UpdateSavepoint,
                cancellationToken).ConfigureAwait(false);
            return new GroupWriteResult(GroupWriteDisposition.NameConflict, Group: null);
        }
        catch (PostgresException exception) when (
            string.Equals(exception.SqlState, "P0001", StringComparison.Ordinal))
        {
            await RollbackAndReleaseSavepointAsync(
                session,
                UpdateSavepoint,
                cancellationToken).ConfigureAwait(false);
            return new GroupWriteResult(
                write.SupplyEvidence is null
                    ? GroupWriteDisposition.LifecycleConflict
                    : GroupWriteDisposition.ActivationNotReady,
                Group: null);
        }
    }

    public async ValueTask<GroupResource?> GetForActivationAsync(
        EntityId groupId,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(GetForActivationSql);
        command.Parameters.AddWithValue(groupId.Value);
        return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<FunctionResult> ReadFunctionResultAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The Group database function returned no result.");
        }

        return new FunctionResult(
            reader.GetString(0),
            reader.GetBoolean(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3));
    }

    private static async ValueTask<GroupResource> GetRequiredAsync(
        EntityId groupId,
        PostgresTransactionSession session,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand(GetSql);
        command.Parameters.AddWithValue(groupId.Value);
        return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The written Group could not be reloaded.");
    }

    private static async ValueTask<GroupResource?> ReadSingleAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadGroup(reader)
            : null;
    }

    private static GroupResource ReadGroup(NpgsqlDataReader reader) => new(
        new EntityId(reader.GetGuid(0)),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        ParseLifecycle(reader.GetString(3)),
        reader.GetInt64(4),
        reader.GetFieldValue<DateTimeOffset>(5),
        reader.GetFieldValue<DateTimeOffset>(6),
        reader.GetBoolean(7),
        reader.GetFieldValue<DateTimeOffset>(8));

    private static GroupResource? ParseBeforeState(
        string? json,
        bool hasCurrentQuotaPeriod)
    {
        if (json is null)
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        return new GroupResource(
            new EntityId(root.GetProperty("id").GetGuid()),
            root.GetProperty("name").GetString()
                ?? throw new InvalidOperationException("The Group before-state name is invalid."),
            root.GetProperty("description").ValueKind == JsonValueKind.Null
                ? null
                : root.GetProperty("description").GetString(),
            ParseLifecycle(root.GetProperty("status").GetString() ?? string.Empty),
            root.GetProperty("version").GetInt64(),
            root.GetProperty("created_at").GetDateTimeOffset(),
            root.GetProperty("updated_at").GetDateTimeOffset(),
            hasCurrentQuotaPeriod,
            root.GetProperty("updated_at").GetDateTimeOffset());
    }

    private static GroupWriteDisposition MapDisposition(
        string disposition,
        bool isActivation) => disposition switch
        {
            "updated" => GroupWriteDisposition.Written,
            "not_found" => GroupWriteDisposition.NotFound,
            "version_conflict" => GroupWriteDisposition.VersionConflict,
            "invalid_transition" => isActivation
                ? GroupWriteDisposition.ActivationNotReady
                : GroupWriteDisposition.LifecycleConflict,
            "archive_blocked" => GroupWriteDisposition.ArchiveBlocked,
            "validation_failed" => isActivation
                ? GroupWriteDisposition.ActivationNotReady
                : GroupWriteDisposition.ValidationFailed,
            _ => throw new InvalidOperationException(
                "The Group database function returned an unknown disposition."),
        };

    internal static GroupWriteDisposition MapCreateDisposition(string disposition) =>
        disposition switch
        {
            "conflict" => GroupWriteDisposition.NameConflict,
            "validation_failed" => GroupWriteDisposition.ValidationFailed,
            _ => throw new InvalidOperationException(
                "The Group create function returned an unknown disposition."),
        };

    private static GroupLifecycle ParseLifecycle(string value) => value switch
    {
        "disabled" => GroupLifecycle.Disabled,
        "active" => GroupLifecycle.Active,
        "archived" => GroupLifecycle.Archived,
        _ => throw new InvalidOperationException("The persisted Group lifecycle is invalid."),
    };

    private static string LifecycleCode(GroupLifecycle lifecycle) => lifecycle switch
    {
        GroupLifecycle.Disabled => "disabled",
        GroupLifecycle.Active => "active",
        GroupLifecycle.Archived => "archived",
        _ => throw new ArgumentOutOfRangeException(nameof(lifecycle)),
    };

    private static bool IsKnownCreateFailure(PostgresException exception) =>
        string.Equals(exception.SqlState, "P0001", StringComparison.Ordinal)
        || string.Equals(
            exception.SqlState,
            PostgresErrorCodes.UniqueViolation,
            StringComparison.Ordinal);

    private static void AddNullableText(
        NpgsqlParameterCollection parameters,
        string? value) => parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Text,
            Value = value ?? (object)DBNull.Value,
        });

    private static void AddNullableTimestamp(
        NpgsqlParameterCollection parameters,
        DateTimeOffset? value) => parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.TimestampTz,
            Value = value ?? (object)DBNull.Value,
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
        using (NpgsqlCommand rollback = session.CreateCommand($"ROLLBACK TO SAVEPOINT {name};"))
        {
            _ = await rollback.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ReleaseSavepointAsync(session, name, cancellationToken).ConfigureAwait(false);
    }

    private sealed record FunctionResult(
        string Disposition,
        bool WasChanged,
        string? BeforeState,
        long? CurrentVersion);
}
#pragma warning restore MA0051
