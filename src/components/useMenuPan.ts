import type { PointerEvent } from 'react';
import { useBlankPan } from './useBlankPan';
import { useRightPan } from './useRightPan';

/** Sub-menu scroll surface: left blank grab plus right-drag vertical pan.
    A moved right drag suppresses the context menu; a stationary right click keeps it. */
export function useMenuPan() {
  const blank = useBlankPan();
  const right = useRightPan('y');
  return {
    'data-grab-pan': true as const,
    onPointerDownCapture: (event: PointerEvent<HTMLElement>) => { blank.onPointerDownCapture(event); right.onPointerDownCapture(event); },
    onPointerMoveCapture: (event: PointerEvent<HTMLElement>) => { blank.onPointerMoveCapture(event); right.onPointerMoveCapture(event); },
    onPointerUpCapture: (event: PointerEvent<HTMLElement>) => { blank.onPointerUpCapture(event); right.onPointerUpCapture(event); },
    onPointerCancelCapture: (event: PointerEvent<HTMLElement>) => { blank.onPointerCancelCapture(event); right.onPointerCancelCapture(); },
    onLostPointerCaptureCapture: blank.onLostPointerCaptureCapture,
    onClickCapture: blank.onClickCapture,
    onContextMenuCapture: right.onContextMenuCapture,
  };
}
