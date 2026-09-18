# 004 — Verification

## Local Implementation Evidence

Run against the final working tree without a deployment or live database mutation:

- .NET solution: 1,279 passed, 0 failed, 0 skipped.
- Focused identity, durable state, PostgreSQL contract, and trust-boundary suite: 118 passed, 0 failed, 0 skipped.
- Client production build: passed.
- Playwright desktop/mobile matrix: 105 passed, 15 intentional project/device skips, 0 failed.
- Bicep compilation: passed for `infra/workbench/main.bicep`.
- Deployment PowerShell parser check: passed for `infra/workbench/Deploy-Workbench.ps1`.
- Editor diagnostics: no errors.
- `git diff --check`: no whitespace errors; Git reported only existing CRLF-to-LF conversion notices.

The browser suite uses explicit Development authentication, a durable file-backed state store, and a declared non-writing sandbox target. It exercises real HTTP project membership, source ownership, approval request/decision/revocation, production denial, invalid-scope denial, anonymous denial, and the approval GUI. It does not authenticate against the live Container Apps proxy or connect to PostgreSQL.

## Review Evidence

- Claude application/security review found and rechecked production role, approval-hash, deployment-configuration, target-coordinate, concurrency, and database-isolation defects. The final implementation uses persisted project roles, one shared trusted request preparation path, complete deployment coordinates, and separate PostgreSQL databases for platform state and sandbox DDL.
- GPT-5.6-Sol independent QA found target-stack drift was not invalidating an approval. The final plan and target-profile hashes bind database, front end, and back end; a changed stack leaves execution approval pending.
- Production writes remain unavailable by design.

## Live Read-only Preflight

Observed on September 18, 2026 for `ca-ofmfleet-dev-ykbpnrpd`:

- external HTTPS ingress, `allowInsecure=false`, one healthy revision;
- Container Apps authentication enabled;
- unauthenticated action redirects to Entra;
- issuer tenant `1984d248-06ca-4d04-a3b8-4c0c1577ab86`;
- client and allowed audiences `0ff0fa49-fce8-4801-ab93-e862a62fd6ab` and `api://0ff0fa49-fce8-4801-ab93-e862a62fd6ab`;
- one allowed operator principal;
- `/readiness` excluded from authentication;
- PostgreSQL Flexible Server `pg-ofmfleet-dev-ykbpnrpd` version 16 exists and the workbench has host/user/database bindings by environment-variable name.

No SQL connection, database creation, schema mutation, auth change, deployment, or resource creation was performed. Direct-ingress header overwrite, managed-identity database connectivity/permissions, creation of the separate `ofm_platform` database, and live platform-schema migration remain unqualified. The deployment now requires platform state in database `ofm_platform` and sandbox migration in a different database such as `postgres`; those live prerequisites must be provisioned and qualified before deployment.