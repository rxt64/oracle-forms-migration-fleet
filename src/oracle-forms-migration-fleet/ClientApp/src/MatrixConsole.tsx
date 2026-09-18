import { useEffect, useMemo, useRef, type RefObject } from "react";
import { Activity, AlertTriangle, CheckCircle2, CircleHelp, Info, PauseCircle, Search, X, XCircle } from "lucide-react";
import { OPERATION_LABELS, isTerminal, type ConsoleLine } from "./sourceClient";
import { EVIDENCE_NAMES, help } from "./Guidance";
import { InfoTip } from "./InfoTip";

const LEVEL_LABELS: Record<ConsoleLine["level"], string> = {
  info: "Step",
  warn: "Warning",
  error: "Error",
  found: "Found",
  skip: "Skipped",
  done: "Finished",
  keepalive: "Still working",
};

/** Kinds the progress stream counts that are not operator-declared evidence kinds. */
const COUNT_LABELS: Readonly<Record<string, string>> = {
  ExtractedFile: "Files expanded from the archive",
  RecognisedArtifactKind: "Oracle artifact kinds recognised",
  GeneratedFile: "Files written by this run",
};

type ActivityStateId = "idle" | "running" | "waiting" | "completed" | "failed" | "interrupted";

interface ActivityState {
  id: ActivityStateId;
  /** The words the operator reads, and the only thing a screen reader is told automatically. */
  label: string;
  tone: "info" | "neutral" | "success" | "danger" | "warn";
}

const STATES: Record<ActivityStateId, ActivityState> = {
  idle: { id: "idle", label: "Nothing has run yet", tone: "neutral" },
  running: { id: "running", label: "Running", tone: "info" },
  waiting: { id: "waiting", label: "Waiting for updates", tone: "neutral" },
  completed: { id: "completed", label: "Completed", tone: "success" },
  failed: { id: "failed", label: "Failed", tone: "danger" },
  interrupted: { id: "interrupted", label: "Interrupted or unknown", tone: "warn" },
};

/**
 * Transcript of a server-side operation.
 *
 * Every line here was emitted by the server as the work happened. The summary above the log is built
 * from the typed fields the server attached to those lines — operation, state, what it observed, what
 * happens next — and never from reading the text. That distinction is what lets the pane say
 * "Interrupted or unknown" when the stream simply stopped: a stream that ends without the server
 * declaring an outcome has not succeeded, and calling it success is the failure this pane prevents.
 */
