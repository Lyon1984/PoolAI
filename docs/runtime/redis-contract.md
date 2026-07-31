# Redis 运行时协调契约 v1

状态：Release 1 冻结  
目标：Redis 8，StackExchange.Redis  
前缀：`poolai:r1:<environment>:`

Redis 只承载短生命周期协调、限流、粘性和缓存，不是 Subscription、API Key、Account、Group quota、usage 或审计的业务真相。Group 的 `consumed + reserved + estimate <= total` 只能由 PostgreSQL 事务判定；Redis 中即使存在 quota snapshot，也不得参与放行。

## 1. 通用规则

- `<environment>` 只允许 `[a-z0-9-]{1,32}`，生产、预发和测试必须使用不同实例或至少不同前缀；尖括号表示文档占位符，不写入实际 key。
- ID 统一使用小写、带连字符的 UUID；会话信号先做 HMAC-SHA256，再取 32 个十六进制字符，不能把 prompt、邮箱、原始 API Key、JWT 或 Refresh Token 放进 key/value/log。
- Account lease 和 Group/登录 RPM 使用 Redis `TIME`，不得使用各节点本地时钟；PostgreSQL reservation/period、Subscription、API Key、一次性 Token 与 Refresh issued/expiry/rotation/revoke 使用数据库时钟，两类截止时间不能互相替代。
- 所有 Lua 通过 [`../release-manifest-v1.json`](../release-manifest-v1.json) 登记逻辑名称、逻辑版本、权威正文路径和正文 SHA-256，应用启动时加载；Redis 返回的 SHA-1 只是 content-addressed script cache 标识，不能替代清单完整性使用的 SHA-256。遇到 `NOSCRIPT` 只允许重新加载同逻辑版本、同 SHA-256 的正文一次。滚动升级期间新旧脚本的 key 结构和返回数组必须兼容，否则使用新 key version。
- M0 只建立连接、`TIME`、机器清单和 versioned script registry 测试基座，所以 M0 Exit 时 release manifest 的 `scripts` 是显式空数组；空数组不是省略登记规则。M1-E1 首次加入密码重置所需的 [`fixed_window_increment_v1.lua`](scripts/fixed_window_increment_v1.lua)；M2-E3 复用它交付 Group RPM 协调原语，并新增 Account lease acquire/renew/release 脚本；Gateway 仅从 M4-E1 起在鉴权后的模型 POST 路径调用 Group RPM 原语。后续 M2-E4 再交付 breaker 脚本。Lua 正文只能位于 `docs/runtime/scripts/`，且必须与本契约、release manifest 登记和测试原子提交。
- Api/Worker readiness 必须校验 Redis server major、`TIME` 返回、配置 key prefix 的 schema version，以及清单中每个脚本在所有可写 primary 上可按正文加载。不得写单一全局 manifest marker：共享 script cache 可以同时保存滚动版本的不同 SHA，额外 SHA 不构成不兼容。应用不得为 readiness 执行 `SCRIPT FLUSH`。
- Lua 返回固定数组，首项为整数 code，后续项不得因实现语言改变类型。任何非预期 code 都按协调层不可用处理并记录 script version。
- 禁止 `KEYS`、无界 `SCAN`、无 TTL 临时 key，以及依赖 Pub/Sub 才能保证正确性的设计。

## 2. Key 与 TTL

`{...}` 是 Redis Cluster hash tag；一个 Lua 脚本访问的所有 key 必须拥有相同 hash tag。

| 能力 | Key / 类型 | Value | TTL | 故障行为 |
|---|---|---|---|---|
| API Key 预筛快照 | `cache:api-key:v1:{digest32}` / String JSON | key_id、user_id、group_id、status、expires_at、version；无 secret | 60 s ± 10% jitter | digest32 是 API Key HMAC digest 前 32 hex；miss/error 或模型最终准入均强读 PostgreSQL |
| Subscription 快照 | `cache:subscription:v1:{user_id}:{group_id}` / String JSON | canonical subscription 状态、起止、version | 60 s ± 10% | miss/error 回源 PostgreSQL；PG 也失败则 fail-closed |
| Group 非配额策略快照 | `cache:group-policy:v1:{group_id}` / String JSON | enabled、model allowlist version、RPM；不含 quota counters | 30 s ± 10% | 回源 PostgreSQL；PG 也失败则 fail-closed |
| Group quota 展示快照 | `cache:group-usage:v1:{group_id}` / String JSON | 仅 `/v1/usage` 查询优化，含源 `updated_at` | 最长 15 s | 删除/失效后回源；绝不用于准入 |
| Account 并发 lease | `lease:account:v1:{account_id}` / ZSET | member=owner token，score=到期毫秒 | key 120 s；member lease 60 s | acquire/renew 异常 fail-closed；未取得不得调用上游 |
| Group RPM | `rate:group:v1:{group_id}:{minute_epoch}` / String integer | 当前 Redis 分钟内的模型 POST 计数 | 120 s | 仅 `/v1/responses`、`/v1/chat/completions` 进入计数；异常 fail-closed，超限返回 `429 group_rate_limited`；`/v1/models`、`/v1/usage` 不计数且不返回该错误 |
| 登录失败 | `rate:login:v1:{ip_hash}:{minute_epoch}` / String integer | 匿名 login 与 login-TOTP 共享的当前 Redis 分钟 IP 失败次数 | 120 s | limit=`Auth:Login:IpFailuresPerMinute`（默认 20）；超限返回 `429 rate_limit_exceeded`，需要计数/判定的失败路径异常返回 `503 coordination_unavailable`，不得继续认证写路径 |
| 密码重置 | `rate:password-reset:v1:{scope_hash}:{minute_epoch}` / String integer | 当前 Redis 分钟内按 IP 或账户族群区分的请求次数 | 120 s | anonymous forgot-password 对 IP 与规范化邮箱两个 scope 分别计数；任一超限返回 `429 rate_limit_exceeded`，异常返回 `503 coordination_unavailable`；存在/不存在/disabled User 使用相同路径 |
| 粘性路由 | `sticky:v1:{group_id}:{session_hash}` / String | account_id、group_policy_version、supply_configuration_version | 60 min，命中续期 | miss/error 或任一 version 不匹配时重新调度；仍必须强读 Supply Configuration 并取得 Account lease |
| Account 冷却 | `cooldown:account:v1:{account_id}` / String JSON | reason、retry_at、source_status | `retry_at-now`，上限 24 h | miss 使用 PostgreSQL 最近健康状态；不得绕过 Account lease |
| Account breaker 窗口 | `breaker:account:v1:{account_id}` / HASH | window_started_at_ms、samples、failures、consecutive_failures、open_until_ms、open_count、half_open_successes、auth_blocked | 每次有效写入后 48 h | 脚本异常 fail-closed；TTL 必须长于最长 24 h cooldown 并保留一整天调度余量；不得仅用进程内计数判定 breaker |
| Account half-open 单探针 | `breaker-probe:account:v1:{account_id}` / String | member=随机 128-bit owner token | 固定 10 s，不续期 | 只能通过 `breaker_probe_acquire_v1` 取得；异常 fail-closed |
| Access Token 撤销预筛快照 | `auth:token-version:v1:{user_id}` / String integer | 最近观察到的 token_version | 5 min | 只能提前拒绝；M1-E2 的每个 `UserJwt` 请求仍强读 PostgreSQL 验证 JWT `sid` family 有 active generation，M1-E3 再完整覆盖 User/status/role/token_version canonical 强读，PG 失败均 fail-closed |
| 缓存失效通知 | channel `poolai:r1:<environment>:invalidate:v1` | entity type、id、version | Pub/Sub 无持久 TTL | best effort；正确性依靠短 TTL 与数据库 version |

