#pragma warning disable MA0048 // The two PostgreSQL function rows belong to this ABI boundary.
#pragma warning disable MA0051 // Each validator keeps its signed SQL result contract visible in one place.
using System.Globalization;
using System.Numerics;
using Npgsql;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application.Ports;
using PoolAI.Modules.GroupQuota.Domain;

namespace PoolAI.Modules.GroupQuota.Infrastructure.Persistence;

/// <summary>
/// Reads and validates the PostgreSQL row shapes owned by the signed Group quota SQL contract.
/// </summary>
/// <remarks>
/// This boundary is internal so integration tests can exercise hostile PostgreSQL rows without
/// replacing the signed functions or reaching into repository implementation details.
/// </remarks>
internal static class PostgresQuotaAbiContract
{
    private static readonly BigInteger MaximumSafeTokenCount =
        new(9_007_199_254_740_991L);

    internal static async ValueTask<GroupQuotaResource?> ReadSnapshotAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        GroupQuotaResource snapshot = new(
            new EntityId(reader.GetGuid(0)),
            new EntityId(reader.GetGuid(1)),
            ParseStatus(reader.GetString(2)),
            reader.GetFieldValue<BigInteger>(3),
            reader.GetFieldValue<BigInteger>(4),
            reader.GetFieldValue<BigInteger>(5),
            reader.GetFieldValue<BigInteger>(6),
            reader.GetFieldValue<BigInteger>(7),
            reader.GetFieldValue<DateTimeOffset>(8),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
            reader.GetInt64(10),
            reader.GetFieldValue<DateTimeOffset>(11));
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The Group quota snapshot query returned an invalid result cardinality.");
        }

        ValidateSnapshot(snapshot);
        return snapshot;
    }

    internal static async ValueTask<PostgresQuotaAdjustFunctionRow> ReadAdjustResultAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The quota-adjust function returned an invalid result cardinality.");
        }

        PostgresQuotaAdjustFunctionRow result = new(
            new EntityId(reader.GetGuid(0)),
            reader.GetFieldValue<BigInteger>(1),
            reader.GetFieldValue<BigInteger>(2),
            reader.GetFieldValue<BigInteger>(3),
            reader.GetFieldValue<BigInteger>(4),
            reader.GetInt64(5),
            reader.GetString(6));
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The quota-adjust function returned an invalid result cardinality.");
        }

        return result;
    }

    internal static async ValueTask<PostgresQuotaResetFunctionRow> ReadResetResultAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The quota-reset function returned an invalid result cardinality.");
        }

        PostgresQuotaResetFunctionRow result = new(
            new EntityId(reader.GetGuid(0)),
            reader.GetInt64(1),
            reader.GetFieldValue<BigInteger>(2),
            reader.GetFieldValue<BigInteger>(3),
            reader.GetFieldValue<BigInteger>(4),
            reader.GetFieldValue<BigInteger>(5),
            reader.GetInt64(6),
            reader.GetString(7));
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The quota-reset function returned an invalid result cardinality.");
        }

        return result;
    }

    internal static GroupQuotaResource ParseBeforeState(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "The quota mutation before-state is not a JSON object.");
        }

        return new GroupQuotaResource(
            new EntityId(root.GetProperty("group_id").GetGuid()),
            new EntityId(root.GetProperty("period_id").GetGuid()),
            ParseStatus(root.GetProperty("status").GetString()!),
            ParseTokenCount(root.GetProperty("total_tokens")),
            ParseTokenCount(root.GetProperty("consumed_tokens")),
            ParseTokenCount(root.GetProperty("reserved_tokens")),
            ParseTokenCount(root.GetProperty("remaining_tokens")),
            ParseTokenCount(root.GetProperty("overage_tokens")),
            root.GetProperty("period_started_at").GetDateTimeOffset(),
            root.GetProperty("period_ended_at").ValueKind == JsonValueKind.Null
                ? null
                : root.GetProperty("period_ended_at").GetDateTimeOffset(),
            root.GetProperty("version").GetInt64(),
            root.GetProperty("updated_at").GetDateTimeOffset());
    }

    internal static void ValidateAdjustResult(
        AdjustQuotaWrite write,
        GroupQuotaResource? before,
        GroupQuotaResource after,
        PostgresQuotaAdjustFunctionRow result)
    {
        // Frozen HTTP replay is owned by idempotency_records in the same UoW.
        // A successful raw-SQL event replay cannot reconstruct that response
        // after later quota changes, so this adapter accepts only a new CAS
        // transition and fails closed instead of emitting a duplicate audit.
        if (before is null)
        {
            throw new InvalidOperationException(
                "The quota-adjust function omitted its canonical before-state.");
        }

        ValidateSnapshot(before);
        ValidateSnapshot(after);
        if (before.GroupId != write.GroupId
            || after.GroupId != write.GroupId
            || before.Version != write.ExpectedVersion
            || after.Version != checked(before.Version + 1)
            || before.PeriodId != after.PeriodId
            || before.ConsumedTokens != after.ConsumedTokens
            || before.ReservedTokens != after.ReservedTokens
            || before.PeriodStartedAt != after.PeriodStartedAt
            || after.UpdatedAt < before.UpdatedAt
            || result.PeriodId != after.PeriodId
            || result.TotalTokens != write.NewTotalTokens
            || result.TotalTokens != after.TotalTokens
            || result.ConsumedTokens != after.ConsumedTokens
            || result.ReservedTokens != after.ReservedTokens
            || result.RemainingTokens != after.RemainingTokens
            || result.QuotaVersion != after.Version)
        {
            throw new InvalidOperationException(
                "The quota-adjust function result does not match the canonical snapshot.");
        }
    }

    internal static void ValidateResetResult(
        ResetQuotaWrite write,
        GroupQuotaResource? before,
        GroupQuotaResource after,
        PostgresQuotaResetFunctionRow result)
    {
        // See ValidateAdjustResult: SQL-level replay is a database protocol,
        // while this adapter consumes only newly written application commands.
        if (before is null)
        {
            throw new InvalidOperationException(
                "The quota-reset function omitted its canonical before-state.");
        }

        ValidateSnapshot(before);
        ValidateSnapshot(after);
        if (before.GroupId != write.GroupId
            || after.GroupId != write.GroupId
            || before.Version != write.ExpectedVersion
            || after.Version != checked(before.Version + 1)
            || before.PeriodId == after.PeriodId
            || after.PeriodId != write.NewPeriodId
            || result.PeriodId != after.PeriodId
            || result.PeriodNumber <= 0
            || result.TotalTokens != write.TotalTokens
            || result.TotalTokens != after.TotalTokens
            || result.ConsumedTokens != BigInteger.Zero
            || result.ReservedTokens != BigInteger.Zero
            || result.RemainingTokens != result.TotalTokens
            || result.ConsumedTokens != after.ConsumedTokens
            || result.ReservedTokens != after.ReservedTokens
            || result.QuotaVersion != after.Version
            || after.PeriodStartedAt < before.PeriodStartedAt
            || after.UpdatedAt < before.UpdatedAt)
        {
            throw new InvalidOperationException(
                "The quota-reset function result does not match the canonical snapshot.");
        }
    }

    internal static void ValidateSnapshot(GroupQuotaResource snapshot)
    {
        BigInteger remaining = BigInteger.Max(
            snapshot.TotalTokens - snapshot.ConsumedTokens - snapshot.ReservedTokens,
            BigInteger.Zero);
        BigInteger overage = BigInteger.Max(
            snapshot.ConsumedTokens - snapshot.TotalTokens,
            BigInteger.Zero);
        bool statusValid = snapshot.Status switch
        {
            GroupPoolQuotaStatus.Active => snapshot.ConsumedTokens < snapshot.TotalTokens,
            GroupPoolQuotaStatus.Exhausted => snapshot.ConsumedTokens >= snapshot.TotalTokens,
            GroupPoolQuotaStatus.Disabled => true,
            _ => false,
        };
        if (snapshot.GroupId.Value == Guid.Empty
            || snapshot.PeriodId.Value == Guid.Empty
            || snapshot.TotalTokens < BigInteger.One
            || snapshot.TotalTokens > MaximumSafeTokenCount
            || snapshot.ConsumedTokens < BigInteger.Zero
            || snapshot.ReservedTokens < BigInteger.Zero
            || snapshot.RemainingTokens != remaining
            || snapshot.OverageTokens != overage
            || snapshot.PeriodEndedAt is not null
            || snapshot.Version <= 0
            || snapshot.UpdatedAt < snapshot.PeriodStartedAt
            || !statusValid)
        {
            throw new InvalidOperationException(
                "The canonical Group quota snapshot is invalid.");
        }
    }

    internal static GroupPoolQuotaStatus ParseStatus(string value) => value switch
    {
        "active" => GroupPoolQuotaStatus.Active,
        "exhausted" => GroupPoolQuotaStatus.Exhausted,
        "disabled" => GroupPoolQuotaStatus.Disabled,
        _ => throw new InvalidOperationException("The derived Group quota status is invalid."),
    };

    private static BigInteger ParseTokenCount(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                "The quota before-state Token count must be a string.");
        }

        string text = value.GetString()!;
        if (text.Length is < 1 or > 78
            || !string.Equals(text, "0", StringComparison.Ordinal)
                && (text[0] is < '1' or > '9'
                || text.AsSpan(1).ContainsAnyExceptInRange('0', '9')))
        {
            throw new InvalidOperationException(
                "The quota before-state Token count is not canonical.");
        }

        return BigInteger.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
    }
}

internal sealed record PostgresQuotaAdjustFunctionRow(
    EntityId PeriodId,
    BigInteger TotalTokens,
    BigInteger ConsumedTokens,
    BigInteger ReservedTokens,
    BigInteger RemainingTokens,
    long QuotaVersion,
    string BeforeState);

internal sealed record PostgresQuotaResetFunctionRow(
    EntityId PeriodId,
    long PeriodNumber,
    BigInteger TotalTokens,
    BigInteger ConsumedTokens,
    BigInteger ReservedTokens,
    BigInteger RemainingTokens,
    long QuotaVersion,
    string BeforeState);
#pragma warning restore MA0051
#pragma warning restore MA0048
