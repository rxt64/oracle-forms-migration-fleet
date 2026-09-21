# 006 - Generated-Application Verification

## Goal

An emitted application is not promotable because it compiled. Before production cutover can be planned,
the workbench must execute generated backend tests, generated frontend interaction tests, and generated
PostgreSQL DDL against an isolated disposable target. Results are deterministic, machine-readable,
retained with the durable run, and visible in the GUI.

## Requirements

- `GeneratedApplicationVerification` is distinct from build/static validation and from differential
  behavior testing. Existing phase numeric identities remain unchanged; the new phase is value 12.
- The phase requires sandbox mutation approval and is reauthorized immediately before target writes.
- Backend, frontend, and target database legs each report a typed state. Only `Executed` with at least one
  test is success. Missing, malformed, zero-test, stale, timed-out, unavailable, setup-failed, or
  assertion-failed results block the phase.
- Every report directory is cleared before execution. A report from another run cannot satisfy a leg.
- The backend command is host-owned Maven test execution. The frontend command is host-owned Vitest
  interaction execution. Source or generated content cannot choose an executable or argument.
- Generated build and test commands run from private copies inside a bubblewrap namespace with no network,
  isolated home/tmp directories, and read-only dependency caches populated from checked-in manifests
  during CI/image construction. Missing sandbox support or caches fail closed.
- Target DDL runs in a server-generated PostgreSQL schema and that schema is dropped after each attempt.
- Raw JUnit XML exists only in an unpredictable temporary directory, is parsed and secret-screened, and
  is deleted before the redacted JSON report is retained or exported.
- A successful phase mints only `GeneratedApplicationTestsPassed`. It never mints
  `DifferentialBehaviorTestPassed` and never claims Oracle Forms or Oracle Database equivalence.
- Production authorization requires `GeneratedApplicationTestsPassed` in addition to the existing
  sandbox, reconciliation, and human acceptance attestations.
- The durable report is previewable and its phase state appears in run history and the capability summary.
- CI executes two independent generated fixtures: generic CRUD output and the structurally recognized
  Northstar workflow. CI also executes the disposable-target gateway against PostgreSQL 16.

## Non-goals

- These tests do not run an Oracle Forms runtime or an Oracle Database instance.
- They do not establish business-process parity or source-runtime equivalence.
- They do not replace differential testing, human acceptance, reconciliation, or production approval.