Refresh Token family 的 HMAC hash、generation、轮换和撤销事实保存在 PostgreSQL `refresh_sessions`；发行/过期/轮换/撤销均使用 PostgreSQL clock。每次成功轮换保持 family_id，新 generation 从本次数据库时间重新获得 30 天有效期。Redis 只缓存 `token_version`，不缓存 family 存活真相；因此 Redis 数据丢失不会恢复已撤销会话。

API Key、Subscription 与 Group policy 的正缓存只用于尽早拒绝和减少非安全查询；每个入站模型请求及每个新 failover attempt 在同 Group Account 调度/lease/reservation 前必须按 canonical ID 强读 PostgreSQL API Key、User、Subscription、Group，以及 Supply 所有的 `group_supply_configurations`、configured Channel、`group_accounts`、Account lifecycle/health。请求所选 Channel 必须等于当前 configured Channel。每个 JWT 认证的控制面请求也必须强读 User status、role 和 token_version。管理员 revoke/disable/角色变更或 Supply Configuration mutation 的事务提交是线性化点：此前已完成准入的当前 attempt 可按规则结算，提交后的新请求不得因旧 Redis 正缓存、旧粘性记录或 Group activation evidence 继续放行。Supply mutation 不更新 Group version/ETag，也不自动禁用 Group。

## 3. Lease Lua v1

R1 只对 Account 使用该 lease 算法，不创建 User lease。owner token 是服务端生成的 16 字节 CSPRNG 随机值，以恰好 32 个小写十六进制字符编码后作为 opaque ZSET member，只在当前 attempt 上下文保存；它不是 UUID、用户/节点标识或可复用 capability。Account 并发上限来自 PostgreSQL Account 版本化配置 `max_concurrency`（`1..10000`），它是供应容量，不是累计 Token 配额。

### 3.1 [`lease_acquire_v1`](scripts/lease_acquire_v1.lua)

输入：

- `KEYS[1]`：lease ZSET；
- `ARGV[1]`：上述 32 位小写十六进制 owner token；
- `ARGV[2]`：正整数 limit，必须为 `1..10000`；
- `ARGV[3]`：lease_ms，Release 1 固定 60000；
- `ARGV[4]`：key_ttl_ms，固定 120000。

原子算法：用 Redis `TIME` 计算 `now_ms`；删除 score `<= now_ms` 的 member；若 owner 已存在则把 score 更新为 `now_ms + lease_ms` 并返回幂等成功；否则在 `ZCARD < limit` 时加入 owner；最后 `PEXPIRE`。

返回：

- `[1, active_count, expires_at_ms, 0]`：新取得；
- `[2, active_count, expires_at_ms, 0]`：同 owner 幂等取得/续期；
- `[0, active_count, 0, retry_after_ms]`：容量已满，`retry_after_ms=max(min_score-now_ms,1)`；
- `[-1, 0, 0, 0]`：参数不合法。

应用对 code 0 可在释放数据库事务后按调度策略等待，但不能持有 Group reservation 等待 Account lease；达到调用总 deadline 后返回 `503 account_capacity_unavailable`。

### 3.2 [`lease_renew_v1`](scripts/lease_renew_v1.lua)

输入为 lease key、owner、lease_ms、key_ttl_ms。owner 编码、`lease_ms=60000` 与 `key_ttl_ms=120000` 必须精确匹配；先以 Redis `TIME` 清理过期 member。owner 存在时更新 score、重置 key TTL 并返回 `[1, expires_at_ms]`，不存在返回 `[0, 0]`，参数不合法返回 `[-1, 0]`。活跃请求每 20 秒续租；连续两次失败或返回 0 时取消上游请求，进入最长 15 秒 drain，并按已知 usage/保守估算结算 PostgreSQL reservation。

### 3.3 [`lease_release_v1`](scripts/lease_release_v1.lua)

输入为 lease key、owner。owner 编码不合法返回 `[-1]`；合法时 `ZREM` 后若集合为空则 `DEL`，返回 `[removed_count]`。重复 release 返回 `[0]` 且视为成功，不能释放其他 owner。

