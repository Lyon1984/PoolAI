# PoolAI Release 1 错误契约

状态：**冻结（v1.0）**  
机器契约：[openapi-v1.yaml](openapi-v1.yaml)

## 1. 两种传输投影，一个业务错误

服务端只维护一套稳定 `code`。`/api/v1/*` 控制面错误只以 `application/problem+json`
返回 OpenAPI `ControlPlaneProblem`（RFC Problem Details 字段）；`/v1/*` 数据面错误只以 `application/json` 返回相同字段，并额外携带 OpenAI/Codex SDK 可识别的
`error` 投影：

```json
{
  "type": "https://poolai.example/problems/group-quota-reserved",
  "title": "Group quota temporarily reserved",
  "status": 429,
  "detail": "Group 可用 Token 当前被在途请求占用，请稍后重试。",
  "instance": "/v1/responses",
  "code": "group_quota_reserved",
  "request_id": "0190f8bf-a040-7444-a2ca-c4bc32e48b47",
  "retryable": true,
  "retry_after_seconds": 1,
  "error": {
    "message": "Group 可用 Token 当前被在途请求占用，请稍后重试。",
    "type": "rate_limit_error",
    "param": null,
    "code": "group_quota_reserved"
  }
}
```

约束：

- 程序只依赖 `status`、外层 `code`、`retryable` 和 `Retry-After`；`title/detail` 可改文案。
- `request_id` 与 `X-Request-Id` 必须相同；由服务端为每次入站请求生成 UUIDv7。幂等重放使用本次请求的新关联 ID，不复用首次请求 ID。
- 数据面 `error.code` 非 null 时必须等于外层 `code`；`error.message` 等于安全的 `detail`。
- 不做错误媒体类型协商：控制面错误不得返回 `application/json`，数据面错误不得返回
  `application/problem+json`；成功响应仍按各 operation 声明的媒体类型返回。
- 字段错误放在 `errors`，key 为 JSON Pointer，例如 `"/expires_at"`。
- 不返回堆栈、SQL、内部主机、上游凭据、API Key、JWT、用户是否存在等敏感信息。
- 未开始流之前按普通 HTTP 错误返回；HTTP/SSE Header 一旦提交，不再伪造状态码。

## 2. 重试规则

| `retryable` | 客户端行为 |
|---|---|
| `false` | 不自动重放；修改请求或等待管理员/用户改变状态。 |
| `true` + `Retry-After` | 至少等待 Header 指定秒数；应用指数退避和抖动。body 的 `retry_after_seconds` 必须与 Header 相同。 |
| `true`、无 `Retry-After` | 仅对幂等读，或下方 phase 矩阵明确为 `prepared`/已证明上游可幂等重放的模型请求退避重试；“尚未产生业务输出”单独不足以证明安全。控制面写操作必须复用原 `Idempotency-Key`。 |

`retryable` 是对外客户端契约，与服务端内部的 Account breaker 计数、同 Group failover 不是同一个决策。服务端必须为每个活跃 attempt 在 `AttemptContext` 中单向推进以下 phase 并记录日志/trace；数据库只持久 dispatch fence，不持久下游输出 phase：

| Phase | 精确边界 | 服务端内部 retry/failover | 当前 attempt 与对外错误 |
|---|---|---|---|
| `prepared` | 逻辑 `dispatch_state=not_started`，且 Adapter 未进入上游发送 | 可在 `MaxAttempts`、retry budget 和总 deadline 内选择同 Group 其他 Account | 只有此阶段可零消耗 release reservation |
| `dispatched_no_downstream_headers` | 逻辑 `dispatch_state=started`，下游 Header 未提交、无业务输出 | 只有 transport 证明一个上游请求字节都未发出，上游明确以 `401/403/429` 拒绝且证明未执行，或 Adapter 证明上游幂等键/协议可安全重放，才允许 failover；已实际写出上游字节后的 5xx、首包超时和断连默认都是歧义结果 | dispatch fence 一旦 started 就绝不 release。有可靠“零字节/明确未执行”证据时以 `usage_source=confirmed_no_execution`、total=0、`is_estimated=false` settle；否则按已知 usage 或 `conservative_estimate` settle。歧义结果返回 `upstream_dispatch_ambiguous`，`retryable=false` |
| `downstream_headers_committed` | HTTP/SSE Header 已提交，尚无有效业务事件 | 禁止切换 Account 或伪造新 HTTP 状态；流式协议可发一个终止 error event 后关闭 | 核销/保守估算；`upstream_first_byte_timeout`、`upstream_stream_error` 均是终止且不可自动重放 |
| `business_output_started` | 已向客户端 flush 任何 Responses/Chat 有效事件或非流业务 body | 绝对禁止透明重试/failover；流中断或 idle timeout 只能发终止 error event 后关闭 | 按已知 usage/保守估算核销；`upstream_stream_error` / `upstream_stream_idle_timeout` 在代码语义上固定为不可自动重放 |

