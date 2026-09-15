# Oracle Forms 6i research

Status: research and implementation plan, not a compatibility claim.

Oracle's current documentation calls this release family **Oracle Forms 6i**. Intake must record the
exact Forms build, patch set, platform, character set, database release, and deployment model rather
than accepting the ambiguous label "Forms 6".

## Executive findings

1. Oracle documents an upgrade path from Forms 6i to the current 14.1.2 release, but this is an
   Oracle-to-Oracle Forms upgrade path. It does not generate a browser application or migrate the
   Oracle database to PostgreSQL.
2. Forms 6i binaries are not a stable direct input for the current fleet. The fleet currently counts
   `.fmb` files but does not read them or invoke Oracle tooling. It can parse a supplied XML document
   containing `FormModule` elements and recover a limited attribute-level subset; it does not validate
   Forms2XML provenance or parse menu and object-library exports.
3. Source normalization needs an operator-provided Oracle installation used under the customer's
   applicable license and support terms. Oracle recommends upgrading modules through Forms 10g
   `10.1.2.x` in most cases. If that bridge is omitted, `FRM-18130` proves it is mandatory.
4. Conversion order matters: common dependencies belong in `FORMS_PATH`, and Oracle directs users to
   upgrade object libraries, PL/SQL libraries, menus, and forms in that order: `.olb`, `.pll`, `.mmb`,
   then `.fmb`.
5. A successful compile is necessary but insufficient. Some obsolete calls still compile but do
   nothing or fail only at runtime, and the Migration Assistant can report matches inside comments.
6. Client/server assumptions are the largest modernization risk. Forms 6i could run UI, triggers,
   host commands, file access, user exits, and platform-specific controls on the desktop. A browser
   and stateless service require those responsibilities to be allocated explicitly.

## Oracle upgrade guidance and proposed fleet normalization path

### Forms 6i FMB, MMB, PLL, and OLB source

Oracle's basic process is to back up the source, open the 6i modules in a newer Forms Builder, save
them, and compile them. Oracle recommends passing modules through Forms 10g `10.1.2.x` in most cases
before moving to a current release. If that bridge is omitted, `FRM-18130` proves it is required.

Based on Oracle's upgrade requirements, the proposed fleet-specific normalization chain is:

1. Hash and preserve the original source tree without modifying it.
2. Inventory dependencies and establish `FORMS_PATH`.
3. Upgrade `.olb`, `.pll`, `.mmb`, and `.fmb` modules in dependency order through Forms `10.1.2.x`
   in most cases, preserving the untouched 6i copy.
4. Open, save, and compile the bridged modules with the current Builder or Compiler. If a direct path
   was explicitly chosen and raises `FRM-18130`, return to the untouched source and use the bridge.
5. Run the Forms Migration Assistant and retain every per-module log.
6. Compile all normalized modules and libraries with the real Oracle compiler.
7. Export normalized modules to Forms XML or another stable textual representation.
8. Give the normalized text, original source hashes, compiler logs, migration logs, database source,
   runtime configuration, and behavioral baseline to the fleet.

An upgraded module cannot be opened in an earlier Forms Developer release. Normalization must always
operate on a disposable copy.

### FMT and MMT text modules

Oracle says 6i FMT/MMT files cannot be converted directly by a current Builder because obsolete
properties may be present. Use a Forms 6i Builder or Compiler to convert FMT/MMT to 6i FMB/MMB first,
then open, save, and compile those binaries with the newer toolchain.

### Pre-6i source

Oracle distinguishes pre-6i applications from 6i. Forms 3.x, 4.x, 4.5, and 5.x applications must
first be upgraded to Forms 10g `10.1.2`, with all modules and libraries recompiled, before moving to
the latest release. Modules stored in the database must first be saved to the file system. Client-side
PL/SQL v1 or v2 must be converted.

## What the Forms Migration Assistant does

The Oracle Forms Migration Assistant (`frmplsqlconv` / `frmplsqlconv.sh`) processes Forms modules,
menus, object libraries, and PL/SQL libraries. It can:

- replace selected one-to-one built-ins and constants;
- perform some structured substitutions, such as `CHANGE_ALERT_MESSAGE` to `SET_ALERT_PROPERTY`;
- convert selected Reports calls such as `RUN_PRODUCT` to `RUN_REPORT_OBJECT` when required Reports
  settings and libraries are available;
- warn about obsolete built-ins, item types, V2 triggers, and invalid trigger placement;
- run in batch or wizard mode and emit one or multiple logs.

It does not establish behavioral equivalence. Oracle directs users to inspect logs and manually make
changes the tool could not perform. Its search can also match obsolete names in comments, so findings
need source-location review rather than simple counts.

