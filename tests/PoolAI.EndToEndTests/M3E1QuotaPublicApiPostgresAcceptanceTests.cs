using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using NpgsqlTypes;
using PoolAI.BuildingBlocks;
using PoolAI.Database.Migrations;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application;
using PoolAI.Modules.Identity.Abstractions;
using Testcontainers.PostgreSql;

#pragma warning disable MA0048 // The acceptance fixture and its records intentionally share one scenario file.
#pragma warning disable MA0051 // The acceptance fixture keeps the complete public-API/PostgreSQL proof visible.

namespace PoolAI.EndToEndTests;

[Collection(M3E1QuotaPostgresApiTestGroup.Name)]
public sealed class M3E1QuotaPublicApiPostgresAcceptanceTests(
    M3E1QuotaPostgresApiFixture fixture)
{
    private static readonly string[] QuotaResponseProperties =
    [
        "consumed_tokens",
        "group_id",
        "overage_tokens",
        "period_ended_at",
        "period_id",
        "period_started_at",
        "remaining_tokens",
        "reserved_tokens",
        "status",
        "total_tokens",
        "updated_at",
        "version",
    ];

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task NonAdminQuotaMutationsAreDeniedOnceAndLeaveEveryQuotaFactUnchanged()
    {
        // Governing contract: AC-004 requires every authenticated non-Admin
        // adjust/reset attempt to return 403, append exactly one denial audit,
        // and leave the quota period/version/event/idempotency/outbox unchanged.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        M3E1QuotaScenario scenario = await fixture.SeedScenarioAsync(
            "denied",
            totalTokens: 1_000,
            consumedTokens: 300,
            reservedTokens: 50,
            cancellationToken).ConfigureAwait(true);

        foreach ((string role, string operation) in new[]
                 {
                     ("operator", "adjust"),
                     ("operator", "reset"),
                     ("auditor", "adjust"),
                     ("auditor", "reset"),
                     ("user", "adjust"),
                     ("user", "reset"),
                 })
        {
            QuotaInvariantSnapshot before = await fixture.ReadInvariantSnapshotAsync(
                scenario.GroupId,
                cancellationToken).ConfigureAwait(true);
            long auditsBefore = await fixture.CountAuditsAsync(
                scenario.GroupId,
                cancellationToken).ConfigureAwait(true);
            using HttpClient client = fixture.AuthenticatedClient(
                role,
                scenario.Actor(role));
            string key = $"m3-e1-denied-{role}-{operation}-{Guid.NewGuid():N}";
            using HttpRequestMessage request = QuotaCommand(
                scenario.GroupId,
                operation,
                totalTokens: 750,
                reason: $"AC-004 {role} {operation} denial",
                key,
                ifMatch: "\"v1\"");

            using HttpResponseMessage response = await client.SendAsync(
                request,
                cancellationToken).ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Guid requestId = await AssertProblemAsync(
                response,
                "role_required",
                cancellationToken).ConfigureAwait(true);
            Assert.Equal(
                auditsBefore + 1,
                await fixture.CountAuditsAsync(
                    scenario.GroupId,
                    cancellationToken).ConfigureAwait(true));
            await fixture.AssertSingleDenialAuditAsync(
                requestId,
                scenario.GroupId,
                scenario.Actor(role),
                role,
                operation,
                key,
                cancellationToken).ConfigureAwait(true);
            Assert.Equal(
                0,
                await fixture.CountIdempotencyByKeyAsync(
                    key,
                    cancellationToken).ConfigureAwait(true));
            Assert.Equal(
                before,
                await fixture.ReadInvariantSnapshotAsync(
                    scenario.GroupId,
                    cancellationToken).ConfigureAwait(true));
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task RejectedOrUnavailableDenialGateWritesNoPostgreSqlFact()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        M3E1QuotaScenario scenario = await fixture.SeedScenarioAsync(
            "denial-gate",
            totalTokens: 1_000,
            consumedTokens: 300,
            reservedTokens: 50,
            cancellationToken).ConfigureAwait(true);

        (QuotaMutationDenialRateLimitDecision Decision, HttpStatusCode Status,
            string Code, TimeSpan RetryAfter)[] cases =
        [
            (QuotaMutationDenialRateLimitDecision.Rejected(17),
                HttpStatusCode.TooManyRequests,
                GroupErrorCodes.RateLimitExceeded,
                TimeSpan.FromSeconds(17)),
            (QuotaMutationDenialRateLimitDecision.Unavailable,
                HttpStatusCode.ServiceUnavailable,
                GroupErrorCodes.CoordinationUnavailable,
                TimeSpan.FromSeconds(1)),
        ];

        try
        {
            foreach ((QuotaMutationDenialRateLimitDecision decision,
                         HttpStatusCode status, string code, TimeSpan retryAfter) in cases)
            {
                fixture.SetDenialRateLimitDecision(decision);
                QuotaInvariantSnapshot before = await fixture.ReadInvariantSnapshotAsync(
                    scenario.GroupId,
                    cancellationToken).ConfigureAwait(true);
                long auditsBefore = await fixture.CountAuditsAsync(
                    scenario.GroupId,
                    cancellationToken).ConfigureAwait(true);
                using HttpClient client = fixture.AuthenticatedClient(
                    "operator",
                    scenario.Actor("operator"));
                using HttpRequestMessage request = QuotaCommand(
                    scenario.GroupId,
                    "adjust",
                    totalTokens: 750,
                    reason: "denial gate must precede PostgreSQL",
                    $"m3-e1-denial-gate-{code}",
                    ifMatch: "\"v1\"");

                using HttpResponseMessage response = await client.SendAsync(
                    request,
                    cancellationToken).ConfigureAwait(true);

                Assert.Equal(status, response.StatusCode);
                Assert.Equal(retryAfter, response.Headers.RetryAfter?.Delta);
                _ = await AssertProblemAsync(
                    response,
                    code,
                    cancellationToken,
                    expectedRetryable: true)
                    .ConfigureAwait(true);
                Assert.Equal(
                    auditsBefore,
                    await fixture.CountAuditsAsync(
                        scenario.GroupId,
                        cancellationToken).ConfigureAwait(true));
                Assert.Equal(
                    before,
                    await fixture.ReadInvariantSnapshotAsync(
                        scenario.GroupId,
                        cancellationToken).ConfigureAwait(true));
            }
        }
        finally
        {
            fixture.SetDenialRateLimitDecision(
                QuotaMutationDenialRateLimitDecision.Allowed);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task AdminAdjustResetAndExactReplaysUseProductionAtomicPersistence()
    {
        // Governing contracts: M3-E1, AC-017 and AC-018. The public API must
        // expose canonical string counters/strong ETags while each production
        // command commits quota, event, outbox, audit and idempotency together.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        M3E1QuotaScenario scenario = await fixture.SeedScenarioAsync(
            "admin",
            totalTokens: 1_000,
            consumedTokens: 300,
            reservedTokens: 50,
            cancellationToken).ConfigureAwait(true);
        using HttpClient admin = fixture.AuthenticatedClient(
            "admin",
            scenario.Actor("admin"));

        const string adjustKey = "m3-e1-public-api-adjust";
        const string adjustReason = "Lower total below consumed for acceptance";
        using HttpRequestMessage adjust = QuotaCommand(
            scenario.GroupId,
            "adjust",
            totalTokens: 275,
            adjustReason,
            adjustKey,
            ifMatch: "\"v1\"");
        using HttpResponseMessage adjusted = await admin.SendAsync(
            adjust,
            cancellationToken).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, adjusted.StatusCode);
        Assert.Equal("\"v2\"", adjusted.Headers.ETag?.Tag);
        Guid adjustRequestId = RequestId(adjusted);
        string frozenAdjustBody = await adjusted.Content.ReadAsStringAsync(
            cancellationToken).ConfigureAwait(true);
        Guid adjustedPeriodId = AssertQuotaResponse(
            frozenAdjustBody,
            scenario.GroupId,
            scenario.InitialPeriodId,
            status: "exhausted",
            total: "275",
            consumed: "300",
            reserved: "50",
            remaining: "0",
            overage: "25",
            version: 2);
        Assert.Equal(scenario.InitialPeriodId, adjustedPeriodId);

        QuotaInvariantSnapshot afterAdjust = await fixture.ReadInvariantSnapshotAsync(
            scenario.GroupId,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            new QuotaInvariantSnapshot(
                scenario.InitialPeriodId,
                QuotaVersion: 2,
                CurrentPeriodNumber: 1,
                CurrentPeriodTotal: "275",
                CurrentPeriodConsumed: "300",
                CurrentPeriodReserved: "50",
                CurrentPeriodStatus: "current",
                CurrentPeriodVersion: 2,
                PeriodCount: 1,
                EventCount: 1,
                OutboxCount: 1,
                IdempotencyCount: 1),
            afterAdjust);
        await fixture.AssertCommittedMutationAsync(
            scenario,
            eventType: "total_adjusted",
            auditAction: "groupquota.quota.total_adjusted",
            requestId: adjustRequestId,
            idempotencyKey: adjustKey,
            reason: adjustReason,
            expectedVersion: 1,
            expectedPeriodId: scenario.InitialPeriodId,
            previousPeriodId: null,
            expectedDeltaTotal: "-725",
            expectedTotal: "275",
            expectedConsumed: "300",
            expectedReserved: "50",
            expectedETag: "\"v2\"",
            cancellationToken).ConfigureAwait(true);

        using HttpResponseMessage adjustedGet = await admin.GetAsync(
            $"/api/v1/admin/groups/{scenario.GroupId:D}/quota",
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, adjustedGet.StatusCode);
        Assert.Equal("\"v2\"", adjustedGet.Headers.ETag?.Tag);
        Assert.Equal(
            JsonDocument.Parse(frozenAdjustBody).RootElement.GetRawText(),
            JsonDocument.Parse(await adjustedGet.Content.ReadAsStringAsync(
                cancellationToken).ConfigureAwait(true)).RootElement.GetRawText());

        const string resetKey = "m3-e1-public-api-reset";
        const string resetReason = "Start the next manual quota period";
        using HttpRequestMessage reset = QuotaCommand(
            scenario.GroupId,
            "reset",
            totalTokens: 500,
            resetReason,
            resetKey,
            ifMatch: "\"v2\"");
        using HttpResponseMessage resetResponse = await admin.SendAsync(
            reset,
            cancellationToken).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);
        Assert.Equal("\"v3\"", resetResponse.Headers.ETag?.Tag);
        Guid resetRequestId = RequestId(resetResponse);
        string frozenResetBody = await resetResponse.Content.ReadAsStringAsync(
            cancellationToken).ConfigureAwait(true);
        Guid newPeriodId = AssertQuotaResponse(
            frozenResetBody,
            scenario.GroupId,
            expectedPeriodId: null,
            status: "active",
            total: "500",
            consumed: "0",
            reserved: "0",
            remaining: "500",
            overage: "0",
            version: 3);
        Assert.NotEqual(Guid.Empty, newPeriodId);
        Assert.NotEqual(scenario.InitialPeriodId, newPeriodId);

        QuotaInvariantSnapshot afterReset = await fixture.ReadInvariantSnapshotAsync(
            scenario.GroupId,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            new QuotaInvariantSnapshot(
                newPeriodId,
                QuotaVersion: 3,
                CurrentPeriodNumber: 2,
                CurrentPeriodTotal: "500",
                CurrentPeriodConsumed: "0",
                CurrentPeriodReserved: "0",
                CurrentPeriodStatus: "current",
                CurrentPeriodVersion: 1,
                PeriodCount: 2,
                EventCount: 2,
                OutboxCount: 2,
                IdempotencyCount: 2),
            afterReset);
        await fixture.AssertClosedPeriodAsync(
            scenario.InitialPeriodId,
            total: "275",
            consumed: "300",
            reserved: "50",
            resetReason,
            expectedVersion: 3,
            cancellationToken).ConfigureAwait(true);
        await fixture.AssertCommittedMutationAsync(
            scenario,
            eventType: "period_reset",
            auditAction: "groupquota.quota.period_reset",
            requestId: resetRequestId,
            idempotencyKey: resetKey,
            reason: resetReason,
            expectedVersion: 2,
            expectedPeriodId: newPeriodId,
            previousPeriodId: scenario.InitialPeriodId,
            expectedDeltaTotal: "225",
            expectedTotal: "500",
            expectedConsumed: "0",
            expectedReserved: "0",
            expectedETag: "\"v3\"",
            cancellationToken).ConfigureAwait(true);

        QuotaInvariantSnapshot beforeReplays = await fixture.ReadInvariantSnapshotAsync(
            scenario.GroupId,
            cancellationToken).ConfigureAwait(true);
        long auditsBeforeReplays = await fixture.CountAuditsAsync(
            scenario.GroupId,
            cancellationToken).ConfigureAwait(true);

        using HttpRequestMessage adjustReplay = QuotaCommand(
            scenario.GroupId,
            "adjust",
            totalTokens: 275,
            adjustReason,
            adjustKey,
            ifMatch: "\"v1\"");
        using HttpResponseMessage adjustedAgain = await admin.SendAsync(
            adjustReplay,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, adjustedAgain.StatusCode);
        Assert.Equal("\"v2\"", adjustedAgain.Headers.ETag?.Tag);
        Assert.Equal(
            frozenAdjustBody,
            await adjustedAgain.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(true));

        using HttpRequestMessage resetReplay = QuotaCommand(
            scenario.GroupId,
            "reset",
            totalTokens: 500,
            resetReason,
            resetKey,
            ifMatch: "\"v2\"");
        using HttpResponseMessage resetAgain = await admin.SendAsync(
            resetReplay,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, resetAgain.StatusCode);
        Assert.Equal("\"v3\"", resetAgain.Headers.ETag?.Tag);
        Assert.Equal(
            frozenResetBody,
            await resetAgain.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(true));

        Assert.Equal(
            beforeReplays,
            await fixture.ReadInvariantSnapshotAsync(
                scenario.GroupId,
                cancellationToken).ConfigureAwait(true));
        Assert.Equal(
            auditsBeforeReplays,
            await fixture.CountAuditsAsync(
                scenario.GroupId,
                cancellationToken).ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task AuditFailureRollsBackQuotaEventOutboxAndIdempotencyTogether()
    {
        // A test-only trigger fails the final production audit append. If quota,
        // event/outbox, audit and idempotency were not owned by the same outer
        // UoW, the earlier repository savepoint writes would leak through.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        M3E1QuotaScenario scenario = await fixture.SeedScenarioAsync(
            "rollback",
            totalTokens: 1_000,
            consumedTokens: 300,
            reservedTokens: 50,
            cancellationToken).ConfigureAwait(true);
        QuotaInvariantSnapshot before = await fixture.ReadInvariantSnapshotAsync(
            scenario.GroupId,
            cancellationToken).ConfigureAwait(true);
        long auditsBefore = await fixture.CountAuditsAsync(
            scenario.GroupId,
            cancellationToken).ConfigureAwait(true);
        await fixture.InstallAuditFailureInjectionAsync(cancellationToken)
            .ConfigureAwait(true);
        const string RollbackKey = "m3-e1-public-api-rollback";
        try
        {
            using HttpClient admin = fixture.AuthenticatedClient(
                "admin",
                scenario.Actor("admin"));
            using HttpRequestMessage request = QuotaCommand(
                scenario.GroupId,
                "adjust",
                totalTokens: 275,
                reason: "This command must roll back",
                idempotencyKey: RollbackKey,
                ifMatch: "\"v1\"");
            using HttpResponseMessage response = await admin.SendAsync(
                request,
                cancellationToken).ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            _ = await AssertProblemAsync(
                response,
                "internal_error",
                cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            await fixture.RemoveAuditFailureInjectionAsync(CancellationToken.None)
                .ConfigureAwait(true);
        }

        Assert.Equal(
            before,
            await fixture.ReadInvariantSnapshotAsync(
                scenario.GroupId,
                cancellationToken).ConfigureAwait(true));
        Assert.Equal(
            auditsBefore,
            await fixture.CountAuditsAsync(
                scenario.GroupId,
                cancellationToken).ConfigureAwait(true));
        Assert.Equal(
            0,
            await fixture.CountIdempotencyByKeyAsync(
                RollbackKey,
                cancellationToken).ConfigureAwait(true));
    }

    private static HttpRequestMessage QuotaCommand(
        Guid groupId,
        string operation,
        long totalTokens,
        string reason,
        string idempotencyKey,
        string ifMatch)
    {
        object body = operation switch
        {
            "adjust" => new
            {
                new_total_tokens = totalTokens,
                reason,
            },
            "reset" => new
            {
                total_tokens = totalTokens,
                reason,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        HttpRequestMessage request = new(
            HttpMethod.Post,
            $"/api/v1/admin/groups/{groupId:D}/quota/{operation}")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return request;
    }

    private static async ValueTask<Guid> AssertProblemAsync(
        HttpResponseMessage response,
        string expectedCode,
        CancellationToken cancellationToken,
        bool expectedRetryable = false)
    {
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        Guid requestId = RequestId(response);
        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(true));
        Assert.Equal(expectedCode, document.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            expectedRetryable,
            document.RootElement.GetProperty("retryable").GetBoolean());
        Assert.Equal(
            requestId,
            document.RootElement.GetProperty("request_id").GetGuid());
        return requestId;
    }

    private static Guid RequestId(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues(
            "X-Request-Id",
            out IEnumerable<string>? values));
        return Guid.Parse(Assert.Single(values), CultureInfo.InvariantCulture);
    }

    private static Guid AssertQuotaResponse(
        string json,
        Guid expectedGroupId,
        Guid? expectedPeriodId,
        string status,
        string total,
        string consumed,
        string reserved,
        string remaining,
        string overage,
        long version)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement quota = document.RootElement;
        Assert.Equal(
            QuotaResponseProperties,
            quota.EnumerateObject()
                .Select(static property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(expectedGroupId, quota.GetProperty("group_id").GetGuid());
        Guid periodId = quota.GetProperty("period_id").GetGuid();
        if (expectedPeriodId is not null)
        {
            Assert.Equal(expectedPeriodId.Value, periodId);
        }

        Assert.Equal(status, quota.GetProperty("status").GetString());
        Assert.Equal(total, quota.GetProperty("total_tokens").GetString());
        Assert.Equal(consumed, quota.GetProperty("consumed_tokens").GetString());
        Assert.Equal(reserved, quota.GetProperty("reserved_tokens").GetString());
        Assert.Equal(remaining, quota.GetProperty("remaining_tokens").GetString());
        Assert.Equal(overage, quota.GetProperty("overage_tokens").GetString());
        Assert.Equal(JsonValueKind.Null, quota.GetProperty("period_ended_at").ValueKind);
        Assert.Equal(version, quota.GetProperty("version").GetInt64());
        Assert.NotEqual(
            default,
            quota.GetProperty("period_started_at").GetDateTimeOffset());
        Assert.NotEqual(default, quota.GetProperty("updated_at").GetDateTimeOffset());
        return periodId;
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class M3E1QuotaPostgresApiTestGroup :
    ICollectionFixture<M3E1QuotaPostgresApiFixture>
{
    public const string Name = "M3-E1 PostgreSQL public API";
}

public sealed class M3E1QuotaPostgresApiFixture : IAsyncLifetime
{
    private const string AdminRoleId = "01900000-0000-7000-8000-000000000001";
    private const string OperatorRoleId = "01900000-0000-7000-8000-000000000002";
    private const string AuditorRoleId = "01900000-0000-7000-8000-000000000003";
    private const string UserRoleId = "01900000-0000-7000-8000-000000000004";
    private PostgreSqlContainer? container;
    private RealQuotaApiFactory? factory;
    private readonly MutableQuotaMutationDenialRateLimiter denialRateLimiter = new();

    internal NpgsqlDataSource AdministratorDataSource { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string administratorPassword = SecretHex();
        container = new PostgreSqlBuilder(ReadPostgresImage())
            .WithDatabase("poolai")
            .WithUsername("postgres")
            .WithPassword(administratorPassword)
            .Build();
        await container.StartAsync(cancellationToken).ConfigureAwait(true);

        RuntimeConnections connections = await ProvisionRuntimeRolesAsync(
            container.GetConnectionString(),
            cancellationToken).ConfigureAwait(true);
        MigrationCatalog catalog = await MigrationCatalog.LoadAsync(cancellationToken)
            .ConfigureAwait(true);
        await new PostgresMigrator(catalog).ApplyAsync(
            connections.Migrator,
            "PoolAI.EndToEndTests.m3-e1-quota-public-api",
            cancellationToken).ConfigureAwait(true);

        AdministratorDataSource = NpgsqlDataSource.Create(
            container.GetConnectionString());
        factory = new RealQuotaApiFactory(connections.Api, denialRateLimiter);
    }

    public async ValueTask DisposeAsync()
    {
        if (factory is not null)
        {
            await factory.DisposeAsync().ConfigureAwait(true);
        }

        if (AdministratorDataSource is not null)
        {
            await AdministratorDataSource.DisposeAsync().ConfigureAwait(true);
        }

        if (container is not null)
        {
            await container.DisposeAsync().ConfigureAwait(true);
        }
    }

    internal HttpClient AuthenticatedClient(string role, Guid actorId)
    {
        RealQuotaApiFactory apiFactory = factory
            ?? throw new InvalidOperationException("The API fixture is not initialized.");
        apiFactory.AccessSessionValidator.CanonicalRole = role switch
        {
            "admin" => SystemRole.Admin,
            "operator" => SystemRole.Operator,
            "auditor" => SystemRole.Auditor,
            "user" => SystemRole.User,
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
        HttpClient client = apiFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            IdentityAuthorizationTests.CreateJwt(
                apiFactory.JwtSigningKey,
                "PoolAI",
                "PoolAI.Web",
                role,
                tokenVersion: 2,
                TimeProvider.System.GetUtcNow().AddMinutes(5),
                actorId));
        return client;
    }

    internal void SetDenialRateLimitDecision(
        QuotaMutationDenialRateLimitDecision decision) =>
        denialRateLimiter.NextDecision = decision;

    internal async ValueTask<M3E1QuotaScenario> SeedScenarioAsync(
        string prefix,
        long totalTokens,
        long consumedTokens,
        long reservedTokens,
        CancellationToken cancellationToken)
    {
        string suffix = Guid.NewGuid().ToString("N")[..12];
        Guid groupId = Guid.CreateVersion7();
        Guid periodId = Guid.CreateVersion7();
        Guid channelId = Guid.CreateVersion7();
        Guid accountId = Guid.CreateVersion7();
        Dictionary<string, Guid> actors = new(StringComparer.Ordinal)
        {
            ["admin"] = Guid.CreateVersion7(),
            ["operator"] = Guid.CreateVersion7(),
            ["auditor"] = Guid.CreateVersion7(),
            ["user"] = Guid.CreateVersion7(),
        };

        NpgsqlConnection connection = await AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(true);
        await using ConfiguredAsyncDisposable connectionLease =
            connection.ConfigureAwait(false);
        NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(true);
        await using ConfiguredAsyncDisposable transactionLease =
            transaction.ConfigureAwait(false);

        foreach ((string role, Guid actorId) in actors)
        {
            using NpgsqlCommand user = new("""
                INSERT INTO public.users (
                    id, email, normalized_email, display_name, password_hash,
                    security_stamp
                ) VALUES ($1, $2, $2, $3, 'poolai-password-v1:test', $4);
                """, connection, transaction);
            string email = $"{prefix}-{role}-{suffix}@example.test";
            user.Parameters.AddWithValue(actorId);
            user.Parameters.AddWithValue(email);
            user.Parameters.AddWithValue($"M3-E1 {role} actor");
            user.Parameters.AddWithValue(Guid.CreateVersion7());
            Assert.Equal(
                1,
                await user.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(true));

            using NpgsqlCommand roleAssignment = new("""
                INSERT INTO public.user_roles (user_id, role_id, assigned_by)
                VALUES ($1, $2, $1);
                """, connection, transaction);
            roleAssignment.Parameters.AddWithValue(actorId);
            roleAssignment.Parameters.AddWithValue(RoleId(role));
            Assert.Equal(
                1,
                await roleAssignment.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(true));
        }

        using (NpgsqlCommand group = new("""
                   INSERT INTO public.groups (
                       id, name, description, status, version
                   ) VALUES (
                       $1, $2, 'M3-E1 PostgreSQL public API acceptance',
                       'disabled', 1
                   );
                   """, connection, transaction))
        {
            group.Parameters.AddWithValue(groupId);
            group.Parameters.AddWithValue($"M3-E1 {prefix} {suffix}");
            Assert.Equal(
                1,
                await group.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(true));
        }

        using (NpgsqlCommand quota = new("""
                   INSERT INTO public.group_token_quotas (
                       group_id, current_period_id, enabled, version
                   ) VALUES ($1, $2, true, 1);
                   """, connection, transaction))
        {
            quota.Parameters.AddWithValue(groupId);
            quota.Parameters.AddWithValue(periodId);
            Assert.Equal(
                1,
                await quota.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(true));
        }

        using (NpgsqlCommand period = new("""
                   INSERT INTO public.group_quota_periods (
                       id, group_id, period_number, total_tokens,
                       consumed_tokens, reserved_tokens, status, opened_at, version
                   ) VALUES (
                       $1, $2, 1, $3, $4, $5, 'current',
                       clock_timestamp() - interval '1 hour', 1
                   );
                   """, connection, transaction))
        {
            period.Parameters.AddWithValue(periodId);
            period.Parameters.AddWithValue(groupId);
            AddNumeric(period.Parameters, totalTokens);
            AddNumeric(period.Parameters, consumedTokens);
            AddNumeric(period.Parameters, reservedTokens);
            Assert.Equal(
                1,
                await period.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(true));
        }

        using (NpgsqlCommand channel = new("""
                   INSERT INTO public.channels (
                       id, provider, name, model_rules, capabilities, status
                   ) VALUES (
                       $1, 'openai', $2, '{"gpt-test":"gpt-test"}'::jsonb,
                       '{"responses":true,"chat_completions":true,"function_tools":true,"streaming":true}'::jsonb,
                       'active'
                   );
                   """, connection, transaction))
        {
            channel.Parameters.AddWithValue(channelId);
            channel.Parameters.AddWithValue($"M3-E1 channel {suffix}");
            Assert.Equal(
                1,
                await channel.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(true));
        }

        using (NpgsqlCommand account = new("""
                   INSERT INTO public.accounts (
                       id, provider, name, auth_type, upstream_base_url,
                       credential_envelope, credential_prefix,
                       status, last_health_at, last_health_status
                   ) VALUES (
                       $1, 'openai', $2, 'api_key', 'https://example.test/v1',
                       '{}'::jsonb, 'sk-acceptance',
                       'active', clock_timestamp(), 'healthy'
                   );
                   """, connection, transaction))
        {
            account.Parameters.AddWithValue(accountId);
            account.Parameters.AddWithValue($"M3-E1 account {suffix}");
            Assert.Equal(
                1,
                await account.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(true));
        }

        using (NpgsqlCommand configuration = new("""
                   INSERT INTO public.group_supply_configurations (
                       group_id, channel_id
                   ) VALUES ($1, $2);
                   """, connection, transaction))
        {
            configuration.Parameters.AddWithValue(groupId);
            configuration.Parameters.AddWithValue(channelId);
            Assert.Equal(
                1,
                await configuration.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(true));
        }

        using (NpgsqlCommand binding = new("""
                   INSERT INTO public.group_accounts (
                       group_id, account_id, is_enabled
                   ) VALUES ($1, $2, true);
                   """, connection, transaction))
        {
            binding.Parameters.AddWithValue(groupId);
            binding.Parameters.AddWithValue(accountId);
            Assert.Equal(
                1,
                await binding.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(true));
        }

        using (NpgsqlCommand activate = new("""
                   UPDATE public.groups
                   SET status = 'active',
                       activation_supply_readiness_token = 'acceptance.ready',
                       activation_supply_observed_at = clock_timestamp(),
                       version = version + 1,
                       updated_at = clock_timestamp()
                   WHERE id = $1;
                   """, connection, transaction))
        {
            activate.Parameters.AddWithValue(groupId);
            Assert.Equal(
                1,
                await activate.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(true));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(true);
        return new M3E1QuotaScenario(groupId, periodId, actors);
    }

    internal async ValueTask<QuotaInvariantSnapshot> ReadInvariantSnapshotAsync(
        Guid groupId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = AdministratorDataSource.CreateCommand("""
            SELECT
                quota.current_period_id,
                quota.version,
                period.period_number,
                period.total_tokens::text,
                period.consumed_tokens::text,
                period.reserved_tokens::text,
                period.status,
                period.version,
                (SELECT count(*) FROM public.group_quota_periods WHERE group_id = $1),
                (SELECT count(*) FROM public.group_quota_events WHERE group_id = $1),
                (SELECT count(*) FROM public.outbox_messages
                    WHERE topic = 'poolai.quota.v1' AND aggregate_id = $1),
                (SELECT count(*) FROM public.idempotency_records
                    WHERE scope LIKE
                        'groupquota:%:post:/api/v1/admin/groups/'
                        || $1::text
                        || '/quota/%')
            FROM public.group_token_quotas AS quota
            JOIN public.group_quota_periods AS period
              ON period.id = quota.current_period_id
             AND period.group_id = quota.group_id
            WHERE quota.group_id = $1;
            """);
        command.Parameters.AddWithValue(groupId);
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(true);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        QuotaInvariantSnapshot snapshot = new(
            reader.GetGuid(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.GetInt64(11));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        return snapshot;
    }

    internal async ValueTask<long> CountIdempotencyByKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = AdministratorDataSource.CreateCommand("""
            SELECT count(*)
            FROM public.idempotency_records
            WHERE idempotency_key = $1;
            """);
        command.Parameters.AddWithValue(idempotencyKey);
        return Assert.IsType<long>(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(true));
    }

    internal async ValueTask<long> CountAuditsAsync(
        Guid groupId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = AdministratorDataSource.CreateCommand("""
            SELECT count(*)
            FROM public.audit_logs
            WHERE target_type = 'group_quota' AND target_id = $1;
            """);
        command.Parameters.AddWithValue(groupId);
        return Assert.IsType<long>(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(true));
    }

    internal async ValueTask AssertSingleDenialAuditAsync(
        Guid requestId,
        Guid groupId,
        Guid actorId,
        string role,
        string operation,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = AdministratorDataSource.CreateCommand("""
            SELECT
                actor_type,
                actor_user_id,
                action,
                target_type,
                target_id,
                reason,
                before_state,
                after_state,
                metadata ->> 'operation',
                metadata ->> 'denial_code',
                metadata ->> 'idempotency_key_status',
                metadata ->> 'idempotency_key_hash',
                metadata::text
            FROM public.audit_logs
            WHERE request_id = $1;
            """);
        command.Parameters.AddWithValue(requestId);
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(true);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        Assert.Equal(role, reader.GetString(0));
        Assert.Equal(actorId, reader.GetGuid(1));
        Assert.Equal(
            string.Equals(operation, "adjust", StringComparison.Ordinal)
                ? "groupquota.quota.total_adjust_denied"
                : "groupquota.quota.period_reset_denied",
            reader.GetString(2));
        Assert.Equal("group_quota", reader.GetString(3));
        Assert.Equal(groupId, reader.GetGuid(4));
        Assert.True(reader.IsDBNull(5));
        Assert.True(reader.IsDBNull(6));
        Assert.True(reader.IsDBNull(7));
        Assert.Equal(
            string.Equals(operation, "adjust", StringComparison.Ordinal)
                ? "adjust_total"
                : "reset_period",
            reader.GetString(8));
        Assert.Equal("role_required", reader.GetString(9));
        Assert.Equal("valid", reader.GetString(10));
        Assert.Matches("^[0-9a-f]{64}$", reader.GetString(11));
        Assert.DoesNotContain(
            idempotencyKey,
            reader.GetString(12),
            StringComparison.Ordinal);
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
    }

    internal async ValueTask AssertClosedPeriodAsync(
        Guid periodId,
        string total,
        string consumed,
        string reserved,
        string resetReason,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = AdministratorDataSource.CreateCommand("""
            SELECT
                total_tokens::text,
                consumed_tokens::text,
                reserved_tokens::text,
                status,
                reset_reason,
                closed_at IS NOT NULL,
                version
            FROM public.group_quota_periods
            WHERE id = $1;
            """);
        command.Parameters.AddWithValue(periodId);
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(true);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        Assert.Equal(total, reader.GetString(0));
        Assert.Equal(consumed, reader.GetString(1));
        Assert.Equal(reserved, reader.GetString(2));
        Assert.Equal("closed", reader.GetString(3));
        Assert.Equal(resetReason, reader.GetString(4));
        Assert.True(reader.GetBoolean(5));
        Assert.Equal(expectedVersion, reader.GetInt64(6));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
    }

    internal async ValueTask AssertCommittedMutationAsync(
        M3E1QuotaScenario scenario,
        string eventType,
        string auditAction,
        Guid requestId,
        string idempotencyKey,
        string reason,
        long expectedVersion,
        Guid expectedPeriodId,
        Guid? previousPeriodId,
        string expectedDeltaTotal,
        string expectedTotal,
        string expectedConsumed,
        string expectedReserved,
        string expectedETag,
        CancellationToken cancellationToken)
    {
        using (NpgsqlCommand facts = AdministratorDataSource.CreateCommand("""
                   SELECT
                       quota_event.id,
                       quota_event.event_sequence,
                       quota_event.period_id,
                       quota_event.delta_total_tokens::text,
                       quota_event.total_tokens_after::text,
                       quota_event.consumed_tokens_after::text,
                       quota_event.reserved_tokens_after::text,
                       quota_event.actor_type,
                       quota_event.actor_user_id,
                       quota_event.reason,
                       quota_event.metadata ->> 'expected_quota_version',
                       outbox.topic,
                       outbox.aggregate_type,
                       outbox.aggregate_id,
                       outbox.event_type,
                       outbox.source_event_sequence,
                       outbox.payload ->> 'event_id',
                       outbox.payload ->> 'period_id',
                       outbox.payload ->> 'total_tokens',
                       audit.actor_type,
                       audit.actor_user_id,
                       audit.action,
                       audit.target_type,
                       audit.target_id,
                       audit.reason,
                       audit.before_state ->> 'version',
                       audit.after_state ->> 'version',
                       idempotency.status,
                       idempotency.response_status,
                       idempotency.resource_type,
                       idempotency.resource_id,
                       idempotency.response_headers ->> 'ETag'
                   FROM public.group_quota_events AS quota_event
                   JOIN public.outbox_messages AS outbox
                     ON outbox.topic = 'poolai.quota.v1'
                    AND outbox.source_event_sequence = quota_event.event_sequence
                   CROSS JOIN public.audit_logs AS audit
                   CROSS JOIN public.idempotency_records AS idempotency
                   WHERE quota_event.group_id = $1
                     AND quota_event.event_type = $2
                     AND audit.request_id = $3
                     AND idempotency.idempotency_key = $4
                     AND idempotency.resource_type = 'group_quota'
                     AND idempotency.resource_id = $1;
                   """))
        {
            facts.Parameters.AddWithValue(scenario.GroupId);
            facts.Parameters.AddWithValue(eventType);
            facts.Parameters.AddWithValue(requestId);
            facts.Parameters.AddWithValue(idempotencyKey);
            using NpgsqlDataReader reader = await facts
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(true);
            Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
            Guid eventId = reader.GetGuid(0);
            long eventSequence = reader.GetInt64(1);
            Assert.Equal(expectedPeriodId, reader.GetGuid(2));
            Assert.Equal(expectedDeltaTotal, reader.GetString(3));
            Assert.Equal(expectedTotal, reader.GetString(4));
            Assert.Equal(expectedConsumed, reader.GetString(5));
            Assert.Equal(expectedReserved, reader.GetString(6));
            Assert.Equal("admin", reader.GetString(7));
            Assert.Equal(scenario.Actor("admin"), reader.GetGuid(8));
            Assert.Equal(reason, reader.GetString(9));
            Assert.Equal(
                expectedVersion.ToString(CultureInfo.InvariantCulture),
                reader.GetString(10));
            Assert.Equal("poolai.quota.v1", reader.GetString(11));
            Assert.Equal("group", reader.GetString(12));
            Assert.Equal(scenario.GroupId, reader.GetGuid(13));
            Assert.Equal(eventType, reader.GetString(14));
            Assert.Equal(eventSequence, reader.GetInt64(15));
            Assert.Equal(eventId.ToString("D", CultureInfo.InvariantCulture), reader.GetString(16));
            Assert.Equal(expectedPeriodId.ToString("D", CultureInfo.InvariantCulture), reader.GetString(17));
            Assert.Equal(expectedTotal, reader.GetString(18));
            Assert.Equal("admin", reader.GetString(19));
            Assert.Equal(scenario.Actor("admin"), reader.GetGuid(20));
            Assert.Equal(auditAction, reader.GetString(21));
            Assert.Equal("group_quota", reader.GetString(22));
            Assert.Equal(scenario.GroupId, reader.GetGuid(23));
            Assert.Equal(reason, reader.GetString(24));
            Assert.Equal(
                expectedVersion.ToString(CultureInfo.InvariantCulture),
                reader.GetString(25));
            Assert.Equal(
                (expectedVersion + 1).ToString(CultureInfo.InvariantCulture),
                reader.GetString(26));
            Assert.Equal("completed", reader.GetString(27));
            Assert.Equal(200, reader.GetInt32(28));
            Assert.Equal("group_quota", reader.GetString(29));
            Assert.Equal(scenario.GroupId, reader.GetGuid(30));
            Assert.Equal(expectedETag, reader.GetString(31));
            Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        }

        List<string> transactionIds = [];
        using (NpgsqlCommand transaction = AdministratorDataSource.CreateCommand("""
                   SELECT quota.xmin::text
                   FROM public.group_token_quotas AS quota
                   WHERE quota.group_id = $1
                   UNION ALL
                   SELECT period.xmin::text
                   FROM public.group_quota_periods AS period
                   WHERE period.id = $2
                   UNION ALL
                   SELECT quota_event.xmin::text
                   FROM public.group_quota_events AS quota_event
                   WHERE quota_event.group_id = $1 AND quota_event.event_type = $3
                   UNION ALL
                   SELECT outbox.xmin::text
                   FROM public.outbox_messages AS outbox
                   JOIN public.group_quota_events AS quota_event
                     ON quota_event.event_sequence = outbox.source_event_sequence
                   WHERE quota_event.group_id = $1 AND quota_event.event_type = $3
                   UNION ALL
                   SELECT audit.xmin::text
                   FROM public.audit_logs AS audit
                   WHERE audit.request_id = $4
                   UNION ALL
                   SELECT idempotency.xmin::text
                   FROM public.idempotency_records AS idempotency
                   WHERE idempotency.idempotency_key = $5
                     AND idempotency.resource_type = 'group_quota'
                     AND idempotency.resource_id = $1;
                   """))
        {
            transaction.Parameters.AddWithValue(scenario.GroupId);
            transaction.Parameters.AddWithValue(expectedPeriodId);
            transaction.Parameters.AddWithValue(eventType);
            transaction.Parameters.AddWithValue(requestId);
            transaction.Parameters.AddWithValue(idempotencyKey);
            using NpgsqlDataReader reader = await transaction
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(true);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(true))
            {
                transactionIds.Add(reader.GetString(0));
            }
        }

        if (previousPeriodId is not null)
        {
            using NpgsqlCommand previous = AdministratorDataSource.CreateCommand("""
                SELECT xmin::text
                FROM public.group_quota_periods
                WHERE id = $1;
                """);
            previous.Parameters.AddWithValue(previousPeriodId.Value);
            transactionIds.Add(Assert.IsType<string>(
                await previous.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(true)));
        }

        Assert.Equal(previousPeriodId is null ? 6 : 7, transactionIds.Count);
        // The repository deliberately brackets the SQL function call with a
        // savepoint. PostgreSQL therefore records quota/period/event/outbox
        // under one subtransaction XID and audit/idempotency under the outer
        // XID. Both groups are committed by the one production UoW; the
        // separate failure-injection test proves the outer rollback boundary.
        Assert.Single(transactionIds.Take(4).Distinct(StringComparer.Ordinal));
        Assert.Single(transactionIds.Skip(4).Take(2).Distinct(StringComparer.Ordinal));
        if (previousPeriodId is not null)
        {
            Assert.Equal(transactionIds[0], transactionIds[6]);
        }

        Assert.InRange(
            transactionIds.Distinct(StringComparer.Ordinal).Count(),
            1,
            2);
    }

    internal async ValueTask InstallAuditFailureInjectionAsync(
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = AdministratorDataSource.CreateCommand("""
            CREATE FUNCTION public.poolai_test_fail_m3_e1_quota_audit()
            RETURNS trigger
            LANGUAGE plpgsql
            SET search_path = pg_catalog, public, pg_temp
            AS $function$
            BEGIN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'm3_e1_test_audit_failure';
            END;
            $function$;

            CREATE TRIGGER tr_test_fail_m3_e1_quota_audit
            BEFORE INSERT ON public.audit_logs
            FOR EACH ROW
            WHEN (NEW.action = 'groupquota.quota.total_adjusted')
            EXECUTE FUNCTION public.poolai_test_fail_m3_e1_quota_audit();
            """);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true);
    }

    internal async ValueTask RemoveAuditFailureInjectionAsync(
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = AdministratorDataSource.CreateCommand("""
            DROP TRIGGER IF EXISTS tr_test_fail_m3_e1_quota_audit
                ON public.audit_logs;
            DROP FUNCTION IF EXISTS public.poolai_test_fail_m3_e1_quota_audit();
            """);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true);
    }

    private static Guid RoleId(string role) => Guid.Parse(role switch
    {
        "admin" => AdminRoleId,
        "operator" => OperatorRoleId,
        "auditor" => AuditorRoleId,
        "user" => UserRoleId,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    });

    private static void AddNumeric(NpgsqlParameterCollection parameters, long value) =>
        parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Numeric,
            Value = value,
        });

    private static async ValueTask<RuntimeConnections> ProvisionRuntimeRolesAsync(
        string administratorConnectionString,
        CancellationToken cancellationToken)
    {
        RuntimePasswords passwords = new(SecretHex(), SecretHex(), SecretHex());
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(
            administratorConnectionString);
        NpgsqlConnection connection = await dataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(true);
        await using ConfiguredAsyncDisposable connectionLease =
            connection.ConfigureAwait(false);
        await SetPasswordSettingsAsync(connection, passwords, cancellationToken)
            .ConfigureAwait(true);
        try
        {
            using NpgsqlCommand command = new("""
                CREATE ROLE poolai_runtime_owner NOLOGIN
                    NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                DO $roles$
                BEGIN
                    EXECUTE pg_catalog.format(
                        'CREATE ROLE poolai_migrator LOGIN PASSWORD %L '
                        'NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS',
                        pg_catalog.current_setting('poolai.test_migrator_password'));
                    EXECUTE pg_catalog.format(
                        'CREATE ROLE poolai_api LOGIN PASSWORD %L '
                        'NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS',
                        pg_catalog.current_setting('poolai.test_api_password'));
                    EXECUTE pg_catalog.format(
                        'CREATE ROLE poolai_worker LOGIN PASSWORD %L '
                        'NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS',
                        pg_catalog.current_setting('poolai.test_worker_password'));
                END;
                $roles$;
                GRANT poolai_runtime_owner TO poolai_migrator WITH INHERIT FALSE, SET TRUE;
                ALTER DATABASE poolai OWNER TO poolai_migrator;
                REVOKE CREATE, TEMPORARY ON DATABASE poolai FROM PUBLIC;
                GRANT CONNECT ON DATABASE poolai
                    TO poolai_migrator, poolai_api, poolai_worker;
                """, connection);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            await SetPasswordSettingsAsync(
                connection,
                new RuntimePasswords(string.Empty, string.Empty, string.Empty),
                CancellationToken.None).ConfigureAwait(true);
        }

        return new RuntimeConnections(
            WithRole(administratorConnectionString, "poolai_migrator", passwords.Migrator),
            WithRole(administratorConnectionString, "poolai_api", passwords.Api),
            WithRole(administratorConnectionString, "poolai_worker", passwords.Worker));
    }

    private static async ValueTask SetPasswordSettingsAsync(
        NpgsqlConnection connection,
        RuntimePasswords passwords,
        CancellationToken cancellationToken)
    {
        foreach ((string setting, string value) in new[]
                 {
                     ("poolai.test_migrator_password", passwords.Migrator),
                     ("poolai.test_api_password", passwords.Api),
                     ("poolai.test_worker_password", passwords.Worker),
                 })
        {
            using NpgsqlCommand command = new(
                "SELECT pg_catalog.set_config($1, $2, false);",
                connection);
            command.Parameters.AddWithValue(setting);
            command.Parameters.AddWithValue(value);
            _ = await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(true);
        }
    }

    private static string WithRole(
        string administratorConnectionString,
        string role,
        string password) => new NpgsqlConnectionStringBuilder(
        administratorConnectionString)
    {
        Username = role,
        Password = password,
        ApplicationName = $"PoolAI.EndToEndTests.M3E1.{role}",
    }.ConnectionString;

    private static string ReadPostgresImage()
    {
        using JsonDocument versions = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "eng", "versions.json")));
        JsonElement root = versions.RootElement;
        string image = root.GetProperty("containers").GetProperty("postgresql").GetString()
            ?? throw new InvalidOperationException(
                "The PostgreSQL image lock is missing.");
        string digest = root.GetProperty("containerDigests")
            .GetProperty("postgresql").GetString()
            ?? throw new InvalidOperationException(
                "The PostgreSQL digest lock is missing.");
        return $"{image}@{digest}";
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "eng", "versions.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The PoolAI repository root was not found.");
    }

    private static string SecretHex() => Convert.ToHexString(
        RandomNumberGenerator.GetBytes(24));

    private sealed class RealQuotaApiFactory(
        string postgresConnectionString,
        IQuotaMutationDenialRateLimiter denialRateLimiter) :
        PoolAiApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            Dictionary<string, string?> overrides = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Data:Postgres:ConnectionString"] = postgresConnectionString,
            };
            foreach ((string key, string? value) in overrides)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(overrides));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IQuotaMutationDenialRateLimiter>();
                services.AddSingleton(denialRateLimiter);
            });
        }
    }

    private sealed class MutableQuotaMutationDenialRateLimiter :
        IQuotaMutationDenialRateLimiter
    {
        internal QuotaMutationDenialRateLimitDecision NextDecision { get; set; } =
            QuotaMutationDenialRateLimitDecision.Allowed;

        public ValueTask<QuotaMutationDenialRateLimitDecision> AcquireAsync(
            EntityId actorUserId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(NextDecision);
        }
    }

    private sealed record RuntimePasswords(
        string Migrator,
        string Api,
        string Worker);

    private sealed record RuntimeConnections(
        string Migrator,
        string Api,
        string Worker);
}

public sealed record M3E1QuotaScenario(
    Guid GroupId,
    Guid InitialPeriodId,
    IReadOnlyDictionary<string, Guid> Actors)
{
    public Guid Actor(string role) => Actors[role];
}

public sealed record QuotaInvariantSnapshot(
    Guid CurrentPeriodId,
    long QuotaVersion,
    long CurrentPeriodNumber,
    string CurrentPeriodTotal,
    string CurrentPeriodConsumed,
    string CurrentPeriodReserved,
    string CurrentPeriodStatus,
    long CurrentPeriodVersion,
    long PeriodCount,
    long EventCount,
    long OutboxCount,
    long IdempotencyCount);

#pragma warning restore MA0051
#pragma warning restore MA0048
