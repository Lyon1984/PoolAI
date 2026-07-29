#pragma warning disable MA0051 // Security matrix tests intentionally keep each contract scenario together.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Secrets;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Supply.Application.Ports;
using PoolAI.Modules.Supply.Infrastructure.Security;

namespace PoolAI.UnitTests;

public sealed class SecretEnvelopeV1Tests
{
    private const string CurrentKeyId = "unit-current-v2";
    private const string OldKeyId = "unit-old-v1";
    private const string Credential = "deterministic-account-credential";
    private static readonly EntityId AccountId = new(Guid.Parse(
        "019c13e8-9a5b-7b87-a63f-6e55a2785dc1"));

    [Fact]
    public void EnvelopeV1RejectsMalformedUnknownAndTamperedDocuments()
    {
        SecretEnvelopeV1 envelope = Envelope(CurrentKeyId, Key(0x31));
        SecretEnvelopeContext context = AccountContext(AccountId);
        JsonElement encrypted = envelope.Encrypt(Encoding.UTF8.GetBytes(Credential), context);

        AssertFailure(
            envelope,
            Remove(encrypted, "tag"),
            context,
            SecretEnvelopeFailure.InvalidDocument);
        AssertFailure(
            envelope,
            Add(encrypted, "unexpected", JsonValue.Create(true)),
            context,
            SecretEnvelopeFailure.InvalidDocument);
        AssertFailure(
            envelope,
            DuplicateVersion(encrypted),
            context,
            SecretEnvelopeFailure.InvalidDocument);
        AssertFailure(
            envelope,
            Replace(encrypted, "v", JsonValue.Create(2)),
            context,
            SecretEnvelopeFailure.UnsupportedVersion);
        AssertFailure(
            envelope,
            Replace(encrypted, "alg", JsonValue.Create("A256GCM")),
            context,
            SecretEnvelopeFailure.UnsupportedAlgorithm);
        AssertFailure(
            envelope,
            Replace(encrypted, "kid", JsonValue.Create("unknown-key")),
            context,
            SecretEnvelopeFailure.UnknownKey);
        AssertFailure(
            envelope,
            Replace(encrypted, "nonce", JsonValue.Create("invalid+base64")),
            context,
            SecretEnvelopeFailure.InvalidDocument);
        AssertFailure(
            envelope,
            Replace(encrypted, "tag", JsonValue.Create(Base64Url(new byte[16]))),
            context,
            SecretEnvelopeFailure.AuthenticationFailed);
        AssertFailure(
            envelope,
            Replace(encrypted, "wrap_tag", JsonValue.Create(Base64Url(new byte[16]))),
            context,
            SecretEnvelopeFailure.AuthenticationFailed);
        AssertFailure(
            envelope,
            encrypted,
            new SecretEnvelopeContext(
                "totp-secret",
                "account",
                AccountId.Value.ToString("D"),
                "credential_envelope"),
            SecretEnvelopeFailure.AuthenticationFailed);

        Assert.Throws<ArgumentException>(() =>
            new SecretEnvelopeContext(
                "account-credential\ud800",
                "account",
                AccountId.Value.ToString("D"),
                "credential_envelope"));
        Assert.Throws<ArgumentException>(() =>
            new SecretEnvelopeContext(
                "account-credential\udc00",
                "account",
                AccountId.Value.ToString("D"),
                "credential_envelope"));
        ArgumentException invalidContext = Assert.Throws<ArgumentException>(() =>
            new SecretEnvelopeContext(
                "account-credential\ud800",
                "account",
                AccountId.Value.ToString("D"),
                "credential_envelope"));
        Assert.Null(invalidContext.InnerException);
        AssertFailure(
            envelope,
            encrypted,
            new SecretEnvelopeContext(
                "account-credential",
                "account",
                EntityId.New().Value.ToString("D"),
                "credential_envelope"),
            SecretEnvelopeFailure.AuthenticationFailed);
        AssertFailure(
            envelope,
            encrypted,
            new SecretEnvelopeContext(
                "account-credential",
                "account",
                AccountId.Value.ToString("D"),
                "other_field"),
            SecretEnvelopeFailure.AuthenticationFailed);
    }

