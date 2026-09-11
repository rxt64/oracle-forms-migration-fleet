# Oracle Forms Migration Fleet

A Microsoft Foundry **hosted agent** that designs, plans, and coordinates the Oracle Forms migration
lifecycle end to end: source
analysis and documentation, conversion of the Forms UI to **React** and its logic to **Java/Spring Boot**,
conversion of the Oracle database to **PostgreSQL** or the **SQL Server family** (SQL Server, Azure SQL
Database, Azure SQL Managed Instance), build and behavior validation, sandbox data migration,
reconciliation, human acceptance, and production cutover.

> **What runs inside this service today.** Everything in `Fleet/` is deterministic and offline. It assesses,
> plans, and gates. It does not start a process, write a generated file, connect to Oracle, PostgreSQL, or
> SQL Server, or move data. Work is performed by **execution adapters** outside this deterministic core, and
> a phase counts as done only when an adapter returns artifacts plus a matching successful attestation.

## Documentation

| Document | Read it when |
|---|---|
| [CONTRIBUTING.md](CONTRIBUTING.md) | Before your first change — branch naming, the PR loop, and what a change must include |
| [docs/SECURITY.md](docs/SECURITY.md) | Before touching source acquisition, classification, auth, or anything the browser can see |
| [docs/OPERATIONS.md](docs/OPERATIONS.md) | Deploying, rolling back, or debugging a deployment that misbehaved |
| [infra/legacy-estate/README.md](infra/legacy-estate/README.md) | Deploying the disposable synthetic Oracle database used by demonstrations |
| [infra/forms-demo/README.md](infra/forms-demo/README.md) | Running the browser-executable banking workflow replica against that database |
| [CHANGELOG.md](CHANGELOG.md) | Understanding why current behaviour differs from what you expected |

## Product boundary

| Layer | What it does | Where it lives |
|---|---|---|
| **Deterministic planning** | Validates requests, scores the target platform, sequences assessment stages, and authorizes lifecycle phases, owners, inputs, outputs, tooling, mutation class, and approval gates | `Fleet/` — pure C#, no network, fully unit-tested |
| **Local execution adapters** | Actually parse source, generate React/Java/database artifacts, build, test, and load the sandbox. **To be implemented and invoked outside the deterministic core.** | not in `Fleet/` |
| **Attestation-backed completion** | An adapter reports back a signed `MigrationAttestation` naming a signer and citing at least one valid workspace-relative artifact. Without one, the agent must say the phase is *planned*, never *performed*. | `MigrationAttestation`, gate logic in `MigrationRunPlanner` |

The assessment pipeline (`assess_oracle_forms_migration`) is unchanged and still produces plans only. The
execution lifecycle (`plan_oracle_forms_migration_run`) is a separate, additive contract.

## Architecture

The externally hosted service is a **single process**. Inside it, a model-backed outer agent fronts a
**deterministic specialist fleet**. The model handles intake dialogue and reporting; every stage
transition and the platform recommendation are computed by pure C# with no network, Azure, or model
dependency, and are exposed to the model as tools.

```
POST /responses
   │
   ▼
Outer agent  (Microsoft.Agents.AI · Foundry Responses protocol · AgentHost)
   │   instructions: FleetAgentInstructions.Build()
   │   tools:        assess_oracle_forms_migration
   │                 plan_oracle_forms_migration_run
   │                 describe_fleet_roles
   │                 describe_evidence_requirements
  │                 describe_migration_landscape
   ▼
MigrationFleetOrchestrator   ← deterministic, offline, unit-tested
   │
   ├── Intake                 · Orchestrator            · validates request, rejects credential material
   ├── InventoryAnalysis      · Inventory Analyst       · requires FormsModuleInventory
   ├── DependencyMapping      · Dependency Mapper       · requires DatabaseSchemaExport or PlSqlProgramUnit
   ├── TargetPlatformAdvisory · Target Platform Advisor · TargetPlatformAdvisor, explicit cited criteria
  ├── ConversionPlanning     · Conversion Planner      · blocks approval when source/discovery evidence is absent
   ├── ValidationReview       · Validation Reviewer     · rejects unresolved Critical findings
   └── HumanApproval          · Orchestrator            · only path to IsAccepted == true

MigrationRunPlanner          ← deterministic, offline, unit-tested
   │
   ├── SourceAcquisition          · Inventory Analyst          · None
   ├── SourceAnalysis             · Dependency Mapper          · None
   ├── DocumentationGeneration    · Documentation Author       · WorkspaceArtifactWrite
   ├── SourceNormalization        · Application Code Converter · WorkspaceArtifactWrite
   ├── ApplicationCodeConversion  · Application Code Converter · WorkspaceArtifactWrite  → React + Java/Spring Boot
   ├── DatabaseConversion         · Database Converter         · WorkspaceArtifactWrite  → SSMA or Ora2Pg by target
   ├── BuildAndStaticValidation   · Build and Test Engineer    · WorkspaceArtifactWrite
   ├── DifferentialBehaviorTesting· Build and Test Engineer    · SandboxDatabaseWrite    ← execution approval
   ├── SandboxDataMigration       · Data Migration Engineer    · SandboxDatabaseWrite    ← execution approval
   ├── DataReconciliation         · Reconciliation Analyst     · WorkspaceArtifactWrite  ← execution approval
   ├── HumanAcceptance            · Acceptance Coordinator     · None                    ← execution approval
   └── ProductionCutover          · Orchestrator               · ProductionWrite         ← production approval
                                                                                            + attestations
```

