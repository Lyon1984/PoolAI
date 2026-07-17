# PoolAI Release 1 数据库执行规格

本目录是绿地 `.NET 10 + PostgreSQL 18` 的可执行基线，不是上游 Go migration 的续写。它冻结了订阅访问、Group 唯一共享配额、Account Token 事实和 `/v1/usage` 聚合所需的物理模型与事务边界；没有计费、支付、价格、余额、订单、促销或兑换对象。

## 1. 文件与迁移所有权

按顺序执行：

1. `0001_baseline.sql`：表、约束、索引、系统角色种子和数据完整性 Trigger。
2. `0002_quota_functions.sql`：配额初始化、预留、dispatch 边界、续租、核销、释放、过期、迟到修正、总量调整和周期重置函数。
3. `0003_runtime_permissions.sql`：收回 PUBLIC 权限、固定 SECURITY DEFINER 搜索路径，并向 API/Worker 授予最小运行时权限。
4. `0004_identity_m1_e1.sql`：M1-E1 前向撤销 API 对 `user_roles` 的直接 UPDATE/DELETE，增加最小权限的原子 User/Role 更新函数，并持久化 email outbox 失败分类事实；不改写 0001–0003。
5. `0005_identity_m1_e2.sql`：M1-E2 前向增加 login/setup TOTP challenge 快照、TOTP recovery-code 摘要、refresh family 活跃查找索引和最小 API 权限；不改写 0001–0004。
6. `0006_identity_m1_e3.sql`：M1-E3 前向收窄 `poolai_api` 的 User UPDATE 列，禁止绕过原子 User 状态/角色入口，并把 Auditor 保留为独立审计 actor；不改写 0001–0005。
7. `0007_group_subscription_m1_e4.sql`：M1-E4 前向收回 Group、Template、canonical Subscription 的 API 宽表写权限，增加由 NOLOGIN owner 持有的窄写入口、CAS/生命周期/数据库时钟与归档 fence，以及 keyset/归档热路径索引；不改写 0001–0006。

只有 `PoolAI.Migrator` 可以执行 DDL。集群必须预先创建 `poolai_runtime_owner NOLOGIN`、`poolai_api`、`poolai_worker`；0003 只断言角色，不执行 `CREATE ROLE`。Migrator 必须是待转移函数/`public` schema 的 owner（或等效受控迁移角色），可 `SET ROLE poolai_runtime_owner`，并可授予 API/Worker 权限。PostgreSQL 的 owner 转移要求新 owner 对所在 schema 有 CREATE，故 0003 只在同一迁移事务内临时授予 runtime owner CREATE，全部函数转移后立即撤销；提交后的运行时角色均无 DDL。Migrator 取得 PostgreSQL advisory lock，在同一事务中完成以下步骤：读取 `poolai_schema_migrations`、校验已执行文件的 SHA-256、按 [`../release-manifest-v1.json`](../release-manifest-v1.json) 的顺序依次执行 0001/0002/0003/0004/0005/0006/0007、写入 `(version, name, checksum_sha256, applied_by)`，最后提交。Api 与 Worker 只以各自运行时角色读取 migration history：历史必须是清单的 checksum 完全一致前缀，当前最高版本必须落在清单声明的兼容窗口内；它们不取得锁、不执行 DDL，也不自动迁移。手工验收需使用 `psql --single-transaction`，不能逐语句提交。

`roles` 由 0001 写入四个稳定系统角色：`admin`、`operator`、`auditor`、`user`。首个 Admin 只由 `PoolAI.Migrator bootstrap-admin` 一次性子命令创建：应用生成 UUIDv7、密码哈希和 security stamp，在一个事务中插入 User、唯一 UserRole、AuditLog 和 `user_created` Integration Event；`users` 已有任何行或已经存在 Admin 时命令必须失败且不写部分数据。数据库用 `UNIQUE(user_id)` 保证每个用户最多一个角色；服务在创建/启用用户前保证恰有一个角色。

R1 密码哈希字符串固定为 `poolai-password-v1:<identity-v3-base64>`：冒号后是 ASP.NET Core Identity `PasswordHasherCompatibilityMode.IdentityV3` 的原始 Base64 payload，配置固定 PBKDF2-HMAC-SHA512、100000 iterations、128-bit random salt 和 256-bit subkey。写入只能产生该前缀；读取遇到未知 PoolAI 前缀必须 fail closed，不能尝试上游 Go/明文兼容。参数升级发布新的 `poolai-password-vN:`，成功认证后才允许受控 rehash，不能就地改变 v1 的含义。

角色变更必须在一个短事务内调用 `poolai_identity_update_user`；API 没有 `user_roles` 的直接 UPDATE/DELETE。该 `SECURITY DEFINER` 函数由 `poolai_runtime_owner NOLOGIN` 持有，以 User → UserRole 的固定顺序锁行，并对现有 `user_roles` 行执行恰好一条 UPDATE；禁止 DELETE+INSERT，因为它会产生两个安全版本跃迁。所有 status/role mutation 先取得固定 transaction-scoped advisory lock `pg_advisory_xact_lock(5786931235259499597)`（十六进制 `0x504F4F4C4941444D`），再锁目标 User/UserRole；函数在同一 fence 内校验 expected version，并在变更会减少 active Admin 时按变更后的状态验证至少保留一个 `users.status='active' AND deleted_at IS NULL` 的 Admin。冲突返回稳定 disposition，由应用映射为 `409 resource_conflict` 或 `412 version_conflict`，不修改 User、UserRole、token_version、audit 或 outbox。角色与 display/status 同改仍只递增一次 User version/token_version；不依赖 `xmin`、无锁 count 或 API 对角色表的直接权限。

## 2. Release 1 物理边界

