#pragma warning disable MA0051 // One vertical slice keeps setup, canonical reads, and invalidation assertions together.
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Application.Ports;
using PoolAI.Modules.Supply.Domain;
using PoolAI.Modules.Supply.Infrastructure.Persistence;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class SupplyControlPlanePostgresRuntimeTests(
    PostgresRuntimeFixture fixture)
{
    private readonly PostgresRuntimeFixture _fixture =
        fixture ?? throw new ArgumentNullException(nameof(fixture));

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task CanonicalConfigurationDrivesReadinessCandidatesAndModels()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        EntityId groupId = EntityId.New();
        EntityId accountId = EntityId.New();
        EntityId channelId = EntityId.New();
        await SeedGroupAndAccountAsync(
            groupId,
            accountId,
            cancellationToken).ConfigureAwait(true);

        NpgsqlDataSource dataSource =
            _fixture.ApiServices.GetRequiredService<NpgsqlDataSource>();
        PostgresChannelControlPlaneRepository channels = new(dataSource);
        PostgresGroupSupplyConfigurationRepository configurations = new(dataSource);
        PostgresAccountCandidateReader candidates = new(dataSource);
        PostgresModelCatalog models = new(dataSource);
        PostgresGroupSupplyReadiness readiness = new(dataSource);

        ChannelCapabilitiesValue capabilities = new(
            Responses: true,
            ChatCompletions: true,
            FunctionTools: true,
            Streaming: true);
        IReadOnlyList<ChannelModelMappingValue> mappings =
            ChannelInput.ModelMappings(
            [
                new("client-model", "upstream-model"),
            ]);
        ChannelMutationResult createdChannel = await CommitAsync(
            (context, token) => channels.CreateAsync(
                new ChannelCreateWrite(
                    channelId,
                    UpstreamProvider.OpenAiCompatible,
                    "integration channel",
                    capabilities,
                    mappings),
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(ChannelMutationDisposition.Written, createdChannel.Disposition);
        Assert.Equal(ChannelResourceStatus.Disabled, createdChannel.Value?.Status);

        ChannelMutationResult activatedChannel = await CommitAsync(
            (context, token) => channels.UpdateAsync(
                new ChannelUpdateWrite(
                    channelId,
                    ExpectedVersion: 1,
                    NameSpecified: false,
                    Name: null,
                    StatusSpecified: true,
                    Status: ChannelResourceStatus.Active,
                    CapabilitiesSpecified: false,
                    Capabilities: null,
                    ModelMappingsSpecified: false,
                    ModelMappings: null,
                    Reason: "activate for integration"),
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(ChannelResourceStatus.Active, activatedChannel.Value?.Status);
        Assert.Equal(2, activatedChannel.CurrentVersion);

        GroupSupplyMutationResult createdConfiguration = await CommitAsync(
            (context, token) => configurations.CreateAsync(
                new GroupSupplyConfigurationCreateWrite(
                    groupId,
                    channelId,
                    GroupSupplyInput.Bindings(
                    [
                        new GroupSupplyBindingValue(
                            accountId,
                            Enabled: true,
                            PriorityOverride: 42,
                            WeightOverride: 250),
                    ])),
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            GroupSupplyMutationDisposition.Written,
            createdConfiguration.Disposition);
        GroupSupplyConfigurationResource configuration =
            Assert.IsType<GroupSupplyConfigurationResource>(
                createdConfiguration.Value);
        Assert.True(configuration.Version > 1);

        Result<GroupSupplyConfigurationSnapshot> published =
            await configurations.GetCurrentAsync(
                groupId,
                cancellationToken).ConfigureAwait(true);
        Assert.True(published.IsSuccess);
        Assert.Equal(configuration.Version, published.Value.Version);
        Assert.Single(published.Value.AccountBindings);

        Result<IReadOnlyList<AccountCandidate>> candidateResult =
            await candidates.GetCandidatesAsync(
                groupId,
                "client-model",
                cancellationToken).ConfigureAwait(true);
        AccountCandidate candidate = Assert.Single(candidateResult.Value);
        Assert.Equal(groupId, candidate.GroupId);
        Assert.Equal(channelId, candidate.ChannelId);
        Assert.Equal(accountId, candidate.AccountId);
        Assert.Equal("upstream-model", candidate.UpstreamModel);
        Assert.Equal("https://fixture.invalid/v1", candidate.UpstreamBaseUrl);
        Assert.Equal(42, candidate.Priority);
        Assert.Equal(250, candidate.Weight);
        Assert.Equal(configuration.Version, candidate.ConfigurationVersion);
        Assert.Equal(2, candidate.ChannelVersion);

        Result<IReadOnlyList<string>> modelResult = await models.GetModelsAsync(
            groupId,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(["client-model"], modelResult.Value);

        Result<SupplyReadinessSnapshot> ready = await readiness.ObserveAsync(
            groupId,
            cancellationToken).ConfigureAwait(true);
        Assert.True(ready.IsSuccess);
        Assert.True(ready.Value.IsReady);
        Assert.Equal(configuration.Version, ready.Value.ConfigurationVersion);
        Assert.StartsWith("v1.", ready.Value.OpaqueToken, StringComparison.Ordinal);

        GroupSupplyMutationResult disabled = await CommitAsync(
            (context, token) => configurations.PatchAsync(
                new GroupSupplyConfigurationPatchWrite(
                    groupId,
                    configuration.Version,
                    ChannelSpecified: false,
                    ChannelId: null,
                    AccountBindingsSpecified: true,
                    GroupSupplyInput.Bindings(
                    [
                        new GroupSupplyBindingValue(
                            accountId,
                            Enabled: false,
                            PriorityOverride: 42,
                            WeightOverride: 250),
                    ]),
                    "disable integration binding"),
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(GroupSupplyMutationDisposition.Written, disabled.Disposition);
        Assert.True(disabled.Value?.Version > configuration.Version);

        Result<SupplyReadinessSnapshot> notReady = await readiness.ObserveAsync(
            groupId,
            cancellationToken).ConfigureAwait(true);
        Assert.True(notReady.IsFailure);
        Assert.Equal("group_activation_not_ready", notReady.Error.Code);
        Assert.Empty((await candidates.GetCandidatesAsync(
            groupId,
            "client-model",
            cancellationToken).ConfigureAwait(true)).Value);
        Assert.Empty((await models.GetModelsAsync(
            groupId,
            cancellationToken).ConfigureAwait(true)).Value);
    }

    private async ValueTask<T> CommitAsync<T>(
        Func<IUnitOfWorkContext, CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken)
    {
        IUnitOfWorkFactory factory =
            _fixture.ApiServices.GetRequiredService<IUnitOfWorkFactory>();
        IUnitOfWork unitOfWork = await factory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        T result = await action(unitOfWork.Context, cancellationToken)
            .ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async ValueTask SeedGroupAndAccountAsync(
        EntityId groupId,
        EntityId accountId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            WITH inserted_group AS (
                INSERT INTO public.groups (
                    id, name, status
                ) VALUES (
                    $1, $2, 'disabled'
                )
                RETURNING id
            )
            INSERT INTO public.accounts (
                id,
                provider,
                name,
                auth_type,
                upstream_base_url,
                credential_envelope,
                credential_prefix,
                status,
                priority,
                weight,
                max_concurrency,
                last_health_at,
                last_health_status
            )
            SELECT
                $3,
                'openai_compatible',
                $4,
                'api_key',
                'https://fixture.invalid/v1',
                '{}'::jsonb,
                'fixture',
                'active',
                5,
                100,
                7,
                clock_timestamp(),
                'healthy'
            FROM inserted_group;
            """);
        command.Parameters.AddWithValue(groupId.Value);
        command.Parameters.AddWithValue($"supply-{groupId.Value:N}");
        command.Parameters.AddWithValue(accountId.Value);
        command.Parameters.AddWithValue($"account-{accountId.Value:N}");
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }
}
#pragma warning restore MA0051