The pipeline halts at the first blocking stage. Stages after a halt never run and are absent from the
returned plan, so the output can never imply work that was not performed.

| File | Responsibility |
|---|---|
| `Fleet/FleetContracts.cs` | Immutable records and enums for request, evidence, findings, recommendation, stage status, and the final plan |
| `Fleet/MigrationRunContracts.cs` | Execution-lifecycle contracts: target stack, execution mode, lifecycle phase, mutation class, artifact reference, attestation, run request and run plan |
| `Fleet/MigrationRunPlanner.cs` | Deterministic lifecycle planner, workspace-path rules, target-specific tooling, and the execution/production gates |
| `Fleet/FleetRoleCatalog.cs` | Separate prompt/configuration definition per specialist role, for both the assessment stages and the execution lifecycle |
| `Fleet/MigrationFleetOrchestrator.cs` | Deterministic stage machine and `MigrationStageSequence` |
| `Fleet/TargetPlatformAdvisor.cs` | Azure SQL Database vs Managed Instance decision from explicit criteria |
| `Fleet/LegacyModernizationCatalog.cs` | Forms/runtime/integration risk signals, severities, evidence needs, and remediation tasks |
| `Fleet/MigrationLandscapeCatalog.cs` | Source-backed tool boundaries, exit strategies, vendor-claim labels, and mandatory human reviews |
| `Fleet/RequestValidator.cs` | Input validation and secret rejection |
| `Fleet/FleetGuardrails.cs` | Security boundaries, disclaimers, credential detection |
| `Fleet/FleetTools.cs` | The fleet exposed to the model as `AITool` instances |
| `Fleet/FleetAgentInstructions.cs` | Outer agent system prompt |

### Target platform decision

The advisor evaluates three explicit criterion sets, each citing the evidence identifiers it came from.

**Hard constraints — a validated requirement to preserve one in the database forces Azure SQL Managed Instance:**
`SqlAgentRequired`, `ClrRequiredInDatabase`, `ServiceBrokerRequired`,
`InstanceLevelCollationRequired`.

**Soft indicators — majority vote when no hard constraint applies:**
toward Managed Instance: `ScheduledDatabaseJobs`, `ClrOrExternalAssemblies`, `CrossDatabaseQueries`,
`DistributedTransactions`, `VnetIsolationRequired`, `LargeDatabaseFootprint`;
toward SQL Database: `SelfContainedSchema`, `PerDatabaseElasticScale`, `ServerlessCostSensitivity`.

**Platform-agnostic blockers — must be redesigned on either target:**
`DatabaseLinksInUse`, `FileSystemAccess`, `ExternalProcedureCalls`. These halt target advisory until
the redesign requirement is resolved; they do not advance to plan approval.

Confidence is `High` for a verified hard constraint, `Medium` for a clear verified soft-indicator
majority, and `Low` on a tie or when no verified `WorkloadProfile` evidence was supplied. Unverified
signals and signals attached to an unrelated evidence kind are ignored and recorded as assumptions.
`Insufficient` means no qualifying signals were extracted. An `Undetermined` result blocks
the pipeline; it is never reported as a recommendation.

## Request schema

The model calls `assess_oracle_forms_migration` with a `MigrationAssessmentRequest`:

| Field | Type | Notes |
|---|---|---|
| `engagementId` | string | Required. Audit identifier. |
| `applicationName` | string | Required. |
| `oracleFormsVersion` | string | Defaults to `unknown`. |
| `evidence[]` | `EvidenceItem` | `id`, `kind`, `source`, `summary`, `isVerified`, `signals[]`. |
| `businessConstraints[]` | string | Regulatory/downtime constraints. |
| `approval` | `HumanApproval` | `decision` (`Pending` \| `Approved` \| `Rejected`), `approverId`, `notes`. |

Evidence covers Forms source and XML exports; menus, PLLs, OLBs, and Reports; PL/SQL and schema;
integrations, identity, business processes, data profiling, regression baselines, cutover/rollback,
support/licensing, usage/business value, compliance, network, and workload profiling. Call
`describe_evidence_requirements` for the exact enum values accepted by the current agent version.

