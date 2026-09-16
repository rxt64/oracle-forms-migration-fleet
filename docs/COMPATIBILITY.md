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
| PostgreSQL target | Azure Database for PostgreSQL 16 | The deployment workflow reads the live server resource version and refuses a major version other than `16`. |
| Application target | Java 21 / Spring Boot 3.3.5 and a browser client | GitHub Actions compiles the generated application and runs its generated route tests before deployment. |

### Live acceptance evidence

The development deployment was revalidated on 2026-09-16:

- Source workflow replica: <https://ca-ofmfleet-forms-dev-ykbpnrpd.jollyground-7a57bcec.eastus2.azurecontainerapps.io/>
- Migrated destination: <https://ca-ofmfleet-mig-dev-ykbpnrpd.jollyground-7a57bcec.eastus2.azurecontainerapps.io/>
- Both applications passed customer authentication and loaded account `500001`, its balance, and posted transactions from their respective databases.
- Both applications passed manager authentication and loaded the authorization-gated submitted-account-request worklist.
- The destination reported a connected PostgreSQL database. Azure reported revision `ca-ofmfleet-mig-dev-ykbpnrpd--0000008` running and ready from image `northstar-migrated:3c496b0e2d22`.
- The deployed destination `app.js` matched the fleet-generated artifact byte-for-byte at SHA-256 `0169ef470cecca57785e1ef4e5f31736a60a8c8b8ebeb50cf29f7623e892e22b`.
- At that acceptance checkpoint, the local solution passed all 1,062 tests, and GitHub Actions CI run `35055850554` passed its build, test, and container-image jobs.

This evidence applies only to the synthetic Northstar path and the deployment above. It does not establish general Oracle Forms release compatibility.

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

## Intake status by release

`OracleLegacyVersionCatalog` canonicalizes the release an operator supplies and decides what evidence
would have to arrive before an artifact could be generated. Every status below is an **intake and
normalization route**, not a tested conversion. No row means a generated application from that release
has been compiled, deployed, or behaviourally tested.

### Oracle Forms

| Release | Intake | Readiness | What the fleet actually requires |
|---|---|---|---|
| pre-6i (3.x, 4.x, 4.5, 5.x) | Recognized, outside the implemented range | `NormalizedTextRequired` | Oracle requires an upgrade to Forms 10.1.2 with every module and library recompiled first. Database-resident modules must be saved to the file system and client-side PL/SQL v1/v2 converted. |
| 6i (6.0.8.x) | Accepted for assessment | `NormalizedTextRequired` | Operator-produced Forms XML. Oracle recommends bridging through Forms 10.1.2 in most cases; `FRM-18130` proves that bridge is mandatory where it is raised. Upgrade order is `.olb`, `.pll`, `.mmb`, `.fmb`. `.fmt`/`.mmt` need 6i tooling to become 6i `.fmb`/`.mmb` first. |
| 9i | Accepted for assessment | `NormalizedTextRequired` | Operator-produced Forms XML, upgraded in the same dependency order. |
| 10g (9.0.4, 10.1.2.x) | Accepted for assessment | `NormalizedTextRequired` | Operator-produced Forms XML. |
| 11g (11.1.x) | Accepted for assessment | `NormalizedTextRequired` | Operator-produced Forms XML. |
| 12c (12.2.1.x) | Accepted for assessment; the demonstrated fixture declares 12.2.1.4 | `NormalizedTextRequired` | Operator-produced Forms XML. The demonstrated path uses a synthetic hand-authored export in this shape. |
| 14c (14.1.2) | Recognized, newer than the implemented range | `AssessmentOnly` | Recorded only. No conversion claim. |
| unknown | Accepted | `Unknown` | Planning and assessment continue. Artifact generation reports the gap; an export that also declares no version fails the normalization phase closed. |
| anything else | Rejected | `Rejected` | Nothing downstream treats the string as a version. |