export function ActivityPane({
  title,
  subtitle,
  lines,
  running,
  onClose,
  fallbackFocus,
}: {
  title: string;
  subtitle: string;
  lines: ConsoleLine[];
  running: boolean;
  onClose: () => void;
  fallbackFocus?: RefObject<HTMLElement | null>;
}) {
  const log = useRef<HTMLDivElement>(null);
  const panel = useRef<HTMLElement>(null);
  const closeButton = useRef<HTMLButtonElement>(null);
  const opener = useRef<HTMLElement | null>(null);
  const closeAction = useRef(onClose);

  useEffect(() => {
    closeAction.current = onClose;
  }, [onClose]);

  useEffect(() => {
    log.current?.scrollTo({ top: log.current.scrollHeight });
  }, [lines.length]);

  useEffect(() => {
    opener.current = document.activeElement as HTMLElement | null;
    closeButton.current?.focus();
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") {
        closeAction.current();
        return;
      }
      if (event.key !== "Tab") return;
      // `offsetParent` is not enough: the raw-output log carries tabindex and still reports one while
      // its <details> is closed, which put an untabbable element last in the list and let Tab walk
      // straight out of the dialog. A box the browser actually laid out is the honest test.
      const stops = Array.from(panel.current?.querySelectorAll<HTMLElement>(
        'button:not([disabled]), a[href], input:not([disabled]), textarea:not([disabled]), select:not([disabled]), details > summary, [tabindex]:not([tabindex="-1"])') ?? [])
        .filter((element) => element.getClientRects().length > 0);
      if (stops.length === 0) return;
      // Movement is computed rather than left to the browser, so the trap cannot be defeated by a
      // focusable element the selector misses or reports in a different order.
      const index = stops.indexOf(document.activeElement as HTMLElement);
      const next = event.shiftKey
        ? stops[(index <= 0 ? stops.length : index) - 1]
        : stops[index < 0 ? 0 : (index + 1) % stops.length];
      event.preventDefault();
      next.focus();
    }
    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("keydown", onKeyDown);
      // Closing the pane returns the operator to whatever opened it rather than to the page top.
      const returnTarget = opener.current?.isConnected ? opener.current : fallbackFocus?.current;
      returnTarget?.focus();
    };
  }, [fallbackFocus]);

  const counts = useMemo(() => summarise(lines), [lines]);
  const measured = useMemo(() => measuredCounts(lines), [lines]);
  const view = useMemo(() => describe(lines, running), [lines, running]);
  const state = STATES[view.state];
  const StateIcon = ICONS[view.state];

  return (
    <div className="mf-activity-backdrop" onMouseDown={(event) => event.target === event.currentTarget && onClose()}>
      <section ref={panel} className="mf-activity" role="dialog" aria-modal="true" aria-labelledby="mf-activity-title">
        <header>
          <span className="mf-activity-mark"><Activity aria-hidden="true" /></span>
          <div>
            <h2 id="mf-activity-title">{title}</h2>
            <p>{subtitle}</p>
          </div>
          <button type="button" ref={closeButton} onClick={onClose} aria-label="Close activity">
            <X aria-hidden="true" />
          </button>
        </header>

        <div className="mf-activity-body">
          {/*
            The only thing announced automatically. It changes when the operation or its state
            changes, so a screen reader hears "Running", then "Completed" — not one announcement per
            log line. The transcript below is reachable on demand and announces nothing on its own.
          */}
          <p className="mf-sr-only" role="status" aria-live="polite">{view.announcement}</p>

          <div className={`mf-activity-summary ${state.tone}`}>
            <p className="mf-activity-state">
              <span className={`mf-pill ${state.tone}`}><StateIcon aria-hidden="true" />{state.label}</span>
              <span className="mf-activity-operation">{view.operationLabel}</span>
            </p>
            <dl>
              <div><dt>Purpose</dt><dd>{view.purpose}</dd></div>
              <div><dt>Observed so far</dt><dd>{view.observed}</dd></div>
              <div><dt>Next</dt><dd>{view.nextAction}</dd></div>
            </dl>
          </div>

          {measured.length > 0 && <section className="mf-activity-measured" aria-labelledby="mf-activity-measured-title">
            <h3 id="mf-activity-measured-title">
              {view.state === "completed" ? "What the server counted" : "What the server counted before it stopped"}
              <InfoTip label="what the server counted">{help("metric.serverCounted")}</InfoTip>
            </h3>
            <ul>
              {measured.map((item) => (
                <li key={item.kind}>
                  <strong>{item.count}</strong>
                  <span>{COUNT_LABELS[item.kind] ?? EVIDENCE_NAMES[item.kind] ?? item.kind}</span>
                </li>
              ))}
            </ul>
            {view.state !== "completed" && <p className="mf-help">
              These are partial findings: what the server had reported when the transcript stopped, not a finished total.
            </p>}
          </section>}

          <ul className="mf-activity-counts">
            <li><Info aria-hidden="true" /><span>Activity messages<InfoTip label="activity messages">{help("metric.activityMessages")}</InfoTip></span><strong>{counts.activity}</strong></li>
            <li><Search aria-hidden="true" /><span>Discovery messages<InfoTip label="discovery messages">{help("metric.discoveryMessages")}</InfoTip></span><strong>{counts.discovery}</strong></li>
            <li className={counts.warnings > 0 ? "attention" : undefined}><AlertTriangle aria-hidden="true" /><span>Warning messages<InfoTip label="warning messages">{help("metric.warningMessages")}</InfoTip></span><strong>{counts.warnings}</strong></li>
            <li className={counts.errors > 0 ? "failure" : undefined}><XCircle aria-hidden="true" /><span>Error messages<InfoTip label="error messages">{help("metric.errorMessages")}</InfoTip></span><strong>{counts.errors}</strong></li>
          </ul>

          {/*
            Collapsed by default. The raw stream is evidence, not the summary, and opening a pane on
            a wall of git output buries the one sentence that says what is happening.
          */}
          <details className="mf-activity-raw">
            <summary>Raw server output ({lines.length} {lines.length === 1 ? "line" : "lines"})</summary>
            <p className="mf-help">Exactly what the server sent, in order. Nothing here is interpreted for the summary above.</p>
            <div className="mf-activity-log" ref={log} tabIndex={0} role="group" aria-label="Raw server output">
              {lines.length === 0 && <p className="mf-activity-line info"><span>Step</span>No output yet.</p>}
              {lines.map((line, index) => (
                <p className={`mf-activity-line ${line.level}`} key={line.sequence ?? `${index}-${line.text}`}>
                  <span>{LEVEL_LABELS[line.level] ?? line.level}</span>
                  {line.text || line.observed || ""}
                </p>
              ))}
            </div>
          </details>
        </div>

        <footer>
          <p><strong>Closing this pane does not stop the work.</strong> It hides the transcript only; the request that
            started the operation carries on.</p>
          <p className="mf-activity-limit">
            <AlertTriangle aria-hidden="true" />
            There is no cancel button because cancellation here is cooperative and bound to this browser request. The server
            stops only where it next checks for cancellation, so work already done is not undone and files already written
            are not removed. Reloading the page or losing the connection ends the request the same way: the operation stops
            without reporting an outcome, no durable job resumes it, and this pane says so rather than calling it finished.
          </p>
        </footer>
      </section>
    </div>
  );
}

