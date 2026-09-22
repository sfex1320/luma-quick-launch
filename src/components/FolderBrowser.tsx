import { useCallback, useEffect, useLayoutEffect, useRef, useState, type PointerEvent, type ReactNode } from 'react';
import { FolderOpen, Terminal } from 'lucide-react';
import type { Color, FolderEntry, FolderListing, LaunchItem, ProjectTestTask } from '../contracts';
import { request } from '../bridge';
import { fuzzyIncludes } from '../core/searchMatch';
import { FolderEntryIcon } from './FolderEntryIcon';
import { SplitFolderTile } from './SplitFolderTile';
import { clearFolderThumbnails } from './folder-thumbnail-cache';
import './folder-browser.css';
import { useMenuPan } from './useMenuPan';
import { DirectoryActionButtons, DirectoryActions, useDirectoryActions } from './DirectoryActions';

export type FolderHeaderControls = { name: string; loading: boolean; disabled: boolean; refresh: () => void; back?: () => void };
interface Props {
  projectId: string; item: LaunchItem; color: Color; highlight: string | null;
  filter: string;
  editing?: boolean; actionBadges?: Map<string, string>;
  onOpen: (entryId: string) => void; onBackToGroup?: () => void; onNavigate?: () => void; leadingColumns?: number;
  onRunTest: (taskId: string) => void; runningTest: boolean;
  onHeaderChange?: (controls: FolderHeaderControls | null) => void;
  renderEntryAccessory?: (entry: FolderEntry, listing: FolderListing, trail: string[]) => ReactNode;
  initialTrail?: string[]; onTrailChange?: (trail: string[]) => void;
  onPointerDown: (event: PointerEvent<HTMLButtonElement>) => void;
  onPointerMove: (event: PointerEvent) => void; onPointerUp: (event: PointerEvent) => void; onPointerCancel: () => void;
}
type Level = { folderId?: string; name: string };
export function FolderBrowser(props: Props) {
  const [levels, setLevels] = useState<Level[]>([{ name: props.item.name }]);
  const [limit, setLimit] = useState('');
  const [generation, setGeneration] = useState(0);
  const root = useRef<HTMLDivElement>(null);
  const listings = useRef(new Map<number, FolderListing>());
  const levelsRef = useRef(levels); levelsRef.current = levels;
  const current = useRef(props); current.current = props;
  // Remember names only. Every reopened level must acquire a fresh host token
  // from its current parent listing before the next level can be requested.
  const restoring = useRef({ names: (props.initialTrail ?? []).slice(0, 11), active: !!props.initialTrail?.length });
  const refreshTree = useCallback(() => {
    restoring.current.active = false;
    current.current.onPointerCancel(); current.current.onNavigate?.(); clearFolderThumbnails(); listings.current.clear();
    const next = [{ name: current.current.item.name }]; levelsRef.current = next; setLevels(next); setLimit('');
    setGeneration(value => value + 1); window.dispatchEvent(new Event('luma:cascade-layout'));
  }, []);
  const actions = useDirectoryActions({ projectId: props.projectId, itemId: props.item.id, onOpen: props.onOpen, onMutated: refreshTree, cancelGesture: props.onPointerCancel });
  const actionsRef = useRef(actions); actionsRef.current = actions;
  const enter = useCallback((index: number, folderId: string, name: string) => {
    if (!actionsRef.current.beforeNavigate()) return;
    restoring.current.active = false;
    if (index >= 11) { setLimit('已展开 12 层，可打开当前目录继续浏览。'); return; }
    if (levelsRef.current[index + 1]?.folderId === folderId) return;
    current.current.onNavigate?.();
    for (const level of listings.current.keys()) if (level > index) listings.current.delete(level);
    window.dispatchEvent(new Event('luma:cascade-layout'));
    setLevels(previous => [...previous.slice(0, index + 1), { folderId, name }]); setLimit('');
  }, []);
  useEffect(() => { if (!restoring.current.active) current.current.onTrailChange?.(levels.slice(1).map(level => level.name)); }, [levels, limit]);
  const reportListing = (index: number, listing: FolderListing | null) => {
    if (!listing) { listings.current.delete(index); return; }
    listings.current.set(index, listing);
    const restore = restoring.current;
    if (!restore.active || index !== levelsRef.current.length - 1) return;
    const name = restore.names[index];
    if (!name) { restore.active = false; current.current.onTrailChange?.(levelsRef.current.slice(1).map(level => level.name)); return; }
    const child = listing.entries.find(entry => entry.kind === 'folder' && entry.name === name);
    if (!child) { restore.active = false; setLimit(`记忆目录「${name}」已不可用，已停留在当前目录。`); return; }
    const next = [...levelsRef.current, { folderId: child.id, name: child.name }];
    levelsRef.current = next; setLevels(next);
    window.dispatchEvent(new Event('luma:cascade-layout'));
  };
  useEffect(() => {
    if (!props.highlight) return;
    for (const [index, listing] of listings.current) {
      const entry = listing.entries.find(item => item.id === props.highlight && item.kind === 'folder');
      if (entry) { const timer = setTimeout(() => enter(index, entry.id, entry.name), 450); return () => clearTimeout(timer); }
    }
  }, [props.highlight, enter]);
  useLayoutEffect(() => {
    const host = root.current?.closest<HTMLElement>('.stack-panel'); if (!host) return;
    const resize = () => { host.style.width = `${Math.min(innerWidth - 32, levels.length * 414 + (props.leadingColumns ?? 0) * 402 + 14)}px`; host.dataset.cascadeLayout = String(levels.length); if (root.current) root.current.style.width = `${levels.length * (Math.min(402, innerWidth - 58) + 12) - 12}px`; };
    resize(); window.addEventListener('resize', resize);
    if (levels.length > 1) root.current?.lastElementChild?.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: 'instant' });
    return () => { window.removeEventListener('resize', resize); host.style.removeProperty('width'); delete host.dataset.cascadeLayout; };
  }, [levels.length, props.leadingColumns]);
  return <><div ref={root} className="folder-cascade" aria-label="逐级目录">
    {levels.map((level, index) => <FolderPane {...props} key={`${generation}:${level.folderId ?? 'root'}`} level={level} index={index} trail={levels.slice(1, index + 1).map(parent => parent.name)} isCurrent={index === levels.length - 1} actions={actions}
      onListing={listing => reportListing(index, listing)}
      onLoadError={() => { if (restoring.current.active) { restoring.current.active = false; if (index > 0) setLevels(previous => previous.slice(0, index)); setLimit('记忆目录读取失败，已停留在可用的上级目录。'); } }}
      onEnter={(id, name) => enter(index, id, name)}
      onBack={index ? () => { if (!actions.beforeNavigate()) return; restoring.current.active = false; props.onPointerCancel(); props.onNavigate?.(); for (const key of listings.current.keys()) if (key >= index) listings.current.delete(key); setLevels(previous => previous.slice(0, index)); } : props.onBackToGroup ? () => { if (actions.beforeNavigate()) props.onBackToGroup?.(); } : undefined}/>) }
  </div>{limit && <p className="directory-limit" role="status">{limit}</p>}</>;
}

