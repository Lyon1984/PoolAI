using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application;
using PoolAI.Modules.Identity.Application.Ports;
using PoolAI.Modules.Identity.Domain;
using PoolAI.Modules.Identity.Infrastructure.Security;

namespace PoolAI.UnitTests.Identity;

public sealed class ApiKeySecurityPrimitiveTests
{
    [Fact]
    public void CredentialUsesFrozenFormatDisplayPrefixAndDomainSeparatedHmac()
    {
        byte[] pepper = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
        ApiKeyCredentialService service = new(new ApiKeyHashOptions(
            "sk-pool-",
            new ApiKeyPepper(7, pepper),
            previous: null));

        ApiKeyCredential credential = service.Create();

        Assert.StartsWith("sk-pool-", credential.Secret, StringComparison.Ordinal);
        Assert.Equal(51, credential.Secret.Length);
        Assert.Equal(
            credential.Secret[..16],
            credential.DisplayPrefix);
        Assert.Matches("^sk-pool-[A-Za-z0-9_-]{43}$", credential.Secret);
        Assert.Equal(7, credential.PepperVersion);
        byte[] expected = HMACSHA256.HashData(
            pepper,
            Encoding.UTF8.GetBytes("PoolAI:ApiKey:v1:" + credential.Secret));
        Assert.Equal(expected, credential.Hash);
        CryptographicOperations.ZeroMemory(expected);
        CryptographicOperations.ZeroMemory(credential.Hash);
    }

    [Fact]
    public void VerificationSelectsOnlyThePepperNamedByThePersistedRowVersion()
    {
        byte[] current = Enumerable.Repeat((byte)0x31, 32).ToArray();
        byte[] previous = Enumerable.Repeat((byte)0x52, 32).ToArray();
        ApiKeyCredentialService service = new(new ApiKeyHashOptions(
            "sk-test-",
            new ApiKeyPepper(11, current),
            new ApiKeyPepper(9, previous)));
        ApiKeyCredential credential = service.Create();

        byte[] currentHash = Hmac(current, credential.Secret);
        byte[] previousHash = Hmac(previous, credential.Secret);
        Assert.True(service.TryGetDisplayPrefix(
            credential.Secret,
            out string? displayPrefix));
        Assert.Equal(credential.DisplayPrefix, displayPrefix);
        Assert.True(service.Verify(credential.Secret, currentHash, 11));
        Assert.True(service.Verify(credential.Secret, previousHash, 9));
        Assert.False(service.Verify(credential.Secret, currentHash, 99));
        Assert.False(service.TryGetDisplayPrefix("sk-test-invalid", out _));
        Assert.False(service.TryGetDisplayPrefix(
            credential.Secret[..^1] + "=",
            out _));
        CryptographicOperations.ZeroMemory(currentHash);
        CryptographicOperations.ZeroMemory(previousHash);
        CryptographicOperations.ZeroMemory(credential.Hash);
    }

    [Fact]
    public async Task AuthenticatorIgnoresUnknownRowVersionAndUsesOneExactMatch()
    {
        byte[] current = Enumerable.Repeat((byte)0x14, 32).ToArray();
        byte[] previous = Enumerable.Repeat((byte)0x13, 32).ToArray();
        ApiKeyCredentialService credentials = new(new ApiKeyHashOptions(
            "sk-test-",
            new ApiKeyPepper(4, current),
            new ApiKeyPepper(3, previous)));
        ApiKeyCredential issued = credentials.Create();
        byte[] unknownHash = Hmac(current, issued.Secret);
        byte[] currentHash = Hmac(current, issued.Secret);
        CapturingRepository repository = new(
        [
            new ApiKeyAuthenticationCandidate(
                Resource(issued.DisplayPrefix),
                unknownHash,
                PepperVersion: 99),
            new ApiKeyAuthenticationCandidate(
                Resource(issued.DisplayPrefix),
                currentHash,
                PepperVersion: 4),
        ]);
        ApiKeyAuthenticationService authenticator = new(repository, credentials);

        Result<ApiKeyAccessSnapshot> result = await authenticator.AuthenticateAsync(
            issued.Secret,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error.Description);
        Assert.Equal(issued.DisplayPrefix, repository.ObservedDisplayPrefix);
        Assert.All(unknownHash, static value => Assert.Equal(0, value));
        Assert.All(currentHash, static value => Assert.Equal(0, value));
        CryptographicOperations.ZeroMemory(issued.Hash);
    }

