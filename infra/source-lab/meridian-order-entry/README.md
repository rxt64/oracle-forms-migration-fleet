# Meridian Order Entry — synthetic source lab

An **owned, original, fictional** Oracle order-entry estate: schema, seed data, a real PL/SQL
package, a textual Forms export, and a target-mapping manifest. It exists to give the fleet a
source it is allowed to read end to end, without a customer, a licence, or a native binary.

**This is source-lab provisioning material. It is not a customer migration, and nothing here is
evidence of one.**

## What this is not

- **Not Oracle Forms.** `source/forms/MRD_ORDER_ENTRY.xml` is a hand-authored `SyntheticExport`.
  No `.fmb`, `.fmx`, `.mmb`, `.mmx`, `.pll`, `.plx` or `.olb` exists here, and none is simulated.
  There are no fabricated native bytes anywhere in this fixture.
- **Not a Forms runtime proof.** No Forms Builder, Compiler, Runtime, Migration Assistant, JDAPI or
  Open API call has ever touched this module. It has never been compiled and never executed.
- **Not Forms 6i evidence.** The export declares `version="12.2.1.4"` because 12.2.1.4 is a release
  this fleet's normalizer already reads, so the fixture exercises a supported path. That attribute
  is a deliberate synthetic choice, not a claim that a 12.2.1.4 tool ran. Nothing here advances the
  native 6i prerequisites in [docs/NATIVE_SOURCE_PREREQUISITES.md](../../../docs/NATIVE_SOURCE_PREREQUISITES.md).
- **Not derived from the generated target.** The PL/SQL package was written first and independently.
  The PostgreSQL routine this repository's generator emits is *target output*; it is never used as
  the oracle for what this source is supposed to do, and the verifier's expectations are literals
  declared in the verifier itself.
- **Not the banking estate.** It installs into its own schema owner, `MERIDIAN`. The banking init
  scripts under `infra/legacy-estate/oracle/initdb` are untouched — not edited, not renumbered, not
  reordered — and no `BANKING` object is modified.

## Provenance

| Fact | Statement |
|---|---|
| Authorship | Written in this repository for this repository. No customer, sample application, upstream repository, PDF, or vendor artifact was transcribed or reverse-engineered. |
| People and companies | All invented. No name, address, price, or order corresponds to anything real. |
| Table and column names | Identical to the `synthetic-fixture-meridian-orders` mapping fixture already declared in `tests/oracle-forms-migration-fleet.Tests/DotNetPilotFixtures.cs`, so the lab and the fixture are one estate rather than two inconsistent ones. `source/db/schema.sql` is byte-identical to that fixture and a test enforces it. |
| Business logic | `source/db/package.sql` is new. It shares no text with the generator's emitted routine. |
| Forms export | Hand-authored text in the shape `frmf2xml` emits. Labelled `SyntheticExport` in its own header. |
| Oracle syntax target | Oracle Database Free 23 (`FREEPDB1`), the release this repository's lab image is built from. |

## Layout

```
source/db/schema.sql       Table DDL. Byte-identical to the checked-in mapping fixture.
source/db/sequences.sql    Surrogate-key sequences and indexes (lab installation material).
source/db/seed.sql         Invented customers, articles, and two historical orders.
source/db/package.sql      MRD_ORDER_LINE_T / MRD_ORDER_LINE_TAB / MRD_ORDER_ENTRY_API.
source/forms/MRD_ORDER_ENTRY.xml   SyntheticExport of the order-entry module.
source/mapping/target-mapping.json Conventional fleet target-mapping manifest.
initdb/00{1..4}_*.sql      Ordered installation: schema, seed, PL/SQL, independent verifier.
Dockerfile                 Definition only. Not built and not deployed by this change.
```

`MERIDIAN` is created `NO AUTHENTICATION`, so nothing can log in as it and no credential exists.
Its storage is bounded — `DEFAULT TABLESPACE USERS` with `QUOTA 64M ON USERS`, not
`GRANT UNLIMITED TABLESPACE` — because the lab holds four small tables and a few dozen rows, and a
runtime bug should not be able to fill a shared tablespace. `001` checks that a permanent `USERS`
tablespace exists and stops with a named error if it does not, rather than failing later with a
quota error that reads like a defect in the DDL. **Like everything else here, that has not been
run** — see the next section.

`source/` is laid out as a fleet **source root**: `db/schema.sql` is where the conversion adapter
looks for Oracle DDL, `mapping/target-mapping.json` is `TargetMappingReader.ConventionalPath`, and
the Forms export is discovered by extension. It can be pointed at a run as-is.

## The supported contract

`MRD_ORDER_ENTRY_API` is the routine a caller is meant to use. It is deliberately a *contract*, not
a script:

