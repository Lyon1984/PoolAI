#pragma warning disable MA0051 // SQL entry points keep authorization-relevant filters visible.
using System.Runtime.CompilerServices;
using Npgsql;
using NpgsqlTypes;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.SubscriptionAccess.Application;
using PoolAI.Modules.SubscriptionAccess.Application.Ports;
using PoolAI.Modules.SubscriptionAccess.Domain;

namespace PoolAI.Modules.SubscriptionAccess.Infrastructure.Persistence;

internal sealed partial class PostgresSubscriptionRepository : ISubscriptionRepository
{
    private const string TemplateColumns = """
        template.id,
        template.group_id,
        template.name,
        template.description,
        template.default_duration_days,
        template.status,
        template.version,
        template.created_at,
        template.updated_at
        """;

    private const string SubscriptionColumns = """
        subscription.id,
        subscription.user_id,
        subscription.group_id,
        subscription.template_id,
        subscription.template_name_snapshot,
        subscription.starts_at,
        subscription.expires_at,
        subscription.status,
        CASE
            WHEN subscription.status = 'suspended' THEN 'suspended'
            WHEN subscription.status = 'revoked' THEN 'revoked'
            WHEN subscription.starts_at > observed.at THEN 'scheduled'
            WHEN subscription.expires_at <= observed.at THEN 'expired'
            ELSE 'active'
        END AS effective_status,
        subscription.assigned_by,
        subscription.version,
        subscription.created_at,
        subscription.updated_at,
        observed.at AS observed_at
        """;

    private static readonly string ListTemplatesSql = $"""
        SELECT {TemplateColumns}
        FROM public.subscription_templates AS template
        WHERE ($1::timestamptz IS NULL)
           OR template.created_at < $1
           OR (template.created_at = $1 AND template.id < $2)
        ORDER BY template.created_at DESC, template.id DESC
        LIMIT $3;
        """;

    private static readonly string GetTemplateSql = $"""
        SELECT {TemplateColumns}
        FROM public.subscription_templates AS template
        WHERE template.id = $1;
        """;

    private static readonly string ListSubscriptionsSql = $"""
        WITH observed AS MATERIALIZED (
            SELECT clock_timestamp() AS at
        )
        SELECT {SubscriptionColumns}
        FROM public.subscriptions AS subscription
        CROSS JOIN observed
        WHERE ($1::uuid IS NULL OR subscription.user_id = $1)
          AND ($2::uuid IS NULL OR subscription.group_id = $2)
          AND (
              $3::timestamptz IS NULL
              OR subscription.created_at < $3
              OR (subscription.created_at = $3 AND subscription.id < $4)
          )
        ORDER BY subscription.created_at DESC, subscription.id DESC
        LIMIT $5;
        """;

    private static readonly string GetSubscriptionSql = $"""
        WITH observed AS MATERIALIZED (
            SELECT clock_timestamp() AS at
        )
        SELECT {SubscriptionColumns}
        FROM public.subscriptions AS subscription
        CROSS JOIN observed
        WHERE subscription.id = $1
          AND ($2::uuid IS NULL OR subscription.user_id = $2);
        """;

    private static readonly string GetEffectiveAccessSql = $"""
        WITH observed AS MATERIALIZED (
            SELECT clock_timestamp() AS at
        )
        SELECT {SubscriptionColumns}
        FROM public.subscriptions AS subscription
        CROSS JOIN observed
        WHERE subscription.user_id = $1
          AND subscription.group_id = $2
          AND subscription.status = 'active'
          AND subscription.starts_at <= observed.at
          AND subscription.expires_at > observed.at;
        """;

    private static readonly string ListForUserSql = $"""
        WITH observed AS MATERIALIZED (
            SELECT clock_timestamp() AS at
        )
        SELECT {SubscriptionColumns}
        FROM public.subscriptions AS subscription
        CROSS JOIN observed
        WHERE subscription.user_id = $1
        ORDER BY subscription.created_at DESC, subscription.id DESC;
        """;

    private static readonly string ListActiveForUserSql = $"""
        WITH observed AS MATERIALIZED (
            SELECT clock_timestamp() AS at
        )
        SELECT {SubscriptionColumns}
        FROM public.subscriptions AS subscription
        CROSS JOIN observed
        WHERE subscription.user_id = $1
          AND subscription.status = 'active'
          AND subscription.starts_at <= observed.at
          AND subscription.expires_at > observed.at
        ORDER BY subscription.group_id, subscription.id;
        """;

