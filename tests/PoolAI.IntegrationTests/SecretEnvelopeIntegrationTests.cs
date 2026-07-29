#pragma warning disable MA0051 // Acceptance matrices intentionally keep each security scenario together.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PoolAI.Infrastructure.Secrets;

namespace PoolAI.IntegrationTests;

public sealed class SecretEnvelopeIntegrationTests
{
    private const string OldKeyId = "integration-old-v1";
    private const string CurrentKeyId = "integration-current-v2";
    private const string Credential = "integration-fake-account-credential";
    private const string AccountId = "019c13f0-a870-7f74-9ca5-17738484903a";

    [Fact]
    public void EnvelopeV1BindsPurposeEntityFieldAndKeyVersion()
    {
        byte[] oldKey = Key(0x21);
        byte[] currentKey = Key(0x22);
        SecretEnvelopeContext context = AccountContext(AccountId);
        SecretEnvelopeV1 oldRuntime = Runtime(OldKeyId, (OldKeyId, oldKey));
        JsonElement encrypted = oldRuntime.Encrypt(
            Encoding.UTF8.GetBytes(Credential),
            context);
        SecretEnvelopeV1 rotatingRuntime = Runtime(
            CurrentKeyId,
            (CurrentKeyId, currentKey),
            (OldKeyId, oldKey));

        Assert.Equal(OldKeyId, encrypted.GetProperty("kid").GetString());
        AssertPlaintext(rotatingRuntime, encrypted, context, Credential);
        AssertAuthenticationFailure(
            rotatingRuntime,
            encrypted,
            new SecretEnvelopeContext(
                "totp-secret",
                "account",
                AccountId,
                "credential_envelope"));
        AssertAuthenticationFailure(
            rotatingRuntime,
            encrypted,
            AccountContext("019c13f0-a870-7f74-9ca5-17738484903b"));
        AssertAuthenticationFailure(
            rotatingRuntime,
            encrypted,
            new SecretEnvelopeContext(
                "account-credential",
                "account",
                AccountId,
                "credential_hint"));

        AssertFailure(
            rotatingRuntime,
            Replace(encrypted, "v", JsonValue.Create(2)),
            context,
            SecretEnvelopeFailure.UnsupportedVersion);
        AssertFailure(
            rotatingRuntime,
            Replace(encrypted, "kid", JsonValue.Create("unknown-v9")),
            context,
            SecretEnvelopeFailure.UnknownKey);
        AssertFailure(
            rotatingRuntime,
            Replace(encrypted, "tag", JsonValue.Create(Base64Url(new byte[16]))),
            context,
            SecretEnvelopeFailure.AuthenticationFailed);
    }

    [Fact]
    public void CorrectAadDecryptsAndTamperUnknownKeyRotationAndRestoreFailClosed()
    {
        byte[] oldKey = Key(0x31);
        byte[] currentKey = Key(0x32);
        SecretEnvelopeContext context = AccountContext(AccountId);
        JsonElement beforeRotation = Runtime(
            OldKeyId,
            (OldKeyId, oldKey)).Encrypt(
            Encoding.UTF8.GetBytes(Credential),
            context);

        JsonElement restoredOldBackup = Restore(beforeRotation.GetRawText());
        SecretEnvelopeV1 rotatingRuntime = Runtime(
            CurrentKeyId,
            (CurrentKeyId, currentKey),
            (OldKeyId, oldKey));
        AssertPlaintext(
            rotatingRuntime,
            restoredOldBackup,
            context,
            Credential);

        SecretEnvelopeRewrapResult rewrapped = rotatingRuntime.Rewrap(
            restoredOldBackup,
            context);
        Assert.True(rewrapped.Changed);
        Assert.Equal(OldKeyId, rewrapped.PreviousKeyId);
        Assert.Equal(CurrentKeyId, rewrapped.Metadata.KeyId);
        foreach (string field in new[] { "ciphertext", "nonce", "tag" })
        {
            Assert.Equal(
                restoredOldBackup.GetProperty(field).GetString(),
                rewrapped.Envelope.GetProperty(field).GetString());
        }

        JsonElement restoredRewrappedBackup = Restore(rewrapped.Envelope.GetRawText());
        SecretEnvelopeV1 currentOnlyRuntime = Runtime(
            CurrentKeyId,
            (CurrentKeyId, currentKey));
        AssertPlaintext(
            currentOnlyRuntime,
            restoredRewrappedBackup,
            context,
            Credential);

        SecretEnvelopeException retiredKey = Assert.Throws<SecretEnvelopeException>(
            () => Runtime(OldKeyId, (OldKeyId, oldKey)).Decrypt(
                restoredRewrappedBackup,
                context));
        Assert.Equal(SecretEnvelopeFailure.UnknownKey, retiredKey.Failure);
        Assert.DoesNotContain(Credential, retiredKey.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(CurrentKeyId, retiredKey.ToString(), StringComparison.Ordinal);

        JsonElement tamperedBackup = Replace(
            restoredOldBackup,
            "tag",
            JsonValue.Create(Base64Url(new byte[16])));
        SecretEnvelopeException tampered = Assert.Throws<SecretEnvelopeException>(
            () => rotatingRuntime.Rewrap(tamperedBackup, context));
        Assert.Equal(
            SecretEnvelopeFailure.AuthenticationFailed,
            tampered.Failure);
    }

    private static SecretEnvelopeV1 Runtime(
        string currentKeyId,
        params (string KeyId, byte[] Key)[] keys) =>
        new(new SecretEnvelopeKeyRing(
            currentKeyId,
            keys.ToDictionary(
                static item => item.KeyId,
                static item => item.Key,
                StringComparer.Ordinal)));

    private static SecretEnvelopeContext AccountContext(string accountId) =>
        new(
            "account-credential",
            "account",
            accountId,
            "credential_envelope");

    private static byte[] Key(byte value) =>
        Enumerable.Repeat(value, SecretEnvelopeKeyRing.KeySize).ToArray();

    private static void AssertPlaintext(
        SecretEnvelopeV1 runtime,
        JsonElement envelope,
        SecretEnvelopeContext context,
        string expected)
    {
        byte[] plaintext = runtime.Decrypt(envelope, context);
        try
        {
            Assert.Equal(expected, Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void AssertAuthenticationFailure(
        SecretEnvelopeV1 runtime,
        JsonElement envelope,
        SecretEnvelopeContext context) =>
        AssertFailure(
            runtime,
            envelope,
            context,
            SecretEnvelopeFailure.AuthenticationFailed);

    private static void AssertFailure(
        SecretEnvelopeV1 runtime,
        JsonElement envelope,
        SecretEnvelopeContext context,
        SecretEnvelopeFailure expected)
    {
        SecretEnvelopeException exception = Assert.Throws<SecretEnvelopeException>(
            () => runtime.Decrypt(envelope, context));
        Assert.Equal(expected, exception.Failure);
        Assert.Equal("Secret envelope validation failed.", exception.Message);
        Assert.DoesNotContain(Credential, exception.ToString(), StringComparison.Ordinal);
    }

    private static JsonElement Replace(
        JsonElement source,
        string propertyName,
        JsonNode? value)
    {
        JsonObject root = JsonNode.Parse(source.GetRawText())!.AsObject();
        root[propertyName] = value;
        return Restore(root.ToJsonString());
    }

    private static JsonElement Restore(string json)
    {
        byte[] backupBytes = Encoding.UTF8.GetBytes(json);
        try
        {
            using JsonDocument document = JsonDocument.Parse(backupBytes);
            return document.RootElement.Clone();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(backupBytes);
        }
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
#pragma warning restore MA0051