- **No routine commits.** Each public routine opens its own `SAVEPOINT` and, on any error, rolls
  back only its own work before re-raising. A caller can therefore compose several calls in one
  transaction, and a failure rolls back the failed call without discarding the caller's earlier
  work. The verifier asserts both halves of that.
- **Locking.** `CREATE_ORDER` takes the `MRD_CUSTOMER` row `FOR UPDATE` first, then `MRD_ARTICLE`
  rows `FOR UPDATE` in ascending `ART_NO` order. Every session takes the same locks in the same
  order, so two sessions ordering the same articles queue rather than deadlock; the customer's
  eligibility cannot be revoked between the check and the insert, and the stock check and the stock
  decrement happen under one lock.
- **Optimistic revision.** `ART_REV` increments on every stock movement. `ADJUST_STOCK` takes an
  expected revision and fails with `-20107` when the article moved underneath the caller.
- **Money is decimal throughout.** `NUMBER(11,2)` unit prices, `NUMBER(14,2)` extended and order
  amounts, every extended amount `ROUND(qty * price, 2)`. No binary floating point anywhere.

Error numbers are part of the contract:

| Number | Meaning |
|---|---|
| `-20101` | The order carries no lines. |
| `-20102` | The customer does not exist or is not active. |
| `-20103` | An article does not exist or is not active. |
| `-20104` | A quantity is null, zero, negative, or fractional. |
| `-20105` | Stock on hand cannot cover the line. |
| `-20106` | The same article appears on more than one line. |
| `-20107` | The article's revision moved since the caller read it. |

## Installation — validated on Azure (2026-09-23)

The fixture is installed in the isolated `MERIDIAN` schema of the disposable Azure source lab. The
checkpointed installer, independent verifier, exact live inventory, and remaining concurrency limit
are recorded in [docs/MERIDIAN_AZURE_VALIDATION.md](../../../docs/MERIDIAN_AZURE_VALIDATION.md).
No database image was rebuilt or redeployed, and no BANKING or target SQL object was changed.

The already-released `oracle-forms-legacy-db:v2` image **cannot pick these scripts up**: the Oracle
Free entrypoint runs `/container-entrypoint-initdb.d` only on the first startup of an empty data
volume. Automatic first-start initialization would require a new image built by the trusted GitHub
workflow, tagged from a commit, and deployed only under a separate approval. The approved disposable
existing lab instead uses the checkpointed direct installer below; do not switch its running image.

The `Dockerfile`'s `COPY initdb/ ...` is relative to the build context, and the only context it
resolves against is this directory. The trusted workflow must therefore build with:

| Setting | Value |
|---|---|
| Build context | `infra/source-lab/meridian-order-entry` |
| Dockerfile | `infra/source-lab/meridian-order-entry/Dockerfile` |

A build rooted at the repository root finds no `initdb/` and fails; a build rooted elsewhere would
copy some other directory's scripts into the entrypoint. No Meridian image build was needed or run:
the reusable installer uses supported Container Apps exec against the existing disposable source lab.

```powershell
.\Provision-MeridianSourceLab.ps1 -Operation Probe
.\Provision-MeridianSourceLab.ps1 -Operation Install
```

`Install` resumes only the exact owned checkpoints `Absent`, `Schema`, `Seed`, and `Ready`. Any
unexpected MERIDIAN object, invalid named constraint, non-fixture row count, or compile error is
`PartialUnsupported` and refuses mutation; the installer never drops or resets a schema.

Against a disposable Oracle Free 23 instance, as a user able to `CREATE USER` in `FREEPDB1`:

```text
@initdb/001_meridian_schema.sql
@initdb/002_meridian_seed.sql
@initdb/003_meridian_plsql.sql
@initdb/004_meridian_verify.sql
```

`004` fails initialization unless every expectation below holds, and prints one `PASS`/`FAIL` line
per check followed by `MERIDIAN ORDER LAB: SEED OK`.

### What the verifier asserts

