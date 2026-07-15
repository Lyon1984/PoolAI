# Redis 运行时协调契约 v1

状态：Release 1 冻结  
目标：Redis 8，StackExchange.Redis  
前缀：`poolai:r1:<environment>:`

Redis 只承载短生命周期协调、限流、粘性和缓存，不是 Subscription、API Key、Account、Group quota、usage 或审计的业务真相。Group 的 `consumed + reserved + estimate <= total` 只能由 PostgreSQL 事务判定；Redis 中即使存在 quota snapshot，也不得参与放行。

## 1. 通用规则

- `<environment>` 只允许 `[a-z0-9-]{1,32}`，生产、预发和测试必须使用不同实例或至少不同前缀；尖括号表示文档占位符，不写入实际 key。
- ID 统一使用小写、带连字符的 UUID；会话信号先做 HMAC-SHA256，再取 32 个十六进制字符，不能把 prompt、邮箱、原始 API Key、JWT 或 Refresh Token 放进 key/value/log。
- Account lease 和 Group/登录 RPM 使用 Redis `TIME`，不得使用各节点本地时钟；PostgreSQL reservation/period、Subscription、API Key 与一次性 Token 使用数据库时钟，两类截止时间不能互相替代。
- 所有 Lua 通过 [`../release-manifest-v1.json`](../release-manifest-v1.json) 登记逻辑名称、逻辑版本、权威正文路径和正文 SHA-256，应用启动时加载；Redis 返回的 SHA-1 只是 content-addressed script cache 标识，不能替代清单完整性使用的 SHA-256。遇到 `NOSCRIPT` 只允许重新加载同逻辑版本、同 SHA-256 的正文一次。滚动升级期间新旧脚本的 key 结构和返回数组必须兼容，否则使用新 key version。
- M0 只建立连接、`TIME`、机器清单和 versioned script registry 测试基座，不提前实现 Account lease、RPM 或 breaker 业务 Lua，因此当前 release manifest 的 `scripts` 显式为空数组；空数组不是省略登记规则。M2 新增脚本时，Lua 正文只能位于 `docs/runtime/scripts/` 并与本契约、release manifest 和测试同一变更提交。
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
| 登录失败 | `rate:login:v1:{ip_hash}:{minute_epoch}` / String integer | 失败次数 | 10 min | 异常 fail-closed，保护认证端点 |
| 粘性路由 | `sticky:v1:{group_id}:{session_hash}` / String | account_id、group_policy_version、supply_configuration_version | 60 min，命中续期 | miss/error 或任一 version 不匹配时重新调度；仍必须强读 Supply Configuration 并取得 Account lease |
| Account 冷却 | `cooldown:account:v1:{account_id}` / String JSON | reason、retry_at、source_status | `retry_at-now`，上限 24 h | miss 使用 PostgreSQL 最近健康状态；不得绕过 Account lease |
| Account breaker 窗口 | `breaker:account:v1:{account_id}` / HASH | window_started_at_ms、samples、failures、consecutive_failures、open_until_ms、open_count、half_open_successes、auth_blocked | 每次记录后 24 h | 脚本异常 fail-closed；不得仅用进程内计数判定 breaker |
| Account half-open 单探针 | `breaker-probe:account:v1:{account_id}` / String | member=随机 128-bit owner token | 固定 10 s，不续期 | 只能通过 `breaker_probe_acquire_v1` 取得；异常 fail-closed |
| Access Token 撤销预筛快照 | `auth:token-version:v1:{user_id}` / String integer | 最近观察到的 token_version | 5 min | 只能提前拒绝；每个 JWT 控制面请求仍强读 PostgreSQL User/status/role/token_version，PG 失败则 fail-closed |
| 缓存失效通知 | channel `poolai:r1:<environment>:invalidate:v1` | entity type、id、version | Pub/Sub 无持久 TTL | best effort；正确性依靠短 TTL 与数据库 version |

Refresh Token family 的 hash、轮换和撤销事实保存在 PostgreSQL `refresh_sessions`；Redis 只缓存 `token_version`，因此 Redis 数据丢失不会恢复已撤销会话。

