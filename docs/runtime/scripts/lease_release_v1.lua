-- lease_release_v1
-- KEYS[1]: Account lease ZSET.
-- ARGV[1]: opaque owner token encoded as exactly 32 lowercase hexadecimal characters.
-- Returns [removed_count], or [-1] for invalid arguments.

local function valid_owner(owner)
    return owner ~= nil
        and string.len(owner) == 32
        and string.match(owner, "^[0-9a-f]+$") ~= nil
end

if #KEYS ~= 1
    or #ARGV ~= 1
    or KEYS[1] == ""
    or not valid_owner(ARGV[1]) then
    return {-1}
end

local removed_count = redis.call("ZREM", KEYS[1], ARGV[1])
if redis.call("ZCARD", KEYS[1]) == 0 then
    redis.call("DEL", KEYS[1])
end

return {removed_count}
