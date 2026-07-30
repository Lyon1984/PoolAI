#pragma warning disable MA0051 // Channel command handlers keep the transactional protocol explicit.
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Application.Ports;
using PoolAI.Modules.Supply.Domain;

namespace PoolAI.Modules.Supply.Application;

internal sealed class ChannelControlPlaneService(
    IChannelControlPlaneRepository repository,
    IUnitOfWorkFactory unitOfWorkFactory,
    GroupSupplyCommandCoordinator coordinator) :
    IListChannelsUseCase,
    IGetChannelUseCase,
    ICreateChannelUseCase,
    IUpdateChannelUseCase,
    IRetireChannelUseCase
{
    private const string ResourceType = "channel";
    private readonly IChannelControlPlaneRepository _repository =
        repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly IUnitOfWorkFactory _unitOfWorkFactory =
        unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
    private readonly GroupSupplyCommandCoordinator _coordinator =
        coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public async ValueTask<Result<ChannelPage>> ExecuteAsync(
        ListChannelsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!CanRead(query.Actor))
        {
            return Failure<ChannelPage>(
                SupplyControlErrorCodes.RoleRequired,
                "The actor role cannot read Channels.");
        }

        if (query.Limit is < 1 or > 100
            || !TryDecodeCursor(query.Cursor, out ChannelCursor? cursor))
        {
            return Failure<ChannelPage>(
                SupplyControlErrorCodes.InvalidRequest,
                "The Channel pagination request is invalid.");
        }

        ChannelSlice slice = await _repository
            .ListAsync(cursor, query.Limit, cancellationToken)
            .ConfigureAwait(false);
        ChannelView[] channels = slice.Items.Select(ToView).ToArray();
        string? nextCursor = slice.HasMore && slice.Items.Count > 0
            ? EncodeCursor(slice.Items[^1])
            : null;
        return Result.Success(new ChannelPage(channels, nextCursor, slice.HasMore));
    }

    public async ValueTask<Result<ChannelView>> ExecuteAsync(
        GetChannelQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!CanRead(query.Actor))
        {
            return Failure<ChannelView>(
                SupplyControlErrorCodes.RoleRequired,
                "The actor role cannot read Channels.");
        }

        ChannelResource? channel = await _repository
            .GetAsync(query.ChannelId, cancellationToken)
            .ConfigureAwait(false);
        return channel is null
            ? Failure<ChannelView>(
                SupplyControlErrorCodes.ResourceNotFound,
                "The Channel does not exist.")
            : Result.Success(ToView(channel));
    }

    public async ValueTask<Result<SupplyCommandOutcome<ChannelView>>> ExecuteAsync(
        CreateChannelCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!CanManage(command.Actor))
        {
            return Failure<SupplyCommandOutcome<ChannelView>>(
                SupplyControlErrorCodes.RoleRequired,
                "The Admin or Operator role is required.");
        }

        PreparedChannelCreate prepared;
        try
        {
            prepared = PrepareCreate(command);
        }
        catch (ArgumentException)
        {
            return Failure<SupplyCommandOutcome<ChannelView>>(
                SupplyControlErrorCodes.ValidationFailed,
                "The create-Channel request is invalid.");
        }

        EntityId channelId = EntityId.New();
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        CommandIdempotencyAcquireResult acquire = await _coordinator.AcquireAsync(
            CreateScope(command.Actor),
            command.IdempotencyKey,
            command.RequestId,
            command.Actor,
            prepared.RequestHash,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Result<SupplyCommandOutcome<ChannelView>>? early =
            GroupSupplyCommandCoordinator.ReplayOrFailure<ChannelView>(
                acquire,
                expectedStatus: 201,
                ResourceType);
        if (early is not null)
        {
            return early;
        }

        CommandIdempotencyLease lease = acquire.Lease!;
        ChannelMutationResult mutation = await _repository.CreateAsync(
            new ChannelCreateWrite(
                channelId,
                command.Provider,
                prepared.Name,
                prepared.Capabilities,
                prepared.ModelMappings),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (mutation.Disposition != ChannelMutationDisposition.Written)
        {
            return await _coordinator.CompleteFailureAsync<
                SupplyCommandOutcome<ChannelView>>(
                    lease,
                    FailureFor(mutation),
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false);
        }

        ChannelResource channel = RequiredValue(mutation);
        await AppendChangeAsync(
            command.Actor,
            "supply.channel.created",
            "channel_created",
            channel,
            before: null,
            command.RequestId,
            reason: null,
            command.IpAddress,
            command.UserAgent,
            command.IdempotencyKey,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        ChannelView view = ToView(channel);
        string etag = GroupSupplyCommandCoordinator.ETag(channel.Version);
        string location = $"/api/v1/admin/channels/{channel.Id.Value:D}";
        await _coordinator.CompleteSuccessAsync(
            lease,
            201,
            view,
            etag,
            location,
            ResourceType,
            channel.Id,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(new SupplyCommandOutcome<ChannelView>(
            201,
            IsReplay: false,
            view,
            etag,
            location));
    }

    public async ValueTask<Result<SupplyCommandOutcome<ChannelView>>> ExecuteAsync(
        UpdateChannelCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!CanManage(command.Actor))
        {
            return Failure<SupplyCommandOutcome<ChannelView>>(
                SupplyControlErrorCodes.RoleRequired,
                "The Admin or Operator role is required.");
        }

        PreparedChannelUpdate prepared;
        try
        {
            prepared = PrepareUpdate(command);
        }
        catch (ArgumentException)
        {
            return Failure<SupplyCommandOutcome<ChannelView>>(
                SupplyControlErrorCodes.ValidationFailed,
                "The update-Channel request is invalid.");
        }

        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        CommandIdempotencyAcquireResult acquire = await _coordinator.AcquireAsync(
            UpdateScope(command.Actor, command.ChannelId),
            command.IdempotencyKey,
            command.RequestId,
            command.Actor,
            prepared.RequestHash,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Result<SupplyCommandOutcome<ChannelView>>? early =
            GroupSupplyCommandCoordinator.ReplayOrFailure<ChannelView>(
                acquire,
                expectedStatus: 200,
                ResourceType,
                command.ChannelId);
        if (early is not null)
        {
            return early;
        }

        CommandIdempotencyLease lease = acquire.Lease!;
        ChannelMutationResult mutation = await _repository.UpdateAsync(
            new ChannelUpdateWrite(
                command.ChannelId,
                command.ExpectedVersion,
                command.NameSpecified,
                prepared.Name,
                command.StatusSpecified,
                prepared.Status,
                command.CapabilitiesSpecified,
                prepared.Capabilities,
                command.ModelMappingsSpecified,
                prepared.ModelMappings,
                prepared.Reason),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (mutation.Disposition != ChannelMutationDisposition.Written)
        {
            return await _coordinator.CompleteFailureAsync<
                SupplyCommandOutcome<ChannelView>>(
                    lease,
                    FailureFor(mutation),
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false);
        }

        ChannelResource channel = RequiredValue(mutation);
        if (mutation.WasChanged)
        {
            await AppendChangeAsync(
                command.Actor,
                "supply.channel.updated",
                "channel_updated",
                channel,
                mutation.Before,
                command.RequestId,
                prepared.Reason,
                command.IpAddress,
                command.UserAgent,
                command.IdempotencyKey,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
        }

        ChannelView view = ToView(channel);
        string etag = GroupSupplyCommandCoordinator.ETag(channel.Version);
        await _coordinator.CompleteSuccessAsync(
            lease,
            200,
            view,
            etag,
            location: null,
            ResourceType,
            channel.Id,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(new SupplyCommandOutcome<ChannelView>(
            200,
            IsReplay: false,
            view,
            etag));
    }

    public async ValueTask<Result<SupplyCommandOutcome>> ExecuteAsync(
        RetireChannelCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!CanManage(command.Actor))
        {
            return Failure<SupplyCommandOutcome>(
                SupplyControlErrorCodes.RoleRequired,
                "The Admin or Operator role is required.");
        }

        string reason;
        try
        {
            AccountInput.IdempotencyKey(command.IdempotencyKey);
            ChannelInput.ExpectedVersion(command.ExpectedVersion);
            reason = ChannelInput.Reason(command.Reason);
        }
        catch (ArgumentException)
        {
            return Failure<SupplyCommandOutcome>(
                SupplyControlErrorCodes.ValidationFailed,
                "The retire-Channel request is invalid.");
        }

        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        CommandIdempotencyAcquireResult acquire = await _coordinator.AcquireAsync(
            RetireScope(command.Actor, command.ChannelId),
            command.IdempotencyKey,
            command.RequestId,
            command.Actor,
            new
            {
                channel_id = command.ChannelId.Value,
                expected_version = command.ExpectedVersion,
                reason,
            },
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Result<SupplyCommandOutcome>? early =
            GroupSupplyCommandCoordinator.RetireReplayOrFailure(
                acquire,
                ResourceType,
                command.ChannelId);
        if (early is not null)
        {
            return early;
        }

        CommandIdempotencyLease lease = acquire.Lease!;
        ChannelMutationResult mutation = await _repository.RetireAsync(
            new ChannelRetireWrite(
                command.ChannelId,
                command.ExpectedVersion,
                reason),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (mutation.Disposition != ChannelMutationDisposition.Written)
        {
            return await _coordinator.CompleteFailureAsync<SupplyCommandOutcome>(
                lease,
                FailureFor(mutation),
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        ChannelResource channel = RequiredValue(mutation);
        await AppendChangeAsync(
            command.Actor,
            "supply.channel.retired",
            "channel_retired",
            channel,
            mutation.Before,
            command.RequestId,
            reason,
            command.IpAddress,
            command.UserAgent,
            command.IdempotencyKey,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        string etag = GroupSupplyCommandCoordinator.ETag(channel.Version);
        await _coordinator.CompleteRetireAsync(
            lease,
            etag,
            ResourceType,
            channel.Id,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(new SupplyCommandOutcome(
            204,
            IsReplay: false,
            etag));
    }

    private static PreparedChannelCreate PrepareCreate(
        CreateChannelCommand command)
    {
        AccountInput.IdempotencyKey(command.IdempotencyKey);
        string name = ChannelInput.Name(command.Name);
        _ = ProviderCode(command.Provider);
        ChannelCapabilitiesValue capabilities = ToCapabilities(command.Capabilities);
        IReadOnlyList<ChannelModelMappingValue> mappings =
            ToMappings(command.ModelMappings);
        return new PreparedChannelCreate(
            name,
            capabilities,
            mappings,
            new
            {
                name,
                provider = ProviderCode(command.Provider),
                capabilities,
                mappings,
            });
    }

    private static PreparedChannelUpdate PrepareUpdate(
        UpdateChannelCommand command)
    {
        AccountInput.IdempotencyKey(command.IdempotencyKey);
        ChannelInput.ExpectedVersion(command.ExpectedVersion);
        if (!command.NameSpecified
            && !command.StatusSpecified
            && !command.CapabilitiesSpecified
            && !command.ModelMappingsSpecified)
        {
            throw new ArgumentException(
                "At least one Channel field is required.",
                nameof(command));
        }

        string? name = command.NameSpecified
            ? ChannelInput.Name(command.Name
                ?? throw new ArgumentException(
                    "The Channel name is required.",
                    nameof(command)))
            : command.Name is null
                ? null
                : throw new ArgumentException(
                    "An unspecified name must be null.",
                    nameof(command));
        ChannelResourceStatus? status = command.StatusSpecified
            ? ToStatus(command.Status
                ?? throw new ArgumentException(
                    "The Channel status is required.",
                    nameof(command)))
            : command.Status is null
                ? null
                : throw new ArgumentException(
                    "An unspecified status must be null.",
                    nameof(command));
        ChannelCapabilitiesValue? capabilities = command.CapabilitiesSpecified
            ? ToCapabilities(command.Capabilities
                ?? throw new ArgumentException(
                    "The Channel capabilities are required.",
                    nameof(command)))
            : command.Capabilities is null
                ? null
                : throw new ArgumentException(
                    "Unspecified Channel capabilities must be null.",
                    nameof(command));
        IReadOnlyList<ChannelModelMappingValue>? mappings =
            command.ModelMappingsSpecified
                ? ToMappings(command.ModelMappings
                    ?? throw new ArgumentException(
                        "The Channel model mappings are required.",
                        nameof(command)))
                : command.ModelMappings is null
                    ? null
                    : throw new ArgumentException(
                        "Unspecified Channel mappings must be null.",
                        nameof(command));
        string? reason = command.Reason is null
            ? null
            : ChannelInput.Reason(command.Reason);
        if (command.StatusSpecified && reason is null)
        {
            throw new ArgumentException(
                "A Channel lifecycle change requires a reason.",
                nameof(command));
        }

        return new PreparedChannelUpdate(
            name,
            status,
            capabilities,
            mappings,
            reason,
            new
            {
                channel_id = command.ChannelId.Value,
                expected_version = command.ExpectedVersion,
                name_specified = command.NameSpecified,
                name,
                status_specified = command.StatusSpecified,
                status,
                capabilities_specified = command.CapabilitiesSpecified,
                capabilities,
                model_mappings_specified = command.ModelMappingsSpecified,
                mappings,
                reason,
            });
    }

    private async ValueTask AppendChangeAsync(
        AccountActor actor,
        string auditAction,
        string eventType,
        ChannelResource channel,
        ChannelResource? before,
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
            channel.Id,
            requestId,
            reason,
            ipAddress,
            userAgent,
            before is null ? null : AuditState(before),
            AuditState(channel),
            idempotencyKey,
            unitOfWorkContext,
            cancellationToken).ConfigureAwait(false);
        await _coordinator.AppendEventAsync(
            eventType,
            ResourceType,
            channel.Id,
            channel.Version,
            requestId,
            JsonSerializer.SerializeToElement(new
            {
                schema_version = 1,
                event_type = eventType,
                channel_id = channel.Id.Value,
                provider = ProviderCode(channel.Provider),
                status = StatusCode(channel.Status),
                version = channel.Version,
            }),
            channel.UpdatedAt,
            unitOfWorkContext,
            cancellationToken).ConfigureAwait(false);
    }

    private static ChannelResource RequiredValue(ChannelMutationResult mutation) =>
        mutation.Value ?? throw new InvalidOperationException(
            "A successful Channel mutation did not return the resource.");

    private static SupplyMutationFailure FailureFor(
        ChannelMutationResult mutation) => mutation.Disposition switch
        {
            ChannelMutationDisposition.ValidationFailed => new(
                422,
                SupplyControlErrorCodes.ValidationFailed,
                "The Channel mutation failed validation."),
            ChannelMutationDisposition.Conflict => new(
                409,
                SupplyControlErrorCodes.ResourceConflict,
                "The requested Channel conflicts with an existing resource."),
            ChannelMutationDisposition.NotFound => new(
                404,
                SupplyControlErrorCodes.ResourceNotFound,
                "The Channel does not exist."),
            ChannelMutationDisposition.VersionConflict => new(
                412,
                SupplyControlErrorCodes.VersionConflict,
                "The Channel version has changed.",
                VersionETag(mutation.CurrentVersion)),
            ChannelMutationDisposition.LifecycleConflict => new(
                409,
                SupplyControlErrorCodes.ResourceConflict,
                "The Channel lifecycle does not allow the requested change."),
            ChannelMutationDisposition.ChannelInUse => new(
                409,
                SupplyControlErrorCodes.ChannelInUse,
                "The Channel is still referenced by a Supply Configuration."),
            _ => throw new InvalidOperationException(
                "A successful Channel mutation cannot be mapped as a failure."),
        };

    private static string? VersionETag(long? version) => version is > 0
        ? GroupSupplyCommandCoordinator.ETag(version.Value)
        : null;

    private static ChannelView ToView(ChannelResource channel) => new(
        channel.Id,
        channel.Name,
        channel.Provider,
        ToLifecycle(channel.Status),
        new ChannelCapabilitiesSnapshot(
            channel.Capabilities.Responses,
            channel.Capabilities.ChatCompletions,
            channel.Capabilities.FunctionTools,
            channel.Capabilities.Streaming),
        channel.ModelMappings
            .Select(static mapping => new ChannelModelMappingView(
                mapping.ClientModel,
                mapping.UpstreamModel))
            .ToArray(),
        channel.Version,
        channel.CreatedAt,
        channel.UpdatedAt);

    private static JsonElement AuditState(ChannelResource channel) =>
        JsonSerializer.SerializeToElement(new
        {
            channel_id = channel.Id.Value,
            name = channel.Name,
            provider = ProviderCode(channel.Provider),
            status = StatusCode(channel.Status),
            capabilities = channel.Capabilities,
            model_mappings = channel.ModelMappings,
            version = channel.Version,
        });

    private static ChannelCapabilitiesValue ToCapabilities(
        ChannelCapabilitiesSnapshot value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new ChannelCapabilitiesValue(
            value.Responses,
            value.ChatCompletions,
            value.FunctionTools,
            value.Streaming);
    }

    private static IReadOnlyList<ChannelModelMappingValue> ToMappings(
        IReadOnlyList<ChannelModelMappingView> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return ChannelInput.ModelMappings(values.Select(static value =>
            new ChannelModelMappingValue(
                value.ClientModel,
                value.UpstreamModel)));
    }

    private static ChannelResourceStatus ToStatus(ChannelLifecycle status) =>
        status switch
        {
            ChannelLifecycle.Active => ChannelResourceStatus.Active,
            ChannelLifecycle.Disabled => ChannelResourceStatus.Disabled,
            ChannelLifecycle.Retired => throw new ArgumentException(
                "Retirement is only available through DELETE.",
                nameof(status)),
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

    private static ChannelLifecycle ToLifecycle(ChannelResourceStatus status) =>
        status switch
        {
            ChannelResourceStatus.Active => ChannelLifecycle.Active,
            ChannelResourceStatus.Disabled => ChannelLifecycle.Disabled,
            ChannelResourceStatus.Retired => ChannelLifecycle.Retired,
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

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

    private static bool CanManage(AccountActor actor) =>
        actor.TokenVersion > 0
        && actor.Role is AccountControlRole.Admin or AccountControlRole.Operator;

    private static bool CanRead(AccountActor actor) =>
        actor.TokenVersion > 0
        && actor.Role is AccountControlRole.Admin
            or AccountControlRole.Operator
            or AccountControlRole.Auditor;

    private static string CreateScope(AccountActor actor) =>
        $"supply:{actor.UserId.Value:D}:post:/api/v1/admin/channels";

    private static string UpdateScope(AccountActor actor, EntityId channelId) =>
        $"supply:{actor.UserId.Value:D}:patch:/api/v1/admin/channels/{channelId.Value:D}";

    private static string RetireScope(AccountActor actor, EntityId channelId) =>
        $"supply:{actor.UserId.Value:D}:delete:/api/v1/admin/channels/{channelId.Value:D}";

    private static string EncodeCursor(ChannelResource channel)
    {
        Span<byte> bytes = stackalloc byte[25];
        bytes[0] = 0x01;
        long unixMicroseconds = checked(
            (channel.CreatedAt.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks) / 10);
        BinaryPrimitives.WriteInt64BigEndian(bytes[1..9], unixMicroseconds);
        Convert.FromHexString(channel.Id.Value.ToString("N"), bytes[9..], out _, out _);
        return ToBase64Url(bytes);
    }

    private static bool TryDecodeCursor(string? encoded, out ChannelCursor? cursor)
    {
        cursor = null;
        if (encoded is null)
        {
            return true;
        }

        try
        {
            if (encoded.Length != 34
                || encoded.Contains('=', StringComparison.Ordinal))
            {
                return false;
            }

            string base64 = encoded.Replace('-', '+').Replace('_', '/') + "==";
            byte[] bytes = Convert.FromBase64String(base64);
            if (bytes.Length != 25
                || bytes[0] != 0x01
                || !string.Equals(ToBase64Url(bytes), encoded, StringComparison.Ordinal))
            {
                return false;
            }

            long microseconds =
                BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(1, 8));
            long ticks = checked(
                DateTime.UnixEpoch.Ticks + checked(microseconds * 10));
            Guid id = new(bytes.AsSpan(9, 16), bigEndian: true);
            if (id == Guid.Empty)
            {
                return false;
            }

            cursor = new ChannelCursor(
                new DateTimeOffset(ticks, TimeSpan.Zero),
                new EntityId(id));
            return true;
        }
        catch (Exception exception) when (
            exception is FormatException
                or OverflowException
                or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static string ToBase64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static Result<T> Failure<T>(string code, string description) =>
        Result.Failure<T>(code, description);

    private sealed record PreparedChannelCreate(
        string Name,
        ChannelCapabilitiesValue Capabilities,
        IReadOnlyList<ChannelModelMappingValue> ModelMappings,
        object RequestHash);

    private sealed record PreparedChannelUpdate(
        string? Name,
        ChannelResourceStatus? Status,
        ChannelCapabilitiesValue? Capabilities,
        IReadOnlyList<ChannelModelMappingValue>? ModelMappings,
        string? Reason,
        object RequestHash);
}
#pragma warning restore MA0051
