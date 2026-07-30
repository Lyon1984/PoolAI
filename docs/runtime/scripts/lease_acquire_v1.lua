-- lease_acquire_v1
-- KEYS[1]: Account lease ZSET.
-- ARGV[1]: opaque owner token encoded as exactly 32 lowercase hexadecimal characters.
-- ARGV[2]: Account max_concurrency in the inclusive range 1..10000.
-- ARGV[3]: lease duration in milliseconds; R1 requires 60000.
-- ARGV[4]: key TTL in milliseconds; R1 requires 120000.
-- Returns [code, active_count, expires_at_ms, retry_after_ms].

local function valid_owner(owner)
    return owner ~= nil
        and string.len(owner) == 32
        and string.match(owner, "^[0-9a-f]+$") ~= nil
end

local function valid_integer(value, minimum, maximum)
    return value ~= nil
        and value >= minimum
        and value <= maximum
        and value == math.floor(value)
end

local function redis_now_ms()
    local redis_time = redis.call("TIME")
    local seconds = tonumber(redis_time[1])
    local microseconds = tonumber(redis_time[2])

    if not valid_integer(seconds, 0, 9007199254740)
        or not valid_integer(microseconds, 0, 999999) then
        return nil
    end

    return (seconds * 1000) + math.floor(microseconds / 1000)
end

if #KEYS ~= 1 or #ARGV ~= 4 or KEYS[1] == "" then
    return {-1, 0, 0, 0}
end

local owner = ARGV[1]
local limit = tonumber(ARGV[2])
local lease_ms = tonumber(ARGV[3])
local key_ttl_ms = tonumber(ARGV[4])

if not valid_owner(owner)
    or not valid_integer(limit, 1, 10000)
    or lease_ms ~= 60000
    or key_ttl_ms ~= 120000 then
    return {-1, 0, 0, 0}
end

local now_ms = redis_now_ms()
if now_ms == nil then
    return {-1, 0, 0, 0}
end

redis.call("ZREMRANGEBYSCORE", KEYS[1], "-inf", now_ms)

local expires_at_ms = now_ms + lease_ms
local existing_score = redis.call("ZSCORE", KEYS[1], owner)

if existing_score then
    redis.call("ZADD", KEYS[1], expires_at_ms, owner)
    local active_count = redis.call("ZCARD", KEYS[1])
    redis.call("PEXPIRE", KEYS[1], key_ttl_ms)
    return {2, active_count, expires_at_ms, 0}
end

local active_count = redis.call("ZCARD", KEYS[1])
if active_count < limit then
    redis.call("ZADD", KEYS[1], expires_at_ms, owner)
    active_count = active_count + 1
    redis.call("PEXPIRE", KEYS[1], key_ttl_ms)
    return {1, active_count, expires_at_ms, 0}
end

local earliest = redis.call("ZRANGE", KEYS[1], 0, 0, "WITHSCORES")
local minimum_score = nil
if #earliest == 2 then
    minimum_score = tonumber(earliest[2])
end

if minimum_score == nil or minimum_score <= now_ms then
    return {-1, 0, 0, 0}
end

redis.call("PEXPIRE", KEYS[1], key_ttl_ms)
return {0, active_count, 0, math.max(minimum_score - now_ms, 1)}
