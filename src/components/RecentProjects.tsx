import { useEffect, useLayoutEffect, useRef, useState, type PointerEvent } from 'react';
import { FolderOpen, RefreshCw } from 'lucide-react';
import type { LaunchItem, Methods } from '../contracts';
import { request } from '../bridge';
import { ItemIcon } from './ItemIcon';
import { useBlankPan } from './useBlankPan';
export function RecentProjects({ projectId, item, limit, leadingColumns = 0, onOpen, ...gesture }: { projectId: string; item: LaunchItem; limit: number; leadingColumns?: number; onOpen: (entryId: string) => void; onPointerDown: (e: PointerEvent<HTMLButtonElement>) => void; onPointerMove: (e: PointerEvent) => void; onPointerUp: (e: PointerEvent) => void; onPointerCancel: () => void }) {
  const [recent, setRecent] = useState<Methods['shell.getRecent']['result'] | null>(null), [error, setError] = useState(''), [reload, setReload] = useState(0);
  const pan = useBlankPan();
  const root = useRef<HTMLDivElement>(null);
  useLayoutEffect(() => {
    const panel = root.current?.closest<HTMLElement>('.stack-panel'); if (!panel) return;
    const resize = () => { panel.style.width = `${Math.min(innerWidth - 32, (leadingColumns + 1) * 402 + 14)}px`; panel.dataset.cascadeLayout = `recent-${leadingColumns}`; };
    resize(); window.addEventListener('resize', resize);
    return () => { window.removeEventListener('resize', resize); panel.style.removeProperty('width'); delete panel.dataset.cascadeLayout; };
  }, [leadingColumns]);
  useEffect(() => { let alive = true; setRecent(null); setError(''); void request('shell.getRecent', { projectId, itemId: item.id, limit }).then(data => { if (alive) setRecent(data); }).catch(reason => { if (alive) setError(reason.message); }); return () => { alive = false; }; }, [projectId, item.id, item.path, limit, reload]);
  return <div ref={root} className="folder-column" aria-label={`${item.name} 最近项目`}><div className="folder-browser-path"><FolderOpen size={15}/><strong>最近项目 · {item.name}</strong><button className="icon-button" aria-label="刷新最近项目" onClick={() => setReload(n => n + 1)}><RefreshCw size={14}/></button></div>
    <div className="recent-list" {...pan}>{recent?.entries.map(entry => <button key={entry.id} data-item-id={entry.id} data-project-id={projectId} data-recent-item-id={item.id} {...gesture} onLostPointerCapture={gesture.onPointerCancel} onClick={e => { if (e.detail === 0) onOpen(entry.id); }} title={entry.path}><ItemIcon kind={entry.kind} size={30}/><span><strong>{entry.name}</strong><small>{entry.path}</small></span></button>)}</div>
    {!recent && !error && <p className="folder-browser-message">正在读取最近项目…</p>}{recent && !recent.entries.length && <p className="folder-browser-message">暂无可关联的最近项目</p>}<p className="directory-limit" role="status">{error || recent?.note}</p>
  </div>;
}
