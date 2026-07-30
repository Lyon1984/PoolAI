#pragma warning disable MA0051 // The processor contract and its deterministic fakes stay together.
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Supply;
using PoolAI.Modules.Supply.Application;
using PoolAI.Modules.Supply.Application.Ports;
using PoolAI.Modules.Supply.Infrastructure.Workers;
using PoolAI.Modules.Supply.Worker;

namespace PoolAI.UnitTests;

public sealed class SupplyAccountCredentialRewrapWorkerTests
{
    private const string PlaintextSentinel =
        "poolai-unit-credential-plaintext-sentinel";
    private const string OldKeyId = "poolai-unit-old-kid";
    private const string CurrentKeyId = "poolai-unit-current-kid";
    private static readonly EntityId FirstAccount = new(Guid.Parse(
        "019c213b-f4e1-7955-b02e-197045646801"));
    private static readonly EntityId SecondAccount = new(Guid.Parse(
        "019c213b-f4e1-7955-b02e-197045646802"));

    [Fact]
    public void ApiSupplyRegistrationNeverAddsTheCredentialRewrapHostedLoop()
    {
        ServiceCollection services = new();

        services.AddSupplyModule(SupplyConfiguration(enabled: true));

        Assert.DoesNotContain(
            services,
            static descriptor => descriptor.ServiceType == typeof(IHostedService));
        Assert.DoesNotContain(
            services,
            static descriptor =>
                descriptor.ImplementationType == typeof(AccountCredentialRewrapService));
    }

    [Theory]
    [InlineData("not-base64", "Idempotency:RequestHashPepper is invalid.")]
    [InlineData("AQID", "Idempotency:RequestHashPepper must contain at least 256 bits.")]
    public void SupplyControlPlaneResolutionRejectsInvalidRequestHashPepper(
        string requestHashPepper,
        string expectedMessage)
    {
        IConfiguration configuration = SupplyConfiguration(enabled: false);
        configuration["Idempotency:RequestHashPepper"] = requestHashPepper;
        ServiceCollection services = new();
        services.AddSupplyModule(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() =>
                provider.GetRequiredService<AccountControlPlanePolicy>());

        Assert.Equal(expectedMessage, exception.Message);
    }

