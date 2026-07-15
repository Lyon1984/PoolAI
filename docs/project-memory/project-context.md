# Project context

## Purpose

PoolAI provides administrator-managed subscriptions, OpenAI/Codex-compatible access, shared Group Token quota enforcement, and Account-level Token usage statistics. It is intentionally non-commercial: subscription represents access eligibility only.

## Target stack

- Backend: .NET 10, ASP.NET Core 10, EF Core 10, Npgsql 10
- Data: PostgreSQL 18 and Redis
- Frontend: Vue 3, TypeScript, Vite, Pinia, Vue Router
- Deployment: independent Api, Worker, and one-shot Migrator processes

## Core invariants

- Group is the sole cumulative Token quota subject.
- API keys bind permanently to one Group.
- Subscription grants access and has no personal quota, price, balance, or payment behavior.
- GroupQuota owns reservation, settlement, and immutable usage facts; Usage owns rebuildable query projections.
- PostgreSQL is the cumulative quota authority. Redis coordinates Account leases and Group RPM but does not decide cumulative quota.
- Payment, billing, pricing, balance, refund, promo, redeem code, affiliate, commission, purchasable quota, and personal quota capabilities are permanent non-goals.

## Authoritative navigation

- Refactoring target, capability disposition, and delivery route: [`../系统重构方案-v1.0.md`](../系统重构方案-v1.0.md)
- Contract priority and frozen decisions: [`../README.md`](../README.md)
- HTTP/SSE: [`../contracts/openapi-v1.yaml`](../contracts/openapi-v1.yaml)
- Database: [`../database/README.md`](../database/README.md)
- Redis: [`../runtime/redis-contract.md`](../runtime/redis-contract.md)
- Architecture: [`../architecture/design-pattern-baseline.md`](../architecture/design-pattern-baseline.md)
- Delivery specification: [`../开发执行规格-v1.0.md`](../开发执行规格-v1.0.md)
