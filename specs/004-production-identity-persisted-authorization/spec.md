# 004 — Production identity and persisted authorization

## Status

In progress on `feat/persisted-authorization`, based on CI-green integration commit `9eff943af718f71208330e2c64d3d1f1604b401f`.

Spec Kit is not installed in this repository. These artifacts are written manually without claiming that `/speckit.*` commands ran.

## Problem

Spec 003 strips browser-created authority and denies every external mutation. That is safe but not operational. Identity is still represented by platform headers behind an environment flag, there is no organization/project membership, target identity is only stack enums, and approvals cannot be requested, reviewed, persisted, revoked, or materialized into a planner gate.

## Requirements

- **AUTH-001**: Support exactly two explicit authentication modes: `Development` and `ContainerApps`. `Development` is valid only in the ASP.NET Development environment. Every other environment requires complete Container Apps authentication configuration and fails startup otherwise.
- **AUTH-002**: In Container Apps mode, accept platform identity only when the principal declares AAD authentication and exact expected issuer, audience/client ID, tenant ID, object ID, and role claims. Tenant ID plus object ID forms actor identity. Missing, malformed, mismatched, or untrusted claims return 401.
- **AUTH-003**: The Azure deployment uses Container Apps built-in authentication with HTTPS, no unauthenticated access to protected APIs, one configured tenant/client/audience, and no alternate backend ingress. Live ingress qualification remains separate evidence.
- **AUTH-004**: Development mode uses explicit isolated development actors, never production tenant identity, and never grants production-write scope.
- **AUTH-005**: Persist organizations, projects, tenant-scoped memberships, immutable versioned target profiles, approval requests, decisions, revocations, audit timestamps, and concurrency versions in the isolated `ofm_platform` schema. Apply ordered idempotent schema migrations.
- **AUTH-006**: Production uses PostgreSQL through managed identity. Development uses a durable local adapter with atomic writes so restart behavior is testable without cloud credentials. Neither adapter stores credentials in records or logs.
- **AUTH-007**: Target profiles bind environment, Azure tenant/subscription/resource group/resource ID, region, endpoint identity, database/schema, execution identity, stack, version, and canonical hash. Passwords and arbitrary caller URLs are forbidden.
- **AUTH-008**: Project membership authorizes source acquisition, workspace access, planning, runs, artifacts, approvals, and downloads. An object ID without its tenant or project membership grants nothing.
- **AUTH-009**: Authenticated APIs support project/context reads and approval request, approve, reject, and revoke actions. Requesters cannot approve their own requests. Production approval requires a distinct configured role and remains unavailable in this increment.
- **AUTH-010**: Approval request bindings are derived by the server from the owned source snapshot, sanitized plan input, persisted target profile, requested scope, and expiry. Clients cannot supply trusted hashes, actor identity, role, decision timestamps, or grant records.
- **AUTH-011**: A valid persisted sandbox approval may materialize the internal planner execution approval and grant only for the same tenant, project, actor, role, source, plan, target-profile version/hash, scope, and unexpired/unrevoked decision.
- **AUTH-012**: Authorization is rechecked immediately before each mutation. Revocation or drift stops the next safe checkpoint but never claims to undo committed effects.
- **AUTH-013**: Preserve planning and workspace artifact generation without mutation approval. Absent persistence, membership, target profile, or approval remains a denial.
- **AUTH-014**: The GUI shows authenticated project context, immutable target identity, real approval state, and permitted request/review/revoke actions. Typed contacts remain planning notes only.

## Acceptance Criteria

1. Non-Development startup fails when auth mode or expected identity claims are incomplete.
2. Wrong tenant, issuer, audience, authentication type, object ID, malformed roles, missing identity, and cross-project access are rejected.
3. Development actors are isolated and cannot obtain production scope.
4. Schema migration replay is idempotent and the local durable adapter survives restart.
5. Request, approve, reject, revoke, expiry, optimistic concurrency, and separation of duty are tested.
6. Changed source, plan, target profile/version, actor role, project, tenant, scope, expiry, or revocation denies.
7. A valid persisted sandbox approval authorizes the matching internal sandbox plan/grant; forged browser approval remains ineffective.
8. Production approval and live PostgreSQL/Container Apps ingress qualification remain explicitly pending unless executed.

## Out of Scope

- Durable run queue, worker leases, outbox, event replay, and durable artifacts (next increment).
- Production cutover enablement.
- Provisioning new Azure resources or changing the live auth/database configuration.
- Native Oracle acquisition or broader conversion coverage.