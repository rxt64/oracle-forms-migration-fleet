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

## Live deployment evidence

Main SHA `43f0d8528a71b4c5726cbbefa572f7786e54d240` deployed in workflow run
`35489040221` as revision `ca-ofmfleet-dev-ykbpnrpd--0000091` with digest
`sha256:77aac7504e8a57c85aa37222e1cb165fd990a39374f2348dfc46733aef6ebcbb`. Startup applied
platform schema v3, authenticated smoke passed, its ACI runner was removed, and the intended digest was
left healthy.

The smoke exercised authenticated bootstrap, project access, platform-state reads, unauthorized-access
denial, and non-writing approval persistence. It deliberately did not execute customer migration work.
This evidence does not prove worker-process restart during active work, recovery of replica-local source
or artifact bytes on another replica, or business equivalence between generated output and native Oracle
Forms behavior.

## Recovery boundary

Browser disconnect is recoverable through persisted event replay. `Running` work is never taken over.
Expired started work is fenced and becomes `Interrupted`; expired same-replica pre-start leases may be
reclaimed, while expired foreign-replica leases are interrupted because their local source bytes cannot
be proven. Artifact metadata and hashes remain durable; expired bytes return an explicit response.
