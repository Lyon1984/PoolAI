# PoolAI 软件设计模式基线 v1.0

> 状态：Release 1 冻结架构契约  
> 适用范围：.NET 10 后端 Solution、Api/Worker/Migrator Host、模块间协作与数据访问  
> 冻结日期：2026-07-14

本文冻结 PoolAI 对 DDD、模块化单体、Clean Architecture、Hexagonal Architecture、CQRS、Repository、Unit of Work、Composition Root 与事件模式的使用方式。它约束代码结构和依赖方向，不改变 OpenAPI、错误码、数据库字段或业务状态机；若实现需要突破本文边界，必须先建立 ADR，并同步架构测试和相关执行资产。

文中的“模块”默认指 `PoolAI.Modules.<Name>` 的实现程序集及其 `*.Abstractions`；“Host”指一个独立可执行进程；“写模型”指负责业务真相和不变量的模型；“读模型”指可重建、允许明确延迟的查询投影。

## 1. 总体原则与明确非目标

Release 1 采用 **模块化单体 + 多 Host**：业务模块位于同一 Solution、共享一个 PostgreSQL 权威域，但 Api、Worker 和 Migrator 是三个独立进程。模块边界必须在进程内也保持真实，不能因为共享数据库或 DI 容器而绕过公开端口。

冻结原则：

1. 每个业务模块对应一个 bounded context；一个 bounded context 可以包含多个 Aggregate，但 Aggregate 不能跨 context。
2. 依赖只指向内部策略或公开端口，框架与外部系统位于适配器一侧。
3. 写模型维护不变量；查询模型为使用场景优化，不反向决定写模型准入。
4. 一个 Command 至多拥有一个本地数据库 Unit of Work；不跨外部 HTTP、Redis、SMTP 或长流持有数据库事务。
5. 跨模块协作使用 `*.Abstractions`、不可变 Snapshot、强类型 ID 或版本化 Integration Event，不共享可变 Domain Entity、EF Entity、DbContext 或 `IQueryable`。
6. 数据表的写 owner 唯一；读取权限不构成业务所有权。

Release 1 明确不引入：

- 微服务拆分、独立模块数据库或网络 RPC；
- Event Sourcing；`group_quota_events` 是追加式账本/审计事实，当前 quota/period 行仍是命令侧状态，不通过重放事件作为日常写入入口；
- 分布式 Saga、补偿事务框架或跨模块两阶段提交；
- 通用 `IRepository<T>`、Generic Repository、共享 God DbContext、跨模块 Unit of Work 或返回 `IQueryable` 的 Repository；
- 通用 SQL executor/callback、`TransactionScope`/ambient transaction、`AsyncLocal` transaction lookup、静态/全局 connection registry；
- Service Locator、静态 `IServiceProvider`、模块内部调用 `BuildServiceProvider()`；
- 为使用设计模式而引入 MediatR、消息代理或反射扫描；这些库不是模式成立的前提。

## 2. Bounded Context 与 Context Map

### 2.1 一模块一 Context

| Bounded Context | 领域语言与责任 | 独占写入的业务事实 | 对外 Published Language |
|---|---|---|---|
| Identity | User、Role、Password/TOTP、Session、Refresh family、API Key、一次性 Token | 身份、安全状态、API Key HMAC、密码重置 token 与 email outbox | 最小身份/Key 状态 Snapshot、认证与签发端口 |
| SubscriptionAccess | Template、canonical Subscription、effective access | Template 和 `(user_id, group_id)` canonical Subscription | Access Snapshot、订阅命令结果 |
| GroupQuota | Group 生命周期、activation evidence、Quota Ledger、Period、Reservation、核销与纠错 | Group、opaque Supply readiness evidence、quota/period/reservation/event、`usage_requests`、`usage_attempts`、`usage_attempt_adjustments` 及 settlement event 语义 | Group/Quota Snapshot、Ledger 命令端口、版本化 settlement Integration Event |
| Supply | Channel、Account、credential envelope、模型映射、Group Supply Configuration、健康事实 | Channel、Account、`group_supply_configurations`、其 `group_accounts` 子项、能力/映射和持久健康事实 | Supply Configuration/Readiness Snapshot、候选 Snapshot、配置命令 |
| Routing | 同 Group 过滤、评分、粘性、Account lease 与容量协调 | Redis 中的 lease、RPM、粘性和短期协调状态；不拥有累计 Token 事实 | Router、Lease 与候选决策端口 |
| Gateway | 协议无关的 request/attempt pipeline、流生命周期、同 Group failover | 不直接拥有业务表；通过其他 context 的端口提交命令 | 规范化 request/event/error/usage 与 Adapter 端口 |
| Usage | Group/Account 查询、小时聚合、对账与读模型新鲜度 | `group_usage_hourly`、`account_usage_hourly`、`aggregation_watermarks` 等可重建投影；不拥有通用 Inbox 表 | Pool Usage 与 Account Usage Query DTO |
| Operations | Audit、通用命令幂等、outbox/inbox 投递状态、diagnostic、告警 | `idempotency_records`、append-only audit 和通用消息投递元数据 | 事务内 Idempotency Store/Appender、运维事件与只读诊断端口 |

以下是“一模块一 bounded context”的明确例外，它们不是新的业务 context：

