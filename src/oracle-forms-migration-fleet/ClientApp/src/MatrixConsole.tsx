import { useEffect, useRef } from "react";
import { Terminal, X } from "lucide-react";
import type { ConsoleLine } from "./sourceClient";

/**
 * Live transcript of a server-side operation.
 *
 * Every line here is emitted by the server as the work happens: git output, extraction counts, and
 * the artifacts the indexer recognised. Nothing is invented to fill the gaps, so an empty console
 * means nothing has happened yet.
 */
export function MatrixConsole({
  title,
  subtitle,
  lines,
  running,
  onClose,
}: {
  title: string;
  subtitle: string;
  lines: ConsoleLine[];
  running: boolean;
  onClose: () => void;
}) {
  const log = useRef<HTMLDivElement>(null);
  const closeButton = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    log.current?.scrollTo({ top: log.current.scrollHeight, behavior: "smooth" });
  }, [lines.length]);

  useEffect(() => {
    closeButton.current?.focus();
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape" && !running) onClose();
    }
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [running, onClose]);

  return (
    <div className="mf-console-backdrop">
      <section className="mf-console" role="dialog" aria-modal="true" aria-labelledby="mf-console-title">
        <div className="mf-console-rain" aria-hidden="true">
          {Array.from({ length: 14 }, (_, column) => (
            <span key={column} style={{ animationDelay: `${(column % 7) * 0.45}s`, left: `${(column / 14) * 100}%` }} />
          ))}
        </div>
        <header>
          <span className="mf-console-mark"><Terminal aria-hidden="true" /></span>
          <div>
            <h2 id="mf-console-title">{title}</h2>
            <p>{subtitle}</p>
          </div>
          <button type="button" ref={closeButton} onClick={onClose} disabled={running} aria-label="Close the activity log">
            <X aria-hidden="true" />
          </button>
        </header>

        <div className="mf-console-log" ref={log} role="log" aria-live="polite" aria-busy={running} tabIndex={0}>
          {lines.length === 0 && <p className="mf-console-line info">Waiting for the server...</p>}
          {lines.map((line, index) => (
            <p className={`mf-console-line ${line.level}`} key={`${index}-${line.text}`}>
              <span aria-hidden="true">{prefix(line.level)}</span>
              {line.text}
            </p>
          ))}
          {running && <p className="mf-console-line cursor" aria-hidden="true"><span>&gt;</span><i /></p>}
        </div>

        <footer>
          {running
            ? <span className="mf-console-state running">Working. This window shows exactly what the server is doing.</span>
            : <span className="mf-console-state done">Finished. Close this window to carry on.</span>}
        </footer>
      </section>
    </div>
  );
}

function prefix(level: ConsoleLine["level"]) {
  switch (level) {
    case "error": return "!!";
    case "warn": return "??";
    case "found": return "++";
    case "skip": return "--";
    case "done": return "==";
    default: return ">>";
  }
}