三份脚本的参数数量必须精确匹配，返回数组长度与整数类型固定。Acquire 的 code `0` 是容量拒绝而不是成功；调用方只能在释放任何数据库事务、且尚未创建 Group reservation 时按正 `retry_after_ms` 调度等待。参数错误 `-1`、Redis 命令错误、超时/断开、`NOSCRIPT` 同正文重载一次后仍失败、错误数组长度/类型或未知 code 均返回 `503 coordination_unavailable + Retry-After: 1`，不得本地计数、超卖放行或调用上游。

## 4. Rate Limit Lua v1

### `fixed_window_increment_v1`

权威正文是 [`scripts/fixed_window_increment_v1.lua`](scripts/fixed_window_increment_v1.lua)。输入：`KEYS[1]` 与 `KEYS[2]` 分别是调用方按最近一次 Redis `TIME` 计算的当前分钟、下一分钟候选 key；两者除尾部 `minute_epoch` 外必须完全相同，并使用同一 `{scope_id}` hash tag。`ARGV[1]=limit`、`ARGV[2]=increment`、`ARGV[3]=ttl_ms`，R1 固定 `ttl_ms=120000`，limit/increment 必须为正整数。

脚本再次读取 Redis `TIME`，以其秒值计算 `floor(seconds/60)`，只选择尾部 epoch 与服务器当前分钟相符的一个 key；候选基部不一致、两个/零个候选匹配或参数不合法都返回 `[-1,0,0,0]` 且不写 key。选中后原子执行 `INCRBY`；首次写入设置 `PEXPIRE 120000`，若发现遗留 key 没有 TTL 则补设相同 TTL，但已有正 TTL 不续期。正常返回 `[allowed,current,limit,retry_after_ms]`：`current <= limit` 时 `[1,current,limit,0]`，否则 `[0,current,limit,距离下个 Redis 分钟边界的正毫秒数]`。超限计数仍保留，防止边界内反复重试绕过；任何未知数组形状/code 都按协调不可用处理。

M2-E3 交付并验证 Group RPM 的 Redis 协调原语（既有 `fixed_window_increment_v1` 正文、版本登记、key/`TIME`/返回解析与 fail-closed 边界），但不提前接入尚未实现的 Gateway。M4-E1 才在通过 API Key 鉴权的 `/v1/responses` 和 `/v1/chat/completions` 模型 POST 进入 Gateway Process Manager 时调用该原语并计数，流式/非流一视同仁；`/v1/models` 归 NonStream bulkhead、`/v1/usage` 归 Usage bulkhead，两个查询端点均不调用 RPM 脚本。Group RPM 的 key minute_epoch 必须由同一次 Redis `TIME` 结果构造。为避免客户端先取时间再执行脚本产生跨分钟竞争，应用传入两个候选 key（当前分钟与下一分钟），脚本再次读取 Redis `TIME` 并只操作匹配的一个；两个 key 使用相同 `{group_id}` hash tag。

登录的 `ip_hash` 取 `HMAC-SHA256(Auth:Login:RateLimitScopePepper, "ip" || 0x00 || normalized_ip_bytes)` 前 32 个小写 hex；IP 必须先规范化为网络地址 bytes，Redis key/value、日志和 metrics 都不得出现原始 IP。该 HMAC pepper 只服务 Login IP scope，不得复用 password-reset scope、Refresh/one-time token、TOTP 恢复码、API Key 或 JWT 密钥。

匿名 `POST /api/v1/auth/login` 的未知邮箱/错误密码/锁定期错误密码，以及 `POST /api/v1/auth/totp/verify` 的 challenge/TOTP 失败，均在返回认证错误前调用同一 `fixed_window_increment_v1`，limit 取 `Auth:Login:IpFailuresPerMinute`（默认 20），`ttl_ms=120000`；两个端点对同一 IP 共享 `rate:login:v1` 计数。正确密码/TOTP 不增加该失败计数，TOTP 错误也不增加 PostgreSQL `failed_login_count`。任一计数返回 0 时用 `ceil(retry_after_ms/1000)` 生成 `Retry-After` 和 `429 rate_limit_exceeded`；Redis 超时、断开、`NOSCRIPT` 重载仍失败或返回 -1/未知结果时返回 `503 coordination_unavailable + Retry-After: 1`，不得回退为进程内计数或继续认证写路径。

PostgreSQL 账户锁定与 Redis IP fixed-window 是两份不同事实：前者仅对已知 active User 的错误密码按 PostgreSQL clock 累加；未知用户无数据库锁定行。Redis 允许记录失败后，已知与未知账户都必须进入同一 UoW、执行同一条条件 password-failure SQL 并追加同形审计；未知账户使用不可能由 UUIDv7 User 生成器产生的保留 id/security-stamp 使该 SQL 零更新，不能省略数据库往返。锁定期内错误密码仍返回 `invalid_credentials`，且不延长 `locked_until`/改写失败数；只有密码正确才返回 `account_locked + Retry-After`。锁定到期后当前尝试先把旧失败数视为 0：正确密码清空，错误密码从 1 重新累计。登录限流可在后续增加指数退避，但不能把用户是否存在暴露为不同错误或时延。

密码重置的 `scope_hash` 取 `HMAC-SHA256(Auth:PasswordReset:RateLimitScopePepper, scope_type || 0x00 || canonical_scope)` 前 32 个小写 hex；`scope_type` 只能是 `ip` 或 `account`，IP 使用规范化网络地址 bytes，account 对输入邮箱做与 User 查找完全相同的规范化后再计算，因此不存在、active 和 disabled User 都生成同一种账户 scope，Redis 中不出现原始 IP/邮箱。该 HMAC pepper 只服务密码重置限流，不能复用 Login rate-scope、一次性 token、Refresh Token、TOTP 恢复码、API Key 或 JWT 密钥。

