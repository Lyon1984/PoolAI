# R1.1 发布认证证据

[`r1.1-certification-plan.json`](r1.1-certification-plan.json) 是 M0 开发启动门使用的机器声明：它固定未来 Release Candidate 的逻辑认证环境、直接采用执行规格第 8.2 节的参考硬件与数据规模，并约定负载报告的长期归档位置。

这份声明只回答“在哪里、按什么形状、把证据归档到哪里”。它不表示物理环境已经提供，也不表示负载已经运行或通过：环境部署属于 M6-E3；第 8.2 节 11 个场景、每场景同一构建独立运行 3 次及其容量结论属于 M6-E2；Release Candidate 和生产验收继续受执行规格第 12.3 节约束。

## 环境与秘密边界

- 认证环境使用非秘密逻辑标识 `r1.1-reference`，类型为专用预发布、生产等价拓扑；M0 状态保持 `not-provisioned`。
- 私网主机名、IP、SSH/数据库/Redis/SMTP/OTLP 连接信息、云账号和 Secret Provider/KMS 标识不得写入本目录、项目记忆、公开 Release asset 或负载报告索引；脱敏配置摘要与原始指标也必须在归档前执行同一检查。
- 本地 Compose 和共享开发依赖只能验证工程路径，不能自动充当第 8.2 节认证环境；实际环境必须在 M6 证明资源隔离、本地 SSD 和 RTT 等约束。

## 归档规则

- GitHub Actions artifact 仅用于工作流到发布流程的临时传输，不是长期证据源。
- 完整负载包长期归档为公开仓库 `Lyon1984/PoolAI` 对应 `r1.1-rc.*` GitHub Release 的 asset。
- 每个包必须包含 commit、Api/Worker/Migrator image digest、脱敏配置摘要、实际硬件、数据规模、脚本摘要、原始指标和逐轮结论，并以 SHA-256 校验。
- 仓库只在 [`r1.1-certification-index.json`](r1.1-certification-index.json) 保存不含秘密的小型索引；M6 写入的条目记录 Release asset URL、字节数和 SHA-256，不复制原始大体量指标。M0 阶段索引必须为空。

校验命令：

```bash
node eng/release/validate-release-certification-plan.mjs
```

该命令进入仓库 quality gate。M0 状态下它必须拒绝伪造的已部署环境、已运行报告或已通过认证结论。M6 开始写入真实环境和报告前，必须在同一受保护变更中升级 plan/index schema、validator 与脱敏/归档验证；不得直接放宽当前空清单门禁。
