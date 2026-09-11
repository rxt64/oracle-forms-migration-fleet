import { FormEvent, startTransition, useEffect, useId, useRef, useState } from "react";
import {
  AlertTriangle,
  ArrowLeft,
  ArrowRight,
  Check,
  CheckCircle2,
  Cloud,
  Cpu,
  Database,
  FileArchive,
  FileCheck2,
  FileText,
  GitBranch,
  Info,
  ListChecks,
  LockKeyhole,
  PanelTop,
  Pencil,
  Play,
  ShieldCheck,
  Sparkles,
  Trash2,
  X,
  XCircle,
} from "lucide-react";
import type {
  AzureFootprint,
  Bootstrap,
  EvidenceOption,
  ExecutionResult,
  FieldErrors,
  FleetAttribution,
  Phase,
  PhaseAttribution,
  PhaseEngine,
  PlanResponse,
  RunFields,
} from "./types";
import {
  acquireSource,
  executeRun,
  fetchArtifact,
  formatBytes,
  parseRepositoryUrl,
  proposedResourceNames,
  releaseSource,
  type ConsoleLine,
  type SourceWorkspace,
} from "./sourceClient";
import { ServiceGlyph, glyphForComponent, glyphForDatabase, glyphForHost } from "./ServiceGlyph";
import { MatrixConsole } from "./MatrixConsole";
import { ArchitectureReveal } from "./ArchitectureReveal";
import { InfoTip } from "./InfoTip";
import "./wizard.css";

const STORAGE_KEY = "ofm-workbench-draft-v1";
const INTRO_KEY = "ofm-workbench-intro-v1";
const STEPS = ["Application", "Destination", "Evidence", "Approvals", "Review"] as const;
const SOURCE_CHOICES = [
  { value: "repo", name: "Clone a Git repository", description: "GitHub, Azure DevOps, GitLab, or Bitbucket. The server takes a shallow, read-only copy into a private folder for your session." },
  { value: "zip", name: "Upload a zip", description: "Send an export of your code. It is expanded into the same private per-session folder and locked read-only." },
  { value: "manual", name: "Type a folder path", description: "Describe where the code lives without copying it. The plan records the path only." },
];

const STEP_SUMMARIES = [
  "Name the app and point at its code",
  "Pick the Azure database",
  "Tick what you already have",
  "Name the approvers (optional)",
  "Read it back, then generate",
] as const;

const emptyFields: RunFields = {
  engagementId: "",
  applicationName: "",
  sourceRoot: "",
  outputRoot: "",
  executionApprover: "",
  productionApprover: "",
};

const exampleFields: RunFields = {
  engagementId: "ENG-0042",
  applicationName: "ORDERS",
  sourceRoot: "legacy/forms",
  outputRoot: "out/orders",
  executionApprover: "",
  productionApprover: "",
};