API Key、Subscription 与 Group policy 的正缓存只用于尽早拒绝和减少非安全查询；每个入站模型请求及每个新 failover attempt 在同 Group Account 调度/lease/reservation 前必须按 canonical ID 强读 PostgreSQL API Key、User、Subscription、Group，以及 Supply 所有的 `group_supply_configurations`、configured Channel、`group_accounts`、Account lifecycle/health。请求所选 Channel 必须等于当前 configured Channel。每个 JWT 认证的控制面请求也必须强读 User status、role 和 token_version。管理员 revoke/disable/角色变更或 Supply Configuration mutation 的事务提交是线性化点：此前已完成准入的当前 attempt 可按规则结算，提交后的新请求不得因旧 Redis 正缓存、旧粘性记录或 Group activation evidence 继续放行。Supply mutation 不更新 Group version/ETag，也不自动禁用 Group。

## 3. Lease Lua v1

R1 只对 Account 使用该 lease 算法，不创建 User lease。owner token 是服务端生成的随机 128-bit 值，只在当前 attempt 上下文保存。Account 并发上限来自 PostgreSQL Account 版本化配置，它是供应容量，不是累计 Token 配额。

### 3.1 `lease_acquire_v1`

输入：

- `KEYS[1]`：lease ZSET；
- `ARGV[1]`：owner token；
- `ARGV[2]`：正整数 limit；
- `ARGV[3]`：lease_ms，Release 1 固定 60000；
- `ARGV[4]`：key_ttl_ms，固定 120000。

原子算法：用 Redis `TIME` 计算 `now_ms`；删除 score `<= now_ms` 的 member；若 owner 已存在则把 score 更新为 `now_ms + lease_ms` 并返回幂等成功；否则在 `ZCARD < limit` 时加入 owner；最后 `PEXPIRE`。

返回：

- `[1, active_count, expires_at_ms, 0]`：新取得；
- `[2, active_count, expires_at_ms, 0]`：同 owner 幂等取得/续期；
- `[0, active_count, 0, retry_after_ms]`：容量已满，`retry_after_ms=max(min_score-now_ms,1)`；
- `[-1, 0, 0, 0]`：参数不合法。

应用对 code 0 可在释放数据库事务后按调度策略等待，但不能持有 Group reservation 等待 Account lease；达到调用总 deadline 后返回 `503 account_capacity_unavailable`。

### 3.2 `lease_renew_v1`

输入为 lease key、owner、lease_ms、key_ttl_ms。先清理过期 member；owner 存在时更新 score 并返回 `[1, expires_at_ms]`，不存在返回 `[0, 0]`。活跃请求每 20 秒续租；连续两次失败或返回 0 时取消上游请求，进入最长 15 秒 drain，并按已知 usage/保守估算结算 PostgreSQL reservation。

### 3.3 `lease_release_v1`

输入为 lease key、owner。`ZREM` 后若集合为空则 `DEL`，返回 `[removed_count]`。重复 release 返回 0 且视为成功，不能释放其他 owner。

## 4. Rate Limit Lua v1

### `fixed_window_increment_v1`

输入：`KEYS[1]` 与 `KEYS[2]` 分别是调用方按最近 Redis 时间计算的当前分钟、下一分钟候选 key，二者必须使用同一 `{scope_id}` hash tag；`ARGV[1]=limit`、`ARGV[2]=increment`、`ARGV[3]=ttl_ms(120000)`。脚本再次读取 Redis `TIME` 并只选择 epoch 与服务器当前分钟相符的 key，两个候选都不匹配时返回参数错误。算法对选中 key 执行 `INCRBY`，首次写入时 `PEXPIRE`；返回 `[allowed, current, limit, retry_after_ms]`。`current <= limit` 时 allowed=1，否则 0；超限计数仍保留，防止边界内反复重试绕过。

