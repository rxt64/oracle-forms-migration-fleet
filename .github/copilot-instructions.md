# Oracle Forms Migration Fleet — Copilot instructions

## The mission, and the standing order

**This product exists to migrate Oracle Forms applications and their Oracle databases onto Azure
components, and to do it from the GUI.** Not to plan a migration. Not to describe one. To perform one.

The operator's standing instruction, which overrides any convenient narrower reading:

> The Oracle database **and** the Oracle Forms application must both be migrated to Azure components,
> **using the GUI**. That is the whole point of this app.

Both halves are in scope. A run that converts a schema and leaves the application on Oracle has not
migrated the application, and must not be reported as if it had.

## How to behave when a phase cannot run

When something blocks a migration, the job is to **remove the blocker**, not to narrate it.

Ranked responses, best first:

1. **Build the missing capability.** No adapter for a phase? Write the adapter. That is the work.
2. **Fix the thing that is wrong.** A gate demanding evidence a phase never reads is a defect; fix the
   gate. An emitter producing SQL that cannot execute is a defect; fix the emitter.
3. **Only then** report a limit — and when reporting one, say precisely what would remove it.

Do not answer a request to migrate with an explanation of why migration is not implemented. If the
capability is missing, the correct response is to add it and then run it.

## Never fake progress

These rules are not in tension with the mission; they are what makes a completed migration believable.

- Never report a phase as performed unless an adapter ran and returned artifacts.
- Never tick evidence on the operator's behalf, and never suggest ticking a box that is not true. If a
  gate is blocking for a bad reason, fix the gate rather than defeating it with a false attestation.
- Never claim generated output works because it parses. Executable output is verified by executing it.
- State plainly what did **not** happen. "Schema converted, application untouched, no data moved" is a
  useful report. "Migration complete" over the same facts is a lie.

## What counts as done

A migration of this estate is complete when all of the following are true and evidenced:

| Tier | Done means |
|---|---|
| Database schema | DDL generated **and executed** against the Azure target |
| Data | Rows moved and reconciled against the source counts |
| Application code | Generated, building, and running against the Azure database |
| Runtime | The deployed app serves its workflows with **no Oracle in the path** |

Until the last row is true, say so.

## The GUI is the product surface

Every capability must be reachable from the workbench. A migration that only works because an engineer
ran a console harness has not met the requirement — the fleet has to be able to do it on its own, from
the browser, for an operator who is not the person who wrote it.

When adding a capability: adapter first, register it in `MigrationExecutor.DefaultAdapters`, surface it
in the wizard, then prove it by running it in the browser and reading the artifacts back.

## Where the boundaries genuinely are

Some limits are real. Respect these, and be specific about them rather than vague.

- `.fmb` files are a proprietary binary; their contents need Forms Builder or the Forms JDAPI. Index
  them by name and size, and never claim to have read their triggers or blocks.
- PL/SQL bodies are not machine-translated here. Report them as manual PL/pgSQL work, with reasons.
- A model may review generated artifacts and nothing else. No model output may open a gate, sign an
  attestation, alter a deterministic report, or reach the platform recommendation.
- Nothing writes to a customer tenant without a named approver on the run.

## Verified environment facts

- `.slnx`, not `.sln`: `oracle-forms-migration-fleet.slnx`. Scripts globbing `*.sln` find nothing.
- `dotnet` is at `C:\Users\rosobra\.dotnet\dotnet.exe` and is **not** on `PATH`.
- Test: `& 'C:\Users\rosobra\.dotnet\dotnet.exe' test '.\oracle-forms-migration-fleet.slnx' --nologo`
- Kill a running workbench before building; it locks `bin/Debug/net10.0/*.dll`.
- Client bundle: `npm run build` in `src/oracle-forms-migration-fleet/ClientApp`.
- Azure Container Instances SNAT **outbound** through a different address than the public inbound IP.
  Allowlisting the inbound IP silently fails, and PostgreSQL Flexible Server drops rather than rejects,
  so it presents as a timeout. Make the endpoint report the address it actually sees.
