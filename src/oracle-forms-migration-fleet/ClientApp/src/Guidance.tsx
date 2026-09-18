/**
 * Single registry for every piece of operator-facing help in the workbench.
 *
 * Help copy lives here rather than beside each control so coverage can be audited in one place:
 * a field, group, choice, action, status, metric or navigation entry either has an entry here or
 * it has no help at all. Nothing in this file asserts that a capability exists; the wording only
 * describes what the implemented API already does.
 */

export type GuidanceCategory =
  | "step"
  | "field"
  | "group"
  | "choice"
  | "action"
  | "status"
  | "metric"
  | "navigation"
  | "glossary";

export interface GuidanceEntry {
  readonly category: GuidanceCategory;
  readonly term: string;
  readonly body: string;
}

/** The plain-language frame every setup step repeats: goal, input, what runs, what comes out. */
export interface StepGuidance {
  readonly id: string;
  /** Operator-facing step name. The backend step order is unchanged. */
  readonly name: string;
  readonly railSummary: string;
  readonly heading: string;
  readonly goal: string;
  readonly input: string;
  readonly platform: string;
  readonly output: string;
}

export const STEP_GUIDANCE = [
  {
    id: "application",
    name: "Your application",
    railSummary: "Name it and point at the code",
    heading: "Which application are you moving?",
    goal: "Identify the Oracle Forms application and tell the workbench where its code is.",
    input: "A label you will recognise later, the application name, and either a Git address, a zip file, or a folder path. Example: ENG-0042 / ORDERS / legacy/forms.",
    platform: "For Git or zip the server copies the files into a private folder for this browser session and locks the copy read-only. A typed folder path copies nothing.",
    output: "A named setup, and — for Git or zip — a file count and a list of the Oracle artifact types the indexer recognised.",
  },
  {
    id: "destination",
    name: "Azure destination",
    railSummary: "Choose the Azure database",
    heading: "Where should the migrated application land?",
    goal: "Choose the Azure database the plan targets and how far ahead the plan should reach.",
    input: "One database option and one planning depth. The application route is fixed: a React front end and a Java Spring Boot back end.",
    platform: "The workbench records your choice and derives proposed resource names from the application name. It signs in to no subscription and creates nothing.",
    output: "A target recorded in the plan, with proposed resource names and the Azure services the plan would need.",
  },
  {
    id: "checklist",
    name: "Source checklist",
    railSummary: "Declare what you already have",
    heading: "What source material do you already have?",
    goal: "Declare which exports and documents exist so the plan can say what is missing.",
    input: "A tick against each item you have produced and checked yourself. Leaving an item unticked is a valid answer.",
    platform: "Ticks are treated as operator-declared inputs. The workbench verifies none of them; where a copied source was indexed, matching items are pre-ticked and can be changed.",
    output: "A requirement count, and a blocker in the plan for every requirement left unmet.",
  },
  {
    id: "permissions",
    name: "Execution permissions",
    railSummary: "Record who would approve",
    heading: "Who would approve changes?",
    goal: "Record the people who would authorise a sandbox run and a production release.",
    input: "Optional names or email addresses. Sandbox and production must be different people.",
    platform: "The names are sent as pending planning notes. Nobody is contacted, no identity is checked, and no write is authorised by typing a name.",
    output: "A review summary of the two planning contacts, or blank values if you leave them empty.",
  },
  {
    id: "review",
    name: "Review & plan",
    railSummary: "Read it back, then generate",
    heading: "Check the setup, then generate the plan",
    goal: "Confirm the setup reads correctly and generate the written migration plan.",
    input: "Nothing new — this step reads back the answers from the previous four.",
    platform: "The deterministic planner decides which phases it can authorise and lists every blocker. Generating a plan writes no file and changes nothing.",
    output: "A plan covering the six migration stages, the authorised phases, and the blockers still outstanding.",
  },
] as const satisfies readonly StepGuidance[];

export type StepIndex = 0 | 1 | 2 | 3 | 4;