- `*.Abstractions` 是所属 context 的 Published Language，不是独立 context。
- `PoolAI.Application.Orchestration` 是**无数据所有权**的跨模块应用编排项目，只依赖模块 `*.Abstractions`；它不包含 Domain、Infrastructure、DbContext、Repository 或 Endpoint。
- `PoolAI.Api`、`PoolAI.Worker`、`PoolAI.Migrator` 是 Composition Root/Host，不是业务 context。
- `PoolAI.Contracts`、`PoolAI.BuildingBlocks`、`PoolAI.Database.Migrations` 是共享技术资产，不得放入业务规则、业务枚举或可变领域对象。
- 按 [`ADR 0002`](adr/0002-introduce-shared-postgres-transaction-runtime.md)，`PoolAI.Infrastructure.Postgres` 是无业务所有权的共享技术 Infrastructure：只承载 Host-local `NpgsqlDataSource`、显式 UoW/context、专用连接的 session advisory lock 与技术诊断，不拥有表、业务 SQL、Repository、Entity、Migration 或业务 Worker。
- `PoolAI.Adapters.OpenAI` 是外部协议 Anti-Corruption Layer，不是上游供应或 Gateway context 的一部分。

`PoolAI.BuildingBlocks` 只允许 Result、强类型 ID 基础、时钟/事务相关的最小且 vendor-neutral 技术原语。任何只被一个 context 使用的概念必须留在该 context；跨 context 共享业务枚举属于违规。Npgsql/EF 类型、连接、事务与 concrete PostgreSQL context 不得进入 BuildingBlocks。

### 2.2 Context Map

| 上游/提供方 | 下游/使用方 | 关系与集成方式 | 禁止方式 |
|---|---|---|---|
| Identity | SubscriptionAccess、Gateway、控制面 Orchestrator | 同步 Open Host Service；只返回最小身份/Key Snapshot | 共享 User/ApiKey Entity 或查询 Identity DbContext |
| GroupQuota | SubscriptionAccess、Gateway、Usage | 同步 Group/Quota 端口；结算事实通过版本化 outbox 事件异步发布 | Usage 修改 quota，或查询方以聚合结果决定准入 |
| Supply | Routing、Gateway、Group Activation Orchestrator | 同步 Supply Configuration/Readiness/候选端口 | 共享 Account credential Entity、由调用方写 `group_supply_configurations` 或 `group_accounts` |
| Routing | Gateway | 同步策略/lease 端口；返回有生命周期的 lease handle | Gateway 直接写 Redis key 或复制 Lua |
| Gateway | OpenAI/OpenAI-compatible | Adapter/Anti-Corruption Layer，将 `platform=openai` 入站协议族转换到显式 `provider=openai/openai_compatible` 的上游能力 | Adapter 注入 DbContext、quota、Subscription 或 Redis，或把 platform 当作 provider selector |
| GroupQuota | Usage | Published Language + Transactional Outbox；at-least-once | 直接序列化 EF/Domain Entity，或依赖全局一次投递 |
| 各业务 Context | Operations | 事务内 Audit/Outbox Appender；投递与重试由 Operations Worker 负责 | Appender 自行开启/提交第二事务 |

所有同步 Snapshot 必须不可变、最小化，并带有需要的 `version`/观察时间。下游不得把 Snapshot 反序列化为上游 Aggregate，也不得长期缓存后用作授权真相。

Operations 拥有通用 `idempotency_records`、`outbox_messages`、`inbox_messages` 和 audit 的物理 Schema、投递状态和保留策略，并通过事务内 Idempotency Store port、`IAuditAppender`、`IOutboxAppender`、`IInboxReceiptAppender` 暴露只能加入调用方当前事务的端口；这些端口只接收不具备 commit/dispose 能力的 `IUnitOfWorkContext`。按 ADR 0002，当前 Command owner 通过 `IUnitOfWorkFactory.BeginAsync(...)` 取得可 `CommitAsync`/dispose 的 `IUnitOfWork`，再把其独立的 `Context` capability 交给参与者；`IUnitOfWork` 不继承或实现 `IUnitOfWorkContext`。调用业务 context 拥有幂等 scope/request 语义，产生事件的业务 context 拥有 `event_type`、payload 与发生时机的业务语义。事务内 Store/Appender 是“只能写自己表”规则的受控技术例外：生产者/消费者不能获取 Operations DbContext、自行提交或自行修改投递状态。Usage 只拥有投影与 `aggregation_watermarks`，通过 `IInboxReceiptAppender` 让 receipt、投影写入和 checkpoint 前进处于同一 UoW。

按 [`ADR 0006`](adr/0006-register-group-subscription-lifecycle-fence.md)，R1 的跨 Context 数据库只读/行锁白名单固定为三个 exception family：

