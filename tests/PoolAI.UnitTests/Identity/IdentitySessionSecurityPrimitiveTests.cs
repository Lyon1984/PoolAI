using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application.Ports;
using PoolAI.Modules.Identity.Infrastructure.Security;

namespace PoolAI.UnitTests;

public sealed class IdentitySessionSecurityPrimitiveTests
{
    private static readonly byte[] Rfc6238Sha1Seed =
        Encoding.ASCII.GetBytes("12345678901234567890");
    private static readonly string Rfc6238Sha1SeedBase32 = Base32.Encode(Rfc6238Sha1Seed);

    [Fact]
    public void RefreshCredentialUsesDedicatedVersionedPepperAndConstantTimeVerification()
    {
        byte[] currentPepper = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
        byte[] previousPepper = Enumerable.Range(33, 32).Select(static value => (byte)value).ToArray();
        RefreshCredentialHasher hasher = new(new RefreshTokenHashOptions(
            new RefreshTokenPepper(7, currentPepper),
            new RefreshTokenPepper(6, previousPepper)));

        RefreshCredentialSecret secret = hasher.Create();
        IReadOnlyList<RefreshCredentialCandidate> candidates =
            hasher.HashCandidates(secret.Token);

        Assert.Equal(43, secret.Token.Length);
        Assert.Equal(32, secret.Hash.Length);
        Assert.Equal((short)7, secret.PepperVersion);
        Assert.Collection(
            candidates,
            candidate =>
            {
                Assert.Equal((short)7, candidate.PepperVersion);
                Assert.Equal(secret.Hash, candidate.Hash);
            },
            candidate => Assert.Equal((short)6, candidate.PepperVersion));
        Assert.True(hasher.Verify(secret.Token, secret.Hash, secret.PepperVersion));
        Assert.True(hasher.Verify(
            secret.Token,
            candidates[1].Hash,
            candidates[1].PepperVersion));

        byte[] tamperedHash = secret.Hash.ToArray();
        tamperedHash[0] ^= 0xff;
        Assert.False(hasher.Verify(secret.Token, tamperedHash, secret.PepperVersion));
        Assert.False(hasher.Verify(secret.Token, secret.Hash, 5));
        Assert.False(hasher.Verify(secret.Token, new byte[31], secret.PepperVersion));
        Assert.False(hasher.Verify("not_base64url!", secret.Hash, secret.PepperVersion));
        Assert.Empty(hasher.HashCandidates("not_base64url!"));
    }

