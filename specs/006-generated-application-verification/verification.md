# 006 - Verification

## Local evidence

- Typed verifier and process hardening matrix: 50 passed. Covered missing, malformed, counter-mismatched,
  zero-test, all-skipped, stale, and cross-run reports; secret redaction and raw-report deletion;
  backend/frontend/target failures; tool absence; setup failure; timeout; full pass; attestation
  separation; emitted locks; and fail-closed sandbox wiring.
- Focused lifecycle/planner/executor/verifier matrix: 113 passed.
- Generated emitter/verifier matrix: 20 passed.
- Independent-fixture and Northstar offline contracts: 100 passed.
- Client TypeScript and Vite production build: passed.
- Real generated Northstar Vitest interaction: 1 passed; production Vite build passed.
- Targeted durable/trust desktop Chromium: 4 passed. Full desktop Chromium: 61 passed, 1 skipped.
- Full-suite diagnostic: 1,272 passed before a 60-second inactivity detector stopped two existing
  concurrent 20,000-file stress tests. Feature, platform-state (43), and durable-store/worker (13) groups
  pass separately. The complete Linux CI run remains authoritative.

## Pending CI evidence

- Real Maven and Vitest execution for generic CRUD and Northstar generated outputs.
- Runtime bubblewrap proof that a host sentinel and managed-identity address are unreachable.
- Disposable PostgreSQL 16 generated-schema execution and cleanup.
- Full .NET, client, browser, and container matrices for the exact PR SHA.

## Claim boundary

Passing this phase proves only that generated tests executed against generated application output and
that generated PostgreSQL DDL executed in the configured target engine. No Oracle Forms runtime or
Oracle Database instance is contacted. Native behavior equivalence remains unverified unless a separate
successful differential-behavior attestation exists.