1. **GroupQuota canonical admission 与 route/provider identity**。`poolai_quota_reserve` 可读取/锁定 `users(id,status,deleted_at,locked_until)`、`user_roles(user_id)`、`api_keys(id,user_id,group_id,status,expires_at)`、`subscriptions(id,user_id,group_id,status,starts_at,expires_at)`、`group_supply_configurations(group_id,channel_id)`、`group_accounts(group_id,account_id,is_enabled)`、`accounts(id,status,deleted_at,last_health_status,upstream_rate_limited_until,provider)` 和 `channels(id,status,deleted_at,provider)`；GroupQuota 自有的 request/Group/quota/period/reservation 行不构成额外例外。锁序固定从 quota row 开始，锁定 request/Identity/Subscription/Group identity；completed replay 在 current Supply 锁前返回，新 attempt 再按 Supply Configuration → binding/Account → configured Channel → period 取锁，最后读取 DB clock 并重检状态/时间/Channel。`poolai_quota_settle`、`poolai_quota_mark_dispatched`、`poolai_quota_adjust_usage` 仅可在 quota → period → reservation 后以 `FOR SHARE` 锁 `accounts(id,provider)` 与 `channels(id,provider)`，校验已冻结 route 的 provider identity，再读取 DB clock；不得读取 lifecycle/health/cooldown/credential/configuration/binding。这里对三个后续函数的登记是对已签 `0002_quota_functions.sql` 行为的文档纠正，不改写旧 migration。
2. **Group activation guard**。`poolai_validate_group_activation` 只读 GroupQuota 自有 current quota/period，以及 Supply 的 `group_supply_configurations(group_id,channel_id)`、`group_accounts(group_id,account_id,is_enabled)`、`channels(id,status,deleted_at,model_rules,provider)`、`accounts(id,status,deleted_at,last_health_status,upstream_rate_limited_until,provider)`；不锁定/写 Supply，不把点时 readiness 提升为跨 Context 永久不变量，正常入口仍经过 `GroupActivationOrchestrator` 并记录 opaque readiness evidence。
3. **Group–Subscription lifecycle fence**。`poolai_group_update` 仅在 archive 分支、持有 quota → Group fence 后读取 `subscriptions(group_id,status,expires_at)`；`poolai_subscription_template_create/update/retire` 与 `poolai_subscription_assign/update` 仅可读取/锁定 `groups(id,status)`，并按 `Quota → Group → Template/Subscription` 全局偏序取得所需行锁。等待后的时间判定重新读取 PostgreSQL `clock_timestamp()`。

三个 family 都绝不授权跨 Context INSERT/UPDATE/DELETE/MERGE、额外 function/table/field/lock direction、dynamic/general SQL executor、共享 DbContext/Repository 或复用到其他用例。ADR 0006 在 `@Lyon1984` 明确签核前保持 `Proposed`，候选 registry 不得作为 M1-E4 architecture sign-off 或 release-ready 证据。Architecture/Integration Test 必须证明完整函数/表/字段白名单、只读边界、锁序、等待后 DB clock、两种提交顺序和直接跨表写入被拒绝；除此之外不允许任何跨 Context 事务或表直读例外。

### 2.3 Group Activation Orchestrator

跨 GroupQuota 与 Supply 的激活用例固定由 `PoolAI.Application.Orchestration` 中的 `GroupActivationOrchestrator` 承担：

1. 接收 Api 从已认证主体绑定的 actor context，以及 `If-Match`、`Idempotency-Key` 和 reason；在应用层执行操作级 Policy，Api 不承载激活规则。
2. 通过 `GroupQuota.Abstractions` 强读 Group、current quota period 和版本前置条件。
3. 通过 `Supply.Abstractions` 获取当前 `SupplyReadinessSnapshot` 及其 opaque、版本化 token 和数据库观察时间；快照要求配置了启用模型映射的 Channel，以及至少一个 active 且 healthy/degraded 的已绑定 Account。
4. 调用 GroupQuota 的 activation command，以 expected Group version 在 GroupQuota 自己的单一 UoW 内锁定、重检 Group/quota 状态，写 active 状态、`activation_supply_readiness_token`、`activation_supply_observed_at`、幂等记录、审计和 outbox。GroupQuota 只持久化/发布 evidence，不解析 token，也不把它当作 lease 或 capability。
5. Orchestrator 不打开共享事务，不持有 DbConnection，不直接写任一模块的数据。

Readiness 是激活时的点时前置条件，不是跨 context 永久不变量。数据库 guard 在 activation mutation 时做最后一次只读防绕过检查；若 Supply 在激活提交后发生变化，已提交的 Group 状态和 evidence 不被 Supply 改写，Group 不自动回滚或自动 disable；后续每个新 attempt/failover 仍按既有契约强读当前 Supply Configuration，当前无可用供应时安全拒绝并告警。这个流程不是分布式 Saga，也不引入补偿事务。

Group 与 Group Supply Configuration 是两个独立 versioned resource：Group `If-Match` 只授权 GroupQuota 命令，Supply Configuration `If-Match` 只授权 Supply 命令。两者使用独立强 ETag；Supply Configuration 的 Channel/绑定变化只推进自身单调 version，不推进 Group version，也不使 Group ETag 失效。多行绑定替换可以让 Supply version 跳过多个整数，客户端只能比较相等性，不得假设每次恰好加一。

Supply Configuration Repository 必须以 Aggregate Root 为唯一写入口：configured Channel 或任一 binding mutation 都先以 expected version `FOR UPDATE` 锁 Configuration，再写子项并返回最终 version；不得暴露可绕过 Root 预锁的单行 binding writer。Architecture Test 冻结端口形状，双连接 Integration Test 冻结无死锁、无 lost update 的锁序。

Account/Channel 退役前置条件只读取 Supply 自有事实，不查询 Group lifecycle：任一 `group_accounts.is_enabled=true` 都阻止对应 Account 退役并映射 `account_in_use`；任一 `group_supply_configurations.channel_id IS NOT NULL` 引用都阻止对应 Channel 退役并映射 `channel_in_use`。管理员必须先以 Supply Configuration PATCH 清理/禁用引用，再执行退役；不得为判断“active Group”引入新的跨 Context 读取或 Orchestrator。

任何其他同时需要两个 context 的控制面用例也必须放入 `PoolAI.Application.Orchestration`，但只有确实跨 context 的用例才可进入；单模块命令不得经由它转发形成 God Application Service。