const ENTRIES = {
  // ---------------------------------------------------------------- navigation
  "nav.overview": {
    category: "navigation",
    term: "Overview",
    body: "The starting page: what this workbench does, what it needs from you, and what it will not do.",
  },
  "nav.setup": {
    category: "navigation",
    term: "Migration setup",
    body: "The five setup steps. Answers are held in this browser tab only; there is no saved project on the server.",
  },
  "nav.activity": {
    category: "navigation",
    term: "Activity",
    body: "The transcript of the operation running now, or the last one this tab ran. It contains only lines the server sent.",
  },
  "nav.results": {
    category: "navigation",
    term: "Plan & results",
    body: "The generated plan, and anything an authorised run produced. Available once you have generated a plan in this tab.",
  },

  // -------------------------------------------------------------------- fields
  "field.engagementId": {
    category: "field",
    term: "Reference for this plan",
    body: "Any label that helps you recognise this plan later — a ticket number or a project code. Example: ENG-0042.",
  },
  "field.applicationName": {
    category: "field",
    term: "Application name",
    body: "The name your team uses for the Oracle Forms application. It becomes the plan title and the basis of the proposed Azure resource names. Example: ORDERS.",
  },
  "field.sourceRoot": {
    category: "field",
    term: "Folder holding the Forms files",
    body: "The folder a developer would find the .fmb files in, written relative to the top of your repository. Example: legacy/forms. Typing a path copies nothing and connects to nothing.",
  },
  "field.outputRoot": {
    category: "field",
    term: "Output folder",
    body: "Where generated Java, React and SQL would be written when an authorised phase runs. Example: out/orders. It is created inside your session workspace, never in your repository.",
  },
  "field.repoUrl": {
    category: "field",
    term: "Repository address",
    body: "Copy it from your browser's address bar while looking at the repository. Only public repositories on GitHub, Azure DevOps, GitLab and Bitbucket can be copied. Never paste a token or a password.",
  },
  "field.repoBranch": {
    category: "field",
    term: "Branch",
    body: "Leave empty to copy the repository's default branch. Example: main.",
  },
  "field.repoFolder": {
    category: "field",
    term: "Folder inside the repository",
    body: "Leave empty and the workbench uses the folder it found the Forms files in. Example: legacy/forms.",
  },
  "field.zipFile": {
    category: "field",
    term: "Zip upload",
    body: "Export your repository as a zip, or zip the folder holding the Forms files. Up to 256 MB. The archive is expanded into the same private session folder and locked read-only.",
  },
  "field.oracleFormsVersion": {
    category: "field",
    term: "Oracle Forms release",
    body: "The Forms release the application was built with, if you know it. Leaving it unestablished is recorded as such rather than guessed. Choosing a release unlocks nothing: .fmb, .mmb, .pll and .olb files are a proprietary binary, so a Forms XML export produced by your own Oracle tooling is still required before an application tier can be generated. Forms 6i additionally carries Oracle's recommendation to bridge through 10.1.2.",
  },
  "field.oracleDatabaseVersion": {
    category: "field",
    term: "Oracle Database release",
    body: "The database release behind the SQL you supply. The workbench converts the SQL text either way; recording the release lets the conversion report say which release that text came from. It connects to no Oracle instance, so it cannot confirm this for you.",
  },
  "field.executionApprover": {
    category: "field",
    term: "Sandbox approver",
    body: "The person who would authorise changes in a sandbox or test environment. Typing a name records a contact in the plan. It does not sign in as that person and does not authorise any write.",
  },
  "field.productionApprover": {
    category: "field",
    term: "Production approver",
    body: "The person who would authorise a production release. Must be someone other than the sandbox approver. This is plan metadata only; production release is not available from this workbench.",
  },

  // -------------------------------------------------------------------- groups
  "group.sourceLocation": {
    category: "group",
    term: "Where the code is",
    body: "Pick one. Only the option you choose opens its fields, so you never face three sets of inputs at once. Git and zip produce a private read-only copy; a typed path produces a description only.",
  },
  "group.databaseTarget": {
    category: "group",
    term: "Azure database",
    body: "Where the converted schema, data and PL/SQL would end up. Azure SQL Managed Instance keeps the most Oracle-like behaviour; PostgreSQL is the most portable. Choosing one records an intent — nothing is provisioned.",
  },
  "group.planningDepth": {
    category: "group",
    term: "Planning depth",
    body: "How far ahead the plan reaches. Depths beyond the implemented adapters are still described in writing, and the plan says which ones have no adapter here.",
  },
  "group.requiredEvidence": {
    category: "group",
    term: "Needed before code can be generated",
    body: "Each requirement must be met by at least one ticked item before a generation phase can be authorised. An unmet requirement becomes a blocker rather than an error.",
  },
  "group.optionalEvidence": {
    category: "group",
    term: "Extra context",
    body: "Optional material. It never blocks a plan; it lets the plan describe the estate more precisely.",
  },

  // ------------------------------------------------------------------- choices
  "choice.sourceRepo": {
    category: "choice",
    term: "Clone a Git repository",
    body: "The server takes a shallow, history-free copy into a folder scoped to this session. The copy cannot push back to your repository and is deleted automatically after four hours.",
  },
  "choice.sourceZip": {
    category: "choice",
    term: "Upload a zip",
    body: "The archive is expanded into the same private session folder and locked read-only. Nothing is sent back to wherever the archive came from.",
  },
  "choice.sourceManual": {
    category: "choice",
    term: "Describe a folder path",
    body: "Records where the code lives without copying it. No source is read, so phases that need to read files stay unavailable.",
  },

  // ------------------------------------------------------------------- actions
  "action.newMigration": {
    category: "action",
    term: "New migration",
    body: "Clears the answers in this tab and starts the five setup steps. Nothing is saved on the server, so nothing is lost anywhere else.",
  },
  "action.loadExample": {
    category: "action",
    term: "Load example values",
    body: "Fills the text fields with sample values so you can see the shape of the setup. The example connects to nothing, copies nothing, and describes no real application.",
  },
  "action.copyRepository": {
    category: "action",
    term: "Copy this repository",
    body: "Starts a server-side shallow clone into your session workspace and streams the server's own output into Activity.",
  },
  "action.deleteCopy": {
    category: "action",
    term: "Delete this copy now",
    body: "Removes the copied source from the server immediately instead of waiting for the four-hour expiry. Ticks the indexer made on your behalf are removed with it.",
  },
  "action.generatePlan": {
    category: "action",
    term: "Generate migration plan",
    body: "Sends the setup to the deterministic planner and returns the written plan. It writes no file and changes nothing, in Azure or in your repository.",
  },
  "action.runAuthorized": {
    category: "action",
    term: "Run authorised phases",
    body: "Runs only the phases the planner marked Planned. Output is written into your private session workspace. Your source repository is never modified and no Azure resource is created.",
  },
  "action.viewActivity": {
    category: "action",
    term: "View activity",
    body: "Opens the transcript of the current or most recent server operation in this tab.",
  },
  "action.downloadOutput": {
    category: "action",
    term: "Download what this run generated",
    body: "Packages the files the run wrote into a zip. Your copied source is not included, and the workspace is deleted after four hours either way.",
  },
  "action.openArtifact": {
    category: "action",
    term: "Open a generated file",
    body: "Reads the file straight from your session workspace and shows at most 512 KB of it. Opening a file does not run or validate it.",
  },
  "action.askAgent": {
    category: "action",
    term: "Ask the migration fleet",
    body: "Sends one question to the configured Foundry agent and shows the reply. It executes no migration step, opens no gate, and the message is not stored.",
  },
  "action.azureStatus": {
    category: "action",
    term: "Azure status",
    body: "Lists the Azure services this workbench is configured against and whether each one has been proven at runtime.",
  },
  "action.help": {
    category: "action",
    term: "Help",
    body: "Opens the glossary: the words this workbench uses, in plain language.",
  },
  "action.theme": {
    category: "action",
    term: "Theme",
    body: "Switches between the dark and light versions of the same interface. The choice is remembered in this browser only.",
  },
  "action.editSetup": {
    category: "action",
    term: "Edit setup",
    body: "Returns to the five setup steps with your answers intact. The current plan and any run result are discarded, because they describe the setup as it was.",
  },
  "action.rawOutput": {
    category: "action",
    term: "Raw server output",
    body: "Every line the server sent, in order, including tool output such as git's own progress. It is collapsed because the summary above already states what is happening; open it when you need the detail behind that summary.",
  },

  // ------------------------------------------------------------------ statuses
  "status.planningReady": {
    category: "status",
    term: "Ready to plan",
    body: "The setup is complete enough for the planner to produce a plan. It says nothing about whether a phase can run.",
  },
  "status.generated": {
    category: "status",
    term: "Generated",
    body: "A file was written into your session workspace. Generated code that has never been compiled is not working software, and DDL that has never executed is not a migrated schema.",
  },
  "status.executed": {
    category: "status",
    term: "Executed",
    body: "An adapter ran this phase and returned a result. Read the phase detail for what it actually did.",
  },
  "status.noAdapter": {
    category: "status",
    term: "No adapter",
    body: "No implementation exists for this phase here, so it was described in the plan and skipped. This is a missing capability, not a failure of your setup.",
  },
  "status.blocked": {
    category: "status",
    term: "Blocker",
    body: "Something the plan needs is missing. Blockers are listed in business terms and each one names what would clear it.",
  },
  "status.attestation": {
    category: "status",
    term: "Attestation",
    body: "A signed statement that a specific check passed. Conversion and generation phases deliberately produce none, so an absent attestation is expected rather than a fault.",
  },
  "status.triggerRetained": {
    category: "status",
    term: "Source logic retained; not yet converted",
    body: "The normalized Forms artifact retains original trigger text where the supplied export contained it, but no conversion has been applied to that text.",
  },
  "status.componentActive": {
    category: "status",
    term: "Active",
    body: "This service has been reached at runtime by this workbench. Components without that evidence are shown as not configured rather than assumed working.",
  },
  "status.runInterrupted": {
    category: "status",
    term: "Interrupted or unknown",
    body: "The progress stream stopped without the server declaring an outcome. That is not success and not a reported failure: what the server did after the last message is not known from the browser. Anything shown is partial.",
  },

  // ------------------------------------------------------------------- metrics
  "metric.stagesReady": {
    category: "metric",
    term: "Stages ready",
    body: "How many of the six migration stages the planner could authorise with the setup as it stands.",
  },
  "metric.inputsProvided": {
    category: "metric",
    term: "Inputs provided",
    body: "How many source-checklist requirements you have met. Each requirement can be satisfied by any one of its listed items.",
  },
  "metric.stillMissing": {
    category: "metric",
    term: "Still missing",
    body: "The number of blockers in the plan. Every one names what would clear it.",
  },
  "metric.azureActive": {
    category: "metric",
    term: "Azure services active",
    body: "How many configured Azure services this workbench has actually reached, out of those it knows about.",
  },
  "metric.requirementsMet": {
    category: "metric",
    term: "Required inputs ready",
    body: "How many checklist requirements have at least one item ticked. You can generate a plan without meeting them all; each unmet one becomes a blocker.",
  },
  "metric.activityMessages": {
    category: "metric",
    term: "Activity messages",
    body: "How many progress and completion messages the server sent. It counts messages, not files, phases or artifacts \u2014 a single step can send several.",
  },
  "metric.discoveryMessages": {
    category: "metric",
    term: "Discovery messages",
    body: "How many messages reported something recognised in your source. It counts messages, not files. Actual file counts appear under what the server counted.",
  },
  "metric.warningMessages": {
    category: "metric",
    term: "Warning messages",
    body: "How many messages the server marked as a warning or a skipped step. A warning does not stop the operation; read the transcript for what it was.",
  },
  "metric.errorMessages": {
    category: "metric",
    term: "Error messages",
    body: "How many messages the server marked as an error. One error can stop an operation, so read the state above rather than inferring an outcome from this number.",
  },
  "metric.serverCounted": {
    category: "metric",
    term: "What the server counted",
    body: "Totals the server measured and sent as numbers, not figures read out of a message. When the operation did not finish these are partial: they are what had been counted when the transcript stopped.",
  },
  "metric.phasesExecuted": {
    category: "metric",
    term: "Phases that ran",
    body: "How many authorised phases an adapter actually executed in this run, out of the phases the plan contained. Skipped and unimplemented phases are counted separately below.",
  },
  "metric.filesWritten": {
    category: "metric",
    term: "Files written",
    body: "How many files this run wrote into your private session workspace. Writing a file is not compiling it, running it, or deploying it.",
  },

  // ------------------------------------------------------------------ glossary
  "glossary.phase": {
    category: "glossary",
    term: "Phase",
    body: "One unit of migration work — for example analysing the source or converting a schema. The planner decides which phases it can authorise; it never runs one itself.",
  },
  "glossary.adapter": {
    category: "glossary",
    term: "Adapter",
    body: "The code that actually performs a phase. Where no adapter exists the phase is described in the plan and skipped.",
  },
  "glossary.ir": {
    category: "glossary",
    term: "Intermediate model",
    body: "A structured description of the source application that conversion works from, instead of reading Oracle Forms files repeatedly.",
  },
  "glossary.evidence": {
    category: "glossary",
    term: "Evidence",
    body: "Something you declare you have — an export, an inventory, a document. The workbench does not verify it; it records what you said and blocks on what you did not.",
  },
  "glossary.attestation": {
    category: "glossary",
    term: "Attestation",
    body: "A signed record that a check passed, such as a reconciliation matching row counts. Only phases that perform a check can produce one.",
  },
  "glossary.workspace": {
    category: "glossary",
    term: "Session workspace",
    body: "A private folder on the server that holds the copy of your source and anything a run generates. It is scoped to your session and deleted automatically after four hours.",
  },
  "glossary.sandbox": {
    category: "glossary",
    term: "Sandbox",
    body: "The non-production database the host was configured with. Only separately approved phases may write to it, and no caller can change which database it is.",
  },
  "glossary.cutover": {
    category: "glossary",
    term: "Cutover",
    body: "Switching real users from Oracle to the migrated system. It is out of scope for this workbench and has no control here.",
  },
  "glossary.planningDepth": {
    category: "glossary",
    term: "Planning depth",
    body: "How far the plan looks ahead. A depth can be planned in writing even when no adapter exists to carry it out.",
  },
  "glossary.blocker": {
    category: "glossary",
    term: "Blocker",
    body: "A missing input or unmet condition that stops a phase being authorised. Blockers are informational until you try to run the phase.",
  },
} as const satisfies Record<string, GuidanceEntry>;