Group RPM 只在通过 API Key 鉴权的 `/v1/responses` 和 `/v1/chat/completions` 模型 POST 进入 Gateway Process Manager 时计数，流式/非流一视同仁；`/v1/models` 归 NonStream bulkhead、`/v1/usage` 归 Usage bulkhead，两个查询端点均不调用 RPM 脚本。Group RPM 的 key minute_epoch 必须由同一次 Redis `TIME` 结果构造。为避免客户端先取时间再执行脚本产生跨分钟竞争，应用传入两个候选 key（当前分钟与下一分钟），脚本再次读取 Redis `TIME` 并只操作匹配的一个；两个 key 使用相同 `{group_id}` hash tag。

登录限流除分钟计数外可在后续增加指数退避，但不能把用户是否存在暴露为不同错误或时延。

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

- `breaker_record_v1` 的 `KEYS[1..2]` 依次为 breaker HASH 与 cooldown String；`ARGV` 包含 outcome（`success/transient_failure/rate_limited/auth_failure/ignored`）、经校验的 retry-after-ms、有界 jitter 与逻辑版本。脚本用 Redis `TIME` 换窗、原子更新计数/open_until，并把 HASH TTL 重置为 24 h。返回固定数组 `[state_code,samples,failures,consecutive_failures,open_until_ms,action_code]`。
- `breaker_probe_acquire_v1` 的 `KEYS[1..3]` 为 breaker、cooldown、probe；它用 Redis `TIME` 确认 breaker 存在、`open_count > 0`、`auth_blocked=0` 且 `open_until_ms <= now_ms`，再以等价于 `SET probe owner NX PX 10000` 的原子操作选出全集群唯一探针。返回 `[1,probe_expires_at_ms]`、`[0,retry_after_ms]` 或参数错误。
- `breaker_probe_complete_v1` 只接受 probe key 中的当前 owner。成功时原子增加 `half_open_successes`并删除 probe；第 1 次连续成功仍保持 half-open，只允许下一个单 probe，第 2 次连续成功才清零窗口/open_count/half_open_successes、删除 cooldown 并返“写 healthy/closed”。任一探针失败都清零 `half_open_successes`，按指数 break duration 或受限 Retry-After 重新 open，删除 probe 并返“写 cooling/unhealthy”。迟到/非 owner complete 不能改变状态。
- 探针仍必须获取 Account lease。如以真实模型请求做探针，必须走完 Group reservation、attempt 事实和核销，不存在“免配额健康请求”。
- 三个 breaker 脚本的所有 key 共用 `{account_id}` hash tag。任一脚本超时、未知 code、Redis 断开或 probe 所有权不可验证均 fail-closed，不降级为本地 breaker。

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
| Account lease、Group RPM、登录限流 | `503 coordination_unavailable` | 按各自数据依赖处理 | 不允许单节点失控放量；R1 无 User lease |
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
10. 两个 Worker 实例竞争同一 job 时只有一个取得 PostgreSQL session advisory lock；持锁连接中断后旧 owner 停止，新 owner 可接管，幂等写不重复。
11. Redis dump、日志、metrics 和 tracing 中不存在原始 API Key、JWT、Refresh Token、邮箱、prompt 或 Account credential。
12. 30 秒窗口内的 10 个 sample/50% 失败率与连续 5 次失败均只打开一次；两个 API + Worker 竞争 half-open 时全集群只有一个 probe owner，owner 崩溃后 10 秒可重新接管；第 1 次成功仍 half-open，连续第 2 次成功才 closed。
13. Account `401/403` 进入 auth-blocked/unhealthy 且不自动 half-open；`429` 的 Retry-After 正确限制在 1 s..24 h；流已输出后的断流会记 breaker 失败但绝不重放请求。
14. 单 API 实例占满 600 个 SSE 令牌后新 SSE 零排队返回 `429 gateway_overloaded + Retry-After: 1`；同时 NonStream 200、Control 100/queue 50 和 Usage 100/queue 20 仍各自达标，SSE 不占 NonStream permit，取消与断流无令牌泄漏。
15. dispatch fence 之前失败可 release；fence 提交后 transport 证明零字节或上游明确未执行时，attempt 必须以 `confirmed_no_execution`、0 Token、`is_estimated=false` settle；5xx/首包超时/断连无此证据时必须 `conservative_estimate` settle，两者都不得 release。
