# Migration Pilot Delivery Tracker

This is the controlling delivery record. Small specs are implementation slices, not completion criteria.
Export/fixture pilot and genuine native Forms 6i qualification are separate outcomes. Neither is complete.

## Gateway Installation Retry: Succeeded

Trusted main CI `36943306853` completed successfully for
`73c5666ef39e792900c36555183f07f35aed043e`, including all four required checks and both
release jobs. Approved installation retry `36945107616` completed successfully: the
allowlisted guest result reports `installed` and `OFMSourceGateway` is `Running`.
Managed Run Command cleanup succeeded. The installed bundle SHA-256 is
`6a0d4b33592b2790cc57c2b687a6d70324dee3e59ae57f5b7d43b87fa809f6f6`.

The public root certificate was recovered from that successful workflow's allowlisted
result to `infra/source-gateway/trust/ofm-source-gateway-dev-root-2026.cer`. Its SHA-256
is `f285dc714a836a0c3b34855e4eb7c17009c5ac48421b7ca4092252aee1f2a327`, matching the
previously persisted root. Local validation confirmed its CA constraint, validity,
expected thumbprint, and absence of a private key. Certificate publication and the
trusted image overlay remain pending.

Service installation is not native extraction or end-to-end gateway qualification.
The private workbench and PostgreSQL endpoint remain undeployed; its sign-in callback
still requires interactive lab-tenant access. No Oracle credential was provisioned,
no migration DDL executed, no rows moved, and no migrated application verified.

## Status Vocabulary

Implemented = code exists; Tested = scoped executable checks observed; Integrated = connected and
merged through required gates; Deployed = released artifact observed in Azure; Verified = operational
acceptance evidence retained. Blocked names an external prerequisite; NotExecuted means no execution
evidence. These labels are not interchangeable. Local implementation below remains unpublished unless noted.

## GitHub Checkpoint, 2026-09-29

The operator authorized documenting and pushing all accumulated implementation work to
`feat/runtime-verification-deployment`. This checkpoint includes native extraction and
gateway code, GUI preparation, trusted artifact normalization, regression tests, dedicated
.NET target configuration, source-lab tooling, and the associated operational records.
Publication does not mean merge, deployment, approval of a migration run, or successful
native qualification. Exact-commit CI and the existing release/review gates still apply.

Publication excludes machine-local protected `.agent_configs/` and `eval.yaml`, generated
ARM JSON, build outputs, raw test/browser artifacts, credentials, and Oracle installation
media/binaries. They are not source changes to publish. Test outcomes and relevant hashes
are recorded below; operational credentials remain outside the repository.

The private-artifact connectivity document is a proposal, not authorization to enable
SMB, public storage access, shared keys, or a different security boundary. Publishing
infrastructure scripts does not run them. The current live migration status remains
**NotExecuted / HOLD**.

## Full Fleet Migration Test Handoff

Operator request, 2026-09-28: perform a comprehensive migration test using the migration
fleet now that the native Meridian source application is working. This is the next
end-to-end delivery objective, not permission to bypass approvals or a claim that the
migration has started. GUI intake, planning, and a source-compatibility probe were exercised
on 2026-09-29; migration execution remains **NotExecuted**, blocked before queueing a run.

### Ownership And Rules

- Application specialist: Claude Opus 5. Own product capability gaps, native extraction,
  schema/application conversion, GUI reachability, and bounded repairs. We build the
  fleet; only its planner-authorized adapters perform migration operations from the GUI.
- Infrastructure specialist: GPT 5.6 Sol. Verify approved source connectivity, dedicated
  Azure target, builder configuration, and trusted release provenance. Use approved
  `infra/` paths for foundation work, not one-off migration infrastructure.
- Independent QA: GPT 5.6 Sol. Receive the specialists' explicit handoffs, verify the
  released candidate and real run evidence, and return PASS/HOLD with unmet criteria.
  Existing exact-candidate CI and independent Astra review requirements remain in force.
- Agents propose; deterministic code authorizes. Preserve separate named human plan,
  sandbox execution, and production approvals. This coordination request is not a
  recorded approval on any product run and does not authorize production cutover.
- No console DDL, manual row copies, hand-deployed generated applications, fabricated
  attestations, checked evidence boxes, or substituted fixtures to get a green result.
  Fix missing capabilities in the product, release through the trusted runner, and retry
  through the GUI. Stop at genuine external authorization/licensing prerequisites.
- Deployable images come only from trusted GitHub CI with OIDC, exact commit/digest
  provenance, and required reviews. Do not substitute workstation builds. Preserve
  unrelated work, protected configuration, source databases, and existing evidence.
- Keep secrets out of prompts, logs, evidence, and output. Preserve scoped connectivity,
  security controls, and step/cost budgets. No governance bypass or destructive cleanup.

### Required Test And Evidence