Gateway 必须在调用上游 transport 之前先 CAS 写入并提交 `dispatch_started_at`，该列为 null 时逻辑 `dispatch_state=not_started`、非 null 时为 `started`；提交后再把运行时 phase 推进到 `dispatched_no_downstream_headers`。Adapter 必须显式报告 transport 是否确认零字节发送、上游拒绝是否证明未执行、`downstream_headers_committed` 和 `business_output_started`。phase 的冻结四态是 `prepared`、`dispatched_no_downstream_headers`、`downstream_headers_committed`、`business_output_started`，不得与二态 dispatch 混名或在数据库增加第二份同义事实。`confirmed_no_execution` 只是“以 0 settle”的审计证据，不得把终态改成 released。不得用“尚未看到完整 SSE frame”推断未输出或未 dispatch。每次 failover 先 settle 旧 attempt，再重做 canonical 访问/Supply 强读、Account lease 和 Group reservation。

`group_quota_reserved` 固定立即返回 `429` 与 `Retry-After: 1`，服务端不排队；
`group_quota_exhausted` 不可自动重试。`/v1/models` 和 `/v1/usage` 都不受 Group quota exhausted/reserved/insufficient 或 Group RPM 拒绝影响；前者归 NonStream bulkhead，后者归独立 Usage bulkhead，饱和时只能返回 `429 gateway_overloaded + Retry-After: 1`，不得复用含 quota/RPM 错误的响应组件。

Token 表示规则：管理输入与单请求 OpenAI `usage` 是 `0..2^53-1` 的 JSON number；
Group quota、`/v1/usage` 时间窗口、Account/Group 聚合是无符号规范十进制 string。
累计 consumed 因迟到核销或 overage 超过 `2^53-1` 是正常状态，不得报错、截断或转成浮点数。
上游单请求 usage 是非负整数、超过 `2^53-1` 但仍可由 `numeric(78,0)` 精确保存时，必须保留
精确审计事实并返回 `upstream_usage_out_of_range`。负数、小数或 usage 字段关系错误返回
`upstream_protocol_error`；超过 78 位或数据库精确整数运算越界则事务回滚并返回
`token_numeric_overflow`。所有路径都不得截断或转成浮点数。

## 3. 稳定错误码

### 3.1 通用 HTTP 与校验

