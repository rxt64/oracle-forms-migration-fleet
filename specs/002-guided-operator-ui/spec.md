# Guided Operator UI

## Status

Implemented on `feat/guided-operator-ui`; focused follow-up verification is in progress on `feat/guided-operator-ui-followup`, starting from commit `0aa5bb2f5bc548b0e17f2b1d9b45e597ac35b64b`.

Spec Kit is not installed in this environment. This directory records equivalent specification artifacts without claiming that `/speckit.*` commands ran.

## Problem

The workbench exposes useful source intake, planning, execution, and evidence details, but its visual hierarchy is decorative and dense. A first-time operator can mistake planning inputs for verified source access, typed names for trusted approval, or a completed request for a completed migration. Contextual help exists but is scattered through components, so coverage cannot be audited consistently.

## Requirements

- **GUI-001**: Default to a dark, Azure-inspired corporate shell with a persistent product header, service navigation, breadcrumb, command bar, and focused working pane. Do not claim to be an Azure portal extension.
- **GUI-002**: Retain the current five backend-compatible setup steps while presenting them as `Your application`, `Azure destination`, `Source checklist`, `Execution permissions`, and `Review & plan`.
- **GUI-003**: Every step states its goal, an example or required input, what the platform will do, and the output produced. Advanced details remain collapsed unless required validation fails.
- **GUI-004**: Centralize help content in an auditable registry. Meaningful fields, groups, choices, statuses, metrics, navigation entries, and actions have keyboard- and touch-accessible help without nested interactive controls.
- **GUI-005**: Help closes with Escape or outside interaction, stays open while the operator moves from its button to its content, restores no unexpected focus, and remains within the viewport at 390 px. Hover is transient; clicking, keyboard activation, or touch pins help until explicit dismissal.
- **GUI-006**: Approval copy states that typed names are planning metadata only and cannot authorize sandbox writes or production release.
- **GUI-007**: Source acquisition distinguishes Git/ZIP intake from a described path. Example values are clearly labeled and never imply connection, extraction, migration, or deployment.
- **GUI-008**: The activity surface uses typed server events with operation/action IDs, state, sequence, timestamp, deterministic purpose/observation/next-action text, and optional measured counts. It distinguishes Running, Waiting, Completed, Failed, and Interrupted/Unknown without parsing log prose or treating a stopped stream as success.
- **GUI-009**: Results distinguish planning readiness, generated artifacts, execution, attestations, and blockers. Retained trigger source is described as `Source logic retained; not yet converted`.
- **GUI-010**: Use explicit text and icons in addition to color for states. Reserve red for actionable failures and green for verified success.
- **GUI-011**: Meet automated WCAG 2.2 AA checks for the tested states, support keyboard operation, visible focus, reduced motion, forced colors, and no page-level horizontal overflow at 390 px.
- **GUI-012**: Preserve request, validation, source-workspace, planning, and execution semantics. Progress wire fields are additive so older level/text consumers remain valid. This increment adds no durable project, trusted approval, connector, or deployment capability.
- **GUI-013**: Raw technical output is collapsed initially and does not announce every line. The primary activity view states the current operation, its purpose, observed result, and next action; numeric estate/artifact counts appear only when supplied as typed values.
- **GUI-014**: Results default to outcome, top blockers, one next safe action, and concise run results. Architecture, lifecycle, full phase detail, model attribution, and Azure footprint begin collapsed.
- **GUI-015**: Streaming rerenders never recapture focus or move it from the control the operator chose. Closing Activity restores the original opener or the persistent Activity command when the opener was replaced.

## Acceptance Criteria

1. A new operator can identify the source, destination, next action, active operation, and blocker without knowing `IR`, `attestation`, or `adapter`.
2. Desktop and 390 px screenshots show no overlapping controls, clipped help, or page-level horizontal scrolling. Narrow viewports are reflow evidence, not browser-zoom proof.
3. Keyboard-only use can open and close help, traverse setup, reach validation errors, open activity, and return to the triggering control.
4. Automated accessibility checks report no serious or critical findings for intro, every setup step, validation failure, disabled capability, activity, results, and dialog states represented by fixtures or live APIs.
5. `npm run build` and `npm run test:e2e` succeed against the real local host with browser-only stream fixtures, and CI uploads Playwright reports/screenshots.
6. Existing server-side tests remain green; typed progress fields are tested as an additive wire contract.

## Out of Scope

- Durable projects, drafts, jobs, events, artifacts, or replay.
- Authenticated approval records and trusted attestations.
- Oracle database or Forms runtime connectors.
- New conversion, validation, deployment, or cancellation adapters.
- Claiming visual checks establish native Oracle or migration equivalence.