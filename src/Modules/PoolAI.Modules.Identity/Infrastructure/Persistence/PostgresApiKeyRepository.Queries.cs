using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Identity.Application.Ports;
using PoolAI.Modules.Identity.Domain;

namespace PoolAI.Modules.Identity.Infrastructure.Persistence;

internal sealed partial class PostgresApiKeyRepository
{
    public async ValueTask<ApiKeySlice> ListAsync(
        EntityId userId,
        ApiKeyCursor? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);
        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease =
            connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = cursor is null ? ListFirstSql : ListAfterSql;
        command.Parameters.AddWithValue(userId.Value);
        if (cursor is null)
        {
            command.Parameters.AddWithValue(limit + 1);
        }
        else
        {
            command.Parameters.AddWithValue(cursor.CreatedAt.ToUniversalTime());
            command.Parameters.AddWithValue(cursor.Id.Value);
            command.Parameters.AddWithValue(limit + 1);
        }

        List<ApiKeyResource> apiKeys = new(limit + 1);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            apiKeys.Add(ReadApiKey(reader));
        }

        bool hasMore = apiKeys.Count > limit;
        if (hasMore)
        {
            apiKeys.RemoveAt(apiKeys.Count - 1);
        }

        return new ApiKeySlice(apiKeys, hasMore);
    }

    public async ValueTask<ApiKeyResource?> GetAsync(
        EntityId userId,
        EntityId apiKeyId,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease =
            connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = GetSql;
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(apiKeyId.Value);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        ApiKeyResource candidate = ReadApiKey(reader);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? null
            : candidate;
    }

    public async ValueTask<IReadOnlyList<ApiKeyAuthenticationCandidate>>
        ListAuthenticationCandidatesAsync(
        string displayPrefix,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayPrefix);

        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease =
            connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = ListAuthenticationCandidatesSql;
        command.Parameters.AddWithValue(displayPrefix);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ApiKeyAuthenticationCandidate> candidates = [];
        try
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                byte[] secretHash = reader.GetFieldValue<byte[]>(14);
                try
                {
                    candidates.Add(new ApiKeyAuthenticationCandidate(
                        ReadApiKey(reader),
                        secretHash,
                        reader.GetInt16(15)));
                }
                catch
                {
                    CryptographicOperations.ZeroMemory(secretHash);
                    throw;
                }
            }

            return candidates;
        }
        catch
        {
            foreach (ApiKeyAuthenticationCandidate candidate in candidates)
            {
                CryptographicOperations.ZeroMemory(candidate.SecretHash);
            }

            throw;
        }
    }
}
