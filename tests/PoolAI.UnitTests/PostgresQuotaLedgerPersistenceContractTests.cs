using System.Reflection;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application.Ports;
using PoolAI.Modules.GroupQuota.Infrastructure.Persistence;

namespace PoolAI.UnitTests;

public sealed class PostgresQuotaLedgerPersistenceContractTests
{
    [Fact]
    public void SignedDatabaseErrorsMapToStableApplicationFailures()
    {
        // Governing contract: docs/database/README.md section 7 fixes the SQL
        // error vocabulary that the application boundary must translate.
        MethodInfo map = typeof(PostgresQuotaLedgerRepository).GetMethod(
            "MapBusinessError",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        Type operationType = typeof(PostgresQuotaLedgerRepository).GetNestedType(
            "QuotaSqlOperation",
            BindingFlags.NonPublic)!;
        (string Operation, string Code, QuotaLedgerFailure Failure)[] cases =
        [
            ("Reserve", "invalid_quota_reservation", QuotaLedgerFailure.ValidationFailed),
            ("Reserve", "group_disabled", QuotaLedgerFailure.GroupDisabled),
            ("Reserve", "group_quota_disabled", QuotaLedgerFailure.GroupDisabled),
            ("Reserve", "group_quota_exhausted", QuotaLedgerFailure.QuotaExhausted),
            ("Reserve", "group_quota_insufficient", QuotaLedgerFailure.QuotaInsufficient),
            ("Reserve", "group_quota_reserved", QuotaLedgerFailure.QuotaReserved),
            ("Reserve", "token_numeric_overflow", QuotaLedgerFailure.TokenNumericOverflow),
            ("Reserve", "invalid_api_key", QuotaLedgerFailure.InvalidApiKey),
            ("Reserve", "subscription_inactive", QuotaLedgerFailure.SubscriptionInactive),
            ("Reserve", "no_available_account", QuotaLedgerFailure.NoAvailableAccount),
            ("Reserve", "group_quota_not_found", QuotaLedgerFailure.ResourceNotFound),
            ("Reserve", "group_not_found_or_archived", QuotaLedgerFailure.ResourceNotFound),
            ("Reserve", "group_quota_period_not_current", QuotaLedgerFailure.ResourceConflict),
            ("Reserve", "idempotency_key_reused", QuotaLedgerFailure.IdempotencyConflict),
            ("MarkDispatched", "reservation_lease_expired", QuotaLedgerFailure.ReservationLeaseLost),
            ("MarkDispatched", "reservation_max_lifetime_reached", QuotaLedgerFailure.ReservationLeaseLost),
            ("Settle", "reservation_lease_expired", QuotaLedgerFailure.Internal),
            ("AdjustUsage", "unrecognized_contract_error", QuotaLedgerFailure.Internal),
        ];

        foreach ((string operation, string code, QuotaLedgerFailure expected) in cases)
        {
            object operationValue = Enum.Parse(operationType, operation);
            object? actual = map.Invoke(null, [operationValue, code]);

            Assert.Equal(expected, Assert.IsType<QuotaLedgerFailure>(actual));
        }
    }

    [Fact]
    public void SignedEnumsMapToExactDatabaseLexemesAndRejectUnknownValues()
    {
        // Governing contract: migration 0015 signs these textual SQL values;
        // unknown enum values must fail closed before a command is executed.
        Func<UsageRequestEndpoint, string> endpoint = Mapper<UsageRequestEndpoint>("Endpoint");
        Func<SettlementProvider, string> provider = Mapper<SettlementProvider>("Provider");
        Func<UsageAttemptOutcome, string> attempt = Mapper<UsageAttemptOutcome>("AttemptOutcome");
        Func<UsageRequestOutcome, string> request = Mapper<UsageRequestOutcome>("RequestOutcome");
        Func<SettlementUsageSource, string> source = Mapper<SettlementUsageSource>("UsageSource");

        Assert.Equal("/v1/responses", endpoint(UsageRequestEndpoint.Responses));
        Assert.Equal("/v1/chat/completions", endpoint(UsageRequestEndpoint.ChatCompletions));
        Assert.Equal("openai", provider(SettlementProvider.OpenAi));
        Assert.Equal("openai_compatible", provider(SettlementProvider.OpenAiCompatible));
        Assert.Equal("succeeded", attempt(UsageAttemptOutcome.Succeeded));
        Assert.Equal("failed", attempt(UsageAttemptOutcome.Failed));
        Assert.Equal("cancelled", attempt(UsageAttemptOutcome.Cancelled));
        Assert.Equal("succeeded", request(UsageRequestOutcome.Succeeded));
        Assert.Equal("failed", request(UsageRequestOutcome.Failed));
        Assert.Equal("cancelled", request(UsageRequestOutcome.Cancelled));
        Assert.Equal("upstream", source(SettlementUsageSource.Upstream));
        Assert.Equal("local_tokenizer", source(SettlementUsageSource.LocalTokenizer));
        Assert.Equal(
            "conservative_estimate",
            source(SettlementUsageSource.ConservativeEstimate));
        Assert.Equal(
            "confirmed_no_execution",
            source(SettlementUsageSource.ConfirmedNoExecution));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => endpoint((UsageRequestEndpoint)int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => provider((SettlementProvider)int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => attempt((UsageAttemptOutcome)int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => request((UsageRequestOutcome)int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => source((SettlementUsageSource)int.MaxValue));
    }

    [Theory]
    [InlineData("pending", ReservationStatus.Pending)]
    [InlineData("settled", ReservationStatus.Settled)]
    [InlineData("released", ReservationStatus.Released)]
    [InlineData("expired", ReservationStatus.Expired)]
    public void ReservationStatusParserAcceptsOnlySignedLexemes(
        string value,
        ReservationStatus expected)
    {
        Assert.Equal(expected, PostgresQuotaLedgerAbiContract.ParseReservationStatus(value));
    }

    [Fact]
    public void ReservationStatusParserRejectsUnknownLexeme()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => PostgresQuotaLedgerAbiContract.ParseReservationStatus("unknown"));

        Assert.Contains("signed ABI", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AttemptFactValidatorRejectsUnknownUsageSource()
    {
        // Governing contract: DEC-015 closes the persisted usage-source
        // vocabulary. A corrupted enum value must not become a valid fact.
        DateTimeOffset dispatchStartedAt = new(
            2030,
            1,
            2,
            3,
            4,
            5,
            TimeSpan.Zero);
        EntityId attemptId = EntityId.New();
        AttemptSettlementFact fact = new(
            attemptId,
            EntityId.New(),
            0,
            EntityId.New(),
            EntityId.New(),
            EntityId.New(),
            EntityId.New(),
            EntityId.New(),
            SettlementProvider.OpenAi,
            "gpt-contract",
            "gpt-upstream",
            UsageAttemptOutcome.Succeeded,
            200,
            null,
            false,
            new AttemptUsage(
                new TokenUsage(6, 4, 0, 0, 0),
                (SettlementUsageSource)int.MaxValue,
                false),
            null,
            dispatchStartedAt,
            dispatchStartedAt.AddTicks(10),
            dispatchStartedAt.AddTicks(20));
        MethodInfo validate = typeof(PostgresQuotaLedgerAbiContract).GetMethod(
            "ValidateAttemptFact",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(
            () => validate.Invoke(null, [fact, attemptId]));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    private static Func<T, string> Mapper<T>(string methodName)
        where T : struct, Enum => typeof(PostgresQuotaLedgerRepository)
            .GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<T, string>>();
}