- Account 与 Channel 的创建时 Provider 是不可变 Supply 字段，仅允许 `openai`、`openai_compatible`，Account 凭据仅 `api_key`。公开传输字段 `platform: openai` 表示 R1 入站协议族，不是 Provider selector；扩展 Anthropic、OAuth 或 Service Account 必须新增前向 migration，不能提前写入“预留枚举”。
- 上游 `base_url` 的唯一权威在 Account；Channel 只保存模型映射、能力与非秘密策略。
- Account、Channel 默认 `disabled`；Account 凭据只保存 envelope、非秘密 `credential_prefix` 和可选 `credential_hint`。Group 由 GroupQuota 以 `disabled` 创建且不保存 Channel/Account binding；Supply 可另行创建或暂存 `group_supply_configurations`，其 `channel_id` 可为 null。Group 切换为 `active` 时必须在 GroupQuota 同一 UoW 记录 `activation_supply_readiness_token/observed_at`，且 Trigger 强制存在：启用且有模型映射的 configured Channel、已初始化的 current quota、至少一个 binding 启用、Provider 匹配、health 为 `healthy/degraded` 且不在数据库 cooldown 的 active Account。`poolai_validate_group_activation` 是防止绕过应用入口的只读窄 guard：只按本条语句时点读取 quota 与 Supply Configuration/readiness，不锁、不写 Supply，也不承诺观察后的状态永久不变；正常变更仍由 Orchestrator 编排，并由每次 attempt 的 reserve 强读做最终放行。Opaque token 只保存观察证据，不是 lease/capability，GroupQuota 不解析。
- Release 1 禁止跨 Group fallback，所以所有 request/attempt 的 `routing_group_id = quota_group_id`。一个 Group 可关联多个 Account，一个 Account 可服务多个 Group。
- API Key 必须且只能属于一个 Group。数据库 Trigger 禁止改 `group_id`；换组必须 revoke 原 Key 后新建。Key hash 为带版本 pepper 的 32 字节 HMAC-SHA-256，只展示 prefix。
- 每个 `user + group` 只有一条 canonical Subscription。分配、延长、暂停、恢复和撤销都更新同一行并追加 AuditLog；不建立并行历史行。Subscription 的 `(template_id, group_id)` 必须指向同 Group Template，并保存不可随模板改名而变化的 `template_name_snapshot`；必须记录分配者，且没有价格或个人配额。Template retired 后不能新分配，已有 revoked Subscription 可显式重新授予。
- 0007 后 `poolai_api` 不再直接 INSERT/UPDATE `groups/subscription_templates/subscriptions`。Group 创建必须经 `poolai_group_create` 在同一事务复用 `poolai_quota_initialize`；其余写入只经 `poolai_group_update`、三条 Template 入口及两条 Subscription 入口。所有入口固定搜索路径、由 `poolai_runtime_owner NOLOGIN` 持有、只向 API 授予 EXECUTE，并返回 disposition、是否变更、变更前快照与当前 version 供审计和 ETag 冲突处理。归档按 quota → Group 锁序取得 fence，Template/Subscription 按 Group → Template/Subscription 锁序；Template 更新在目标 Template 之前取得冲突 Group fence，使同 Group 交叉改名串行化；所有时间判定在潜在阻塞锁之后重新取 `clock_timestamp()`。按 [`ADR 0006`](../architecture/adr/0006-register-group-subscription-lifecycle-fence.md) 的 Proposed 候选登记，这是一项不可拆分的 Group–Subscription lifecycle fence：`poolai_group_update` 只在 archive 分支、持有 quota/Group fence 后读取 `subscriptions(group_id,status,expires_at)`；`poolai_subscription_template_create/update/retire` 与 `poolai_subscription_assign/update` 只读取/锁定 `groups(id,status)`。任何其他函数、表、字段、跨 Context UPDATE 或通用/dynamic SQL executor 都不在白名单内。Group 只有 disabled 且无 pending reservation、无 effective active/scheduled Subscription 时可归档；分配只允许 active Group 与 active Template，未知 User/分配者外键稳定返回 `conflict`，revoked regrant 必须由上层 Admin 授权显式打开入口参数。ADR 0006 在 `@Lyon1984` 明确签核前不得作为 M1-E4 architecture sign-off 或 release-ready 证据。
- Group archived、Account/Channel/Template retired、API Key revoked 均不可逆；Subscription revoked 不是终态，可由显式重新授予命令恢复。
- `group_supply_configurations` 与其 `group_accounts` 子项只由 Supply 写。修改 Channel/绑定前，应用先 `FOR UPDATE` 锁 Supply Configuration，再写子项；解绑只把 `is_enabled=false`，不物理删除历史绑定。绑定 Trigger 在同一事务只递增 Supply Configuration version，绝不更新 Group。Group 与 Supply Configuration 使用独立强 ETag；多行绑定替换可让 Supply version 跳过多个整数，客户端不得假设恰好 `+1`。RESTRICT 外键是历史引用的最后防线。
- Account/Channel 退役只检查 Supply 自有引用，不读取 Group status：任一 enabled `group_accounts` 行都使 Account 退役返回 `account_in_use`；任一 non-null `group_supply_configurations.channel_id` 引用都使 Channel 退役返回 `channel_in_use`。管理员必须先 PATCH Supply Configuration 清理/禁用引用，再退役对象；disabled/archived Group 也不放宽该规则。
- `usage_attempts` 是 GroupQuota 在核销或 dispatch 后保守过期时与 counter 同事务写入的 immutable settlement/integration fact，不是 Usage 模块可写的 read model；Usage 只拥有由它派生的 hourly projection。`usage_attempt_adjustments` 是唯一 Token 纠错事实；`group_quota_events` 与 `audit_logs` 是追加式账本。Trigger 拒绝 UPDATE/DELETE。

所有业务 UUID 均由 .NET 生成 UUIDv7，数据库不提供 UUID default。固定系统 Role ID 是 migration-owned 的稳定 UUIDv7 形式标识。

## 3. 身份、订阅与有效状态

持久状态与有效状态分开，避免 Worker 按时间反复改状态：

| 对象 | 持久状态 | effective 规则 |
|---|---|---|
| User | `active / disabled` | `locked_until > DB now` 时临时锁定；不持久化 `locked` |
| API Key | `active / disabled / revoked` | active 且 `expires_at IS NULL OR expires_at > DB now` 才有效；过期是派生状态 |
| Subscription | `active / suspended / revoked` | active 且 `starts_at <= DB now < expires_at` 才有效；此前是 scheduled，此后是 expired |
| Group | `disabled / active / archived` | 仅 active 可进入 Gateway；archived 是终态软删除 |
| Group Supply Configuration | 无 lifecycle status；可不存在或 staged/unready | `channel_id` 可空；readiness 由当前 Channel、binding、Account lifecycle/health 和数据库观察时间派生，version 与 Group version 独立 |
| Account | `active / disabled / retired` | health 独立持久化为 `unknown / healthy / degraded / cooling / unhealthy`；仅 healthy/degraded 且 cooldown 已过可调度 |

`users.token_version` 是 JWT 立即失效版本。User status、角色、密码、TOTP、security stamp 或全会话撤销发生安全变化时，在同一事务把 token_version 恰好递增并递增资源 version；所有 JWT 控制面请求强读这两个版本。应用角色替换经 `poolai_identity_update_user` 显式使对应 User 版本失效；Trigger 仍保护迁移/维护路径，且只在该受控函数的 transaction-local 标记与 NOLOGIN owner 身份同时成立时跳过重复递增。API 对 UserRole 没有直接 UPDATE/DELETE，不能以 DELETE+INSERT 绕过入口。

`poolai_quota_reserve` 不信任最长 60 秒的 Redis 正缓存。Gateway 在调用前仍须检查 Redis Account lease、健康/cooldown 镜像和 RPM，用于硬协调或快速失败；累计配额以及最终 Supply/health/cooldown 放行只认 PostgreSQL。函数先锁 quota，再以**不带 status/时间条件**的 `FOR SHARE` 强读并锁定 request、User、Role、API Key、canonical Subscription 与 Group。若 `attempt_id` 已存在，它随后锁原 period/reservation、校验原 event/outbox 并直接返回，因此 revoke 或 Supply 切换后仍可安全重放。只有新 attempt 才按 Supply 所有权锁序锁当前 Configuration、目标 binding/Account 和 **configured Channel**，在锁 period 前确认请求 Channel 与 configured Channel 相等；所有等待结束后重新取一次 `clock_timestamp()`，再在同一批已锁行上验证当前状态、到期时间、health 和数据库 cooldown，最后才插入 reservation。管理员的 disable/revoke/configuration mutation 是 UPDATE，会与该共享锁串行：

- revoke 先提交：后续新 attempt 必须看到失效并拒绝；
- reserve 先提交：这个已获准 attempt 可继续，revoke 在其提交后生效；
- 已存在 attempt_id 的重放只返回原 reservation，不创建“新 attempt”。

这就是撤销、Supply Configuration 与时间有效性的线性化点。若函数在行锁上等待到 Subscription、Key 或 cooldown 边界之后，必须按等待结束后的数据库时间判定，不能沿用进入函数前的时间。应用在 reserve 前仍做一次普通权限检查用于快速失败，但只有这个 PostgreSQL 强读是放行真相；PostgreSQL 不可用时所有依赖它的公开路径统一返回 `503 dependency_unavailable`，绝不依据 Redis 快照放行。