## 3. Clean/Hexagonal 依赖规则

### 3.1 允许的依赖矩阵

| 逻辑层/项目 | 可以依赖 | 不得依赖 |
|---|---|---|
| Module Domain | BCL 与本模块最小 Domain primitives；必要时引用最小 BuildingBlocks | Application、Infrastructure、Endpoints、ASP.NET Core、EF/Npgsql、`PoolAI.Infrastructure.Postgres`、Redis、SMTP、HTTP Adapter、其他模块 |
| Module Application | 本模块 Domain、本模块 Application Ports、其他模块 `*.Abstractions`、最小 BuildingBlocks | 本模块/其他模块 Infrastructure、Endpoints、DbContext、EF/Npgsql、`PoolAI.Infrastructure.Postgres`、Redis/SMTP 实现、ASP.NET transport |
| Module Infrastructure | 本模块 Application Ports 与 Domain、必要的 EF/Npgsql/Redis/SMTP/加密实现；可引用 `PoolAI.Infrastructure.Postgres` | Endpoints、其他模块实现或其 DbContext/Entity；不得把共享 PostgreSQL runtime 当业务 SQL/Repository hub |
| Module Endpoints | 本模块 Application use case、`PoolAI.Contracts`、ASP.NET Core transport | `PoolAI.Infrastructure.Postgres`、DbContext、Npgsql/SQL、Redis、Infrastructure 实现、Domain Repository |
| `*.Abstractions` | BCL、最小 BuildingBlocks | 模块实现、Infrastructure、`PoolAI.Infrastructure.Postgres`、Contracts、EF/Npgsql、外部 SDK |
| `PoolAI.Application.Orchestration` | 模块 `*.Abstractions` | 任一模块实现、Domain、Infrastructure、`PoolAI.Infrastructure.Postgres`、DbContext/Npgsql、外部 SDK、Endpoint |
| `PoolAI.Infrastructure.Postgres` | `PoolAI.BuildingBlocks`、Npgsql 与无业务规则的配置/诊断依赖 | 业务模块/Abstractions、Contracts、EF Entity、业务表 SQL/Repository/Migration、Endpoint |
| Gateway Adapter | `Gateway.Abstractions`、BCL/必要 HTTP SDK | 模块实现、DbContext、Redis、quota/Subscription 事务、Endpoint |
| Host Composition Root | 本 Host 允许加载的模块实现、Adapter、配置/观测基础设施；Api/Worker 可注册 `PoolAI.Infrastructure.Postgres` | 业务规则、SQL/Redis 业务操作、跨 Host ServiceProvider；Migrator 不得注册 runtime application UoW |

Domain/Application/Infrastructure/Endpoints 即使暂时位于同一实现程序集，也必须按 namespace 依赖图执行；“在同一项目所以可以引用”不是例外。若 Architecture Test 无法可靠阻断反向引用，应把逻辑层拆成独立项目，而不是放宽依赖规则。

### 3.2 Ports 与适配器

- Inbound Port 表达一个 Application use case；Endpoint、Worker job 或 Orchestrator 是调用方。
- Outbound Port 表达 Application 所需能力；EF/Npgsql、Redis、SMTP、Secret Provider、外部 HTTP 是实现该 Port 的 Adapter。
- Port 使用领域 ID、Value Object、Command Result 或最小 Snapshot；不得暴露 `DbConnection`、`DbTransaction`、`DbContext`、EF Entry、HTTP request/response 或厂商 SDK 类型。
- `IUnitOfWorkFactory.BeginAsync(...)`、`IUnitOfWork` 与 `IUnitOfWorkContext` 位于 vendor-neutral BuildingBlocks；`IUnitOfWork` 暴露独立 `Context` 并独占 commit/dispose capability，不能实现或继承 `IUnitOfWorkContext`。Handler 只把 `unitOfWork.Context` 传给 Repository/Appender。
- 模块 Infrastructure adapter 可以把该中立 context type-check 为 `PoolAI.Infrastructure.Postgres` 提供的 concrete PostgreSQL transaction capability，以复用既有 connection/transaction；不匹配必须在任何写入前 fail-closed，禁止 fallback 新 connection/transaction。该 concrete capability 不能反向出现在 Port 签名。
- 外部协议变化只进入 Adapter 和 Gateway 规范化映射；Domain/Application 不使用 OpenAI DTO 作为自己的模型。
- `CancellationToken` 沿端口传播，但数据库 commit、断连后有界 drain 等必须使用已冻结的独立生命周期规则，不能让 transport cancellation 破坏核销。

## 4. 每个 Host 一个 Composition Root

“唯一 Composition Root”按**每个进程**解释，而不是整个 Solution 只有 Api 一个根：

| Host | 唯一 Composition Root | 允许组合 | 明确禁止 |
|---|---|---|---|
| Api | `PoolAI.Api/Program.cs` | Api 所需模块、`PoolAI.Application.Orchestration`、`PoolAI.Infrastructure.Postgres` runtime、OpenAI Adapter、HTTP/观测基础设施 | Worker loop、DDL runner、在 Program 中编写业务规则 |
| Worker | `PoolAI.Worker/Program.cs` | `PoolAI.Infrastructure.Postgres` runtime/session lock、email/outbox、reservation、Supply health、Usage projection、告警等 Worker 能力 | 公开 Endpoint、协议 Adapter、Api ServiceProvider、DDL runner |
| Migrator | `PoolAI.Migrator/Program.cs` | Database.Migrations、migration-specific lock/history/checksum、受限 bootstrap writer | runtime application UoW、Redis、Gateway、Worker loop、运行时模块业务服务 |

