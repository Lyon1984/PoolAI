-- breaker_record_v1
-- KEYS[1]: Account breaker HASH.
-- KEYS[2]: Account cooldown String JSON.
-- KEYS[3]: Account half-open probe String.
-- ARGV[1]: success/transient_failure/rate_limited/auth_failure/ignored.
-- ARGV[2]: retry-after milliseconds; 0 means absent for rate_limited.
-- ARGV[3]: bounded jitter in basis points (0..1000).
-- ARGV[4]: upstream source status (0..599).
-- ARGV[5]: passive/controlled_active.
-- ARGV[6]: logical version; R1 requires 1.
-- Returns [state,samples,failures,consecutive,open_until_ms,action].

local max_safe_integer = 9007199254740991
local max_counter = 2147483647
local breaker_ttl_ms = 172800000
local window_ms = 30000

local function parse_integer(value, minimum, maximum)
    if type(value) ~= "string" then
        return nil
    end
    if value ~= "0"
        and string.match(value, "^[1-9][0-9]*$") == nil then
        return nil
    end
    local parsed = tonumber(value)
    if parsed == nil
        or parsed < minimum
        or parsed > maximum
        or parsed ~= math.floor(parsed) then
        return nil
    end
    return parsed
end

local function redis_now_ms()
    local redis_time = redis.call("TIME")
    local seconds = parse_integer(redis_time[1], 0, 9007199254740)
    local microseconds = parse_integer(redis_time[2], 0, 999999)
    if seconds == nil or microseconds == nil then
        return nil
    end
    return (seconds * 1000) + math.floor(microseconds / 1000)
end

local function key_type(key)
    local result = redis.call("TYPE", key)
    if type(result) == "table" then
        return result.ok
    end
    return result
end

local function valid_keys()
    if #KEYS ~= 3
        or KEYS[1] == ""
        or KEYS[2] == ""
        or KEYS[3] == "" then
        return false
    end
    local breaker_tag = string.match(
        KEYS[1],
        "breaker:account:v1:{([^{}]+)}$")
    local cooldown_tag = string.match(
        KEYS[2],
        "cooldown:account:v1:{([^{}]+)}$")
    local probe_tag = string.match(
        KEYS[3],
        "breaker%-probe:account:v1:{([^{}]+)}$")
    return breaker_tag ~= nil
        and cooldown_tag ~= nil
        and probe_tag ~= nil
        and breaker_tag == cooldown_tag
        and breaker_tag == probe_tag
end

local function invalid()
    return {-1, 0, 0, 0, 0, 0}
end

local function current_state(
    now_ms,
    open_count,
    open_until_ms,
    auth_blocked)
    if auth_blocked == 1 or open_until_ms > now_ms then
        return 1
    end
    if open_count > 0 then
        return 2
    end
    return 0
end

local function strict_action(
    now_ms,
    open_count,
    open_until_ms,
    auth_blocked)
    if auth_blocked == 1 then
        return 4
    end
    if open_until_ms > now_ms then
        return 3
    end
    if open_count > 0 then
        return 5
    end
    return 0
end

local function normalized_retry_after_ms(value)
    if value == 0 then
        return 30000
    end
    return math.min(math.max(value, 1000), 86400000)
end

local function open_duration_ms(open_count, jitter_basis_points)
    local exponent = math.min(open_count - 1, 4)
    local base_ms = 30000 * (2 ^ exponent)
    local jitter_ms = math.floor(
        (base_ms * jitter_basis_points) / 10000)
    return math.min(base_ms + jitter_ms, 300000)
end

local function write_cooldown(
    reason,
    source_status,
    now_ms,
    retry_at_ms)
    local payload =
        "{\"reason\":\""
        .. reason
        .. "\",\"retry_at\":"
        .. string.format("%.0f", retry_at_ms)
        .. ",\"source_status\":"
        .. string.format("%.0f", source_status)
        .. "}"
    redis.call(
        "SET",
        KEYS[2],
        payload,
        "PX",
        math.max(retry_at_ms - now_ms, 1))
end

local function save(
    window_started_at_ms,
    samples,
    failures,
    consecutive_failures,
    open_until_ms,
    open_count,
    half_open_successes,
    auth_blocked)
    redis.call(
        "HSET",
        KEYS[1],
        "window_started_at_ms", window_started_at_ms,
        "samples", samples,
        "failures", failures,
        "consecutive_failures", consecutive_failures,
        "open_until_ms", open_until_ms,
        "open_count", open_count,
        "half_open_successes", half_open_successes,
        "auth_blocked", auth_blocked)
    redis.call("PEXPIRE", KEYS[1], breaker_ttl_ms)
