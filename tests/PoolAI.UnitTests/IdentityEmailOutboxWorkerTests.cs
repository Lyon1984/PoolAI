using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Identity;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application;
using PoolAI.Modules.Identity.Infrastructure.Email;
using PoolAI.Modules.Identity.Worker;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.UnitTests;

public sealed class IdentityEmailOutboxWorkerTests
{
    private static readonly EntityId EmailId = new(
        Guid.Parse("019bd5e8-30e0-7d4c-a7f2-bb1db0634041"));
    private static readonly EntityId Owner = new(
        Guid.Parse("019bd5e8-30e0-7d4c-a7f2-bb1db0634042"));
    private static readonly string[] ExpectedDeadEventProperties =
        ["attempt", "email_id", "failure_class", "generation", "terminal_reason"];
    private const string Recipient = "person@example.test";
    private const string ResetUrl = "https://poolai.example.test/reset-password?token=secret-token";

    [Fact]
    public async Task SmtpSendRunsOnlyAfterClaimUnitOfWorkIsDisposed()
    {
        TrackingUnitOfWorkFactory unitOfWorkFactory = new();
        RecordingDeliveryStore store = new(CreateMessage(attempt: 1));
        ScriptedTransport transport = new(unitOfWorkFactory, EmailTransportResult.Sent);
        EmailOutboxProcessor processor = CreateProcessor(
            unitOfWorkFactory,
            store,
            transport: transport);

        EmailOutboxProcessResult result = await processor.ProcessNextAsync(
            new OwnedJobLock(),
            TestContext.Current.CancellationToken);

        Assert.Equal(EmailOutboxProcessResult.Processed, result);
        Assert.Equal(0, unitOfWorkFactory.ActiveCount);
        Assert.Equal(1, store.SentCount);
        Assert.Equal(0, transport.ActiveUnitOfWorkCounts.Single());
    }

