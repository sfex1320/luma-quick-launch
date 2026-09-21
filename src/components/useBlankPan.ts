import { useCallback, useEffect, useRef, type MouseEvent, type PointerEvent } from 'react';

const controls = 'button,a,input,textarea,select,[contenteditable]:not([contenteditable="false"]),[draggable="true"],[role="button"],[data-item-id]';

/** Only a left press on the scroll surface's blank space becomes a grab. */
export function useBlankPan() {
  const drag = useRef<{ element: HTMLElement; pointer: number; y: number; scroll: number; moved: boolean } | null>(null);
  const suppressUntil = useRef(0);
  const clear = useCallback(() => {
    const record = drag.current; drag.current = null;
    if (!record) return;
    if (record.moved) suppressUntil.current = Date.now() + 500;
    delete record.element.dataset.grabPanning;
    if (record.element.hasPointerCapture(record.pointer)) record.element.releasePointerCapture(record.pointer);
  }, []);
  useEffect(() => {
    window.addEventListener('blur', clear);
    return () => { window.removeEventListener('blur', clear); clear(); };
  }, [clear]);
  return {
    'data-grab-pan': true,
    onPointerDownCapture: (event: PointerEvent<HTMLElement>) => {
      if (event.button !== 0 || (event.target as Element).closest(controls)) return;
      clear();
      drag.current = { element: event.currentTarget, pointer: event.pointerId, y: event.clientY, scroll: event.currentTarget.scrollTop, moved: false };
      event.currentTarget.setPointerCapture(event.pointerId);
    },
    onPointerMoveCapture: (event: PointerEvent<HTMLElement>) => {
      const record = drag.current;
      if (!record || record.pointer !== event.pointerId) return;
      if (!(event.buttons & 1)) { clear(); return; }
      const delta = event.clientY - record.y;
      if (!record.moved && Math.abs(delta) < 7) return;
      record.moved = true;
      record.element.dataset.grabPanning = 'true';
      event.preventDefault(); event.stopPropagation();
      record.element.scrollTop = record.scroll - delta;
    },
    onPointerUpCapture: (event: PointerEvent<HTMLElement>) => {
      if (event.button !== 0 || event.pointerId !== drag.current?.pointer) return;
      if (drag.current.moved) { event.preventDefault(); event.stopPropagation(); }
      clear();
    },
    onPointerCancelCapture: (event: PointerEvent<HTMLElement>) => { if (event.pointerId === drag.current?.pointer) clear(); },
    onLostPointerCaptureCapture: (event: PointerEvent<HTMLElement>) => { if (event.pointerId === drag.current?.pointer) clear(); },
    onClickCapture: (event: MouseEvent<HTMLElement>) => {
      if (event.detail > 0 && Date.now() < suppressUntil.current) { event.preventDefault(); event.stopPropagation(); }
    },
  };
}
