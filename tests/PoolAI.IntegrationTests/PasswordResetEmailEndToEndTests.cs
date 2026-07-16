using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Identity;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application;
using PoolAI.Modules.Identity.Application.Ports;
using PoolAI.Modules.Identity.Infrastructure.Email;
using PoolAI.Modules.Identity.Infrastructure.Persistence;
using PoolAI.Modules.Identity.Infrastructure.Security;
using PoolAI.Modules.Identity.Worker;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class PasswordResetEmailEndToEndTests(
    PostgresRuntimeFixture fixture)
{
    private static readonly Guid UserRoleId = Guid.Parse(
        "01900000-0000-7000-8000-000000000004");

    [Fact]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Redis")]
    public async Task PasswordResetWorkerRetriesThroughStartTlsAndConsumesTheTokenOnce()
    {
        // Governing contracts: DEC-033 and AC-035 require real PostgreSQL/Redis,
        // a non-enumerating 202 response, fenced SMTP retry with stable Message-ID,
        // single-use reset credentials, and secret-safe persistence/diagnostics.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        PasswordResetScenario scenario = await CreateScenarioAsync(cancellationToken)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable serviceLease = scenario.Services
            .ConfigureAwait(true);
        await using ObservableStartTlsSmtpServer smtp = new(
            scenario.SmtpUsername,
            scenario.SmtpPassword);

        ResetEmail email = await RequestAndAssertNonEnumerationAsync(
            scenario,
            cancellationToken).ConfigureAwait(true);
        string persistenceBeforeDelivery = await ReadPasswordResetPersistenceTextAsync(
            scenario.ActiveUserId,
            cancellationToken).ConfigureAwait(true);

        await DeliverWithTwoTransientFailuresAsync(
            scenario,
            smtp,
            email,
            cancellationToken).ConfigureAwait(true);
        await ConsumeOnceAsync(scenario, email, cancellationToken).ConfigureAwait(true);

        string persistenceAfterCompletion = await ReadPasswordResetPersistenceTextAsync(
            scenario.ActiveUserId,
            cancellationToken).ConfigureAwait(true);
        AssertSecretsAbsent(
            scenario,
            email,
            string.Concat(persistenceBeforeDelivery, persistenceAfterCompletion));
    }

    private async ValueTask<PasswordResetScenario> CreateScenarioAsync(
        CancellationToken cancellationToken)
    {
        string suffix = Guid.NewGuid().ToString("N")[..12];
        string activeEmail = $"active-{suffix}@poolai.test";
        string disabledEmail = $"disabled-{suffix}@poolai.test";
        string missingEmail = $"missing-{suffix}@poolai.test";
        EntityId activeUserId = EntityId.New();
        EntityId disabledUserId = EntityId.New();
        const string OriginalPassword = "Original-Password-123!";
        const string NewPassword = "Replacement-Password-456!";
        string smtpUsername = $"smtp-{suffix}";
        string smtpPassword = Convert.ToBase64String(
            RandomNumberGenerator.GetBytes(36));
        VersionedPasswordHasher passwordHasher = new();

        await InsertUserAsync(
            activeUserId,
            activeEmail,
            UserLifecycle.Active,
            passwordHasher.Hash(OriginalPassword),
            cancellationToken).ConfigureAwait(true);
        await InsertUserAsync(
            disabledUserId,
            disabledEmail,
            UserLifecycle.Disabled,
            passwordHasher.Hash(OriginalPassword),
            cancellationToken).ConfigureAwait(true);

        IConfiguration configuration = BuildConfiguration();
        RecordingLoggerProvider logs = new();
        ServiceProvider services = BuildIdentityServices(configuration, logs);
        return new PasswordResetScenario(
            services,
            logs,
            activeUserId,
            disabledUserId,
            activeEmail,
            disabledEmail,
            missingEmail,
            smtpUsername,
            smtpPassword,
            NewPassword);
    }

    private async ValueTask<ResetEmail> RequestAndAssertNonEnumerationAsync(
        PasswordResetScenario scenario,
        CancellationToken cancellationToken)
    {
        IRequestPasswordResetUseCase forgot = scenario.Services
            .GetRequiredService<IRequestPasswordResetUseCase>();
        Result<IdentityCommandOutcome> active = await forgot.ExecuteAsync(
            Forgot(scenario.ActiveEmail),
            cancellationToken).ConfigureAwait(true);
        Result<IdentityCommandOutcome> missing = await forgot.ExecuteAsync(
            Forgot(scenario.MissingEmail),
            cancellationToken).ConfigureAwait(true);
        Result<IdentityCommandOutcome> disabled = await forgot.ExecuteAsync(
            Forgot(scenario.DisabledEmail),
            cancellationToken).ConfigureAwait(true);

        AssertAccepted(active);
        AssertAccepted(missing);
        AssertAccepted(disabled);
        Assert.Equal(active.Value, missing.Value);
        Assert.Equal(active.Value, disabled.Value);
        Assert.Equal(1L, await CountAsync(
            "SELECT count(*) FROM public.one_time_tokens WHERE user_id = $1;",
            scenario.ActiveUserId.Value,
            cancellationToken).ConfigureAwait(true));
        Assert.Equal(1L, await CountAsync(
            "SELECT count(*) FROM public.email_outbox WHERE user_id = $1;",
            scenario.ActiveUserId.Value,
            cancellationToken).ConfigureAwait(true));
        Assert.Equal(0L, await CountAsync(
            "SELECT count(*) FROM public.one_time_tokens WHERE user_id = $1;",
            scenario.DisabledUserId.Value,
            cancellationToken).ConfigureAwait(true));
        Assert.Equal(0L, await CountAsync(
            "SELECT count(*) FROM public.email_outbox WHERE user_id = $1;",
            scenario.DisabledUserId.Value,
            cancellationToken).ConfigureAwait(true));

        ResetEmail email = await ReadResetEmailAsync(
            scenario.ActiveUserId,
            cancellationToken).ConfigureAwait(true);
        EmailSecretEnvelopePlaintext plaintext = scenario.Services
            .GetRequiredService<IEmailSecretEnvelope>()
            .Decrypt(
                email.RecipientEnvelope,
                email.DeliverySecretEnvelope,
                email.EmailId);
        Assert.Equal(scenario.ActiveEmail, plaintext.Recipient);
        Assert.Equal("password-reset-v1", email.TemplateCode);
        Assert.Equal(30, email.TemplatePayload.GetProperty(
            "expires_in_minutes").GetInt32());
        Assert.Equal($"<{email.EmailId.Value:D}@poolai.test>", email.MessageId);
        return email with
        {
            Recipient = plaintext.Recipient,
            ResetUrl = plaintext.ResetUrl,
            Token = ExtractToken(plaintext.ResetUrl),
        };
    }

    private async ValueTask DeliverWithTwoTransientFailuresAsync(
        PasswordResetScenario scenario,
        ObservableStartTlsSmtpServer smtp,
        ResetEmail email,
        CancellationToken cancellationToken)
    {
        EmailDeliveryRuntime runtime = CreateEmailDeliveryRuntime(scenario, smtp);
        await fixture.DeferOtherPendingEmailOutboxAsync(
            email.EmailId.Value,
            cancellationToken).ConfigureAwait(true);
        IWorkerSessionLock? jobLock = await fixture.WorkerServices
            .GetRequiredService<IWorkerSessionLockProvider>()
            .TryAcquireAsync(WorkerJobs.EmailOutboxSender, cancellationToken)
            .ConfigureAwait(true);
        Assert.NotNull(jobLock);
        await using ConfiguredAsyncDisposable jobLockLease = jobLock.ConfigureAwait(true);

        await ProcessAndAssertRetryAsync(
            runtime.Processor,
            jobLock,
            email.EmailId,
            expectedAttempt: 1,
            cancellationToken).ConfigureAwait(true);
        await ForceEmailDueAsync(email.EmailId, cancellationToken).ConfigureAwait(true);
        await ProcessAndAssertRetryAsync(
            runtime.Processor,
            jobLock,
            email.EmailId,
            expectedAttempt: 2,
            cancellationToken).ConfigureAwait(true);
        await ForceEmailDueAsync(email.EmailId, cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            EmailOutboxProcessResult.Processed,
            await runtime.Processor.ProcessNextAsync(jobLock, cancellationToken)
                .ConfigureAwait(true));

        await AssertSentAsync(
            runtime.OperationalEvents,
            email.EmailId,
            cancellationToken).ConfigureAwait(true);
        await AssertSmtpDeliveryAsync(scenario, smtp, email, cancellationToken)
            .ConfigureAwait(true);
    }

    private EmailDeliveryRuntime CreateEmailDeliveryRuntime(
        PasswordResetScenario scenario,
        ObservableStartTlsSmtpServer smtp)
    {
        EmailOutboxWorkerOptions options = CreateWorkerOptions(scenario, smtp.Port);
        SmtpEmailTransport transport = new(
            options,
            (_, certificate, _, _) => CertificateMatches(
                certificate,
                smtp.CertificateSha256));
        RecordingOperationalEventWriter operationalEvents = new();
        EmailOutboxProcessor processor = new(
            fixture.WorkerServices.GetRequiredService<IUnitOfWorkFactory>(),
            new PostgresEmailOutboxDeliveryStore(),
            scenario.Services.GetRequiredService<IEmailSecretEnvelope>(),
            transport,
            new ZeroJitter(),
            operationalEvents,
            options);
        return new EmailDeliveryRuntime(processor, operationalEvents);
    }

    private static EmailOutboxWorkerOptions CreateWorkerOptions(
        PasswordResetScenario scenario,
        int port) => new(
        "localhost",
        port,
        SmtpSecurityMode.StartTls,
        scenario.SmtpUsername,
        scenario.SmtpPassword,
        "no-reply@poolai.test",
        "PoolAI",
        maximumAttempts: 3,
        pollInterval: TimeSpan.FromMilliseconds(10),
        claimDuration: TimeSpan.FromSeconds(5),
        heartbeatInterval: TimeSpan.FromSeconds(1),
        smtpTimeout: TimeSpan.FromSeconds(10),
        retryBase: TimeSpan.FromMilliseconds(1),
        retryMaximum: TimeSpan.FromMilliseconds(1));

    private async ValueTask AssertSentAsync(
        RecordingOperationalEventWriter operationalEvents,
        EntityId emailId,
        CancellationToken cancellationToken)
    {
        EmailDeliveryState sent = await ReadDeliveryStateAsync(
            emailId,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal("sent", sent.Status);
        Assert.Equal(3, sent.Attempts);
        Assert.Equal(3L, sent.Generation);
        Assert.True(sent.RecipientEnvelopeCleared);
        Assert.True(sent.DeliverySecretEnvelopeCleared);
        Assert.NotNull(sent.SentAt);
        Assert.Null(sent.DeadAt);
        Assert.Null(sent.LastError);
        Assert.Empty(operationalEvents.Events);
    }

    private static async ValueTask AssertSmtpDeliveryAsync(
        PasswordResetScenario scenario,
        ObservableStartTlsSmtpServer smtp,
        ResetEmail email,
        CancellationToken cancellationToken)
    {
        await smtp.Completion.WaitAsync(cancellationToken).ConfigureAwait(true);
        Assert.Equal(3, smtp.StartTlsSessions);
        Assert.Equal(3, smtp.AuthenticatedSessions);
        Assert.Equal(3, smtp.Messages.Count);
        Assert.All(smtp.Messages, message =>
        {
            Assert.Equal(email.MessageId, message.MessageId);
            Assert.Equal(scenario.ActiveEmail, message.Recipient);
            Assert.Contains(email.ResetUrl, message.Body, StringComparison.Ordinal);
            Assert.DoesNotContain(
                scenario.SmtpPassword,
                message.RawMessage,
                StringComparison.Ordinal);
        });
        Assert.Single(smtp.Messages
            .Select(static message => message.MessageId)
            .Distinct(StringComparer.Ordinal));
    }

    private async ValueTask ConsumeOnceAsync(
        PasswordResetScenario scenario,
        ResetEmail email,
        CancellationToken cancellationToken)
    {
        ICompletePasswordResetUseCase complete = scenario.Services
            .GetRequiredService<ICompletePasswordResetUseCase>();
        Result<IdentityCommandOutcome> first = await complete.ExecuteAsync(
            Complete(email.Token, scenario.NewPassword),
            cancellationToken).ConfigureAwait(true);
        Result<IdentityCommandOutcome> replay = await complete.ExecuteAsync(
            Complete(email.Token, scenario.NewPassword),
            cancellationToken).ConfigureAwait(true);

        Assert.True(first.IsSuccess);
        Assert.Equal(204, first.Value.StatusCode);
        Assert.True(replay.IsFailure);
        Assert.Equal(IdentityErrorCodes.PasswordResetTokenInvalid, replay.Error.Code);
        Assert.True(await TokenWasUsedAsync(
            email.TokenId,
            cancellationToken).ConfigureAwait(true));
        string passwordHash = await ReadPasswordHashAsync(
            scenario.ActiveUserId,
            cancellationToken).ConfigureAwait(true);
        Assert.True(scenario.Services
            .GetRequiredService<IVersionedPasswordHasher>()
            .Verify(passwordHash, scenario.NewPassword));
    }

    private async ValueTask ProcessAndAssertRetryAsync(
        EmailOutboxProcessor processor,
        IWorkerSessionLock jobLock,
        EntityId emailId,
        int expectedAttempt,
        CancellationToken cancellationToken)
    {
        Assert.Equal(
            EmailOutboxProcessResult.Processed,
            await processor.ProcessNextAsync(jobLock, cancellationToken).ConfigureAwait(true));
        EmailDeliveryState state = await ReadDeliveryStateAsync(
            emailId,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal("pending", state.Status);
        Assert.Equal(expectedAttempt, state.Attempts);
        Assert.Equal(expectedAttempt, state.Generation);
        Assert.Equal("smtp_4xx", state.LastError);
        Assert.False(state.RecipientEnvelopeCleared);
        Assert.False(state.DeliverySecretEnvelopeCleared);
        Assert.Null(state.SentAt);
        Assert.Null(state.DeadAt);
    }

    private ServiceProvider BuildIdentityServices(
        IConfiguration configuration,
        ILoggerProvider loggerProvider)
    {
        IServiceProvider shared = fixture.ApiServices;
        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddSingleton(shared.GetRequiredService<NpgsqlDataSource>());
        services.AddSingleton(shared.GetRequiredService<IUnitOfWorkFactory>());
        services.AddSingleton(shared.GetRequiredService<ICommandIdempotencyStore>());
        services.AddSingleton(shared.GetRequiredService<IAuditAppender>());
        services.AddSingleton(shared.GetRequiredService<IOutboxAppender>());
        services.AddSingleton(shared.GetRequiredService<IFixedWindowCounter>());
        services.AddLogging(builder => builder.AddProvider(loggerProvider));
        services.AddIdentityModule(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private static ConfigurationManager BuildConfiguration()
    {
        string envelopeKey = SecretBase64();
        ConfigurationManager configuration = new();
        configuration["App:PublicBaseUrl"] = "https://app.poolai.test";
        configuration["Email:FromAddress"] = "no-reply@poolai.test";
        configuration["Auth:Password:MinLength"] = "12";
        configuration["Auth:PasswordReset:TokenMinutes"] = "30";
        configuration["Auth:PasswordReset:IpRequestsPerMinute"] = "5";
        configuration["Auth:PasswordReset:AccountRequestsPerMinute"] = "3";
        configuration["Auth:PasswordReset:RateLimitScopePepper"] = SecretBase64();
        configuration["Auth:TokenHash:CurrentPepperVersion"] = "7";
        configuration["Auth:TokenHash:CurrentPepper"] = SecretBase64();
        configuration["Idempotency:RequestHashPepper"] = SecretBase64();
        configuration["Secrets:Envelope:CurrentKeyId"] = "email-e2e-k1";
        configuration["Secrets:Envelope:CurrentKey"] = envelopeKey;
        configuration["Secrets:Envelope:DecryptKeyRing:email-e2e-k1"] = envelopeKey;
        return configuration;
    }

    private async ValueTask InsertUserAsync(
        EntityId userId,
        string email,
        UserLifecycle status,
        string passwordHash,
        CancellationToken cancellationToken)
    {
        using (NpgsqlCommand user = fixture.AdministratorDataSource.CreateCommand("""
                   INSERT INTO public.users (
                       id, email, normalized_email, display_name, password_hash,
                       security_stamp, status
                   ) VALUES ($1, $2, $2, $3, $4, $5, $6);
                   """))
        {
            user.Parameters.AddWithValue(userId.Value);
            user.Parameters.AddWithValue(email);
            user.Parameters.AddWithValue($"Password reset {status}");
            user.Parameters.AddWithValue(passwordHash);
            user.Parameters.AddWithValue(Guid.CreateVersion7());
            user.Parameters.AddWithValue(status is UserLifecycle.Active ? "active" : "disabled");
            Assert.Equal(
                1,
                await user.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true));
        }

        using NpgsqlCommand role = fixture.AdministratorDataSource.CreateCommand("""
            INSERT INTO public.user_roles (user_id, role_id, assigned_by)
            VALUES ($1, $2, $1);
            """);
        role.Parameters.AddWithValue(userId.Value);
        role.Parameters.AddWithValue(UserRoleId);
        Assert.Equal(
            1,
            await role.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true));
    }

    private static ForgotPasswordCommand Forgot(string email) => new(
        EntityId.New(),
        email,
        "192.0.2.77",
        "password-reset-e2e");

    private static CompletePasswordResetCommand Complete(
        string token,
        string password) => new(
        EntityId.New(),
        token,
        password,
        "192.0.2.77",
        "password-reset-e2e");

    private static void AssertAccepted(Result<IdentityCommandOutcome> result)
    {
        Assert.True(result.IsSuccess);
        Assert.Equal(202, result.Value.StatusCode);
        Assert.False(result.Value.IsReplay);
    }

    private async ValueTask<ResetEmail> ReadResetEmailAsync(
        EntityId userId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            SELECT id, one_time_token_id, message_id, recipient_envelope::text,
                   template_code, template_payload::text,
                   delivery_secret_envelope::text
            FROM public.email_outbox
            WHERE user_id = $1;
            """);
        command.Parameters.AddWithValue(userId.Value);
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(true);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        ResetEmail result = new(
            new EntityId(reader.GetGuid(0)),
            new EntityId(reader.GetGuid(1)),
            reader.GetString(2),
            JsonSerializer.Deserialize<JsonElement>(reader.GetString(3)),
            reader.GetString(4),
            JsonSerializer.Deserialize<JsonElement>(reader.GetString(5)),
            JsonSerializer.Deserialize<JsonElement>(reader.GetString(6)),
            Recipient: string.Empty,
            ResetUrl: string.Empty,
            Token: string.Empty);
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        return result;
    }

    private async ValueTask<string> ReadPasswordResetPersistenceTextAsync(
        EntityId userId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            SELECT concat_ws('|',
                COALESCE((SELECT string_agg(concat_ws('|',
                    encode(token_hash, 'base64'), pepper_version::text,
                    used_at::text, revoked_at::text, revoke_reason), '|')
                    FROM public.one_time_tokens WHERE user_id = $1), ''),
                COALESCE((SELECT string_agg(concat_ws('|',
                    recipient_envelope::text, template_payload::text,
                    delivery_secret_envelope::text, last_error), '|')
                    FROM public.email_outbox WHERE user_id = $1), ''),
                COALESCE((SELECT string_agg(concat_ws('|',
                    before_state::text, after_state::text, metadata::text), '|')
                    FROM public.audit_logs WHERE target_id = $1), ''),
                COALESCE((SELECT string_agg(payload::text, '|')
                    FROM public.outbox_messages
                    WHERE aggregate_id = $1 AND topic = 'poolai.identity.v1'), ''));
            """);
        command.Parameters.AddWithValue(userId.Value);
        return Assert.IsType<string>(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(true));
    }

    private async ValueTask<EmailDeliveryState> ReadDeliveryStateAsync(
        EntityId emailId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            SELECT status, attempts, lock_generation, last_error,
                   recipient_envelope IS NULL, delivery_secret_envelope IS NULL,
                   sent_at, dead_at
            FROM public.email_outbox
            WHERE id = $1;
            """);
        command.Parameters.AddWithValue(emailId.Value);
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(true);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        return new EmailDeliveryState(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetBoolean(4),
            reader.GetBoolean(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7));
    }

    private async ValueTask ForceEmailDueAsync(
        EntityId emailId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            UPDATE public.email_outbox
            SET next_attempt_at = clock_timestamp() - interval '1 second'
            WHERE id = $1 AND status = 'pending';
            """);
        command.Parameters.AddWithValue(emailId.Value);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true));
    }

    private async ValueTask<long> CountAsync(
        string sql,
        object parameter,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(parameter);
        return Assert.IsType<long>(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(true));
    }

    private async ValueTask<bool> TokenWasUsedAsync(
        EntityId tokenId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            SELECT used_at IS NOT NULL
            FROM public.one_time_tokens
            WHERE id = $1;
            """);
        command.Parameters.AddWithValue(tokenId.Value);
        return Assert.IsType<bool>(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(true));
    }

    private async ValueTask<string> ReadPasswordHashAsync(
        EntityId userId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            SELECT password_hash
            FROM public.users
            WHERE id = $1;
            """);
        command.Parameters.AddWithValue(userId.Value);
        return Assert.IsType<string>(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(true));
    }

    private static string ExtractToken(string resetUrl)
    {
        Uri uri = new(resetUrl, UriKind.Absolute);
        const string Prefix = "?token=";
        Assert.StartsWith(Prefix, uri.Query, StringComparison.Ordinal);
        string token = Uri.UnescapeDataString(uri.Query[Prefix.Length..]);
        Assert.Equal(43, token.Length);
        return token;
    }

    private static bool CertificateMatches(
        X509Certificate? certificate,
        byte[] expectedSha256) => certificate is not null
        && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(certificate.GetRawCertData()),
            expectedSha256);

    private static string SecretBase64() => Convert.ToBase64String(
        RandomNumberGenerator.GetBytes(32));

    private static void AssertSecretsAbsent(
        PasswordResetScenario scenario,
        ResetEmail email,
        string persistenceText)
    {
        string diagnostics = string.Join('|', scenario.Logs.Messages);
        foreach (string forbidden in new[]
                 {
                     email.Token,
                     email.ResetUrl,
                     scenario.SmtpPassword,
                     scenario.NewPassword,
                     scenario.ActiveEmail,
                 })
        {
            Assert.DoesNotContain(forbidden, persistenceText, StringComparison.Ordinal);
            Assert.DoesNotContain(forbidden, diagnostics, StringComparison.Ordinal);
        }
    }

    private sealed record PasswordResetScenario(
        ServiceProvider Services,
        RecordingLoggerProvider Logs,
        EntityId ActiveUserId,
        EntityId DisabledUserId,
        string ActiveEmail,
        string DisabledEmail,
        string MissingEmail,
        string SmtpUsername,
        string SmtpPassword,
        string NewPassword);

    private sealed record ResetEmail(
        EntityId EmailId,
        EntityId TokenId,
        string MessageId,
        JsonElement RecipientEnvelope,
        string TemplateCode,
        JsonElement TemplatePayload,
        JsonElement DeliverySecretEnvelope,
        string Recipient,
        string ResetUrl,
        string Token);

    private sealed record EmailDeliveryState(
        string Status,
        int Attempts,
        long Generation,
        string? LastError,
        bool RecipientEnvelopeCleared,
        bool DeliverySecretEnvelopeCleared,
        DateTimeOffset? SentAt,
        DateTimeOffset? DeadAt);

    private sealed class ZeroJitter : IEmailRetryJitter
    {
        public double NextFraction() => 0;
    }

    private sealed class RecordingOperationalEventWriter : IOperationalEventWriter
    {
        internal ConcurrentQueue<(string Name, JsonElement Payload)> Events { get; } = new();

        public ValueTask WriteAsync(
            string eventName,
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Enqueue((eventName, payload.Clone()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        internal ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) => messages.Enqueue(
                    formatter(state, exception));
        }
    }

    private sealed class ObservableStartTlsSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 _certificate = CreateCertificate();
        private readonly CancellationTokenSource _shutdown = new();
        private readonly string _username;
        private readonly string _password;
        private readonly Task _serverTask;
        private int _authenticatedSessions;
        private int _startTlsSessions;

        internal ObservableStartTlsSmtpServer(string username, string password)
        {
            _username = username;
            _password = password;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            CertificateSha256 = SHA256.HashData(_certificate.RawData);
            _serverTask = ServeAsync(_shutdown.Token);
        }

        internal int Port { get; }

        internal byte[] CertificateSha256 { get; }

        internal int AuthenticatedSessions => Volatile.Read(ref _authenticatedSessions);

        internal int StartTlsSessions => Volatile.Read(ref _startTlsSessions);

        internal ConcurrentQueue<CapturedSmtpMessage> Messages { get; } = new();

        internal Task Completion => _serverTask;

        public async ValueTask DisposeAsync()
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
            _listener.Stop();
            try
            {
                await _serverTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }
            catch (SocketException) when (_shutdown.IsCancellationRequested)
            {
            }

            _certificate.Dispose();
            _shutdown.Dispose();
        }

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken)
                    .ConfigureAwait(false);
                await ServeSessionAsync(client, attempt, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private async Task ServeSessionAsync(
            TcpClient client,
            int attempt,
            CancellationToken cancellationToken)
        {
            NetworkStream network = client.GetStream();
            await WriteReplyAsync(network, "220 localhost ESMTP ready\r\n", cancellationToken)
                .ConfigureAwait(false);
            using (StreamReader reader = Reader(network))
            {
                ExpectCommand(await ReadLineAsync(reader, cancellationToken).ConfigureAwait(false), "EHLO ");
                await WriteReplyAsync(
                    network,
                    "250-localhost\r\n250 STARTTLS\r\n",
                    cancellationToken).ConfigureAwait(false);
                ExpectExact(
                    await ReadLineAsync(reader, cancellationToken).ConfigureAwait(false),
                    "STARTTLS");
                await WriteReplyAsync(
                    network,
                    "220 2.0.0 Ready to start TLS\r\n",
                    cancellationToken).ConfigureAwait(false);
            }

            using SslStream secure = new(network, leaveInnerStreamOpen: true);
            await secure.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                },
                cancellationToken).ConfigureAwait(false);
            _ = Interlocked.Increment(ref _startTlsSessions);
            using StreamReader secureReader = Reader(secure);
            ExpectCommand(
                await ReadLineAsync(secureReader, cancellationToken).ConfigureAwait(false),
                "EHLO ");
            await WriteReplyAsync(
                secure,
                "250-localhost\r\n250 AUTH LOGIN\r\n",
                cancellationToken).ConfigureAwait(false);
            await AuthenticateAsync(secure, secureReader, cancellationToken).ConfigureAwait(false);
            await ReceiveMessageAsync(secure, secureReader, attempt, cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task AuthenticateAsync(
            Stream stream,
            StreamReader reader,
            CancellationToken cancellationToken)
        {
            ExpectExact(
                await ReadLineAsync(reader, cancellationToken).ConfigureAwait(false),
                "AUTH LOGIN");
            await WriteReplyAsync(stream, "334 VXNlcm5hbWU6\r\n", cancellationToken)
                .ConfigureAwait(false);
            string username = DecodeCredential(
                await ReadLineAsync(reader, cancellationToken).ConfigureAwait(false));
            await WriteReplyAsync(stream, "334 UGFzc3dvcmQ6\r\n", cancellationToken)
                .ConfigureAwait(false);
            string password = DecodeCredential(
                await ReadLineAsync(reader, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(username, _username, StringComparison.Ordinal)
                || !string.Equals(password, _password, StringComparison.Ordinal))
            {
                await WriteReplyAsync(stream, "535 5.7.8 Authentication failed\r\n", cancellationToken)
                    .ConfigureAwait(false);
                throw new InvalidDataException("SMTP test authentication failed.");
            }

            await WriteReplyAsync(stream, "235 2.7.0 Authentication successful\r\n", cancellationToken)
                .ConfigureAwait(false);
            _ = Interlocked.Increment(ref _authenticatedSessions);
        }

        private async Task ReceiveMessageAsync(
            Stream stream,
            StreamReader reader,
            int attempt,
            CancellationToken cancellationToken)
        {
            ExpectCommand(
                await ReadLineAsync(reader, cancellationToken).ConfigureAwait(false),
                "MAIL FROM:<");
            await WriteReplyAsync(stream, "250 2.1.0 Sender accepted\r\n", cancellationToken)
                .ConfigureAwait(false);
            string recipientCommand = await ReadLineAsync(reader, cancellationToken)
                .ConfigureAwait(false);
            ExpectCommand(recipientCommand, "RCPT TO:<");
            string recipient = recipientCommand[9..^1];
            await WriteReplyAsync(stream, "250 2.1.5 Recipient accepted\r\n", cancellationToken)
                .ConfigureAwait(false);
            ExpectExact(
                await ReadLineAsync(reader, cancellationToken).ConfigureAwait(false),
                "DATA");
            await WriteReplyAsync(stream, "354 End data with <CRLF>.<CRLF>\r\n", cancellationToken)
                .ConfigureAwait(false);

            List<string> lines = [];
            while (true)
            {
                string line = await ReadLineAsync(reader, cancellationToken).ConfigureAwait(false);
                if (string.Equals(line, ".", StringComparison.Ordinal))
                {
                    break;
                }

                lines.Add(line);
            }

            Messages.Enqueue(ParseMessage(lines, recipient));
            string reply = attempt <= 2
                ? "451 4.3.0 Temporary delivery failure\r\n"
                : "250 2.0.0 Message accepted\r\n";
            await WriteReplyAsync(stream, reply, cancellationToken).ConfigureAwait(false);
        }

        private static CapturedSmtpMessage ParseMessage(
            List<string> lines,
            string recipient)
        {
            int separator = -1;
            for (int index = 0; index < lines.Count; index++)
            {
                if (lines[index].Length == 0)
                {
                    separator = index;
                    break;
                }
            }

            if (separator <= 0)
            {
                throw new InvalidDataException("SMTP test message has no header separator.");
            }

            string messageId = lines.Take(separator)
                .Single(line => line.StartsWith("Message-ID: ", StringComparison.Ordinal))[12..];
            string encodedBody = string.Concat(lines.Skip(separator + 1));
            string body = Encoding.UTF8.GetString(Convert.FromBase64String(encodedBody));
            return new CapturedSmtpMessage(
                messageId,
                recipient,
                body,
                string.Join("\r\n", lines));
        }

        private static StreamReader Reader(Stream stream) => new(
            stream,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1_024,
            leaveOpen: true);

        private static async ValueTask<string> ReadLineAsync(
            StreamReader reader,
            CancellationToken cancellationToken) => await reader
            .ReadLineAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new EndOfStreamException("SMTP test peer closed the connection.");

        private static async ValueTask WriteReplyAsync(
            Stream stream,
            string reply,
            CancellationToken cancellationToken)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(reply);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private static void ExpectCommand(string actual, string prefix)
        {
            if (!actual.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException("SMTP test received an unexpected command.");
            }
        }

        private static void ExpectExact(string actual, string expected)
        {
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new InvalidDataException("SMTP test received an unexpected command.");
            }
        }

        private static string DecodeCredential(string value) => Encoding.UTF8.GetString(
            Convert.FromBase64String(value));

        private static X509Certificate2 CreateCertificate()
        {
            using RSA rsa = RSA.Create(2_048);
            CertificateRequest request = new(
                "CN=localhost",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
                certificateAuthority: false,
                hasPathLengthConstraint: false,
                pathLengthConstraint: 0,
                critical: true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));
            OidCollection usages = new();
            usages.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                usages,
                critical: true));
            SubjectAlternativeNameBuilder names = new();
            names.AddDnsName("localhost");
            request.CertificateExtensions.Add(names.Build());
            DateTimeOffset now = TimeProvider.System.GetUtcNow();
            return request.CreateSelfSigned(
                now.AddMinutes(-5),
                now.AddHours(1));
        }
    }

    private sealed record CapturedSmtpMessage(
        string MessageId,
        string Recipient,
        string Body,
        string RawMessage);

    private sealed record EmailDeliveryRuntime(
        EmailOutboxProcessor Processor,
        RecordingOperationalEventWriter OperationalEvents);
}