end

local function valid_hash_state(
    now_ms,
    window_started_at_ms,
    samples,
    failures,
    consecutive_failures,
    open_until_ms,
    open_count,
    half_open_successes,
    auth_blocked)
    if window_started_at_ms == nil
        or samples == nil
        or failures == nil
        or consecutive_failures == nil
        or open_until_ms == nil
        or open_count == nil
        or half_open_successes == nil
        or auth_blocked == nil
        or failures > samples
        or window_started_at_ms > now_ms then
        return false
    end
    if open_count == 0 then
        return open_until_ms == 0
            and half_open_successes == 0
            and auth_blocked == 0
    end
    if auth_blocked == 1 then
        return open_until_ms == 0 and half_open_successes == 0
    end
    return half_open_successes == 0 or open_until_ms <= now_ms
end

local function valid_outcome(
    outcome,
    retry_after_ms,
    jitter_basis_points,
    source_status,
    observation_mode)
    if observation_mode ~= "passive"
        and observation_mode ~= "controlled_active" then
        return false
    end
    if outcome == "success" then
        return retry_after_ms == 0
            and jitter_basis_points == 0
            and source_status >= 200
            and source_status <= 299
    end
    if outcome == "transient_failure" then
        return retry_after_ms == 0
            and (source_status == 0
                or (source_status >= 200 and source_status <= 399)
                or source_status == 408
                or (source_status >= 500 and source_status <= 599))
    end
    if outcome == "rate_limited" then
        return jitter_basis_points == 0 and source_status == 429
    end
    if outcome == "auth_failure" then
        return retry_after_ms == 0
            and jitter_basis_points == 0
            and (source_status == 401 or source_status == 403)
    end
    if outcome == "ignored" then
        return retry_after_ms == 0
            and jitter_basis_points == 0
            and (source_status == 0
                or (source_status >= 400
                    and source_status <= 499
                    and source_status ~= 401
                    and source_status ~= 403
                    and source_status ~= 408
                    and source_status ~= 429))
    end
    return false
end

if not valid_keys() or #ARGV ~= 6 or ARGV[6] ~= "1" then
    return invalid()
end

local retry_after_ms = parse_integer(
    ARGV[2],
    0,
    max_safe_integer)
local jitter_basis_points = parse_integer(ARGV[3], 0, 1000)
local source_status = parse_integer(ARGV[4], 0, 599)
if retry_after_ms == nil
    or jitter_basis_points == nil
    or source_status == nil
    or not valid_outcome(
        ARGV[1],
        retry_after_ms,
        jitter_basis_points,
        source_status,
        ARGV[5]) then
    return invalid()
end

local breaker_type = key_type(KEYS[1])
local cooldown_type = key_type(KEYS[2])
local probe_type = key_type(KEYS[3])
if (breaker_type ~= "none" and breaker_type ~= "hash")
    or (cooldown_type ~= "none" and cooldown_type ~= "string")
    or (probe_type ~= "none" and probe_type ~= "string") then
    return invalid()
end
if cooldown_type == "string"
    and redis.call("PTTL", KEYS[2]) <= 0 then
    return invalid()
end
if breaker_type == "none" and cooldown_type ~= "none" then
    return invalid()
end

local now_ms = redis_now_ms()
if now_ms == nil then
    return invalid()
end

local window_started_at_ms = now_ms
local samples = 0
local failures = 0
local consecutive_failures = 0
local open_until_ms = 0
local open_count = 0
local half_open_successes = 0
local auth_blocked = 0

