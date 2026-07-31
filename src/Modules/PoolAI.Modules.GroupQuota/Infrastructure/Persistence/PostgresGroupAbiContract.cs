#pragma warning disable MA0048 // The PostgreSQL function row belongs to this ABI boundary.
using Npgsql;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application.Ports;

namespace PoolAI.Modules.GroupQuota.Infrastructure.Persistence;

/// <summary>
/// Reads and validates the PostgreSQL values owned by the signed Group SQL contract.
/// </summary>
internal static class PostgresGroupAbiContract
{
    internal static async ValueTask<PostgresGroupFunctionRow> ReadFunctionResultAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The Group database function returned an invalid result cardinality.");
        }

        PostgresGroupFunctionRow result = new(
            reader.GetString(0),
            reader.GetBoolean(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3));
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The Group database function returned an invalid result cardinality.");
        }

        return result;
    }

    internal static GroupWriteDisposition MapUpdateDisposition(
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

    internal static GroupLifecycle ParseLifecycle(string value) => value switch
    {
        "disabled" => GroupLifecycle.Disabled,
        "active" => GroupLifecycle.Active,
        "archived" => GroupLifecycle.Archived,
        _ => throw new InvalidOperationException("The persisted Group lifecycle is invalid."),
    };

    internal static string LifecycleCode(GroupLifecycle lifecycle) => lifecycle switch
    {
        GroupLifecycle.Disabled => "disabled",
        GroupLifecycle.Active => "active",
        GroupLifecycle.Archived => "archived",
        _ => throw new ArgumentOutOfRangeException(nameof(lifecycle)),
    };

    internal static GroupPoolQuotaStatus ParseQuotaStatus(string value) => value switch
    {
        "active" => GroupPoolQuotaStatus.Active,
        "exhausted" => GroupPoolQuotaStatus.Exhausted,
        "disabled" => GroupPoolQuotaStatus.Disabled,
        _ => throw new InvalidOperationException("The derived Group quota status is invalid."),
    };
}

internal sealed record PostgresGroupFunctionRow(
    string Disposition,
    bool WasChanged,
    string? BeforeState,
    long? CurrentVersion);
#pragma warning restore MA0048
