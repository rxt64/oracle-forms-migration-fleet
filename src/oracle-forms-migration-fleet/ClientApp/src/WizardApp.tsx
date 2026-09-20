import { FormEvent, startTransition, useCallback, useEffect, useId, useRef, useState } from "react";
import {
  Activity,
  AlertTriangle,
  ArrowLeft,
  ArrowRight,
  Check,
  CheckCircle2,
  ChevronRight,
  ClipboardList,
  Cloud,
  Cpu,
  Database,
  FileArchive,
  FileBarChart2,
  FileCheck2,
  FileText,
  GitBranch,
  HelpCircle,
  Home,
  Info,
  ListChecks,
  LockKeyhole,
  Moon,
  PanelTop,
  Pencil,
  Play,
  Plus,
  ShieldCheck,
  Sparkles,
  Sun,
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
  enqueueRun,
  fetchArtifact,
  followRun,
  formatBytes,
  parseRepositoryUrl,
  proposedResourceNames,
  releaseSource,
  isTerminal,
  type ConsoleLine,
  type SourceWorkspace,
} from "./sourceClient";
import { ServiceGlyph, glyphForComponent, glyphForDatabase, glyphForHost } from "./ServiceGlyph";
import { ActivityPane } from "./MatrixConsole";
import { ArchitectureReveal } from "./ArchitectureReveal";
import { InfoTip } from "./InfoTip";
import { ProjectApprovals } from "./ProjectApprovals";
import { RunHistory } from "./RunHistory";
import {
  EVIDENCE_HELP,
  EVIDENCE_NAMES,
  GLOSSARY,
  STEP_GUIDANCE,
  help,
  type StepGuidance,
} from "./Guidance";
import "./portal.css";

const INTRO_KEY = "ofm-workbench-intro-v1";
const THEME_KEY = "ofm-workbench-theme-v1";
const STEPS = STEP_GUIDANCE.map((item) => item.name);
const SOURCE_CHOICES = [
  { value: "repo", name: "Clone a Git repository", description: help("choice.sourceRepo") },
  { value: "zip", name: "Upload a zip", description: help("choice.sourceZip") },
  { value: "manual", name: "Describe a folder path", description: help("choice.sourceManual") },
];

type ShellView = "overview" | "setup" | "results";

const emptyFields: RunFields = {
  engagementId: "",
  applicationName: "",
  sourceRoot: "",
  outputRoot: "",
  oracleFormsVersion: "unknown",
  oracleDatabaseVersion: "unknown",
  executionApprover: "",
  productionApprover: "",
};

const exampleFields: RunFields = {
  engagementId: "ENG-0042",
  applicationName: "ORDERS",
  sourceRoot: "legacy/forms",
  outputRoot: "out/orders",
  oracleFormsVersion: "unknown",
  oracleDatabaseVersion: "unknown",
  executionApprover: "",
  productionApprover: "",
};

// Values the server's version catalog canonicalises. "unknown" stays first: it is the correct answer
// until the release has actually been established, and it never blocks planning.
const FORMS_VERSIONS = [
  { value: "unknown", label: "Not established yet" },
  { value: "6i", label: "Forms 6i" },
  { value: "9i", label: "Forms 9i" },
  { value: "10g", label: "Forms 10g (10.1.2)" },
  { value: "11g", label: "Forms 11g" },
  { value: "12c", label: "Forms 12c (12.2.1)" },
];

const DATABASE_VERSIONS = [
  { value: "unknown", label: "Not established yet" },
  { value: "6", label: "Oracle 6" },
  { value: "7", label: "Oracle 7" },
  { value: "8i", label: "Oracle 8i" },
  { value: "9i", label: "Oracle 9i" },
  { value: "10g", label: "Oracle 10g" },
  { value: "11g", label: "Oracle 11g" },
  { value: "12c", label: "Oracle 12c" },
  { value: "18c", label: "Oracle 18c" },
  { value: "19c", label: "Oracle 19c" },
  { value: "21c", label: "Oracle 21c" },
  { value: "23", label: "Oracle 23ai / Free 23" },
];

