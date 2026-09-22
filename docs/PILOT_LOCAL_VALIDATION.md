# Pilot Local Validation

This records local implementation evidence, not a released migration. Current work is unpublished on
`feat/dotnet-migration-pilot`, based on `d66095320270810e52151e36ece8cd8390343020`.
No new Azure deployment, customer migration, or native Forms execution occurred.

## Independent Milestone Review

Tool-selected reviewer: GPT-6 Astra (copilot), read-only and nondelegated. The tool invocation succeeded;
the routing label is not independent attestation of backend identity. Initial milestone verdict: HOLD.

The reviewed dirty snapshot was bound by SHA-256, not misrepresented as the committed HEAD:

- Tracked `git diff --binary --no-ext-diff HEAD`: `cc62342708ddc0a92772b194147d23606b97959c44fba250e63d9a223b1f7ea8`.
- Sorted untracked-file manifest, excluding protected paths: `a84f2892d40f9c367f578de419e2a1d7b63c99611af5e1ed4a01d541b2983ffc`.

Reviewer executed 328 focused tests. Findings were invalid DDL separators following SQL comments,
alias-qualified NULL expressions for absent optional fields, dropped required column defaults, and late
verification replay without decision/content binding. Subsequent edits invalidate this snapshot review.
Focused re-review is required; this document does not grant merge or deployment approval.

## Repairs And Local Checks

- Server-owned `TARGET_BACKEND_STACK` defaults to Java and supports ASP.NET Core. Immutable profile
  hashes and both approval/execution checks bind database, front end and back end. Numeric enum inputs
  are rejected. Independent target-binding QA: 122 tests passed.
- Generation asks a server-bound ledger provider at phase time. All retained Forms ledger entries must
  be decided; Preserve/Transform require a property the resolved generator actually carries. Unknown
  behavior requires explicit operator retirement or remains blocking. No automatic retirement occurs.
- Durable runs carry the ledger locator in the plan-input hash. Wizard approval/execution use the same
  request. A real file-store worker test executed normalization, ledger ingestion/decisions, a second
  generation run and evidence recording. Desktop/mobile browser QA: 57 passed, 9 conditional skips.
- Ledger generation updates are atomic within each store. Run and ledger stores are not a shared
  transaction: producer-fence binding and server projection make displaced-owner evidence inadmissible.
- Generation and verification bind decision revision, source, content, run and fence. Monotonic
  `DecisionSequence` prevents identical redecisions at the same clock timestamp reviving old evidence.
  Evidence-only changes do not change decision revision. Legacy unbound evidence fails closed.
- Generator fixes preserve SQL separators, qualify only actual columns, and translate only supported
  literal defaults. Unsupported required defaults block generation. Tests cover omitted optional header
  roles. API parsing separates malformed JSON (400), unsupported media (415), and invalid values (422).
- Emitted frontend tests explicitly clean up rendered components between cases.
- Latest full offline solution: 1705 passed, zero failures. Environment-gated passes in this total are
  not counted as database acceptance; the separately executed database results below provide that proof.

## Real PostgreSQL Execution

Independent QA used PostgreSQL 16.15 portable binaries from the official EDB distribution on a dedicated
loopback-only ephemeral port, with .NET SDK 10.0.400, Release and `SkipClientBuild=true`.
No Azure access, platform production database, or customer data was used.

Initial execution failed and was retained at `C:\ofm-generated-app-evidence-20260922`:
incorrect inactive-state seed, fractional-quantity API response, frontend cleanup and a timing-sensitive
250 ms lease test. Fixes were made in source generators/tests, never manually in generated output.
The lease test now uses a live 30-second lease and explicit expiry through the store rather than sleeps.

Successful fresh rerun: `C:\ofm-generated-app-evidence-20260922-rerun`.

| Executed check | Result |
|---|---|
| PostgreSQL integration category | 14/14 passed, including real atomic ledger batch rollback/commit |
| Focused decision sequence and durable replay | 3/3 passed |
| Generated application class, serialized aggregate | 24/24 passed |
| Meridian backend / no-target / frontend | 21 passed / 21 failed as required / 4 passed |
| Kestrel backend / no-target / frontend | 21 passed / 21 failed as required / 4 passed |
| Meridian unbound-header backend / no-target / frontend | 21 passed / 21 failed as required / 4 passed |

Each fixture retained seven sentinels and three reports. The six CI-required legs are SQL, SQL
concurrency, compile, no-target refusal, PostgreSQL-backed HTTP acceptance, and frontend. Request-contract
checks supply the additional local sentinel. Counts across these checks overlap and must not be summed.

Business checks cover atomic valid orders, invalid quantities, missing/inactive parties and items,
insufficient stock, concurrent oversell prevention, decimal totals, rollback, lookup filtering and
optional-field read-back. They are synthetic target acceptance, not native source equivalence.

The CI evidence script requires all three fixtures. YAML parse and `bash -n` passed; complete synthetic
evidence returned zero, a missing third-fixture sentinel and an empty report each returned nonzero.
No remote CI run has yet tested these unpublished changes.