匿名 forgot-password 必须分别调用一次脚本计入 IP scope 和 account scope，limit 依次取 `Auth:PasswordReset:IpRequestsPerMinute` 与 `Auth:PasswordReset:AccountRequestsPerMinute`；两个调用都通过才进入数据库路径。两个 scope 位于不同 Cluster slot，不承诺跨 scope 原子性：若第二次拒绝或失败，第一次已消费的计数保留，这是有意的 fail-closed 行为。任一返回 0 时使用 `ceil(retry_after_ms/1000)` 生成 `Retry-After` 和 `429 rate_limit_exceeded`；Redis 超时、断开、`NOSCRIPT` 重载仍失败或返回 -1/未知结果时返回 `503 coordination_unavailable + Retry-After: 1`，不得回退为进程内计数或继续创建 token/outbox。Admin 代理端点仍受 Control bulkhead 和通用控制面限流；它解析目标 User 后按其规范化邮箱使用同一 account scope，但不使用匿名防枚举响应。

## 5. 缓存、失效与版本

- 数据库事务提交成功后写 outbox；Worker 发布 `{entity_type,id,version}` 失效事件。订阅者只删除本地/Redis 旧版本，不把事件 payload 当新真相。
- 写路径在提交后可以 best-effort 删除对应 cache；删除失败不回滚数据库事务，最迟由 TTL 收敛。
- cache JSON 顶层固定 `{schema_version, entity_version, cached_at, data}`；未知 schema_version 视为 miss。
- 使用带随机抖动的 TTL 防止同一 Group 大量 key 同时到期；negative cache 仅用于“确实不存在”，TTL 10 秒，禁用/撤销状态不得做长时间 negative cache。
- Pub/Sub 断连、重复和乱序均是正常情况；只有 entity_version 更大的通知才触发动作。

## 6. Worker 单 owner：PostgreSQL session advisory lock

R1 不使用 Redis Worker leader key 或 fencing token。reservation sweeper、outbox publisher、usage aggregator/rebuild、email outbox sender、Supply health 和 Operations 告警每类 job 都使用独立 PostgreSQL session advisory lock：

- lock ID 由版本化常量名使用 SHA-256 的前 8 bytes 按固定字节序生成有符号 `bigint`，禁止使用进程随机化的 `string.GetHashCode()`；
- Worker 使用专用 NpgsqlConnection 调用 `pg_try_advisory_lock(lock_id)`，并在整轮任务期间保持同一物理连接；该连接不得归还连接池后继续执行；
- 未取得 lock 则跳过本轮；连接断开、lock 丢失或 PostgreSQL 不可用时立即停止本轮并告警，不切换到 Redis leader；
- 任务仍必须用行级 CAS、唯一约束、checkpoint/outbox 幂等和有界重试保证副作用安全；advisory lock 不替代数据库幂等；
- 任务结束在 `finally` 中尝试 `pg_advisory_unlock`并关闭专用连接；重复 unlock 只记录诊断，不重放副作用。

## 7. Account Circuit Breaker 与进程级 Bulkhead

### 7.1 Breaker 状态与 Account health 映射

Circuit breaker 是 Account 级共享运行时策略，不代替 PostgreSQL 中的 lifecycle/health 事实，也不改变 Group quota 语义。每个新普通 attempt 仍先强读 persistent Group Supply Configuration、configured Channel、binding、Account lifecycle/health，再读 Redis breaker：

| Breaker 状态 | Account health 映射 | 调度行为 |
|---|---|---|
| `closed` | 只允许 `healthy` / `degraded` | 可参与普通调度；`degraded` 降权，仍必须取得 Account lease |
| `open` | 短暂故障为 `cooling`；凭据失效为 `unhealthy` | `open_until_ms` 前完全排除，不排队、不取 lease、不 reserve |
| `half_open` | cooling 到期后为 `unknown` | 全集群只允许一个持有 probe key 的请求；其他请求仍排除该 Account |

新建 Account 的 `unknown` 不是 breaker half-open；它只能由 Supply 主动健康探测转为 `healthy/degraded`。只有 `open_count > 0`、短暂 cooling 已到期且持久 lifecycle 仍为 active 的 Account 可走 half-open 探针专用路径；该路径是对普通 `health=healthy/degraded` 资格过滤的唯一例外，且仍强读 lifecycle/binding。`401/403` 凭据失效会直接写 `unhealthy + auth_blocked=1`，不自动进入 half-open；只有替换凭据、管理员重新启用或明确的受控健康验证才能解除。

### 7.2 故障分类、窗口与开启阈值

`breaker_record_v1` 使用 Redis `TIME` 和 30 秒固定采样窗口，所有 API/Worker 实例共享同一 Account 计数。窗口内至少 10 个 eligible sample 且失败率 `>= 50%`，或连续 5 个 transient failure，立即打开。首次 open 基准为 30 秒，连续 reopen 按 `min(30 * 2^(open_count-1), 300)` 秒增长并施加有界 jitter，最终值不小于 1 秒、不大于 300 秒。普通 success 会清零 `consecutive_failures`；窗口换代只清零 samples/failures，不隐式清除连续失败。

| 上游结果 | Breaker 动作 | 与 retry/failover 的关系 |
|---|---|---|
| 正常完成且协议可验证 | 记录 success，重置连续失败 | 不触发重试 |
| DNS/TCP/TLS 建连失败、上游 5xx、首包超时、协议解析失败、流中断/空闲超时 | 记录 transient failure，按阈值打开 | 即使已有业务输出、不可重试，仍必须记录 breaker 失败；重试权由错误目录的 phase 矩阵决定 |
| 上游 `429` | 立即 open/cooling；有效 `Retry-After` 限制在 1 s..24 h，缺失/非法则 30 s | 状态码与“未提交下游 Header”本身不授权重放；只有 Adapter capability 或 transport 证据证明上游明确未执行，或有可验证幂等保证，才可按 phase 矩阵同 Group failover |
| 上游 `401/403` | 立即 `auth_blocked=1`，Account health 写 `unhealthy` | 当前 Account 不再自动探测；状态码与“未提交下游 Header”本身不授权重放，仅有可审计的零字节/明确未执行证据或可验证幂等保证时可切换同 Group Account |
| 客户端取消、入站 4xx、Group quota/RPM 拒绝、本地 bulkhead 拒绝 | ignored，不计入 Account breaker | 按各自稳定错误返回 |

