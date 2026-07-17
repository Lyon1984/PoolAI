#pragma warning disable CA5350 // RFC 6238 freezes HMAC-SHA1 for the R1 TOTP profile.
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace PoolAI.EndToEndTests;

public sealed class IdentitySessionHttpEndToEndTests
{
    private static readonly string TotpSecret = string.Concat(
        "GEZD", "GNBV", "GY3T", "QOJQ",
        "GEZD", "GNBV", "GY3T", "QOJQ");
    private static readonly byte[] TotpSeed =
        Encoding.ASCII.GetBytes("12345678901234567890");

    [Fact]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Redis")]
    public async Task PasswordAndTotpAreBothRequiredWhenEnabled()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PasswordResetHttpEndToEndEnvironment environment =
            await CreateTotpEnabledEnvironmentAsync(cancellationToken).ConfigureAwait(true);

        MfaResponse firstChallenge = await LoginAsync(
            environment,
            cancellationToken).ConfigureAwait(true);
        SessionFacts afterPassword = await ReadSessionFactsAsync(
            environment,
            cancellationToken).ConfigureAwait(true);
        AssertPasswordStage(afterPassword);

        string code = TotpCodeAt(TimeProvider.System.GetUtcNow());
        TokenResponse tokens = await VerifyAsync(
            environment,
            firstChallenge.ChallengeId,
            code,
            cancellationToken).ConfigureAwait(true);
        SessionFacts afterTotp = await ReadSessionFactsAsync(
            environment,
            cancellationToken).ConfigureAwait(true);
        AssertTotpStage(afterTotp);
        AssertAccessTokenClaims(
            tokens.AccessToken,
            environment.ActiveUserId,
            Assert.IsType<Guid>(afterTotp.ActiveFamilyId),
            afterTotp.TokenVersion);

        await AssertTotpFailureAsync(
            environment,
            firstChallenge.ChallengeId,
            code,
            "mfa_challenge_invalid",
            cancellationToken).ConfigureAwait(true);

        MfaResponse secondChallenge = await LoginAsync(
            environment,
            cancellationToken).ConfigureAwait(true);
        Assert.NotEqual(firstChallenge.ChallengeId, secondChallenge.ChallengeId);
        await AssertTotpFailureAsync(
            environment,
            secondChallenge.ChallengeId,
            code,
            "totp_code_invalid",
            cancellationToken).ConfigureAwait(true);
        await AssertAuditCountAsync(
            environment,
            "identity.login.totp_replayed",
            expected: 1,
            cancellationToken: cancellationToken).ConfigureAwait(true);

        SessionFacts afterReplays = await ReadSessionFactsAsync(
            environment,
            cancellationToken).ConfigureAwait(true);
        AssertReplayState(afterTotp, afterReplays);
        await AssertRefreshReuseRevokesFamilyAsync(
            environment,
            tokens,
            cancellationToken).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Redis")]
    public async Task ExpiredMfaChallengeDoesNotRevealTotpCorrectness()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PasswordResetHttpEndToEndEnvironment environment =
            await PasswordResetHttpEndToEndEnvironment.CreateAsync(cancellationToken)
                .ConfigureAwait(true);
        await environment.EnableTotpAsync(
            environment.ActiveUserId,
            TotpSecret,
            cancellationToken).ConfigureAwait(true);

