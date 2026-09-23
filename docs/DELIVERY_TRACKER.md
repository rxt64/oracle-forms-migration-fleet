# Migration Pilot Delivery Tracker

This is the controlling delivery record. Small specs are implementation slices, not completion criteria.
Export/fixture pilot and genuine native Forms 6i qualification are separate outcomes. Neither is complete.

## Status Vocabulary

Implemented = code exists; Tested = scoped executable checks observed; Integrated = connected and
merged through required gates; Deployed = released artifact observed in Azure; Verified = operational
acceptance evidence retained. Blocked names an external prerequisite; NotExecuted means no execution
evidence. These labels are not interchangeable. Local implementation below remains unpublished unless noted.

## Refreshed Baseline

- PR32 and PR33 are merged. PR34 head `46bcd135a4b8c260d67051fdda5dfb4ca0e09095` is merged;
  resulting main is `0454f08cf45223491566433847dc0dc16d4b96f5`.
- Main CI35916158560 passed build/test, native-worker contract, browser, container build, and both
  exact-commit workbench release jobs. Northstar deployment is separate from a generated Meridian app.
- Live workbench: `ca-ofmfleet-dev-ykbpnrpd--0000097`, provisioning Succeeded, image digest
  `sha256:a82eb9847ffb9f132ee7b50f871e0f8f20d1ba426306279c7d271c9775aab0cd`.
  That digest also appears in CI35916158560 deployment job107369985680.
- Protected `.agent_configs/` and `eval.yaml` remain excluded and untouched.
- Runtime verification/deployment follow-up is locally implemented, not yet released. Last full local
  solution run: 1830 fleet tests plus 56 demo tests passed. Subsequent independent QA added two tests
  and passed six fence/authorization cases and 20 policy/evidence cases; counts overlap.
- Parent invoked independent QA as `GPT-5.6 Sol (copilot)`. Parent invoked the checkpoint reviewer as
  `GPT-6 Astra (copilot)`; its scoped PASS covered PR34, not this unpublished runtime follow-up.
  Routing labels are not independent serving-backend identity attestations.

## Current Operational Evidence

- Meridian is installed in the existing Oracle source container: 37 verifier assertions passed,
  zero compile errors, seed counts 5/6/2/3, BANKING unchanged at 19 objects/0 invalid. Single-exec
  two-session tests proved commit blocking/oversell rejection and rollback release, then restored
  the fixture. See [MERIDIAN_AZURE_VALIDATION.md](MERIDIAN_AZURE_VALIDATION.md).
- Dedicated `ofm_dotnet_pilot` database and runtime/workbench identity grants are verified; the runtime
  identity was denied access to `ofm_platform`. No generated target SQL or data was applied. See
  [DOTNET_FOUNDATION_AZURE_VALIDATION.md](DOTNET_FOUNDATION_AZURE_VALIDATION.md).
- Runtime follow-up reads approved target catalogs after data migration and persists admitted evidence
  before deployment. It is structural verification, not business or native Forms equivalence.
- Deployment compares source/tenant/project/ledger/output bindings, and durable gateways authorize,
  renew the fence, then dispatch. No claim of atomic remote revocation after dispatch is made.
- Source lab installation/testing and identity/database foundation setup were builder interventions,
  not a GUI migration. No migration run ID, generated target URL/digest, deployed business acceptance,
  reconciliation, or restart/retry acceptance exists for this pilot.
- Publication excludes pending `infra/workbench` mount changes, pending `infra/dotnet-pilot` setup
  changes, and generated `infra/dotnet-pilot/main.json`. They remain local for separate validation.

The requirements table below retains checkpoint-level evidence; current operational facts above
supersede its historical source-lab, foundation, and platform-release status.

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

No generated target URL, generated-target image digest, migration run ID, ledger verified count, PostgreSQL
reconciliation or target business acceptance is claimed yet. Source-runtime differential: NotExecuted.
Manual interventions include product development/tests, source fixture installation/testing and dedicated
database/identity provisioning, not a product run.
All later interventions during a run must be recorded here and in that run's evidence.

The generated-target builder is written and tested but **unconfigured**, so no generated application tier
can be deployed yet: GitHub App configuration is missing or unconfirmed, and
`GitHubActionsDeploymentOptions.TryRead` registers no gateway when required fields are absent.
Restricted installation APIs do not prove that an App does not exist.
The exact non-secret fields, the single least-privilege App permission,
and the Key Vault secret identifier are listed in `OPERATIONS.md`, "Configure the generated-target
builder". This is an external blocker — creating the App and its key needs a human with repository-admin
and App-owner rights, and the federated CI identity cannot perform or bootstrap it.

Two gaps remain open inside `generated-target.yml` itself. It cannot ask the product whether an approval
was revoked mid-build, so it fails closed on expiry only. And generated code executes in the `build` job
as the runner's user, so every later step of that job is estate-influenced: the host image recipe is
pinned by digest in the workflow definition against exactly that, but nothing else in that workspace is.
The job holds no Azure credential, which is what bounds the consequence.

Historical PR34 checkpoint: full solution 1721/1721 passed after source-lab and filesystem hardening.
The earlier real PostgreSQL acceptance remains separately snapshot-bound, not rerun after hardening.
Scoped independent review passed that checkpoint; overall delivery remains HOLD. See
`PILOT_LOCAL_VALIDATION.md` for historical reports and review hashes. Runtime publication remains pending.
The operator explicitly authorized selective commit/push, PR creation, gated merge/release, source-lab
execution, and development provisioning within `rg-oracle-forms-migration-fleet-dev-b9f0e875`.
This does not waive exact-candidate reviews, CI, named product approvals, or security-scope constraints.

## Cost And Shutdown

Prepared foundation estimate: roughly USD0.30-4.50/month for artifact storage; target ACA scales to zero,
with compute/requests/logging/cross-region traffic usage-based. See infra/dotnet-pilot/README.md for
assumptions and exact resource diff. Identity/storage/database foundation now exists; no target app exists.
The pending SMB template would enable shared-key and public-network storage access; it was not applied.
Live storage keeps both disabled. A private Blob design needs new networking and workspace persistence
code because the existing Container Apps environment has no VNet. Moving the workbench also needs
explicit source-connectivity and authentication planning; this is not an approved infrastructure change.
Preserve evidence/databases;
destructive cleanup requires explicit approval. Do not stop the shared PostgreSQL server for this pilot.