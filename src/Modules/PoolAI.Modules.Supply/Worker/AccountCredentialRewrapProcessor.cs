using System.Diagnostics;
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Supply.Application.Ports;

namespace PoolAI.Modules.Supply.Worker;

internal sealed class AccountCredentialRewrapProcessor
{
    private const string AuditAction = "supply.account_credential_rewrap";
    private const string CompletionEventName =
        "supply.account_credential_rewrap_completed";

    private readonly IUnitOfWorkFactory _unitOfWorkFactory;
    private readonly IAccountCredentialStore _credentialStore;
    private readonly IAccountCredentialProtector _credentialProtector;
    private readonly IAuditAppender _auditAppender;
    private readonly IOperationalEventWriter _operationalEventWriter;

    public AccountCredentialRewrapProcessor(
        IUnitOfWorkFactory unitOfWorkFactory,
        IAccountCredentialStore credentialStore,
        IAccountCredentialProtector credentialProtector,
        IAuditAppender auditAppender,
        IOperationalEventWriter operationalEventWriter)
    {
        _unitOfWorkFactory = unitOfWorkFactory
            ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
        _credentialStore = credentialStore
            ?? throw new ArgumentNullException(nameof(credentialStore));
        _credentialProtector = credentialProtector
            ?? throw new ArgumentNullException(nameof(credentialProtector));
        _auditAppender = auditAppender
            ?? throw new ArgumentNullException(nameof(auditAppender));
        _operationalEventWriter = operationalEventWriter
            ?? throw new ArgumentNullException(nameof(operationalEventWriter));
    }

    internal async ValueTask<AccountCredentialRewrapProcessResult> ProcessAsync(
        IWorkerSessionLock jobLock,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobLock);
        if (batchSize is <= 0 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        RewrapCounters counters = new();
        if (!await OwnsLockAsync(jobLock, cancellationToken).ConfigureAwait(false))
        {
            return counters.OwnershipLost();
        }

        EntityId? cursor = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!await OwnsLockAsync(jobLock, cancellationToken).ConfigureAwait(false))
            {
                return counters.OwnershipLost();
            }

            IReadOnlyList<AccountCredentialSnapshot> batch = await _credentialStore
                .SelectBatchAsync(cursor, batchSize, cancellationToken)
                .ConfigureAwait(false);
            ValidateBatch(batch, cursor, batchSize);
            if (batch.Count == 0)
            {
                AccountCredentialRewrapProcessResult result = counters.Completed();
                await WriteCompletionEventAsync(result, cancellationToken)
                    .ConfigureAwait(false);
                return result;
            }

            foreach (AccountCredentialSnapshot snapshot in batch)
            {
                counters.ScannedCount++;
                if (!await ProcessSnapshotAsync(
                        jobLock,
                        snapshot,
                        counters,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    return counters.OwnershipLost();
                }
            }

            cursor = batch[^1].AccountId;
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new UnreachableException();
    }