        MfaResponse challenge = await LoginAsync(environment, cancellationToken)
            .ConfigureAwait(true);
        await ExpireChallengeAsync(environment, challenge.ChallengeId, cancellationToken)
            .ConfigureAwait(true);
        await AssertTotpFailureAsync(
            environment,
            challenge.ChallengeId,
            "000000",
            "mfa_challenge_invalid",
            cancellationToken).ConfigureAwait(true);
        await AssertTotpFailureAsync(
            environment,
            challenge.ChallengeId,
            TotpCodeAt(TimeProvider.System.GetUtcNow()),
            "mfa_challenge_invalid",
            cancellationToken).ConfigureAwait(true);
    }

    private static async ValueTask<PasswordResetHttpEndToEndEnvironment>
        CreateTotpEnabledEnvironmentAsync(CancellationToken cancellationToken)
    {
        PasswordResetHttpEndToEndEnvironment environment =
            await PasswordResetHttpEndToEndEnvironment.CreateAsync(cancellationToken)
                .ConfigureAwait(false);
        await environment.EnableTotpAsync(
            environment.ActiveUserId,
            TotpSecret,
            cancellationToken).ConfigureAwait(false);
        return environment;
    }

    private static void AssertPasswordStage(SessionFacts facts)
    {
        Assert.Equal(0, facts.RefreshSessions);
        Assert.Equal(0, facts.ActiveRefreshSessions);
        Assert.Equal(1, facts.OpenLoginChallenges);
        Assert.Equal(0, facts.UsedLoginChallenges);
        Assert.Null(facts.LastAcceptedStep);
    }

    private static void AssertTotpStage(SessionFacts facts)
    {
        Assert.Equal(1, facts.RefreshSessions);
        Assert.Equal(1, facts.ActiveRefreshSessions);
        Assert.Equal(0, facts.OpenLoginChallenges);
        Assert.Equal(1, facts.UsedLoginChallenges);
        Assert.NotNull(facts.LastAcceptedStep);
        Assert.NotNull(facts.ActiveFamilyId);
    }

    private static void AssertReplayState(SessionFacts accepted, SessionFacts replayed)
    {
        Assert.Equal(1, replayed.RefreshSessions);
        Assert.Equal(1, replayed.ActiveRefreshSessions);
        Assert.Equal(accepted.LastAcceptedStep, replayed.LastAcceptedStep);
        Assert.Equal(accepted.ActiveFamilyId, replayed.ActiveFamilyId);
    }

    private static async ValueTask<MfaResponse> LoginAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await environment.Client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new
            {
                email = environment.ActiveEmail,
                password = PasswordResetHttpEndToEndEnvironment.OriginalPassword,
            },
            cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(body);
        JsonElement root = json.RootElement;
        Assert.Equal("mfa_required", root.GetProperty("object").GetString());
        Assert.Equal(300, root.GetProperty("expires_in").GetInt32());
        Assert.True(Guid.TryParse(root.GetProperty("challenge_id").GetString(), out Guid challengeId));
        Assert.NotEqual(Guid.Empty, challengeId);
        Assert.False(root.TryGetProperty("access_token", out _));
        Assert.False(root.TryGetProperty("refresh_token", out _));
        return new MfaResponse(challengeId);
    }

    private static async ValueTask<TokenResponse> VerifyAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        Guid challengeId,
        string code,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await environment.Client.PostAsJsonAsync(
            "/api/v1/auth/totp/verify",
            new
            {
                challenge_id = challengeId,
                totp_code = code,
            },
            cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(body);
        JsonElement root = json.RootElement;
        Assert.Equal("Bearer", root.GetProperty("token_type").GetString());
        Assert.Equal(900, root.GetProperty("expires_in").GetInt32());
        Assert.Equal(2_592_000, root.GetProperty("refresh_expires_in").GetInt32());
        string accessToken = Assert.IsType<string>(
            root.GetProperty("access_token").GetString());
        string refreshToken = Assert.IsType<string>(
            root.GetProperty("refresh_token").GetString());
        Assert.Equal(3, accessToken.Split('.').Length);
        Assert.Equal(43, refreshToken.Length);
        return new TokenResponse(accessToken, refreshToken);
    }

    private static async ValueTask AssertTotpFailureAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        Guid challengeId,
        string code,
        string expectedCode,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await environment.Client.PostAsJsonAsync(
            "/api/v1/auth/totp/verify",
            new
            {
                challenge_id = challengeId,
                totp_code = code,
            },
            cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(
            response.Headers.WwwAuthenticate,
            static value => string.Equals(value.Scheme, "Bearer", StringComparison.Ordinal));
        using JsonDocument json = JsonDocument.Parse(body);
        Assert.Equal(expectedCode, json.RootElement.GetProperty("code").GetString());
    }

    private static async ValueTask AssertRefreshReuseRevokesFamilyAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        TokenResponse tokens,
        CancellationToken cancellationToken)
    {
        Task<HttpResponseMessage> first = environment.Client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refresh_token = tokens.RefreshToken },
            cancellationToken);
        Task<HttpResponseMessage> second = environment.Client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refresh_token = tokens.RefreshToken },
            cancellationToken);
        HttpResponseMessage[] responses = await Task.WhenAll(first, second).ConfigureAwait(false);
        using HttpResponseMessage success = Assert.Single(
            responses,
            static response => response.StatusCode == HttpStatusCode.OK);
        using HttpResponseMessage reused = Assert.Single(
            responses,
            static response => response.StatusCode == HttpStatusCode.Unauthorized);
        using JsonDocument problem = JsonDocument.Parse(
            await reused.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        Assert.Equal("refresh_token_reused", problem.RootElement.GetProperty("code").GetString());

        using HttpRequestMessage profile = new(HttpMethod.Get, "/api/v1/me");
        profile.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        using HttpResponseMessage revoked = await environment.Client.SendAsync(
            profile,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);

        using NpgsqlCommand audit = environment.AdministratorDataSource.CreateCommand("""
            SELECT count(*)
            FROM public.audit_logs
            WHERE target_id = $1 AND action = 'identity.refresh.reused';
            """);
        audit.Parameters.AddWithValue(environment.ActiveUserId);
        Assert.Equal(
            1L,
            Assert.IsType<long>(await audit.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)));
        SessionFacts facts = await ReadSessionFactsAsync(environment, cancellationToken)
            .ConfigureAwait(false);
        Assert.Equal(2, facts.RefreshSessions);
        Assert.Equal(0, facts.ActiveRefreshSessions);
    }

    private static async ValueTask AssertAuditCountAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        string action,
        long expected,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = environment.AdministratorDataSource.CreateCommand("""
            SELECT count(*)
            FROM public.audit_logs
            WHERE action = $1;
            """);
        command.Parameters.AddWithValue(action);
        Assert.Equal(
            expected,
            Assert.IsType<long>(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)));
    }

    private static async ValueTask<SessionFacts> ReadSessionFactsAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = environment.AdministratorDataSource.CreateCommand("""
            SELECT
                (SELECT count(*) FROM public.refresh_sessions WHERE user_id = $1),
                (SELECT count(*) FROM public.refresh_sessions
                    WHERE user_id = $1 AND status = 'active'
                      AND expires_at > clock_timestamp()),
                (SELECT count(*) FROM public.one_time_tokens
                    WHERE user_id = $1 AND purpose = 'totp_challenge'
                      AND challenge_kind = 'login'
                      AND used_at IS NULL AND revoked_at IS NULL),
                (SELECT count(*) FROM public.one_time_tokens
                    WHERE user_id = $1 AND purpose = 'totp_challenge'
                      AND challenge_kind = 'login' AND used_at IS NOT NULL),
                users.totp_last_accepted_step,
                users.token_version,
                (SELECT family_id FROM public.refresh_sessions
                    WHERE user_id = $1 AND status = 'active'
                      AND expires_at > clock_timestamp()
                    ORDER BY issued_at DESC, id DESC LIMIT 1)
            FROM public.users AS users
            WHERE users.id = $1;
            """);
        command.Parameters.AddWithValue(environment.ActiveUserId);
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return new SessionFacts(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.GetInt64(5),
            reader.IsDBNull(6) ? null : reader.GetGuid(6));
    }

    private static async ValueTask ExpireChallengeAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        Guid challengeId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = environment.AdministratorDataSource.CreateCommand("""
            UPDATE public.one_time_tokens
            SET expires_at = created_at + interval '1 microsecond'
            WHERE id = $1;
            """);
        command.Parameters.AddWithValue(challengeId);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private static void AssertAccessTokenClaims(
        string accessToken,
        Guid expectedUserId,
        Guid expectedFamilyId,
        long expectedTokenVersion)
    {
        string[] segments = accessToken.Split('.');
        Assert.Equal(3, segments.Length);
        using JsonDocument payload = JsonDocument.Parse(DecodeBase64Url(segments[1]));
        JsonElement claims = payload.RootElement;
        Assert.Equal(expectedUserId, claims.GetProperty("sub").GetGuid());
        Assert.Equal("user", claims.GetProperty("role").GetString());
        Assert.Equal(expectedTokenVersion, claims.GetProperty("token_version").GetInt64());
        Assert.Equal(expectedFamilyId, claims.GetProperty("sid").GetGuid());
        Assert.Equal(
            900,
            claims.GetProperty("exp").GetInt64() - claims.GetProperty("iat").GetInt64());
    }

    private static byte[] DecodeBase64Url(string value)
    {
        string base64 = value.Replace('-', '+').Replace('_', '/');
        int padding = (4 - (base64.Length % 4)) % 4;
        return Convert.FromBase64String(base64.PadRight(base64.Length + padding, '='));
    }

    private static string TotpCodeAt(DateTimeOffset timestamp)
    {
        long step = timestamp.ToUnixTimeSeconds() / 30;
        Span<byte> counter = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        byte[] hmac = HMACSHA1.HashData(TotpSeed, counter);
        try
        {
            int offset = hmac[^1] & 0x0f;
            int binaryCode = BinaryPrimitives.ReadInt32BigEndian(hmac.AsSpan(offset, 4))
                & 0x7fff_ffff;
            return (binaryCode % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hmac);
            CryptographicOperations.ZeroMemory(counter);
        }
    }

    private sealed record MfaResponse(Guid ChallengeId);

    private sealed record TokenResponse(string AccessToken, string RefreshToken);

    private sealed record SessionFacts(
        long RefreshSessions,
        long ActiveRefreshSessions,
        long OpenLoginChallenges,
        long UsedLoginChallenges,
        long? LastAcceptedStep,
        long TokenVersion,
        Guid? ActiveFamilyId);
}
#pragma warning restore CA5350
