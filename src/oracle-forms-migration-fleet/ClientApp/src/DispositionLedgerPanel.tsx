import { useCallback, useEffect, useState } from "react";
import { AlertTriangle, ClipboardList, RefreshCw } from "lucide-react";

/**
 * The disposition ledger console.
 *
 * Every number, every identity, and every verification state on this screen is read back from the
 * server. The browser computes no count of its own and never derives a verification: a property is
 * shown as verified only where the server says something executed and returned a result, because a
 * console that infers "verified" from a generated file is a console that reports a migration nobody
 * performed.
 *
 * The only thing an operator can write here is a decision, and the server refuses one without a
 * rationale and a published rule version that authorizes it. Those refusals are surfaced verbatim
 * rather than pre-empted by a weaker copy of the same rule in this file.
 *
 * The panel also owns the one control that decides which recorded decisions the *next* run generates
 * under. It is a locator and nothing more: choosing a ledger here puts its identifier on the approval
 * request and on the run request, and the server re-resolves it under this project and this source
 * snapshot both times. Nothing is selected for the operator, because selecting a ledger changes the
 * inputs an approval is issued against, and picking one on someone's behalf would be this console
 * deciding which decisions a generated application is allowed to claim.
 */

export type DispositionDecision = "Unresolved" | "Preserve" | "Transform" | "Retire" | "Defer";
export type DispositionVerification = "NotExecuted" | "Passed" | "Failed";

export interface DispositionCounts {
  discovered: number;
  decided: number;
  generated: number;
  verified: number;
  unresolved: number;
  deferred: number;
  failedVerification: number;
}

export interface DispositionLedgerSummary {
  ledgerId: string;
  projectId: string;
  runId: string;
  sourceSnapshotHash: string;
  sourceRoot: string;
  createdUtc: string;
  createdByObjectId: string;
  moduleCount: number;
  counts: DispositionCounts;
  completion: { canComplete: boolean; blockers: string[] };
  isStale: boolean;
  staleReason: string | null;
}

export interface DispositionScreenView {
  moduleName: string;
  filePath: string;
  counts: DispositionCounts;
  groups: Array<{ behaviorGroup: string; counts: DispositionCounts }>;
}

export interface DispositionMappingRule {
  ruleId: string;
  version: number;
  summary: string;
  authorizes: DispositionDecision[];
}

export interface DispositionLedgerView {
  summary: DispositionLedgerSummary;
  screens: DispositionScreenView[];
  rules: DispositionMappingRule[];
}

export interface DispositionEntry {
  ledgerId: string;
  entryId: string;
  identity: {
    moduleName: string;
    filePath: string;
    objectPath: string;
    objectType: string;
    propertyName: string;
    behaviorGroup: string;
  };
  observedValue: string;
  observedValueTruncated: boolean;
  observedEvidence: string;
  decision: DispositionDecision;
  rationale: string | null;
  mappingRuleId: string | null;
  mappingRuleVersion: number;
  decidedUtc: string | null;
  generatedRefs: Array<{ runId: string; artifactPath: string; contentSha256: string }>;
  testRefs: Array<{ runId: string; testId: string; outcome: DispositionVerification; detail: string }>;
  verification: DispositionVerification;
  version: number;
}

interface EntryPage {
  entries: DispositionEntry[];
  total: number;
  skip: number;
  take: number;
}

const PAGE = 25;
const DECISIONS: DispositionDecision[] = ["Preserve", "Transform", "Retire", "Defer"];

const VERIFICATION_LABEL: Record<DispositionVerification, string> = {
  NotExecuted: "Not executed",
  Passed: "Passed",
  Failed: "Failed",
};

