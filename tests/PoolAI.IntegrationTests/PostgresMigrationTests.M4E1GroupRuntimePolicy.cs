#pragma warning disable MA0051 // The v18 failure and v19 upgrade proof stay visible as one contract scenario.
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using PoolAI.Database.Migrations;
using Testcontainers.PostgreSql;

namespace PoolAI.IntegrationTests;

public sealed partial class PostgresMigrationTests
{
    private const int M4E1RuntimePolicyHardTimeoutMilliseconds = 5 * 60 * 1000;

    [Fact(Timeout = M4E1RuntimePolicyHardTimeoutMilliseconds)]
    [Trait("Category", "PostgreSQL")]
    public async Task M4E1RuntimePolicyUpgradeIsAtomicCanonicalAndBackwardCompatible()
    {
        // Governing contracts: docs/database/README.md section 2 and ADR 0015.
        // The existing Group-owned jsonb becomes one exact versioned policy;
        // unknown old data must stop the forward migration without partial ETag
        // drift, while both 0007 ABIs remain callable after schema 19.
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        PostgreSqlContainer container = new PostgreSqlBuilder(ReadPostgresImage())
            .WithDatabase("poolai")
            .WithUsername("postgres")
            .WithPassword(password)
            .Build();
        await using ConfiguredAsyncDisposable containerLease = container.ConfigureAwait(true);

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await container.StartAsync(cancellationToken).ConfigureAwait(true);
        string connectionString = container.GetConnectionString();
        await ProvisionRuntimeRolesAsync(connectionString, cancellationToken).ConfigureAwait(true);
        MigrationCatalog catalog = await MigrationCatalog
            .LoadAsync(cancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(19, catalog.Assets.Count);
        await ApplyM4E1MigrationPrefixAsync(catalog, connectionString, cancellationToken)
            .ConfigureAwait(true);

        using NpgsqlDataSource administrator = NpgsqlDataSource.Create(connectionString);
        using NpgsqlConnection connection = await administrator
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(true);
        await SeedM3E1ActorAsync(connection, cancellationToken).ConfigureAwait(true);
        await SetM1E4ApiRoleAsync(connection, cancellationToken).ConfigureAwait(true);
        await CreateM3E1GroupAsync(
            connection,
            Guid.Parse("01900000-0000-7000-8000-00000000f100"),
            Guid.Parse("01900000-0000-7000-8000-00000000f101"),
            Guid.Parse("01900000-0000-7000-8000-00000000f102"),
            Guid.Parse("01900000-0000-7000-8000-00000000f103"),
            "M4-E1 empty policy",
            "m4-e1-empty-policy",
            cancellationToken).ConfigureAwait(true);
        await CreateM3E1GroupAsync(
            connection,
            Guid.Parse("01900000-0000-7000-8000-00000000f110"),
            Guid.Parse("01900000-0000-7000-8000-00000000f111"),
            Guid.Parse("01900000-0000-7000-8000-00000000f112"),
            Guid.Parse("01900000-0000-7000-8000-00000000f113"),
            "M4-E1 canonical policy",
            "m4-e1-canonical-policy",
            cancellationToken).ConfigureAwait(true);
        await CreateM3E1GroupAsync(
            connection,
            Guid.Parse("01900000-0000-7000-8000-00000000f120"),
            Guid.Parse("01900000-0000-7000-8000-00000000f121"),
            Guid.Parse("01900000-0000-7000-8000-00000000f122"),
            Guid.Parse("01900000-0000-7000-8000-00000000f123"),
            "M4-E1 invalid policy",
            "m4-e1-invalid-policy",
            cancellationToken).ConfigureAwait(true);
        await ResetM4E1RoleAsync(connection, cancellationToken).ConfigureAwait(true);

        using (NpgsqlCommand seed = connection.CreateCommand())
        {
            seed.CommandText = """
                UPDATE public.groups
                SET runtime_policy = '{"schema_version":1,"requests_per_minute":7777}'::jsonb
                WHERE id = '01900000-0000-7000-8000-00000000f110';
                """;
            await seed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true);
        }

        M4E1PolicyEvidence emptyAt18 = await ReadM4E1PolicyEvidenceAsync(
            connection,
            Guid.Parse("01900000-0000-7000-8000-00000000f100"),
            cancellationToken).ConfigureAwait(true);
        M4E1PolicyEvidence canonicalAt18 = await ReadM4E1PolicyEvidenceAsync(
            connection,
            Guid.Parse("01900000-0000-7000-8000-00000000f110"),
            cancellationToken).ConfigureAwait(true);
        M4E1PolicyEvidence invalidAt18 = await ReadM4E1PolicyEvidenceAsync(
            connection,
            Guid.Parse("01900000-0000-7000-8000-00000000f120"),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal("{}", emptyAt18.RuntimePolicy);
        Assert.Equal(1, emptyAt18.Version);
        Assert.Equal(
            "{\"schema_version\": 1, \"requests_per_minute\": 7777}",
            canonicalAt18.RuntimePolicy);
        Assert.Equal("{}", invalidAt18.RuntimePolicy);

        PostgresException canonicalLooking = await Assert.ThrowsAsync<PostgresException>(() =>
            new PostgresMigrator(catalog).ApplyAsync(
                connectionString,
                "PoolAI.IntegrationTests.m4-e1-canonical-looking-old-data",
                cancellationToken).AsTask()).ConfigureAwait(true);
        Assert.Equal(PostgresErrorCodes.CheckViolation, canonicalLooking.SqlState);
        Assert.Equal(
            "poolai_m4_e1_existing_runtime_policy_not_empty",
            canonicalLooking.MessageText);
        Assert.Null(canonicalLooking.ConstraintName);
        await AssertM4E1FailedUpgradeRolledBackAsync(
            connection,
            emptyAt18,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            canonicalAt18,
            await ReadM4E1PolicyEvidenceAsync(
                connection,
                Guid.Parse("01900000-0000-7000-8000-00000000f110"),
                cancellationToken).ConfigureAwait(true));

        using (NpgsqlCommand extra = connection.CreateCommand())
        {
            extra.CommandText = """
                UPDATE public.groups
                SET runtime_policy = '{}'::jsonb
                WHERE id = '01900000-0000-7000-8000-00000000f110';
                UPDATE public.groups
                SET runtime_policy = '{"schema_version":1,"requests_per_minute":8000,"burst":1}'::jsonb
                WHERE id = '01900000-0000-7000-8000-00000000f120';
            """;
            await extra.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true);
        }
        M4E1PolicyEvidence extraAt18 = await ReadM4E1PolicyEvidenceAsync(
            connection,
            Guid.Parse("01900000-0000-7000-8000-00000000f120"),
            cancellationToken).ConfigureAwait(true);

        PostgresException extraKey = await Assert.ThrowsAsync<PostgresException>(() =>
            new PostgresMigrator(catalog).ApplyAsync(
                connectionString,
                "PoolAI.IntegrationTests.m4-e1-extra-key",
                cancellationToken).AsTask()).ConfigureAwait(true);
        Assert.Equal(PostgresErrorCodes.CheckViolation, extraKey.SqlState);
        Assert.Equal(
            "poolai_m4_e1_existing_runtime_policy_not_empty",
            extraKey.MessageText);
        Assert.Null(extraKey.ConstraintName);
        await AssertM4E1FailedUpgradeRolledBackAsync(
            connection,
            emptyAt18,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            extraAt18,
            await ReadM4E1PolicyEvidenceAsync(
                connection,
                Guid.Parse("01900000-0000-7000-8000-00000000f120"),
                cancellationToken).ConfigureAwait(true));

        using (NpgsqlCommand malformed = connection.CreateCommand())
        {
            malformed.CommandText = """
                UPDATE public.groups
                SET runtime_policy = '{"schema_version":1,"requests_per_minute":"8000"}'::jsonb
                WHERE id = '01900000-0000-7000-8000-00000000f120';
            """;
            await malformed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true);
        }
        M4E1PolicyEvidence malformedAt18 = await ReadM4E1PolicyEvidenceAsync(
            connection,
            Guid.Parse("01900000-0000-7000-8000-00000000f120"),
            cancellationToken).ConfigureAwait(true);

