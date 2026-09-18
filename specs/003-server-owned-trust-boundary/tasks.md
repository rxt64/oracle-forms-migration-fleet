# 003 — Tasks

> Spec Kit is not available in this repository; this checklist was written by hand.

| # | Task | Requirement | File | State |
|---|---|---|---|---|
| T1 | Phase mutation authorizer interface and result types | R5 | `Fleet/Execution/PhaseMutationAuthorization.cs` | Done |
| T2 | Optional authorizer on `MigrationExecutor`, checked immediately before a `SandboxDatabaseWrite` or `ProductionWrite` phase | R5 | `Fleet/Execution/MigrationExecutor.cs` | Done |
| T3 | Deterministic source snapshot hash at acquisition, excluding `.fleet-run`, bounded by intake limits | R3 | `Hosting/SourceWorkspaceService.cs` | Done |
| T4 | `SourceWorkspaceFacts` + owner-checked `Describe`, keeping the hash off the serialized summary | R3 | `Hosting/SourceWorkspaceService.cs` | Done |
| T5 | `WorkbenchActor`, grant record, store interface, deny-all store | R4, R6 | `Hosting/WorkbenchTrustBoundary.cs` | Done |
| T6 | `WorkbenchAuthorizationService` checking every binding, fixed-time hash comparison | R4, R7 | `Hosting/WorkbenchTrustBoundary.cs` | Done |
| T7 | `WorkbenchTrustBoundary.Prepare`: derive verified evidence, force declarations unverified, clear signals, approvals, attestations | R1, R2 | `Hosting/WorkbenchTrustBoundary.cs` | Done |
| T8 | Canonical structured `PlanInputHash` and `TargetHash` | R4 | `Hosting/WorkbenchTrustBoundary.cs` | Done |
| T9 | `WorkbenchMutationAuthorizer`, fail-closed, re-evaluated per call | R5, R6 | `Hosting/WorkbenchTrustBoundary.cs` | Done |
| T10 | `WorkbenchExecution.TryPlanRun` — the exact method the plan endpoint calls, rejecting unowned workspaces | R1, R7, R9 | `Hosting/WorkbenchExecution.cs` | Done |
| T11 | `WorkbenchExecution.TryPrepareRun` — the exact method the execute endpoint calls | R1, R5 | `Hosting/WorkbenchExecution.cs` | Done |
| T12 | Both endpoints routed through T10/T11; execute passes the authorizer to the executor | R1, R5 | `Hosting/WorkbenchEndpoints.cs` | Done |
| T13 | `Actor` / `Roles` from `X-MS-CLIENT-PRINCIPAL-ID` and `X-MS-CLIENT-PRINCIPAL` only | R8 | `Hosting/WorkbenchEndpoints.cs` | Done |
| T14 | `workspaceId` on the plan request when a copied source exists | R9 | `ClientApp/src/WizardApp.tsx` | Done |
| T15 | Boundary tests: forged approvals, forged verification and signals, forged attestations, owner mismatch, project mismatch, source hash mismatch, plan-input and target mismatch, wrong role, wrong scope, expired grant, valid scoped grant, per-mutation recheck, artifact generation retained | R1–R9 | `tests/.../WorkbenchTrustBoundaryTests.cs` | Done |
| T16 | Spec, plan, tasks, verification | — | `specs/003-server-owned-trust-boundary/` | Done |
| T17 | Bind source evidence and snapshot hash to the exact selected workspace folder | R2, R3, R7 | `Hosting/SourceWorkspaceService.cs` | Done |

## Deliberately not done

| Item | Why |
|---|---|
| An endpoint that issues a grant | R6. There is no trusted store to persist it in and no trusted identity service to attribute it to. Adding one in the request path would recreate the defect. |
| A `[JsonIgnore]` snapshot hash on `SourceWorkspaceSummary` | A separate server-only record is stronger: the value never reaches a serializer. |
| Changes to `MigrationRunPlanner` | The planner was already correct. Only its inputs were untrusted. |
| A production route that emits test fixtures | Browser tests intercept streams or upload an in-memory ZIP against the local host; no fake-run route belongs in production. |