At **every** Forms release, a `.fmb`, `.mmb`, `.pll`, or `.olb` is a proprietary binary this fleet does
not open. It counts those files by name and size and never decodes them. `SourceNormalization` fails
closed on a binary-only estate, and `ApplicationCodeConversion` refuses to generate an application tier
when Forms binaries exist and no readable `FormModule` XML accompanies them, at every release including
12c. Normalization is performed by the operator, on their own Oracle installation, under their own
licence and support terms: the fleet runs no Oracle tool and holds no `ORACLE_HOME`.

### Automated textual pipeline matrix

CI runs one positive synthetic export through normalization, validated normalized IR, and application
generation for each intake family: Forms 6i (`6.0.8.28`), 9i (`9.0.2.0`), 10g (`10.1.2.3`), 11g
(`11.1.2.2`), and 12c (`12.2.1.4`). Each row verifies the exact declared version and family, source-root
and source-file provenance, IR authority metadata, and key generated Java and browser descriptor content. The existing negative
matrix verifies that a binary-only estate fails closed for every family.

With these five matrix rows and the reference-catalog checks, the current local solution passes all
1,072 tests.

Run the positive matrix locally with:

```powershell
dotnet test tests/oracle-forms-migration-fleet.Tests/oracle-forms-migration-fleet.Tests.csproj `
    --filter "FullyQualifiedName~A_text_export_from_each_intake_family_reaches_strict_ir_and_application_generation"
```

This is parser and generator regression evidence over a common synthetic XML shape. It does not test
Oracle's native binary formats, version-specific Forms widgets or runtime behavior, generated target
compilation for each release, or source-versus-target equivalence. Those require the release
qualification procedure below and authorized source corpora.

### Oracle Database

The database converter reads supplied DDL and PL/SQL **text**. It connects to no Oracle instance, so a
clean conversion is evidence about the export it was given and not about the release that produced it.

| Release | Intake | Readiness | Limit |
|---|---|---|---|
| 6, 7, 8, 8i, 9i, 10g, 11g, 12c (12.1/12.2) | Accepted | `TextEvidenceReady` | Converts the constructs present in the supplied export. Not release-wide support. |
| 18c, 19c, 21c, 23ai / Free 23 | Accepted | `TextEvidenceReady` | Same limit. The demonstrated source database is Oracle Database Free 23. |
| unknown | Accepted with a warning | `Unknown` | Conversion still runs from verified SQL; the report states the release was never established. |
| anything else | Rejected | `Rejected` | The phase fails before writing any DDL. |

`oracleFormsVersion` and `oracleDatabaseVersion` are assessment metadata supplied by the operator. The
fleet records and canonicalizes both, but uses neither to bypass an evidence check or select a
converter. `unknown` remains the correct value when the authoritative source version has not been
verified.

## External sample evidence

The MIT-0 [AWS sample-oracleforms-to-angular](https://github.com/aws-samples/sample-oracleforms-to-angular)
repository contributes useful workshop patterns: separate pipeline and target diagrams, deterministic
extraction before model generation, per-rule traceability, generated equivalence tests, and optional
source-versus-target shadow comparison. No source code or diagram asset from that repository is copied
into this project. Its third-party notice attributes the included FMB and SQL artifacts to a separate
MIT-licensed upstream project; none of those artifacts is copied here either.

Its Oracle Forms input contains six `.fmb` modules. **Six is the file count, not Oracle Forms version
6.** Every inspected binary begins with `ROS.60050`; the sample parser describes the files as Forms
10g/12c object stores. The parser extracts printable byte runs and associates nearby `BEGIN ... END;`
text with trigger-name markers. That can be useful characterization evidence for those sample files,
but it is not a general FMB decoder and provides no Forms 6i compatibility evidence. The sample's
published target also retains Oracle, so it does not prove an Oracle-to-Azure-PostgreSQL database exit.

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