export type GuidanceKey = keyof typeof ENTRIES;

export const GUIDANCE: Readonly<Record<GuidanceKey, GuidanceEntry>> = ENTRIES;

/** The help text for a registry key. Unknown keys cannot compile, so help can never go missing silently. */
export function help(key: GuidanceKey): string {
  return ENTRIES[key].body;
}

export function term(key: GuidanceKey): string {
  return ENTRIES[key].term;
}

export const GLOSSARY: readonly GuidanceEntry[] = Object.values(ENTRIES as Record<string, GuidanceEntry>)
  .filter((entry) => entry.category === "glossary")
  .sort((left, right) => left.term.localeCompare(right.term));

/** Server evidence names are enum-derived, so the plain-language wording lives here. */
export const EVIDENCE_NAMES: Readonly<Record<string, string>> = {
  FormsModuleInventory: "Forms module inventory",
  FormsModuleSource: "Forms module source (.fmb)",
  FormsXmlExport: "Forms XML export",
  MenuModuleSource: "Menu module source (.mmb)",
  SharedLibrarySource: "Shared library source (.pll)",
  ObjectLibrarySource: "Object library source (.olb)",
  OracleReportsInventory: "Oracle Reports inventory",
  PlSqlProgramUnit: "PL/SQL program units",
  DatabaseSchemaExport: "Database schema export",
  DatabaseLinkUsage: "Database link usage",
  ExternalProcedureUsage: "External procedure usage",
};

