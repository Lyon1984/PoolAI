#pragma warning disable MA0048 // Compact command protocol records stay beside the coordinator.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Domain;

namespace PoolAI.Modules.Supply.Application;

internal sealed record SupplyMutationFailure(
    int Status,
    string Code,
    string Description,
    string? ETag = null);

internal sealed record SupplyReplayFailure(
    string Code,
    string Description);

internal sealed class GroupSupplyCommandCoordinator(
    ICommandIdempotencyStore idempotencyStore,
    IAuditAppender auditAppender,
    IOutboxAppender outboxAppender,
    AccountControlPlanePolicy policy)
{
    private const string EventTopic = "poolai.supply.v1";
    private const int EventSchemaVersion = 1;
    private static readonly TimeSpan IdempotencyLease = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdempotencyRetention = TimeSpan.FromHours(24);

    private readonly ICommandIdempotencyStore _idempotencyStore =
        idempotencyStore ?? throw new ArgumentNullException(nameof(idempotencyStore));
    private readonly IAuditAppender _auditAppender =
        auditAppender ?? throw new ArgumentNullException(nameof(auditAppender));
    private readonly IOutboxAppender _outboxAppender =
        outboxAppender ?? throw new ArgumentNullException(nameof(outboxAppender));
    private readonly AccountControlPlanePolicy _policy =
        policy ?? throw new ArgumentNullException(nameof(policy));

    internal ValueTask<CommandIdempotencyAcquireResult> AcquireAsync(
        string scope,
        string key,
        EntityId requestId,
        AccountActor actor,
        object request,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        byte[] requestHash = HashRequest(request);
        return _idempotencyStore.AcquireAsync(
            new CommandIdempotencyRequest(
                scope,
                key,
                EntityId.New(),
                $"user:{actor.UserId.Value:D}",
                requestHash,
                requestId,
                IdempotencyLease,
                IdempotencyRetention),
            unitOfWorkContext,
            cancellationToken);
    }

    internal static Result<SupplyCommandOutcome<T>>? ReplayOrFailure<T>(
        CommandIdempotencyAcquireResult acquire,
        int expectedStatus,
        string resourceType,
        EntityId? expectedResourceId = null) =>
        acquire.Disposition switch
        {
            CommandIdempotencyDisposition.Acquired => null,
            CommandIdempotencyDisposition.Conflict =>
                Failure<SupplyCommandOutcome<T>>(
                    SupplyControlErrorCodes.IdempotencyConflict,
                    "The idempotency key was used for a different request."),
            CommandIdempotencyDisposition.Busy =>
                Failure<SupplyCommandOutcome<T>>(
                    SupplyControlErrorCodes.CoordinationUnavailable,
                    "The matching idempotent command is still in progress.",
                    retryAfterSeconds: 1),
            CommandIdempotencyDisposition.Replay => Replay<T>(
                acquire.Response!,
                expectedStatus,
                resourceType,
                expectedResourceId),
            _ => throw new ArgumentOutOfRangeException(nameof(acquire)),
        };

    internal static Result<SupplyCommandOutcome>? RetireReplayOrFailure(
        CommandIdempotencyAcquireResult acquire,
        string resourceType,
        EntityId resourceId) => acquire.Disposition switch
        {
            CommandIdempotencyDisposition.Acquired => null,
            CommandIdempotencyDisposition.Conflict =>
                Failure<SupplyCommandOutcome>(
                    SupplyControlErrorCodes.IdempotencyConflict,
                    "The idempotency key was used for a different request."),
            CommandIdempotencyDisposition.Busy =>
                Failure<SupplyCommandOutcome>(
                    SupplyControlErrorCodes.CoordinationUnavailable,
                    "The matching idempotent command is still in progress.",
                    retryAfterSeconds: 1),
            CommandIdempotencyDisposition.Replay => ReplayRetire(
                acquire.Response!,
                resourceType,
                resourceId),
            _ => throw new ArgumentOutOfRangeException(nameof(acquire)),
        };

    internal async ValueTask<Result<T>> CompleteFailureAsync<T>(
        CommandIdempotencyLease lease,
        SupplyMutationFailure failure,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        bool completed = await _idempotencyStore.CompleteAsync(
            new CommandIdempotencyCompletion(
                lease,
                CommandIdempotencyTerminalStatus.Failed,
                failure.Status,
                JsonSerializer.SerializeToElement(new SupplyReplayFailure(
                    failure.Code,
                    failure.Description)),
                ResponseBodyEnvelope: null,
                Headers(failure.ETag, location: null),
                ResourceType: null,
                ResourceId: null),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (!completed)
        {
            throw new InvalidOperationException(
                "The Supply idempotency lease was lost.");
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Failure<T>(
            failure.Code,
            failure.Description,
            etag: failure.ETag);
    }

    internal async ValueTask CompleteSuccessAsync<T>(
        CommandIdempotencyLease lease,
        int status,
        T value,
        string etag,
        string? location,
        string resourceType,
        EntityId resourceId,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        bool completed = await _idempotencyStore.CompleteAsync(
            new CommandIdempotencyCompletion(
                lease,
                CommandIdempotencyTerminalStatus.Completed,
                status,
                SerializeReplayValue(value),
                ResponseBodyEnvelope: null,
                Headers(etag, location),
                resourceType,
                resourceId),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (!completed)
        {
            throw new InvalidOperationException(
                "The Supply idempotency lease was lost.");
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask CompleteRetireAsync(
        CommandIdempotencyLease lease,
        string etag,
        string resourceType,
        EntityId resourceId,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        bool completed = await _idempotencyStore.CompleteAsync(
            new CommandIdempotencyCompletion(
                lease,
                CommandIdempotencyTerminalStatus.Completed,
                ResponseStatus: 204,
                ResponseBody: null,
                ResponseBodyEnvelope: null,
                Headers(etag, location: null),
                resourceType,
                resourceId),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (!completed)
        {
            throw new InvalidOperationException(
                "The Supply idempotency lease was lost.");
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal ValueTask AppendAuditAsync(
        AccountActor actor,
        string action,
        string targetType,
        EntityId targetId,
        EntityId requestId,
        string? reason,
        string? ipAddress,
        string? userAgent,
        JsonElement? before,
        JsonElement? after,
        string idempotencyKey,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken) => _auditAppender.AppendAsync(
        new AuditEntry(
            EntityId.New(),
            AuditActor(actor.Role),
            actor.UserId,
            action,
            targetType,
            targetId,
            requestId,
            reason,
            ipAddress,
            userAgent,
            before,
            after,
            JsonSerializer.SerializeToElement(new
            {
                idempotency_key_hash = HmacText(
                    "poolai|audit-idempotency-key|supply-control|v1\0",
                    idempotencyKey),
            })),
        unitOfWorkContext,
        cancellationToken);

    internal ValueTask AppendEventAsync(
        string eventType,
        string aggregateType,
        EntityId aggregateId,
        long version,
        EntityId requestId,
        JsonElement payload,
        DateTimeOffset occurredAt,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        EntityId eventId = EntityId.New();
        return _outboxAppender.AppendAsync(
            new IntegrationEvent(
                eventId,
                $"supply-control:{eventType}:{eventId.Value:D}",
                EventTopic,
                EventSchemaVersion,
                aggregateType,
                aggregateId,
                version,
                eventType,
                SourceEventSequence: null,
                requestId,
                CausationId: null,
                payload,
                occurredAt),
            unitOfWorkContext,
            cancellationToken);
    }

    internal static string ETag(long version) => $"\"v{version}\"";

    private static Result<SupplyCommandOutcome<T>> Replay<T>(
        CommandIdempotencyResponse response,
        int expectedStatus,
        string resourceType,
        EntityId? expectedResourceId)
    {
        if (response.TerminalStatus == CommandIdempotencyTerminalStatus.Failed)
        {
            SupplyReplayFailure failure = ParseReplayFailure(response);
            return Failure<SupplyCommandOutcome<T>>(
                failure.Code,
                failure.Description,
                etag: Header(response.Headers, "ETag"));
        }

        if (response.Body is not JsonElement body)
        {
            throw new InvalidOperationException(
                "The Supply success replay body is invalid.");
        }

        T value = DeserializeReplayValue<T>(body);
        SupplyReplayIdentity identity = ReplayIdentity(value, expectedStatus);
        string? etag = Header(response.Headers, "ETag");
        string? location = Header(response.Headers, "Location");
        int expectedHeaderCount = identity.Location is null ? 1 : 2;
        if (response.TerminalStatus != CommandIdempotencyTerminalStatus.Completed
            || response.Status != expectedStatus
            || response.BodyEnvelope is not null
            || !string.Equals(
                response.ResourceType,
                resourceType,
                StringComparison.Ordinal)
            || response.ResourceId != identity.ResourceId
            || expectedResourceId is not null
                && response.ResourceId != expectedResourceId
            || !string.Equals(
                etag,
                ETag(identity.Version),
                StringComparison.Ordinal)
            || !string.Equals(
                location,
                identity.Location,
                StringComparison.Ordinal)
            || HeaderCount(response.Headers) != expectedHeaderCount)
        {
            throw new InvalidOperationException(
                "The Supply success replay is invalid.");
        }

        return Result.Success(new SupplyCommandOutcome<T>(
            response.Status,
            IsReplay: true,
            value,
            etag!,
            location));
    }

    private static Result<SupplyCommandOutcome> ReplayRetire(
        CommandIdempotencyResponse response,
        string resourceType,
        EntityId resourceId)
    {
        if (response.TerminalStatus == CommandIdempotencyTerminalStatus.Failed)
        {
            SupplyReplayFailure failure = ParseReplayFailure(response);
            return Failure<SupplyCommandOutcome>(
                failure.Code,
                failure.Description,
                etag: Header(response.Headers, "ETag"));
        }

        string? etag = Header(response.Headers, "ETag");
        if (response.TerminalStatus != CommandIdempotencyTerminalStatus.Completed
            || response.Status != 204
            || response.Body is not null
            || response.BodyEnvelope is not null
            || !string.Equals(
                response.ResourceType,
                resourceType,
                StringComparison.Ordinal)
            || response.ResourceId != resourceId
            || etag is null
            || !IsCanonicalETag(etag)
            || HeaderCount(response.Headers) != 1)
        {
            throw new InvalidOperationException(
                "The Supply retirement replay is invalid.");
        }

        return Result.Success(new SupplyCommandOutcome(
            204,
            IsReplay: true,
            etag));
    }

    private static SupplyReplayFailure ParseReplayFailure(
        CommandIdempotencyResponse response)
    {
        SupplyReplayFailure failure =
            response.Body?.Deserialize<SupplyReplayFailure>()
            ?? throw new InvalidOperationException(
                "The Supply failure replay body is invalid.");
        string? etag = Header(response.Headers, "ETag");
        int expectedStatus = FailureStatus(failure.Code);
        bool expectsETag = expectedStatus == 412;
        if (response.TerminalStatus != CommandIdempotencyTerminalStatus.Failed
            || response.Status != expectedStatus
            || response.BodyEnvelope is not null
            || response.ResourceType is not null
            || response.ResourceId is not null
            || string.IsNullOrWhiteSpace(failure.Description)
            || (etag is not null) != expectsETag
            || etag is not null && !IsCanonicalETag(etag)
            || HeaderCount(response.Headers) != (expectsETag ? 1 : 0))
        {
            throw new InvalidOperationException(
                "The Supply failure replay is invalid.");
        }

        return failure;
    }

    private byte[] HashRequest(object value)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(value);
        byte[] domain = Encoding.UTF8.GetBytes(
            "poolai|idempotency-request-hash|supply-control|v1\0");
        byte[] input = new byte[domain.Length + body.Length];
        try
        {
            domain.CopyTo(input, 0);
            body.CopyTo(input, domain.Length);
            return HMACSHA256.HashData(_policy.RequestHashPepper, input);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
            CryptographicOperations.ZeroMemory(input);
        }
    }

    private string HmacText(string domain, string value)
    {
        byte[] domainBytes = Encoding.UTF8.GetBytes(domain);
        byte[] valueBytes = Encoding.UTF8.GetBytes(value);
        byte[] input = new byte[domainBytes.Length + valueBytes.Length];
        try
        {
            domainBytes.CopyTo(input, 0);
            valueBytes.CopyTo(input, domainBytes.Length);
            return Convert.ToHexStringLower(
                HMACSHA256.HashData(_policy.RequestHashPepper, input));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(valueBytes);
            CryptographicOperations.ZeroMemory(input);
        }
    }

    private static JsonElement Headers(string? etag, string? location)
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal);
        if (etag is not null)
        {
            headers["ETag"] = etag;
        }

        if (location is not null)
        {
            headers["Location"] = location;
        }

        return JsonSerializer.SerializeToElement(headers);
    }

    private static string? Header(JsonElement headers, string name) =>
        headers.ValueKind == JsonValueKind.Object
            && headers.TryGetProperty(name, out JsonElement value)
                ? value.GetString()
                : null;

    private static int HeaderCount(JsonElement headers) =>
        headers.ValueKind == JsonValueKind.Object
            ? headers.EnumerateObject().Count()
            : -1;

    private static bool IsCanonicalETag(string value) =>
        value.Length >= 4
        && value.StartsWith("\"v", StringComparison.Ordinal)
        && value.EndsWith('"')
        && long.TryParse(
            value.AsSpan(2, value.Length - 3),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out long version)
        && version > 0;

    private static int FailureStatus(string code) => code switch
    {
        SupplyControlErrorCodes.ValidationFailed => 422,
        SupplyControlErrorCodes.ResourceConflict => 409,
        SupplyControlErrorCodes.ResourceNotFound => 404,
        SupplyControlErrorCodes.VersionConflict => 412,
        SupplyControlErrorCodes.ChannelInUse => 409,
        _ => throw new InvalidOperationException(
            "The Supply failure replay code is invalid."),
    };

    private static JsonElement SerializeReplayValue<T>(T value) => value switch
    {
        ChannelView channel =>
            JsonSerializer.SerializeToElement(ChannelViewReplay.From(channel)),
        GroupSupplyConfigurationView configuration =>
            JsonSerializer.SerializeToElement(
                GroupSupplyConfigurationViewReplay.From(configuration)),
        _ => throw new InvalidOperationException(
            "The Supply replay type is unsupported."),
    };

    private static T DeserializeReplayValue<T>(JsonElement body)
    {
        object value = typeof(T) == typeof(ChannelView)
            ? body.Deserialize<ChannelViewReplay>()?.ToView()
                ?? throw new InvalidOperationException(
                    "The Channel replay body is invalid.")
            : typeof(T) == typeof(GroupSupplyConfigurationView)
                ? body.Deserialize<GroupSupplyConfigurationViewReplay>()?.ToView()
                    ?? throw new InvalidOperationException(
                        "The Group Supply replay body is invalid.")
                : throw new InvalidOperationException(
                    "The Supply replay type is unsupported.");
        return (T)value;
    }

    private static SupplyReplayIdentity ReplayIdentity<T>(
        T value,
        int expectedStatus)
    {
        if (expectedStatus is not 200 and not 201)
        {
            throw new InvalidOperationException(
                "The Supply replay response status is unsupported.");
        }

        return value switch
        {
            ChannelView channel => new SupplyReplayIdentity(
                channel.Id,
                channel.Version,
                expectedStatus == 201
                    ? $"/api/v1/admin/channels/{channel.Id.Value:D}"
                    : null),
            GroupSupplyConfigurationView configuration =>
                new SupplyReplayIdentity(
                    configuration.GroupId,
                    configuration.Version,
                    expectedStatus == 201
                        ? $"/api/v1/admin/groups/{configuration.GroupId.Value:D}/supply-configuration"
                        : null),
            _ => throw new InvalidOperationException(
                "The Supply replay type is unsupported."),
        };
    }

    private static AuditActorType AuditActor(AccountControlRole role) => role switch
    {
        AccountControlRole.Admin => AuditActorType.Admin,
        AccountControlRole.Operator => AuditActorType.Operator,
        AccountControlRole.Auditor => AuditActorType.Auditor,
        AccountControlRole.User => AuditActorType.User,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private static Result<T> Failure<T>(
        string code,
        string description,
        long? retryAfterSeconds = null,
        string? etag = null) => Result.Failure<T>(
        code,
        description,
        retryAfterSeconds,
        etag);

    private sealed record SupplyReplayIdentity(
        EntityId ResourceId,
        long Version,
        string? Location);

    private sealed record ChannelModelMappingReplay(
        string ClientModel,
        string UpstreamModel);

    private sealed record ChannelViewReplay(
        Guid Id,
        string Name,
        string Provider,
        string Status,
        ChannelCapabilitiesSnapshot Capabilities,
        IReadOnlyList<ChannelModelMappingReplay> ModelMappings,
        long Version,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt)
    {
        internal static ChannelViewReplay From(ChannelView value) => new(
            value.Id.Value,
            value.Name,
            ProviderCode(value.Provider),
            LifecycleCode(value.Status),
            value.Capabilities,
            value.ModelMappings.Select(static mapping =>
                new ChannelModelMappingReplay(
                    mapping.ClientModel,
                    mapping.UpstreamModel)).ToArray(),
            value.Version,
            value.CreatedAt,
            value.UpdatedAt);

        internal ChannelView ToView()
        {
            if (Id == Guid.Empty
                || Capabilities is null
                || ModelMappings is null
                || Version <= 0
                || CreatedAt == default
                || UpdatedAt == default)
            {
                throw new InvalidOperationException(
                    "The Channel replay body is invalid.");
            }

            string name = ChannelInput.Name(Name);
            IReadOnlyList<ChannelModelMappingValue> mappings =
                ChannelInput.ModelMappings(ModelMappings.Select(
                    static mapping => new ChannelModelMappingValue(
                        mapping.ClientModel,
                        mapping.UpstreamModel)));
            return new ChannelView(
                new EntityId(Id),
                name,
                ParseProvider(Provider),
                ParseLifecycle(Status),
                Capabilities,
                mappings.Select(static mapping => new ChannelModelMappingView(
                    mapping.ClientModel,
                    mapping.UpstreamModel)).ToArray(),
                Version,
                CreatedAt,
                UpdatedAt);
        }

        private static string ProviderCode(UpstreamProvider provider) =>
            provider switch
            {
                UpstreamProvider.OpenAi => "openai",
                UpstreamProvider.OpenAiCompatible => "openai_compatible",
                _ => throw new ArgumentOutOfRangeException(nameof(provider)),
            };

        private static UpstreamProvider ParseProvider(string value) => value switch
        {
            "openai" => UpstreamProvider.OpenAi,
            "openai_compatible" => UpstreamProvider.OpenAiCompatible,
            _ => throw new InvalidOperationException(
                "The Channel replay provider is invalid."),
        };

        private static string LifecycleCode(ChannelLifecycle status) =>
            status switch
            {
                ChannelLifecycle.Active => "active",
                ChannelLifecycle.Disabled => "disabled",
                ChannelLifecycle.Retired => "retired",
                _ => throw new ArgumentOutOfRangeException(nameof(status)),
            };

        private static ChannelLifecycle ParseLifecycle(string value) => value switch
        {
            "active" => ChannelLifecycle.Active,
            "disabled" => ChannelLifecycle.Disabled,
            "retired" => ChannelLifecycle.Retired,
            _ => throw new InvalidOperationException(
                "The Channel replay lifecycle is invalid."),
        };
    }

    private sealed record GroupSupplyBindingReplay(
        Guid AccountId,
        bool Enabled,
        int? PriorityOverride,
        int? WeightOverride);

    private sealed record GroupSupplyConfigurationViewReplay(
        Guid GroupId,
        Guid? ChannelId,
        IReadOnlyList<GroupSupplyBindingReplay> AccountBindings,
        long Version,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt)
    {
        internal static GroupSupplyConfigurationViewReplay From(
            GroupSupplyConfigurationView value) => new(
            value.GroupId.Value,
            value.ChannelId?.Value,
            value.AccountBindings.Select(static binding =>
                new GroupSupplyBindingReplay(
                    binding.AccountId.Value,
                    binding.Enabled,
                    binding.PriorityOverride,
                    binding.WeightOverride)).ToArray(),
            value.Version,
            value.CreatedAt,
            value.UpdatedAt);

        internal GroupSupplyConfigurationView ToView()
        {
            if (GroupId == Guid.Empty
                || ChannelId == Guid.Empty
                || AccountBindings is null
                || AccountBindings.Any(static binding =>
                    binding.AccountId == Guid.Empty)
                || Version <= 0
                || CreatedAt == default
                || UpdatedAt == default)
            {
                throw new InvalidOperationException(
                    "The Group Supply replay body is invalid.");
            }

            IReadOnlyList<GroupSupplyBindingValue> bindings =
                GroupSupplyInput.Bindings(AccountBindings.Select(
                    static binding => new GroupSupplyBindingValue(
                        new EntityId(binding.AccountId),
                        binding.Enabled,
                        binding.PriorityOverride,
                        binding.WeightOverride)));
            return new GroupSupplyConfigurationView(
                new EntityId(GroupId),
                ChannelId is Guid channelId ? new EntityId(channelId) : null,
                bindings.Select(static binding => new GroupSupplyBindingView(
                    binding.AccountId,
                    binding.Enabled,
                    binding.PriorityOverride,
                    binding.WeightOverride)).ToArray(),
                Version,
                CreatedAt,
                UpdatedAt);
        }
    }
}
#pragma warning restore MA0048