function queryFields(): RunFields {
  const query = new URLSearchParams(window.location.search);
  return Object.fromEntries(
    Object.entries(emptyFields).map(([key, fallback]) => [key, query.get(key) ?? fallback]),
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

function VersionSelect({
  id,
  label,
  help,
  options,
  value,
  onChange,
}: {
  id: keyof RunFields;
  label: string;
  help: string;
  options: { value: string; label: string }[];
  value: string;
  onChange: (value: string) => void;
}) {
  return (
    <div className="mf-field">
      <div className="mf-label-row">
        <label htmlFor={id}>{label}</label>
        <InfoTip label={label.toLowerCase()}>{help}</InfoTip>
        <span className="mf-label-hint">Optional</span>
      </div>
      <p className="mf-sr-only" id={`${id}-help`}>{help}</p>
      <select id={id} value={value} aria-describedby={`${id}-help`} onChange={(event) => onChange(event.target.value)}>
        {options.map((option) => <option value={option.value} key={option.value}>{option.label}</option>)}
      </select>
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

/** The same four questions on every setup step: goal, input, what runs, what comes out. */
function StepPurpose({ guide }: { guide: StepGuidance }) {
  return (
    <dl className="mf-step-purpose">
      <div><dt>Goal</dt><dd>{guide.goal}</dd></div>
      <div><dt>What you provide</dt><dd>{guide.input}</dd></div>
      <div><dt>What the workbench does</dt><dd>{guide.platform}</dd></div>
      <div><dt>What you get</dt><dd>{guide.output}</dd></div>
    </dl>
  );
}

/**
 * Copies a closing frame onto the transcript without its payload.
 *
 * The typed fields have to survive: they are how the activity pane tells a stream that finished from
 * one that merely stopped. The `workspace` and `result` payloads deliberately do not, because the
 * transcript is a list of messages and not a place to park state.
 */
function transcriptLine(frame: object, fallbackText: string): ConsoleLine {
  const line = frame as ConsoleLine;
  return {
    level: line.level,
    text: line.text || fallbackText,
    sequence: line.sequence,
    timestampUtc: line.timestampUtc,
    operation: line.operation,
    action: line.action,
    state: line.state,
    purpose: line.purpose,
    observed: line.observed,
    nextAction: line.nextAction,
    artifactKind: line.artifactKind,
    artifactCount: line.artifactCount,
  };
}

const ENGINE_LABELS: Record<PhaseEngine, string> = {
  NotImplemented: "No adapter",
  Deterministic: "Deterministic code",
  DeterministicWithModelReview: "Deterministic code + model review",
  DeterministicWithBoundedModelRepair: "Deterministic code + compiler-bounded model repair",
};

function PhaseCard({ phase, attribution }: { phase: Phase; attribution?: PhaseAttribution }) {
  const blocked = phase.status.startsWith("Blocked");
  return (
    <article className="mf-phase">
      <header>
        <div><p className="mf-kicker">{humanize(phase.owner)} · {humanize(phase.mutation)}</p><h3>{humanize(phase.phase)}</h3></div>
        <span className={blocked ? "mf-pill danger" : "mf-pill neutral"}>{blocked ? <XCircle /> : <Info />}{humanize(phase.status)}</span>
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
          No adapter here provisions an Azure resource, so a sign-in could not deploy this list. The sandbox
          load is the one thing that does reach a database, and it writes only to the sandbox the host was
          configured with, after a named execution approval. Wiring provisioning before the cross-tenant
          consent, scoping, and audit trail above are agreed would create a path to write into a customer
          subscription that nobody had reviewed. Take this list to whoever owns the subscription instead.
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
  BlockedByDependency: "Blocked by a failed prerequisite",
};

function ExecutionReport({ result, onPreview, workspaceId }: { result: ExecutionResult; onPreview: (path: string) => void; workspaceId?: string }) {
  const executed = result.phases.filter((phase) => phase.state === "Executed").length;
  const unsuccessful = result.phases.filter((phase) => phase.state !== "Executed");
  const problems = unsuccessful.filter((phase) => phase.state === "Failed" || phase.state === "BlockedByDependency");
  return (
    <div className="mf-run-report">
      <p className="mf-run-summary">
        <strong>{executed} of {result.phases.length} phases ran.</strong> The planner authorized {humanize(result.authorizedMode)} of
        the requested {humanize(result.requestedMode)}. Everything written went into <code>{result.outputRoot}</code> inside your
        private session workspace.
      </p>

      {/* Failures stay in the open. Everything that merely succeeded is one disclosure away. */}
      {problems.length > 0 && <ul className="mf-run-phases">
        {problems.map((phase) => (
          <li key={phase.phase}>
            <div className="mf-run-phase-head">
              <strong>{humanize(phase.phase)}</strong>
              <span className="mf-pill danger"><XCircle />{PHASE_STATE_LABELS[phase.state] ?? phase.state}</span>
            </div>
            {phase.detail && <p>{phase.detail}</p>}
            {phase.findings.length > 0 && <ul className="mf-run-findings">{phase.findings.map((finding) => <li key={finding}>{finding}</li>)}</ul>}
          </li>
        ))}
      </ul>}

      <details className="mf-optional">
        <summary>Every phase in this run <span>{result.phases.length}</span></summary>
        <ul className="mf-run-phases">
          {result.phases.map((phase) => (
            <li key={phase.phase}>
              <div className="mf-run-phase-head">
                <strong>{humanize(phase.phase)}</strong>
                <span className={phase.state === "Executed" ? "mf-pill success" : phase.state === "Failed" || phase.state === "BlockedByDependency" ? "mf-pill danger" : "mf-pill warn"}>
                  {phase.state === "Executed" ? <CheckCircle2 /> : phase.state === "Failed" || phase.state === "BlockedByDependency" ? <XCircle /> : <AlertTriangle />}
                  {PHASE_STATE_LABELS[phase.state] ?? phase.state}
                </span>
              </div>
              {phase.detail && <p>{phase.detail}</p>}
              {phase.findings.length > 0 && <ul className="mf-run-findings">{phase.findings.map((finding) => <li key={finding}>{finding}</li>)}</ul>}
            </li>
          ))}
        </ul>
      </details>

      {result.artifacts.length === 0
        ? <p className="mf-help">Nothing was written, so there is nothing to open.</p>
        : <>
          <details className="mf-optional">
            <summary>Files this run wrote <span>{result.artifacts.length}</span></summary>
            <ul className="mf-run-artifacts">{result.artifacts.map((artifact) => (
              <li key={artifact.path}>
                <div><code>{artifact.path}</code><small>{artifact.description}</small></div>
                {artifact.previewable
                  ? <span className="mf-command-help"><button type="button" className="mf-inline-link" onClick={() => onPreview(artifact.path)}><FileText />Open</button><InfoTip label="opening a generated file">{help("action.openArtifact")}</InfoTip></span>
                  : <span className="mf-help">Not a text file</span>}
              </li>
            ))}</ul>
          </details>
          {workspaceId && <p className="mf-help">Your session workspace is deleted after four hours. Nothing leaves here except what the run wrote; your source copy is not included.</p>}
        </>}

      <p className="mf-run-attestation-note">
        {result.attestations.length === 0
          ? <><ShieldCheck aria-hidden="true" />No phase in this run produces a signable attestation, so none was recorded. Nothing here is evidence that the output builds, runs, or behaves like the original.</>
          : <><ShieldCheck aria-hidden="true" />{result.attestations.length} attestation{result.attestations.length === 1 ? "" : "s"} recorded: {result.attestations.map((attestation) => humanize(attestation.kind)).join(", ")}.</>}
      </p>
    </div>
  );
}

/**
 * What this run has and has not achieved, as separate claims.
 *
 * Generated, built, tested, deployed and behaviour-verified are different things. Each state is
 * derived from its own executed phase, artifact, or successful attestation.
 */
function CapabilityStates({ execution }: { execution: ExecutionResult | null }) {
  const generated = execution?.artifacts.length ?? 0;
  const normalizedForms = execution?.artifacts.some((artifact) => artifact.path.endsWith("/forms-ir.json")) ?? false;
  const build = execution?.phases.find((phase) => phase.phase === "BuildAndStaticValidation");
  const behavior = execution?.attestations.find((attestation) => attestation.kind === "DifferentialBehaviorTestPassed" && attestation.succeeded);
  const rows: Array<{ label: string; state: "done" | "pending" | "retained" | "failed" | "unavailable"; detail: string }> = [
    { label: "Plan generated", state: "done", detail: "The deterministic planner produced the plan below." },
    {
      label: "Files generated",
      state: generated > 0 ? "done" : "pending",
      detail: generated > 0
        ? `${generated} file${generated === 1 ? "" : "s"} written into your session workspace.`
        : "No authorized run has written a file in this tab yet.",
    },
    {
      label: "Code compiled",
      state: build?.state === "Executed" ? "done" : build?.state === "Failed" ? "failed" : "pending",
      detail: build?.state === "Executed"
        ? "The build and static-validation adapter completed for this run."
        : build?.state === "Failed"
          ? "Build or static validation failed; inspect the phase report before continuing."
          : "No build and static-validation phase has completed in this tab yet.",
    },
    {
      label: "Behaviour tested",
      state: behavior ? "done" : "unavailable",
      detail: behavior ? behavior.summary : "No successful differential behavior-test attestation is recorded.",
    },
    ...(normalizedForms ? [{
      label: "Source logic retained; not yet converted",
      state: "retained" as const,
      detail: help("status.triggerRetained"),
    }] : []),
    { label: "Deployed", state: "unavailable", detail: "No adapter provisions or deploys an Azure resource from this workbench." },
    {
      label: "Behaviour verified",
      state: behavior ? "done" : "unavailable",
      detail: behavior
        ? `Successful differential behavior-test attestation: ${behavior.summary}`
        : "Conversion, generation, sandbox migration, reconciliation, and acceptance do not by themselves verify behavior.",
    },
  ];

  return (
    <ul className="mf-capabilities">
      {rows.map((row) => (
        <li key={row.label} className={row.state}>
          {row.state === "done" ? <CheckCircle2 aria-hidden="true" /> : row.state === "failed" ? <XCircle aria-hidden="true" /> : row.state === "pending" ? <AlertTriangle aria-hidden="true" /> : row.state === "retained" ? <Info aria-hidden="true" /> : <LockKeyhole aria-hidden="true" />}
          <div>
            <strong>{row.label}</strong>
            <span className="mf-capability-state">{row.state === "done" ? "Yes" : row.state === "failed" ? "Failed" : row.state === "pending" ? "Not yet" : row.state === "retained" ? "Not converted" : "Not available here"}</span>
            <small>{row.detail}</small>
          </div>
        </li>
      ))}
    </ul>
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

  // The project the operator is acting in. It travels as an identifier beside the run request; the
  // server resolves the membership and the target profile behind it.
  const [projectId, setProjectId] = useState<string | null>(null);
  const [consoleLines, setConsoleLines] = useState<ConsoleLine[]>([]);
  const [consoleOpen, setConsoleOpen] = useState(false);
  const [acquiring, setAcquiring] = useState(false);
  const [sourceError, setSourceError] = useState("");
  const [step, setStep] = useState(0);
  const [view, setView] = useState<ShellView>(() => {
    try { return localStorage.getItem(INTRO_KEY) === "1" ? "setup" : "overview"; } catch { return "overview"; }
  });
  const [theme, setTheme] = useState<"dark" | "light">(() => (document.documentElement.dataset.theme === "light" ? "light" : "dark"));
  const [errors, setErrors] = useState<FieldErrors>({});
  const [status, setStatus] = useState("Loading migration catalog...");
  const [plan, setPlan] = useState<PlanResponse | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [executing, setExecuting] = useState(false);
  const [execution, setExecution] = useState<ExecutionResult | null>(null);
  const [executionError, setExecutionError] = useState("");
  const [activeRunId, setActiveRunId] = useState<string | null>(null);
  const [consoleMode, setConsoleMode] = useState<"source" | "execution">("source");
  const [artifact, setArtifact] = useState<{ path: string; text: string; error: string } | null>(null);
  const [dialog, setDialog] = useState<"agent" | "azure" | "artifact" | "glossary" | null>(null);
  const [agentMessage, setAgentMessage] = useState("");
  const [agentAnswer, setAgentAnswer] = useState("");
  const [agentStatus, setAgentStatus] = useState("");
  const [asking, setAsking] = useState(false);
  const heading = useRef<HTMLHeadingElement>(null);
  const activityButton = useRef<HTMLButtonElement>(null);
  const dialogBody = useRef<HTMLElement>(null);
  const dialogOpener = useRef<HTMLElement | null>(null);
  const acquisition = useRef<AbortController | null>(null);
  const run = useRef<AbortController | null>(null);
  const closeActivity = useCallback(() => setConsoleOpen(false), []);

  useEffect(() => {
    let cancelled = false;
    fetch("/api/workbench/bootstrap")
      .then(async (response) => {
        if (!response.ok) throw new Error(`Bootstrap failed (${response.status}).`);
        return response.json() as Promise<Bootstrap>;
      })
      .then((catalog) => {
        if (cancelled) return;
        const databases = catalog.databaseTargets.map((option) => option.target);
        setBootstrap(catalog);
        setDatabase(databases[0] ?? "");
        setMode("PlanOnly");
        setEvidence([]);
        setStatus("Ready when you are.");
      })
      .catch((error: Error) => setStatus(error.message));
    return () => { cancelled = true; };
  }, []);

  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    try { localStorage.setItem(THEME_KEY, theme); } catch { /* storage unavailable */ }
  }, [theme]);

  useEffect(() => {
    document.title = view === "results"
      ? "Plan and results · Migration Fleet"
      : view === "setup"
        ? `Step ${step + 1} of ${STEPS.length}: ${STEPS[step]} · Migration Fleet`
        : "Overview · Migration Fleet";
    requestAnimationFrame(() => heading.current?.focus());
  }, [step, view]);

  useEffect(() => {
    if (!dialog) return;
    dialogOpener.current = document.activeElement as HTMLElement | null;
    const body = dialogBody.current;
    const focusable = () => Array.from(
      body?.querySelectorAll<HTMLElement>('button:not([disabled]), a[href], input:not([disabled]), textarea:not([disabled]), select:not([disabled]), details > summary, [tabindex]:not([tabindex="-1"])') ?? [])
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
  const runnable = Boolean(projectId) && Boolean(workspace) && sourceMode !== "manual" && plannedPhases > 0;
  const runHint = !projectId
    ? "Select or create a project before running. Project membership owns the source copy and every generated artifact."
    : sourceMode === "manual"
    ? "A typed folder path only describes where the code lives. Copy a repository or upload a zip on Your application to give the fleet something to read."
    : !workspace
      ? "Copy a repository or upload a zip on Your application first. The fleet only reads the copy held for your session."
      : plannedPhases === 0
        ? "The planner authorized no phase, so there is nothing to run. Clear the blockers above, then generate the plan again."
        : "";

  /**
   * Runs a server-side clone or upload and mirrors the server's own progress lines into the
   * console. The stream is the only source of truth for what happened.
   */
  async function acquire(request: { mode: "repo"; repositoryUrl: string; branch?: string } | { mode: "zip"; file: File }) {
    if (!projectId) {
      setSourceError("Select or create a project before copying source.");
      return;
    }

    acquisition.current?.abort();
    const controller = new AbortController();
    acquisition.current = controller;

    if (workspace) void releaseSource(workspace.workspaceId, projectId).catch(() => undefined);
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
      for await (const event of acquireSource(request, projectId, controller.signal)) {
        if (acquisition.current !== controller || controller.signal.aborted) {
          if (event.level === "done" && "workspace" in event) {
            void releaseSource(event.workspace.workspaceId, projectId).catch(() => undefined);
          }
          return;
        }
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
          setConsoleLines((current) => [...current, transcriptLine(
            event,
            `${acquired.fileCount} files (${formatBytes(acquired.byteCount)}) ready in workspace ${acquired.workspaceId}.`,
          )]);
          setStatus(recognised.length > 0
            ? `Copied your source. ${recognised.length} matching ${recognised.length === 1 ? "item" : "items"} on the source checklist ${recognised.length === 1 ? "is" : "are"} ticked for you.`
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
    if (!workspace || !projectId) return;
    void releaseSource(workspace.workspaceId, projectId).catch(() => undefined);
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

  function clearWorkspaceEvidence(preserveManualEvidence: boolean) {
    const activeAcquisition = acquisition.current;
    acquisition.current = null;
    activeAcquisition?.abort();
    if (workspace && projectId) void releaseSource(workspace.workspaceId, projectId).catch(() => undefined);
    const manual = preserveManualEvidence ? evidence.filter((kind) => !autoEvidence.includes(kind)) : [];
    setWorkspace(null);
    setEvidence(manual);
    setAutoEvidence([]);
    setConsoleLines([]);
    setConsoleOpen(false);
    setAcquiring(false);
    setExecution(null);
    setExecutionError("");
  }

  function changeSourceMode(nextMode: string) {
    if (nextMode !== sourceMode) clearWorkspaceEvidence(true);
    setSourceMode(nextMode);
    setSourceError("");
  }

  function beginSetup(prefill?: RunFields) {
    clearWorkspaceEvidence(false);
    setFields(prefill ?? emptyFields);
    setDatabase(bootstrap?.databaseTargets[0]?.target ?? "");
    setMode("PlanOnly");
    setSourceMode(prefill ? "manual" : "repo");
    setRepoUrl("");
    setRepoBranch("");
    setRepoFolder("");
    setSourceError("");
    setPlan(null);
    try { localStorage.setItem(INTRO_KEY, "1"); } catch { /* storage unavailable */ }
    setErrors({});
    setStep(0);
    setView("setup");
    setStatus(prefill
      ? "Example values filled in. They describe no real application; change anything you like."
      : "Answers are held in this browser tab only. Nothing is saved on the server.");
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
    setStatus("Answers held in this browser tab.");
  }

  function goToStep(next: number) {
    setPlan(null);
    setExecution(null);
    setExecutionError("");
    setStep(next);
    setView("setup");
  }

  function toggleEvidence(kind: string) {
    // Once the operator has ruled on a kind themselves it is their answer, not a detected one.
    setAutoEvidence((current) => current.filter((item) => item !== kind));
    setEvidence((current) => current.includes(kind) ? current.filter((item) => item !== kind) : [...current, kind]);
  }

  /** The exact request shape both the planner and the executor accept. */
  function runRequestBody() {
    const pendingContact = (contact: string) => ({
      decision: "Pending",
      approverId: null,
      notes: contact.trim() ? `Planning contact: ${contact.trim()}` : null,
    });
    return {
      engagementId: fields.engagementId.trim(),
      applicationName: fields.applicationName.trim(),
      requestedMode: mode,
      target: { frontEnd: "React", backEnd: "JavaSpringBoot", database },
      oracleFormsVersion: fields.oracleFormsVersion.trim() || "unknown",
      oracleDatabaseVersion: fields.oracleDatabaseVersion.trim() || "unknown",
      sourceRoot: sourceRoot.trim(),
      outputRoot: fields.outputRoot.trim(),
      evidence: evidence.map((kind, index) => {
        const detected = autoEvidence.includes(kind);
        return {
          id: `EV-${index + 1}`,
          kind,
          source: detected ? "source-indexer" : "operator-declared",
          summary: detected ? `${humanize(kind)} detected in the copied source.` : `${humanize(kind)} declared by the operator; not independently verified.`,
          isVerified: detected,
          signals: [],
        };
      }),
      planApproval: pendingContact(""),
      executionApproval: pendingContact(fields.executionApprover),
      productionApproval: pendingContact(fields.productionApprover),
      attestations: [],
    };
  }

  async function generatePlan(event: FormEvent) {
    event.preventDefault();
    if (!projectId) {
      setStatus("Select or create a project before planning.");
      return;
    }
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
        // The identifier lets the server derive source facts from the copy it took itself. Without a
        // copy it plans from declarations alone, and nothing in the plan is marked verified.
        body: JSON.stringify(workspace
          ? { ...runRequestBody(), workspaceId: workspace.workspaceId, projectId }
          : { ...runRequestBody(), projectId }),
      });
      if (!response.ok) throw new Error(`Planner failed (${response.status}).`);
      const result = await response.json() as PlanResponse;
      startTransition(() => setPlan(result));
      setView("results");
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
  async function consumeExecution(
    events: ReturnType<typeof followRun>,
    afterSequence: number,
  ) {
    let cursor = afterSequence;
    let terminal = false;
    for await (const event of events) {
      const sequence = event.sequence ?? cursor + 1;
      if (sequence <= cursor) continue;
      if (sequence !== cursor + 1) {
        throw new Error(`Run activity has a sequence gap after ${cursor}. Reopen the run to replay its retained history.`);
      }
      cursor = sequence;
      terminal = isTerminal(event);
      if ("result" in event) {
        const result = event.result;
        const ran = result.phases.filter((phase) => phase.state === "Executed").length;
        const unsuccessful = event.level === "error";
        setExecution(result);
        setConsoleLines((current) => [...current, transcriptLine(
          event,
          `${ran} phase${ran === 1 ? "" : "s"} executed. ${result.artifacts.length} file${result.artifacts.length === 1 ? "" : "s"} written.`,
        )]);
        setStatus(unsuccessful
          ? `The run did not complete successfully. ${ran} phase${ran === 1 ? "" : "s"} ran and ${result.artifacts.length} file${result.artifacts.length === 1 ? " was" : "s were"} retained.`
          : `${ran} phase${ran === 1 ? "" : "s"} ran and wrote ${result.artifacts.length} file${result.artifacts.length === 1 ? "" : "s"} into your session workspace.`);
        if (unsuccessful) setExecutionError(event.observed || "The run did not complete successfully.");
      } else {
        setConsoleLines((current) => [...current, event as ConsoleLine]);
        if (event.level === "error") setExecutionError(event.text);
      }
    }
    return { cursor, terminal };
  }

  async function followWithReconnect(runId: string, controller: AbortController, start = 0) {
    let cursor = start;
    for (let attempt = 0; attempt < 4; attempt += 1) {
      try {
        const followed = await consumeExecution(followRun(runId, cursor, controller.signal), cursor);
        cursor = followed.cursor;
        if (followed.terminal || controller.signal.aborted) return;
      } catch (error) {
        if (controller.signal.aborted || attempt === 3) throw error;
      }
      setStatus("The activity connection closed. Reconnecting to the retained run...");
      await new Promise((resolve) => window.setTimeout(resolve, 500 * (attempt + 1)));
    }
    throw new Error("The run outcome is still unknown after reconnecting. Reopen it from run history to replay retained activity.");
  }

  async function runAuthorized() {
    if (!workspace || !plan || !projectId) return;
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
      const durableRunId = await enqueueRun({
        ...runRequestBody(),
        workspaceId: workspace.workspaceId,
        // Identifiers only. The server resolves membership and the immutable target profile itself.
        projectId,
        targetProfileId: "sandbox",
      });
      setActiveRunId(durableRunId);
      await followWithReconnect(durableRunId, controller);
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

  async function reopenRun(runId: string) {
    run.current?.abort();
    const controller = new AbortController();
    run.current = controller;
    setActiveRunId(runId);
    setExecuting(true);
    setExecution(null);
    setExecutionError("");
    setConsoleMode("execution");
    setConsoleLines([]);
    setConsoleOpen(true);
    setStatus("Reopening retained run activity...");
    try {
      await followWithReconnect(runId, controller);
    } catch (error) {
      if (!controller.signal.aborted) {
        const message = error instanceof Error ? error.message : "The run activity could not be reopened.";
        setConsoleLines((current) => [...current, { level: "error", text: message }]);
        setExecutionError(message);
      }
    } finally {
      if (run.current === controller) setExecuting(false);
    }
  }

  async function openArtifact(path: string) {
    if ((!workspace && !activeRunId) || !projectId) return;
    setArtifact({ path, text: "", error: "" });
    setDialog("artifact");
    try {
      const text = await fetchArtifact(workspace?.workspaceId ?? "", path, projectId, undefined, activeRunId);
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

  const activeView: ShellView = view === "results" && !plan ? "setup" : view;
  const guide = STEP_GUIDANCE[step];
  const activityAvailable = consoleLines.length > 0 || acquiring || executing;

  const phasesExecuted = execution?.phases.filter((phase) => phase.state === "Executed").length ?? 0;
  const failedPhases = execution?.phases.filter((phase) =>
    phase.state === "Failed" ||
    phase.state === "BlockedByDependency" ||
    (phase.plannedStatus === "Planned" && phase.state !== "Executed")).length ?? 0;
  const filesWritten = execution?.artifacts.length ?? 0;
  const blockerCount = plan?.plan.blockers.length ?? 0;

  /**
   * The one sentence at the top of the results page.
   *
   * A finished run is never described as a success on its own: writing files is not compiling them,
   * and the detail line says so rather than leaving the reader to assume.
   */
  const outcome: { tone: "info" | "attention" | "danger" | "success"; icon: typeof Info; headline: string; detail: string } =
    executing
      ? {
        tone: "info",
        icon: Activity,
        headline: "The run is working now.",
        detail: "Activity shows every line the server sends. Results appear here when the run reports an outcome.",
      }
      : execution
        ? failedPhases > 0 || phasesExecuted === 0
          ? {
            tone: "danger",
            icon: XCircle,
            headline: phasesExecuted === 0
              ? "The run finished without completing any phase."
              : `Run finished with ${failedPhases} phase${failedPhases === 1 ? "" : "s"} that did not complete.`,
            detail: `${phasesExecuted} of ${execution.phases.length} phases ran and ${filesWritten} file${filesWritten === 1 ? "" : "s"} were written into your session workspace. Read the failed phases before running anything else.`,
          }
          : {
            tone: "success",
            icon: CheckCircle2,
            headline: `Run finished: ${phasesExecuted} of ${execution.phases.length} phases ran.`,
            detail: `${filesWritten} file${filesWritten === 1 ? "" : "s"} written into your private session workspace. Nothing in this run compiled, deployed, or behaviour-verified that output.`,
          }
        : blockerCount > 0
          ? {
            tone: "attention",
            icon: AlertTriangle,
            headline: `Plan generated with ${blockerCount} blocker${blockerCount === 1 ? "" : "s"}.`,
            detail: runnable
              ? `${plannedPhases} phase${plannedPhases === 1 ? "" : "s"} can still run from here; the blockers below are what the rest of the plan is waiting on.`
              : runHint || "No phase can run from this setup.",
          }
          : runnable
            ? {
              tone: "info",
              icon: Info,
              headline: "Plan generated with no blockers.",
              detail: `The planner authorized ${plannedPhases} phase${plannedPhases === 1 ? "" : "s"} you can run from here.`,
            }
            : {
              tone: "info",
              icon: Info,
              headline: "Plan generated.",
              detail: runHint || "No phase can run from this setup.",
            };

  /**
   * Exactly one primary action, chosen for the state the page is actually in. A results page that
   * offers run, download, and edit with equal weight leaves the operator to work out which is safe.
   */
  const primaryAction: {
    kind: "run" | "edit" | "download";
    label: string;
    explanation: string;
    help: string;
    hint?: string;
    disabled?: boolean;
    href?: string;
    run?: () => void;
  } = executing
    ? {
      kind: "run",
      label: "Running...",
      disabled: true,
      explanation: "The run the planner authorized is under way. Nothing else should be started until it reports an outcome.",
      help: help("action.runAuthorized"),
      run: () => undefined,
    }
    : execution && filesWritten > 0
      ? {
        kind: "download",
        label: "Download what this run generated",
        href: `/api/workbench/export?workspaceId=${encodeURIComponent(workspace?.workspaceId ?? "")}&projectId=${encodeURIComponent(projectId ?? "")}`,
        explanation: "The safest next step is to take the output off this host. Your session workspace is deleted four hours after the source was copied.",
        help: help("action.downloadOutput"),
      }
      : execution
        ? {
          kind: "edit",
          label: "Edit setup",
          explanation: "This run wrote nothing, so there is nothing to take away. Change the setup and generate the plan again.",
          help: help("action.editSetup"),
          run: () => goToStep(0),
        }
        : runnable
          ? {
            kind: "run",
            label: "Run authorized phases",
            explanation: "This runs only the phases the planner marked Planned. It is the next step that produces anything.",
            help: help("action.runAuthorized"),
            run: () => void runAuthorized(),
          }
          : {
            kind: "edit",
            label: "Edit setup",
            explanation: "No phase can run from this setup, so the next useful step is to change the setup rather than to start a run.",
            hint: runHint,
            help: help("action.editSetup"),
            run: () => goToStep(0),
          };

  const trail = activeView === "setup"
    ? ["Migration setup", guide.name]
    : activeView === "results" ? ["Plan & results"] : ["Overview"];

  const sections = [
    { id: "overview", label: "Overview", icon: <Home aria-hidden="true" />, enabled: true, reason: "" },
    { id: "setup", label: "Migration setup", icon: <ClipboardList aria-hidden="true" />, enabled: true, reason: "" },
    { id: "activity", label: "Activity", icon: <Activity aria-hidden="true" />, enabled: activityAvailable, reason: "Nothing has run in this tab yet." },
    { id: "results", label: "Plan & results", icon: <FileBarChart2 aria-hidden="true" />, enabled: Boolean(plan), reason: "Generate a plan first." },
  ] as const;

  function openSection(id: (typeof sections)[number]["id"]) {
    if (id === "activity") { setConsoleOpen(true); return; }
    if (id === "overview") { setView("overview"); return; }
    if (id === "results") { setView("results"); return; }
    setView("setup");
  }

  return (
    <>
      <a className="mf-skip" href="#workspace">Skip to main content</a>
      <header className="mf-topbar">
        <div className="mf-brand">
          <span className="mf-logo" aria-hidden="true"><Database /><ArrowRight /><PanelTop /></span>
          <span><strong>Migration Fleet</strong><small>Oracle Forms modernization workbench</small></span>
        </div>
        <p className="mf-env">
          <ShieldCheck aria-hidden="true" />
          <span>Planning workbench · session workspace</span>
          <InfoTip label="this environment">This workbench plans migrations and writes into a private per-session folder on the server. It is not an Azure portal extension, it holds no saved project, and it is signed in to no customer subscription.</InfoTip>
        </p>
        <div className="mf-header-actions">
          <button type="button" onClick={() => setDialog("glossary")} title="Open the glossary"><HelpCircle aria-hidden="true" /><span>Help</span></button>
          <button type="button" aria-pressed={theme === "light"} onClick={() => setTheme((current) => (current === "dark" ? "light" : "dark"))}>
            {theme === "dark" ? <Sun aria-hidden="true" /> : <Moon aria-hidden="true" />}
            <span>{theme === "dark" ? "Light theme" : "Dark theme"}</span>
          </button>
        </div>
      </header>

      <div className="mf-shell">
        <nav className="mf-servicenav" aria-label="Workbench sections">
          <p>Migration Fleet</p>
          <ul>
            {sections.map((section) => (
              <li key={section.id}>
                <button
                  type="button"
                  disabled={!section.enabled}
                  title={section.enabled ? undefined : section.reason}
                  aria-current={section.id === activeView ? "page" : undefined}
                  onClick={() => openSection(section.id)}
                >
                  {section.icon}<span>{section.label}</span>
                </button>
              </li>
            ))}
          </ul>
        </nav>

        <div className="mf-pane">
          <nav className="mf-breadcrumb" aria-label="Breadcrumb">
            <ol>
              <li>Migration Fleet</li>
              {trail.map((crumb, index) => (
                <li key={crumb}>
                  <ChevronRight aria-hidden="true" />
                  <span aria-current={index === trail.length - 1 ? "page" : undefined}>{crumb}</span>
                </li>
              ))}
            </ol>
          </nav>

          <div className="mf-commandbar" role="toolbar" aria-label="Commands">
            {activeView !== "setup" && <button type="button" className="mf-command accent" disabled={!projectId} onClick={() => beginSetup()}><Plus aria-hidden="true" />New migration</button>}
            {activeView === "results" && <button type="button" className="mf-command" onClick={() => goToStep(0)}><Pencil aria-hidden="true" />Edit setup</button>}
            <button type="button" ref={activityButton} className="mf-command" disabled={!activityAvailable} aria-describedby={activityAvailable ? undefined : "cmd-activity-note"} onClick={() => setConsoleOpen(true)}><Activity aria-hidden="true" />Activity</button>
            <InfoTip label="activity">{help("action.viewActivity")}</InfoTip>
            <button type="button" className="mf-command" onClick={() => setDialog("azure")}><Cloud aria-hidden="true" />Azure status</button>
            <button type="button" className="mf-command" disabled={!bootstrap.agentChatAvailable} aria-describedby={bootstrap.agentChatAvailable ? undefined : "cmd-agent-note"} onClick={() => setDialog("agent")}><Sparkles aria-hidden="true" />Ask AI</button>
            {!activityAvailable && <span className="mf-command-note" id="cmd-activity-note">Activity opens once something has run in this tab.</span>}
            {!bootstrap.agentChatAvailable && <span className="mf-command-note" id="cmd-agent-note">Ask AI needs a configured Foundry deployment; this host has none.</span>}
          </div>

      {activeView === "overview" ? <main className="mf-intro" id="workspace">
        <p className="mf-kicker">Overview</p>
        <h1 ref={heading} tabIndex={-1}>Plan a move off Oracle Forms</h1>
        <p className="mf-lead">Answer five short sets of questions about your Oracle Forms application. The workbench turns them into a written migration plan for a React front end, a Java Spring Boot back end, and an Azure database.</p>

        <ol className="mf-intro-steps">
          {STEP_GUIDANCE.map((item, index) => <li key={item.id}><span>{index + 1}</span><div><strong>{item.name}</strong><small>{item.railSummary}</small></div></li>)}
        </ol>

        <div className="mf-intro-cards">
          <article><ListChecks aria-hidden="true" /><h2>What you need</h2><p>A repository address, a zip export, or the folder path holding your Forms files, plus a rough idea of which Oracle exports you already have.</p></article>
          <article><FileCheck2 aria-hidden="true" /><h2>What you get</h2><p>A written plan across the six migration stages: what each stage would produce, which phases can be authorised here, and what is still missing.</p></article>
          <article><ShieldCheck aria-hidden="true" /><h2>What it will not do</h2><p>It does not modify your repository, provision anything in Azure, or deploy. Authorised phases write only into your private session workspace.</p></article>
        </div>

        <div className="mf-boundary compact">
          <LockKeyhole aria-hidden="true" />
          <p>
            <strong>Your answers stay in this browser tab.</strong>
            There is no saved project and no server-side autosave. Closing or reloading the tab discards the setup, and any copied
            source is deleted from the server four hours after it was copied.
          </p>
        </div>

        <ProjectApprovals onProjectChange={setProjectId} />

        <div className="mf-intro-actions">
          <button type="button" className="mf-primary" disabled={!projectId} onClick={() => beginSetup()}><Play aria-hidden="true" />New migration</button>
          <button type="button" className="mf-secondary" disabled={!projectId} onClick={() => beginSetup(exampleFields)}>Load example values</button>
          <p className="mf-help">
            {projectId
              ? "The example fills the text fields with sample values so you can see the shape of the setup. It describes no real application, copies no code, and connects to nothing."
              : "Create or select a project above first. Project membership owns every source copy, plan, run, and generated artifact."}
          </p>
        </div>
      </main> : activeView === "setup" || !plan ? <main className="mf-journey" id="workspace">
        <nav className="mf-rail" aria-label="Migration setup progress">
          <p>Migration setup</p>
          <ol>{STEP_GUIDANCE.map((item, index) => <li className={index === step ? "current" : index < step ? "complete" : ""} key={item.id}>
            <button type="button" disabled={index > step} aria-current={index === step ? "step" : undefined} onClick={() => index < step && setStep(index)}>
              <span>{index < step ? <Check aria-hidden="true" /> : index + 1}</span><span><strong>{item.name}</strong><small>{item.railSummary}</small></span>
            </button>
          </li>)}</ol>
          <div className="mf-boundary compact"><LockKeyhole aria-hidden="true" /><p><strong>Gated mode</strong>Planning writes nothing. Authorized conversion writes stay in your private session workspace. Separately approved sandbox phases may write to the host-configured PostgreSQL target; source and production resources remain unchanged.</p></div>
        </nav>

        <section className="mf-page">
          {step === 0 && <ProjectApprovals onProjectChange={setProjectId} />}
          <div className="mf-progress"><span aria-hidden="true">Step {step + 1} of {STEPS.length}</span><progress max={STEPS.length} value={step + 1} /></div>
          <p className="mf-visually-hidden" aria-live="polite">{`Step ${step + 1} of ${STEPS.length}: ${STEPS[step]}`}</p>

          {step === 0 && <div className="mf-step"><p className="mf-kicker">{guide.name}</p><h1 ref={heading} tabIndex={-1}>{guide.heading}</h1>
            <StepPurpose guide={guide} />
            <div className="mf-assurance"><Info aria-hidden="true" /><div><strong>We work on a copy, never your original</strong><p>The server takes a shallow, read-only copy into a private folder that only your session can reach. Git history is stripped, so the copy cannot push back to your repository, and it is deleted automatically after four hours.</p><button type="button" className="mf-inline-link" onClick={() => beginSetup(exampleFields)}>Not sure? Load example values</button></div></div>
            <div className="mf-field-grid">
            <Field id="engagementId" label="Reference for this plan" hint="Any label" help={help("field.engagementId")} placeholder="ENG-0042" value={fields.engagementId} error={errors.engagementId} onChange={(value) => updateField("engagementId", value)} />
            <Field id="applicationName" label="Application name" help={help("field.applicationName")} placeholder="ORDERS" value={fields.applicationName} error={errors.applicationName} onChange={(value) => updateField("applicationName", value)} />
          </div>
            <ChoiceCards
              legend="Where is the code?"
              hint={help("group.sourceLocation")}
              value={sourceMode}
              onChange={changeSourceMode}
              options={SOURCE_CHOICES.map((choice) => ({
                ...choice,
                glyph: <ServiceGlyph id={choice.value === "repo" ? glyphForHost(repository?.host ?? "") : choice.value === "zip" ? "archive" : "oracle-forms"} size={22} />,
              }))}
              renderDetail={(value) => <>
                {value === "repo" && <>
                  <div className="mf-field"><div className="mf-label-row"><label htmlFor="repoUrl"><GitBranch aria-hidden="true" />Repository address</label><InfoTip label="the repository address">{help("field.repoUrl")}</InfoTip></div><p className="mf-sr-only" id="repoUrl-help">Example: https://github.com/contoso/orders</p><input id="repoUrl" value={repoUrl} placeholder="https://github.com/contoso/orders" autoComplete="off" spellCheck={false} aria-invalid={Boolean(sourceError)} aria-describedby={sourceError ? "source-error repoUrl-help" : "repoUrl-help"} onChange={(event) => { setRepoUrl(event.target.value); setSourceError(""); }} /></div>
                  <div className="mf-field"><div className="mf-label-row"><label htmlFor="repoBranch">Branch</label><InfoTip label="the branch">{help("field.repoBranch")}</InfoTip><span className="mf-label-hint">Optional</span></div><input id="repoBranch" value={repoBranch} placeholder="main" autoComplete="off" spellCheck={false} onChange={(event) => setRepoBranch(event.target.value)} /></div>
                  <div className="mf-field"><div className="mf-label-row"><label htmlFor="repoFolder">Folder inside the repository</label><InfoTip label="the folder">{help("field.repoFolder")}</InfoTip><span className="mf-label-hint">Optional</span></div><input id="repoFolder" value={repoFolder} placeholder="legacy/forms" autoComplete="off" spellCheck={false} onChange={(event) => setRepoFolder(event.target.value)} /></div>
                  {repository && !workspace && <p className="mf-detected"><CheckCircle2 />Recognised {repository.label}</p>}
                  <button type="button" className="mf-secondary mf-acquire" disabled={acquiring || !repository || !projectId} onClick={() => void acquire({ mode: "repo", repositoryUrl: repoUrl.trim(), branch: repoBranch.trim() || undefined })}>
                    <GitBranch />{acquiring ? "Copying..." : workspace ? "Copy again" : "Copy this repository"}
                  </button>
                  <InfoTip label="copying this repository">{help("action.copyRepository")}</InfoTip>
                </>}

                {value === "zip" && <div className="mf-drop">
                  <FileArchive aria-hidden="true" />
                  <div>
                    <label className="mf-file" htmlFor="zipFile">{acquiring ? "Uploading..." : workspace ? "Choose a different zip" : "Choose a zip file"}</label>
                    <input id="zipFile" type="file" accept=".zip,application/zip" disabled={acquiring || !projectId} aria-describedby={sourceError ? "source-error zipFile-help" : "zipFile-help"} onChange={(event) => { const file = event.target.files?.[0]; event.target.value = ""; if (file) void acquire({ mode: "zip", file }); }} />
                    <p className="mf-help" id="zipFile-help">{help("field.zipFile")}</p>
                  </div>
                </div>}

                {value === "manual" && <div className="mf-field-grid"><Field id="sourceRoot" label="Where the Forms files live" hint="Folder path" help={help("field.sourceRoot")} placeholder="legacy/forms" value={fields.sourceRoot} error={errors.sourceRoot} onChange={(value) => updateField("sourceRoot", value)} /></div>}

                {sourceError && <p id="source-error" className="mf-error" role="alert">{sourceError}</p>}

                {workspace && value !== "manual" && <div className="mf-findings">
                  <p><strong>{workspace.fileCount} files copied ({formatBytes(workspace.byteCount)}).</strong> Locked read-only in your private workspace.</p>
                  {workspace.artifacts.length > 0
                    ? <ul>{workspace.artifacts.map((artifact) => <li key={artifact.kind}><span className="mf-tag">{artifact.count}</span><div><strong>{EVIDENCE_NAMES[artifact.kind] ?? humanize(artifact.kind)}</strong><small>{artifact.example}</small></div></li>)}</ul>
                    : <p className="mf-help">Nothing recognisable was found, so nothing has been ticked for you. You can still continue and fill in the source checklist yourself.</p>}
                  <div className="mf-workspace-actions">
                    <p className="mf-help">Detected folder: {workspace.sourceRoot} · deleted automatically {new Date(workspace.expiresUtc).toLocaleTimeString()}</p>
                    <button type="button" className="mf-inline-link" onClick={() => setConsoleOpen(true)}>View activity</button>
                    <InfoTip label="view activity">{help("action.viewActivity")}</InfoTip>
                    <button type="button" className="mf-inline-link danger" onClick={discardWorkspace}><Trash2 aria-hidden="true" />Delete this copy now</button>
                    <InfoTip label="deleting this copy">{help("action.deleteCopy")}</InfoTip>
                  </div>
                </div>}
              </>}
            />

            <div className="mf-field-grid">
            <Field id="outputRoot" label="Where new code would go" hint="Folder path" help={help("field.outputRoot")} placeholder="out/orders" value={fields.outputRoot} error={errors.outputRoot} onChange={(value) => updateField("outputRoot", value)} />
          </div>
            <div className="mf-field-grid">
            <VersionSelect id="oracleFormsVersion" label="Oracle Forms release" options={FORMS_VERSIONS} value={fields.oracleFormsVersion} onChange={(value) => updateField("oracleFormsVersion", value)} help={help("field.oracleFormsVersion")} />
            <VersionSelect id="oracleDatabaseVersion" label="Oracle Database release" options={DATABASE_VERSIONS} value={fields.oracleDatabaseVersion} onChange={(value) => updateField("oracleDatabaseVersion", value)} help={help("field.oracleDatabaseVersion")} />
          </div></div>}

          {step === 1 && <div className="mf-step"><p className="mf-kicker">{guide.name}</p><h1 ref={heading} tabIndex={-1}>{guide.heading}</h1>
            <StepPurpose guide={guide} />
            <div className="mf-assurance"><Info aria-hidden="true" /><div><strong>The application route is fixed</strong><p>Every plan targets a React front end and a Java Spring Boot back end, because those are the only converters implemented. Subscription, resource group and region are not offered here: no authorized Azure API is wired into this workbench, so choosing one would be a claim it could not keep.</p></div></div>
            <ChoiceCards
              legend="Azure database"
              hint={help("group.databaseTarget")}
              value={database}
              onChange={setDatabase}
              options={bootstrap.databaseTargets.map((option) => ({ value: option.target, name: option.name, description: option.guidance, glyph: <ServiceGlyph id={glyphForDatabase(option.target)} size={22} /> }))}
              renderDetail={(value) => {
                const option = bootstrap.databaseTargets.find((item) => item.target === value);
                if (!option) return null;
                const names = proposedResourceNames(fields.applicationName, value);
                return <dl className="mf-detail-list">
                  <div><dt>Azure service</dt><dd>{option.service}</dd></div>
                  <div><dt>{names.databaseHostLabel}</dt><dd><code>{names.databaseHost}</code> <span className="mf-help">Proposed name. Nothing is created.</span></dd></div>
                  {names.database !== names.databaseHost && <div><dt>Database</dt><dd><code>{names.database}</code></dd></div>}
                </dl>;
              }}
            />
            <ChoiceCards
              legend="Planning depth"
              hint={help("group.planningDepth")}
              value={mode}
              onChange={setMode}
              options={bootstrap.executionModes.map((option) => ({ value: option.mode, name: option.name, description: option.description }))}
              renderDetail={(value) => {
                const option = bootstrap.executionModes.find((item) => item.mode === value);
                return option ? <p className="mf-detail-note">{option.executableHere ? "This depth has an implementation here, but evidence, runtime configuration, and approvals still gate each phase." : "This depth describes work that would need execution adapters, which are not built. The plan still covers it in writing."}</p> : null;
              }}
            />
          </div>}

          {step === 2 && <div className="mf-step"><p className="mf-kicker">{guide.name}</p><h1 ref={heading} tabIndex={-1}>{guide.heading}</h1>
            <StepPurpose guide={guide} />
            <div className="mf-assurance"><Info aria-hidden="true" /><div><strong>These are your declarations, not verified facts</strong><p>Ticking an item records that you have it. The workbench opens no Oracle instance and checks nothing. Where a copied source was indexed, matching items are ticked for you and you can change any of them.</p></div></div>
            <div className="mf-meter"><FileCheck2 aria-hidden="true" /><div><strong>{readyGroups} of {groups.length} required inputs ready<InfoTip label="required inputs ready">{help("metric.requirementsMet")}</InfoTip></strong><progress max={groups.length} value={readyGroups} /></div></div>
            <fieldset className="mf-evidence-group" aria-labelledby="required-evidence-legend"><legend><span id="required-evidence-legend">Needed before code can be generated</span><InfoTip label="required source material">{help("group.requiredEvidence")}</InfoTip></legend><div className="mf-evidence-list">{requiredEvidence.map((option) => <EvidenceChoice option={option} key={option.kind} />)}</div></fieldset>
            <details className="mf-optional"><summary>Extra context, if you have it <span>{optionalEvidence.filter((item) => evidence.includes(item.kind)).length} selected</span></summary><p className="mf-help">{help("group.optionalEvidence")}</p><div className="mf-evidence-list">{optionalEvidence.map((option) => <EvidenceChoice option={option} key={option.kind} />)}</div></details>
          </div>}

          {step === 3 && <div className="mf-step"><p className="mf-kicker">{guide.name}</p><h1 ref={heading} tabIndex={-1}>{guide.heading}</h1>
            <StepPurpose guide={guide} />
            <div className="mf-boundary"><LockKeyhole aria-hidden="true" /><p><strong>A typed name is planning metadata. It authorizes nothing.</strong>These fields do not sign anyone in, do not check that the person exists, and do not grant permission to write anywhere. Sandbox writes need an execution approval this workbench cannot issue, and production release is not available here at all. Treat this as recording who you would ask.</p></div>
            <div className="mf-assurance"><CheckCircle2 aria-hidden="true" /><div><strong>Sandbox and production stay separate</strong><p>Approving the plan never approves a sandbox run, and neither one approves a production release. The two names must be different people.</p></div></div>
            <div className="mf-field-grid"><Field id="executionApprover" label="Sandbox approver" hint="Optional planning contact" help={help("field.executionApprover")} placeholder="approver@contoso.com" value={fields.executionApprover} error={errors.executionApprover} onChange={(value) => updateField("executionApprover", value)} /><Field id="productionApprover" label="Production approver" hint="Optional planning contact" help={help("field.productionApprover")} placeholder="cab-chair@contoso.com" value={fields.productionApprover} error={errors.productionApprover} onChange={(value) => updateField("productionApprover", value)} /></div>
            <p className="mf-help">These names are sent as pending planning notes with plan and run requests. The server does not treat them as identity or authorization and does not persist them as approvals.</p>
          </div>}

          {step === 4 && <form className="mf-step" onSubmit={generatePlan} noValidate><p className="mf-kicker">{guide.name}</p><h1 ref={heading} tabIndex={-1}>{guide.heading}</h1>
            <StepPurpose guide={guide} />
            <section className="mf-review"><header><h2>Your application</h2><button type="button" onClick={() => goToStep(0)}>Change</button></header><dl><ReviewRow label="Reference" value={fields.engagementId} onEdit={() => goToStep(0)} /><ReviewRow label="Application" value={fields.applicationName} onEdit={() => goToStep(0)} /><ReviewRow label="Source" value={sourceLabel} onEdit={() => goToStep(0)} /><ReviewRow label="Output folder" value={fields.outputRoot} onEdit={() => goToStep(0)} /><ReviewRow label="Oracle Forms release" value={FORMS_VERSIONS.find((item) => item.value === fields.oracleFormsVersion)?.label ?? fields.oracleFormsVersion} onEdit={() => goToStep(0)} /><ReviewRow label="Oracle Database release" value={DATABASE_VERSIONS.find((item) => item.value === fields.oracleDatabaseVersion)?.label ?? fields.oracleDatabaseVersion} onEdit={() => goToStep(0)} /></dl></section>
            <section className="mf-review"><header><h2>Azure destination</h2><button type="button" onClick={() => goToStep(1)}>Change</button></header><dl><ReviewRow label="Database" value={selectedDatabase?.name ?? database} onEdit={() => goToStep(1)} /><ReviewRow label="Planning depth" value={selectedMode?.name ?? mode} onEdit={() => goToStep(1)} /></dl></section>
            <section className="mf-review"><header><h2>Checklist and permissions</h2><button type="button" onClick={() => goToStep(2)}>Change</button></header><dl><ReviewRow label="Declared source material" value={`${evidence.length} item types (${readyGroups} of ${groups.length} requirements met)`} onEdit={() => goToStep(2)} /><ReviewRow label="Sandbox approver" value={fields.executionApprover} onEdit={() => goToStep(3)} /><ReviewRow label="Production approver" value={fields.productionApprover} onEdit={() => goToStep(3)} /></dl></section>
            {readyGroups < groups.length && <div className="mf-notice"><AlertTriangle aria-hidden="true" /><p><strong>{groups.length - readyGroups} requirement{groups.length - readyGroups === 1 ? "" : "s"} still unmet.</strong>You can still generate a plan. Each unmet requirement is listed as a blocker rather than stopping you here.</p></div>}
            <details className="mf-optional"><summary>Technical names sent to the planner</summary><dl className="mf-detail-list">
              <div><dt>requestedMode</dt><dd><code>{mode}</code></dd></div>
              <div><dt>target.frontEnd</dt><dd><code>React</code></dd></div>
              <div><dt>target.backEnd</dt><dd><code>JavaSpringBoot</code></dd></div>
              <div><dt>target.database</dt><dd><code>{database}</code></dd></div>
              <div><dt>evidence kinds</dt><dd><code>{evidence.join(", ") || "none"}</code></dd></div>
            </dl></details>
            <div className="mf-boundary"><LockKeyhole aria-hidden="true" /><p><strong>Generating the plan writes nothing.</strong>It creates no file, changes no repository, and touches no Azure resource. Running authorized conversion phases is a separate decision on the next screen, and writes only into your private session workspace. With separate execution approval, sandbox phases may write to the host-configured PostgreSQL target. Production release is not available here.</p></div>
            <button className="mf-primary mf-generate" type="submit" disabled={submitting || !projectId}><Sparkles aria-hidden="true" />{submitting ? "Generating plan..." : "Generate migration plan"}</button>
          </form>}

          <div className="mf-actions">
            <button type="button" className="mf-back" onClick={() => (step === 0 ? setView("overview") : setStep((current) => current - 1))}><ArrowLeft aria-hidden="true" />Back</button>
            {step < STEPS.length - 1 && <button type="button" className="mf-primary" onClick={continueJourney}>Continue<ArrowRight aria-hidden="true" /></button>}
          </div>
        </section>
      </main> : <main className="mf-results" id="workspace">
        <div className="mf-result-heading"><div><p className="mf-kicker">Plan &amp; results</p><h1 ref={heading} tabIndex={-1}>Migration plan for {plan.plan.applicationName}</h1></div></div>

        {/* 1. The short outcome. One state, one sentence, before any detail. */}
        <section className={`mf-outcome ${outcome.tone}`} aria-labelledby="mf-outcome-title">
          <outcome.icon aria-hidden="true" />
          <div>
            <h2 id="mf-outcome-title">{outcome.headline}</h2>
            <p>{outcome.detail}</p>
          </div>
        </section>

        {/* 2. What is actually blocking, before anything that merely describes the plan. */}
        {plan.plan.blockers.length > 0 && <section className="mf-alert" aria-labelledby="mf-blockers-title">
          <h2 id="mf-blockers-title"><AlertTriangle aria-hidden="true" />What is blocking this migration</h2>
          <ul>{plan.plan.blockers.slice(0, 3).map((item) => <li key={item}>{item}</li>)}</ul>
          {plan.plan.blockers.length > 3 && <details className="mf-optional">
            <summary>The remaining blockers <span>{plan.plan.blockers.length - 3}</span></summary>
            <ul>{plan.plan.blockers.slice(3).map((item) => <li key={item}>{item}</li>)}</ul>
          </details>}
        </section>}

        {/* 3. Exactly one primary action for the state the run is actually in. */}
        <section className="mf-next-action" aria-labelledby="mf-next-title">
          <h2 id="mf-next-title">Next available action</h2>
          <p className="mf-run-lead">{primaryAction.explanation}</p>
          {primaryAction.hint && <p className="mf-help" id="next-action-hint">{primaryAction.hint}</p>}
          {primaryAction.kind === "download"
            ? <a className="mf-primary mf-generate" href={primaryAction.href} download="migration-output.zip">
              <FileArchive aria-hidden="true" />{primaryAction.label}
            </a>
            : <button
              type="button"
              className="mf-primary mf-generate"
              disabled={primaryAction.disabled}
              aria-describedby={primaryAction.hint ? "next-action-hint" : undefined}
              onClick={primaryAction.run}
            >
              {primaryAction.kind === "run" ? <Play aria-hidden="true" /> : <Pencil aria-hidden="true" />}{primaryAction.label}
            </button>}
          <InfoTip label={primaryAction.label.toLowerCase()}>{primaryAction.help}</InfoTip>
          <p className="mf-status" role="status" aria-live="polite">{executing ? "The fleet is working. Activity shows each line the server sends." : execution ? "Run finished." : ""}</p>
          {executionError && <p className="mf-error" role="alert">{executionError}</p>}

          {/* Write effects stay visible. They are the part an operator must not have to expand to find. */}
          <div className="mf-boundary"><LockKeyhole aria-hidden="true" /><p><strong>What running actually writes.</strong>Conversion artifacts are written into your private session workspace and your source repository is never modified. Separately approved sandbox phases may write schema and data to the host-configured PostgreSQL target, and no caller can change that endpoint. Nothing is deployed, no Azure resource is created, and no production resource is touched.</p></div>
          {(activityAvailable || executing) && <span className="mf-command-help"><button type="button" className="mf-inline-link" onClick={() => { setConsoleMode(execution || executing ? "execution" : "source"); setConsoleOpen(true); }}><Activity aria-hidden="true" />View activity</button><InfoTip label="view activity">{help("action.viewActivity")}</InfoTip></span>}
        </section>

        <ProjectApprovals
          workspaceId={workspace?.workspaceId}
          runRequest={runRequestBody()}
          onProjectChange={setProjectId}
        />

        {projectId && <RunHistory
          projectId={projectId}
          activeRunId={activeRunId}
          onOpen={(runId) => void reopenRun(runId)}
        />}

        {/* 4. The run's own results, concise. */}
        <section className="mf-result-section" aria-labelledby="mf-run-results-title">
          <p className="mf-kicker">Run results</p>
          <h2 id="mf-run-results-title">What this run produced, and what it did not</h2>
          <div className="mf-metrics" role="group" aria-label="Plan summary">
            <article><span>Stages ready<InfoTip label="stages ready">{help("metric.stagesReady")}</InfoTip></span><strong>{plan.steps.filter((item) => item.state === "Ready" || item.state === "Current").length}/6</strong></article>
            <article><span>Inputs provided<InfoTip label="inputs provided">{help("metric.inputsProvided")}</InfoTip></span><strong>{readyGroups}/{groups.length}</strong></article>
            <article><span>Still missing<InfoTip label="still missing">{help("metric.stillMissing")}</InfoTip></span><strong>{plan.plan.blockers.length}</strong></article>
            <article><span>Phases that ran<InfoTip label="phases that ran">{help("metric.phasesExecuted")}</InfoTip></span><strong>{execution ? `${phasesExecuted}/${execution.phases.length}` : "0/0"}</strong></article>
            <article><span>Files written<InfoTip label="files written">{help("metric.filesWritten")}</InfoTip></span><strong>{execution?.artifacts.length ?? 0}</strong></article>
            <article><span>Azure services active<InfoTip label="active Azure services">{help("metric.azureActive")}</InfoTip></span><strong>{activeAzure}/{bootstrap.azureComponents.length}</strong></article>
          </div>
          <CapabilityStates execution={execution} />
          {execution && <ExecutionReport result={execution} onPreview={(path) => void openArtifact(path)} workspaceId={workspace?.workspaceId} />}
        </section>

        {/* Everything below explains the plan rather than telling the operator what to do next. */}
        <details className="mf-disclosure"><summary>Target architecture diagram</summary>
          <ArchitectureReveal applicationName={plan.plan.applicationName} database={database} databaseName={selectedDatabase?.name ?? database} />
        </details>

        <details className="mf-disclosure"><summary>Six-stage modernization lifecycle</summary>
          <ol className="mf-lifecycle">{plan.steps.map((item) => <li key={item.step}><span>{item.order}</span><div><strong>{item.title}</strong><small>{item.phases.map(humanize).join(" · ")}</small></div><em className={item.state === "Blocked" ? "mf-pill danger" : "mf-pill neutral"}>{item.state === "Blocked" ? <XCircle aria-hidden="true" /> : <Info aria-hidden="true" />}{item.state}</em></li>)}</ol>
        </details>

        <details className="mf-disclosure"><summary>Full phase detail: inputs, outputs, and blockers</summary>
          <div className="mf-phases">{plan.plan.phases.map((phase) => <PhaseCard key={phase.phase} phase={phase} attribution={bootstrap.attribution?.phases.find((item) => item.phase === phase.phase)} />)}</div>
        </details>

        {bootstrap.attribution && <details className="mf-disclosure"><summary>Which model and which code runs each step</summary>
          <AttributionSection attribution={bootstrap.attribution} />
        </details>}

        {plan.azureFootprint && <details className="mf-disclosure"><summary>Azure footprint this plan would need</summary>
          <AzureFootprintSection footprint={plan.azureFootprint} />
        </details>}
      </main>}

          <p className="mf-status mf-pane-status" role="status" aria-live="polite">{status}</p>
        </div>
      </div>

      {dialog && <div className="mf-dialog-backdrop" onMouseDown={(event) => event.target === event.currentTarget && setDialog(null)}><section className="mf-dialog" role="dialog" aria-modal="true" aria-labelledby="mf-dialog-title" ref={dialogBody}><header><div><p className="mf-kicker">{dialog === "agent" ? "Microsoft Foundry" : dialog === "artifact" ? "Generated artifact" : dialog === "glossary" ? "Help" : "Environment"}</p><h2 id="mf-dialog-title">{dialog === "agent" ? "Ask the migration fleet" : dialog === "artifact" ? artifact?.path ?? "Artifact" : dialog === "glossary" ? "What these words mean" : "Azure readiness"}</h2></div><button type="button" onClick={() => setDialog(null)} aria-label="Close dialog"><X aria-hidden="true" /></button></header>
        {dialog === "agent" ? <><p>Ask about your checklist, target choices, or blockers. The message is sent once, is not stored, and executes no migration step.</p><form onSubmit={askAgent}><label htmlFor="agent-message">Your question</label><textarea id="agent-message" rows={6} maxLength={8000} value={agentMessage} disabled={asking} onChange={(event) => setAgentMessage(event.target.value)} /><div className="mf-agent-actions"><span>{agentMessage.length}/8,000</span><button className="mf-primary" type="submit" disabled={!agentMessage.trim() || asking}><Sparkles aria-hidden="true" />{asking ? "Asking..." : "Send question"}</button></div></form><p role="status">{agentStatus}</p>{agentAnswer && <div className="mf-answer">{agentAnswer}</div>}</>
          : dialog === "artifact" ? <><p className="mf-help">Read straight from your session workspace. At most 512 KB is shown. Opening a file does not run or validate it.</p>{artifact?.error
            ? <p className="mf-error" role="alert">{artifact.error}</p>
            : artifact?.text
              ? <pre className="mf-artifact" tabIndex={0} aria-label={`Contents of ${artifact.path}`}>{artifact.text}</pre>
              : <p role="status" aria-live="polite">Loading the artifact...</p>}</>
          : dialog === "glossary" ? <><p>The words this workbench uses, in plain language. You do not need any of them to complete the setup.</p><dl className="mf-glossary">{GLOSSARY.map((entry) => <div key={entry.term}><dt>{entry.term}</dt><dd>{entry.body}</dd></div>)}</dl></>
          : <><div className="mf-assurance"><Cloud aria-hidden="true" /><div><strong>{activeAzure} of {bootstrap.azureComponents.length} components active</strong><p>Only services this workbench has actually reached at runtime are marked active. The rest are configured intentions.</p></div></div><div className="mf-components">{bootstrap.azureComponents.map((component) => <article key={component.id}><span className={component.state === "Active" ? "active" : ""}><ServiceGlyph id={glyphForComponent(component.id)} size={22} /></span><div><h3>{component.name}<InfoTip label={component.name}>{component.evidence}</InfoTip></h3><p>{component.role}</p><small className={component.state === "Active" ? "mf-pill success" : "mf-pill neutral"}>{component.state === "Active" ? <CheckCircle2 aria-hidden="true" /> : <AlertTriangle aria-hidden="true" />}{humanize(component.state)}</small></div></article>)}</div><div className="mf-boundary compact"><LockKeyhole aria-hidden="true" /><p><strong>Gated execution</strong>Analysis, conversion, and build phases write into the session workspace. A configured PostgreSQL sandbox can receive writes only after separate execution approval. Differential testing and production release are not available.</p></div></>}
      </section></div>}

      {consoleOpen && <ActivityPane
        title={consoleMode === "execution" ? "Running the authorized phases" : sourceMode === "repo" ? "Copying your repository" : "Expanding your upload"}
        subtitle={consoleMode === "execution"
          ? "The server is running the phases the planner authorized and writing into your session workspace."
          : "The server is taking a private read-only copy of your source so the fleet has something to read."}
        lines={consoleLines}
        running={acquiring || executing}
        fallbackFocus={activityButton}
        onClose={closeActivity}
      />}
    </>
  );
}