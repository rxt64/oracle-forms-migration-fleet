import { useEffect, useRef, type RefObject } from "react";
import { Activity, AlertTriangle, CheckCircle2, Info, Search, X, XCircle } from "lucide-react";
import type { ConsoleLine } from "./sourceClient";

const LEVEL_LABELS: Record<ConsoleLine["level"], string> = {
  info: "Step",
  warn: "Warning",
  error: "Error",
  found: "Found",
  skip: "Skipped",
  done: "Finished",
};

/**
 * Transcript of a server-side operation.
 *
 * Every line here was emitted by the server as the work happened: git output, extraction counts,
 * and the artifacts the indexer recognised. Nothing is invented to fill the gaps, so an empty pane
 * means nothing has happened yet. The summary counts those same lines and adds no claim of its own.
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

  useEffect(() => {
    log.current?.scrollTo({ top: log.current.scrollHeight });
  }, [lines.length]);

  useEffect(() => {
    opener.current = document.activeElement as HTMLElement | null;
    closeButton.current?.focus();
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") {
        onClose();
        return;
      }
      if (event.key !== "Tab") return;
      const stops = Array.from(panel.current?.querySelectorAll<HTMLElement>(
        'button:not([disabled]), a[href], input:not([disabled]), textarea:not([disabled]), select:not([disabled]), details > summary, [tabindex]:not([tabindex="-1"])') ?? [])
        .filter((element) => element.offsetParent !== null);
      if (stops.length === 0) return;
      const first = stops[0];
      const last = stops[stops.length - 1];
      const active = document.activeElement;
      if (event.shiftKey && (active === first || !panel.current?.contains(active))) {
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
      // Closing the pane returns the operator to whatever opened it rather than to the page top.
      const returnTarget = opener.current?.isConnected ? opener.current : fallbackFocus?.current;
      returnTarget?.focus();
    };
  }, [fallbackFocus, onClose]);

  const counts = summarise(lines);

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
          <p className="mf-activity-state" role="status" aria-live="polite">
            {running
              ? <><span className="mf-pill info"><Activity aria-hidden="true" />Working</span>The server is still sending lines.</>
              : <><span className="mf-pill neutral"><CheckCircle2 aria-hidden="true" />Idle</span>The server has stopped sending lines.</>}
          </p>

          <ul className="mf-activity-counts">
            <li><Info aria-hidden="true" /><span>Steps reported</span><strong>{counts.steps}</strong></li>
            <li><Search aria-hidden="true" /><span>Items found</span><strong>{counts.found}</strong></li>
            <li className={counts.warnings > 0 ? "attention" : undefined}><AlertTriangle aria-hidden="true" /><span>Warnings</span><strong>{counts.warnings}</strong></li>
            <li className={counts.errors > 0 ? "failure" : undefined}><XCircle aria-hidden="true" /><span>Errors</span><strong>{counts.errors}</strong></li>
          </ul>

          <details className="mf-activity-raw" open>
            <summary>Server output ({lines.length} {lines.length === 1 ? "line" : "lines"})</summary>
            <div className="mf-activity-log" ref={log} role="log" aria-live="polite" aria-busy={running} tabIndex={0}>
              {lines.length === 0 && <p className="mf-activity-line info"><span>Step</span>No output yet.</p>}
              {lines.map((line, index) => (
                <p className={`mf-activity-line ${line.level}`} key={`${index}-${line.text}`}>
                  <span>{LEVEL_LABELS[line.level]}</span>
                  {line.text}
                </p>
              ))}
            </div>
          </details>
        </div>

        <footer>
          <p><strong>Closing this pane does not cancel anything.</strong> It only hides the transcript; whatever the server is doing carries on.</p>
          <p className="mf-activity-limit">
            <AlertTriangle aria-hidden="true" />
            This run is carried by the browser request that started it. Reloading the page or losing the connection ends that
            request, and there is no durable job to resume — so the work stops and no result comes back. There is no cancel
            control here because no adapter can be stopped once it has started.
          </p>
        </footer>
      </section>
    </div>
  );
}

function summarise(lines: ConsoleLine[]) {
  return {
    steps: lines.filter((line) => line.level === "info" || line.level === "done").length,
    found: lines.filter((line) => line.level === "found").length,
    warnings: lines.filter((line) => line.level === "warn" || line.level === "skip").length,
    errors: lines.filter((line) => line.level === "error").length,
  };
}