| Group | Expectation |
|---|---|
| Seeded estate | 5 customers, 6 articles, 2 order headers, 3 order lines, 0 invalid `MERIDIAN` objects. |
| Decimals | `LINE_AMOUNT(3, 19.99) = 59.97`; `LINE_AMOUNT(7, 0.05) = 0.35`; `LINE_AMOUNT(3, 4.255) = 12.77`; `ORDER_TOTAL(9001) = 68.47`. |
| Valid order | Customer 1001, lines `(2001, 2)` and `(2002, 4)` → header total `56.98`, lines sum `56.98`, stock `40 → 38` and `150 → 146`, both revisions `+1`, one header and two lines added. |
| Invalid quantity | `0`, `-3`, and `1.5` each rejected with `-20104`. |
| Missing customer | Customer `9999` rejected with `-20102`; inactive customer `1005` rejected with `-20102`. |
| Missing article | Article `8888` rejected with `-20103`; inactive article `2006` rejected with `-20103`. |
| Empty order | No lines rejected with `-20101`. |
| Duplicate line | Article `2001` twice rejected with `-20106`. |
| Insufficient stock | 4 of article `2005` (3 on hand) rejected with `-20105`. |
| Atomic rollback | `(2004, 5)` then `(2005, 99)` rejected with `-20105`; article `2004` stock and revision unchanged; no header or line rows left behind; the caller's earlier uncommitted order survives. |
| Optimistic revision | An in-date `ADJUST_STOCK` moves stock and revision together; repeating it with the stale revision is rejected with `-20107` and moves nothing. |
| Restoration | After `ROLLBACK`, counts and every stock/revision pair are back to the seeded values. |

## Concurrency — reusable harness, blocked at ACA concurrent exec

**A single session cannot prove a race.** The verifier says so in its own output. The reusable
`Test-MeridianConcurrency.ps1` opens independent SQL*Plus jobs, measures S2 wait time, covers both
S1 commit and rollback, and restores the synthetic seed. On 2026-09-23, ACA rejected the second
exec WebSocket with HTTP 429 while S1 held the transaction, so two-session blocking remains
unverified. The harness reports that platform boundary as a failure and never converts it into a
database pass.

The real check needs two sessions against one database. Run it manually:

1. Open **two** SQL\*Plus sessions, S1 and S2, both connected to `FREEPDB1` with `MERIDIAN` as the
   current schema. Do not use one session with `AUTOCOMMIT`; the whole point is two transactions.
2. Note the starting position: `SELECT ART_NO, ART_ON_HAND, ART_REV FROM MRD_ARTICLE WHERE ART_NO = 2005;`
   The seed leaves 3 on hand.
3. In **S1**, without committing:
   ```sql
   DECLARE v_ord NUMBER; BEGIN
     MRD_ORDER_ENTRY_API.CREATE_ORDER(1001, MRD_ORDER_LINE_TAB(MRD_ORDER_LINE_T(2005, 2)), v_ord);
     DBMS_OUTPUT.PUT_LINE('S1 order ' || v_ord);
   END;
   /
   ```
   S1 now holds the row lock on article 2005 and has **not** committed.
4. In **S2**, run the same block asking for 2. **Expected: S2 blocks.** It must not return. If it
   returns immediately, the lock is not doing its job and the result is a defect, not a pass.
5. In **S1**, `COMMIT;`.
6. **Expected: S2 now completes and fails with `ORA-20105`** — 1 remains on hand, 2 were asked for.
   If S2 instead succeeds, the estate has oversold and the lab has failed.
7. End both sessions' transactions, reset the disposable fixture to its original seed, and confirm
  article 2005 has 3 on hand and the original revision. Then repeat from step 3 with `COMMIT`
  replaced by `ROLLBACK` in step 5. **Expected: S2 succeeds**,
   because S1's reservation was released, and article 2005 ends at 1 on hand with `ART_REV` moved
   exactly once.
8. Record, for each step: the session, the exact statement, the observed block/return, the error
   number, and the `ART_ON_HAND`/`ART_REV` values read afterwards. A run without those readings is
   not evidence.

Deadlock check, same shape: S1 orders `(2001, 1)` then `(2005, 1)`; S2 orders `(2005, 1)` then
`(2001, 1)`. Because the package sorts its lines by `ART_NO` before locking, both sessions take the
locks in the same order. **Expected: one session waits and then proceeds or fails cleanly; neither
raises `ORA-00060`.**

## Unresolved translation gaps

These are **not** hand-wired into any target. They are the explicit coverage gaps this fixture puts
in front of the fleet, and they stay unresolved until a rule is implemented and tested. No trigger
property is automatically retired, and nothing below is silently dropped.

A construct the fleet cannot translate is a **coverage gap**. A name that resolves to nothing is a
**defect in the source**. The two are different findings and this fixture keeps them apart: every
design-time name the installed export references is declared in the installed export, and a test
asserts it. The unresolved-reference case — a `VISUAL_ATTRIBUTE` naming an undeclared attribute, and
a `CALL_FORM` naming a module the export does not carry — is exercised by an in-memory variant
inside `tests/oracle-forms-migration-fleet.Tests/MeridianSourceLabFixtureTests.cs`. That variant is
never installed, never pointed at a run, and adds no file here.

The Unsupported and ManualReview findings the installed export produces are pinned exactly by the
same test file, so a construct that newly disappears and a construct that becomes translated both
fail the build and have to be accounted for.

