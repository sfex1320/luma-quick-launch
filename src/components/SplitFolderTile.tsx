import { useEffect, useRef, useState, type ReactNode, type PointerEvent } from 'react';
import { FolderOpen, ArrowRight } from 'lucide-react';

type Gesture = { onPointerDown: (event: PointerEvent<HTMLButtonElement>) => void; onPointerMove: (event: PointerEvent) => void; onPointerUp: (event: PointerEvent) => void; onPointerCancel: () => void };
export function SplitFolderTile({ children, name, projectId, entryId, folderItemId, onOpen, onEnter, gesture, className = '', enabled = true }: { children: ReactNode; name: string; projectId: string; entryId: string; folderItemId?: string; onOpen: () => void; onEnter: () => void; gesture: Gesture; className?: string; enabled?: boolean }) {
  const [ready, setReady] = useState(false);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => () => clearTimeout(timer.current), []);
  const identity = { 'data-item-id': entryId, 'data-project-id': projectId, ...(folderItemId ? { 'data-folder-item-id': folderItemId } : {}) };
  if (!enabled) return children;
  return <div className={`split-folder ${ready ? 'split-ready' : ''} ${className}`}
    onPointerEnter={() => { clearTimeout(timer.current); timer.current = setTimeout(() => setReady(true), 250); }}
    onPointerLeave={() => { clearTimeout(timer.current); setReady(false); }}
    onPointerDownCapture={() => { if (!ready) clearTimeout(timer.current); }}
    onKeyDown={event => { if (event.key === 'ArrowRight') { event.preventDefault(); gesture.onPointerCancel(); onEnter(); } }}>
    {children}
    {ready && <div className="split-folder-actions">
      <button {...identity} className="split-open" aria-label={`打开 ${name}`} title="打开当前文件夹" {...gesture} onLostPointerCapture={gesture.onPointerCancel} onClick={event => { if (event.detail === 0) onOpen(); }}><FolderOpen size={17}/><span>打开</span></button>
      <button {...identity} data-entry-action="enter" className="split-enter" aria-label={`浏览 ${name} 子目录`} title="进入并浏览" onPointerDown={event => { event.stopPropagation(); gesture.onPointerCancel(); }} onClick={() => { gesture.onPointerCancel(); onEnter(); }}><ArrowRight size={17}/><span>进入</span></button>
    </div>}
  </div>;
}