模块通过显式 `AddIdentityModule()`、`AddGroupQuotaModule()` 等扩展注册。Api/Worker 在各自 Host 根部显式注册独立的 PostgreSQL data source/UoW runtime；不得跨进程共享 data source、connection、UoW 或根 `IServiceProvider`。注册扩展只做 binding、Options 校验和健康检查注册，不解析服务、不执行数据库写入、不启动后台循环，也不得调用 `BuildServiceProvider()`。对象图只能在对应 Host 根部闭合；测试可以建立测试专用 Composition Root，但生产模块不得感知它。Migrator 仍只有 migration-specific 数据路径，不以共享 runtime application UoW 取得 Schema 所有权。

## 5. Aggregate 与 Repository 目录

### 5.1 Aggregate Catalog

| Context | Aggregate Root | 主要内部事实/对象 | 必须由 Root/原子命令保护的不变量 |
|---|---|---|---|
| Identity | User | status、role、password/TOTP、`token_version` | 状态/角色安全变更与 token_version、审计一致 |
| Identity | ApiKey | HMAC/prefix、固定 Group、status/effective expiry | 明文只回显一次；Group 不可原地更换；revoked 终态 |
| Identity | RefreshFamily | refresh token generations、revocation | 单次轮换；重放撤销 family |
| Identity | PasswordResetRequest | one-time token 状态与 email outbox reference | token 单次消费；token 与 email outbox 同事务创建 |
| SubscriptionAccess | SubscriptionTemplate | 名称、Group、默认有效期 | 无价格/配额；退役规则一致 |
| SubscriptionAccess | Subscription | canonical user+group、status、有效期、version | 每 user+group 一条；状态机、ETag、审计一致 |
| GroupQuota | Group | lifecycle、version、最近一次成功激活的 opaque Supply readiness token/observed_at | archived 终态；activation/disable 条件、Group ETag 与 evidence 同一 UoW 一致；不包含 Channel/Account binding |
| GroupQuota | GroupQuotaLedger | stable quota、current/closed Period、Reservation、Quota Event | `consumed + reserved`、唯一 current period、单次 reservation 转换、reset/adjust/settle 原子性 |
| Supply | Channel | lifecycle、模型能力/映射 | retired 终态；active 前映射完整；任一 non-null Supply Configuration 引用时不可退役 |
| Supply | Account | lifecycle、credential envelope、容量与健康事实 | credential 不回读；retired 终态；健康与 lifecycle 分离；任一 enabled binding 存在时不可退役 |
| Supply | GroupSupplyConfiguration | 可空 Channel reference、单调 version、GroupAccountBinding 子项 | 只有 Supply 可配置 Channel/绑定；子项变化只推进 Supply version；配置不可物理删除；历史 attempt 不被改写；清理引用后才能退役 Account/Channel |

Routing、Gateway 是策略/应用编排 context，Release 1 没有需要通过 Repository 保存的业务 Aggregate。Usage 是 CQRS 查询 context，只维护可重建投影；Operations 维护 append-only/投递技术事实，不成为业务 Aggregate 的所有者。

`GroupQuotaLedger` 是逻辑一致性边界，不要求把全部历史 Period/Reservation 加载为内存对象图。高并发 reserve/renew/settle/release/expire/adjust/reset 通过 GroupQuota Infrastructure 中的 Npgsql/数据库函数 Repository Adapter 原子执行；这个实现仍然只能通过 `IGroupQuotaLedger` 等 Application Port 暴露，不能从 Endpoint/Gateway 直接调用 SQL。

### 5.2 `usage_attempts` 的唯一所有权

`usage_attempts` 是 **GroupQuota-owned immutable settlement fact**，不是 Usage context 的写模型：

- 仅 GroupQuota 的 settle/late-settlement 原子命令可以插入；事实与 quota counter、Reservation 状态、Quota Event、outbox 在同一个 UoW 提交。
- 已插入的 `usage_attempts` 不允许 UPDATE/DELETE；后得真实用量使用追加式 `usage_attempt_adjustments` 和对应 quota event 修正，不能覆盖原事实。
- Usage 不得写 `usage_attempts`、`usage_attempt_adjustments`、Reservation 或 quota 表，也不得把其聚合值用于准入。
- Usage 通过 GroupQuota 的版本化 Integration Event 构建 `group_usage_hourly`、`account_usage_hourly` 等读模型；投影使用 inbox/checkpoint 幂等处理并可从权威 settlement feed 重建。
- 重建时如需扫描事实，必须通过 GroupQuota 提供的只读 settlement feed/port；不得给 Usage 一个可查询 GroupQuota 任意表的通用 DbContext。
- `/v1/usage` 组合 GroupQuota 的强一致 quota Snapshot 与 Usage 的最终一致趋势 DTO，并继续返回明确的数据延迟；两者不能相互反算或覆盖。

### 5.3 Repository 规则

- Repository 只服务本 context 的 Aggregate Root 或明确的原子 Ledger Port，接口位于 Application Port，实现在 Infrastructure。
- Repository 不返回 EF Entity、`IQueryable`、change tracker、数据库 transaction 或可变集合；跨 context 不共享 Repository。
- Repository 方法不调用 `SaveChanges`/commit；提交由当前 Command 的 UoW owner 统一完成。
- Query Handler 使用专用 Query Port/SQL 投影，不为了复用而调用写侧 Generic Repository。
- 一个命令需要修改同一 context 的多个 Aggregate 时，由 Application Handler 协调并在该 context 的一个 UoW 内提交；若必须跨 context，则转为 Orchestrator 顺序调用，不建立跨 context Repository/UoW。

