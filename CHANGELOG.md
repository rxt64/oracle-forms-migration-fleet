# Changelog

Notable changes to behaviour, with the reasoning that is not visible in a diff.

## Unreleased

### Added

- Compiler-driven PL/pgSQL repair (`Fleet/Execution/ProgramUnitRepairLoop.cs`). PostgreSQL deployment
  failures now retain the rejected statement with its diagnostic and enter a bounded two-attempt repair
  cycle during the execution-approved sandbox phase. Model output may change only a routine's dollar-quoted
  body; routine identity, parameters, return contract, language, security clauses, and all surrounding DDL
  must remain unchanged. PostgreSQL compilation, not the model, accepts a revision.

  Accepted revisions and an audit report are returned as artifacts and survive workbench reruns. Every reuse
  is compared with the newly generated routine and recompiled before it is trusted. Source ingestion removes
  supplied `.fleet-run` state so an uploaded archive or repository cannot pre-seed an accepted repair.
  Partially accepted sets are retained and merged across runs. If any routine remains rejected, valid rows
  are still loaded but the phase fails and cannot produce `SandboxMigrationCompleted`.

- Application tier generation (`Fleet/Execution/ApplicationCodeEmitter.cs` and its adapter).
  `ApplicationCodeConversion` had no adapter, so every run reported the phase as unperformed and a run
  converted the schema while leaving the application on Oracle. It now emits JPA entities, Spring Data
  repositories, REST controllers, a typed React client and screen, and a Spring Boot build bound to Azure
  Database for PostgreSQL with Entra authentication. Oracle does not appear in the generated data path.

  What it refuses to do is stated in its own output: `.fmb` modules are counted and reported unread rather
  than guessed at, PL/SQL units are listed as untranslated, and every endpoint is declared unauthenticated.
  The phase produces no attestation, because code that has never been compiled is not working software.

- Run export (`GET /api/workbench/export`). Session workspaces are swept after four hours, so a completed
  migration used to evaporate. The console now offers the run output as a zip. Only `.fleet-run` is
  exportable: the acquired source copy is the customer's code and never leaves in an export, and entry
  names are relative so an archive cannot carry a rooted or traversing path to whoever opens it.

- Artifact review agent (`Fleet/Execution/ArtifactReview.cs`). After the schema conversion writes its DDL, an
  optional `IArtifactReviewer` reads that DDL and reports suspected defects a rules engine did not anticipate.
  Findings go to `model-review.md` and are prefixed `Advisory:` in the phase findings. They cannot open a gate,
  sign an attestation, or alter the deterministic conversion report, so a prompt-injected or wrong reviewer can
  only add noise to a section labelled unverified.

  First measured run against the Trisha11r banking schema: two claims, one correct (a foreign key to a table no
  `CREATE TABLE` defines) and one false positive (it reported a missing unique constraint on a column the same
  DDL declares `PRIMARY KEY`). It did not detect that `CHECK (LENGTH(WorkPhone) = 10)` cannot execute, because
  PostgreSQL has no `length(bigint)`. Useful as a lead generator; not yet trustworthy as a reviewer.

- Per-agent model deployments. Conversation and tool calling stay on `gpt-5.4-mini`; artifact review moved to a
  `gpt-5.6-sol` deployment selected by `AZURE_AI_REVIEW_MODEL_DEPLOYMENT_NAME`, falling back to the conversation
  model when unset. On the same banking schema the review model found all four `length(bigint)` CHECK
  constraints that will not execute, each with the `::text` cast that fixes it, plus nineteen Oracle/PostgreSQL
  behaviour differences with concrete DDL. The mini model found none of them.

- Attribution in the console. Each phase card now states what performs it — deterministic code, deterministic
  code followed by a model review, or nothing at all — and names the deployment when a model is involved. A
  section lists both model capabilities with their deployments, and says plainly that specialist role names
  label ownership rather than dispatch to independent agents.

- Azure footprint disclosure on every plan (`Fleet/AzureFootprint.cs`). Lists the resources a run would need and
  which it would create, the role assignments the deploying identity requires and at what scope, and the
  cross-tenant sign-in model. Pure and offline: it contacts nothing. `Contributor` is scoped to a single
  resource group and the only subscription-scoped role is `Reader`, both enforced by test.

  No deploy button ships with it. No adapter in this build provisions an Azure resource, so a customer-tenant
  sign-in could not deploy anything; adding one before the consent, scoping, and audit model is agreed would
  create an unreviewed write path into a customer subscription.

### Fixed

- The generation gate no longer blocks a phase on evidence that phase does not read. Converting the Northstar
  Oracle schema was refused for want of Forms `.fmb` binaries, which a schema conversion never opens. Each
  phase is now gated on what it consumes: `DatabaseConversion` needs the schema export and PL/SQL units,
  source analysis needs source, and `TestBaseline` is required by the phases that make a behavioural claim
  rather than by DDL emission into a workspace.

  This is a security fix, not a relaxation. A gate demanding irrelevant evidence gets defeated by ticking the
  box falsely, and a false attestation in an auditable plan is the outcome the gate exists to prevent.

