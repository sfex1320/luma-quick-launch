import { useCallback, useEffect, useLayoutEffect, useRef, useState, type ReactNode, type PointerEvent } from 'react';
import type { LaunchItem, Methods } from '../contracts';
import { request } from '../bridge';
import { fuzzyIncludes } from '../core/searchMatch';
import { ItemIcon } from './ItemIcon';
import { useMenuPan } from './useMenuPan';
import type { FolderHeaderControls } from './FolderBrowser';
export function RecentProjects({ projectId, item, limit, filter, leadingColumns = 0, onOpen, renderAccessory, onHeaderChange, ...gesture }: { renderAccessory?: (entry: LaunchItem) => ReactNode; projectId: string; item: LaunchItem; limit: number; filter?: string; leadingColumns?: number; onOpen: (entryId: string) => void; onHeaderChange?: (controls: FolderHeaderControls | null) => void; onPointerDown: (e: PointerEvent<HTMLButtonElement>) => void; onPointerMove: (event: PointerEvent) => void; onPointerUp: (event: PointerEvent) => void; onPointerCancel: () => void }) {
  const [recent, setRecent] = useState<Methods['shell.getRecent']['result'] | null>(null), [error, setError] = useState(''), [reload, setReload] = useState(0);
  const pan = useMenuPan();
  const root = useRef<HTMLDivElement>(null);
  const refresh = useCallback(() => setReload(n => n + 1), []);
  const callbacks = useRef(onHeaderChange); callbacks.current = onHeaderChange;
  useLayoutEffect(() => {
    const panel = root.current?.closest<HTMLElement>('.stack-panel'); if (!panel) return;
    const resize = () => { panel.style.width = `${Math.min(innerWidth - 32, (leadingColumns + 1) * 402 + 14)}px`; panel.dataset.cascadeLayout = `recent-${leadingColumns}`; };
    resize(); window.addEventListener('resize', resize);
    return () => { window.removeEventListener('resize', resize); panel.style.removeProperty('width'); delete panel.dataset.cascadeLayout; };
  }, [leadingColumns]);
  useEffect(() => { let alive = true; setRecent(null); setError(''); void request('shell.getRecent', { projectId, itemId: item.id, limit }).then(data => { if (alive) setRecent(data); }).catch(reason => { if (alive) setError(reason.message); }); return () => { alive = false; }; }, [projectId, item.id, item.path, limit, reload]);
  useEffect(() => {
    callbacks.current?.({ name: `最近项目 · ${item.name}`, loading: !recent && !error, disabled: !recent && !error, refresh });
    return () => callbacks.current?.(null);
  }, [recent, error, refresh, item.name]);
  const visible = recent && filter?.trim() ? recent.entries.filter(entry => fuzzyIncludes(filter, entry.name)) : recent?.entries ?? [];
  return <div ref={root} className="folder-column" aria-label={`${item.name} 最近项目`}>
    <div className="recent-list" {...pan}>{visible.map(entry => <div className="recent-row" key={entry.id}><button data-item-id={entry.id} data-project-id={projectId} data-recent-item-id={item.id} {...gesture} onLostPointerCapture={gesture.onPointerCancel} onClick={e => { if (e.detail === 0) onOpen(entry.id); }} title={`${entry.name}\n${entry.path}`}><ItemIcon kind={entry.kind} size={30}/><span><strong title={entry.name}>{entry.name}</strong><small title={entry.path}>{entry.path}</small></span></button>{renderAccessory?.(entry)}</div>)}</div>
    {!recent && !error && <p className="folder-browser-message">正在读取最近项目…</p>}{recent && !recent.entries.length && <p className="folder-browser-message">暂无可关联的最近项目</p>}{recent && recent.entries.length > 0 && !visible.length && <p className="folder-browser-message">当前菜单没有匹配「{filter?.trim()}」的内容</p>}<p className="directory-limit" role="status">{error || recent?.note}</p>
  </div>;
}
