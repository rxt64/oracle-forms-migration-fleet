# Verification

## Executable Checks

- `npm run build` in `src/oracle-forms-migration-fleet/ClientApp`: passed after the final UI changes; 1,586 modules transformed.
- `dotnet test .\oracle-forms-migration-fleet.slnx --nologo`: passed 1,157 of 1,157 tests on .NET 10.0.11.
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

The backend still accepts approval-shaped request records and does not authenticate their actor. Trusted durable approvals remain a separate required increment.

## Working Tree Boundary

`portal.css` and `Guidance.tsx` are intentional new source files in this uncommitted increment. `wizard.css` is intentionally removed and replaced by `portal.css`. Generated `wwwroot` output is ignored and is not part of the source change. The pre-existing untracked `.agent_configs/` and `eval.yaml` remain unrelated and untouched.