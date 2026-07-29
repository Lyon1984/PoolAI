#pragma warning disable MA0051 // The Configuration function-call protocol remains visible in one adapter.
using System.Text.Json;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Application.Ports;
using PoolAI.Modules.Supply.Domain;

namespace PoolAI.Modules.Supply.Infrastructure.Persistence;

internal sealed partial class PostgresGroupSupplyConfigurationRepository(
    NpgsqlDataSource dataSource) :
    IGroupSupplyConfigurationRepository,
    IGroupSupplyConfigurationReader
{
    internal const string CreateFunctionName =
        "public.poolai_supply_create_group_configuration";
    internal const string PatchFunctionName =
        "public.poolai_supply_patch_group_configuration";

    private const string CreateSavepoint = "supply_group_config_create_call";
    private const string PatchSavepoint = "supply_group_config_patch_call";

    private static readonly string CreateSql = $"""
        SELECT disposition,
               was_changed,
               before_state::text,
               current_version
        FROM {CreateFunctionName}($1, $2, $3, $4, $5, $6);
        """;

    private static readonly string PatchSql = $"""
        SELECT disposition,
               was_changed,
               before_state::text,
               current_version
        FROM {PatchFunctionName}(
            $1, $2, $3, $4, $5, $6, $7, $8, $9, $10
        );
        """;

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async ValueTask<Result<GroupSupplyConfigurationSnapshot>> GetCurrentAsync(
        EntityId groupId,
        CancellationToken cancellationToken)
    {
        GroupSupplyConfigurationResource? configuration = await GetAsync(
            groupId,
            cancellationToken).ConfigureAwait(false);
        return configuration is null
            ? Result.Failure<GroupSupplyConfigurationSnapshot>(
                "resource_not_found",
                "The Group Supply Configuration does not exist.")
            : Result.Success(ToSnapshot(configuration));
    }

    public ValueTask<GroupSupplyMutationResult> CreateAsync(
        GroupSupplyConfigurationCreateWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        return ExecuteMutationAsync(
            write.GroupId,
            CreateSql,
            CreateSavepoint,
            isCreate: true,
            static (command, state) =>
            {
                GroupSupplyConfigurationCreateWrite create =
                    (GroupSupplyConfigurationCreateWrite)state;
                command.Parameters.AddWithValue(create.GroupId.Value);
                GroupSupplyPersistenceProtocol.AddNullableUuid(
                    command.Parameters,
                    create.ChannelId?.Value);
                AddBindings(command, create.AccountBindings);
            },
            write,
            unitOfWorkContext,
            cancellationToken);
    }

    public ValueTask<GroupSupplyMutationResult> PatchAsync(
        GroupSupplyConfigurationPatchWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        return ExecuteMutationAsync(
            write.GroupId,
            PatchSql,
            PatchSavepoint,
            isCreate: false,
            static (command, state) =>
            {
                GroupSupplyConfigurationPatchWrite patch =
                    (GroupSupplyConfigurationPatchWrite)state;
                command.Parameters.AddWithValue(patch.GroupId.Value);
                command.Parameters.AddWithValue(patch.ExpectedVersion);
                command.Parameters.AddWithValue(patch.ChannelSpecified);
                GroupSupplyPersistenceProtocol.AddNullableUuid(
                    command.Parameters,
                    patch.ChannelId?.Value);
                command.Parameters.AddWithValue(patch.AccountBindingsSpecified);
                AddBindings(
                    command,
                    patch.AccountBindingsSpecified
                        ? patch.AccountBindings
                            ?? throw new InvalidOperationException(
                                "Specified bindings cannot be null.")
                        : null);
                command.Parameters.AddWithValue(patch.Reason);
            },
            write,
            unitOfWorkContext,
            cancellationToken);
    }

    private static async ValueTask<GroupSupplyMutationResult> ExecuteMutationAsync(
        EntityId groupId,
        string sql,
        string savepoint,
        bool isCreate,
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
                        "Group Supply Configuration",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            GroupSupplyMutationResult result = await ToMutationResultAsync(
                functionResult,
                groupId,
                isCreate,
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
            return new GroupSupplyMutationResult(
                MapException(exception),
                WasChanged: false,
                Value: null,
                Before: null);
        }
    }

    private static async ValueTask<GroupSupplyMutationResult> ToMutationResultAsync(
        SupplyMutationFunctionResult result,
        EntityId groupId,
        bool isCreate,
        PostgresTransactionSession session,
        CancellationToken cancellationToken)
    {
        GroupSupplyMutationDisposition disposition = MapDisposition(
            result.Disposition,
            isCreate);
        GroupSupplyConfigurationResource? before =
            ParseBeforeState(result.BeforeState);
        if (disposition != GroupSupplyMutationDisposition.Written)
        {
            return new GroupSupplyMutationResult(
                disposition,
                result.WasChanged,
                Value: null,
                before,
                result.CurrentVersion);
        }

        GroupSupplyConfigurationResource value = await GetRequiredAsync(
            groupId,
            session,
            cancellationToken).ConfigureAwait(false);
        if (result.CurrentVersion != value.Version
            || isCreate == (before is not null))
        {
            throw new InvalidOperationException(
                "The Group Supply Configuration mutation result is internally inconsistent.");
        }

        return new GroupSupplyMutationResult(
            GroupSupplyMutationDisposition.Written,
            result.WasChanged,
            value,
            before,
            result.CurrentVersion);
    }

    private static GroupSupplyMutationDisposition MapDisposition(
        string disposition,
        bool isCreate) => disposition switch
        {
            "created" when isCreate => GroupSupplyMutationDisposition.Written,
            "updated" when !isCreate => GroupSupplyMutationDisposition.Written,
            "validation_failed" => GroupSupplyMutationDisposition.ValidationFailed,
            "conflict" => GroupSupplyMutationDisposition.Conflict,
            "not_found" => GroupSupplyMutationDisposition.NotFound,
            "version_conflict" => GroupSupplyMutationDisposition.VersionConflict,
            _ => throw new InvalidOperationException(
                "The Group Supply Configuration function returned an unknown disposition."),
        };

    private static GroupSupplyMutationDisposition MapException(
        PostgresException exception) =>
        string.Equals(
            exception.SqlState,
            PostgresErrorCodes.UniqueViolation,
            StringComparison.Ordinal)
            ? GroupSupplyMutationDisposition.Conflict
            : GroupSupplyMutationDisposition.ValidationFailed;

    private static bool IsKnownMutationFailure(PostgresException exception) =>
        string.Equals(exception.SqlState, "P0001", StringComparison.Ordinal)
        || string.Equals(
            exception.SqlState,
            PostgresErrorCodes.CheckViolation,
            StringComparison.Ordinal)
        || string.Equals(
            exception.SqlState,
            PostgresErrorCodes.UniqueViolation,
            StringComparison.Ordinal)
        || string.Equals(
            exception.SqlState,
            PostgresErrorCodes.ForeignKeyViolation,
            StringComparison.Ordinal);

    private static void AddBindings(
        NpgsqlCommand command,
        IReadOnlyList<GroupSupplyBindingValue>? bindings)
    {
        GroupSupplyPersistenceProtocol.AddNullableUuidArray(
            command.Parameters,
            bindings?
                .Select(static value => value.AccountId.Value)
                .ToArray());
        GroupSupplyPersistenceProtocol.AddNullableIntegerArray(
            command.Parameters,
            bindings?
                .Select(static value => value.PriorityOverride)
                .ToArray());
        GroupSupplyPersistenceProtocol.AddNullableIntegerArray(
            command.Parameters,
            bindings?
                .Select(static value => value.WeightOverride)
                .ToArray());
        GroupSupplyPersistenceProtocol.AddBooleanArray(
            command.Parameters,
            bindings?
                .Select(static value => value.Enabled)
                .ToArray());
    }

    private static GroupSupplyConfigurationResource? ParseBeforeState(
        string? json)
    {
        if (json is null)
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        List<GroupSupplyBindingValue> bindings = [];
        foreach (JsonElement binding in root
            .GetProperty("account_bindings")
            .EnumerateArray())
        {
            bindings.Add(new GroupSupplyBindingValue(
                new EntityId(binding.GetProperty("account_id").GetGuid()),
                ReadEnabled(binding),
                ReadNullableInteger(binding, "priority_override"),
                ReadNullableInteger(binding, "weight_override")));
        }

        JsonElement channelId = root.GetProperty("channel_id");
        return new GroupSupplyConfigurationResource(
            new EntityId(root.GetProperty("group_id").GetGuid()),
            channelId.ValueKind == JsonValueKind.Null
                ? null
                : new EntityId(channelId.GetGuid()),
            GroupSupplyInput.Bindings(bindings),
            root.GetProperty("version").GetInt64(),
            root.GetProperty("created_at").GetDateTimeOffset(),
            root.GetProperty("updated_at").GetDateTimeOffset());
    }

    private static bool ReadEnabled(JsonElement binding) =>
        binding.TryGetProperty("enabled", out JsonElement enabled)
            ? enabled.GetBoolean()
            : binding.GetProperty("is_enabled").GetBoolean();

    private static int? ReadNullableInteger(
        JsonElement root,
        string propertyName)
    {
        JsonElement value = root.GetProperty(propertyName);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetInt32();
    }

    private static GroupSupplyConfigurationSnapshot ToSnapshot(
        GroupSupplyConfigurationResource value) => new(
        value.GroupId,
        value.ChannelId,
        value.AccountBindings
            .Select(static binding => new GroupSupplyAccountBindingSnapshot(
                binding.AccountId,
                binding.Enabled,
                binding.PriorityOverride,
                binding.WeightOverride))
            .ToArray(),
        value.Version,
        value.CreatedAt,
        value.UpdatedAt);
}
#pragma warning restore MA0051