Breaker 记录与 retry 是两个正交决策：“计入上游健康失败”不等于“当前请求可重放”。具体是否 failover 只读取错误目录的 phase/evidence 矩阵，Redis breaker 不得独立给出重放许可。
同理，`usage_source=confirmed_no_execution` 是 dispatch fence 已提交后，Adapter/transport 以可审计证据证明零请求字节或上游明确未执行时的 Group quota 结算来源；它使 attempt 以 total=0、`is_estimated=false` settle，不会把 breaker failure 改成 success，也绝不允许把 reservation 终态改为 released。

### 7.3 Breaker Lua v1 与 half-open 单探针

以下 ABI 是逻辑版本 1 的完整协议。Lua 正文、Operations 固定端口和测试不得自行增加可选参数、缩短数组或重新解释整数 code。

#### 7.3.1 通用编码、HASH 完整性与状态 code

- 三个脚本的所有 key 必须非空且共用同一个非空 `{account_id}` hash tag；应用侧 key guard 还必须分别验证 `breaker:account:v1:`、`cooldown:account:v1:` 与 `breaker-probe:account:v1:` 的完整前缀/类型。key 数量、参数数量、hash tag 或逻辑版本不精确匹配时返回该脚本的 `-1` 固定错误数组且不写 Redis。
- owner token 恰为 32 个小写十六进制字符，由 16-byte CSPRNG 生成。所有整数参数使用无空白、正号、小数点或指数的十进制编码；`0` 只能编码为 `"0"`，正数不能有前导零，最大值为 `9007199254740991`。`logical_version` 必须逐字节等于 `"1"`。
- breaker HASH 要么不存在，要么恰有且只有以下八个字段；缺失、额外、非整数、负数、`failures > samples`、`half_open_successes > 1`、`auth_blocked` 非 `0/1`、`open_count=0` 却存在 auth/open/half-open 信息，或时间超过安全整数范围都视为不兼容并返回 `-1`：

  | 字段 | 范围与含义 |
  |---|---|
  | `window_started_at_ms` | `0..9007199254740991`；当前 closed 固定窗口的 Redis epoch milliseconds |
  | `samples` | `0..2147483647`；本窗口内仅 `success/transient_failure` 的样本数 |
  | `failures` | `0..samples`；本窗口内 `transient_failure` 数 |
  | `consecutive_failures` | `0..2147483647`；跨窗口保留、由 closed success 清零的连续 transient 数 |
  | `open_until_ms` | `0..9007199254740991`；auth-blocked/closed 使用 0，瞬态 open 使用绝对 Redis epoch milliseconds |
  | `open_count` | `0..2147483647`；每次从 closed/half-open 进入瞬态 open 至多加一，成功关闭时归零 |
  | `half_open_successes` | `0/1`；第一个已完成的 owner success |
  | `auth_blocked` | `0/1`；1 时不因时间经过进入 half-open |

- `state_code` 固定为：`-1=invalid_or_incompatible`、`0=closed`、`1=open`、`2=half_open`。HASH 不存在或 `open_count=0/auth_blocked=0` 为 closed；`auth_blocked=1` 为 open；否则 `open_until_ms > Redis now` 为 open，`open_until_ms <= Redis now` 为 half-open。
- `action_code` 是 Supply health writer 应尝试持久化的目标，而不是脚本替 PostgreSQL 完成的写入：`0=none`、`1=healthy`、`2=degraded`、`3=cooling`、`4=unhealthy`、`5=unknown`。writer 必须以观察到的 Account version/credential revision 和时间做 stale/no-change 判定；同状态重复 observation 不制造 version/audit 风暴。
- 返回数组的每一个元素都必须是 RESP Integer；即使文本可解析为相同十进制数，RESP Bulk String/Simple String、浮点或其他类型也一律视为不兼容。调用方还必须校验本节列出的完整 tuple 语义，不能只接受已知首项 code。
- 三个脚本只使用 Redis `TIME` 生成 `now_ms`。写入或有效完成后 breaker HASH 的 TTL 精确重置为 `172800000 ms`（48 h），必须长于最大 24 h cooldown 并保留至少 24 h 调度余量；只读拒绝、ignored、stale/non-owner complete 不续期。任何整数加法将超过上述范围时返回错误数组且不部分写入。

#### 7.3.2 `breaker_record_v1`

输入必须精确为：

| 位置 | 值 |
|---|---|
| `KEYS[1]` | breaker HASH |
| `KEYS[2]` | cooldown String |
| `KEYS[3]` | half-open probe String；仅 `controlled_active success` 原子删除，其他 record 路径不修改 |
| `ARGV[1]` | `outcome`：`success/transient_failure/rate_limited/auth_failure/ignored` |
| `ARGV[2]` | `retry_after_ms`：HTTP Retry-After 的规范化正时长；`0` 表示缺失/非法 |
| `ARGV[3]` | `jitter_basis_points`：`0..1000`，即指数 cooldown 的 `0..10%` 非负 jitter |
| `ARGV[4]` | `source_status`：无 HTTP status 时为 `0`，否则为三位 HTTP status |
| `ARGV[5]` | `observation_mode`：`passive` 或 `controlled_active` |
| `ARGV[6]` | `logical_version`：精确 `"1"` |

参数组合还必须满足：

- `success`：`retry_after_ms=0`、`jitter_basis_points=0`、`source_status=200..299`；
- `transient_failure`：`retry_after_ms=0`、`jitter_basis_points=0..1000`，`source_status` 只可为 `0`、`200..399`、`408` 或 `500..599`；其中 2xx 表示响应体/协议验证失败，3xx 表示禁止的 redirect；
- `rate_limited`：`source_status=429`、`jitter_basis_points=0`，`retry_after_ms` 可为 `0..9007199254740991`；
- `auth_failure`：`source_status=401/403`，retry/jitter 均为 0；
- `ignored`：retry/jitter 均为 0，status 只可为 0 或除 `401/403/408/429` 外的 `400..499`。

不匹配返回 `[-1,0,0,0,0,0]`。正常返回始终为六个整数：

```text
[state_code, samples, failures, consecutive_failures, open_until_ms, action_code]
```