Even when the minimum pipeline evidence is present, the inventory stage reports missing
cross-functional evidence and the planner adds `CT-DISCOVERY`. Conversion planning remains
`BlockedOnEvidence`, and human approval is not reached, until those gaps are closed or formally waived.
A clean schema conversion percentage therefore cannot hide missing workflow, identity, integration,
test, cutover, support, or business-value analysis.

## Run request schema

The model calls `plan_oracle_forms_migration_run` with a `MigrationRunRequest`:

| Field | Type | Notes |
|---|---|---|
| `engagementId` / `applicationName` | string | Required. Audit identifiers. |
| `requestedMode` | `ExecutionMode` | `PlanOnly` \| `GenerateArtifacts` \| `SandboxMigration` \| `ProductionCutover`. The planner may authorize less. |
| `target` | `TargetStack` | `frontEnd` (React), `backEnd` (JavaSpringBoot), `database` (`PostgreSql` \| `SqlServer` \| `AzureSqlDatabase` \| `AzureSqlManagedInstance`). |
| `sourceRoot` / `outputRoot` | string | **Workspace-relative only.** Rooted, UNC, URI, and `..` traversal paths are rejected and produce a plan with no phases. |
| `evidence[]` | `EvidenceItem` | Same evidence model as the assessment request. |
| `planApproval` / `executionApproval` / `productionApproval` | `HumanApproval` | Three independent gates. They are never interchangeable. |
| `attestations[]` | `MigrationAttestation` | `kind`, `succeeded`, `attestedBy`, `artifacts[]`. Returned by execution adapters. An attestation only unlocks a gate when it cites at least one artifact whose path is workspace-relative and credential-free. |

Every phase in the returned plan carries its owner, required inputs, expected workspace-relative output
artifacts, tooling, mutation class, approval requirement, status, and blockers. Artifact references never
contain a credential, connection string, or URL.

### Gates

- **GenerateArtifacts** requires verified `FormsModuleSource` **or** `FormsXmlExport`, plus
  `PlSqlProgramUnit`, `DatabaseSchemaExport`, and `TestBaseline`. An inventory alone is not enough.
- **SandboxMigration** requires an `executionApproval` with `Approved` and an `approverId`.
  Assessment plan approval does **not** authorize execution.
- **ProductionCutover** requires a `productionApproval` separate from the execution approval, plus
  successful `SandboxMigrationCompleted`, `DataReconciliationPassed`, and `HumanAcceptanceSigned`
  attestations, each with an attesting identity and at least one backing artifact. Every cited artifact
  path must be workspace-relative, and credential-like paths, descriptions, signers, or summaries are
  rejected. The planner is pure, so it validates the reference, not the file on disk.

Generated artifacts are proposals: they require human review and acceptance before use. Planning a phase
in `PlanOnly` or `GenerateArtifacts` is not an approval and never stands in for the separate sandbox and
production approvals.

When a gate fails, `authorizedMode` is downgraded and the affected phases report
`BlockedOnEvidence`, `BlockedOnApproval`, or `BlockedOnAttestation`. Phases above the requested mode
report `NotRequested` rather than pretending to be blocked.

### Conversion ownership

- **Database only:** SSMA for Oracle on the SQL Server family; Ora2Pg on PostgreSQL. The planner selects
  one or the other from `target.database` and never both.
- **Forms UI and logic:** owned by the fleet's own conversion adapter plus a compiler-driven and
  AI-assisted repair loop over the generated React and Java sources. That adapter is specified and planned
  here, not implemented in this repository. Neither SSMA nor Ora2Pg converts Forms UI or runtime
  behavior, and the planner never lists them for `ApplicationCodeConversion`.

## Legacy exit strategy

Do not assume every Forms application should be rewritten. The fleet exposes these options through
`describe_migration_landscape` and requires evidence before choosing among them:

| Option | Best fit | Principal caution |
|---|---|---|
| Retire and archive | Low/no usage or records retained only for compliance | Preserve searchable records, legal hold, evidentiary integrity, access audit, and deletion policy |
| Replace with SaaS/package | Commodity process with a credible fit-to-standard product | Validate process fit, integrations, export rights, identity, compliance, lock-in, and total cost |
| Stabilize and upgrade | Unsupported Forms estate needs a safer bridge | Time-box it; an upgrade retains Forms and Oracle coupling |
| Encapsulate behind APIs | Consumers must be decoupled before replacement | Legacy runtime remains and can become a distributed monolith |
| Strangler by business capability | Large estate needs phased value and rollback | Define system-of-record ownership, coexistence sync, reconciliation, and compatibility contracts |
| Full replacement | Process and technology both require redesign | Highest delivery/cutover risk; demands characterization tests and rehearsed rollback |