    [Fact]
    public void WorkerSupplyRegistrationDoesNotReadApiOnlyRequestHashPepper()
    {
        IConfiguration configuration = SupplyConfiguration(enabled: false);
        configuration["Idempotency:RequestHashPepper"] = null;
        ServiceCollection services = new();

        IServiceCollection returned = services.AddSupplyModule(configuration);

        Assert.Same(services, returned);
        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredService<AccountControlPlanePolicy>());
    }

    [Fact]
    public void AccountControlPlanePolicyRejectsWeakRequestHashPepper()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AccountControlPlanePolicy(null!));
        Assert.Throws<ArgumentException>(() =>
            new AccountControlPlanePolicy(new byte[31]));
    }

    [Fact]
    public void CredentialRewrapWorkerRequiresAnExplicitEnabledFlag()
    {
        ServiceCollection disabledServices = new();
        disabledServices.AddSupplyCredentialRewrapWorker(
            SupplyConfiguration(enabled: false));

        Assert.DoesNotContain(
            disabledServices,
            static descriptor => descriptor.ServiceType == typeof(IHostedService));
        Assert.DoesNotContain(
            disabledServices,
            static descriptor =>
                descriptor.ServiceType == typeof(AccountCredentialRewrapProcessor));

        ServiceCollection enabledServices = new();
        IServiceCollection returned = enabledServices
            .AddSupplyCredentialRewrapWorker(SupplyConfiguration(enabled: true));

        Assert.Same(enabledServices, returned);
        Assert.Single(
            enabledServices,
            static descriptor =>
                descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType
                    == typeof(AccountCredentialRewrapService));
        Assert.Contains(
            enabledServices,
            static descriptor =>
                descriptor.ServiceType == typeof(AccountCredentialRewrapProcessor));
        Assert.Contains(
            enabledServices,
            static descriptor =>
                descriptor.ServiceType
                    == typeof(AccountCredentialRewrapWorkerOptions));
    }

    [Fact]
    public void EnabledCredentialRewrapWorkerResolvesFromTheDefaultContainer()
    {
        TrackingUnitOfWorkFactory units = new();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IUnitOfWorkFactory>(units);
        services.AddSingleton<IAccountCredentialStore>(
            new ScriptedCredentialStore(units));
        services.AddSingleton<IAccountCredentialProtector>(
            new ScriptedProtector(units));
        services.AddSingleton<IAuditAppender>(
            new RecordingAuditAppender(units));
        services.AddSingleton<IOperationalEventWriter>(
            new RecordingOperationalEventWriter(units));
        services.AddSingleton<IWorkerSessionLockProvider>(
            new ScriptedLockProvider(new ScriptedJobLock()));
        services.AddSupplyCredentialRewrapWorker(
            SupplyConfiguration(enabled: true));

        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

        IHostedService hostedService = Assert.Single(
            provider.GetServices<IHostedService>());
        Assert.IsType<AccountCredentialRewrapService>(hostedService);
    }

    [Fact]
    public void CredentialRewrapWorkerRegistrationIsIdempotentForTheSameOptions()
    {
        ServiceCollection services = new();
        IConfiguration configuration = SupplyConfiguration(enabled: true);

        services.AddSupplyCredentialRewrapWorker(configuration);
        services.AddSupplyCredentialRewrapWorker(configuration);

        Assert.Single(
            services,
            static descriptor =>
                descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType
                    == typeof(AccountCredentialRewrapService));
        Assert.Single(
            services,
            static descriptor =>
                descriptor.ServiceType
                    == typeof(AccountCredentialRewrapWorkerOptions));
    }

    [Fact]
    public void CredentialRewrapWorkerRegistrationRejectsInconsistentOptions()
    {
        ServiceCollection services = new();
        services.AddSupplyCredentialRewrapWorker(
            SupplyConfiguration(enabled: false));

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() =>
                services.AddSupplyCredentialRewrapWorker(
                    SupplyConfiguration(enabled: true)));

        Assert.Equal(
            "Account credential rewrap was registered with inconsistent options.",
            exception.Message);
        Assert.DoesNotContain(
            services,
            static descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void CredentialRewrapWorkerOptionsBindSafeDefaultsAndExplicitValues()
    {
        AccountCredentialRewrapWorkerOptions defaults =
            AccountCredentialRewrapWorkerOptions.FromConfiguration(
                new ConfigurationBuilder().Build());
        AccountCredentialRewrapWorkerOptions configured =
            AccountCredentialRewrapWorkerOptions.FromConfiguration(
                SupplyConfiguration(
                    enabled: true,
                    batchSize: 321,
                    maxAttempts: 7,
                    retryDelaySeconds: 23));

        Assert.False(defaults.Enabled);
        Assert.Equal(100, defaults.BatchSize);
        Assert.Equal(3, defaults.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(5), defaults.RetryDelay);
        Assert.Equal(
            nameof(AccountCredentialRewrapWorkerOptions),
            defaults.ToString());
        Assert.True(configured.Enabled);
        Assert.Equal(321, configured.BatchSize);
        Assert.Equal(7, configured.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(23), configured.RetryDelay);
    }

    [Theory]
    [InlineData("batch", 0)]
    [InlineData("batch", 1001)]
    [InlineData("attempts", 0)]
    [InlineData("attempts", 11)]
    [InlineData("delay", 0)]
    [InlineData("delay", 61)]
    public void CredentialRewrapWorkerOptionsFailClosedAtEveryNumericBoundary(
        string setting,
        int invalidValue)
    {
        int batchSize = string.Equals(
            setting,
            "batch",
            StringComparison.Ordinal) ? invalidValue : 100;
        int maxAttempts = string.Equals(
            setting,
            "attempts",
            StringComparison.Ordinal) ? invalidValue : 3;
        int retryDelaySeconds = string.Equals(
            setting,
            "delay",
            StringComparison.Ordinal) ? invalidValue : 5;

        Assert.Throws<InvalidOperationException>(() =>
            AccountCredentialRewrapWorkerOptions.FromConfiguration(
                SupplyConfiguration(
                    enabled: true,
                    batchSize,
                    maxAttempts,
                    retryDelaySeconds)));
    }

    [Fact]
    public async Task UnavailableSessionLockIsPolledUntilTakeoverWithoutConsumingAttempts()
    {
        TrackingUnitOfWorkFactory units = new();
        ScriptedCredentialStore store = new(units, batches: [[]]);
        RecordingOperationalEventWriter events = new(units);
        ScriptedLockProvider locks = new(
            null,
            new ScriptedJobLock());
        using AccountCredentialRewrapService service = CreateService(
            units,
            store,
            new ScriptedProtector(units),
            events,
            locks,
            maxAttempts: 1);

        await RunServiceToCompletionAsync(service);

        Assert.Equal(2, locks.AcquireCount);
        AssertAggregateEvent(
            events.SingleEvent,
            new(
                AccountCredentialRewrapProcessDisposition.Completed,
                ScannedCount: 0,
                AuthenticatedCurrentCount: 0,
                RewrappedCount: 0,
                CasMissCount: 0,
                RetryCount: 0));
    }

    [Theory]
    [InlineData("database")]
    [InlineData("io")]
    [InlineData("timeout")]
    public async Task TransientWorkerFailureRetriesOutsideAnyUnitOfWork(
        string failure)
    {
        Exception transient = failure switch
        {
            "database" => new DeterministicDbException("database unavailable"),
            "io" => new IOException("transport interrupted"),
            "timeout" => new TimeoutException("command timed out"),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        TrackingUnitOfWorkFactory units = new();
        ScriptedCredentialStore store = new(
            units,
            batches: [[]],
            selectFailures: [transient]);
        RecordingOperationalEventWriter events = new(units);
        ScriptedLockProvider locks = new(
            new ScriptedJobLock(),
            new ScriptedJobLock());
        using AccountCredentialRewrapService service = CreateService(
            units,
            store,
            new ScriptedProtector(units),
            events,
            locks,
            maxAttempts: 2);

        await RunServiceToCompletionAsync(service);

        Assert.Equal(2, locks.AcquireCount);
        Assert.Equal(2, store.SelectCount);
        Assert.Equal(0, units.ActiveCount);
        Assert.Equal(0, units.BeginCount);
        Assert.Single(events.Events);
    }

    [Fact]
    public async Task TransientWorkerFailureStopsAtTheConfiguredAttemptBound()
    {
        TrackingUnitOfWorkFactory units = new();
        ScriptedCredentialStore store = new(
            units,
            selectFailures:
            [
                new DeterministicDbException("first database failure"),
                new DeterministicDbException("second database failure"),
            ]);
        RecordingOperationalEventWriter events = new(units);
        ScriptedLockProvider locks = new(
            new ScriptedJobLock(),
            new ScriptedJobLock());
        using AccountCredentialRewrapService service = CreateService(
            units,
            store,
            new ScriptedProtector(units),
            events,
            locks,
            maxAttempts: 2);

        DeterministicDbException thrown =
            await Assert.ThrowsAsync<DeterministicDbException>(() =>
                RunServiceToCompletionAsync(service));

        Assert.Equal("second database failure", thrown.Message);
        Assert.Equal(2, locks.AcquireCount);
        Assert.Equal(2, store.SelectCount);
        Assert.Equal(0, units.ActiveCount);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task OwnershipLossStartsANewOwnedAttempt()
    {
        TrackingUnitOfWorkFactory units = new();
        ScriptedCredentialStore store = new(units, batches: [[]]);
        RecordingOperationalEventWriter events = new(units);
        ScriptedLockProvider locks = new(
            new ScriptedJobLock(false),
            new ScriptedJobLock());
        using AccountCredentialRewrapService service = CreateService(
            units,
            store,
            new ScriptedProtector(units),
            events,
            locks,
            maxAttempts: 2);

        await RunServiceToCompletionAsync(service);

        Assert.Equal(2, locks.AcquireCount);
        Assert.Equal(1, store.SelectCount);
        Assert.Single(events.Events);
    }

    [Fact]
    public async Task HostedWorkerCancellationStopsAnInFlightLockProbe()
    {
        TrackingUnitOfWorkFactory units = new();
        CancellationLockProvider locks = new();
        using AccountCredentialRewrapService service = CreateService(
            units,
            new ScriptedCredentialStore(units),
            new ScriptedProtector(units),
            new RecordingOperationalEventWriter(units),
            locks,
            maxAttempts: 2);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await locks.Entered.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, locks.AcquireCount);
        Assert.NotNull(service.ExecuteTask);
        Assert.True(service.ExecuteTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task PermanentWorkerFailureIsNotRetriedOrSwallowed()
    {
        TrackingUnitOfWorkFactory units = new();
        AccountCredentialSnapshot snapshot = Snapshot(FirstAccount, 1, OldEnvelope());
        ScriptedCredentialStore store = new(
            units,
            batches: [[snapshot]]);
        CryptographicException expected = new(
            "Account credential envelope validation failed.");
        ScriptedProtector protector = new(units, expected);
        RecordingOperationalEventWriter events = new(units);
        ScriptedLockProvider locks = new(new ScriptedJobLock());
        using AccountCredentialRewrapService service = CreateService(
            units,
            store,
            protector,
            events,
            locks,
            maxAttempts: 3);

        CryptographicException thrown =
            await Assert.ThrowsAsync<CryptographicException>(() =>
                RunServiceToCompletionAsync(service));

        Assert.Same(expected, thrown);
        Assert.Equal(1, locks.AcquireCount);
        Assert.Single(protector.Calls);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task CurrentEnvelopeIsAuthenticatedWithoutOpeningAUnitOfWork()
    {
        TrackingUnitOfWorkFactory units = new();
        AccountCredentialSnapshot snapshot = Snapshot(FirstAccount, 7, OldEnvelope());
        ScriptedCredentialStore store = new(
            units,
            batches: [[snapshot], []]);
        ScriptedProtector protector = new(
            units,
            Rewrap(snapshot.Envelope, changed: false));
        RecordingAuditAppender audit = new(units);
        RecordingOperationalEventWriter events = new(units);
        AccountCredentialRewrapProcessor processor = new(
            units,
            store,
            protector,
            audit,
            events);

        AccountCredentialRewrapProcessResult result = await processor.ProcessAsync(
            new ScriptedJobLock(),
            batchSize: 100,
            TestContext.Current.CancellationToken);

        Assert.Equal(AccountCredentialRewrapProcessDisposition.Completed, result.Disposition);
        Assert.Equal(1, result.ScannedCount);
        Assert.Equal(1, result.AuthenticatedCurrentCount);
        Assert.Equal(0, result.RewrappedCount);
        Assert.Equal(0, result.CasMissCount);
        Assert.Equal(0, result.RetryCount);
        Assert.Equal(nameof(AccountCredentialRewrapProcessResult), result.ToString());
        Assert.Equal(0, units.BeginCount);
        Assert.Empty(store.Writes);
        Assert.Empty(audit.Entries);
        AssertAggregateEvent(events.SingleEvent, result);
    }

    [Fact]
    public async Task ChangedEnvelopeUsesOneShortUnitOfWorkAndWritesOneAuditFact()
    {
        TrackingUnitOfWorkFactory units = new();
        JsonElement oldEnvelope = OldEnvelope();
        JsonElement currentEnvelope = CurrentEnvelope();
        AccountCredentialSnapshot snapshot = Snapshot(FirstAccount, 11, oldEnvelope);
        ScriptedCredentialStore store = new(
            units,
            batches: [[snapshot], []],
            writeResults:
            [
                new(
                    AccountCredentialRewrapWriteDisposition.Rewrapped,
                    CurrentCredentialRevision: 12),
            ]);
        ScriptedProtector protector = new(
            units,
            Rewrap(currentEnvelope, changed: true));
        RecordingAuditAppender audit = new(units);
        RecordingOperationalEventWriter events = new(units);
        AccountCredentialRewrapProcessor processor = new(
            units,
            store,
            protector,
            audit,
            events);

        AccountCredentialRewrapProcessResult result = await processor.ProcessAsync(
            new ScriptedJobLock(),
            batchSize: 1,
            TestContext.Current.CancellationToken);

        Assert.Equal(AccountCredentialRewrapProcessDisposition.Completed, result.Disposition);
        Assert.Equal(1, result.ScannedCount);
        Assert.Equal(0, result.AuthenticatedCurrentCount);
        Assert.Equal(1, result.RewrappedCount);
        Assert.Equal(0, result.CasMissCount);
        Assert.Equal(0, result.RetryCount);
        Assert.Equal(1, units.BeginCount);
        Assert.Equal(1, units.CommitCount);
        Assert.Equal(1, units.DisposeCount);
        AccountCredentialRewrapWrite write = Assert.Single(store.Writes);
        Assert.Equal(FirstAccount, write.AccountId);
        Assert.Equal(11, write.ExpectedCredentialRevision);
        Assert.Equal(currentEnvelope.GetRawText(), write.Envelope.GetRawText());

        AuditEntry entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActorType.Service, entry.ActorType);
        Assert.Equal("supply.account_credential_rewrap", entry.Action);
        Assert.Equal("account", entry.TargetType);
        Assert.Equal(FirstAccount, entry.TargetId);
        Assert.Null(entry.BeforeState);
        Assert.Null(entry.AfterState);
        Assert.Equal(
            "maintenance_rewrap",
            entry.Metadata.GetProperty("mode").GetString());
        Assert.Equal(11, entry.Metadata
            .GetProperty("credential_revision_from")
            .GetInt64());
        Assert.Equal(12, entry.Metadata
            .GetProperty("credential_revision_to")
            .GetInt64());
        Assert.DoesNotContain(
            PlaintextSentinel,
            entry.Metadata.GetRawText(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            OldKeyId,
            entry.Metadata.GetRawText(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            CurrentKeyId,
            entry.Metadata.GetRawText(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "credential_envelope",
            entry.Metadata.GetRawText(),
            StringComparison.Ordinal);
        AssertAggregateEvent(events.SingleEvent, result);
    }

    [Fact]
    public async Task CasMissRereadsAndRecomputesOnceThenStopsAfterSecondMiss()
    {
        TrackingUnitOfWorkFactory units = new();
        AccountCredentialSnapshot first = Snapshot(FirstAccount, 3, OldEnvelope());
        AccountCredentialSnapshot concurrent = Snapshot(
            FirstAccount,
            4,
            ConcurrentEnvelope());
        AccountCredentialSnapshot converged = Snapshot(
            FirstAccount,
            5,
            SecondCurrentEnvelope());
        ScriptedCredentialStore store = new(
            units,
            batches: [[first], []],
            finds: [concurrent, converged],
            writeResults:
            [
                new(
                    AccountCredentialRewrapWriteDisposition.CredentialRevisionConflict,
                    CurrentCredentialRevision: 4),
                new(
                    AccountCredentialRewrapWriteDisposition.CredentialRevisionConflict,
                    CurrentCredentialRevision: 5),
            ]);
        ScriptedProtector protector = new(
            units,
            Rewrap(CurrentEnvelope(), changed: true),
            Rewrap(SecondCurrentEnvelope(), changed: true),
            Rewrap(converged.Envelope, changed: false));
        RecordingAuditAppender audit = new(units);
        RecordingOperationalEventWriter events = new(units);
        AccountCredentialRewrapProcessor processor = new(
            units,
            store,
            protector,
            audit,
            events);

        AccountCredentialRewrapProcessResult result = await processor.ProcessAsync(
            new ScriptedJobLock(),
            batchSize: 100,
            TestContext.Current.CancellationToken);

        Assert.Equal(AccountCredentialRewrapProcessDisposition.Completed, result.Disposition);
        Assert.Equal(1, result.ScannedCount);
        Assert.Equal(1, result.AuthenticatedCurrentCount);
        Assert.Equal(0, result.RewrappedCount);
        Assert.Equal(2, result.CasMissCount);
        Assert.Equal(1, result.RetryCount);
        Assert.Equal(3, protector.Calls.Count);
        Assert.Equal(
            [
                first.Envelope.GetRawText(),
                concurrent.Envelope.GetRawText(),
                converged.Envelope.GetRawText(),
            ],
            protector.Calls.Select(static call => call.Envelope.GetRawText()));
        Assert.Equal([3L, 4L], store.Writes
            .Select(static write => write.ExpectedCredentialRevision));
        Assert.Equal(2, store.FindCount);
        Assert.Equal(2, units.BeginCount);
        Assert.Equal(0, units.CommitCount);
        Assert.Equal(2, units.DisposeCount);
        Assert.Empty(audit.Entries);
        AssertAggregateEvent(events.SingleEvent, result);
    }

    [Fact]
    public async Task CasMissRewrapsTheRereadSnapshotOnOneBoundedRetry()
    {
        TrackingUnitOfWorkFactory units = new();
        AccountCredentialSnapshot first = Snapshot(FirstAccount, 8, OldEnvelope());
        AccountCredentialSnapshot concurrent = Snapshot(
            FirstAccount,
            9,
            ConcurrentEnvelope());
        ScriptedCredentialStore store = new(
            units,
            batches: [[first], []],
            finds: [concurrent],
            writeResults:
            [
                new(
                    AccountCredentialRewrapWriteDisposition.CredentialRevisionConflict,
                    CurrentCredentialRevision: 9),
                new(
                    AccountCredentialRewrapWriteDisposition.Rewrapped,
                    CurrentCredentialRevision: 10),
            ]);
        ScriptedProtector protector = new(
            units,
            Rewrap(CurrentEnvelope(), changed: true),
            Rewrap(SecondCurrentEnvelope(), changed: true));
        RecordingAuditAppender audit = new(units);
        RecordingOperationalEventWriter events = new(units);
        AccountCredentialRewrapProcessor processor = new(
            units,
            store,
            protector,
            audit,
            events);

        AccountCredentialRewrapProcessResult result = await processor.ProcessAsync(
            new ScriptedJobLock(),
            batchSize: 100,
            TestContext.Current.CancellationToken);

        Assert.Equal(AccountCredentialRewrapProcessDisposition.Completed, result.Disposition);
        Assert.Equal(1, result.ScannedCount);
        Assert.Equal(1, result.RewrappedCount);
        Assert.Equal(1, result.CasMissCount);
        Assert.Equal(1, result.RetryCount);
        Assert.Equal([8L, 9L], store.Writes
            .Select(static write => write.ExpectedCredentialRevision));
        Assert.Equal(1, store.FindCount);
        Assert.Equal(2, units.BeginCount);
        Assert.Equal(1, units.CommitCount);
        Assert.Equal(2, units.DisposeCount);
        AuditEntry entry = Assert.Single(audit.Entries);
        Assert.Equal(9, entry.Metadata
            .GetProperty("credential_revision_from")
            .GetInt64());
        Assert.Equal(10, entry.Metadata
            .GetProperty("credential_revision_to")
            .GetInt64());
        AssertAggregateEvent(events.SingleEvent, result);
    }

    [Fact]
    public async Task CasMissRereadOfCurrentEnvelopeBecomesANoop()
    {
        TrackingUnitOfWorkFactory units = new();
        AccountCredentialSnapshot first = Snapshot(FirstAccount, 5, OldEnvelope());
        AccountCredentialSnapshot replacement = Snapshot(
            FirstAccount,
            6,
            ConcurrentEnvelope());
        ScriptedCredentialStore store = new(
            units,
            batches: [[first], []],
            finds: [replacement],
            writeResults:
            [
                new(
                    AccountCredentialRewrapWriteDisposition.CredentialRevisionConflict,
                    CurrentCredentialRevision: 6),
            ]);
        ScriptedProtector protector = new(
            units,
            Rewrap(CurrentEnvelope(), changed: true),
            Rewrap(replacement.Envelope, changed: false));
        RecordingOperationalEventWriter events = new(units);
        AccountCredentialRewrapProcessor processor = new(
            units,
            store,
            protector,
            new RecordingAuditAppender(units),
            events);

        AccountCredentialRewrapProcessResult result = await processor.ProcessAsync(
            new ScriptedJobLock(),
            batchSize: 100,
            TestContext.Current.CancellationToken);

        Assert.Equal(AccountCredentialRewrapProcessDisposition.Completed, result.Disposition);
        Assert.Equal(1, result.AuthenticatedCurrentCount);
        Assert.Equal(0, result.RewrappedCount);
        Assert.Equal(1, result.CasMissCount);
        Assert.Equal(1, result.RetryCount);
        Assert.Single(store.Writes);
        Assert.Equal(1, store.FindCount);
        Assert.Equal(1, units.BeginCount);
        Assert.Equal(0, units.CommitCount);
        AssertAggregateEvent(events.SingleEvent, result);
    }

    [Fact]
    public async Task OwnershipLossAfterCryptographyPreventsAnyDatabaseWrite()
    {
        TrackingUnitOfWorkFactory units = new();
        AccountCredentialSnapshot snapshot = Snapshot(
            FirstAccount,
            1,
            OldEnvelope());
        ScriptedCredentialStore store = new(
            units,
            batches: [[snapshot]]);
        ScriptedProtector protector = new(
            units,
            Rewrap(CurrentEnvelope(), changed: true));
        RecordingOperationalEventWriter events = new(units);
        AccountCredentialRewrapProcessor processor = new(
            units,
            store,
            protector,
            new RecordingAuditAppender(units),
            events);
        ScriptedJobLock jobLock = new(true, true, true, false);

        AccountCredentialRewrapProcessResult result = await processor.ProcessAsync(
            jobLock,
            batchSize: 100,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            AccountCredentialRewrapProcessDisposition.OwnershipLost,
            result.Disposition);
        Assert.Equal(1, result.ScannedCount);
        Assert.Equal(0, result.RewrappedCount);
        Assert.Equal(4, jobLock.VerifyCount);
        Assert.Single(protector.Calls);
        Assert.Empty(store.Writes);
        Assert.Equal(0, units.BeginCount);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task CryptographicFailureStopsTheBatchAndDoesNotPublishCompletion()
    {
        TrackingUnitOfWorkFactory units = new();
        AccountCredentialSnapshot first = Snapshot(FirstAccount, 1, OldEnvelope());
        AccountCredentialSnapshot second = Snapshot(SecondAccount, 1, OldEnvelope());
        CryptographicException expected = new(
            "Account credential envelope validation failed.");
        ScriptedCredentialStore store = new(
            units,
            batches: [[first, second]]);
        ScriptedProtector protector = new(units, expected);
        RecordingOperationalEventWriter events = new(units);
        AccountCredentialRewrapProcessor processor = new(
            units,
            store,
            protector,
            new RecordingAuditAppender(units),
            events);

        CryptographicException thrown =
            await Assert.ThrowsAsync<CryptographicException>(() =>
                processor.ProcessAsync(
                        new ScriptedJobLock(),
                        batchSize: 100,
                        TestContext.Current.CancellationToken)
                    .AsTask());

        Assert.Same(expected, thrown);
        Assert.Single(protector.Calls);
        Assert.Equal(FirstAccount, protector.Calls[0].AccountId);
        Assert.Empty(store.Writes);
        Assert.Equal(0, units.BeginCount);
        Assert.Empty(events.Events);
    }

    [Fact]
    public void CredentialPersistenceRecordsNeverRenderEnvelopeContent()
    {
        JsonElement envelope = OldEnvelope();
        object[] records =
        [
            new AccountCredentialCreate(
                FirstAccount,
                "openai",
                "primary",
                "https://example.invalid",
                envelope,
                "sk-unit",
                "unit hint",
                MaxConcurrency: 1,
                Priority: 0,
                Weight: 1),
            new AccountCredentialCreateResult(
                AccountCredentialCreateDisposition.Created,
                CurrentVersion: 1,
                CurrentCredentialRevision: 1),
            new AccountCredentialReplacement(
                FirstAccount,
                ExpectedVersion: 1,
                envelope,
                "sk-unit",
                "unit hint"),
            new AccountCredentialReplacementResult(
                AccountCredentialReplacementDisposition.Replaced,
                CurrentVersion: 2,
                CurrentCredentialRevision: 2),
            new AccountCredentialRewrapWrite(
                FirstAccount,
                ExpectedCredentialRevision: 1,
                envelope),
            new AccountCredentialRewrapWriteResult(
                AccountCredentialRewrapWriteDisposition.Rewrapped,
                CurrentCredentialRevision: 2),
            new AccountCredentialSnapshot(
                FirstAccount,
                CredentialRevision: 1,
                envelope),
            new AccountCredentialRewrapProcessResult(
                AccountCredentialRewrapProcessDisposition.Completed,
                ScannedCount: 1,
                AuthenticatedCurrentCount: 0,
                RewrappedCount: 1,
                CasMissCount: 0,
                RetryCount: 0),
        ];

        foreach (object record in records)
        {
            string rendered = Assert.IsType<string>(record.ToString());
            Assert.Equal(record.GetType().Name, rendered);
            Assert.DoesNotContain(OldKeyId, rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("ciphertext", rendered, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task ProcessorRejectsInvalidBatchBoundsBeforeDependencies(
        int batchSize)
    {
        TrackingUnitOfWorkFactory units = new();
        ScriptedCredentialStore store = new(units);
        ScriptedProtector protector = new(units);
        RecordingOperationalEventWriter events = new(units);
        AccountCredentialRewrapProcessor processor = new(
            units,
            store,
            protector,
            new RecordingAuditAppender(units),
            events);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            processor.ProcessAsync(
                    new ScriptedJobLock(),
                    batchSize,
                    TestContext.Current.CancellationToken)
                .AsTask());

        Assert.Equal(0, store.SelectCount);
        Assert.Empty(protector.Calls);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task OwnershipLossBeforeSelectionStopsWithoutReadingCredentials()
    {
        TrackingUnitOfWorkFactory units = new();
        ScriptedCredentialStore store = new(units);
        ScriptedProtector protector = new(units);
        RecordingOperationalEventWriter events = new(units);
        AccountCredentialRewrapProcessor processor = new(
            units,
            store,
            protector,
            new RecordingAuditAppender(units),
            events);

        AccountCredentialRewrapProcessResult result = await processor.ProcessAsync(
            new ScriptedJobLock(true, false),
            batchSize: 100,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            AccountCredentialRewrapProcessDisposition.OwnershipLost,
            result.Disposition);
        Assert.Equal(0, result.ScannedCount);
        Assert.Equal(0, store.SelectCount);
        Assert.Empty(protector.Calls);
        Assert.Empty(events.Events);
    }

    [Theory]
    [InlineData("oversized", 1,
        "The Account credential selector exceeded its batch bound.")]
    [InlineData("non_keyset", 2,
        "The Account credential selector did not return a strict keyset page.")]
    [InlineData("invalid_snapshot", 1,
        "The Account credential snapshot is invalid.")]
    public async Task SelectorInvariantViolationsFailClosedBeforeCryptography(
        string violation,
        int batchSize,
        string expectedMessage)
    {
        TrackingUnitOfWorkFactory units = new();
        IReadOnlyList<AccountCredentialSnapshot> batch = violation switch
        {
            "oversized" =>
            [
                Snapshot(FirstAccount, 1, OldEnvelope()),
                Snapshot(SecondAccount, 1, OldEnvelope()),
            ],
            "non_keyset" =>
            [
                Snapshot(SecondAccount, 1, OldEnvelope()),
                Snapshot(FirstAccount, 1, OldEnvelope()),
            ],
            "invalid_snapshot" =>
            [
                Snapshot(FirstAccount, 0, OldEnvelope()),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(violation)),
        };
        ScriptedCredentialStore store = new(units, batches: [batch]);
        ScriptedProtector protector = new(units);
        RecordingOperationalEventWriter events = new(units);
        AccountCredentialRewrapProcessor processor = new(
            units,
            store,
            protector,
            new RecordingAuditAppender(units),
            events);

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                processor.ProcessAsync(
                        new ScriptedJobLock(),
                        batchSize,
                        TestContext.Current.CancellationToken)
                    .AsTask());

        Assert.Equal(expectedMessage, exception.Message);
        Assert.Empty(protector.Calls);
        Assert.Equal(0, units.BeginCount);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task NonCasRewrapRejectionFailsClosedWithoutCommit()
    {
        TrackingUnitOfWorkFactory units = new();
        AccountCredentialSnapshot snapshot = Snapshot(
            FirstAccount,
            1,
            OldEnvelope());
        ScriptedCredentialStore store = new(
            units,
            batches: [[snapshot]],
            writeResults:
            [
                new(
                    AccountCredentialRewrapWriteDisposition.ContentMismatch,
                    CurrentCredentialRevision: 1),
            ]);
        ScriptedProtector protector = new(
            units,
            Rewrap(CurrentEnvelope(), changed: true));
        RecordingOperationalEventWriter events = new(units);
        AccountCredentialRewrapProcessor processor = new(
            units,
            store,
            protector,
            new RecordingAuditAppender(units),
            events);

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                processor.ProcessAsync(
                        new ScriptedJobLock(),
                        batchSize: 100,
                        TestContext.Current.CancellationToken)
                    .AsTask());

        Assert.Equal(
            "The Account credential rewrap was rejected with ContentMismatch.",
            exception.Message);
        Assert.Single(store.Writes);
        Assert.Equal(1, units.BeginCount);
        Assert.Equal(0, units.CommitCount);
        Assert.Equal(1, units.DisposeCount);
        Assert.Empty(events.Events);
    }

    private static AccountCredentialSnapshot Snapshot(
        EntityId accountId,
        long revision,
        JsonElement envelope) => new(accountId, revision, envelope);

    private static AccountCredentialRewrapService CreateService(
        TrackingUnitOfWorkFactory units,
        ScriptedCredentialStore store,
        ScriptedProtector protector,
        RecordingOperationalEventWriter events,
        IWorkerSessionLockProvider lockProvider,
        int maxAttempts)
    {
        AccountCredentialRewrapProcessor processor = new(
            units,
            store,
            protector,
            new RecordingAuditAppender(units),
            events);
        return new AccountCredentialRewrapService(
            lockProvider,
            processor,
            new AccountCredentialRewrapWorkerOptions(
                Enabled: true,
                BatchSize: 100,
                MaxAttempts: maxAttempts,
                RetryDelay: TimeSpan.Zero),
            NullLogger<AccountCredentialRewrapService>.Instance);
    }

    private static async Task RunServiceToCompletionAsync(
        AccountCredentialRewrapService service)
    {
        await service.StartAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        Assert.NotNull(service.ExecuteTask);
        await service.ExecuteTask.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    private static IConfiguration SupplyConfiguration(
        bool enabled,
        int batchSize = 100,
        int maxAttempts = 3,
        int retryDelaySeconds = 5)
    {
        string currentKey = Convert.ToBase64String(
            Enumerable.Repeat((byte)0x5a, 32).ToArray());
        Dictionary<string, string?> values = new(StringComparer.Ordinal)
        {
            ["Idempotency:RequestHashPepper"] = Convert.ToBase64String(
                Enumerable.Repeat((byte)0x5c, 32).ToArray()),
            ["Secrets:Envelope:CurrentKeyId"] = CurrentKeyId,
            ["Secrets:Envelope:CurrentKey"] = currentKey,
            [$"Secrets:Envelope:DecryptKeyRing:{CurrentKeyId}"] = currentKey,
            ["Secrets:Envelope:Rewrap:Enabled"] =
                enabled ? "true" : "false",
            ["Secrets:Envelope:Rewrap:BatchSize"] =
                batchSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Secrets:Envelope:Rewrap:MaxAttempts"] =
                maxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Secrets:Envelope:Rewrap:RetryDelaySeconds"] =
                retryDelaySeconds.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
        };
        if (enabled)
        {
            values["Secrets:Envelope:DecryptKeyRing:unit-retired-kid"] =
                Convert.ToBase64String(
                    Enumerable.Repeat((byte)0x5b, 32).ToArray());
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static AccountCredentialRewrap Rewrap(
        JsonElement envelope,
        bool changed) => new(
        envelope,
        changed ? OldKeyId : CurrentKeyId,
        CurrentKeyId,
        changed);

    private static JsonElement OldEnvelope() => Envelope(
        OldKeyId,
        "old-wrapped-dek",
        "stable-ciphertext");

    private static JsonElement ConcurrentEnvelope() => Envelope(
        "concurrent-current-kid",
        "concurrent-wrapped-dek",
        "concurrent-ciphertext");

    private static JsonElement CurrentEnvelope() => Envelope(
        CurrentKeyId,
        "current-wrapped-dek",
        "stable-ciphertext");

    private static JsonElement SecondCurrentEnvelope() => Envelope(
        CurrentKeyId,
        "second-current-wrapped-dek",
        "concurrent-ciphertext");

    private static JsonElement Envelope(
        string keyId,
        string wrappedDek,
        string ciphertext) => JsonSerializer.SerializeToElement(new
        {
            v = 1,
            alg = "A256GCM+A256GCM-v1",
            kid = keyId,
            wrapped_dek = wrappedDek,
            wrap_nonce = "unit-wrap-nonce",
            wrap_tag = "unit-wrap-tag",
            nonce = "unit-nonce",
            ciphertext,
            tag = "unit-tag",
        });

    private static void AssertAggregateEvent(
        (string Name, JsonElement Payload) operationalEvent,
        AccountCredentialRewrapProcessResult result)
    {
        Assert.Equal(
            "supply.account_credential_rewrap_completed",
            operationalEvent.Name);
        string[] properties = operationalEvent.Payload
            .EnumerateObject()
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                "authenticated_current_count",
                "cas_miss_count",
                "retry_count",
                "rewrapped_count",
                "scanned_count",
            ],
            properties);
        Assert.Equal(
            result.ScannedCount,
            operationalEvent.Payload.GetProperty("scanned_count").GetInt64());
        Assert.Equal(
            result.AuthenticatedCurrentCount,
            operationalEvent.Payload
                .GetProperty("authenticated_current_count")
                .GetInt64());
        Assert.Equal(
            result.RewrappedCount,
            operationalEvent.Payload.GetProperty("rewrapped_count").GetInt64());
        Assert.Equal(
            result.CasMissCount,
            operationalEvent.Payload.GetProperty("cas_miss_count").GetInt64());
        Assert.Equal(
            result.RetryCount,
            operationalEvent.Payload.GetProperty("retry_count").GetInt64());

        string raw = operationalEvent.Payload.GetRawText();
        foreach (string forbidden in new[]
        {
            PlaintextSentinel,
            OldKeyId,
            CurrentKeyId,
            "concurrent-current-kid",
            "credential_envelope",
            "wrapped_dek",
            "ciphertext",
            "nonce",
            "tag",
        })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.Ordinal);
        }
    }

    private static InvalidOperationException Unexpected() => new(
        "The test invoked an unexpected Account credential dependency path.");

    private sealed class TrackingUnitOfWorkFactory : IUnitOfWorkFactory
    {
        private int _activeCount;

        internal int ActiveCount => Volatile.Read(ref _activeCount);

        internal int BeginCount { get; private set; }

        internal int CommitCount { get; private set; }

        internal int DisposeCount { get; private set; }

        public ValueTask<IUnitOfWork> BeginAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(0, ActiveCount);
            BeginCount++;
            _ = Interlocked.Increment(ref _activeCount);
            return ValueTask.FromResult<IUnitOfWork>(new UnitOfWork(this));
        }

        private sealed class UnitOfWork(TrackingUnitOfWorkFactory owner) : IUnitOfWork
        {
            private int _disposed;

            public IUnitOfWorkContext Context { get; } = new ContextMarker();

            public ValueTask CommitAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.Equal(1, owner.ActiveCount);
                owner.CommitCount++;
                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    owner.DisposeCount++;
                    _ = Interlocked.Decrement(ref owner._activeCount);
                }

                return ValueTask.CompletedTask;
            }
        }

        private sealed class ContextMarker : IUnitOfWorkContext
        {
        }
    }

    private sealed class ScriptedCredentialStore : IAccountCredentialStore
    {
        private readonly TrackingUnitOfWorkFactory _units;
        private readonly Queue<IReadOnlyList<AccountCredentialSnapshot>> _batches;
        private readonly Queue<AccountCredentialSnapshot?> _finds;
        private readonly Queue<AccountCredentialRewrapWriteResult> _writeResults;
        private readonly Queue<Exception> _selectFailures;

        internal ScriptedCredentialStore(
            TrackingUnitOfWorkFactory units,
            IEnumerable<IReadOnlyList<AccountCredentialSnapshot>>? batches = null,
            IEnumerable<AccountCredentialSnapshot?>? finds = null,
            IEnumerable<AccountCredentialRewrapWriteResult>? writeResults = null,
            IEnumerable<Exception>? selectFailures = null)
        {
            _units = units;
            _batches = new Queue<IReadOnlyList<AccountCredentialSnapshot>>(
                batches ?? []);
            _finds = new Queue<AccountCredentialSnapshot?>(finds ?? []);
            _writeResults = new Queue<AccountCredentialRewrapWriteResult>(
                writeResults ?? []);
            _selectFailures = new Queue<Exception>(selectFailures ?? []);
        }

        internal List<AccountCredentialRewrapWrite> Writes { get; } = [];

        internal int FindCount { get; private set; }

        internal int SelectCount { get; private set; }

        public ValueTask<AccountCredentialCreateResult> CreateAsync(
            AccountCredentialCreate account,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<AccountCredentialCreateResult>(Unexpected());

        public ValueTask<AccountCredentialReplacementResult> ReplaceAsync(
            AccountCredentialReplacement replacement,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<AccountCredentialReplacementResult>(Unexpected());

        public ValueTask<IReadOnlyList<AccountCredentialSnapshot>> SelectBatchAsync(
            EntityId? afterExclusive,
            int maximumCount,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(0, _units.ActiveCount);
            Assert.InRange(maximumCount, 1, 1000);
            SelectCount++;
            if (_selectFailures.Count > 0)
            {
                return ValueTask.FromException<
                    IReadOnlyList<AccountCredentialSnapshot>>(
                    _selectFailures.Dequeue());
            }

            return ValueTask.FromResult(
                _batches.Count == 0
                    ? (IReadOnlyList<AccountCredentialSnapshot>)[]
                    : _batches.Dequeue());
        }

        public ValueTask<AccountCredentialSnapshot?> FindAsync(
            EntityId accountId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(0, _units.ActiveCount);
            FindCount++;
            return ValueTask.FromResult(
                _finds.Count == 0 ? null : _finds.Dequeue());
        }

        public ValueTask<AccountCredentialRewrapWriteResult> TryRewrapAsync(
            AccountCredentialRewrapWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(1, _units.ActiveCount);
            Writes.Add(write);
            return ValueTask.FromResult(
                _writeResults.Count == 0
                    ? throw Unexpected()
                    : _writeResults.Dequeue());
        }
    }

    private sealed class ScriptedProtector : IAccountCredentialProtector
    {
        private readonly TrackingUnitOfWorkFactory _units;
        private readonly Queue<object> _results;

        internal ScriptedProtector(
            TrackingUnitOfWorkFactory units,
            params object[] results)
        {
            _units = units;
            _results = new Queue<object>(results);
        }

        internal List<(JsonElement Envelope, EntityId AccountId)> Calls { get; } = [];

        public AccountCredentialProtection Protect(
            string credential,
            EntityId accountId) => throw Unexpected();

        public ValueTask<AccountCredentialLease> UnprotectAsync(
            JsonElement envelope,
            EntityId accountId,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<AccountCredentialLease>(Unexpected());

        public ValueTask<AccountCredentialRewrap> RewrapAsync(
            JsonElement envelope,
            EntityId accountId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(0, _units.ActiveCount);
            Calls.Add((envelope.Clone(), accountId));
            object result = _results.Count == 0
                ? throw Unexpected()
                : _results.Dequeue();
            return result switch
            {
                AccountCredentialRewrap rewrap => ValueTask.FromResult(rewrap),
                Exception exception =>
                    ValueTask.FromException<AccountCredentialRewrap>(exception),
                _ => ValueTask.FromException<AccountCredentialRewrap>(Unexpected()),
            };
        }
    }

    private sealed class RecordingAuditAppender(
        TrackingUnitOfWorkFactory units) : IAuditAppender
    {
        internal List<AuditEntry> Entries { get; } = [];

        public ValueTask AppendAsync(
            AuditEntry entry,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(1, units.ActiveCount);
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingOperationalEventWriter(
        TrackingUnitOfWorkFactory units) : IOperationalEventWriter
    {
        internal List<(string Name, JsonElement Payload)> Events { get; } = [];

        internal (string Name, JsonElement Payload) SingleEvent =>
            Assert.Single(Events);

        public ValueTask WriteAsync(
            string eventName,
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(0, units.ActiveCount);
            Events.Add((eventName, payload.Clone()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedJobLock(params bool[] ownership) : IWorkerSessionLock
    {
        private readonly Queue<bool> _ownership = new(ownership);

        internal int VerifyCount { get; private set; }

        public WorkerJobIdentity Job => WorkerJobs.AccountCredentialRewrap;

        public long LockId => 1;

        public ValueTask<bool> VerifyOwnershipAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifyCount++;
            return ValueTask.FromResult(
                _ownership.Count == 0 || _ownership.Dequeue());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedLockProvider(
        params IWorkerSessionLock?[] locks) : IWorkerSessionLockProvider
    {
        private readonly Queue<IWorkerSessionLock?> _locks = new(locks);

        internal int AcquireCount { get; private set; }

        public ValueTask<IWorkerSessionLock?> TryAcquireAsync(
            WorkerJobIdentity job,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(WorkerJobs.AccountCredentialRewrap, job);
            AcquireCount++;
            return ValueTask.FromResult(
                _locks.Count == 0 ? throw Unexpected() : _locks.Dequeue());
        }
    }

    private sealed class CancellationLockProvider : IWorkerSessionLockProvider
    {
        internal TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal int AcquireCount { get; private set; }

        public async ValueTask<IWorkerSessionLock?> TryAcquireAsync(
            WorkerJobIdentity job,
            CancellationToken cancellationToken)
        {
            Assert.Equal(WorkerJobs.AccountCredentialRewrap, job);
            AcquireCount++;
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ConfigureAwait(false);
            throw Unexpected();
        }
    }

    private sealed class DeterministicDbException(string message)
        : DbException(message)
    {
    }
}
