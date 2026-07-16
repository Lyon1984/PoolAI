# PoolAI 开发执行包 v1.0

状态：**Release 1（R1.1 GA）开发基线**  
冻结日期：2026-07-14  
目标后端：.NET 10 / ASP.NET Core 10 / EF Core 10 / Npgsql 10 / PostgreSQL 18 / Redis

本目录把 [`系统重构方案-v1.0.md`](系统重构方案-v1.0.md) 中的目标态收敛为可版本化、可测试、可拆分任务的执行契约。重构方案负责能力取舍、目标架构、工作流、交付顺序、切换和验收追踪；开发不得只根据上游 Go 源码猜测目标行为。

## 仓库导航

- 仓库目录与项目边界：[`architecture/repository-structure.md`](architecture/repository-structure.md)
- 系统重构目标、路线和门禁：[`系统重构方案-v1.0.md`](系统重构方案-v1.0.md)
- Codex/贡献者协作规则：[`../AGENTS.md`](../AGENTS.md)
- 项目维护记忆与当前状态：[`project-memory/README.md`](project-memory/README.md)
- Release 1 DEC/AC 到证据或计划测试的机器索引：[`traceability/README.md`](traceability/README.md)
- M0–M7 全部 Epic 的已验证 GitHub Issues 导入登记与索引：[`traceability/delivery-epics.json`](traceability/delivery-epics.json)
- R1.1 认证逻辑环境、参考硬件与负载证据归档约定：[`release-evidence/README.md`](release-evidence/README.md)

项目记忆只用于导航和交接，不能覆盖下列契约优先级。

## 1. 契约优先级

出现冲突时按下列顺序处理，禁止开发者自行选择一种实现：

1. HTTP 路由、字段、状态码、认证和 Header：以 [`contracts/openapi-v1.yaml`](contracts/openapi-v1.yaml) 为准。
2. 错误 code、重试性和流中错误：以 [`contracts/error-catalog.md`](contracts/error-catalog.md) 与 [`contracts/fixtures/`](contracts/fixtures/) 为准。
3. 表、列、约束、索引、数据库函数和运行时授权：以 [`database/0001_baseline.sql`](database/0001_baseline.sql)、[`database/0002_quota_functions.sql`](database/0002_quota_functions.sql) 和 [`database/0003_runtime_permissions.sql`](database/0003_runtime_permissions.sql) 为准。
4. 状态转换与数据库并发语义：以 [`database/README.md`](database/README.md) 为准。
5. Bounded Context、Clean/Hexagonal 依赖、Aggregate、CQRS、UoW、事件与 Composition Root：以 [`architecture/design-pattern-baseline.md`](architecture/design-pattern-baseline.md) 为准。
6. Redis key、Lua、TTL 与故障开闭：以 [`runtime/redis-contract.md`](runtime/redis-contract.md) 为准。
7. Release 范围、RBAC、配置、前端状态、SLO、验收和 Backlog：以 [`开发执行规格-v1.0.md`](开发执行规格-v1.0.md) 为准。
8. 重构目标、能力处置、实施工作流、切换策略和验收追踪：以 [`系统重构方案-v1.0.md`](系统重构方案-v1.0.md) 为准。

若高优先级资产内部或彼此矛盾，应先提交 ADR/契约修订并让相关资产在同一变更中同步，不能通过实现细节静默改变公开行为。

## 2. Release 1（R1.1 GA）冻结边界

R1.0 是内部工程与领域基础里程碑；Release 1 完成指 R1.1 通过 GA 发布门。它是全新 .NET 10 绿地部署，只交付一个可端到端验收的 OpenAI/Codex 垂直切片：

- 封闭开户、邮箱密码登录、可选 TOTP、Access/Refresh Token 轮换、SMTP 密码重置与数据库 email outbox；
- 用户、角色、API Key、Subscription、Group、独立 Group Supply Configuration、Channel、OpenAI/OpenAI-compatible Account 的最小控制面；
- `/v1/responses` 非流式与 SSE、`/v1/chat/completions`、`/v1/models`、`/v1/usage`；
- API Key 创建时固定绑定一个 Group；Subscription 只决定访问资格；
- Group 是唯一累计 Token 配额主体，按 Account/attempt 保存 Token 事实；累计事实可无损超过 JavaScript 安全整数；
- Group 总量调整、手工重置、reservation 回收、outbox/聚合、审计、健康检查和基础可观测性；
- Compose 拓扑：相互独立的 Api、Worker、一次性 Migrator、PostgreSQL、Redis；生产不在 Api 内嵌 Worker loop。

