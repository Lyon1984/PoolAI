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

    internal async ValueTask<bool> IsSessionFamilyActiveAsync(
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
            SELECT EXISTS (
                SELECT 1
                FROM public.refresh_sessions AS session
                JOIN public.users AS user_account
                  ON user_account.id = session.user_id
                WHERE session.user_id = $1
                  AND session.family_id = $2
                  AND session.status = 'active'
                  AND session.expires_at > clock_timestamp()
                  AND user_account.status = 'active'
                  AND user_account.deleted_at IS NULL
                  AND user_account.token_version = $3
            );
            """;
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(familyId.Value);
        command.Parameters.AddWithValue(tokenVersion);
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is bool active
            ? active
            : throw new InvalidOperationException("Session-family validation returned no Boolean value.");
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
}
