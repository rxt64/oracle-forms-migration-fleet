# Migration Pilot Delivery Tracker

This is the controlling delivery record. Small specs are implementation slices, not completion criteria.
Export/fixture pilot and genuine native Forms 6i qualification are separate outcomes. Neither is complete.

## Status Vocabulary

Implemented = code exists; Tested = scoped executable checks observed; Integrated = connected and
merged through required gates; Deployed = released artifact observed in Azure; Verified = operational
acceptance evidence retained. Blocked names an external prerequisite; NotExecuted means no execution
evidence. These labels are not interchangeable. Local implementation below remains unpublished unless noted.

## Refreshed Baseline

- Worktree: feat/dotnet-migration-pilot, based on d66095320270810e52151e36ece8cd8390343020.
- PR32: 714113c3a0482ac276280c979dc5e96ffee29a74, open against main.
- PR33: d66095320270810e52151e36ece8cd8390343020, open against PR32 branch.
- Prior observed CI35669953528 passed four required checks; deployment skipped. Reconfirm before merge.
- Protected `.agent_configs/` and `eval.yaml` remain excluded and untouched.
- Last live observation: workbench revision0000093, digest
  sha256:6599199edf85486c457e0943a8f80087a63bdff1f44958b6059f62d1fbbc3a4e.
- Azure source database container and ASP.NET browser replica were Running. That is not native Forms
  or a database correctness check. No VM was found in the inspected resource group, not a subscription-wide claim.

## Requirements And Next Actions

| Requirement | Implementation location | Status | Evidence | Blocker / next executable action |
|---|---|---|---|---|
| Neutral source facts | FormsSourceFacts, FormsIntermediateReader, SourceNormalizationAdapter; spec009 | Tested | PR33 exact-head CI35669953528 and astra-review-d660953.md focused PASS | Integrate under merge authorization; no overall delivery approval |
| Instruction consistency | AGENTS.md, .github/copilot-instructions.md | Implemented | Recovery patch preserves gates | Review diff and publish independently |
| Persistent property ledger | DispositionLedger, DispositionLedgerService, PlatformState stores/schema V5 | Tested | Real PostgreSQL atomic batch test; decision/content/fence-bound evidence; PILOT_LOCAL_VALIDATION.md | Wire runtime per-entry verification; retain cross-store fencing caveat |
| Decisions GUI | DispositionLedgerPanel, WizardApp | Tested | Shared ledger locator in approval/run; desktop/mobile 57 passed/9 conditional skips; real durable worker test | Fresh released GUI run with real APIs, not only mocked browser endpoints |
| React/.NET target selection | MigrationRunContracts, WizardApp, PlatformAuthorization | Tested | TARGET_BACKEND_STACK; immutable version/hash; both gates reject mismatch; independent QA122 | Set approved deployment configuration; old grants require renewal |
| Source-driven .NET generator | TargetMappingManifest, DotNet*Emitter, ApplicationCodeConversionAdapter | Tested | Three fixtures executed against local PostgreSQL; backend21/frontend4 cases per fixture | Trusted remote CI and runtime product verification, not local acceptance alone |
| Mapping decisions and coverage | TargetMappingManifest, GenerationAuthorization, generator mapping-manifest.json | Tested | Fresh ledger authority; explicit supported property carriage; stale evidence and replay regressions | Add DB dependency closure and supported behavior transformations; unsupported requirements still block |
| Isolated build/test | ProcessApplication*Gateway, Dockerfile, CI trusted caches | Tested | Restricted environment/unit tests; no cloud credentials in generated scripts | CI image and .NET runtime path validation; real target acceptance separate |
| Generated business acceptance | DotNetGeneratedApplicationPostgresTests | Tested | Local PostgreSQL16.15, three fixtures; 24/24 host tests; all required reports/sentinels | Execute mandatory runner CI; then independent deployed target acceptance |
| Owned Oracle order source fixture | infra/source-lab/meridian-order-entry | Implemented | 13 static/product-parser tests; explicit synthetic export; bounded schema and source verifier; independent scoped review | Trusted image build and approved lab deployment, then actual Oracle compilation/transaction/concurrency checks |
| Live Oracle source fixture | infra/legacy-estate; docs/NATIVE_SOURCE_PREREQUISITES.md | NotExecuted | Existing Oracle SELECT1 FROM DUAL passed; new order fixture not installed | Execute source-lab verifier after approved deployment; health alone is not data correctness |
| Native Forms6i qualification | SourceWorker, source profiles; spec008 | Blocked | Worker truthfully refuses extraction | Authorized media/patch/native libs, viable OS/runtime, DB/client and executable genuine FMB baseline |
| Summit source use | spec009/summit-source-manifest.json | Blocked | Pinned258c82b; declared122010400 preserved | Establish applicable rights and dependency closure; use owned fixtures meanwhile |
| Dedicated Azure target | infra/dotnet-pilot, infra/workbench | Implemented | Bicep compile and additive what-if, documented cost | Validate/apply authorized foundation; no app deployed by setup |
| Product-controlled build/deploy | Existing adapters/worker plus required controlled workflow | NotExecuted | No autonomous deployment evidence | Bind approved source/plan/artifact/target, runner build/digest, operation/recovery and URL |
| Platform release | .github/workflows/ci.yml and deploy.yml | NotExecuted | New code local, live platform unchanged | Publish candidate, exact-SHA CI/review, resolve merge authorization, release |
| Data migration/reconciliation | Existing migration/reconciliation adapters | NotExecuted | No new pilot run | Dedicated DB/principal; product applies schema/data and retains row reconciliation |
| Fresh GUI migration | Workbench durable runs | NotExecuted | No project/run ID for new pilot | Import, assess, decide, approve, generate, test, deploy and execute order workflow |
| Restart/retry safety | MigrationRunWorker leases/fences plus deployment state | NotExecuted | Existing run controls; new side effects untested | Exercise interruption at deployment/write boundary, prove no duplicate writes |
| Bounded model dispatch | Existing reviewer/Foundry integration | NotExecuted | No new pilot model invocation claimed | Use typed bounded roles only where needed; retain observed invocation evidence |
| Independent closure | Astra milestone records; PILOT_LOCAL_VALIDATION.md | Tested | Focused re-review closed four findings; addendum verified actual aggregate TRX and source hashes; overall HOLD | Exact-commit CI/review and separate operational review; no release approval |