精确转换规则如下：

1. `ignored` 不创建、不修改、不删除 key，也不续 TTL；HASH 不存在返回 `[0,0,0,0,0,0]`，存在时只验证并返回当前 state/counters、`action_code=0`。closed HASH（`open_count=0`）与仍存活的 cooldown 是不兼容状态，必须返回固定错误数组且不得把持久 health 放宽。
2. closed 状态仅把 `success/transient_failure` 计入 30 秒固定窗口。`now_ms - window_started_at_ms >= 30000` 时先清零 `samples/failures` 并以 `now_ms` 开新窗口，但保留 `consecutive_failures`；`now_ms` 早于已存窗口起点视为不兼容。success 使 samples 加一并把 consecutive 清零，返回 closed + `healthy`。transient 使 samples/failures/consecutive 各按规则加一；未达阈值返回 closed + `degraded`。
3. 更新后的 `samples >= 10 && failures * 2 >= samples`，或 `consecutive_failures >= 5` 时，只从 closed 打开一次：`open_count` 从 0 变为 1、`half_open_successes=0`，计算瞬态 cooldown 并返回 open + `cooling`。达到阈值后的其他迟到 observation 不能在当前 open 周期重复增加 `open_count`。
4. 瞬态 reopen 基准为 `min(30000 * 2^(open_count-1),300000)`；实现必须在乘法前封顶，不以浮点溢出决定结果。最终时长为 `min(base_ms + floor(base_ms * jitter_basis_points / 10000),300000)` 且不少于 1000 ms。只有从 half-open 因 transient failure 重新打开时把 `open_count` 加一；仍在 `open_until_ms` 前的迟到 transient 不延长或重复 reopen。
5. `rate_limited` 从 closed/half-open 立即进入 open；`retry_after_ms=0` 使用 30000 ms，正值限制到 `1000..86400000 ms`，不加 jitter。已处于瞬态 open 时不增加 `open_count`，但把 deadline 更新为 `max(existing_open_until_ms, now_ms + bounded_retry_after_ms)`。
6. `auth_failure` 从任一非 auth 状态设置 `auth_blocked=1`、`open_count=max(open_count,1)`、`open_until_ms=0`、`half_open_successes=0`，删除 cooldown 并返回 open + `unhealthy`。重复 auth 保持该状态。时间经过和后续 passive success 都不能清除此标志。
7. `controlled_active success` 只授权给 ADR 0011 中 canonical health=`unknown`、`last_health_at IS NULL` 的新建/凭据替换版本，并且调用方必须携带已验证的 Account version 与 credential revision 进入条件 health writer；它在同一 Lua 原子步骤删除属于旧凭据版本的 breaker、cooldown 和 probe owner，避免旧 owner 在后续新 breaker generation 上完成（generation ABA），并返回 `[0,0,0,0,0,1]`。若条件 writer 随后判为 stale/retired，persistent `unknown` 仍排除普通路由，下一轮必须重新验证新版本，不能沿用本次 success。例行 healthy/degraded 主动检查只使用无重置权限的 `passive` breaker 语义；瞬态 half-open 只能由 owner complete。其他 `passive success` 在任何 open/auth/half-open 状态都不能关闭或降低严格度。
8. open/half-open 下除上述明确转换外的迟到 observation 不放宽状态。返回 action 必须维持严格映射：瞬态 open 为 cooling、auth open 为 unhealthy、half-open 为 unknown。探针 success/failure 只能由 `breaker_probe_complete_v1` 完成。

瞬态或 rate-limit 打开/延长时 cooldown value 必须是无空白、字段顺序固定、数值为十进制整数的 canonical JSON：

```json
{"reason":"transient_failure","retry_at":1234567890,"source_status":0}
```

`reason` 只能为 `transient_failure/rate_limited`；`retry_at` 必须精确等于本次生效的 `open_until_ms`；`source_status` 等于该生效 observation 的规范化 status。TTL 为 `max(retry_at-now_ms,1)`，瞬态最长 300000 ms、rate-limit 最长 86400000 ms；不得留下无 TTL cooldown。未延长既有 open 的迟到 observation 不重写既有 reason/status。caller 对 delta-seconds 与 IMF-fixdate Retry-After 都先做严格语法验证；HTTP-date 的时长以协调 adapter 最近一次 Redis `TIME` 为参考，最终 deadline 仍由脚本再次读取的 Redis `TIME` 锚定。

#### 7.3.3 `breaker_probe_acquire_v1`

输入必须精确为 `KEYS[1..3]=breaker,cooldown,probe`，
`ARGV[1]=owner`、`ARGV[2]="10000"`、`ARGV[3]="1"`。返回固定两个整数：

- `[-1,0]`：参数/key/hash 不兼容，cooldown/probe 存在但没有正 TTL，或 Redis 时间/类型异常；
- `[0,retry_after_ms]`：未取得。先检查 cooldown/probe：任一仍有正 TTL/deadline 时，retry 为相关正剩余时间的最大值且至少 1；仅在不存在活跃 cooldown/probe 时，HASH 不存在、closed 或 auth-blocked 才返回 retry=0；
- `[1,probe_expires_at_ms]`：breaker 存在、`open_count>0`、`auth_blocked=0`、`open_until_ms<=now_ms`、cooldown 已不存在/到期，且原子 `SET probe owner NX PX 10000` 成功。

相同 owner 重试 acquire 也不续租，只返回当前 probe 的正剩余 TTL。acquire 不修改 breaker fields/TTL；成功后调用方把 persistent health 收敛为 `unknown`，再按 canonical lifecycle/binding 重检取得 Account lease。新建但没有 `open_count` 的 unknown Account 永远不能通过本脚本。

#### 7.3.4 `breaker_probe_complete_v1`

输入必须精确为：

