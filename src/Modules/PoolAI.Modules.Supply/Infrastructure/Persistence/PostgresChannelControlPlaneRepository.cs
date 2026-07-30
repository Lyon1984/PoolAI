#pragma warning disable MA0051 // The Channel function-call protocol remains visible in one adapter.
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Application.Ports;
using PoolAI.Modules.Supply.Domain;

namespace PoolAI.Modules.Supply.Infrastructure.Persistence;

internal sealed partial class PostgresChannelControlPlaneRepository(
    NpgsqlDataSource dataSource) : IChannelControlPlaneRepository
{
    internal const string CreateFunctionName =
        "public.poolai_supply_create_channel";
    internal const string UpdateFunctionName =
        "public.poolai_supply_update_channel";
    internal const string RetireFunctionName =
        "public.poolai_supply_retire_channel";

    private const string CreateSavepoint = "supply_channel_create_call";
    private const string UpdateSavepoint = "supply_channel_update_call";
    private const string RetireSavepoint = "supply_channel_retire_call";

    private static readonly string CreateSql = $"""
        SELECT disposition,
               was_changed,
               before_state::text,
               current_version
        FROM {CreateFunctionName}($1, $2, $3, $4::jsonb, $5::jsonb);
        """;

    private static readonly string UpdateSql = $"""
        SELECT disposition,
               was_changed,
               before_state::text,
               current_version
        FROM {UpdateFunctionName}(
            $1, $2, $3, $4, $5, $6, $7, $8::jsonb, $9, $10::jsonb, $11
        );
        """;

    private static readonly string RetireSql = $"""
        SELECT disposition,
               was_changed,
               before_state::text,
               current_version
        FROM {RetireFunctionName}($1, $2, $3);
        """;

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public ValueTask<ChannelMutationResult> CreateAsync(
        ChannelCreateWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        return ExecuteMutationAsync(
            write.ChannelId,
            CreateSql,
            CreateSavepoint,
            isCreate: true,
            isRetire: false,
            static (command, state) =>
            {
                ChannelCreateWrite create = (ChannelCreateWrite)state;
                command.Parameters.AddWithValue(create.ChannelId.Value);
                command.Parameters.AddWithValue(ProviderCode(create.Provider));
                command.Parameters.AddWithValue(create.Name);
                command.Parameters.Add(
                    new NpgsqlParameter
                    {
                        NpgsqlDbType = NpgsqlDbType.Jsonb,
                        Value = SerializeModelMappings(create.ModelMappings),
                    });
                command.Parameters.Add(
                    new NpgsqlParameter
                    {
                        NpgsqlDbType = NpgsqlDbType.Jsonb,
                        Value = SerializeCapabilities(create.Capabilities),
                    });
            },
            write,
            unitOfWorkContext,
            cancellationToken);
    }

    public ValueTask<ChannelMutationResult> UpdateAsync(
        ChannelUpdateWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        return ExecuteMutationAsync(
            write.ChannelId,
            UpdateSql,
            UpdateSavepoint,
            isCreate: false,
            isRetire: false,
            static (command, state) =>
            {
                ChannelUpdateWrite update = (ChannelUpdateWrite)state;
                command.Parameters.AddWithValue(update.ChannelId.Value);
                command.Parameters.AddWithValue(update.ExpectedVersion);
                command.Parameters.AddWithValue(update.NameSpecified);
                GroupSupplyPersistenceProtocol.AddNullableText(
                    command.Parameters,
                    update.Name);
                command.Parameters.AddWithValue(update.StatusSpecified);
                GroupSupplyPersistenceProtocol.AddNullableText(
                    command.Parameters,
                    update.Status is null
                        ? null
                        : StatusCode(update.Status.Value));
                command.Parameters.AddWithValue(update.ModelMappingsSpecified);
                GroupSupplyPersistenceProtocol.AddNullableJson(
                    command.Parameters,
                    update.ModelMappings is null
                        ? null
                        : SerializeModelMappings(update.ModelMappings));
                command.Parameters.AddWithValue(update.CapabilitiesSpecified);
                GroupSupplyPersistenceProtocol.AddNullableJson(
                    command.Parameters,
                    update.Capabilities is null
                        ? null
                        : SerializeCapabilities(update.Capabilities));
                GroupSupplyPersistenceProtocol.AddNullableText(
                    command.Parameters,
                    update.Reason);
            },
            write,
            unitOfWorkContext,
            cancellationToken);
    }

    public ValueTask<ChannelMutationResult> RetireAsync(
        ChannelRetireWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        return ExecuteMutationAsync(
            write.ChannelId,
            RetireSql,
            RetireSavepoint,
            isCreate: false,
            isRetire: true,
            static (command, state) =>
            {
                ChannelRetireWrite retire = (ChannelRetireWrite)state;
                command.Parameters.AddWithValue(retire.ChannelId.Value);
                command.Parameters.AddWithValue(retire.ExpectedVersion);
                command.Parameters.AddWithValue(retire.Reason);
            },
            write,
            unitOfWorkContext,
            cancellationToken);
    }

    private static async ValueTask<ChannelMutationResult> ExecuteMutationAsync(
        EntityId channelId,
        string sql,
        string savepoint,
        bool isCreate,
        bool isRetire,
        Action<NpgsqlCommand, object> bind,
        object state,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        PostgresTransactionSession session =
            PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        await GroupSupplyPersistenceProtocol.BeginSavepointAsync(
            session,
            savepoint,
            cancellationToken).ConfigureAwait(false);
        try
        {
            SupplyMutationFunctionResult functionResult;
            using (NpgsqlCommand command = session.CreateCommand(sql))
            {
                bind(command, state);
                functionResult = await GroupSupplyPersistenceProtocol
                    .ReadMutationAsync(
                        command,
                        "Channel",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            ChannelMutationResult result = await ToMutationResultAsync(
                functionResult,
                channelId,
                isCreate,
                isRetire,
                session,
                cancellationToken).ConfigureAwait(false);
            await GroupSupplyPersistenceProtocol.ReleaseSavepointAsync(
                session,
                savepoint,
                cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (PostgresException exception) when (IsKnownMutationFailure(exception))
        {
            await GroupSupplyPersistenceProtocol.RollbackAndReleaseSavepointAsync(
                session,
                savepoint,
                cancellationToken).ConfigureAwait(false);
            return new ChannelMutationResult(
                MapException(exception, isRetire),
                WasChanged: false,
                Value: null,
                Before: null);
        }
    }

    private static async ValueTask<ChannelMutationResult> ToMutationResultAsync(
        SupplyMutationFunctionResult result,
        EntityId channelId,
        bool isCreate,
        bool isRetire,
        PostgresTransactionSession session,
        CancellationToken cancellationToken)
    {
        ChannelMutationDisposition disposition = MapDisposition(
            result.Disposition,
            isCreate,
            isRetire);
        ChannelResource? before = ParseBeforeState(result.BeforeState);
        if (disposition != ChannelMutationDisposition.Written)
        {
            return new ChannelMutationResult(
                disposition,
                result.WasChanged,
                Value: null,
                before,
                result.CurrentVersion);
        }

        ChannelResource value = await GetRequiredAsync(
            channelId,
            session,
            cancellationToken).ConfigureAwait(false);
        if (result.CurrentVersion != value.Version
            || isCreate == (before is not null)
            || isRetire
                && (!result.WasChanged
                    || value.Status != ChannelResourceStatus.Retired))
        {
            throw new InvalidOperationException(
                "The Channel mutation result is internally inconsistent.");
        }

        return new ChannelMutationResult(
            ChannelMutationDisposition.Written,
            result.WasChanged,
            value,
            before,
            result.CurrentVersion);
    }

    private static ChannelMutationDisposition MapDisposition(
        string disposition,
        bool isCreate,
        bool isRetire) => disposition switch
        {
            "created" when isCreate => ChannelMutationDisposition.Written,
            "updated" when !isCreate && !isRetire =>
                ChannelMutationDisposition.Written,
            "retired" when isRetire => ChannelMutationDisposition.Written,
            "validation_failed" => ChannelMutationDisposition.ValidationFailed,
            "conflict" => ChannelMutationDisposition.Conflict,
            "not_found" => ChannelMutationDisposition.NotFound,
            "version_conflict" => ChannelMutationDisposition.VersionConflict,
            "channel_retired" or "invalid_transition" =>
                ChannelMutationDisposition.LifecycleConflict,
            "channel_in_use" when isRetire =>
                ChannelMutationDisposition.ChannelInUse,
            _ => throw new InvalidOperationException(
                "The Channel database function returned an unknown disposition."),
        };

    private static ChannelMutationDisposition MapException(
        PostgresException exception,
        bool isRetire)
    {
        if (isRetire
            && IsRaised(exception, "channel_in_use"))
        {
            return ChannelMutationDisposition.ChannelInUse;
        }

        if (IsRaised(exception, "channel_retired")
            || IsRaised(exception, "invalid_transition"))
        {
            return ChannelMutationDisposition.LifecycleConflict;
        }

        return string.Equals(
            exception.SqlState,
            PostgresErrorCodes.UniqueViolation,
            StringComparison.Ordinal)
            ? ChannelMutationDisposition.Conflict
            : ChannelMutationDisposition.ValidationFailed;
    }

    private static bool IsRaised(
        PostgresException exception,
        string message) =>
        string.Equals(exception.SqlState, "P0001", StringComparison.Ordinal)
        && string.Equals(exception.MessageText, message, StringComparison.Ordinal);

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

    private static string SerializeCapabilities(
        ChannelCapabilitiesValue value) => JsonSerializer.Serialize(new
        {
            responses = value.Responses,
            chat_completions = value.ChatCompletions,
            function_tools = value.FunctionTools,
            streaming = value.Streaming,
        });

    private static string SerializeModelMappings(
        IReadOnlyList<ChannelModelMappingValue> values)
    {
        SortedDictionary<string, string> mappings =
            new(StringComparer.Ordinal);
        foreach (ChannelModelMappingValue value in values)
        {
            if (!mappings.TryAdd(value.ClientModel, value.UpstreamModel))
            {
                throw new InvalidOperationException(
                    "The Channel model mappings are not canonical.");
            }
        }

        return JsonSerializer.Serialize(mappings);
    }

    private static string ProviderCode(UpstreamProvider provider) => provider switch
    {
        UpstreamProvider.OpenAi => "openai",
        UpstreamProvider.OpenAiCompatible => "openai_compatible",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    private static string StatusCode(ChannelResourceStatus status) => status switch
    {
        ChannelResourceStatus.Active => "active",
        ChannelResourceStatus.Disabled => "disabled",
        ChannelResourceStatus.Retired => "retired",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
#pragma warning restore MA0051