Anthropic Messages 属于 Release 1.2。Gemini、Antigravity、Grok、图片/视频、批任务、Responses WebSocket、上游 OAuth、第三方登录、外部权益同步、iframe、自助注册和既有 Go 数据迁移均延后。

支付、订单、退款、余额、价格、成本结算、返佣、Promo、兑换码、邀请码和可购买配额不属于任何目标版本，不能作为“预留模块”进入 Solution、Schema、配置或 API。

## 3. 版本与变更规则

- `openapi-v1.yaml`、错误目录和 JSON/SSE fixture 已由 [Issue #44 OpenAPI 批准评论](https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-4992036352) 完成合同基线签收；该签收不表示对应 endpoint、handler、adapter 或运行时行为已经实现或验收。
- `/v1` 内只能增加可选字段、可选 endpoint 或新的稳定 error code；删除、重命名、改变类型/状态码、收紧既有合法输入、改变 SSE event 形状均视为 breaking change，必须进入 `/v2` 或经过明确兼容窗口。
- `openapi-v1.yaml`、错误目录和 fixture 必须在同一变更中更新；contract test 对 fixture 做字节级或规范化事件级比较。
- 已在任何环境执行的 SQL migration 内容与 checksum 永不改写；修正通过新 migration 前向演进。`0001/0002/0003` 已由 [Issue #44 数据库批准评论](https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-4990098502) 完成首次实现签收，自该评论起进入不可变状态；后续修正只能新增前向 migration，不得改写已签收 SQL 或 checksum。
- 数据库、Redis 和公开 API 的 schema/version 必须进入唯一机器清单
  [`release-manifest-v1.json`](release-manifest-v1.json)。该文件只登记权威契约的版本、来源与
  SHA-256，不复制 SQL、Lua 或 OpenAPI 正文；构建门必须重新计算来源摘要并拒绝漂移。
- PostgreSQL 兼容窗口由清单的 `minimum_compatible_version` 到
  `maximum_compatible_version`（含）声明。Api/Worker 只读
  `poolai_schema_migrations`，要求数据库历史是清单 migration 列表的 checksum 完全一致前缀，
  且当前最高版本位于该窗口内；缺表、缺口、名称/checksum 漂移或未知未来版本均使 readiness
  失败，绝不触发迁移。
- Redis 兼容性由清单中的 Redis major、key schema version、contract SHA-256 和登记脚本
  `{name, logical_version, source, body_sha256}` 决定。共享 Redis 不写可被滚动版本互相覆盖的
  全局 manifest key，也不因存在额外 content-addressed script 而判定不兼容；每个 Host 只证明
  自身清单要求可用。M0 尚无业务 Lua，`scripts` 必须显式为空数组，M2 起随权威 Lua 正文前向登记。
- Api 通过 `/health/ready` 暴露上述结果且不影响 liveness；Worker 无公开 HTTP endpoint，必须在
  启动任何 job 前执行同一门禁并在不兼容或依赖不可用时非零退出。Api、Worker、Migrator 的
  清单或兼容窗口不匹配时不得继续放量。
- 新 error code 必须先加入目录；既有 code 的 HTTP status、retryable 和 Retry-After 语义不可静默改变。

## 4. 不可变设计决定

| ID | 决定 |
|---|---|
| D-001 | Group 是唯一累计 Token 配额主体；User、API Key、Subscription、Account 无个人累计配额。 |
| D-002 | 管理输入的 Group total 和单 attempt estimate 必须是 `1..9007199254740991` JSON number；无 unlimited。所有 Token 事实与累计值用 PostgreSQL `numeric(78,0)`/.NET `BigInteger`，quota/Group/Account 累计公开输出用最长 78 位十进制 string；OpenAI 单请求 usage 保持安全整数 number，实际用量绝不截断；78 位溢出使事务失败并发 P0 告警。 |
| D-003 | API Key 必须绑定一个 Group，绑定后不可修改；换组只能撤销并重建 Key。 |
| D-004 | 服务端生成 UUIDv7 request/attempt ID；attempt ID 全局唯一，且 `(request_id, attempt_index)` 唯一。 |
| D-005 | PostgreSQL quota/period 行是准入真相；Redis 不决定累计配额。任何依赖 PostgreSQL 的公开路径在数据库不可用时 fail-closed，并统一返回 `503 dependency_unavailable`。 |
| D-006 | 若 `consumed >= total` 返回 `429 group_quota_exhausted`；若即使所有 reservation 释放也无法容纳本 estimate，返回不可立即重试的 `429 group_quota_insufficient`；仅因在途 reservation 占用时返回 `429 group_quota_reserved` 和 `Retry-After: 1`。`reserved-full` 只是不持久化、不公开的内部派生标签；公开 quota status 仍只有 `active/exhausted/disabled`。 |
| D-007 | Release 1 只支持管理员手工订阅、手工 total 调整和手工 period reset。 |
| D-008 | 模型调用不提供 Idempotency-Key 响应重放；控制面变更使用 Idempotency-Key，所有版本化资源变更同时使用其自身资源的 If-Match。Group 与 Group Supply Configuration 使用独立强 ETag，任一 ETag 不得授权或失效另一资源。 |
| D-009 | API Key 只保存 HMAC-SHA256 digest、prefix 和 pepper version；完整值只在创建时返回一次。 |
| D-010 | 非流 reservation lease 为 300 秒、每 60 秒续租且总时长最多 600 秒；流 lease 为 120 秒并每 30 秒续租；最长流 2 小时；断连最多 drain 15 秒。 |
| D-011 | 订阅在请求准入时校验；已成功准入的请求不因请求中途到期而强制截断，后续 attempt/failover 仍需重新校验。 |
| D-012 | R1 数据库迁移只有 PoolAI.Migrator 一个 owner；API 与 Worker 不执行自动迁移。 |
| D-013 | R1 禁止跨 Group fallback，`routing_group_id = quota_group_id`；故障切换只更换同一 Group 内的 Account。 |
| D-014 | 每个 `(user_id, group_id)` 只有一条 canonical Subscription；分配、延长、撤销和恢复更新同一资源并保留审计。 |
| D-015 | R1 只有 Account lease 和 Group RPM 属于 Redis 硬协调，故障时 fail-closed；不实现 User lease；缓存可回源；任何 Redis quota snapshot 都不能参与累计配额准入。 |
| D-016 | 所有 JWT 认证的控制面请求都强读 PostgreSQL User status、role 和 token_version；角色或用户状态变更递增 token_version。 |
| D-017 | Worker 单 owner 使用 PostgreSQL session advisory lock；R1 不实现 Redis Worker leader lease。 |
| D-018 | 按 [`ADR 0001`](architecture/adr/0001-separate-group-quota-from-supply-configuration.md)，GroupQuota 独占 Group 生命周期与 Group version；Supply 独占 `group_supply_configurations` 及其 `group_accounts` 子项，Supply 写入不得改变 Group/ETag。无数据所有权的 `Application.Orchestration` 以版本化 `IGroupSupplyReadiness` 取得 opaque activation evidence，再让 GroupQuota 在自己的 UoW 中记录 evidence 并激活；GroupQuota 不解析 token，也不把它当 lease/capability。 |
| D-019 | R1 设置面只读；配置仅通过版本化部署配置与 Secret Provider 变更，不提供运行时修改 API。 |
| D-020 | Subscription、API Key 与一次性 Token 用 PostgreSQL clock；Account lease/RPM 用 Redis `TIME`；JWT 用注入 `TimeProvider` 且节点 NTP 同步、`ClockSkew=30s`；TOTP 步长 30 秒、接受 ±1 步并防同 step 重放。 |
| D-021 | Subscription 持久 status 只有 active/suspended/revoked；scheduled/expired 由 PostgreSQL clock 即时派生，不存在订阅到期状态写回 Worker。 |
| D-022 | 每个入站模型请求和每个 failover attempt 在同 Group 调度、Account lease 与 reservation 前，都强读 canonical Key/User/Subscription/Group 以及 Supply 绑定和状态。 |
| D-023 | Account/Channel lifecycle 均为 active/disabled/retired，Account health 独立为 unknown/healthy/degraded/cooling/unhealthy；已准入的当前 attempt 可完成结算，新 attempt/failover 必须强读拒绝。退役只依赖 Supply 自有引用：Account 仍有任一 enabled binding 时返回 `account_in_use`，Channel 仍被任一 non-null Supply Configuration 引用时返回 `channel_in_use`；必须先 PATCH 清理/禁用引用，Supply 不查询 Group 状态。 |
| D-024 | R1 使用同一模块化 Solution，但 Api、Worker 和 Migrator 始终是独立 Host；Api 不加载 Worker loop。 |
| D-025 | Api、Worker、Migrator 各自只有一个 Composition Root；完整 Clean/Hexagonal 依赖矩阵由 Architecture Test 强制，跨 Context 控制面用例只进入无数据所有权的 `Application.Orchestration`。 |
| D-026 | 一条 Command 只有一个本地 PostgreSQL Unit of Work；业务事实、audit、幂等响应和 integration outbox 同事务提交，外部 HTTP/Redis/SMTP/流式 I/O 不得持有该事务。 |
| D-027 | reservation 必须在发送任何上游字节前持久化 `dispatch=started`；只有 `not_started` 可零消耗 release/expire，`started` 的未知结果按 estimate 保守核销并由 adjustment 纠正。 |
| D-028 | `usage_requests`、`usage_attempts`、`usage_attempt_adjustments` 是 GroupQuota 独占写入的不可变结算事实；Usage 只通过版本化事件/只读 feed 构建可重建投影。 |
| D-029 | 非流数据面、SSE、控制面和 `/v1/usage` 使用独立有界 admission bulkhead；Account breaker 为 closed/open/half-open，重试/failover 受 phase、deadline、attempt/retry budget 共同约束。 |
| D-030 | 可逆秘密统一使用版本化 AEAD Envelope v1，AAD 绑定用途/实体/字段，decrypt keyring、轮换/重包裹、未知版本 fail-closed 和备份恢复均为发布门。 |
| D-031 | GroupQuota Published Language 只有 `poolai.quota.v1`/schema v1 的 `GroupQuotaEventV1` union；`settled`、conservative `expired`、`usage_adjusted` 驱动 Usage 事实重算，不另发同义 AttemptUsage 消息。 |
| D-032 | Account 与 Channel 的创建时 `provider` 是不可变 Supply 字段，只能为 `openai` 或 `openai_compatible`；公开 `platform: openai` 表示 R1 入站协议族，不是上游 provider selector。 |

改变上述决定必须建立 ADR，并同步 OpenAPI、SQL、状态机、测试 fixture 和执行规格。

## 5. 开发启动顺序

1. 先执行执行规格中的 M0 契约校验和决策签收。
2. 由平台先预置 `poolai_runtime_owner NOLOGIN`、`poolai_api`、`poolai_worker`，再由具备 owner 切换/授权能力的 Migrator 按 `0001_baseline.sql`、`0002_quota_functions.sql`、`0003_runtime_permissions.sql` 顺序建立空库，并生成 EF Core 映射；0003 不创建角色，API/Worker 不得执行迁移，也不得让 EF migration 反向改变 baseline。
3. M0 按 `runtime/redis-contract.md` 建立 Redis 连接、时间、versioned script 登记和测试框架，同时建立 Solution、模块依赖测试、配置启动校验、认证和审计基座；Account lease、Group RPM、breaker 的业务 Lua/TTL 与完整故障矩阵在 M2 实现并验收。
4. 实现控制面，再实现 Group reservation/settlement 与 `/v1/usage`。
5. 最后接入 OpenAI/Codex Gateway 垂直切片，并用 fixture、并发、故障和长流测试验收。
6. 达到执行规格的质量门、SLO 认证和 M6 生产门后才可标记 Release 1 可上线。

## 6. 完整性的含义

本执行包补齐后，团队可以创建 Solution、拆分 Epic/Story、并行实现 Release 1 并据契约联调；它不是“已经通过生产验收”。真实性能阈值、上游兼容和故障恢复仍必须由实现后的自动化测试、压测与演练证明，不能用文档评审代替。
