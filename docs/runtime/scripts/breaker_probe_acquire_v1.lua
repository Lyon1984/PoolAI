-- breaker_probe_acquire_v1
-- KEYS[1]: Account breaker HASH.
-- KEYS[2]: Account cooldown String JSON.
-- KEYS[3]: Account half-open probe String.
-- ARGV[1]: opaque owner token, exactly 32 lowercase hexadecimal characters.
-- ARGV[2]: probe TTL milliseconds; R1 requires 10000.
-- ARGV[3]: logical version; R1 requires 1.
-- Returns [-1,0], [0,retry_after_ms], or [1,probe_expires_at_ms].

local max_safe_integer = 9007199254740991
local max_counter = 2147483647

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

local function valid_owner(owner)
    return type(owner) == "string"
        and string.len(owner) == 32
        and string.match(owner, "^[0-9a-f]+$") ~= nil
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

if not valid_keys()
    or #ARGV ~= 3
    or not valid_owner(ARGV[1])
    or ARGV[2] ~= "10000"
    or ARGV[3] ~= "1" then
    return {-1, 0}
end

local breaker_type = key_type(KEYS[1])
local cooldown_type = key_type(KEYS[2])
local probe_type = key_type(KEYS[3])
if (breaker_type ~= "none" and breaker_type ~= "hash")
    or (cooldown_type ~= "none" and cooldown_type ~= "string")
    or (probe_type ~= "none" and probe_type ~= "string") then
    return {-1, 0}
end

local cooldown_ttl_ms = 0
if cooldown_type == "string" then
    cooldown_ttl_ms = redis.call("PTTL", KEYS[2])
    if cooldown_ttl_ms <= 0 then
        return {-1, 0}
    end
end
local probe_ttl_ms = 0
if probe_type == "string" then
    probe_ttl_ms = redis.call("PTTL", KEYS[3])
    if probe_ttl_ms <= 0
        or not valid_owner(redis.call("GET", KEYS[3])) then
        return {-1, 0}
    end
end

local now_ms = redis_now_ms()
if now_ms == nil then
    return {-1, 0}
end

local open_until_ms = 0
local open_count = 0
local auth_blocked = 0
if breaker_type == "hash" then
    if redis.call("HLEN", KEYS[1]) ~= 8 then
        return {-1, 0}
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
    local window_started_at_ms = parse_integer(
        values[1],
        0,
        max_safe_integer)
    local samples = parse_integer(values[2], 0, max_counter)
    local failures = parse_integer(values[3], 0, max_counter)
    local consecutive_failures = parse_integer(
        values[4],
        0,
        max_counter)
    open_until_ms = parse_integer(values[5], 0, max_safe_integer)
    open_count = parse_integer(values[6], 0, max_counter)
    local half_open_successes = parse_integer(values[7], 0, 1)
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
        return {-1, 0}
    end
elseif cooldown_type ~= "none" then
    return {-1, 0}
end

local retry_after_ms = math.max(cooldown_ttl_ms, probe_ttl_ms)
if open_until_ms > now_ms then
    retry_after_ms = math.max(
        retry_after_ms,
        open_until_ms - now_ms)
end
if retry_after_ms > 0 then
    return {0, retry_after_ms}
end
if breaker_type == "none"
    or open_count == 0
    or auth_blocked == 1 then
    return {0, 0}
end
if now_ms > max_safe_integer - 10000 then
    return {-1, 0}
end

local acquired = redis.call(
    "SET",
    KEYS[3],
    ARGV[1],
    "NX",
    "PX",
    ARGV[2])
if not acquired then
    return {-1, 0}
end
return {1, now_ms + 10000}