同属 ADR 0006 Family A 的 `poolai_quota_settle`、`poolai_quota_mark_dispatched`、`poolai_quota_adjust_usage` 不重复准入读取。它们只在 quota → period → reservation 已锁定后，以 `FOR SHARE` 读取/锁定 `accounts(id,provider)` 与 `channels(id,provider)`，确认 reservation 冻结 route 的 provider identity，再采样数据库时间；不得读取 Account/Channel lifecycle、health、cooldown、credential、Configuration 或 binding。该登记是对已签 `0002_quota_functions.sql` 既有行为的文档纠正，不改写迁移。

首次 attempt 必须在同一个短数据库事务中先 INSERT `usage_requests`、再调用 `poolai_quota_reserve` 并一次提交；reserve 失败时 request INSERT 一并回滚，不留下永久 `accepted` 的孤儿。failover 复用同一 request，只为新的 `attempt_id/attempt_index` 调用 reserve，不重复插 request。

password-reset credential 是 32 个 CSPRNG bytes 的无填充 base64url；数据库只存 `HMAC-SHA256(Auth:TokenHash:<pepper-version>, credential-bytes)` 的 32-byte `one_time_tokens.token_hash` 与正 `pepper_version`，Current pepper 只生成新 token，Previous pepper 只验证轮换窗口内旧 token。它们专用于 one-time token，不能复用 API Key pepper、JWT signing key 或密码材料。所有 `created_at/expires_at/used_at/revoked_at` 判定只取 PostgreSQL `clock_timestamp()`，不接受 API 节点或客户端时间。

forgot-password 对存在的 active User，在同一事务先把该 User 之前所有 `purpose='password_reset' AND used_at IS NULL AND revoked_at IS NULL` 的 token 设为 `revoked_at=clock_timestamp(), revoke_reason='superseded'`，再创建一个新 token、`email_outbox` 和 `password_reset_requested` Integration Event；所以旧邮件即使稍后送达，其 credential 也无效。不存在或 disabled User 不创建 token/outbox/event，但 anonymous 响应、Redis 限流和可观测字段必须与 active 路径保持防枚举语义。Admin 代理 disabled User 明确返回 `409 resource_conflict`，不创建邮件。禁用 User 的事务同时撤销其未使用 password-reset token；禁用后迟到的 reset 一律返回公开的 `password_reset_token_invalid`。

reset-password 在同一事务锁定 active User，并以单条 CAS 消费 token：`UPDATE one_time_tokens SET used_at=clock_timestamp() WHERE purpose='password_reset' AND token_hash=:digest AND pepper_version=:version AND used_at IS NULL AND revoked_at IS NULL AND expires_at > clock_timestamp() RETURNING user_id,id`。恰好一行成功后才更新密码/security stamp、恰好递增一次 User `token_version`/资源 version、撤销 Refresh families、写 audit 与 `password_reset_completed` Integration Event并提交；零行、User 非 active 或任何后续步骤失败都不得留下已消费 token，且对外统一为 `401 password_reset_token_invalid`。并发使用同一 credential 最多一个事务成功。

邮件接收目标和实际 reset credential/full URL 使用 envelope encryption，`template_payload` 只允许非秘密渲染数据。每条邮件在创建时冻结唯一稳定 `message_id`，所有重试复用它。Email Worker 使用 PostgreSQL advisory lock 选主，并以随机 `lock_owner`、单调 `lock_generation`、数据库 `locked_until` 和 `FOR UPDATE SKIP LOCKED` 执行 `pending -> processing -> sent/dead`：每次 claim/takeover 更换 owner 且 `generation + 1`，heartbeat/成功/重试/dead 都以 `status='processing' AND lock_owner=:owner AND lock_generation=:generation` CAS，零更新的旧 owner 立即停止。处理失败在上限内回到 pending 并设置下一次时间。每次 retry/dead 与状态迁移同事务插入一条 `email_outbox_delivery_failures` 低基数事实，并更新 `last_failure_class`；所以 Worker 在提交后立即崩溃也不会丢失 failure/dead 告警证据。sent/dead 终态必须在同一 UPDATE 清空 recipient/delivery 两个密文 envelope，dead 同时写 `dead_at` 和非秘密错误摘要。Worker 从数据库事实刷新 `poolai_email_outbox_pending`、`poolai_email_outbox_oldest_age_seconds`、`poolai_email_outbox_dead` 和按 failure_class/outcome/terminal_reason 聚合的 `poolai_email_outbox_failures_total`；标签不得包含邮箱、User ID 或 Message-ID。不使用 Redis Worker leader。

`GET /api/v1/admin/users` 固定按 `created_at DESC, id DESC` 做 keyset pagination。`next_cursor` 是 25-byte binary payload（`0x01` version、8-byte big-endian Unix microseconds、16-byte RFC 4122 UUID）无填充 base64url；下一页使用独占条件 `created_at < :created_at OR (created_at = :created_at AND id < :id)` 并沿用相同排序。长度、base64url、版本、timestamp 或 UUID 非规范输入返回 `400 invalid_request`；cursor 只决定起点，绝不承载角色/过滤/授权真相。

所有 `*_envelope` 的 JSON v1 固定字段为 `v/alg/kid/wrapped_dek/wrap_nonce/wrap_tag/ciphertext/nonce/tag`，二进制使用无填充 base64url，`alg` 固定 `A256GCM+A256GCM-v1`。AAD 是 UTF-8 `poolai|v1|<purpose>|<entity_type>|<entity_id>|<field_name>`；应用的统一 Encryption Service 必须在解密前严格校验字段集合、类型、版本、算法、keyring 中的 `kid`、base64/nonce/tag 长度，并用调用方给出的 purpose/entity/field 重建 AAD 后验签。数据库的 `jsonb_typeof(...)=object` 只保证存储形状，不代表 envelope 有效；未知字段/版本/算法/key、AAD 不匹配或认证失败全部 fail closed 并告警。M1-E2 TOTP 幂等秘密响应的 AAD 进一步把当前请求重新计算的 actor User ID、scope、Idempotency-Key、request hash、响应 resource ID 和 response field 绑定成 canonical JSON 摘要；数据库行中的 `resource_id/request_hash` 不能单独充当可信绑定。setup/confirm 使用不同 field，跨用户、跨 key、跨请求或跨响应类型的 envelope/resource 成对复制必须认证失败。

TOTP 成功后，以数据库事务执行 `UPDATE users SET totp_last_accepted_step = :step ... WHERE :step > COALESCE(totp_last_accepted_step, -1)`；零更新即同一或旧 30 秒 step 重放。R1 不使用 Redis User lease。Refresh、API Key 和 one-time token hash 都保存 `pepper_version`，便于轮换。

`one_time_tokens.purpose='totp_challenge'` 必须同时冻结 `challenge_kind`（只取 `login/setup`）、创建时的 `security_stamp` 和正 `token_version`。登录 MFA challenge 不保存 TOTP secret；setup challenge 必须在 `secret_envelope` 保存待确认 seed，确认成功后的幂等恢复响应才可保存到 `response_body_envelope`。两个 envelope 都遵守本节统一 Envelope v1/AAD 规则，不能写到日志、审计 payload 或普通 JSON。每个 User 的每个 challenge kind 最多有一条 `used_at/revoked_at` 均为空的行；创建替代 challenge 时必须先在同一短事务撤销旧行，再插入新行。数据库时钟负责 challenge 创建、到期、消费和撤销判断；应用节点时钟不参与这些持久状态比较。