if breaker_type == "hash" then
    if redis.call("HLEN", KEYS[1]) ~= 8 then
        return invalid()
    end
    local values = redis.call(
        "HMGET",
        KEYS[1],
        "window_started_at_ms",
        "samples",
        "failures",
        "consecutive_failures",
        "open_until_ms",
        "open_count",
        "half_open_successes",
        "auth_blocked")
    window_started_at_ms = parse_integer(
        values[1],
        0,
        max_safe_integer)
    samples = parse_integer(values[2], 0, max_counter)
    failures = parse_integer(values[3], 0, max_counter)
    consecutive_failures = parse_integer(
        values[4],
        0,
        max_counter)
    open_until_ms = parse_integer(values[5], 0, max_safe_integer)
    open_count = parse_integer(values[6], 0, max_counter)
    half_open_successes = parse_integer(values[7], 0, 1)
    auth_blocked = parse_integer(values[8], 0, 1)
    if not valid_hash_state(
        now_ms,
        window_started_at_ms,
        samples,
        failures,
        consecutive_failures,
        open_until_ms,
        open_count,
        half_open_successes,
        auth_blocked) then
        return invalid()
    end
    if open_count == 0 and cooldown_type ~= "none" then
        return invalid()
    end
end

local outcome = ARGV[1]
if outcome == "ignored" then
    return {
        current_state(now_ms, open_count, open_until_ms, auth_blocked),
        samples,
        failures,
        consecutive_failures,
        open_until_ms,
        0
    }
end

-- Only the version-fenced initial/replacement validation path may send
-- controlled_active. It retires any breaker generation for the old credential.
if outcome == "success" and ARGV[5] == "controlled_active" then
    redis.call("DEL", KEYS[1], KEYS[2], KEYS[3])
    return {0, 0, 0, 0, 0, 1}
end

local active_open = auth_blocked == 1 or open_until_ms > now_ms
local half_open = not active_open and open_count > 0

if outcome == "auth_failure" then
    auth_blocked = 1
    open_count = math.max(open_count, 1)
    open_until_ms = 0
    half_open_successes = 0
    redis.call("DEL", KEYS[2])
    save(
        window_started_at_ms,
        samples,
        failures,
        consecutive_failures,
        open_until_ms,
        open_count,
        half_open_successes,
        auth_blocked)
    return {1, samples, failures, consecutive_failures, 0, 4}
end

if auth_blocked == 1 then
    return {1, samples, failures, consecutive_failures, 0, 4}
end

if outcome == "rate_limited" then
    local duration_ms = normalized_retry_after_ms(retry_after_ms)
    if now_ms > max_safe_integer - duration_ms then
        return invalid()
    end
    if not active_open then
        if open_count >= max_counter then
            return invalid()
        end
        open_count = open_count + 1
    end
    half_open_successes = 0
    local candidate_until_ms = now_ms + duration_ms
    if candidate_until_ms > open_until_ms then
        open_until_ms = candidate_until_ms
        write_cooldown(
            "rate_limited",
            source_status,
            now_ms,
            open_until_ms)
    end
    save(
        window_started_at_ms,
        samples,
        failures,
        consecutive_failures,
        open_until_ms,
        open_count,
        half_open_successes,
        0)
    return {
        1,
        samples,
        failures,
        consecutive_failures,
        open_until_ms,
        3
    }
end

if active_open or half_open then
    return {
        current_state(now_ms, open_count, open_until_ms, auth_blocked),
        samples,
        failures,
        consecutive_failures,
        open_until_ms,
        strict_action(now_ms, open_count, open_until_ms, auth_blocked)
    }
end

if now_ms - window_started_at_ms >= window_ms then
    window_started_at_ms = now_ms
    samples = 0
    failures = 0
end
if samples >= max_counter then
    return invalid()
end
samples = samples + 1

local action = 1
if outcome == "success" then
    consecutive_failures = 0
else
    if failures >= max_counter or consecutive_failures >= max_counter then
        return invalid()
    end
    failures = failures + 1
    consecutive_failures = consecutive_failures + 1
    action = 2
    local threshold_open =
        consecutive_failures >= 5
        or (samples >= 10 and (failures * 2) >= samples)
    if threshold_open then
        open_count = 1
        half_open_successes = 0
        local duration_ms = open_duration_ms(
            open_count,
            jitter_basis_points)
        if now_ms > max_safe_integer - duration_ms then
            return invalid()
        end
        open_until_ms = now_ms + duration_ms
        write_cooldown(
            "transient_failure",
            source_status,
            now_ms,
            open_until_ms)
        action = 3
    end
end

save(
    window_started_at_ms,
    samples,
    failures,
    consecutive_failures,
    open_until_ms,
    open_count,
    half_open_successes,
    auth_blocked)

return {
    current_state(now_ms, open_count, open_until_ms, auth_blocked),
    samples,
    failures,
    consecutive_failures,
    open_until_ms,
    action
}