const ICONS: Record<ActivityStateId, typeof Activity> = {
  idle: Info,
  running: Activity,
  waiting: PauseCircle,
  completed: CheckCircle2,
  failed: XCircle,
  interrupted: CircleHelp,
};

interface ActivityView {
  state: ActivityStateId;
  operationLabel: string;
  purpose: string;
  observed: string;
  nextAction: string;
  announcement: string;
}

/**
 * Turns the typed frames into the one paragraph the operator reads.
 *
 * The rule that matters is the last one: when the stream is closed and no frame declared an outcome,
 * the result is `interrupted`, never `completed`. No message text is inspected to reach that.
 */
function describe(lines: ConsoleLine[], running: boolean): ActivityView {
  const signalled = lines.filter((line) => Boolean(line.state));
  const latest = signalled.length > 0 ? signalled[signalled.length - 1] : undefined;
  const terminal = [...signalled].reverse().find(isTerminal);
  const operationLabel = latest?.operation
    ? OPERATION_LABELS[latest.operation] ?? latest.operation
    : "No operation";

  if (!running && lines.length === 0) {
    return {
      state: "idle",
      operationLabel: "No operation",
      purpose: "Nothing has been started from this browser tab yet.",
      observed: "The server has sent no output.",
      nextAction: "Start a source copy or a run, and its transcript appears here.",
      announcement: "Activity: nothing has run yet.",
    };
  }

  const base = {
    operationLabel,
    purpose: latest?.purpose ?? "The server did not state a purpose for this operation.",
    observed: latest?.observed ?? "The server has sent output but has not summarised it.",
  };

  if (running) {
    const waiting = latest?.state === "Waiting";
    return {
      ...base,
      state: waiting ? "waiting" : "running",
      nextAction: latest?.nextAction ?? "Waiting for the server's next message.",
      announcement: `${operationLabel}: ${waiting ? STATES.waiting.label : STATES.running.label}.`,
    };
  }

  if (terminal) {
    const failed = terminal.state === "Failed";
    const label = terminal.operation ? OPERATION_LABELS[terminal.operation] ?? terminal.operation : operationLabel;
    return {
      operationLabel: label,
      purpose: terminal.purpose ?? base.purpose,
      observed: terminal.observed ?? base.observed,
      nextAction: terminal.nextAction ?? "",
      state: failed ? "failed" : "completed",
      announcement: `${label}: ${failed ? STATES.failed.label : STATES.completed.label}. ${terminal.observed ?? ""}`.trim(),
    };
  }

  return {
    ...base,
    state: "interrupted",
    observed: `${base.observed} The stream then stopped without the server reporting an outcome, so what happened after that is not known from here.`,
    nextAction: "Treat everything above as partial. Start the operation again if you need a result you can rely on.",
    announcement: `${operationLabel}: ${STATES.interrupted.label}.`,
  };
}

/** Counts the server measured, latest value per kind. Never parsed out of a message. */
function measuredCounts(lines: ConsoleLine[]) {
  const latest = new Map<string, number>();
  for (const line of lines) {
    if (typeof line.artifactCount === "number" && line.artifactKind) {
      latest.set(line.artifactKind, line.artifactCount);
    }
  }
  return [...latest.entries()].map(([kind, count]) => ({ kind, count }));
}

/** Message counts. They describe the transcript, not the estate, and are labelled as such. */
function summarise(lines: ConsoleLine[]) {
  return {
    activity: lines.filter((line) => line.level === "info" || line.level === "done" || line.level === "keepalive").length,
    discovery: lines.filter((line) => line.level === "found").length,
    warnings: lines.filter((line) => line.level === "warn" || line.level === "skip").length,
    errors: lines.filter((line) => line.level === "error").length,
  };
}