    private const string CreateTemplateSql = """
        SELECT disposition, was_changed
        FROM public.poolai_subscription_template_create($1, $2, $3, $4, $5);
        """;

    private const string UpdateTemplateSql = """
        SELECT disposition, was_changed, before_state::text, current_version
        FROM public.poolai_subscription_template_update(
            $1, $2, $3, $4, $5, $6, $7, $8, $9, $10);
        """;

    private const string RetireTemplateSql = """
        SELECT disposition, was_changed, before_state::text, current_version
        FROM public.poolai_subscription_template_retire($1, $2, $3);
        """;

    private const string AssignSubscriptionSql = """
        SELECT disposition, was_changed
        FROM public.poolai_subscription_assign($1, $2, $3, $4, $5, $6, $7);
        """;

    private const string UpdateSubscriptionSql = """
        SELECT disposition, was_changed, before_state::text, current_version
        FROM public.poolai_subscription_update(
            $1, $2, $3, $4, $5, $6, $7, $8, $9, $10);
        """;

    private readonly NpgsqlDataSource _dataSource;

    internal PostgresSubscriptionRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async ValueTask<TemplateMutationResult> CreateTemplateAsync(
        EntityId templateId,
        EntityId groupId,
        string name,
        string? description,
        int defaultDurationDays,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(CreateTemplateSql);
        command.Parameters.AddWithValue(templateId.Value);
        command.Parameters.AddWithValue(groupId.Value);
        command.Parameters.AddWithValue(name);
        AddNullableText(command.Parameters, description);
        command.Parameters.AddWithValue(defaultDurationDays);
        MutationEnvelope mutation = await ReadMutationAsync(
            command,
            hasBeforeState: false,
            cancellationToken).ConfigureAwait(false);
        SubscriptionMutationDisposition disposition = ParseDisposition(mutation.Disposition);
        SubscriptionTemplateRecord? value = disposition == SubscriptionMutationDisposition.Updated
            ? await GetTemplateAsync(session, templateId, cancellationToken).ConfigureAwait(false)
            : null;
        return new TemplateMutationResult(
            disposition,
            mutation.Changed,
            value,
            null,
            mutation.CurrentVersion);
    }

    public async ValueTask<TemplateMutationResult> UpdateTemplateAsync(
        EntityId templateId,
        long expectedVersion,
        bool nameSpecified,
        string? name,
        bool descriptionSpecified,
        string? description,
        bool durationSpecified,
        int? durationDays,
        bool statusSpecified,
        SubscriptionTemplateLifecycle? status,
        bool retire,
        string? reason,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(retire ? RetireTemplateSql : UpdateTemplateSql);
        command.Parameters.AddWithValue(templateId.Value);
        command.Parameters.AddWithValue(expectedVersion);
        if (retire)
        {
            command.Parameters.AddWithValue(reason!);
        }
        else
        {
            command.Parameters.AddWithValue(nameSpecified);
            AddNullableText(command.Parameters, name);
            command.Parameters.AddWithValue(descriptionSpecified);
            AddNullableText(command.Parameters, description);
            command.Parameters.AddWithValue(durationSpecified);
            AddNullableInteger(command.Parameters, durationDays);
            AddNullableText(command.Parameters, statusSpecified ? TemplateStatusCode(status) : null);
            AddNullableText(command.Parameters, reason);
        }

        MutationEnvelope mutation = await ReadMutationAsync(
            command,
            hasBeforeState: true,
            cancellationToken).ConfigureAwait(false);
        SubscriptionMutationDisposition disposition = ParseDisposition(mutation.Disposition);
        SubscriptionTemplateRecord? value = disposition == SubscriptionMutationDisposition.Updated
            ? await GetTemplateAsync(session, templateId, cancellationToken).ConfigureAwait(false)
            : null;
        return new TemplateMutationResult(
            disposition,
            mutation.Changed,
            value,
            mutation.BeforeState,
            mutation.CurrentVersion);
    }

    public async ValueTask<SubscriptionMutationResult> AssignSubscriptionAsync(
        EntityId subscriptionId,
        EntityId userId,
        EntityId templateId,
        DateTimeOffset? startsAt,
        DateTimeOffset? expiresAt,
        EntityId assignedBy,
        string reason,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(AssignSubscriptionSql);
        command.Parameters.AddWithValue(subscriptionId.Value);
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(templateId.Value);
        AddNullableTimestamp(command.Parameters, startsAt);
        AddNullableTimestamp(command.Parameters, expiresAt);
        command.Parameters.AddWithValue(assignedBy.Value);
        command.Parameters.AddWithValue(reason);
        MutationEnvelope mutation = await ReadMutationAsync(
            command,
            hasBeforeState: false,
            cancellationToken).ConfigureAwait(false);
        SubscriptionMutationDisposition disposition = ParseDisposition(mutation.Disposition);
        SubscriptionRecord? value = disposition == SubscriptionMutationDisposition.Updated
            ? await GetSubscriptionAsync(session, subscriptionId, cancellationToken).ConfigureAwait(false)
            : null;
        return new SubscriptionMutationResult(
            disposition,
            mutation.Changed,
            value,
            null,
            mutation.CurrentVersion);
    }

