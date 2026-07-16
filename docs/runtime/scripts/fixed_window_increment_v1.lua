-- fixed_window_increment_v1
-- KEYS[1], KEYS[2]: adjacent minute candidates with an identical key base/hash tag
-- ARGV[1]: positive integer limit
-- ARGV[2]: positive integer increment
-- ARGV[3]: key TTL in milliseconds; R1 requires 120000
-- Returns [allowed_or_error, current, limit, retry_after_ms].

local function positive_integer(value)
    return value ~= nil
        and value > 0
        and value <= 2147483647
        and value == math.floor(value)
end

if #KEYS ~= 2 or #ARGV ~= 3 then
    return {-1, 0, 0, 0}
end

local limit = tonumber(ARGV[1])
local increment = tonumber(ARGV[2])
local ttl_ms = tonumber(ARGV[3])

if not positive_integer(limit)
    or not positive_integer(increment)
    or ttl_ms ~= 120000 then
    return {-1, 0, 0, 0}
end

local key1_base, key1_epoch = string.match(KEYS[1], "^(.*):(%d+)$")
local key2_base, key2_epoch = string.match(KEYS[2], "^(.*):(%d+)$")

if key1_base == nil
    or key2_base == nil
    or key1_base ~= key2_base
    or string.match(key1_base, "{[^{}]+}") == nil then
    return {-1, 0, 0, 0}
end

local redis_time = redis.call("TIME")
local seconds = tonumber(redis_time[1])
local microseconds = tonumber(redis_time[2])

if seconds == nil or microseconds == nil then
    return {-1, 0, 0, 0}
end

local current_minute = math.floor(seconds / 60)
local key1_matches = tonumber(key1_epoch) == current_minute
local key2_matches = tonumber(key2_epoch) == current_minute

if key1_matches == key2_matches then
    return {-1, 0, 0, 0}
end

local selected_key = KEYS[2]
if key1_matches then
    selected_key = KEYS[1]
end

local existed = redis.call("EXISTS", selected_key)
local current = redis.call("INCRBY", selected_key, increment)

if existed == 0 then
    redis.call("PEXPIRE", selected_key, ttl_ms)
elseif redis.call("PTTL", selected_key) < 0 then
    -- Heal a legacy/corrupt counter without extending any existing positive TTL.
    redis.call("PEXPIRE", selected_key, ttl_ms)
end

if current <= limit then
    return {1, current, limit, 0}
end

local now_ms = (seconds * 1000) + math.floor(microseconds / 1000)
local next_minute_ms = (current_minute + 1) * 60000
local retry_after_ms = math.max(next_minute_ms - now_ms, 1)

return {0, current, limit, retry_after_ms}