### Tool boundaries

- **Oracle Forms 14.1.2 upgrade tooling** can stabilize old applications, including 6i-era sources.
  It is an upgrade path, not an exit to Azure SQL.
- **Forms2XML / Forms XML Converter and JDAPI** help extract module, menu, and object-library
  structure. They require compatible tooling and authoritative source; XML is not a behavioral test.
- **SQL Server Migration Assistant (SSMA) for Oracle** assesses and converts Oracle database schema
  and code, reviews type mappings, and migrates data. It does not convert Forms UI/runtime behavior;
  conversion reports still require remediation and testing, and SYS/SYSTEM schemas are excluded.
- **SSMA Tester** helps compare database objects and data. It does not prove end-to-end workflow,
  security, concurrency, performance, printing, or UX parity.
- **Azure Database Migration Service through SSMA** supports scaled one-time Oracle movement to
  Azure SQL as an offline preview scenario. Low-downtime/online cutovers require separately validated
  replication tooling and reconciliation controls.
- **Azure Migrate / GitHub Copilot modernization** can help with infrastructure discovery and
  supported ASP.NET/Java code. Current Microsoft documentation does not establish Oracle Forms code
  conversion support.
- **Oracle APEX** is a credible Forms modernization target when retaining Oracle is acceptable. It is
  not an Oracle-to-Azure-SQL exit and still requires UI/workflow redesign. Oracle desupported the old
  APEX Migration Workbench in APEX 21.1, so do not plan around that historical conversion wizard.
- **KodeSage, ORMIT OpenJava, PITSS, AuraPlayer, GAPVelocity/Forms2Net, Ispirer, and Pretius** are examples of commercial accelerators and vendor guidance.
  Their automation and conversion coverage are vendor claims: prove them on the hardest modules,
  shared libraries, desktop integrations, reports, and tests before procurement or planning credit.
  KodeSage's complexity-based automation, visual testing, timeline, cost/ROI, and production-support
  claims require reproducible customer evidence; the cited article says ORMIT testing and UAT remain manual.
  Pretius contributes useful technical prompts, but its timeline, cost, licensing, scale, and commercial
  claims require independent verification and it is not execution proof.
- **Open-source implementations** (`aoreshkov/oracle-forms-mcp`, Ora2Pg, `franklingjr/oracle-forms-migration`)
  are catalogued as `OpenSourceImplementation`. Each covers one narrow slice: Forms parsing/indexing,
  Oracle-to-PostgreSQL database conversion, or Object List Report to PL/SQL package generation. None of
  them is end-to-end Forms-to-React/Java conversion proof.
- **Oracle Forms MCP** is a candidate parser/indexer for `SourceNormalization` only. It needs JDK/JRE 21
  or later plus either the licensed Oracle `frmf2xml`/`frmcmp_batch` utilities via `ORACLE_HOME` or
  adjacent pre-converted XML/PLD input. The fleet does not redistribute the proprietary Oracle utilities,
  and the MCP never generates target code.
- **Workshop and reference estates** (Cognition workshop HRMS/workflow, SierraSystems reference,
  `patrickmonaco/formstools`, `armandoblanco/legacy-modernization-playbook`) are catalogued as
  `WorkshopReference`: illustrative, procedural, or historical only, never proof of a repeatable migration.
  The Armando Blanco playbook contributes pilot-first process, mapping, and parity guidance; it is not
  validated executable tooling.

### Reference estate status

| Estate | Status | Why |
|---|---|---|
| Cognition workshop HRMS/workflow | **Not imported** | Synthetic single-commit static source. Verified tree: 5 Forms XML, 2 PLLs as SQL, 1 menu as SQL, packages and triggers, schema and seed SQL. No `.fmb`, no runtime, no regression baseline, no setup runner, and no standalone LICENSE text. The README's MIT statement alone is not sufficient redistribution proof, so the fixture stays out of this repository pending explicit license text. Useful only as static-analysis or pilot input. |
| SierraSystems reference | **Not imported** | Ships `.fmb` modules, an Oracle schema with data, and Java/Quarkus plus React layers, but stays Oracle-backed and has no verified clear license. Illustrative architecture only, not reusable proof. |
| `patrickmonaco/formstools` | **Not imported** | Obsolete high-privilege APEX Forms Migration loader. Historical reference only. |
| `armandoblanco/legacy-modernization-playbook` | **Not imported** | Agent-facing process guidance: pilot-first sequencing, Forms-to-target mapping conventions, and parity checkpoints. Ships no runnable converter, so it is not validated executable tooling and not conversion proof. |

