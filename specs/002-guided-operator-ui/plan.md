# Implementation Plan

## Design

Progressively restyle the existing React 19 workbench instead of replacing its API or state model. Keep `WizardApp` as the current orchestration surface, extract durable presentation data into a guidance registry, and make `InfoTip` a reusable accessible popover. Replace decorative console treatment with an activity pane grounded only in existing server-emitted lines.

## Components

- `Guidance.tsx`: typed registry for step, field, choice, action, status, metric, navigation, and glossary help.
- `InfoTip.tsx`: accessible help trigger and viewport-aware popover behavior.
- `WizardApp.tsx`: shell, navigation, breadcrumb, command bar, guided copy, accurate capability and approval language.
- `MatrixConsole.tsx`: calm activity dialog, real event transcript, honest connection/request boundary.
- `portal.css`: cohesive dark tokens and responsive layout replacing decorative overrides.

## Contracts And Boundaries

- Keep request and response types in `types.ts` unchanged.
- Keep source acquisition and execution calls in `sourceClient.ts` unchanged.
- Do not add tenant selectors, source connectors, persistence, cancellation, or approval controls without matching backend contracts.
- The current local-storage draft remains session/browser convenience and is never described as server autosave.
- Typed approver names remain plan annotations only.
- Raw server lines are technical details, not migration progress percentages.

## Verification

- TypeScript and Vite production build.
- Focused component/browser tests for help Escape/outside dismissal, activity close/return behavior, validation focus, and truthful labels.
- Playwright screenshots and horizontal-overflow assertions at desktop and 390 px.
- Automated accessibility scan plus manual keyboard, 200% zoom, reduced-motion, and forced-colors checks.
- Full .NET solution tests because the UI is bundled into the host application.

## Rollout

Ship as an independently reviewable frontend increment after spec 001 hardening. Deployment remains a separate authorized operation. Screenshots and fixtures are UI evidence only.