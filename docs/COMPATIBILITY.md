# Version compatibility and evidence

This project does not claim universal Oracle Forms or Oracle Database compatibility. A version is
supported only after representative source from that version has passed parsing, generation, target
compilation, database deployment, and differential workflow tests. An intake value or a documentation
link is not compatibility evidence.

## Demonstrated Northstar path

| Layer | Demonstrated version | Evidence and limit |
|---|---|---|
| Oracle Forms source format | `12.2.1.4` | The synthetic XML fixture declares `FormsVersion="12.2.1.4"` and uses the shape produced by Forms2XML. It is hand-authored test evidence, not an export from a licensed Forms runtime. |
| Oracle source database | Oracle Database Free 23 | The disposable source uses `gvenzl/oracle-free:23-slim-faststart`. Its schema, PL/SQL, and data are synthetic. |
| PostgreSQL target | Azure Database for PostgreSQL 16 | The live development target reports major version `16`. |
| Application target | Java 21 / Spring Boot 3.3.5 and a browser client | GitHub Actions compiles the generated application and runs its generated route tests before deployment. |

The defensible demo statement is:

> A representative migration from an Oracle Forms 12.2.1.4-style XML export backed by Oracle
> Database Free 23 to Azure Database for PostgreSQL 16.

The browser-accessible source is an independently implemented workflow replica. Oracle Forms
Services, WebLogic, Forms Builder, `.fmx` executables, and a licensed Forms runtime are not deployed.
The XML fixture covers one synthetic form block; the complete Northstar workflow contract also uses
the synthetic schema, PL/SQL package, browser application, and executable tests in this repository.

## Not yet claimed

The fleet has no validated product-wide compatibility matrix. It does not currently claim general
conversion support for Oracle Forms 6i, 9i, 10g, 11g, 12c, 14c/14.1.2, or for every Oracle Database
release. References to those releases in the migration landscape are upgrade or discovery guidance,
not proof that generated applications from those releases are compatible.

The researched intake and normalization path for 6i is documented in
[ORACLE_FORMS_6I_RESEARCH.md](ORACLE_FORMS_6I_RESEARCH.md). It keeps 6i at **accepted for assessment**
until an authorized representative pilot passes the stated readiness gates.

`oracleFormsVersion` is assessment metadata supplied by the operator. The fleet records it but does
not use the value to bypass evidence checks or select a converter. `unknown` remains the correct value
when the authoritative source version has not been verified.

## Adding a compatibility claim

Before adding a Forms or database release to a compatibility matrix:

1. Acquire authorized representative Forms modules and all dependent menus, PLLs, OLBs, reports,
   schema objects, PL/SQL, runtime configuration, and integrations.
2. Record the exact Forms and database patch levels and the export/build tools used.
3. Run the fleet from source acquisition through generated application and database deployment.
4. Compile the generated target with its real toolchain and deploy it to an isolated target database.
5. Pass source-versus-target workflow, validation, security, locking, transaction, data, accessibility,
   and performance tests.
6. Publish the tested construct inventory and any unsupported or manually remediated behavior.

Until those steps pass, describe the release as **accepted for assessment**, not **supported for
conversion**.