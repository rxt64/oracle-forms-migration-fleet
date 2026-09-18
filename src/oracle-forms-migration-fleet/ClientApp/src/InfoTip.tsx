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
 *
 * Opening has two modes, and the difference matters for long text. A hover opens it transiently: it
 * closes again when the pointer leaves, which is right for a glance. A click *pins* it: it then
 * survives the pointer leaving and the button losing focus, so the operator can read to the end
 * without keeping the mouse still. Clicking a pinned tip dismisses it. Keyboard focus previews the
 * text and Enter/Space pins it; moving keyboard focus away dismisses it so no unreachable help is
 * left on screen. Touch activation pins because touch has no hover state.
 */
export function InfoTip({ label, children }: { label: string; children: string }) {
  const [open, setOpen] = useState(false);
  const [pinned, setPinned] = useState(false);
  const [placement, setPlacement] = useState<Placement | null>(null);
  const id = useId();
  const trigger = useRef<HTMLButtonElement>(null);
  const panel = useRef<HTMLSpanElement>(null);
  const closeTimer = useRef<number | undefined>(undefined);
  // The close timer fires outside React's update cycle, so it reads the pin from a ref rather than
  // from a state value captured when the timer was scheduled.
  const isPinned = useRef(false);

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
    isPinned.current = false;
    setPinned(false);
    setOpen(false);
    setPlacement(null);
  }, [cancelClose]);

  // A short grace period, so moving the pointer off the button and onto the panel does not close it.
  // A pinned tip ignores this entirely: the operator asked for it to stay.
  const scheduleClose = useCallback(() => {
    cancelClose();
    closeTimer.current = window.setTimeout(() => {
      if (isPinned.current) return;
      setOpen(false);
      setPlacement(null);
    }, CLOSE_DELAY_MS);
  }, [cancelClose]);

  const toggle = useCallback(() => {
    // A click on an already-pinned tip dismisses it. A click on a hover-opened one pins it, so the
    // pointer can leave. A click on a closed one opens it pinned.
    if (isPinned.current) {
      hide();
      return;
    }
    cancelClose();
    isPinned.current = true;
    setPinned(true);
    setOpen(true);
  }, [hide, cancelClose]);

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
      const active = document.activeElement;
      const ownsFocus = trigger.current?.contains(active) || panel.current?.contains(active);
      event.stopImmediatePropagation();
      hide();
      if (ownsFocus) trigger.current?.focus();
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
        aria-expanded={open}
        data-open={open ? "true" : undefined}
        data-pinned={pinned ? "true" : undefined}
        aria-describedby={open ? id : undefined}
        onClick={toggle}
        onPointerEnter={(event) => { if (event.pointerType === "mouse") show(); }}
        onPointerLeave={(event) => { if (event.pointerType === "mouse") scheduleClose(); }}
        onFocus={show}
        onBlur={scheduleClose}
        onKeyDown={(event) => {
          if (event.key === "Tab" && isPinned.current) hide();
        }}
      >
        <Info aria-hidden="true" />
      </button>
      {open && createPortal(
        <span
          id={id}
          ref={panel}
          role="tooltip"
          className="mf-tip-bubble"
          data-pinned={pinned ? "true" : undefined}
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
