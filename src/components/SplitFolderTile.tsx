import { useEffect, useRef, useState, type ReactNode, type PointerEvent } from 'react';
import { FolderOpen, ArrowRight } from 'lucide-react';

type Gesture = { onPointerDown: (event: PointerEvent<HTMLButtonElement>) => void; onPointerMove: (event: PointerEvent) => void; onPointerUp: (event: PointerEvent) => void; onPointerCancel: () => void };
export function SplitFolderTile({ children, name, projectId, entryId, folderItemId, onOpen, onEnter, gesture, className = '', enabled = true, software = false }: { children: ReactNode; name: string; projectId: string; entryId: string; folderItemId?: string; onOpen: () => void; onEnter: () => void; gesture: Gesture; className?: string; enabled?: boolean; software?: boolean }) {
  const [ready, setReady] = useState(false);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const hold = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const held = useRef(false);
  const origin = useRef<{ x: number; y: number; at: number } | null>(null);
  useEffect(() => () => { clearTimeout(timer.current); clearTimeout(hold.current); }, []);
  const cancel = () => { clearTimeout(hold.current); held.current = false; gesture.onPointerCancel(); };
  const splitGesture = {
    onPointerDown: (event: PointerEvent<HTMLButtonElement>) => {
      if (event.button !== 0) return;
      held.current = true; gesture.onPointerDown(event);
      clearTimeout(hold.current);
      hold.current = setTimeout(() => { if (held.current) { window.dispatchEvent(new Event('luma:cascade-layout')); onEnter(); } }, 320);
    },
    onPointerMove: gesture.onPointerMove,
    onPointerUp: (event: PointerEvent) => { clearTimeout(hold.current); held.current = false; gesture.onPointerUp(event); },
    onPointerCancel: cancel,
    onLostPointerCapture: cancel,
  };
  const identity = { 'data-item-id': entryId, 'data-project-id': projectId, ...(folderItemId ? { 'data-folder-item-id': folderItemId } : {}) };
  if (!enabled) return children;
  return <div className={`split-folder ${ready ? 'split-ready' : ''} ${className}`}
    onPointerEnter={() => { clearTimeout(timer.current); timer.current = setTimeout(() => setReady(true), 250); }}
    onPointerLeave={() => { clearTimeout(timer.current); if (!held.current) setReady(false); }}
    onPointerDownCapture={event => {
      if (event.button !== 0) return;
      held.current = true; origin.current = { x: event.clientX, y: event.clientY, at: performance.now() };
      clearTimeout(timer.current);
      if (!ready) { clearTimeout(hold.current); hold.current = setTimeout(() => { if (held.current) { window.dispatchEvent(new Event('luma:cascade-layout')); onEnter(); } }, 320); }
    }}
    onPointerMoveCapture={event => { const start = origin.current; if (start && performance.now() - start.at < 300 && Math.hypot(event.clientX - start.x, event.clientY - start.y) > 6) { clearTimeout(hold.current); held.current = false; } }}
    onPointerUpCapture={() => { clearTimeout(hold.current); held.current = false; origin.current = null; }}
    onLostPointerCaptureCapture={() => { clearTimeout(hold.current); held.current = false; origin.current = null; }}
    onPointerCancelCapture={() => { clearTimeout(hold.current); held.current = false; origin.current = null; }}
    onKeyDown={event => { if (event.key === 'ArrowRight') { event.preventDefault(); gesture.onPointerCancel(); onEnter(); } }}>
    {children}
    {ready && <div className="split-folder-actions">
      <button {...identity} className="split-open" aria-label={`打开 ${name}`} title="单击打开，长按进入" {...splitGesture} onClick={event => { if (event.detail === 0) onOpen(); }}><FolderOpen size={17}/><span>{software ? '打开软件' : '打开'}</span></button>
      <button {...identity} data-entry-action="enter" className="split-enter" aria-label={software ? `查看 ${name} 最近项目` : `浏览 ${name} 子目录`} title="单击或长按进入" {...splitGesture} onClick={event => { if (event.detail === 0) onEnter(); }}><ArrowRight size={17}/><span>{software ? '最近' : '进入'}</span></button>
    </div>}
  </div>;
}
