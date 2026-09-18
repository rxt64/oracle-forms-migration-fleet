import { useCallback, useEffect, useId, useLayoutEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { Info } from "lucide-react";

const CLOSE_DELAY_MS = 180;
const EDGE_MARGIN = 8;
const GAP = 8;

interface Placement {
  top: number;
  left: number;
}

/**
 * An "i" button that reveals a short explanation.
 *
 * It is a real button rather than a title attribute so the text is reachable by keyboard and by
 * touch. The panel is rendered into a portal and positioned against the viewport, so a scrolling
 * or clipped ancestor cannot cut it off and it stays inside a 390 px screen. The content holds no
 * interactive controls, so a tooltip role is honest and nothing interactive nests inside a label.
 */
export function InfoTip({ label, children }: { label: string; children: string }) {
  const [open, setOpen] = useState(false);
  const [placement, setPlacement] = useState<Placement | null>(null);
  const id = useId();
  const trigger = useRef<HTMLButtonElement>(null);
  const panel = useRef<HTMLSpanElement>(null);
  const closeTimer = useRef<number | undefined>(undefined);

  const cancelClose = useCallback(() => {
    if (closeTimer.current !== undefined) window.clearTimeout(closeTimer.current);
    closeTimer.current = undefined;
  }, []);

  const show = useCallback(() => {
    cancelClose();
    setOpen(true);
  }, [cancelClose]);

  const hide = useCallback(() => {
    cancelClose();
    setOpen(false);
    setPlacement(null);
  }, [cancelClose]);

  // A short grace period, so moving the pointer off the button and onto the panel does not close it.
  const scheduleClose = useCallback(() => {
    cancelClose();
    closeTimer.current = window.setTimeout(() => {
      setOpen(false);
      setPlacement(null);
    }, CLOSE_DELAY_MS);
  }, [cancelClose]);

  useEffect(() => cancelClose, [cancelClose]);

  useLayoutEffect(() => {
    if (!open) return;

    function place() {
      const anchor = trigger.current?.getBoundingClientRect();
      const content = panel.current?.getBoundingClientRect();
      if (!anchor || !content) return;
      const maxLeft = Math.max(EDGE_MARGIN, window.innerWidth - content.width - EDGE_MARGIN);
      const left = Math.min(Math.max(EDGE_MARGIN, anchor.left + anchor.width / 2 - content.width / 2), maxLeft);
      const above = anchor.top - content.height - GAP;
      const top = above >= EDGE_MARGIN
        ? above
        : Math.max(EDGE_MARGIN, Math.min(anchor.bottom + GAP, window.innerHeight - content.height - EDGE_MARGIN));
      setPlacement((current) => (current && current.top === top && current.left === left ? current : { top, left }));
    }

    place();
    window.addEventListener("resize", place);
    window.addEventListener("scroll", place, true);
    return () => {
      window.removeEventListener("resize", place);
      window.removeEventListener("scroll", place, true);
    };
  }, [open, children]);

  useEffect(() => {
    if (!open) return;

    function onPointerDown(event: PointerEvent) {
      const target = event.target as Node;
      if (trigger.current?.contains(target) || panel.current?.contains(target)) return;
      hide();
    }

    function onKeyDown(event: KeyboardEvent) {
      if (event.key !== "Escape") return;
      event.stopImmediatePropagation();
      hide();
      trigger.current?.focus();
    }

    document.addEventListener("pointerdown", onPointerDown);
    document.addEventListener("keydown", onKeyDown, true);
    return () => {
      document.removeEventListener("pointerdown", onPointerDown);
      document.removeEventListener("keydown", onKeyDown, true);
    };
  }, [open, hide]);

  return (
    <span className="mf-tip">
      <button
        type="button"
        ref={trigger}
        className="mf-tip-button"
        aria-label={`More information about ${label}`}
        data-open={open ? "true" : undefined}
        aria-describedby={open ? id : undefined}
        onClick={() => (open ? hide() : show())}
        onPointerEnter={(event) => { if (event.pointerType === "mouse") show(); }}
        onPointerLeave={(event) => { if (event.pointerType === "mouse") scheduleClose(); }}
        onBlur={scheduleClose}
      >
        <Info aria-hidden="true" />
      </button>
      {open && createPortal(
        <span
          id={id}
          ref={panel}
          role="tooltip"
          className="mf-tip-bubble"
          style={{ top: placement?.top ?? 0, left: placement?.left ?? 0, visibility: placement ? "visible" : "hidden" }}
          onPointerEnter={cancelClose}
          onPointerLeave={scheduleClose}
        >
          {children}
        </span>,
        document.body,
      )}
    </span>
  );
}
