#pragma warning disable MA0048 // Focused service fakes stay beside the frozen-route tests.
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Supply.Application;
using PoolAI.Modules.Supply.Application.Ports;
using PoolAI.Modules.Supply.Domain;

namespace PoolAI.UnitTests;

public sealed class GroupSupplyControlPlaneServiceTests
{
    [Fact]
    public async Task CreateUsesFrozenSupplyConfigurationLocationAndScope()
    {
        EntityId groupId = EntityId.New();
        Fixture fixture = new(groupId);
        Result<SupplyCommandOutcome<GroupSupplyConfigurationView>> result =
            await fixture.Service.ExecuteAsync(
                new CreateGroupSupplyConfigurationCommand(
                    EntityId.New(),
                    Admin(),
                    "create-key",
                    groupId,
                    ChannelId: null,
                    AccountBindings: [],
                    IpAddress: null,
                    UserAgent: null),
                TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        string path =
            $"/api/v1/admin/groups/{groupId.Value:D}/supply-configuration";
        Assert.Equal(path, result.Value.Location);
        Assert.EndsWith(
            $":post:{path}",
            fixture.Idempotency.Request?.Scope,
            StringComparison.Ordinal);
        Assert.True(fixture.UnitOfWork.Committed);
    }

    [Fact]
    public async Task PatchUsesFrozenSupplyConfigurationScope()
    {
        EntityId groupId = EntityId.New();
        Fixture fixture = new(groupId);
        Result<SupplyCommandOutcome<GroupSupplyConfigurationView>> result =
            await fixture.Service.ExecuteAsync(
                new PatchGroupSupplyConfigurationCommand(
                    EntityId.New(),
                    Admin(),
                    "patch-key",
                    groupId,
                    ExpectedVersion: 1,
                    ChannelSpecified: true,
                    ChannelId: null,
                    AccountBindingsSpecified: false,
                    AccountBindings: null,
                    Reason: "clear channel",
                    IpAddress: null,
                    UserAgent: null),
                TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        string path =
            $"/api/v1/admin/groups/{groupId.Value:D}/supply-configuration";
        Assert.EndsWith(
            $":patch:{path}",
            fixture.Idempotency.Request?.Scope,
            StringComparison.Ordinal);
        Assert.Null(result.Value.Location);
    }

    private static AccountActor Admin() => new(
        EntityId.New(),
        AccountControlRole.Admin,
        TokenVersion: 1);

    private sealed class Fixture
    {
        internal Fixture(EntityId groupId)
        {
            Repository = new FakeRepository(groupId);
            UnitOfWork = new FakeUnitOfWork();
            Idempotency = new FakeIdempotencyStore();
            GroupSupplyCommandCoordinator coordinator = new(
                Idempotency,
                new FakeAuditAppender(),
                new FakeOutboxAppender(),
                new AccountControlPlanePolicy(new byte[32]));
            Service = new GroupSupplyControlPlaneService(
                Repository,
                new FakeUnitOfWorkFactory(UnitOfWork),
                coordinator);
        }

        internal FakeRepository Repository { get; }

        internal FakeUnitOfWork UnitOfWork { get; }

        internal FakeIdempotencyStore Idempotency { get; }

        internal GroupSupplyControlPlaneService Service { get; }
    }

    private sealed class FakeRepository(EntityId groupId) :
        IGroupSupplyConfigurationRepository
    {
        private readonly EntityId _groupId = groupId;
        private long _version = 1;

        public ValueTask<GroupSupplyConfigurationResource?> GetAsync(
            EntityId requestedGroupId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<GroupSupplyConfigurationResource?>(
                Resource(requestedGroupId));

        public ValueTask<GroupSupplyMutationResult> CreateAsync(
            GroupSupplyConfigurationCreateWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new GroupSupplyMutationResult(
                GroupSupplyMutationDisposition.Written,
                WasChanged: true,
                Resource(write.GroupId),
                Before: null,
                CurrentVersion: _version));

        public ValueTask<GroupSupplyMutationResult> PatchAsync(
            GroupSupplyConfigurationPatchWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            GroupSupplyConfigurationResource before = Resource(write.GroupId);
            _version++;
            return ValueTask.FromResult(new GroupSupplyMutationResult(
                GroupSupplyMutationDisposition.Written,
                WasChanged: true,
                Resource(write.GroupId),
                before,
                CurrentVersion: _version));
        }

        private GroupSupplyConfigurationResource Resource(EntityId requestedId)
        {
            Assert.Equal(_groupId, requestedId);
            return new GroupSupplyConfigurationResource(
                requestedId,
                ChannelId: null,
                AccountBindings: [],
                _version,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch);
        }
    }

    private sealed class FakeUnitOfWorkFactory(FakeUnitOfWork unitOfWork) :
        IUnitOfWorkFactory
    {
        public ValueTask<IUnitOfWork> BeginAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IUnitOfWork>(unitOfWork);
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public IUnitOfWorkContext Context { get; } = new FakeContext();

        internal bool Committed { get; private set; }

        public ValueTask CommitAsync(CancellationToken cancellationToken)
        {
            Committed = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class FakeContext : IUnitOfWorkContext;
    }

    private sealed class FakeIdempotencyStore : ICommandIdempotencyStore
    {
        internal CommandIdempotencyRequest? Request { get; private set; }

        public ValueTask<CommandIdempotencyAcquireResult> AcquireAsync(
            CommandIdempotencyRequest request,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            Request = request;
            return ValueTask.FromResult(
                CommandIdempotencyAcquireResult.Acquired(
                    new CommandIdempotencyLease(
                        request.Scope,
                        request.Key,
                        request.Owner,
                        Generation: 1,
                        Version: 1)));
        }

        public ValueTask<bool> HeartbeatAsync(
            CommandIdempotencyHeartbeat heartbeat,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);

        public ValueTask<bool> CompleteAsync(
            CommandIdempotencyCompletion completion,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);
    }

    private sealed class FakeAuditAppender : IAuditAppender
    {
        public ValueTask AppendAsync(
            AuditEntry entry,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class FakeOutboxAppender : IOutboxAppender
    {
        public ValueTask AppendAsync(
            IntegrationEvent integrationEvent,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
#pragma warning restore MA0048