| code | HTTP | retryable | Retry-After | 语义 |
|---|---:|:---:|:---:|---|
| `invalid_request` | 400 | 否 | — | JSON、query、header 或业务参数无效。 |
| `validation_failed` | 422 | 否 | — | JSON 语法有效，但字段或业务约束失败，必须带 `errors`。 |
| `unsupported_feature` | 400 | 否 | — | 请求使用 R1 未冻结的 Responses/Chat 能力。 |
| `authentication_required` | 401 | 否 | — | 未提供凭据。 |
| `invalid_user_token` | 401 | 否 | — | JWT 无效、过期或 TokenVersion 已撤销。 |
| `invalid_api_key` | 401 | 否 | — | `sk-` Key 不存在、已禁用、已过期或已撤销；不细分原因。 |
| `forbidden` | 403 | 否 | — | 已认证但无权执行操作。 |
| `role_required` | 403 | 否 | — | 不满足 operation 的 `x-required-roles`。 |
| `resource_not_found` | 404 | 否 | — | 资源不存在或对调用者不可见。 |
| `method_not_allowed` | 405 | 否 | — | 路由存在但方法不允许。 |
| `resource_conflict` | 409 | 否 | — | 唯一键、引用或当前状态冲突。 |
| `idempotency_conflict` | 409 | 否 | — | 同一作用域的 Key 已用于不同请求摘要。 |
| `version_conflict` | 412 | 是 | — | `If-Match` 不是当前强 ETag；先 GET 再决定是否重试。 |
| `payload_too_large` | 413 | 否 | — | 请求体超过入口或应用上限。 |
| `unsupported_media_type` | 415 | 否 | — | Content-Type 不支持。 |
| `rate_limit_exceeded` | 429 | 是 | 必须 | 登录、控制面或两个模型 POST 的通用入口 RPM 限制；不适用 `/v1/models`、`/v1/usage`。 |
| `group_rate_limited` | 429 | 是 | 必须 | Group 级模型 POST RPM 限制；只适用 `/v1/responses` 和 `/v1/chat/completions`，与 Token 配额无关。 |
| `idempotency_key_required` | 428 | 否 | — | 资源变更缺少 `Idempotency-Key`。 |
| `if_match_required` | 428 | 否 | — | versioned 资源变更缺少 `If-Match`。 |
| `internal_error` | 500 | 视情况 | 可选 | 未分类内部错误；错误详情只进受控日志。 |
| `service_unavailable` | 503 | 是 | 必须 | 服务临时不可用。 |
| `gateway_overloaded` | 429 | 是 | **1** | 当前 API 副本的 NonStream/SSE/Control/Usage 任一独立 bulkhead 令牌或有界队列已满；拒绝发生在 canonical 强读、Account lease 和 reservation 之前。 |
| `coordination_unavailable` | 503 | 是 | 必须 | Redis 硬协调不可用且该路径必须 fail-closed。 |
| `dependency_unavailable` | 503 | 是 | **1** | PostgreSQL（包括配额真相）或其他必需依赖不可用，服务无法安全完成请求；必须 fail-closed。 |
| `token_numeric_overflow` | 500 | 否 | — | Token 精确整数超出数据库可表示范围；事务必须回滚且不得回显原始数值。 |

### 3.2 身份、密码与 TOTP

| code | HTTP | retryable | Retry-After | 语义 |
|---|---:|:---:|:---:|---|
| `invalid_credentials` | 401 | 否 | — | 邮箱/密码错误；不说明邮箱是否存在。 |
| `account_locked` | 429 | 是 | 必须 | 仅当 active 账户的密码正确且 PostgreSQL clock 判定仍在锁定中时返回；`Retry-After`/body 秒数取至 `locked_until` 的向上取整正整数。 |
| `user_disabled` | 403 | 否 | — | 用户已禁用。 |
| `mfa_challenge_invalid` | 401 | 否 | — | MFA challenge 无效、过期或已消费。 |
| `totp_code_invalid` | 401 | 否 | — | TOTP 当前码错误。 |
| `totp_already_enabled` | 409 | 否 | — | 用户已启用 TOTP。 |
| `totp_not_enabled` | 409 | 否 | — | 用户尚未启用 TOTP。 |
| `totp_setup_expired` | 409 | 否 | — | 待确认设置已过期；重新 setup。 |
| `refresh_token_invalid` | 401 | 否 | — | Refresh Token 无效或过期。 |
| `refresh_token_reused` | 401 | 否 | — | 已轮换 Token 被重放；撤销该 token family。 |
| `password_reset_token_invalid` | 401 | 否 | — | 重置 Token 无效、过期或已使用。 |
| `password_policy_failed` | 422 | 否 | — | 新密码不符合策略，带字段错误。 |

