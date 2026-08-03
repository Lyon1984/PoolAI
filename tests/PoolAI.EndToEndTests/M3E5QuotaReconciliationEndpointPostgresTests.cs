using System.Globalization;
using System.Net;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace PoolAI.EndToEndTests;

[Collection(M3E1QuotaPostgresApiTestGroup.Name)]
public sealed class M3E5QuotaReconciliationEndpointPostgresTests(
    M3E1QuotaPostgresApiFixture fixture)
{
    [Theory]
    [InlineData("admin")]
    [InlineData("operator")]
    [InlineData("auditor")]
    [Trait("Category", "PostgreSQL")]
    public async Task AuthorizedReadRolesReceiveTheCurrentCheckpointAlignedSnapshot(
        string role)
    {
        // Governing contracts: ADR 0013 and adminGetGroupQuotaReconciliation.
        // Omitting period_id selects the current period, while Usage is compared
        // only at its accepted logical source checkpoint.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        M3E1QuotaScenario scenario = await fixture.SeedScenarioAsync(
            $"m3-e5-{role}",
            totalTokens: 1_100,
            consumedTokens: 0,
            reservedTokens: 0,
            cancellationToken).ConfigureAwait(true);
        CurrentLaggingSeed seed = await SeedCurrentLaggingAsync(
            scenario,
            cancellationToken).ConfigureAwait(true);
        using HttpClient client = fixture.AuthenticatedClient(
            role,
            scenario.Actor(role));

        using HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/admin/groups/{scenario.GroupId:D}/quota/reconciliation",
            cancellationToken).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        _ = AssertRequestId(response);
        using JsonDocument body = await ReadJsonAsync(response, cancellationToken)
            .ConfigureAwait(true);
        AssertSuccessfulClosedShape(body.RootElement);
        AssertAuthoritativeSnapshot(
            body.RootElement,
            scenario.GroupId,
            scenario.InitialPeriodId,
            totalTokens: "1100");

        JsonElement projection = body.RootElement.GetProperty("usage_projection");
        Assert.Equal("lagging", projection.GetProperty("status").GetString());
        Assert.Equal("0", projection.GetProperty("expected_consumed_tokens").GetString());
        Assert.Equal("0", projection.GetProperty("projected_consumed_tokens").GetString());
        Assert.Equal("0", projection.GetProperty("consumed_variance").GetString());
        Assert.Equal(
            seed.CheckpointSequence.ToString(CultureInfo.InvariantCulture),
            projection.GetProperty("checkpoint_source_event_sequence").GetString());
        Assert.Equal(
            seed.LatestSequence.ToString(CultureInfo.InvariantCulture),
            projection.GetProperty("latest_source_event_sequence").GetString());
        Assert.Equal(
            seed.DataThrough,
            projection.GetProperty("data_through").GetDateTimeOffset());
        Assert.Equal(
            seed.LatestOccurredAt,
            body.RootElement.GetProperty("data_watermark").GetDateTimeOffset());
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task UserIsForbiddenAndAnonymousIsUnauthorized()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid groupId = Guid.CreateVersion7();
        Guid userId = Guid.CreateVersion7();
        Guid adminId = Guid.CreateVersion7();
        string path =
            $"/api/v1/admin/groups/{groupId:D}/quota/reconciliation";

        using HttpClient user = fixture.AuthenticatedClient(
            "user",
            userId);
        using HttpResponseMessage forbidden = await user.GetAsync(
            path,
            cancellationToken).ConfigureAwait(true);

        await AssertProblemAsync(
            forbidden,
            HttpStatusCode.Forbidden,
            "role_required",
            path,
            cancellationToken).ConfigureAwait(true);

        using HttpClient anonymous = fixture.AuthenticatedClient(
            "admin",
            adminId);
        anonymous.DefaultRequestHeaders.Authorization = null;
        using HttpResponseMessage unauthorized = await anonymous.GetAsync(
            path,
            cancellationToken).ConfigureAwait(true);

        Assert.Contains(
            unauthorized.Headers.WwwAuthenticate,
            static header => string.Equals(
                header.Scheme,
                "Bearer",
                StringComparison.Ordinal));
        await AssertProblemAsync(
            unauthorized,
            HttpStatusCode.Unauthorized,
            "authentication_required",
            path,
            cancellationToken).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task MalformedPeriodReturnsTheCanonicalClosedInvalidRequestProblem()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid groupId = Guid.CreateVersion7();
        using HttpClient admin = fixture.AuthenticatedClient(
            "admin",
            Guid.CreateVersion7());
        string path =
            $"/api/v1/admin/groups/{groupId:D}/quota/reconciliation";

        using HttpResponseMessage response = await admin.GetAsync(
            $"{path}?period_id=not-a-uuid",
            cancellationToken).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        Guid requestId = AssertRequestId(response);
        using JsonDocument actual = await ReadJsonAsync(response, cancellationToken)
            .ConfigureAwait(true);
        using JsonDocument canonical = LoadFixture(
            "control-plane-group-quota-reconciliation-invalid-period.json");
        AssertClosedObject(canonical.RootElement, actual.RootElement);
        foreach (JsonProperty property in canonical.RootElement.EnumerateObject())
        {
            if (property.Name is "request_id" or "instance")
            {
                continue;
            }

            Assert.True(
                JsonElement.DeepEquals(
                    property.Value,
                    actual.RootElement.GetProperty(property.Name)),
                $"Property '{property.Name}' did not match the canonical fixture.");
        }

        Assert.Equal(requestId, actual.RootElement.GetProperty("request_id").GetGuid());
        Assert.Equal(path, actual.RootElement.GetProperty("instance").GetString());
        Assert.False(actual.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task MissingAndCrossGroupPeriodsReturnTheSameNonDisclosingNotFound()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        M3E1QuotaScenario selectedGroup = await fixture.SeedScenarioAsync(
            "m3-e5-not-found-selected",
            totalTokens: 1_000,
            consumedTokens: 0,
            reservedTokens: 0,
            cancellationToken).ConfigureAwait(true);
        M3E1QuotaScenario otherGroup = await fixture.SeedScenarioAsync(
            "m3-e5-not-found-other",
            totalTokens: 1_000,
            consumedTokens: 0,
            reservedTokens: 0,
            cancellationToken).ConfigureAwait(true);
        using HttpClient admin = fixture.AuthenticatedClient(
            "admin",
            selectedGroup.Actor("admin"));
        string path =
            $"/api/v1/admin/groups/{selectedGroup.GroupId:D}/quota/reconciliation";

        using HttpResponseMessage missingResponse = await admin.GetAsync(
            $"{path}?period_id={Guid.CreateVersion7():D}",
            cancellationToken).ConfigureAwait(true);
        using HttpResponseMessage crossGroupResponse = await admin.GetAsync(
            $"{path}?period_id={otherGroup.InitialPeriodId:D}",
            cancellationToken).ConfigureAwait(true);

        ProblemSnapshot missing = await ReadProblemAsync(
            missingResponse,
            HttpStatusCode.NotFound,
            "resource_not_found",
            path,
            cancellationToken).ConfigureAwait(true);
        ProblemSnapshot crossGroup = await ReadProblemAsync(
            crossGroupResponse,
            HttpStatusCode.NotFound,
            "resource_not_found",
            path,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(missing with { RequestId = Guid.Empty },
            crossGroup with { RequestId = Guid.Empty });
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ExplicitClosedPeriodClampsAnAdvancedGroupCheckpointToItsLastEvent()
    {
        // ADR 0013 requires a normal historical query to remain reconciled when
        // the Group watermark has already advanced into the current period.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        M3E1QuotaScenario scenario = await fixture.SeedScenarioAsync(
            "m3-e5-closed-period",
            totalTokens: 900,
            consumedTokens: 0,
            reservedTokens: 0,
            cancellationToken).ConfigureAwait(true);
        ClosedPeriodSeed seed = await SeedClosedPeriodAsync(
            scenario,
            cancellationToken).ConfigureAwait(true);
        using HttpClient auditor = fixture.AuthenticatedClient(
            "auditor",
            scenario.Actor("auditor"));

        using HttpResponseMessage response = await auditor.GetAsync(
            $"/api/v1/admin/groups/{scenario.GroupId:D}/quota/reconciliation"
                + $"?period_id={seed.ClosedPeriodId:D}",
            cancellationToken).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _ = AssertRequestId(response);
        using JsonDocument body = await ReadJsonAsync(response, cancellationToken)
            .ConfigureAwait(true);
        AssertSuccessfulClosedShape(body.RootElement);
        AssertAuthoritativeSnapshot(
            body.RootElement,
            scenario.GroupId,
            seed.ClosedPeriodId,
            totalTokens: "500");

        JsonElement projection = body.RootElement.GetProperty("usage_projection");
        Assert.Equal("reconciled", projection.GetProperty("status").GetString());
        Assert.Equal(
            seed.CurrentEventSequence.ToString(CultureInfo.InvariantCulture),
            projection.GetProperty("checkpoint_source_event_sequence").GetString());
        Assert.Equal(
            seed.ClosedEventSequence.ToString(CultureInfo.InvariantCulture),
            projection.GetProperty("latest_source_event_sequence").GetString());
        Assert.Equal("0", projection.GetProperty("expected_consumed_tokens").GetString());
        Assert.Equal("0", projection.GetProperty("projected_consumed_tokens").GetString());
        Assert.Equal("0", projection.GetProperty("consumed_variance").GetString());
        Assert.Equal(
            seed.DataThrough,
            projection.GetProperty("data_through").GetDateTimeOffset());
        Assert.Equal(
            seed.ClosedEventOccurredAt,
            body.RootElement.GetProperty("data_watermark").GetDateTimeOffset());
    }

    private async ValueTask<CurrentLaggingSeed> SeedCurrentLaggingAsync(
        M3E1QuotaScenario scenario,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = await fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(true);
        await using ConfiguredAsyncDisposable connectionLease =
            connection.ConfigureAwait(false);
        NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(true);
        await using ConfiguredAsyncDisposable transactionLease =
            transaction.ConfigureAwait(false);
        DateTimeOffset databaseNow = await ReadDatabaseClockAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(true);
        SeededEvent checkpoint = await InsertEventAsync(
            connection,
            transaction,
            scenario.GroupId,
            scenario.InitialPeriodId,
            "initialized",
            deltaTotal: 1_000,
            totalAfter: 1_000,
            databaseNow.AddMinutes(-2),
            cancellationToken).ConfigureAwait(true);
        SeededEvent latest = await InsertEventAsync(
            connection,
            transaction,
            scenario.GroupId,
            scenario.InitialPeriodId,
            "total_adjusted",
            deltaTotal: 100,
            totalAfter: 1_100,
            databaseNow.AddMinutes(-1),
            cancellationToken).ConfigureAwait(true);
        await InsertWatermarkAsync(
            connection,
            transaction,
            scenario.GroupId,
            checkpoint.Sequence,
            checkpoint.OccurredAt,
            cancellationToken).ConfigureAwait(true);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(true);
        return new CurrentLaggingSeed(
            checkpoint.Sequence,
            latest.Sequence,
            checkpoint.OccurredAt,
            latest.OccurredAt);
    }

    private async ValueTask<ClosedPeriodSeed> SeedClosedPeriodAsync(
        M3E1QuotaScenario scenario,
        CancellationToken cancellationToken)
    {
        Guid closedPeriodId = Guid.CreateVersion7();
        NpgsqlConnection connection = await fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(true);
        await using ConfiguredAsyncDisposable connectionLease =
            connection.ConfigureAwait(false);
        NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(true);
        await using ConfiguredAsyncDisposable transactionLease =
            transaction.ConfigureAwait(false);
        DateTimeOffset databaseNow = await ReadDatabaseClockAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(true);
        await InsertClosedPeriodAsync(
            connection,
            transaction,
            scenario,
            closedPeriodId,
            databaseNow,
            cancellationToken).ConfigureAwait(true);
        SeededEvent closedEvent = await InsertEventAsync(
            connection,
            transaction,
            scenario.GroupId,
            closedPeriodId,
            "period_reset",
            deltaTotal: 500,
            totalAfter: 500,
            databaseNow.AddHours(-2),
            cancellationToken).ConfigureAwait(true);
        SeededEvent currentEvent = await InsertEventAsync(
            connection,
            transaction,
            scenario.GroupId,
            scenario.InitialPeriodId,
            "initialized",
            deltaTotal: 900,
            totalAfter: 900,
            databaseNow.AddMinutes(-1),
            cancellationToken).ConfigureAwait(true);
        await InsertWatermarkAsync(
            connection,
            transaction,
            scenario.GroupId,
            currentEvent.Sequence,
            currentEvent.OccurredAt,
            cancellationToken).ConfigureAwait(true);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(true);
        return new ClosedPeriodSeed(
            closedPeriodId,
            closedEvent.Sequence,
            currentEvent.Sequence,
            closedEvent.OccurredAt,
            currentEvent.OccurredAt);
    }

    private static async ValueTask InsertClosedPeriodAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        M3E1QuotaScenario scenario,
        Guid closedPeriodId,
        DateTimeOffset databaseNow,
        CancellationToken cancellationToken)
    {
        using (NpgsqlCommand renumberCurrent = new("""
                   UPDATE public.group_quota_periods
                   SET period_number = 2
                   WHERE id = $1 AND group_id = $2;
                   """, connection, transaction))
        {
            renumberCurrent.Parameters.AddWithValue(
                NpgsqlDbType.Uuid,
                scenario.InitialPeriodId);
            renumberCurrent.Parameters.AddWithValue(
                NpgsqlDbType.Uuid,
                scenario.GroupId);
            Assert.Equal(
                1,
                await renumberCurrent.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(true));
        }

        using NpgsqlCommand closedPeriod = new("""
            INSERT INTO public.group_quota_periods (
                id, group_id, period_number, total_tokens,
                consumed_tokens, reserved_tokens, status,
                opened_at, closed_at, reset_reason, version)
            VALUES ($1, $2, 1, 500, 0, 0, 'closed', $3, $4,
                'M3-E5 historical reconciliation fixture', 1);
            """, connection, transaction);
        closedPeriod.Parameters.AddWithValue(NpgsqlDbType.Uuid, closedPeriodId);
        closedPeriod.Parameters.AddWithValue(NpgsqlDbType.Uuid, scenario.GroupId);
        closedPeriod.Parameters.AddWithValue(
            NpgsqlDbType.TimestampTz,
            databaseNow.AddHours(-3));
        closedPeriod.Parameters.AddWithValue(
            NpgsqlDbType.TimestampTz,
            databaseNow.AddHours(-2));
        Assert.Equal(
            1,
            await closedPeriod.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(true));
    }

    private static async ValueTask<SeededEvent> InsertEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid groupId,
        Guid periodId,
        string eventType,
        long deltaTotal,
        long totalAfter,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        Guid eventId = Guid.CreateVersion7();
        using NpgsqlCommand command = new("""
            INSERT INTO public.group_quota_events (
                id, group_id, period_id, event_type,
                delta_total_tokens, delta_consumed_tokens,
                delta_reserved_tokens, total_tokens_after,
                consumed_tokens_after, reserved_tokens_after,
                actor_type, idempotency_key, metadata, occurred_at)
            VALUES (
                $1, $2, $3, $4, $5, 0, 0, $6, 0, 0,
                'system', $7, '{}'::jsonb, $8)
            RETURNING event_sequence, occurred_at;
            """, connection, transaction);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, eventId);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, groupId);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, periodId);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, eventType);
        command.Parameters.AddWithValue(NpgsqlDbType.Numeric, new BigInteger(deltaTotal));
        command.Parameters.AddWithValue(NpgsqlDbType.Numeric, new BigInteger(totalAfter));
        command.Parameters.AddWithValue(NpgsqlDbType.Text, $"m3-e5:{eventId:N}");
        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, occurredAt);
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(true);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        SeededEvent result = new(
            reader.GetInt64(0),
            reader.GetFieldValue<DateTimeOffset>(1));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        return result;
    }

    private static async ValueTask InsertWatermarkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid groupId,
        long sourceEventSequence,
        DateTimeOffset completedThrough,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = new("""
            INSERT INTO public.aggregation_watermarks (
                projector_name, partition_key,
                last_event_sequence, completed_through)
            VALUES ('usage-hourly-v1', $1, $2, $3);
            """, connection, transaction);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Text,
            $"poolai.quota.v1:group:{groupId:D}");
        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, sourceEventSequence);
        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, completedThrough);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true));
    }

    private static async ValueTask<DateTimeOffset> ReadDatabaseClockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = new(
            "SELECT clock_timestamp();",
            connection,
            transaction);
        DateTime result = Assert.IsType<DateTime>(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(true));
        return new DateTimeOffset(result.ToUniversalTime());
    }

    private static void AssertAuthoritativeSnapshot(
        JsonElement root,
        Guid groupId,
        Guid periodId,
        string totalTokens)
    {
        Assert.Equal(groupId, root.GetProperty("group_id").GetGuid());
        Assert.Equal(periodId, root.GetProperty("period_id").GetGuid());
        Assert.Equal(totalTokens, root.GetProperty("ledger_total_tokens").GetString());
        Assert.Equal("0", root.GetProperty("ledger_consumed_tokens").GetString());
        Assert.Equal("0", root.GetProperty("ledger_reserved_tokens").GetString());
        Assert.Equal("0", root.GetProperty("fact_consumed_tokens").GetString());
        Assert.Equal("0", root.GetProperty("pending_reservation_tokens").GetString());
        Assert.Equal("0", root.GetProperty("consumed_variance").GetString());
        Assert.Equal("0", root.GetProperty("reserved_variance").GetString());
        Assert.True(root.GetProperty("is_reconciled").GetBoolean());
        AssertCanonicalSuccessStrings(root);
        DateTimeOffset checkedAt = root.GetProperty("checked_at").GetDateTimeOffset();
        DateTimeOffset dataWatermark = root.GetProperty("data_watermark")
            .GetDateTimeOffset();
        Assert.Equal(TimeSpan.Zero, checkedAt.Offset);
        Assert.Equal(TimeSpan.Zero, dataWatermark.Offset);
        Assert.True(checkedAt >= dataWatermark);
    }

    private static void AssertSuccessfulClosedShape(JsonElement actual)
    {
        using JsonDocument canonical = LoadFixture(
            "control-plane-group-quota-reconciliation.json");
        AssertClosedObject(canonical.RootElement, actual);
        Assert.Equal(
            JsonValueKind.Object,
            actual.GetProperty("usage_projection").ValueKind);
        AssertClosedObject(
            canonical.RootElement.GetProperty("usage_projection"),
            actual.GetProperty("usage_projection"));
    }

    private static void AssertClosedObject(JsonElement canonical, JsonElement actual)
    {
        Assert.Equal(JsonValueKind.Object, canonical.ValueKind);
        Assert.Equal(JsonValueKind.Object, actual.ValueKind);
        Assert.Equal(
            canonical.EnumerateObject()
                .Select(static property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            actual.EnumerateObject()
                .Select(static property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    private static void AssertCanonicalSuccessStrings(JsonElement root)
    {
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Name.EndsWith("_tokens", StringComparison.Ordinal))
            {
                AssertCanonicalIntegerString(property.Value, allowNegative: false);
            }
            else if (property.Name.EndsWith("_variance", StringComparison.Ordinal))
            {
                AssertCanonicalIntegerString(property.Value, allowNegative: true);
            }
        }

        JsonElement projection = root.GetProperty("usage_projection");
        foreach (JsonProperty property in projection.EnumerateObject())
        {
            if (property.Name.EndsWith("_tokens", StringComparison.Ordinal)
                || property.Name.EndsWith("_event_sequence", StringComparison.Ordinal))
            {
                AssertCanonicalIntegerString(property.Value, allowNegative: false);
            }
            else if (property.Name.EndsWith("_variance", StringComparison.Ordinal))
            {
                AssertCanonicalIntegerString(property.Value, allowNegative: true);
            }
        }
    }

    private static void AssertCanonicalIntegerString(
        JsonElement element,
        bool allowNegative)
    {
        Assert.Equal(JsonValueKind.String, element.ValueKind);
        string value = Assert.IsType<string>(element.GetString());
        BigInteger parsed = BigInteger.Parse(value, CultureInfo.InvariantCulture);
        Assert.Equal(parsed.ToString(CultureInfo.InvariantCulture), value);
        if (!allowNegative)
        {
            Assert.True(parsed >= BigInteger.Zero);
        }

        Assert.InRange(value.Length, 1, allowNegative ? 79 : 78);
    }

    private static async ValueTask AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode,
        string expectedInstance,
        CancellationToken cancellationToken)
    {
        _ = await ReadProblemAsync(
            response,
            expectedStatus,
            expectedCode,
            expectedInstance,
            cancellationToken).ConfigureAwait(true);
    }

    private static async ValueTask<ProblemSnapshot> ReadProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode,
        string expectedInstance,
        CancellationToken cancellationToken)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        Guid requestId = AssertRequestId(response);
        using JsonDocument document = await ReadJsonAsync(response, cancellationToken)
            .ConfigureAwait(true);
        JsonElement root = document.RootElement;
        Assert.Equal(requestId, root.GetProperty("request_id").GetGuid());
        Assert.Equal((int)expectedStatus, root.GetProperty("status").GetInt32());
        Assert.Equal(expectedCode, root.GetProperty("code").GetString());
        Assert.Equal(expectedInstance, root.GetProperty("instance").GetString());
        Assert.False(root.GetProperty("retryable").GetBoolean());
        Assert.False(root.TryGetProperty("error", out _));
        return new ProblemSnapshot(
            root.GetProperty("type").GetString(),
            root.GetProperty("title").GetString(),
            root.GetProperty("status").GetInt32(),
            root.GetProperty("detail").GetString(),
            root.GetProperty("instance").GetString(),
            root.GetProperty("code").GetString(),
            requestId,
            root.GetProperty("retryable").GetBoolean(),
            string.Join(
                '|',
                root.EnumerateObject()
                    .Select(static property => property.Name)
                    .Order(StringComparer.Ordinal)));
    }

    private static Guid AssertRequestId(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues(
            "X-Request-Id",
            out IEnumerable<string>? values));
        return Guid.Parse(Assert.Single(values), CultureInfo.InvariantCulture);
    }

    private static async ValueTask<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken) => JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(true));

    private static JsonDocument LoadFixture(string fileName) => JsonDocument.Parse(
        File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "contracts",
            "fixtures",
            fileName)));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PoolAI.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The PoolAI repository root was not found.");
    }

    private sealed record SeededEvent(long Sequence, DateTimeOffset OccurredAt);

    private sealed record CurrentLaggingSeed(
        long CheckpointSequence,
        long LatestSequence,
        DateTimeOffset DataThrough,
        DateTimeOffset LatestOccurredAt);

    private sealed record ClosedPeriodSeed(
        Guid ClosedPeriodId,
        long ClosedEventSequence,
        long CurrentEventSequence,
        DateTimeOffset ClosedEventOccurredAt,
        DateTimeOffset DataThrough);

    private sealed record ProblemSnapshot(
        string? Type,
        string? Title,
        int Status,
        string? Detail,
        string? Instance,
        string? Code,
        Guid RequestId,
        bool Retryable,
        string PropertySet);
}
