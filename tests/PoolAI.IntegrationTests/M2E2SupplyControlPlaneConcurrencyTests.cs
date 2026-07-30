#pragma warning disable MA0051 // The concurrency matrix keeps each lock order and terminal assertion explicit.
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class M2E2SupplyControlPlaneConcurrencyTests(
    PostgresRuntimeFixture fixture)
{
    private const string EnvelopeJson = """
        {
          "v": 1,
          "alg": "A256GCM+A256GCM-v1",
          "kid": "m2-e2-concurrency-k1",
          "wrapped_dek": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
          "wrap_nonce": "AAECAwQFBgcICQoL",
          "wrap_tag": "AAECAwQFBgcICQoLDA0ODw",
          "ciphertext": "AQ",
          "nonce": "AQIDBAUGBwgJCgsM",
          "tag": "AAECAwQFBgcICQoLDA0ODw"
        }
        """;

    private readonly PostgresRuntimeFixture _fixture =
        fixture ?? throw new ArgumentNullException(nameof(fixture));

    private NpgsqlDataSource ApiDataSource =>
        _fixture.ApiServices.GetRequiredService<NpgsqlDataSource>();

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ConcurrentBindingReplacementsSerializeWithoutDeadlockOrLostVersion()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        foreach (bool firstAccountWins in new[] { true, false })
        {
            BindingScenario scenario = await CreateBindingScenarioAsync(
                cancellationToken).ConfigureAwait(true);
            Guid winnerAccountId = firstAccountWins
                ? scenario.FirstAccountId
                : scenario.SecondAccountId;
            Guid loserAccountId = firstAccountWins
                ? scenario.SecondAccountId
                : scenario.FirstAccountId;

            ContendedMutations mutations = await ExecuteContendedAsync(
                (connection, transaction, token) => PatchBindingsAsync(
                    connection,
                    transaction,
                    scenario.GroupId,
                    scenario.ConfigurationVersion,
                    [winnerAccountId],
                    [true],
                    "winning binding replacement",
                    token),
                (connection, transaction, token) => PatchBindingsAsync(
                    connection,
                    transaction,
                    scenario.GroupId,
                    scenario.ConfigurationVersion,
                    [loserAccountId],
                    [true],
                    "stale concurrent binding replacement",
                    token),
                cancellationToken).ConfigureAwait(true);

            Assert.Equal("updated", mutations.Winner.Disposition);
            Assert.True(mutations.Winner.WasChanged);
            Assert.True(
                mutations.Winner.CurrentVersion > scenario.ConfigurationVersion);
            Assert.Equal("version_conflict", mutations.Loser.Disposition);
            Assert.False(mutations.Loser.WasChanged);
            Assert.Equal(
                mutations.Winner.CurrentVersion,
                mutations.Loser.CurrentVersion);

            BindingState state = await ReadBindingStateAsync(
                scenario.GroupId,
                cancellationToken).ConfigureAwait(true);
            Assert.Equal(mutations.Winner.CurrentVersion, state.ConfigurationVersion);
            Assert.Equal(2, state.Bindings.Count);
            Assert.True(state.Bindings[winnerAccountId]);
            Assert.False(state.Bindings[loserAccountId]);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task LifecycleHealthAndRetirementReferencesAreIndependent()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        foreach (BindingRaceKind bindingKind in Enum.GetValues<BindingRaceKind>())
        {
            await AssertAccountRetirementRaceAsync(
                bindingKind,
                retirementWins: true,
                cancellationToken).ConfigureAwait(true);
            await AssertAccountRetirementRaceAsync(
                bindingKind,
                retirementWins: false,
                cancellationToken).ConfigureAwait(true);
        }

        await AssertChannelRetirementRaceAsync(
            retirementWins: true,
            cancellationToken).ConfigureAwait(true);
        await AssertChannelRetirementRaceAsync(
            retirementWins: false,
            cancellationToken).ConfigureAwait(true);
    }

    private async ValueTask AssertAccountRetirementRaceAsync(
        BindingRaceKind bindingKind,
        bool retirementWins,
        CancellationToken cancellationToken)
    {
        Guid groupId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();
        await SeedGroupAsync(groupId, cancellationToken).ConfigureAwait(false);
        await CreateAccountAsync(accountId, cancellationToken).ConfigureAwait(false);

        bool startsWithDisabledBinding = bindingKind == BindingRaceKind.ReEnable;
        Mutation created = await CreateConfigurationAsync(
            groupId,
            channelId: null,
            startsWithDisabledBinding ? [accountId] : [],
            startsWithDisabledBinding ? [false] : [],
            cancellationToken).ConfigureAwait(false);
        Assert.Equal("created", created.Disposition);
        long initialConfigurationVersion = Assert.IsType<long>(
            created.CurrentVersion);

        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, ValueTask<Mutation>>
            retire = (connection, transaction, token) => RetireAccountAsync(
                connection,
                transaction,
                accountId,
                expectedVersion: 1,
                token);
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, ValueTask<Mutation>>
            bind = (connection, transaction, token) => PatchBindingsAsync(
                connection,
                transaction,
                groupId,
                initialConfigurationVersion,
                [accountId],
                [true],
                bindingKind == BindingRaceKind.Insert
                    ? "insert enabled binding"
                    : "re-enable existing binding",
                token);

        ContendedMutations mutations = retirementWins
            ? await ExecuteContendedAsync(
                retire,
                bind,
                cancellationToken).ConfigureAwait(false)
            : await ExecuteContendedAsync(
                bind,
                retire,
                cancellationToken).ConfigureAwait(false);

        AccountReferenceState state = await ReadAccountReferenceStateAsync(
            groupId,
            accountId,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal("unknown", state.HealthStatus);
        Assert.Null(state.LastHealthAt);

        if (retirementWins)
        {
            Assert.Equal("retired", mutations.Winner.Disposition);
            Assert.Equal(2, mutations.Winner.CurrentVersion);
            Assert.Equal("validation_failed", mutations.Loser.Disposition);
            Assert.Equal(
                initialConfigurationVersion,
                mutations.Loser.CurrentVersion);
            Assert.Equal("retired", state.AccountStatus);
            Assert.Equal(2, state.AccountVersion);
            Assert.Equal(initialConfigurationVersion, state.ConfigurationVersion);
            Assert.Equal(
                startsWithDisabledBinding ? false : null,
                state.BindingEnabled);
        }
        else
        {
            Assert.Equal("updated", mutations.Winner.Disposition);
            Assert.True(
                mutations.Winner.CurrentVersion > initialConfigurationVersion);
            Assert.Equal("account_in_use", mutations.Loser.Disposition);
            Assert.Equal(1, mutations.Loser.CurrentVersion);
            Assert.Equal("disabled", state.AccountStatus);
            Assert.Equal(1, state.AccountVersion);
            Assert.True(state.BindingEnabled);
            Assert.Equal(
                mutations.Winner.CurrentVersion,
                state.ConfigurationVersion);
        }
    }

    private async ValueTask AssertChannelRetirementRaceAsync(
        bool retirementWins,
        CancellationToken cancellationToken)
    {
        Guid groupId = Guid.NewGuid();
        Guid channelId = Guid.NewGuid();
        await SeedGroupAsync(groupId, cancellationToken).ConfigureAwait(false);
        await CreateChannelAsync(channelId, cancellationToken).ConfigureAwait(false);
        Mutation created = await CreateConfigurationAsync(
            groupId,
            channelId: null,
            [],
            [],
            cancellationToken).ConfigureAwait(false);
        Assert.Equal("created", created.Disposition);
        long initialConfigurationVersion = Assert.IsType<long>(
            created.CurrentVersion);

        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, ValueTask<Mutation>>
            retire = (connection, transaction, token) => RetireChannelAsync(
                connection,
                transaction,
                channelId,
                expectedVersion: 1,
                token);
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, ValueTask<Mutation>>
            reference = (connection, transaction, token) => PatchChannelAsync(
                connection,
                transaction,
                groupId,
                initialConfigurationVersion,
                channelId,
                token);

        ContendedMutations mutations = retirementWins
            ? await ExecuteContendedAsync(
                retire,
                reference,
                cancellationToken).ConfigureAwait(false)
            : await ExecuteContendedAsync(
                reference,
                retire,
                cancellationToken).ConfigureAwait(false);

        ChannelReferenceState state = await ReadChannelReferenceStateAsync(
            groupId,
            channelId,
            cancellationToken).ConfigureAwait(false);
        if (retirementWins)
        {
            Assert.Equal("retired", mutations.Winner.Disposition);
            Assert.Equal(2, mutations.Winner.CurrentVersion);
            Assert.Equal("validation_failed", mutations.Loser.Disposition);
            Assert.Equal(
                initialConfigurationVersion,
                mutations.Loser.CurrentVersion);
            Assert.Equal("retired", state.ChannelStatus);
            Assert.Equal(2, state.ChannelVersion);
            Assert.Null(state.ConfiguredChannelId);
            Assert.Equal(initialConfigurationVersion, state.ConfigurationVersion);
        }
        else
        {
            Assert.Equal("updated", mutations.Winner.Disposition);
            Assert.True(
                mutations.Winner.CurrentVersion > initialConfigurationVersion);
            Assert.Equal("channel_in_use", mutations.Loser.Disposition);
            Assert.Equal(1, mutations.Loser.CurrentVersion);
            Assert.Equal("disabled", state.ChannelStatus);
            Assert.Equal(1, state.ChannelVersion);
            Assert.Equal(channelId, state.ConfiguredChannelId);
            Assert.Equal(
                mutations.Winner.CurrentVersion,
                state.ConfigurationVersion);
        }
    }

    private async ValueTask<BindingScenario> CreateBindingScenarioAsync(
        CancellationToken cancellationToken)
    {
        Guid groupId = Guid.NewGuid();
        Guid firstAccountId = Guid.NewGuid();
        Guid secondAccountId = Guid.NewGuid();
        await SeedGroupAsync(groupId, cancellationToken).ConfigureAwait(false);
        await CreateAccountAsync(firstAccountId, cancellationToken)
            .ConfigureAwait(false);
        await CreateAccountAsync(secondAccountId, cancellationToken)
            .ConfigureAwait(false);
        Mutation created = await CreateConfigurationAsync(
            groupId,
            channelId: null,
            [firstAccountId, secondAccountId],
            [true, true],
            cancellationToken).ConfigureAwait(false);
        Assert.Equal("created", created.Disposition);
        return new BindingScenario(
            groupId,
            firstAccountId,
            secondAccountId,
            Assert.IsType<long>(created.CurrentVersion));
    }

    private async ValueTask<ContendedMutations> ExecuteContendedAsync(
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, ValueTask<Mutation>>
            winningOperation,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, ValueTask<Mutation>>
            blockedOperation,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection winnerConnection = await ApiDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable winnerConnectionLease =
            winnerConnection.ConfigureAwait(false);
        NpgsqlConnection blockedConnection = await ApiDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable blockedConnectionLease =
            blockedConnection.ConfigureAwait(false);
        NpgsqlTransaction winnerTransaction = await winnerConnection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable winnerTransactionLease =
            winnerTransaction.ConfigureAwait(false);
        NpgsqlTransaction blockedTransaction = await blockedConnection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable blockedTransactionLease =
            blockedTransaction.ConfigureAwait(false);

        int winnerBackendPid = await AssertApiRoleAndGetBackendPidAsync(
            winnerConnection,
            winnerTransaction,
            cancellationToken).ConfigureAwait(false);
        int blockedBackendPid = await AssertApiRoleAndGetBackendPidAsync(
            blockedConnection,
            blockedTransaction,
            cancellationToken).ConfigureAwait(false);

        Mutation winner = await winningOperation(
            winnerConnection,
            winnerTransaction,
            cancellationToken).ConfigureAwait(false);
        Task<Mutation> blockedTask = blockedOperation(
            blockedConnection,
            blockedTransaction,
            cancellationToken).AsTask();
        await WaitForLockAsync(
            blockedBackendPid,
            winnerBackendPid,
            blockedTask,
            cancellationToken).ConfigureAwait(false);

        await winnerTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        Mutation blocked = await blockedTask.ConfigureAwait(false);
        await blockedTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ContendedMutations(winner, blocked);
    }

    private async ValueTask SeedGroupAsync(
        Guid groupId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            INSERT INTO public.groups (id, name, status)
            VALUES ($1, $2, 'disabled');
            """);
        command.Parameters.AddWithValue(groupId);
        command.Parameters.AddWithValue($"M2-E2 concurrency {groupId:N}");
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false));
    }

    private async ValueTask CreateAccountAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = ApiDataSource.CreateCommand("""
            SELECT
                disposition,
                disposition = 'created' AS was_changed,
                current_version
            FROM public.poolai_supply_create_account(
                $1,
                'openai',
                $2,
                'https://example.test/v1',
                $3::jsonb,
                'm2-e2-test',
                NULL,
                4,
                0,
                100
            );
            """);
        command.Parameters.AddWithValue(accountId);
        command.Parameters.AddWithValue($"M2-E2 Account {accountId:N}");
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = EnvelopeJson,
        });
        Mutation mutation = await ReadMutationAsync(command, cancellationToken)
            .ConfigureAwait(false);
        Assert.Equal(new Mutation("created", true, 1), mutation);
    }

    private async ValueTask CreateChannelAsync(
        Guid channelId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = ApiDataSource.CreateCommand("""
            SELECT disposition, was_changed, current_version
            FROM public.poolai_supply_create_channel(
                $1,
                'openai',
                $2,
                '{"client-model":"upstream-model"}'::jsonb,
                '{
                  "responses": true,
                  "chat_completions": true,
                  "function_tools": true,
                  "streaming": true
                }'::jsonb
            );
            """);
        command.Parameters.AddWithValue(channelId);
        command.Parameters.AddWithValue($"M2-E2 Channel {channelId:N}");
        Mutation mutation = await ReadMutationAsync(command, cancellationToken)
            .ConfigureAwait(false);
        Assert.Equal(new Mutation("created", true, 1), mutation);
    }

    private async ValueTask<Mutation> CreateConfigurationAsync(
        Guid groupId,
        Guid? channelId,
        Guid[] accountIds,
        bool[] enabled,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = ApiDataSource.CreateCommand("""
            SELECT disposition, was_changed, current_version
            FROM public.poolai_supply_create_group_configuration(
                $1, $2, $3, $4, $5, $6
            );
            """);
        command.Parameters.AddWithValue(groupId);
        AddNullableUuid(command.Parameters, channelId);
        AddBindingArrays(command.Parameters, accountIds, enabled);
        return await ReadMutationAsync(command, cancellationToken)
            .ConfigureAwait(false);
    }

    private static ValueTask<Mutation> PatchBindingsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid groupId,
        long expectedVersion,
        Guid[] accountIds,
        bool[] enabled,
        string reason,
        CancellationToken cancellationToken)
    {
        NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT disposition, was_changed, current_version
            FROM public.poolai_supply_patch_group_configuration(
                $1, $2, false, NULL::uuid, true, $3, $4, $5, $6, $7
            );
            """;
        command.Parameters.AddWithValue(groupId);
        command.Parameters.AddWithValue(expectedVersion);
        AddBindingArrays(command.Parameters, accountIds, enabled);
        command.Parameters.AddWithValue(reason);
        return ReadAndDisposeMutationAsync(command, cancellationToken);
    }

    private static ValueTask<Mutation> PatchChannelAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid groupId,
        long expectedVersion,
        Guid channelId,
        CancellationToken cancellationToken)
    {
        NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT disposition, was_changed, current_version
            FROM public.poolai_supply_patch_group_configuration(
                $1,
                $2,
                true,
                $3,
                false,
                NULL::uuid[],
                NULL::integer[],
                NULL::integer[],
                NULL::boolean[],
                'reference concurrent Channel'
            );
            """;
        command.Parameters.AddWithValue(groupId);
        command.Parameters.AddWithValue(expectedVersion);
        command.Parameters.AddWithValue(channelId);
        return ReadAndDisposeMutationAsync(command, cancellationToken);
    }

    private static ValueTask<Mutation> RetireAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT disposition, was_changed, current_version
            FROM public.poolai_supply_retire_account(
                $1, $2, 'concurrent Account retirement'
            );
            """;
        command.Parameters.AddWithValue(accountId);
        command.Parameters.AddWithValue(expectedVersion);
        return ReadAndDisposeMutationAsync(command, cancellationToken);
    }

    private static ValueTask<Mutation> RetireChannelAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid channelId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT disposition, was_changed, current_version
            FROM public.poolai_supply_retire_channel(
                $1, $2, 'concurrent Channel retirement'
            );
            """;
        command.Parameters.AddWithValue(channelId);
        command.Parameters.AddWithValue(expectedVersion);
        return ReadAndDisposeMutationAsync(command, cancellationToken);
    }

    private async ValueTask<AccountReferenceState> ReadAccountReferenceStateAsync(
        Guid groupId,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT
                account.status,
                account.version,
                account.last_health_status,
                account.last_health_at,
                binding.is_enabled,
                configuration.version
            FROM public.accounts AS account
            CROSS JOIN public.group_supply_configurations AS configuration
            LEFT JOIN public.group_accounts AS binding
              ON binding.group_id = configuration.group_id
             AND binding.account_id = account.id
            WHERE account.id = $1
              AND configuration.group_id = $2;
            """);
        command.Parameters.AddWithValue(accountId);
        command.Parameters.AddWithValue(groupId);
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        AccountReferenceState state = new(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.IsDBNull(3)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(3),
            reader.IsDBNull(4) ? null : reader.GetBoolean(4),
            reader.GetInt64(5));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return state;
    }

    private async ValueTask<ChannelReferenceState> ReadChannelReferenceStateAsync(
        Guid groupId,
        Guid channelId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT
                channel.status,
                channel.version,
                configuration.channel_id,
                configuration.version
            FROM public.channels AS channel
            CROSS JOIN public.group_supply_configurations AS configuration
            WHERE channel.id = $1
              AND configuration.group_id = $2;
            """);
        command.Parameters.AddWithValue(channelId);
        command.Parameters.AddWithValue(groupId);
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        ChannelReferenceState state = new(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.GetInt64(3));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return state;
    }

    private async ValueTask<BindingState> ReadBindingStateAsync(
        Guid groupId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT
                configuration.version,
                binding.account_id,
                binding.is_enabled
            FROM public.group_supply_configurations AS configuration
            JOIN public.group_accounts AS binding
              ON binding.group_id = configuration.group_id
            WHERE configuration.group_id = $1
            ORDER BY binding.account_id;
            """);
        command.Parameters.AddWithValue(groupId);
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        long? configurationVersion = null;
        Dictionary<Guid, bool> bindings = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            configurationVersion ??= reader.GetInt64(0);
            bindings.Add(reader.GetGuid(1), reader.GetBoolean(2));
        }

        return new BindingState(
            Assert.IsType<long>(configurationVersion),
            bindings);
    }

    private async ValueTask WaitForLockAsync(
        int backendPid,
        int expectedBlockingPid,
        Task blockedOperation,
        CancellationToken cancellationToken)
    {
        for (int probe = 0; probe < 250; probe++)
        {
            if (blockedOperation.IsCompleted)
            {
                throw new InvalidOperationException(
                    "The competing Supply mutation completed before waiting on the expected row lock.");
            }

            using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
                SELECT activity.wait_event_type = 'Lock'
                   AND $2 = ANY (pg_catalog.pg_blocking_pids(activity.pid))
                FROM pg_catalog.pg_stat_activity AS activity
                WHERE activity.pid = $1;
                """);
            command.Parameters.AddWithValue(backendPid);
            command.Parameters.AddWithValue(expectedBlockingPid);
            object? waiting = await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            if (waiting is true)
            {
                return;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(20),
                cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            "The competing Supply mutation did not wait on the expected row lock.");
    }

    private static async ValueTask<int> AssertApiRoleAndGetBackendPidAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT pg_catalog.pg_backend_pid(), current_user;
            """;
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        int backendPid = reader.GetInt32(0);
        Assert.Equal("poolai_api", reader.GetString(1));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return backendPid;
    }

    private static void AddNullableUuid(
        NpgsqlParameterCollection parameters,
        Guid? value) => parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Uuid,
            Value = value is Guid identifier ? identifier : DBNull.Value,
        });

    private static void AddBindingArrays(
        NpgsqlParameterCollection parameters,
        Guid[] accountIds,
        bool[] enabled)
    {
        Assert.Equal(accountIds.Length, enabled.Length);
        parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid,
            Value = accountIds,
        });
        parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer,
            Value = Enumerable.Repeat(0, accountIds.Length).ToArray(),
        });
        parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer,
            Value = Enumerable.Repeat(100, accountIds.Length).ToArray(),
        });
        parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Boolean,
            Value = enabled,
        });
    }

    private static async ValueTask<Mutation> ReadAndDisposeMutationAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        using (command)
        {
            return await ReadMutationAsync(command, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask<Mutation> ReadMutationAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        Mutation mutation = new(
            reader.GetString(0),
            reader.GetBoolean(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return mutation;
    }

    private enum BindingRaceKind
    {
        Insert,
        ReEnable,
    }

    private sealed record Mutation(
        string Disposition,
        bool WasChanged,
        long? CurrentVersion);

    private sealed record ContendedMutations(
        Mutation Winner,
        Mutation Loser);

    private sealed record BindingScenario(
        Guid GroupId,
        Guid FirstAccountId,
        Guid SecondAccountId,
        long ConfigurationVersion);

    private sealed record BindingState(
        long ConfigurationVersion,
        IReadOnlyDictionary<Guid, bool> Bindings);

    private sealed record AccountReferenceState(
        string AccountStatus,
        long AccountVersion,
        string HealthStatus,
        DateTimeOffset? LastHealthAt,
        bool? BindingEnabled,
        long ConfigurationVersion);

    private sealed record ChannelReferenceState(
        string ChannelStatus,
        long ChannelVersion,
        Guid? ConfiguredChannelId,
        long ConfigurationVersion);
}
#pragma warning restore MA0051