## High-risk Forms 6i constructs

| Area | Oracle-documented change | Fleet treatment |
|---|---|---|
| Deployment | Client/server and character-mode runtimes were removed | Block automatic conversion until desktop, terminal, and process assumptions are inventoried |
| V2 triggers | Dropped during upgrade; names are logged | Require recoding to PL/SQL before normalization and preserve the original trigger inventory |
| Trigger placement | Several triggers are valid only at form or block scope | Model scope in the intermediate representation and add event-order tests |
| Mouse triggers | mouse-enter, mouse-leave, and mouse-move do not run on the web | Redesign or retire; do not silently omit |
| OLE, OCX, ActiveX, VBX | VBX, OLE Container, and OCX/ActiveX item types are obsolete and can prevent compilation; programmatic OLE interaction with an external middle-tier OLE server is a separate supported case | Classify UI controls separately from middle-tier automation; capture side effects and user workflow evidence |
| Host and file access | `TEXT_IO`, `HOST`, `ORA_FFI`, and image-file operations execute against the application server in Forms web deployment; `GET_FILE_NAME` compiles but returns `NULL`; client-disk access is not native | Classify client-versus-server intent; redesign as secured service or browser upload/download flows, or assess WebUtil when retaining Forms |
| User exits | V2 callbacks were removed; external libraries require compatible recompilation | Inventory native source, ABI, platform, side effects, and secrets; usually redesign as services |
| Menus | full-screen/character menus, menu parameters, and many built-ins were removed | Convert navigation semantics, not only labels; preserve keyboard and role behavior in tests |
| Item types | chart, sound, VBX, OLE Container, and OCX/ActiveX item types are obsolete and may prevent compilation | Flag unsupported widgets, preserve the distinct programmatic middle-tier OLE case, and require target UX acceptance |
| LOVs | old-style LOVs are converted to record-group query LOVs | Extract query, bind variables, validation, ordering, and empty-result behavior |
| Reports | Graphics and old `RUN_PRODUCT` patterns changed; Reports requires server configuration | Inventory RDF/REP files, parameters, destinations, printers, schedules, and output parity |
| Java | 6i PJCs/JavaBeans may depend on removed `oracle.ewt` classes; client JARs must be signed | Inventory JARs/classes/certificates and redesign browser-incompatible code |
| Fonts and layout | Java font metrics changed and can cause label/field overlap | Require screenshot and accessibility comparison at representative resolutions |
| Runtime compatibility | 4.5 compatibility mode is ignored; 5.0 behavior is used | Capture validation and navigation behavior before normalization |
| Obsolete calls | Some have no replacement; some compile but have no effect or fail at runtime | Static signatures create blockers, and executable tests decide acceptance |

## Required source inventory

A credible 6i assessment should collect, with provenance and hashes:

- Forms, menus, libraries, and textual modules: FMB, MMB, PLL, OLB, FMT, and MMT;
- compiled runtime artifacts for inventory, deployment comparison, and executable baselining only:
   FMX, MMX, and PLX; do not assume editable source can be recovered from them;
- Oracle Reports source and output definitions: RDF, REP, parameter forms, printer and distribution
  configuration;
- database DDL, packages, procedures, functions, triggers, views, synonyms, sequences, grants, and
  representative data profiles;
- `FORMS_PATH`, registry/terminal settings, environment variables, startup parameters, and NLS values;
- icons, images, fonts, help files, message files, and localization assets;
- JavaBeans, PJCs, JARs, certificates, OLE/OCX/VBX inventory, WebUtil use, user exits, native libraries,
  and external executable calls;
- authentication, authorization, SSO, database roles, menu security, and audit behavior;
- integrations, file shares, email, printing, reports, batch jobs, and downstream consumers;
- observed workflows, screenshots, keyboard behavior, error paths, transaction boundaries, locking,
  performance, and regression tests.

The Forms release does not identify the Oracle Database release. Database version and patch level must
be discovered independently.

## Proposed fleet architecture

### 1. Add a gated 6i source-recovery adapter

Run Oracle tools in an operator-provided, isolated worker with an approved `ORACLE_HOME`. Confirm
tool use, hosting, and redistribution restrictions against the governing Oracle agreement and current
Oracle licensing documentation. The worker should have read-only source input, a disposable output
directory, no target credentials, bounded process execution, and captured stdout/stderr.

### 2. Produce a normalization manifest

For every module, record:

- original path and SHA-256;
- detected source version and module type;
- each tool/version and command invoked;
- whether the 10.1.2 bridge was required;
- normalized binary and text output hashes;
- compile status and diagnostic log;
- Migration Assistant warnings by category and source location;
- unresolved dependencies and manual modifications.