    [Fact]
    public void EnvelopeV1RewrapsOnlyDekAndPreservesCiphertext()
    {
        byte[] oldKey = Key(0x41);
        byte[] currentKey = Key(0x42);
        SecretEnvelopeContext context = AccountContext(AccountId);
        JsonElement oldEnvelope = Envelope(OldKeyId, oldKey).Encrypt(
            Encoding.UTF8.GetBytes(Credential),
            context);
        SecretEnvelopeV1 rotatingEnvelope = Envelope(
            CurrentKeyId,
            currentKey,
            (OldKeyId, oldKey));

        SecretEnvelopeRewrapResult result = rotatingEnvelope.Rewrap(
            oldEnvelope,
            context);

        Assert.True(result.Changed);
        Assert.Equal(OldKeyId, result.PreviousKeyId);
        Assert.Equal(CurrentKeyId, result.Metadata.KeyId);
        Assert.Equal(nameof(SecretEnvelopeRewrapResult), result.ToString());
        Assert.Equal(nameof(SecretEnvelopeMetadata), result.Metadata.ToString());
        Assert.Equal(
            StringField(oldEnvelope, "ciphertext"),
            StringField(result.Envelope, "ciphertext"));
        Assert.Equal(
            StringField(oldEnvelope, "nonce"),
            StringField(result.Envelope, "nonce"));
        Assert.Equal(
            StringField(oldEnvelope, "tag"),
            StringField(result.Envelope, "tag"));
        Assert.NotEqual(
            StringField(oldEnvelope, "wrapped_dek"),
            StringField(result.Envelope, "wrapped_dek"));
        Assert.NotEqual(
            StringField(oldEnvelope, "wrap_nonce"),
            StringField(result.Envelope, "wrap_nonce"));

        byte[] plaintext = Envelope(CurrentKeyId, currentKey).Decrypt(
            result.Envelope,
            context);
        try
        {
            Assert.Equal(Credential, Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        SecretEnvelopeException retiredKeyFailure = Assert.Throws<SecretEnvelopeException>(
            () => Envelope(OldKeyId, oldKey).Decrypt(result.Envelope, context));
        Assert.Equal(SecretEnvelopeFailure.UnknownKey, retiredKeyFailure.Failure);

        SecretEnvelopeRewrapResult noChange = Envelope(
            CurrentKeyId,
            currentKey).Rewrap(result.Envelope, context);
        Assert.False(noChange.Changed);
        Assert.Equal(result.Envelope.GetRawText(), noChange.Envelope.GetRawText());

        JsonElement tamperedContent = Replace(
            oldEnvelope,
            "tag",
            JsonValue.Create(Base64Url(new byte[16])));
        SecretEnvelopeException contentFailure = Assert.Throws<SecretEnvelopeException>(
            () => rotatingEnvelope.Rewrap(tamperedContent, context));
        Assert.Equal(
            SecretEnvelopeFailure.AuthenticationFailed,
            contentFailure.Failure);
    }

    [Fact]
    public async Task AccountCredentialEnvelopeUsesCanonicalAadAndFreshCryptographicMaterial()
    {
        RecordingOperationalEventWriter events = new();
        byte[] key = Key(0x51);
        SecretEnvelopeKeyRing keyRing = Ring(CurrentKeyId, key);
        AccountCredentialProtector protector = new(
            new AccountCredentialEnvelopeOptions(keyRing),
            events);

        var first = protector.Protect(Credential, AccountId);
        var replacement = protector.Protect(Credential, AccountId);

        Assert.Equal(CurrentKeyId, first.KeyId);
        Assert.Equal("AccountCredentialProtection", first.ToString());
        Assert.DoesNotContain(
            StringField(first.Envelope, "ciphertext"),
            first.ToString(),
            StringComparison.Ordinal);
        Assert.NotEqual(
            StringField(first.Envelope, "wrapped_dek"),
            StringField(replacement.Envelope, "wrapped_dek"));
        Assert.NotEqual(
            StringField(first.Envelope, "wrap_nonce"),
            StringField(replacement.Envelope, "wrap_nonce"));
        Assert.NotEqual(
            StringField(first.Envelope, "nonce"),
            StringField(replacement.Envelope, "nonce"));
        Assert.NotEqual(
            StringField(first.Envelope, "ciphertext"),
            StringField(replacement.Envelope, "ciphertext"));

        using AccountCredentialLease decrypted = await protector.UnprotectAsync(
            first.Envelope,
            AccountId,
            CancellationToken.None);
        Assert.Equal(
            Credential,
            decrypted.Use(static credential => Encoding.UTF8.GetString(credential)));
        Assert.Equal(nameof(AccountCredentialLease), decrypted.ToString());
        Assert.Empty(events.Events);

        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            protector.UnprotectAsync(
                    first.Envelope,
                    AccountId,
                    canceled.Token)
                .AsTask());
        Assert.Empty(events.Events);

        SecretEnvelopeV1 sharedRuntime = new(keyRing);
        SecretEnvelopeMetadata metadata = sharedRuntime.Inspect(
            first.Envelope,
            AccountContext(AccountId));
        Assert.Equal(CurrentKeyId, metadata.KeyId);
        Assert.Equal(nameof(SecretEnvelopeMetadata), metadata.ToString());
        Assert.Equal(
            nameof(SecretEnvelopeContext),
            AccountContext(AccountId).ToString());
        SecretEnvelopeException wrongField = Assert.Throws<SecretEnvelopeException>(() =>
            sharedRuntime.Inspect(
                first.Envelope,
                new SecretEnvelopeContext(
                    "account-credential",
                    "account",
                    AccountId.Value.ToString("D"),
                    "credential_hint")));
        Assert.Equal(
            SecretEnvelopeFailure.AuthenticationFailed,
            wrongField.Failure);
    }

    [Fact]
    public async Task AccountCredentialFailuresAlertWithoutSecretMaterial()
    {
        RecordingOperationalEventWriter events = new();
        byte[] key = Key(0x61);
        AccountCredentialProtector protector = new(
            new AccountCredentialEnvelopeOptions(Ring(CurrentKeyId, key)),
            events);
        JsonElement encrypted = protector.Protect(Credential, AccountId).Envelope;
        JsonElement tampered = Replace(
            encrypted,
            "tag",
            JsonValue.Create(Base64Url(new byte[16])));

        CryptographicException failure = await Assert.ThrowsAsync<CryptographicException>(
            () => protector.UnprotectAsync(
                    tampered,
                    AccountId,
                    CancellationToken.None)
                .AsTask());

        Assert.Equal(
            "Account credential envelope validation failed.",
            failure.Message);
        (string eventName, JsonElement payload) = Assert.Single(events.Events);
        Assert.Equal(
            "supply.account_credential_envelope_validation_failed",
            eventName);
        Assert.Equal(
            AccountId.Value,
            payload.GetProperty("account_id").GetGuid());
        Assert.Equal("decrypt", payload.GetProperty("operation").GetString());
        Assert.Equal(
            "authentication_failed",
            payload.GetProperty("failure").GetString());
        string serializedPayload = payload.GetRawText();
        Assert.DoesNotContain(Credential, serializedPayload, StringComparison.Ordinal);
        Assert.DoesNotContain(CurrentKeyId, serializedPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("ciphertext", serializedPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("wrapped_dek", serializedPayload, StringComparison.Ordinal);

        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        CryptographicException canceledFailure =
            await Assert.ThrowsAsync<CryptographicException>(() =>
                protector.UnprotectAsync(
                        tampered,
                        AccountId,
                        canceled.Token)
                    .AsTask());
        Assert.Equal(
            "Account credential envelope validation failed.",
            canceledFailure.Message);
        Assert.Equal(2, events.Events.Count);
        Assert.Equal(
            "decrypt",
            events.Events[^1].Payload.GetProperty("operation").GetString());
    }

    [Fact]
    public void AccountCredentialConfigurationRequiresAnExactDistinctCurrentRing()
    {
        string current = Convert.ToBase64String(Key(0x62));
        string historical = Convert.ToBase64String(Key(0x63));
        AccountCredentialEnvelopeOptions options =
            AccountCredentialEnvelopeOptions.FromConfiguration(Configuration(
                ("Secrets:Envelope:CurrentKeyId", CurrentKeyId),
                ("Secrets:Envelope:CurrentKey", current),
                ($"Secrets:Envelope:DecryptKeyRing:{CurrentKeyId}", current),
                ($"Secrets:Envelope:DecryptKeyRing:{OldKeyId}", historical)));
        Assert.Equal(CurrentKeyId, options.KeyRing.CurrentKeyId);

        Assert.Throws<InvalidOperationException>(() =>
            AccountCredentialEnvelopeOptions.FromConfiguration(Configuration(
                ("Secrets:Envelope:CurrentKey", current),
                ($"Secrets:Envelope:DecryptKeyRing:{CurrentKeyId}", current))));
        Assert.Throws<InvalidOperationException>(() =>
            AccountCredentialEnvelopeOptions.FromConfiguration(Configuration(
                ("Secrets:Envelope:CurrentKeyId", CurrentKeyId),
                ("Secrets:Envelope:CurrentKey", "not-base64"),
                ($"Secrets:Envelope:DecryptKeyRing:{CurrentKeyId}", current))));
        Assert.Throws<InvalidOperationException>(() =>
            AccountCredentialEnvelopeOptions.FromConfiguration(Configuration(
                ("Secrets:Envelope:CurrentKeyId", CurrentKeyId),
                ("Secrets:Envelope:CurrentKey", current),
                ($"Secrets:Envelope:DecryptKeyRing:{OldKeyId}", historical))));
        Assert.Throws<InvalidOperationException>(() =>
            AccountCredentialEnvelopeOptions.FromConfiguration(Configuration(
                ("Secrets:Envelope:CurrentKeyId", CurrentKeyId),
                ("Secrets:Envelope:CurrentKey", current),
                ($"Secrets:Envelope:DecryptKeyRing:{CurrentKeyId}", historical))));
        Assert.Throws<InvalidOperationException>(() =>
            AccountCredentialEnvelopeOptions.FromConfiguration(Configuration(
                ("Secrets:Envelope:CurrentKeyId", CurrentKeyId),
                ("Secrets:Envelope:CurrentKey", current),
                ($"Secrets:Envelope:DecryptKeyRing:{CurrentKeyId}", current),
                ($"Secrets:Envelope:DecryptKeyRing:{OldKeyId}", current))));
        Assert.Throws<InvalidOperationException>(() =>
            AccountCredentialEnvelopeOptions.FromConfiguration(Configuration(
                ("Secrets:Envelope:CurrentKeyId", CurrentKeyId),
                ("Secrets:Envelope:CurrentKey", current),
                ($"Secrets:Envelope:DecryptKeyRing:{CurrentKeyId}", current),
                ($"Secrets:Envelope:DecryptKeyRing:{OldKeyId}", "AA=="))));
    }

    [Fact]
    public async Task AccountCredentialRewrapAndEveryFailurePathStayFailClosed()
    {
        byte[] oldKey = Key(0x64);
        byte[] currentKey = Key(0x65);
        RecordingOperationalEventWriter events = new();
        AccountCredentialProtector oldProtector = new(
            new AccountCredentialEnvelopeOptions(Ring(OldKeyId, oldKey)),
            events);
        JsonElement oldEnvelope = oldProtector.Protect(Credential, AccountId).Envelope;
        SecretEnvelopeKeyRing rotatingRing = new(
            CurrentKeyId,
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [CurrentKeyId] = currentKey,
                [OldKeyId] = oldKey,
            });
        AccountCredentialProtector protector = new(
            new AccountCredentialEnvelopeOptions(rotatingRing),
            events);

        var rewrapped = await protector.RewrapAsync(
            oldEnvelope,
            AccountId,
            CancellationToken.None);
        Assert.True(rewrapped.Changed);
        Assert.Equal(OldKeyId, rewrapped.PreviousKeyId);
        Assert.Equal(CurrentKeyId, rewrapped.CurrentKeyId);
        Assert.Equal("AccountCredentialRewrap", rewrapped.ToString());
        Assert.DoesNotContain(
            StringField(rewrapped.Envelope, "ciphertext"),
            rewrapped.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(
            StringField(oldEnvelope, "ciphertext"),
            StringField(rewrapped.Envelope, "ciphertext"));

        var failures = new[]
        {
            (Envelope: Remove(oldEnvelope, "tag"), Code: "invalid_document"),
            (Envelope: Replace(oldEnvelope, "v", JsonValue.Create(2)), Code: "unsupported_version"),
            (Envelope: Replace(oldEnvelope, "alg", JsonValue.Create("A256GCM")), Code: "unsupported_algorithm"),
            (Envelope: Replace(oldEnvelope, "kid", JsonValue.Create("unknown-key")), Code: "unknown_key"),
            (Envelope: Replace(oldEnvelope, "tag", JsonValue.Create(Base64Url(new byte[16]))), Code: "authentication_failed"),
        };
        foreach ((JsonElement envelope, string code) in failures)
        {
            CryptographicException exception =
                await Assert.ThrowsAsync<CryptographicException>(() =>
                    protector.RewrapAsync(
                            envelope,
                            AccountId,
                            CancellationToken.None)
                        .AsTask());
            Assert.Equal(
                "Account credential envelope validation failed.",
                exception.Message);
            Assert.Equal(code, events.Events[^1].Payload
                .GetProperty("failure")
                .GetString());
            Assert.Equal(
                "rewrap",
                events.Events[^1].Payload.GetProperty("operation").GetString());
        }

        JsonElement invalidUtf8 = new SecretEnvelopeV1(rotatingRing).Encrypt(
            new byte[] { 0xff },
            AccountContext(AccountId));
        CryptographicException invalidUtf8Unprotect =
            await Assert.ThrowsAsync<CryptographicException>(() =>
                protector.UnprotectAsync(
                        invalidUtf8,
                        AccountId,
                        CancellationToken.None)
                    .AsTask());
        Assert.Null(invalidUtf8Unprotect.InnerException);
        Assert.DoesNotContain(
            "DecoderFallbackException",
            invalidUtf8Unprotect.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "BytesUnknown",
            invalidUtf8Unprotect.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("FF", invalidUtf8Unprotect.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("255", invalidUtf8Unprotect.ToString(), StringComparison.Ordinal);
        Assert.Equal(
            "invalid_plaintext",
            events.Events[^1].Payload.GetProperty("failure").GetString());
        CryptographicException invalidUtf8Rewrap =
            await Assert.ThrowsAsync<CryptographicException>(() =>
                protector.RewrapAsync(
                        invalidUtf8,
                        AccountId,
                        CancellationToken.None)
                    .AsTask());
        Assert.Null(invalidUtf8Rewrap.InnerException);
        Assert.DoesNotContain(
            "DecoderFallbackException",
            invalidUtf8Rewrap.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "BytesUnknown",
            invalidUtf8Rewrap.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("FF", invalidUtf8Rewrap.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("255", invalidUtf8Rewrap.ToString(), StringComparison.Ordinal);
        Assert.Equal(
            "rewrap",
            events.Events[^1].Payload.GetProperty("operation").GetString());
        Assert.Equal(
            "invalid_plaintext",
            events.Events[^1].Payload.GetProperty("failure").GetString());

        Assert.Throws<ArgumentException>(() =>
            protector.Protect("\ud800", AccountId));
        Assert.Throws<ArgumentException>(() =>
            protector.Protect(" ", AccountId));

        AccountCredentialProtector alertFailureProtector = new(
            new AccountCredentialEnvelopeOptions(rotatingRing),
            new ThrowingOperationalEventWriter());
        CryptographicException alertFailure =
            await Assert.ThrowsAsync<CryptographicException>(() =>
                alertFailureProtector.UnprotectAsync(
                        Replace(
                            rewrapped.Envelope,
                            "tag",
                            JsonValue.Create(Base64Url(new byte[16]))),
                        AccountId,
                        CancellationToken.None)
                    .AsTask());
        Assert.Equal(
            "Account credential envelope validation and security alerting failed.",
            alertFailure.Message);
        Assert.Null(alertFailure.InnerException);
        Assert.DoesNotContain(Credential, alertFailure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(CurrentKeyId, alertFailure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Deterministic alert sink failure.",
            alertFailure.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void EnvelopeV1EnforcesPlaintextAndBinaryDocumentBounds()
    {
        SecretEnvelopeV1 envelope = Envelope(CurrentKeyId, Key(0x66));
        SecretEnvelopeContext context = AccountContext(AccountId);
        JsonElement encrypted = envelope.Encrypt(
            Encoding.UTF8.GetBytes(Credential),
            context);

        Assert.Throws<ArgumentException>(() =>
            envelope.Encrypt(Array.Empty<byte>(), context));
        Assert.Throws<ArgumentException>(() =>
            envelope.Encrypt(new byte[1_048_577], context));
        AssertFailure(
            envelope,
            Parse("[]"),
            context,
            SecretEnvelopeFailure.InvalidDocument);
        AssertFailure(
            envelope,
            Replace(encrypted, "wrapped_dek", JsonValue.Create(7)),
            context,
            SecretEnvelopeFailure.InvalidDocument);
        AssertFailure(
            envelope,
            Replace(
                encrypted,
                "wrapped_dek",
                JsonValue.Create(Base64Url(new byte[31]))),
            context,
            SecretEnvelopeFailure.InvalidDocument);
        AssertFailure(
            envelope,
            Replace(
                encrypted,
                "tag",
                JsonValue.Create(string.Concat(StringField(encrypted, "tag"), "=="))),
            context,
            SecretEnvelopeFailure.InvalidDocument);
        AssertFailure(
            envelope,
            Replace(encrypted, "ciphertext", JsonValue.Create(string.Empty)),
            context,
            SecretEnvelopeFailure.InvalidDocument);
        AssertFailure(
            envelope,
            Replace(encrypted, "kid", JsonValue.Create(new string('k', 257))),
            context,
            SecretEnvelopeFailure.InvalidDocument);
        AssertFailure(
            envelope,
            Parse(encrypted.GetRawText().Replace(
                $"\"kid\":\"{CurrentKeyId}\"",
                "\"kid\":\"\\uD800\"",
                StringComparison.Ordinal)),
            context,
            SecretEnvelopeFailure.InvalidDocument);
    }

    [Fact]
    public void KeyRingDefensivelyCopiesAndRejectsAmbiguousMaterial()
    {
        byte[] key = Key(0x71);
        var source = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [CurrentKeyId] = key,
        };
        SecretEnvelopeKeyRing keyRing = new(CurrentKeyId, source);
        CryptographicOperations.ZeroMemory(key);
        source[CurrentKeyId] = Key(0x72);

        SecretEnvelopeV1 envelope = new(keyRing);
        SecretEnvelopeContext context = AccountContext(AccountId);
        JsonElement encrypted = envelope.Encrypt(
            Encoding.UTF8.GetBytes(Credential),
            context);
        byte[] plaintext = envelope.Decrypt(encrypted, context);
        try
        {
            Assert.Equal(Credential, Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        byte[] duplicate = Key(0x73);
        var ambiguous = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [CurrentKeyId] = duplicate,
            [OldKeyId] = duplicate.ToArray(),
        };
        Assert.Throws<ArgumentException>(() =>
            new SecretEnvelopeKeyRing(CurrentKeyId, ambiguous));
        Assert.Throws<ArgumentException>(() =>
            new SecretEnvelopeKeyRing("missing-current", source));
        Assert.Throws<ArgumentException>(() =>
            new SecretEnvelopeKeyRing(
                "invalid\ud800",
                new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    ["invalid\ud800"] = Key(0x74),
                }));
        Assert.Throws<ArgumentException>(() =>
            new SecretEnvelopeKeyRing(
                CurrentKeyId,
                new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    [CurrentKeyId] = Key(0x75),
                    ["invalid\udc00"] = Key(0x76),
                }));
        Assert.Throws<ArgumentException>(() =>
            new SecretEnvelopeKeyRing(
                "\uFFFD",
                new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    ["\uFFFD"] = Key(0x77),
                }));

        byte[] leasedBytes = Encoding.UTF8.GetBytes(Credential);
        AccountCredentialLease lease = new(leasedBytes);
        lease.Dispose();
        lease.Dispose();
        Assert.All(leasedBytes, static value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() =>
            lease.Use(static credential => credential.Length));
    }

    private static SecretEnvelopeV1 Envelope(
        string currentKeyId,
        byte[] currentKey,
        params (string KeyId, byte[] Key)[] historical)
    {
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [currentKeyId] = currentKey,
        };
        foreach ((string keyId, byte[] key) in historical)
        {
            keys.Add(keyId, key);
        }

        return new SecretEnvelopeV1(new SecretEnvelopeKeyRing(currentKeyId, keys));
    }

    private static SecretEnvelopeKeyRing Ring(string keyId, byte[] key) =>
        new(
            keyId,
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [keyId] = key,
            });

    private static IConfiguration Configuration(
        params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(
                static item => item.Key,
                static item => item.Value,
                StringComparer.Ordinal))
            .Build();

    private static SecretEnvelopeContext AccountContext(EntityId accountId) =>
        new(
            "account-credential",
            "account",
            accountId.Value.ToString("D"),
            "credential_envelope");

    private static byte[] Key(byte value) =>
        Enumerable.Repeat(value, SecretEnvelopeKeyRing.KeySize).ToArray();

    private static string StringField(JsonElement value, string propertyName) =>
        value.GetProperty(propertyName).GetString()!;

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static void AssertFailure(
        SecretEnvelopeV1 envelope,
        JsonElement value,
        SecretEnvelopeContext context,
        SecretEnvelopeFailure expected)
    {
        SecretEnvelopeException exception = Assert.Throws<SecretEnvelopeException>(
            () => envelope.Decrypt(value, context));
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
        return Parse(root.ToJsonString());
    }

    private static JsonElement Remove(JsonElement source, string propertyName)
    {
        JsonObject root = JsonNode.Parse(source.GetRawText())!.AsObject();
        Assert.True(root.Remove(propertyName));
        return Parse(root.ToJsonString());
    }

    private static JsonElement Add(
        JsonElement source,
        string propertyName,
        JsonNode? value)
    {
        JsonObject root = JsonNode.Parse(source.GetRawText())!.AsObject();
        root.Add(propertyName, value);
        return Parse(root.ToJsonString());
    }

    private static JsonElement DuplicateVersion(JsonElement source)
    {
        string json = source.GetRawText();
        return Parse(string.Concat(json.AsSpan(0, json.Length - 1), ",\"v\":1}"));
    }

    private static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class RecordingOperationalEventWriter : IOperationalEventWriter
    {
        internal List<(string EventName, JsonElement Payload)> Events { get; } = [];

        public ValueTask WriteAsync(
            string eventName,
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add((eventName, payload.Clone()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingOperationalEventWriter : IOperationalEventWriter
    {
        public ValueTask WriteAsync(
            string eventName,
            JsonElement payload,
            CancellationToken cancellationToken) =>
            ValueTask.FromException(new InvalidOperationException(
                "Deterministic alert sink failure."));
    }
}
#pragma warning restore MA0051