`POST /auth/login` 的未知邮箱、错误密码和“锁定中 + 错误密码”都投影为上表已冻结的 `invalid_credentials`；disabled User 只在密码正确后投影 `user_disabled`。已知 active User 的错误密码按 PostgreSQL clock 累加失败数并在阈值设置锁定；仍在锁定期的错误密码不延长锁定或改写失败数。锁定过期后当前尝试先把旧计数视为 0，正确密码清空计数/锁定，错误密码从 1 重新累计。匿名 login 与 login-TOTP 失败还共享 Redis IP fixed-window，超限投影为上表已冻结的 `rate_limit_exceeded`；失败路径需要计数/判定时 Redis 异常固定为 `503 coordination_unavailable + Retry-After: 1`。

登录 MFA challenge 保存发起时 `security_stamp/token_version`；验证时 User 非 active 或快照不一致也投影为上表已冻结的 `mfa_challenge_invalid`。TOTP step 已接受的重放投影为 `totp_code_invalid`，TOTP 错误/重放均不累加 PostgreSQL 密码锁定计数。

Refresh Token 的无效、过期或已撤销状态都投影为上表已冻结的 `refresh_token_invalid`。已轮换 Token 重放时在同一 PostgreSQL 事务内撤销整个 family、记录审计，再投影为 `refresh_token_reused`。M1-E2 Access JWT 的 `sid` 是 refresh `family_id`；该 family 不再有 active generation 时投影为上表已冻结的 `invalid_user_token`。

`POST /auth/logout` 先遵守 M1-E2 UserJwt family 存活检查；省略 body 或 `all_sessions=false` 时按 JWT `sid` 定位当前 refresh family。未提供 refresh token 时撤销当前 family，提供时只在它属于 actor 且与 `sid` family 一致时撤销；不属于 actor/当前 family、无效或已撤销 token 在本次已通过认证的请求内也返回幂等 `204` no-op。当前 family 撤销后重用原 Access JWT 则按已冻结语义返回 `401 invalid_user_token`。`all_sessions=true` 与 `refresh_token` 同时出现是 `422 validation_failed`；合法的全会话撤销还必须恰好递增一次 `token_version`。

TOTP confirm enable 和 disable 都撤销全部 refresh family 并通过 TOTP secret/security stamp 变更递增 `token_version/version`。恢复码明文只在 confirm 的首次成功/同幂等键安全重放响应中返回，只以专用 pepper HMAC 持久化，并在 disable 或下次 enable/re-enable 时撤销；R1 当前没有 recovery-code 登录入口。

`POST /auth/forgot-password` 对有效或无效邮箱都返回相同 `202`；邮件发送结果不得改变响应。

### 3.3 订阅、Group、API Key 与路由

| code | HTTP | retryable | Retry-After | 语义 |
|---|---:|:---:|:---:|---|
| `subscription_required` | 403 | 否 | — | API Key 所属 Group 没有当前用户的 Subscription。 |
| `subscription_inactive` | 403 | 否 | — | Subscription 的有效状态为 scheduled、expired、suspended 或 revoked。 |
| `subscription_conflict` | 409 | 否 | — | 同一 user + group 已有 canonical Subscription；应更新原资源。 |
| `subscription_template_disabled` | 409 | 否 | — | 不能从 disabled Template 新分配订阅。 |
| `group_disabled` | 403 | 否 | — | Group 已禁用。 |
| `group_activation_not_ready` | 409 | 否 | — | Group 激活时，配额、Channel 映射或可调度 Account 等同一时点前置条件不满足；修复配置后以新的管理操作重试。 |
| `group_account_binding_invalid` | 422 | 否 | — | account_bindings 重复、覆盖值越界、绑定身份被修改，或引用缺失/retired Account、非法/retired Channel，或 Account 与 Channel provider 不匹配。 |
| `cross_group_fallback_forbidden` | 409 | 否 | — | 配置试图把 fallback 指向另一 Group；R1 明确禁止。 |
| `api_key_group_immutable` | 409 | 否 | — | 已创建 Key 的 group_id 不可变。 |
| `api_key_revoked` | 409 | 否 | — | terminal revoked Key 不能恢复。 |
| `model_not_found` | 404 | 否 | — | 当前 Group/Channel 未发布请求模型。 |
| `model_not_allowed` | 403 | 否 | — | 模型存在但当前 Group 不允许。 |
| `account_in_use` | 409 | 否 | — | Account 仍存在 enabled Supply binding，不能退役。 |
| `channel_in_use` | 409 | 否 | — | Channel 仍被任一 non-null Supply Configuration 引用，不能退役。 |

