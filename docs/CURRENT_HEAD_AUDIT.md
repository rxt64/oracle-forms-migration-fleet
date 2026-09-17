# Current-HEAD Audit

Baseline: `c2d0ff561441ec0e8ef141dab59dcba5e5994a49`.

Audit date: 2026-09-17. Status reflects baseline behavior plus the trigger-body IR increment on `feat/behavioral-ir-trigger-bodies` where noted.

| Finding | Status | Category | Evidence and disposition |
| --- | --- | --- | --- |
| Trigger, program-unit, and LOV body fidelity | Partially fixed | Functional | IR v2 now retains trigger bodies in `FormsTrigger`, `SourceNormalizationAdapter`, and `FormsIntermediateReader`. Program-unit bodies and LOV queries remain names only. No emitter translates trigger text. |
| Generic generation emits one screen | Confirmed | Functional | `ApplicationCodeEmitter` emits the first screen and reports additional screens for manual review. |
| Northstar coverage proves semantics | Confirmed risk | Inaccurate claim | `NorthstarBankingApplicationProfile` recognizes a structural fingerprint and known program-unit identities, then emits templates. It does not compare program-unit semantics. |
| Browser-supplied approvals and verified evidence are trusted | Confirmed | Security | `WizardApp.tsx` constructs approvals and `isVerified: true`; `WorkbenchEndpoints` deserializes them and server preparation does not bind them to a durable authenticated approval record. No production cutover adapter currently limits the blast radius, but the trust defect remains. |
| Workspaces and runs are durable | Confirmed gap | Architecture | `SourceWorkspaceService` uses replica-local memory and a four-hour lifetime. `ResetOutput` deletes prior `.fleet-run` output except selected repairs. No durable run/event/artifact store exists. |
| Equivalent target-stack support | Confirmed gap | Inaccurate claim | Contracts advertise multiple database targets, while conversion and migration adapters execute only PostgreSQL. Frontend/backend enums currently expose only React and Java/Spring Boot. |
| Live Oracle connector and native Forms worker | Confirmed gap | Architecture | No Oracle client/connector or authorized native Forms extraction worker is registered. Intake uses supplied files, ZIP, and Git workspaces. |
| Schema deployment, differential testing, deployment, and cutover | Partially fixed | Functional | Schema preparation/execution now runs through `IDataMigrationGateway`. Differential behavior testing, human acceptance execution, generic deployment, and production cutover adapters remain absent. |
| Northstar deployment is per-project | Confirmed gap | Architecture | Workflow and Bicep defaults are tied to fixed development resource names. |
| Workbench Java build executes generated tests | Confirmed gap | Functional | The in-product gateway and generated Dockerfile use `-DskipTests`; the demo workflow separately runs Maven tests. |
| Version matrix proves native compatibility | Confirmed risk, disclosed | Inaccurate claim | Tests use synthetic XML and `COMPATIBILITY.md` explicitly disclaims native extraction/runtime qualification. |
| PostgreSQL sandbox is project-isolated | Confirmed gap | Security | One singleton gateway targets one configured PostgreSQL server/database for all sessions; no per-project schema/database boundary exists. |

## Priority

1. Replace client-trusted approvals/evidence with authenticated durable backend records.
2. Add durable runs, events, artifacts, leases, and recovery.
3. Isolate sandbox database state per project/run.
4. Add a bounded trigger-body analysis consumer without claiming translation.
5. Generate multiple screens and routes or block affected modules explicitly.
6. Run generated tests inside the product build gate.

## Evidence Limits

Passing CI proves only the checks that CI actually invokes. This audit does not claim native Forms compatibility, runtime equivalence, Azure cost approval, Oracle entitlement, or production readiness.
