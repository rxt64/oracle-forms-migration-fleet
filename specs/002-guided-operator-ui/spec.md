# Guided Operator UI

## Status

In progress on `feat/behavioral-ir-trigger-bodies` as a presentation-only increment over the current workbench APIs.

Spec Kit is not installed in this environment. This directory records equivalent specification artifacts without claiming that `/speckit.*` commands ran.

## Problem

The workbench exposes useful source intake, planning, execution, and evidence details, but its visual hierarchy is decorative and dense. A first-time operator can mistake planning inputs for verified source access, typed names for trusted approval, or a completed request for a completed migration. Contextual help exists but is scattered through components, so coverage cannot be audited consistently.

## Requirements

- **GUI-001**: Default to a dark, Azure-inspired corporate shell with a persistent product header, service navigation, breadcrumb, command bar, and focused working pane. Do not claim to be an Azure portal extension.
- **GUI-002**: Retain the current five backend-compatible setup steps while presenting them as `Your application`, `Azure destination`, `Source checklist`, `Execution permissions`, and `Review & plan`.
- **GUI-003**: Every step states its goal, an example or required input, what the platform will do, and the output produced. Advanced details remain collapsed unless required validation fails.
- **GUI-004**: Centralize help content in an auditable registry. Meaningful fields, groups, choices, statuses, metrics, navigation entries, and actions have keyboard- and touch-accessible help without nested interactive controls.
- **GUI-005**: Help closes with Escape or outside interaction, stays open while the operator moves from its button to its content, restores no unexpected focus, and remains within the viewport at 390 px and 200% zoom.
- **GUI-006**: Approval copy states that typed names are planning metadata only and cannot authorize sandbox writes or production release.
- **GUI-007**: Source acquisition distinguishes Git/ZIP intake from a described path. Example values are clearly labeled and never imply connection, extraction, migration, or deployment.
- **GUI-008**: The activity surface uses only server-emitted lines, explains that closing the pane does not cancel work, and truthfully warns that the current request can be interrupted by browser reload or disconnect until durable jobs exist.
- **GUI-009**: Results distinguish planning readiness, generated artifacts, execution, attestations, and blockers. Retained trigger source is described as `Source logic retained; not yet converted`.
- **GUI-010**: Use explicit text and icons in addition to color for states. Reserve red for actionable failures and green for verified success.
- **GUI-011**: Meet automated WCAG 2.2 AA checks for the tested states, support keyboard operation, visible focus, reduced motion, forced colors, and no page-level horizontal overflow at 390 px.
- **GUI-012**: Preserve current API requests, validation semantics, source workspace behavior, and plan/execution contracts. This increment adds no durable project, trusted approval, connector, or deployment capability.

## Acceptance Criteria

1. A new operator can identify the source, destination, next action, active operation, and blocker without knowing `IR`, `attestation`, or `adapter`.
2. Desktop and 390 px screenshots show no overlapping controls, clipped help, or page-level horizontal scrolling.
3. Keyboard-only use can open and close help, traverse setup, reach validation errors, open activity, and return to the triggering control.
4. Automated accessibility checks report no serious or critical findings for intro, every setup step, validation failure, disabled capability, activity, results, and dialog states represented by fixtures or live APIs.
5. `npm run build` succeeds and browser smoke checks cover Chromium desktop and mobile viewports.
6. Existing server-side tests remain green; no API contract changes are introduced.

## Out of Scope

- Durable projects, drafts, jobs, events, artifacts, or replay.
- Authenticated approval records and trusted attestations.
- Oracle database or Forms runtime connectors.
- New conversion, validation, deployment, or cancellation adapters.
- Claiming visual checks establish native Oracle or migration equivalence.