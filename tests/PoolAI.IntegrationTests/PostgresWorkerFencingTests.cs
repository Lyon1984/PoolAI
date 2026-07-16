using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using PoolAI.BuildingBlocks;
using PoolAI.Database.Migrations;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Operations.Abstractions;
using Testcontainers.PostgreSql;

namespace PoolAI.IntegrationTests;

public sealed class PostgresWorkerFencingTests
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task EmailDeliveryAndWorkerSessionLocksUseDatabaseFencing()
    {
        // Governing contracts: docs/database/README.md sections 3 and 8 plus
        // AC-037/041 require real Worker-role claims, generation fencing and
        // PostgreSQL session advisory locks without a Redis leader lease.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        WorkerPostgresEnvironment environment = await WorkerPostgresEnvironment
            .StartAsync(cancellationToken)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable environmentLease = environment.ConfigureAwait(true);

        await AssertEmailDeliveryFencingAsync(environment, cancellationToken)
            .ConfigureAwait(true);
        await AssertWorkerSessionLocksAsync(environment, cancellationToken)
            .ConfigureAwait(true);
    }

    private static async ValueTask AssertEmailDeliveryFencingAsync(
        WorkerPostgresEnvironment environment,
        CancellationToken cancellationToken)
    {
        IEmailOutboxDeliveryStore store = CreateInternal<IEmailOutboxDeliveryStore>(
            typeof(PoolAI.Modules.Identity.DependencyInjection).Assembly,
            "PoolAI.Modules.Identity.Infrastructure.Persistence.PostgresEmailOutboxDeliveryStore");
        PreparedEmails prepared = await PrepareEmailsAsApiAsync(environment, cancellationToken)
            .ConfigureAwait(true);

        EmailOutboxMessage firstMessage = await ClaimInitialEmailAsync(
            store,
            environment,
            prepared,
            cancellationToken).ConfigureAwait(true);

        EmailOutboxMessage takeover = await TakeOverEmailAsync(
            store,
            environment,
            firstMessage,
            prepared.FirstEmailId,
            cancellationToken).ConfigureAwait(true);

        await AssertStaleEmailLeaseRejectedAsync(
            store,
            environment,
            firstMessage.Lease,
            cancellationToken).ConfigureAwait(true);

        await RetryEmailAsync(store, environment, takeover, prepared, cancellationToken)
            .ConfigureAwait(true);

        await SendEmailAsync(store, environment, prepared, cancellationToken)
            .ConfigureAwait(true);

        await DeadLetterEmailAsync(store, environment, prepared, cancellationToken)
            .ConfigureAwait(true);

        await AssertDurableEmailObservabilityAsync(store, environment, cancellationToken)
            .ConfigureAwait(true);

        Assert.Empty(await ClaimAndCommitAsync(
            store,
            environment.WorkerUnitOfWorkFactory,
            EntityId.New(),
            cancellationToken).ConfigureAwait(true));
    }

    private static async ValueTask<EmailOutboxMessage> ClaimInitialEmailAsync(
        IEmailOutboxDeliveryStore store,
        WorkerPostgresEnvironment environment,
        PreparedEmails prepared,
        CancellationToken cancellationToken)
    {
        EntityId owner = EntityId.New();
        EmailOutboxMessage message = Assert.Single(await ClaimAndCommitAsync(
            store,
            environment.WorkerUnitOfWorkFactory,
            owner,
            cancellationToken).ConfigureAwait(true));
        Assert.Equal(prepared.FirstEmailId, message.Lease.EmailId.Value);
        Assert.Equal(owner, message.Lease.Owner);
        Assert.Equal(1, message.Lease.Generation);
        Assert.Equal(1, message.Lease.Attempt);
        Assert.Equal(prepared.FirstMessageId, message.MessageId);
        Assert.Equal(prepared.FirstRecipientEnvelope, message.RecipientEnvelope.GetRawText());
        Assert.Equal(prepared.FirstDeliveryEnvelope, message.DeliverySecretEnvelope.GetRawText());
        Assert.Equal("password-reset-v1", message.TemplateCode);
        Assert.Equal("Worker test", message.TemplatePayload.GetProperty("display_name").GetString());
        return message;
    }

    private static async ValueTask<EmailOutboxMessage> TakeOverEmailAsync(
        IEmailOutboxDeliveryStore store,
        WorkerPostgresEnvironment environment,
        EmailOutboxMessage firstMessage,
        Guid emailId,
        CancellationToken cancellationToken)
    {
        Assert.True(await ExecuteAndCommitAsync(
            environment.WorkerUnitOfWorkFactory,
            (context, token) => store.HeartbeatAsync(
                firstMessage.Lease,
                TimeSpan.FromSeconds(30),
                context,
                token),
            cancellationToken).ConfigureAwait(true));
        EntityId nextOwner = EntityId.New();
        Assert.Empty(await ClaimAndCommitAsync(
            store,
            environment.WorkerUnitOfWorkFactory,
            nextOwner,
            cancellationToken).ConfigureAwait(true));
        await SetProcessingLeaseExpiredAsync(
            environment.AdministratorDataSource,
            emailId,
            cancellationToken).ConfigureAwait(true);
        EmailOutboxMessage takeover = Assert.Single(await ClaimAndCommitAsync(
            store,
            environment.WorkerUnitOfWorkFactory,
            nextOwner,
            cancellationToken).ConfigureAwait(true));
        Assert.Equal(2, takeover.Lease.Generation);
        Assert.Equal(2, takeover.Lease.Attempt);
        Assert.Equal(firstMessage.MessageId, takeover.MessageId);
        return takeover;
    }

    private static async ValueTask AssertStaleEmailLeaseRejectedAsync(
        IEmailOutboxDeliveryStore store,
        WorkerPostgresEnvironment environment,
        EmailOutboxDeliveryLease staleLease,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await environment.WorkerUnitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        Assert.False(await store.HeartbeatAsync(
            staleLease,
            TimeSpan.FromSeconds(30),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false));
        Assert.False(await store.MarkSentAsync(
            staleLease,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false));
        Assert.False(await store.ReleaseForRetryAsync(
            staleLease,
            TimeSpan.FromSeconds(5),
            "smtp_4xx",
            "stale retry",
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false));
        Assert.False(await store.MarkDeadAsync(
            staleLease,
            "smtp_5xx",
            "smtp_5xx",
            "stale dead",
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false));
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask RetryEmailAsync(
        IEmailOutboxDeliveryStore store,
        WorkerPostgresEnvironment environment,
        EmailOutboxMessage takeover,
        PreparedEmails prepared,
        CancellationToken cancellationToken)
    {
        Assert.True(await ExecuteAndCommitAsync(
            environment.WorkerUnitOfWorkFactory,
            (context, token) => store.ReleaseForRetryAsync(
                takeover.Lease,
                TimeSpan.FromMinutes(1),
                "smtp_4xx",
                "temporary smtp failure",
                context,
                token),
            cancellationToken).ConfigureAwait(true));
        EmailState retried = await ReadEmailStateAsync(
            environment.AdministratorDataSource,
            prepared.FirstEmailId,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal("pending", retried.Status);
        Assert.Equal(prepared.FirstMessageId, retried.MessageId);
        Assert.Equal(prepared.FirstRecipientEnvelope, retried.RecipientEnvelope);
        Assert.Equal(prepared.FirstDeliveryEnvelope, retried.DeliveryEnvelope);
        Assert.Equal(2, retried.Attempts);
        Assert.Equal(2, retried.Generation);
        Assert.Null(retried.LockOwner);
    }

    private static async ValueTask SendEmailAsync(
        IEmailOutboxDeliveryStore store,
        WorkerPostgresEnvironment environment,
        PreparedEmails prepared,
        CancellationToken cancellationToken)
    {
        await SetPendingDueAsync(
            environment.AdministratorDataSource,
            prepared.FirstEmailId,
            cancellationToken).ConfigureAwait(true);
        EmailOutboxMessage claim = Assert.Single(await ClaimAndCommitAsync(
            store,
            environment.WorkerUnitOfWorkFactory,
            EntityId.New(),
            cancellationToken).ConfigureAwait(true));
        Assert.Equal(3, claim.Lease.Generation);
        Assert.Equal(3, claim.Lease.Attempt);
        Assert.True(await ExecuteAndCommitAsync(
            environment.WorkerUnitOfWorkFactory,
            (context, token) => store.MarkSentAsync(claim.Lease, context, token),
            cancellationToken).ConfigureAwait(true));
        EmailState sent = await ReadEmailStateAsync(
            environment.AdministratorDataSource,
            prepared.FirstEmailId,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal("sent", sent.Status);
        Assert.Equal(prepared.FirstMessageId, sent.MessageId);
        Assert.Null(sent.RecipientEnvelope);
        Assert.Null(sent.DeliveryEnvelope);
        Assert.NotNull(sent.SentAt);
        Assert.Null(sent.DeadAt);
    }

    private static async ValueTask DeadLetterEmailAsync(
        IEmailOutboxDeliveryStore store,
        WorkerPostgresEnvironment environment,
        PreparedEmails prepared,
        CancellationToken cancellationToken)
    {
        await SetPendingDueAsync(
            environment.AdministratorDataSource,
            prepared.SecondEmailId,
            cancellationToken).ConfigureAwait(true);
        EmailOutboxMessage initial = Assert.Single(await ClaimAndCommitAsync(
            store,
            environment.WorkerUnitOfWorkFactory,
            EntityId.New(),
            cancellationToken).ConfigureAwait(true));
        await SetProcessingLeaseExpiredAsync(
            environment.AdministratorDataSource,
            prepared.SecondEmailId,
            cancellationToken).ConfigureAwait(true);
        EmailOutboxMessage takeover = Assert.Single(await ClaimAndCommitAsync(
            store,
            environment.WorkerUnitOfWorkFactory,
            EntityId.New(),
            cancellationToken).ConfigureAwait(true));
        Assert.NotEqual(initial.Lease.Owner, takeover.Lease.Owner);
        Assert.Equal(2, takeover.Lease.Generation);
        Assert.Equal(2, takeover.Lease.Attempt);
        Assert.True(await ExecuteAndCommitAsync(
            environment.WorkerUnitOfWorkFactory,
            (context, token) => store.MarkDeadAsync(
                takeover.Lease,
                "smtp_5xx",
                "smtp_5xx",
                "permanent smtp failure",
                context,
                token),
            cancellationToken).ConfigureAwait(true));
        EmailState dead = await ReadEmailStateAsync(
            environment.AdministratorDataSource,
            prepared.SecondEmailId,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal("dead", dead.Status);
        Assert.Equal(prepared.SecondMessageId, dead.MessageId);
        Assert.Null(dead.RecipientEnvelope);
        Assert.Null(dead.DeliveryEnvelope);
        Assert.Null(dead.SentAt);
        Assert.NotNull(dead.DeadAt);
        Assert.Equal("permanent smtp failure", dead.LastError);
    }

    private static async ValueTask AssertWorkerSessionLocksAsync(
        WorkerPostgresEnvironment environment,
        CancellationToken cancellationToken)
    {
        AssertStableWorkerLockIds();
        AssertTechnicalLockLeaseHasNoCommitCapability();

        using NpgsqlDataSource secondWorkerDataSource = NpgsqlDataSource.Create(
            environment.WorkerConnectionString);
        PostgresSessionAdvisoryLockProvider firstAdvisoryLocks = new(
            environment.WorkerDataSource);
        PostgresSessionAdvisoryLockProvider secondAdvisoryLocks = new(
            secondWorkerDataSource);
        IWorkerSessionLockProvider firstProvider = CreateInternal<IWorkerSessionLockProvider>(
            typeof(PoolAI.Modules.Operations.DependencyInjection).Assembly,
            "PoolAI.Modules.Operations.Infrastructure.Workers.PostgresWorkerSessionLockProvider",
            firstAdvisoryLocks);
        IWorkerSessionLockProvider secondProvider = CreateInternal<IWorkerSessionLockProvider>(
            typeof(PoolAI.Modules.Operations.DependencyInjection).Assembly,
            "PoolAI.Modules.Operations.Infrastructure.Workers.PostgresWorkerSessionLockProvider",
            secondAdvisoryLocks);
        Assert.DoesNotContain(
            firstProvider.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            field => field.FieldType.FullName?.StartsWith(
                "StackExchange.Redis",
                StringComparison.Ordinal) is true);

        await AssertSessionLockLifecycleAsync(
            firstProvider,
            secondProvider,
            environment.AdministratorDataSource,
            cancellationToken).ConfigureAwait(true);
    }

    private static async ValueTask AssertDurableEmailObservabilityAsync(
        IEmailOutboxDeliveryStore store,
        WorkerPostgresEnvironment environment,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await environment.WorkerUnitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        EmailOutboxObservabilitySnapshot snapshot = await store.ReadObservabilityAsync(
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);

        Assert.Equal(0, snapshot.PendingCount);
        Assert.Equal(0, snapshot.OldestAgeSeconds);
        Assert.Equal(1, snapshot.DeadCount);
        Assert.Contains(snapshot.Failures, static failure =>
            string.Equals(failure.FailureClass, "smtp_4xx", StringComparison.Ordinal)
                && string.Equals(failure.Outcome, "retry", StringComparison.Ordinal)
                && string.Equals(
                    failure.TerminalReason,
                    "not_terminal",
                    StringComparison.Ordinal)
                && failure.Count == 1);
        Assert.Contains(snapshot.Failures, static failure =>
            string.Equals(failure.FailureClass, "smtp_5xx", StringComparison.Ordinal)
                && string.Equals(failure.Outcome, "dead", StringComparison.Ordinal)
                && string.Equals(
                    failure.TerminalReason,
                    "smtp_5xx",
                    StringComparison.Ordinal)
                && failure.Count == 1);
    }

    private static async ValueTask AssertSessionLockLifecycleAsync(
        IWorkerSessionLockProvider firstProvider,
        IWorkerSessionLockProvider secondProvider,
        NpgsqlDataSource administratorDataSource,
        CancellationToken cancellationToken)
    {
        IWorkerSessionLock? first = null;
        IWorkerSessionLock? second = null;
        IWorkerSessionLock? afterTermination = null;
        try
        {
            first = await AcquireInitialWorkerLockAsync(
                firstProvider,
                secondProvider,
                cancellationToken).ConfigureAwait(true);
            Assert.Equal(
                WorkerSessionLockId.Derive(WorkerJobs.EmailOutboxSender),
                ReadTechnicalLockLease(first).LockId);
            Assert.True(await first.VerifyOwnershipAsync(cancellationToken).ConfigureAwait(true));
            Assert.Null(await secondProvider
                .TryAcquireAsync(WorkerJobs.EmailOutboxSender, cancellationToken)
                .ConfigureAwait(true));

            await first.DisposeAsync().ConfigureAwait(true);
            first = null;
            second = await secondProvider
                .TryAcquireAsync(WorkerJobs.EmailOutboxSender, cancellationToken)
                .ConfigureAwait(true);
            Assert.NotNull(second);
            await TerminateWorkerLockSessionAsync(
                administratorDataSource,
                second,
                cancellationToken).ConfigureAwait(true);
            afterTermination = await firstProvider
                .TryAcquireAsync(WorkerJobs.EmailOutboxSender, cancellationToken)
                .ConfigureAwait(true);
            Assert.NotNull(afterTermination);
            Assert.True(await afterTermination
                .VerifyOwnershipAsync(cancellationToken)
                .ConfigureAwait(true));
        }
        finally
        {
            if (afterTermination is not null)
            {
                await afterTermination.DisposeAsync().ConfigureAwait(true);
            }

            if (second is not null)
            {
                await second.DisposeAsync().ConfigureAwait(true);
            }

            if (first is not null)
            {
                await first.DisposeAsync().ConfigureAwait(true);
            }
        }
    }

    private static async ValueTask<IWorkerSessionLock> AcquireInitialWorkerLockAsync(
        IWorkerSessionLockProvider firstProvider,
        IWorkerSessionLockProvider secondProvider,
        CancellationToken cancellationToken)
    {
        IWorkerSessionLock? first = await firstProvider
            .TryAcquireAsync(WorkerJobs.EmailOutboxSender, cancellationToken)
            .ConfigureAwait(true);
        Assert.NotNull(first);
        Assert.Equal(WorkerSessionLockId.Derive(WorkerJobs.EmailOutboxSender), first.LockId);
        Assert.Null(await secondProvider
            .TryAcquireAsync(WorkerJobs.EmailOutboxSender, cancellationToken)
            .ConfigureAwait(true));
        return first;
    }

    private static async ValueTask TerminateWorkerLockSessionAsync(
        NpgsqlDataSource administratorDataSource,
        IWorkerSessionLock sessionLock,
        CancellationToken cancellationToken)
    {
        int backendProcessId = ReadTechnicalLockLease(sessionLock).BackendProcessId;
        using NpgsqlCommand terminate = administratorDataSource.CreateCommand(
            "SELECT pg_catalog.pg_terminate_backend($1, 5000);");
        terminate.Parameters.AddWithValue(backendProcessId);
        Assert.True(Assert.IsType<bool>(await terminate
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(true)));
        Assert.False(await sessionLock.VerifyOwnershipAsync(cancellationToken).ConfigureAwait(true));
    }

    private static void AssertStableWorkerLockIds()
    {
        (WorkerJobIdentity Job, long LockId)[] expectedLocks =
        [
            (WorkerJobs.ReservationSweeper, 7_363_728_125_447_077_054),
            (WorkerJobs.OutboxPublisher, -7_621_624_086_400_655_958),
            (WorkerJobs.UsageAggregator, 451_344_536_216_699_593),
            (WorkerJobs.UsageRebuild, 2_109_179_889_849_020_703),
            (WorkerJobs.EmailOutboxSender, 5_101_722_440_924_637_420),
            (WorkerJobs.SupplyHealth, -2_750_803_758_266_494_581),
            (WorkerJobs.OperationsAlerts, -7_697_813_494_015_939_377),
        ];
        Assert.Equal(
            expectedLocks.Length,
            expectedLocks.Select(item => item.LockId).Distinct().Count());
        foreach ((WorkerJobIdentity job, long expectedLockId) in expectedLocks)
        {
            Assert.EndsWith(":v1", job.Name, StringComparison.Ordinal);
            Assert.Equal(expectedLockId, WorkerSessionLockId.Derive(job));
        }
    }

    private static async ValueTask<PreparedEmails> PrepareEmailsAsApiAsync(
        WorkerPostgresEnvironment environment,
        CancellationToken cancellationToken)
    {
        Guid firstEmailId = Guid.CreateVersion7();
        Guid secondEmailId = Guid.CreateVersion7();
        EmailFixtureSeed seed = new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            firstEmailId,
            secondEmailId,
            $"<{firstEmailId:N}@poolai.test>",
            $"<{secondEmailId:N}@poolai.test>",
            BuildEnvelope("cmVjaXBpZW50QHBvb2xhaS50ZXN0"),
            BuildEnvelope("cmVzZXQtY3JlZGVudGlhbA"));
        await InsertEmailFixtureAsApiAsync(environment, seed, cancellationToken)
            .ConfigureAwait(true);

        await DeferPendingEmailAsync(
            environment.AdministratorDataSource,
            seed.SecondEmailId,
            cancellationToken).ConfigureAwait(true);
        EmailState firstState = await ReadEmailStateAsync(
            environment.AdministratorDataSource,
            seed.FirstEmailId,
            cancellationToken).ConfigureAwait(true);
        return new PreparedEmails(
            seed.FirstEmailId,
            seed.SecondEmailId,
            seed.FirstMessageId,
            seed.SecondMessageId,
            Assert.IsType<string>(firstState.RecipientEnvelope),
            Assert.IsType<string>(firstState.DeliveryEnvelope));
    }

    private static async ValueTask InsertEmailFixtureAsApiAsync(
        WorkerPostgresEnvironment environment,
        EmailFixtureSeed seed,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await environment.ApiUnitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWork.Context);
        await AssertDatabaseUserAsync(session, "poolai_api", cancellationToken).ConfigureAwait(false);
        await InsertUserAsync(session, seed.UserId, cancellationToken).ConfigureAwait(false);
        await InsertEmailAsync(
            session,
            seed.UserId,
            seed.FirstTokenId,
            seed.FirstEmailId,
            seed.FirstMessageId,
            seed.RecipientEnvelope,
            seed.DeliveryEnvelope,
            cancellationToken).ConfigureAwait(false);
        await InsertEmailAsync(
            session,
            seed.UserId,
            seed.SecondTokenId,
            seed.SecondEmailId,
            seed.SecondMessageId,
            seed.RecipientEnvelope,
            seed.DeliveryEnvelope,
            cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AssertDatabaseUserAsync(
        PostgresTransactionSession session,
        string expectedUser,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand currentUser = session.CreateCommand("SELECT current_user;");
        Assert.Equal(
            expectedUser,
            Assert.IsType<string>(await currentUser
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(true)));
    }

    private static async ValueTask InsertUserAsync(
        PostgresTransactionSession session,
        Guid userId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand("""
            INSERT INTO public.users (
                id, email, normalized_email, display_name, password_hash, security_stamp
            ) VALUES ($1, 'worker-test@poolai.test', 'worker-test@poolai.test',
                'Worker test', 'test-password-hash', $2);
            """);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(Guid.CreateVersion7());
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true));
    }

    private static async ValueTask InsertEmailAsync(
        PostgresTransactionSession session,
        Guid userId,
        Guid tokenId,
        Guid emailId,
        string messageId,
        string recipientEnvelope,
        string deliveryEnvelope,
        CancellationToken cancellationToken)
    {
        using (NpgsqlCommand token = session.CreateCommand("""
                   INSERT INTO public.one_time_tokens (
                       id, user_id, purpose, token_hash, pepper_version, expires_at
                   ) VALUES ($1, $2, 'password_reset', $3, 1,
                       clock_timestamp() + interval '30 minutes');
                   """))
        {
            token.Parameters.AddWithValue(tokenId);
            token.Parameters.AddWithValue(userId);
            token.Parameters.AddWithValue(RandomNumberGenerator.GetBytes(32));
            Assert.Equal(
                1,
                await token.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true));
        }

        using NpgsqlCommand email = session.CreateCommand("""
            INSERT INTO public.email_outbox (
                id, idempotency_key, message_id, user_id, one_time_token_id,
                recipient_envelope, template_code, template_payload,
                delivery_secret_envelope
            ) VALUES ($1, $2, $3, $4, $5, $6, 'password-reset-v1',
                $7, $8);
            """);
        email.Parameters.AddWithValue(emailId);
        email.Parameters.AddWithValue($"password-reset:{tokenId:N}");
        email.Parameters.AddWithValue(messageId);
        email.Parameters.AddWithValue(userId);
        email.Parameters.AddWithValue(tokenId);
        email.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = recipientEnvelope });
        email.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = "{\"display_name\":\"Worker test\"}",
        });
        email.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = deliveryEnvelope });
        Assert.Equal(
            1,
            await email.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true));
    }

    private static async ValueTask<IReadOnlyList<EmailOutboxMessage>> ClaimAndCommitAsync(
        IEmailOutboxDeliveryStore store,
        PostgresUnitOfWorkFactory factory,
        EntityId owner,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await factory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        IReadOnlyList<EmailOutboxMessage> messages = await store.ClaimDueAsync(
            owner,
            maximumCount: 10,
            TimeSpan.FromSeconds(30),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return messages;
    }

    private static async ValueTask<bool> ExecuteAndCommitAsync(
        PostgresUnitOfWorkFactory factory,
        Func<IUnitOfWorkContext, CancellationToken, ValueTask<bool>> operation,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await factory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        bool result = await operation(unitOfWork.Context, cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async ValueTask<EmailState> ReadEmailStateAsync(
        NpgsqlDataSource dataSource,
        Guid emailId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = dataSource.CreateCommand("""
            SELECT status, message_id, recipient_envelope::text,
                   delivery_secret_envelope::text, attempts, lock_generation,
                   lock_owner, sent_at, dead_at, last_error
            FROM public.email_outbox
            WHERE id = $1;
            """);
        command.Parameters.AddWithValue(emailId);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(true);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        EmailState state = new(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt64(5),
            reader.IsDBNull(6) ? null : reader.GetGuid(6),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
            reader.IsDBNull(9) ? null : reader.GetString(9));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        return state;
    }

    private static ValueTask DeferPendingEmailAsync(
        NpgsqlDataSource dataSource,
        Guid emailId,
        CancellationToken cancellationToken) =>
        UpdateEmailClockAsync(
            dataSource,
            "UPDATE public.email_outbox SET next_attempt_at = clock_timestamp() + interval '1 hour' WHERE id = $1 AND status = 'pending';",
            emailId,
            cancellationToken);

    private static ValueTask SetPendingDueAsync(
        NpgsqlDataSource dataSource,
        Guid emailId,
        CancellationToken cancellationToken) =>
        UpdateEmailClockAsync(
            dataSource,
            "UPDATE public.email_outbox SET next_attempt_at = clock_timestamp() - interval '1 second' WHERE id = $1 AND status = 'pending';",
            emailId,
            cancellationToken);

    private static ValueTask SetProcessingLeaseExpiredAsync(
        NpgsqlDataSource dataSource,
        Guid emailId,
        CancellationToken cancellationToken) =>
        UpdateEmailClockAsync(
            dataSource,
            "UPDATE public.email_outbox SET locked_until = clock_timestamp() - interval '1 second' WHERE id = $1 AND status = 'processing';",
            emailId,
            cancellationToken);

    private static async ValueTask UpdateEmailClockAsync(
        NpgsqlDataSource dataSource,
        string sql,
        Guid emailId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(emailId);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true));
    }

    private static string BuildEnvelope(string ciphertext) => $$"""
        {
          "v": 1,
          "alg": "A256GCM+A256GCM-v1",
          "kid": "test-kek-v1",
          "wrapped_dek": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
          "wrap_nonce": "AAAAAAAAAAAAAAAA",
          "wrap_tag": "AAAAAAAAAAAAAAAAAAAAAA",
          "ciphertext": "{{ciphertext}}",
          "nonce": "AQEBAQEBAQEBAQEB",
          "tag": "AgICAgICAgICAgICAgICAg"
        }
        """;

    private static void AssertTechnicalLockLeaseHasNoCommitCapability()
    {
        const BindingFlags Surface = BindingFlags.Instance
            | BindingFlags.Public
            | BindingFlags.NonPublic;
        Assert.DoesNotContain(
            typeof(PostgresSessionAdvisoryLockLease).GetProperties(Surface),
            property => property.PropertyType == typeof(NpgsqlConnection)
                || property.PropertyType == typeof(NpgsqlTransaction));
        Assert.DoesNotContain(
            typeof(PostgresSessionAdvisoryLockLease).GetMethods(Surface),
            method => method.Name.Contains("Commit", StringComparison.Ordinal)
                || method.Name.Contains("Rollback", StringComparison.Ordinal));
    }

    private static PostgresSessionAdvisoryLockLease ReadTechnicalLockLease(
        IWorkerSessionLock sessionLock)
    {
        FieldInfo? field = sessionLock.GetType().GetField(
            "_lease",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<PostgresSessionAdvisoryLockLease>(field.GetValue(sessionLock));
    }

    private static T CreateInternal<T>(
        Assembly assembly,
        string typeName,
        params object?[] arguments)
        where T : class
    {
        Type? type = assembly.GetType(typeName, throwOnError: false, ignoreCase: false);
        Assert.NotNull(type);
        object? instance = Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: arguments,
            culture: null);
        return Assert.IsAssignableFrom<T>(instance);
    }

    private sealed record EmailFixtureSeed(
        Guid UserId,
        Guid FirstTokenId,
        Guid SecondTokenId,
        Guid FirstEmailId,
        Guid SecondEmailId,
        string FirstMessageId,
        string SecondMessageId,
        string RecipientEnvelope,
        string DeliveryEnvelope);

    private sealed record PreparedEmails(
        Guid FirstEmailId,
        Guid SecondEmailId,
        string FirstMessageId,
        string SecondMessageId,
        string FirstRecipientEnvelope,
        string FirstDeliveryEnvelope);

    private sealed record EmailState(
        string Status,
        string MessageId,
        string? RecipientEnvelope,
        string? DeliveryEnvelope,
        int Attempts,
        long Generation,
        Guid? LockOwner,
        DateTimeOffset? SentAt,
        DateTimeOffset? DeadAt,
        string? LastError);

    private sealed class WorkerPostgresEnvironment : IAsyncDisposable
    {
        private WorkerPostgresEnvironment(
            PostgreSqlContainer container,
            NpgsqlDataSource administratorDataSource,
            NpgsqlDataSource apiDataSource,
            NpgsqlDataSource workerDataSource,
            string workerConnectionString)
        {
            Container = container;
            AdministratorDataSource = administratorDataSource;
            ApiDataSource = apiDataSource;
            WorkerDataSource = workerDataSource;
            WorkerConnectionString = workerConnectionString;
            ApiUnitOfWorkFactory = new PostgresUnitOfWorkFactory(ApiDataSource);
            WorkerUnitOfWorkFactory = new PostgresUnitOfWorkFactory(WorkerDataSource);
        }

        private PostgreSqlContainer Container { get; }

        public NpgsqlDataSource AdministratorDataSource { get; }

        public NpgsqlDataSource ApiDataSource { get; }

        public NpgsqlDataSource WorkerDataSource { get; }

        public string WorkerConnectionString { get; }

        public PostgresUnitOfWorkFactory ApiUnitOfWorkFactory { get; }

        public PostgresUnitOfWorkFactory WorkerUnitOfWorkFactory { get; }

        public static async ValueTask<WorkerPostgresEnvironment> StartAsync(
            CancellationToken cancellationToken)
        {
            string administratorPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            string apiPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            string workerPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            PostgreSqlContainer container = new PostgreSqlBuilder(ReadPostgresImage())
                .WithDatabase("poolai")
                .WithUsername("postgres")
                .WithPassword(administratorPassword)
                .Build();
            try
            {
                await container.StartAsync(cancellationToken).ConfigureAwait(true);
                string administratorConnectionString = container.GetConnectionString();
                await ProvisionRuntimeRolesAsync(
                    administratorConnectionString,
                    apiPassword,
                    workerPassword,
                    cancellationToken).ConfigureAwait(true);
                MigrationCatalog catalog = await MigrationCatalog
                    .LoadAsync(cancellationToken)
                    .ConfigureAwait(true);
                await new PostgresMigrator(catalog).ApplyAsync(
                    administratorConnectionString,
                    "PoolAI.IntegrationTests.worker-fencing",
                    cancellationToken).ConfigureAwait(true);

                string apiConnectionString = RuntimeConnectionString(
                    administratorConnectionString,
                    "poolai_api",
                    apiPassword,
                    "PoolAI.IntegrationTests.api-fixture");
                string workerConnectionString = RuntimeConnectionString(
                    administratorConnectionString,
                    "poolai_worker",
                    workerPassword,
                    "PoolAI.IntegrationTests.worker-fencing");
                return new WorkerPostgresEnvironment(
                    container,
                    NpgsqlDataSource.Create(administratorConnectionString),
                    NpgsqlDataSource.Create(apiConnectionString),
                    NpgsqlDataSource.Create(workerConnectionString),
                    workerConnectionString);
            }
            catch
            {
                await container.DisposeAsync().ConfigureAwait(true);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await WorkerDataSource.DisposeAsync().ConfigureAwait(true);
            await ApiDataSource.DisposeAsync().ConfigureAwait(true);
            await AdministratorDataSource.DisposeAsync().ConfigureAwait(true);
            await Container.DisposeAsync().ConfigureAwait(true);
        }

        private static async ValueTask ProvisionRuntimeRolesAsync(
            string administratorConnectionString,
            string apiPassword,
            string workerPassword,
            CancellationToken cancellationToken)
        {
            using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(administratorConnectionString);
            using NpgsqlConnection connection = await dataSource
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(true);
            using (NpgsqlCommand owner = new("""
                       CREATE ROLE poolai_runtime_owner NOLOGIN
                           NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT
                           NOREPLICATION NOBYPASSRLS;
                       """, connection))
            {
                await owner.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true);
            }

            await CreateLoginRoleAsync(connection, "poolai_api", apiPassword, cancellationToken)
                .ConfigureAwait(true);
            await CreateLoginRoleAsync(connection, "poolai_worker", workerPassword, cancellationToken)
                .ConfigureAwait(true);
            using NpgsqlCommand connect = new("""
                GRANT CONNECT ON DATABASE poolai TO poolai_api, poolai_worker;
                """, connection);
            await connect.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true);
        }

        private static async ValueTask CreateLoginRoleAsync(
            NpgsqlConnection connection,
            string roleName,
            string password,
            CancellationToken cancellationToken)
        {
            using (NpgsqlCommand settings = new("""
                       SELECT pg_catalog.set_config('poolai.test_role_name', $1, false),
                              pg_catalog.set_config('poolai.test_role_password', $2, false);
                       """, connection))
            {
                settings.Parameters.AddWithValue(roleName);
                settings.Parameters.AddWithValue(password);
                await settings.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true);
            }

            try
            {
                using NpgsqlCommand create = new("""
                    DO $provision$
                    BEGIN
                        EXECUTE pg_catalog.format(
                            'CREATE ROLE %I LOGIN PASSWORD %L '
                            'NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT '
                            'NOREPLICATION NOBYPASSRLS',
                            pg_catalog.current_setting('poolai.test_role_name'),
                            pg_catalog.current_setting('poolai.test_role_password'));
                    END;
                    $provision$;
                    """, connection);
                await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true);
            }
            finally
            {
                using NpgsqlCommand clear = new("""
                    SELECT pg_catalog.set_config('poolai.test_role_name', '', false),
                           pg_catalog.set_config('poolai.test_role_password', '', false);
                    """, connection);
                await clear.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(true);
            }
        }

        private static string RuntimeConnectionString(
            string administratorConnectionString,
            string username,
            string password,
            string applicationName)
        {
            NpgsqlConnectionStringBuilder builder = new(administratorConnectionString)
            {
                Username = username,
                Password = password,
                ApplicationName = applicationName,
            };
            return builder.ConnectionString;
        }

        private static string ReadPostgresImage()
        {
            string root = MigrationCatalogTests.FindRepositoryRoot();
            using JsonDocument versions = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(root, "eng", "versions.json")));
            string image = versions.RootElement
                .GetProperty("containers")
                .GetProperty("postgresql")
                .GetString()
                ?? throw new InvalidOperationException("The PostgreSQL image lock is missing.");
            string digest = versions.RootElement
                .GetProperty("containerDigests")
                .GetProperty("postgresql")
                .GetString()
                ?? throw new InvalidOperationException("The PostgreSQL digest lock is missing.");
            return $"{image}@{digest}";
        }
    }
}
