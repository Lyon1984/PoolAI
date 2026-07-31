using System.Runtime.CompilerServices;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Supply.Abstractions;

namespace PoolAI.Modules.Supply.Infrastructure.Persistence;

internal sealed class PostgresAccountHealthWriter(
    IUnitOfWorkFactory unitOfWorkFactory,
    IAuditAppender auditAppender) : IAccountHealthWriter
{
    internal const string FunctionName =
        "public.poolai_supply_record_account_health";

    private const string AuditAction = "supply.account.health_transition";
    private const string RecordSql = $"""
        SELECT disposition,
               was_changed,
               before_health_status,
               before_retry_at,
               before_observed_at,
               before_version,
               current_health_status,
               current_retry_at,
               current_observed_at,
               current_version,
               current_account_status,
               current_credential_revision
        FROM {FunctionName}($1, $2, $3, $4, $5, $6);
        """;

    private readonly IUnitOfWorkFactory _unitOfWorkFactory =
        unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
    private readonly IAuditAppender _auditAppender =
        auditAppender ?? throw new ArgumentNullException(nameof(auditAppender));

    public async ValueTask<Result<AccountHealthTransitionResult>> RecordAsync(
        AccountHealthTransition transition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transition);
        if (!IsValid(transition))
        {
            return InvalidTransition();
        }

        AccountHealthTransition postgresTransition =
            NormalizeForPostgres(transition);
        if (!IsValid(postgresTransition))
        {
            return InvalidTransition();
        }

        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        PostgresTransactionSession session =
            PostgresUnitOfWorkAccessor.Require(unitOfWork.Context);
        FunctionResult functionResult = await ExecuteAsync(
            session,
            postgresTransition,
            cancellationToken).ConfigureAwait(false);

        if (string.Equals(
                functionResult.Disposition,
                "validation_failed",
                StringComparison.Ordinal))
        {
            throw InvalidResult("The database rejected a locally valid transition.");
        }

        if (string.Equals(
                functionResult.Disposition,
                "not_found",
                StringComparison.Ordinal))
        {
            ValidateEmpty(functionResult);
            await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Failure<AccountHealthTransitionResult>(
                "resource_not_found",
                "The Account does not exist.");
        }

        AccountHealthTransitionResult result = ToTransitionResult(
            functionResult,
            postgresTransition);
        if (result.WasChanged)
        {
            await _auditAppender.AppendAsync(
                CreateAuditEntry(postgresTransition, result),
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(result);
    }

    private static Result<AccountHealthTransitionResult> InvalidTransition() =>
        Result.Failure<AccountHealthTransitionResult>(
            "validation_failed",
            "The Account health transition is invalid.");

    private static bool IsValid(AccountHealthTransition transition)
    {
        if (!Enum.IsDefined(transition.Health)
            || transition.AccountId.Value == Guid.Empty
            || transition.ExpectedAccountVersion <= 0
            || transition.ExpectedCredentialRevision <= 0
            || transition.ObservedAt.Offset != TimeSpan.Zero
            || transition.ObservedAt is { } observed
                && (observed == DateTimeOffset.MinValue
                    || observed == DateTimeOffset.MaxValue))
        {
            return false;
        }

        return transition.Health == AccountHealth.Cooling
            ? transition.RetryAt is { Offset: var offset } retryAt
                && offset == TimeSpan.Zero
                && retryAt != DateTimeOffset.MinValue
                && retryAt != DateTimeOffset.MaxValue
            : transition.RetryAt is null;
    }

    private static AccountHealthTransition NormalizeForPostgres(
        AccountHealthTransition transition) =>
        transition with
        {
            ObservedAt = PostgresTimestamp(transition.ObservedAt),
            RetryAt = transition.RetryAt is { } retryAt
                ? PostgresTimestamp(retryAt)
                : null,
        };

    private static DateTimeOffset PostgresTimestamp(DateTimeOffset value)
    {
        DateTimeOffset utc = value.ToUniversalTime();
        long normalizedTicks =
            utc.Ticks - utc.Ticks % TimeSpan.TicksPerMicrosecond;
        return new DateTimeOffset(normalizedTicks, TimeSpan.Zero);
    }

    private static async ValueTask<FunctionResult> ExecuteAsync(
        PostgresTransactionSession session,
        AccountHealthTransition transition,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand(RecordSql);
        command.Parameters.AddWithValue(transition.AccountId.Value);
        command.Parameters.AddWithValue(HealthCode(transition.Health));
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.TimestampTz,
            Value = transition.ObservedAt,
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.TimestampTz,
            Value = transition.RetryAt is null
                ? DBNull.Value
                : transition.RetryAt.Value,
        });
        command.Parameters.AddWithValue(transition.ExpectedAccountVersion);
        command.Parameters.AddWithValue(transition.ExpectedCredentialRevision);
        return await ReadFunctionResultAsync(
            command,
            cancellationToken).ConfigureAwait(false);
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
            throw InvalidResult("The database function returned no result.");
        }

        FunctionResult result = new(
            reader.GetString(0),
            reader.GetBoolean(1),
            NullableText(reader, 2),
            NullableTimestamp(reader, 3),
            NullableTimestamp(reader, 4),
            NullableInt64(reader, 5),
            NullableText(reader, 6),
            NullableTimestamp(reader, 7),
            NullableTimestamp(reader, 8),
            NullableInt64(reader, 9),
            NullableText(reader, 10),
            NullableInt64(reader, 11));
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw InvalidResult("The database function returned multiple results.");
        }

        return result;
    }

    private static AccountHealthTransitionResult ToTransitionResult(
        FunctionResult result,
        AccountHealthTransition transition)
    {
        ValidateComplete(result);
        AccountHealthTransitionDisposition disposition =
            ParseDisposition(result.Disposition);
        AccountHealthState before = new(
            ParseHealth(result.BeforeHealth!),
            result.BeforeRetryAt,
            result.BeforeObservedAt,
            result.BeforeVersion!.Value);
        AccountHealthState current = new(
            ParseHealth(result.CurrentHealth!),
            result.CurrentRetryAt,
            result.CurrentObservedAt,
            result.CurrentVersion!.Value);

        if (!IsValidResult(
                result,
                transition,
                disposition,
                before,
                current))
        {
            throw InvalidResult("The database function returned an invalid state.");
        }

        return new AccountHealthTransitionResult(
            disposition,
            result.WasChanged,
            before,
            current);
    }

    private static void ValidateComplete(FunctionResult result)
    {
        if (result.BeforeHealth is null
            || result.BeforeVersion is null
            || result.CurrentHealth is null
            || result.CurrentVersion is null
            || result.AccountStatus is null
            || result.CredentialRevision is null
            || result.BeforeVersion <= 0
            || result.CurrentVersion <= 0
            || result.CredentialRevision <= 0)
        {
            throw InvalidResult(
                "The database function returned an incomplete state.");
        }
    }

    private static AccountHealthTransitionDisposition ParseDisposition(
        string disposition) =>
        disposition switch
        {
            "applied" => AccountHealthTransitionDisposition.Applied,
            "duplicate" => AccountHealthTransitionDisposition.Duplicate,
            "stale_observation" =>
                AccountHealthTransitionDisposition.StaleObservation,
            "account_retired" =>
                AccountHealthTransitionDisposition.AccountRetired,
            _ => throw InvalidResult(
                "The database function returned an unknown disposition."),
        };

    private static bool IsValidResult(
        FunctionResult result,
        AccountHealthTransition transition,
        AccountHealthTransitionDisposition disposition,
        AccountHealthState before,
        AccountHealthState current)
    {
        bool sameState = before == current;
        return disposition switch
        {
            AccountHealthTransitionDisposition.Applied =>
                result.WasChanged
                && result.BeforeVersion == transition.ExpectedAccountVersion
                && result.CredentialRevision
                    == transition.ExpectedCredentialRevision
                && result.CurrentVersion == checked(result.BeforeVersion + 1)
                && Matches(current, transition)
                && !string.Equals(
                    result.AccountStatus,
                    "retired",
                    StringComparison.Ordinal),
            AccountHealthTransitionDisposition.Duplicate =>
                !result.WasChanged
                && result.CurrentVersion == result.BeforeVersion
                && result.BeforeVersion == transition.ExpectedAccountVersion
                && result.CredentialRevision
                    == transition.ExpectedCredentialRevision
                && before.Health == current.Health
                && before.RetryAt == current.RetryAt
                && before.ObservedAt <= current.ObservedAt
                && Matches(current, transition),
            AccountHealthTransitionDisposition.StaleObservation =>
                !result.WasChanged && sameState,
            AccountHealthTransitionDisposition.AccountRetired =>
                !result.WasChanged
                && sameState
                && string.Equals(
                    result.AccountStatus,
                    "retired",
                    StringComparison.Ordinal),
            _ => false,
        };
    }

    private static bool Matches(
        AccountHealthState state,
        AccountHealthTransition transition) =>
        state.Health == transition.Health
        && state.ObservedAt == transition.ObservedAt
        && state.RetryAt == transition.RetryAt;

    private static void ValidateEmpty(FunctionResult result)
    {
        if (result.WasChanged
            || result.BeforeHealth is not null
            || result.BeforeRetryAt is not null
            || result.BeforeObservedAt is not null
            || result.BeforeVersion is not null
            || result.CurrentHealth is not null
            || result.CurrentRetryAt is not null
            || result.CurrentObservedAt is not null
            || result.CurrentVersion is not null
            || result.AccountStatus is not null
            || result.CredentialRevision is not null)
        {
            throw InvalidResult("The database function returned state for a missing Account.");
        }
    }

    private static AuditEntry CreateAuditEntry(
        AccountHealthTransition transition,
        AccountHealthTransitionResult result) =>
        new(
            EntityId.New(),
            AuditActorType.Service,
            ActorUserId: null,
            AuditAction,
            TargetType: "account",
            transition.AccountId,
            RequestId: null,
            Reason: null,
            IpAddress: null,
            UserAgent: null,
            BeforeState: AuditState(result.Before),
            AfterState: AuditState(result.Current),
            JsonSerializer.SerializeToElement(new
            {
                observation_source = "canonical_health",
                observed_at = transition.ObservedAt,
                retry_at = transition.RetryAt,
            }));

    private static JsonElement AuditState(AccountHealthState state) =>
        JsonSerializer.SerializeToElement(new
        {
            health = HealthCode(state.Health),
            retry_at = state.RetryAt,
            observed_at = state.ObservedAt,
            version = state.Version,
        });

    private static string HealthCode(AccountHealth health) => health switch
    {
        AccountHealth.Unknown => "unknown",
        AccountHealth.Healthy => "healthy",
        AccountHealth.Degraded => "degraded",
        AccountHealth.Cooling => "cooling",
        AccountHealth.Unhealthy => "unhealthy",
        _ => throw new ArgumentOutOfRangeException(nameof(health)),
    };

    private static AccountHealth ParseHealth(string health) => health switch
    {
        "unknown" => AccountHealth.Unknown,
        "healthy" => AccountHealth.Healthy,
        "degraded" => AccountHealth.Degraded,
        "cooling" => AccountHealth.Cooling,
        "unhealthy" => AccountHealth.Unhealthy,
        _ => throw InvalidResult(
            "The database function returned an unknown health status."),
    };

    private static string? NullableText(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? NullableTimestamp(
        NpgsqlDataReader reader,
        int ordinal) => reader.IsDBNull(ordinal)
            ? null
            : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static long? NullableInt64(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static InvalidOperationException InvalidResult(string message) =>
        new($"{FunctionName}: {message}");

    private sealed record FunctionResult(
        string Disposition,
        bool WasChanged,
        string? BeforeHealth,
        DateTimeOffset? BeforeRetryAt,
        DateTimeOffset? BeforeObservedAt,
        long? BeforeVersion,
        string? CurrentHealth,
        DateTimeOffset? CurrentRetryAt,
        DateTimeOffset? CurrentObservedAt,
        long? CurrentVersion,
        string? AccountStatus,
        long? CredentialRevision);
}
