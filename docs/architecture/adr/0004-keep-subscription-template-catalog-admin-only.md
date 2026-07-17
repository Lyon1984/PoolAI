# ADR 0004: Keep the Subscription Template catalog admin-only

- Status: Proposed
- Date: 2026-07-17
- Decider: `@Lyon1984` (pending explicit approval)
- Relates to: M1-E4, DEC-011, AC-008, AC-009

## Context

The frozen OpenAPI v1 contract exposes Subscription Template list and mutation operations only under `/api/v1/admin/subscription-templates`. It exposes a User's assigned access through `GET /api/v1/me/subscriptions` and `GET /api/v1/me/group-pools`; both projections carry the immutable `plan_name` snapshot copied into the canonical Subscription.

The execution-specification RBAC matrix nevertheless granted User a read-only "available Subscription Template summary". No UserJwt operation, response field, self-service assignment workflow, or UI journey defines that catalog. Adding one during M1-E4 would expose a new global provisioning-resource directory without a supported action and would expand the signed OpenAPI and Epic scope. Leaving the matrix unchanged while omitting the route would silently implement only one side of a contract conflict.

## Decision

1. Subscription Template is an Admin/Operator provisioning resource, not a User catalog or self-service entitlement surface.
2. Admin, Operator, and Auditor may read the admin Template resource. Only Admin and Operator may create, update, disable, or retire it. User has no Template query or mutation permission.
3. User may read only the canonical Subscriptions assigned to that User and the Group-pool summaries derived from the User's effective Subscriptions.
4. The `plan_name` returned by those User projections is the immutable Subscription snapshot captured at assignment time. It is not a live Template view, and a later Template rename or retirement does not rewrite it.
5. Release 1 adds no template browsing, request, purchase, redeem, or self-service assignment route, field, menu, or placeholder.
6. The frozen OpenAPI v1 and PostgreSQL schema already express this decision and require no change for this ADR.

## Alternatives considered

### Add `GET /api/v1/me/subscription-templates`

Rejected for Release 1. The endpoint would expose a new catalog without a permitted follow-up action, require a new visibility definition for disabled Groups and Templates, enlarge M1-E4, and permanently add a public v1 surface. It can be introduced compatibly later if a concrete non-commercial workflow is approved.

### Add Template fields to `GroupPoolSummary`

Rejected. A Group may have multiple Templates, while `GroupPoolSummary` represents one already effective Subscription and already contains its assigned `plan_name`. Embedding a mutable catalog would conflate provisioning resources with granted access.

### Keep the conflicting matrix text and implement no route

Rejected. That would make runtime behavior contradict the frozen RBAC specification and invite a later self-service implementation without a deliberate contract decision.

## Consequences

- M1-E4 stays within the existing Admin control-plane and User self-read endpoints.
- User cannot enumerate Template names, descriptions, or default durations that have not been assigned to that User.
- Template rename/retirement behavior remains stable because existing Subscriptions retain `plan_name_snapshot`.
- A future User catalog remains possible only as an explicit, additive contract change with its own authorization, privacy, and product acceptance criteria.
- M1-E4 tests must prove that no User Template route exists and that User projections contain only the caller's assigned snapshot.

## Migration and rollback impact

No database migration or OpenAPI change is required. Before this ADR is accepted, rollback consists of removing this proposed ADR and restoring the execution-specification wording. After acceptance, introducing a User Template catalog requires a new additive OpenAPI decision; it must not reinterpret the existing User Subscription projections.

## Security impact

- The decision follows least privilege and avoids a new cross-tenant enumeration surface.
- Existing User endpoints must filter by the canonical authenticated User identity and must never accept a target `user_id` override.
- Admin Template and Subscription mutations retain operation-level RBAC, idempotency, audit, and optimistic-concurrency requirements.
- No secret, price, purchasable quota, or personal quota data is introduced.

## Contract and test files updated by this decision

- `docs/开发执行规格-v1.0.md`
- `docs/系统重构方案-v1.0.md`
- `docs/project-memory/open-items.md`
- M1-E4 authorization, contract, integration, and end-to-end tests for Template and User Subscription surfaces
