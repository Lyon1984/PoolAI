using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Identity.Application;
using PoolAI.Modules.Identity.Application.Ports;
using PoolAI.Modules.Identity.Domain;
using PoolAI.Modules.Identity.Infrastructure;
using PoolAI.Modules.Identity.Infrastructure.Security;

namespace PoolAI.UnitTests;

public sealed class IdentitySecurityTests
{
    private static readonly string[] EnvelopePropertyNames =
    [
        "alg", "ciphertext", "kid", "nonce", "tag", "v",
        "wrap_nonce", "wrap_tag", "wrapped_dek",
    ];

    [Fact]
    public void IdentityMailboxUsesAsciiDotAtomAndCanonicalIdnaDomain()
    {
        string normalized = IdentityInput.NormalizeEmail("Admin+Reset@BÜCHER.Example");

        Assert.Equal("admin+reset@xn--bcher-kva.example", normalized);
    }

    [Theory]
    [InlineData("PoolAI <admin@example.test>")]
    [InlineData(" admin@example.test")]
    [InlineData("admin@example.test ")]
    [InlineData("\"admin\"@example.test")]
    [InlineData("admín@example.test")]
    [InlineData(".admin@example.test")]
    [InlineData("admin.@example.test")]
    [InlineData("admin..reset@example.test")]
    [InlineData("admin@[127.0.0.1]")]
    [InlineData("admin@invalid_domain.test")]
    public void IdentityMailboxRejectsValuesTheEmailWorkerCannotDeliver(string value)
    {
        _ = Assert.Throws<ArgumentException>(() => IdentityInput.NormalizeEmail(value));
    }