// Server-supplied evidence names are enum-derived, so the plain-language wording lives here.
const EVIDENCE_NAMES: Record<string, string> = {
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

const EVIDENCE_HELP: Record<string, string> = {
  FormsModuleInventory: "A list of every form in the application, with names and owners.",
  FormsModuleSource: "The original .fmb form files.",
  FormsXmlExport: "Forms exported to XML with the Forms2XML converter.",
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

function queryFields(): RunFields {
  const query = new URLSearchParams(window.location.search);
  return Object.fromEntries(
    Object.keys(emptyFields).map((key) => [key, query.get(key) ?? ""]),
  ) as unknown as RunFields;
}

function humanize(value: string) {
  return value.replace(/([a-z0-9])([A-Z])/g, "$1 $2");
}

function relativePathError(value: string) {
  if (!value.trim()) return "Enter a folder path.";
  if (/^([a-zA-Z]:|\\\\|\/)/.test(value) || value.includes("://")) {
    return "Use a folder path inside the repository, not a drive letter or a web address.";
  }
  return value.split(/[\\/]/).includes("..") ? "Path traversal segments are not allowed." : "";
}

function evidenceGroups(options: EvidenceOption[]) {
  const groups = new Map<string, EvidenceOption[]>();
  options
    .filter((option) => option.requiredForGeneration || option.alternativeRequirement)
    .forEach((option) => {
      const key = option.alternativeRequirement ?? `single:${option.kind}`;
      groups.set(key, [...(groups.get(key) ?? []), option]);
    });
  return [...groups.values()];
}

function Field({
  id,
  label,
  hint,
  help,
  placeholder,
  value,
  error,
  onChange,
}: {
  id: keyof RunFields;
  label: string;
  hint?: string;
  help?: string;
  placeholder: string;
  value: string;
  error?: string;
  onChange: (value: string) => void;
}) {
  const describedBy = [error && `${id}-error`, help && `${id}-help`].filter(Boolean).join(" ");
  return (
    <div className="mf-field">
      {/* The info button is a sibling of the label, not a child: nesting it would fold its text
          into the field's accessible name. */}
      <div className="mf-label-row">
        <label htmlFor={id}>{label}</label>
        {help && <InfoTip label={label.toLowerCase()}>{help}</InfoTip>}
        {hint && <span className="mf-label-hint">{hint}</span>}
      </div>
      {help && <p className="mf-sr-only" id={`${id}-help`}>{help}</p>}
      <input
        id={id}
        value={value}
        placeholder={placeholder}
        autoComplete="off"
        spellCheck={false}
        aria-invalid={Boolean(error)}
        aria-describedby={describedBy || undefined}
        onChange={(event) => onChange(event.target.value)}
      />
      {error && <p id={`${id}-error`} className="mf-error" role="alert">{error}</p>}
    </div>
  );
}

function ChoiceCards({
  legend,
  hint,
  value,
  options,
  onChange,
  renderDetail,
}: {
  legend: string;
  hint?: string;
  value: string;
  options: Array<{ value: string; name: string; description: string; glyph?: React.ReactNode }>;
  onChange: (value: string) => void;
  renderDetail?: (value: string) => React.ReactNode;
}) {
  const legendId = useId();
  return (
    // aria-labelledby points at the legend text alone, so the info button's label stays out of
    // the group's accessible name.
    <fieldset className="mf-choices" aria-labelledby={legendId}>
      <legend><span id={legendId}>{legend}</span>{hint && <InfoTip label={legend.toLowerCase()}>{hint}</InfoTip>}</legend>
      {options.map((option) => {
        const selected = option.value === value;
        // Only the chosen option expands. Everything else stays a one-line summary so the page
        // never presents every branch of the decision at the same time.
        return (
          <div className="mf-choice-shell" key={option.value}>
            <label className={selected ? "mf-choice selected" : "mf-choice"}>
              <input type="radio" name={legend} checked={selected} onChange={() => onChange(option.value)} />
              <span className="mf-radio" aria-hidden="true"><span /></span>
              {option.glyph && <span className="mf-choice-glyph">{option.glyph}</span>}
              <span><strong>{option.name}</strong><small>{option.description}</small></span>
            </label>
            {selected && renderDetail && (
              <div className="mf-choice-detail">{renderDetail(option.value)}</div>
            )}
          </div>
        );
      })}
    </fieldset>
  );
}

function ReviewRow({ label, value, onEdit }: { label: string; value: string; onEdit: () => void }) {
  return (
    <div className="mf-review-row">
      <div><dt>{label}</dt><dd>{value || "Not provided"}</dd></div>
      <button type="button" onClick={onEdit} aria-label={`Change ${label}`} title={`Change ${label}`}><Pencil /></button>
    </div>
  );
}

const ENGINE_LABELS: Record<PhaseEngine, string> = {
  NotImplemented: "No adapter",
  Deterministic: "Deterministic code",
  DeterministicWithModelReview: "Deterministic code + model review",
};

function PhaseCard({ phase, attribution }: { phase: Phase; attribution?: PhaseAttribution }) {
  const blocked = phase.status.startsWith("Blocked");
  return (
    <article className="mf-phase">
      <header>
        <div><p className="mf-kicker">{humanize(phase.owner)} · {humanize(phase.mutation)}</p><h3>{humanize(phase.phase)}</h3></div>
        <span className={blocked ? "mf-pill danger" : "mf-pill success"}>{blocked ? <XCircle /> : <CheckCircle2 />}{humanize(phase.status)}</span>
      </header>
      <p>{phase.objective}</p>
      {attribution && <p className="mf-attribution">
        <Cpu />
        <span>
          <strong>{ENGINE_LABELS[attribution.engine]}</strong>
          {attribution.modelDeployment && <code>{attribution.modelDeployment}</code>}
          <small>{attribution.summary}</small>
        </span>
      </p>}
      {phase.blockers.length > 0 && <ul>{phase.blockers.map((blocker) => <li key={blocker}>{blocker}</li>)}</ul>}
      <details><summary>Inputs, outputs, and tooling</summary><div className="mf-phase-detail">
        <div><strong>Required inputs</strong><p>{phase.requiredInputs.map(humanize).join(", ") || "None"}</p></div>
        <div><strong>Expected outputs</strong><p>{phase.expectedOutputs.map((item) => item.path).join(", ") || "None"}</p></div>
        <div><strong>Tooling</strong><p>{phase.tooling.join(" · ") || "None"}</p></div>
      </div></details>
    </article>
  );
}

function AttributionSection({ attribution }: { attribution: FleetAttribution }) {
  return (
    <section className="mf-result-section">
      <p className="mf-kicker">Who does the work</p>
      <h2>Which model and which code runs each step</h2>
      <div className="mf-models">
        {attribution.models.map((model) => (
          <article key={model.capability}>
            <h3>{model.capability}</h3>
            {model.deployment
              ? <code>{model.deployment}</code>
              : <span className="mf-pill">Not configured</span>}
            <p>{model.summary}</p>
          </article>
        ))}
      </div>
      <ul className="mf-caveats">
        {attribution.disclaimers.map((text) => <li key={text}><ShieldCheck />{text}</li>)}
      </ul>
    </section>
  );
}

function AzureFootprintSection({ footprint }: { footprint: AzureFootprint }) {
  return (
    <section className="mf-result-section">
      <p className="mf-kicker">Before you deploy</p>
      <h2>What this needs in Azure, and what it would create</h2>

      <div className="mf-boundary compact">
        <LockKeyhole />
        <p>
          <strong>Nothing below has been created.</strong>
          {footprint.disclaimers[0]}
        </p>
      </div>

      <h3 className="mf-subhead">Resources</h3>
      <ul className="mf-requirements">
        {footprint.resources.map((resource) => (
          <li key={resource.resourceType}>
            <span className={resource.disposition === "Created" ? "mf-pill warn" : "mf-pill"}>{resource.disposition}</span>
            <div><code>{resource.resourceType}</code><small>{resource.purpose}</small></div>
            <em>from {humanize(resource.neededFrom)}</em>
          </li>
        ))}
      </ul>

      <h3 className="mf-subhead">Permissions the deploying identity needs</h3>
      <ul className="mf-requirements">
        {footprint.roles.map((role) => (
          <li key={`${role.role}-${role.scope}`}>
            <span className="mf-pill">{role.role}</span>
            <div><code>{role.scope}</code><small>{role.why}</small></div>
            <em>from {humanize(role.neededFrom)}</em>
          </li>
        ))}
      </ul>

      <h3 className="mf-subhead">Signing in to a customer tenant</h3>
      <ul className="mf-caveats">
        {footprint.tenantModel.map((text) => <li key={text}><ShieldCheck />{text}</li>)}
      </ul>

      <div className="mf-boundary compact">
        <AlertTriangle />
        <p>
          <strong>There is no deploy button, deliberately.</strong>
          This build has no adapter that provisions an Azure resource or opens a database connection, so a
          sign-in here could not deploy anything. Wiring one before the cross-tenant consent, scoping, and
          audit trail above are agreed would create a path to write into a customer subscription that nobody
          had reviewed. Take this list to whoever owns the subscription instead.
        </p>
      </div>

      <ul className="mf-caveats">
        {footprint.disclaimers.slice(1).map((text) => <li key={text}><ShieldCheck />{text}</li>)}
      </ul>
    </section>
  );
}

const PHASE_STATE_LABELS: Record<string, string> = {
  Executed: "Executed",
  SkippedByPlanner: "Skipped",
  AdapterNotImplemented: "No adapter",
  Failed: "Failed",
};

function ExecutionReport({ result, onPreview }: { result: ExecutionResult; onPreview: (path: string) => void }) {
  const executed = result.phases.filter((phase) => phase.state === "Executed").length;
  return (
    <div className="mf-run-report">
      <p className="mf-run-summary">
        <strong>{executed} of {result.phases.length} phases ran.</strong> The planner authorized {humanize(result.authorizedMode)} of
        the requested {humanize(result.requestedMode)}. Everything written went into <code>{result.outputRoot}</code> inside your
        private session workspace.
      </p>

      <ul className="mf-run-phases">
        {result.phases.map((phase) => (
          <li key={phase.phase}>
            <div className="mf-run-phase-head">
              <strong>{humanize(phase.phase)}</strong>
              <span className={phase.state === "Executed" ? "mf-pill success" : phase.state === "Failed" ? "mf-pill danger" : "mf-pill warn"}>
                {phase.state === "Executed" ? <CheckCircle2 /> : phase.state === "Failed" ? <XCircle /> : <AlertTriangle />}
                {PHASE_STATE_LABELS[phase.state] ?? phase.state}
              </span>
            </div>
            {phase.detail && <p>{phase.detail}</p>}
            {phase.findings.length > 0 && <ul className="mf-run-findings">{phase.findings.map((finding) => <li key={finding}>{finding}</li>)}</ul>}
          </li>
        ))}
      </ul>

      <h3>Files written</h3>
      {result.artifacts.length === 0
        ? <p className="mf-help">Nothing was written, so there is nothing to open.</p>
        : <ul className="mf-run-artifacts">{result.artifacts.map((artifact) => (
          <li key={artifact.path}>
            <div><code>{artifact.path}</code><small>{artifact.description}</small></div>
            {artifact.previewable
              ? <button type="button" className="mf-inline-link" onClick={() => onPreview(artifact.path)}><FileText />Open</button>
              : <span className="mf-help">Not a text file</span>}
          </li>
        ))}</ul>}

      <h3>Attestations</h3>
      {result.attestations.length === 0
        ? <p className="mf-help">No phase in this run produces a signable attestation, so none was recorded.</p>
        : <ul className="mf-run-attestations">{result.attestations.map((attestation) => (
          <li key={attestation.kind}><strong>{humanize(attestation.kind)}</strong><small>{attestation.summary}</small></li>
        ))}</ul>}
    </div>
  );
}

export default function WizardApp() {
  const [bootstrap, setBootstrap] = useState<Bootstrap | null>(null);
  const [fields, setFields] = useState<RunFields>(queryFields);
  const [database, setDatabase] = useState("");
  const [mode, setMode] = useState("PlanOnly");
  const [evidence, setEvidence] = useState<string[]>([]);
  // Ticks the workbench made on the operator's behalf after reading a copied source. They belong to
  // that copy, so they are dropped when the source changes and are never persisted: a reloaded page
  // has no workspace, and a tick that claims evidence nobody can point at would be a false answer.
  const [autoEvidence, setAutoEvidence] = useState<string[]>([]);
  const [sourceMode, setSourceMode] = useState("repo");
  const [repoUrl, setRepoUrl] = useState("");
  const [repoBranch, setRepoBranch] = useState("");
  const [repoFolder, setRepoFolder] = useState("");
  const [workspace, setWorkspace] = useState<SourceWorkspace | null>(null);
  const [consoleLines, setConsoleLines] = useState<ConsoleLine[]>([]);
  const [consoleOpen, setConsoleOpen] = useState(false);
  const [acquiring, setAcquiring] = useState(false);
  const [sourceError, setSourceError] = useState("");
  const [step, setStep] = useState(0);
  const [started, setStarted] = useState(() => {
    try { return localStorage.getItem(INTRO_KEY) === "1"; } catch { return false; }
  });
  const [errors, setErrors] = useState<FieldErrors>({});
  const [status, setStatus] = useState("Loading migration catalog...");
  const [plan, setPlan] = useState<PlanResponse | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [executing, setExecuting] = useState(false);
  const [execution, setExecution] = useState<ExecutionResult | null>(null);
  const [executionError, setExecutionError] = useState("");
  const [consoleMode, setConsoleMode] = useState<"source" | "execution">("source");
  const [artifact, setArtifact] = useState<{ path: string; text: string; error: string } | null>(null);
  const [dialog, setDialog] = useState<"agent" | "azure" | "artifact" | null>(null);
  const [agentMessage, setAgentMessage] = useState("");
  const [agentAnswer, setAgentAnswer] = useState("");
  const [agentStatus, setAgentStatus] = useState("");
  const [asking, setAsking] = useState(false);
  const heading = useRef<HTMLHeadingElement>(null);
  const dialogBody = useRef<HTMLElement>(null);
  const dialogOpener = useRef<HTMLElement | null>(null);
  const acquisition = useRef<AbortController | null>(null);
  const run = useRef<AbortController | null>(null);

  useEffect(() => {
    let cancelled = false;
    fetch("/api/workbench/bootstrap")
      .then(async (response) => {
        if (!response.ok) throw new Error(`Bootstrap failed (${response.status}).`);
        return response.json() as Promise<Bootstrap>;
      })
      .then((catalog) => {
        if (cancelled) return;
        let saved: { database?: string; mode?: string; evidence?: string[] } = {};
        try { saved = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? "{}"); } catch { saved = {}; }
        const databases = catalog.databaseTargets.map((option) => option.target);
        const modes = catalog.executionModes.map((option) => option.mode);
        const kinds = new Set(catalog.evidenceKinds.map((option) => option.kind));
        setBootstrap(catalog);
        setDatabase(databases.includes(saved.database ?? "") ? saved.database! : databases[0] ?? "");
        setMode(modes.includes(saved.mode ?? "") ? saved.mode! : "PlanOnly");
        setEvidence((saved.evidence ?? []).filter((kind) => kinds.has(kind)));
        setStatus("Ready when you are.");
      })
      .catch((error: Error) => setStatus(error.message));
    return () => { cancelled = true; };
  }, []);

  useEffect(() => {
    if (!bootstrap || !database) return;
    const manual = evidence.filter((kind) => !autoEvidence.includes(kind));
    localStorage.setItem(STORAGE_KEY, JSON.stringify({ database, mode, evidence: manual }));
  }, [bootstrap, database, mode, evidence, autoEvidence]);

  useEffect(() => {
    document.title = plan
      ? "Migration plan · Migration Fleet"
      : started
        ? `Step ${step + 1} of ${STEPS.length}: ${STEPS[step]} · Migration Fleet`
        : "Migration Fleet · Plan an Oracle Forms migration";
    requestAnimationFrame(() => heading.current?.focus());
  }, [step, plan, started]);

  useEffect(() => {
    if (!dialog) return;
    dialogOpener.current = document.activeElement as HTMLElement | null;
    const body = dialogBody.current;
    const focusable = () => Array.from(
      body?.querySelectorAll<HTMLElement>('button:not([disabled]), a[href], input:not([disabled]), textarea:not([disabled]), select:not([disabled])') ?? [])
      .filter((element) => element.offsetParent !== null);
    requestAnimationFrame(() => focusable()[0]?.focus());

    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") {
        setDialog(null);
        return;
      }
      if (event.key !== "Tab") return;
      const stops = focusable();
      if (stops.length === 0) return;
      const first = stops[0];
      const last = stops[stops.length - 1];
      const active = document.activeElement;
      if (event.shiftKey && (active === first || !body?.contains(active))) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && active === last) {
        event.preventDefault();
        first.focus();
      }
    }

    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("keydown", onKeyDown);
      dialogOpener.current?.focus();
    };
  }, [dialog]);

  if (!bootstrap) {
    return <main className="mf-loading"><span className="mf-logo"><Database /><ArrowRight /><PanelTop /></span><h1>Migration Fleet</h1><p role="status">{status}</p></main>;
  }

  const groups = evidenceGroups(bootstrap.evidenceKinds);
  const readyGroups = groups.filter((group) => group.some((item) => evidence.includes(item.kind))).length;
  const requiredEvidence = bootstrap.evidenceKinds.filter((item) => item.requiredForGeneration || item.alternativeRequirement);
  const optionalEvidence = bootstrap.evidenceKinds.filter((item) => !item.requiredForGeneration && !item.alternativeRequirement);
  const selectedDatabase = bootstrap.databaseTargets.find((option) => option.target === database);
  const selectedMode = bootstrap.executionModes.find((option) => option.mode === mode);
  const activeAzure = bootstrap.azureComponents.filter((component) => component.state === "Active").length;
  const repository = parseRepositoryUrl(repoUrl).target;
  const sourceRoot = sourceMode === "manual"
    ? fields.sourceRoot
    : workspace
      ? (sourceMode === "repo" && repoFolder.trim() ? repoFolder.trim() : workspace.sourceRoot)
      : "";
  const sourceLabel = sourceMode === "manual"
    ? fields.sourceRoot
    : workspace
      ? [workspace.originLabel, sourceRoot].filter(Boolean).join(" \u00b7 ")
      : "";

  // Execution reads a copy the server holds, so it needs a workspace and at least one phase the
  // planner actually authorized. Anything else is explained rather than silently disabled.
  const plannedPhases = plan?.plan.phases.filter((phase) => phase.status === "Planned").length ?? 0;
  const runnable = Boolean(workspace) && sourceMode !== "manual" && plannedPhases > 0;
  const runHint = sourceMode === "manual"
    ? "A typed folder path only describes where the code lives. Copy a repository or upload a zip on step 1 to give the fleet something to read."
    : !workspace
      ? "Copy a repository or upload a zip on step 1 first. The fleet only reads the copy held for your session."
      : plannedPhases === 0
        ? "The planner authorized no phase, so there is nothing to run. Clear the blockers above, then generate the plan again."
        : "";

  /**
   * Runs a server-side clone or upload and mirrors the server's own progress lines into the
   * console. The stream is the only source of truth for what happened.
   */
  async function acquire(request: { mode: "repo"; repositoryUrl: string; branch?: string } | { mode: "zip"; file: File }) {
    acquisition.current?.abort();
    const controller = new AbortController();
    acquisition.current = controller;

    if (workspace) void releaseSource(workspace.workspaceId).catch(() => undefined);
    setWorkspace(null);
    // The outgoing copy's detected ticks describe a source that is being replaced, so they are
    // retired here. Whatever the operator ticked themselves survives.
    const manual = evidence.filter((kind) => !autoEvidence.includes(kind));
    setEvidence(manual);
    setAutoEvidence([]);
    setConsoleMode("source");
    setConsoleLines([]);
    setConsoleOpen(true);
    setAcquiring(true);
    setSourceError("");

    try {
      for await (const event of acquireSource(request, controller.signal)) {
        if (event.level === "done" && "workspace" in event) {
          const acquired = event.workspace;
          const known = new Set(bootstrap!.evidenceKinds.map((option) => option.kind));
          const detected = acquired.artifacts.map((artifact) => artifact.kind).filter((kind) => known.has(kind));
          // Several files can share a kind, so count kinds rather than files: the operator is told
          // how many answers moved, and three copies of one kind only ever move one answer.
          const recognised = [...new Set(detected)];
          const added = recognised.filter((kind) => !manual.includes(kind));
          setWorkspace(acquired);
          setEvidence([...manual, ...added]);
          setAutoEvidence(added);
          setConsoleLines((current) => [...current, {
            level: "done",
            text: `${acquired.fileCount} files (${formatBytes(acquired.byteCount)}) ready in workspace ${acquired.workspaceId}.`,
          }]);
          setStatus(recognised.length > 0
            ? `Copied your source. ${recognised.length} matching ${recognised.length === 1 ? "answer" : "answers"} on step 3 ${recognised.length === 1 ? "is" : "are"} ticked for you.`
            : "Copied your source, but no Oracle Forms or PL/SQL artifacts were recognised.");
        } else {
          setConsoleLines((current) => [...current, event as ConsoleLine]);
          if (event.level === "error") setSourceError(event.text);
        }
      }
    } catch (error) {
      if (!controller.signal.aborted) {
        const message = error instanceof Error ? error.message : "The copy failed.";
        setConsoleLines((current) => [...current, { level: "error", text: message }]);
        setSourceError(message);
      }
    } finally {
      if (acquisition.current === controller) setAcquiring(false);
    }
  }

  function discardWorkspace() {
    if (!workspace) return;
    void releaseSource(workspace.workspaceId).catch(() => undefined);
    setWorkspace(null);
    // The source those ticks pointed at no longer exists, so the ticks go with it.
    setEvidence((current) => current.filter((kind) => !autoEvidence.includes(kind)));
    setAutoEvidence([]);
    setConsoleLines([]);
    setExecution(null);
    setExecutionError("");
    setStatus("The copied source was deleted from the server.");
  }

  function updateField(name: keyof RunFields, value: string) {
    setFields((current) => ({ ...current, [name]: value }));
    setErrors((current) => ({ ...current, [name]: undefined }));
  }

  function beginSetup(prefill?: RunFields) {
    if (prefill) setFields(prefill);
    try { localStorage.setItem(INTRO_KEY, "1"); } catch { /* storage unavailable */ }
    setErrors({});
    setStep(0);
    setStarted(true);
    setStatus(prefill ? "Example details filled in. Change anything you like." : "Answers are kept for this session only.");
  }

  function validateApplication() {
    const next: FieldErrors = {};
    if (!fields.engagementId.trim()) next.engagementId = "Enter a reference for this plan.";
    if (!fields.applicationName.trim()) next.applicationName = "Enter the application name.";
    next.outputRoot = relativePathError(fields.outputRoot) || undefined;

    let source = "";
    if (sourceMode === "manual") {
      next.sourceRoot = relativePathError(fields.sourceRoot) || undefined;
    } else if (!workspace) {
      source = sourceMode === "repo"
        ? parseRepositoryUrl(repoUrl).error ?? "Copy the repository before continuing."
        : "Upload a zip before continuing, or pick a different option above.";
    } else if (sourceMode === "repo" && repoFolder.trim()) {
      source = relativePathError(repoFolder);
    }

    setSourceError(source);
    setErrors(next);
    return !source && !Object.values(next).some(Boolean);
  }

  function validateApprovals() {
    const next: FieldErrors = {};
    if (fields.productionApprover.trim() && fields.executionApprover.trim().toLowerCase() === fields.productionApprover.trim().toLowerCase()) {
      next.productionApprover = "Production and sandbox approvals require distinct identities.";
    }
    setErrors(next);
    return !Object.values(next).some(Boolean);
  }

  function continueJourney() {
    const valid = step === 0 ? validateApplication() : step === 3 ? validateApprovals() : true;
    if (!valid) {
      setStatus("Resolve the highlighted fields to continue.");
      requestAnimationFrame(() => document.querySelector<HTMLElement>("[aria-invalid='true']")?.focus());
      return;
    }
    setStep((current) => Math.min(current + 1, STEPS.length - 1));
    setStatus("Answers preserved for this session.");
  }

  function goToStep(next: number) {
    setPlan(null);
    setExecution(null);
    setExecutionError("");
    setStep(next);
  }

  function toggleEvidence(kind: string) {
    // Once the operator has ruled on a kind themselves it is their answer, not a detected one.
    setAutoEvidence((current) => current.filter((item) => item !== kind));
    setEvidence((current) => current.includes(kind) ? current.filter((item) => item !== kind) : [...current, kind]);
  }

  /** The exact request shape both the planner and the executor accept. */
  function runRequestBody() {
    const approval = (approverId: string) => approverId.trim()
      ? { decision: "Approved", approverId: approverId.trim(), notes: null }
      : { decision: "Pending", approverId: null, notes: null };
    return {
      engagementId: fields.engagementId.trim(),
      applicationName: fields.applicationName.trim(),
      requestedMode: mode,
      target: { frontEnd: "React", backEnd: "JavaSpringBoot", database },
      sourceRoot: sourceRoot.trim(),
      outputRoot: fields.outputRoot.trim(),
      evidence: evidence.map((kind, index) => ({ id: `EV-${index + 1}`, kind, source: "operator-console", summary: `${humanize(kind)} verified by the operator.`, isVerified: true, signals: [] })),
      planApproval: approval(""),
      executionApproval: approval(fields.executionApprover),
      productionApproval: approval(fields.productionApprover),
      attestations: [],
    };
  }

  async function generatePlan(event: FormEvent) {
    event.preventDefault();
    if (!validateApplication() || !validateApprovals()) {
      setStatus("Review the highlighted fields before planning.");
      return;
    }
    setSubmitting(true);
    setExecution(null);
    setExecutionError("");
    setStatus("Generating deterministic plan...");
    try {
      const response = await fetch("/api/workbench/plan", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(runRequestBody()),
      });
      if (!response.ok) throw new Error(`Planner failed (${response.status}).`);
      const result = await response.json() as PlanResponse;
      startTransition(() => setPlan(result));
      setStatus(`Plan generated with ${result.plan.blockers.length} blocker${result.plan.blockers.length === 1 ? "" : "s"}.`);
    } catch (error) {
      setStatus(error instanceof Error ? error.message : "The planner request failed.");
    } finally {
      setSubmitting(false);
    }
  }

  /**
   * Runs the phases the server's own planner authorized. The browser sends the workspace identifier
   * and never a path, and every line in the console is one the server emitted while working.
   */
  async function runAuthorized() {
    if (!workspace || !plan) return;
    run.current?.abort();
    const controller = new AbortController();
    run.current = controller;

    setExecuting(true);
    setExecution(null);
    setExecutionError("");
    setConsoleMode("execution");
    setConsoleLines([]);
    setConsoleOpen(true);
    setStatus("Running the phases the planner authorized...");

    try {
      for await (const event of executeRun({ ...runRequestBody(), workspaceId: workspace.workspaceId }, controller.signal)) {
        if (event.level === "done" && "result" in event) {
          const result = event.result;
          const ran = result.phases.filter((phase) => phase.state === "Executed").length;
          setExecution(result);
          setConsoleLines((current) => [...current, {
            level: "done",
            text: `${ran} phase${ran === 1 ? "" : "s"} executed. ${result.artifacts.length} file${result.artifacts.length === 1 ? "" : "s"} written.`,
          }]);
          setStatus(`${ran} phase${ran === 1 ? "" : "s"} ran and wrote ${result.artifacts.length} file${result.artifacts.length === 1 ? "" : "s"} into your session workspace.`);
        } else {
          setConsoleLines((current) => [...current, event as ConsoleLine]);
          if (event.level === "error") setExecutionError(event.text);
        }
      }
    } catch (error) {
      if (!controller.signal.aborted) {
        const message = error instanceof Error ? error.message : "The run failed.";
        setConsoleLines((current) => [...current, { level: "error", text: message }]);
        setExecutionError(message);
      }
    } finally {
      if (run.current === controller) setExecuting(false);
    }
  }

  async function openArtifact(path: string) {
    if (!workspace) return;
    setArtifact({ path, text: "", error: "" });
    setDialog("artifact");
    try {
      const text = await fetchArtifact(workspace.workspaceId, path);
      setArtifact({ path, text, error: "" });
    } catch (error) {
      setArtifact({ path, text: "", error: error instanceof Error ? error.message : "The artifact could not be loaded." });
    }
  }

  async function askAgent(event: FormEvent) {
    event.preventDefault();
    if (!agentMessage.trim() || !bootstrap?.agentChatAvailable) return;
    setAsking(true);
    setAgentAnswer("");
    setAgentStatus("Consulting the Foundry migration fleet...");
    try {
      const response = await fetch("/api/workbench/agent", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ message: agentMessage.trim() }),
      });
      const payload = await response.json() as { text?: string; error?: string; detail?: string };
      if (!response.ok) throw new Error(payload.error ?? payload.detail ?? `Agent request failed (${response.status}).`);
      setAgentAnswer(payload.text || "The agent returned no text output.");
      setAgentStatus("Response received. No migration action was executed.");
    } catch (error) {
      setAgentStatus(error instanceof Error ? error.message : "The Foundry request failed.");
    } finally {
      setAsking(false);
    }
  }

  const EvidenceChoice = ({ option }: { option: EvidenceOption }) => (
    <label className={evidence.includes(option.kind) ? "mf-evidence selected" : "mf-evidence"}>
      <input type="checkbox" checked={evidence.includes(option.kind)} onChange={() => toggleEvidence(option.kind)} />
      <span className="mf-check"><Check /></span>
      <span>
        <strong>{EVIDENCE_NAMES[option.kind] ?? option.name}<span className="mf-tag">{option.requiredForGeneration ? "Required" : option.alternativeRequirement ? "One of these" : "Optional"}</span></strong>
        <small>{EVIDENCE_HELP[option.kind] ?? "Supporting material for the plan."}</small>
      </span>
    </label>
  );

  return (
    <>
      <a className="mf-skip" href="#workspace">Skip to current step</a>
      <header className="mf-topbar">
        <div className="mf-brand"><span className="mf-logo"><Database /><ArrowRight /><PanelTop /></span><span><strong>Migration Fleet</strong><small>Oracle Forms modernization</small></span></div>
        <div className="mf-header-actions">
          <button type="button" title="See which Azure services this workbench is connected to" onClick={() => setDialog("azure")}><Cloud /><span>Azure status</span></button>
          <button type="button" title="Ask the migration AI a question about your plan" onClick={() => setDialog("agent")} disabled={!bootstrap.agentChatAvailable}><Sparkles /><span>Ask AI</span></button>
        </div>      </header>

      {!started ? <main className="mf-intro" id="workspace">
        <p className="mf-kicker">Oracle Forms modernization</p>
        <h1 ref={heading} tabIndex={-1}>Plan your move off Oracle Forms</h1>
        <p className="mf-lead">Answer five short questions about your Oracle Forms application. The workbench turns them into a written migration plan for a React front end, a Java Spring Boot back end, and an Azure database. It takes a couple of minutes.</p>

        <ol className="mf-intro-steps">
          {STEPS.map((item, index) => <li key={item}><span>{index + 1}</span><div><strong>{item}</strong><small>{STEP_SUMMARIES[index]}</small></div></li>)}
        </ol>

        <div className="mf-intro-cards">
          <article><ListChecks /><h2>What you need</h2><p>A project reference, the folder that holds your Forms files, and a rough idea of which exports you already have. Nothing is uploaded.</p></article>
          <article><FileCheck2 /><h2>What you get</h2><p>A plan covering the six migration stages, what each stage produces, and a plain list of anything still missing.</p></article>
          <article><ShieldCheck /><h2>What it will not do</h2><p>It never touches your code, your database, or your Azure resources. Anything it generates is written into your own private session workspace.</p></article>
        </div>

        <div className="mf-intro-actions">
          <button type="button" className="mf-primary" onClick={() => beginSetup()}><Play />Start</button>
          <button type="button" className="mf-secondary" onClick={() => beginSetup(exampleFields)}>Fill in an example</button>
        </div>
        <p className="mf-status" role="status" aria-live="polite">{status}</p>
      </main> : !plan ? <main className="mf-journey" id="workspace">
        <nav className="mf-rail" aria-label="Migration setup progress">
          <p>Migration setup</p>
          <ol>{STEPS.map((item, index) => <li className={index === step ? "current" : index < step ? "complete" : ""} key={item}>
            <button type="button" disabled={index > step} aria-current={index === step ? "step" : undefined} onClick={() => index < step && setStep(index)}>
              <span>{index < step ? <Check /> : index + 1}</span><span><strong>{item}</strong><small>{STEP_SUMMARIES[index]}</small></span>
            </button>
          </li>)}</ol>
          <div className="mf-boundary compact"><LockKeyhole /><p><strong>Safe mode</strong>Planning writes nothing. Running the authorized phases writes files into your private session workspace only — never your repository, a database, or an Azure resource.</p></div>
        </nav>

        <section className="mf-page">
          <div className="mf-progress"><span aria-hidden="true">Step {step + 1} of {STEPS.length}</span><progress max={STEPS.length} value={step + 1} /></div>
          <p className="mf-visually-hidden" aria-live="polite">{`Step ${step + 1} of ${STEPS.length}: ${STEPS[step]}`}</p>

          {step === 0 && <div className="mf-step"><p className="mf-kicker">Application</p><h1 ref={heading} tabIndex={-1}>What are you migrating?</h1><p className="mf-lead">Name the application, then point the plan at the code.</p>
            <div className="mf-assurance"><Info /><div><strong>We work on a copy, never your original</strong><p>The server takes a shallow, read-only copy into a private folder that only your sign-in can reach. Git history is stripped, so the copy cannot push back to your repository, and it is deleted automatically after four hours.</p><button type="button" className="mf-inline-link" onClick={() => { setFields(exampleFields); setSourceMode("manual"); setSourceError(""); setErrors({}); setStatus("Example details filled in. Change anything you like."); }}>Not sure? Fill in an example</button></div></div>
            <div className="mf-field-grid">
            <Field id="engagementId" label="Reference for this plan" hint="Any label" help="So you can recognise this plan later: a ticket number, a project code, or anything else. Example: ENG-0042" placeholder="ENG-0042" value={fields.engagementId} error={errors.engagementId} onChange={(value) => updateField("engagementId", value)} />
            <Field id="applicationName" label="Which application?" help="The name your team uses for the Oracle Forms application you want to move. It becomes the plan title. Example: ORDERS" placeholder="ORDERS" value={fields.applicationName} error={errors.applicationName} onChange={(value) => updateField("applicationName", value)} />
          </div>
            <ChoiceCards
              legend="Where is the code?"
              hint="Pick one. Only the option you choose opens up, so you never see three sets of fields at once."
              value={sourceMode}
              onChange={(value) => { setSourceMode(value); setSourceError(""); }}
              options={SOURCE_CHOICES.map((choice) => ({
                ...choice,
                glyph: <ServiceGlyph id={choice.value === "repo" ? glyphForHost(repository?.host ?? "") : choice.value === "zip" ? "archive" : "oracle-forms"} size={22} />,
              }))}
              renderDetail={(value) => <>
                {value === "repo" && <>
                  <div className="mf-field"><div className="mf-label-row"><label htmlFor="repoUrl"><GitBranch />Repository address</label><InfoTip label="the repository address">Copy it from your browser's address bar while looking at the repository. Only public repositories on GitHub, Azure DevOps, GitLab and Bitbucket can be copied. Never paste a token or password.</InfoTip></div><p className="mf-sr-only" id="repoUrl-help">Example: https://github.com/contoso/orders</p><input id="repoUrl" value={repoUrl} placeholder="https://github.com/contoso/orders" autoComplete="off" spellCheck={false} aria-invalid={Boolean(sourceError)} aria-describedby={sourceError ? "source-error repoUrl-help" : "repoUrl-help"} onChange={(event) => { setRepoUrl(event.target.value); setSourceError(""); }} /></div>
                  <div className="mf-field"><div className="mf-label-row"><label htmlFor="repoBranch">Branch</label><InfoTip label="the branch">Leave empty to copy the repository's default branch. Example: main</InfoTip><span className="mf-label-hint">Optional</span></div><input id="repoBranch" value={repoBranch} placeholder="main" autoComplete="off" spellCheck={false} onChange={(event) => setRepoBranch(event.target.value)} /></div>
                  <div className="mf-field"><div className="mf-label-row"><label htmlFor="repoFolder">Folder inside the repository</label><InfoTip label="the folder">Leave this empty and the workbench uses the folder it found the Forms files in. Example: legacy/forms</InfoTip><span className="mf-label-hint">Optional</span></div><input id="repoFolder" value={repoFolder} placeholder="legacy/forms" autoComplete="off" spellCheck={false} onChange={(event) => setRepoFolder(event.target.value)} /></div>
                  {repository && !workspace && <p className="mf-detected"><CheckCircle2 />Recognised {repository.label}</p>}
                  <button type="button" className="mf-secondary mf-acquire" disabled={acquiring || !repository} onClick={() => void acquire({ mode: "repo", repositoryUrl: repoUrl.trim(), branch: repoBranch.trim() || undefined })}>
                    <GitBranch />{acquiring ? "Copying..." : workspace ? "Copy again" : "Copy this repository"}
                  </button>
                </>}

                {value === "zip" && <div className="mf-drop">
                  <FileArchive />
                  <div>
                    <label className="mf-file" htmlFor="zipFile">{acquiring ? "Uploading..." : workspace ? "Choose a different zip" : "Choose a zip file"}</label>
                    <input id="zipFile" type="file" accept=".zip,application/zip" disabled={acquiring} aria-describedby={sourceError ? "source-error zipFile-help" : "zipFile-help"} onChange={(event) => { const file = event.target.files?.[0]; event.target.value = ""; if (file) void acquire({ mode: "zip", file }); }} />
                    <p className="mf-help" id="zipFile-help">Export your repository as a zip, or zip the folder holding the Forms files. Up to 256 MB.</p>
                  </div>
                </div>}

                {value === "manual" && <div className="mf-field-grid"><Field id="sourceRoot" label="Where the Forms files live" hint="Folder path" help="The folder a developer would find the .fmb files in, written from the top of your code repository. Example: legacy/forms" placeholder="legacy/forms" value={fields.sourceRoot} error={errors.sourceRoot} onChange={(value) => updateField("sourceRoot", value)} /></div>}

                {sourceError && <p id="source-error" className="mf-error" role="alert">{sourceError}</p>}

                {workspace && value !== "manual" && <div className="mf-findings">
                  <p><strong>{workspace.fileCount} files copied ({formatBytes(workspace.byteCount)}).</strong> Locked read-only in your private workspace.</p>
                  {workspace.artifacts.length > 0
                    ? <ul>{workspace.artifacts.map((artifact) => <li key={artifact.kind}><span className="mf-tag">{artifact.count}</span><div><strong>{EVIDENCE_NAMES[artifact.kind] ?? humanize(artifact.kind)}</strong><small>{artifact.example}</small></div></li>)}</ul>
                    : <p className="mf-help">Nothing recognisable was found, so nothing has been ticked for you. You can still continue and answer step 3 yourself.</p>}
                  <div className="mf-workspace-actions">
                    <p className="mf-help">Detected folder: {workspace.sourceRoot} · deleted automatically {new Date(workspace.expiresUtc).toLocaleTimeString()}</p>
                    <button type="button" className="mf-inline-link" onClick={() => setConsoleOpen(true)}>View activity log</button>
                    <button type="button" className="mf-inline-link danger" onClick={discardWorkspace}><Trash2 />Delete this copy now</button>
                  </div>
                </div>}
              </>}
            />

            <div className="mf-field-grid">
            <Field id="outputRoot" label="Where new code would go" hint="Folder path" help="The folder the generated Java, React and SQL would be written to when someone carries the plan out. Example: out/orders" placeholder="out/orders" value={fields.outputRoot} error={errors.outputRoot} onChange={(value) => updateField("outputRoot", value)} />
          </div></div>}

          {step === 1 && <div className="mf-step"><p className="mf-kicker">Destination</p><h1 ref={heading} tabIndex={-1}>Where should it land?</h1><p className="mf-lead">Every plan targets a React front end and a Java Spring Boot back end. Choose the Azure database, and how far ahead you want the plan to reach.</p>
            <ChoiceCards
              legend="Azure database"
              hint="Where the converted schema, data and PL/SQL would end up. Managed Instance keeps the most Oracle-like behaviour; PostgreSQL is the most portable."
              value={database}
              onChange={setDatabase}
              options={bootstrap.databaseTargets.map((option) => ({ value: option.target, name: option.name, description: option.guidance, glyph: <ServiceGlyph id={glyphForDatabase(option.target)} size={22} /> }))}
              renderDetail={(value) => {
                const option = bootstrap.databaseTargets.find((item) => item.target === value);
                if (!option) return null;
                const names = proposedResourceNames(fields.applicationName, value);
                return <dl className="mf-detail-list">
                  <div><dt>Azure service</dt><dd>{option.service}</dd></div>
                  <div><dt>{names.databaseHostLabel}</dt><dd><code>{names.databaseHost}</code></dd></div>
                  {names.database !== names.databaseHost && <div><dt>Database</dt><dd><code>{names.database}</code></dd></div>}
                </dl>;
              }}
            />
            <ChoiceCards
              legend="Planning depth"
              hint="How far the plan looks ahead. The workbench can then run the phases the planner authorizes, which write into your session workspace only."
              value={mode}
              onChange={setMode}
              options={bootstrap.executionModes.map((option) => ({ value: option.mode, name: option.name, description: option.description }))}
              renderDetail={(value) => {
                const option = bootstrap.executionModes.find((item) => item.mode === value);
                return option ? <p className="mf-detail-note">{option.executableHere ? "This depth is produced entirely by this workbench." : "This depth describes work that would need execution adapters, which are not built. The plan still covers it in writing."}</p> : null;
              }}
            />
          </div>}

          {step === 2 && <div className="mf-step"><p className="mf-kicker">Evidence</p><h1 ref={heading} tabIndex={-1}>What do you already have?</h1><p className="mf-lead">Tick each export or document you have produced and checked. Anything you leave unticked is listed as a blocker in the plan, so you can still generate a plan without them.</p>
            <div className="mf-meter"><FileCheck2 /><div><strong>{readyGroups} of {groups.length} required inputs ready</strong><progress max={groups.length} value={readyGroups} /></div></div>
            <fieldset className="mf-evidence-group"><legend>Needed to generate the plan</legend><div className="mf-evidence-list">{requiredEvidence.map((option) => <EvidenceChoice option={option} key={option.kind} />)}</div></fieldset>
            <details className="mf-optional"><summary>More detail, if you have it <span>{optionalEvidence.filter((item) => evidence.includes(item.kind)).length} selected</span></summary><div className="mf-evidence-list">{optionalEvidence.map((option) => <EvidenceChoice option={option} key={option.kind} />)}</div></details>
          </div>}

          {step === 3 && <div className="mf-step"><p className="mf-kicker">Approvals</p><h1 ref={heading} tabIndex={-1}>Who signs off on changes?</h1><p className="mf-lead">Optional. If you know who would approve a test run or a production cutover, name them here. They are written into the plan only. Nobody is contacted, and these names are not saved in your browser.</p>
            <div className="mf-assurance"><CheckCircle2 /><div><strong>Separate sign-offs</strong><p>Approving the plan never approves a test run, and neither one approves a production cutover.</p></div></div>
            <div className="mf-field-grid"><Field id="executionApprover" label="Test environment approver" hint="Optional" help="The person who would authorize changes in a sandbox or test environment." placeholder="approver@contoso.com" value={fields.executionApprover} error={errors.executionApprover} onChange={(value) => updateField("executionApprover", value)} /><Field id="productionApprover" label="Production approver" hint="Optional" help="The person who would authorize the final production cutover. Must be a different person." placeholder="cab-chair@contoso.com" value={fields.productionApprover} error={errors.productionApprover} onChange={(value) => updateField("productionApprover", value)} /></div>
          </div>}

          {step === 4 && <form className="mf-step" onSubmit={generatePlan} noValidate><p className="mf-kicker">Review</p><h1 ref={heading} tabIndex={-1}>Check the migration setup</h1><p className="mf-lead">Read your answers back, then generate the plan. Generating a plan changes nothing; running it is a separate decision on the next screen.</p>
            <section className="mf-review"><header><h2>Application</h2><button type="button" onClick={() => goToStep(0)}>Change</button></header><dl><ReviewRow label="Engagement" value={fields.engagementId} onEdit={() => goToStep(0)} /><ReviewRow label="Application" value={fields.applicationName} onEdit={() => goToStep(0)} /><ReviewRow label="Source" value={sourceLabel} onEdit={() => goToStep(0)} /><ReviewRow label="Output" value={fields.outputRoot} onEdit={() => goToStep(0)} /></dl></section>
            <section className="mf-review"><header><h2>Destination</h2><button type="button" onClick={() => goToStep(1)}>Change</button></header><dl><ReviewRow label="Database" value={selectedDatabase?.name ?? database} onEdit={() => goToStep(1)} /><ReviewRow label="Planning depth" value={selectedMode?.name ?? mode} onEdit={() => goToStep(1)} /></dl></section>
            <section className="mf-review"><header><h2>Evidence and approvals</h2><button type="button" onClick={() => goToStep(2)}>Change</button></header><dl><ReviewRow label="Verified evidence" value={`${evidence.length} artifact types (${readyGroups}/${groups.length} requirements)`} onEdit={() => goToStep(2)} /><ReviewRow label="Sandbox approver" value={fields.executionApprover} onEdit={() => goToStep(3)} /><ReviewRow label="Production approver" value={fields.productionApprover} onEdit={() => goToStep(3)} /></dl></section>
            <div className="mf-boundary"><LockKeyhole /><p><strong>Generating the plan writes nothing.</strong>Afterwards you can run the phases the planner authorizes; those write files into your private session workspace only. Sandbox database migration and production cutover are not available here.</p></div>
            <button className="mf-primary mf-generate" type="submit" disabled={submitting}><Sparkles />{submitting ? "Generating plan..." : "Generate migration plan"}</button>
          </form>}

          <div className="mf-actions">
            <button type="button" className="mf-back" onClick={() => (step === 0 ? setStarted(false) : setStep((current) => current - 1))}><ArrowLeft />Back</button>
            {step < STEPS.length - 1 && <button type="button" className="mf-primary" onClick={continueJourney}>Continue<ArrowRight /></button>}
          </div>
          <p className="mf-status" role="status" aria-live="polite">{status}</p>
        </section>
      </main> : <main className="mf-results" id="workspace">
        <div className="mf-result-heading"><div><p className="mf-kicker">Your plan</p><h1 ref={heading} tabIndex={-1}>Migration plan for {plan.plan.applicationName}</h1><p>You asked for <strong>{humanize(plan.plan.requestedMode)}</strong>; the workbench authorized <strong>{humanize(plan.plan.authorizedMode)}</strong>.</p></div><button type="button" className="mf-secondary" onClick={() => setPlan(null)}><Pencil />Edit setup</button></div>
        <section className="mf-metrics" aria-label="Plan summary"><article><span>Stages ready</span><strong>{plan.steps.filter((item) => item.state === "Ready" || item.state === "Current").length}/6</strong></article><article><span>Inputs provided</span><strong>{readyGroups}/{groups.length}</strong></article><article><span>Still missing</span><strong>{plan.plan.blockers.length}</strong></article><article><span>Azure services active</span><strong>{activeAzure}/{bootstrap.azureComponents.length}</strong></article></section>
        {plan.plan.blockers.length > 0 && <section className="mf-alert"><h2><AlertTriangle />What is still missing</h2><ul>{plan.plan.blockers.map((item) => <li key={item}>{item}</li>)}</ul></section>}
        <ArchitectureReveal applicationName={plan.plan.applicationName} database={database} databaseName={selectedDatabase?.name ?? database} />
        <section className="mf-result-section"><p className="mf-kicker">Lifecycle</p><h2>Six-stage modernization path</h2><ol className="mf-lifecycle">{plan.steps.map((item) => <li key={item.step}><span>{item.order}</span><div><strong>{item.title}</strong><small>{item.phases.map(humanize).join(" · ")}</small></div><em className={item.state === "Blocked" ? "mf-pill danger" : "mf-pill success"}>{item.state === "Blocked" ? <XCircle /> : <CheckCircle2 />}{item.state}</em></li>)}</ol></section>
        <section className="mf-result-section"><p className="mf-kicker">Phase detail</p><h2>Authorized work and blockers</h2><div className="mf-phases">{plan.plan.phases.map((phase) => <PhaseCard key={phase.phase} phase={phase} attribution={bootstrap.attribution?.phases.find((item) => item.phase === phase.phase)} />)}</div></section>
        {bootstrap.attribution && <AttributionSection attribution={bootstrap.attribution} />}
        {plan.azureFootprint && <AzureFootprintSection footprint={plan.azureFootprint} />}
        <section className="mf-result-section"><p className="mf-kicker">Execution</p><h2>Run the authorized phases</h2>
          <p className="mf-run-lead">This runs only the phases marked <strong>Planned</strong> above. Everything it produces is written into your private session workspace, which is deleted with the rest of your copy. Your repository, your databases, and your Azure resources are never touched.</p>
          {!runnable && <p className="mf-help" id="run-hint">{runHint}</p>}
          <button type="button" className="mf-primary mf-generate" disabled={!runnable || executing} aria-describedby={runnable ? undefined : "run-hint"} onClick={() => void runAuthorized()}>
            <Play />{executing ? "Running..." : "Run authorized phases"}
          </button>
          <p className="mf-status" role="status" aria-live="polite">{executing ? "The fleet is working. The activity log shows each step as it happens." : execution ? "Run finished." : ""}</p>
          {executionError && <p className="mf-error" role="alert">{executionError}</p>}
          {execution && <ExecutionReport result={execution} onPreview={(path) => void openArtifact(path)} />}
          {execution && <button type="button" className="mf-inline-link" onClick={() => { setConsoleMode("execution"); setConsoleOpen(true); }}>View activity log</button>}
        </section>
      </main>}

      {dialog && <div className="mf-dialog-backdrop" onMouseDown={(event) => event.target === event.currentTarget && setDialog(null)}><section className="mf-dialog" role="dialog" aria-modal="true" aria-labelledby="mf-dialog-title" ref={dialogBody}><header><div><p className="mf-kicker">{dialog === "agent" ? "Microsoft Foundry" : dialog === "artifact" ? "Generated artifact" : "Environment"}</p><h2 id="mf-dialog-title">{dialog === "agent" ? "Ask the migration fleet" : dialog === "artifact" ? artifact?.path ?? "Artifact" : "Azure readiness"}</h2></div><button type="button" onClick={() => setDialog(null)} aria-label="Close dialog"><X /></button></header>
        {dialog === "agent" ? <><p>Ask about evidence, target choices, or blockers. Messages are sent once and are not stored.</p><form onSubmit={askAgent}><label htmlFor="agent-message">Your question</label><textarea id="agent-message" rows={6} maxLength={8000} value={agentMessage} disabled={asking} onChange={(event) => setAgentMessage(event.target.value)} /><div className="mf-agent-actions"><span>{agentMessage.length}/8,000</span><button className="mf-primary" type="submit" disabled={!agentMessage.trim() || asking}><Sparkles />{asking ? "Asking..." : "Send question"}</button></div></form><p role="status">{agentStatus}</p>{agentAnswer && <div className="mf-answer">{agentAnswer}</div>}</>
          : dialog === "artifact" ? <><p className="mf-help">Read straight from your session workspace. At most 512 KB is shown.</p>{artifact?.error
            ? <p className="mf-error" role="alert">{artifact.error}</p>
            : artifact?.text
              ? <pre className="mf-artifact" tabIndex={0} aria-label={`Contents of ${artifact.path}`}>{artifact.text}</pre>
              : <p role="status" aria-live="polite">Loading the artifact...</p>}</>
          : <><div className="mf-assurance"><Cloud /><div><strong>{activeAzure} of {bootstrap.azureComponents.length} components active</strong><p>Only runtime-proven services are marked active.</p></div></div><div className="mf-components">{bootstrap.azureComponents.map((component) => <article key={component.id}><span className={component.state === "Active" ? "active" : ""}><ServiceGlyph id={glyphForComponent(component.id)} size={22} /></span><div><h3>{component.name}<InfoTip label={component.name}>{component.evidence}</InfoTip></h3><p>{component.role}</p><small className={component.state === "Active" ? "mf-pill success" : "mf-pill"}>{component.state === "Active" ? <CheckCircle2 /> : <AlertTriangle />}{humanize(component.state)}</small></div></article>)}</div><div className="mf-boundary compact"><LockKeyhole /><p><strong>Local generation only</strong>Analysis and PostgreSQL schema conversion run here and write into your session workspace. Sandbox database migration and production cutover are not available.</p></div></>}
      </section></div>}

      {consoleOpen && <MatrixConsole
        title={consoleMode === "execution" ? "Running the authorized phases" : sourceMode === "repo" ? "Cloning your repository" : "Expanding your upload"}
        subtitle="Live output from the server. Every line is something that actually happened."
        lines={consoleLines}
        running={acquiring || executing}
        onClose={() => setConsoleOpen(false)}
      />}
    </>
  );
}