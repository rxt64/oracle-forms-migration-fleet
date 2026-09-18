# 004 — Implementation plan

## Authentication Boundary

Use Azure Container Apps built-in authentication for deployed requests. `WorkbenchAuthenticationOptions` validates startup mode and expected tenant/client/audience/issuer. `ContainerAppsIdentityProvider` validates the platform principal envelope and claim set before constructing `WorkbenchActor(tenantId, objectId, roles)`. Development uses `DevelopmentIdentityProvider` only when `IHostEnvironment.IsDevelopment()` and isolates actors under a non-Azure development tenant.

The live Container App already has auth enabled, HTTPS required, tenant-scoped issuer, allowed audiences, redirect-on-unauthenticated, and one allowed operator principal. This is read-only observed configuration, not proof that forged headers cannot reach the backend; live ingress qualification remains pending.

## Platform State

Introduce Hosting-level platform records and `IPlatformStateStore`:

- PostgreSQL adapter for `ofm_platform`, using managed-identity token connections.
- JSON file adapter for explicit Development mode, using lock + temporary file + atomic replacement.
- Ordered migration `V001` with schema-version ledger and idempotent DDL.

Records: organization, project, membership, target profile, approval request, immutable decision/audit events, and authorization grant projection. Optimistic concurrency uses an integer version checked on every state transition.

## Target Binding

`TargetProfile` is server-owned and versioned. Its canonical non-secret hash includes Azure resource and endpoint identity. The configured sandbox gateway exposes the identity it actually uses; persisted profiles must match it before sandbox authorization can materialize. A caller selects only an accessible profile ID.

## Approval Flow

1. Requester submits project/workspace/run intent and desired sandbox scope.
2. Server resolves membership, source facts, sanitized plan, and target profile; stores derived bindings.
3. A different project member with `SandboxApprover` decides.
4. Approved, unexpired, unrevoked records project an authorization grant and internal execution approval.
5. Planner and mutation authorizer receive the same server-owned binding; mutation authorizer rechecks store state immediately before each external write.

Production scope is denied in this increment even when requested.

## APIs And GUI

- `GET /api/workbench/context`
- `POST /api/workbench/projects`
- `GET /api/workbench/projects/{projectId}/approvals`
- `POST /api/workbench/projects/{projectId}/approvals`
- `POST /api/workbench/approvals/{approvalId}/decision`
- `POST /api/workbench/approvals/{approvalId}/revoke`

The GUI displays project/target/approval status and actions allowed by membership. No free-text approver authorizes a gate.

## Verification

- Offline identity claim tests.
- Durable local-store restart, migration replay, membership and approval state-machine tests.
- PostgreSQL SQL/mapping contract tests; live managed-identity integration remains pending if credentials are unavailable.
- Direct HTTP authorization tests and GUI state tests.
- Existing full .NET and Playwright suites.
- Bicep build and read-only live auth comparison; no deployment.