### 3.4 Group Token 配额

| code | HTTP | retryable | Retry-After | 语义 |
|---|---:|:---:|:---:|---|
| `group_quota_exhausted` | 429 | 否 | — | `consumed_tokens >= total_tokens`。 |
| `group_quota_reserved` | 429 | 是 | **1** | 仅因 pending reservation 暂时没有足够可分配量；立即拒绝。 |
| `group_quota_insufficient` | 429 | 否 | — | 当前请求 estimate 大于未被永久消耗的余量；缩小输出上限后可作为新请求尝试。 |
| `reservation_lease_lost` | 503 | 是 | 1 | 调用上游前丢失 reservation；没有放行。 |

### 3.5 上游与容量

| code | HTTP | retryable | Retry-After | 语义 |
|---|---:|:---:|:---:|---|
| `no_available_account` | 503 | 是 | 必须 | Group 内没有当前可调度 Account。 |
| `account_capacity_unavailable` | 503 | 是 | 必须 | Group 内账号并发槽暂满。 |
| `upstream_auth_failed` | 502 | 否 | — | 所有合格 Account 的上游认证失败；不透传凭据细节。 |
| `upstream_rejected` | 502 | 否 | — | 上游拒绝且无法安全映射到更具体错误；客户端修改请求前不自动重放。 |
| `upstream_protocol_error` | 502 | 否 | — | 上游返回无法解析的协议；仍计入 Account breaker，但不据此重放已 dispatch 的请求。 |
| `upstream_usage_out_of_range` | 502 或 SSE | 否 | — | 上游单请求 usage 是非负整数且大于 `2^53-1`、仍可由 `numeric(78,0)` 精确保存；不得截断、浮点化或发送成功终止事件。 |
| `upstream_dispatch_ambiguous` | 502 | 否 | — | 请求已 dispatch，但 5xx/断连/首包前失败无法证明上游未执行；禁止透明重放。 |
| `upstream_stream_error` | 502 或 SSE | 否 | — | 上游流在完成前中断；一旦 Header/业务输出已提交即为终止错误。 |
| `upstream_unavailable` | 502 | 是 | 必须 | transport 可证明未写出任何上游请求字节，且同 Group 安全 failover 已耗尽；已建立 dispatch fence 的旧 attempt 以 `confirmed_no_execution=0` settle。 |
| `upstream_connect_timeout` | 504 | 是 | 可选 | DNS/TCP/TLS 建连阶段超时且 transport 证明零上游请求字节；允许同 Group 安全 failover，但 dispatch fence 已建立时旧 attempt 必须以 `confirmed_no_execution=0` settle而非 release。 |
| `upstream_first_byte_timeout` | 504 或 SSE | 否 | — | 请求已 dispatch 但未收到首个可验证业务字节；默认视为执行结果有歧义。 |
| `upstream_stream_idle_timeout` | SSE | 否 | — | 已开始流在允许的空闲期间内无新字节；发终止 error event 后关闭。 |

### 3.6 SQLSTATE `P0001` 边界

[SQL P0001 错误映射](fixtures/sql-p0001-error-map.json) 是数据库字面错误码的机器可读封闭清单：

- `public`：必须投影为条目声明的稳定 `public_code`，不得把 SQL 字面 code、DETAIL、CONTEXT 或约束名直接返回客户端。
- `internal`：统一投影为 `internal_error`；SQL 字面 code 只允许进入受控日志和 trace。
- `migrator_only`：只允许由 Migrator 的启动/权限验收消费，不进入 Api 或 Worker 的公共错误路径。