### 3. Expand the Forms intermediate representation

The current parser accepts supplied XML documents containing `FormModule` elements. It recovers a
limited subset of block/item attributes plus trigger, program-unit, and LOV names, but does not invoke
Forms2XML, validate export provenance, parse menu/object-library exports, or recover trigger bodies and
runtime semantics. A 6i-capable IR needs exact trigger/program-unit source, scope, event ordering,
navigation, validation, commit/rollback behavior, LOV/record-group queries, menus, visual properties,
canvas/window geometry, object-library references, and external integrations.

### 4. Add 6i-specific deterministic findings

Inventory signatures should cover every high-risk area above. A construct with no safe target mapping
must become a blocking or manual-review finding, never disappear from generated output.

### 5. Generate and validate a representative pilot

Start with a dependency-complete slice: several forms, one menu, shared PLL/OLB code, at least one
report, and one client-machine integration. Build the target with its real compiler and database, then
run source-versus-target differential tests for workflows, validation, data, authorization, locking,
printing/report output, accessibility, and performance.

## Readiness gates for a Forms 6i claim

Do not change Forms 6i from **accepted for assessment** to **supported for conversion** until all of
these are true:

1. Authorized representative 6i source and an executable baseline are available.
2. The exact 6i patch level, platform, database version, NLS settings, and deployment model are known.
3. The Oracle normalization toolchain is reproducible, and its use and hosting have been confirmed
   against the customer's governing Oracle license and support terms.
4. Every module and dependency is normalized, compiled, hashed, and represented in the manifest.
5. Migration Assistant warnings and fleet findings are reconciled with no silent omissions.
6. The generated target compiles, deploys, and passes security and data-integrity tests.
7. Differential workflow tests pass across the selected construct matrix.
8. Unsupported constructs and manual remediation rates are published with the claim.

Until then, the correct statement is:

> The fleet can assess Oracle Forms 6i evidence and has a researched normalization path, but Forms 6i
> conversion compatibility has not yet been demonstrated.

## Primary Oracle sources

- [Oracle Forms and Reports 14.1.2 documentation](https://docs.oracle.com/en/middleware/developer-tools/forms/14.1.2/index.html)
- [Upgrading Oracle Forms 6i Applications, December 2024](https://docs.oracle.com/en/middleware/developer-tools/forms/14.1.2/upgrade-forms/index.html)
- [Preparing to Upgrade](https://docs.oracle.com/en/middleware/developer-tools/forms/14.1.2/upgrade-forms/preparing-upgrade.html)
- [Oracle Forms Migration Assistant](https://docs.oracle.com/en/middleware/developer-tools/forms/14.1.2/upgrade-forms/using-oracle-forms-migration-assistant.html)
- [Converting Forms 6i FMTs to current FMBs](https://docs.oracle.com/en/middleware/developer-tools/forms/14.1.2/upgrade-forms/steps-convert-forms-6i-fmts-latest-oracle-forms-fmbs.html)
- [Built-ins, packages, constants, and syntax](https://docs.oracle.com/en/middleware/developer-tools/forms/14.1.2/upgrade-forms/built-ins-packages-constants-and-syntax.html)
- [Triggers](https://docs.oracle.com/en/middleware/developer-tools/forms/14.1.2/upgrade-forms/triggers.html)
- [Properties](https://docs.oracle.com/en/middleware/developer-tools/forms/14.1.2/upgrade-forms/properties.html)
- [Client/server and runtime changes](https://docs.oracle.com/en/middleware/developer-tools/forms/14.1.2/upgrade-forms/changes-client-server-deployment-and-forms-runtime.html)
- [Client/server to Web guidance](https://docs.oracle.com/en/middleware/developer-tools/forms/14.1.2/upgrade-forms/upgrade-client-server-applications-web.html)
- [Pre-6i upgrade path](https://docs.oracle.com/en/middleware/developer-tools/forms/14.1.2/upgrade-forms/upgrade-forms-6i-applications.html)
- [Item types](https://docs.oracle.com/en/middleware/developer-tools/forms/14.1.2/upgrade-forms/item-types.html)
- [LOVs](https://docs.oracle.com/en/middleware/developer-tools/forms/14.1.2/upgrade-forms/list-values-lovs.html)
- [User exits](https://docs.oracle.com/en/middleware/developer-tools/forms/14.1.2/upgrade-forms/user-exits.html)
- [Java-related issues](https://docs.oracle.com/en/middleware/developer-tools/forms/14.1.2/upgrade-forms/java-related-issues.html)
- [Oracle Reports integration](https://docs.oracle.com/en/middleware/developer-tools/forms/14.1.2/upgrade-forms/integration-oracle-reports.html)