TOTP confirm 一次生成并仅在成功响应中展示 8 个 recovery code；数据库只把每个 code 以专用、带版本 pepper 的 HMAC-SHA-256 32-byte 摘要写入 `totp_recovery_codes`。明文仅可在上述 setup response envelope 中短期保存，用于同一个 Idempotency-Key 的成功重放；不能进入 User 行。Recovery code 以单条 `UPDATE ... SET used_at=clock_timestamp() ... WHERE used_at IS NULL AND revoked_at IS NULL RETURNING id` CAS 消费，同一 code 最多成功一次；disable/re-enable TOTP 时在同一 User UoW 撤销全部未用 code 并写非空 `revoke_reason`。`used_at` 与 `revoked_at` 互斥，所有终态只认数据库时钟。M1-E2 只以真实 PostgreSQL 并发集成测试锁定这条保留凭据的 CAS 性质；R1 当前没有 recovery-code 登录/恢复 Application 用例或公开端点，该测试证据不授权当前登录路径消费恢复码。

Refresh credential 继续只保存专用 pepper 的 32-byte HMAC 摘要。合法形态的随机未知 credential 先用 token-hash 唯一索引做事务外、status-agnostic `EXISTS` 预检，miss 不开启 UoW；预检命中不代表有效，rotated/revoked/expired 全部仍进入原短事务锁定并复核，避免绕过 reuse family revoke。发行、到期、rotation、replay family revoke、当前 family logout 和全 family revoke 全部以 PostgreSQL `clock_timestamp()` 为权威，并在一个短事务内锁定原 session 后更新；不能在事务内等待 Redis/HTTP。每次成功 rotation 在原 `family_id` 下只产生一个新 active child；固定写序是先把旧行变为 `rotated`，再插入 active child，最后把旧行指向 replacement。`UNIQUE(family_id) WHERE status='active'` 是并发绕过时的数据库终局防线；已 rotated credential 的并发/迟到重放撤销该 User 对应 family 的全部 active 行。Access JWT 的 session family 检查先用该唯一索引定位 family，再强读 `user_id/expires_at` 判断有效；全会话撤销还必须在同一 User UoW 推进 `token_version`。

## 4. Token 类型和公开表示

累计与事实 Token 列使用 `numeric(78,0)`，.NET Domain 映射为 `BigInteger`。公开的 Group quota、池聚合和 Account 聚合一律是最多 78 位的非负十进制字符串；不要把累计值序列化成 JSON number。OpenAI 单请求 usage 只有在 `<= 2^53-1` 时才按兼容协议返回整数。

管理输入 `total_tokens`、准入输入 `estimated_tokens` 仍限制在 `1..9007199254740991`。上游异常单次值可以作为 `numeric(78,0)` 原样进入事实；若任何事实或运算会超过 78 位，函数返回 `token_numeric_overflow`、整笔事务回滚并触发 P0 告警，绝不截断或饱和。`raw_upstream_usage` 保留供应商原始证据。

标准化关系：

- `total_tokens = input_tokens + output_tokens`；
- `cache_read_tokens`、`cache_creation_tokens` 是互不重叠的 input 子集，均不重复加入 total，且二者之和不得超过 `input_tokens`；
- `thinking_tokens` 是 output 子集；
- `usage_source` 只取 `upstream / local_tokenizer / conservative_estimate / confirmed_no_execution`。`upstream` 与 `confirmed_no_execution` 是已确认事实，`is_estimated=false`；后者还必须为零 Token、无 first token、失败/取消状态，并且 HTTP status 为空或是 Adapter capability 明确认定未执行的 `401/403/429`。其余两类必须 `is_estimated=true`。

## 5. 配额事务和锁序

运行时使用 `READ COMMITTED`，每次调用只执行一个配额函数并立即提交。固定相对锁序是：

新 attempt：`group_token_quotas -> canonical 授权行 -> Group Supply Configuration -> GroupAccount/Account -> configured Channel -> group_quota_periods -> fresh clock -> 状态/时间校验`。

已存在 attempt 重放：`group_token_quotas -> canonical 授权行 -> 原 group_quota_periods -> 原 group_token_reservations -> event/outbox 校验并返回`；不重新要求当前 Supply ready。

禁止在已按相反顺序持锁的事务里调用函数。配额行锁同时把同一 Group 的所有 mutation 串行化；不同 Group 可以并行。函数统一使用数据库 `clock_timestamp()`，不接受客户端时间作为租约判断；任何会受锁等待影响的 `v_now` 都只能在相应 quota/period/reservation/canonical 行完成线性化之后采样。renew、expire、settle、adjust 也遵守这一规则。

| 函数 | 前置状态 | 原子结果 |
|---|---|---|
| `poolai_quota_initialize` | Group 未有 quota | 建 quota/current period，追加 `initialized` event + outbox |
| `poolai_quota_reserve` | 有效访问链、current period | `reserved += estimate`，建 pending reservation，追加 event + outbox |
| `poolai_quota_mark_dispatched` | pending、owner 相同、lease 未过期 | 在调用上游前冻结 DB `dispatch_started_at`、Provider/Model 与 estimate input/output split，追加零 delta event + outbox |
| `poolai_quota_renew` | pending、owner 相同、租约未过期 | 流式向后 120 秒、非流式向后 300 秒滚动，均不超过 absolute max |
| `poolai_quota_settle` | pending 且已 mark-dispatched | `reserved -= estimate; consumed += actual`，插入 attempt，更新 request，追加 event + outbox；有可靠零执行证据时以 `confirmed_no_execution` 正常 settle actual=0，不得 release |
| `poolai_quota_release` | pending 且尚未 dispatch | `reserved -= estimate`，状态 released，追加 event + outbox；dispatch 后一律拒绝 release |
| `poolai_quota_expire` | pending 且 DB now 已过 lease | 未 dispatch 时只释放；已 dispatch 时 `reserved -= estimate; consumed += estimate` 并写 conservative immutable attempt，绝不按零消耗释放 |
| `poolai_quota_adjust_usage` | settled 或 dispatched-expired 且未修正 | 追加唯一 adjustment；已有 conservative expiry fact 时以它为 previous；只改 consumed，保留原终态和终态时间；pre-dispatch released/expired 不可修正为上游执行 |
| `poolai_quota_adjust_total` | current period、If-Match version | 设置绝对 total；允许低于 consumed+reserved，停止新预留但不取消既有流 |
| `poolai_quota_reset` | current period、If-Match version | 关闭旧 period、开新 period、切 current；pending 永久留在旧 period 继续结算 |

每个 mutation 的 quota counter、reservation、最小 usage fact、quota event 和 outbox 在同一事务。事件 `idempotency_key` 与 outbox dedup key 唯一；attempt 使用全局唯一 `attempt_id`，另有 `(request_id, attempt_index)`；reservation 显式唯一 `(period_id, attempt_id)`。事件 key 是数据库 mutation 去重标识：同一 key 若 event identity 或关键参数不同必须冲突；adjustment 重放还必须证明该 key 正好属于 adjustment 保存的 event。renew 重放从 event metadata 返回原续租 lease 快照，不受 reservation 后续续租/终态影响。

HTTP 幂等的唯一真相是控制面 `idempotency_records`：`request_hash` 只能是 `HMAC-SHA256(Idempotency:RequestHashPepper, canonical_request_bytes)` 的 32-byte keyed digest，并用于比较同 key 请求；canonical bytes 必须覆盖该 operation 冻结的全部语义输入。即使输入含 password、one-time token、API Key 或其他低熵/敏感字段，也不得存 canonical 明文、普通 SHA-256/其他无键摘要，避免数据库泄露后做离线字典猜测；pepper 不能复用 JWT、API Key、one-time token、password-reset rate-scope 或 envelope key。R1 只有一个启动期 pepper且不轮换；未来轮换必须先发布可识别 current/previous 的兼容方案，并保留旧 pepper 至旧 `idempotency_records` 全部过期或完成受控迁移。