映射键是 `(operation, AttemptContext phase, sql_code)`；条目的 `operation_overrides` 优先于默认分类，
phase 只使用第 2 节冻结的四态。`poolai_quota_mark_dispatched` 在 `prepared` 的 lease 失败可映射为
`reservation_lease_lost`；`poolai_quota_renew` 在 `dispatch_started_at` 非 null 后发生同一 SQL code 时只能返回
`internal_error` 且 `retryable=false`，按已知 usage 或保守估算核销并触发 P0，不能向客户端承诺安全重试。

`0001_baseline.sql`、`0002_quota_functions.sql`、`0003_runtime_permissions.sql` 新增、删除或重命名任何
字面 `P0001` code 时，必须原子更新该 fixture 与本目录契约测试；未列入清单的 code 一律按
`internal_error` fail closed，不能临时透传。

## 4. Responses 与 Chat 流错误

Responses typed SSE 以官方 streaming event 形状为兼容边界：

- 每个事件的 `event:` 必须与 JSON `data.type` 一致；`sequence_number` 从 0 连续递增。
- 正常流以 `response.completed` 结束，不发送 `[DONE]`。
- 流开始后错误使用官方 `ResponseErrorEvent` 平面形状：
  `{"type":"error","code":"upstream_stream_error","message":"…","param":null,"sequence_number":3}`。
- `error` 后立即关闭，不再发送 `response.completed`；`X-Request-Id` 已在首个 HTTP Header 中提供。
- SSE error event 不增加自定义 `retryable` 字段；一旦 Header 已提交，`upstream_stream_error`、`upstream_first_byte_timeout`、`upstream_stream_idle_timeout` 和 `upstream_usage_out_of_range` 的代码语义都是终止、不可自动重放。

Chat Completions 正常流发送 chunk 后以 `data: [DONE]` 结束。官方 Chat chunk 契约没有把
流开始后的错误定义成 Responses typed event；PoolAI R1 冻结自己的 SDK 兼容策略：发送
`data: {"error":{"message":"…","type":"server_error","param":null,"code":"upstream_stream_error"}}`
后关闭，并且不再发送 `[DONE]`。
该 Chat 错误同样是终止信号，不表示客户端可自动重放整个模型请求。

当请求的 `stream_options.include_usage=true` 时，每个正常内容/tool chunk 的 `usage` 为
`null`；finish chunk 后、`[DONE]` 前另发一个 `choices: []` 的 usage chunk，且字段固定为
`prompt_tokens`、`completion_tokens`、`total_tokens`。Responses usage 继续使用
`input_tokens`、`output_tokens`、`total_tokens`，两套字段不得混用。

当 `stream_options.include_usage` 缺省或为 `false` 时，不发送 usage chunk；finish chunk 后必须
立即发送 `[DONE]`。普通内容/tool chunk 可以省略 `usage`，也可以保持为 `null`，但不得出现
`choices: []` 且 `usage` 为对象的独立 usage chunk。

Golden fixtures：

- [Control Plane 字段校验错误](fixtures/control-plane-validation-error.json)
- [非流 upstream usage 超出范围 502](fixtures/gateway-upstream-usage-out-of-range.json)
- [Responses 正常完成](fixtures/responses-stream-completed.sse)
- [Responses 函数调用](fixtures/responses-stream-function-call.sse)
- [Responses 函数结果回传](fixtures/responses-function-tool-followup.json)
- [Responses 流开始后错误](fixtures/responses-stream-error.sse)
- [Responses Header 已提交但首个业务事件前超时](fixtures/responses-stream-first-byte-timeout.sse)
- [Responses usage 超出范围](fixtures/responses-stream-usage-out-of-range.sse)
- [Chat Completions 正常完成](fixtures/chat-completions-text.sse)
- [Chat Completions 正常完成（不含 usage chunk）](fixtures/chat-completions-text-no-usage.sse)
- [Chat Completions 函数调用](fixtures/chat-completions-function-call.sse)
- [Chat Completions tool 结果回传](fixtures/chat-completions-tool-followup.json)
- [Chat Completions 流开始后错误](fixtures/chat-completions-error.sse)
- [Chat Completions usage 超出范围](fixtures/chat-completions-usage-out-of-range.sse)