    public async ValueTask<SubscriptionMutationResult> UpdateSubscriptionAsync(
        EntityId subscriptionId,
        long expectedVersion,
        bool startsAtSpecified,
        DateTimeOffset? startsAt,
        bool expiresAtSpecified,
        DateTimeOffset? expiresAt,
        bool statusSpecified,
        SubscriptionLifecycle? status,
        bool allowRevokedRegrant,
        EntityId actorId,
        string reason,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(UpdateSubscriptionSql);
        command.Parameters.AddWithValue(subscriptionId.Value);
        command.Parameters.AddWithValue(expectedVersion);
        command.Parameters.AddWithValue(startsAtSpecified);
        AddNullableTimestamp(command.Parameters, startsAt);
        command.Parameters.AddWithValue(expiresAtSpecified);
        AddNullableTimestamp(command.Parameters, expiresAt);
        AddNullableText(command.Parameters, statusSpecified ? SubscriptionStatusCode(status) : null);
        command.Parameters.AddWithValue(allowRevokedRegrant);
        command.Parameters.AddWithValue(actorId.Value);
        command.Parameters.AddWithValue(reason);
        MutationEnvelope mutation = await ReadMutationAsync(
            command,
            hasBeforeState: true,
            cancellationToken).ConfigureAwait(false);
        SubscriptionMutationDisposition disposition = ParseDisposition(mutation.Disposition);
        SubscriptionRecord? value = disposition == SubscriptionMutationDisposition.Updated
            ? await GetSubscriptionAsync(session, subscriptionId, cancellationToken).ConfigureAwait(false)
            : null;
        return new SubscriptionMutationResult(
            disposition,
            mutation.Changed,
            value,
            mutation.BeforeState,
            mutation.CurrentVersion);
    }

    private static async ValueTask<SubscriptionTemplateRecord?> GetTemplateAsync(
        PostgresTransactionSession session,
        EntityId templateId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand(GetTemplateSql);
        command.Parameters.AddWithValue(templateId.Value);
        return await ReadTemplateSingleAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<SubscriptionRecord?> GetSubscriptionAsync(
        PostgresTransactionSession session,
        EntityId subscriptionId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand(GetSubscriptionSql);
        command.Parameters.AddWithValue(subscriptionId.Value);
        AddNullableUuid(command.Parameters, null);
        return await ReadSubscriptionSingleAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<MutationEnvelope> ReadMutationAsync(
        NpgsqlCommand command,
        bool hasBeforeState,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The control-plane function returned no disposition.");
        }

        string disposition = reader.GetString(0);
        bool changed = reader.GetBoolean(1);
        JsonElement? beforeState = null;
        long? currentVersion = null;
        if (hasBeforeState)
        {
            if (!reader.IsDBNull(2))
            {
                using JsonDocument document = JsonDocument.Parse(reader.GetString(2));
                beforeState = document.RootElement.Clone();
            }

            currentVersion = reader.IsDBNull(3) ? null : reader.GetInt64(3);
        }

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The control-plane function returned multiple dispositions.");
        }

        return new MutationEnvelope(disposition, changed, beforeState, currentVersion);
    }

    private static async ValueTask<SubscriptionTemplateRecord?> ReadTemplateSingleAsync(
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

        SubscriptionTemplateRecord value = ReadTemplate(reader);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Template identity is not unique.");
        }