## Operational Evidence Still Required

No generated target URL, verified image digest, migration run ID, ledger verified count, PostgreSQL
reconciliation or target business acceptance is claimed yet. Source-runtime differential: NotExecuted.
Manual interventions so far are product development/tests and infrastructure previews, not a product run.
All later interventions during a run must be recorded here and in that run's evidence.

Latest local checkpoint: full solution 1721/1721 passed after source-lab and filesystem hardening.
The earlier real PostgreSQL acceptance remains separately snapshot-bound, not rerun after hardening.
Scoped independent review passed these follow-ups; overall delivery remains HOLD. See
`PILOT_LOCAL_VALIDATION.md` for reports and review hashes. Work remains dirty and unpublished.
On 2026-09-22 the operator explicitly authorized selective commit, push and dependent draft PR publication,
with protected paths excluded. Any later Azure work is limited to development resource group
`rg-oracle-forms-migration-fleet-dev-b9f0e875`; merge, release, customer operations and product approvals
remain separate gates. Runtime publication remains pending. Independent runtime-verifier implementation
can proceed.

## Cost And Shutdown

Prepared foundation estimate: roughly USD0.30-4.50/month for artifact storage; target ACA scales to zero,
with compute/requests/logging/cross-region traffic usage-based. See infra/dotnet-pilot/README.md for
assumptions and exact resource diff. No new pilot resources applied yet. Preserve evidence/databases;
destructive cleanup requires explicit approval. Do not stop the shared PostgreSQL server for this pilot.