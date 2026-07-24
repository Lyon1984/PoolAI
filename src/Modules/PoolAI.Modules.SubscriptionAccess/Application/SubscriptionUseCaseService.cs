#pragma warning disable MA0051 // Transactional command sequences are intentionally visible.
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.SubscriptionAccess.Abstractions;
using PoolAI.Modules.SubscriptionAccess.Application.Ports;
using PoolAI.Modules.SubscriptionAccess.Domain;

namespace PoolAI.Modules.SubscriptionAccess.Application;

internal sealed class SubscriptionUseCaseService :
    IListSubscriptionTemplatesUseCase,
    IGetSubscriptionTemplateUseCase,
    ICreateSubscriptionTemplateUseCase,
    IUpdateSubscriptionTemplateUseCase,
    IRetireSubscriptionTemplateUseCase,
    IListSubscriptionsUseCase,
    IGetSubscriptionUseCase,
    IAssignSubscriptionUseCase,
    IUpdateSubscriptionUseCase,
    ISubscriptionAccessReader,
    IUserSubscriptionGrantReader
{
    private const string EventTopic = "poolai.subscription-access.v1";
    private const int EventSchemaVersion = 1;
    private static readonly TimeSpan IdempotencyLease = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdempotencyRetention = TimeSpan.FromHours(24);
    private static readonly JsonElement EmptyObject = JsonSerializer.SerializeToElement(
        new Dictionary<string, string>(StringComparer.Ordinal));

    private readonly ISubscriptionRepository _repository;
    private readonly IUserStatusReader _userStatusReader;
    private readonly IUnitOfWorkFactory _unitOfWorkFactory;
    private readonly ICommandIdempotencyStore _idempotencyStore;
    private readonly IAuditAppender _auditAppender;
    private readonly IOutboxAppender _outboxAppender;
    private readonly SubscriptionPolicy _policy;

    internal SubscriptionUseCaseService(
        ISubscriptionRepository repository,
        IUserStatusReader userStatusReader,
        IUnitOfWorkFactory unitOfWorkFactory,
        ICommandIdempotencyStore idempotencyStore,
        IAuditAppender auditAppender,
        IOutboxAppender outboxAppender,
        SubscriptionPolicy policy)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _userStatusReader = userStatusReader ?? throw new ArgumentNullException(nameof(userStatusReader));
        _unitOfWorkFactory = unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
        _idempotencyStore = idempotencyStore ?? throw new ArgumentNullException(nameof(idempotencyStore));
        _auditAppender = auditAppender ?? throw new ArgumentNullException(nameof(auditAppender));
        _outboxAppender = outboxAppender ?? throw new ArgumentNullException(nameof(outboxAppender));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public async ValueTask<Result<SubscriptionTemplatePage>> ExecuteAsync(
        ListSubscriptionTemplatesQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!CanReadAdminResources(query.Actor))
        {
            return Failure<SubscriptionTemplatePage>(
                SubscriptionErrorCodes.RoleRequired,
                "The actor role cannot read Subscription Templates.");
        }

        if (!TryValidatePage(query.Cursor, query.Limit, out SubscriptionCursor? cursor))
        {
            return Failure<SubscriptionTemplatePage>(
                SubscriptionErrorCodes.InvalidRequest,
                "The pagination request is invalid.");
        }

        SubscriptionTemplateSlice slice = await _repository
            .ListTemplatesAsync(cursor, query.Limit, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<SubscriptionTemplateView> data = slice.Items
            .Select(static value => value.ToView())
            .ToArray();
        return Result.Success(new SubscriptionTemplatePage(
            data,
            NextCursor(slice.Items, slice.HasMore),
            slice.HasMore));
    }

    public async ValueTask<Result<SubscriptionTemplateView>> ExecuteAsync(
        GetSubscriptionTemplateQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!CanReadAdminResources(query.Actor))
        {
            return Failure<SubscriptionTemplateView>(
                SubscriptionErrorCodes.RoleRequired,
                "The actor role cannot read Subscription Templates.");
        }

        SubscriptionTemplateRecord? value = await _repository
            .GetTemplateAsync(query.TemplateId, cancellationToken)
            .ConfigureAwait(false);
        return value is null
            ? Failure<SubscriptionTemplateView>(
                SubscriptionErrorCodes.ResourceNotFound,
                "The Subscription Template does not exist.")
            : Result.Success(value.ToView());
    }

    public async ValueTask<Result<SubscriptionCommandOutcome<SubscriptionTemplateView>>> ExecuteAsync(
        CreateSubscriptionTemplateCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!CanManageSubscriptions(command.Actor))
        {
            return RoleFailure<SubscriptionTemplateView>();
        }

        string name;
        string? description;
        int duration;
        byte[] requestHash;
        try
        {
            SubscriptionInput.IdempotencyKey(command.IdempotencyKey);
            name = SubscriptionInput.Name(command.Name);
            description = SubscriptionInput.Description(command.Description);
            duration = SubscriptionInput.DurationDays(command.DefaultDurationDays);
            requestHash = HashRequest(new
            {
                group_id = command.GroupId.Value,
                name,
                description,
                default_duration_days = duration,
            });
        }
        catch (ArgumentException)
        {
            return ValidationFailure<SubscriptionTemplateView>(
                "The create-Template request is invalid.");
        }

        IUnitOfWork unitOfWork = await BeginAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lease = unitOfWork.ConfigureAwait(false);
        CommandIdempotencyAcquireResult acquire = await AcquireAsync(
            Scope(command.Actor, "post", "/api/v1/admin/subscription-templates"),
            command,
            requestHash,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Result<SubscriptionCommandOutcome<SubscriptionTemplateView>>? early =
            ReplayOrAcquireFailure<SubscriptionTemplateView>(acquire);
        if (early is not null)
        {
            return early;
        }

        CommandIdempotencyLease idempotencyLease = acquire.Lease!;
        EntityId templateId = EntityId.New();
        TemplateMutationResult mutation = await _repository.CreateTemplateAsync(
            templateId,
            command.GroupId,
            name,
            description,
            duration,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (mutation.Disposition != SubscriptionMutationDisposition.Updated)
        {
            return await CompleteCreateMutationFailureAsync<
                SubscriptionCommandOutcome<SubscriptionTemplateView>>(
                idempotencyLease,
                mutation,
                "The Group required to create the Subscription Template does not exist.",
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        if (!mutation.WasChanged)
        {
            throw new InvalidOperationException(
                "A successful Template creation must report a changed resource.");
        }

        SubscriptionTemplateView view = mutation.Value!.ToView();
        await AppendAuditAsync(
            command.Actor,
            "subscription_access.template.created",
            "subscription_template",
            view.Id,
            command.RequestId,
            reason: null,
            command.IpAddress,
            command.UserAgent,
            before: null,
            TemplateAuditState(view),
            command.IdempotencyKey,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(
            "template_created",
            "subscription_template",
            view.Id,
            view.Version,
            command.RequestId,
            view.UpdatedAt,
            TemplateAuditState(view),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        string etag = ETag(view.Version);
        string location = $"/api/v1/admin/subscription-templates/{view.Id.Value:D}";
        await CompleteSuccessAsync(
            idempotencyLease,
            201,
            view,
            Headers(etag, location),
            "subscription_template",
            view.Id,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(new SubscriptionCommandOutcome<SubscriptionTemplateView>(
            201,
            false,
            view,
            etag,
            location));
    }

    public async ValueTask<Result<SubscriptionCommandOutcome<SubscriptionTemplateView>>> ExecuteAsync(
        UpdateSubscriptionTemplateCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!CanManageSubscriptions(command.Actor))
        {
            return RoleFailure<SubscriptionTemplateView>();
        }

        string? name;
        string? description;
        int? duration;
        string? reason;
        byte[] requestHash;
        try
        {
            SubscriptionInput.IdempotencyKey(command.IdempotencyKey);
            SubscriptionInput.ExpectedVersion(command.ExpectedVersion);
            if (!command.NameSpecified
                && !command.DescriptionSpecified
                && !command.DefaultDurationDaysSpecified
                && !command.StatusSpecified)
            {
                throw new ArgumentException("The update is empty.", nameof(command));
            }

            name = command.NameSpecified ? SubscriptionInput.Name(command.Name!) : null;
            description = command.DescriptionSpecified
                ? SubscriptionInput.Description(command.Description)
                : null;
            duration = command.DefaultDurationDaysSpecified
                ? SubscriptionInput.DurationDays(command.DefaultDurationDays ?? 0)
                : null;
            if (command.StatusSpecified
                && command.Status is not (SubscriptionTemplateLifecycle.Active
                    or SubscriptionTemplateLifecycle.Disabled))
            {
                throw new ArgumentException("Template retirement requires DELETE.", nameof(command));
            }

            reason = command.StatusSpecified
                ? SubscriptionInput.Reason(command.Reason ?? string.Empty)
                : command.Reason is null ? null : SubscriptionInput.Reason(command.Reason);
            requestHash = HashRequest(new
            {
                template_id = command.TemplateId.Value,
                expected_version = command.ExpectedVersion,
                set_name = command.NameSpecified,
                name,
                set_description = command.DescriptionSpecified,
                description,
                set_duration = command.DefaultDurationDaysSpecified,
                default_duration_days = duration,
                set_status = command.StatusSpecified,
                status = command.StatusSpecified ? TemplateStatusCode(command.Status!.Value) : null,
                reason,
            });
        }
        catch (ArgumentException)
        {
            return ValidationFailure<SubscriptionTemplateView>(
                "The update-Template request is invalid.");
        }

        IUnitOfWork unitOfWork = await BeginAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lease = unitOfWork.ConfigureAwait(false);
        CommandIdempotencyAcquireResult acquire = await AcquireAsync(
            Scope(command.Actor, "patch", $"/api/v1/admin/subscription-templates/{command.TemplateId.Value:D}"),
            command,
            requestHash,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Result<SubscriptionCommandOutcome<SubscriptionTemplateView>>? early =
            ReplayOrAcquireFailure<SubscriptionTemplateView>(acquire);
        if (early is not null)
        {
            return early;
        }

        CommandIdempotencyLease idempotencyLease = acquire.Lease!;
        TemplateMutationResult mutation = await _repository.UpdateTemplateAsync(
            command.TemplateId,
            command.ExpectedVersion,
            command.NameSpecified,
            name,
            command.DescriptionSpecified,
            description,
            command.DefaultDurationDaysSpecified,
            duration,
            command.StatusSpecified,
            command.Status,
            retire: false,
            reason,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (mutation.Disposition != SubscriptionMutationDisposition.Updated)
        {
            return await CompleteMutationFailureAsync<SubscriptionCommandOutcome<SubscriptionTemplateView>>(
                idempotencyLease,
                mutation,
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }


        SubscriptionTemplateView view = mutation.Value!.ToView();
        if (mutation.WasChanged)
        {
            JsonElement beforeState = mutation.BeforeState
                ?? throw new InvalidOperationException(
                    "A changed Template mutation must return its before-state snapshot.");
            await AppendAuditAsync(
                command.Actor,
                "subscription_access.template.updated",
                "subscription_template",
                view.Id,
                command.RequestId,
                reason,
                command.IpAddress,
                command.UserAgent,
                beforeState,
                TemplateAuditState(view),
                command.IdempotencyKey,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(
                "template_updated",
                "subscription_template",
                view.Id,
                view.Version,
                command.RequestId,
                view.UpdatedAt,
                TemplateAuditState(view),
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
        }

        string etag = ETag(view.Version);
        await CompleteSuccessAsync(
            idempotencyLease,
            200,
            view,
            Headers(etag),
            "subscription_template",
            view.Id,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(new SubscriptionCommandOutcome<SubscriptionTemplateView>(
            200,
            false,
            view,
            etag));
    }

    public async ValueTask<Result<SubscriptionCommandOutcome>> ExecuteAsync(
        RetireSubscriptionTemplateCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!CanManageSubscriptions(command.Actor))
        {
            return Failure<SubscriptionCommandOutcome>(
                SubscriptionErrorCodes.RoleRequired,
                "The Admin or Operator role is required.");
        }

        string reason;
        byte[] requestHash;
        try
        {
            SubscriptionInput.IdempotencyKey(command.IdempotencyKey);
            SubscriptionInput.ExpectedVersion(command.ExpectedVersion);
            reason = SubscriptionInput.Reason(command.Reason);
            requestHash = HashRequest(new
            {
                template_id = command.TemplateId.Value,
                expected_version = command.ExpectedVersion,
                reason,
            });
        }
        catch (ArgumentException)
        {
            return Failure<SubscriptionCommandOutcome>(
                SubscriptionErrorCodes.ValidationFailed,
                "The retire-Template request is invalid.");
        }

        IUnitOfWork unitOfWork = await BeginAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lease = unitOfWork.ConfigureAwait(false);
        CommandIdempotencyAcquireResult acquire = await AcquireAsync(
            Scope(command.Actor, "delete", $"/api/v1/admin/subscription-templates/{command.TemplateId.Value:D}"),
            command,
            requestHash,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Result<SubscriptionCommandOutcome>? early = ReplayRetireOrAcquireFailure(
            acquire,
            command.TemplateId);
        if (early is not null)
        {
            return early;
        }

        CommandIdempotencyLease idempotencyLease = acquire.Lease!;
        TemplateMutationResult mutation = await _repository.UpdateTemplateAsync(
            command.TemplateId,
            command.ExpectedVersion,
            nameSpecified: false,
            name: null,
            descriptionSpecified: false,
            description: null,
            durationSpecified: false,
            durationDays: null,
            statusSpecified: false,
            status: null,
            retire: true,
            reason,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (mutation.Disposition != SubscriptionMutationDisposition.Updated)
        {
            return await CompleteMutationFailureAsync<SubscriptionCommandOutcome>(
                idempotencyLease,
                mutation,
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        if (!mutation.WasChanged)
        {
            throw new InvalidOperationException(
                "A successful Template retirement must report a changed resource.");
        }

        SubscriptionTemplateView view = mutation.Value!.ToView();
        JsonElement beforeState = mutation.BeforeState
            ?? throw new InvalidOperationException(
                "A retired Template mutation must return its before-state snapshot.");
        await AppendAuditAsync(
            command.Actor,
            "subscription_access.template.retired",
            "subscription_template",
            view.Id,
            command.RequestId,
            reason,
            command.IpAddress,
            command.UserAgent,
            beforeState,
            TemplateAuditState(view),
            command.IdempotencyKey,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(
            "template_retired",
            "subscription_template",
            view.Id,
            view.Version,
            command.RequestId,
            view.UpdatedAt,
            TemplateAuditState(view),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        string etag = ETag(view.Version);
        await CompleteSuccessAsync<SubscriptionTemplateView>(
            idempotencyLease,
            204,
            body: null,
            Headers(etag),
            "subscription_template",
            view.Id,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(new SubscriptionCommandOutcome(204, false, etag));
    }

    public async ValueTask<Result<SubscriptionPage>> ExecuteAsync(
        ListSubscriptionsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.IsSelfQuery)
        {
            if (!IsAuthenticated(query.Actor)
                || query.UserId is not null && query.UserId != query.Actor.UserId
                || query.GroupId is not null)
            {
                return Failure<SubscriptionPage>(
                    SubscriptionErrorCodes.RoleRequired,
                    "A self query can read only the current user's Subscriptions.");
            }

            IReadOnlyList<SubscriptionRecord> ownSubscriptions =
                await _repository.ListForUserAsync(
                    query.Actor.UserId,
                    cancellationToken).ConfigureAwait(false);
            return Result.Success(new SubscriptionPage(
                ownSubscriptions.Select(static value => value.ToView()).ToArray(),
                NextCursor: null,
                HasMore: false));
        }

        if (!CanReadAdminResources(query.Actor))
        {
            return Failure<SubscriptionPage>(
                SubscriptionErrorCodes.RoleRequired,
                "The actor role cannot read Subscriptions.");
        }

        if (!TryValidatePage(query.Cursor, query.Limit, out SubscriptionCursor? cursor))
        {
            return Failure<SubscriptionPage>(
                SubscriptionErrorCodes.InvalidRequest,
                "The pagination request is invalid.");
        }

        SubscriptionSlice slice = await _repository.ListSubscriptionsAsync(
            cursor,
            query.Limit,
            query.UserId,
            query.GroupId,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SubscriptionView> data = slice.Items
            .Select(static value => value.ToView())
            .ToArray();
        return Result.Success(new SubscriptionPage(
            data,
            NextCursor(slice.Items, slice.HasMore),
            slice.HasMore));
    }

    public async ValueTask<Result<SubscriptionView>> ExecuteAsync(
        GetSubscriptionQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!CanReadAdminResources(query.Actor))
        {
            return Failure<SubscriptionView>(
                SubscriptionErrorCodes.RoleRequired,
                "The actor role cannot read Subscriptions.");
        }

        SubscriptionRecord? value = await _repository.GetSubscriptionAsync(
            query.SubscriptionId,
            visibleToUserId: null,
            cancellationToken).ConfigureAwait(false);
        return value is null
            ? Failure<SubscriptionView>(
                SubscriptionErrorCodes.ResourceNotFound,
                "The Subscription does not exist.")
            : Result.Success(value.ToView());
    }

    public async ValueTask<Result<SubscriptionCommandOutcome<SubscriptionView>>> ExecuteAsync(
        AssignSubscriptionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!CanManageSubscriptions(command.Actor))
        {
            return RoleFailure<SubscriptionView>();
        }

        string reason;
        byte[] requestHash;
        try
        {
            SubscriptionInput.IdempotencyKey(command.IdempotencyKey);
            reason = SubscriptionInput.Reason(command.Reason);
            if (command.StartsAt is not null && command.ExpiresAt is not null)
            {
                SubscriptionInput.TimeRange(command.StartsAt.Value, command.ExpiresAt.Value);
            }

            requestHash = HashRequest(new
            {
                user_id = command.UserId.Value,
                template_id = command.TemplateId.Value,
                starts_at = command.StartsAt?.ToUniversalTime(),
                expires_at = command.ExpiresAt?.ToUniversalTime(),
                reason,
            });
        }
        catch (ArgumentException)
        {
            return ValidationFailure<SubscriptionView>(
                "The assign-Subscription request is invalid.");
        }

        IUnitOfWork unitOfWork = await BeginAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lease = unitOfWork.ConfigureAwait(false);
        CommandIdempotencyAcquireResult acquire = await AcquireAsync(
            Scope(command.Actor, "post", "/api/v1/admin/subscriptions"),
            command,
            requestHash,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Result<SubscriptionCommandOutcome<SubscriptionView>>? early =
            ReplayOrAcquireFailure<SubscriptionView>(acquire);
        if (early is not null)
        {
            return early;
        }

        CommandIdempotencyLease idempotencyLease = acquire.Lease!;
        Result<UserStatusSnapshot> userStatus = await _userStatusReader
            .GetCurrentAsync(command.UserId, cancellationToken)
            .ConfigureAwait(false);
        if (userStatus.IsFailure)
        {
            if (!string.Equals(
                    userStatus.Error.Code,
                    SubscriptionErrorCodes.ResourceNotFound,
                    StringComparison.Ordinal))
            {
                return ForwardFailure<SubscriptionCommandOutcome<SubscriptionView>>(
                    userStatus.Error);
            }

            return await CompleteFailureAsync<SubscriptionCommandOutcome<SubscriptionView>>(
                idempotencyLease,
                TargetUserConflict(),
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        if (userStatus.Value.Lifecycle != UserLifecycle.Active)
        {
            return await CompleteFailureAsync<SubscriptionCommandOutcome<SubscriptionView>>(
                idempotencyLease,
                TargetUserConflict(),
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        EntityId subscriptionId = EntityId.New();
        SubscriptionMutationResult mutation = await _repository.AssignSubscriptionAsync(
            subscriptionId,
            command.UserId,
            command.TemplateId,
            command.StartsAt,
            command.ExpiresAt,
            command.Actor.UserId,
            reason,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (mutation.Disposition != SubscriptionMutationDisposition.Updated)
        {
            return await CompleteCreateMutationFailureAsync<
                SubscriptionCommandOutcome<SubscriptionView>>(
                idempotencyLease,
                mutation,
                "The Subscription Template required to assign the Subscription does not exist.",
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }


        if (!mutation.WasChanged)
        {
            throw new InvalidOperationException(
                "A successful Subscription assignment must report a changed resource.");
        }

        SubscriptionView view = mutation.Value!.ToView();
        await AppendAuditAsync(
            command.Actor,
            "subscription_access.subscription.assigned",
            "subscription",
            view.Id,
            command.RequestId,
            reason,
            command.IpAddress,
            command.UserAgent,
            before: null,
            SubscriptionAuditState(view),
            command.IdempotencyKey,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(
            "subscription_assigned",
            "subscription",
            view.Id,
            view.Version,
            command.RequestId,
            view.UpdatedAt,
            SubscriptionAuditState(view),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        string etag = ETag(view.Version);
        string location = $"/api/v1/admin/subscriptions/{view.Id.Value:D}";
        await CompleteSuccessAsync(
            idempotencyLease,
            201,
            view,
            Headers(etag, location),
            "subscription",
            view.Id,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(new SubscriptionCommandOutcome<SubscriptionView>(
            201,
            false,
            view,
            etag,
            location));
    }

    public async ValueTask<Result<SubscriptionCommandOutcome<SubscriptionView>>> ExecuteAsync(
        UpdateSubscriptionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!CanManageSubscriptions(command.Actor))
        {
            return RoleFailure<SubscriptionView>();
        }

        string reason;
        byte[] requestHash;
        try
        {
            SubscriptionInput.IdempotencyKey(command.IdempotencyKey);
            SubscriptionInput.ExpectedVersion(command.ExpectedVersion);
            reason = SubscriptionInput.Reason(command.Reason);
            if (!command.StartsAtSpecified && !command.ExpiresAtSpecified && !command.StatusSpecified)
            {
                throw new ArgumentException("The Subscription update is empty.", nameof(command));
            }

            if (command.StartsAtSpecified && command.StartsAt is null
                || command.ExpiresAtSpecified && command.ExpiresAt is null
                || command.StatusSpecified && command.Status is null)
            {
                throw new ArgumentException(
                    "A selected Subscription field cannot be null.",
                    nameof(command));
            }

            if (command.StartsAtSpecified && command.ExpiresAtSpecified)
            {
                SubscriptionInput.TimeRange(command.StartsAt!.Value, command.ExpiresAt!.Value);
            }

            requestHash = HashRequest(new
            {
                subscription_id = command.SubscriptionId.Value,
                expected_version = command.ExpectedVersion,
                set_starts_at = command.StartsAtSpecified,
                starts_at = command.StartsAt?.ToUniversalTime(),
                set_expires_at = command.ExpiresAtSpecified,
                expires_at = command.ExpiresAt?.ToUniversalTime(),
                set_status = command.StatusSpecified,
                status = command.StatusSpecified ? SubscriptionStatusCode(command.Status!.Value) : null,
                reason,
            });
        }
        catch (ArgumentException)
        {
            return ValidationFailure<SubscriptionView>(
                "The update-Subscription request is invalid.");
        }

        IUnitOfWork unitOfWork = await BeginAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lease = unitOfWork.ConfigureAwait(false);
        CommandIdempotencyAcquireResult acquire = await AcquireAsync(
            Scope(command.Actor, "patch", $"/api/v1/admin/subscriptions/{command.SubscriptionId.Value:D}"),
            command,
            requestHash,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Result<SubscriptionCommandOutcome<SubscriptionView>>? early =
            ReplayOrAcquireFailure<SubscriptionView>(acquire);
        if (early is not null)
        {
            return early;
        }

        CommandIdempotencyLease idempotencyLease = acquire.Lease!;
        SubscriptionMutationResult mutation = await _repository.UpdateSubscriptionAsync(
            command.SubscriptionId,
            command.ExpectedVersion,
            command.StartsAtSpecified,
            command.StartsAt,
            command.ExpiresAtSpecified,
            command.ExpiresAt,
            command.StatusSpecified,
            command.Status,
            allowRevokedRegrant: command.Actor.Role == SystemRole.Admin,
            command.Actor.UserId,
            reason,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (mutation.Disposition != SubscriptionMutationDisposition.Updated)
        {
            if (IsForbiddenOperatorRegrant(command, mutation))
            {
                return await CompleteFailureAsync<SubscriptionCommandOutcome<SubscriptionView>>(
                    idempotencyLease,
                    new MutationFailure(
                        403,
                        SubscriptionErrorCodes.RoleRequired,
                        "Only an Admin can regrant a revoked Subscription.",
                        null),
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false);
            }

            return await CompleteMutationFailureAsync<SubscriptionCommandOutcome<SubscriptionView>>(
                idempotencyLease,
                mutation,
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        SubscriptionView view = mutation.Value!.ToView();
        if (mutation.WasChanged)
        {
            JsonElement beforeState = mutation.BeforeState
                ?? throw new InvalidOperationException(
                    "A changed Subscription mutation must return its before-state snapshot.");
            await AppendAuditAsync(
                command.Actor,
                "subscription_access.subscription.updated",
                "subscription",
                view.Id,
                command.RequestId,
                reason,
                command.IpAddress,
                command.UserAgent,
                beforeState,
                SubscriptionAuditState(view),
                command.IdempotencyKey,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(
                "subscription_updated",
                "subscription",
                view.Id,
                view.Version,
                command.RequestId,
                view.UpdatedAt,
                SubscriptionAuditState(view),
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
        }

        string etag = ETag(view.Version);
        await CompleteSuccessAsync(
            idempotencyLease,
            200,
            view,
            Headers(etag),
            "subscription",
            view.Id,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(new SubscriptionCommandOutcome<SubscriptionView>(
            200,
            false,
            view,
            etag));
    }

    public async ValueTask<Result<SubscriptionAccessSnapshot>> GetEffectiveAccessAsync(
        EntityId userId,
        EntityId groupId,
        CancellationToken cancellationToken)
    {
        SubscriptionRecord? value = await _repository.GetEffectiveAccessAsync(
            userId,
            groupId,
            cancellationToken).ConfigureAwait(false);
        if (value is null)
        {
            return Failure<SubscriptionAccessSnapshot>(
                "subscription_required",
                "No canonical Subscription exists for the requested Group.");
        }

        if (value.EffectiveStatus != SubscriptionEffectiveLifecycle.Active)
        {
            return Failure<SubscriptionAccessSnapshot>(
                "subscription_inactive",
                "The canonical Subscription is not currently active.");
        }

        return Result.Success(new SubscriptionAccessSnapshot(
            value.Id,
            value.UserId,
            value.GroupId,
            value.PlanName,
            value.StartsAt,
            value.ExpiresAt,
            ToAbstractionStatus(value.EffectiveStatus),
            value.Version,
            value.ObservedAt));
    }

    public async ValueTask<Result<IReadOnlyList<UserSubscriptionGrantSnapshot>>> ListActiveAsync(
        EntityId userId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SubscriptionRecord> values = await _repository.ListActiveForUserAsync(
            userId,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<UserSubscriptionGrantSnapshot> snapshots = values
            .Select(static value => new UserSubscriptionGrantSnapshot(
                value.Id,
                value.UserId,
                value.GroupId,
                value.PlanName,
                value.ExpiresAt,
                value.UpdatedAt))
            .ToArray();
        return Result.Success(snapshots);
    }

    private ValueTask<IUnitOfWork> BeginAsync(CancellationToken cancellationToken) =>
        _unitOfWorkFactory.BeginAsync(cancellationToken);

    private ValueTask<CommandIdempotencyAcquireResult> AcquireAsync<T>(
        string scope,
        T command,
        byte[] requestHash,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        (string key, EntityId requestId, SubscriptionActor actor) = command switch
        {
            CreateSubscriptionTemplateCommand value =>
                (value.IdempotencyKey, value.RequestId, value.Actor),
            UpdateSubscriptionTemplateCommand value =>
                (value.IdempotencyKey, value.RequestId, value.Actor),
            RetireSubscriptionTemplateCommand value =>
                (value.IdempotencyKey, value.RequestId, value.Actor),
            AssignSubscriptionCommand value =>
                (value.IdempotencyKey, value.RequestId, value.Actor),
            UpdateSubscriptionCommand value =>
                (value.IdempotencyKey, value.RequestId, value.Actor),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
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

    private static Result<SubscriptionCommandOutcome<T>>? ReplayOrAcquireFailure<T>(
        CommandIdempotencyAcquireResult acquire) => acquire.Disposition switch
        {
            CommandIdempotencyDisposition.Acquired => null,
            CommandIdempotencyDisposition.Conflict =>
                Failure<SubscriptionCommandOutcome<T>>(
                    SubscriptionErrorCodes.IdempotencyConflict,
                    "The idempotency key was used for a different request."),
            CommandIdempotencyDisposition.Busy =>
                Failure<SubscriptionCommandOutcome<T>>(
                    SubscriptionErrorCodes.CoordinationUnavailable,
                    "The matching idempotent command is still in progress."),
            CommandIdempotencyDisposition.Replay => Replay<T>(acquire.Response!),
            _ => throw new ArgumentOutOfRangeException(nameof(acquire)),
        };

    private static Result<SubscriptionCommandOutcome>? ReplayRetireOrAcquireFailure(
        CommandIdempotencyAcquireResult acquire,
        EntityId templateId) => acquire.Disposition switch
        {
            CommandIdempotencyDisposition.Acquired => null,
            CommandIdempotencyDisposition.Conflict =>
                Failure<SubscriptionCommandOutcome>(
                    SubscriptionErrorCodes.IdempotencyConflict,
                    "The idempotency key was used for a different request."),
            CommandIdempotencyDisposition.Busy =>
                Failure<SubscriptionCommandOutcome>(
                    SubscriptionErrorCodes.CoordinationUnavailable,
                    "The matching idempotent command is still in progress."),
            CommandIdempotencyDisposition.Replay => ReplayRetire(acquire.Response!, templateId),
            _ => throw new ArgumentOutOfRangeException(nameof(acquire)),
        };

    private static Result<SubscriptionCommandOutcome<T>> Replay<T>(
        CommandIdempotencyResponse response)
    {
        if (response.TerminalStatus == CommandIdempotencyTerminalStatus.Failed)
        {
            ReplayFailure failure = ParseReplayFailure(response);
            return Failure<SubscriptionCommandOutcome<T>>(
                failure.Presentation.Code,
                failure.Description,
                etag: Header(response.Headers, "ETag"),
                presentation: failure.Presentation);
        }

        if (response.Body is null)
        {
            throw new InvalidOperationException("The idempotency replay body is missing.");
        }

        T value = DeserializeReplay<T>(response.Body.Value);
        long version = VersionOf(value);
        EntityId id = IdOf(value);
        string etag = Header(response.Headers, "ETag")
            ?? throw new InvalidOperationException("The idempotency replay ETag is missing.");
        string? location = Header(response.Headers, "Location");
        if (response.TerminalStatus != CommandIdempotencyTerminalStatus.Completed
            || response.Status is not (200 or 201)
            || response.ResourceId != id
            || !string.Equals(etag, ETag(version), StringComparison.Ordinal)
            || response.Status == 201 && string.IsNullOrWhiteSpace(location))
        {
            throw new InvalidOperationException("The idempotency replay is invalid.");
        }

        return Result.Success(new SubscriptionCommandOutcome<T>(
            response.Status,
            true,
            value,
            etag,
            location));
    }

    private static Result<SubscriptionCommandOutcome> ReplayRetire(
        CommandIdempotencyResponse response,
        EntityId templateId)
    {
        if (response.TerminalStatus == CommandIdempotencyTerminalStatus.Failed)
        {
            ReplayFailure failure = ParseReplayFailure(response);
            return Failure<SubscriptionCommandOutcome>(
                failure.Presentation.Code,
                failure.Description,
                etag: Header(response.Headers, "ETag"),
                presentation: failure.Presentation);
        }

        string etag = Header(response.Headers, "ETag")
            ?? throw new InvalidOperationException("The retirement replay ETag is missing.");
        if (response.TerminalStatus != CommandIdempotencyTerminalStatus.Completed
            || response.Status != 204
            || response.Body is not null
            || response.ResourceId != templateId)
        {
            throw new InvalidOperationException("The Template retirement replay is invalid.");
        }

        return Result.Success(new SubscriptionCommandOutcome(204, true, etag));
    }

    private async ValueTask<Result<T>> CompleteMutationFailureAsync<T>(
        CommandIdempotencyLease lease,
        TemplateMutationResult mutation,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken) => await CompleteFailureAsync<T>(
            lease,
            FailureFor(mutation.Disposition, mutation.CurrentVersion),
            unitOfWork,
            cancellationToken).ConfigureAwait(false);

    private async ValueTask<Result<T>> CompleteCreateMutationFailureAsync<T>(
        CommandIdempotencyLease lease,
        TemplateMutationResult mutation,
        string notFoundDescription,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken) => await CompleteFailureAsync<T>(
            lease,
            CreateFailureFor(
                mutation.Disposition,
                mutation.CurrentVersion,
                notFoundDescription),
            unitOfWork,
            cancellationToken).ConfigureAwait(false);

    private async ValueTask<Result<T>> CompleteCreateMutationFailureAsync<T>(
        CommandIdempotencyLease lease,
        SubscriptionMutationResult mutation,
        string notFoundDescription,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken) => await CompleteFailureAsync<T>(
            lease,
            CreateFailureFor(
                mutation.Disposition,
                mutation.CurrentVersion,
                notFoundDescription),
            unitOfWork,
            cancellationToken).ConfigureAwait(false);

    private async ValueTask<Result<T>> CompleteMutationFailureAsync<T>(
        CommandIdempotencyLease lease,
        SubscriptionMutationResult mutation,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken) => await CompleteFailureAsync<T>(
            lease,
            FailureFor(mutation.Disposition, mutation.CurrentVersion),
            unitOfWork,
            cancellationToken).ConfigureAwait(false);

    private async ValueTask<Result<T>> CompleteFailureAsync<T>(
        CommandIdempotencyLease lease,
        MutationFailure failure,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        ResultErrorPresentation presentation = Presentation(failure.Status, failure.Code);
        JsonElement body = JsonSerializer.SerializeToElement(
            new ReplayFailure(failure.Description, presentation));
        JsonElement headers = failure.ETag is null ? EmptyObject : Headers(failure.ETag);
        bool completed = await _idempotencyStore.CompleteAsync(
            new CommandIdempotencyCompletion(
                lease,
                CommandIdempotencyTerminalStatus.Failed,
                failure.Status,
                body,
                ResponseBodyEnvelope: null,
                headers,
                ResourceType: null,
                ResourceId: null),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (!completed)
        {
            throw new InvalidOperationException("The idempotency lease was lost before completion.");
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Failure<T>(
            failure.Code,
            failure.Description,
            etag: failure.ETag,
            presentation: presentation);
    }

    private async ValueTask CompleteSuccessAsync<T>(
        CommandIdempotencyLease lease,
        int status,
        T? body,
        JsonElement headers,
        string resourceType,
        EntityId resourceId,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        JsonElement? responseBody = body is null ? null : SerializeReplay(body);
        bool completed = await _idempotencyStore.CompleteAsync(
            new CommandIdempotencyCompletion(
                lease,
                CommandIdempotencyTerminalStatus.Completed,
                status,
                responseBody,
                ResponseBodyEnvelope: null,
                headers,
                resourceType,
                resourceId),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (!completed)
        {
            throw new InvalidOperationException("The idempotency lease was lost before completion.");
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AppendAuditAsync(
        SubscriptionActor actor,
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
        CancellationToken cancellationToken) => await _auditAppender.AppendAsync(
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
                        "poolai|audit-idempotency-key|subscription-access|v1\0",
                        idempotencyKey),
                })),
            unitOfWorkContext,
            cancellationToken).ConfigureAwait(false);

    private async ValueTask AppendEventAsync(
        string eventType,
        string aggregateType,
        EntityId aggregateId,
        long version,
        EntityId requestId,
        DateTimeOffset occurredAt,
        JsonElement payload,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        EntityId eventId = EntityId.New();
        await _outboxAppender.AppendAsync(
            new IntegrationEvent(
                eventId,
                $"subscription-access:{eventType}:{eventId.Value:D}",
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
            cancellationToken).ConfigureAwait(false);
    }

    private byte[] HashRequest<T>(T value)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(value);
        byte[] domain = Encoding.UTF8.GetBytes(
            "poolai|idempotency-request-hash|subscription-access|v1\0");
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
        byte[] input = Encoding.UTF8.GetBytes(domain + value);
        try
        {
            return Convert.ToHexStringLower(
                HMACSHA256.HashData(_policy.RequestHashPepper, input));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    private static MutationFailure FailureFor(
        SubscriptionMutationDisposition disposition,
        long? currentVersion) => disposition switch
        {
            SubscriptionMutationDisposition.NotFound => new(
                404,
                SubscriptionErrorCodes.ResourceNotFound,
                "The requested resource does not exist.",
                null),
            SubscriptionMutationDisposition.VersionConflict => new(
                412,
                SubscriptionErrorCodes.VersionConflict,
                "The resource version has changed.",
                currentVersion is > 0 ? ETag(currentVersion.Value) : null),
            SubscriptionMutationDisposition.TemplateDisabled => new(
                409,
                SubscriptionErrorCodes.SubscriptionTemplateDisabled,
                "The Subscription Template is not active.",
                null),
            SubscriptionMutationDisposition.CanonicalConflict => new(
                409,
                SubscriptionErrorCodes.SubscriptionConflict,
                "A canonical Subscription already exists for this user and Group.",
                null),
            SubscriptionMutationDisposition.GroupArchived => new(
                409,
                SubscriptionErrorCodes.ResourceConflict,
                "An archived Group cannot receive provisioning changes.",
                null),
            SubscriptionMutationDisposition.GroupDisabled => new(
                403,
                SubscriptionErrorCodes.GroupDisabled,
                "The Group is not active.",
                null),
            SubscriptionMutationDisposition.ResourceConflict
                or SubscriptionMutationDisposition.InvalidTransition => new(
                    409,
                    SubscriptionErrorCodes.ResourceConflict,
                    "The requested state conflicts with the current resource state.",
                    null),
            _ => throw new InvalidOperationException("Unknown unsuccessful mutation disposition."),
        };

    private static MutationFailure CreateFailureFor(
        SubscriptionMutationDisposition disposition,
        long? currentVersion,
        string notFoundDescription) => disposition == SubscriptionMutationDisposition.NotFound
            ? new MutationFailure(
                409,
                SubscriptionErrorCodes.ResourceConflict,
                notFoundDescription,
                null)
            : FailureFor(disposition, currentVersion);

    private static MutationFailure TargetUserConflict() => new(
        409,
        SubscriptionErrorCodes.ResourceConflict,
        "The target user does not exist or is not active.",
        null);

    private static Result<T> ForwardFailure<T>(ResultError error) => Result.Failure<T>(
        error.Code,
        error.Description,
        error.RetryAfterSeconds,
        error.ETag,
        error.Presentation);

    private static bool IsForbiddenOperatorRegrant(
        UpdateSubscriptionCommand command,
        SubscriptionMutationResult mutation) =>
        command.Actor.Role == SystemRole.Operator
        && command.StatusSpecified
        && command.Status == SubscriptionLifecycle.Active
        && mutation.Disposition == SubscriptionMutationDisposition.InvalidTransition
        && mutation.BeforeState is { ValueKind: JsonValueKind.Object } beforeState
        && beforeState.TryGetProperty("status", out JsonElement status)
        && status.ValueKind == JsonValueKind.String
        && string.Equals(status.GetString(), "revoked", StringComparison.Ordinal);

    private static ResultErrorPresentation Presentation(int status, string code)
    {
        (string title, string detail, bool retryable) = status switch
        {
            403 => ("Forbidden", "The requested operation is not allowed for the current resource state.", false),
            404 => ("Resource not found", "The requested resource was not found.", false),
            409 => ("Resource conflict", "The requested state conflicts with the current resource state.", false),
            412 => ("Version conflict", "The resource version no longer matches; retrieve it again before retrying.", true),
            _ => throw new InvalidOperationException("Unsupported idempotent failure status."),
        };
        return new ResultErrorPresentation(code, status, title, detail, retryable);
    }

    private static JsonElement TemplateAuditState(SubscriptionTemplateView value) =>
        JsonSerializer.SerializeToElement(new
        {
            id = value.Id.Value,
            group_id = value.GroupId.Value,
            name = value.Name,
            description = value.Description,
            default_duration_days = value.DefaultDurationDays,
            status = TemplateStatusCode(value.Status),
            version = value.Version,
            created_at = value.CreatedAt,
            updated_at = value.UpdatedAt,
        });

    private static JsonElement SubscriptionAuditState(SubscriptionView value) =>
        JsonSerializer.SerializeToElement(new
        {
            id = value.Id.Value,
            user_id = value.UserId.Value,
            group_id = value.GroupId.Value,
            template_id = value.TemplateId.Value,
            plan_name = value.PlanName,
            starts_at = value.StartsAt,
            expires_at = value.ExpiresAt,
            status = SubscriptionStatusCode(value.Status),
            effective_status = EffectiveStatusCode(value.EffectiveStatus),
            assigned_by = value.AssignedBy.Value,
            version = value.Version,
            created_at = value.CreatedAt,
            updated_at = value.UpdatedAt,
        });

    private static JsonElement SerializeReplay<T>(T value) => value switch
    {
        SubscriptionTemplateView template =>
            JsonSerializer.SerializeToElement(TemplateReplay.From(template)),
        SubscriptionView subscription =>
            JsonSerializer.SerializeToElement(SubscriptionReplay.From(subscription)),
        _ => throw new InvalidOperationException("Unsupported replay body."),
    };

    private static T DeserializeReplay<T>(JsonElement body)
    {
        object value = typeof(T) == typeof(SubscriptionTemplateView)
            ? (object)(body.Deserialize<TemplateReplay>()
                ?? throw new InvalidOperationException("Template replay body is invalid.")).ToView()
            : typeof(T) == typeof(SubscriptionView)
                ? (body.Deserialize<SubscriptionReplay>()
                    ?? throw new InvalidOperationException("Subscription replay body is invalid.")).ToView()
                : throw new InvalidOperationException("Unsupported replay body type.");
        return (T)value;
    }

    private static EntityId IdOf<T>(T value) => value switch
    {
        SubscriptionTemplateView template => template.Id,
        SubscriptionView subscription => subscription.Id,
        _ => throw new InvalidOperationException("Unsupported resource type."),
    };

    private static long VersionOf<T>(T value) => value switch
    {
        SubscriptionTemplateView template => template.Version,
        SubscriptionView subscription => subscription.Version,
        _ => throw new InvalidOperationException("Unsupported resource type."),
    };

    private static ReplayFailure ParseReplayFailure(CommandIdempotencyResponse response) =>
        response.Body?.Deserialize<ReplayFailure>()
        ?? throw new InvalidOperationException("The idempotency failure body is invalid.");

    private static string? Header(JsonElement headers, string name) =>
        headers.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;

    private static JsonElement Headers(string etag, string? location = null) => location is null
        ? JsonSerializer.SerializeToElement(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ETag"] = etag,
        })
        : JsonSerializer.SerializeToElement(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ETag"] = etag,
            ["Location"] = location,
        });

    private static bool IsAuthenticated(SubscriptionActor actor) => actor.TokenVersion > 0;

    private static bool CanReadAdminResources(SubscriptionActor actor) =>
        IsAuthenticated(actor)
        && actor.Role is SystemRole.Admin or SystemRole.Operator or SystemRole.Auditor;

    private static bool CanManageSubscriptions(SubscriptionActor actor) =>
        IsAuthenticated(actor)
        && actor.Role is SystemRole.Admin or SystemRole.Operator;

    private static AuditActorType AuditActor(SystemRole role) => role switch
    {
        SystemRole.Admin => AuditActorType.Admin,
        SystemRole.Operator => AuditActorType.Operator,
        SystemRole.Auditor => AuditActorType.Auditor,
        SystemRole.User => AuditActorType.User,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private static Result<SubscriptionCommandOutcome<T>> RoleFailure<T>() =>
        Failure<SubscriptionCommandOutcome<T>>(
            SubscriptionErrorCodes.RoleRequired,
            "The Admin or Operator role is required.");

    private static Result<SubscriptionCommandOutcome<T>> ValidationFailure<T>(string description) =>
        Failure<SubscriptionCommandOutcome<T>>(
            SubscriptionErrorCodes.ValidationFailed,
            description);

    private static string Scope(SubscriptionActor actor, string method, string path) =>
        $"subscription-access:{actor.UserId.Value:D}:{method}:{path}";

    private static string ETag(long version) => $"\"v{version}\"";

    private static bool TryValidatePage(
        string? encoded,
        int limit,
        out SubscriptionCursor? cursor)
    {
        cursor = null;
        return limit is >= 1 and <= 100 && TryDecodeCursor(encoded, out cursor);
    }

    private static string? NextCursor(
        IReadOnlyList<SubscriptionTemplateRecord> items,
        bool hasMore) => hasMore && items.Count > 0
            ? EncodeCursor(items[^1].CreatedAt, items[^1].Id)
            : null;

    private static string? NextCursor(
        IReadOnlyList<SubscriptionRecord> items,
        bool hasMore) => hasMore && items.Count > 0
            ? EncodeCursor(items[^1].CreatedAt, items[^1].Id)
            : null;

    private static string EncodeCursor(DateTimeOffset createdAt, EntityId id)
    {
        Span<byte> bytes = stackalloc byte[25];
        bytes[0] = 0x01;
        long unixMicroseconds = checked(
            (createdAt.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks) / 10);
        BinaryPrimitives.WriteInt64BigEndian(bytes[1..9], unixMicroseconds);
        Convert.FromHexString(id.Value.ToString("N"), bytes[9..], out _, out _);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool TryDecodeCursor(string? encoded, out SubscriptionCursor? cursor)
    {
        cursor = null;
        if (encoded is null)
        {
            return true;
        }

        try
        {
            if (encoded.Length != 34
                || encoded.Contains('=', StringComparison.Ordinal)
                || encoded.Any(static character => !(character is >= 'A' and <= 'Z'
                    or >= 'a' and <= 'z'
                    or >= '0' and <= '9'
                    or '-' or '_')))
            {
                return false;
            }

            string base64 = encoded.Replace('-', '+').Replace('_', '/') + "==";
            byte[] bytes = Convert.FromBase64String(base64);
            if (bytes.Length != 25 || bytes[0] != 0x01)
            {
                return false;
            }

            long micros = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(1, 8));
            long ticks = checked(DateTime.UnixEpoch.Ticks + checked(micros * 10));
            bool validId = Guid.TryParseExact(
                Convert.ToHexString(bytes.AsSpan(9, 16)),
                "N",
                out Guid id);
            if (!validId
                || id == Guid.Empty
                || ticks < DateTimeOffset.MinValue.UtcDateTime.Ticks
                || ticks > DateTimeOffset.MaxValue.UtcDateTime.Ticks)
            {
                return false;
            }

            cursor = new SubscriptionCursor(
                new DateTimeOffset(ticks, TimeSpan.Zero),
                new EntityId(id));
            return string.Equals(
                EncodeCursor(cursor.CreatedAt, cursor.Id),
                encoded,
                StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static string TemplateStatusCode(SubscriptionTemplateLifecycle value) => value switch
    {
        SubscriptionTemplateLifecycle.Active => "active",
        SubscriptionTemplateLifecycle.Disabled => "disabled",
        SubscriptionTemplateLifecycle.Retired => "retired",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string SubscriptionStatusCode(SubscriptionLifecycle value) => value switch
    {
        SubscriptionLifecycle.Active => "active",
        SubscriptionLifecycle.Suspended => "suspended",
        SubscriptionLifecycle.Revoked => "revoked",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string EffectiveStatusCode(SubscriptionEffectiveLifecycle value) => value switch
    {
        SubscriptionEffectiveLifecycle.Scheduled => "scheduled",
        SubscriptionEffectiveLifecycle.Active => "active",
        SubscriptionEffectiveLifecycle.Expired => "expired",
        SubscriptionEffectiveLifecycle.Suspended => "suspended",
        SubscriptionEffectiveLifecycle.Revoked => "revoked",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static SubscriptionEffectiveStatus ToAbstractionStatus(
        SubscriptionEffectiveLifecycle value) => value switch
        {
            SubscriptionEffectiveLifecycle.Scheduled => SubscriptionEffectiveStatus.Scheduled,
            SubscriptionEffectiveLifecycle.Active => SubscriptionEffectiveStatus.Active,
            SubscriptionEffectiveLifecycle.Expired => SubscriptionEffectiveStatus.Expired,
            SubscriptionEffectiveLifecycle.Suspended => SubscriptionEffectiveStatus.Suspended,
            SubscriptionEffectiveLifecycle.Revoked => SubscriptionEffectiveStatus.Revoked,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static Result<T> Failure<T>(
        string code,
        string description,
        string? etag = null,
        ResultErrorPresentation? presentation = null) =>
        Result.Failure<T>(code, description, retryAfterSeconds: null, etag, presentation);

    private sealed record MutationFailure(
        int Status,
        string Code,
        string Description,
        string? ETag);

    private sealed record ReplayFailure(
        string Description,
        ResultErrorPresentation Presentation);

    private sealed record TemplateReplay(
        Guid Id,
        Guid GroupId,
        string Name,
        string? Description,
        int DefaultDurationDays,
        SubscriptionTemplateLifecycle Status,
        long Version,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt)
    {
        internal static TemplateReplay From(SubscriptionTemplateView value) => new(
            value.Id.Value,
            value.GroupId.Value,
            value.Name,
            value.Description,
            value.DefaultDurationDays,
            value.Status,
            value.Version,
            value.CreatedAt,
            value.UpdatedAt);

        internal SubscriptionTemplateView ToView() => new(
            new EntityId(Id),
            new EntityId(GroupId),
            Name,
            Description,
            DefaultDurationDays,
            Status,
            Version,
            CreatedAt,
            UpdatedAt);
    }

    private sealed record SubscriptionReplay(
        Guid Id,
        Guid UserId,
        Guid GroupId,
        Guid TemplateId,
        string PlanName,
        DateTimeOffset StartsAt,
        DateTimeOffset ExpiresAt,
        SubscriptionLifecycle Status,
        SubscriptionEffectiveLifecycle EffectiveStatus,
        Guid AssignedBy,
        long Version,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset ObservedAt)
    {
        internal static SubscriptionReplay From(SubscriptionView value) => new(
            value.Id.Value,
            value.UserId.Value,
            value.GroupId.Value,
            value.TemplateId.Value,
            value.PlanName,
            value.StartsAt,
            value.ExpiresAt,
            value.Status,
            value.EffectiveStatus,
            value.AssignedBy.Value,
            value.Version,
            value.CreatedAt,
            value.UpdatedAt,
            value.ObservedAt);

        internal SubscriptionView ToView() => new(
            new EntityId(Id),
            new EntityId(UserId),
            new EntityId(GroupId),
            new EntityId(TemplateId),
            PlanName,
            StartsAt,
            ExpiresAt,
            Status,
            EffectiveStatus,
            new EntityId(AssignedBy),
            Version,
            CreatedAt,
            UpdatedAt,
            ObservedAt);
    }
}
#pragma warning restore MA0051
