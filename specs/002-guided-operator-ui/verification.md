# Verification

## Repeatable Browser Suite

The follow-up suite is checked in under `ClientApp/tests/e2e`, configured by `ClientApp/playwright.config.ts`, and starts the real .NET workbench host itself. From `src/oracle-forms-migration-fleet/ClientApp`:

```powershell
npm ci
npm run build
npm run test:e2e:install
npm run test:e2e
```

Prerequisites are Node.js 22 and .NET 10. Set `DOTNET_EXE` only when the .NET executable is neither on `PATH` nor under the user's `.dotnet` directory. CI runs the same build and browser command on pull requests to `main`, installs Chromium with its Linux dependencies, and uploads `playwright-report` plus `test-results` even when a test fails.

Newly reproduced on `feat/guided-operator-ui-followup`, starting from `0aa5bb2f5bc548b0e17f2b1d9b45e597ac35b64b`:

- `npm ci --no-audit --no-fund`: passed from the checked-in lockfile, installing 77 packages.
- `npm run build`: passed, 1,586 modules transformed.
- `npm run test:e2e`: 98 passed, 8 skipped, 0 failed in 2.9 minutes across desktop Chromium at 1,440 x 900 and mobile Chromium at 390 x 844 after the activity-state correction.
- The eight skips are intentional: mouse-hover cases do not run on the touch project, touch-tap does not run on the desktop project, and viewport-independent direct HTTP trust tests run once on desktop.

The suite covers every setup step, validation focus, disabled capabilities, completed and in-flight source switches, reset behavior, result order/disclosures, pinned and transient help, viewport edges and long help, Activity focus while events arrive, keyboard containment while streaming, completed/failed/waiting/interrupted outcomes, concise status announcements, typed counts, failed zero-work execution results, retained diagnostic artifacts, action/help sibling markup, screenshots, and axe scans. Test-only `fetch` interception supplies only progress frames and selected execution-result states; bootstrap, validation, and planning use the real host. One test uploads an in-memory ZIP through the real source endpoint and plans against the real owned workspace. No production fixture route or fake-run mode exists.

Measured-count headings are state-specific: `Reported so far` for Running/Waiting, `Partial findings` for Failed/Interrupted, and `Completed counts` only for successful completion. Disconnect guidance requires inspection of retained results and the actual destination before retry because cooperative cancellation cannot guarantee that external effects stopped or rolled back.

## Executable Checks

- `npm run build` in `src/oracle-forms-migration-fleet/ClientApp`: passed after the final UI changes; 1,586 modules transformed.
- `dotnet test .\oracle-forms-migration-fleet.slnx --nologo`: passed 1,190 of 1,190 tests on .NET 10.0.11 after the follow-up trust and progress regressions were added.
- Focused strict-reader tests: passed 18 of 18 cases, including numeric encoding rejection and actionable IR v1 refusal.
- `git diff --check`: passed; Git reported only line-ending conversion notices.

## Browser Checks

Playwright drove the real local host at `http://127.0.0.1:8088`.

- Desktop overview at 1,440 x 900: rendered without overlap or horizontal overflow.
- Mobile overview at 390 x 844: rendered without horizontal overflow.
- Every setup step and the result view rendered without horizontal overflow.
- A 720 px viewport used as a 200% desktop-zoom layout proxy had no page-level horizontal overflow.
- Help opened by click, stayed inside the 390 px viewport, closed with Escape, and restored focus.
- Help inside the Azure status dialog closed first on Escape while the containing dialog remained open; focus returned to the help button.
- Glossary trapped Tab focus, closed with Escape, and restored focus to Help.
- Activity trapped Tab focus, including its scrollable log, and restored focus to its opener or the persistent Activity command when acquisition replaced that opener.
- Empty setup validation focused `engagementId` and exposed four associated alert messages.
- Reduced-motion and forced-colors media rules activated; focused controls retained a visible outline.
- Closing Activity restored focus to its opener, or to the persistent Activity command when acquisition replaced the opener.

## Accessibility Scan

Axe 4.10.2 ran with WCAG 2 A/AA, WCAG 2.1 AA, and WCAG 2.2 AA tags.

- Mobile overview: zero violations; no serious or critical incomplete checks.
- Validation state: zero violations; no serious or critical incomplete checks.
- Glossary dialog: zero violations; no serious or critical incomplete checks.
- Results state: zero violations; no serious or critical incomplete checks.
- Activity state: zero violations. Axe could not automatically determine three contrast pairs because the fixed dialog overlaps page content. Direct rendered-color measurements were 8.49:1 for the green completion label and 9.17:1 for both footer text blocks.
- Corrected results state: zero violations; planning lifecycle states use neutral styling, and a plan without an executed normalized Forms artifact makes no trigger-retention claim.

Automated checks complement rather than replace screen-reader and native high-contrast review.

## Controlled Fixture Boundary

The activity screenshots and event-state scan intercepted the source-copy endpoint in Playwright and returned five clearly controlled server-event frames. They prove only the activity UI's state, count, focus, and responsive behavior. They are not evidence of Git acquisition, Oracle extraction, migration execution, or behavioral equivalence. No fixture is exposed by the production application.

## Trust Boundary Check

The captured plan request proved:

- A manually selected checklist item is sent as `source: operator-declared` and `isVerified: false`.
- A source-indexer selection is the only GUI path that can set `isVerified: true`.
- Typed sandbox and production contacts are sent with `decision: Pending` and `approverId: null`.
- The contact text is retained only as a pending planning note.
- After a copied source auto-selected verified evidence, switching to `Describe a folder path` released the workspace and produced a captured plan request with `evidence: []` and the manual source root. Workspace-bound verification did not cross source contexts.
- The same transition was repeated while the clone response was deliberately delayed. Source-mode reset aborted and invalidated the active acquisition; releasing the stale response afterward left `evidence: []`, zero checked items, and Activity unavailable.
- Starting `New migration` cleared all manual and detected evidence, source values, application fields, plan, and execution state.

The API still deserializes approval-shaped request records for contract compatibility, but the server trust boundary forces them to Pending, drops client attestations, and derives verified source facts from the owned workspace. Durable authorization issuance and authenticated approval persistence remain separate required increments.

## Manual Checks Still Required

- Native screen-reader review.
- Native Windows high-contrast review.
- Actual browser zoom at 200%; the automated narrow viewport is only a reflow proxy.

The earlier manual browser observations remain historical evidence from the parent commit. The checked-in suite above is the reproducible evidence for this follow-up. Axe results are not a WCAG conformance claim.