    [Fact]
    public void IdentityMailboxRejectsCanonicalAddressLongerThan254Characters()
    {
        string localPart = new('a', 64);
        string domain = string.Join(
            '.',
            new string('b', 63),
            new string('c', 63),
            new string('d', 62));

        _ = Assert.Throws<ArgumentException>(() =>
            IdentityInput.NormalizeEmail(string.Concat(localPart, "@", domain)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("admin@example.test\0")]
    [InlineData("admin@")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa@example.test")]
    [InlineData("admin@-invalid.example")]
    [InlineData("admin@invalid-.example")]
    [InlineData("admin@example..test")]
    public void IdentityMailboxRejectsBoundaryAndDnsLabelViolations(string value)
    {
        _ = Assert.Throws<ArgumentException>(() => IdentityInput.NormalizeEmail(value));
    }

    [Fact]
    public void IdentityTextAndCredentialInputsEnforceEveryFrozenBoundary()
    {
        Assert.Equal("Display name", IdentityInput.DisplayName("  Display name  "));
        Assert.Throws<ArgumentException>(() => IdentityInput.DisplayName(new string('x', 101)));
        Assert.Throws<ArgumentException>(() => IdentityInput.DisplayName("invalid\u0001name"));

        Assert.Equal("operator reason", IdentityInput.Reason("  operator reason  "));
        Assert.Throws<ArgumentException>(() => IdentityInput.Reason(new string('x', 501)));
        Assert.Throws<ArgumentException>(() => IdentityInput.Reason("invalid\nreason"));

        IdentityInput.Password(new string('x', 12), minimumLength: 12);
        Assert.Throws<ArgumentException>(() => IdentityInput.Password("short", 12));
        Assert.Throws<ArgumentException>(() => IdentityInput.Password(new string('x', 1_025), 12));
        Assert.Throws<ArgumentNullException>(() => IdentityInput.Password(null!, 12));

        IdentityInput.IdempotencyKey("visible-ascii-key");
        Assert.Throws<ArgumentException>(() => IdentityInput.IdempotencyKey(new string('x', 129)));
        Assert.Throws<ArgumentException>(() => IdentityInput.IdempotencyKey("not visible\t"));
    }

    [Fact]
    public void IdentityPolicyRejectsNullDependenciesAndBlankResetToken()
    {
        byte[] pepper = new byte[32];
        Uri baseUrl = new("https://poolai.example.test/", UriKind.Absolute);

        Assert.Throws<ArgumentNullException>(() => new IdentityPolicy(
            null!, 12, TimeSpan.FromMinutes(30), "example.test", pepper));
        Assert.Throws<ArgumentNullException>(() => new IdentityPolicy(
            baseUrl, 12, TimeSpan.FromMinutes(30), null!, pepper));
        Assert.Throws<ArgumentNullException>(() => new IdentityPolicy(
            baseUrl, 12, TimeSpan.FromMinutes(30), "example.test", null!));

        IdentityPolicy policy = new(
            baseUrl, 12, TimeSpan.FromMinutes(30), "example.test", pepper);
        Assert.Throws<ArgumentException>(() => policy.BuildPasswordResetUrl(" "));
        Assert.Equal(
            "https://poolai.example.test/reset-password?token=opaque",
            policy.BuildPasswordResetUrl("opaque"));
    }

    [Fact]
    public void PasswordHasherUsesVersionedIdentityV3FormatAndRejectsOtherPrefixes()
    {
        VersionedPasswordHasher hasher = new();

        string encoded = hasher.Hash("correct horse battery staple");

        Assert.StartsWith(VersionedPasswordHasher.Prefix, encoded, StringComparison.Ordinal);
        Assert.True(hasher.Verify(encoded, "correct horse battery staple"));
        Assert.False(hasher.Verify(encoded, "incorrect horse battery staple"));
        Assert.False(hasher.Verify(encoded[VersionedPasswordHasher.Prefix.Length..], "correct horse battery staple"));
    }

    [Theory]
    [InlineData(17, 32)]
    [InlineData(16, 31)]
    [InlineData(16, 33)]
    public void PasswordHasherRejectsOtherwiseValidNonCanonicalSaltOrSubkeyLengths(
        int saltLength,
        int subkeyLength)
    {
        const string Password = "correct horse battery staple";
        VersionedPasswordHasher hasher = new();
        string encoded = BuildPasswordHash(Password, saltLength, subkeyLength);

        Assert.False(hasher.Verify(encoded, Password));
    }

    [Fact]
    public void IdentityOptionsNormalizesMessageIdDomainToIdnaAsciiLowercase()
    {
        IdentityPolicy policy = IdentityOptions.FromConfiguration(
            BuildIdentityConfiguration("no-reply@BÜCHER.example"));
        EntityId emailId = new(Guid.Parse("019bd5e8-30e0-7d4c-a7f2-bb1db0634041"));

        Assert.Equal("xn--bcher-kva.example", policy.MessageIdDomain);
        Assert.Equal(
            $"<{emailId.Value:D}@xn--bcher-kva.example>",
            policy.BuildMessageId(emailId));
    }

    [Theory]
    [InlineData("PoolAI <no-reply@poolai.example.test>")]
    [InlineData(" no-reply@poolai.example.test")]
    [InlineData("no-reply@poolai.example.test ")]
    [InlineData("no-reply@[127.0.0.1]")]
    [InlineData("no-reply@invalid_domain.example")]
    public void IdentityOptionsRejectsNonCanonicalFromMailbox(string fromAddress)
    {
        Assert.Throws<InvalidOperationException>(() =>
            IdentityOptions.FromConfiguration(BuildIdentityConfiguration(fromAddress)));
    }

    [Theory]
    [InlineData("public-url")]
    [InlineData("password-length")]
    [InlineData("reset-lifetime")]
    [InlineData("pepper-format")]
    [InlineData("pepper-length")]
    public void IdentityOptionsRejectsEverySecurityConfigurationBoundary(string scenario)
    {
        IConfiguration configuration = BuildInvalidIdentityConfiguration(scenario);

        Assert.Throws<InvalidOperationException>(() =>
            IdentityOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void PasswordResetTokenStoresOnlyCanonicalPepperedHashes()
    {
        byte[] currentKey = RandomNumberGenerator.GetBytes(32);
        byte[] previousKey = RandomNumberGenerator.GetBytes(32);
        PasswordResetTokenHasher hasher = new(
            new TokenHashOptions(
                new TokenPepper(7, currentKey),
                new TokenPepper(6, previousKey)));

        PasswordResetTokenSecret secret = hasher.Create();
        IReadOnlyList<PasswordResetTokenCandidate> candidates = hasher.HashCandidates(secret.Token);

        Assert.Equal(43, secret.Token.Length);
        Assert.Equal(32, secret.Hash.Length);
        Assert.Equal((short)7, secret.PepperVersion);
        Assert.Equal(2, candidates.Count);
        Assert.True(CryptographicOperations.FixedTimeEquals(secret.Hash, candidates[0].Hash));
        byte[] tokenBytes = Base64Url.Decode(secret.Token);
        Assert.True(CryptographicOperations.FixedTimeEquals(
            HMACSHA256.HashData(currentKey, tokenBytes),
            secret.Hash));
        CryptographicOperations.ZeroMemory(tokenBytes);
        Assert.Equal((short)6, candidates[1].PepperVersion);
        Assert.Empty(hasher.HashCandidates("not-a-canonical-reset-token"));
    }

    [Fact]
    public void EmailEnvelopeRoundTripsAndBindsBothFieldsToOutboxIdentity()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        EmailSecretEnvelopeV1 envelope = new(
            new EnvelopeKeyRingOptions(
                "email-k1",
                key,
                new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    ["email-k1"] = key,
                }));
        EntityId outboxId = EntityId.New();
        EmailSecretEnvelopePlaintext plaintext = new(
            "person@example.test",
            "https://app.example.test/reset-password?token=opaque");

        PasswordResetEmailEnvelopes encrypted = envelope.Encrypt(plaintext, outboxId);
        EmailSecretEnvelopePlaintext decrypted = envelope.Decrypt(
            encrypted.RecipientEnvelope,
            encrypted.DeliverySecretEnvelope,
            outboxId);

        Assert.Equal(plaintext, decrypted);
        Assert.Equal(
            EnvelopePropertyNames,
            encrypted.RecipientEnvelope.EnumerateObject()
                .Select(static property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.Throws<CryptographicException>(() => envelope.Decrypt(
            encrypted.RecipientEnvelope,
            encrypted.DeliverySecretEnvelope,
            EntityId.New()));
        Assert.Throws<CryptographicException>(() => envelope.Decrypt(
            encrypted.DeliverySecretEnvelope,
            encrypted.RecipientEnvelope,
            outboxId));
    }

    private static string BuildPasswordHash(
        string password,
        int saltLength,
        int subkeyLength)
    {
        const int HeaderLength = 13;
        byte[] salt = Enumerable.Range(1, saltLength)
            .Select(static value => checked((byte)value))
            .ToArray();
        byte[] subkey = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            VersionedPasswordHasher.IterationCount,
            HashAlgorithmName.SHA512,
            subkeyLength);
        byte[] payload = new byte[HeaderLength + saltLength + subkeyLength];
        payload[0] = 0x01;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1, 4), 2);
        BinaryPrimitives.WriteUInt32BigEndian(
            payload.AsSpan(5, 4),
            VersionedPasswordHasher.IterationCount);
        BinaryPrimitives.WriteUInt32BigEndian(
            payload.AsSpan(9, 4),
            checked((uint)saltLength));
        salt.CopyTo(payload, HeaderLength);
        subkey.CopyTo(payload, HeaderLength + saltLength);
        CryptographicOperations.ZeroMemory(subkey);
        return VersionedPasswordHasher.Prefix + Convert.ToBase64String(payload);
    }

    private static IConfiguration BuildIdentityConfiguration(string fromAddress) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["App:PublicBaseUrl"] = "https://poolai.example.test",
                ["Email:FromAddress"] = fromAddress,
                ["Idempotency:RequestHashPepper"] = Convert.ToBase64String(
                    Enumerable.Range(1, 32)
                        .Select(static value => checked((byte)value))
                        .ToArray()),
            })
            .Build();

    private static IConfiguration BuildInvalidIdentityConfiguration(string scenario)
    {
        Dictionary<string, string?> values = new(StringComparer.Ordinal)
        {
            ["App:PublicBaseUrl"] = "https://poolai.example.test",
            ["Email:FromAddress"] = "no-reply@poolai.example.test",
            ["Idempotency:RequestHashPepper"] = Convert.ToBase64String(new byte[32]),
        };
        (string key, string value) = scenario switch
        {
            "public-url" => ("App:PublicBaseUrl", "https://poolai.example.test?bad=true"),
            "password-length" => ("Auth:Password:MinLength", "11"),
            "reset-lifetime" => ("Auth:PasswordReset:TokenMinutes", "61"),
            "pepper-format" => ("Idempotency:RequestHashPepper", "not-base64"),
            "pepper-length" => ("Idempotency:RequestHashPepper",
                Convert.ToBase64String(new byte[31])),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