## 6. Command、Query 与 Unit of Work

### 6.1 Command Handler

每个外部写操作映射为一个不可变 Command 和一个明确 Handler。Handler 固定负责：

1. 使用服务端 Policy 完成操作级授权；
2. 校验 Command 语义、`Idempotency-Key`、expected version/`If-Match`；
3. 通过 Repository/Port 加载并调用 Aggregate 行为，或调用原子 Ledger Port；
4. 在同一 UoW 追加幂等记录、审计和待发布 Integration Event；
5. commit 一次并返回最小 Result（ID、version/ETag、状态或错误），不返回 Domain/EF Entity。

Domain 对象维护自身不变量；Handler 负责用例顺序与跨 Aggregate 协调。Endpoint 不复制状态机，Repository 不承担授权或 HTTP 错误映射。

### 6.2 Query Handler

- Query 不改变业务状态，不产生 Domain Event，不取得写锁；日志、trace 和被拒绝访问的安全审计属于基础设施副作用，不得改变查询结果。
- Query 返回专用 DTO/Snapshot，不返回 Domain/EF Entity。
- 列表、报表和趋势优先读取 Usage/专用 read model；授权、当前 Group/quota 等必须按既有契约读取 canonical 强一致 Snapshot。
- Query 不复用 Command Handler，不通过调用写 Repository 拼装报表，也不因缓存 miss 写业务默认值。
- Query/Command 的语义分离不要求引入特定 mediator 库；若使用 pipeline behavior，其顺序必须通过测试冻结。

### 6.3 单 Command Unit of Work

- 一个 Command 最多一个本地 PostgreSQL transaction，由 Application transaction behavior/Handler 通过 vendor-neutral `IUnitOfWorkFactory.BeginAsync(...)` 创建并唯一拥有；Repository、Audit/Outbox Appender 不得自行 commit 或开启嵌套事务。
- `IUnitOfWork` 暴露独立的 `IUnitOfWorkContext Context`，并且只有前者具有 `CommitAsync`/dispose capability；`IUnitOfWork` 不继承/实现 context。Handler 持有前者，只把后者传给本次事务的 Repository、Idempotency Store 与 Appender。
- `CommitAsync` 是一次性终态操作；未成功 commit 的 UoW 在 dispose 时 rollback，commit/dispose 后 context 失效。UoW/context 不得跨 Command 缓存、复用或并发共享。
- 按 ADR 0002，同一 UoW 中的 EF 与参数化 Npgsql 操作通过 `PoolAI.Infrastructure.Postgres` 的 concrete context 显式共享同一 connection/transaction，但这些基础设施类型不得暴露给 Domain/Application/Endpoints/Abstractions。Infrastructure adapter 可以检查 concrete capability；检查失败必须在写入前 fail-closed，不能另开 fallback transaction。
- 幂等记录、业务状态、append-only audit 与 outbox 必须在同一事务；Operations 的 Idempotency Store/Appender 是加入当前 UoW 的 Outbound Port，Operations 不为业务写入另开事务。
- outbox 的 claim、发布、重试和状态更新属于 Worker 的后续独立 UoW，不回滚已经提交的业务事务。
- 不在数据库事务中等待 Redis lease、调用上游 HTTP/SMTP、发送 SSE 或执行退避。Gateway 一次请求是长应用工作流，其中 reserve、renew、settle/release 各自是短且幂等的 Ledger Command/UoW，不把整个上游生命周期包在一个事务内。
- `PoolAI.Application.Orchestration` 不创建跨模块 UoW。它按顺序调用各模块端口，并依赖幂等、乐观并发和明确失败结果；Group activation 只有最终 GroupQuota mutation 是写事务。
- `PoolAI.Infrastructure.Postgres` 只管理 data source/connection/transaction/session-lock 生命周期，不接收任意 SQL delegate，不包含业务表 SQL/Repository/Migration，也不通过 `TransactionScope`、ambient/`AsyncLocal` 或静态/全局 registry 发现“当前事务”。

## 7. Domain Event 与 Integration Event

| 维度 | Domain Event | Integration Event |
|---|---|---|
| 范围 | 同一 bounded context 内 | 跨 context 的 Published Language |
| 表示 | 领域类型，可随模块内部重构 | 不可变、显式名称和 `schema_version` 的稳定契约 |
| 持久化 | 不要求独立保存 | 与业务状态同 UoW 写入 Transactional Outbox |
| 处理时机 | commit 前由同模块同步处理，或转换为 outbox 消息 | commit 后由 Worker at-least-once 投递/投影 |
| 允许依赖 | 同模块 Domain/Application | 消费方只依赖事件契约，不依赖生产方 Entity/DbContext |
| 失败语义 | 失败使 Command 回滚 | 有界重试、dead、显式安全重放；不能回滚已提交业务事务 |

Domain Event 必须使用过去式业务名称，Handler 不得执行 HTTP/SMTP/Redis 等不可回滚 I/O。需要跨模块传播的事实必须在 Application 层映射为 Integration Event，禁止直接序列化 Aggregate 或 EF Entity。

Integration Event envelope 固定包含：

