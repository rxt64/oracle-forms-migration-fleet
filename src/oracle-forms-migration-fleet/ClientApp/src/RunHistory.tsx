import { History, RefreshCw, Square } from "lucide-react";
import { useEffect, useEffectEvent, useState } from "react";
import { cancelRun, fetchRuns, type DurableRunSummary } from "./sourceClient";

export function RunHistory({
  projectId,
  activeRunId,
  onOpen,
}: {
  projectId: string;
  activeRunId?: string | null;
  onOpen: (runId: string) => void;
}) {
  const [runs, setRuns] = useState<DurableRunSummary[]>([]);
  const [error, setError] = useState("");

  const load = useEffectEvent(async (signal?: AbortSignal) => {
    if (!projectId) {
      setRuns([]);
      return;
    }
    try {
      setRuns(await fetchRuns(projectId, signal));
      setError("");
    } catch (reason) {
      if (!signal?.aborted) setError(reason instanceof Error ? reason.message : "Run history could not be loaded.");
    }
  });

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    const timer = window.setInterval(() => void load(controller.signal), 3000);
    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
  }, [projectId]);

  return <section className="mf-run-history" aria-labelledby="run-history-title">
    <div className="mf-run-history-head">
      <div>
        <p className="mf-kicker">Durable execution</p>
        <h3 id="run-history-title">Run history</h3>
      </div>
      <button type="button" className="mf-secondary mf-run-refresh" title="Refresh run history" aria-label="Refresh run history" onClick={() => void load()}>
        <RefreshCw aria-hidden="true" />
        <span className="mf-sr-only">Refresh run history</span>
      </button>
    </div>
    {error && <p className="mf-error" role="alert">{error}</p>}
    {!error && runs.length === 0 && <p className="mf-help">No run has been queued for this project.</p>}
    {runs.length > 0 && <ul className="mf-run-history-list">
      {runs.map((run) => <li key={run.runId}>
        <div>
          <strong>{run.applicationName}</strong>
          <small>{run.engagementId} · {new Date(run.enqueuedUtc).toLocaleString()}</small>
        </div>
        <span className={`mf-pill ${tone(run.state)}`}>{run.state}</span>
        <button type="button" className="mf-secondary" onClick={() => onOpen(run.runId)}>
          <History aria-hidden="true" />{activeRunId === run.runId ? "Reopen activity" : "Open activity"}
        </button>
        {!terminal(run.state) && <button type="button" className="mf-secondary" onClick={async () => {
          try {
            await cancelRun(run.runId);
            await load();
          } catch (reason) {
            setError(reason instanceof Error ? reason.message : "Cancellation could not be requested.");
          }
        }}>
          <Square aria-hidden="true" />Cancel
        </button>}
      </li>)}
    </ul>}
  </section>;
}

function tone(state: DurableRunSummary["state"]) {
  if (state === "Succeeded") return "success";
  if (state === "Failed" || state === "Interrupted") return "danger";
  if (state === "Cancelled") return "warn";
  return "info";
}

function terminal(state: DurableRunSummary["state"]) {
  return state === "Succeeded" || state === "Failed" || state === "Cancelled" || state === "Interrupted";
}