    [Fact]
    public void RefreshCredentialConfigurationDoesNotFallBackToOneTimeTokenPepper()
    {
        byte[] pepper = RandomNumberGenerator.GetBytes(32);
        IConfiguration oneTimeOnly = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Auth:TokenHash:CurrentPepperVersion"] = "1",
                ["Auth:TokenHash:CurrentPepper"] = Convert.ToBase64String(pepper),
            })
            .Build();
        IConfiguration dedicated = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Auth:RefreshToken:CurrentPepperVersion"] = "3",
                ["Auth:RefreshToken:CurrentPepper"] = Convert.ToBase64String(pepper),
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() =>
            RefreshTokenHashOptions.FromConfiguration(oneTimeOnly));
        Assert.Equal(
            (short)3,
            RefreshTokenHashOptions.FromConfiguration(dedicated).Current.Version);
    }

    [Fact]
    public void OneTimeChallengeHashesCanonicalRfc4122UuidBytesWithTokenPepperRing()
    {
        byte[] currentPepper = Enumerable.Repeat((byte)0xa5, 32).ToArray();
        byte[] previousPepper = Enumerable.Repeat((byte)0x5a, 32).ToArray();
        OneTimeChallengeHasher hasher = new(new TokenHashOptions(
            new TokenPepper(9, currentPepper),
            new TokenPepper(8, previousPepper)));
        EntityId challenge = new(Guid.Parse(
            "00112233-4455-6677-8899-aabbccddeeff",
            CultureInfo.InvariantCulture));
        byte[] canonicalBytes =
        [
            0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77,
            0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff,
        ];

        IReadOnlyList<OneTimeChallengeCandidate> candidates =
            hasher.HashCandidates(challenge);

        Assert.Collection(
            candidates,
            candidate =>
            {
                Assert.Equal((short)9, candidate.PepperVersion);
                Assert.Equal(HMACSHA256.HashData(currentPepper, canonicalBytes), candidate.Hash);
            },
            candidate =>
            {
                Assert.Equal((short)8, candidate.PepperVersion);
                Assert.Equal(HMACSHA256.HashData(previousPepper, canonicalBytes), candidate.Hash);
            });
        Assert.True(hasher.Verify(challenge, candidates[0].Hash, 9));
        Assert.True(hasher.Verify(challenge, candidates[1].Hash, 8));
        Assert.False(hasher.Verify(EntityId.New(), candidates[0].Hash, 9));
        Assert.False(hasher.Verify(challenge, candidates[0].Hash, 7));

        OneTimeChallengeSecret generated = hasher.Create();
        Assert.NotEqual(Guid.Empty, generated.Challenge.Value);
        Assert.Equal((short)9, generated.PepperVersion);
        Assert.True(hasher.Verify(generated.Challenge, generated.Hash, generated.PepperVersion));
    }

    [Theory]
    [InlineData(15, "755224", 0)]
    [InlineData(45, "755224", 0)]
    [InlineData(45, "287082", 1)]
    [InlineData(45, "359152", 2)]
    [InlineData(59, "287082", 1)]
    [InlineData(60, "359152", 2)]
    public void TotpMatchesRfc6238Sha1VectorsAcrossTheFrozenAdjacentWindow(
        long unixSeconds,
        string code,
        long expectedStep)
    {
        TotpAuthenticator authenticator = new(new TotpOptions("PoolAI"));

        bool matched = authenticator.TryMatchStep(
            Rfc6238Sha1SeedBase32,
            code,
            DateTimeOffset.FromUnixTimeSeconds(unixSeconds),
            out long step);

        Assert.True(matched);
        Assert.Equal(expectedStep, step);
    }

    [Fact]
    public void TotpRejectsOutsideWindowAndNonCanonicalSecretOrCode()
    {
        TotpAuthenticator authenticator = new(new TotpOptions("PoolAI"));
        DateTimeOffset currentStepOne = DateTimeOffset.FromUnixTimeSeconds(45);

        Assert.False(authenticator.TryMatchStep(
            Rfc6238Sha1SeedBase32,
            "969429",
            currentStepOne,
            out _));
        Assert.False(authenticator.TryMatchStep(
            Rfc6238Sha1SeedBase32.ToLowerInvariant(),
            "287082",
            currentStepOne,
            out _));
        Assert.False(authenticator.TryMatchStep(
            string.Concat(Rfc6238Sha1SeedBase32, "="),
            "287082",
            currentStepOne,
            out _));
        Assert.False(authenticator.TryMatchStep(
            Rfc6238Sha1SeedBase32,
            " 287082",
            currentStepOne,
            out _));
        Assert.False(authenticator.TryMatchStep(
            Rfc6238Sha1SeedBase32,
            "28708a",
            currentStepOne,
            out _));
    }

    [Fact]
    public void TotpProvisioningUsesTwentyByteSeedAndEscapesIssuerAndAccountSeparately()
    {
        TotpAuthenticator authenticator = new(new TotpOptions("Pool AI/研发"));

        TotpProvisioningSecret provisioning =
            authenticator.CreateProvisioningSecret("User+tag@example.test");

        byte[] decoded = Base32.Decode(provisioning.Base32Secret);
        Assert.Equal(20, decoded.Length);
        Assert.Equal(32, provisioning.Base32Secret.Length);
        Assert.Equal(provisioning.Base32Secret.ToUpperInvariant(), provisioning.Base32Secret);
        Assert.DoesNotContain('=', provisioning.Base32Secret);
        Assert.Equal(
            string.Concat(
                "otpauth://totp/Pool%20AI%2F%E7%A0%94%E5%8F%91:",
                "User%2Btag%40example.test?secret=",
                provisioning.Base32Secret,
                "&issuer=Pool%20AI%2F%E7%A0%94%E5%8F%91",
                "&algorithm=SHA1&digits=6&period=30"),
            provisioning.OtpAuthUri);
        Assert.Equal(
            provisioning.OtpAuthUri,
            authenticator.BuildProvisioningUri(
                provisioning.Base32Secret,
                "User+tag@example.test"));
        CryptographicOperations.ZeroMemory(decoded);
    }

    [Fact]
    public void TotpSeedEnvelopeBindsPurposeEntityIdentityAndField()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        EnvelopeKeyRingOptions keyRing = KeyRing(key);
        TotpSecretEnvelopeV1 envelope = new(keyRing);
        EntityId challengeId = new(Guid.Parse(
            "019bd5e8-30e0-7d4c-a7f2-bb1db0634041",
            CultureInfo.InvariantCulture));

        JsonElement encrypted = envelope.Encrypt(
            Rfc6238Sha1SeedBase32,
            TotpSecretEnvelopeTarget.SetupChallenge,
            challengeId);

        Assert.Equal(
            Rfc6238Sha1SeedBase32,
            envelope.Decrypt(
                encrypted,
                TotpSecretEnvelopeTarget.SetupChallenge,
                challengeId));
        Assert.Equal(
            "poolai|v1|totp-secret|one_time_token|019bd5e8-30e0-7d4c-a7f2-bb1db0634041|secret_envelope",
            TotpSecretEnvelopeV1.BuildAad(
                TotpSecretEnvelopeTarget.SetupChallenge,
                challengeId));
        Assert.Equal(
            "poolai|v1|totp-secret|user|019bd5e8-30e0-7d4c-a7f2-bb1db0634041|totp_secret_envelope",
            TotpSecretEnvelopeV1.BuildAad(TotpSecretEnvelopeTarget.User, challengeId));
        Assert.Throws<CryptographicException>(() =>
        {
            _ = envelope.Decrypt(
                encrypted,
                TotpSecretEnvelopeTarget.User,
                challengeId);
        });
        Assert.Throws<CryptographicException>(() =>
        {
            _ = envelope.Decrypt(
                encrypted,
                TotpSecretEnvelopeTarget.SetupChallenge,
                EntityId.New());
        });
    }

    [Fact]
    public void RecoveryCodesAreEightUniqueEightyBitCredentialsHashedWithDedicatedPepper()
    {
        byte[] pepper = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
        TotpRecoveryCodeGenerator generator = new(
            new TotpRecoveryCodeHashOptions(4, pepper));

        IReadOnlyList<TotpRecoveryCodeSecret> codes = generator.CreateBatch();

        Assert.Equal(8, codes.Count);
        Assert.Equal(8, codes.Select(static item => item.Code).Distinct(StringComparer.Ordinal).Count());
        foreach (TotpRecoveryCodeSecret item in codes)
        {
            Assert.Equal(16, item.Code.Length);
            Assert.Equal(item.Code.ToUpperInvariant(), item.Code);
            Assert.DoesNotContain('=', item.Code);
            Assert.Equal((short)4, item.PepperVersion);
            byte[] rawCode = Base32.Decode(item.Code);
            Assert.Equal(10, rawCode.Length);
            Assert.Equal(HMACSHA256.HashData(pepper, rawCode), item.Hash);
            CryptographicOperations.ZeroMemory(rawCode);
        }
    }

    [Fact]
    public void RecoveryCodeConfigurationDoesNotFallBackToOneTimeTokenPepper()
    {
        byte[] pepper = RandomNumberGenerator.GetBytes(32);
        IConfiguration oneTimeOnly = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Auth:TokenHash:CurrentPepperVersion"] = "1",
                ["Auth:TokenHash:CurrentPepper"] = Convert.ToBase64String(pepper),
            })
            .Build();
        IConfiguration dedicated = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Auth:TOTP:RecoveryCodePepperVersion"] = "5",
                ["Auth:TOTP:RecoveryCodePepper"] = Convert.ToBase64String(pepper),
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() =>
            TotpRecoveryCodeHashOptions.FromConfiguration(oneTimeOnly));
        Assert.Equal(
            (short)5,
            TotpRecoveryCodeHashOptions.FromConfiguration(dedicated).PepperVersion);
    }

    [Fact]
    public void RecoveryCodeEnvelopeRoundTripsAndBindsFrozenIdempotencyResponseAad()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        TotpRecoveryCodeGenerator generator = new(
            new TotpRecoveryCodeHashOptions(1, RandomNumberGenerator.GetBytes(32)));
        string[] codes = generator.CreateBatch().Select(static value => value.Code).ToArray();
        TotpRecoveryCodeEnvelopeV1 envelope = new(KeyRing(key));
        EntityId tokenId = new(Guid.Parse(
            "019bd5e8-30e0-7d4c-a7f2-bb1db0634042",
            CultureInfo.InvariantCulture));
        EntityId victimUserId = new(Guid.Parse(
            "019bd5e8-30e0-7d4c-a7f2-bb1db0634041",
            CultureInfo.InvariantCulture));
        IdempotencySecretBinding binding = Binding(victimUserId, "confirm");

        JsonElement encrypted = envelope.Encrypt(codes, tokenId, binding);

        Assert.Equal(codes, envelope.Decrypt(encrypted, tokenId, binding));
        string aad = TotpRecoveryCodeEnvelopeV1.BuildAad(tokenId, binding);
        Assert.StartsWith(
            "poolai|v1|idempotency-response|idempotency-request-binding|",
            aad,
            StringComparison.Ordinal);
        Assert.EndsWith("|totp_confirm_response", aad, StringComparison.Ordinal);
        Assert.Throws<CryptographicException>(() =>
        {
            _ = envelope.Decrypt(encrypted, EntityId.New(), binding);
        });
        Assert.Throws<CryptographicException>(() =>
        {
            IdempotencySecretBinding attacker = Binding(EntityId.New(), "confirm");
            _ = envelope.Decrypt(encrypted, tokenId, attacker);
        });
        Assert.Throws<CryptographicException>(() =>
        {
            _ = envelope.Decrypt(
                encrypted,
                tokenId,
                binding with { IdempotencyKey = "another-confirm-key" });
        });
    }

    [Fact]
    public void TotpSetupResponseEnvelopeSupportsSafeIdempotentReplay()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        EntityId tokenId = new(Guid.Parse(
            "019bd5e8-30e0-7d4c-a7f2-bb1db0634045",
            CultureInfo.InvariantCulture));
        EntityId publicChallenge = new(Guid.Parse(
            "019bd5e8-30e0-7d4c-a7f2-bb1db0634046",
            CultureInfo.InvariantCulture));
        IdempotencySecretBinding binding = Binding(
            new EntityId(Guid.Parse(
                "019bd5e8-30e0-7d4c-a7f2-bb1db0634044",
                CultureInfo.InvariantCulture)),
            "setup");
        TotpAuthenticator authenticator = new(new TotpOptions("PoolAI"));
        string uri = authenticator.BuildProvisioningUri(
            Rfc6238Sha1SeedBase32,
            "person@example.test");
        TotpSetupResponseSecret response = new(
            publicChallenge,
            Rfc6238Sha1SeedBase32,
            uri,
            600);
        TotpSetupResponseEnvelopeV1 envelope = new(KeyRing(key));

        JsonElement encrypted = envelope.Encrypt(response, tokenId, binding);

        Assert.Equal(response, envelope.Decrypt(encrypted, tokenId, binding));
        Assert.NotEqual(
            TotpRecoveryCodeEnvelopeV1.BuildAad(tokenId, binding),
            TotpSetupResponseEnvelopeV1.BuildAad(tokenId, binding));
        Assert.Throws<CryptographicException>(() =>
        {
            _ = envelope.Decrypt(encrypted, EntityId.New(), binding);
        });
        Assert.Throws<CryptographicException>(() =>
        {
            IdempotencySecretBinding attacker = Binding(EntityId.New(), "setup");
            _ = envelope.Decrypt(encrypted, tokenId, attacker);
        });
    }

    [Fact]
    public void TotpSetupResponseEnvelopeRejectsUriForAnotherSeed()
    {
        TotpAuthenticator authenticator = new(new TotpOptions("PoolAI"));
        string anotherSeed = Base32.Encode(Enumerable.Repeat((byte)0x5a, 20).ToArray());
        TotpSetupResponseSecret inconsistent = new(
            EntityId.New(),
            Rfc6238Sha1SeedBase32,
            authenticator.BuildProvisioningUri(anotherSeed, "person@example.test"),
            600);
        TotpSetupResponseEnvelopeV1 envelope = new(
            KeyRing(RandomNumberGenerator.GetBytes(32)));

        Assert.Throws<ArgumentException>(() =>
        {
            EntityId userId = EntityId.New();
            _ = envelope.Encrypt(
                inconsistent,
                EntityId.New(),
                Binding(userId, "setup"));
        });
    }

    [Fact]
    public void AccessTokenIssuerUsesInjectedTimeAndSignsEveryFrozenClaim()
    {
        byte[] signingKey = Enumerable.Repeat((byte)0x7b, 32).ToArray();
        DateTimeOffset now = new(2026, 7, 17, 1, 2, 3, TimeSpan.Zero);
        FakeTimeProvider clock = new(now);
        AccessTokenIssuer issuer = new(
            new AccessTokenOptions(
                "PoolAI",
                "PoolAI.Web",
                TimeSpan.FromMinutes(15),
                signingKey),
            clock);
        EntityId userId = new(Guid.Parse(
            "019bd5e8-30e0-7d4c-a7f2-bb1db0634043",
            CultureInfo.InvariantCulture));
        EntityId familyId = new(Guid.Parse(
            "019bd5e8-30e0-7d4c-a7f2-bb1db0634044",
            CultureInfo.InvariantCulture));

        AccessTokenSecret token = issuer.Issue(new AccessTokenSubject(
            userId,
            SystemRole.Operator,
            17,
            familyId));

        string[] segments = token.Token.Split('.');
        Assert.Equal(3, segments.Length);
        using JsonDocument header = JsonDocument.Parse(Base64Url.Decode(segments[0]));
        using JsonDocument payload = JsonDocument.Parse(Base64Url.Decode(segments[1]));
        Assert.Equal("HS256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("JWT", header.RootElement.GetProperty("typ").GetString());
        Assert.Equal("PoolAI", payload.RootElement.GetProperty("iss").GetString());
        Assert.Equal("PoolAI.Web", payload.RootElement.GetProperty("aud").GetString());
        Assert.Equal(userId.Value, payload.RootElement.GetProperty("sub").GetGuid());
        Assert.Equal("operator", payload.RootElement.GetProperty("role").GetString());
        Assert.Equal(17, payload.RootElement.GetProperty("token_version").GetInt64());
        Assert.Equal(familyId.Value, payload.RootElement.GetProperty("sid").GetGuid());
        Assert.True(Guid.TryParse(payload.RootElement.GetProperty("jti").GetString(), out Guid jti));
        Assert.NotEqual(Guid.Empty, jti);
        long issuedAt = now.ToUnixTimeSeconds();
        Assert.Equal(issuedAt, payload.RootElement.GetProperty("iat").GetInt64());
        Assert.Equal(issuedAt, payload.RootElement.GetProperty("nbf").GetInt64());
        Assert.Equal(issuedAt + 900, payload.RootElement.GetProperty("exp").GetInt64());
        Assert.Equal(now.AddMinutes(15), token.ExpiresAt);
        byte[] expectedSignature = HMACSHA256.HashData(
            signingKey,
            Encoding.ASCII.GetBytes(string.Concat(segments[0], ".", segments[1])));
        Assert.Equal(Base64Url.Encode(expectedSignature), segments[2]);
    }

    private static EnvelopeKeyRingOptions KeyRing(byte[] key) => new(
        "identity-k1",
        key,
        new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["identity-k1"] = key,
        });

    private static IdempotencySecretBinding Binding(EntityId userId, string operation) => new(
        userId,
        $"identity:{userId.Value:D}:post:/api/v1/me/totp/{operation}",
        $"{operation}-key",
        Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray());
}
