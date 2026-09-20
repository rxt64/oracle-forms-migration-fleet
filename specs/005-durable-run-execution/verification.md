# 005 - Verification

## Local evidence

- .NET solution: 1,319 passed, 0 failed, 0 skipped.
- Durable file-store, worker, lease/fence, cancellation, restart, retention, and PostgreSQL contract matrix: 50 passed.
- Client TypeScript and Vite production build: passed.
- Desktop Chromium matrix: passed after durable enqueue/follow, reconnect, trust-boundary, and footer updates.
- Durable browser regressions: real enqueue-to-terminal trust path, secret-before-storage refusal, project membership gates, and reconnect cursor/deduplication passed.
- Workbench Bicep compilation: passed.
- `git diff --check`: passed with line-ending notices only.

## PostgreSQL CI evidence

PostgreSQL 16 integration: 5 passed, 0 failed. The durable case proves server-time lease expiry,
late-renewal and stale-fence rejection, ordered events, atomic cancellation/terminal state, history,
artifact manifests, and expired-run reconciliation. Existing lifecycle, sandbox ownership, migration,
and concurrent initialization cases also passed.

## Pending live evidence

The exact-SHA main deployment must apply schema v3, pass authenticated product smoke, remove its ACI,
and leave the intended digest healthy.

## Recovery boundary

Browser disconnect is recoverable through persisted event replay. `Running` work is never taken over.
Expired started work is fenced and becomes `Interrupted`; expired same-replica pre-start leases may be
reclaimed, while expired foreign-replica leases are interrupted because their local source bytes cannot
be proven. Artifact metadata and hashes remain durable; expired bytes return an explicit response.