- A review that could not run no longer reads as a review that found nothing. Unparseable output and a failed
  model call now write "The review did not run" with the reason, instead of "returned no findings".
- An unrecognised severity word no longer discards the whole review. `gpt-5.6-sol` answered `"Error"` for a
  correct finding and strict enum parsing threw away every finding in the reply; severities now map leniently.
- The deterministic findings passed to the reviewer were framed as "do not repeat", which suppressed exactly the
  constructs where the model adds value: the converter had already flagged those CHECK constraints as needing
  review, so the model stayed silent about them. They are now framed as a list to adjudicate.

**The fleet can now execute the phases it authorizes.** `Fleet/Execution/` adds an Oracle DDL parser, a
PostgreSQL emitter, a phase-adapter framework, and a workbench execution API with a browser action, so a
plan can be carried out rather than only read. Two adapters ship: source analysis and Oracle-to-PostgreSQL
schema conversion. The planner remains authoritative — the executor calls it and runs only phases resolved
to `Planned`, so the browser cannot forge an authorization. Artifacts are written into the operator's
private session workspace under `.fleet-run`, never into the acquired source copy and never into the
customer's repository.

PL/SQL bodies are deliberately **not** translated. Packages, procedures, functions, triggers, `%ROWTYPE`,
and `STANDARD_HASH` are reported as manual PL/pgSQL rewrites with reasons, because emitting plausible but
unverified PL/pgSQL is worse than emitting nothing. Adapters also emit no attestation for analysis or
conversion: the defined attestation kinds all unlock the sandbox or production gate, and signing one for a
schema conversion would be a false statement.

### Fixed

**"Plan only" could authorize a phase that writes files.** `SourceAnalysis` declared five output artifacts
while being classified `MutationClass.None` at `ExecutionMode.PlanOnly`. Once an adapter existed to carry
the phase out, that combination would have written files for an operator who explicitly asked for a plan
only. It now requires `GenerateArtifacts`, restoring the invariant that plan-only authorizes nothing
mutating.

### Added

**A deployable Oracle Forms workflow replica now exercises the reference estate.** The upstream case
study publishes design-time `.fmb` modules but no compiled `.fmx`, licensed Forms runtime, WebLogic
domain, or deployment package, and declares no source license. Rather than claim those modules are
running, the new Northstar Online Banking app independently implements the published account-opening,
online-registration, interest, statement, transaction, customer-login, and manager-approval workflows.
Its .NET backend connects server-side to the internal disposable Oracle estate; the browser never
receives the database host or credential. The UI identifies itself as a workflow replica, and no
upstream source or binary artifact is copied into this repository.

### Fixed

**Build descriptors were counted as Oracle Forms XML exports.** `SourceInventory` classified any `.xml`
whose *path* contained `form`, so in a repository with a directory named `OracleFormsTester`, Ant's
`build.xml` was reported as a Forms2XML export. That is not cosmetic: detected artifacts tick evidence
boxes, and evidence gates whether artifact generation is authorized, so the planner was being handed
evidence that did not exist. Classification now matches on the file name or an exact forms directory
segment, and ignores a list of well-known build and framework descriptors. Two tests cover it — one for
the misclassification, one asserting genuine Forms XML is *still* detected, so the fix cannot silently
become "the rule is off".

**Auto-detected evidence ticks were never cleared.** Ticks derived from a copied source were only ever
added to the operator's selections and persisted to local storage, so ticks from an earlier, unrelated
source survived into a new session and could satisfy the generate-versus-plan-only gate on their own.
Detected ticks now carry provenance: they are bound to the live workspace and retired when the source is
replaced or discarded, while ticks the operator made themselves persist untouched. Detected kinds are
also de-duplicated before being counted, so three files of one kind no longer claim three answers.

**An expired sign-in surfaced as "Failed to fetch".** When the auth cookie lapsed, the API call was
answered with a redirect to the login service, which the browser blocked as cross-origin. That rejects
the `fetch` promise itself, so the `response.ok` check never ran and the raw `TypeError` reached the UI.
The client now distinguishes that case and tells the operator their session expired and to reload, and
also treats 401 and 403 the same way.

### Known limitation

**The repository subfolder is not applied to the clone.** The folder entered on step 1 reaches the
*plan*, but never reaches the clone request, so the whole repository is copied and inventoried. In
testing, entering `demoapp` still copied 100 files. The detected `sourceRoot` was nonetheless correct,
because it is derived from where the Forms files actually live rather than from the entered value.
Closing this properly means extending the clone contract and adding sparse checkout; it was left open
deliberately rather than changed unilaterally.

### Verified

Exercised end to end against a real third-party repository (`v-p-b/oracle_forms`) rather than a
synthetic fixture. That repository is a security-testing toolkit rather than a Forms application — about
100 Java files of Burp interface stubs surrounding a single genuine `demoapp/` — which made it a useful
adversarial test, and is what exposed the misclassification above.

The safety gate behaved correctly: asked for **Generate artifacts**, the workbench authorized **Plan
Only** and cited 11 blockers naming the specific missing evidence (`DatabaseSchemaExport`,
`TestBaseline`).