`idempotency_records` 保存首次业务状态码、白名单 Header 和业务响应快照；请求级 `request_id`/`X-Request-Id` 不持久为重放结果，每次入站由 API 重新生成并保持 Header/body/log/trace 一致，`instance` 由当前 normalized path 生成。所有纯数据库控制面命令必须使用一个短 Unit of Work：取得/创建该行并锁定，执行领域写入、Audit/Outbox 和完成响应，然后一次提交；进程崩溃会使整笔事务回滚，不允许先提交 `in_progress` 再无保护地执行领域写入。若维护任务确需接管已提交的 in-progress 行，必须用数据库时间并以 `(scope,key,status='in_progress',lock_owner,lock_generation,locked_until)` 做 CAS，换随机 owner、`generation/version + 1`；heartbeat 和 complete 也必须带相同 owner+generation，旧 owner零更新即停止，终态原子清空 owner/lease。SQL mutation 函数的事件去重不能代替 HTTP 响应重放；reset/total-adjust 等 SQL 重放允许返回事件快照或当前 version，由 API 返回的仍必须是 `idempotency_records` 中首次业务响应。普通非秘密响应可写 `response_body`；API Key 创建等只在首次响应出现的 secret 必须 envelope-encrypt 后写 `response_body_envelope`，两者互斥。secret 不得进入明文 response body、日志、AuditLog、Outbox metadata 或 trace；204 允许两个 body 字段都为空。

函数用 SQLSTATE `P0001`，异常 message 是稳定业务 code。唯一冲突、`40001`、`40P01` 只允许在整个短事务未对上游产生副作用时由应用做有界指数退避；最多 3 次。已经向上游发送的 attempt 不得生成新 attempt_id 重试结算。

## 6. Reservation 状态机与租约

```text
pending/not-dispatched -- mark-dispatched --> pending/dispatched
          |                                      |
          | release/expire(0)                    | settle(actual)
          v                                      v
       released/expired                       settled
                                                 |
          dispatched lease expiry                | one Token correction
          -> expired + conservative fact --------+
                         |
                         +--> adjustment fact/event
```

`dispatch_started_at` 是 PostgreSQL 中不可逆的 dispatch/settlement fence，也是 `usage_attempts.dispatch_started_at` 的唯一时间语义；它表示持久 fence 已提交，不声称已经写出上游字节。逻辑 dispatch 状态只由该列派生：`NULL = not_started`、非 `NULL = started`；数据库不另存一个可漂移的 `dispatch_state` 列，活请求的四态 phase 只属于 Gateway `AttemptContext`。Gateway 必须先提交 `poolai_quota_mark_dispatched`，再发送任何上游 HTTP byte；此边界前确认未发送才可 release。fence 后若 Adapter 能证明零字节写出，或版本化 capability 明确认定 `401/403/429` 没有执行请求，必须 settle actual=0 且写 `usage_source=confirmed_no_execution`，不能伪装成 release。只有结果不确定时才按“可能已产生消耗”处理：到期事务把 estimate input/output split 写成 `usage_source=conservative_estimate` 的 immutable attempt，同时增加 consumed。它可能在“标记后、实际发送前崩溃”时保守多记，但不会少记并重新放出 Group 容量；后续只有凭可靠证据追加 adjustment 才能纠正。

`pending` 只能转成 `settled / released / expired` 之一。released 和 pre-dispatch expired 都证明没有越过 fence，不能追加上游 usage；dispatch 后的 conservative expiry 已经有 immutable attempt，adjustment 以该 estimate 为 previous。调用方的 attempt identity 参数必须回显这条 immutable base fact（包括同一个 `dispatch_started_at`），corrected Token/source/raw evidence 写 adjustment；若后得证据确认未执行，可用零 Token 的 `confirmed_no_execution` adjustment 抵消 estimate，但原 expired 终态不变。唯一 adjustment PK 保证只修正一次。

租约冻结如下：

- 非流式：初始 5 分钟，每 60 秒可续到“DB now + 5 分钟”，absolute maximum 为创建后 10 分钟；
- 流式：创建时 120 秒，Gateway 每 30 秒续租到“DB now + 120 秒”；
- 流式 absolute maximum：创建后 2 小时；
- 客户端断连：独立 drain context 最多 15 秒，期间继续读最终 usage 和续租；随后取消上游并用保守估算核销；
- Expiry Worker 扫描 `status='pending' AND lease_expires_at <= DB now`，逐条调用 expire；与 settle 竞争时由同一 quota/period/reservation 行锁决定唯一胜者。dispatch 后 expire 必须原子写 conservative attempt/counter/event/outbox；其中任何一步失败整笔回滚并保留 pending reservation。

`consumed >= total` 返回 `429 group_quota_exhausted`。若 `consumed + estimate > total` 返回 `group_quota_insufficient`；仅因并发 reserved 占用而放不下时立即返回可重试的 `429 group_quota_reserved` 与 `Retry-After: 1`，R1 不在数据库内等待。`reserved-full` 仅是 `consumed < total AND consumed + reserved >= total` 的内部派生标签，不持久化也不进入公开 Schema；此时公开 quota status 仍为 `active`，公开 status 始终只有 `active/exhausted/disabled`。

## 7. 周期、总量调整与对账

R1 只有管理员手工 reset，没有日/月定时 reset。调整和 reset 必须携带 `Idempotency-Key`、reason 与 `If-Match` 对应的 `group_token_quotas.version`。

- 订阅续期不重置 Group period。
- 降低 total 可以使 `consumed + reserved > total`，从而立即停止新预留；既有 reservation 不取消，仍在原 period 核销、释放或过期。
- reset 不迁移、不释放 pending reservation。旧 period 立即 closed，pending 永久引用旧 period并继续 renew/settle/release/expire；新请求只进入新 current period，迟到事实仍调整原 period。
- 账实对账以 `SUM(effective attempt tokens)` 和 quota event ledger 重算 period。发现 `reserved_tokens != SUM(pending.estimated_tokens)`、重复 attempt、缺 event/outbox 或 78 位边界风险时 fail closed、禁用 quota 并发 P0 告警，不自动覆盖事实。

## 8. Transactional Outbox、Inbox 与事件契约

`outbox_messages` 只承载跨 context 的版本化 Integration Event；进程内 Domain Event 不写 Outbox。它是 at-least-once 投递，不承诺外部 broker exactly-once。Publisher 必须从列构造并冻结完整 Envelope：`message_id(=id)/topic/event_type/schema_version/event_sequence/source_event_sequence/aggregate_type/aggregate_id/aggregate_version/deduplication_key/occurred_at/correlation_id/causation_id/payload/replay_of`，不得只发送 payload；nullable 字段也必须以 JSON null 保留键。`event_sequence` 是 Outbox 全局顺序，quota 消息的 `source_event_sequence` 对应 `group_quota_events.event_sequence`，其 `aggregate_version` 在 R1 明确为 null（quota ledger 用 source sequence 排序）。未知 `schema_version` 必须进入 dead/告警，不能猜测反序列化。

