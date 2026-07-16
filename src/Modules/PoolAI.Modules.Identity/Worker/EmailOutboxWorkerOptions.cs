using Microsoft.Extensions.Configuration;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.Identity.Worker;

internal sealed class EmailOutboxWorkerOptions
{
    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryMaximumDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SmtpOperationTimeout = TimeSpan.FromSeconds(30);

    internal EmailOutboxWorkerOptions(
        string smtpHost,
        int smtpPort,
        SmtpSecurityMode smtpSecurity,
        string? smtpUsername,
        string? smtpPassword,
        string fromAddress,
        string fromName,
        int maximumAttempts,
        TimeSpan pollInterval,
        TimeSpan claimDuration,
        TimeSpan heartbeatInterval,
        TimeSpan smtpTimeout,
        TimeSpan retryBase,
        TimeSpan retryMaximum)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(smtpHost);
        if (smtpHost.Any(static character => character is '\r' or '\n' or '\0'
                || char.IsWhiteSpace(character))
            || Uri.CheckHostName(smtpHost) is not UriHostNameType.Dns)
        {
            throw new ArgumentException("SMTP host is invalid.", nameof(smtpHost));
        }

        if (smtpPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(smtpPort));
        }

        if ((smtpUsername is null) != (smtpPassword is null))
        {
            throw new ArgumentException(
                "SMTP username and password must be configured together.",
                nameof(smtpUsername));
        }

        if (smtpUsername is not null)
        {
            ValidateCredential(smtpUsername, nameof(smtpUsername));
            ValidateCredential(smtpPassword!, nameof(smtpPassword));
        }

        if (maximumAttempts is < 1 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        Positive(pollInterval, nameof(pollInterval));
        Positive(claimDuration, nameof(claimDuration));
        Positive(heartbeatInterval, nameof(heartbeatInterval));
        Positive(smtpTimeout, nameof(smtpTimeout));
        Positive(retryBase, nameof(retryBase));
        if (heartbeatInterval >= claimDuration || retryMaximum < retryBase)
        {
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));
        }

        SmtpHost = smtpHost;
        SmtpPort = smtpPort;
        SmtpSecurity = smtpSecurity;
        SmtpUsername = smtpUsername;
        SmtpPassword = smtpPassword;
        FromAddress = EmailHeaderValueValidator.NormalizeMailbox(
            fromAddress,
            nameof(fromAddress));
        FromName = EmailHeaderValueValidator.ValidateDisplayName(fromName, nameof(fromName));
        MaximumAttempts = maximumAttempts;
        PollInterval = pollInterval;
        ClaimDuration = claimDuration;
        HeartbeatInterval = heartbeatInterval;
        SmtpTimeout = smtpTimeout;
        RetryPolicy = new DeliveryRetryPolicy(maximumAttempts, retryBase, retryMaximum);
    }

    internal string SmtpHost { get; }

    internal int SmtpPort { get; }

    internal SmtpSecurityMode SmtpSecurity { get; }

    internal string? SmtpUsername { get; }

    internal string? SmtpPassword { get; }

    internal string FromAddress { get; }

    internal string FromName { get; }

    internal int MaximumAttempts { get; }

    internal TimeSpan PollInterval { get; }

    internal TimeSpan ClaimDuration { get; }

    internal TimeSpan HeartbeatInterval { get; }

    internal TimeSpan SmtpTimeout { get; }

    internal DeliveryRetryPolicy RetryPolicy { get; }

    internal static EmailOutboxWorkerOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string securityValue = configuration["Email:Smtp:Security"] ?? "starttls";
        SmtpSecurityMode security = securityValue switch
        {
            "starttls" => SmtpSecurityMode.StartTls,
            "tls" => SmtpSecurityMode.ImplicitTls,
            _ => throw new InvalidOperationException("Email:Smtp:Security is invalid."),
        };
        int claimSeconds = configuration.GetValue("Email:Outbox:ClaimSeconds", 30);
        int pollSeconds = configuration.GetValue("Email:Outbox:PollSeconds", 5);
        if (claimSeconds is < 10 or > 300 || pollSeconds is < 1 or > 60)
        {
            throw new InvalidOperationException("Email:Outbox timing is invalid.");
        }

        TimeSpan claimDuration = TimeSpan.FromSeconds(claimSeconds);
        TimeSpan heartbeatInterval = TimeSpan.FromTicks(claimDuration.Ticks / 3);
        try
        {
            return new EmailOutboxWorkerOptions(
                configuration["Email:Smtp:Host"]
                    ?? throw new InvalidOperationException("Email:Smtp:Host is required."),
                configuration.GetValue("Email:Smtp:Port", 587),
                security,
                configuration["Email:Smtp:Username"],
                configuration["Email:Smtp:Password"],
                configuration["Email:FromAddress"]
                    ?? throw new InvalidOperationException("Email:FromAddress is required."),
                configuration["Email:FromName"] ?? "PoolAI",
                configuration.GetValue("Email:Outbox:MaxAttempts", 8),
                TimeSpan.FromSeconds(pollSeconds),
                claimDuration,
                heartbeatInterval,
                SmtpOperationTimeout,
                RetryBaseDelay,
                RetryMaximumDelay);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("Email Worker configuration is invalid.", exception);
        }
    }

    private static void ValidateCredential(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Any(static character => character is '\r' or '\n' or '\0'))
        {
            throw new ArgumentException("SMTP credential is invalid.", parameterName);
        }
    }

    private static void Positive(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