    [Fact]
    public async Task AuthenticatorFailsClosedWhenTwoRowsMatchTheSamePresentedSecret()
    {
        byte[] current = Enumerable.Repeat((byte)0x24, 32).ToArray();
        byte[] previous = Enumerable.Repeat((byte)0x23, 32).ToArray();
        ApiKeyCredentialService credentials = new(new ApiKeyHashOptions(
            "sk-test-",
            new ApiKeyPepper(4, current),
            new ApiKeyPepper(3, previous)));
        ApiKeyCredential issued = credentials.Create();
        byte[] currentHash = Hmac(current, issued.Secret);
        byte[] previousHash = Hmac(previous, issued.Secret);
        CapturingRepository repository = new(
        [
            new ApiKeyAuthenticationCandidate(
                Resource(issued.DisplayPrefix),
                currentHash,
                PepperVersion: 4),
            new ApiKeyAuthenticationCandidate(
                Resource(issued.DisplayPrefix),
                previousHash,
                PepperVersion: 3),
        ]);
        ApiKeyAuthenticationService authenticator = new(repository, credentials);

        Result<ApiKeyAccessSnapshot> result = await authenticator.AuthenticateAsync(
            issued.Secret,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(IdentityErrorCodes.InvalidApiKey, result.Error.Code);
        Assert.All(currentHash, static value => Assert.Equal(0, value));
        Assert.All(previousHash, static value => Assert.Equal(0, value));
        CryptographicOperations.ZeroMemory(issued.Hash);
    }

    [Fact]
    public async Task AuthenticatorFailsClosedAndClearsAllHashesAtCandidateSentinel()
    {
        byte[] current = Enumerable.Repeat((byte)0x34, 32).ToArray();
        ApiKeyCredentialService credentials = new(new ApiKeyHashOptions(
            "sk-test-",
            new ApiKeyPepper(4, current),
            previous: null));
        ApiKeyCredential issued = credentials.Create();
        ApiKeyAuthenticationCandidate[] candidates = Enumerable.Range(0, 17)
            .Select(_ => new ApiKeyAuthenticationCandidate(
                Resource(issued.DisplayPrefix),
                Hmac(current, issued.Secret),
                PepperVersion: 4))
            .ToArray();
        ApiKeyAuthenticationService authenticator = new(
            new CapturingRepository(candidates),
            credentials);

        Result<ApiKeyAccessSnapshot> result = await authenticator.AuthenticateAsync(
            issued.Secret,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(IdentityErrorCodes.InvalidApiKey, result.Error.Code);
        Assert.All(
            candidates,
            static candidate => Assert.All(
                candidate.SecretHash,
                static value => Assert.Equal(0, value)));
        CryptographicOperations.ZeroMemory(issued.Hash);
    }

    [Fact]
    public void OptionsDefaultCurrentVersionAndRequireACompleteDistinctPreviousPair()
    {
        ConfigurationManager configuration = new();
        configuration["ApiKeys:CurrentPepper"] =
            Convert.ToBase64String(Enumerable.Repeat((byte)0x21, 32).ToArray());

        ApiKeyHashOptions options = ApiKeyHashOptions.FromConfiguration(configuration);

        Assert.Equal(1, options.Current.Version);
        Assert.Equal("sk-pool-", options.Prefix);
        Assert.Null(options.Previous);

        configuration["ApiKeys:PreviousPepperVersion"] = "2";
        Assert.Throws<InvalidOperationException>(
            () => ApiKeyHashOptions.FromConfiguration(configuration));

        configuration["ApiKeys:PreviousPepper"] =
            configuration["ApiKeys:CurrentPepper"];
        Assert.Throws<ArgumentException>(
            () => ApiKeyHashOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void CidrsAreMaskedCanonicalizedDeduplicatedAndOrdinallySorted()
    {
        IReadOnlyList<string> canonical = ApiKeyInput.AllowedCidrs(
        [
            "2001:0DB8:0:0:1234:0:0:1/64",
            "192.168.1.99/24",
            "2001:db8::1/64",
            "10.9.8.7/8",
        ]);

        Assert.Equal(
        [
            "10.0.0.0/8",
            "192.168.1.0/24",
            "2001:db8::/64",
        ],
            canonical);
    }

    [Theory]
    [InlineData("192.168.001.1/24")]
    [InlineData("192.168.1.1/024")]
    [InlineData("2001:db8::1%eth0/64")]
    [InlineData("::ffff:192.0.2.1/128")]
    [InlineData("::192.168.001.1/120")]
    [InlineData("2001:db8::010.0.0.1/120")]
    [InlineData("2001:db8::1/129")]
    [InlineData("192.0.2.1/33")]
    [InlineData("192.0.2.1")]
    public void InvalidOrAmbiguousCidrsAreRejected(string value)
    {
        Assert.Throws<ArgumentException>(() => ApiKeyInput.CanonicalCidr(value));
    }

    [Fact]
    public void ApiKeyNameCountsUnicodeScalarsAndPreservesLegalEdgeWhitespace()
    {
        string name = "\u00a0"
            + string.Concat(Enumerable.Repeat("\U0001f600", 98))
            + "\u3000";
        string tooLong = string.Concat(
            Enumerable.Repeat("\U0001f600", 101));

        Assert.Same(name, ApiKeyInput.Name(name));
        Assert.Throws<ArgumentException>(() => ApiKeyInput.Name(tooLong));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \u00a0\u3000")]
    [InlineData("name\u0001")]
    [InlineData("name\u0085")]
    [InlineData("name\u2028")]
    [InlineData("name\u2029")]
    public void ApiKeyNameRejectsWhitespaceOnlyAndForbiddenControls(
        string name)
    {
        Assert.Throws<ArgumentException>(() => ApiKeyInput.Name(name));
    }

    [Fact]
    public void AdminReasonCountsUnicodeScalarsAndPreservesLegalEdgeWhitespace()
    {
        string reason = " \u00a0approved \U0001f600\u3000";
        string fiveHundredScalars = string.Concat(
            Enumerable.Repeat("\U0001f600", 500));

        Assert.Same(reason, ApiKeyInput.AdminReason(reason));
        Assert.Same(
            fiveHundredScalars,
            ApiKeyInput.AdminReason(fiveHundredScalars));
        Assert.Throws<ArgumentException>(() => ApiKeyInput.AdminReason(
            fiveHundredScalars + "\U0001f600"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \u00a0\u3000")]
    [InlineData("approved\u0001")]
    [InlineData("approved\u0085")]
    [InlineData("approved\u2028")]
    [InlineData("approved\u2029")]
    public void AdminReasonRejectsWhitespaceOnlyAndForbiddenControls(string reason)
    {
        Assert.Throws<ArgumentException>(() => ApiKeyInput.AdminReason(reason));
    }

    [Fact]
    public void ApiKeyNameAndAdminReasonRejectLoneSurrogates()
    {
        string high = new((char)0xd800, 1);
        string low = new((char)0xdc00, 1);

        Assert.Throws<ArgumentException>(() => ApiKeyInput.Name("name" + high));
        Assert.Throws<ArgumentException>(() => ApiKeyInput.Name("name" + low));
        Assert.Throws<ArgumentException>(
            () => ApiKeyInput.AdminReason("approved" + high));
        Assert.Throws<ArgumentException>(
            () => ApiKeyInput.AdminReason("approved" + low));
    }

    [Fact]
    public void CreateResponseEnvelopeIsBoundToActorScopeKeyHashAndResource()
    {
        byte[] envelopeKey = Enumerable.Repeat((byte)0x61, 32).ToArray();
        EnvelopeKeyRingOptions keyRing = new(
            "unit-v1",
            envelopeKey,
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["unit-v1"] = envelopeKey,
            });
        ApiKeyCreateResponseEnvelopeV1 envelope = new(keyRing);
        EntityId actorId = EntityId.New();
        EntityId userId = EntityId.New();
        EntityId apiKeyId = EntityId.New();
        ApiKeyControlPlaneSnapshot snapshot = Snapshot(apiKeyId, userId);
        byte[] requestHash = SHA256.HashData("request"u8);
        IdempotencySecretBinding binding = new(
            actorId,
            "identity:actor:post:/api/v1/me/api-keys",
            "unit-create-key",
            requestHash);
        ApiKeyCreateResponseSecret response = new(
            snapshot,
            "sk-pool-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            "\"v1\"",
            ApiKeyCreateResponseEnvelopeV1.SelfLocation(apiKeyId));
        JsonElement encrypted = envelope.Encrypt(response, apiKeyId, binding);

        ApiKeyCreateResponseSecret roundTrip =
            envelope.Decrypt(encrypted, apiKeyId, binding);

        Assert.Equal(response.Secret, roundTrip.Secret);
        Assert.Equal(response.ETag, roundTrip.ETag);
        Assert.Equal(response.Location, roundTrip.Location);
        Assert.Equal(response.ApiKey.ApiKeyId, roundTrip.ApiKey.ApiKeyId);
        Assert.Equal(response.ApiKey.AllowedCidrs, roundTrip.ApiKey.AllowedCidrs);
        byte[] otherHash = SHA256.HashData("other-request"u8);
        IdempotencySecretBinding transplanted = binding with
        {
            RequestHash = otherHash,
        };
        Assert.Throws<CryptographicException>(
            () =>
            {
                _ = envelope.Decrypt(encrypted, apiKeyId, transplanted);
            });
        Assert.Throws<CryptographicException>(
            () =>
            {
                _ = envelope.Decrypt(encrypted, EntityId.New(), binding);
            });
        CryptographicOperations.ZeroMemory(requestHash);
        CryptographicOperations.ZeroMemory(otherHash);
    }

    private static ApiKeyControlPlaneSnapshot Snapshot(
        EntityId apiKeyId,
        EntityId userId)
    {
        DateTimeOffset observed = DateTimeOffset.Parse(
            "2026-07-23T01:02:03Z",
            System.Globalization.CultureInfo.InvariantCulture);
        return new ApiKeyControlPlaneSnapshot(
            apiKeyId,
            userId,
            EntityId.New(),
            "Unit key",
            "sk-pool-AAAAAAAA",
            ApiKeyPersistentStatus.Active,
            ApiKeyEffectiveStatus.Active,
            ExpiresAt: null,
            AllowedCidrs: ["192.0.2.0/24"],
            LastUsedAt: null,
            Version: 1,
            observed,
            observed,
            observed);
    }

    private static byte[] Hmac(byte[] pepper, string secret) =>
        HMACSHA256.HashData(
            pepper,
            Encoding.UTF8.GetBytes("PoolAI:ApiKey:v1:" + secret));

    private static ApiKeyResource Resource(string displayPrefix)
    {
        DateTimeOffset observed = DateTimeOffset.Parse(
            "2026-07-23T01:02:03Z",
            System.Globalization.CultureInfo.InvariantCulture);
        return new ApiKeyResource(
            EntityId.New(),
            EntityId.New(),
            EntityId.New(),
            "Unit key",
            displayPrefix,
            ApiKeyPersistentStatus.Active,
            ApiKeyEffectiveStatus.Active,
            ExpiresAt: null,
            AllowedCidrs: [],
            LastUsedAt: null,
            Version: 1,
            observed,
            observed,
            observed);
    }

    private sealed class CapturingRepository(
        IReadOnlyList<ApiKeyAuthenticationCandidate> candidates) : IApiKeyRepository
    {
        internal string? ObservedDisplayPrefix { get; private set; }

        public ValueTask<IReadOnlyList<ApiKeyAuthenticationCandidate>>
            ListAuthenticationCandidatesAsync(
            string displayPrefix,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            ObservedDisplayPrefix = displayPrefix;
            return ValueTask.FromResult(candidates);
        }

        public ValueTask<ApiKeySlice> ListAsync(
            EntityId userId,
            ApiKeyCursor? cursor,
            int limit,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ApiKeyResource?> GetAsync(
            EntityId userId,
            EntityId apiKeyId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ApiKeyCreateResult> CreateAsync(
            ApiKeyCreateWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ApiKeyResource?> LockAsync(
            EntityId userId,
            EntityId apiKeyId,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ApiKeyUpdateResult> UpdateAsync(
            ApiKeyUpdateWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ApiKeyRevokeResult> RevokeAsync(
            ApiKeyRevokeWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ApiKeyRotateResult> RotateAsync(
            ApiKeyRotateWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