- `message_id`（等于 outbox `id`）、`topic`、`event_type`、`schema_version`；
- outbox 全局 `event_sequence`、可空 `source_event_sequence`、`aggregate_type`、`aggregate_id` 与可空 `aggregate_version`；
- `occurred_at`、`correlation_id`、可空 `causation_id`；
- 稳定 `deduplication_key`、对象形态 `payload` 与可空 `replay_of`。

业务 payload 内可以另有生产 Context 的 `event_id`，但它不是 transport `message_id`，不得混用去重键。Publisher 必须发送完整 envelope，不能只发送 payload；安全重放生成新 `message_id`/deduplication key，并以 `replay_of` 指向 dead 原消息。

投递语义为 at-least-once，不承诺全局顺序。需要顺序的消费者只能按已声明的 topic/aggregate sequence 处理；inbox receipt、投影写入和 checkpoint 前进必须在消费者的同一 UoW。重复消息结果不变，同 sequence 不同 payload hash 必须告警并停止该分区；未知 `schema_version` 不得静默丢弃。

GroupQuota Published Language 固定为 `topic=poolai.quota.v1`、`schema_version=1` 的 `GroupQuotaEventV1` union，`event_type` 只能是 `initialized/reserved/dispatch_started/renewed/settled/released/expired/usage_adjusted/total_adjusted/period_reset`。Usage 仅对 `settled`、`expired` 且 `payload.metadata.conservative_expiry=true`、`usage_adjusted` 通过 `IAttemptSettlementFactReader` 按 `attempt_id` 重算；其他事件不增加 usage。不另发 `AttemptUsageRecordedV1/AttemptUsageAdjustedV1` 重复消息，精确 payload/映射以数据库执行规格第 8 节为准。GroupQuota 事件必须携带构建 Usage 投影所需的稳定字段或权威 fact reference，不允许 Usage 解析 GroupQuota 内部 JSON/Entity。新增必填字段或改变含义必须发布新 schema version；旧消费者的兼容/退出窗口由 ADR 和 contract test 固定。

Audit record 不是 Domain Event，也不是可由业务事件重建的替代品；它是同 UoW 追加的安全/操作事实。Email outbox 是持久化投递命令，遵守同事务创建和 commit 后发送，但不冒充领域状态。

## 8. Gateway 与运行时模式目录

设计模式只用于稳定变化轴和故障边界，不能把顺序隐藏在反射/中间件魔法中。R1 固定采用：

| 模式 | 落点 | 必须保证 | 禁止退化 |
|---|---|---|---|
| Process Manager | Gateway 每个入口 request 的 `GatewayRequestProcess`，内部创建逐 attempt `AttemptContext` | 显式推进 admission、canonical read、route/lease、reserve、dispatch fence、upstream、settle/drain；每个 failover 是新 attempt，长流程不持有数据库事务 | Endpoint 巨型方法、递归 failover、把整个请求当一个数据库 UoW、分布式 Saga 框架 |
| State | Reservation、attempt phase、Account breaker/health、Subscription/Resource lifecycle | 合法转换由数据库 CAS/领域方法执行；非法转换有稳定错误；状态与时间来源明确 | 多个互相矛盾 boolean、在 Endpoint 直接赋状态、仅内存 breaker |
| Strategy | 同 Group Account 评分、Token estimate、retry/failover eligibility | 接口输入是不可变 Snapshot/AttemptContext；默认策略显式注册并可确定性测试 | Strategy 直接读 DbContext/Redis、修改 quota、在多层各自重试 |
| Adapter / Anti-Corruption Layer | `PoolAI.Adapters.OpenAI` 的请求、响应、SSE、usage 与错误规范化 | 厂商 DTO/SDK 类型止于 Adapter；capability 明确说明是否能证明未执行/幂等重放 | Adapter 获取业务 DbContext、自己 reserve/settle、把外部错误原样泄露 |
| Decorator | Gateway Stage 与 outbound 调用的观测、超时、header 清洗等横切能力 | 顺序由显式注册和 contract test 冻结；Decorator 不改变业务状态机，不吞取消/错误 | 通用 HTTP retry Decorator、重复计时/重复 breaker、运行时任意重排 |
| Factory/Registry | Composition Root 根据 `(inbound_protocol, upstream_type, operation)` 选择唯一 Adapter capability | 启动时验证缺失/重复，返回强类型 capability | Service Locator、按字符串反射加载、运行时下载插件 |
| Bulkhead | 非流数据面、SSE、控制面、`/v1/usage` 四个独立 admission policy | 有界并发/队列；拒绝在 canonical read、lease、reservation 前；所有路径 finally 释放 | 无界 Channel/队列、SSE 同时占用非流 permit、用 Redis lease 代替进程隔离 |
| Circuit Breaker | Routing 的 Account 级 closed/open/half-open 状态与跨实例 probe lease | Redis 共享窗口/单 probe，持久 health 取更严格状态；只记录可归责 outcome | 每实例独立 breaker、401/403 自动探测、breaker 自行重放请求 |

`AttemptContext` 至少携带 request/attempt ID、attempt index、quota/routing Group、Account/Channel、reservation/lease handle、dispatch 状态、是否已写上游字节、是否已提交下游 Header/业务输出、deadline/retry budget、usage/evidence 与最终处置。phase 只允许 `prepared → dispatched_no_downstream_headers → downstream_headers_committed → business_output_started` 的单向前进（请求可在任一阶段终止）。PostgreSQL 只持久 `dispatch_started_at`，逻辑 dispatch 唯一派生为 `NULL = not_started`、非 `NULL = started`；四态 phase 是 Process Manager 的活请求状态并进入日志/trace，不在数据库复制一份会漂移的真相。`dispatch=started` 后即使 Adapter 证明未执行，也只能以 `confirmed_no_execution` settle 0，不能 release。

