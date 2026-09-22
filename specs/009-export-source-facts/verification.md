# 009 - Verification

## Baseline

On 2026-09-21 GitHub API confirmed PR #32 open at
714113c3a0482ac276280c979dc5e96ffee29a74 against main 9433e59031267ff378265e85f0b4895a02760e33.
Run 35656433491 completed successfully: Guided UI browser checks, Native source worker contract,
Container image builds, and build-and-test. Deploy verified workbench was skipped. No merge performed.

## Local product evidence

Final Release solution validation on 2026-09-21 passed: 1,508 total, 1,508 succeeded, zero failed or
skipped; build passed. Command: `dotnet test oracle-forms-migration-fleet.slnx -c Release
-p:SkipClientBuild=true --nologo` using the user-local .NET 10 executable.

Tests exercise parser-to-normalization-to-strict-reader round trips with independently authored exports,
property omission comparisons, bounded depth, preserved whitespace, invalid identity and tree mutations,
and unchanged native/Java boundaries. The final focused matrix passed 318/318; independent GPT-5.6 Sol
QA reran `FullyQualifiedName~FormsIntermediateReaderTests` in Release: 175/175 passed, no high or medium
findings. QA fixes include projection/fact reconciliation, exact sibling ordinals, matching depth limits,
and rejecting structural projection-limit findings rather than accepting an empty projection.

These are local product tests, not evidence of executing a generated .NET target, a fresh browser flow,
or an opt-in external integration. Published commit 8389baf1fba178dc181e495d191e7c87fe3c3512 is on
feat/export-source-facts, PR https://github.com/rxt64/oracle-forms-migration-fleet/pull/33, based on PR32.
The original main-only PR trigger did not run CI for this stacked base; feature-base PR CI is now enabled.
Exact-SHA CI for the corrected commit remains pending; no deployment is claimed.

GPT-6 Astra (copilot) independently reviewed the published SHA and ran 312 focused tests successfully.
Review result: HOLD, with medium findings for wrapper-release inconsistency and mixed-content order loss,
plus absent exact-SHA CI. Corrections reconcile release declarations using existing catalog rules and
refuse unsupported mixed content instead of silently rearranging text. Post-repair focused tests:
327 source parser/reader/normalization tests and 53 executor/workspace/application conversion tests passed.
The earlier 1,508 result predates these repairs; focused Astra re-review is required.
See astra-review-8389baf.md for the requirement-by-requirement record.

## Source qualification

Summit commit 258c82b4a22fd7ccc517e3be6a05f25e52e31a0d was inspected outside the repository.
Original bytes were hashed without rewriting. Export metadata literally says 122010400; original FMB
authoring version and runtime version are unknown. No native 6i qualification is established.
No applicable license grant was identified. Summit is not imported into a product run in this increment.
See summit-source-manifest.json for classifications, hashes, dependencies, and unresolved SQL closure.

The experiment demonstrates guided Vaadin/Java conversion retaining Oracle; its third, stricter ledger run
was specified but not completed according to the article. Neither screenshots nor upstream generated
ledgers are our behavior verification. ORA_ROWSCN is not a PostgreSQL design.

## Evidence levels

1. Static export retention: tested on independently authored fixtures; diagnostic counts only.
2. Approved migration decisions: not implemented by this increment; ledger totals not available.
3. Generated .NET compilation: NotExecuted.
4. Generated .NET/frontend application tests: NotExecuted.
5. PostgreSQL target reconciliation/behavior: NotExecuted for this slice.
6. Genuine Forms runtime differential checks: BlockedPrerequisite.

No new workbench deployment, generated target deployment, or GUI migration run is claimed. Existing
normalization adapter integration is tested; newly retained source facts stay in protected run exports.