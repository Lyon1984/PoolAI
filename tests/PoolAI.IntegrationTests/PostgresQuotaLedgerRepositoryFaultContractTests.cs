#pragma warning disable MA0051 // Five signed repository operations stay explicit in fault probes.
using System.Numerics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application.Ports;
using PoolAI.Modules.GroupQuota.Infrastructure.Persistence;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class PostgresQuotaLedgerRepositoryFaultContractTests(
    PostgresRuntimeFixture fixture)
{
    private static readonly DateTimeOffset ContractTime = new(
        2030,
        1,
        2,
        3,
        4,
        5,
        TimeSpan.Zero);

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task EveryLedgerWriteMapsTerminatedDatabaseSessionToDependencyUnavailable()
    {
        // Governing contract: DEC-015 requires all ledger mutations to fail
        // closed as a retryable dependency failure when PostgreSQL disappears.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        IQuotaLedgerRepository repository = fixture.ApiServices
            .GetRequiredService<IQuotaLedgerRepository>();
        IUnitOfWorkFactory units = fixture.ApiServices
            .GetRequiredService<IUnitOfWorkFactory>();
        FaultProbe[] probes =
        [
            ReserveAfterTerminationAsync,
            MarkDispatchAfterTerminationAsync,
            SettleAfterTerminationAsync,
            ReleaseAfterTerminationAsync,
            AdjustAfterTerminationAsync,
        ];

        foreach (FaultProbe probe in probes)
        {
            IUnitOfWork unit = await units.BeginAsync(cancellationToken).ConfigureAwait(true);
            try
            {
                await TerminateBackendAsync(unit.Context, cancellationToken)
                    .ConfigureAwait(true);
                QuotaLedgerFailure failure = await probe(
                    repository,
                    unit.Context,
                    cancellationToken).ConfigureAwait(true);

                Assert.Equal(QuotaLedgerFailure.DependencyUnavailable, failure);
            }
            finally
            {
                await DisposeTerminatedUnitAsync(unit).ConfigureAwait(true);
            }
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task MissingDispatchReservationUsesSignedBusinessFailureMapping()
    {
        // Governing contract: migration 0015 returns group_quota_not_found for
        // a dispatch whose quota root is absent; the adapter maps it stably.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        IQuotaLedgerRepository repository = fixture.ApiServices
            .GetRequiredService<IQuotaLedgerRepository>();
        IUnitOfWorkFactory units = fixture.ApiServices
            .GetRequiredService<IUnitOfWorkFactory>();
        await using IUnitOfWork unit = await units.BeginAsync(cancellationToken)
            .ConfigureAwait(true);

        QuotaRepositoryResult<QuotaDispatchRow> result = await repository
            .MarkDispatchedAsync(DispatchWrite(), unit.Context, cancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(QuotaLedgerFailure.ResourceNotFound, result.Failure);
        Assert.Null(result.Value);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task NonInitialAttemptWithoutUsageRequestFailsClosedBeforeReserve()
    {
        // Governing contract: DEC-015 requires every attempt to retain the
        // immutable request identity established by attempt zero.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        IQuotaLedgerRepository repository = fixture.ApiServices
            .GetRequiredService<IQuotaLedgerRepository>();
        IUnitOfWorkFactory units = fixture.ApiServices
            .GetRequiredService<IUnitOfWorkFactory>();
        await using IUnitOfWork unit = await units.BeginAsync(cancellationToken)
            .ConfigureAwait(true);

        QuotaRepositoryResult<QuotaReservationRow> result = await repository.ReserveAsync(
            ReserveWrite(attemptIndex: 1),
            unit.Context,
            cancellationToken).ConfigureAwait(true);

        Assert.Equal(QuotaLedgerFailure.Internal, result.Failure);
        Assert.Null(result.Value);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task EmptyReservationAttemptShapeFailsClosed()
    {
        // Governing contract: DEC-015 requires indices to be contiguous from
        // zero. An aggregate over an unknown request has null min/max and is invalid.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        IUnitOfWorkFactory units = fixture.ApiServices
            .GetRequiredService<IUnitOfWorkFactory>();
        await using IUnitOfWork unit = await units.BeginAsync(cancellationToken)
            .ConfigureAwait(true);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unit.Context);
        ReservationShapeValidator validator = typeof(PostgresQuotaLedgerRepository)
            .GetMethod(
                "ValidateReservationAttemptIndicesAsync",
                BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<ReservationShapeValidator>();

        bool isValid = await validator(session, EntityId.New(), cancellationToken)
            .ConfigureAwait(true);

        Assert.False(isValid);
    }

    private async ValueTask TerminateBackendAsync(
        IUnitOfWorkContext context,
        CancellationToken cancellationToken)
    {
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(context);
        using NpgsqlCommand pidCommand = session.CreateCommand("SELECT pg_backend_pid();");
        int backendPid = Assert.IsType<int>(
            await pidCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(true));
        using NpgsqlCommand terminate = fixture.AdministratorDataSource.CreateCommand(
            "SELECT pg_terminate_backend($1);");
        terminate.Parameters.AddWithValue(backendPid);
        bool terminated = Assert.IsType<bool>(
            await terminate.ExecuteScalarAsync(cancellationToken).ConfigureAwait(true));

        Assert.True(terminated);
    }

    private static async ValueTask DisposeTerminatedUnitAsync(IUnitOfWork unit)
    {
        try
        {
            await unit.DisposeAsync().ConfigureAwait(true);
        }
        catch (NpgsqlException exception) when (exception.IsTransient)
        {
            // The connection was deliberately terminated after the repository
            // translated the failure; rollback cannot contact that backend.
        }
        catch (ObjectDisposedException exception)
            when (exception.InnerException is NpgsqlException { IsTransient: true })
        {
            // Npgsql can surface the same deliberate backend termination by
            // marking its transaction disposed before the rollback attempt.
        }
    }

    private static async ValueTask<QuotaLedgerFailure> ReserveAfterTerminationAsync(
        IQuotaLedgerRepository repository,
        IUnitOfWorkContext context,
        CancellationToken cancellationToken) => (await repository.ReserveAsync(
            ReserveWrite(attemptIndex: 0),
            context,
            cancellationToken).ConfigureAwait(true)).Failure;

    private static async ValueTask<QuotaLedgerFailure> MarkDispatchAfterTerminationAsync(
        IQuotaLedgerRepository repository,
        IUnitOfWorkContext context,
        CancellationToken cancellationToken) => (await repository.MarkDispatchedAsync(
            DispatchWrite(),
            context,
            cancellationToken).ConfigureAwait(true)).Failure;

    private static async ValueTask<QuotaLedgerFailure> SettleAfterTerminationAsync(
        IQuotaLedgerRepository repository,
        IUnitOfWorkContext context,
        CancellationToken cancellationToken) => (await repository.SettleAsync(
            SettleWrite(),
            context,
            cancellationToken).ConfigureAwait(true)).Failure;

    private static async ValueTask<QuotaLedgerFailure> ReleaseAfterTerminationAsync(
        IQuotaLedgerRepository repository,
        IUnitOfWorkContext context,
        CancellationToken cancellationToken) => (await repository.ReleaseAsync(
            ReleaseWrite(),
            context,
            cancellationToken).ConfigureAwait(true)).Failure;

    private static async ValueTask<QuotaLedgerFailure> AdjustAfterTerminationAsync(
        IQuotaLedgerRepository repository,
        IUnitOfWorkContext context,
        CancellationToken cancellationToken) => (await repository.AdjustUsageAsync(
            AdjustmentWrite(),
            context,
            cancellationToken).ConfigureAwait(true)).Failure;

    private static ReserveQuotaWrite ReserveWrite(int attemptIndex) => new(
        new ReserveQuotaCommand(
            EntityId.New(),
            EntityId.New(),
            attemptIndex,
            EntityId.New(),
            EntityId.New(),
            EntityId.New(),
            EntityId.New(),
            EntityId.New(),
            EntityId.New(),
            UsageRequestEndpoint.Responses,
            "gpt-contract",
            null,
            10,
            false,
            "fault-contract"),
        EntityId.New(),
        Mutation());

    private static MarkReservationDispatchedWrite DispatchWrite() => new(
        new MarkReservationDispatchedCommand(
            Reservation(),
            SettlementProvider.OpenAi,
            "gpt-contract",
            new TokenEstimateSplit(6, 4)),
        Mutation());

    private static SettleReservationWrite SettleWrite()
    {
        ReservationHandle reservation = Reservation();
        DispatchedReservationHandle dispatched = new(
            ReservationStatus.Pending,
            reservation,
            SettlementProvider.OpenAi,
            "gpt-contract",
            new TokenEstimateSplit(6, 4),
            ContractTime);
        return new SettleReservationWrite(
            new SettleReservationCommand(
                dispatched,
                UsageAttemptOutcome.Succeeded,
                200,
                null,
                null,
                ContractTime.AddTicks(10),
                ContractTime.AddTicks(20),
                UsageRequestOutcome.Succeeded,
                new TokenUsage(6, 4, 0, 0, 0),
                SettlementUsageSource.Upstream,
                null),
            Mutation());
    }

    private static ReleaseReservationWrite ReleaseWrite() => new(
        new ReleaseReservationCommand(Reservation(), "fault contract"),
        Mutation());

    private static AdjustAttemptUsageWrite AdjustmentWrite() => new(
        new AdjustAttemptUsageCommand(
            EntityId.New(),
            EntityId.New(),
            EntityId.New(),
            EntityId.New(),
            SettlementProvider.OpenAi,
            "gpt-contract",
            UsageAttemptOutcome.Succeeded,
            200,
            null,
            null,
            ContractTime,
            ContractTime.AddTicks(10),
            ContractTime.AddTicks(20),
            UsageRequestOutcome.Succeeded,
            new TokenUsage(6, 4, 0, 0, 0),
            SettlementUsageSource.Upstream,
            null,
            "fault contract"),
        Mutation());

    private static ReservationHandle Reservation() => new(
        EntityId.New(),
        EntityId.New(),
        EntityId.New(),
        0,
        EntityId.New(),
        EntityId.New(),
        EntityId.New(),
        EntityId.New(),
        10,
        false,
        "fault-contract",
        ContractTime.AddMinutes(1),
        ContractTime.AddMinutes(2));

    private static QuotaMutationIdentity Mutation() => new(
        EntityId.New(),
        EntityId.New(),
        $"fault:{Guid.CreateVersion7():N}");

    private delegate ValueTask<QuotaLedgerFailure> FaultProbe(
        IQuotaLedgerRepository repository,
        IUnitOfWorkContext context,
        CancellationToken cancellationToken);

    private delegate ValueTask<bool> ReservationShapeValidator(
        PostgresTransactionSession session,
        EntityId requestId,
        CancellationToken cancellationToken);
}
#pragma warning restore MA0051