| 位置 | 值 |
|---|---|
| `KEYS[1..3]` | breaker、cooldown、probe |
| `ARGV[1]` | 当前 owner token |
| `ARGV[2]` | `success/transient_failure/rate_limited/auth_failure` |
| `ARGV[3]` | 与 record 相同语义的 `retry_after_ms` |
| `ARGV[4]` | 与 record 相同语义的 `jitter_basis_points` |
| `ARGV[5]` | 与 record 相同约束的 `source_status` |
| `ARGV[6]` | 精确 `"1"` |

outcome 的 retry/jitter/status 组合与 record 对应 outcome 完全相同；`ignored` 不能 complete，调用方取消或无法归责时让 10 秒 probe 自然到期。返回始终为五个整数：

```text
[completion_code, state_code, half_open_successes, open_until_ms, action_code]
```

- `[-1,0,0,0,0]`：参数/key/HASH/Redis 时间不兼容；
- `[0,0,0,0,0]`：probe 已不存在、owner 不匹配，或 owner 仍匹配但 breaker 已被并发 record 改成 closed/open/auth 而不再满足 half-open。该结果不得改变 breaker、cooldown 或 probe，迟到 success 不能覆盖较新的 failure；
- `[1,2,1,0,5]`：当前 owner 的第 1 次 success。原子设 `half_open_successes=1/open_until_ms=0`、删除 cooldown 和 probe、重置 HASH TTL；仍为 half-open/unknown；
- `[1,0,0,0,1]`：另一轮重新 acquire 后的第 2 次连续 success。原子删除 breaker、cooldown、probe，表示 closed/healthy；
- `[1,1,0,reopen_until_ms,3]`：当前 owner transient/rate-limit failure。原子清零 half-open success/window counters，按 record 的指数或 Retry-After 规则增加一次 `open_count` 并 reopen，写 canonical cooldown、删除 probe；
- `[1,1,0,0,4]`：当前 owner auth failure。原子设置 auth-blocked、删除 cooldown/probe，持久目标为 unhealthy。

owner 匹配后仍必须再次确认 `open_count>0`、`auth_blocked=0`、`open_until_ms<=now_ms` 且 cooldown 已到期，才能应用 completion；这是防止 probe 运行期间另一个在途 passive failure 已重新打开 breaker 后，迟到 success 错误关闭它的 fencing 条件。

#### 7.3.5 调用边界

探针仍必须获取 Account lease。ADR 0011 的 Supply Health Worker 使用无请求体、非生成式的受控 `/models` 验证，不创建 Group reservation/usage；若未来以真实模型请求作 probe，则必须走完同 Group canonical read、lease、Group reservation、attempt、dispatch fence 和核销，不存在“免配额健康请求”。

任一脚本超时、命令错误、未知 code、错误数组长度/类型、Redis 断开、`NOSCRIPT` 同正文重载一次后仍失败、HASH/cooldown/probe 不兼容或 probe 所有权不可验证均 fail-closed，不降级为本地 breaker。普通调度不得在不读取 breaker 的情况下仅凭 PostgreSQL healthy/degraded 放行。

### 7.4 API 进程级 Bulkhead

Bulkhead 使用 ASP.NET Core 进程内 `ConcurrencyLimiter`，不写 Redis；它保护每个 API 副本，Redis Account lease 继续保护跨副本上游容量。R1 参考实例（4 vCPU / 8 GiB）冻结为四个真正独立的 policy：

| 分区 | 路由 | 并发令牌 | 队列 | 拒绝 |
|---|---|---:|---|---|
| NonStream | `/v1/responses`、`/v1/chat/completions` 的 `stream!=true`，以及 `/v1/models` | 200 | 0（零队列） | `429 gateway_overloaded`，`Retry-After: 1` |
| SSE | `/v1/responses`、`/v1/chat/completions` 的 `stream=true` | 600 | 0（零队列） | `429 gateway_overloaded`，`Retry-After: 1`；尚未提交 SSE Header |
| Control | `/api/v1/*` | 100 | FIFO 最多 50，等待受请求 deadline/cancellation 约束 | 队列满或等待预算/deadline 耗尽时 `429 gateway_overloaded`，`Retry-After: 1`；使用 ControlPlaneProblem |
| Usage | `/v1/usage` | 100 | FIFO 最多 20，等待受请求 deadline/cancellation 约束 | 队列满或等待预算/deadline 耗尽时 `429 gateway_overloaded`，`Retry-After: 1`；使用 GatewayProblem |

每个请求只获取与自己路由/模式对应的一个 policy 令牌：SSE 绝不同时占用 NonStream permit，`/v1/usage` 绝不占用 NonStream 或 SSE permit，Control 也不与其它三者共享 semaphore/队列。Bulkhead 必须在 PostgreSQL canonical 强读、Account lease 和 Group reservation 之前取得；拒绝时不得创建 request/attempt/reservation，也不计入 Account breaker。所有成功、错误、取消、SSE 断连和异常路径均在 `finally` 释放令牌。

客户端 `RequestAborted` 只取消排队/执行并释放已持有的本地 permit，不得伪造 `gateway_overloaded` 响应；只有服务端 bulkhead 已满或其等待预算/deadline 耗尽才返回上述 429。

发布后只能根据第 10 节负载证据修改令牌数；NonStream/SSE 队列在 R1 始终为 0，Control/Usage 队列必须分别不超过 50/20。指标只以 `nonstream/sse/control/usage + result` 为 label，不使用 user/group/account ID。

## 8. Fail-open / fail-closed 矩阵

