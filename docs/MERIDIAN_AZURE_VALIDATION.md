# Meridian Azure source-lab validation

Date: 2026-09-23

## Scope and safety

This evidence covers only the repository-owned synthetic Meridian Order Entry source fixture. It is
not customer migration evidence and does not validate Oracle Forms runtime behavior or any target
database. No database image was rebuilt or redeployed, no Container App was restarted, no network or
firewall setting changed, no credential value was read or printed, and no BANKING or target SQL object
was modified.

The validated Azure source was:

- Subscription: `d4394e57-c076-4c92-a870-5de6bf44f255`
- Resource group: `rg-oracle-forms-migration-fleet-dev-b9f0e875`
- Container App: `ca-ofmfleet-db-dev-ykbpnrpd`
- Revision: `ca-ofmfleet-db-dev-ykbpnrpd--0000001`
- Image: `acrofmfleedevykbpnrpd.azurecr.io/oracle-forms-legacy-db:v2`
- Ingress checked by the installer: internal TCP 1521
- Observed SQL*Plus server: Oracle AI Database 26ai Free, version `23.26.3.0.0`

## Reusable execution path

`Invoke-MeridianSqlPlus.ps1` uses the existing Container Apps exec WebSocket and `sqlplus / as
sysdba`. The repaired transport:

- resolves the Windows Azure CLI MSI wrapper to its bundled `python.exe -IBm azure.cli` entry point;
- consumes the initial `SQL>` prompt before sending input;
- accepts one-, two-, and three-digit continuation prompts;
- does not depend on terminal echo, which can wrap long lines;
- appends `EXIT SUCCESS` when an input script does not close SQL*Plus;
- supports a raw bounded input mode for a caller-supplied in-container command without changing the
	prompt-paced SQL*Plus default;
- enforces a configurable 1-30 minute bound, defaulting to 15 minutes;
- propagates anchored `ORA-`, `PLS-`, and `SP2-` diagnostics.

`Provision-MeridianSourceLab.ps1` inventories exact fixture ownership and resumes only these states:
`Absent`, `Schema`, `Seed`, and `Ready`. Unknown or foreign partial ownership is
`PartialUnsupported` and refuses drop, reset, or mutation.

## Installation and inventory evidence

The first live probe reported `MERIDIAN_CHECKPOINT=Absent` and BANKING `19` objects / `0` invalid.
The prompt-paced schema stage completed before the original five-minute transport bound expired, so
the resumed installer correctly recognized `Schema` rather than replaying DDL. It then completed
`Seed` and `Ready` checkpoints.

Final live inventory:

| Assertion | Observed |
|---|---:|
| MERIDIAN users | 1 |
| Expected schema objects | 13 |
| Expected program objects | 4 |
| Unexpected MERIDIAN objects | 0 |
| Named constraints | 10 |
| Disabled or unvalidated named constraints | 0 |
| `DBA_ERRORS` rows | 0 |
| Customers / articles / order headers / order items | 5 / 6 / 2 / 3 |
| BANKING objects | 19 |
| Invalid BANKING objects | 0 |
| Checkpoint | `Ready` |

The final installer result was `OFM_INSTALL|STATE=PASSED`.

## Behavioral verification

The independent `004_meridian_verify.sql` executed against the installed Oracle objects and produced
37 `MERIDIAN ORDER LAB: PASS` lines, zero actual `FAIL` lines, and `MERIDIAN ORDER LAB: SEED OK`.
The assertions covered seed counts, decimal rounding, valid order creation, all declared `-20101`
through `-20107` rejection contracts, atomic savepoint rollback, optimistic revision behavior, and
full rollback restoration to the seeded estate.

## Single-exec two-session concurrency result

Before execution, one bounded read-only Container Apps exec confirmed `/usr/bin/bash`, SQL*Plus
`23.26.3.0.0`, GNU `timeout` `8.30`, `mkfifo`, shell `wait`, `date`, `sha256sum`, and `wc` in the
approved `oracle-free` container. No environment variables, tokens, shared keys, connection strings,
or network configuration were read.

`Test-MeridianConcurrency.ps1` now opens exactly one Container Apps exec WebSocket and runs a bounded
Bash coordinator inside that container. The coordinator starts two independent `sqlplus -s / as
sysdba` processes for each case. Named FIFOs signal S1 lock acquisition, S2 entry, release, and
completion. A five-second `timeout` against the completion FIFO proves S2 did not return before S1
released its transaction; there is no polling or sleep. The outer command is bounded to 180 seconds
and the WebSocket transport to four minutes.

The harness uses only disposable MERIDIAN customer `19001` (`OFM Concurrency Fixture`) and article
`29001` (`OFM Concurrency Fixture Article`). It refuses to start unless the exact `5 / 6 / 2 / 3`
seed baseline is present, restores that baseline after each case, and has an exit trap that removes
only those named rows and their generated orders on failure.

Observed live results:

| Case | S2 blocked for proof window | S1 release | S2 result | Measured wait | S2 stock / revision | Post-case seed |
|---|---|---|---:|---:|---:|---:|
| Commit | Yes | `COMMIT` | `-20105` | 500 centiseconds | `1 / 1` | `5 / 6 / 2 / 3` |
| Rollback | Yes | `ROLLBACK` | `0` | 501 centiseconds | `1 / 1` | `5 / 6 / 2 / 3` |

The commit case proves the waiting order rechecked committed stock and rejected the oversell. The
rollback case proves the waiting order acquired the released lock and succeeded. The successful run
ended with `OFM_CONCURRENCY|STATE=PASSED`, zero MERIDIAN compile errors, exact seed counts
`5 / 6 / 2 / 3`, and unchanged BANKING inventory `19 / 0`.

The local source hashes recorded by that run were:

| Source | SHA-256 |
|---|---|
| `source/db/schema.sql` | `28b5a7880f1b59ec42c54dfa21e576e90f4d11a05c91ffca267b61d73d853369` |
| `source/db/seed.sql` | `54762043ba6495129dfe864d1b2de43cedf268aae181aaeebd0aae11f153de3e` |
| `source/db/package.sql` | `f0e33efa28885bac9a065ca20534b7c013d0da1e02760e7e3679370dc42123c3` |

This proves transaction serialization, oversell prevention, and rollback release only for the owned
synthetic Meridian package on this Oracle source lab. It is not Oracle Forms runtime evidence, target
migration evidence, a customer-estate claim, or the separate two-order reversed-line deadlock check.

## Offline validation

The focused `MeridianSourceLabFixtureTests` suite passed `15/15` after the script changes. It pins the
canonical fixture text, parser/mapping behavior, exact checkpoint safety contract, bounded transport,
single-exec two-session harness shape, and prohibition on schema drops or BANKING mutation.