export const EVIDENCE_HELP: Readonly<Record<string, string>> = {
  FormsModuleInventory: "A list of every form in the application, with names and owners.",
  FormsModuleSource: "The original .fmb form files. They are a proprietary binary, so they are indexed by name and size and never read.",
  FormsXmlExport: "Forms exported to XML with Oracle's own Forms2XML converter. This is the readable form of a module.",
  MenuModuleSource: "The .mmb menu module files.",
  SharedLibrarySource: "The .pll shared library files.",
  ObjectLibrarySource: "The .olb object library files.",
  OracleReportsInventory: "A list of the Oracle Reports the application calls.",
  PlSqlProgramUnit: "The PL/SQL packages, procedures, functions and triggers.",
  DatabaseSchemaExport: "A schema-only export of the Oracle database: structure, no data.",
  DatabaseLinkUsage: "Where the schema reaches other databases through database links.",
  ExternalProcedureUsage: "Any calls out to external C or Java procedures.",
  ScheduledJobInventory: "Scheduled jobs the application depends on.",
  IntegrationInventory: "The other systems this application exchanges data with.",
  BusinessProcessCatalog: "What the application does, described in business terms.",
  AuthenticationTopology: "How users sign in today.",
  DataProfile: "Table sizes, row counts and growth rates.",
  TestBaseline: "Existing tests, or a record of what working looks like today.",
  CutoverAndRollbackPlan: "How you would switch over, and how you would roll back.",
  LicensingAndSupportPosition: "Current Oracle licence and support commitments.",
  UsageAndBusinessValue: "Who uses the application and how much it matters.",
  ReplacementProductFit: "Whether an off-the-shelf product could replace it instead.",
  WorkloadProfile: "Peak load, concurrency and performance expectations.",
  ComplianceConstraint: "Regulatory rules the system has to satisfy.",
  NetworkTopology: "How the network and connectivity are laid out.",
};
