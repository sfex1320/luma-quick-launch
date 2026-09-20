import { useRef, type PointerEvent, type MouseEvent } from 'react';

/** A right drag pans; a stationary right click remains available for context menus. */
export function useRightPan(axis: 'x' | 'y') {
  const drag = useRef<{ x: number; y: number; scroll: number; moved: boolean } | null>(null);
  const suppressUntil = useRef(0);
  return {
    onPointerDownCapture: (event: PointerEvent<HTMLElement>) => {
      if (event.button !== 2) return;
      drag.current = { x: event.clientX, y: event.clientY, scroll: axis === 'x' ? event.currentTarget.scrollLeft : event.currentTarget.scrollTop, moved: false };
    },
    onPointerMoveCapture: (event: PointerEvent<HTMLElement>) => {
      const record = drag.current; if (!record || !(event.buttons & 2)) return;
      const delta = axis === 'x' ? event.clientX - record.x : event.clientY - record.y;
      if (!record.moved && Math.abs(delta) < 7) return;
      record.moved = true; suppressUntil.current = Date.now() + 500;
      event.preventDefault(); event.stopPropagation();
      event.currentTarget.setPointerCapture(event.pointerId);
      if (axis === 'x') event.currentTarget.scrollLeft = record.scroll - delta;
      else event.currentTarget.scrollTop = record.scroll - delta;
    },
    onPointerUpCapture: (event: PointerEvent<HTMLElement>) => {
      if (event.button !== 2) return;
      if (drag.current?.moved) { suppressUntil.current = Date.now() + 500; event.preventDefault(); event.stopPropagation(); }
      drag.current = null;
      if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId);
    },
    onPointerCancelCapture: () => { drag.current = null; },
    onContextMenuCapture: (event: MouseEvent<HTMLElement>) => {
      if (Date.now() < suppressUntil.current) { event.preventDefault(); event.stopPropagation(); }
    },
  };
}
