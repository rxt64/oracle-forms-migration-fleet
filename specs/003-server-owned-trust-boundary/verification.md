# 003 — Verification

> Spec Kit is not available in this repository; this record was written by hand.

## What was run

All commands from `oracle-forms-migration-fleet/`. `dotnet` is at `$env:USERPROFILE\.dotnet\dotnet.exe`
and is not on `PATH`.

```powershell
& "$env:USERPROFILE\.dotnet\dotnet.exe" build '.\src\oracle-forms-migration-fleet\oracle-forms-migration-fleet.csproj' --nologo -v q
```

Exit code 0, no errors, no warnings.

```powershell
& "$env:USERPROFILE\.dotnet\dotnet.exe" test '.\tests\oracle-forms-migration-fleet.Tests\oracle-forms-migration-fleet.Tests.csproj' --nologo `
  --filter "FullyQualifiedName~WorkbenchTrustBoundaryTests"
```

`Failed: 0, Passed: 29, Skipped: 0, Total: 29`

```powershell
& "$env:USERPROFILE\.dotnet\dotnet.exe" test '.\tests\oracle-forms-migration-fleet.Tests\oracle-forms-migration-fleet.Tests.csproj' --nologo `
  --filter "FullyQualifiedName~WorkbenchExecutionTests|FullyQualifiedName~WorkbenchExportTests|FullyQualifiedName~MigrationRunPlannerTests|FullyQualifiedName~MigrationExecutorTests|FullyQualifiedName~SourceWorkspaceTests"
```

`Failed: 0, Passed: 139, Skipped: 0, Total: 139`

```powershell
cd src/oracle-forms-migration-fleet/ClientApp; npm run build
```

`tsc -b` clean, vite built `app.js` 315.99 kB, `styles.css` 38.96 kB, `index.html` 1.05 kB.

Final validation from the repository root:

```powershell
& "$env:USERPROFILE\.dotnet\dotnet.exe" test '.\oracle-forms-migration-fleet.slnx' --nologo
```

`Failed: 0, Passed: 1190, Skipped: 0, Total: 1190`

From `src/oracle-forms-migration-fleet/ClientApp`, `npm run test:e2e` passed 96 tests, intentionally skipped 8 project/device-inapplicable cases, and failed 0. The run includes direct HTTP missing-principal, forged-plan, cross-owner, and forged-execute checks against the real host.

## Coverage map

| Requirement | Test |
|---|---|
| R1 forged verification and signals | `Plan_endpoint_refuses_a_forged_verification_flag_and_forged_signals` |
| R1 forged approvals | `Plan_endpoint_never_authorizes_a_mutation_on_the_strength_of_forged_approvals`, `Execute_endpoint_clears_forged_approvals_and_attestations_before_planning` |
| R1 forged attestations | `Execute_endpoint_clears_forged_approvals_and_attestations_before_planning` |
| R2 derived-only verification | `Plan_endpoint_derives_verified_evidence_only_from_the_indexed_source` |
| R2/R7 owner mismatch rejects the workspace | `Plan_endpoint_rejects_a_workspace_owned_by_someone_else` |
| R2 selected source scope excludes sibling evidence | `Plan_endpoint_derives_evidence_only_from_the_selected_source_folder` |
| R3 hash determinism, sensitivity, opacity | `A_changed_source_copy_produces_a_different_snapshot_hash` |
| R4 binding sensitivity | `A_changed_plan_input_or_target_produces_a_different_binding` |
| R5 recheck per mutation | `A_grant_is_rechecked_before_every_mutation_so_a_later_expiry_denies` |
| R5 executor refusal | `The_executor_refuses_an_authorized_mutating_phase_when_the_authorizer_denies` |
| R5 existing callers unchanged | `An_executor_without_an_authorizer_keeps_the_planner_as_the_only_gate` |
| R6 default deployment denies | `Execute_endpoint_supplies_a_fail_closed_authorizer_when_no_grant_exists` |
| R6 generation retained | `Plan_endpoint_still_authorizes_artifact_generation_from_indexed_source` |
| R7 owner, engagement, role, scope, source, input, target drift | `A_grant_that_does_not_match_the_run_in_every_respect_denies` (7 cases) |
| R7 valid scoped grant | `A_scoped_grant_authorizes_the_run_it_was_issued_for` |
| R4/R6 grant storage does not materialize planner approval | `A_matching_store_record_does_not_materialize_a_planner_approval_in_this_deployment` |
| R7 valid record survives an older expired record | `A_valid_grant_is_not_hidden_by_an_older_expired_matching_record` |
| R2 generated output is never source evidence | `Workbench_output_never_becomes_verified_source_evidence` |
| R2 output directory cannot be selected as source | `Plan_endpoint_rejects_the_workbench_output_as_a_source_folder` |
| R2 nested output paths rejected on plan and execute | `Plan_and_execute_reject_any_source_inside_the_workbench_output` (2 cases) |
| terminal activity cannot call zero work success | `Run_outcome_requires_at_least_one_executed_phase_and_no_authorized_gap` and direct HTTP forged execute |
| R9 no workspace leaves everything unverified | `Plan_endpoint_leaves_every_declaration_unverified_when_no_workspace_is_supplied` |
| ownership on the execute path | `Execute_endpoint_refuses_a_workspace_the_caller_does_not_own` |
| real HTTP plan sanitization | `trust-api.spec.ts`: forged verification, approvals, and attestations cannot open plan gates |
| real HTTP ownership rejection | `trust-api.spec.ts`: another actor's workspace ID returns 404 |
| real HTTP execute sanitization | `trust-api.spec.ts`: forged execution authority is removed before planning |

## Limits of this verification — stated precisely

The .NET boundary tests are not HTTP tests. Both endpoints were reduced to a single call each —
`WorkbenchExecution.TryPlanRun` and `WorkbenchExecution.TryPrepareRun` — and every test above runs against
those exact methods. The endpoint bodies contain no authorization logic of their own.

The checked-in Playwright suite drives the real HTTP host for forged planning, cross-owner workspace rejection, and forged execution. What is still not covered at HTTP level is:

- `WorkbenchEndpoints.Actor` / `Roles`, the header parsing that builds the actor.

That parsing path is thin and readable in `WorkbenchEndpoints.cs`, but no test exercises role-claim decoding. A reviewer should read it directly.

**No grant can be issued or materialized into planner approval in this deployment.** `NoWorkbenchAuthorizationStore` is the registered default,
so the valid-grant and drift tests use a stub store. What is proven is that the service accepts a matching
grant and rejects every mismatch — not that any production path can produce one. Nothing can, by design.

**The GUI path for `workspaceId` is exercised with a real ZIP upload to the local host.** It proves local source acquisition, ownership, ID propagation, scoped evidence derivation, and planning. It does not prove Git acquisition, native Oracle extraction, or cloud identity behavior.

**Production identity headers are a deployment precondition.** The host ignores `X-MS-CLIENT-PRINCIPAL*` unless `WORKBENCH_ENTRA_AUTH_ENABLED=true`. The browser tests enable that switch to exercise ownership. They do not prove that a production reverse proxy overwrites caller-supplied headers; that must be qualified before registering a non-empty authorization store.
