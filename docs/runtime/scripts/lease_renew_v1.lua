-- lease_renew_v1
-- KEYS[1]: Account lease ZSET.
-- ARGV[1]: opaque owner token encoded as exactly 32 lowercase hexadecimal characters.
-- ARGV[2]: lease duration in milliseconds; R1 requires 60000.
-- ARGV[3]: key TTL in milliseconds; R1 requires 120000.
-- Returns [code, expires_at_ms].

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

if #KEYS ~= 1 or #ARGV ~= 3 or KEYS[1] == "" then
    return {-1, 0}
end

local owner = ARGV[1]
local lease_ms = tonumber(ARGV[2])
local key_ttl_ms = tonumber(ARGV[3])

if not valid_owner(owner)
    or lease_ms ~= 60000
    or key_ttl_ms ~= 120000 then
    return {-1, 0}
end

local now_ms = redis_now_ms()
if now_ms == nil then
    return {-1, 0}
end

redis.call("ZREMRANGEBYSCORE", KEYS[1], "-inf", now_ms)

if not redis.call("ZSCORE", KEYS[1], owner) then
    return {0, 0}
end

local expires_at_ms = now_ms + lease_ms
redis.call("ZADD", KEYS[1], expires_at_ms, owner)
redis.call("PEXPIRE", KEYS[1], key_ttl_ms)

return {1, expires_at_ms}
