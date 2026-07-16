using System.Globalization;
using PoolAI.Modules.Operations.Abstractions;
using StackExchange.Redis;

namespace PoolAI.Modules.Operations.Infrastructure.Redis;

internal sealed class RedisFixedWindowCounter(
    RedisConnectionProvider connections,
    RedisScriptCatalog scripts,
    RedisScriptRegistry registry,
    RuntimeDependencyOptions options,
    ReleaseManifestV1 releaseManifest) : IFixedWindowCounter
{
    private const string ScriptName = "fixed_window_increment";
    private const int ScriptVersion = 1;
    private const int TtlMilliseconds = 120_000;

    private readonly RedisConnectionProvider _connections =
        connections ?? throw new ArgumentNullException(nameof(connections));
    private readonly RedisScriptAsset _script = (scripts
        ?? throw new ArgumentNullException(nameof(scripts))).Scripts.Single(script =>
            string.Equals(script.Name, ScriptName, StringComparison.Ordinal)
            && script.LogicalVersion == ScriptVersion);
    private readonly RedisScriptRegistry _registry =
        registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly RuntimeDependencyOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));
    private readonly ReleaseManifestV1 _releaseManifest =
        releaseManifest ?? throw new ArgumentNullException(nameof(releaseManifest));

    public async ValueTask<FixedWindowCounterResult> IncrementAsync(
        FixedWindowCounterRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        try
        {
            ConnectionMultiplexer connection = await _connections
                .GetAsync(cancellationToken)
                .ConfigureAwait(false);
            IDatabase database = connection.GetDatabase();
            long observedMinute = await ReadRedisMinuteAsync(database, cancellationToken)
                .ConfigureAwait(false);
            RedisKey first = $"{_options.RedisKeyPrefix}{request.KeyBase}:{observedMinute.ToString(CultureInfo.InvariantCulture)}";
            RedisKey second = $"{_options.RedisKeyPrefix}{request.KeyBase}:{checked(observedMinute + 1).ToString(CultureInfo.InvariantCulture)}";

            RedisResult result;
            try
            {
                result = await EvaluateAsync(
                    database,
                    first,
                    second,
                    request,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (RedisServerException exception) when (
                exception.Message.StartsWith("NOSCRIPT", StringComparison.Ordinal))
            {
                await _registry.EnsureLoadedAsync(
                    connection,
                    _releaseManifest.Redis.RequiredServerMajor,
                    cancellationToken).ConfigureAwait(false);
                result = await EvaluateAsync(
                    database,
                    first,
                    second,
                    request,
                    cancellationToken).ConfigureAwait(false);
            }

            return Parse(result, request.Limit);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is RedisException
            or TimeoutException
            or InvalidOperationException
            or OverflowException)
        {
            return FixedWindowCounterResult.Unavailable;
        }
    }

    private async ValueTask<RedisResult> EvaluateAsync(
        IDatabase database,
        RedisKey first,
        RedisKey second,
        FixedWindowCounterRequest request,
        CancellationToken cancellationToken)
    {
        string sha = Convert.ToHexStringLower(_script.RedisSha1);
        return await database.ExecuteAsync(
                "EVALSHA",
                sha,
                2,
                first,
                second,
                request.Limit,
                request.Increment,
                TtlMilliseconds)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<long> ReadRedisMinuteAsync(
        IDatabase database,
        CancellationToken cancellationToken)
    {
        RedisResult result = await database
            .ExecuteAsync("TIME")
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        if (result.Resp2Type != ResultType.Array
            || (RedisResult[]?)result is not { Length: 2 } parts
            || !long.TryParse(
                parts[0].ToString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long seconds)
            || seconds < 0)
        {
            throw new InvalidOperationException("Redis TIME returned an invalid value.");
        }

        return seconds / 60;
    }

    private static FixedWindowCounterResult Parse(RedisResult result, int expectedLimit)
    {
        if (result.Resp2Type != ResultType.Array
            || (RedisResult[]?)result is not { Length: 4 } parts
            || !long.TryParse(parts[0].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long code)
            || !long.TryParse(parts[1].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long current)
            || !long.TryParse(parts[2].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long returnedLimit)
            || !long.TryParse(parts[3].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long retryAfterMs)
            || current < 0
            || returnedLimit != expectedLimit
            || retryAfterMs < 0)
        {
            return FixedWindowCounterResult.Unavailable;
        }

        return code switch
        {
            1 when current is > 0 && current <= expectedLimit && retryAfterMs == 0 =>
                FixedWindowCounterResult.Allowed(current),
            0 when current > expectedLimit && retryAfterMs > 0 => FixedWindowCounterResult.Rejected(
                current,
                TimeSpan.FromMilliseconds(retryAfterMs)),
            _ => FixedWindowCounterResult.Unavailable,
        };
    }

    private static void Validate(FixedWindowCounterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.KeyBase))
        {
            throw new ArgumentException(
                "The fixed-window key base is required.",
                nameof(request));
        }
        if (request.Limit <= 0 || request.Increment <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        int openBrace = request.KeyBase.IndexOf('{', StringComparison.Ordinal);
        int closeBrace = request.KeyBase.IndexOf('}', StringComparison.Ordinal);
        if (!request.KeyBase.StartsWith("rate:", StringComparison.Ordinal)
            || request.KeyBase.Length > 256
            || openBrace < 5
            || closeBrace <= openBrace + 1
            || closeBrace != request.KeyBase.Length - 1
            || request.KeyBase.IndexOf('{', openBrace + 1) >= 0
            || request.KeyBase.IndexOf('}', closeBrace + 1) >= 0
            || request.KeyBase.Any(static character => character is not (
                >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or ':'
                or '-'
                or '{'
                or '}')))
        {
            throw new ArgumentException("The fixed-window key base is invalid.", nameof(request));
        }
    }
}