        PostgresException wrongType = await Assert.ThrowsAsync<PostgresException>(() =>
            new PostgresMigrator(catalog).ApplyAsync(
                connectionString,
                "PoolAI.IntegrationTests.m4-e1-wrong-type",
                cancellationToken).AsTask()).ConfigureAwait(true);
        Assert.Equal(PostgresErrorCodes.CheckViolation, wrongType.SqlState);
        Assert.Equal(
            "poolai_m4_e1_existing_runtime_policy_not_empty",
            wrongType.MessageText);
        Assert.Null(wrongType.ConstraintName);
        await AssertM4E1FailedUpgradeRolledBackAsync(
            connection,
            emptyAt18,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            malformedAt18,
            await ReadM4E1PolicyEvidenceAsync(
                connection,
                Guid.Parse("01900000-0000-7000-8000-00000000f120"),
                cancellationToken).ConfigureAwait(true));

        using (NpgsqlCommand repairAndOverflow = connection.CreateCommand())
        {
            repairAndOverflow.CommandText = """
                UPDATE public.groups
                SET runtime_policy = '{}'::jsonb
                WHERE id = '01900000-0000-7000-8000-00000000f120';
                UPDATE public.groups
                SET version = 9223372036854775807
                WHERE id = '01900000-0000-7000-8000-00000000f100';
                """;
            await repairAndOverflow.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true);
        }

        PostgresException versionOverflow = await Assert.ThrowsAsync<PostgresException>(() =>
            new PostgresMigrator(catalog).ApplyAsync(
                connectionString,
                "PoolAI.IntegrationTests.m4-e1-version-overflow",
                cancellationToken).AsTask()).ConfigureAwait(true);
        Assert.Equal(PostgresErrorCodes.NumericValueOutOfRange, versionOverflow.SqlState);
        Assert.Equal(18, await ReadM4E1TopMigrationAsync(connection, cancellationToken)
            .ConfigureAwait(true));
        Assert.Equal(
            "{}",
            (await ReadM4E1PolicyEvidenceAsync(
                connection,
                Guid.Parse("01900000-0000-7000-8000-00000000f100"),
                cancellationToken).ConfigureAwait(true)).RuntimePolicy);
        Assert.Equal(
            "'{}'::jsonb",
            await ReadM4E1RuntimePolicyDefaultAsync(connection, cancellationToken)
                .ConfigureAwait(true));

        using (NpgsqlCommand restoreVersion = connection.CreateCommand())
        {
            restoreVersion.CommandText = """
                UPDATE public.groups
                SET version = 1,
                    updated_at = $1
                WHERE id = '01900000-0000-7000-8000-00000000f100';
                """;
            restoreVersion.Parameters.AddWithValue(emptyAt18.UpdatedAt);
            await restoreVersion.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true);
        }

        await new PostgresMigrator(catalog).ApplyAsync(
            connectionString,
            "PoolAI.IntegrationTests.m4-e1-success",
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(19, await ReadM4E1TopMigrationAsync(connection, cancellationToken)
            .ConfigureAwait(true));
        Assert.True(await ReadM4E1ConstraintValidatedAsync(connection, cancellationToken)
            .ConfigureAwait(true));
        Assert.Equal(
            "'{\"schema_version\": 1, \"requests_per_minute\": 6000}'::jsonb",
            await ReadM4E1RuntimePolicyDefaultAsync(connection, cancellationToken)
                .ConfigureAwait(true));

        M4E1PolicyEvidence backfilled = await ReadM4E1PolicyEvidenceAsync(
            connection,
            Guid.Parse("01900000-0000-7000-8000-00000000f100"),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            "{\"schema_version\": 1, \"requests_per_minute\": 6000}",
            backfilled.RuntimePolicy);
        Assert.Equal(2, backfilled.Version);
        Assert.True(backfilled.UpdatedAt > emptyAt18.UpdatedAt);
        M4E1PolicyEvidence canonicalBackfilled = await ReadM4E1PolicyEvidenceAsync(
            connection,
            Guid.Parse("01900000-0000-7000-8000-00000000f110"),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            "{\"schema_version\": 1, \"requests_per_minute\": 6000}",
            canonicalBackfilled.RuntimePolicy);
        Assert.Equal(2, canonicalBackfilled.Version);
        Assert.True(canonicalBackfilled.UpdatedAt > canonicalAt18.UpdatedAt);
        M4E1PolicyEvidence invalidBackfilled = await ReadM4E1PolicyEvidenceAsync(
            connection,
            Guid.Parse("01900000-0000-7000-8000-00000000f120"),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            "{\"schema_version\": 1, \"requests_per_minute\": 6000}",
            invalidBackfilled.RuntimePolicy);
        Assert.Equal(2, invalidBackfilled.Version);
        Assert.True(invalidBackfilled.UpdatedAt > invalidAt18.UpdatedAt);

        await AssertM4E1ConstraintRejectsNonCanonicalPoliciesAsync(
            connection,
            cancellationToken).ConfigureAwait(true);
        await AssertM4E1FunctionSecurityAsync(
            connection,
            connectionString,
            cancellationToken)
            .ConfigureAwait(true);
        await AssertM4E1OldAndV2AbisAsync(connection, cancellationToken)
            .ConfigureAwait(true);
        await AssertM4E1V2FamilyCConcurrencyAsync(
            administrator,
            connection,
            cancellationToken).ConfigureAwait(true);
    }

    private static async ValueTask ApplyM4E1MigrationPrefixAsync(
        MigrationCatalog catalog,
        string connectionString,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using NpgsqlConnection connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (MigrationAsset asset in catalog.Assets.Take(18))
        {
            using (NpgsqlCommand migration = new(asset.Sql, connection, transaction)
            {
                CommandTimeout = 0,
            })
            {
                await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using NpgsqlCommand history = new(
                """
                INSERT INTO public.poolai_schema_migrations (
                    version, name, checksum_sha256, applied_by
                ) VALUES ($1, $2, $3, 'PoolAI.IntegrationTests.m4-e1-prefix');
                """,
                connection,
                transaction);
            history.Parameters.AddWithValue(asset.Version);
            history.Parameters.AddWithValue(asset.Name);
            history.Parameters.AddWithValue(asset.ChecksumSha256);
            await history.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AssertM4E1FailedUpgradeRolledBackAsync(
        NpgsqlConnection connection,
        M4E1PolicyEvidence expectedEmpty,
        CancellationToken cancellationToken)
    {
        Assert.Equal(18, await ReadM4E1TopMigrationAsync(connection, cancellationToken)
            .ConfigureAwait(false));
        Assert.Equal(
            expectedEmpty,
            await ReadM4E1PolicyEvidenceAsync(
                connection,
                Guid.Parse("01900000-0000-7000-8000-00000000f100"),
                cancellationToken).ConfigureAwait(false));
        using NpgsqlCommand constraint = connection.CreateCommand();
        constraint.CommandText = """
            SELECT count(*)
            FROM pg_catalog.pg_constraint AS constraint_definition
            WHERE constraint_definition.conname = 'ck_groups_runtime_policy_m4_e1';
            """;
        Assert.Equal(
            0L,
            Assert.IsType<long>(await constraint.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));
        Assert.Equal(
            "'{}'::jsonb",
            await ReadM4E1RuntimePolicyDefaultAsync(connection, cancellationToken)
                .ConfigureAwait(false));
    }

    private static async ValueTask AssertM4E1ConstraintRejectsNonCanonicalPoliciesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        (string Policy, string Constraint)[] invalidPolicies =
        [
            ("{\"schema_version\":1}", "ck_groups_runtime_policy_m4_e1"),
            ("{\"requests_per_minute\":6000}", "ck_groups_runtime_policy_m4_e1"),
            ("{\"schema_version\":2,\"requests_per_minute\":6000}", "ck_groups_runtime_policy_m4_e1"),
            ("{\"schema_version\":\"1\",\"requests_per_minute\":6000}", "ck_groups_runtime_policy_m4_e1"),
            ("{\"schema_version\":1,\"requests_per_minute\":\"6000\"}", "ck_groups_runtime_policy_m4_e1"),
            ("{\"schema_version\":1,\"requests_per_minute\":0}", "ck_groups_runtime_policy_m4_e1"),
            ("{\"schema_version\":1,\"requests_per_minute\":1000001}", "ck_groups_runtime_policy_m4_e1"),
            ("{\"schema_version\":1,\"requests_per_minute\":1.5}", "ck_groups_runtime_policy_m4_e1"),
            ("{\"schema_version\":1,\"requests_per_minute\":6000,\"burst\":1}", "ck_groups_runtime_policy_m4_e1"),
            ("[]", "ck_groups_runtime_policy"),
            ("null", "ck_groups_runtime_policy"),
        ];
        foreach ((string invalidPolicy, string expectedConstraint) in invalidPolicies)
        {
            using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE public.groups
                SET runtime_policy = $1::jsonb
                WHERE id = '01900000-0000-7000-8000-00000000f110';
                """;
            command.Parameters.AddWithValue(invalidPolicy);
            PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
                () => command.ExecuteNonQueryAsync(cancellationToken)).ConfigureAwait(false);
            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
            Assert.Equal(expectedConstraint, exception.ConstraintName);
        }
    }

    private static async ValueTask AssertM4E1FunctionSecurityAsync(
        NpgsqlConnection connection,
        string administratorConnectionString,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT string_agg(
                procedure.proname || ':'
                || owner.rolname || ':'
                || owner.rolcanlogin::text || ':'
                || procedure.prosecdef::text || ':'
                || (procedure.proconfig @> ARRAY[
                    'search_path=pg_catalog, public, pg_temp'
                ]::text[])::text || ':'
                || pg_catalog.has_function_privilege(
                    'poolai_api', procedure.oid, 'EXECUTE')::text || ':'
                || pg_catalog.has_function_privilege(
                    'poolai_worker', procedure.oid, 'EXECUTE')::text || ':'
                || EXISTS (
                    SELECT 1
                    FROM pg_catalog.aclexplode(COALESCE(
                        procedure.proacl,
                        pg_catalog.acldefault('f', procedure.proowner))) AS privilege
                    WHERE privilege.privilege_type = 'EXECUTE'
                      AND privilege.grantee = 0
                )::text,
                ',' ORDER BY procedure.proname)
            FROM pg_catalog.pg_proc AS procedure
            JOIN pg_catalog.pg_namespace AS namespace
              ON namespace.oid = procedure.pronamespace
            JOIN pg_catalog.pg_roles AS owner
              ON owner.oid = procedure.proowner
            WHERE namespace.nspname = 'public'
              AND procedure.proname IN (
                  'poolai_group_create_v2',
                  'poolai_group_update_v2'
              );
            """;
        Assert.Equal(
            "poolai_group_create_v2:poolai_runtime_owner:false:true:true:true:false:false,"
                + "poolai_group_update_v2:poolai_runtime_owner:false:true:true:true:false:false",
            Assert.IsType<string>(await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));

        await AssertPermissionDeniedAsync(
            administratorConnectionString,
            """
            SET ROLE poolai_worker;
            SELECT * FROM public.poolai_group_create_v2(
                '01900000-0000-7000-8000-00000000f1e0', 'forbidden', NULL, 6000,
                '01900000-0000-7000-8000-00000000f1e1', 200,
                '01900000-0000-7000-8000-00000000d000',
                '01900000-0000-7000-8000-00000000f1e2',
                '01900000-0000-7000-8000-00000000f1e3',
                'm4-e1-worker-forbidden', 'forbidden');
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AssertM4E1OldAndV2AbisAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await SetM1E4ApiRoleAsync(connection, cancellationToken).ConfigureAwait(false);
        await CreateM3E1GroupAsync(
            connection,
            Guid.Parse("01900000-0000-7000-8000-00000000f130"),
            Guid.Parse("01900000-0000-7000-8000-00000000f131"),
            Guid.Parse("01900000-0000-7000-8000-00000000f132"),
            Guid.Parse("01900000-0000-7000-8000-00000000f133"),
            "M4-E1 old ABI",
            "m4-e1-old-abi",
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(
            6000,
            await ReadM4E1RequestsPerMinuteAsync(
                connection,
                Guid.Parse("01900000-0000-7000-8000-00000000f130"),
                cancellationToken).ConfigureAwait(false));

        await AssertM4E1CreateV2Async(
            connection,
            Guid.Parse("01900000-0000-7000-8000-00000000f140"),
            Guid.Parse("01900000-0000-7000-8000-00000000f141"),
            Guid.Parse("01900000-0000-7000-8000-00000000f142"),
            Guid.Parse("01900000-0000-7000-8000-00000000f143"),
            "M4-E1 v2 default",
            null,
            "m4-e1-v2-default",
            "created",
            cancellationToken).ConfigureAwait(false);
        await AssertM4E1CreateV2Async(
            connection,
            Guid.Parse("01900000-0000-7000-8000-00000000f150"),
            Guid.Parse("01900000-0000-7000-8000-00000000f151"),
            Guid.Parse("01900000-0000-7000-8000-00000000f152"),
            Guid.Parse("01900000-0000-7000-8000-00000000f153"),
            "M4-E1 v2 minimum",
            1,
            "m4-e1-v2-minimum",
            "created",
            cancellationToken).ConfigureAwait(false);
        await AssertM4E1CreateV2Async(
            connection,
            Guid.Parse("01900000-0000-7000-8000-00000000f160"),
            Guid.Parse("01900000-0000-7000-8000-00000000f161"),
            Guid.Parse("01900000-0000-7000-8000-00000000f162"),
            Guid.Parse("01900000-0000-7000-8000-00000000f163"),
            "M4-E1 v2 maximum",
            1_000_000,
            "m4-e1-v2-maximum",
            "created",
            cancellationToken).ConfigureAwait(false);
        await AssertM4E1CreateV2Async(
            connection,
            Guid.Parse("01900000-0000-7000-8000-00000000f170"),
            Guid.Parse("01900000-0000-7000-8000-00000000f171"),
            Guid.Parse("01900000-0000-7000-8000-00000000f172"),
            Guid.Parse("01900000-0000-7000-8000-00000000f173"),
            "M4-E1 v2 invalid zero",
            0,
            "m4-e1-v2-invalid-zero",
            "validation_failed",
            cancellationToken).ConfigureAwait(false);
        await AssertM4E1CreateV2Async(
            connection,
            Guid.Parse("01900000-0000-7000-8000-00000000f180"),
            Guid.Parse("01900000-0000-7000-8000-00000000f181"),
            Guid.Parse("01900000-0000-7000-8000-00000000f182"),
            Guid.Parse("01900000-0000-7000-8000-00000000f183"),
            "M4-E1 v2 invalid maximum",
            1_000_001,
            "m4-e1-v2-invalid-maximum",
            "validation_failed",
            cancellationToken).ConfigureAwait(false);

        M4E1PolicyEvidence beforeUpdate = await ReadM4E1PolicyEvidenceAsync(
            connection,
            Guid.Parse("01900000-0000-7000-8000-00000000f140"),
            cancellationToken).ConfigureAwait(false);
        M4E1MutationResult missingReason = await ExecuteM4E1UpdateV2Async(
            connection,
            1,
            false,
            null,
            false,
            null,
            true,
            9000,
            null,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal("validation_failed", missingReason.Disposition);
        Assert.False(missingReason.WasChanged);
        Assert.Null(missingReason.BeforeState);
        Assert.Equal(
            beforeUpdate,
            await ReadM4E1PolicyEvidenceAsync(
                connection,
                Guid.Parse("01900000-0000-7000-8000-00000000f140"),
                cancellationToken).ConfigureAwait(false));

        M4E1MutationResult changed = await ExecuteM4E1UpdateV2Async(
            connection,
            1,
            true,
            "M4-E1 v2 combined",
            true,
            "combined mutation",
            true,
            9000,
            "change Group RPM",
            cancellationToken).ConfigureAwait(false);
        Assert.Equal("updated", changed.Disposition);
        Assert.True(changed.WasChanged);
        Assert.Equal(2, changed.CurrentVersion);
        using (JsonDocument beforeState = JsonDocument.Parse(Assert.IsType<string>(changed.BeforeState)))
        {
            Assert.Equal(
                6000,
                beforeState.RootElement.GetProperty("requests_per_minute").GetInt32());
            Assert.Equal(1, beforeState.RootElement.GetProperty("version").GetInt64());
        }
        M4E1PolicyEvidence afterCombined = await ReadM4E1PolicyEvidenceAsync(
            connection,
            Guid.Parse("01900000-0000-7000-8000-00000000f140"),
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(2, afterCombined.Version);
        Assert.True(afterCombined.UpdatedAt > beforeUpdate.UpdatedAt);
        Assert.Equal(
            "{\"schema_version\": 1, \"requests_per_minute\": 9000}",
            afterCombined.RuntimePolicy);

        M4E1MutationResult noOp = await ExecuteM4E1UpdateV2Async(
            connection,
            2,
            false,
            null,
            false,
            null,
            true,
            9000,
            "confirm same Group RPM",
            cancellationToken).ConfigureAwait(false);
        Assert.Equal("updated", noOp.Disposition);
        Assert.False(noOp.WasChanged);
        Assert.Equal(2, noOp.CurrentVersion);
        Assert.Equal(
            afterCombined,
            await ReadM4E1PolicyEvidenceAsync(
                connection,
                Guid.Parse("01900000-0000-7000-8000-00000000f140"),
                cancellationToken).ConfigureAwait(false));

        M4E1MutationResult conflict = await ExecuteM4E1UpdateV2Async(
            connection,
            1,
            false,
            null,
            false,
            null,
            false,
            null,
            null,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal("version_conflict", conflict.Disposition);
        Assert.False(conflict.WasChanged);
        Assert.Equal(2, conflict.CurrentVersion);
        using (JsonDocument beforeState = JsonDocument.Parse(Assert.IsType<string>(conflict.BeforeState)))
        {
            Assert.Equal(
                9000,
                beforeState.RootElement.GetProperty("requests_per_minute").GetInt32());
        }

        using (NpgsqlCommand oldUpdate = connection.CreateCommand())
        {
            oldUpdate.CommandText = """
                SELECT disposition || ':' || was_changed::text || ':' || current_version::text
                FROM public.poolai_group_update(
                    '01900000-0000-7000-8000-00000000f140', 2,
                    true, 'M4-E1 old update preserves RPM',
                    false, NULL, NULL, NULL, NULL, NULL);
                """;
            Assert.Equal(
                "updated:true:3",
                Assert.IsType<string>(await oldUpdate.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }
        Assert.Equal(
            9000,
            await ReadM4E1RequestsPerMinuteAsync(
                connection,
                Guid.Parse("01900000-0000-7000-8000-00000000f140"),
                cancellationToken).ConfigureAwait(false));
        await ResetM4E1RoleAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AssertM4E1V2FamilyCConcurrencyAsync(
        NpgsqlDataSource dataSource,
        NpgsqlConnection setup,
        CancellationToken cancellationToken)
    {
        // Governing contracts: ADR 0006 Family C and Proposed ADR 0015.
        // The v2 Group archive must keep the Quota -> Group -> Subscription
        // order, observe PostgreSQL time only after any Group-lock wait, and
        // serialize atomically against Subscription mutations in both commit
        // orders without introducing a deadlock cycle.
        await SetM1E4ApiRoleAsync(setup, cancellationToken).ConfigureAwait(false);
        await AssertM4E1CreateV2Async(
            setup,
            Guid.Parse("01900000-0000-7000-8000-00000000f200"),
            Guid.Parse("01900000-0000-7000-8000-00000000f201"),
            Guid.Parse("01900000-0000-7000-8000-00000000f202"),
            Guid.Parse("01900000-0000-7000-8000-00000000f203"),
            "M4-E1 v2 clock fence",
            7_100,
            "m4-e1-v2-clock-fence",
            "created",
            cancellationToken).ConfigureAwait(false);
        await SeedM4E1SupplyForGroupAsync(
            setup,
            Guid.Parse("01900000-0000-7000-8000-00000000f200"),
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(
            await ExecuteM1E4MutationAsync(
                setup,
                null,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_group_update_v2(
                    '01900000-0000-7000-8000-00000000f200', 1,
                    false, NULL, false, NULL, false, NULL,
                    'active', 'activate v2 clock Group',
                    'v1.m4e1-clock-fence', clock_timestamp());
                """,
                cancellationToken).ConfigureAwait(false),
            "updated",
            true,
            2);
        AssertM1E4Mutation(
            await ExecuteM1E4MutationAsync(
                setup,
                null,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_subscription_template_create(
                    '01900000-0000-7000-8000-00000000f204',
                    '01900000-0000-7000-8000-00000000f200',
                    'M4-E1 v2 clock template', NULL, 30);
                """,
                cancellationToken).ConfigureAwait(false),
            "created",
            true,
            1);
        AssertM1E4Mutation(
            await ExecuteM1E4MutationAsync(
                setup,
                null,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_subscription_assign(
                    '01900000-0000-7000-8000-00000000f205',
                    '01900000-0000-7000-8000-00000000d000',
                    '01900000-0000-7000-8000-00000000f204',
                    clock_timestamp() - interval '2 days',
                    clock_timestamp() - interval '1 day',
                    '01900000-0000-7000-8000-00000000d000',
                    'seed expired v2 clock grant');
                """,
                cancellationToken).ConfigureAwait(false),
            "created",
            true,
            1);
        AssertM1E4Mutation(
            await ExecuteM1E4MutationAsync(
                setup,
                null,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_group_update_v2(
                    '01900000-0000-7000-8000-00000000f200', 2,
                    false, NULL, false, NULL, false, NULL,
                    'disabled', 'disable v2 clock Group', NULL, NULL);
                """,
                cancellationToken).ConfigureAwait(false),
            "updated",
            true,
            3);

        NpgsqlConnection subscriptionFirst = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable subscriptionFirstLease =
            subscriptionFirst.ConfigureAwait(false);
        await SetM1E4ApiRoleAsync(subscriptionFirst, cancellationToken).ConfigureAwait(false);
        NpgsqlTransaction subscriptionFirstTransaction = await subscriptionFirst
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable subscriptionFirstTransactionLease =
            subscriptionFirstTransaction.ConfigureAwait(false);
        AssertM1E4Mutation(
            await ExecuteM1E4MutationAsync(
                subscriptionFirst,
                subscriptionFirstTransaction,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_subscription_update(
                    '01900000-0000-7000-8000-00000000f205', 1,
                    false, NULL, true, clock_timestamp() + interval '2 seconds',
                    NULL, false,
                    '01900000-0000-7000-8000-00000000d000',
                    'extend while v2 archive waits');
                """,
                cancellationToken).ConfigureAwait(false),
            "updated",
            true,
            2);

        NpgsqlConnection waitingArchive = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable waitingArchiveLease =
            waitingArchive.ConfigureAwait(false);
        await SetM1E4ApiRoleAsync(waitingArchive, cancellationToken).ConfigureAwait(false);
        int waitingArchivePid = await ReadM1E4BackendPidAsync(
            waitingArchive,
            cancellationToken).ConfigureAwait(false);
        using CancellationTokenSource firstOrderTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        firstOrderTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        Task<M1E4Mutation> waitingArchiveTask = ExecuteM1E4MutationAsync(
            waitingArchive,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_group_update_v2(
                '01900000-0000-7000-8000-00000000f200', 3,
                false, NULL, false, NULL, false, NULL,
                'archived', 'archive after subscription wait', NULL, NULL);
            """,
            firstOrderTimeout.Token).AsTask();
        Assert.True(
            await WaitForM1E4LockWaitAsync(
                dataSource,
                waitingArchivePid,
                firstOrderTimeout.Token).ConfigureAwait(false),
            "The v2 Group archive did not wait behind the Subscription Group fence.");
        await WaitForM4E1SubscriptionExpiryAsync(
            subscriptionFirst,
            subscriptionFirstTransaction,
            Guid.Parse("01900000-0000-7000-8000-00000000f205"),
            firstOrderTimeout.Token).ConfigureAwait(false);
        await subscriptionFirstTransaction
            .CommitAsync(firstOrderTimeout.Token)
            .ConfigureAwait(false);
        AssertM1E4Mutation(
            await waitingArchiveTask.ConfigureAwait(false),
            "updated",
            true,
            4);
        Assert.Equal(
            "archived:4:7100,active:2:expired",
            await ReadM4E1FamilyCStateAsync(
                setup,
                Guid.Parse("01900000-0000-7000-8000-00000000f200"),
                Guid.Parse("01900000-0000-7000-8000-00000000f205"),
                cancellationToken).ConfigureAwait(false));

        await AssertM4E1CreateV2Async(
            setup,
            Guid.Parse("01900000-0000-7000-8000-00000000f210"),
            Guid.Parse("01900000-0000-7000-8000-00000000f211"),
            Guid.Parse("01900000-0000-7000-8000-00000000f212"),
            Guid.Parse("01900000-0000-7000-8000-00000000f213"),
            "M4-E1 v2 archive fence",
            7_200,
            "m4-e1-v2-archive-fence",
            "created",
            cancellationToken).ConfigureAwait(false);
        await SeedM4E1SupplyForGroupAsync(
            setup,
            Guid.Parse("01900000-0000-7000-8000-00000000f210"),
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(
            await ExecuteM1E4MutationAsync(
                setup,
                null,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_group_update_v2(
                    '01900000-0000-7000-8000-00000000f210', 1,
                    false, NULL, false, NULL, false, NULL,
                    'active', 'activate v2 archive Group',
                    'v1.m4e1-archive-fence', clock_timestamp());
                """,
                cancellationToken).ConfigureAwait(false),
            "updated",
            true,
            2);
        AssertM1E4Mutation(
            await ExecuteM1E4MutationAsync(
                setup,
                null,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_subscription_template_create(
                    '01900000-0000-7000-8000-00000000f214',
                    '01900000-0000-7000-8000-00000000f210',
                    'M4-E1 v2 archive template', NULL, 30);
                """,
                cancellationToken).ConfigureAwait(false),
            "created",
            true,
            1);
        AssertM1E4Mutation(
            await ExecuteM1E4MutationAsync(
                setup,
                null,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_subscription_assign(
                    '01900000-0000-7000-8000-00000000f215',
                    '01900000-0000-7000-8000-00000000d000',
                    '01900000-0000-7000-8000-00000000f214',
                    clock_timestamp() - interval '2 days',
                    clock_timestamp() - interval '1 day',
                    '01900000-0000-7000-8000-00000000d000',
                    'seed expired v2 archive grant');
                """,
                cancellationToken).ConfigureAwait(false),
            "created",
            true,
            1);
        AssertM1E4Mutation(
            await ExecuteM1E4MutationAsync(
                setup,
                null,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_group_update_v2(
                    '01900000-0000-7000-8000-00000000f210', 2,
                    false, NULL, false, NULL, false, NULL,
                    'disabled', 'disable v2 archive Group', NULL, NULL);
                """,
                cancellationToken).ConfigureAwait(false),
            "updated",
            true,
            3);

        NpgsqlConnection archiveFirst = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable archiveFirstLease =
            archiveFirst.ConfigureAwait(false);
        await SetM1E4ApiRoleAsync(archiveFirst, cancellationToken).ConfigureAwait(false);
        NpgsqlTransaction archiveFirstTransaction = await archiveFirst
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable archiveFirstTransactionLease =
            archiveFirstTransaction.ConfigureAwait(false);
        AssertM1E4Mutation(
            await ExecuteM1E4MutationAsync(
                archiveFirst,
                archiveFirstTransaction,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_group_update_v2(
                    '01900000-0000-7000-8000-00000000f210', 3,
                    false, NULL, false, NULL, false, NULL,
                    'archived', 'v2 archive wins', NULL, NULL);
                """,
                cancellationToken).ConfigureAwait(false),
            "updated",
            true,
            4);

        NpgsqlConnection waitingSubscription = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable waitingSubscriptionLease =
            waitingSubscription.ConfigureAwait(false);
        await SetM1E4ApiRoleAsync(waitingSubscription, cancellationToken)
            .ConfigureAwait(false);
        int waitingSubscriptionPid = await ReadM1E4BackendPidAsync(
            waitingSubscription,
            cancellationToken).ConfigureAwait(false);
        using CancellationTokenSource secondOrderTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        secondOrderTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        Task<M1E4Mutation> waitingSubscriptionTask = ExecuteM1E4MutationAsync(
            waitingSubscription,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_subscription_update(
                '01900000-0000-7000-8000-00000000f215', 1,
                false, NULL, true, clock_timestamp() + interval '1 day',
                NULL, false,
                '01900000-0000-7000-8000-00000000d000',
                'subscription after v2 archive');
            """,
            secondOrderTimeout.Token).AsTask();
        Assert.True(
            await WaitForM1E4LockWaitAsync(
                dataSource,
                waitingSubscriptionPid,
                secondOrderTimeout.Token).ConfigureAwait(false),
            "The Subscription mutation did not wait behind the v2 Group archive fence.");
        await archiveFirstTransaction
            .CommitAsync(secondOrderTimeout.Token)
            .ConfigureAwait(false);
        AssertM1E4Mutation(
            await waitingSubscriptionTask.ConfigureAwait(false),
            "group_archived",
            false,
            null);
        Assert.Equal(
            "archived:4:7200,active:1:expired",
            await ReadM4E1FamilyCStateAsync(
                setup,
                Guid.Parse("01900000-0000-7000-8000-00000000f210"),
                Guid.Parse("01900000-0000-7000-8000-00000000f215"),
            cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask SeedM4E1SupplyForGroupAsync(
        NpgsqlConnection connection,
        Guid groupId,
        CancellationToken cancellationToken)
    {
        using (NpgsqlCommand supply = connection.CreateCommand())
        {
            supply.CommandText = """
                RESET ROLE;
                INSERT INTO public.channels (
                    id, provider, name, model_rules, capabilities, status
                ) VALUES (
                    '01900000-0000-7000-8000-00000000f220',
                    'openai', 'M4-E1 Family C Channel',
                    '{"gpt-m4-e1":"gpt-m4-e1"}'::jsonb,
                    '{"responses":true,"chat_completions":true,
                      "function_tools":true,"streaming":true}'::jsonb,
                    'active'
                ) ON CONFLICT DO NOTHING;
                INSERT INTO public.accounts (
                    id, provider, name, auth_type, upstream_base_url,
                    credential_envelope, credential_prefix,
                    status, last_health_at, last_health_status
                ) VALUES (
                    '01900000-0000-7000-8000-00000000f221',
                    'openai', 'M4-E1 Family C Account', 'api_key',
                    'https://example.test/v1',
                    '{"v":1,"alg":"A256GCM+A256GCM-v1","kid":"test-kek-v1",
                      "wrapped_dek":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                      "wrap_nonce":"AAAAAAAAAAAAAAAA","wrap_tag":"AAAAAAAAAAAAAAAAAAAAAA",
                      "ciphertext":"bTQtZTEtZmFtaWx5LWM","nonce":"AQEBAQEBAQEBAQEB",
                      "tag":"AgICAgICAgICAgICAgICAg"}'::jsonb,
                    'sk-m4-e1-family-c', 'active', clock_timestamp(), 'healthy'
                ) ON CONFLICT DO NOTHING;
                """;
            await supply.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using (NpgsqlCommand configuration = connection.CreateCommand())
        {
            configuration.CommandText = """
                INSERT INTO public.group_supply_configurations (group_id, channel_id)
                VALUES ($1, '01900000-0000-7000-8000-00000000f220');
                """;
            configuration.Parameters.AddWithValue(groupId);
            await configuration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using (NpgsqlCommand binding = connection.CreateCommand())
        {
            binding.CommandText = """
                INSERT INTO public.group_accounts (group_id, account_id, is_enabled)
                VALUES ($1, '01900000-0000-7000-8000-00000000f221', true);
                """;
            binding.Parameters.AddWithValue(groupId);
            await binding.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await SetM1E4ApiRoleAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask WaitForM4E1SubscriptionExpiryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid subscriptionId,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 240; attempt++)
        {
            using NpgsqlCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT clock_timestamp() >= expires_at
                FROM public.subscriptions
                WHERE id = $1;
                """;
            command.Parameters.AddWithValue(subscriptionId);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken)
                .ConfigureAwait(false);
        }

        Assert.Fail("The v2 Family C Subscription did not reach its PostgreSQL expiry boundary.");
    }

    private static async ValueTask<string> ReadM4E1FamilyCStateAsync(
        NpgsqlConnection connection,
        Guid groupId,
        Guid subscriptionId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT current_group.status || ':' || current_group.version::text || ':'
                   || (current_group.runtime_policy ->> 'requests_per_minute') || ','
                   || subscription.status || ':' || subscription.version::text || ':'
                   || CASE
                          WHEN clock_timestamp() >= subscription.expires_at
                              THEN 'expired'
                          ELSE 'active'
                      END
            FROM public.groups AS current_group
            JOIN public.subscriptions AS subscription
              ON subscription.group_id = current_group.id
            WHERE current_group.id = $1
              AND subscription.id = $2;
            """;
        command.Parameters.AddWithValue(groupId);
        command.Parameters.AddWithValue(subscriptionId);
        return Assert.IsType<string>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static async ValueTask AssertM4E1CreateV2Async(
        NpgsqlConnection connection,
        Guid groupId,
        Guid periodId,
        Guid eventId,
        Guid outboxId,
        string name,
        int? requestsPerMinute,
        string idempotencyKey,
        string expectedDisposition,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT disposition
            FROM public.poolai_group_create_v2(
                $1, $2, NULL, $3, $4, 200, $5, $6, $7, $8,
                'M4-E1 database fixture');
            """;
        command.Parameters.AddWithValue(groupId);
        command.Parameters.AddWithValue(name);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Integer,
            Value = requestsPerMinute.HasValue
                ? requestsPerMinute.Value
                : DBNull.Value,
        });
        command.Parameters.AddWithValue(periodId);
        command.Parameters.AddWithValue(M3E1ActorId);
        command.Parameters.AddWithValue(eventId);
        command.Parameters.AddWithValue(outboxId);
        command.Parameters.AddWithValue(idempotencyKey);
        Assert.Equal(
            expectedDisposition,
            Assert.IsType<string>(await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));
        Assert.Equal(
            string.Equals(expectedDisposition, "created", StringComparison.Ordinal) ? 1L : 0L,
            await ReadM4E1GroupCountAsync(connection, groupId, cancellationToken)
                .ConfigureAwait(false));
    }

    private static async ValueTask<M4E1MutationResult> ExecuteM4E1UpdateV2Async(
        NpgsqlConnection connection,
        long expectedVersion,
        bool setName,
        string? name,
        bool setDescription,
        string? description,
        bool setRequestsPerMinute,
        int? requestsPerMinute,
        string? reason,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_group_update_v2(
                '01900000-0000-7000-8000-00000000f140',
                $1, $2, $3, $4, $5, $6, $7, NULL, $8, NULL, NULL);
            """;
        command.Parameters.AddWithValue(expectedVersion);
        command.Parameters.AddWithValue(setName);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Text,
            Value = name ?? (object)DBNull.Value,
        });
        command.Parameters.AddWithValue(setDescription);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Text,
            Value = description ?? (object)DBNull.Value,
        });
        command.Parameters.AddWithValue(setRequestsPerMinute);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Integer,
            Value = requestsPerMinute.HasValue
                ? requestsPerMinute.Value
                : DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Text,
            Value = reason ?? (object)DBNull.Value,
        });
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        M4E1MutationResult result = new(
            reader.GetString(0),
            reader.GetBoolean(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return result;
    }

    private static async ValueTask<M4E1PolicyEvidence> ReadM4E1PolicyEvidenceAsync(
        NpgsqlConnection connection,
        Guid groupId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT runtime_policy::text, version, updated_at
            FROM public.groups
            WHERE id = $1;
            """;
        command.Parameters.AddWithValue(groupId);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        M4E1PolicyEvidence result = new(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetDateTime(2));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return result;
    }

    private static async ValueTask<int> ReadM4E1RequestsPerMinuteAsync(
        NpgsqlConnection connection,
        Guid groupId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT (runtime_policy ->> 'requests_per_minute')::integer
            FROM public.groups
            WHERE id = $1;
            """;
        command.Parameters.AddWithValue(groupId);
        return Assert.IsType<int>(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static async ValueTask<long> ReadM4E1GroupCountAsync(
        NpgsqlConnection connection,
        Guid groupId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM public.groups WHERE id = $1;";
        command.Parameters.AddWithValue(groupId);
        return Assert.IsType<long>(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static async ValueTask<long> ReadM4E1TopMigrationAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT max(version) FROM public.poolai_schema_migrations;";
        return Assert.IsType<long>(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static async ValueTask<bool> ReadM4E1ConstraintValidatedAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT constraint_definition.convalidated
            FROM pg_catalog.pg_constraint AS constraint_definition
            WHERE constraint_definition.conname = 'ck_groups_runtime_policy_m4_e1'
              AND constraint_definition.conrelid = 'public.groups'::regclass;
            """;
        return Assert.IsType<bool>(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static async ValueTask<string> ReadM4E1RuntimePolicyDefaultAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT pg_catalog.pg_get_expr(
                attribute_default.adbin,
                attribute_default.adrelid)
            FROM pg_catalog.pg_attrdef AS attribute_default
            JOIN pg_catalog.pg_attribute AS attribute
              ON attribute.attrelid = attribute_default.adrelid
             AND attribute.attnum = attribute_default.adnum
            WHERE attribute_default.adrelid = 'public.groups'::regclass
              AND attribute.attname = 'runtime_policy';
            """;
        return Assert.IsType<string>(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static async ValueTask ResetM4E1RoleAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "RESET ROLE;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record M4E1PolicyEvidence(
        string RuntimePolicy,
        long Version,
        DateTime UpdatedAt);

    private sealed record M4E1MutationResult(
        string Disposition,
        bool WasChanged,
        string? BeforeState,
        long? CurrentVersion);
}