### Forms source constructs in `MRD_ORDER_ENTRY.xml`

| Construct | Where | Why it is unresolved |
|---|---|---|
| `KEY-COMMIT` + `COMMIT_FORM` | `ORDER_BLOCK` | The Forms runtime owns the transaction boundary. The target has no equivalent block-level commit, so the boundary has to be re-decided, not translated. |
| `ON-ERROR` with `DBMS_ERROR_CODE` / `DBMS_ERROR_TEXT` | form level | Reads the Forms runtime error stack. There is no target equivalent to read. |
| `POST-CHANGE` | `LINE_BLOCK` | An obsolete trigger kept for backward compatibility; its firing rules are not reproducible from any target event model. |
| `SET_ITEM_PROPERTY(..., UPDATE_ALLOWED, PROPERTY_FALSE)` | `WHEN-NEW-RECORD-INSTANCE` | Runtime mutation of a design-time property. Carrying it forward is a product decision, not a mechanical rewrite. |
| `SET_ITEM_PROPERTY(..., VISUAL_ATTRIBUTE, 'VA_TOTALS')` | `WHEN-NEW-RECORD-INSTANCE` | Named visual attributes have no target equivalent. `VA_TOTALS` **is** declared in the module, so the reference resolves; what is unresolved is the translation, not the name. |
| `WHEN-TIMER-EXPIRED` | `TOTALS_BLOCK` | Forms timers have no target scheduler bound to them. |
| `GO_BLOCK`, `FIRST_RECORD`, `NEXT_RECORD`, `:SYSTEM.LAST_RECORD` | `SUBMIT_CURRENT_ORDER` | The collection is assembled by driving the Forms navigation state machine. There is no target state machine to drive. |
| `RAISE FORM_TRIGGER_FAILURE` | several triggers | Forms-specific control flow, not a PL/SQL exception a target can re-raise. |
| `MESSAGE(...)` | several triggers | Writes to the Forms message line. |
| `:BLOCK.ITEM` bind references inside SQL | `POST-CHANGE`, `REFRESH_TOTALS` | Forms item binds inside embedded SQL have no direct target form. |
| `LOV` / `RecordGroup` (`CUSTOMER_LOV`, `ARTICLE_LOV`) | form level | Read as structure by the normalizer; no target picker is generated from them. |

### Oracle PL/SQL constructs in `package.sql`

| Construct | Why it is unresolved |
|---|---|
| `MRD_ORDER_LINE_T` object type and `MRD_ORDER_LINE_TAB` nested table as a procedure parameter | This fleet's generator emits no PostgreSQL composite-type/array equivalent, and the call shape a client would use is different. |
| `SELECT ... FROM TABLE(p_lines)` | Collection unnesting inside SQL. No emitted equivalent. |
| `SAVEPOINT` / `ROLLBACK TO SAVEPOINT` inside a routine | The generated target routine does not reproduce nested savepoint ownership, so the composability guarantee above does not survive conversion today. |
| `SELECT ... FOR UPDATE` with a deliberate key-ordered lock sequence | The generator's concurrency strategy is chosen from the mapped `ConcurrencyVersion` role, not from this source's lock ordering. The lock ordering is therefore not carried. |
| `PRAGMA EXCEPTION_INIT` and the `-20101`..`-20107` caller contract | No mapping from these numbers to target error codes exists, so a client branching on them would break. |
| `%TYPE` anchored declarations | Not re-anchored in the target. |
| `DETERMINISTIC` on `LINE_AMOUNT` | No equivalent is emitted. |
| `MRD_ORDER_SEQ` / `MRD_ORDER_ITEM_SEQ` with `NOCACHE ORDER NOCYCLE` | Ordering and caching semantics are not reproduced. |
| `TRUNC(SYSDATE)` into `ORD_RAISED DATE` | Oracle `DATE` carries a time component that `TRUNC` removes; the target type decision changes the semantics. |

## Status

| Item | State |
|---|---|
| Files authored | Done |
| JSON / XML well-formedness | Checked offline |
| Manifest resolves against the schema through `TargetMappingReader` | Checked offline by test |
| Forms export parses through `FormsModuleParser` | Checked offline by test |
| Lab text matches the checked-in mapping fixture | Checked offline by test |
| Oracle DDL / PL/SQL compiles | **Passed on Azure source lab** — 0 `DBA_ERRORS`, 0 invalid MERIDIAN objects |
| Verifier expectations | **Passed on Azure source lab** — 37 PASS lines, 0 FAIL lines, seed restored by rollback |
| Two-session concurrency protocol | **Blocked externally** — reusable harness added; ACA concurrent exec returned HTTP 429 |
| Image built, released, or deployed | **Not done, and deliberately not attempted** |