No reference estate above is treated as end-to-end conversion proof, and none is a substitute for a
customer-owned pilot with its own regression baseline.

### Pitfalls encoded as deterministic signals

The fleet creates cited findings and remediation tasks for client-side PL/SQL; trigger/navigation and
commit semantics; multi-record blocks; ENTER-QUERY; POST-QUERY; PLL/OLB coupling; Java Beans/PJCs;
WebUtil/OLE/Jacob; HOST commands; Oracle Reports; dynamic SQL; Oracle AQ; VPD/RLS; NLS and
empty-string/NULL semantics; locking/concurrency; legacy
authentication; inaccessible source; missing regression baselines; online cutover; coexistence;
retirement/archive candidates; and package-replacement candidates.

Planning also requires versioned global design rules plus form-level exceptions, and it creates
dependency-clustered migration-wave tasks. Expert-session recordings and vision comparison can add
runtime evidence, but they do not replace source analysis, data reconciliation, accessibility,
security, concurrency, performance, or user acceptance.

WebUtil/OLE/Jacob, HOST execution, VPD/RLS parity, and inaccessible authoritative source are
`Critical` and stop the plan at validation until remediated. This does not mean other `High` risks are
production-ready: identity, data mappings, generated code, workflow fidelity, performance, operations,
licensing, and rollback always require human acceptance.

### Research basis

Checked on **2026-09-09**. Product capabilities and support dates change; the URL and current product
documentation, not this snapshot, are authoritative.