async function send(path: string, method: string, body?: unknown) {
  const response = await fetch(path, {
    method,
    headers: { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await response.text();
  const payload = text ? JSON.parse(text) as Record<string, unknown> : {};
  if (!response.ok) {
    throw new Error(typeof payload.error === "string" ? payload.error : `Request failed (${response.status}).`);
  }
  return payload;
}

function reason(failure: unknown, fallback: string) {
  return failure instanceof Error ? failure.message : fallback;
}

function CountRow({ counts }: { counts: DispositionCounts }) {
  return <div className="mf-ledger-counts" role="group" aria-label="Disposition counts">
    <span><small>Discovered</small><strong data-testid="ledger-discovered">{counts.discovered}</strong></span>
    <span><small>Decided</small><strong data-testid="ledger-decided">{counts.decided}</strong></span>
    <span><small>Generated</small><strong data-testid="ledger-generated">{counts.generated}</strong></span>
    <span><small>Verified</small><strong data-testid="ledger-verified">{counts.verified}</strong></span>
    <span><small>Unresolved</small><strong data-testid="ledger-unresolved">{counts.unresolved}</strong></span>
  </div>;
}

/** One property, its declared value, and the decision form the server will adjudicate. */
function EntryRow({
  entry,
  rules,
  disabled,
  onDecide,
}: {
  entry: DispositionEntry;
  rules: DispositionMappingRule[];
  disabled: boolean;
  onDecide: (entry: DispositionEntry, decision: DispositionDecision, ruleId: string, ruleVersion: number, rationale: string) => Promise<void>;
}) {
  const [decision, setDecision] = useState<DispositionDecision | "">(
    entry.decision === "Unresolved" ? "" : entry.decision);
  const [ruleKey, setRuleKey] = useState(
    entry.mappingRuleId ? `${entry.mappingRuleId}@${entry.mappingRuleVersion}` : "");
  const [rationale, setRationale] = useState(entry.rationale ?? "");
  const [saving, setSaving] = useState(false);

  const allowed = decision ? rules.filter((rule) => rule.authorizes.includes(decision)) : [];
  const chosen = allowed.find((rule) => `${rule.ruleId}@${rule.version}` === ruleKey) ?? null;
  const ready = Boolean(decision) && chosen !== null && rationale.trim().length > 0;

  return <li className="mf-ledger-entry">
    <div className="mf-ledger-entry-head">
      <div>
        <strong>{entry.identity.objectPath}</strong>
        <small>{entry.identity.objectType} · {entry.identity.propertyName} · {entry.identity.behaviorGroup}</small>
      </div>
      <span className={entry.decision === "Unresolved" ? "mf-pill warn" : "mf-pill success"}>{entry.decision}</span>
      <span
        className={entry.verification === "Passed" ? "mf-pill success" : entry.verification === "Failed" ? "mf-pill danger" : "mf-pill neutral"}
        data-testid="entry-verification"
      >
        {VERIFICATION_LABEL[entry.verification]}
      </span>
    </div>
    <dl className="mf-ledger-facts">
      <div><dt>Declared value</dt><dd><code>{entry.observedValue || "(empty)"}</code>{entry.observedValueTruncated ? " …" : ""}</dd></div>
      <div><dt>How this is known</dt><dd>{entry.observedEvidence}</dd></div>
      <div><dt>Generated artifacts</dt><dd>{entry.generatedRefs.length === 0 ? "None recorded" : entry.generatedRefs.map((item) => item.artifactPath).join(", ")}</dd></div>
      <div><dt>Executed tests</dt><dd>{entry.testRefs.length === 0 ? "None executed" : entry.testRefs.map((item) => `${item.testId}: ${VERIFICATION_LABEL[item.outcome]}`).join(", ")}</dd></div>
    </dl>
    <div className="mf-ledger-decide">
      <label>
        <span>Disposition</span>
        <select
          value={decision}
          disabled={disabled || saving}
          data-testid="entry-decision"
          onChange={(event) => { setDecision(event.target.value as DispositionDecision | ""); setRuleKey(""); }}
        >
          <option value="">Choose…</option>
          {DECISIONS.map((value) => <option key={value} value={value}>{value}</option>)}
        </select>
      </label>
      <label>
        <span>Mapping rule</span>
        <select value={ruleKey} disabled={disabled || saving || !decision} data-testid="entry-rule" onChange={(event) => setRuleKey(event.target.value)}>
          <option value="">{decision ? "Choose a rule that authorizes it…" : "Choose a disposition first"}</option>
          {allowed.map((rule) => <option key={`${rule.ruleId}@${rule.version}`} value={`${rule.ruleId}@${rule.version}`}>
            {rule.ruleId} v{rule.version} — {rule.summary}
          </option>)}
        </select>
      </label>
      <label className="mf-ledger-rationale">
        <span>Why (required)</span>
        <textarea
          rows={2}
          value={rationale}
          disabled={disabled || saving}
          data-testid="entry-rationale"
          placeholder="State the reason this disposition is correct for this property."
          onChange={(event) => setRationale(event.target.value)}
        />
      </label>
      <button
        type="button"
        className="mf-secondary"
        disabled={disabled || saving || !ready}
        onClick={async () => {
          if (!ready || !chosen || !decision) return;
          setSaving(true);
          try {
            await onDecide(entry, decision, chosen.ruleId, chosen.version, rationale.trim());
          } finally {
            setSaving(false);
          }
        }}
      >
        {saving ? "Recording…" : "Record disposition"}
      </button>
    </div>
  </li>;
}

/** One screen: its counts, its behaviour groups, and its properties behind a disclosure. */
function ScreenSection({
  screen,
  ledgerId,
  rules,
  stale,
  onChanged,
}: {
  screen: DispositionScreenView;
  ledgerId: string;
  rules: DispositionMappingRule[];
  stale: boolean;
  onChanged: () => void;
}) {
  const [open, setOpen] = useState(false);
  const [group, setGroup] = useState("");
  const [undecidedOnly, setUndecidedOnly] = useState(false);
  const [page, setPage] = useState<EntryPage | null>(null);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(false);

  const load = useCallback(async (skip: number) => {
    setLoading(true);
    try {
      const query = new URLSearchParams({
        module: screen.moduleName,
        skip: String(skip),
        take: String(PAGE),
      });
      if (group) query.set("group", group);
      if (undecidedOnly) query.set("undecidedOnly", "true");
      const payload = await send(
        `/api/workbench/disposition-ledgers/${encodeURIComponent(ledgerId)}/entries?${query}`, "GET") as unknown as EntryPage;
      setPage((current) => skip > 0 && current
        ? { ...payload, entries: [...current.entries, ...payload.entries] }
        : payload);
      setError("");
    } catch (failure) {
      setError(reason(failure, "The properties for this screen could not be read."));
    } finally {
      setLoading(false);
    }
  }, [ledgerId, screen.moduleName, group, undecidedOnly]);

  useEffect(() => { if (open) void load(0); }, [open, load]);

  async function decide(
    entry: DispositionEntry,
    decision: DispositionDecision,
    ruleId: string,
    ruleVersion: number,
    rationale: string,
  ) {
    try {
      await send(
        `/api/workbench/disposition-ledgers/${encodeURIComponent(ledgerId)}/entries/${encodeURIComponent(entry.entryId)}/decision`,
        "POST",
        { decision, rationale, mappingRuleId: ruleId, mappingRuleVersion: ruleVersion, expectedVersion: entry.version });
      setError("");
      await load(0);
      onChanged();
    } catch (failure) {
      setError(reason(failure, "That disposition was refused."));
    }
  }

  return <details className="mf-ledger-screen" open={open} onToggle={(event) => setOpen(event.currentTarget.open)}>
    <summary>
      <strong>{screen.moduleName}</strong>
      <small>{screen.filePath}</small>
      <span className="mf-pill neutral">{screen.counts.decided}/{screen.counts.discovered} decided</span>
    </summary>
    <ul className="mf-ledger-groups">
      {screen.groups.map((item) => <li key={item.behaviorGroup}>
        <strong>{item.behaviorGroup}</strong>
        <small>{item.counts.decided} decided · {item.counts.unresolved} unresolved · {item.counts.verified} verified</small>
      </li>)}
    </ul>
    <div className="mf-ledger-filters">
      <label>
        <span>Behaviour group</span>
        <select value={group} onChange={(event) => setGroup(event.target.value)}>
          <option value="">All groups</option>
          {screen.groups.map((item) => <option key={item.behaviorGroup} value={item.behaviorGroup}>{item.behaviorGroup}</option>)}
        </select>
      </label>
      <label className="mf-ledger-toggle">
        <input type="checkbox" checked={undecidedOnly} onChange={(event) => setUndecidedOnly(event.target.checked)} />
        <span>Undecided only</span>
      </label>
    </div>
    {error && <p className="mf-error" role="alert">{error}</p>}
    {stale && <p className="mf-help">This ledger describes a superseded source snapshot, so the server refuses new dispositions against it.</p>}
    {page && <>
      <ul className="mf-ledger-entries">
        {page.entries.map((entry) => <EntryRow
          key={entry.entryId}
          entry={entry}
          rules={rules}
          disabled={stale}
          onDecide={decide}
        />)}
      </ul>
      <p className="mf-help">Showing {page.entries.length} of {page.total} properties.</p>
      {page.entries.length < page.total && <button type="button" className="mf-secondary" disabled={loading} onClick={() => void load(page.entries.length)}>
        {loading ? "Loading…" : "Show more properties"}
      </button>}
    </>}
    {!page && loading && <p role="status">Reading the properties for this screen…</p>}
  </details>;
}

export interface DispositionLedgerPanelProps {
  projectId: string;
  runId: string | null;
  /** The locator the next approval request and the next run request both carry, or null for neither. */
  selectedLedgerId?: string | null;
  /** Raised on an operator's own choice, and on an invalidation the server's list made unavoidable. */
  onSelectLedger?: (ledgerId: string | null) => void;
}

export function DispositionLedgerPanel({
  projectId,
  runId,
  selectedLedgerId = null,
  onSelectLedger,
}: DispositionLedgerPanelProps) {
  const [ledgers, setLedgers] = useState<DispositionLedgerSummary[]>([]);
  const [view, setView] = useState<DispositionLedgerView | null>(null);
  const [error, setError] = useState("");
  const [status, setStatus] = useState("");
  const [invalidated, setInvalidated] = useState("");
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    try {
      const payload = await send(
        `/api/workbench/projects/${encodeURIComponent(projectId)}/disposition-ledgers`, "GET") as { ledgers?: DispositionLedgerSummary[] };
      const listed = payload.ledgers ?? [];
      setLedgers(listed);
      setError("");

      const match = runId ? listed.find((item) => item.runId === runId) : undefined;
      setView(match
        ? await send(`/api/workbench/disposition-ledgers/${encodeURIComponent(match.ledgerId)}`, "GET") as unknown as DispositionLedgerView
        : null);
    } catch (failure) {
      setView(null);
      setError(reason(failure, "The disposition ledger could not be read."));
    }
  }, [projectId, runId]);

  useEffect(() => { void load(); }, [load]);

  // A held locator survives only while the server still lists it as usable. A ledger the project no
  // longer holds, or one the server now reports as describing a superseded snapshot, is dropped here
  // rather than sent on a request the server would refuse. The reason outlives the selection on
  // purpose: a choice that disappears without one looks like the console forgot it.
  useEffect(() => {
    if (!selectedLedgerId || ledgers.length === 0) {
      return;
    }
    const held = ledgers.find((item) => item.ledgerId === selectedLedgerId);
    if (!held) {
      setInvalidated("The decisions this run was set to generate under are no longer listed for this project, so nothing is selected.");
      onSelectLedger?.(null);
      return;
    }
    if (held.isStale) {
      setInvalidated(held.staleReason
        ?? "The selected decisions describe a superseded source snapshot, so nothing is selected.");
      onSelectLedger?.(null);
      return;
    }
    setInvalidated("");
  }, [ledgers, selectedLedgerId, onSelectLedger]);

  const selectable = ledgers.filter((item) => !item.isStale);
  const chosen = ledgers.find((item) => item.ledgerId === selectedLedgerId) ?? null;

  if (!runId && ledgers.length === 0 && !invalidated) return null;

  async function build() {
    if (!runId) return;
    setBusy(true);
    setError("");
    setStatus("");
    try {
      setView(await send(
        `/api/workbench/projects/${encodeURIComponent(projectId)}/disposition-ledgers`, "POST", { runId }) as unknown as DispositionLedgerView);
      setStatus("The ledger was built from the source facts this run exported. Nothing is decided yet, and no run generates under it until you select it below.");
      await load();
    } catch (failure) {
      setError(reason(failure, "The ledger could not be built from this run."));
    } finally {
      setBusy(false);
    }
  }

  return <section className="mf-result-section mf-ledger" aria-labelledby="mf-ledger-title" data-testid="disposition-ledger">
    <p className="mf-kicker">Source disposition</p>
    <h2 id="mf-ledger-title"><ClipboardList aria-hidden="true" />What the source declared, and what was decided</h2>
    <p className="mf-help">
      Discovered, decided, generated, and verified are four separate facts. A property counts as verified
      only where the server recorded a test that actually executed against generated output.
    </p>

    {error && <p className="mf-error" role="alert">{error}</p>}
    {status && <p role="status" aria-live="polite">{status}</p>}

    {onSelectLedger && <div data-testid="ledger-choice">
      <label className="mf-field">
        <span>Decisions the next run generates under</span>
        <select
          data-testid="run-ledger-select"
          aria-describedby="mf-ledger-choice-help"
          value={selectedLedgerId ?? ""}
          onChange={(event) => { setInvalidated(""); onSelectLedger(event.target.value || null); }}
        >
          <option value="">None — the next run generates under no recorded decisions</option>
          {selectable.map((item) => <option key={item.ledgerId} value={item.ledgerId}>
            {item.ledgerId} · {item.moduleCount} screen{item.moduleCount === 1 ? "" : "s"} · {item.counts.decided} of {item.counts.discovered} decided
          </option>)}
        </select>
      </label>
      <p className="mf-help" id="mf-ledger-choice-help">
        This identifier travels on the sandbox approval request and on the run request, and the server resolves
        it against this project and the source snapshot it hashed on both. Selecting one adjudicates nothing:
        the generation phase re-reads these decisions at the moment it generates. It is needed only where a run
        generates an Oracle Forms estate onto the .NET back end — an assessment run, a normalization run, and a
        schema-only .NET run each need none.
      </p>
      {ledgers.length > selectable.length && <p className="mf-help" data-testid="ledger-superseded-count">
        {ledgers.length - selectable.length} ledger{ledgers.length - selectable.length === 1 ? " is" : "s are"} not offered:
        the server reports {ledgers.length - selectable.length === 1 ? "it" : "them"} as describing a superseded source snapshot.
      </p>}
      {invalidated && <p className="mf-error" role="alert" data-testid="ledger-selection-invalid">{invalidated}</p>}
      {chosen && <p className="mf-help" data-testid="ledger-selection-summary">
        Selected <code>{chosen.ledgerId}</code>, recorded over snapshot <code>{chosen.sourceSnapshotHash.slice(0, 12)}</code> of <code>{chosen.sourceRoot}</code>.
        {" "}{chosen.counts.unresolved} propert{chosen.counts.unresolved === 1 ? "y is" : "ies are"} still unresolved, and the server refuses to
        generate while any are.
      </p>}
    </div>}

    {!view && runId && <div className="mf-ledger-empty">
      <p className="mf-help">No disposition ledger has been built from this run yet.</p>
      <button type="button" className="mf-secondary" disabled={busy} onClick={() => void build()} data-testid="build-ledger">
        <ClipboardList aria-hidden="true" />{busy ? "Building…" : "Build the ledger from this run"}
      </button>
    </div>}

    {view && <>
      <div className="mf-ledger-head">
        <CountRow counts={view.summary.counts} />
        <button type="button" className="mf-secondary mf-run-refresh" aria-label="Refresh the disposition ledger" onClick={() => void load()}>
          <RefreshCw aria-hidden="true" /><span className="mf-sr-only">Refresh the disposition ledger</span>
        </button>
      </div>

      {view.summary.isStale && <p className="mf-error" role="alert">
        <AlertTriangle aria-hidden="true" />{view.summary.staleReason ?? "This ledger describes a superseded source snapshot."}
      </p>}

      {!view.summary.completion.canComplete && <div className="mf-notice">
        <AlertTriangle aria-hidden="true" />
        <div>
          <strong>This ledger cannot be reported complete.</strong>
          <ul>{view.summary.completion.blockers.slice(0, 5).map((item) => <li key={item}>{item}</li>)}</ul>
          {view.summary.completion.blockers.length > 5 && <p className="mf-help">
            and {view.summary.completion.blockers.length - 5} more.
          </p>}
        </div>
      </div>}

      <p className="mf-help">
        {view.summary.moduleCount} screen{view.summary.moduleCount === 1 ? "" : "s"} read from <code>{view.summary.sourceRoot}</code>,
        snapshot <code>{view.summary.sourceSnapshotHash.slice(0, 12)}</code>.
      </p>

      <div className="mf-ledger-screens">
        {view.screens.map((screen) => <ScreenSection
          key={`${screen.moduleName}:${screen.filePath}`}
          screen={screen}
          ledgerId={view.summary.ledgerId}
          rules={view.rules}
          stale={view.summary.isStale}
          onChanged={() => void load()}
        />)}
      </div>
    </>}
  </section>;
}
