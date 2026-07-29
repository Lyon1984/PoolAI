#pragma warning disable MA0051 // Aggregate command handlers keep the transaction protocol explicit.
using System.Text.Json;
using System.Runtime.CompilerServices;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Supply.Application.Ports;
using PoolAI.Modules.Supply.Domain;

namespace PoolAI.Modules.Supply.Application;

internal sealed class GroupSupplyControlPlaneService(
    IGroupSupplyConfigurationRepository repository,
    IUnitOfWorkFactory unitOfWorkFactory,
    GroupSupplyCommandCoordinator coordinator) :
    IGetGroupSupplyConfigurationUseCase,
    ICreateGroupSupplyConfigurationUseCase,
    IPatchGroupSupplyConfigurationUseCase
{
    private const string ResourceType = "group_supply_configuration";
    private readonly IGroupSupplyConfigurationRepository _repository =
        repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly IUnitOfWorkFactory _unitOfWorkFactory =
        unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
    private readonly GroupSupplyCommandCoordinator _coordinator =
        coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public async ValueTask<Result<GroupSupplyConfigurationView>> ExecuteAsync(
        GetGroupSupplyConfigurationQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!CanRead(query.Actor))
        {
            return Failure<GroupSupplyConfigurationView>(
                SupplyControlErrorCodes.RoleRequired,
                "The actor role cannot read a Group Supply Configuration.");
        }

        GroupSupplyConfigurationResource? configuration = await _repository
            .GetAsync(query.GroupId, cancellationToken)
            .ConfigureAwait(false);
        return configuration is null
            ? Failure<GroupSupplyConfigurationView>(
                SupplyControlErrorCodes.ResourceNotFound,
                "The Group Supply Configuration does not exist.")
            : Result.Success(ToView(configuration));
    }

    public async ValueTask<
        Result<SupplyCommandOutcome<GroupSupplyConfigurationView>>> ExecuteAsync(
        CreateGroupSupplyConfigurationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!CanManage(command.Actor))
        {
            return Failure<SupplyCommandOutcome<GroupSupplyConfigurationView>>(
                SupplyControlErrorCodes.RoleRequired,
                "The Admin role is required.");
        }

        IReadOnlyList<GroupSupplyBindingValue> bindings;
        try
        {
            AccountInput.IdempotencyKey(command.IdempotencyKey);
            bindings = ToBindings(command.AccountBindings);
        }
        catch (ArgumentException)
        {
            return Failure<SupplyCommandOutcome<GroupSupplyConfigurationView>>(
                SupplyControlErrorCodes.ValidationFailed,
                "The create Group Supply Configuration request is invalid.");
        }

        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        CommandIdempotencyAcquireResult acquire = await _coordinator.AcquireAsync(
            CreateScope(command.Actor, command.GroupId),
            command.IdempotencyKey,
            command.RequestId,
            command.Actor,
            new
            {
                group_id = command.GroupId.Value,
                channel_id = command.ChannelId?.Value,
                account_bindings = bindings,
            },
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Result<SupplyCommandOutcome<GroupSupplyConfigurationView>>? early =
            GroupSupplyCommandCoordinator
                .ReplayOrFailure<GroupSupplyConfigurationView>(
                    acquire,
                    expectedStatus: 201,
                    ResourceType,
                    command.GroupId);
        if (early is not null)
        {
            return early;
        }

        CommandIdempotencyLease lease = acquire.Lease!;
        GroupSupplyMutationResult mutation = await _repository.CreateAsync(
            new GroupSupplyConfigurationCreateWrite(
                command.GroupId,
                command.ChannelId,
                bindings),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (mutation.Disposition != GroupSupplyMutationDisposition.Written)
        {
            return await _coordinator.CompleteFailureAsync<
                SupplyCommandOutcome<GroupSupplyConfigurationView>>(
                    lease,
                    FailureFor(mutation),
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false);
        }

        GroupSupplyConfigurationResource configuration = RequiredValue(mutation);
        await AppendChangeAsync(
            command.Actor,
            "supply.group_configuration.created",
            "group_supply_configuration_created",
            configuration,
            before: null,
            command.RequestId,
            reason: null,
            command.IpAddress,
            command.UserAgent,
            command.IdempotencyKey,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        GroupSupplyConfigurationView view = ToView(configuration);
        string etag = GroupSupplyCommandCoordinator.ETag(configuration.Version);
        string location =
            $"/api/v1/admin/groups/{configuration.GroupId.Value:D}/supply-configuration";
        await _coordinator.CompleteSuccessAsync(
            lease,
            201,
            view,
            etag,
            location,
            ResourceType,
            configuration.GroupId,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(
            new SupplyCommandOutcome<GroupSupplyConfigurationView>(
                201,
                IsReplay: false,
                view,
                etag,
                location));
    }

    public async ValueTask<
        Result<SupplyCommandOutcome<GroupSupplyConfigurationView>>> ExecuteAsync(
        PatchGroupSupplyConfigurationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!CanManage(command.Actor))
        {
            return Failure<SupplyCommandOutcome<GroupSupplyConfigurationView>>(
                SupplyControlErrorCodes.RoleRequired,
                "The Admin role is required.");
        }

        IReadOnlyList<GroupSupplyBindingValue>? bindings;
        string reason;
        try
        {
            AccountInput.IdempotencyKey(command.IdempotencyKey);
            GroupSupplyInput.ExpectedVersion(command.ExpectedVersion);
            reason = GroupSupplyInput.Reason(command.Reason);
            if (!command.ChannelSpecified && !command.AccountBindingsSpecified)
            {
                throw new ArgumentException(
                    "At least one Group Supply field is required.",
                    nameof(command));
            }

            bindings = command.AccountBindingsSpecified
                ? ToBindings(command.AccountBindings
                    ?? throw new ArgumentException(
                        "Specified bindings cannot be null.",
                        nameof(command)))
                : command.AccountBindings is null
                    ? null
                    : throw new ArgumentException(
                        "Unspecified bindings must be null.",
                        nameof(command));
            if (!command.ChannelSpecified && command.ChannelId is not null)
            {
                throw new ArgumentException(
                    "An unspecified Channel must be null.",
                    nameof(command));
            }
        }
        catch (ArgumentException)
        {
            return Failure<SupplyCommandOutcome<GroupSupplyConfigurationView>>(
                SupplyControlErrorCodes.ValidationFailed,
                "The patch Group Supply Configuration request is invalid.");
        }

        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        CommandIdempotencyAcquireResult acquire = await _coordinator.AcquireAsync(
            PatchScope(command.Actor, command.GroupId),
            command.IdempotencyKey,
            command.RequestId,
            command.Actor,
            new
            {
                group_id = command.GroupId.Value,
                expected_version = command.ExpectedVersion,
                channel_specified = command.ChannelSpecified,
                channel_id = command.ChannelId?.Value,
                bindings_specified = command.AccountBindingsSpecified,
                account_bindings = bindings,
                reason,
            },
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Result<SupplyCommandOutcome<GroupSupplyConfigurationView>>? early =
            GroupSupplyCommandCoordinator
                .ReplayOrFailure<GroupSupplyConfigurationView>(
                    acquire,
                    expectedStatus: 200,
                    ResourceType,
                    command.GroupId);
        if (early is not null)
        {
            return early;
        }

        CommandIdempotencyLease lease = acquire.Lease!;
        GroupSupplyMutationResult mutation = await _repository.PatchAsync(
            new GroupSupplyConfigurationPatchWrite(
                command.GroupId,
                command.ExpectedVersion,
                command.ChannelSpecified,
                command.ChannelId,
                command.AccountBindingsSpecified,
                bindings,
                reason),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (mutation.Disposition != GroupSupplyMutationDisposition.Written)
        {
            return await _coordinator.CompleteFailureAsync<
                SupplyCommandOutcome<GroupSupplyConfigurationView>>(
                    lease,
                    FailureFor(mutation),
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false);
        }

        GroupSupplyConfigurationResource configuration = RequiredValue(mutation);
        if (mutation.WasChanged)
        {
            await AppendChangeAsync(
                command.Actor,
                "supply.group_configuration.updated",
                "group_supply_configuration_updated",
                configuration,
                mutation.Before,
                command.RequestId,
                reason,
                command.IpAddress,
                command.UserAgent,
                command.IdempotencyKey,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
        }

        GroupSupplyConfigurationView view = ToView(configuration);
        string etag = GroupSupplyCommandCoordinator.ETag(configuration.Version);
        await _coordinator.CompleteSuccessAsync(
            lease,
            200,
            view,
            etag,
            location: null,
            ResourceType,
            configuration.GroupId,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(
            new SupplyCommandOutcome<GroupSupplyConfigurationView>(
                200,
                IsReplay: false,
                view,
                etag));
    }

    private async ValueTask AppendChangeAsync(
        AccountActor actor,
        string auditAction,
        string eventType,
        GroupSupplyConfigurationResource configuration,
        GroupSupplyConfigurationResource? before,
        EntityId requestId,
        string? reason,
        string? ipAddress,
        string? userAgent,
        string idempotencyKey,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        await _coordinator.AppendAuditAsync(
            actor,
            auditAction,
            ResourceType,
            configuration.GroupId,
            requestId,
            reason,
            ipAddress,
            userAgent,
            before is null ? null : AuditState(before),
            AuditState(configuration),
            idempotencyKey,
            unitOfWorkContext,
            cancellationToken).ConfigureAwait(false);
        await _coordinator.AppendEventAsync(
            eventType,
            ResourceType,
            configuration.GroupId,
            configuration.Version,
            requestId,
            JsonSerializer.SerializeToElement(new
            {
                schema_version = 1,
                event_type = eventType,
                group_id = configuration.GroupId.Value,
                channel_id = configuration.ChannelId?.Value,
                binding_count = configuration.AccountBindings.Count,
                enabled_binding_count =
                    configuration.AccountBindings.Count(
                        static binding => binding.Enabled),
                version = configuration.Version,
            }),
            configuration.UpdatedAt,
            unitOfWorkContext,
            cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<GroupSupplyBindingValue> ToBindings(
        IReadOnlyList<GroupSupplyBindingView> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return GroupSupplyInput.Bindings(values.Select(static binding =>
            new GroupSupplyBindingValue(
                binding.AccountId,
                binding.Enabled,
                binding.PriorityOverride,
                binding.WeightOverride)));
    }

    private static GroupSupplyConfigurationResource RequiredValue(
        GroupSupplyMutationResult mutation) =>
        mutation.Value ?? throw new InvalidOperationException(
            "A successful Group Supply mutation did not return the resource.");

    private static SupplyMutationFailure FailureFor(
        GroupSupplyMutationResult mutation) => mutation.Disposition switch
        {
            GroupSupplyMutationDisposition.ValidationFailed => new(
                422,
                SupplyControlErrorCodes.ValidationFailed,
                "The Group Supply Configuration failed validation."),
            GroupSupplyMutationDisposition.Conflict => new(
                409,
                SupplyControlErrorCodes.ResourceConflict,
                "A Group Supply Configuration already exists."),
            GroupSupplyMutationDisposition.NotFound => new(
                404,
                SupplyControlErrorCodes.ResourceNotFound,
                "The Group Supply Configuration does not exist."),
            GroupSupplyMutationDisposition.VersionConflict => new(
                412,
                SupplyControlErrorCodes.VersionConflict,
                "The Group Supply Configuration version has changed.",
                mutation.CurrentVersion is > 0
                    ? GroupSupplyCommandCoordinator.ETag(
                        mutation.CurrentVersion.Value)
                    : null),
            _ => throw new InvalidOperationException(
                "A successful Group Supply mutation cannot be mapped as a failure."),
        };

    private static GroupSupplyConfigurationView ToView(
        GroupSupplyConfigurationResource configuration) => new(
        configuration.GroupId,
        configuration.ChannelId,
        configuration.AccountBindings
            .Select(static binding => new GroupSupplyBindingView(
                binding.AccountId,
                binding.Enabled,
                binding.PriorityOverride,
                binding.WeightOverride))
            .ToArray(),
        configuration.Version,
        configuration.CreatedAt,
        configuration.UpdatedAt);

    private static JsonElement AuditState(
        GroupSupplyConfigurationResource configuration) =>
        JsonSerializer.SerializeToElement(new
        {
            group_id = configuration.GroupId.Value,
            channel_id = configuration.ChannelId?.Value,
            account_bindings = configuration.AccountBindings.Select(
                static binding => new
                {
                    account_id = binding.AccountId.Value,
                    enabled = binding.Enabled,
                    priority_override = binding.PriorityOverride,
                    weight_override = binding.WeightOverride,
                }),
            version = configuration.Version,
        });

    private static bool CanManage(AccountActor actor) =>
        actor.TokenVersion > 0 && actor.Role == AccountControlRole.Admin;

    private static bool CanRead(AccountActor actor) =>
        actor.TokenVersion > 0
        && actor.Role is AccountControlRole.Admin
            or AccountControlRole.Operator
            or AccountControlRole.Auditor;

    private static string CreateScope(AccountActor actor, EntityId groupId) =>
        $"supply:{actor.UserId.Value:D}:post:/api/v1/admin/groups/{groupId.Value:D}/supply-configuration";

    private static string PatchScope(AccountActor actor, EntityId groupId) =>
        $"supply:{actor.UserId.Value:D}:patch:/api/v1/admin/groups/{groupId.Value:D}/supply-configuration";

    private static Result<T> Failure<T>(string code, string description) =>
        Result.Failure<T>(code, description);
}
#pragma warning restore MA0051
