# 005 - Durable run execution

## Goal

A migration run belongs to the server, not to one HTTP connection. Closing the browser must not cancel
work, and reconnecting must replay the same ordered activity. A process restart must preserve the run,
events, outcome, and artifact manifest; work resumes only when the same workspace node can still prove it
owns the source bytes. Otherwise the run becomes `Interrupted` with a durable reason.

## Requirements

- **RUN-001**: `POST /api/workbench/execute` persists a sanitized, server-authorized run and returns `202` with a server-generated run identifier. The browser never supplies authority, hashes, lease values, state, or a fence token.
- **RUN-002**: Workers claim queued or expired-leased runs atomically. Every claim increments a fencing token. A stale worker cannot append an event, renew, complete, or perform the next external mutation.
- **RUN-003**: Browser disconnect cancels only event following. It does not cancel the run. Operator cancellation is a persisted request observed at a safe point and never claims to undo committed effects.
- **RUN-004**: Activity events are persisted with a gapless per-run sequence and can be replayed after any sequence. One terminal event is persisted for every terminal run.
- **RUN-005**: Run history and run detail are membership-gated by tenant and project. Former or unrelated members cannot enumerate or read a run.
- **RUN-006**: Outcomes and artifact manifests survive restart. Artifact paths remain workspace-relative, hash-addressed metadata; an expired byte payload is reported explicitly rather than hidden as an absent run.
- **RUN-007**: Workspaces and generated output are isolated per run. Starting another run cannot delete or overwrite an earlier run's output.
- **RUN-008**: Authority is re-derived at claim time and rechecked before every external mutation. Stored requests contain sanitized data and immutable bindings, never materialized approval authority.
- **RUN-009**: Same-node recovery may resume an expired lease when source bytes still exist. A run whose workspace node or bytes are unavailable becomes `Interrupted`; it is never silently replayed on another replica.
- **RUN-010**: The GUI exposes project run history, reconnects event following by run ID and last sequence, and renders retained terminal outcomes without rewriting the successful approval or setup experience.

## States

`Queued -> Leased -> Running -> Succeeded | Failed | Cancelled | Interrupted`

A lease may move from `Leased` to a new `Leased` claim only before execution starts, after expiry, and
only on the recorded workspace node. `Running` work is never taken over: an external operation may have
committed after its last observable checkpoint, so a second worker would be unsafe. After process loss,
that run is reconciled to `Interrupted` with its retained event/artifact evidence. Every pre-start
takeover increments the fence. Terminal states are immutable.

## Trust boundary

Agents may still propose changes. Only deterministic planning, membership, persisted approval, latest
target profile, sandbox ownership, run fence, and gateway checks authorize work. A persisted run records
what was requested and bound; it is not itself authorization.

## Operational constraint

Source workspaces currently live on replica-local disk. Durable PostgreSQL state therefore provides full
disconnect recovery and durable restart evidence, but started work is interrupted rather than resumed,
and cross-replica byte recovery is refused. A shared
volume may replace this constraint in a later additive migration; 005 does not pretend local bytes are
shared.