GroupQuota Published Language 只有 `topic=poolai.quota.v1`、`schema_version=1`。Envelope `event_type` 必须与 `payload.event_type` 一致，`source_event_sequence` 必须与 payload 及 ledger 一致。payload 的稳定公共字段为 `schema_version/event_id/source_event_sequence/correlation_id/group_id/period_id/event_type/delta_total_tokens/delta_consumed_tokens/delta_reserved_tokens/total_tokens/consumed_tokens/reserved_tokens/occurred_at/metadata`；`reservation_id/attempt_id/causation_id` 按事件可空，且 payload 内可空键可被省略，这与外层 Envelope 必须保留 null 键的规则不冲突。精确消费映射冻结如下：

| `event_type` | 生产触发 | Usage/其他 Consumer 语义 |
|---|---|---|
| `initialized` | 初始化 Group quota/current period | 失效 quota snapshot；不记 usage |
| `reserved` | 新 attempt 预留 | 预留生命周期/观测；不记 usage |
| `dispatch_started` | dispatch fence 已提交 | 不可逆边界/观测；不记 usage |
| `renewed` | pending reservation 续租 | 租约观测；不记 usage |
| `settled` | 已 dispatch attempt 以真实、本地估算、保守或零执行事实核销 | `attempt_id/reservation_id` 必填；通过 `IAttemptSettlementFactReader` 读 `usage_attempts`，按事实重算受影响桶 |
| `released` | pre-dispatch 明确未发送而释放 | 不记 usage；若存在 attempt fact 则是 P0 不变量破坏 |
| `expired` | lease 到期 | `payload.metadata.conservative_expiry=true` 时必须存在 `attempt_id/reservation_id` 和 immutable `usage_attempts`，按保守 fact 重算；false 表示 pre-dispatch 过期，不记 usage |
| `usage_adjusted` | dispatched-settled/expired fact 的唯一迟到修正 | `attempt_id/reservation_id` 必填；读 `usage_attempt_adjustments` 并从 base fact 重算原完成小时桶，不直接对 delta 累加 |
| `total_adjusted` | 管理员调整 current total | 失效 quota snapshot；不记 usage |
| `period_reset` | 关闭旧 period 并开新 period | 切换 quota snapshot/period；不记 usage，旧 pending 仍归旧 period |

R1 的 .NET 合同可实现为 `GroupQuotaEventV1` discriminated union，不得另发一套 `AttemptUsageRecordedV1/AttemptUsageAdjustedV1` 名称的重复消息。未知 `event_type`、映射所需 ID/metadata 缺失、Envelope/payload 值不一致，或事件声称与权威 fact 不一致时，Consumer 必须停止该分区、进 dead 并发 P0 告警，不得推进 checkpoint。

Outbox 状态机为 `pending -> processing -> published/dead`。Worker 用 `FOR UPDATE SKIP LOCKED` claim due pending 或 lease 已过期的 processing 行，写随机 UUID `locked_by`、DB `locked_until`、递增 attempts，并在每次 claim/takeover 执行 `lock_generation + 1`。heartbeat、publish 成功、失败回 pending 或进入 dead 都必须以 `WHERE status='processing' AND locked_by=:owner AND lock_generation=:generation` CAS；数据库 Trigger 同时拒绝 generation 回退、无 claim 递增和终态再写。达到配置上限后写 dead/dead_at/脱敏 last_error；安全重放创建新 message/dedup key 并设置 `replay_of`，不修改 dead 原行。发布成功但确认 UPDATE 前崩溃会重复发送，这是预期 at-least-once 行为。

每个 durable Consumer 在处理消息的同一数据库事务中先插入 `inbox_messages(consumer_name,message_id,topic,event_sequence,schema_version,payload_hash)`，再写业务投影并推进 checkpoint；PK/sequence 唯一冲突且 hash 相同表示重复成功，hash 不同是 P0 契约破坏。不得先提交 Inbox 再更新投影。R1 同库 Usage Projector 也使用此模式，因而 advisory lock 只是减少竞争，不是幂等真相。

## 9. `/v1/usage` 聚合

`group_quota_periods` 的 total/consumed/reserved 是实时权威，remaining 在查询时取 `max(total-consumed-reserved, 0)`。趋势不扫描事实表：

- `group_usage_hourly` 唯一键 `(group_id, period_id, bucket_start)`；
- `account_usage_hourly` 唯一键 `(group_id, account_id, period_id, bucket_start)`；
- `aggregation_watermarks` 以 `(projector_name, partition_key)` 做 lease/checkpoint UPSERT。

Projector 只以 `poolai.quota.v1` Outbox 的 `event_sequence` 为消费游标；在一个短事务内插 Inbox receipt、从 request/attempt/adjustment 事实重算受影响小时桶并 UPSERT、再以 owner/version CAS 推进 `aggregation_watermarks`。不得在 checkpoint 提交后另开事务写桶。每个小时桶按 `completed_at` 重算，不做脆弱的“收到一次就加一次”。迟到 usage 或 adjustment 重算原完成小时的 Group 与 Account 两个桶。effective Token 取 adjustment corrected 值，否则取 attempt 原值。`request_count` 按 request 去重，`attempt_count` 按 attempt，`failover_count` 为同一 request 的额外真实上游 attempt 数；released 且未发上游的 reservation 不计 attempt，dispatch 后 conservative expiry attempt 计入。

`/v1/usage` 返回当前 API Key 所属 Group 的整个池，不接收客户端 group_id，不返回用户、Key 或 Account 明细。quota 是强一致值；hourly 趋势带 `data_through`，目标新鲜度不超过 60 秒。

## 10. 删除与历史保留

| 对象 | 退役方式 | 物理删除策略 |
|---|---|---|
| User | status=disabled + deleted_at | 有 Subscription/Key/Usage 时 FK RESTRICT；邮箱身份不复用 |
| Group | status=archived + deleted_at | quota、subscription、usage 全部 RESTRICT |
| Account/Channel/Template | status=retired + deleted_at | Account 先清理全部 enabled binding、Channel 先清理全部 non-null Supply Configuration 引用；历史 route/usage 继续 RESTRICT |
| API Key | status=revoked + reason/time | group_id 永久不可改；不物理删 |
| Subscription | status=revoked | canonical 行更新并审计；不物理删 |
| Refresh/one-time token | rotate/revoke/use/自然过期 | 仅按安全保留策略批量清理 |
| Usage/Event/Audit | 无软删除状态 | append-only Trigger 禁止 UPDATE/DELETE |

Reservation 直接外键到历史 `channel_id`，而不是依赖当前 Group Supply Configuration；因此 Supply 后续切换 configured Channel 不会篡改或阻断历史 attempt/settlement。GroupAccount 对 Group Supply Configuration/Account 均为 RESTRICT，configuration 不提供 DELETE，退役对象和历史绑定不能被级联误删。