    private async ValueTask<bool> ProcessSnapshotAsync(
        IWorkerSessionLock jobLock,
        AccountCredentialSnapshot snapshot,
        RewrapCounters counters,
        CancellationToken cancellationToken)
    {
        ValidateSnapshot(snapshot);
        if (!await OwnsLockAsync(jobLock, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        AccountCredentialRewrap rewrap = await _credentialProtector
            .RewrapAsync(
                snapshot.Envelope,
                snapshot.AccountId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!rewrap.Changed)
        {
            counters.AuthenticatedCurrentCount++;
            return true;
        }

        if (!await OwnsLockAsync(jobLock, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        AccountCredentialRewrapWriteDisposition firstDisposition =
            await WriteAsync(snapshot, rewrap, counters, cancellationToken)
                .ConfigureAwait(false);
        if (firstDisposition is AccountCredentialRewrapWriteDisposition.Rewrapped)
        {
            return true;
        }

        if (firstDisposition is not
            AccountCredentialRewrapWriteDisposition.CredentialRevisionConflict)
        {
            throw RewrapRejected(firstDisposition);
        }

        counters.CasMissCount++;
        return await RetryAfterConflictAsync(
            jobLock,
            snapshot.AccountId,
            counters,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> RetryAfterConflictAsync(
        IWorkerSessionLock jobLock,
        EntityId accountId,
        RewrapCounters counters,
        CancellationToken cancellationToken)
    {
        counters.RetryCount++;
        if (!await OwnsLockAsync(jobLock, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        AccountCredentialSnapshot current = await _credentialStore
            .FindAsync(accountId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The Account disappeared during credential rewrap.");
        ValidateSnapshot(current);
        AccountCredentialRewrap retry = await _credentialProtector
            .RewrapAsync(
                current.Envelope,
                current.AccountId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!retry.Changed)
        {
            counters.AuthenticatedCurrentCount++;
            return true;
        }

        if (!await OwnsLockAsync(jobLock, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        AccountCredentialRewrapWriteDisposition retryDisposition =
            await WriteAsync(current, retry, counters, cancellationToken)
                .ConfigureAwait(false);
        if (retryDisposition is
            AccountCredentialRewrapWriteDisposition.CredentialRevisionConflict)
        {
            counters.CasMissCount++;
            return await VerifyConvergedAfterFinalMissAsync(
                jobLock,
                current.AccountId,
                counters,
                cancellationToken).ConfigureAwait(false);
        }

        if (retryDisposition is not AccountCredentialRewrapWriteDisposition.Rewrapped)
        {
            throw RewrapRejected(retryDisposition);
        }

        return true;
    }

    private async ValueTask<bool> VerifyConvergedAfterFinalMissAsync(
        IWorkerSessionLock jobLock,
        EntityId accountId,
        RewrapCounters counters,
        CancellationToken cancellationToken)
    {
        if (!await OwnsLockAsync(jobLock, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        AccountCredentialSnapshot current = await _credentialStore
            .FindAsync(accountId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The Account disappeared after a credential rewrap conflict.");
        ValidateSnapshot(current);
        AccountCredentialRewrap verification = await _credentialProtector
            .RewrapAsync(
                current.Envelope,
                current.AccountId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!await OwnsLockAsync(jobLock, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (verification.Changed)
        {
            throw new InvalidOperationException(
                "The Account credential did not converge after the bounded rewrap retry.");
        }

        counters.AuthenticatedCurrentCount++;
        return true;
    }

    private async ValueTask<AccountCredentialRewrapWriteDisposition> WriteAsync(
        AccountCredentialSnapshot snapshot,
        AccountCredentialRewrap rewrap,
        RewrapCounters counters,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (unitOfWork.ConfigureAwait(false))
        {
            AccountCredentialRewrapWriteResult result = await _credentialStore
                .TryRewrapAsync(
                    new AccountCredentialRewrapWrite(
                        snapshot.AccountId,
                        snapshot.CredentialRevision,
                        rewrap.Envelope),
                    unitOfWork.Context,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.Disposition is not
                AccountCredentialRewrapWriteDisposition.Rewrapped)
            {
                return result.Disposition;
            }

            long expectedRevision = checked(snapshot.CredentialRevision + 1);
            if (result.CurrentCredentialRevision != expectedRevision)
            {
                throw new InvalidOperationException(
                    "The Account credential rewrap returned an invalid revision.");
            }

            await _auditAppender.AppendAsync(
                CreateAuditEntry(
                    snapshot,
                    expectedRevision),
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
            counters.RewrappedCount++;
            return result.Disposition;
        }
    }

    private async ValueTask WriteCompletionEventAsync(
        AccountCredentialRewrapProcessResult result,
        CancellationToken cancellationToken)
    {
        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            scanned_count = result.ScannedCount,
            authenticated_current_count = result.AuthenticatedCurrentCount,
            rewrapped_count = result.RewrappedCount,
            cas_miss_count = result.CasMissCount,
            retry_count = result.RetryCount,
        });
        await _operationalEventWriter.WriteAsync(
            CompletionEventName,
            payload,
            cancellationToken).ConfigureAwait(false);
    }

    private static AuditEntry CreateAuditEntry(
        AccountCredentialSnapshot snapshot,
        long newRevision) =>
        new(
            EntityId.New(),
            AuditActorType.Service,
            ActorUserId: null,
            AuditAction,
            TargetType: "account",
            snapshot.AccountId,
            RequestId: null,
            Reason: "key_rotation",
            IpAddress: null,
            UserAgent: null,
            BeforeState: null,
            AfterState: null,
            JsonSerializer.SerializeToElement(new
            {
                mode = "maintenance_rewrap",
                credential_revision_from = snapshot.CredentialRevision,
                credential_revision_to = newRevision,
            }));

    private static async ValueTask<bool> OwnsLockAsync(
        IWorkerSessionLock jobLock,
        CancellationToken cancellationToken) =>
        await jobLock.VerifyOwnershipAsync(cancellationToken).ConfigureAwait(false);

    private static void ValidateBatch(
        IReadOnlyList<AccountCredentialSnapshot> batch,
        EntityId? afterExclusive,
        int maximumCount)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count > maximumCount)
        {
            throw new InvalidOperationException(
                "The Account credential selector exceeded its batch bound.");
        }

        Guid? previous = afterExclusive?.Value;
        foreach (AccountCredentialSnapshot snapshot in batch)
        {
            ValidateSnapshot(snapshot);
            if (previous is not null
                && snapshot.AccountId.Value.CompareTo(previous.Value) <= 0)
            {
                throw new InvalidOperationException(
                    "The Account credential selector did not return a strict keyset page.");
            }

            previous = snapshot.AccountId.Value;
        }
    }

    private static void ValidateSnapshot(AccountCredentialSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.CredentialRevision < 1
            || snapshot.Envelope.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "The Account credential snapshot is invalid.");
        }
    }

    private static InvalidOperationException RewrapRejected(
        AccountCredentialRewrapWriteDisposition disposition) =>
        new($"The Account credential rewrap was rejected with {disposition}.");

    private sealed class RewrapCounters
    {
        internal long ScannedCount { get; set; }

        internal long AuthenticatedCurrentCount { get; set; }

        internal long RewrappedCount { get; set; }

        internal long CasMissCount { get; set; }

        internal long RetryCount { get; set; }

        internal AccountCredentialRewrapProcessResult Completed() =>
            Result(AccountCredentialRewrapProcessDisposition.Completed);

        internal AccountCredentialRewrapProcessResult OwnershipLost() =>
            Result(AccountCredentialRewrapProcessDisposition.OwnershipLost);

        private AccountCredentialRewrapProcessResult Result(
            AccountCredentialRewrapProcessDisposition disposition) =>
            new(
                disposition,
                ScannedCount,
                AuthenticatedCurrentCount,
                RewrappedCount,
                CasMissCount,
                RetryCount);
    }
}