## 5. Idempotency 与并发控制

- `Idempotency-Key` 作用域：`principal_id + HTTP method + normalized path + key`；ASCII 1..128。
- 至少保留 24 小时。相同请求摘要重放原业务 status/body/关键 Header；请求级 `request_id`/`X-Request-Id`
  按本次入站请求重新生成，`instance` 由当前 normalized path 生成，不属于持久业务响应快照。API Key secret、TOTP setup
  secret 与 recovery codes 等一次性结果需加密保存，以便安全重放。不同摘要返回
  `409 idempotency_conflict`。已完成记录的同摘要重放先于当前 `If-Match` 比较，返回原 ETag；
  首次执行仍必须校验当前版本。
- 登录、refresh、logout、MFA verify、匿名 forgot/reset password 依靠挑战或 Token 的单用/撤销语义，
  不要求 `Idempotency-Key`；Admin 代用户发起 password reset 和其余资源写操作都要求。
- User、Group、Group Supply Configuration、Subscription Template、Subscription、API Key、Account、Channel 以及 Group quota
  都是 versioned resource。状态变更、PATCH、DELETE、quota adjust/reset 必须携带 GET 返回的强
  `If-Match: "vN"`；缺失为 `428 if_match_required`，失配为 `412 version_conflict` 并返回当前 ETag。
- 高风险变更必须记录非空理由：User role/status、Group status、Group Supply Configuration 的 channel/account bindings、Account
  credential/status、Channel/Template status、Subscription 分配/更新、本人改密，以及 Admin 代用户创建/更新 Key 或发起密码重置，
  在 JSON body 使用 `reason`；DELETE 撤销/退役（含 Admin 代用户撤销 Key）使用 `X-Change-Reason` Header。
- DELETE Template、Account、Channel 都是 terminal `retired` 软退役，不物理删除历史、凭据元数据、
  binding、快照或 usage。任一 enabled Supply binding 会阻止 Account 退役，任一 non-null Supply
  Configuration 引用会阻止 Channel 退役；
  既有 Subscription 使用 Template 快照，不阻止 Template 退役。
- Subscription 的数据库唯一键是 `(user_id, group_id)`，包括 revoked 历史；恢复/延长/撤销只更新
  这一条 canonical 资源，不插入第二条。
- Subscription 持久 `status` 与 `effective_status` 分离；未到 starts_at 的有效状态是 `scheduled`。
  API Key 同样分离持久 `status`（active/disabled/revoked）与派生 `effective_status`
  （active/disabled/revoked/expired），不得把到期写回为持久状态。
- 非流请求 reservation lease 为 300 秒，每 60 秒续租，单请求最大 600 秒；超过上限按已知
  usage 或保守估算核销。配置摘要可由只读 `/api/v1/admin/runtime-settings` 查询。

## 6. RBAC 约定

OpenAPI 的 `x-required-roles` 是 Policy 契约测试输入。最终授权仍必须回查用户当前状态/角色：

| 能力 | admin | operator | auditor | user |
|---|:---:|:---:|:---:|:---:|
| 本人会话、Key、订阅与 Group 池 | ✓ | ✓ | ✓ | ✓ |
| 代任意用户管理 Key、发起密码重置（强制理由与审计） | ✓ | — | — | — |
| 读取 Users/Groups/Templates/Subscriptions/Accounts/Channels/Audit/Usage | ✓ | ✓ | ✓ | — |
| 创建/更新 Account、Channel、Template、Subscription | ✓ | ✓ | — | — |
| 创建/禁用用户、修改角色 | ✓ | — | — | — |
| 创建/修改 Group 及其 Supply Configuration | ✓ | — | — | — |
| 调整/重置 Group Token 总量 | ✓ | — | — | — |
| 查看或导出秘密明文 | — | — | — | — |

任何角色都只能看到 API Key prefix、Account credential prefix；秘密只在创建或轮换输入中出现，
不得通过读接口、错误、审计差异或日志回显。