Decorator 的固定外层顺序为：request correlation/observability → admission bulkhead → authentication → request validation/body limit → Gateway Process Manager。Process Manager 内每个 attempt 固定为 canonical access/Supply read → routing/Account lease → quota reserve → Adapter prepare → dispatch fence → upstream call/stream → settle/drain → lease release。只有 Process Manager 可以根据 capability 和 phase 创建下一个 attempt；timeout、breaker 和 Adapter 都只能返回强类型 outcome，不能自行循环。

## 9. Architecture Test 与质量门

`PoolAI.ArchitectureTests` 必须至少阻断：

1. 模块实现引用其他模块实现、Entity、DbContext、Endpoint，或 `*.Abstractions` 之间形成环。
2. Domain/Application/Infrastructure/Endpoints 违反第 3.1 节依赖矩阵；尤其是 Domain/Application/Endpoints/Abstractions/Orchestration 引用 EF/Npgsql/`PoolAI.Infrastructure.Postgres`，或 Endpoint 引用 Infrastructure/DbContext。
3. `PoolAI.Application.Orchestration` 引用模块实现、Domain、Infrastructure、数据库/Redis/HTTP SDK，或声明 DbContext/Repository/持久化 Entity。
4. Group activation Endpoint 绕过 `GroupActivationOrchestrator` 直接调用 activation mutation port。
5. Adapter 引用业务模块实现、数据库、Redis、Subscription 或 quota transaction。
6. 出现通用 `IRepository<T>`、通用 SQL executor/callback、跨模块 `IQueryable`、God DbContext、`TransactionScope`/ambient/`AsyncLocal` transaction lookup、静态/全局 connection registry、静态 ServiceProvider 或注册扩展中的 `BuildServiceProvider()`。
7. Api/Worker/Migrator 彼此引用可执行项目，或加载超出各自 Composition Root 白名单的模块/loop/Adapter；尤其是 Api/Worker 未各自注册 Host-local PostgreSQL runtime，或 Migrator 注册 runtime application UoW/业务模块。
8. 只有 GroupQuota Infrastructure 能写 `groups`、quota、Reservation、`usage_attempts`、`usage_attempt_adjustments`；只有 Supply 能写 `group_supply_configurations/group_accounts`。Supply 写不得改变 Group version/ETag，GroupQuota 写不得改变 Supply version/ETag；Usage 对结算事实没有写路径，只能写自己的投影/watermark，并须经 Operations 的事务内 `IInboxReceiptAppender` 登记通用 Inbox。
9. Integration Event payload 引用 Domain/EF Entity、缺少显式 schema version，或消费者没有 inbox/checkpoint 幂等路径。
10. Repository 内调用 commit/`SaveChanges`，`IUnitOfWork` 实现/继承 `IUnitOfWorkContext`，Appender/Store 可取得 commit/dispose capability，UoW/context 跨 Command 复用或终态后仍可使用，context 不匹配时另开 fallback transaction，或 Command Handler/UoW 在外部 HTTP、SMTP、Redis 等调用期间保持数据库事务。
11. 新建 Payment/Billing/Pricing/Balance/Redeem/Affiliate/个人 quota 模块或类型。
12. 除 ADR 0006 登记的三个完整 family 外出现跨 Context SQL/DbContext 直读：Family A 仅含 `poolai_quota_reserve` 的 canonical admission 读取以及 `poolai_quota_settle`、`poolai_quota_mark_dispatched`、`poolai_quota_adjust_usage` 的 Account/Channel `id + provider` route-identity 行锁；Family B 仅含 `poolai_validate_group_activation` 的点时 Supply readiness guard；Family C 仅含 `poolai_group_update` 与五条 SubscriptionAccess mutation 的双向 lifecycle fence。任一例外读取超出固定 function/table/field 白名单、跨 Context 写入、引入 dynamic/general executor，或 Family A/C 未遵守登记锁序和等待后 DB clock 重检。ADR 0006 仍为 `Proposed` 或缺少 `@Lyon1984` 永久签核证据时，还必须阻断以该候选 registry 宣称 M1-E4 architecture sign-off/release-ready。

静态架构测试之外，Integration Test 必须用真实 PostgreSQL 角色证明表写 owner、Group/Supply 独立 ETag、Supply 多行替换的单调 version、activation evidence、同一 physical connection/transaction 内的业务事实 + idempotency/audit/outbox、context mismatch fail-closed 且无第二事务、`usage_attempts` append-only、重复投递幂等、Worker dedicated session advisory lock 在 dispose/断连后的释放接管，以及 Group activation 并发版本冲突。Contract Test 必须覆盖 Integration Event envelope/schema version；故障测试必须覆盖 readiness 观察前后竞态、commit 前失败全部回滚、commit 后 Supply 失效不改写 Group 且新 attempt 强读拒绝，以及投递失败可重试。

以下变化必须先有 ADR：新增/合并/split bounded context、改变表 owner、改变 Context Map 同步方向、跨模块共享事务、增加 Host、改变 Composition Root 加载范围、把读模型用于准入、引入消息代理/微服务/Event Sourcing/Saga，或允许 Generic Repository。
