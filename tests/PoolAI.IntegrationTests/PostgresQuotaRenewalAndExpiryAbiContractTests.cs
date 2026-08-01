using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application.Ports;
using PoolAI.Modules.GroupQuota.Infrastructure.Persistence;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class PostgresQuotaRenewalAndExpiryAbiContractTests(
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
    public async Task RenewalReaderAcceptsFreshResultAndIdempotentReplay()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        RenewalShape valid = new(
            EntityId.New(),
            EntityId.New(),
            "pending",
            ContractTime.AddMinutes(2),
            ContractTime.AddMinutes(10));
        RenewReservationWrite write = RenewalWrite(
            valid,
            ContractTime.AddMinutes(1));

        QuotaRenewalRow renewed = await ReadRenewalAsync(
            valid,
            write,
            rowCount: 1,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(valid.LeaseExpiresAt, renewed.LeaseExpiresAt);

        RenewReservationWrite replayWrite = RenewalWrite(valid, valid.LeaseExpiresAt);
        QuotaRenewalRow replay = await ReadRenewalAsync(
            valid,
            replayWrite,
            rowCount: 1,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(valid.LeaseExpiresAt, replay.LeaseExpiresAt);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task RenewalReaderRejectsMalformedSignedLease()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        RenewalShape valid = new(
            EntityId.New(),
            EntityId.New(),
            "pending",
            ContractTime.AddMinutes(2),
            ContractTime.AddMinutes(10));
        RenewReservationWrite write = RenewalWrite(
            valid,
            ContractTime.AddMinutes(1));

        await AssertInvalidRenewalAsync(valid, write, 0, cancellationToken)
            .ConfigureAwait(true);
        await AssertInvalidRenewalAsync(valid, write, 2, cancellationToken)
            .ConfigureAwait(true);
        await AssertInvalidRenewalAsync(
            valid with { ReservationId = EntityId.New() },
            write,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidRenewalAsync(
            valid with { PeriodId = EntityId.New() },
            write,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidRenewalAsync(
            valid with { Status = "settled" },
            write,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidRenewalAsync(
            valid with { LeaseExpiresAt = ContractTime },
            write,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidRenewalAsync(
            valid with { MaxExpiresAt = valid.MaxExpiresAt.AddMilliseconds(1) },
            write,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidRenewalAsync(
            valid with { MaxExpiresAt = valid.LeaseExpiresAt.AddMilliseconds(-1) },
            write,
            1,
            cancellationToken).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ExpiryCandidateReaderEnforcesStrictDatabaseKeysetPage()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateTimeOffset dueAt = ContractTime.AddMinutes(-1);
        CandidateShape first = Candidate(
            "01900000-0000-7000-8000-000000000001",
            dueAt);
        CandidateShape second = Candidate(
            "01900000-0000-7000-8000-000000000002",
            dueAt);

        using (NpgsqlCommand command = CandidateCommand(first, second))
        {
            IReadOnlyList<QuotaExpiryCandidate> page =
                await PostgresQuotaLedgerAbiContract.ReadExpiryCandidatesAsync(
                    command,
                    after: null,
                    pageSize: 2,
                    cancellationToken).ConfigureAwait(true);

            Assert.Equal([first.ReservationId, second.ReservationId],
                page.Select(candidate => candidate.ReservationId));
        }

        await AssertInvalidCandidatesAsync(
            second,
            first,
            after: null,
            pageSize: 2,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidCandidatesAsync(
            first,
            second,
            first.Key,
            pageSize: 2,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidCandidatesAsync(
            first,
            second,
            after: null,
            pageSize: 1,
            cancellationToken).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task RenewalAndExpiryMapTerminatedSessionToDependencyUnavailable()
    {
        // Governing contract: DEC-026 and DEC-036 require renewal and expiry
        // to fail closed when their short PostgreSQL Unit of Work loses the backend.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        IQuotaLedgerRepository repository = fixture.ApiServices
            .GetRequiredService<IQuotaLedgerRepository>();
        IUnitOfWorkFactory units = fixture.ApiServices
            .GetRequiredService<IUnitOfWorkFactory>();
        FaultProbe[] probes =
        [
            RenewAfterTerminationAsync,
            ExpireAfterTerminationAsync,
        ];

        foreach (FaultProbe probe in probes)
        {
            IUnitOfWork unit = await units.BeginAsync(cancellationToken)
                .ConfigureAwait(true);
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
    public async Task ExpiryRepositoryUsesStrictOptionalKeysetAndPositivePageSize()
    {
        // Governing contract: M3-E3's sweeper must use bounded, strictly
        // advancing PostgreSQL pages; an impossible future cursor is empty.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        IQuotaLedgerRepository repository = fixture.WorkerServices
            .GetRequiredService<IQuotaLedgerRepository>();
        IUnitOfWorkFactory units = fixture.WorkerServices
            .GetRequiredService<IUnitOfWorkFactory>();
        await using IUnitOfWork unit = await units.BeginAsync(cancellationToken)
            .ConfigureAwait(true);

        IReadOnlyList<QuotaExpiryCandidate> page = await repository
            .ListDueExpiryCandidatesAsync(
                new QuotaExpiryCandidateKey(
                    ContractTime,
                    new EntityId(Guid.Parse("ffffffff-ffff-7fff-bfff-ffffffffffff"))),
                pageSize: 1,
                unit.Context,
                cancellationToken)
            .ConfigureAwait(true);

        Assert.Empty(page);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await repository.ListDueExpiryCandidatesAsync(
                after: null,
                pageSize: 0,
                unit.Context,
                cancellationToken).ConfigureAwait(true)).ConfigureAwait(true);
    }

    private async ValueTask<QuotaRenewalRow> ReadRenewalAsync(
        RenewalShape shape,
        RenewReservationWrite write,
        int rowCount,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = RenewalCommand(shape, rowCount);
        return await PostgresQuotaLedgerAbiContract.ReadRenewalAsync(
            command,
            write,
            cancellationToken).ConfigureAwait(true);
    }

    private async ValueTask AssertInvalidRenewalAsync(
        RenewalShape shape,
        RenewReservationWrite write,
        int rowCount,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = RenewalCommand(shape, rowCount);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await PostgresQuotaLedgerAbiContract.ReadRenewalAsync(
                command,
                write,
                cancellationToken).ConfigureAwait(true)).ConfigureAwait(true);
    }

    private async ValueTask AssertInvalidCandidatesAsync(
        CandidateShape first,
        CandidateShape second,
        QuotaExpiryCandidateKey? after,
        int pageSize,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = CandidateCommand(first, second);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await PostgresQuotaLedgerAbiContract.ReadExpiryCandidatesAsync(
                command,
                after,
                pageSize,
                cancellationToken).ConfigureAwait(true)).ConfigureAwait(true);
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
            // Deliberately terminated backend; rollback cannot reach it.
        }
        catch (ObjectDisposedException exception)
            when (exception.InnerException is NpgsqlException { IsTransient: true })
        {
            // Npgsql may surface termination by disposing its transaction first.
        }
    }

    private static async ValueTask<QuotaLedgerFailure> RenewAfterTerminationAsync(
        IQuotaLedgerRepository repository,
        IUnitOfWorkContext context,
        CancellationToken cancellationToken)
    {
        RenewalShape shape = new(
            EntityId.New(),
            EntityId.New(),
            "pending",
            ContractTime.AddMinutes(2),
            ContractTime.AddMinutes(10));
        return (await repository.RenewAsync(
            RenewalWrite(shape, ContractTime.AddMinutes(1)),
            context,
            cancellationToken).ConfigureAwait(true)).Failure;
    }

    private static async ValueTask<QuotaLedgerFailure> ExpireAfterTerminationAsync(
        IQuotaLedgerRepository repository,
        IUnitOfWorkContext context,
        CancellationToken cancellationToken) => (await repository.ExpireAsync(
            new ExpireReservationWrite(
                new QuotaExpiryCandidate(
                    EntityId.New(),
                    EntityId.New(),
                    EntityId.New(),
                    EntityId.New(),
                    ContractTime),
                Mutation(),
                "fault contract"),
            context,
            cancellationToken).ConfigureAwait(true)).Failure;

    private NpgsqlCommand RenewalCommand(RenewalShape shape, int rowCount)
    {
        NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            SELECT
                $1::uuid,
                $2::uuid,
                $3::text,
                $4::timestamptz,
                $5::timestamptz
            FROM generate_series(1, $6::integer);
            """);
        AddUuid(command, shape.ReservationId.Value);
        AddUuid(command, shape.PeriodId.Value);
        AddText(command, shape.Status);
        AddTimestamp(command, shape.LeaseExpiresAt);
        AddTimestamp(command, shape.MaxExpiresAt);
        AddInteger(command, rowCount);
        return command;
    }

    private NpgsqlCommand CandidateCommand(CandidateShape first, CandidateShape second)
    {
        NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            SELECT candidate.reservation_id,
                   candidate.attempt_id,
                   candidate.group_id,
                   candidate.period_id,
                   candidate.lease_expires_at
            FROM (VALUES
                ($1::uuid, $2::uuid, $3::uuid, $4::uuid, $5::timestamptz, 1),
                ($6::uuid, $7::uuid, $8::uuid, $9::uuid, $10::timestamptz, 2)
            ) AS candidate(
                reservation_id,
                attempt_id,
                group_id,
                period_id,
                lease_expires_at,
                ordinal)
            ORDER BY candidate.ordinal;
            """);
        AddCandidate(command, first);
        AddCandidate(command, second);
        return command;
    }

    private static RenewReservationWrite RenewalWrite(
        RenewalShape shape,
        DateTimeOffset priorLeaseExpiresAt)
    {
        ReservationHandle reservation = new(
            shape.ReservationId,
            EntityId.New(),
            EntityId.New(),
            0,
            EntityId.New(),
            shape.PeriodId,
            EntityId.New(),
            EntityId.New(),
            30,
            false,
            "abi-test",
            priorLeaseExpiresAt,
            shape.MaxExpiresAt);
        return new RenewReservationWrite(
            new RenewReservationCommand(reservation, RenewalSequence: 1),
            Mutation());
    }

    private static CandidateShape Candidate(string reservationId, DateTimeOffset dueAt) =>
        new(
            new EntityId(Guid.Parse(reservationId)),
            EntityId.New(),
            EntityId.New(),
            EntityId.New(),
            dueAt);

    private static void AddCandidate(NpgsqlCommand command, CandidateShape shape)
    {
        AddUuid(command, shape.ReservationId.Value);
        AddUuid(command, shape.AttemptId.Value);
        AddUuid(command, shape.GroupId.Value);
        AddUuid(command, shape.PeriodId.Value);
        AddTimestamp(command, shape.LeaseExpiresAt);
    }

    private static void AddUuid(NpgsqlCommand command, Guid value) =>
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, value);

    private static void AddText(NpgsqlCommand command, string value) =>
        command.Parameters.AddWithValue(NpgsqlDbType.Text, value);

    private static void AddTimestamp(NpgsqlCommand command, DateTimeOffset value) =>
        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, value);

    private static void AddInteger(NpgsqlCommand command, int value) =>
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, value);

    private static QuotaMutationIdentity Mutation() => new(
        EntityId.New(),
        EntityId.New(),
        $"abi:{Guid.CreateVersion7():N}");

    private sealed record RenewalShape(
        EntityId ReservationId,
        EntityId PeriodId,
        string Status,
        DateTimeOffset LeaseExpiresAt,
        DateTimeOffset MaxExpiresAt);

    private sealed record CandidateShape(
        EntityId ReservationId,
        EntityId AttemptId,
        EntityId GroupId,
        EntityId PeriodId,
        DateTimeOffset LeaseExpiresAt)
    {
        internal QuotaExpiryCandidateKey Key => new(LeaseExpiresAt, ReservationId);
    }

    private delegate ValueTask<QuotaLedgerFailure> FaultProbe(
        IQuotaLedgerRepository repository,
        IUnitOfWorkContext context,
        CancellationToken cancellationToken);
}
