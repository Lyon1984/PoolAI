#pragma warning disable MA0051 // Account command handlers keep the transactional protocol visible.
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Application.Ports;
using PoolAI.Modules.Supply.Domain;

namespace PoolAI.Modules.Supply.Application;

internal sealed class AccountControlPlaneService :
    IListAccountsUseCase,
    IGetAccountUseCase,
    ICreateAccountUseCase,
    IUpdateAccountUseCase,
    IRetireAccountUseCase
{
    private const string EventTopic = "poolai.supply.v1";
    private const int EventSchemaVersion = 1;
    private static readonly TimeSpan IdempotencyLease = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdempotencyRetention = TimeSpan.FromHours(24);
    private static readonly JsonElement EmptyObject = JsonSerializer.SerializeToElement(
        new Dictionary<string, string>(StringComparer.Ordinal));

    private readonly IAccountControlPlaneRepository _repository;
    private readonly IUnitOfWorkFactory _unitOfWorkFactory;
    private readonly ICommandIdempotencyStore _idempotencyStore;
    private readonly IAuditAppender _auditAppender;
    private readonly IOutboxAppender _outboxAppender;
    private readonly IAccountCredentialProtector _credentialProtector;
    private readonly IAccountActiveLeaseReader _activeLeaseReader;
    private readonly AccountControlPlanePolicy _policy;

    internal AccountControlPlaneService(
        IAccountControlPlaneRepository repository,
        IUnitOfWorkFactory unitOfWorkFactory,
        ICommandIdempotencyStore idempotencyStore,
        IAuditAppender auditAppender,
        IOutboxAppender outboxAppender,
        IAccountCredentialProtector credentialProtector,
        IAccountActiveLeaseReader activeLeaseReader,
        AccountControlPlanePolicy policy)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _unitOfWorkFactory = unitOfWorkFactory
            ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
        _idempotencyStore = idempotencyStore
            ?? throw new ArgumentNullException(nameof(idempotencyStore));
        _auditAppender = auditAppender
            ?? throw new ArgumentNullException(nameof(auditAppender));
        _outboxAppender = outboxAppender
            ?? throw new ArgumentNullException(nameof(outboxAppender));
        _credentialProtector = credentialProtector
            ?? throw new ArgumentNullException(nameof(credentialProtector));
        _activeLeaseReader = activeLeaseReader
            ?? throw new ArgumentNullException(nameof(activeLeaseReader));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public async ValueTask<Result<AccountPage>> ExecuteAsync(
        ListAccountsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!CanRead(query.Actor))
        {
            return Failure<AccountPage>(
                AccountErrorCodes.RoleRequired,
                "The actor role cannot read Accounts.");
        }

        if (query.Limit is < 1 or > 100
            || !TryDecodeCursor(query.Cursor, out AccountCursor? cursor))
        {
            return Failure<AccountPage>(
                AccountErrorCodes.InvalidRequest,
                "The Account pagination request is invalid.");
        }

        AccountSlice slice = await _repository
            .ListAsync(cursor, query.Limit, cancellationToken)
            .ConfigureAwait(false);
        Result<IReadOnlyList<AccountView>> readViews = await ReadViewsAsync(
            slice.Items,
            cancellationToken).ConfigureAwait(false);
        if (readViews.IsFailure)
        {
            return CoordinationUnavailable<AccountPage>();
        }

        string? nextCursor = slice.HasMore && slice.Items.Count > 0
            ? EncodeCursor(slice.Items[^1])
            : null;
        return Result.Success(new AccountPage(
            readViews.Value,
            nextCursor,
            slice.HasMore));
    }

    public async ValueTask<Result<AccountView>> ExecuteAsync(
        GetAccountQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!CanRead(query.Actor))
        {
            return Failure<AccountView>(
                AccountErrorCodes.RoleRequired,
                "The actor role cannot read Accounts.");
        }

        AccountResource? account = await _repository
            .GetAsync(query.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
        {
            return Failure<AccountView>(
                AccountErrorCodes.ResourceNotFound,
                "The Account does not exist.");
        }

        Result<IReadOnlyList<AccountView>> readViews = await ReadViewsAsync(
            [account],
            cancellationToken).ConfigureAwait(false);
        return readViews.IsFailure
            ? CoordinationUnavailable<AccountView>()
            : Result.Success(readViews.Value[0]);
    }

    public async ValueTask<Result<AccountCommandOutcome<AccountView>>> ExecuteAsync(
        CreateAccountCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!CanManage(command.Actor))
        {
            return Failure<AccountCommandOutcome<AccountView>>(
                AccountErrorCodes.RoleRequired,
                "The Admin or Operator role is required.");
        }

        PreparedCreate prepared;
        try
        {
            prepared = PrepareCreate(command);
        }
        catch (ArgumentException)
        {
            return Failure<AccountCommandOutcome<AccountView>>(
                AccountErrorCodes.ValidationFailed,
                "The create-Account request is invalid.");
        }

        EntityId accountId = EntityId.New();
        AccountCredentialProtection protection = _credentialProtector.Protect(
            prepared.Credential,
            accountId);
        ValidateProtection(protection);

        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        CommandIdempotencyAcquireResult acquire = await AcquireAsync(
            CreateScope(command.Actor),
            command.IdempotencyKey,
            command.RequestId,
            command.Actor,
            prepared.RequestHash,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Result<AccountCommandOutcome<AccountView>>? early =
            ReplayOrAcquireFailure<AccountView>(acquire, expectedStatus: 201);
        if (early is not null)
        {
            return early;
        }

        CommandIdempotencyLease idempotencyLease = acquire.Lease!;
        AccountMutationResult mutation = await _repository.CreateAsync(
            new AccountCreateWrite(
                accountId,
                command.Provider,
                prepared.Name,
                prepared.BaseUrl,
                protection.Envelope,
                prepared.CredentialPrefix,
                prepared.MaxConcurrency,
                prepared.Priority,
                prepared.Weight),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (mutation.Disposition != AccountMutationDisposition.Written)
        {
            return await CompleteMutationFailureAsync<
                AccountCommandOutcome<AccountView>>(
                    idempotencyLease,
                    mutation,
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false);
        }

        AccountResource account = mutation.Value
            ?? throw new InvalidOperationException(
                "A successful Account create did not return the resource.");
        AccountView view = ToView(account);
        await AppendAuditAsync(
            command.Actor,
            "supply.account.created",
            account.Id,
            command.RequestId,
            reason: null,
            command.IpAddress,
            command.UserAgent,
            before: null,
            after: AuditState(account),
            command.IdempotencyKey,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(
            "account_created",
            account,
            command.RequestId,
            changedFields:
            [
                "name",
                "provider",
                "base_url",
                "credential",
                "max_concurrency",
                "priority",
                "weight",
            ],
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);

        string etag = ETag(account.Version);
        string location = $"/api/v1/admin/accounts/{account.Id.Value:D}";
        await CompleteSuccessAsync(
            idempotencyLease,
            status: 201,
            view,
            etag,
            location,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(new AccountCommandOutcome<AccountView>(
            201,
            IsReplay: false,
            view,
            etag,
            location));
    }

    public async ValueTask<Result<AccountCommandOutcome<AccountView>>> ExecuteAsync(
        UpdateAccountCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!CanManage(command.Actor))
        {
            return Failure<AccountCommandOutcome<AccountView>>(
                AccountErrorCodes.RoleRequired,
                "The Admin or Operator role is required.");
        }

        PreparedUpdate prepared;
        try
        {
            prepared = PrepareUpdate(command);
        }
        catch (ArgumentException)
        {
            return Failure<AccountCommandOutcome<AccountView>>(
                AccountErrorCodes.ValidationFailed,
                "The update-Account request is invalid.");
        }

        AccountCredentialProtection? protection = prepared.Credential is null
            ? null
            : _credentialProtector.Protect(
                prepared.Credential,
                command.AccountId);
        if (protection is not null)
        {
            ValidateProtection(protection);
        }

        Result<AccountCommandOutcome<AccountView>>? preflight =
            await PreflightUpdateAsync(
                command,
                prepared,
                cancellationToken).ConfigureAwait(false);
        if (preflight is not null)
        {
            return preflight;
        }

        Result<int> activeLeaseCount = await ReadActiveLeaseCountAsync(
            command.AccountId,
            cancellationToken).ConfigureAwait(false);
        if (activeLeaseCount.IsFailure)
        {
            return CoordinationUnavailable<AccountCommandOutcome<AccountView>>();
        }

        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        CommandIdempotencyAcquireResult acquire = await AcquireAsync(
            UpdateScope(command.Actor, command.AccountId),
            command.IdempotencyKey,
            command.RequestId,
            command.Actor,
            prepared.RequestHash,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Result<AccountCommandOutcome<AccountView>>? early =
            ReplayOrAcquireFailure<AccountView>(acquire, expectedStatus: 200);
        if (early is not null)
        {
            return early;
        }

        CommandIdempotencyLease idempotencyLease = acquire.Lease!;
        AccountMutationResult mutation = await _repository.UpdateAsync(
            new AccountUpdateWrite(
                command.AccountId,
                command.ExpectedVersion,
                command.NameSpecified,
                prepared.Name,
                command.BaseUrlSpecified,
                prepared.BaseUrl,
                command.CredentialSpecified,
                protection?.Envelope,
                prepared.CredentialPrefix,
                command.StatusSpecified,
                prepared.Status,
                command.MaxConcurrencySpecified,
                prepared.MaxConcurrency,
                command.PrioritySpecified,
                prepared.Priority,
                command.WeightSpecified,
                prepared.Weight,
                prepared.Reason),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (mutation.Disposition != AccountMutationDisposition.Written)
        {
            return await CompleteMutationFailureAsync<
                AccountCommandOutcome<AccountView>>(
                    idempotencyLease,
                    mutation,
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false);
        }

        AccountResource account = mutation.Value
            ?? throw new InvalidOperationException(
                "A successful Account update did not return the resource.");
        AccountResource before = mutation.Before
            ?? throw new InvalidOperationException(
                "A successful Account update did not return its before-state.");
        if (mutation.WasChanged)
        {
            string[] changedFields = ChangedFields(
                before,
                account,
                command.CredentialSpecified);
            await AppendAuditAsync(
                command.Actor,
                "supply.account.updated",
                account.Id,
                command.RequestId,
                prepared.Reason,
                command.IpAddress,
                command.UserAgent,
                AuditState(before),
                AuditState(account),
                command.IdempotencyKey,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(
                "account_updated",
                account,
                command.RequestId,
                changedFields,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
        }

        AccountView view = ToView(account, activeLeaseCount.Value);
        string etag = ETag(account.Version);
        await CompleteSuccessAsync(
            idempotencyLease,
            status: 200,
            view,
            etag,
            location: null,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(new AccountCommandOutcome<AccountView>(
            200,
            IsReplay: false,
            view,
            etag));
    }

    public async ValueTask<Result<AccountCommandOutcome>> ExecuteAsync(
        RetireAccountCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!CanManage(command.Actor))
        {
            return Failure<AccountCommandOutcome>(
                AccountErrorCodes.RoleRequired,
                "The Admin or Operator role is required.");
        }

        string reason;
        byte[] requestHash;
        try
        {
            AccountInput.IdempotencyKey(command.IdempotencyKey);
            AccountInput.ExpectedVersion(command.ExpectedVersion);
            reason = AccountInput.Reason(command.Reason);
            requestHash = HashRequest(new
            {
                account_id = command.AccountId.Value,
                expected_version = command.ExpectedVersion,
                reason,
            });
        }
        catch (ArgumentException)
        {
            return Failure<AccountCommandOutcome>(
                AccountErrorCodes.ValidationFailed,
                "The retire-Account request is invalid.");
        }

        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        CommandIdempotencyAcquireResult acquire = await AcquireAsync(
            RetireScope(command.Actor, command.AccountId),
            command.IdempotencyKey,
            command.RequestId,
            command.Actor,
            requestHash,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Result<AccountCommandOutcome>? early = ReplayRetireOrAcquireFailure(
            acquire,
            command.AccountId);
        if (early is not null)
        {
            return early;
        }

        CommandIdempotencyLease idempotencyLease = acquire.Lease!;
        AccountMutationResult mutation = await _repository.RetireAsync(
            new AccountRetireWrite(
                command.AccountId,
                command.ExpectedVersion,
                reason),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (mutation.Disposition != AccountMutationDisposition.Written)
        {
            return await CompleteMutationFailureAsync<AccountCommandOutcome>(
                idempotencyLease,
                mutation,
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        AccountResource account = mutation.Value
            ?? throw new InvalidOperationException(
                "A successful Account retirement did not return the resource.");
        AccountResource before = mutation.Before
            ?? throw new InvalidOperationException(
                "A successful Account retirement did not return its before-state.");
        if (!mutation.WasChanged
            || account.Status != AccountResourceStatus.Retired)
        {
            throw new InvalidOperationException(
                "A successful Account retirement must change the lifecycle.");
        }

        await AppendAuditAsync(
            command.Actor,
            "supply.account.retired",
            account.Id,
            command.RequestId,
            reason,
            command.IpAddress,
            command.UserAgent,
            AuditState(before),
            AuditState(account),
            command.IdempotencyKey,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(
            "account_retired",
            account,
            command.RequestId,
            changedFields: ["status"],
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);

        string etag = ETag(account.Version);
        await CompleteRetireSuccessAsync(
            idempotencyLease,
            account.Id,
            etag,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(new AccountCommandOutcome(
            204,
            IsReplay: false,
            etag));
    }

    private PreparedCreate PrepareCreate(CreateAccountCommand command)
    {
        AccountInput.IdempotencyKey(command.IdempotencyKey);
        string name = AccountInput.Name(command.Name);
        _ = ProviderCode(command.Provider);
        string baseUrl = AccountInput.BaseUrl(command.BaseUrl);
        string credential = AccountInput.Credential(command.Credential);
        string credentialPrefix = AccountInput.CredentialPrefix(credential);
        int maxConcurrency = AccountInput.MaxConcurrency(command.MaxConcurrency);
        int priority = AccountInput.Priority(command.Priority);
        int weight = AccountInput.Weight(command.Weight);
        string credentialCommitment = CredentialCommitment(credential);
        return new PreparedCreate(
            name,
            baseUrl,
            credential,
            credentialPrefix,
            maxConcurrency,
            priority,
            weight,
            HashRequest(new
            {
                name,
                provider = ProviderCode(command.Provider),
                base_url = baseUrl,
                credential_commitment = credentialCommitment,
                max_concurrency = maxConcurrency,
                priority,
                weight,
            }));
    }

    private PreparedUpdate PrepareUpdate(UpdateAccountCommand command)
    {
        AccountInput.IdempotencyKey(command.IdempotencyKey);
        AccountInput.ExpectedVersion(command.ExpectedVersion);
        if (!command.NameSpecified
            && !command.BaseUrlSpecified
            && !command.CredentialSpecified
            && !command.StatusSpecified
            && !command.MaxConcurrencySpecified
            && !command.PrioritySpecified
            && !command.WeightSpecified)
        {
            throw new ArgumentException(
                "The Account patch has no mutable field.",
                nameof(command));
        }

        string? name = command.NameSpecified
            ? AccountInput.Name(command.Name
                ?? throw new ArgumentException(
                    "The Account name is missing.",
                    nameof(command)))
            : null;
        string? baseUrl = command.BaseUrlSpecified
            ? AccountInput.BaseUrl(command.BaseUrl
                ?? throw new ArgumentException(
                    "The Account Base URL is missing.",
                    nameof(command)))
            : null;
        string? credential = command.CredentialSpecified
            ? AccountInput.Credential(command.Credential
                ?? throw new ArgumentException(
                    "The Account credential is missing.",
                    nameof(command)))
            : null;
        string? credentialPrefix = credential is null
            ? null
            : AccountInput.CredentialPrefix(credential);
        AccountResourceStatus? status = command.StatusSpecified
            ? ToResourceStatus(command.Status
                ?? throw new ArgumentException(
                    "The Account status is missing.",
                    nameof(command)))
            : null;
        if (status == AccountResourceStatus.Retired)
        {
            throw new ArgumentException(
                "The retired lifecycle is entered only through retirement.",
                nameof(command));
        }

        int? maxConcurrency = command.MaxConcurrencySpecified
            ? AccountInput.MaxConcurrency(command.MaxConcurrency
                ?? throw new ArgumentException(
                    "The Account concurrency limit is missing.",
                    nameof(command)))
            : null;
        int? priority = command.PrioritySpecified
            ? AccountInput.Priority(command.Priority
                ?? throw new ArgumentException(
                    "The Account priority is missing.",
                    nameof(command)))
            : null;
        int? weight = command.WeightSpecified
            ? AccountInput.Weight(command.Weight
                ?? throw new ArgumentException(
                    "The Account weight is missing.",
                    nameof(command)))
            : null;
        string? reason = command.Reason is null
            ? null
            : AccountInput.Reason(command.Reason);
        if ((command.CredentialSpecified || command.StatusSpecified)
            && reason is null)
        {
            throw new ArgumentException(
                "Credential or lifecycle changes require a reason.",
                nameof(command));
        }

        string? credentialCommitment = credential is null
            ? null
            : CredentialCommitment(credential);
        return new PreparedUpdate(
            name,
            baseUrl,
            credential,
            credentialPrefix,
            status,
            maxConcurrency,
            priority,
            weight,
            reason,
            HashRequest(new
            {
                account_id = command.AccountId.Value,
                expected_version = command.ExpectedVersion,
                name_specified = command.NameSpecified,
                name,
                base_url_specified = command.BaseUrlSpecified,
                base_url = baseUrl,
                credential_specified = command.CredentialSpecified,
                credential_commitment = credentialCommitment,
                status_specified = command.StatusSpecified,
                status = status is null ? null : StatusCode(status.Value),
                max_concurrency_specified = command.MaxConcurrencySpecified,
                max_concurrency = maxConcurrency,
                priority_specified = command.PrioritySpecified,
                priority,
                weight_specified = command.WeightSpecified,
                weight,
                reason,
            }));
    }

    private ValueTask<CommandIdempotencyAcquireResult> AcquireAsync(
        string scope,
        string key,
        EntityId requestId,
        AccountActor actor,
        byte[] requestHash,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken) => _idempotencyStore.AcquireAsync(
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

    private async ValueTask<Result<AccountCommandOutcome<AccountView>>?>
        PreflightUpdateAsync(
            UpdateAccountCommand command,
            PreparedUpdate prepared,
            CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        CommandIdempotencyAcquireResult acquire = await AcquireAsync(
            UpdateScope(command.Actor, command.AccountId),
            command.IdempotencyKey,
            command.RequestId,
            command.Actor,
            prepared.RequestHash,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        return ReplayOrAcquireFailure<AccountView>(acquire, expectedStatus: 200);
    }

    private static Result<AccountCommandOutcome<T>>? ReplayOrAcquireFailure<T>(
        CommandIdempotencyAcquireResult acquire,
        int expectedStatus) => acquire.Disposition switch
        {
            CommandIdempotencyDisposition.Acquired => null,
            CommandIdempotencyDisposition.Conflict =>
                Failure<AccountCommandOutcome<T>>(
                    AccountErrorCodes.IdempotencyConflict,
                    "The idempotency key was used for a different request."),
            CommandIdempotencyDisposition.Busy =>
                Failure<AccountCommandOutcome<T>>(
                    AccountErrorCodes.CoordinationUnavailable,
                    "The matching idempotent command is still in progress.",
                    retryAfterSeconds: 1),
            CommandIdempotencyDisposition.Replay =>
                Replay<T>(acquire.Response!, expectedStatus),
            _ => throw new ArgumentOutOfRangeException(nameof(acquire)),
        };

    private static Result<AccountCommandOutcome>? ReplayRetireOrAcquireFailure(
        CommandIdempotencyAcquireResult acquire,
        EntityId accountId) => acquire.Disposition switch
        {
            CommandIdempotencyDisposition.Acquired => null,
            CommandIdempotencyDisposition.Conflict =>
                Failure<AccountCommandOutcome>(
                    AccountErrorCodes.IdempotencyConflict,
                    "The idempotency key was used for a different request."),
            CommandIdempotencyDisposition.Busy =>
                Failure<AccountCommandOutcome>(
                    AccountErrorCodes.CoordinationUnavailable,
                    "The matching idempotent command is still in progress.",
                    retryAfterSeconds: 1),
            CommandIdempotencyDisposition.Replay =>
                ReplayRetire(acquire.Response!, accountId),
            _ => throw new ArgumentOutOfRangeException(nameof(acquire)),
        };

    private static Result<AccountCommandOutcome<T>> Replay<T>(
        CommandIdempotencyResponse response,
        int expectedStatus)
    {
        if (response.TerminalStatus == CommandIdempotencyTerminalStatus.Failed)
        {
            ReplayFailureBody failure = ParseReplayFailure(response);
            return Failure<AccountCommandOutcome<T>>(
                failure.Presentation.Code,
                failure.Description,
                etag: Header(response.Headers, "ETag"),
                presentation: failure.Presentation);
        }

        if (typeof(T) != typeof(AccountView))
        {
            throw new InvalidOperationException(
                "The Account replay type is unsupported.");
        }

        AccountView view = (AccountView)(object)(
            response.Body?.Deserialize<AccountViewReplay>()?.ToView()
            ?? throw new InvalidOperationException(
                "The Account success replay body is invalid."));
        string? etag = Header(response.Headers, "ETag");
        string? location = Header(response.Headers, "Location");
        int headerCount = HeaderCount(response.Headers);
        if (response.TerminalStatus != CommandIdempotencyTerminalStatus.Completed
            || response.Status != expectedStatus
            || response.BodyEnvelope is not null
            || !string.Equals(response.ResourceType, "account", StringComparison.Ordinal)
            || response.ResourceId != view.Id
            || !string.Equals(etag, ETag(view.Version), StringComparison.Ordinal)
            || expectedStatus == 201 && view.ActiveLeases != 0
            || expectedStatus == 201
                && (!string.Equals(
                        location,
                        $"/api/v1/admin/accounts/{view.Id.Value:D}",
                        StringComparison.Ordinal)
                    || headerCount != 2)
            || expectedStatus == 200 && (location is not null || headerCount != 1))
        {
            throw new InvalidOperationException(
                "The Account success replay is invalid.");
        }

        return Result.Success(new AccountCommandOutcome<T>(
            response.Status,
            IsReplay: true,
            (T)(object)view,
            etag!,
            location));
    }

    private static Result<AccountCommandOutcome> ReplayRetire(
        CommandIdempotencyResponse response,
        EntityId accountId)
    {
        if (response.TerminalStatus == CommandIdempotencyTerminalStatus.Failed)
        {
            ReplayFailureBody failure = ParseReplayFailure(response);
            return Failure<AccountCommandOutcome>(
                failure.Presentation.Code,
                failure.Description,
                etag: Header(response.Headers, "ETag"),
                presentation: failure.Presentation);
        }

        string? etag = Header(response.Headers, "ETag");
        if (response.TerminalStatus != CommandIdempotencyTerminalStatus.Completed
            || response.Status != 204
            || response.Body is not null
            || response.BodyEnvelope is not null
            || !string.Equals(response.ResourceType, "account", StringComparison.Ordinal)
            || response.ResourceId != accountId
            || etag is null
            || !IsCanonicalETag(etag)
            || HeaderCount(response.Headers) != 1)
        {
            throw new InvalidOperationException(
                "The Account retirement replay is invalid.");
        }

        return Result.Success(new AccountCommandOutcome(
            204,
            IsReplay: true,
            etag));
    }

    private static ReplayFailureBody ParseReplayFailure(
        CommandIdempotencyResponse response)
    {
        ReplayFailureBody failure = response.Body?.Deserialize<ReplayFailureBody>()
            ?? throw new InvalidOperationException(
                "The Account failure replay body is invalid.");
        string? etag = Header(response.Headers, "ETag");
        ValidatePresentation(failure.Presentation);
        if (response.TerminalStatus != CommandIdempotencyTerminalStatus.Failed
            || response.BodyEnvelope is not null
            || response.ResourceType is not null
            || response.ResourceId is not null
            || response.Status != failure.Presentation.Status
            || (response.Status == 412) != (etag is not null)
            || etag is not null && !IsCanonicalETag(etag)
            || HeaderCount(response.Headers) != (etag is null ? 0 : 1))
        {
            throw new InvalidOperationException(
                "The Account failure replay is invalid.");
        }

        return failure;
    }

    private async ValueTask<Result<T>> CompleteMutationFailureAsync<T>(
        CommandIdempotencyLease lease,
        AccountMutationResult mutation,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        MutationFailure failure = FailureFor(
            mutation.Disposition,
            mutation.CurrentVersion);
        ResultErrorPresentation presentation = Presentation(
            failure.Status,
            failure.Code);
        JsonElement headers = failure.ETag is null
            ? EmptyObject
            : Headers(failure.ETag);
        bool completed = await _idempotencyStore.CompleteAsync(
            new CommandIdempotencyCompletion(
                lease,
                CommandIdempotencyTerminalStatus.Failed,
                failure.Status,
                JsonSerializer.SerializeToElement(
                    new ReplayFailureBody(failure.Description, presentation)),
                ResponseBodyEnvelope: null,
                headers,
                ResourceType: null,
                ResourceId: null),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (!completed)
        {
            throw new InvalidOperationException(
                "The Account idempotency lease was lost.");
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Failure<T>(
            failure.Code,
            failure.Description,
            etag: failure.ETag,
            presentation: presentation);
    }

    private async ValueTask CompleteSuccessAsync(
        CommandIdempotencyLease lease,
        int status,
        AccountView view,
        string etag,
        string? location,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        JsonElement headers = location is null
            ? Headers(etag)
            : Headers(etag, location);
        bool completed = await _idempotencyStore.CompleteAsync(
            new CommandIdempotencyCompletion(
                lease,
                CommandIdempotencyTerminalStatus.Completed,
                status,
                JsonSerializer.SerializeToElement(AccountViewReplay.From(view)),
                ResponseBodyEnvelope: null,
                headers,
                "account",
                view.Id),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (!completed)
        {
            throw new InvalidOperationException(
                "The Account idempotency lease was lost.");
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask CompleteRetireSuccessAsync(
        CommandIdempotencyLease lease,
        EntityId accountId,
        string etag,
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
                Headers(etag),
                "account",
                accountId),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (!completed)
        {
            throw new InvalidOperationException(
                "The Account idempotency lease was lost.");
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AppendAuditAsync(
        AccountActor actor,
        string action,
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
                "account",
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
                        "poolai|audit-idempotency-key|supply-account|v1\0",
                        idempotencyKey),
                })),
            unitOfWorkContext,
            cancellationToken).ConfigureAwait(false);

    private async ValueTask AppendEventAsync(
        string eventType,
        AccountResource account,
        EntityId requestId,
        IReadOnlyList<string> changedFields,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        EntityId eventId = EntityId.New();
        await _outboxAppender.AppendAsync(
            new IntegrationEvent(
                eventId,
                $"supply-account:{eventType}:{eventId.Value:D}",
                EventTopic,
                EventSchemaVersion,
                "account",
                account.Id,
                account.Version,
                eventType,
                SourceEventSequence: null,
                requestId,
                CausationId: null,
                JsonSerializer.SerializeToElement(new
                {
                    schema_version = EventSchemaVersion,
                    event_type = eventType,
                    account_id = account.Id.Value,
                    provider = ProviderCode(account.Provider),
                    status = StatusCode(account.Status),
                    health = HealthCode(account.Health),
                    max_concurrency = account.MaxConcurrency,
                    priority = account.Priority,
                    weight = account.Weight,
                    version = account.Version,
                    changed_fields = changedFields,
                }),
                account.UpdatedAt),
            unitOfWorkContext,
            cancellationToken).ConfigureAwait(false);
    }

    private byte[] HashRequest<T>(T value)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(value);
        byte[] domain = Encoding.UTF8.GetBytes(
            "poolai|idempotency-request-hash|supply-account|v1\0");
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

    private string CredentialCommitment(string credential) =>
        HmacText(
            "poolai|idempotency-credential|supply-account|v1\0",
            credential);

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

    private static MutationFailure FailureFor(
        AccountMutationDisposition disposition,
        long? currentVersion) => disposition switch
        {
            AccountMutationDisposition.ValidationFailed => new(
                422,
                AccountErrorCodes.ValidationFailed,
                "The Account mutation failed validation.",
                ETag: null),
            AccountMutationDisposition.Conflict => new(
                409,
                AccountErrorCodes.ResourceConflict,
                "The requested Account state conflicts with an existing resource.",
                ETag: null),
            AccountMutationDisposition.NotFound => new(
                404,
                AccountErrorCodes.ResourceNotFound,
                "The Account does not exist.",
                ETag: null),
            AccountMutationDisposition.VersionConflict => new(
                412,
                AccountErrorCodes.VersionConflict,
                "The Account version has changed.",
                currentVersion is > 0 ? ETag(currentVersion.Value) : null),
            AccountMutationDisposition.LifecycleConflict => new(
                409,
                AccountErrorCodes.ResourceConflict,
                "The Account lifecycle does not allow the requested change.",
                ETag: null),
            AccountMutationDisposition.AccountInUse => new(
                409,
                AccountErrorCodes.AccountInUse,
                "The Account still has an enabled Supply binding.",
                ETag: null),
            _ => throw new InvalidOperationException(
                "The successful Account mutation disposition cannot be mapped as a failure."),
        };

    private static ResultErrorPresentation Presentation(int status, string code)
    {
        (string title, string detail, bool retryable) = (code, status) switch
        {
            (AccountErrorCodes.ValidationFailed, 422) =>
                ("Validation failed", "One or more fields failed validation.", false),
            (AccountErrorCodes.ResourceNotFound, 404) =>
                ("Resource not found", "The requested resource was not found.", false),
            (AccountErrorCodes.ResourceConflict, 409) =>
                ("Resource conflict", "The requested state conflicts with the current resource state.", false),
            (AccountErrorCodes.AccountInUse, 409) =>
                ("Account in use", "The Account still has an enabled Supply binding.", false),
            (AccountErrorCodes.VersionConflict, 412) =>
                ("Version conflict", "The resource version no longer matches; retrieve it again before retrying.", true),
            _ => throw new InvalidOperationException(
                "The Account idempotent failure code and status are unsupported."),
        };
        IReadOnlyDictionary<string, IReadOnlyList<string>>? errors =
            string.Equals(
                code,
                AccountErrorCodes.ValidationFailed,
                StringComparison.Ordinal)
                ? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["/"] = ["The Account mutation failed validation."],
                }
                : null;
        return new ResultErrorPresentation(
            code,
            status,
            title,
            detail,
            retryable,
            Errors: errors);
    }

    private static void ValidatePresentation(ResultErrorPresentation value)
    {
        ResultErrorPresentation expected = Presentation(value.Status, value.Code);
        bool errorsValid = expected.Errors is null
            ? value.Errors is null
            : value.Errors is not null
                && value.Errors.Count == 1
                && value.Errors.TryGetValue("/", out IReadOnlyList<string>? messages)
                && messages.Count == 1
                && string.Equals(
                    messages[0],
                    "The Account mutation failed validation.",
                    StringComparison.Ordinal);
        if (!string.Equals(value.Title, expected.Title, StringComparison.Ordinal)
            || !string.Equals(value.Detail, expected.Detail, StringComparison.Ordinal)
            || value.Retryable != expected.Retryable
            || value.RetryAfterSeconds is not null
            || !errorsValid)
        {
            throw new InvalidOperationException(
                "The Account failure replay presentation is invalid.");
        }
    }

    private static void ValidateProtection(AccountCredentialProtection protection)
    {
        ArgumentNullException.ThrowIfNull(protection);
        if (protection.Envelope.ValueKind != JsonValueKind.Object
            || string.IsNullOrWhiteSpace(protection.KeyId))
        {
            throw new InvalidOperationException(
                "Account credential protection returned an invalid envelope.");
        }
    }

    private static bool CanManage(AccountActor actor) =>
        actor.TokenVersion > 0
        && actor.Role is AccountControlRole.Admin or AccountControlRole.Operator;

    private static bool CanRead(AccountActor actor) =>
        actor.TokenVersion > 0
        && actor.Role is AccountControlRole.Admin
            or AccountControlRole.Operator
            or AccountControlRole.Auditor;

    private static AuditActorType AuditActor(AccountControlRole role) => role switch
    {
        AccountControlRole.Admin => AuditActorType.Admin,
        AccountControlRole.Operator => AuditActorType.Operator,
        AccountControlRole.Auditor => AuditActorType.Auditor,
        AccountControlRole.User => AuditActorType.User,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private async ValueTask<Result<int>> ReadActiveLeaseCountAsync(
        EntityId accountId,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<AccountActiveLeaseCount>> result =
            await _activeLeaseReader.ReadAsync(
                [accountId],
                cancellationToken).ConfigureAwait(false);
        if (result.IsFailure
            || result.Value.Count != 1
            || result.Value[0].AccountId != accountId
            || result.Value[0].ActiveLeases is < 0 or > 10_000)
        {
            return CoordinationUnavailable<int>();
        }

        return Result.Success(result.Value[0].ActiveLeases);
    }

    private async ValueTask<Result<IReadOnlyList<AccountView>>> ReadViewsAsync(
        IReadOnlyList<AccountResource> accounts,
        CancellationToken cancellationToken)
    {
        if (accounts.Count == 0)
        {
            return Result.Success<IReadOnlyList<AccountView>>(
                Array.Empty<AccountView>());
        }

        Result<IReadOnlyList<AccountActiveLeaseCount>> result =
            await _activeLeaseReader.ReadAsync(
                accounts.Select(static account => account.Id).ToArray(),
                cancellationToken).ConfigureAwait(false);
        if (result.IsFailure || result.Value.Count != accounts.Count)
        {
            return CoordinationUnavailable<IReadOnlyList<AccountView>>();
        }

        AccountView[] views = new AccountView[accounts.Count];
        for (int index = 0; index < accounts.Count; index++)
        {
            AccountActiveLeaseCount count = result.Value[index];
            if (count.AccountId != accounts[index].Id
                || count.ActiveLeases is < 0 or > 10_000)
            {
                return CoordinationUnavailable<IReadOnlyList<AccountView>>();
            }

            views[index] = ToView(accounts[index], count.ActiveLeases);
        }

        return Result.Success<IReadOnlyList<AccountView>>(views);
    }

    private static AccountView ToView(AccountResource account) =>
        ToView(account, activeLeases: 0);

    private static AccountView ToView(
        AccountResource account,
        int activeLeases) => new(
        account.Id,
        account.Name,
        account.Provider,
        new Uri(account.UpstreamBaseUrl, UriKind.Absolute),
        account.CredentialPrefix,
        ToLifecycle(account.Status),
        new AccountHealthView(
            account.Health,
            account.Health == AccountHealth.Cooling
                ? account.UpstreamRateLimitedUntil
                : null,
            account.LastHealthAt),
        activeLeases,
        account.MaxConcurrency,
        account.Priority,
        account.Weight,
        account.Version,
        account.CreatedAt,
        account.UpdatedAt);

    private static JsonElement AuditState(AccountResource account) =>
        JsonSerializer.SerializeToElement(new
        {
            account_id = account.Id.Value,
            name = account.Name,
            provider = ProviderCode(account.Provider),
            base_url_sha256 = TextDigest(account.UpstreamBaseUrl),
            credential_prefix = account.CredentialPrefix,
            status = StatusCode(account.Status),
            health = HealthCode(account.Health),
            max_concurrency = account.MaxConcurrency,
            priority = account.Priority,
            weight = account.Weight,
            version = account.Version,
        });

    private static string[] ChangedFields(
        AccountResource before,
        AccountResource after,
        bool credentialSpecified)
    {
        List<string> fields = new(8);
        if (!string.Equals(before.Name, after.Name, StringComparison.Ordinal))
        {
            fields.Add("name");
        }

        if (!string.Equals(
                before.UpstreamBaseUrl,
                after.UpstreamBaseUrl,
                StringComparison.Ordinal))
        {
            fields.Add("base_url");
        }

        if (credentialSpecified)
        {
            fields.Add("credential");
        }

        if (before.Status != after.Status)
        {
            fields.Add("status");
        }

        if (before.MaxConcurrency != after.MaxConcurrency)
        {
            fields.Add("max_concurrency");
        }

        if (before.Priority != after.Priority)
        {
            fields.Add("priority");
        }

        if (before.Weight != after.Weight)
        {
            fields.Add("weight");
        }

        return fields.ToArray();
    }

    private static string TextDigest(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static AccountResourceStatus ToResourceStatus(
        AccountLifecycle status) => status switch
        {
            AccountLifecycle.Active => AccountResourceStatus.Active,
            AccountLifecycle.Disabled => AccountResourceStatus.Disabled,
            AccountLifecycle.Retired => AccountResourceStatus.Retired,
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

    private static AccountLifecycle ToLifecycle(
        AccountResourceStatus status) => status switch
        {
            AccountResourceStatus.Active => AccountLifecycle.Active,
            AccountResourceStatus.Disabled => AccountLifecycle.Disabled,
            AccountResourceStatus.Retired => AccountLifecycle.Retired,
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

    private static string ProviderCode(UpstreamProvider provider) => provider switch
    {
        UpstreamProvider.OpenAi => "openai",
        UpstreamProvider.OpenAiCompatible => "openai_compatible",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    private static string StatusCode(AccountResourceStatus status) => status switch
    {
        AccountResourceStatus.Active => "active",
        AccountResourceStatus.Disabled => "disabled",
        AccountResourceStatus.Retired => "retired",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static string HealthCode(AccountHealth health) => health switch
    {
        AccountHealth.Unknown => "unknown",
        AccountHealth.Healthy => "healthy",
        AccountHealth.Degraded => "degraded",
        AccountHealth.Cooling => "cooling",
        AccountHealth.Unhealthy => "unhealthy",
        _ => throw new ArgumentOutOfRangeException(nameof(health)),
    };

    private static string CreateScope(AccountActor actor) =>
        $"supply:{actor.UserId.Value:D}:post:/api/v1/admin/accounts";

    private static string UpdateScope(AccountActor actor, EntityId accountId) =>
        $"supply:{actor.UserId.Value:D}:patch:/api/v1/admin/accounts/{accountId.Value:D}";

    private static string RetireScope(AccountActor actor, EntityId accountId) =>
        $"supply:{actor.UserId.Value:D}:delete:/api/v1/admin/accounts/{accountId.Value:D}";

    private static string ETag(long version) => $"\"v{version}\"";

    private static bool IsCanonicalETag(string etag) =>
        etag.Length >= 4
        && etag[0] == '"'
        && etag[1] == 'v'
        && etag[2] is >= '1' and <= '9'
        && etag[^1] == '"'
        && long.TryParse(
            etag.AsSpan(2, etag.Length - 3),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out long version)
        && version > 0;

    private static JsonElement Headers(string etag) =>
        JsonSerializer.SerializeToElement(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ETag"] = etag,
            });

    private static JsonElement Headers(string etag, string location) =>
        JsonSerializer.SerializeToElement(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ETag"] = etag,
                ["Location"] = location,
            });

    private static string? Header(JsonElement headers, string name) =>
        headers.ValueKind == JsonValueKind.Object
            && headers.TryGetProperty(name, out JsonElement value)
                ? value.GetString()
                : null;

    private static int HeaderCount(JsonElement headers) =>
        headers.ValueKind == JsonValueKind.Object
            ? headers.EnumerateObject().Count()
            : -1;

    private static string EncodeCursor(AccountResource account)
    {
        Span<byte> bytes = stackalloc byte[25];
        bytes[0] = 0x01;
        long unixMicroseconds = checked(
            (account.CreatedAt.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks) / 10);
        BinaryPrimitives.WriteInt64BigEndian(bytes[1..9], unixMicroseconds);
        Convert.FromHexString(account.Id.Value.ToString("N"), bytes[9..], out _, out _);
        return ToBase64Url(bytes);
    }

    private static bool TryDecodeCursor(
        string? encoded,
        out AccountCursor? cursor)
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
                || encoded.Any(static character =>
                    !(character is >= 'A' and <= 'Z'
                        or >= 'a' and <= 'z'
                        or >= '0' and <= '9'
                        or '-' or '_')))
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

            long unixMicroseconds =
                BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(1, 8));
            long ticks = checked(
                DateTime.UnixEpoch.Ticks + checked(unixMicroseconds * 10));
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

            cursor = new AccountCursor(
                new DateTimeOffset(ticks, TimeSpan.Zero),
                new EntityId(id));
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static string ToBase64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static Result<T> Failure<T>(
        string code,
        string description,
        long? retryAfterSeconds = null,
        string? etag = null,
        ResultErrorPresentation? presentation = null) => Result.Failure<T>(
            code,
            description,
            retryAfterSeconds,
            etag,
            presentation);

    private static Result<T> CoordinationUnavailable<T>() => Failure<T>(
        AccountErrorCodes.CoordinationUnavailable,
        "Redis Account lease coordination is temporarily unavailable.",
        retryAfterSeconds: 1);

    private sealed record MutationFailure(
        int Status,
        string Code,
        string Description,
        string? ETag);

    private sealed record ReplayFailureBody(
        string Description,
        ResultErrorPresentation Presentation);

    private sealed record PreparedCreate(
        string Name,
        string BaseUrl,
        string Credential,
        string CredentialPrefix,
        int MaxConcurrency,
        int Priority,
        int Weight,
        byte[] RequestHash)
    {
        public override string ToString() => nameof(PreparedCreate);
    }

    private sealed record PreparedUpdate(
        string? Name,
        string? BaseUrl,
        string? Credential,
        string? CredentialPrefix,
        AccountResourceStatus? Status,
        int? MaxConcurrency,
        int? Priority,
        int? Weight,
        string? Reason,
        byte[] RequestHash)
    {
        public override string ToString() => nameof(PreparedUpdate);
    }

    private sealed record AccountViewReplay(
        Guid Id,
        string Name,
        string Provider,
        string BaseUrl,
        string CredentialPrefix,
        string Status,
        string Health,
        DateTimeOffset? RetryAt,
        DateTimeOffset? LastCheckedAt,
        int ActiveLeases,
        int MaxConcurrency,
        int Priority,
        int Weight,
        long Version,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt)
    {
        internal static AccountViewReplay From(AccountView value) => new(
            value.Id.Value,
            value.Name,
            ProviderCode(value.Provider),
            value.BaseUrl.OriginalString,
            value.CredentialPrefix,
            LifecycleCode(value.Status),
            HealthCode(value.Health.Status),
            value.Health.RetryAt,
            value.Health.LastCheckedAt,
            value.ActiveLeases,
            value.MaxConcurrency,
            value.Priority,
            value.Weight,
            value.Version,
            value.CreatedAt,
            value.UpdatedAt);

        internal AccountView ToView()
        {
            if (Id == Guid.Empty
                || string.IsNullOrWhiteSpace(Name)
                || string.IsNullOrWhiteSpace(CredentialPrefix)
                || ActiveLeases is < 0 or > 10_000
                || MaxConcurrency is < 1 or > 10000
                || Priority is < -100000 or > 100000
                || Weight is < 1 or > 100000
                || Version <= 0
                || CreatedAt == default
                || UpdatedAt == default)
            {
                throw new InvalidOperationException(
                    "The Account replay body is invalid.");
            }

            string validatedBaseUrl = AccountInput.BaseUrl(BaseUrl);
            AccountHealth parsedHealth = ParseHealth(Health);
            DateTimeOffset? retryAt = parsedHealth == AccountHealth.Cooling
                ? RetryAt
                : null;
            return new AccountView(
                new EntityId(Id),
                Name,
                ParseProvider(Provider),
                new Uri(validatedBaseUrl, UriKind.Absolute),
                CredentialPrefix,
                ParseLifecycle(Status),
                new AccountHealthView(parsedHealth, retryAt, LastCheckedAt),
                ActiveLeases,
                MaxConcurrency,
                Priority,
                Weight,
                Version,
                CreatedAt,
                UpdatedAt);
        }

        private static string LifecycleCode(AccountLifecycle status) => status switch
        {
            AccountLifecycle.Active => "active",
            AccountLifecycle.Disabled => "disabled",
            AccountLifecycle.Retired => "retired",
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

        private static AccountLifecycle ParseLifecycle(string value) => value switch
        {
            "active" => AccountLifecycle.Active,
            "disabled" => AccountLifecycle.Disabled,
            "retired" => AccountLifecycle.Retired,
            _ => throw new InvalidOperationException(
                "The Account replay lifecycle is invalid."),
        };

        private static UpstreamProvider ParseProvider(string value) => value switch
        {
            "openai" => UpstreamProvider.OpenAi,
            "openai_compatible" => UpstreamProvider.OpenAiCompatible,
            _ => throw new InvalidOperationException(
                "The Account replay provider is invalid."),
        };

        private static AccountHealth ParseHealth(string value) => value switch
        {
            "unknown" => AccountHealth.Unknown,
            "healthy" => AccountHealth.Healthy,
            "degraded" => AccountHealth.Degraded,
            "cooling" => AccountHealth.Cooling,
            "unhealthy" => AccountHealth.Unhealthy,
            _ => throw new InvalidOperationException(
                "The Account replay health is invalid."),
        };
    }
}
#pragma warning restore MA0051