- [Oracle Forms 14.1.2 documentation](https://docs.oracle.com/en/middleware/developer-tools/forms/14.1.2/index.html)
  and [6i upgrade guide (December 2024)](https://docs.oracle.com/en/middleware/developer-tools/forms/14.1.2/upgrade-forms/index.html)
- [SSMA for Oracle overview](https://learn.microsoft.com/sql/ssma/oracle/migrating-oracle-databases-to-sql-server-oracletosql)
- [Oracle to Azure SQL Database guide](https://learn.microsoft.com/azure/azure-sql/migration-guides/database/oracle-to-sql-database-guide)
  and [Oracle to SQL Managed Instance guide](https://learn.microsoft.com/azure/azure-sql/migration-guides/managed-instance/oracle-to-managed-instance-guide)
- [DMS supported scenarios](https://learn.microsoft.com/azure/dms/resource-scenario-status)
  and [migration tools matrix](https://learn.microsoft.com/azure/dms/dms-tools-matrix)
- [Azure SQL Database and Managed Instance feature comparison](https://learn.microsoft.com/azure/azure-sql/database/features-comparison)
- [Azure Migrate code insights prerequisites](https://learn.microsoft.com/azure/migrate/add-copilot-code-insights)
- [Oracle APEX modernization overview](https://blogs.oracle.com/apex/post/modernizing-oracle-forms-using-oracle-apex)
- [Oracle APEX 21.1 release notes: Migration Workbench desupported](https://docs.oracle.com/en/database/oracle/application-express/21.1/htmrn/)
- [KodeSage Oracle Forms migration article](https://kodesage.ai/blog/oracle-forms-migration) — used only
  for vendor-claim cataloging and practical Forms UI risk prompts; commercial performance, cost,
  timeline, ROI, automation, and support claims are not treated as independently verified facts.
- [aoreshkov/oracle-forms-mcp](https://github.com/aoreshkov/oracle-forms-mcp) — Apache-2.0 Kotlin/JVM
  Forms parser and indexer. Authority: open-source implementation. Requires JDK/JRE 21+ and licensed
  Oracle `frmf2xml`/`frmcmp_batch` via `ORACLE_HOME`, or adjacent pre-converted XML/PLD. Generates no
  React or Java and migrates no database.
- [Ora2Pg](https://github.com/darold/ora2pg) — GPL-3.0 Oracle-to-PostgreSQL assessment, schema/data
  conversion, partial PL/SQL conversion, and validation aid. Authority: open-source implementation.
  Manual remediation required; no Forms UI capability. Also listed as PostgreSQL database tooling above.
- [franklingjr/oracle-forms-migration](https://github.com/franklingjr/oracle-forms-migration) — MIT
  utility that reads Forms Object List Reports and emits an Oracle PL/SQL package for APEX-oriented
  work. Authority: open-source implementation. Limited fallback when Forms XML is unavailable; not a
  React/Java path and not an Oracle database exit.
- [Cognition workshop HRMS/workflow estate](https://github.com/Cognition-Partner-Workshops/ts-plsql-oracle-forms-hrms)
  — reviewed locally. Authority: workshop reference. Not imported; see **Reference estate status**.
- [SierraSystems Oracle Forms reference application](https://github.com/SierraSystems/Oracle-Modernization)
  — reviewed locally; no verified clear license. Authority: workshop reference. Not imported;
  Oracle-backed and not reusable proof.
- [patrickmonaco/formstools](https://github.com/patrickmonaco/formstools) — obsolete high-privilege APEX
  Forms Migration loader. Authority: workshop reference. Historical context only.
- [armandoblanco/legacy-modernization-playbook — Oracle Forms migration agent](https://github.com/armandoblanco/legacy-modernization-playbook/blob/main/.github/agents/java/oracle-forms-migration.agent.md)
  — pilot-first process, mapping, and parity guidance. Authority: workshop reference. It is not validated
  executable tooling and is not end-to-end conversion proof.
- [Pretius: migrating Oracle Forms](https://pretius.com/blog/migrating-oracle-forms) — vendor guidance.
  Authority: vendor claim. Its technical prompts about triggers, blocks, reports, and target-stack choice
  are useful for risk enumeration, but timeline, cost, licensing, scale, and commercial claims require
  independent verification, and the article is not execution proof.

### Example

```json
{
  "engagementId": "ENG-4471",
  "applicationName": "ORDERS",
  "oracleFormsVersion": "12c",
  "evidence": [
    { "id": "EV-INV",     "kind": "FormsModuleInventory", "source": "forms-inventory.csv", "summary": "142 modules, 1,908 triggers.", "isVerified": true },
    { "id": "EV-PLSQL",   "kind": "PlSqlProgramUnit",     "source": "plsql-units.sql",     "summary": "312 packages and procedures.", "isVerified": true },
    { "id": "EV-SCHEMA",  "kind": "DatabaseSchemaExport", "source": "schema.dmp.manifest", "summary": "Schema object manifest.",      "isVerified": true },
    { "id": "EV-PROFILE", "kind": "WorkloadProfile",      "source": "awr-summary.txt",     "summary": "Peak 2.1k TPS, DBMS_SCHEDULER jobs present.",
      "isVerified": true, "signals": ["ScheduledDatabaseJobs", "VnetIsolationRequired"] }
  ],
  "businessConstraints": ["Maximum 4-hour cutover window"],
  "approval": { "decision": "Pending" }
}
```

That request treats `ScheduledDatabaseJobs` as a redesignable Managed Instance indicator, not a hard
constraint. It also lacks Forms source and cross-functional discovery evidence, so it produces
planning tasks and halts at `ConversionPlanning` with `BlockedOnEvidence`. A validated requirement to
retain SQL Agent scheduling in the database must use `SqlAgentRequired`; external scheduling remains
an alternative that can keep Azure SQL Database viable.

## Prerequisites

1. **[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)** or later.
2. **Azure Developer CLI (`azd`)** — [install](https://learn.microsoft.com/en-us/azure/developer/azure-developer-cli/install-azd), then `azd ext install microsoft.foundry`.
3. A Foundry project with a deployed chat model, and `azd auth login` completed, is required for the
  model-backed `/responses` endpoint. The deterministic operator workbench runs without either.
4. For the model-backed endpoint, copy `src/oracle-forms-migration-fleet/.env.example` to `.env` and
  set `AZURE_OPENAI_ENDPOINT` and `AZURE_AI_MODEL_DEPLOYMENT_NAME` when running without `azd`.
  `FOUNDRY_PROJECT_ENDPOINT`, `AZURE_OPENAI_ENDPOINT`, and
  `APPLICATIONINSIGHTS_CONNECTION_STRING` are injected from the azd environment in hosted containers.
5. RBAC: the developer or hosted identity needs **Cognitive Services OpenAI User** on the Foundry
  account to invoke the deployed model. Local runs use `AzureDeveloperCliCredential`; hosted runs
  fall back to the project's system-assigned managed identity.

`.env` and the `.azure/` environment directory are git-ignored and must never be committed.

## Build, test, run

```bash
cd src/oracle-forms-migration-fleet/ClientApp
npm ci
npm run build
cd ../../..
dotnet build
dotnet test                                  # offline: no Azure or model access required
azd ai agent run --no-client                 # local host on http://localhost:8088
azd ai agent invoke --local "Assess ENG-4471 for ORDERS."
```

Run `npm ci` before `dotnet build`: the csproj regenerates the client only when
`ClientApp/node_modules` already exists, and `wwwroot/` is generated and git-ignored, so a fresh clone
that skips it builds cleanly and then serves an empty page.

Or without `azd`:

```bash
cd src/oracle-forms-migration-fleet
dotnet run
curl -X POST http://localhost:8088/responses \
  -H "Content-Type: application/json" \
  -d '{"input": "What evidence do you need to assess an Oracle Forms application?", "stream": false}'
```

When the two Azure OpenAI settings are absent, `dotnet run` still serves `/` and
`/api/workbench/*`; `/responses` returns `503 Foundry model is not configured`. This offline mode
does not create a credential or make an authenticated Azure call.

Continue a conversation by passing the previous response id as `previous_response_id`.

In VS Code with the [Foundry Toolkit](https://marketplace.visualstudio.com/items?itemName=ms-windows-ai-studio.windows-ai-studio)
and [C# Dev Kit](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.csdevkit), press **F5**
to start the agent and open the Agent Inspector.

### Operator workbench GUI

The same process serves a React 19 and TypeScript operator console. Start the host, then open
<http://localhost:8088/>. Vite builds `ClientApp` into the fixed `wwwroot/index.html`,
`wwwroot/styles.css`, and `wwwroot/app.js` paths consumed by the host; there are no runtime CDN
dependencies. The generated `wwwroot` files remain committed so Foundry code deployment does not
require Node.js. After `npm ci`, normal local .NET builds rebuild the client only when its inputs are
newer than those outputs. The Dockerfile uses a dedicated Node build stage.

The console walks six macro steps — **Analyze source · Plan modernization · Convert application ·
Transform database · Migrate and validate · Cut over destination** — which together own all twelve
`MigrationPhase` values exactly once. Selecting a step filters the phase list. Step state
(`Current`, `Ready`, `Blocked`, `Planned`) is derived from a real plan, never simulated.

| Endpoint | Purpose |
| --- | --- |
| `GET /` | Static operator console from `wwwroot`. |
| `GET /api/workbench/bootstrap` | Macro steps, lifecycle ownership, evidence kinds, Azure destinations, execution modes, Azure component readiness, topology, execution boundary. |
| `POST /api/workbench/plan` | Calls `MigrationRunPlanner.Plan` and returns the plan plus the macro-step projection. |
| `POST /api/workbench/agent` | Proxies one non-persistent message to the allowlisted Foundry hosted-agent Responses endpoint when `FOUNDRY_AGENT_ENDPOINT` is configured; otherwise an explicit `503`. |
| `POST /responses` | Foundry Responses protocol endpoint when the model is configured; otherwise an explicit `503`. |

Enums cross the API as strings. The console persists only controlled selections (destination,
execution mode, and evidence toggles) to `localStorage`. Free-text identifiers, paths, approver
identities, attestations, and secrets are never persisted.

### Azure component boundary

| Component | State | Why |
| --- | --- | --- |
| Microsoft Foundry hosted agent | **Active when connected** | The local model-backed endpoint uses `AddFoundryResponses`/`MapFoundryResponses`. The Container Apps workbench instead proxies to an allowlisted hosted-agent Responses endpoint without exposing that endpoint or Azure credentials to the browser. |
| Azure Container Apps and ACR | **Implemented in `infra/workbench`** | The Bicep stack provisions the public web host, registry, environment, diagnostics, and image pull path. Provisioning still requires an authorized Azure deployment identity. |
| Managed identity | **Implemented in `infra/workbench`** | A user-assigned identity receives only ACR pull and Foundry Agent Consumer at the existing project scope. The backend uses it for Foundry calls. |
| Application Insights | **Implemented in `infra/workbench`** | The deployment provisions Application Insights and injects its connection string into the Container App. The value is never returned by an API. |
| Microsoft Entra ID | **Implemented in `infra/workbench`** | The deployment script creates or updates a dedicated single-tenant app registration and Container Apps built-in authentication. Anonymous readiness remains available for probes. |
| Azure Key Vault | Planned | No Key Vault client; the console never handles a secret value. |
| Azure Blob Storage | Planned | The planner emits workspace-relative artifact paths and writes no file. |
| Azure SQL Database / Azure SQL Managed Instance / Azure Database for PostgreSQL | Planned | Selectable destinations only. No driver or connection string is used anywhere. |
| Migration execution adapters | Planned | Designed and gated here, not implemented. Adapter-dependent steps show **Adapter not connected** and their run command stays disabled. |

### Local Foundry smoke evaluation

The checked-in seed dataset at
`src/oracle-forms-migration-fleet/.foundry/datasets/oracle-forms-migration-fleet-eval-seed-v1.jsonl`
contains Foundry-ready `query` and `expected_behavior` rows for evidence gating, target selection,
tool authority, Forms UI semantics, critical legacy risks, secret rejection, and non-execution.
Use its prompts in Agent Inspector while the local host is running. The `tools` and `signals` fields
are local coverage metadata that keep the dataset aligned with the deterministic fleet. Rows that
expect a specific assessment outcome also include an `assessment` fixture: offline tests bind every
signal to its authoritative evidence kind, execute the fixture through the orchestrator, and verify
the expected target, confidence, final stage, status, acceptance state, and required task identifiers.

Validate the dataset without Azure or model inference:

```bash
dotnet test --filter "FullyQualifiedName~EvalDatasetTests"
```

The offline validator checks JSONL structure, registered tool names, workload-signal names, every
Critical legacy risk, executable platform and stage outcomes, authority labels, and safety boundaries.
A model-backed Foundry evaluation remains a separate step because it requires available deployment
capacity.

## Deploy

Deploy the Foundry hosted agent with `azd`:

```bash
azd deploy
azd ai agent invoke "Assess ENG-4471 for ORDERS."
```

See [Deploy a hosted agent](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/deploy-hosted-agent).

Deploy the authenticated React workbench to Azure Container Apps with the separate
`infra/workbench` stack. Preview first; the preview is read-only and prints resource type/name changes
without expanded resource properties. These scripts require PowerShell 7.2 or later:

```powershell
.\infra\workbench\Preview-WorkbenchInfrastructure.ps1 `
  -ResourceGroupName '<resource-group>'

.\infra\workbench\Deploy-Workbench.ps1 `
  -ResourceGroupName '<resource-group>' `
  -FoundryAgentEndpoint 'https://<account>.services.ai.azure.com/api/projects/<project>/agents/<agent>/endpoint/protocols/openai/responses?api-version=v1'
```

The deployment identity needs permission to create resources and role assignments at the target
resource group, such as **Contributor** plus **User Access Administrator**, and permission to create
or update the dedicated Entra application. The web identity itself receives neither those deployment
roles nor access to Storage, Key Vault, Oracle, or a destination database.

`dev.bicepparam` supplies the Entra object IDs authorized to open the confidential workbench through
`operatorPrincipalObjectIds`; update that allowlist before deploying to another environment. The
deployment creates a new Easy Auth credential without deleting the previous one. Remove superseded
credentials only after the authenticated browser login and callback have been smoke-tested, so a
failed revision cannot invalidate the last working sign-in path. The script reports each retained key
ID and refuses to create a third retained workbench credential. After a successful smoke test, remove
the superseded credential explicitly:

```powershell
$appId = az ad app list `
  --display-name 'Migration Fleet Workbench - dev' `
  --query '[0].appId' `
  --output tsv
az ad app credential delete --id $appId --key-id '<retained-key-id>'
```

This deployment makes the planning workbench and hosted-agent consultation functional. It does not
connect an execution adapter: source extraction, React/Java generation, database conversion, data
movement, reconciliation, and cutover remain blocked until separately implemented adapters return
the required artifacts and attestations.

## Limitations

- **No migration is performed.** No connection is made to Oracle, Azure SQL Database, or Azure SQL
  Managed Instance. Nothing is read from or written to a source or target system.
- **No executable SQL or migration scripts are generated.** The planner emits tasks only. When Forms
  source evidence (`FormsModuleSource` **or** `FormsXmlExport`), `PlSqlProgramUnit`, or
  `DatabaseSchemaExport` evidence is absent, it also records the gap as a blocker.
- **Evidence is taken as attested, not independently verified.** The service does not parse `.fmb`
  files. It ignores platform signals attached to unrelated artifact kinds; `isVerified` remains a
  human attestation; unverified artifacts do not satisfy evidence gates or drive recommendations.
- **Forms2XML/JDAPI, SSMA, DMS, and vendor outputs must be supplied by an operator.** This hosted
  service does not install those products, connect to source systems, or execute their output.
- **Static extraction is not process discovery.** Trigger order, navigation, informal workarounds,
  printer/device use, batch timing, and exception handling require observation and business-owner review.
- **Vendor claims are not guarantees.** Conversion percentages, complexity labels, AI dependency
  analysis, visual comparisons, timelines, cost/ROI estimates, and production-support claims do not
  establish maintainability, security, performance, operational readiness, or functional acceptance.
- **Findings are only as complete as the supplied signals.** A dependency that was never signalled
  cannot be detected.
- **Feature parity is not tracked at runtime.** Azure SQL Database and Managed Instance capabilities
  change; re-confirm the recommendation against current Microsoft documentation before execution.
- **Effort and risk levels are relative rankings**, not estimates in hours or cost.
- **No autonomous production changes.** `MigrationPlan.IsAccepted` is only ever true after all
  evidence blockers are closed and a human plan-approval decision carries an approver identity.
  Plan approval is not production acceptance; production readiness is a separate workstream.
- **Secrets are rejected, not redacted.** The chat-client pipeline returns a fixed refusal before
  credential-like chat content is delegated to model inference. Structured assessment fields are
  also checked by intake validation. Neither path echoes the detected value.
- **Conversation history** is in-process locally and durable only when hosted by Foundry. A local host
  may still persist its own `.checkpoints/` state on disk between restarts; that state is git-ignored,
  is not an audit trail, and is never evidence that a phase ran.