function FolderPane({ projectId, item, color, highlight, filter, editing = false, actionBadges, onOpen, onRunTest, runningTest, level, index, trail, isCurrent, onListing, onLoadError, onEnter, onBack, actions, onHeaderChange, renderEntryAccessory, initialTrail: _trail, onTrailChange: _trailChange, onBackToGroup: _back, onNavigate: _navigate, leadingColumns: _leading, ...gesture }: Props & { level: Level; index: number; trail: string[]; isCurrent: boolean; onListing: (listing: FolderListing | null) => void; onLoadError: () => void; onEnter: (id: string, name: string) => void; onBack?: () => void; actions: ReturnType<typeof useDirectoryActions> }) {
  const [listing, setListing] = useState<FolderListing | null>(null);
  const [error, setError] = useState(''), [loading, setLoading] = useState(true), [reload, setReload] = useState(0);
  const [testTask, setTestTask] = useState<ProjectTestTask | null>(null), [testError, setTestError] = useState('');
  const report = useRef(onListing); report.current = onListing;
  const callbacks = useRef({ actions, onHeaderChange, onLoadError, onBack, cancel: gesture.onPointerCancel }); callbacks.current = { actions, onHeaderChange, onLoadError, onBack, cancel: gesture.onPointerCancel };
  const refresh = useCallback(() => {
    if (!callbacks.current.actions.beforeNavigate()) return;
    callbacks.current.cancel(); clearFolderThumbnails(); setReload(value => value + 1);
  }, []);
  const back = useCallback(() => callbacks.current.onBack?.(), []);
  const hasHeaderBack = !!onBack;
  useEffect(() => {
    if (!isCurrent) return;
    callbacks.current.onHeaderChange?.({ name: listing?.name ?? level.name, loading, disabled: loading || actions.running, refresh, ...(hasHeaderBack ? { back } : {}) });
    return () => callbacks.current.onHeaderChange?.(null);
  }, [isCurrent, loading, actions.running, refresh, hasHeaderBack, back, listing?.name, level.name]);
  useEffect(() => {
    let alive = true; setLoading(true); setError(''); setListing(null); report.current(null);
    request('folder.list', { projectId, itemId: item.id, ...(level.folderId ? { folderId: level.folderId } : {}) })
      .then(result => { if (alive) { setListing(result); report.current(result); } })
      .catch(reason => { if (alive) { setError((reason as Error).message); callbacks.current.onLoadError(); } }).finally(() => { if (alive) setLoading(false); });
    return () => { alive = false; report.current(null); };
  }, [projectId, item.id, item.path, level.folderId, reload]);
  useEffect(() => {
    let alive = true; setTestTask(null); setTestError('');
    if (listing) void request('project.detectTest', { projectId, itemId: item.id, folderId: listing.folderId })
      .then(result => { if (alive) setTestTask(result.task); }).catch(reason => { if (alive) setTestError((reason as Error).message); });
    return () => { alive = false; };
  }, [listing, projectId, item.id, item.launch]);
  const pan = useMenuPan();
  const visibleEntries = listing && filter.trim() ? listing.entries.filter(entry => fuzzyIncludes(filter, entry.name)) : listing?.entries ?? [];
  return <div className="folder-browser folder-column" data-folder-level={index} aria-label={`${listing?.name ?? level.name} 目录层`}
    onContextMenu={event => { if (listing) actions.openContext(event, listing); }}>
    {loading && <p className="folder-browser-message" role="status">正在读取目录…</p>}
    {error && <div className="folder-browser-message" role="alert"><p>{error}</p><button className="text-button" onClick={() => setReload(n => n + 1)}>重新读取</button></div>}
    {listing && <>
      <div className="stack-items directory-items launch-grid" aria-label={`${listing.name} 目录内容`} {...pan}>
        {visibleEntries.map(entry => {
          const button = <button data-item-id={entry.id} data-project-id={projectId} data-folder-item-id={item.id} className={`stack-item ${highlight === entry.id ? 'selected' : ''}`} title={entry.name}
            {...gesture} onLostPointerCapture={gesture.onPointerCancel} onClick={event => { if (event.detail === 0) onOpen(entry.id); }}>
            <FolderEntryIcon projectId={projectId} itemId={item.id} entry={entry} color={color}/><span><strong>{entry.name}</strong><small>{entry.kind === 'folder' ? '文件夹' : entry.kind === 'app' ? '软件 / 快捷方式' : '文件'}</small></span>
          </button>;
          return <div className="directory-row" key={entry.id} draggable={!actions.running} onPointerDownCapture={event => actions.trackPress(event, entry)} onDragStart={event => actions.dragStart(event, entry)} onDragEnd={actions.dragEnd}
            onContextMenu={event => actions.openContext(event, listing, entry, entry.kind === 'folder' ? () => onEnter(entry.id, entry.name) : undefined)}>{entry.kind === 'folder' ? <SplitFolderTile name={entry.name} projectId={projectId} entryId={entry.id} folderItemId={item.id} onOpen={() => onOpen(entry.id)} onEnter={() => onEnter(entry.id, entry.name)} gesture={gesture}>{button}</SplitFolderTile> : button}{renderEntryAccessory?.(entry, listing, trail)}</div>;
        })}
        {!listing.entries.length && <p className="folder-browser-message">此目录为空</p>}
        {listing.entries.length > 0 && !visibleEntries.length && <p className="folder-browser-message">当前菜单没有匹配「{filter.trim()}」的内容</p>}
      </div>
      {listing.truncated && <p className="directory-limit">当前显示前 200 项，可打开目录查看全部内容。</p>}
      <footer className="directory-footer"><div className="directory-footer-actions">
        <button data-item-id={listing.folderId} data-project-id={projectId} data-folder-item-id={item.id} className={`text-button directory-open-current ${highlight === listing.folderId ? 'selected' : ''}`} {...gesture} onLostPointerCapture={gesture.onPointerCancel} onClick={event => { if (event.detail === 0) onOpen(listing.folderId); }}><FolderOpen size={14}/>打开当前目录{editing && actionBadges?.get('openDirectory') && <span className="shortcut-badge">{actionBadges.get('openDirectory')}</span>}</button>
        {testTask && <button data-item-id={testTask.id} data-project-id={projectId} data-folder-item-id={item.id} data-test-task-id={testTask.id} className={`text-button directory-open-current directory-run-test ${highlight === testTask.id ? 'selected' : ''}`} title={`${testTask.label} · ${testTask.command}`} disabled={runningTest}
          {...gesture} onLostPointerCapture={gesture.onPointerCancel} onClick={event => { if (event.detail === 0) onRunTest(testTask.id); }}><Terminal size={14}/>{runningTest ? '正在打开终端…' : testTask.label === '运行测试' ? '测试软件' : testTask.label === '手动启动' ? '打开项目软件' : testTask.label}{editing && actionBadges?.get('runProject') && <span className="shortcut-badge">{actionBadges.get('runProject')}</span>}</button>}
        <DirectoryActionButtons projectId={projectId} itemId={item.id} listing={listing} controller={actions} highlight={highlight} gesture={gesture} editing={editing} badges={actionBadges}/>
      </div></footer>
      {testError && <p className="directory-test-error" role="status">测试识别：{testError}</p>}
      <DirectoryActions listing={listing} controller={actions} showNotice={index === 0}/>
    </>}
  </div>;
}
