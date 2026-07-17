using System.Runtime.CompilerServices;
using Npgsql;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application.Ports;

namespace PoolAI.Modules.Identity.Infrastructure.Persistence;

internal sealed class PostgresIdentitySessionReader
{
    private readonly NpgsqlDataSource _dataSource;

    internal PostgresIdentitySessionReader(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    internal ValueTask<AuthenticationUserSnapshot?> FindAuthenticationUserAsync(
        string normalizedEmail,
        CancellationToken cancellationToken) => ReadUserAsync(
            "u.normalized_email = $1",
            normalizedEmail,
            cancellationToken);

    internal ValueTask<AuthenticationUserSnapshot?> GetAuthenticationUserAsync(
        EntityId userId,
        CancellationToken cancellationToken) => ReadUserAsync(
            "u.id = $1",
            userId.Value,
            cancellationToken);

    internal async ValueTask<UserStatusSnapshot?> ReadCanonicalAuthorizationAsync(
        EntityId userId,
        EntityId familyId,
        long tokenVersion,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease = connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT user_account.id,
                   role.code,
                   user_account.token_version,
                   user_account.version,
                   clock_timestamp()
            FROM public.users AS user_account
            JOIN public.user_roles AS user_role
              ON user_role.user_id = user_account.id
            JOIN public.roles AS role
              ON role.id = user_role.role_id
            WHERE user_account.id = $1
              AND user_account.status = 'active'
              AND user_account.deleted_at IS NULL
              AND user_account.token_version = $3
              AND EXISTS (
                  SELECT 1
                  FROM public.refresh_sessions AS session
                  WHERE session.user_id = user_account.id
                    AND session.family_id = $2
                    AND session.status = 'active'
                    AND session.expires_at > clock_timestamp()
              );
            """;
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(familyId.Value);
        command.Parameters.AddWithValue(tokenVersion);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        UserStatusSnapshot authorization = new(
            new EntityId(reader.GetGuid(0)),
            UserLifecycle.Active,
            ParseRole(reader.GetString(1)),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetFieldValue<DateTimeOffset>(4));
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Canonical access authorization returned multiple role assignments.");
        }

        return authorization;
    }

    internal async ValueTask<bool> HasRefreshCredentialAsync(
        IReadOnlyList<CredentialHashCandidate> candidates,
        CancellationToken cancellationToken)
    {
        PostgresIdentitySessionRepository.ValidateCandidates(candidates);
        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease = connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM public.refresh_sessions
                WHERE (pepper_version = $1 AND token_hash = $2)
                   OR ($3::smallint IS NOT NULL AND pepper_version = $3 AND token_hash = $4)
            );
            """;
        PostgresIdentitySessionRepository.AddCandidates(command.Parameters, candidates);
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is bool exists
            ? exists
            : throw new InvalidOperationException("Refresh preflight returned no Boolean value.");
    }

    internal async ValueTask<TotpChallengeSnapshot?> FindTotpChallengeAsync(
        IReadOnlyList<CredentialHashCandidate> candidates,
        string kind,
        CancellationToken cancellationToken)
    {
        PostgresIdentitySessionRepository.ValidateCandidates(candidates);
        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease = connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = PostgresIdentitySessionRepository.BuildFindChallengeSql(
            forUpdate: false);
        PostgresIdentitySessionRepository.AddCandidates(command.Parameters, candidates);
        command.Parameters.AddWithValue(kind);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? PostgresIdentitySessionRepository.ReadChallenge(reader)
            : null;
    }

    private async ValueTask<AuthenticationUserSnapshot?> ReadUserAsync(
        string predicate,
        object value,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease = connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = PostgresIdentitySessionRepository.BuildUserSql(
            predicate,
            forUpdate: false);
        command.Parameters.AddWithValue(value);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? PostgresIdentitySessionRepository.ReadUser(reader, 0)
            : null;
    }

    private static SystemRole ParseRole(string value) => value switch
    {
        "admin" => SystemRole.Admin,
        "operator" => SystemRole.Operator,
        "auditor" => SystemRole.Auditor,
        "user" => SystemRole.User,
        _ => throw new InvalidOperationException("Identity user has an unknown role."),
    };
}
