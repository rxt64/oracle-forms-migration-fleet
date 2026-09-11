import { useEffect, useId, useRef, useState } from "react";
import { Info } from "lucide-react";

/**
 * An "i" button that reveals a short explanation.
 *
 * It is a real button rather than a title attribute so the text is reachable by keyboard and by
 * touch, and it stays associated with the control through aria-describedby once open.
 */
export function InfoTip({ label, children }: { label: string; children: string }) {
  const [open, setOpen] = useState(false);
  const id = useId();
  const wrapper = useRef<HTMLSpanElement>(null);

  useEffect(() => {
    if (!open) return;

    function onPointerDown(event: PointerEvent) {
      if (!wrapper.current?.contains(event.target as Node)) setOpen(false);
    }

    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") setOpen(false);
    }

    document.addEventListener("pointerdown", onPointerDown);
    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("pointerdown", onPointerDown);
      document.removeEventListener("keydown", onKeyDown);
    };
  }, [open]);

  return (
    <span className="mf-tip" ref={wrapper}>
      <button
        type="button"
        className="mf-tip-button"
        aria-label={`What is ${label}?`}
        data-open={open ? "true" : undefined}
        aria-describedby={open ? id : undefined}
        onClick={() => setOpen(true)}
        onMouseEnter={() => setOpen(true)}
        onMouseLeave={() => setOpen(false)}
        onFocus={() => setOpen(true)}
        onBlur={() => setOpen(false)}
      >
        <Info aria-hidden="true" />
      </button>
      <span className={open ? "mf-tip-bubble open" : "mf-tip-bubble"} id={id} role="tooltip">
        {children}
      </span>
    </span>
  );
}