    [Fact]
    public async Task TwoTransientFailuresIncludingUnknownDataOutcomeReuseMessageIdThenSucceed()
    {
        TrackingUnitOfWorkFactory unitOfWorkFactory = new();
        RecordingDeliveryStore store = new(
            CreateMessage(attempt: 1),
            CreateMessage(attempt: 2),
            CreateMessage(attempt: 3));
        ScriptedTransport transport = new(
            unitOfWorkFactory,
            EmailTransportResult.Transient(EmailDeliveryFailureClass.Smtp4xx),
            EmailTransportResult.Transient(EmailDeliveryFailureClass.Network),
            EmailTransportResult.Sent);
        EmailOutboxProcessor processor = CreateProcessor(
            unitOfWorkFactory,
            store,
            transport: transport);
        OwnedJobLock jobLock = new();

        for (int attempt = 0; attempt < 3; attempt++)
        {
            Assert.Equal(
                EmailOutboxProcessResult.Processed,
                await processor.ProcessNextAsync(
                    jobLock,
                    TestContext.Current.CancellationToken));
        }

        Assert.Equal(2, store.RetryCount);
        Assert.Equal(1, store.SentCount);
        Assert.Equal(3, transport.Messages.Count);
        Assert.Single(transport.Messages
            .Select(static message => message.MessageId)
            .Distinct(StringComparer.Ordinal));
        Assert.All(transport.Messages, static message =>
        {
            Assert.Equal(BuildMessageId(), message.MessageId);
            Assert.Contains(ResetUrl, message.Body, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("smtp_5xx")]
    [InlineData("invalid_recipient")]
    public async Task PermanentTransportFailuresGoDirectlyToDead(string failureCode)
    {
        EmailDeliveryFailureClass failureClass = string.Equals(
            failureCode,
            "smtp_5xx",
            StringComparison.Ordinal)
            ? EmailDeliveryFailureClass.Smtp5xx
            : EmailDeliveryFailureClass.InvalidRecipient;
        TrackingUnitOfWorkFactory unitOfWorkFactory = new();
        RecordingDeliveryStore store = new(CreateMessage(attempt: 1));
        RecordingOperationalEventWriter eventWriter = new(unitOfWorkFactory);
        EmailOutboxProcessor processor = CreateProcessor(
            unitOfWorkFactory,
            store,
            eventWriter,
            new ScriptedTransport(
                unitOfWorkFactory,
                EmailTransportResult.Permanent(failureClass)));

        Assert.Equal(
            EmailOutboxProcessResult.Processed,
            await processor.ProcessNextAsync(
                new OwnedJobLock(),
                TestContext.Current.CancellationToken));

        Assert.Equal(1, store.DeadCount);
        Assert.Equal(0, store.RetryCount);
        AssertNonSecretDeadEvent(eventWriter.SinglePayload);
    }

    [Fact]
    public async Task TransientFailureAtMaximumAttemptGoesToDead()
    {
        TrackingUnitOfWorkFactory unitOfWorkFactory = new();
        RecordingDeliveryStore store = new(CreateMessage(attempt: 3));
        EmailOutboxProcessor processor = CreateProcessor(
            unitOfWorkFactory,
            store,
            transport: new ScriptedTransport(
                unitOfWorkFactory,
                EmailTransportResult.Transient(EmailDeliveryFailureClass.Timeout)));

        Assert.Equal(
            EmailOutboxProcessResult.Processed,
            await processor.ProcessNextAsync(
                new OwnedJobLock(),
                TestContext.Current.CancellationToken));

        Assert.Equal(1, store.DeadCount);
        Assert.Equal("maximum_attempts", store.LastError);
        Assert.Equal(0, store.RetryCount);
    }

    [Fact]
    public async Task StaleLeaseHeartbeatCancelsSendAndForbidsTerminalTransition()
    {
        TrackingUnitOfWorkFactory unitOfWorkFactory = new();
        RecordingDeliveryStore store = new(CreateMessage(attempt: 1))
        {
            HeartbeatResult = false,
        };
        ScriptedTransport transport = ScriptedTransport.WaitForCancellation(unitOfWorkFactory);
        EmailOutboxProcessor processor = CreateProcessor(
            unitOfWorkFactory,
            store,
            transport: transport,
            heartbeatInterval: TimeSpan.FromMilliseconds(5));

        EmailOutboxProcessResult result = await processor.ProcessNextAsync(
            new OwnedJobLock(),
            TestContext.Current.CancellationToken);

        Assert.Equal(EmailOutboxProcessResult.OwnershipLost, result);
        Assert.True(transport.WasCancelled);
        Assert.Equal(1, store.HeartbeatCount);
        Assert.Equal(0, store.TerminalTransitionCount);
        Assert.Equal(0, unitOfWorkFactory.ActiveCount);
    }

    [Fact]
    public async Task TransportFaultDuringStaleLeaseCancellationCannotOverrideOwnershipLost()
    {
        TrackingUnitOfWorkFactory unitOfWorkFactory = new();
        RecordingDeliveryStore store = new(CreateMessage(attempt: 1))
        {
            HeartbeatResult = false,
        };
        AdversarialCancellationTransport transport = new(
            faultAfterCancellation: true,
            throwFromCancellationCallback: false);
        EmailOutboxProcessor processor = CreateProcessor(
            unitOfWorkFactory,
            store,
            transport: transport,
            heartbeatInterval: TimeSpan.FromMilliseconds(5));

        EmailOutboxProcessResult result = await processor.ProcessNextAsync(
            new OwnedJobLock(),
            TestContext.Current.CancellationToken);

        Assert.Equal(EmailOutboxProcessResult.OwnershipLost, result);
        Assert.True(transport.WasCancelled);
        Assert.Equal(0, store.TerminalTransitionCount);
    }

    [Fact]
    public async Task CancellationCallbackFaultDuringStaleLeaseCannotOverrideOwnershipLost()
    {
        TrackingUnitOfWorkFactory unitOfWorkFactory = new();
        RecordingDeliveryStore store = new(CreateMessage(attempt: 1))
        {
            HeartbeatResult = false,
        };
        AdversarialCancellationTransport transport = new(
            faultAfterCancellation: false,
            throwFromCancellationCallback: true);
        EmailOutboxProcessor processor = CreateProcessor(
            unitOfWorkFactory,
            store,
            transport: transport,
            heartbeatInterval: TimeSpan.FromMilliseconds(5));

        EmailOutboxProcessResult result = await processor.ProcessNextAsync(
            new OwnedJobLock(),
            TestContext.Current.CancellationToken);

        Assert.Equal(EmailOutboxProcessResult.OwnershipLost, result);
        Assert.True(transport.WasCancelled);
        Assert.Equal(0, store.TerminalTransitionCount);
    }

    [Fact]
    public async Task HeartbeatExceptionCancelsAndObservesSendBeforeRethrowingSameException()
    {
        InvalidOperationException expected = new("heartbeat probe failed");
        TrackingUnitOfWorkFactory unitOfWorkFactory = new();
        RecordingDeliveryStore store = new(CreateMessage(attempt: 1))
        {
            HeartbeatException = expected,
        };
        ScriptedTransport transport = ScriptedTransport.WaitForCancellation(unitOfWorkFactory);
        EmailOutboxProcessor processor = CreateProcessor(
            unitOfWorkFactory,
            store,
            transport: transport,
            heartbeatInterval: TimeSpan.FromMilliseconds(5));

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ProcessNextAsync(
                new OwnedJobLock(),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Same(expected, thrown);
        Assert.True(transport.WasCancelled);
        Assert.Equal(1, store.HeartbeatCount);
        Assert.Equal(0, store.TerminalTransitionCount);
        Assert.Equal(0, unitOfWorkFactory.ActiveCount);
    }

    [Fact]
    public async Task JobLockVerificationExceptionCancelsAndObservesSendBeforeRethrowingSameException()
    {
        InvalidOperationException expected = new("job-lock probe failed");
        TrackingUnitOfWorkFactory unitOfWorkFactory = new();
        RecordingDeliveryStore store = new(CreateMessage(attempt: 1));
        ScriptedTransport transport = ScriptedTransport.WaitForCancellation(unitOfWorkFactory);
        EmailOutboxProcessor processor = CreateProcessor(
            unitOfWorkFactory,
            store,
            transport: transport,
            heartbeatInterval: TimeSpan.FromMilliseconds(5));

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ProcessNextAsync(
                new ThrowingJobLock(expected, throwOnVerification: 3),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Same(expected, thrown);
        Assert.True(transport.WasCancelled);
        Assert.Equal(0, store.HeartbeatCount);
        Assert.Equal(0, store.TerminalTransitionCount);
        Assert.Equal(0, unitOfWorkFactory.ActiveCount);
    }

    [Fact]
    public async Task OversizedSmtpReplyLineIsAProtocolFailure()
    {
        byte[] greeting = Encoding.ASCII.GetBytes(
            "220 " + new string('x', 2_045));

        EmailTransportResult result = await SendAgainstGreetingAsync(greeting);

        Assert.Equal(EmailTransportDisposition.TransientFailure, result.Disposition);
        Assert.Equal(EmailDeliveryFailureClass.SmtpProtocol, result.FailureClass);
    }

    [Fact]
    public async Task InvalidUtf8SmtpReplyIsATransientProtocolFailure()
    {
        byte[] greeting =
        [
            (byte)'2', (byte)'2', (byte)'0', (byte)' ', 0xc3, 0x28, (byte)'\r', (byte)'\n',
        ];

        EmailTransportResult result = await SendAgainstGreetingAsync(greeting);

        Assert.Equal(EmailTransportDisposition.TransientFailure, result.Disposition);
        Assert.Equal(EmailDeliveryFailureClass.SmtpProtocol, result.FailureClass);
    }

    [Theory]
    [InlineData("421 try later\r\n", "TransientFailure", "Smtp4xx")]
    [InlineData("521 unavailable\r\n", "PermanentFailure", "Smtp5xx")]
    [InlineData("320 unexpected\r\n", "TransientFailure", "SmtpProtocol")]
    public async Task SmtpGreetingFailuresAreClassifiedWithoutRetryAmbiguity(
        string greeting,
        string disposition,
        string failureClass)
    {
        EmailTransportResult result = await SendAgainstGreetingAsync(
            Encoding.ASCII.GetBytes(greeting));

        Assert.Equal(Enum.Parse<EmailTransportDisposition>(disposition), result.Disposition);
        Assert.Equal(Enum.Parse<EmailDeliveryFailureClass>(failureClass), result.FailureClass);
    }

    [Theory]
    [InlineData("ehlo-4xx", "TransientFailure", "Smtp4xx")]
    [InlineData("ehlo-5xx", "PermanentFailure", "Smtp5xx")]
    [InlineData("missing-starttls", "PermanentFailure", "SmtpCapability")]
    [InlineData("starttls-4xx", "TransientFailure", "Smtp4xx")]
    [InlineData("starttls-5xx", "PermanentFailure", "Smtp5xx")]
    public async Task SmtpEhloAndStartTlsFailuresAreClassified(
        string scenario,
        string disposition,
        string failureClass)
    {
        string ehlo = scenario switch
        {
            "ehlo-4xx" => "421 try later\r\n",
            "ehlo-5xx" => "521 unavailable\r\n",
            "missing-starttls" => "250 smtp.local\r\n",
            _ => "250-smtp.local\r\n250 STARTTLS\r\n",
        };
        string[] remaining = scenario.StartsWith("starttls-", StringComparison.Ordinal)
            ? [ehlo, scenario.EndsWith("4xx", StringComparison.Ordinal)
                ? "421 try later\r\n"
                : "521 unavailable\r\n"]
            : [ehlo];

        EmailTransportResult result = await SendAgainstScriptAsync(remaining);

        Assert.Equal(Enum.Parse<EmailTransportDisposition>(disposition), result.Disposition);
        Assert.Equal(Enum.Parse<EmailDeliveryFailureClass>(failureClass), result.FailureClass);
    }

    [Fact]
    public async Task SmtpGreetingTimeoutIsClassifiedAsTransientTimeout()
    {
        EmailTransportResult result = await SendAgainstGreetingAsync([]);

        Assert.Equal(EmailTransportDisposition.TransientFailure, result.Disposition);
        Assert.Equal(EmailDeliveryFailureClass.Timeout, result.FailureClass);
    }

    [Fact]
    public async Task ImplicitTlsSmtpSessionDeliversCompleteMimeMessage()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using X509Certificate2 certificate = CreateLoopbackCertificate();
        Task serverTask = ServeImplicitTlsDeliveryAsync(
            listener,
            certificate,
            TestContext.Current.CancellationToken);
        SmtpEmailTransport transport = new(
            CreateOptions(
                smtpHost: "localhost",
                smtpPort: port,
                smtpSecurity: SmtpSecurityMode.ImplicitTls),
            (_, presented, _, _) => presented is not null
                && string.Equals(
                    presented.GetCertHashString(HashAlgorithmName.SHA256),
                    certificate.GetCertHashString(HashAlgorithmName.SHA256),
                    StringComparison.Ordinal));

        EmailTransportResult result = await transport.SendAsync(
            new EmailTransportMessage(
                "no-reply@poolai.example.test",
                "PoolAI",
                Recipient,
                "Reset password",
                BuildMessageId(),
                "Body"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        await serverTask.ConfigureAwait(true);
        Assert.Equal(EmailTransportDisposition.Sent, result.Disposition);
    }

    [Fact]
    public async Task ImplicitTlsRejectsPlaintextServerAsTlsFailure()
    {
        EmailTransportResult result = await SendAgainstGreetingAsync(
            "220 plaintext is not TLS\r\n"u8.ToArray(),
            SmtpSecurityMode.ImplicitTls);

        Assert.Equal(EmailTransportDisposition.TransientFailure, result.Disposition);
        Assert.Equal(EmailDeliveryFailureClass.Tls, result.FailureClass);
    }

    [Fact]
    public async Task RefusedSmtpConnectionIsANetworkFailure()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        SmtpEmailTransport transport = new(CreateOptions(
            smtpHost: "localhost",
            smtpPort: port,
            smtpTimeout: TimeSpan.FromMilliseconds(250)));

        EmailTransportResult result = await transport.SendAsync(
            new EmailTransportMessage(
                "no-reply@poolai.example.test",
                "PoolAI",
                Recipient,
                "Reset password",
                BuildMessageId(),
                "Body"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(EmailTransportDisposition.TransientFailure, result.Disposition);
        Assert.Equal(EmailDeliveryFailureClass.Network, result.FailureClass);
    }

    [Theory]
    [InlineData("envelope")]
    [InlineData("template")]
    [InlineData("recipient")]
    public async Task InvalidEnvelopeTemplateOrRecipientNeverCallsTransportAndGoesDead(
        string invalidPart)
    {
        TrackingUnitOfWorkFactory unitOfWorkFactory = new();
        RecordingDeliveryStore store = new(CreateMessage(
            attempt: 1,
            templateCode: string.Equals(invalidPart, "template", StringComparison.Ordinal)
                ? "unknown-template"
                : "password-reset-v1"));
        ScriptedTransport transport = new(unitOfWorkFactory, EmailTransportResult.Sent);
        RecordingSecretEnvelope envelope = invalidPart switch
        {
            "envelope" => RecordingSecretEnvelope.Invalid,
            "recipient" => new RecordingSecretEnvelope("victim@example.test\r\nBcc: attacker@test", ResetUrl),
            _ => new RecordingSecretEnvelope(Recipient, ResetUrl),
        };
        EmailOutboxProcessor processor = CreateProcessor(
            unitOfWorkFactory,
            store,
            transport: transport,
            envelope: envelope);

        Assert.Equal(
            EmailOutboxProcessResult.Processed,
            await processor.ProcessNextAsync(
                new OwnedJobLock(),
                TestContext.Current.CancellationToken));

        Assert.Empty(transport.Messages);
        Assert.Equal(1, store.DeadCount);
    }

    [Fact]
    public void EmailHeadersRejectCrLfInjection()
    {
        Assert.Throws<ArgumentException>(() => new EmailTransportMessage(
            "no-reply@poolai.example.test",
            "PoolAI\r\nBcc: attacker@test",
            Recipient,
            "Reset password",
            BuildMessageId(),
            "Body"));
        Assert.Throws<ArgumentException>(() => new EmailTransportMessage(
            "no-reply@poolai.example.test",
            "PoolAI",
            "victim@example.test\r\nBcc: attacker@test",
            "Reset password",
            BuildMessageId(),
            "Body"));
    }

    [Fact]
    public void EmailHeaderValidationCoversCanonicalBoundaryFailures()
    {
        Assert.Throws<ArgumentException>(() =>
            EmailHeaderValueValidator.NormalizeMailbox("not-a-mailbox", "mailbox"));
        Assert.Throws<ArgumentException>(() =>
            EmailHeaderValueValidator.NormalizeMailbox("Person <person@example.test>", "mailbox"));
        Assert.Throws<ArgumentException>(() => EmailHeaderValueValidator.NormalizeMailbox(
            "person@bad_domain.test",
            "mailbox"));
        Assert.Throws<ArgumentException>(() => EmailHeaderValueValidator.NormalizeMailbox(
            "person..reset@example.test",
            "mailbox"));
        Assert.Throws<ArgumentException>(() => EmailHeaderValueValidator.NormalizeMailbox(
            string.Concat(
                new string('a', 64),
                "@",
                new string('b', 63),
                ".",
                new string('c', 63),
                ".",
                new string('d', 62)),
            "mailbox"));
        Assert.Throws<ArgumentException>(() =>
            EmailHeaderValueValidator.ValidateMessageId("bad", "messageId"));
        Assert.Throws<ArgumentException>(() => EmailHeaderValueValidator.ValidateMessageId(
            "<not-a-guid@example.test>",
            "messageId"));
        Assert.Throws<ArgumentException>(() => EmailHeaderValueValidator.ValidateMessageId(
            $"<{EmailId.Value:D}@bad_domain.test>",
            "messageId"));
        Assert.Throws<ArgumentException>(() =>
            EmailHeaderValueValidator.ValidateDisplayName(new string('x', 129), "name"));
        Assert.Throws<ArgumentException>(() =>
            EmailHeaderValueValidator.ValidateSubject(" subject", "subject"));
        Assert.StartsWith(
            "=?UTF-8?B?",
            EmailHeaderValueValidator.EncodeDisplayName("PoolAI 安全"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void PasswordResetTemplateUsesTheNormalizedIdnaDomainForItsStableMessageId()
    {
        const string NormalizedDomain = "xn--bcher-kva.example";
        string messageId = $"<{EmailId.Value:D}@{NormalizedDomain}>";
        EmailOutboxWorkerOptions options = CreateOptions(
            fromAddress: "no-reply@BÜCHER.example");
        EmailOutboxMessage message = CreateMessage(attempt: 1, messageId: messageId);

        EmailTransportMessage rendered = PasswordResetEmailTemplate.Render(
            message,
            new EmailSecretEnvelopePlaintext(Recipient, ResetUrl),
            options);

        Assert.Equal("no-reply@xn--bcher-kva.example", rendered.FromAddress);
        Assert.Equal(messageId, rendered.MessageId);
    }

    [Fact]
    public void PasswordResetTemplatePreservesFrozenMessageIdAcrossFromDomainRotation()
    {
        string frozenMessageId = $"<{EmailId.Value:D}@old-mail.example>";
        EmailOutboxWorkerOptions options = CreateOptions(
            fromAddress: "no-reply@new-mail.example");
        EmailOutboxMessage pendingMessage = CreateMessage(
            attempt: 2,
            messageId: frozenMessageId);

        EmailTransportMessage rendered = PasswordResetEmailTemplate.Render(
            pendingMessage,
            new EmailSecretEnvelopePlaintext(Recipient, ResetUrl),
            options);

        Assert.Equal("no-reply@new-mail.example", rendered.FromAddress);
        Assert.Equal(frozenMessageId, rendered.MessageId);
    }

    [Fact]
    public void PasswordResetTemplateRejectsFrozenMessageIdForAnotherOutboxRow()
    {
        string foreignMessageId = $"<{Guid.Parse("019bd5e8-30e0-7d4c-a7f2-bb1db0634099"):D}@old-mail.example>";
        EmailOutboxMessage pendingMessage = CreateMessage(
            attempt: 1,
            messageId: foreignMessageId);

        Assert.Throws<ArgumentException>(() => PasswordResetEmailTemplate.Render(
            pendingMessage,
            new EmailSecretEnvelopePlaintext(Recipient, ResetUrl),
            CreateOptions(fromAddress: "no-reply@new-mail.example")));
    }

    [Fact]
    public async Task DurableEmailOutboxMetricsExposeOnlyBoundedFailureLabels()
    {
        TrackingUnitOfWorkFactory unitOfWorkFactory = new();
        RecordingDeliveryStore store = new()
        {
            Observability = new EmailOutboxObservabilitySnapshot(
                PendingCount: 3,
                OldestAgeSeconds: 42.5,
                DeadCount: 1,
                [new EmailOutboxFailureMetric(
                    "smtp_4xx",
                    "retry",
                    "not_terminal",
                    2)]),
        };
        using EmailOutboxMetrics metrics = new(unitOfWorkFactory, store);
        List<MetricReading> readings = await ObserveMetricsAsync(metrics);

        Assert.Contains(readings, static reading =>
            string.Equals(
                reading.Name,
                "poolai_email_outbox_pending",
                StringComparison.Ordinal) && reading.Value == 3);
        Assert.Contains(readings, static reading =>
            string.Equals(
                reading.Name,
                "poolai_email_outbox_oldest_age_seconds",
                StringComparison.Ordinal)
                && reading.Value == 42.5);
        Assert.Contains(readings, static reading =>
            string.Equals(
                reading.Name,
                "poolai_email_outbox_dead",
                StringComparison.Ordinal) && reading.Value == 1);
        MetricReading failure = Assert.Single(readings, static reading =>
            string.Equals(
                reading.Name,
                "poolai_email_outbox_failures_total",
                StringComparison.Ordinal));
        Assert.Equal(2, failure.Value);
        Assert.Equal(
            ["failure_class", "outcome", "terminal_reason"],
            failure.Tags.Select(static tag => tag.Key).ToArray());
        Assert.Equal(
            ["smtp_4xx", "retry", "not_terminal"],
            failure.Tags.Select(static tag => tag.Value).ToArray());
    }

    private static async ValueTask<List<MetricReading>> ObserveMetricsAsync(
        EmailOutboxMetrics metrics)
    {
        using MeterListener listener = new();
        List<MetricReading> readings = [];
        listener.InstrumentPublished = (instrument, candidate) =>
        {
            if (string.Equals(
                    instrument.Meter.Name,
                    EmailOutboxMetrics.MeterName,
                    StringComparison.Ordinal))
            {
                candidate.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            readings.Add(new MetricReading(instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            readings.Add(new MetricReading(instrument.Name, value, tags.ToArray())));
        listener.Start();
        await metrics.RefreshIfDueAsync(
            force: true,
            TestContext.Current.CancellationToken).ConfigureAwait(false);
        listener.RecordObservableInstruments();
        return readings;
    }

    [Fact]
    public void ApiIdentityRegistrationDoesNotRegisterEmailHostedLoop()
    {
        ServiceCollection services = new();

        services.AddIdentityModule();

        Assert.DoesNotContain(
            services,
            static descriptor => string.Equals(
                descriptor.ServiceType.FullName,
                "Microsoft.Extensions.Hosting.IHostedService",
                StringComparison.Ordinal));
    }

    [Fact]
    public void EmailWorkerRegistrationAddsOneHostedSenderAndItsBoundedServices()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddIdentityModule();

        IServiceCollection returned = services.AddIdentityEmailOutboxWorker(
            WorkerConfiguration());

        Assert.Same(services, returned);
        Assert.Single(services, static descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(EmailOutboxSenderService));
        Assert.Contains(services, static descriptor =>
            descriptor.ServiceType == typeof(EmailOutboxWorkerOptions));
        Assert.Contains(services, static descriptor =>
            descriptor.ServiceType == typeof(IEmailOutboxDeliveryStore));
        Assert.Contains(services, static descriptor =>
            descriptor.ServiceType == typeof(IEmailTransport));
        Assert.Contains(services, static descriptor =>
            descriptor.ServiceType == typeof(IEmailRetryJitter));
    }

    [Fact]
    public void EmailWorkerOptionsBindBothTlsModesAndDefaults()
    {
        EmailOutboxWorkerOptions startTls = EmailOutboxWorkerOptions.FromConfiguration(
            WorkerConfiguration());
        IConfiguration implicitTlsConfiguration = WorkerConfiguration(
            new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Email:Smtp:Security"] = "tls",
            ["Email:Smtp:Port"] = "465",
            ["Email:Smtp:Username"] = "smtp-user",
            ["Email:Smtp:Password"] = "smtp-password",
            ["Email:Outbox:ClaimSeconds"] = "60",
            ["Email:Outbox:PollSeconds"] = "10",
            ["Email:Outbox:MaxAttempts"] = "9",
        });
        EmailOutboxWorkerOptions implicitTls =
            EmailOutboxWorkerOptions.FromConfiguration(implicitTlsConfiguration);

        Assert.Equal(SmtpSecurityMode.StartTls, startTls.SmtpSecurity);
        Assert.Equal(587, startTls.SmtpPort);
        Assert.Equal("PoolAI", startTls.FromName);
        Assert.Equal(SmtpSecurityMode.ImplicitTls, implicitTls.SmtpSecurity);
        Assert.Equal(465, implicitTls.SmtpPort);
        Assert.Equal("smtp-user", implicitTls.SmtpUsername);
        Assert.Equal("smtp-password", implicitTls.SmtpPassword);
        Assert.Equal(60, implicitTls.ClaimDuration.TotalSeconds);
        Assert.Equal(20, implicitTls.HeartbeatInterval.TotalSeconds);
        Assert.Equal(10, implicitTls.PollInterval.TotalSeconds);
        Assert.Equal(9, implicitTls.MaximumAttempts);
    }

    [Theory]
    [InlineData("security")]
    [InlineData("claim-low")]
    [InlineData("claim-high")]
    [InlineData("poll-low")]
    [InlineData("poll-high")]
    [InlineData("missing-host")]
    [InlineData("missing-from")]
    [InlineData("invalid-host")]
    public void EmailWorkerConfigurationFailsClosed(string scenario)
    {
        Dictionary<string, string?> overrides = scenario switch
        {
            "security" => new(StringComparer.Ordinal) { ["Email:Smtp:Security"] = "plain" },
            "claim-low" => new(StringComparer.Ordinal) { ["Email:Outbox:ClaimSeconds"] = "9" },
            "claim-high" => new(StringComparer.Ordinal) { ["Email:Outbox:ClaimSeconds"] = "301" },
            "poll-low" => new(StringComparer.Ordinal) { ["Email:Outbox:PollSeconds"] = "0" },
            "poll-high" => new(StringComparer.Ordinal) { ["Email:Outbox:PollSeconds"] = "61" },
            "missing-host" => new(StringComparer.Ordinal) { ["Email:Smtp:Host"] = null },
            "missing-from" => new(StringComparer.Ordinal) { ["Email:FromAddress"] = null },
            "invalid-host" => new(StringComparer.Ordinal) { ["Email:Smtp:Host"] = "127.0.0.1" },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        Assert.Throws<InvalidOperationException>(() =>
            EmailOutboxWorkerOptions.FromConfiguration(WorkerConfiguration(overrides)));
    }

    [Fact]
    public void EmailWorkerOptionsRejectInvalidConstructorBoundaries()
    {
        Assert.Throws<ArgumentException>(() => CreateOptions(smtpHost: "bad host"));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateOptions(smtpPort: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateOptions(smtpPort: 65_536));
        Assert.Throws<ArgumentException>(() => CreateOptions(smtpUsername: "only-user"));
        Assert.Throws<ArgumentException>(() => CreateOptions(smtpPassword: "only-password"));
        Assert.Throws<ArgumentException>(() => CreateOptions(
            smtpUsername: "bad\nuser",
            smtpPassword: "password"));
        Assert.Throws<ArgumentException>(() => CreateOptions(
            smtpUsername: "user",
            smtpPassword: "bad\0password"));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateOptions(maximumAttempts: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateOptions(maximumAttempts: 21));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateOptions(
            pollInterval: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateOptions(
            claimDuration: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateOptions(
            heartbeatInterval: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateOptions(
            smtpTimeout: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateOptions(
            retryBase: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateOptions(
            heartbeatInterval: TimeSpan.FromSeconds(2),
            claimDuration: TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateOptions(
            retryBase: TimeSpan.FromSeconds(2),
            retryMaximum: TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task HostedEmailSenderDrainsOwnedWorkSurvivesTransientCycleAndIdles()
    {
        TrackingUnitOfWorkFactory unitOfWorkFactory = new();
        RecordingDeliveryStore store = new(CreateMessage(attempt: 1));
        EmailOutboxProcessor processor = CreateProcessor(unitOfWorkFactory, store);
        using EmailOutboxMetrics metrics = new(unitOfWorkFactory, store);
        SequencedLockProvider lockProvider = new();
        using EmailOutboxSenderService service = new(
            lockProvider,
            processor,
            metrics,
            CreateOptions(),
            NullLogger<EmailOutboxSenderService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await lockProvider.IdleObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, store.SentCount);
        Assert.True(lockProvider.AcquireCalls >= 3);
        Assert.Equal(0, unitOfWorkFactory.ActiveCount);
    }

    private static EmailOutboxProcessor CreateProcessor(
        TrackingUnitOfWorkFactory unitOfWorkFactory,
        RecordingDeliveryStore store,
        RecordingOperationalEventWriter? eventWriter = null,
        IEmailTransport? transport = null,
        RecordingSecretEnvelope? envelope = null,
        TimeSpan? heartbeatInterval = null) => new(
            unitOfWorkFactory,
            store,
            envelope ?? new RecordingSecretEnvelope(Recipient, ResetUrl),
            transport ?? new ScriptedTransport(unitOfWorkFactory, EmailTransportResult.Sent),
            new ZeroJitter(),
            eventWriter ?? new RecordingOperationalEventWriter(unitOfWorkFactory),
            CreateOptions(heartbeatInterval));

    private static EmailOutboxWorkerOptions CreateOptions(
        TimeSpan? heartbeatInterval = null,
        string smtpHost = "smtp.example.test",
        int smtpPort = 587,
        string fromAddress = "no-reply@poolai.example.test",
        TimeSpan? smtpTimeout = null,
        string? smtpUsername = null,
        string? smtpPassword = null,
        int maximumAttempts = 3,
        TimeSpan? pollInterval = null,
        TimeSpan? claimDuration = null,
        TimeSpan? retryBase = null,
        TimeSpan? retryMaximum = null,
        SmtpSecurityMode smtpSecurity = SmtpSecurityMode.StartTls) => new(
        smtpHost,
        smtpPort,
        smtpSecurity,
        smtpUsername,
        smtpPassword,
        fromAddress,
        "PoolAI",
        maximumAttempts,
        pollInterval ?? TimeSpan.FromMilliseconds(10),
        claimDuration ?? TimeSpan.FromMilliseconds(100),
        heartbeatInterval ?? TimeSpan.FromMilliseconds(20),
        smtpTimeout ?? TimeSpan.FromSeconds(1),
        retryBase ?? TimeSpan.FromMilliseconds(1),
        retryMaximum ?? TimeSpan.FromMilliseconds(10));

    private static IConfiguration WorkerConfiguration(
        Dictionary<string, string?>? overrides = null)
    {
        Dictionary<string, string?> values = new(StringComparer.Ordinal)
        {
            ["Email:Smtp:Host"] = "smtp.example.test",
            ["Email:FromAddress"] = "no-reply@poolai.example.test",
        };
        if (overrides is not null)
        {
            foreach ((string key, string? value) in overrides)
            {
                values[key] = value;
            }
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static EmailOutboxMessage CreateMessage(
        int attempt,
        string templateCode = "password-reset-v1",
        string? messageId = null) =>
        new(
            new EmailOutboxDeliveryLease(
                EmailId,
                Owner,
                attempt,
                attempt),
            messageId ?? BuildMessageId(),
            JsonSerializer.SerializeToElement(new { encrypted = "recipient-secret" }),
            templateCode,
            JsonSerializer.SerializeToElement(new { expires_in_minutes = 30 }),
            JsonSerializer.SerializeToElement(new { encrypted = "reset-url-secret" }));

    private static string BuildMessageId() => $"<{EmailId.Value:D}@poolai.example.test>";

    private static async Task<EmailTransportResult> SendAgainstGreetingAsync(
        byte[] greeting,
        SmtpSecurityMode smtpSecurity = SmtpSecurityMode.StartTls)
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        TaskCompletionSource<bool> releaseServer = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task serverTask = ServeGreetingAsync(
            listener,
            greeting,
            releaseServer.Task,
            TestContext.Current.CancellationToken);
        try
        {
            SmtpEmailTransport transport = new(CreateOptions(
                smtpHost: "localhost",
                smtpPort: port,
                smtpTimeout: TimeSpan.FromMilliseconds(250),
                smtpSecurity: smtpSecurity));
            EmailTransportResult result = await transport.SendAsync(
                new EmailTransportMessage(
                    "no-reply@poolai.example.test",
                    "PoolAI",
                    Recipient,
                    "Reset password",
                    BuildMessageId(),
                    "Body"),
                TestContext.Current.CancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            releaseServer.TrySetResult(true);
            await serverTask.ConfigureAwait(false);
            listener.Stop();
        }
    }

    private static async Task<EmailTransportResult> SendAgainstScriptAsync(
        params string[] commandReplies)
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        TaskCompletionSource<bool> releaseServer = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task serverTask = ServeSmtpScriptAsync(
            listener,
            commandReplies,
            releaseServer.Task,
            TestContext.Current.CancellationToken);
        try
        {
            SmtpEmailTransport transport = new(CreateOptions(
                smtpHost: "localhost",
                smtpPort: port,
                smtpTimeout: TimeSpan.FromMilliseconds(250)));
            return await transport.SendAsync(
                new EmailTransportMessage(
                    "no-reply@poolai.example.test",
                    "PoolAI",
                    Recipient,
                    "Reset password",
                    BuildMessageId(),
                    "Body"),
                TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            releaseServer.TrySetResult(true);
            await serverTask.ConfigureAwait(false);
            listener.Stop();
        }
    }

    private static async Task ServeSmtpScriptAsync(
        TcpListener listener,
        IReadOnlyList<string> commandReplies,
        Task releaseSignal,
        CancellationToken cancellationToken)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken)
            .ConfigureAwait(false);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(
            "220 smtp.local ready\r\n"u8.ToArray(),
            cancellationToken).ConfigureAwait(false);
        using StreamReader reader = new(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1_024,
            leaveOpen: true);
        foreach (string reply in commandReplies)
        {
            Assert.NotNull(await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false));
            await stream.WriteAsync(
                Encoding.ASCII.GetBytes(reply),
                cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        await releaseSignal.ConfigureAwait(false);
    }

    private static X509Certificate2 CreateLoopbackCertificate()
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
        SubjectAlternativeNameBuilder names = new();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        return request.CreateSelfSigned(now.AddMinutes(-1), now.AddMinutes(10));
    }

    private static async Task ServeImplicitTlsDeliveryAsync(
        TcpListener listener,
        X509Certificate2 certificate,
        CancellationToken cancellationToken)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken)
            .ConfigureAwait(false);
        using SslStream stream = new(client.GetStream(), leaveInnerStreamOpen: false);
        await stream.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            },
            cancellationToken).ConfigureAwait(false);
        using StreamReader reader = new(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1_024,
            leaveOpen: true);
        using StreamWriter writer = new(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            1_024,
            leaveOpen: true)
        {
            NewLine = "\r\n",
            AutoFlush = true,
        };
        await writer.WriteLineAsync("220 smtp.local ready").ConfigureAwait(false);
        await ExpectSmtpCommandAsync(reader, writer, "EHLO poolai.local", "250 smtp.local")
            .ConfigureAwait(false);
        await ExpectSmtpCommandAsync(reader, writer, "MAIL FROM:", "250 sender accepted")
            .ConfigureAwait(false);
        await ExpectSmtpCommandAsync(reader, writer, "RCPT TO:", "250 recipient accepted")
            .ConfigureAwait(false);
        await ExpectSmtpCommandAsync(reader, writer, "DATA", "354 send message")
            .ConfigureAwait(false);
        string mime = await ReadMimeMessageAsync(reader, cancellationToken).ConfigureAwait(false);
        Assert.Contains("Message-ID: " + BuildMessageId(), mime, StringComparison.Ordinal);
        Assert.Contains("Content-Transfer-Encoding: base64", mime, StringComparison.Ordinal);
        await writer.WriteLineAsync("250 queued").ConfigureAwait(false);
    }

    private static async Task ExpectSmtpCommandAsync(
        StreamReader reader,
        StreamWriter writer,
        string expectedPrefix,
        string reply)
    {
        string? command = await reader.ReadLineAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        Assert.NotNull(command);
        Assert.StartsWith(expectedPrefix, command, StringComparison.Ordinal);
        await writer.WriteLineAsync(reply).ConfigureAwait(false);
    }

    private static async Task<string> ReadMimeMessageAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        StringBuilder message = new();
        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            Assert.NotNull(line);
            if (string.Equals(line, ".", StringComparison.Ordinal))
            {
                return message.ToString();
            }

            _ = message.AppendLine(line);
        }
    }

    private static async Task ServeGreetingAsync(
        TcpListener listener,
        byte[] greeting,
        Task releaseSignal,
        CancellationToken cancellationToken)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken)
            .ConfigureAwait(false);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(greeting, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        await releaseSignal.ConfigureAwait(false);
    }

    private static void AssertNonSecretDeadEvent(JsonElement payload)
    {
        string[] propertyNames = payload.EnumerateObject()
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(ExpectedDeadEventProperties, propertyNames);
        string rawPayload = payload.GetRawText();
        Assert.DoesNotContain(Recipient, rawPayload, StringComparison.Ordinal);
        Assert.DoesNotContain(ResetUrl, rawPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", rawPayload, StringComparison.Ordinal);
    }

    private sealed class TrackingUnitOfWorkFactory : IUnitOfWorkFactory
    {
        private int _activeCount;

        internal int ActiveCount => Volatile.Read(ref _activeCount);

        public ValueTask<IUnitOfWork> BeginAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref _activeCount);
            return ValueTask.FromResult<IUnitOfWork>(new TrackingUnitOfWork(this));
        }

        private sealed class TrackingUnitOfWork(TrackingUnitOfWorkFactory owner) : IUnitOfWork
        {
            private int _disposed;

            public IUnitOfWorkContext Context { get; } = new TrackingContext();

            public ValueTask CommitAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _ = Interlocked.Decrement(ref owner._activeCount);
                }

                return ValueTask.CompletedTask;
            }
        }

        private sealed class TrackingContext : IUnitOfWorkContext
        {
        }
    }

    private sealed class RecordingDeliveryStore(params EmailOutboxMessage[] messages)
        : IEmailOutboxDeliveryStore
    {
        private readonly Queue<EmailOutboxMessage> _messages = new(messages);

        internal bool HeartbeatResult { get; init; } = true;

        internal Exception? HeartbeatException { get; init; }

        internal int HeartbeatCount { get; private set; }

        internal int SentCount { get; private set; }

        internal int RetryCount { get; private set; }

        internal int DeadCount { get; private set; }

        internal int TerminalTransitionCount => SentCount + RetryCount + DeadCount;

        internal string? LastError { get; private set; }

        internal EmailOutboxObservabilitySnapshot Observability { get; init; } =
            EmailOutboxObservabilitySnapshot.Empty;

        public ValueTask<IReadOnlyList<EmailOutboxMessage>> ClaimDueAsync(
            EntityId owner,
            int maximumCount,
            TimeSpan leaseDuration,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<EmailOutboxMessage> result = _messages.Count == 0
                ? []
                : [_messages.Dequeue()];
            return ValueTask.FromResult(result);
        }

        public ValueTask<bool> HeartbeatAsync(
            EmailOutboxDeliveryLease lease,
            TimeSpan leaseDuration,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HeartbeatCount++;
            if (HeartbeatException is not null)
            {
                throw HeartbeatException;
            }

            return ValueTask.FromResult(HeartbeatResult);
        }

        public ValueTask<bool> MarkSentAsync(
            EmailOutboxDeliveryLease lease,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SentCount++;
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> ReleaseForRetryAsync(
            EmailOutboxDeliveryLease lease,
            TimeSpan retryDelay,
            string failureClass,
            string errorSummary,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RetryCount++;
            LastError = errorSummary;
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> MarkDeadAsync(
            EmailOutboxDeliveryLease lease,
            string failureClass,
            string terminalReason,
            string errorSummary,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeadCount++;
            LastError = errorSummary;
            return ValueTask.FromResult(true);
        }

        public ValueTask<EmailOutboxObservabilitySnapshot> ReadObservabilityAsync(
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Observability);
        }
    }

    private sealed record MetricReading(
        string Name,
        double Value,
        KeyValuePair<string, object?>[] Tags);

    private sealed class RecordingSecretEnvelope : IEmailSecretEnvelope
    {
        private readonly string _recipient;
        private readonly string _resetUrl;
        private readonly bool _invalid;

        internal RecordingSecretEnvelope(string recipient, string resetUrl)
        {
            _recipient = recipient;
            _resetUrl = resetUrl;
        }

        private RecordingSecretEnvelope()
        {
            _recipient = string.Empty;
            _resetUrl = string.Empty;
            _invalid = true;
        }

        internal static RecordingSecretEnvelope Invalid { get; } = new();

        public PasswordResetEmailEnvelopes Encrypt(
            EmailSecretEnvelopePlaintext plaintext,
            EntityId emailOutboxId) => throw new NotSupportedException();

        public EmailSecretEnvelopePlaintext Decrypt(
            JsonElement recipientEnvelope,
            JsonElement deliverySecretEnvelope,
            EntityId emailOutboxId)
        {
            if (_invalid)
            {
                throw new CryptographicException("Invalid envelope for the deterministic test.");
            }

            return new EmailSecretEnvelopePlaintext(_recipient, _resetUrl);
        }
    }

    private sealed class ScriptedTransport : IEmailTransport
    {
        private readonly TrackingUnitOfWorkFactory _unitOfWorkFactory;
        private readonly Queue<EmailTransportResult> _results;
        private readonly bool _waitForCancellation;

        internal ScriptedTransport(
            TrackingUnitOfWorkFactory unitOfWorkFactory,
            params EmailTransportResult[] results)
        {
            _unitOfWorkFactory = unitOfWorkFactory;
            _results = new Queue<EmailTransportResult>(results);
        }

        private ScriptedTransport(TrackingUnitOfWorkFactory unitOfWorkFactory)
        {
            _unitOfWorkFactory = unitOfWorkFactory;
            _results = [];
            _waitForCancellation = true;
        }

        internal List<EmailTransportMessage> Messages { get; } = [];

        internal List<int> ActiveUnitOfWorkCounts { get; } = [];

        internal bool WasCancelled { get; private set; }

        internal static ScriptedTransport WaitForCancellation(
            TrackingUnitOfWorkFactory unitOfWorkFactory) => new(unitOfWorkFactory);

        public ValueTask<EmailTransportResult> SendAsync(
            EmailTransportMessage message,
            CancellationToken cancellationToken)
        {
            Messages.Add(message);
            ActiveUnitOfWorkCounts.Add(_unitOfWorkFactory.ActiveCount);
            return _waitForCancellation
                ? WaitUntilCancelledAsync(cancellationToken)
                : ValueTask.FromResult(_results.Dequeue());
        }

        private async ValueTask<EmailTransportResult> WaitUntilCancelledAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                WasCancelled = true;
                throw;
            }

            throw new InvalidOperationException("The cancellation-only transport unexpectedly resumed.");
        }
    }

    private sealed class AdversarialCancellationTransport(
        bool faultAfterCancellation,
        bool throwFromCancellationCallback) : IEmailTransport
    {
        internal bool WasCancelled { get; private set; }

        public ValueTask<EmailTransportResult> SendAsync(
            EmailTransportMessage message,
            CancellationToken cancellationToken) => WaitUntilCancelledAsync(cancellationToken);

        private async ValueTask<EmailTransportResult> WaitUntilCancelledAsync(
            CancellationToken cancellationToken)
        {
            using CancellationTokenRegistration registration = throwFromCancellationCallback
                ? cancellationToken.Register(static () =>
                    throw new InvalidOperationException("cancellation callback failed"))
                : default;
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                WasCancelled = true;
                if (faultAfterCancellation)
                {
                    throw new InvalidOperationException("transport failed during cancellation");
                }

                throw;
            }

            throw new InvalidOperationException("The cancellation-only transport unexpectedly resumed.");
        }
    }

    private sealed class ZeroJitter : IEmailRetryJitter
    {
        public double NextFraction() => 0;
    }

    private sealed class RecordingOperationalEventWriter(
        TrackingUnitOfWorkFactory unitOfWorkFactory) : IOperationalEventWriter
    {
        private readonly List<JsonElement> _payloads = [];

        internal JsonElement SinglePayload => Assert.Single(_payloads);

        public ValueTask WriteAsync(
            string eventName,
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(0, unitOfWorkFactory.ActiveCount);
            Assert.Equal("email_outbox_dead", eventName);
            _payloads.Add(payload.Clone());
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OwnedJobLock : IWorkerSessionLock
    {
        public WorkerJobIdentity Job => WorkerJobs.EmailOutboxSender;

        public long LockId => 1;

        public ValueTask<bool> VerifyOwnershipAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(true);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SequencedLockProvider : IWorkerSessionLockProvider
    {
        internal TaskCompletionSource IdleObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal int AcquireCalls { get; private set; }

        public ValueTask<IWorkerSessionLock?> TryAcquireAsync(
            WorkerJobIdentity job,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(WorkerJobs.EmailOutboxSender, job);
            AcquireCalls++;
            if (AcquireCalls == 1)
            {
                return ValueTask.FromResult<IWorkerSessionLock?>(new OwnedJobLock());
            }

            if (AcquireCalls == 2)
            {
                throw new TimeoutException("transient lock probe");
            }

            IdleObserved.TrySetResult();
            return ValueTask.FromResult<IWorkerSessionLock?>(null);
        }
    }

    private sealed class ThrowingJobLock(Exception exception, int throwOnVerification)
        : IWorkerSessionLock
    {
        private int _verificationCount;

        public WorkerJobIdentity Job => WorkerJobs.EmailOutboxSender;

        public long LockId => 1;

        public ValueTask<bool> VerifyOwnershipAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _verificationCount) == throwOnVerification)
            {
                throw exception;
            }

            return ValueTask.FromResult(true);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
