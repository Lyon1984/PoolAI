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

    [Theory]
    [InlineData("empty")]
    [InlineData("invalid_resource")]
    [InlineData("prefix")]
    [InlineData("hash_length")]
    [InlineData("pepper_version")]
    [InlineData("duplicate_id")]
    [InlineData("no_match")]
    [InlineData("inactive")]
    public async Task AuthenticatorRejectsEveryMalformedCandidateShape(
        string mutation)
    {
        byte[] pepper = Enumerable.Repeat((byte)0x44, 32).ToArray();
        ApiKeyCredentialService credentials = new(new ApiKeyHashOptions(
            "sk-test-",
            new ApiKeyPepper(4, pepper),
            previous: null));
        ApiKeyCredential issued = credentials.Create();
        IReadOnlyList<ApiKeyAuthenticationCandidate> candidates =
            AuthenticationCandidates(mutation, issued, pepper);
        ApiKeyAuthenticationService authenticator = new(
            new CapturingRepository(candidates),
            credentials);

        Result<ApiKeyAccessSnapshot> result = await authenticator.AuthenticateAsync(
            issued.Secret,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(IdentityErrorCodes.InvalidApiKey, result.Error.Code);
        Assert.All(candidates, static candidate => Assert.All(
            candidate.SecretHash,
            static value => Assert.Equal(0, value)));
        CryptographicOperations.ZeroMemory(issued.Hash);
    }

    [Fact]
    public async Task AuthenticatorRejectsMalformedPresentedKeyBeforeRepositoryAccess()
    {
        (ApiKeyCredentialService credentials, ApiKeyCredential issued) =
            CredentialFixture();
        CapturingRepository repository = new([]);
        ApiKeyAuthenticationService authenticator = new(repository, credentials);

        Result<ApiKeyAccessSnapshot> result = await authenticator.AuthenticateAsync(
            "not-an-api-key",
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Null(repository.ObservedDisplayPrefix);
        Assert.Throws<ArgumentNullException>(
            () => new ApiKeyAuthenticationService(null!, credentials));
        Assert.Throws<ArgumentNullException>(
            () => new ApiKeyAuthenticationService(repository, null!));
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
    public void OptionsRejectEveryMalformedPepperAndPrefixShape()
    {
        string current =
            Convert.ToBase64String(Enumerable.Repeat((byte)0x21, 32).ToArray());
        string previous =
            Convert.ToBase64String(Enumerable.Repeat((byte)0x42, 32).ToArray());

        Assert.Throws<InvalidOperationException>(
            () => ApiKeyHashOptions.FromConfiguration(new ConfigurationManager()));
        AssertInvalidOptions("0", current);
        AssertInvalidOptions("not-a-version", current);
        AssertInvalidOptions("7", "not-base64");
        AssertInvalidOptions(
            "7",
            Convert.ToBase64String(Enumerable.Repeat((byte)0x21, 31).ToArray()));

        ConfigurationManager invalidPrefix = Configuration("7", current);
        invalidPrefix["ApiKeys:Prefix"] = "pool-";
        Assert.Throws<ArgumentException>(
            () => ApiKeyHashOptions.FromConfiguration(invalidPrefix));

        ConfigurationManager missingPreviousVersion = Configuration("7", current);
        missingPreviousVersion["ApiKeys:PreviousPepper"] = previous;
        Assert.Throws<InvalidOperationException>(
            () => ApiKeyHashOptions.FromConfiguration(missingPreviousVersion));

        ConfigurationManager sameVersion = Configuration("7", current);
        sameVersion["ApiKeys:PreviousPepperVersion"] = "7";
        sameVersion["ApiKeys:PreviousPepper"] = previous;
        Assert.Throws<ArgumentException>(
            () => ApiKeyHashOptions.FromConfiguration(sameVersion));

        ConfigurationManager sameSecret = Configuration("7", current);
        sameSecret["ApiKeys:PreviousPepperVersion"] = "6";
        sameSecret["ApiKeys:PreviousPepper"] = current;
        Assert.Throws<ArgumentException>(
            () => ApiKeyHashOptions.FromConfiguration(sameSecret));

        ConfigurationManager valid = Configuration("7", current);
        valid["ApiKeys:PreviousPepperVersion"] = "6";
        valid["ApiKeys:PreviousPepper"] = previous;
        ApiKeyHashOptions options = ApiKeyHashOptions.FromConfiguration(valid);
        Assert.Equal(7, options.Current.Version);
        Assert.Equal(6, options.Previous!.Version);
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
    public void ResourceValidatorRejectsEachRepositoryInvariant()
    {
        ApiKeyResource valid = Resource("sk-pool-AAAAAAAA");
        Assert.True(ApiKeyResourceValidator.IsValid(valid));
        ApiKeyResourceValidator.EnsureValid(valid);

        ApiKeyResource[] invalid =
        [
            valid with { Id = default },
            valid with { UserId = default },
            valid with { GroupId = default },
            valid with { Name = string.Empty },
            valid with { Prefix = string.Empty },
            valid with { Prefix = "sk-pool-AAAAAAA=" },
            valid with { Status = (ApiKeyPersistentStatus)999 },
            valid with { EffectiveStatus = (ApiKeyEffectiveStatus)999 },
            valid with { Version = 0 },
            valid with { CreatedAt = default },
            valid with { UpdatedAt = default },
            valid with { ObservedAt = default },
            valid with { CreatedAt = valid.UpdatedAt.AddSeconds(1) },
            valid with { UpdatedAt = valid.ObservedAt.AddSeconds(1) },
            valid with
            {
                ExpiresAt = valid.ObservedAt.ToOffset(TimeSpan.FromHours(1)),
            },
            valid with
            {
                LastUsedAt = valid.ObservedAt.ToOffset(TimeSpan.FromHours(1)),
            },
            valid with { LastUsedAt = valid.ObservedAt.AddSeconds(1) },
            valid with { CreatedAt = valid.CreatedAt.ToOffset(TimeSpan.FromHours(1)) },
            valid with { UpdatedAt = valid.UpdatedAt.ToOffset(TimeSpan.FromHours(1)) },
            valid with { ObservedAt = valid.ObservedAt.ToOffset(TimeSpan.FromHours(1)) },
            valid with { LastUsedAt = valid.CreatedAt.AddSeconds(-1) },
            valid with { EffectiveStatus = ApiKeyEffectiveStatus.Expired },
            valid with { AllowedCidrs = ["192.0.2.1/24"] },
        ];
        Assert.False(ApiKeyResourceValidator.IsValid(null));
        Assert.All(invalid, static value =>
        {
            Assert.False(ApiKeyResourceValidator.IsValid(value));
            Assert.Throws<InvalidOperationException>(
                () => ApiKeyResourceValidator.EnsureValid(value));
        });
    }

    [Theory]
    [InlineData("null_api_key")]
    [InlineData("invalid_resource")]
    [InlineData("status_code")]
    [InlineData("user")]
    [InlineData("group")]
    [InlineData("name")]
    [InlineData("expires")]
    [InlineData("cidrs")]
    [InlineData("status")]
    [InlineData("version")]
    [InlineData("last_used")]
    [InlineData("timestamps")]
    [InlineData("secret")]
    [InlineData("prefix")]
    [InlineData("etag")]
    [InlineData("location")]
    public void CreateOutcomeValidatorRejectsEverySingleFieldDrift(
        string mutation)
    {
        (
            ApiKeyCreatedOutcomeValidator validator,
            CreateApiKeyCommand command,
            ApiKeyCreatedOutcome valid) = ValidCreateOutcome(
                ApiKeyAccessMode.Self);
        ApiKeyCreatedOutcome invalid = MutateCreateOutcome(valid, mutation);

        Assert.Throws<InvalidOperationException>(
            () => validator.EnsureValid(command, invalid));
    }

    [Theory]
    [InlineData("null_api_key")]
    [InlineData("invalid_resource")]
    [InlineData("status_code")]
    [InlineData("same_key")]
    [InlineData("user")]
    [InlineData("status")]
    [InlineData("version")]
    [InlineData("last_used")]
    [InlineData("timestamps")]
    [InlineData("secret")]
    [InlineData("prefix")]
    [InlineData("etag")]
    [InlineData("location")]
    public void RotateOutcomeValidatorRejectsEverySingleFieldDrift(
        string mutation)
    {
        (
            ApiKeyCreatedOutcomeValidator validator,
            RotateApiKeyCommand command,
            ApiKeyCreatedOutcome valid) = ValidRotateOutcome(
                ApiKeyAccessMode.Self);
        ApiKeyCreatedOutcome invalid = MutateRotateOutcome(
            valid,
            command.ApiKeyId,
            mutation);

        Assert.Throws<InvalidOperationException>(
            () => validator.EnsureValid(command, invalid));
    }

    [Fact]
    public void OutcomeValidatorAcceptsBothAccessModesAndRejectsUnknownMode()
    {
        (ApiKeyCreatedOutcomeValidator selfCreate, CreateApiKeyCommand selfCommand,
            ApiKeyCreatedOutcome selfOutcome) =
            ValidCreateOutcome(ApiKeyAccessMode.Self);
        selfCreate.EnsureValid(selfCommand, selfOutcome);
        (ApiKeyCreatedOutcomeValidator adminCreate, CreateApiKeyCommand adminCommand,
            ApiKeyCreatedOutcome adminOutcome) =
            ValidCreateOutcome(ApiKeyAccessMode.AdminProxy);
        adminCreate.EnsureValid(adminCommand, adminOutcome);

        (ApiKeyCreatedOutcomeValidator selfRotate, RotateApiKeyCommand selfRotateCommand,
            ApiKeyCreatedOutcome selfRotateOutcome) =
            ValidRotateOutcome(ApiKeyAccessMode.Self);
        selfRotate.EnsureValid(selfRotateCommand, selfRotateOutcome);
        (ApiKeyCreatedOutcomeValidator adminRotate, RotateApiKeyCommand adminRotateCommand,
            ApiKeyCreatedOutcome adminRotateOutcome) =
            ValidRotateOutcome(ApiKeyAccessMode.AdminProxy);
        adminRotate.EnsureValid(adminRotateCommand, adminRotateOutcome);

        Assert.Throws<InvalidOperationException>(() => selfCreate.EnsureValid(
            selfCommand with { AccessMode = (ApiKeyAccessMode)999 },
            selfOutcome));
        Assert.Throws<InvalidOperationException>(() => selfRotate.EnsureValid(
            selfRotateCommand with { AccessMode = (ApiKeyAccessMode)999 },
            selfRotateOutcome));
        Assert.Throws<ArgumentNullException>(
            () => selfCreate.EnsureValid(
                (CreateApiKeyCommand)null!,
                selfOutcome));
        Assert.Throws<ArgumentNullException>(
            () => selfCreate.EnsureValid(selfCommand, null!));
        Assert.Throws<ArgumentNullException>(
            () => selfRotate.EnsureValid(
                (RotateApiKeyCommand)null!,
                selfRotateOutcome));
        Assert.Throws<ArgumentNullException>(
            () => selfRotate.EnsureValid(selfRotateCommand, null!));
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

    [Theory]
    [InlineData("api_key")]
    [InlineData("id")]
    [InlineData("version")]
    [InlineData("observed")]
    [InlineData("created")]
    [InlineData("updated")]
    [InlineData("name")]
    [InlineData("prefix")]
    [InlineData("secret")]
    [InlineData("etag")]
    [InlineData("location")]
    public void CreateResponseEnvelopeRejectsEveryInvalidResponseShape(
        string mutation)
    {
        (
            ApiKeyCreateResponseEnvelopeV1 envelope,
            ApiKeyCreateResponseSecret response,
            EntityId apiKeyId,
            IdempotencySecretBinding binding) = EnvelopeFixture();
        ApiKeyCreateResponseSecret invalid =
            MutateEnvelopeResponse(response, mutation);

        Assert.Throws<ArgumentException>(
            () => envelope.Encrypt(invalid, apiKeyId, binding));
    }

    [Theory]
    [InlineData(ApiKeyPersistentStatus.Active, ApiKeyEffectiveStatus.Active)]
    [InlineData(ApiKeyPersistentStatus.Active, ApiKeyEffectiveStatus.Expired)]
    [InlineData(ApiKeyPersistentStatus.Disabled, ApiKeyEffectiveStatus.Disabled)]
    [InlineData(ApiKeyPersistentStatus.Revoked, ApiKeyEffectiveStatus.Revoked)]
    public void RotateResponseEnvelopeRoundTripsEveryLifecycleStatus(
        ApiKeyPersistentStatus status,
        ApiKeyEffectiveStatus effectiveStatus)
    {
        (
            ApiKeyCreateResponseEnvelopeV1 envelope,
            ApiKeyCreateResponseSecret response,
            EntityId apiKeyId,
            IdempotencySecretBinding binding) = EnvelopeFixture();
        ApiKeyControlPlaneSnapshot apiKey = response.ApiKey with
        {
            Status = status,
            EffectiveStatus = effectiveStatus,
        };
        ApiKeyCreateResponseSecret expected = response with { ApiKey = apiKey };

        JsonElement encrypted = envelope.EncryptRotate(
            expected,
            apiKeyId,
            binding);
        ApiKeyCreateResponseSecret actual = envelope.DecryptRotate(
            encrypted,
            apiKeyId,
            binding);

        Assert.Equal(status, actual.ApiKey.Status);
        Assert.Equal(effectiveStatus, actual.ApiKey.EffectiveStatus);
    }

    [Fact]
    public void CreateResponseEnvelopeAcceptsAdminLocationAndRejectsInvalidEnums()
    {
        (
            ApiKeyCreateResponseEnvelopeV1 envelope,
            ApiKeyCreateResponseSecret response,
            EntityId apiKeyId,
            IdempotencySecretBinding binding) = EnvelopeFixture();
        ApiKeyCreateResponseSecret admin = response with
        {
            Location = ApiKeyCreateResponseEnvelopeV1.Location(
                response.ApiKey.UserId,
                apiKeyId),
        };
        _ = envelope.Encrypt(admin, apiKeyId, binding);

        Assert.Throws<ArgumentOutOfRangeException>(() => envelope.Encrypt(
            response with
            {
                ApiKey = response.ApiKey with
                {
                    Status = (ApiKeyPersistentStatus)999,
                },
            },
            apiKeyId,
            binding));
        Assert.Throws<ArgumentOutOfRangeException>(() => envelope.Encrypt(
            response with
            {
                ApiKey = response.ApiKey with
                {
                    EffectiveStatus = (ApiKeyEffectiveStatus)999,
                },
            },
            apiKeyId,
            binding));
        Assert.Throws<ArgumentNullException>(
            () => envelope.Encrypt(null!, apiKeyId, binding));
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("api_key")]
    [InlineData("id")]
    [InlineData("status")]
    [InlineData("effective_status")]
    [InlineData("allowed_cidrs")]
    [InlineData("secret")]
    [InlineData("etag")]
    [InlineData("location")]
    public void CreateResponseEnvelopeRejectsEveryMalformedDecryptedPayload(
        string mutation)
    {
        (
            ApiKeyCreateResponseEnvelopeV1 envelope,
            ApiKeyCreateResponseSecret response,
            EntityId apiKeyId,
            IdempotencySecretBinding binding) = EnvelopeFixture();
        byte[] plaintext = RawEnvelopePayload(response, mutation);
        JsonElement encrypted = new AeadEnvelopeV1(EnvelopeKeyRing()).Encrypt(
            plaintext,
            IdempotencyResponseAad.Build(
                "api_key_create_response",
                apiKeyId,
                binding));

        Assert.Throws<CryptographicException>(
            () => envelope.Decrypt(encrypted, apiKeyId, binding));
        CryptographicOperations.ZeroMemory(plaintext);
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

    private static (
        ApiKeyCreateResponseEnvelopeV1 Envelope,
        ApiKeyCreateResponseSecret Response,
        EntityId ApiKeyId,
        IdempotencySecretBinding Binding) EnvelopeFixture()
    {
        EnvelopeKeyRingOptions keyRing = EnvelopeKeyRing();
        EntityId actorId = EntityId.New();
        EntityId userId = EntityId.New();
        EntityId apiKeyId = EntityId.New();
        byte[] requestHash = SHA256.HashData("envelope-request"u8);
        ApiKeyCreateResponseSecret response = new(
            Snapshot(apiKeyId, userId),
            "sk-pool-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            "\"v1\"",
            ApiKeyCreateResponseEnvelopeV1.SelfLocation(apiKeyId));
        IdempotencySecretBinding binding = new(
            actorId,
            "identity:actor:post:/api/v1/me/api-keys",
            "unit-envelope-key",
            requestHash);
        return (
            new ApiKeyCreateResponseEnvelopeV1(keyRing),
            response,
            apiKeyId,
            binding);
    }

    private static EnvelopeKeyRingOptions EnvelopeKeyRing()
    {
        byte[] key = Enumerable.Repeat((byte)0x71, 32).ToArray();
        return new EnvelopeKeyRingOptions(
            "unit-v1",
            key,
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["unit-v1"] = key,
            });
    }

    private static byte[] RawEnvelopePayload(
        ApiKeyCreateResponseSecret response,
        string mutation)
    {
        if (string.Equals(mutation, "payload", StringComparison.Ordinal))
        {
            return "null"u8.ToArray();
        }

        ApiKeyControlPlaneSnapshot value = response.ApiKey;
        Dictionary<string, object?> apiKey = new(StringComparer.Ordinal)
        {
            ["id"] = value.ApiKeyId.Value,
            ["user_id"] = value.UserId.Value,
            ["group_id"] = value.GroupId.Value,
            ["name"] = value.Name,
            ["prefix"] = value.Prefix,
            ["status"] = "active",
            ["effective_status"] = "active",
            ["expires_at"] = value.ExpiresAt,
            ["allowed_cidrs"] = value.AllowedCidrs,
            ["last_used_at"] = value.LastUsedAt,
            ["version"] = value.Version,
            ["created_at"] = value.CreatedAt,
            ["updated_at"] = value.UpdatedAt,
            ["observed_at"] = value.ObservedAt,
        };
        Dictionary<string, object?> payload = new(StringComparer.Ordinal)
        {
            ["api_key"] = apiKey,
            ["secret"] = response.Secret,
            ["etag"] = response.ETag,
            ["location"] = response.Location,
        };
        MutateRawEnvelopePayload(payload, apiKey, mutation);
        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    private static void MutateRawEnvelopePayload(
        Dictionary<string, object?> payload,
        Dictionary<string, object?> apiKey,
        string mutation)
    {
        switch (mutation)
        {
            case "api_key":
                payload["api_key"] = null;
                break;
            case "id":
                apiKey["id"] = Guid.Empty;
                break;
            case "status":
                apiKey["status"] = "unknown";
                break;
            case "effective_status":
                apiKey["effective_status"] = "unknown";
                break;
            case "allowed_cidrs":
                apiKey["allowed_cidrs"] = null;
                break;
            case "secret":
            case "etag":
            case "location":
                payload[mutation] = " ";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
    }

    private static IReadOnlyList<ApiKeyAuthenticationCandidate>
        AuthenticationCandidates(
            string mutation,
            ApiKeyCredential issued,
            byte[] pepper)
    {
        ApiKeyResource resource = Resource(issued.DisplayPrefix);
        byte[] matching = Hmac(pepper, issued.Secret);
        ApiKeyAuthenticationCandidate valid = new(
            resource,
            matching,
            PepperVersion: 4);
        return mutation switch
        {
            "empty" => [],
            "invalid_resource" =>
            [
                valid with { ApiKey = resource with { Id = default } },
            ],
            "prefix" =>
            [
                valid with
                {
                    ApiKey = resource with { Prefix = "sk-test-BBBBBBBB" },
                },
            ],
            "hash_length" =>
            [
                valid with { SecretHash = [0x01] },
            ],
            "pepper_version" =>
            [
                valid with { PepperVersion = 0 },
            ],
            "duplicate_id" =>
            [
                valid,
                valid with { SecretHash = Hmac(pepper, issued.Secret) },
            ],
            "no_match" =>
            [
                valid with { SecretHash = RandomNumberGenerator.GetBytes(32) },
            ],
            "inactive" =>
            [
                valid with
                {
                    ApiKey = resource with
                    {
                        Status = ApiKeyPersistentStatus.Disabled,
                        EffectiveStatus = ApiKeyEffectiveStatus.Disabled,
                    },
                },
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
    }

    private static ApiKeyCreateResponseSecret MutateEnvelopeResponse(
        ApiKeyCreateResponseSecret response,
        string mutation) => mutation switch
        {
            "api_key" => response with { ApiKey = null! },
            "id" => response with
            {
                ApiKey = response.ApiKey with { ApiKeyId = EntityId.New() },
            },
            "version" => response with
            {
                ApiKey = response.ApiKey with { Version = 0 },
            },
            "observed" => response with
            {
                ApiKey = response.ApiKey with { ObservedAt = default },
            },
            "created" => response with
            {
                ApiKey = response.ApiKey with { CreatedAt = default },
            },
            "updated" => response with
            {
                ApiKey = response.ApiKey with { UpdatedAt = default },
            },
            "name" => response with
            {
                ApiKey = response.ApiKey with { Name = " " },
            },
            "prefix" => response with
            {
                ApiKey = response.ApiKey with { Prefix = " " },
            },
            "secret" => response with { Secret = " " },
            "etag" => response with { ETag = "\"v2\"" },
            "location" => response with { Location = "/wrong" },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

    private static ApiKeyCreatedOutcome MutateCreateOutcome(
        ApiKeyCreatedOutcome valid,
        string mutation)
    {
        ApiKeyControlPlaneSnapshot apiKey = valid.ApiKey;
        return mutation switch
        {
            "null_api_key" => valid with { ApiKey = null! },
            "invalid_resource" => valid with
            {
                ApiKey = apiKey with { ApiKeyId = default },
            },
            "status_code" => valid with { StatusCode = 200 },
            "user" => valid with
            {
                ApiKey = apiKey with { UserId = EntityId.New() },
            },
            "group" => valid with
            {
                ApiKey = apiKey with { GroupId = EntityId.New() },
            },
            "name" => valid with
            {
                ApiKey = apiKey with { Name = "Different key" },
            },
            "expires" => valid with
            {
                ApiKey = apiKey with { ExpiresAt = apiKey.ObservedAt.AddDays(1) },
            },
            "cidrs" => valid with { ApiKey = apiKey with { AllowedCidrs = [] } },
            "status" => valid with
            {
                ApiKey = apiKey with
                {
                    Status = ApiKeyPersistentStatus.Disabled,
                    EffectiveStatus = ApiKeyEffectiveStatus.Disabled,
                },
            },
            "version" => valid with { ApiKey = apiKey with { Version = 2 } },
            "last_used" => valid with
            {
                ApiKey = apiKey with { LastUsedAt = apiKey.ObservedAt },
            },
            "timestamps" => TimestampDrift(valid, apiKey),
            "secret" => valid with { Secret = "not-an-api-key" },
            "prefix" => valid with
            {
                ApiKey = apiKey with { Prefix = "sk-pool-BBBBBBBB" },
            },
            "etag" => valid with { ETag = "\"v2\"" },
            "location" => valid with { Location = "/wrong" },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
    }

    private static ApiKeyCreatedOutcome MutateRotateOutcome(
        ApiKeyCreatedOutcome valid,
        EntityId currentApiKeyId,
        string mutation)
    {
        ApiKeyControlPlaneSnapshot apiKey = valid.ApiKey;
        return mutation switch
        {
            "null_api_key" => valid with { ApiKey = null! },
            "invalid_resource" => valid with
            {
                ApiKey = apiKey with { GroupId = default },
            },
            "status_code" => valid with { StatusCode = 200 },
            "same_key" => valid with
            {
                ApiKey = apiKey with { ApiKeyId = currentApiKeyId },
            },
            "user" => valid with
            {
                ApiKey = apiKey with { UserId = EntityId.New() },
            },
            "status" => valid with
            {
                ApiKey = apiKey with
                {
                    Status = ApiKeyPersistentStatus.Disabled,
                    EffectiveStatus = ApiKeyEffectiveStatus.Disabled,
                },
            },
            "version" => valid with { ApiKey = apiKey with { Version = 2 } },
            "last_used" => valid with
            {
                ApiKey = apiKey with { LastUsedAt = apiKey.ObservedAt },
            },
            "timestamps" => TimestampDrift(valid, apiKey),
            "secret" => valid with { Secret = "not-an-api-key" },
            "prefix" => valid with
            {
                ApiKey = apiKey with { Prefix = "sk-pool-BBBBBBBB" },
            },
            "etag" => valid with { ETag = "\"v2\"" },
            "location" => valid with { Location = "/wrong" },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
    }

    private static ApiKeyCreatedOutcome TimestampDrift(
        ApiKeyCreatedOutcome valid,
        ApiKeyControlPlaneSnapshot apiKey) => valid with
        {
            ApiKey = apiKey with
            {
                UpdatedAt = apiKey.CreatedAt.AddMilliseconds(1),
                ObservedAt = apiKey.CreatedAt.AddMilliseconds(1),
            },
        };

    private static (
        ApiKeyCreatedOutcomeValidator Validator,
        CreateApiKeyCommand Command,
        ApiKeyCreatedOutcome Outcome) ValidCreateOutcome(
        ApiKeyAccessMode accessMode)
    {
        (ApiKeyCredentialService credentials, ApiKeyCredential credential) =
            CredentialFixture();
        EntityId userId = EntityId.New();
        EntityId groupId = EntityId.New();
        ApiKeyActor actor = accessMode == ApiKeyAccessMode.Self
            ? new ApiKeyActor(userId, SystemRole.User, TokenVersion: 1)
            : new ApiKeyActor(EntityId.New(), SystemRole.Admin, TokenVersion: 1);
        CreateApiKeyCommand command = new(
            EntityId.New(),
            actor,
            accessMode,
            userId,
            groupId,
            "api-key-validator-create",
            "Valid key",
            ExpiresAt: null,
            AllowedCidrs: ["192.0.2.17/24"],
            Reason: accessMode == ApiKeyAccessMode.AdminProxy
                ? "approved create"
                : null,
            IpAddress: null,
            UserAgent: null);
        DateTimeOffset observed = DateTimeOffset.Parse(
            "2026-07-23T01:02:03Z",
            System.Globalization.CultureInfo.InvariantCulture);
        ApiKeyControlPlaneSnapshot apiKey = new(
            EntityId.New(),
            userId,
            groupId,
            command.Name,
            credential.DisplayPrefix,
            ApiKeyPersistentStatus.Active,
            ApiKeyEffectiveStatus.Active,
            ExpiresAt: null,
            AllowedCidrs: ["192.0.2.0/24"],
            LastUsedAt: null,
            Version: 1,
            observed,
            observed,
            observed);
        string location = accessMode == ApiKeyAccessMode.Self
            ? $"/api/v1/me/api-keys/{apiKey.ApiKeyId.Value:D}"
            : $"/api/v1/admin/users/{userId.Value:D}/api-keys/{apiKey.ApiKeyId.Value:D}";
        ApiKeyCreatedOutcome outcome = new(
            201,
            IsReplay: false,
            apiKey,
            credential.Secret,
            "\"v1\"",
            location);
        CryptographicOperations.ZeroMemory(credential.Hash);
        return (new ApiKeyCreatedOutcomeValidator(credentials), command, outcome);
    }

    private static (
        ApiKeyCreatedOutcomeValidator Validator,
        RotateApiKeyCommand Command,
        ApiKeyCreatedOutcome Outcome) ValidRotateOutcome(
        ApiKeyAccessMode accessMode)
    {
        (ApiKeyCredentialService credentials, ApiKeyCredential credential) =
            CredentialFixture();
        EntityId userId = EntityId.New();
        ApiKeyActor actor = accessMode == ApiKeyAccessMode.Self
            ? new ApiKeyActor(userId, SystemRole.User, TokenVersion: 1)
            : new ApiKeyActor(EntityId.New(), SystemRole.Admin, TokenVersion: 1);
        RotateApiKeyCommand command = new(
            EntityId.New(),
            actor,
            accessMode,
            userId,
            EntityId.New(),
            "api-key-validator-rotate",
            ExpectedVersion: 1,
            Reason: "approved rotation",
            IpAddress: null,
            UserAgent: null);
        DateTimeOffset observed = DateTimeOffset.Parse(
            "2026-07-23T01:02:03Z",
            System.Globalization.CultureInfo.InvariantCulture);
        ApiKeyControlPlaneSnapshot apiKey = new(
            EntityId.New(),
            userId,
            EntityId.New(),
            "Valid key",
            credential.DisplayPrefix,
            ApiKeyPersistentStatus.Active,
            ApiKeyEffectiveStatus.Active,
            ExpiresAt: null,
            AllowedCidrs: ["192.0.2.0/24"],
            LastUsedAt: null,
            Version: 1,
            observed,
            observed,
            observed);
        string location = accessMode == ApiKeyAccessMode.Self
            ? $"/api/v1/me/api-keys/{apiKey.ApiKeyId.Value:D}"
            : $"/api/v1/admin/users/{userId.Value:D}/api-keys/{apiKey.ApiKeyId.Value:D}";
        ApiKeyCreatedOutcome outcome = new(
            201,
            IsReplay: false,
            apiKey,
            credential.Secret,
            "\"v1\"",
            location);
        CryptographicOperations.ZeroMemory(credential.Hash);
        return (new ApiKeyCreatedOutcomeValidator(credentials), command, outcome);
    }

    private static (ApiKeyCredentialService Service, ApiKeyCredential Credential)
        CredentialFixture()
    {
        byte[] pepper = Enumerable.Repeat((byte)0x63, 32).ToArray();
        ApiKeyCredentialService service = new(new ApiKeyHashOptions(
            "sk-pool-",
            new ApiKeyPepper(7, pepper),
            previous: null));
        return (service, service.Create());
    }

    private static byte[] Hmac(byte[] pepper, string secret) =>
        HMACSHA256.HashData(
            pepper,
            Encoding.UTF8.GetBytes("PoolAI:ApiKey:v1:" + secret));

    private static ConfigurationManager Configuration(
        string currentVersion,
        string currentPepper)
    {
        ConfigurationManager configuration = new();
        configuration["ApiKeys:CurrentPepperVersion"] = currentVersion;
        configuration["ApiKeys:CurrentPepper"] = currentPepper;
        return configuration;
    }

    private static void AssertInvalidOptions(
        string currentVersion,
        string currentPepper)
    {
        ConfigurationManager configuration =
            Configuration(currentVersion, currentPepper);
        Assert.Throws<InvalidOperationException>(
            () => ApiKeyHashOptions.FromConfiguration(configuration));
    }

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

        public ValueTask<ApiKeyResource?> LockForMutationAsync(
            EntityId userId,
            EntityId apiKeyId,
            EntityId expectedGroupId,
            long expectedVersion,
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