1. Recheck source health and pin the genuine generated native FMB, release/tool
   compatibility, hashes, dependency closure, and source behavioral baseline. Use
   [native runtime qualification](FORMS6I_AZURE_INSTALLATION_VALIDATION.md#native-meridian-runtime-verified-2026-09-28)
   as the starting evidence, not a substitute for source extraction by the fleet.
   Do not import the original empty stub or relabel synthetic XML as native extraction.
2. Verify the actually released workbench commit/digest, builder availability, dedicated
   target identity/database, and recorded approver. Recheck known builder and release
   blockers below; do not infer readiness from source code or stale release evidence.
3. Drive the real browser with live APIs: import, inventory/extract, assess, record mapping
   decisions and coverage, obtain approvals, then authorize execution. Capture project/run
   IDs, ledger version/hash, source snapshot, plan, target, approvals, and adapter artifacts.
4. Have the fleet generate AND execute Azure-target schema, move rows, and reconcile
   tables, keys, relationships, counts, amounts, and procedure/sequence semantics against
   the pinned source. Mere SQL generation or a connection health check is insufficient.
5. Have the fleet generate, build, test, deploy, and run BOTH the application and database
   on the approved Azure components. Retain runner/workflow IDs, commit, image digest,
   deployment identity, target URL, compiler results, and successful runtime attestations.
6. Independently exercise source/target equivalents: populated customer/product choices,
   product/quantity totals, create/save, cancel/rollback, invalid quantities, constraints,
   and required transaction/concurrency behavior. Current source examples include order
   1001 (quantity 1, total 84.50), cancelled 1002 absent, and 1003 (quantity 2, total 169.00).
   Recapture the baseline if it changed; never seed expected target answers by hand.
7. Exercise bounded failures and restart/retry at write/deployment boundaries, preserving
   checkpoints and proving no duplicate writes/deployments. Test approval expiry/revocation,
   stale or tampered evidence, unsupported required constructs, and denied execution.
   Keep such tests scoped to the approved run; do not stop shared databases or the source VM.
8. Prove the deployed target serves the required workflows with **no Oracle in its runtime
   path**, not just a frontend proxy to the source. Retain dependency/configuration and
   request evidence. Record every intervention and distinguish Tested, NotExecuted, and
   Blocked. Independent QA closes the complete matrix, not an isolated happy-path demo.

Specialist-to-QA handoff must identify the exact candidate, changed product capabilities,
focused checks, trusted CI/review evidence, GUI run and approval IDs, artifact hashes,
reconciliation results, deployed URL/digest, behavioral and recovery results, residual
risks, and next owner for each blocker. No migration completion claim until all four
standing completion tiers are evidenced: executed schema, reconciled data, running
generated application, and Oracle-free target runtime.

The source lab must remain usable: Oracle9i currently needs the signed-in administrator
desktop session with its listener in that same session. Do not log it off or assume
unattended reboot recovery. The existing full-fleet migration/release HOLDs below are
not cleared by the successful source Forms tests.

Handoff delivery: the application specialist (Claude Opus 5) and infrastructure
specialist (GPT 5.6 Sol) acknowledged their scopes in read-only briefings. Their findings
were explicitly delivered to a separate GPT 5.6 Sol QA invocation, which acknowledged
both and returned **NotExecuted / HOLD** against the full matrix. No migration, approval,
release, or resource mutation occurred in these briefings; no background execution is
implied. First readiness gates are genuine fleet-mediated native extraction, exact-candidate
CI/Astra evidence, builder/private connectivity and target readiness, followed by the
applicable named product approvals. Production approval applies only to production
deployment/cutover, not to a sandbox-only test. A superseded memory-only patch statement
in the application briefing was corrected before QA: the IPC patch is installed on disk
with its rollback backup; desktop-session dependence is the remaining source limitation.

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
  solution run: 1949 tests passed across the solution before the final source-derived plan repair.
  That repair passed two new regressions and 84 focused checks. Counts overlap; Docker containment
  did not run locally and is now mandatory on the trusted CI runner.
- Parent invoked independent QA as `GPT-5.6 Sol (copilot)`. Parent invoked the checkpoint reviewer as
  `GPT-6 Astra (copilot)`; its scoped PASS covered PR34, not this unpublished runtime follow-up.
  Routing labels are not independent serving-backend identity attestations.

## Runtime Follow-up Review

Draft PR35 publishes `cd78d878e9d8b1eeabd3b50aa0d74cf83b8aefea`; all four checks in CI35927480197
passed, deployment skipped. Parent then invoked `functions.runSubagent` with model
`GPT-6 Astra (copilot)` against that exact commit and base `0454f08cf45223491566433847dc0dc16d4b96f5`.
The reviewer returned HOLD despite 392 focused tests passing. Its scope excluded dirty infrastructure
and protected configuration; historical Azure evidence was not independently re-observed.

Nine findings covered runner containment, late approval checks, dispatch authorization, report wire
binding, atomic retry claims, incomplete verification plans, fixture cleanup ownership, exact revision
readiness, and result storage coordinates. Repairs are implemented locally pending new exact-SHA CI
and Astra review. Independent Sol QA closed eight, then found that deleting a source-required column
shape from both plan and results evaded the self-consistency audit. The ledger now rebuilds expected
probes from retained IR/mapping/source before admitting results; focused regressions passed.

The generated build now executes inside the trusted Docker recipe, without host command files,
credentials, or a Docker socket mounted into build steps. The executable containment regression is
required in CI, not credited from its locally environment-gated return. Recovery uses ETag claims and
refuses ambiguous dispatch rather than automatically risking a duplicate. Result schema v3 binds the
operation/source/plan/target/artifact and declared readiness. None of this constitutes a live builder run.

The parent invoked Astra again on `2e6386e8223d70db6510c255421ff016787fdf07`. It ran 298 scoped
checks and retained HOLD for two defects: SQL expectations were not pinned to the approved source
snapshot, and expiry was checked before image loading rather than immediately before registry push.
Repairs now reuse the existing source snapshot algorithm while retaining the exact hashed SQL/mapping
buffers for parsing, and check expiry directly before `docker push`. Post-start SQL substitution and
expiry-during-load regressions pass. The parent then ran the full solution: 1954/1954 passed.

CI35933261950 for that earlier candidate passed its mandatory Docker containment execution and evidence
upload. Its later full-suite step failed only the hostile-ZIP test's platform-specific refusal-message
expectation; the guard refused on both platforms. The repaired assertion accepts either explicit unsafe
path reason for that backslash case while retaining nonzero exit, empty output, and the benign control.
The exact repaired SHA still requires fresh runner CI and Astra review; neither prior HOLD is waived.

## Current Operational Evidence

### Authorized Gateway Installation, 2026-10-01

The operator replied `keep going ill authirize` to the explicit reviewed lab gateway
deployment and Forms VM installation request. This authorizes that infrastructure,
identity and service-installation scope within the existing USD 70/month planning
ceiling, not Oracle writes or approval of a migration run.

Stage-one Azure Validate completed before apply. Deployment
`ofm-source-gateway-foundation` succeeded at `2026-10-01T19:38:59Z`, creating
the `.64/27` ACA subnet, `.96/27` private-endpoint subnet, and internal environment
`cae-ofmfleet-private-dev`. Its domain is
`bravesky-bbd4eb91.eastus2.azurecontainerapps.io`; `10.246.0.75` is its inbound
address, not a PostgreSQL firewall egress address. Stage two remains unvalidated.

Gateway Entra application `b16b4127-9ef6-44a1-9f07-bbfe92053baf` is single-tenant
with v2 tokens. Service principal `c7f29be4-bffe-44e9-8108-1449571a031c` requires
app-role assignment; its sole `SourceGateway.Invoke` assignment is the approved
workbench UAMI. The existing Forms VM was started for installation, and its agent
and installed extension report Ready/Succeeded. The native DLL hash matches its pin.
No Oracle operation or gateway extraction was performed.

Installation preflight found no guest GitHub CLI or authentication. The new manual-only
trusted-main workflow therefore publishes a hash-bound public bundle containing only
our worker, installer, registry and CI proof, then installs through a pinned managed
Run Command. No repository token, Oracle binary/source, or credential is sent in that
bundle. Guest code verifies the outer hash before path-safe, size-limited extraction;
temporary command and staging cleanup are checked. Only service status and public root
certificate evidence are returned.

Actual v2 audience testing exposed a separate 401 defect: the API scope uses an
`api://` identifier URI while v2 tokens carry the API GUID as audience. The worker now
accepts only that exact corresponding pair for a strictly parsed GUID configuration.
Issuer, signature, lifetime, caller and project restrictions are unchanged. Opus
reproduced the failure and repaired it; Sol independently passed 60 focused checks.

Installation transport QA passed PowerShell behavior tests, nine Windows PowerShell
5.1 parser checks, and 41 workflow tests. Astra found a nullable header property access
that failed under Windows PowerShell; it was fixed and the actual length guard passed
5.1 runtime tests for absent, valid and oversized headers. Astra's scoped code-release
PASS permits publication and trusted CI, not a claim that service installation has run.

PR41 merged as `d8ea746ea71eb3d3649f3656cab7f84bbe8a56d5`; main release
`36926345611` passed all gates. Authorized installation run `36929115699` verified
and published the code-only bundle and proved unauthenticated download, but the guest
failed before installation. Windows PowerShell invoked `IsInRole` on WindowsIdentity
before applying the cast. Read-only guest diagnostics confirmed no service, installed
worker, or gateway certificates existed after that attempt; transport cleanup ran.

The administrator guard was corrected in the installer and both credential helpers.
The regression executes each actual AST-extracted guard rather than merely parsing
the script. Independent Sol and Astra checks passed; Windows PowerShell 5.1 returned
the correct Boolean for all three, while the old expression reproduced MethodNotFound.
The retry requires a new trusted CI/release and bundle. Separately, Graph authentication
for the existing workbench sign-in app failed with AADSTS90072 for the current CLI
identity. Its private callback was not added; no registration deletion is inferred.

PR42 hotfix `f8ad9ce8fd327087c162be61fb6d7e5ef0fbefcf` passed exact CI
`36935886530`, independent PowerShell 5.1 runtime checks, and Azure Validate for the
bounded retry. It merged as `47118508a3c9ba3417a819299a2845483bb4db8a`;
main release `36936606541` passed all CI and deployment gates. Authorized installation
retry `36938165047` was dispatched once with that main SHA and CI run. Its service
creation failed with `sc.exe` exit 1639 because option/value pairs were passed as
single native arguments. Guest diagnostics confirmed no service or installed worker
remained after rollback, and managed command cleanup succeeded. Certificate preparation
did persist: root thumbprint `888992B079A2C57503C3EC38BF68C4BB4FCDB069`, leaf
`99FF9A6B60B35F2A1F74D65B079BE7AE5D531CD9` for `gateway.ofm.source.internal`,
and public root SHA-256
`f285dc714a836a0c3b34855e4eb7c17009c5ac48421b7ca4092252aee1f2a327`.
These certificates are retained for the retry, not rotated.

The next scoped installer fix separates SCM option/value tokens, preserves the quoted
executable path under Windows PowerShell 5.1 native marshalling, and restores previous
service configuration before restarting during upgrade rollback. A non-mutating native
argv-capture check verified all eight creation arguments; PowerShell behavior tests and
parser checks passed. Independent Sol QA and Astra code-publication closure passed.
No real service mutation was used for local tests. A read-only public-certificate retrieval
is pending; it is not installation or extraction evidence.

The private-workbench preview/apply workflow is implemented but not deployed. It binds
current-main CI, the trusted overlay-image provenance and public-root digest, requires
a reviewed preview before apply, and rechecks the exact create-only resource allowlist.
It passes the existing authentication secret through restricted secure parameters without
displaying or rotating it. Independent QA repaired a false CI job-count restriction;
Astra identified and closed native-verifier exit propagation and strict-mode empty
comparison defects. Actual PowerShell regressions and 43 focused workflow tests pass.
Final scoped CODE PUBLICATION PASS does not mark Stage 2 validated or solve the existing
Graph identity/callback access blocker.

### Gateway Publication Review, 2026-09-30

The unattended gateway and private-network installation artifacts are implemented but
not installed. Independent QA closed the final publication findings: credential-input
validation now precedes platform checks, restricted ACLs replace existing explicit grants,
the trust-overlay workflow declares Actions read permission, and transfer-certificate
cleanup deletes and verifies removal of the CNG private key, including creation failures.

Observed local checks: 317/317 Windows worker tests, 3/3 focused credential tests, installer
behavior/parser checks (`SOURCE_GATEWAY_POWERSHELL_TESTS_PASS`), clean diagnostics and
scoped whitespace checks. No native Linux execution was available locally; trusted CI
must supply that evidence. Astra returned scoped CODE PUBLICATION PASS for these fixes,
not host-apply or operational qualification. An interrupted unrelated test invocation is
not counted as evidence.

Host installation still requires the successful main-push worker artifact, separately
approved private connectivity and stable PostgreSQL egress, Entra gateway identity,
certificate trust publication, and protected read-only Oracle credential provisioning.
No gateway service, certificate, identity, network resource or source database was changed
by these checks. The actual migration remains NotExecuted.

Gateway commit `0c5154f437b3a34e45817cc32844f2796ca41ba3` passed all four
required CI jobs in `36748627097`, including Ubuntu worker execution and Windows
worker checks. Exact-candidate code-release validation completed; PR39 merged as
`c2595857471fc5a39e5b509dfd4d834acf2c1952`. Trusted main CI/release
`36757666326` succeeded, including authenticated smoke and cleanup. Independent
verification observed healthy, active revision `ca-ofmfleet-dev-ykbpnrpd--0000103`
at 100 percent traffic on digest
`bb66f30a14ee34ba164a884b7519f0f83f1e4eaf43ddccb9f89007a6a2d4d1ee`,
matching commit-tagged ACR image `c2595857471f`. No temporary smoke ACI remained.

Trusted worker artifact `11116782905` was downloaded and verified without execution:
main commit/ref/event/run/attempt, x86 PE32 headers, length 99,413,830 bytes and
SHA-256 `dfb6d61d6ffc3aabc0c1b08bd5509cc6a7ce5bad9c57849b7170a7245f7f44ee`.
The package is ready for installation; the gateway is not installed or qualified.

### Private PostgreSQL Route, 2026-09-30

The live PostgreSQL server supports Private Link with subresource `postgresqlServer`
and zone `privatelink.postgres.database.azure.com`. The bounded artifact proposal adds
a private endpoint on the free `10.246.0.96/27` subnet, its private DNS wiring, and
limits the private workbench to one replica. Existing PostgreSQL public access and
firewall rules remain unchanged. No NAT Gateway or guessed outbound firewall IP is used.
Estimated incremental cost is about USD 27.05/month scaled to zero or USD 66.47/month
continuously active, before queries, logs and data processing. The USD 70 planning
ceiling is not a hard billing cap.

Independent Sol artifact QA passed; Astra closed the subnet concurrency finding after
the endpoint subnet was made dependent on the ACA subnet. Both templates compile,
PowerShell behavior/parser checks pass, and the recorded Checkov scan reports four
passed checks and no failures. Fresh full foundation what-if reports three creates
(ACA environment and two subnets), 37 ignored existing resources, no modifications,
no deletes, and no diagnostics. An earlier two-create report was incomplete and is
not the controlling evidence. The application-stage what-if still requires real
foundation outputs and the trusted image/auth inputs. This is artifact-generation
and validation evidence only; infrastructure apply, Entra changes, Forms VM service
installation and Oracle credential provisioning have not been authorized or performed.

Exact candidate `4dc0a34c589e629cddcd711480123d7c949e6f70` passed bounded
CODE PUBLICATION validation for PR40. CI `36758169984` completed successfully for that
exact SHA: all four required jobs succeeded, including infrastructure syntax and the
pinned Checkov scan, while deployment was skipped. The candidate changes only the four
private-route artifacts and this tracker; public `deploy.yml` is unchanged. This does
not advance the private-route proposal beyond Draft/HOLD. Application-stage what-if and
any apply still require real foundation outputs, trusted image/auth inputs, and separate
explicit authority. No infrastructure, identity, host, credential or runtime change is
claimed.

PR40 merged as `29ebfa2ae4b12c75a7ebeb0302798d94cdc77ab0`; trusted main run
`36760953527` completed successfully, including all four CI jobs, the exact-commit
gate, deployment, and authenticated smoke. A specific infrastructure/host apply
question received no selected authorization, only an unavailable-user delegation response.
That is not treated as the explicit risk-acknowledged apply gate. No apply or Forms VM
service installation was attempted. Entra administrator actions and the Oracle DBA's
read-only credential provisioning remain outstanding and must not be fabricated.

### Gateway Unattended-Host Prerequisite, 2026-09-29

Implemented the source-worker code prerequisite the Draft private-gateway proposal names,
so the existing console host can be installed honestly as a Windows service later. This is
**code capability only**. The gateway is **NotExecuted**: no service was installed, no
certificate was created or read, no credential was provisioned, no Oracle connection was
opened, no source was extracted, and nothing in Azure, Entra, the Forms VM or the Oracle
host was inspected or changed. The proposal in `.azure/deployment-plan.md` stays Draft.

Three capabilities, all fail-closed:

- Windows service lifetime under the constant service name `OFMSourceGateway`, with the
  content root pinned to the installed directory rather than the working directory the
  Service Control Manager supplies. `--serve` is unchanged as a console entry point.
- Server certificate selection from `LocalMachine\My` by pinned thumbprint, or by DNS
  name defaulting to the listener host. A candidate must hold a private key, be inside
  its validity window and carry Server Authentication EKU. No match, nothing usable, or
  two usable candidates each refuse to start. No PFX, no password, no wildcard, no
  developer-certificate or cleartext fallback, and no validation bypass on either side.
- DPAPI `CurrentUser` protected credential files, located by a single configured root and
  named for the variable the registry already declares. Entropy binds each blob to that
  variable name. A protected file that cannot be decrypted or does not decrypt to one
  connect string is a refused request with no environment fallback; the environment is
  read only where no protected file was provisioned. `--protect-credential` reads the
  value from standard input, never an argument, and echoes nothing.

The resolved connect string still reaches only the schema child's
`OFM_WORKER_ORACLE_CONNECTION_STRING`. It is not placed in the gateway's own environment
and never in a Forms child; the protected root and registry path are not inherited by a
schema child. Native extraction scope, the caller allowlist, the tenant/project binding and
the read-only registry surface are unchanged.

Validation, local development diagnostics only, not release evidence: full solution build
clean, and `OracleFormsMigrationFleet.SourceWorker.Tests` 309/309 passing (277 before this
change, 32 added). New regressions cover thumbprint parsing and normalization, https
subject defaulting, loopback listeners selecting no certificate, credential-root validation,
certificate selection by pin and by SAN with wrong-host, expired, missing-EKU, missing-key
and ambiguity refusals, host build refusal on an unmatched pin, a real Windows DPAPI round
trip with the variable-name binding proven by a renamed blob, malformed and undecryptable
protected files refusing without fallback, redaction of values from failure text, and schema
child environment isolation. CLI checks on this Windows host: `--help` renders, a real
DPAPI blob was written for a non-secret placeholder variable in a temporary directory and
contained no plaintext, a non-allowlisted variable name was rejected, and `--serve` with an
unmatched pinned thumbprint refused to listen and exited 64. The temporary directory was
removed. No commit, push, release or deployment was made. Operator-facing requirements are
in `docs/OPERATIONS.md`; installation remains outstanding and unauthorized here.

### Trusted Workbench Release, 2026-09-29

Independent Astra rereview closed the path-alias finding for exact candidate
`189a0b79435f67f441aa4020d3f033fa016a4ee0`, with 80/80 independently rebuilt focused
tests and exact CI `36628053547` passing all four required jobs. Its scoped PASS permits
code release with native integration unconfigured and fail-closed; it is not an exhaustive
new review of all 99 PR files or native qualification.

Azure Validate completed for the bounded existing-workbench code release. The operator
delegated decisions after the explicit release-scope question. Only the existing trusted
workflow update and authenticated smoke checks are in this release; the private-gateway
proposal remains Draft. The unrelated CLI default subscription was not changed or used;
workflow OIDC variables were verified against the intended lab subscription and tenant.

PR36 merged at `2026-09-29T21:03:25Z` as
`5f53b71dc429c33de9adcebf275822a9f4699a40`. Main
[CI/release run 36630686238](https://github.com/rxt64/oracle-forms-migration-fleet/actions/runs/36630686238)
passed all four required CI jobs and its exact-commit gate, but failed authenticated smoke:
the validation approval request returned HTTP 409. The smoke hard-coded JavaSpringBoot
while the server-owned target profile selected AspNetCore. The server correctly rejected
the mismatch. The cleanup/rollback step completed successfully at 21:19 UTC, restoring
the schema-compatible previous template. Independent live validation confirmed revision
`ca-ofmfleet-dev-ykbpnrpd--0000100` healthy, active and serving 100 percent traffic on
the previous digest `482cd91765c04fdcc23ba81ecf93f6f4cd286690dd743bfcdccdbf6daa1c0e58`;
the temporary smoke runner was absent. The incidental Northstar demo workflow `36630685698`
was canceled during compilation before deployment; it is not native migration evidence.

The smoke repair uses the server-owned profile identity and stack, verifies the returned
approval binding, and revokes a created validation approval even when a later assertion
fails. Requested/ValidationOnly approvals remain ineffective and cannot authorize side
effects. Opus implemented the repair; independent Sol QA and Astra scoped code-release
review passed with 14/14 offline tests. These results do not replace a successful new
trusted release and live authenticated smoke. No approval guard was weakened.

Repair commit `7200ff9eafcfb7a7926d6875f283f0b03e337502` passed all four required
jobs in CI `36637308763`. Azure Validate completed for the exact bounded retry,
retaining the separate private-gateway proposal as Draft. PR37 merged as
`a06046f2be81ff8b2c3c8569a94cbe179f203bb0`; trusted main release
`36639136287` passed all CI, exact-commit, deployment and authenticated smoke checks.
Independent infrastructure QA confirmed revision `ca-ofmfleet-dev-ykbpnrpd--0000101`
healthy and active at 100 percent traffic, with immutable digest
`0a0a4cce64a7650a1e33e38af960e3229ae9e8226e01893456d20c41a6ece096`
matching the commit-tagged ACR image. The temporary smoke runner was absent.
SourceGateway configuration remains absent; release PASS is not native qualification.

Trusted worker artifact `11062622419` was downloaded locally and verified without execution:
main-push commit/run/ref bindings, x86 PE header, length 99,210,652 bytes and SHA-256
`7ab7d5874b4207fa244197de9285b92078e20ae47dcc0ce2454d1d172ec1ced6`.
No native installation, source extraction or target migration occurred.

### Live GUI Project Selection Repair, 2026-09-29

The released GUI created project `prj-d616e6e807e14b6bb5a468a33da3d744`
(`Migration 2026-09-29T22:50`), bound to React / AspNetCore / PostgreSql and
`ofm_dotnet_pilot`. The old Java / `postgres` project was preserved. The new project
received a declared FormsBuilderWorker source environment for Forms 6i / Oracle 9i,
alias `meridian-native-6i`, schema allowlist `MERIDIAN`, and the retained genuine FMB
through GUI ZIP upload (one file, 61,440 bytes; no warnings or errors). No approver
was entered, missing evidence was not asserted, and the request stayed PlanOnly.

The walkthrough exposed a GUI defect: remounting the project panel on results reset
selection to the first, older Java project. The server's immutable-profile refusal
remained intact. The scoped client repair preserves the parent-selected project,
selects newly created projects, and retires local project-bound copies, evidence and
results on an actual switch without deleting durable server records. Late plan,
enqueue, preview and preparation responses are fenced from the new project context.
Step-one preparation now receives the current workspace and source-root binding.

Opus implemented the repair; independent Sol QA repaired late-response gaps and
passed 10/10 focused browser tests plus the client build. Astra's final scoped
three-file code-release review passed. Earlier broader suites passed 85 desktop
tests (2 skipped) and 64 mobile tests (23 skipped), before the final reload guard.
The operator delegated the explicitly bounded GUI release decision. Publication,
fresh exact-commit CI, validation and a trusted release are still required for this
GUI repair. These tests do not claim real source extraction or migration.

### Canonical Source Identity Repair, 2026-09-29

Exact `210d3f0` CI passed, but Astra found that filesystem aliases such as `legacy/.`
could bypass lexical claim comparisons after prepared files were removed. The repair
rejects noncanonical selected roots and recorded root/output paths, and detects case-only
overlap without silently treating mismatched spellings as an approved identity. Global
workspace normalization is unchanged. Sol independently verified the scoped fix with
76 passing tests; final ancestor/output-path coverage raised the focused result to 80/80.
The specialist's preceding full solution run passed 2,105 host, 277 worker and 56 demo
tests. New-commit CI and exact-candidate release review remain required.

Infrastructure assessment recommends a parallel VNet-integrated workbench and private
HTTPS source gateway, retaining the existing environment. The local plan is approved only
for artifact generation and validation, not apply. Independent infrastructure QA repaired
installer rerun trust churn, checked every SCM/ACL native exit, stopped the service before
binary replacement, admitted both LocalService and the restricted service SID where needed,
converted the reviewed DER root to PEM in the trusted Linux image workflow, retained a
commit/digest image manifest, and pinned the approved gateway IP/subnet. Local Bicep,
PowerShell/mock, and Checkov gates pass. Real Forms-VM service/certificate behavior, the
public root bytes, Entra app/role, stable PostgreSQL egress, exact what-if, trusted image
build, and native extraction remain unverified. No public gateway was exposed, Azure/VM/
Entra/Oracle resources changed, secret handled, or migration run started.

### Claim-Root Review Closure, 2026-09-29

CI for `cebc501` passed all four required jobs; deployment was skipped. Independent
review resumed after the earlier authentication failure and found the combined case of
deleting an entire prepared child folder before selecting its parent. The follow-up checks
both ledgers' claimed roots and output paths before exact-root filtering, refusing affected
mis-scoped records even when no files remain. Unrelated sibling roots stay usable.

Application-specialist red/green evidence: four consumer cases failed before the fix and
passed after it; the sibling positive control passed in both states. Independent Sol QA
returned scoped PASS: 59 focused and 2,088 full host tests passed in Release, and 277 worker
tests passed. This closes the composed bypass at code/unit scope, not native qualification
or whole-candidate release review. The new commit still needs exact CI and Astra rereview.

Read-only Azure inspection on this continuation confirms live revision
`ca-ofmfleet-dev-ykbpnrpd--0000098`, image digest
`sha256:482cd91765c04fdcc23ba81ecf93f6f4cd286690dd743bfcdccdbf6daa1c0e58`, and no VNet
configuration or workload profiles on `cae-ofmfleet-dev-ykbpnrpd`. No gateway private route
was established and no Azure resource changed. Migration remains NotExecuted.

### Latest Guard Repair Checkpoint

Candidate `e569816613a2156a903590e3a39dbf2241c14a42` passed
[CI run 36615157691](https://github.com/rxt64/oracle-forms-migration-fleet/actions/runs/36615157691).
Astra rereview closed the original combined-preparation and child-credential findings,
but kept release HOLD for two consumer-guard bypasses: selecting a parent of a prepared
source root, and removing every prepared SQL file before consumption.

The follow-up refuses nested reserved layouts and checks outstanding schema claims even
when all statements are missing. All four consumers have direct nested-root and deleted-SQL
regressions. Initial focused reproduction: 9 failures; after repair: 52 passing tests.
A parent follow-up also prevents unreadable or wrong-owner ledgers from being ignored after
statement deletion. Final local validation: 54 focused tests and all 2,083 host tests pass
in Release configuration. The unchanged worker/demo suites last passed 277/56 tests during
the preceding specialist solution run. No new diagnostics were reported.

Independent Sol QA for this last follow-up could not start: the agent service returned
`403: token expired or invalid`. These last changes are locally tested, not independently
approved. Restore Copilot authentication, rerun Sol QA, obtain exact-candidate CI and Astra
rereview, and retain release HOLD until those gates close. An unrelated PowerShell process
held the Debug test assembly open; Release outputs were used without terminating it.
No earlier CI result, reviewer PASS, or lab approval authorizes this newer candidate or
gateway installation. The real migration remains NotExecuted.

### Release Review Repairs, 2026-09-29

Exact candidate `c525315bee0420fdfc24b934aecfb1f5a65471a3` passed all four required
jobs in [CI run 36606073632](https://github.com/rxt64/oracle-forms-migration-fleet/actions/runs/36606073632);
deployment was skipped. Independent Astra release review nevertheless returned HOLD:
normalization rejected legitimate prepared schema files because it read only the Forms
ledger, and native Forms children inherited gateway database credentials. No credential
leak was observed; the latter was a static isolation finding.

Application specialists repaired combined ledger admission and rebuilt the Forms child
environment from a minimal allowlist plus source-pinned settings. Sol QA then identified
that SQL consumers could still read rejected prepared statements independently of
normalization. A shared trust check now runs before database/application conversion,
sandbox data migration, and reconciliation consume reserved prepared SQL. Ordinary
uploaded SQL remains readable; unrelated Forms failures still do not block it.

Independent Sol QA closed both findings at code/unit scope: 2,070 host tests and 277
worker tests passed. A final additional reconciliation case passed alongside the sandbox
case (2/2), proving neither reaches its gateway for tampered prepared SQL. The runtime
allowlist also explicitly rejects Oracle/ODBC connection settings. These repairs require
fresh exact-SHA CI and release rereview; earlier CI is not evidence for later code.

Native DLL loading under the restricted environment remains unverified. Source gateway
private connectivity, TLS, Entra identity, registry grants and secure read-only Oracle
configuration still need a separately scoped approval and validation. The historical
lab approval and generated-artifact relay proposal do not approve this gateway. No
merge, release, source extraction, database write, or GUI migration was performed.

### Candidate CI and Worker Artifact Retention, 2026-09-29

Draft [PR #36](https://github.com/rxt64/oracle-forms-migration-fleet/pull/36)
contains the implementation and published GUI retest report (`f4adaa0`). Candidate
[CI run 36603682941](https://github.com/rxt64/oracle-forms-migration-fleet/actions/runs/36603682941)
passed container builds, guided browser checks, and the Windows native-worker contract.
The main test job failed: eight worker assertions used Windows literal paths on Ubuntu;
all 2,044 host tests passed. This is not a successful candidate CI result.

The follow-up changes replace those test fixture paths with platform-native temporary
paths, without changing production validation or invalid-input rejection cases. CI also
records worker commit/run provenance and executable SHA-256 after native contract checks,
and retains the executable plus manifest for 30 days only on main-branch push events.
Artifact presence does not establish full CI success, release approval, native Forms
qualification, or installation approval.

Local validation: 40 workflow tests and 42 scoped worker tests passed on Windows.
Independent GPT 5.6 Sol QA reviewed the four-file code/test patch and returned PASS,
with the same focused test results and no whitespace errors. Fresh exact-candidate
Ubuntu/Windows CI, required release review, and live native qualification remain pending.
No merge, release, gateway installation, migration approval, or target write occurred.

### Real Forms GUI Retest, 2026-09-29 17:05 UTC

**Assessment exercised; migration execution blocked before queueing.** The operator
requested another GUI test against the genuine Forms 6i application after source commit
`84093b8101ada4ad2e9f897b295f5727fbd3d14c` was pushed. The existing workbench tab's expired
sign-in session was recovered with a normal reload. No authentication control was bypassed.

The retained `MRD_ORDER_ENTRY.fmb` and the sole entry in `meridian-native-forms6i.zip`
were rehashed before upload: 61,440 bytes, SHA-256
`EF00865A74D164BF409538278F634B5FA79115F2DD3AD06C9D66A442C7CC74D2`.
This matches the previously transported real source; it is not a freshly recaptured VM
snapshot or a new source-runtime baseline. The unchanged archive was uploaded through
the GUI into a new session copy, with one file, two recognized artifact kinds, zero
warnings, and zero errors. No synthetic XML or additional evidence was supplied.

| Retest observation | Result |
| --- | --- |
| Project | `prj-bc437aa38358444985955993a5855af1` |
| Plan reference / application | `MERIDIAN-NATIVE-RETEST-20260929` / `MERIDIAN_ORDER_ENTRY` |
| Requested plan | Forms 6i, Oracle 9i, React / AspNetCore / PostgreSql, sandbox migration |
| Proposed session output | `out/meridian-native-retest-20260929` |
| Fresh Check connection | `BlockedPrerequisite` for native extraction, Open API load, x86 worker, and Oracle schema extraction; observed versions remain absent |
| Stored source profile | `meridian-native-6i`, version 2, identity digest prefix `9bac4950b7794d2a` |
| Stored target profile | Version 1, JavaSpringBoot, database `postgres`, schema `public`; not the requested dedicated .NET pilot target |
| Assessment | 29 blockers; 1 of 4 required inputs; missing verified PL/SQL, schema export, and bound test baseline |
| Preparation controls | Prepare sources and Read schema (read-only) are absent from the live GUI |
| Approval / run | Sandbox approval request disabled for target mismatch; no approval requested or granted; no run queued |
| Execution results | 0/6 stages ready, 0/0 phases ran, 0 files written; no target DDL, moved rows, build, deployment, or runtime verification |

A read-only Azure Resource Graph query scoped to the known subscription and app returned
`Succeeded` and latest/ready revision `ca-ofmfleet-dev-ykbpnrpd--0000098`. The query did not
return the image digest, so this retest does not independently re-establish it. GitHub's
workflow-run query for exact commit `84093b8101ada4ad2e9f897b295f5727fbd3d14c` returned no
runs at this observation. The pushed implementation is not a tested released candidate.

The browser remains on the actual blocked plan with its blocker list expanded. No evidence
checkbox was manually selected; only the uploaded FMB and derived module inventory were
auto-selected. No direct API mutation, console migration, or target-profile rewrite was
used. This test verifies real-source intake and the current refusal path, not native
decoding or migration. Next gates remain exact-candidate CI/review and trusted release,
gateway deployment/configuration and real-source qualification, a correctly bound .NET
target project, and the separate named execution approval before a GUI migration run.

Independent GPT 5.6 Sol QA returned **report accuracy PASS / full migration HOLD**.
It independently read the 29 blockers, blocked source, Java target (labeled Production),
disabled approval, empty run history, zero execution/file counts, and absent preparation
controls; it also rehashed both the FMB and ZIP entry. Plan-input choices, upload activity
counts, connection-check freshness, Azure revision, and GitHub workflow-query results
remain parent-observed provenance, not independently re-observed facts.

### Source Preparation Implementation, 2026-09-29

**Implemented and locally tested; pushed as `84093b8`, not deployed or native-qualified.**
The worker now has bounded native Forms extraction, read-only Oracle catalog extraction,
and an authenticated HTTPS gateway. The GUI can request Forms preparation and schema
reads. Workbench admission binds inline artifacts to the caller's tenant/project, immutable
source profile, original source hash, and owner-checked workspace. Server-owned trust
ledgers prevent uploaded files from impersonating prepared artifacts; normalization
consumes admitted artifacts rather than treating binary inventory as decoded content.

The release configuration selects `AspNetCore`, database `ofm_dotnet_pilot`, schema
`public`, and preserves the host's `ASPNETCORE_ENVIRONMENT=Production`. Exact-value
preflight and postdeployment checks were added. This does not rewrite the existing
Java/postgres project profile, its approvals, or the deployed configuration.

Native SDK inspection on the existing Forms VM established that `d2ffmdld_Load` takes
four arguments. The installed `C:\orant\bin\ifd2f60.dll` has SHA-256
`0FE37A1A56F53D46A4D4498335BCF9BD0071C9458136C888877E9903B6E677B3`
and no file-version resource. The worker permits an explicit `unversioned` configuration
only with a valid hash pin; observed version evidence remains absent. This is library
inspection, not evidence that the worker successfully opened the genuine FMB. The Forms
VM was started for this read-only inspection and was not stopped afterward.

Validation retained in the outer workspace's `.copilot-artifacts/source-extraction-tests/`:

- `final-solution`: 2,352 passed before the final two QA repairs (2,023 host, 273 worker,
  56 demo). An earlier load-sensitive authorization-test failure remains retained in
  `full-solution`; it passed in isolation and in the later complete run.
- `qa-repairs`: 200 host preparation/admission/normalization checks and 275 worker checks
  passed after the repairs. Counts overlap the earlier complete run and are not additive.
- Browser source-preparation checks: 5 passed, 3 duplicate API cases skipped across
  desktop/mobile Chromium. These exercised rejection and the unconfigured-gateway GUI
  path, not successful extraction or migration.
- Independent GPT 5.6 Sol review closed the unnamed PGU/LOV omission and foreign-key
  owner/inventory/constraint-tail admission findings. This scoped PASS is not release
  approval or native/live qualification.

Program-unit bodies and LOV definitions are still unsupported native features: both named
and unnamed instances fail extraction instead of yielding partial success. Menu/library
modules and other unqualified Forms features must not be represented as supported.
Oracle catalog tests use fake connections; no real Oracle catalog extraction ran.

Remaining release/integration gates: trusted CI packaging and exact-candidate review;
gateway TLS, Entra audience/caller authorization and private connectivity; mandatory
tenant/project source-registry grants and pinned native configuration; approved read-only
Oracle connection configuration; native FMB and real catalog qualification through the
product; and GUI migration against a new or explicitly rebound dedicated target profile.
The separate legacy `Check connection` path still registers unavailable probe/extractor
providers and needs integration with real gateway evidence, not a fabricated Verified
status. Cross-replica source-publication atomicity remains a residual risk.

At the end of implementation validation, no commit, push, release, gateway deployment,
migration approval, target DDL, row movement,
generated application deployment, or Oracle-free runtime verification occurred in this
implementation. The full fleet migration remains **NotExecuted / HOLD**.

### Live GUI Test, 2026-09-29

**Result: blocked before migration, not a successful full-fleet test.** The operator used
the deployed browser workbench, not a console migration harness. Project
`prj-bc437aa38358444985955993a5855af1`, reference `MERIDIAN-NATIVE-GUI-20260928`, was created
through the GUI. Management-plane inspection after the GUI checks reported ready revision
`ca-ofmfleet-dev-ykbpnrpd--0000098` and image digest
`sha256:482cd91765c04fdcc23ba81ecf93f6f4cd286690dd743bfcdccdbf6daa1c0e58`;
exact CI/commit provenance was not independently verified by this test.

| GUI action | Observed result |
| --- | --- |
| Enter the VM's absolute source path | Correctly refused: a drive path is not a copied repository source |
| Upload the genuine native FMB as ZIP | Completed: 1 file / 61,440 bytes; FormsModuleInventory and FormsModuleSource recognized; private copy locked read-only; 0 warnings / 0 errors |
| Select .NET, PostgreSQL, sandbox goal | Accepted as requested planning inputs, not as execution approval |
| Generate plan against uploaded source | 29 phase blockers; 1 of 4 input requirements met; missing PL/SQL, schema export, and bound test baseline |
| Check immutable target | Stored profile is JavaSpringBoot, database `postgres`, schema `public`, environment label `Production`; it is not the dedicated .NET pilot profile |
| Request approval/run controls | Sandbox approval request disabled for the stack mismatch; no approval requested or granted; no run queued |
| Declare source and click Check connection | Source `meridian-native-6i` advanced to version 2 / `BlockedPrerequisite`; Forms and database observed versions remain `Not observed` |

The source probe named `forms.module.extract`, `forms.openapi.load`,
`forms.worker.architecture`, and `oracle.schema.extract` as blocked. Its `6.0.8.22.1` x86
Windows-worker release is the fleet compatibility profile, not a discovered VM version.
The source-lab runtime was previously observed at `6.0.8.11.3`; compatibility and provenance
must be established rather than relabeling one as the other. The source alias saved in the
GUI is a logical declaration only; this test did not provision a gateway alias or worker.

Current host registration uses unavailable source probe, Forms extractor, and Oracle
schema extractor providers. ZIP acquisition itself is implemented and succeeded; the
phase table's `Source Acquisition: No adapter` is not evidence that ZIP upload failed.
Missing fleet-native decoding and schema/PL-SQL extraction must be implemented and
connected behind the approved source gateway, then released through the trusted pipeline.
Do not run migration SQL or substitute synthetic XML to evade those gaps.

The FMB was transported unchanged from the Forms VM using the bounded, read-only
`infra/source-lab/forms6i/Read-MeridianForms6iSource.ps1`, then uploaded through the GUI.
No native content was extracted or converted by that transport. Its SHA-256 is
`EF00865A74D164BF409538278F634B5FA79115F2DD3AD06C9D66A442C7CC74D2`; both compressed transport
and decoded bytes were hash-checked. The new FMB hash still needs explicit linkage to a
fresh behavioral baseline. Browser workspace: `c54c0badd3e1bcd0fc8d750c65a25fc7`, subject
to the workbench's four-hour expiry. Local observations and preserved upload bytes are in
the outer workspace's `.copilot-artifacts/gui-test-prj-bc437aa38358444985955993a5855af1/`,
including `source-transport.json` and `gui-test-result.json`. These are operator test
observations, **not** migration attestations.

No migration run ID exists. Zero migration phases executed; no target DDL, moved rows,
generated application build/deployment, reconciliation, or target behavior claim exists.
Only the application indexer's two observed artifact kinds were automatically selected;
the operator manually checked no evidence box and granted no approval. The workbench tab
is left on the real blocked result. Next: provide the approved source-gateway extractors,
qualify the actual native release, correct the server-owned dedicated target binding,
establish exact-candidate CI/review, then obtain named product approval and retry in the GUI.

Independent GPT 5.6 Sol QA subsequently read the live GUI, rechecked the retained FMB
hash/size and host provider registration, and returned **report accuracy PASS / full
migration HOLD**. Source-VM transfer provenance, earlier native runtime version, and the
Azure revision/digest observation remained parent-observed facts, not independently
re-observed by QA. No material reporting correction was required. This verdict validates
the failed-test report, not a migrated application.

### Prior Source And Platform Evidence

- The [native Meridian runtime qualification](FORMS6I_AZURE_INSTALLATION_VALIDATION.md#native-meridian-runtime-verified-2026-09-28)
  supersedes the stub-only and temporary-network status. A separate native FMB/FMX
  now runs in Forms 6i against the existing Oracle9i schema; the original stub is
  preserved. Real UI Create/Save, Cancel, zero-quantity rejection, and quantity-two
  totals passed independent Net8 checks. Saved test orders are 1001 and 1003;
  cancelled draft 1002 is absent. The final 23,476-byte FMX compiled with exit 0
  and reopened with database-populated choices and a fitting desktop layout.
  Durable redirect rules are restricted to the two private VM addresses. The
  patched DLL persists on disk, but database/listener operation requires the
  administrator desktop session; unattended reboot recovery is not qualified.
  No Defender or governance-policy changes were made in this runtime phase.
  This is working source-lab Forms, not customer migration or product GUI acceptance.
- At 20:12 UTC on 2026-09-28, the [listener memory workaround](FORMS6I_AZURE_INSTALLATION_VALIDATION.md#listener-memory-workaround-and-defender-restoration-2026-09-28)
  was serving TCP with the debugger detached. A fresh TCP SQL*Plus session verified
  `orcl` OPEN, 11 MERIDIAN objects, and zero invalids. Defender was restored after
  the authorized temporary diagnostic disable, and original installed-file hashes
  were unchanged. The patch is memory-only and will not survive listener exit;
  the separate patched DLL copy is staged but not activated. Forms acceptance and
  persistent startup remain outstanding. This supersedes earlier listener-down
  observations below, not migration or Forms qualification gates.
- On 2026-09-28, the separate Oracle9i source VM `vm-ofm-oracle9i-j6mrrerz` was
  recovered through Bastion: `orcl` opened normally and all 11 MERIDIAN objects were
  valid. Its Windows 98 Personal Edition listener still fails implicit IPC startup
  with Windows error 161 on the Server 2022 host; Forms connectivity and automatic
  database startup remain unverified. Diagnostic configuration changes were restored.
  See [FORMS6I_AZURE_INSTALLATION_VALIDATION.md](FORMS6I_AZURE_INSTALLATION_VALIDATION.md#oracle-9i-source-database-recovery-2026-09-28).
  This is source-lab recovery, not a GUI migration or native Forms acceptance.
- The 18:48 UTC [short-PATH retest](FORMS6I_AZURE_INSTALLATION_VALIDATION.md#oracle-9i-short-path-retest-2026-09-28)
  reproduced listener error 161 with unchanged configuration. A fresh RDP session
  initially saw an idle database; normal startup restored `orcl`, and a separate
  18:52 UTC connection verified OPEN with 11 MERIDIAN objects and zero invalids.
  Persistence across logoff or reboot remains unverified; the listener is still down.
- [API tracing](FORMS6I_AZURE_INSTALLATION_VALIDATION.md#oracle-9i-ipc-api-diagnosis-2026-09-28)
  identified the listener failure: `oranipc9.dll` passes a pipe-style name to
  `CreateFileMappingA`, which returns NULL with error 161. Independent 32-bit
  ANSI/Unicode probes fail with that name and succeed with a plain mapping name.
  No Oracle binaries were patched. At 19:34 UTC the debugger had exited and `orcl`
  remained OPEN with 11 MERIDIAN objects and zero invalids. An NT-build replacement
  is still untested; listener and Forms acceptance remain outstanding.
- Windows Oracle9i replacement media remains unavailable locally. Oracle FAQ1727
  confirms the non-technical media-request route. The delayed assistant-to-SR
  handoff eventually displayed an unsubmitted creation form, but session renewal
  and an SR number remain unconfirmed. Entitlement and availability remain
  unverified; no media was downloaded or installed. The
  [media follow-up](FORMS6I_AZURE_INSTALLATION_VALIDATION.md#oracle-9i-media-acquisition-follow-up-2026-09-28)
  retains the inquiry and observed portal failure for a non-duplicating retry.
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
| Fresh GUI migration | Workbench durable runs | Blocked | 2026-09-29 real FMB ZIP intake and source probe; project prj-bc437aa38358444985955993a5855af1; no migration run queued | Wire qualified source extractors and correct dedicated target profile, release/review, then approve and execute through GUI |
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

The workflow cannot ask the product whether an approval was revoked mid-build. The repaired workflow
checks the immutable expiry before registry publication and again before updating the app; remote
revocation after dispatch remains a limitation. Generated commands now run inside Docker build stages,
not as the host runner user. Their success is not independent behavioral equivalence evidence.

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