using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using PoolAI.Modules.Identity.Worker;

namespace PoolAI.Modules.Identity.Infrastructure.Email;

internal sealed class SmtpEmailTransport : IEmailTransport
{
    private const int MaximumReplyLines = 50;
    private const int MaximumReplyLineLength = 2_048;
    private static readonly Encoding SmtpEncoding = new UTF8Encoding(false, true);
    private readonly EmailOutboxWorkerOptions _options;
    private readonly RemoteCertificateValidationCallback? _certificateValidationCallback;

    internal SmtpEmailTransport(EmailOutboxWorkerOptions options)
        : this(options, certificateValidationCallback: null)
    {
    }

    internal SmtpEmailTransport(
        EmailOutboxWorkerOptions options,
        RemoteCertificateValidationCallback? certificateValidationCallback)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _certificateValidationCallback = certificateValidationCallback;
    }

    public async ValueTask<EmailTransportResult> SendAsync(
        EmailTransportMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        using CancellationTokenSource timeoutSource = new(_options.SmtpTimeout);
        using CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        CancellationToken operationToken = linkedSource.Token;
        try
        {
            return await SendCoreAsync(message, operationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        {
            return EmailTransportResult.Transient(EmailDeliveryFailureClass.Timeout);
        }
        catch (AuthenticationException)
        {
            return EmailTransportResult.Transient(EmailDeliveryFailureClass.Tls);
        }
        catch (SocketException exception)
        {
            EmailDeliveryFailureClass failureClass = exception.SocketErrorCode is
                SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain
                ? EmailDeliveryFailureClass.Dns
                : EmailDeliveryFailureClass.Network;
            return EmailTransportResult.Transient(failureClass);
        }
        catch (TimeoutException)
        {
            return EmailTransportResult.Transient(EmailDeliveryFailureClass.Timeout);
        }
        catch (IOException)
        {
            return EmailTransportResult.Transient(EmailDeliveryFailureClass.Network);
        }
        catch (SmtpProtocolException)
        {
            return EmailTransportResult.Transient(EmailDeliveryFailureClass.SmtpProtocol);
        }
        catch (DecoderFallbackException)
        {
            return EmailTransportResult.Transient(EmailDeliveryFailureClass.SmtpProtocol);
        }
        catch (ArgumentException)
        {
            return EmailTransportResult.Permanent(EmailDeliveryFailureClass.InvalidMessage);
        }
    }

    private async ValueTask<EmailTransportResult> SendCoreAsync(
        EmailTransportMessage message,
        CancellationToken cancellationToken)
    {
        using TcpClient client = new() { NoDelay = true };
        await client.ConnectAsync(
            _options.SmtpHost,
            _options.SmtpPort,
            cancellationToken).ConfigureAwait(false);
        NetworkStream networkStream = client.GetStream();
        await using (networkStream.ConfigureAwait(false))
        {
            if (_options.SmtpSecurity is not SmtpSecurityMode.ImplicitTls)
            {
                return await RunSessionAsync(
                    networkStream,
                    message,
                    allowStartTls: true,
                    cancellationToken).ConfigureAwait(false);
            }

            SslStream secureStream = await AuthenticateTlsAsync(
                networkStream,
                cancellationToken).ConfigureAwait(false);
            await using (secureStream.ConfigureAwait(false))
            {
                return await RunSessionAsync(
                    secureStream,
                    message,
                    allowStartTls: false,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<EmailTransportResult> RunSessionAsync(
        Stream stream,
        EmailTransportMessage message,
        bool allowStartTls,
        CancellationToken cancellationToken)
    {
        using SmtpSession session = new(stream);
        EmailTransportResult? failure = await ExpectAsync(
            session.ReadReplyAsync(cancellationToken),
            IsGreetingSuccess,
            EmailDeliveryFailureClass.Smtp5xx,
            cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return failure;
        }

        SmtpReply ehloReply = await session.CommandAsync(
            "EHLO poolai.local",
            cancellationToken).ConfigureAwait(false);
        failure = ClassifyReply(ehloReply, IsCommandSuccess);
        if (failure is not null)
        {
            return failure;
        }

        if (!allowStartTls)
        {
            return await AuthenticateAndDeliverAsync(
                session,
                ehloReply,
                message,
                cancellationToken).ConfigureAwait(false);
        }

        if (!ehloReply.HasCapability("STARTTLS"))
        {
            return EmailTransportResult.Permanent(EmailDeliveryFailureClass.SmtpCapability);
        }

        failure = ClassifyReply(
            await session.CommandAsync("STARTTLS", cancellationToken).ConfigureAwait(false),
            IsStartTlsSuccess);
        if (failure is not null)
        {
            return failure;
        }

        session.Dispose();
        return await UpgradeToTlsAndDeliverAsync(
            stream,
            message,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<EmailTransportResult> UpgradeToTlsAndDeliverAsync(
        Stream stream,
        EmailTransportMessage message,
        CancellationToken cancellationToken)
    {
        SslStream secureStream = await AuthenticateTlsAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        await using (secureStream.ConfigureAwait(false))
        {
            using SmtpSession secureSession = new(secureStream);
            SmtpReply ehloReply = await secureSession.CommandAsync(
                "EHLO poolai.local",
                cancellationToken).ConfigureAwait(false);
            EmailTransportResult? failure = ClassifyReply(ehloReply, IsCommandSuccess);
            if (failure is not null)
            {
                return failure;
            }

            return await AuthenticateAndDeliverAsync(
                secureSession,
                ehloReply,
                message,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<EmailTransportResult> AuthenticateAndDeliverAsync(
        SmtpSession session,
        SmtpReply ehloReply,
        EmailTransportMessage message,
        CancellationToken cancellationToken)
    {
        if (_options.SmtpUsername is not null)
        {
            if (!ehloReply.HasAuthLoginCapability())
            {
                return EmailTransportResult.Permanent(
                    EmailDeliveryFailureClass.SmtpCapability);
            }

            EmailTransportResult? authFailure = await AuthenticateAsync(
                session,
                cancellationToken).ConfigureAwait(false);
            if (authFailure is not null)
            {
                return authFailure;
            }
        }

        EmailTransportResult? failure = await BeginEnvelopeAsync(
            session,
            message,
            cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return failure;
        }

        await session.WriteMessageAsync(BuildMimeMessage(message), cancellationToken)
            .ConfigureAwait(false);
        failure = ClassifyReply(
            await session.ReadReplyAsync(cancellationToken).ConfigureAwait(false),
            IsDeliverySuccess);
        if (failure is not null)
        {
            return failure;
        }

        return EmailTransportResult.Sent;
    }

    private static async ValueTask<EmailTransportResult?> BeginEnvelopeAsync(
        SmtpSession session,
        EmailTransportMessage message,
        CancellationToken cancellationToken)
    {
        EmailTransportResult? failure = ClassifyReply(
            await session.CommandAsync(
                string.Concat("MAIL FROM:<", message.FromAddress, ">"),
                cancellationToken).ConfigureAwait(false),
            IsCommandSuccess);
        if (failure is not null)
        {
            return failure;
        }

        SmtpReply recipientReply = await session.CommandAsync(
            string.Concat("RCPT TO:<", message.Recipient, ">"),
            cancellationToken).ConfigureAwait(false);
        if (recipientReply.Code is >= 500 and <= 599)
        {
            return EmailTransportResult.Permanent(EmailDeliveryFailureClass.InvalidRecipient);
        }

        failure = ClassifyReply(recipientReply, IsRecipientSuccess);
        if (failure is not null)
        {
            return failure;
        }

        failure = ClassifyReply(
            await session.CommandAsync("DATA", cancellationToken).ConfigureAwait(false),
            IsDataReady);
        if (failure is not null)
        {
            return failure;
        }

        return null;
    }

    private async ValueTask<EmailTransportResult?> AuthenticateAsync(
        SmtpSession session,
        CancellationToken cancellationToken)
    {
        EmailTransportResult? failure = ClassifyReply(
            await session.CommandAsync("AUTH LOGIN", cancellationToken).ConfigureAwait(false),
            IsAuthenticationChallenge);
        if (failure is not null)
        {
            return failure;
        }

        failure = ClassifyReply(
            await session.CommandAsync(
                Convert.ToBase64String(SmtpEncoding.GetBytes(_options.SmtpUsername!)),
                cancellationToken).ConfigureAwait(false),
            IsAuthenticationChallenge);
        if (failure is not null)
        {
            return failure;
        }

        return ClassifyReply(
            await session.CommandAsync(
                Convert.ToBase64String(SmtpEncoding.GetBytes(_options.SmtpPassword!)),
                cancellationToken).ConfigureAwait(false),
            IsAuthenticationSuccess);
    }

    private async ValueTask<SslStream> AuthenticateTlsAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        SslStream secureStream = new(
            stream,
            leaveInnerStreamOpen: true,
            _certificateValidationCallback);
        try
        {
            await secureStream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = _options.SmtpHost,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates
                        .X509RevocationMode.Online,
                },
                cancellationToken).ConfigureAwait(false);
            return secureStream;
        }
        catch (IOException exception)
        {
            await secureStream.DisposeAsync().ConfigureAwait(false);
            throw new AuthenticationException("SMTP TLS negotiation failed.", exception);
        }
        catch
        {
            await secureStream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<EmailTransportResult?> ExpectAsync(
        ValueTask<SmtpReply> replyTask,
        Func<int, bool> successPredicate,
        EmailDeliveryFailureClass permanentFailureClass,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SmtpReply reply = await replyTask.ConfigureAwait(false);
        return ClassifyReply(reply, successPredicate, permanentFailureClass);
    }

    private static EmailTransportResult? ClassifyReply(
        SmtpReply reply,
        Func<int, bool> successPredicate,
        EmailDeliveryFailureClass permanentFailureClass = EmailDeliveryFailureClass.Smtp5xx)
    {
        if (successPredicate(reply.Code))
        {
            return null;
        }

        return reply.Code switch
        {
            >= 400 and <= 499 => EmailTransportResult.Transient(
                EmailDeliveryFailureClass.Smtp4xx),
            >= 500 and <= 599 => EmailTransportResult.Permanent(permanentFailureClass),
            _ => EmailTransportResult.Transient(EmailDeliveryFailureClass.SmtpProtocol),
        };
    }

    private static string BuildMimeMessage(EmailTransportMessage message)
    {
        string displayName = EmailHeaderValueValidator.EncodeDisplayName(message.FromName);
        string subject = EmailHeaderValueValidator.EncodeDisplayName(message.Subject);
        string encodedBody = Convert.ToBase64String(SmtpEncoding.GetBytes(message.Body));
        StringBuilder builder = new();
        builder.Append("From: ").Append(displayName).Append(" <")
            .Append(message.FromAddress).Append(">\r\n")
            .Append("To: <").Append(message.Recipient).Append(">\r\n")
            .Append("Subject: ").Append(subject).Append("\r\n")
            .Append("Message-ID: ").Append(message.MessageId).Append("\r\n")
            .Append("MIME-Version: 1.0\r\n")
            .Append("Content-Type: text/plain; charset=UTF-8\r\n")
            .Append("Content-Transfer-Encoding: base64\r\n\r\n");
        for (int index = 0; index < encodedBody.Length; index += 76)
        {
            int length = Math.Min(76, encodedBody.Length - index);
            builder.Append(encodedBody, index, length).Append("\r\n");
        }

        builder.Append(".\r\n");
        return builder.ToString();
    }

    private static bool IsGreetingSuccess(int code) => code == 220;

    private static bool IsCommandSuccess(int code) => code is 250 or 251;

    private static bool IsDeliverySuccess(int code) => code is >= 200 and <= 299;

    private static bool IsRecipientSuccess(int code) => code is 250 or 251 or 252;

    private static bool IsStartTlsSuccess(int code) => code == 220;

    private static bool IsDataReady(int code) => code == 354;

    private static bool IsAuthenticationChallenge(int code) => code == 334;

    private static bool IsAuthenticationSuccess(int code) => code == 235;

    private sealed class SmtpSession : IDisposable
    {
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private bool _disposed;

        internal SmtpSession(Stream stream)
        {
            _reader = new StreamReader(
                stream,
                SmtpEncoding,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 1_024,
                leaveOpen: true);
            _writer = new StreamWriter(
                stream,
                SmtpEncoding,
                bufferSize: 1_024,
                leaveOpen: true)
            {
                NewLine = "\r\n",
                AutoFlush = true,
            };
        }

        internal async ValueTask<SmtpReply> CommandAsync(
            string command,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _writer.WriteLineAsync(command.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return await ReadReplyAsync(cancellationToken).ConfigureAwait(false);
        }

        internal async ValueTask WriteMessageAsync(
            string value,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _writer.WriteAsync(value.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        internal async ValueTask<SmtpReply> ReadReplyAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            List<string> lines = [];
            int expectedCode = 0;
            for (int index = 0; index < MaximumReplyLines; index++)
            {
                string line = await ReadBoundedLineAsync(cancellationToken).ConfigureAwait(false);
                if (line.Length < 3
                    || !int.TryParse(
                        line.AsSpan(0, 3),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int code)
                    || code is < 100 or > 599
                    || index > 0 && code != expectedCode)
                {
                    throw new SmtpProtocolException();
                }

                expectedCode = code;
                lines.Add(line);
                if (line.Length == 3 || line[3] == ' ')
                {
                    return new SmtpReply(code, lines);
                }

                if (line[3] != '-')
                {
                    throw new SmtpProtocolException();
                }
            }

            throw new SmtpProtocolException();
        }

        private async ValueTask<string> ReadBoundedLineAsync(
            CancellationToken cancellationToken)
        {
            char[] lineBuffer = new char[MaximumReplyLineLength];
            char[] characterBuffer = new char[1];
            int length = 0;
            bool sawCarriageReturn = false;
            while (true)
            {
                int read = await _reader.ReadAsync(
                    characterBuffer.AsMemory(),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new SmtpProtocolException();
                }

                char character = characterBuffer[0];
                if (sawCarriageReturn)
                {
                    if (character != '\n')
                    {
                        throw new SmtpProtocolException();
                    }

                    return new string(lineBuffer, 0, length);
                }

                if (character == '\r')
                {
                    sawCarriageReturn = true;
                    continue;
                }

                if (character == '\n' || length == lineBuffer.Length)
                {
                    throw new SmtpProtocolException();
                }

                lineBuffer[length++] = character;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer.Dispose();
            _reader.Dispose();
        }
    }

    private sealed record SmtpReply(int Code, IReadOnlyList<string> Lines)
    {
        internal bool HasCapability(string capability) => Lines.Any(line =>
        {
            if (line.Length <= 4)
            {
                return false;
            }

            ReadOnlySpan<char> text = line.AsSpan(4).Trim();
            return text.Equals(capability, StringComparison.OrdinalIgnoreCase)
                || text.StartsWith(
                    string.Concat(capability, " "),
                    StringComparison.OrdinalIgnoreCase);
        });

        internal bool HasAuthLoginCapability() => Lines.Any(static line =>
        {
            if (line.Length <= 4)
            {
                return false;
            }

            ReadOnlySpan<char> text = line.AsSpan(4).TrimStart();
            if (!text.StartsWith("AUTH ", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return text[5..].ToString().Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains("LOGIN", StringComparer.OrdinalIgnoreCase);
        });
    }

    private sealed class SmtpProtocolException : Exception
    {
    }
}
