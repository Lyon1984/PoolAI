using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application;
using PoolAI.Modules.Identity.Application.Ports;
using PoolAI.Modules.Identity.Infrastructure.Security;

namespace PoolAI.UnitTests;

public sealed class IdentitySessionSecurityFailureCoverageTests
{
    [Fact]
    public void AccessTokenIssuerAndOptionsRejectInvalidInputs()
    {
        byte[] signingKey = Enumerable.Repeat((byte)0x6d, 32).ToArray();
        AccessTokenOptions options = new(
            "PoolAI",
            "PoolAI.Web",
            AccessTokenOptions.FrozenLifetime,
            signingKey);

        Assert.Throws<ArgumentNullException>(() => new AccessTokenIssuer(null!, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new AccessTokenIssuer(options, null!));
        Assert.Throws<ArgumentNullException>(() => new AccessTokenIssuer(options, TimeProvider.System).Issue(null!));

        AccessTokenIssuer issuer = new(options, TimeProvider.System);
        Assert.Throws<ArgumentOutOfRangeException>(() => issuer.Issue(new AccessTokenSubject(
            EntityId.New(),
            SystemRole.User,
            0,
            EntityId.New())));
        Assert.Throws<ArgumentOutOfRangeException>(() => issuer.Issue(new AccessTokenSubject(
            EntityId.New(),
            (SystemRole)999,
            1,
            EntityId.New())));

        Assert.Throws<ArgumentOutOfRangeException>(() => new AccessTokenOptions(
            "PoolAI",
            "PoolAI.Web",
            TimeSpan.FromMinutes(14),
            signingKey));
        Assert.Throws<ArgumentNullException>(() => new AccessTokenOptions(
            "PoolAI",
            "PoolAI.Web",
            AccessTokenOptions.FrozenLifetime,
            null!));
        Assert.Throws<ArgumentException>(() => new AccessTokenOptions(
            "PoolAI",
            "PoolAI.Web",
            AccessTokenOptions.FrozenLifetime,
            new byte[31]));
    }

    [Theory]
    [InlineData(SystemRole.Admin, "admin")]
    [InlineData(SystemRole.Auditor, "auditor")]
    public void AccessTokenIssuerEncodesEveryPrivilegedFrozenRole(
        SystemRole role,
        string expectedCode)
    {
        AccessTokenIssuer issuer = new(
            new AccessTokenOptions(
                "PoolAI",
                "PoolAI.Web",
                AccessTokenOptions.FrozenLifetime,
                Enumerable.Repeat((byte)0x3d, 32).ToArray()),
            TimeProvider.System);

        AccessTokenSecret token = issuer.Issue(new AccessTokenSubject(
            EntityId.New(),
            role,
            1,
            EntityId.New()));

        using JsonDocument payload = JsonDocument.Parse(
            Base64Url.Decode(token.Token.Split('.')[1]));
        Assert.Equal(expectedCode, payload.RootElement.GetProperty("role").GetString());
    }

    [Theory]
    [InlineData("", "PoolAI.Web")]
    [InlineData(" PoolAI", "PoolAI.Web")]
    [InlineData("PoolAI\n", "PoolAI.Web")]
    [InlineData("PoolAI", "")]
    [InlineData("PoolAI", " PoolAI.Web")]
    [InlineData("PoolAI", "PoolAI.Web\n")]
    public void AccessTokenOptionsRejectNonCanonicalNames(string issuer, string audience)
    {
        Assert.Throws<ArgumentException>(() => new AccessTokenOptions(
            issuer,
            audience,
            AccessTokenOptions.FrozenLifetime,
            new byte[32]));
    }

    [Fact]
    public void AccessTokenConfigurationWrapsInvalidValuesAndAcceptsFrozenProfile()
    {
        byte[] signingKey = Enumerable.Repeat((byte)0x25, 32).ToArray();
        IConfiguration valid = Configuration(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Auth:Jwt:SigningKey"] = Convert.ToBase64String(signingKey),
        });

        AccessTokenOptions parsed = AccessTokenOptions.FromConfiguration(valid);
        Assert.Equal("PoolAI", parsed.Issuer);
        Assert.Equal("PoolAI.Web", parsed.Audience);
        Assert.Equal(AccessTokenOptions.FrozenLifetime, parsed.Lifetime);

        Assert.Throws<InvalidOperationException>(() => AccessTokenOptions.FromConfiguration(
            Configuration(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Auth:Jwt:SigningKey"] = "not-base64",
            })));
        Assert.Throws<InvalidOperationException>(() => AccessTokenOptions.FromConfiguration(
            Configuration(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Auth:Jwt:SigningKey"] = Convert.ToBase64String(new byte[31]),
            })));
        Assert.Throws<InvalidOperationException>(() => AccessTokenOptions.FromConfiguration(
            Configuration(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Auth:Jwt:SigningKey"] = Convert.ToBase64String(signingKey),
                ["Auth:Jwt:AccessTokenMinutes"] = "16",
            })));
        Assert.Throws<InvalidOperationException>(() => AccessTokenOptions.FromConfiguration(
            Configuration(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Auth:Jwt:SigningKey"] = Convert.ToBase64String(signingKey),
                ["Auth:Jwt:Issuer"] = " PoolAI",
            })));
    }

    [Theory]
    [InlineData("Auth:Login:MaxFailures", "2")]
    [InlineData("Auth:Login:MaxFailures", "21")]
    [InlineData("Auth:Login:LockoutMinutes", "0")]
    [InlineData("Auth:Login:LockoutMinutes", "1441")]
    [InlineData("Auth:Jwt:RefreshTokenDays", "29")]
    public void SessionPolicyConfigurationRejectsValuesOutsideFrozenBounds(
        string key,
        string value)
    {
        IConfiguration configuration = Configuration(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [key] = value,
            });

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SessionPolicy.FromConfiguration(configuration));

        Assert.Equal("Identity session policy configuration is invalid.", exception.Message);
    }

    [Fact]
    public void AeadEnvelopeRoundTripsAndRejectsInvalidCryptographicContext()
    {
        byte[] key = Enumerable.Repeat((byte)0x11, 32).ToArray();
        EnvelopeKeyRingOptions keyRing = KeyRing("identity-k1", key);
        AeadEnvelopeV1 envelope = new(keyRing);
        byte[] plaintext = "sensitive-value"u8.ToArray();

        JsonElement encrypted = envelope.Encrypt(plaintext, "poolai|test|aad");
        Assert.Equal(plaintext, envelope.Decrypt(encrypted, "poolai|test|aad"));

        Assert.Throws<ArgumentNullException>(() => new AeadEnvelopeV1(null!));
        Assert.Throws<ArgumentException>(() => envelope.Encrypt(Array.Empty<byte>(), "poolai|test|aad"));
        Assert.Throws<ArgumentException>(() => envelope.Encrypt(plaintext, " "));
        Assert.Throws<ArgumentException>(() => envelope.Decrypt(encrypted, string.Empty));
        Assert.Throws<CryptographicException>(() => envelope.Decrypt(encrypted, "poolai|test|other"));

        byte[] otherKey = Enumerable.Repeat((byte)0x22, 32).ToArray();
        AeadEnvelopeV1 otherEnvelope = new(KeyRing("identity-k2", otherKey));
        Assert.Throws<CryptographicException>(() => otherEnvelope.Decrypt(
            encrypted,
            "poolai|test|aad"));

        JsonElement tampered = WithProperty(
            encrypted,
            "ciphertext",
            FlipFirstBase64UrlCharacter(encrypted.GetProperty("ciphertext").GetString()!));
        Assert.Throws<CryptographicException>(() => envelope.Decrypt(tampered, "poolai|test|aad"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"v\":1,\"extra\":true}")]
    public void AeadEnvelopeRejectsMalformedTopLevelDocuments(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        AeadEnvelopeV1 envelope = new(KeyRing("identity-k1", new byte[32]));

        Assert.Throws<CryptographicException>(() => envelope.Decrypt(
            document.RootElement,
            "poolai|test|aad"));
    }

    [Fact]
    public void AeadEnvelopeRejectsInvalidFrozenFieldsAndLengths()
    {
        AeadEnvelopeV1 envelope = new(KeyRing("identity-k1", new byte[32]));
        JsonElement encrypted = envelope.Encrypt("payload"u8, "poolai|test|aad");
        List<JsonElement> malformed =
        [
            WithoutProperty(encrypted, "tag"),
            WithProperty(encrypted, "v", 2),
            WithProperty(encrypted, "v", "1"),
            WithProperty(encrypted, "alg", "A256GCM"),
            WithProperty(encrypted, "kid", " "),
            WithProperty(encrypted, "wrapped_dek", "AA"),
            WithProperty(encrypted, "wrap_nonce", "AA"),
            WithProperty(encrypted, "wrap_tag", "AA"),
            WithProperty(encrypted, "ciphertext", "!"),
            WithProperty(encrypted, "nonce", "AA"),
            WithProperty(encrypted, "tag", "AA"),
            WithProperty(encrypted, "extra", true),
        ];

        foreach (JsonElement value in malformed)
        {
            Assert.Throws<CryptographicException>(() => envelope.Decrypt(value, "poolai|test|aad"));
        }
    }

    [Fact]
    public void EnvelopeKeyRingConfigurationRequiresCanonicalCurrentKeyMembership()
    {
        byte[] key = Enumerable.Repeat((byte)0x31, 32).ToArray();
        string encoded = Convert.ToBase64String(key);
        EnvelopeKeyRingOptions parsed = EnvelopeKeyRingOptions.FromConfiguration(
            Configuration(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Secrets:Envelope:CurrentKeyId"] = "identity-k1",
                ["Secrets:Envelope:CurrentKey"] = encoded,
                ["Secrets:Envelope:DecryptKeyRing:identity-k1"] = encoded,
            }));
        Assert.Equal("identity-k1", parsed.CurrentKeyId);
        Assert.Equal(key, parsed.CurrentKey);

        Assert.Throws<InvalidOperationException>(() => EnvelopeKeyRingOptions.FromConfiguration(
            Configuration(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Secrets:Envelope:CurrentKey"] = encoded,
            })));
        Assert.Throws<InvalidOperationException>(() => EnvelopeKeyRingOptions.FromConfiguration(
            Configuration(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Secrets:Envelope:CurrentKeyId"] = "identity-k1",
                ["Secrets:Envelope:CurrentKey"] = "not-base64",
            })));
        Assert.Throws<InvalidOperationException>(() => EnvelopeKeyRingOptions.FromConfiguration(
            Configuration(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Secrets:Envelope:CurrentKeyId"] = "identity-k1",
                ["Secrets:Envelope:CurrentKey"] = Convert.ToBase64String(new byte[31]),
            })));
        Assert.Throws<InvalidOperationException>(() => EnvelopeKeyRingOptions.FromConfiguration(
            Configuration(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Secrets:Envelope:CurrentKeyId"] = "identity-k1",
                ["Secrets:Envelope:CurrentKey"] = encoded,
                ["Secrets:Envelope:DecryptKeyRing:identity-k2"] = encoded,
            })));
        Assert.Throws<InvalidOperationException>(() => EnvelopeKeyRingOptions.FromConfiguration(
            Configuration(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Secrets:Envelope:CurrentKeyId"] = "identity-k1",
                ["Secrets:Envelope:CurrentKey"] = encoded,
                ["Secrets:Envelope:DecryptKeyRing:identity-k1"] =
                    Convert.ToBase64String(Enumerable.Repeat((byte)0x32, 32).ToArray()),
            })));
    }

    [Fact]
    public void RefreshCredentialOptionsRejectInvalidPepperRingsAndConfiguration()
    {
        byte[] pepper = Enumerable.Repeat((byte)0x41, 32).ToArray();
        RefreshTokenPepper current = new(1, pepper);

        Assert.Throws<ArgumentNullException>(() => new RefreshCredentialHasher(null!));
        Assert.Throws<ArgumentNullException>(() => new RefreshTokenHashOptions(null!, null));
        Assert.Throws<ArgumentException>(() => new RefreshTokenHashOptions(
            current,
            new RefreshTokenPepper(1, pepper)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RefreshTokenPepper(0, pepper));
        Assert.Throws<ArgumentNullException>(() => new RefreshTokenPepper(1, null!));
        Assert.Throws<ArgumentException>(() => new RefreshTokenPepper(1, new byte[31]));

        RefreshCredentialHasher withoutPrevious = new(new RefreshTokenHashOptions(current, null));
        RefreshCredentialSecret credential = withoutPrevious.Create();
        Assert.Single(withoutPrevious.HashCandidates(credential.Token));
        Assert.True(withoutPrevious.Verify(credential.Token, credential.Hash, 1));
        Assert.Empty(withoutPrevious.HashCandidates(Base64Url.Encode(new byte[31])));
        Assert.False(withoutPrevious.Verify(Base64Url.Encode(new byte[31]), credential.Hash, 1));

        foreach (Dictionary<string, string?> values in InvalidRefreshConfigurations())
        {
            Assert.Throws<InvalidOperationException>(() =>
                RefreshTokenHashOptions.FromConfiguration(Configuration(values)));
        }

        Assert.Throws<ArgumentException>(() => RefreshTokenHashOptions.FromConfiguration(
            Configuration(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Auth:RefreshToken:CurrentPepperVersion"] = "1",
                ["Auth:RefreshToken:CurrentPepper"] = Convert.ToBase64String(pepper),
                ["Auth:RefreshToken:PreviousPepperVersion"] = "1",
                ["Auth:RefreshToken:PreviousPepper"] = Convert.ToBase64String(pepper),
            })));
    }

    [Fact]
    public void TotpAuthenticatorAndOptionsRejectNonFrozenOrNonCanonicalInputs()
    {
        Assert.Throws<ArgumentNullException>(() => new TotpAuthenticator(null!));
        foreach (string invalid in new[] { string.Empty, " person@example.test", "person\n@example.test" })
        {
            TotpAuthenticator authenticator = new(new TotpOptions("PoolAI"));
            Assert.Throws<ArgumentException>(() => authenticator.CreateProvisioningSecret(invalid));
        }

        TotpAuthenticator subject = new(new TotpOptions("PoolAI"));
        Assert.Throws<ArgumentException>(() => subject.BuildProvisioningUri("BAD!", "person@example.test"));
        Assert.Throws<ArgumentException>(() => subject.BuildProvisioningUri(
            Base32.Encode(new byte[19]),
            "person@example.test"));
        Assert.Throws<ArgumentException>(() => subject.BuildProvisioningUri(
            Base32.Encode(new byte[20]),
            " person@example.test"));
        Assert.False(subject.TryMatchStep(
            Base32.Encode(new byte[20]),
            null!,
            DateTimeOffset.UnixEpoch,
            out _));
        Assert.False(subject.TryMatchStep(
            Base32.Encode(new byte[19]),
            "000000",
            DateTimeOffset.UnixEpoch,
            out _));
        Assert.False(subject.TryMatchStep(
            Base32.Encode(new byte[20]),
            "000000",
            DateTimeOffset.FromUnixTimeSeconds(-1),
            out _));

        foreach (string invalid in new[] { string.Empty, " PoolAI", "PoolAI\n", new string('x', 65) })
        {
            Assert.Throws<ArgumentException>(() => new TotpOptions(invalid));
        }

        Assert.Throws<InvalidOperationException>(() => TotpOptions.FromConfiguration(
            Configuration(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Auth:TOTP:StepSeconds"] = "60",
            })));
        Assert.Throws<InvalidOperationException>(() => TotpOptions.FromConfiguration(
            Configuration(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Auth:TOTP:AllowedAdjacentSteps"] = "2",
            })));
        Assert.Throws<InvalidOperationException>(() => TotpOptions.FromConfiguration(
            Configuration(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Auth:TOTP:Issuer"] = " PoolAI",
            })));
        Assert.Equal("PoolAI", TotpOptions.FromConfiguration(Configuration([])).Issuer);
    }

    [Fact]
    public void Base32RejectsEmptyAndNonCanonicalValues()
    {
        Assert.Throws<ArgumentException>(() => Base32.Encode(ReadOnlySpan<byte>.Empty));
        Assert.Throws<ArgumentNullException>(() => Base32.Decode(null!));
        Assert.Throws<FormatException>(() => Base32.Decode(string.Empty));
        Assert.Throws<ArgumentException>(() => Base32.Decode("A"));
        Assert.Throws<FormatException>(() => Base32.Decode("MZ======"));
        Assert.Throws<FormatException>(() => Base32.Decode("mz"));
        Assert.Equal("MY", Base32.Encode("f"u8));
        Assert.Equal("f"u8.ToArray(), Base32.Decode("MY"));
    }

    [Fact]
    public void RecoveryCodeOptionsAndEnvelopeRejectInvalidValues()
    {
        byte[] pepper = Enumerable.Repeat((byte)0x51, 32).ToArray();
        Assert.Throws<ArgumentNullException>(() => new TotpRecoveryCodeGenerator(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TotpRecoveryCodeHashOptions(0, pepper));
        Assert.Throws<ArgumentNullException>(() => new TotpRecoveryCodeHashOptions(1, null!));
        Assert.Throws<ArgumentException>(() => new TotpRecoveryCodeHashOptions(1, new byte[31]));

        Assert.Throws<InvalidOperationException>(() => TotpRecoveryCodeHashOptions.FromConfiguration(
            Configuration([])));
        Assert.Throws<InvalidOperationException>(() => TotpRecoveryCodeHashOptions.FromConfiguration(
            Configuration(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Auth:TOTP:RecoveryCodePepperVersion"] = "1",
                ["Auth:TOTP:RecoveryCodePepper"] = "not-base64",
            })));
        Assert.Throws<InvalidOperationException>(() => TotpRecoveryCodeHashOptions.FromConfiguration(
            Configuration(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Auth:TOTP:RecoveryCodePepperVersion"] = "1",
                ["Auth:TOTP:RecoveryCodePepper"] = Convert.ToBase64String(new byte[31]),
            })));

        TotpRecoveryCodeEnvelopeV1 envelope = new(KeyRing("identity-k1", new byte[32]));
        EntityId tokenId = EntityId.New();
        IdempotencySecretBinding binding = Binding();
        Assert.Throws<ArgumentNullException>(() => envelope.Encrypt(null!, tokenId, binding));
        Assert.Throws<ArgumentException>(() => envelope.Encrypt(
            Enumerable.Repeat("AAAAAAAAAAAAAAAA", 8).ToArray(),
            tokenId,
            binding));
        Assert.Throws<ArgumentException>(() => envelope.Encrypt(
            Enumerable.Range(0, 8).Select(static index => $"INVALID-{index:D8}").ToArray(),
            tokenId,
            binding));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"recovery_codes\":\"not-an-array\"}")]
    [InlineData("{\"recovery_codes\":[1,2,3,4,5,6,7,8]}")]
    [InlineData("{\"recovery_codes\":[\"AAAAAAAAAAAAAAAA\",\"AAAAAAAAAAAAAAAA\",\"AAAAAAAAAAAAAAAA\",\"AAAAAAAAAAAAAAAA\",\"AAAAAAAAAAAAAAAA\",\"AAAAAAAAAAAAAAAA\",\"AAAAAAAAAAAAAAAA\",\"AAAAAAAAAAAAAAAA\"]}")]
    public void RecoveryCodeEnvelopeRejectsAuthenticatedMalformedPlaintext(string plaintext)
    {
        byte[] key = Enumerable.Repeat((byte)0x61, 32).ToArray();
        EnvelopeKeyRingOptions keyRing = KeyRing("identity-k1", key);
        EntityId tokenId = EntityId.New();
        IdempotencySecretBinding binding = Binding();
        JsonElement encrypted = new AeadEnvelopeV1(keyRing).Encrypt(
            Encoding.UTF8.GetBytes(plaintext),
            TotpRecoveryCodeEnvelopeV1.BuildAad(tokenId, binding));

        Assert.Throws<CryptographicException>(() =>
            new TotpRecoveryCodeEnvelopeV1(keyRing).Decrypt(encrypted, tokenId, binding));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"challenge_id\":\"not-a-guid\",\"secret\":\"JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP\",\"otpauth_uri\":\"otpauth://totp/PoolAI:user?secret=JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP\",\"expires_in\":600}")]
    [InlineData("{\"challenge_id\":\"019bd5e8-30e0-7d4c-a7f2-bb1db0634041\",\"secret\":42,\"otpauth_uri\":\"x\",\"expires_in\":600}")]
    [InlineData("{\"challenge_id\":\"019bd5e8-30e0-7d4c-a7f2-bb1db0634041\",\"secret\":\"JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP\",\"otpauth_uri\":\"https://example.test\",\"expires_in\":600}")]
    public void SetupResponseEnvelopeRejectsAuthenticatedMalformedPlaintext(string plaintext)
    {
        byte[] key = Enumerable.Repeat((byte)0x71, 32).ToArray();
        EnvelopeKeyRingOptions keyRing = KeyRing("identity-k1", key);
        EntityId tokenId = EntityId.New();
        IdempotencySecretBinding binding = Binding();
        JsonElement encrypted = new AeadEnvelopeV1(keyRing).Encrypt(
            Encoding.UTF8.GetBytes(plaintext),
            TotpSetupResponseEnvelopeV1.BuildAad(tokenId, binding));

        Assert.Throws<CryptographicException>(() =>
            new TotpSetupResponseEnvelopeV1(keyRing).Decrypt(encrypted, tokenId, binding));
    }

    [Fact]
    public void IdempotencyAadRejectsIncompleteBindings()
    {
        EntityId tokenId = EntityId.New();
        IdempotencySecretBinding valid = Binding();
        Assert.Throws<ArgumentNullException>(() =>
            TotpIdempotencyResponseAad.Build("field", tokenId, null!));
        Assert.Throws<ArgumentException>(() =>
            TotpIdempotencyResponseAad.Build(" ", tokenId, valid));
        Assert.Throws<ArgumentException>(() =>
            TotpIdempotencyResponseAad.Build("field", tokenId, valid with { Scope = "" }));
        Assert.Throws<ArgumentException>(() =>
            TotpIdempotencyResponseAad.Build("field", tokenId, valid with { IdempotencyKey = "" }));
        Assert.Throws<ArgumentException>(() => TotpIdempotencyResponseAad.Build(
            "field",
            tokenId,
            valid with { RequestHash = new byte[31] }));
    }

    private static IEnumerable<Dictionary<string, string?>> InvalidRefreshConfigurations()
    {
        byte[] pepper = Enumerable.Repeat((byte)0x41, 32).ToArray();
        yield return [];
        yield return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Auth:RefreshToken:CurrentPepperVersion"] = "0",
            ["Auth:RefreshToken:CurrentPepper"] = Convert.ToBase64String(pepper),
        };
        yield return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Auth:RefreshToken:CurrentPepperVersion"] = "1",
            ["Auth:RefreshToken:CurrentPepper"] = "not-base64",
        };
        yield return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Auth:RefreshToken:CurrentPepperVersion"] = "1",
            ["Auth:RefreshToken:CurrentPepper"] = Convert.ToBase64String(new byte[31]),
        };
        yield return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Auth:RefreshToken:CurrentPepperVersion"] = "1",
            ["Auth:RefreshToken:CurrentPepper"] = Convert.ToBase64String(pepper),
            ["Auth:RefreshToken:PreviousPepperVersion"] = "2",
        };
    }

    private static IConfiguration Configuration(IEnumerable<KeyValuePair<string, string?>> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static EnvelopeKeyRingOptions KeyRing(string keyId, byte[] key) => new(
        keyId,
        key,
        new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [keyId] = key,
        });

    private static IdempotencySecretBinding Binding() => new(
        EntityId.New(),
        $"identity:{Guid.NewGuid():D}:post:/api/v1/me/totp/setup",
        "idempotency-key",
        Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray());

    private static JsonElement WithProperty(JsonElement source, string name, object? value)
    {
        Dictionary<string, object?> fields = source.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => (object?)property.Value.Clone(),
            StringComparer.Ordinal);
        fields[name] = value;
        return JsonSerializer.SerializeToElement(fields);
    }

    private static JsonElement WithoutProperty(JsonElement source, string name)
    {
        Dictionary<string, JsonElement> fields = source.EnumerateObject()
            .Where(property => !string.Equals(property.Name, name, StringComparison.Ordinal))
            .ToDictionary(
                static property => property.Name,
                static property => property.Value.Clone(),
                StringComparer.Ordinal);
        return JsonSerializer.SerializeToElement(fields);
    }

    private static string FlipFirstBase64UrlCharacter(string value) => string.Concat(
        value[0] == 'A' ? "B" : "A",
        value[1..]);
}