Portable archive provenance: PostgreSQL `16.15-4` Windows x64, 371449528 bytes, SHA-256
`f5f55b03bd54ce0dd1c51d524b54c7e015abd4d620af27d6971288a2dbe4a8f8`.
The hash matched the earlier download; publisher checksum endpoints returned 403, so no independent
publisher checksum attestation is claimed. All test servers stopped; ephemeral clusters and credential
files were removed. Portable binaries remain cached at `C:\ofm-pg16-portable-cache`.

## Remaining Gates

- Exact-commit CI/review after publication; focused working-snapshot re-review is recorded below.
- Database dependency inventory and implementation coverage beyond the narrow role-driven generator.
- Production wiring of executed per-entry verification and an isolated database-backed runtime verifier.
- Product-controlled trusted artifact build, deployment, digest binding and side-effect recovery.
- Approved Azure foundation/platform release, owned Oracle order fixture, and a fresh GUI migration with
  row reconciliation, deployed target acceptance and a manual-intervention log.
- Genuine Forms media, compatible native toolchain and executable source baseline remain Blocked.

No overall delivery completion or release authorization is implied by these local results.

## Focused Re-review And Retained Aggregate Reports

Parent-observed tool invocation: `functions.runSubagent`, model `GPT-6 Astra (copilot)`.
The focused re-review closed the four original code defects and found no new blocking code defect in
that inspected scope. Overall verdict remains HOLD. The reviewer initially could not locate the outer
aggregate reports, so QA reran the checks with explicit retained TRX output. A separate read-only
reviewer addendum inspected those actual reports and closed only that retention gap.

Final evidence root: `C:\ofm-generated-app-evidence-20260922-final`.

| Retained host report | Executed / passed / failed |
|---|---|
| host/generated-application-aggregate.trx | 24 / 24 / 0 |
| host/postgres-category.trx | 14 / 14 / 0 |
| host/replay-sameclock-batch3.trx | 3 / 3 / 0 |
| host/full-oracle-forms-migration-fleet-tests.trx | 1649 / 1649 / 0 |
| host/full-oracle-forms-demo-tests.trx | 56 / 56 / 0 |

The parent independently parsed these XML counters. The reviewer also resolved the test identities,
confirmed each fixture's 21 positive backend cases, 21 expected missing-target failures and four frontend
passes, and checked all 42 retained evidence files against the provenance manifest. No tests were rerun
by the reviewer. Counts overlap and must not be summed beyond the two distinct full-solution projects.

Reviewed application snapshot: HEAD `d66095320270810e52151e36ece8cd8390343020`, tracked binary-diff
SHA-256 `583927631db10ab09e705f1f77375f5792daee339630c83c70f8faf87ba821ab`, and untracked manifest
SHA-256 `a7fcca72666e899656ab73991b2992bb8d7c7bbc0a54b73b2e909e6b075b5ba2`.
The latter hashes ordinal-sorted `path<TAB>lowercase-file-sha256<LF>` entries, UTF-8 without BOM,
excluding protected agent configuration and evaluation paths. All 29 entries matched during review.
This subsequent documentation addendum changes the documentation snapshot, not the tested code.

Remaining review dispositions: runtime per-entry verification and product-controlled target deployment
are incomplete; fresh deployed GUI acceptance, reconciliation and autonomous retry are NotExecuted;
native qualification and equivalence remain Blocked. Physical filesystem containment needs explicit
adversarial validation beyond lexical path checks. No merge, release or Azure mutation is authorized by
these reviews. Review routing is observed tool metadata, not independent backend identity attestation.

## Source Lab And Containment Follow-up

Original source package added at `infra/source-lab/meridian-order-entry`: Oracle schema, seed, sequences,
transaction package, ordered initialization/verifier scripts, and a clearly labeled synthetic Forms export.
No native artifacts are manufactured. The source package and Oracle verifier have not run against Oracle.
Known converter gaps remain explicit; the primary package's design-time references are self-contained.
Customer eligibility is locked before articles in key order; Oracle compilation and concurrent behavior
remain NotExecuted. The lab account has a bounded quota and no authentication credential.

Workspace artifact paths now reject observed symlink/junction segments. Tests demonstrate a directory
junction escape is refused with 410 and no generated evidence. Handle-bound physical TOCTOU protection
is not claimed.

Post-hardening reports in the final evidence root's `post-hardening` directory contain 1665 fleet tests
and 56 demo tests: 1721 passed, zero failures, including 13 source-lab tests and both junction regressions.
These are offline tests, not a new PostgreSQL acceptance run or executed Oracle verification.

The tool-selected GPT-6 Astra (copilot) follow-up reviewed actual reports and source changes, with scoped
PASS and overall HOLD. Snapshot: tracked diff `9b0aba463a19381345adc48af3d07e1974897a99fff1440470bc6c51db4bb36e`,
untracked manifest `5efe76db32cc66d5eda50764ae721c72f1e90b0418463568ae78ba97388a6a0c`, using the same manifest
formula above. Worktree remained dirty: 44 tracked modifications and 42 nonprotected untracked files.
Reviewer requested resetting the disposable source fixture before the rollback concurrency scenario;
the protocol was corrected after review. This addendum and protocol correction change documentation only.
No additional test, publication, remote CI, Azure mutation, or native execution is implied.