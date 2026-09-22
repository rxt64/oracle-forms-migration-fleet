# 008 - Verification

## Local evidence

- Solution build: passed, including `OracleFormsMigrationFleet.SourceWorker`.
- Self-contained single-file `win-x86` publish: passed; output contained the executable and PDB with no loose runtime.
- Direct protocol execution: exit `2`, `processArchitecture: X86`, `status: BlockedPrerequisite`, and the expected release echo.
- Direct capabilities: `OracleFormsInstallation`, `OracleFormsOpenApiLibraries`, and `OperatorSuppliedExport`, each requiring Windows/x86 and candidate release `6.0.8.22.1`.
- Malformed, unsupported-command, and oversized-request cases returned exits `65`, `64`, and `65` respectively.
- Host configuration, integrity, strict protocol, and process-boundary matrix: 33 passed.
- Real host-to-worker integration: 1 passed with a sentinel proving the process path executed.
- Exact-SHA deployment verifier tests: passed, including a missing-job matrix for the native worker gate.
- Application-specialist review findings were repaired; independent GPT-5.6 Sol QA returned PASS with no high or medium findings.

## CI contract

The Windows job publishes and executes the real x86 worker, checks positive and negative protocol exits,
and runs the host-to-worker integration test. Deployment depends on this job and the exact-SHA verifier
requires its successful conclusion.

PR #32 CI run `35656048642` passed at implementation commit
`ff183901442e89b8b10c8147aa57fa306608b7b9`: `Native source worker contract`, `build-and-test`,
`Container image builds`, and `Guided UI browser checks` all completed successfully.

Native Forms execution and extraction remain blocked by spec 007 prerequisites.