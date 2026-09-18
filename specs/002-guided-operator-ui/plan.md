# Implementation Plan

## Design

Progressively restyle the existing React 19 workbench instead of replacing its API or state model. Keep `WizardApp` as the current orchestration surface, extract durable presentation data into a guidance registry, and make `InfoTip` a reusable accessible popover. Replace decorative console treatment with an activity pane grounded only in existing server-emitted lines.

## Components

- `Guidance.tsx`: typed registry for step, field, choice, action, status, metric, navigation, and glossary help.
- `InfoTip.tsx`: accessible help trigger and viewport-aware popover behavior.
- `WizardApp.tsx`: shell, navigation, breadcrumb, command bar, guided copy, accurate capability and approval language.
- `MatrixConsole.tsx`: explanatory activity dialog driven by typed progress state, with raw output collapsed.
- `portal.css`: cohesive dark tokens and responsive layout replacing decorative overrides.
- `ProgressSignal.cs`, `SourceWorkspaceService.cs`, `MigrationExecutor.cs`, and `WorkbenchEndpoints.cs`: additive typed activity fields and ordered wire frames.
- `tests/e2e` and `playwright.config.ts`: real-host Chromium checks with browser-only stream fixtures.

## Contracts And Boundaries

- Keep migration request and result types unchanged; extend only progress frames with additive typed fields.
- Keep source acquisition and execution endpoint behavior unchanged while preserving legacy `level` and `text` fields.
- Do not add tenant selectors, source connectors, persistence, cancellation, or approval controls without matching backend contracts.
- The current local-storage draft remains session/browser convenience and is never described as server autosave.
- Typed approver names remain plan annotations only.
- Raw server lines are technical details, not migration progress percentages.
- Typed counts are producer-measured facts. Message counts describe the transcript only.

## Verification

- TypeScript and Vite production build.
- Checked-in Playwright tests for pointer-pinned help, streaming focus, operation outcomes, source-switch races, result disclosures, validation focus, and truthful labels.
- Playwright screenshots and horizontal-overflow assertions at desktop and 390 px.
- Axe scans plus separate manual keyboard, browser-zoom, screen-reader, reduced-motion, and forced-colors checks.
- Full .NET solution tests because the UI is bundled into the host application.

## Rollout

Ship as an independently reviewable frontend increment after spec 001 hardening. Deployment remains a separate authorized operation. Screenshots and fixtures are UI evidence only.