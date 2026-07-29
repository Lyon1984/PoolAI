using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Secrets;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Supply.Application.Ports;

namespace PoolAI.Modules.Supply.Infrastructure.Security;

internal sealed class AccountCredentialProtector : IAccountCredentialProtector
{
    private const string FailureEventName =
        "supply.account_credential_envelope_validation_failed";
    private static readonly TimeSpan AlertTimeout = TimeSpan.FromSeconds(5);
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly SecretEnvelopeV1 _envelope;
    private readonly SecretEnvelopeKeyRing _keyRing;
    private readonly IOperationalEventWriter _operationalEventWriter;

    internal AccountCredentialProtector(
        AccountCredentialEnvelopeOptions options,
        IOperationalEventWriter operationalEventWriter)
    {
        ArgumentNullException.ThrowIfNull(options);
        _keyRing = options.KeyRing;
        _envelope = new SecretEnvelopeV1(_keyRing);
        _operationalEventWriter = operationalEventWriter
            ?? throw new ArgumentNullException(nameof(operationalEventWriter));
    }

    public AccountCredentialProtection Protect(
        string credential,
        EntityId accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        byte[] plaintext;
        try
        {
            plaintext = StrictUtf8.GetBytes(credential);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException(
                "The Account credential is not valid Unicode text.",
                nameof(credential));
        }

        try
        {
            return new AccountCredentialProtection(
                _envelope.Encrypt(plaintext, Context(accountId)),
                _keyRing.CurrentKeyId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async ValueTask<AccountCredentialLease> UnprotectAsync(
        JsonElement envelope,
        EntityId accountId,
        CancellationToken cancellationToken)
    {
        byte[] plaintext;
        try
        {
            plaintext = _envelope.Decrypt(envelope, Context(accountId));
        }
        catch (SecretEnvelopeException exception)
        {
            await ReportAndThrowAsync(
                "decrypt",
                accountId,
                FailureCode(exception.Failure)).ConfigureAwait(false);
            throw new UnreachableException();
        }

        try
        {
            _ = StrictUtf8.GetCharCount(plaintext);
            cancellationToken.ThrowIfCancellationRequested();
            AccountCredentialLease lease = new(plaintext);
            plaintext = [];
            return lease;
        }
        catch (DecoderFallbackException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            await ReportAndThrowAsync(
                "decrypt",
                accountId,
                "invalid_plaintext").ConfigureAwait(false);
            throw new UnreachableException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async ValueTask<AccountCredentialRewrap> RewrapAsync(
        JsonElement envelope,
        EntityId accountId,
        CancellationToken cancellationToken)
    {
        SecretEnvelopeContext context = Context(accountId);
        await ValidateRewrapPlaintextAsync(
            envelope,
            context,
            accountId).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            SecretEnvelopeRewrapResult result = _envelope.Rewrap(
                envelope,
                context);
            return new AccountCredentialRewrap(
                result.Envelope,
                result.PreviousKeyId,
                result.Metadata.KeyId,
                result.Changed);
        }
        catch (SecretEnvelopeException exception)
        {
            await ReportAndThrowAsync(
                "rewrap",
                accountId,
                FailureCode(exception.Failure)).ConfigureAwait(false);
            throw new UnreachableException();
        }
    }

    private async ValueTask ValidateRewrapPlaintextAsync(
        JsonElement envelope,
        SecretEnvelopeContext context,
        EntityId accountId)
    {
        byte[] plaintext;
        try
        {
            plaintext = _envelope.Decrypt(envelope, context);
        }
        catch (SecretEnvelopeException exception)
        {
            await ReportAndThrowAsync(
                "rewrap",
                accountId,
                FailureCode(exception.Failure)).ConfigureAwait(false);
            throw new UnreachableException();
        }

        try
        {
            _ = StrictUtf8.GetCharCount(plaintext);
        }
        catch (DecoderFallbackException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            await ReportAndThrowAsync(
                "rewrap",
                accountId,
                "invalid_plaintext").ConfigureAwait(false);
            throw new UnreachableException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private async ValueTask ReportAndThrowAsync(
        string operation,
        EntityId accountId,
        string failure)
    {
        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            account_id = accountId.Value,
            operation,
            failure,
        });
        using CancellationTokenSource alertTimeout = new(AlertTimeout);
        try
        {
            await _operationalEventWriter.WriteAsync(
                FailureEventName,
                payload,
                alertTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            throw new CryptographicException(
                "Account credential envelope validation and security alerting failed.");
        }

        throw new CryptographicException(
            "Account credential envelope validation failed.");
    }

    private static SecretEnvelopeContext Context(EntityId accountId) =>
        new(
            "account-credential",
            "account",
            accountId.Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture),
            "credential_envelope");

    private static string FailureCode(SecretEnvelopeFailure failure) => failure switch
    {
        SecretEnvelopeFailure.InvalidDocument => "invalid_document",
        SecretEnvelopeFailure.UnsupportedVersion => "unsupported_version",
        SecretEnvelopeFailure.UnsupportedAlgorithm => "unsupported_algorithm",
        SecretEnvelopeFailure.UnknownKey => "unknown_key",
        SecretEnvelopeFailure.AuthenticationFailed => "authentication_failed",
        _ => "unknown_failure",
    };
}