0001 的全部 Trigger function、0003 的 quota entry point 和 0004 的 Identity 更新 entry point 都固定 `search_path = pg_catalog, public, pg_temp`（显式把临时 schema 放最后），使调用会话中的同名临时表不能 shadow canonical 表。0003/0004 把这些 entry point 的 owner 设为 `poolai_runtime_owner NOLOGIN`、启用 `SECURITY DEFINER`，并撤销 PUBLIC 和三个运行时角色的 schema CREATE；API/Worker 禁止直接或间接成为 runtime owner 成员。PostgreSQL 的 `FOR UPDATE/SHARE` 要求每个锁表至少一列 UPDATE 权限，因此 runtime owner 仅取得 QuotaAdmissionSnapshot 与原子 Identity 更新所需 canonical 列的 SELECT/锁/更新权限，不能读取 password/TOTP/API-key digest 或 Account credential，也不能业务修改 Supply Configuration。`poolai_api` 是 R1 合并部署的 Identity/Admin/Gateway 角色：password/TOTP、token/key digest、Account credential 和 idempotency secret response 的 SELECT 仅因这些明确用例保留，但 Email delivery、Outbox 和 Inbox 内部表不在其读取白名单；0004 撤销 `user_roles` 的直接 UPDATE/DELETE，只授予 `poolai_identity_update_user` EXECUTE。API 对 `usage_requests`、`email_outbox` 只有“初始行字段”的列级 INSERT，对 Supply Configuration/binding 也只有创建与受控可变列权限，不能改稳定 identity/created_at、物理删除、跳过单调 version 或伪造终态，并且只能经函数修改其余权威 quota/reservation/usage fact。单一 API 数据库角色不能证明进程内模块 owner，因此独立 DbContext/Repository 与 Architecture Test 仍必须证明只有 GroupQuota 写 Group、只有 Supply 写 Supply Configuration/bindings。`poolai_worker` 只能读取具体 job 所需列；Email Sender 的邮件 envelope 与 Supply Health 主动鉴权探测的 `accounts.credential_envelope` 是两个显式敏感例外，它仍不能读取 User authentication、token/API-key digest 或 idempotency response envelope。Worker 只能经 expire/adjust 函数修改 quota 事实；两者直接 UPDATE `usage_requests`、`group_quota_periods`、`group_token_reservations`、`group_quota_events`、`usage_attempts` 都必须得到 permission denied。Worker 的直接写权限仅覆盖 Outbox/Email claim 与终态及其失败分类事实、Inbox receipt、Outbox 安全 replay 的 pending 新行、Account health/cooldown、派生小时聚合和 watermarks。连接应用的数据库角色不授予 DDL、事实表 DELETE 或绕过 Trigger 的权限。合规清理必须由单独受审计的维护流程执行，不由 API 暴露。

0005 撤销 API 对 `one_time_tokens` 的表级 INSERT/UPDATE，重新授予 password-reset/TOTP 所需的显式列：insert 后 token identity/hash/到期时间/challenge 快照均不可改，只能更新 `used_at/revoked_at/revoke_reason/response_body_envelope`。0005 还撤销 0003 曾授予 API 的 `one_time_tokens/refresh_sessions` DELETE，防止物理删除 challenge、rotated refresh 或撤销事实；`totp_recovery_codes` 同样只有显式 SELECT/INSERT/UPDATE 列，不授予 DELETE。Worker/runtime owner 都不能读取 `one_time_tokens.secret_envelope/response_body_envelope` 或 `totp_recovery_codes.code_hash`；迁移本身以权限审计 fail closed。

0006 撤销 0003 曾授予 `poolai_api` 的 `users` 表级 UPDATE，只重新授予 password/TOTP/security stamp、token version、登录失败计数、锁定与最后登录时间这些会话安全列。`status` 与 `display_name` 不再能被 API 数据库角色直接修改，必须经过 `poolai_identity_update_user`，从而保留 expected-version、最后一名 active Admin、角色锁序和单次 `token_version` 推进约束。创建 User 后的唯一角色 INSERT 仍需推进安全版本，因此固定搜索路径的 role trigger 改由具备窄列权限的 `poolai_runtime_owner NOLOGIN` 以 `SECURITY DEFINER` 执行，而 `poolai_api` 不重新获得 `version/updated_at` 直写权限。0006 同时把 `auditor` 加入 append-only `audit_logs.actor_type`，且要求其与其他用户 actor 一样携带 `actor_user_id`；Auditor 本人安全操作不得再折叠为 `user` actor。

0007 撤销 0003 曾授予 `poolai_api` 的 Group、Template、Subscription 表级 INSERT/UPDATE，并撤销 API 对旧 `poolai_quota_initialize/reset/adjust_total` 的直接 EXECUTE；初始化只能由 `poolai_group_create` 在新 Group 事务内复用该函数，避免旧 Group → Quota initialize 与 Quota → Group archive 形成锁序环，M3 的 reset/total 管理则必须由后续前向迁移提供带 Group 归档门禁的新入口。Gateway 的 reserve/renew/settle/release 与 Worker 的 expire/late adjustment 保留，以便已准入历史 attempt 在归档后仍能完成或纠正。NOLOGIN owner 只取得这些入口所需列和既有 Group activation guard 所需的 `channels.model_rules` 只读列；API/Worker/PUBLIC 不能继承 owner 或调用未列入 allowlist 的入口。ADR 0006 的完整 registry 只有三个 family：quota admission/route identity、activation guard、Group–Subscription lifecycle fence。Family C 只授权前述六个函数体中的精确跨 Context SELECT/行锁，不把 runtime owner 的 Group/Template/Subscription owner 权限转化为其他函数的访问许可。迁移内权限审计和 Architecture Test 必须在任一宽表写权限、额外跨 Context function/table/field、跨 owner UPDATE、dynamic SQL、函数 owner、`SECURITY DEFINER`、固定 search path、锁序/等待后 DB clock 或 EXECUTE allowlist 漂移时 fail closed；ADR 仍为 Proposed 时不得宣称架构签核完成。

## 11. 最低数据库验收

发布前至少用真实 PostgreSQL 18 + Testcontainers 验证：