| 路径 | Redis 不可用 | PostgreSQL 不可用 | 说明 |
|---|---|---|---|
| API Key、Subscription、Group policy | 回源 PostgreSQL | `503 dependency_unavailable` | 访问资格 fail-closed |
| Group 累计配额 | 配额真相不受影响，但模型请求仍需 Account lease/RPM | `503 dependency_unavailable` | PostgreSQL 是唯一配额真相 |
| Account lease、Group RPM、登录/密码重置限流 | `503 coordination_unavailable` | 按各自数据依赖处理 | 不允许单节点失控放量；密码重置不得在 Redis 失败后创建 token/outbox；R1 无 User lease |
| Account breaker/probe | `503 coordination_unavailable` 且不调用上游 | 强读 health 失败则 `503 dependency_unavailable` | 不降级为进程内计数或多探针 |
| API 进程 bulkhead | 仍按本地令牌判定 | 仍按本地令牌判定 | 与依赖故障正交；饱和时先返回 `429 gateway_overloaded + Retry-After: 1` |
| 粘性和健康缓存 | 忽略缓存并重新计算/回源 | 取得硬 lease 后仍需真实候选；无候选则失败 | 只影响选路质量 |
| `/v1/usage` 展示缓存 | 直接查 PostgreSQL | `503 dependency_unavailable` | 耗尽时仍可查；依赖故障例外 |
| 失效 Pub/Sub | 等待 TTL 收敛 | 不适用 | best effort |
| Worker 单 owner | 不受 Redis 影响 | 不执行该轮数据库任务 | 使用第 6 节 PostgreSQL session advisory lock |

`coordination_unavailable` 统一为 503、`retryable=true`、`Retry-After: 1`；不得把它伪装成 quota exhausted。任何依赖 PostgreSQL 的公开路径在 PostgreSQL 不可用时统一返回 `503 dependency_unavailable`，不得按 Redis 快照或 activation evidence 继续授权、选路或预留。

## 9. 安全与运维

- 生产使用 TLS、ACL 最小权限、独立账号与网络隔离；应用账号禁止 CONFIG、MODULE、FLUSH、KEYS 和跨环境前缀访问。
- 不记录命令完整参数；metrics 只标 capability/result，不以 user_id、group_id、account_id、key 或 owner token 作为 label。
- Redis persistence/replication 用于缩短协调状态丢失窗口，但恢复后仍把所有 lease 视为可能过期；Lua 每次 acquire 都先按 Redis TIME 清理。
- 监控至少包含连接状态、命令延迟、Lua code 分布、lease active/capacity、renew failure、rate-limit reject、breaker state/open/probe contention、bulkhead active/reject/queue wait、cache hit/miss、Pub/Sub reconnect、memory/eviction。任何 eviction policy 不得静默淘汰硬协调 key；生产使用 `noeviction` 并为内存水位告警。

## 10. 必测场景

1. 100 个并发 acquire 在 limit=10 时任何时刻成功 owner 不超过 10；重复 owner 不重复占槽。
2. acquire/renew/release 在 Redis Cluster 相同 hash slot；脚本返回类型在 Redis 8 与测试容器一致。
3. owner 崩溃后 60 秒 lease 到期并可被新 owner 获取；旧 owner 的迟到 release 不影响新 owner。
4. 续租连续失败时上游被取消并执行有限 drain，PostgreSQL reservation 最终 settled/released/expired 之一。
5. Redis 整体不可用时模型请求不调用上游并返回 503；`/v1/usage` 仍可直接从 PostgreSQL 返回权威 quota。
6. cache/PubSub 丢失、重复、乱序后，禁用 Key/撤销 Subscription 的缓存最迟 60 秒收敛；但 revoke/disable 提交后的新模型请求与 failover attempt 即使命中旧 active 缓存，也必须通过 PostgreSQL canonical 强读立即拒绝。
7. Group Supply Configuration 的 configured Channel/绑定/version 变化、Account/Channel disable/retire、Account health 或 `group_accounts` 解绑提交后，新请求与 failover 即使命中旧候选/粘性缓存或旧 activation evidence，也必须经 Supply canonical 强读立即拒绝；已准入当前 attempt 可结算，Group 状态/version/ETag 不被 Supply mutation 改写。Account/Channel 退役前必须先按 PostgreSQL Supply 事实清理 enabled binding/non-null Channel 引用，Redis 缓存和 Group status 都不参与 `account_in_use/channel_in_use` 判定。
8. 角色/用户状态/token_version 变更提交后，旧 JWT 调用任一控制面路由都因 PostgreSQL 强读立即拒绝；Redis token-version 旧值不得授权。
9. 分钟边界并发模型 POST RPM 不丢计数、不出现两个窗口同时放行；超限包含正确 Retry-After；`/v1/models` 和 `/v1/usage` 不创建/递增 Group RPM key，且不返回 `group_rate_limited`。
10. 密码重置对 IP/account 两个 scope 都使用 Redis `TIME` 窗口；存在、不存在和 disabled 邮箱走同样 HMAC/计数路径，原始 IP/邮箱不进入 key/log；任一 scope 超限为 `429 rate_limit_exceeded`，Redis/脚本异常为 `503 coordination_unavailable` 且不写 token/outbox。120 秒 TTL 不因正常重复请求续期，分钟跨界只递增服务器当前窗口。
11. 两个 Worker 实例竞争同一 job 时只有一个取得 PostgreSQL session advisory lock；持锁连接中断后旧 owner 停止，新 owner 可接管，幂等写不重复。
12. Redis dump、日志、metrics 和 tracing 中不存在原始 API Key、JWT、Refresh Token、邮箱、prompt 或 Account credential。
13. 30 秒窗口内的 10 个 sample/50% 失败率与连续 5 次失败均只打开一次；两个 API + Worker 竞争 half-open 时全集群只有一个 probe owner，owner 崩溃后 10 秒可重新接管；第 1 次成功仍 half-open，连续第 2 次成功才 closed。
14. Account `401/403` 进入 auth-blocked/unhealthy 且不自动 half-open；`429` 的 Retry-After 正确限制在 1 s..24 h；流已输出后的断流会记 breaker 失败但绝不重放请求。
15. 单 API 实例占满 600 个 SSE 令牌后新 SSE 零排队返回 `429 gateway_overloaded + Retry-After: 1`；同时 NonStream 200、Control 100/queue 50 和 Usage 100/queue 20 仍各自达标，SSE 不占 NonStream permit，取消与断流无令牌泄漏。
16. dispatch fence 之前失败可 release；fence 提交后 transport 证明零字节或上游明确未执行时，attempt 必须以 `confirmed_no_execution`、0 Token、`is_estimated=false` settle；5xx/首包超时/断连无此证据时必须 `conservative_estimate` settle，两者都不得 release。