        return value;
    }

    private static async ValueTask<SubscriptionRecord?> ReadSubscriptionSingleAsync(
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

        SubscriptionRecord value = ReadSubscription(reader);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Subscription identity is not unique.");
        }

        return value;
    }

    private static SubscriptionTemplateRecord ReadTemplate(NpgsqlDataReader reader) => new(
        new EntityId(reader.GetGuid(0)),
        new EntityId(reader.GetGuid(1)),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetInt32(4),
        ParseTemplateStatus(reader.GetString(5)),
        reader.GetInt64(6),
        reader.GetFieldValue<DateTimeOffset>(7),
        reader.GetFieldValue<DateTimeOffset>(8));

    private static SubscriptionRecord ReadSubscription(NpgsqlDataReader reader) => new(
        new EntityId(reader.GetGuid(0)),
        new EntityId(reader.GetGuid(1)),
        new EntityId(reader.GetGuid(2)),
        new EntityId(reader.GetGuid(3)),
        reader.GetString(4),
        reader.GetFieldValue<DateTimeOffset>(5),
        reader.GetFieldValue<DateTimeOffset>(6),
        ParseSubscriptionStatus(reader.GetString(7)),
        ParseEffectiveStatus(reader.GetString(8)),
        new EntityId(reader.GetGuid(9)),
        reader.GetInt64(10),
        reader.GetFieldValue<DateTimeOffset>(11),
        reader.GetFieldValue<DateTimeOffset>(12),
        reader.GetFieldValue<DateTimeOffset>(13));

    private static SubscriptionMutationDisposition ParseDisposition(string value) => value switch
    {
        "created" or "updated" or "retired" => SubscriptionMutationDisposition.Updated,
        "not_found" => SubscriptionMutationDisposition.NotFound,
        "version_conflict" => SubscriptionMutationDisposition.VersionConflict,
        "conflict" => SubscriptionMutationDisposition.ResourceConflict,
        "group_archived" => SubscriptionMutationDisposition.GroupArchived,
        "group_disabled" => SubscriptionMutationDisposition.GroupDisabled,
        "template_disabled" => SubscriptionMutationDisposition.TemplateDisabled,
        "canonical_conflict" or "subscription_conflict" =>
            SubscriptionMutationDisposition.CanonicalConflict,
        "invalid_transition" or "validation_failed" =>
            SubscriptionMutationDisposition.InvalidTransition,
        _ => throw new InvalidOperationException($"Unknown mutation disposition '{value}'."),
    };

    private static SubscriptionTemplateLifecycle ParseTemplateStatus(string value) => value switch
    {
        "active" => SubscriptionTemplateLifecycle.Active,
        "disabled" => SubscriptionTemplateLifecycle.Disabled,
        "retired" => SubscriptionTemplateLifecycle.Retired,
        _ => throw new InvalidOperationException($"Unknown Template status '{value}'."),
    };

    private static SubscriptionLifecycle ParseSubscriptionStatus(string value) => value switch
    {
        "active" => SubscriptionLifecycle.Active,
        "suspended" => SubscriptionLifecycle.Suspended,
        "revoked" => SubscriptionLifecycle.Revoked,
        _ => throw new InvalidOperationException($"Unknown Subscription status '{value}'."),
    };

    private static SubscriptionEffectiveLifecycle ParseEffectiveStatus(string value) => value switch
    {
        "scheduled" => SubscriptionEffectiveLifecycle.Scheduled,
        "active" => SubscriptionEffectiveLifecycle.Active,
        "expired" => SubscriptionEffectiveLifecycle.Expired,
        "suspended" => SubscriptionEffectiveLifecycle.Suspended,
        "revoked" => SubscriptionEffectiveLifecycle.Revoked,
        _ => throw new InvalidOperationException($"Unknown effective status '{value}'."),
    };

    private static string? TemplateStatusCode(SubscriptionTemplateLifecycle? value) => value switch
    {
        SubscriptionTemplateLifecycle.Active => "active",
        SubscriptionTemplateLifecycle.Disabled => "disabled",
        null => null,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string? SubscriptionStatusCode(SubscriptionLifecycle? value) => value switch
    {
        SubscriptionLifecycle.Active => "active",
        SubscriptionLifecycle.Suspended => "suspended",
        SubscriptionLifecycle.Revoked => "revoked",
        null => null,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static void ValidateLimit(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);
    }

    private static void AddNullableUuid(NpgsqlParameterCollection parameters, Guid? value) =>
        parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = value ?? (object)DBNull.Value });

    private static void AddNullableTimestamp(
        NpgsqlParameterCollection parameters,
        DateTimeOffset? value) => parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.TimestampTz,
            Value = value?.ToUniversalTime() ?? (object)DBNull.Value,
        });

    private static void AddNullableText(NpgsqlParameterCollection parameters, string? value) =>
        parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = value ?? (object)DBNull.Value });

    private static void AddNullableInteger(NpgsqlParameterCollection parameters, int? value) =>
        parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = value ?? (object)DBNull.Value });

    private sealed record MutationEnvelope(
        string Disposition,
        bool Changed,
        JsonElement? BeforeState,
        long? CurrentVersion);
}
#pragma warning restore MA0051