1. 预置三个运行时角色后，空库按 0001/0002/0003/0004/0005/0006/0007 执行；验证缺角色、runtime owner 可登录、重复 migration 与 checksum 漂移都被拒绝。
2. 100 个并发 reserve 对同一 Group 不超放；不同 Group 可并行。
3. revoke 与 reserve 两种提交顺序都符合线性化规则，Redis 正缓存不能绕过。
4. reserve/mark-dispatched/renew/settle/release/expire 的每条合法与非法状态转换；dispatch 后 release 必须拒绝；零字节或 capability 认可的 401/403/429 只能以 actual=0 + `confirmed_no_execution` settle，其他状态/非零 Token 必须被约束拒绝。
5. stream 30 秒续租、120 秒 lease、2 小时上限、15 秒断连 drain。
6. 同一 attempt/idempotency 重放不重复改变 counter/event/outbox；不同参数冲突。
7. settle 与 expire 竞争只有一个终态；dispatch 后 expire 原子增加 consumed estimate 并写 conservative immutable attempt，后得真实/零执行证据只 adjustment 一次且原终态不变。用 kill 注入覆盖“mark 后发送前”和“上游已接收但 settle 前”两个崩溃窗，均不得低记或放出已保留容量。
8. 有 pending 时 reset 仍成功；旧 reservation 留旧 period 结算，新请求只写新 period。
9. numeric(78,0) 边界、78 位溢出事务失败且无部分写入；公开累计字段是字符串。
10. 小时聚合重建、迟到桶回算、水位恢复，以及 quota 权威值不依赖 projector；Inbox receipt、桶 UPSERT、checkpoint 任一点故障回滚后重放结果一致。
11. API Key 组不可变、跨 Group route/check 失败、事实复合 FK 拒绝错接 identity。
12. password-reset token/email 同事务；重复 forgot 原子撤销旧 token 后只创建一个新有效 token，disabled/no-user 不写 token/outbox，Current/Previous pepper 与 DB clock/单次 CAS 正反路径通过。Email pending/processing/sent/dead、每次 claim/takeover generation 递增、旧 owner 晚到 CAS 零更新、稳定 Message-ID 和终态清密文通过。TOTP login/setup challenge 必须验证 kind/安全快照/envelope 约束、每 User/kind 仅一条 open 行、setup 幂等恢复响应、8 个 recovery-code hash、同 code 并发只消费一次、disable/re-enable 全部撤销和 TOTP step 防重放；Refresh 必须验证单次 rotation、并发重放 family revoke、当前/全部会话撤销及数据库到期边界。Envelope v1 还必须覆盖字段缺失/多余、错误 alg/kid/base64 长度、AAD 跨实体/字段复制和 tag 篡改，全部 fail closed。
13. 双连接锁等待时钟门禁：连接 A 锁住 quota，连接 B 在 Subscription/Key 到期前发起 reserve；等到期后释放 A，B 必须按锁后时间拒绝且无 reservation/event/outbox。另用 A 锁 quota/period，让 B 的 renew 等到 lease 过期后再继续，必须返回 `reservation_lease_expired` 且 lease/event/outbox 不变。
14. 分别以真实 `poolai_api`、`poolai_worker` 登录：确认二者不是 runtime owner 成员，且 runtime owner/API/Worker 均没有（含继承的）public schema CREATE；用同名临时表验证 `pg_temp` 不能 shadow canonical 表；`poolai_group_create`、reserve、mark-dispatched、settle、release 与 `poolai_identity_update_user` 等授权函数在行锁下可成功；0007 后 API 直调旧 `poolai_quota_initialize`、`poolai_quota_reset`、`poolai_quota_adjust_total` 必须 permission denied，后续只能由保持 Group→Quota 锁序且拒绝 archived Group 的受控入口承载这些管理命令；API 直接 UPDATE/DELETE `user_roles` 必须 permission denied，角色/status 只能经原子函数；API 对 `usage_requests`/`email_outbox` 只能 INSERT 初始字段，显式写终态列以及直接改 request、邮件投递状态、quota counter、reservation、event 或 usage fact 必须 permission denied；API 不能调用 worker-only usage-adjust/expire，Worker 不能调用 reserve/mark-dispatched/reset/settle。用 `information_schema.column_privileges` 与实际 SELECT 双重断言 Worker、runtime owner 均不能读取 `users.password_hash/totp_secret_envelope`、`one_time_tokens.secret_envelope/response_body_envelope`、`totp_recovery_codes.code_hash`、`api_keys.secret_hash`、`idempotency_records.response_body_envelope`，且 runtime owner 不能读 `accounts.credential_envelope`；API 必须只具有 M1-E2 TOTP challenge/recovery 所需列的 SELECT/INSERT/UPDATE，不能 DELETE recovery code；Supply Health Worker 必须能读该 Account envelope 并通过错误 AAD/未知 key 的 fail-closed 探测测试；API 也不能读取 Email delivery envelope、Outbox 或 Inbox。
15. Account/Channel 默认 disabled、unknown health 不可激活 Group、future cooldown 不可激活/预留，只有 ready Group Supply Configuration、healthy/degraded 且 cooldown 已过可进入新 attempt；直接 SQL 激活缺少 opaque evidence 或当前 readiness 时由只读 guard 拦截。激活提交后 Supply 改变不得改写 Group/evidence/version，Gateway 每 attempt 的 Configuration/Account canonical 强读和 Redis Account lease/cooldown 门禁仍须独立通过。
16. HTTP 幂等短 UoW 在提交前 kill 必须使领域写/idempotency/audit/outbox 全回滚；owner/generation 接管与旧 owner 晚到 complete 只有新 owner CAS 成功。
17. 通用 Outbox 覆盖完整 Integration Event Envelope（包括 null aggregate_version/replay/causation）、重复发布、processing lease 接管、claim generation 递增、旧 owner 晚到提交失败、poison dead、`replay_of` 新消息和未知 schema；对 `GroupQuotaEventV1` 的 10 个 event_type 各做正向 fixture，并对 Envelope/payload 不一致、缺 ID、conservative/pre-dispatch expired 做反向 fixture，只有 settled/conservative expired/usage_adjusted 改变 Usage 投影；Consumer Inbox 对同 message 重放只更新一次，篡改 payload hash 必须告警并拒绝；Domain Event 不得出现在 Outbox。
18. 首次 request INSERT 与 reserve 在同一事务的任一点 kill 都必须全部回滚；reserve 失败不得留下 accepted 孤儿，failover 复用原 request 并只新增下一 attempt reservation。
19. Group 与 Group Supply Configuration 的创建/更新分别验证独立 `Idempotency-Key`/ETag；直接 UPDATE configured Channel 若未把 Supply version 恰好推进一次必须被 Trigger 拒绝，稳定 `group_id/created_at` 与 binding identity 的越权 UPDATE、两类物理 DELETE 必须 permission denied；Channel 或多行 binding replacement 只推进单调 Supply version（允许跳号），绝不推进 Group version。每个 binding mutation command 必须先 `FOR UPDATE` 锁 Configuration，Repository 不暴露绕过该预锁的写方法；双连接并发替换/启停 binding 不得死锁或丢失 version。激活必须原子记录 opaque token/observed_at；Group `If-Match` 不能授权 Supply 写，反之亦然。
20. bootstrap 只成功一次且只写 `poolai-password-v1:`；未知 hash prefix 拒绝。角色切换只有一条 UPDATE/一次 token_version 跃迁；两个并发事务分别尝试 disable/降级最后两个 active Admin 时，固定 transaction advisory fence 使至多一个成功且最终至少一名 active Admin。User cursor 在相同 `created_at`、跨页新增和非法 payload 下仍无重复/跳项并按合同拒绝无效输入。
21. Account 有任一 enabled binding 时退役稳定返回 `account_in_use`，Channel 有任一 non-null Supply Configuration 引用时退役稳定返回 `channel_in_use`；先 PATCH 清理/禁用后才成功。使用双连接分别竞争“Account retire vs binding enable/insert”和“Channel retire vs Configuration 引用”，每种提交顺序都必须只有合法一方成功，数据库不得出现 retired 资源的新 enabled/non-null 引用；判定不得查询或依赖 Group status。
22. 以真实 `poolai_api` 证明 Group/Template/Subscription 直接 INSERT/UPDATE 均 permission denied，七条 M1-E4 函数由 NOLOGIN owner 持有、固定 search path 且 EXECUTE allowlist 精确；Architecture Test 另须证明 ADR 0006 恰有三个 family：Family A 的 `poolai_quota_reserve` canonical admission 字段白名单及 settle/mark-dispatched/adjust-usage 仅 `accounts/channels(id,provider)`，Family B 的 activation guard 精确 Supply readiness 字段，Family C 的一个双向 lifecycle fence（`poolai_group_update` 加五条 SubscriptionAccess mutation，跨 Context 字段恰为 `subscriptions(group_id,status,expires_at)` 与 `groups(id,status)`）。任一未知 function/table/field、跨 Context INSERT/UPDATE/DELETE/MERGE、dynamic/general executor、锁序或等待后 DB clock 漂移都必须失败。覆盖 NULL/陈旧 version CAS、Template/Group 终态、snapshot、未知 User/分配者返回 `conflict`、revoked regrant 授权位、`expires_at = T` 不写回、归档 active/scheduled blocker，以及双连接“Subscription 先提交使 archive blocked / archive 先提交使晚到 mutation 被 fence”。另以双连接交叉交换同 Group 两个 Template 名称，证明 Group fence 串行化后稳定返回 `conflict` 而非 PostgreSQL deadlock；锁住 Subscription 直到有效期跨界，resume 必须按锁后 DB 时间拒绝。ADR 0006 未由 `@Lyon1984` 明确签核前，本项只能作为候选实现证据，不能完成 M1-E4 architecture sign-off。

本环境若没有 PostgreSQL 18/`psql`，文本静态检查不能替代以上 Testcontainers 门禁；CI 中的空库 apply 和并发事务测试是合并 Release 1 数据层的必要条件。
