import { useCallback, useEffect, useLayoutEffect, useRef, useState, type PointerEvent } from 'react';
import { ArrowLeft, FolderOpen, RefreshCw, Terminal } from 'lucide-react';
import type { Color, FolderListing, LaunchItem, ProjectTestTask } from '../contracts';
import { request } from '../bridge';
import { FolderEntryIcon } from './FolderEntryIcon';
import { SplitFolderTile } from './SplitFolderTile';
import { clearFolderThumbnails } from './folder-thumbnail-cache';
import './folder-browser.css';
import { useRightPan } from './useRightPan';
import { DirectoryActions, useDirectoryActions } from './DirectoryActions';

interface Props {
  projectId: string; item: LaunchItem; color: Color; highlight: string | null;
  onOpen: (entryId: string) => void; onBackToGroup?: () => void; onNavigate?: () => void; leadingColumns?: number;
  onRunTest: (taskId: string) => void; runningTest: boolean;
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
  const refreshTree = useCallback(() => {
    current.current.onPointerCancel(); current.current.onNavigate?.(); clearFolderThumbnails(); listings.current.clear();
    const next = [{ name: current.current.item.name }]; levelsRef.current = next; setLevels(next); setLimit('');
    setGeneration(value => value + 1); window.dispatchEvent(new Event('luma:cascade-layout'));
  }, []);
  const actions = useDirectoryActions({ projectId: props.projectId, itemId: props.item.id, onOpen: props.onOpen, onMutated: refreshTree, cancelGesture: props.onPointerCancel });
  const actionsRef = useRef(actions); actionsRef.current = actions;
  const enter = useCallback((index: number, folderId: string, name: string) => {
    if (!actionsRef.current.beforeNavigate()) return;
    if (index >= 11) { setLimit('已展开 12 层，可打开当前目录继续浏览。'); return; }
    if (levelsRef.current[index + 1]?.folderId === folderId) return;
    current.current.onNavigate?.();
    for (const level of listings.current.keys()) if (level > index) listings.current.delete(level);
    window.dispatchEvent(new Event('luma:cascade-layout'));
    setLevels(previous => [...previous.slice(0, index + 1), { folderId, name }]); setLimit('');
  }, []);
  useEffect(() => {
    if (!props.highlight) return;
    for (const [index, listing] of listings.current) {
      const entry = listing.entries.find(item => item.id === props.highlight && item.kind === 'folder');
      if (entry) { const timer = setTimeout(() => enter(index, entry.id, entry.name), 450); return () => clearTimeout(timer); }
    }
  }, [props.highlight, enter]);
  useLayoutEffect(() => {
    const host = root.current?.closest<HTMLElement>('.stack-panel'); if (!host) return;
    const resize = () => { host.style.width = `${Math.min(innerWidth - 32, (levels.length + (props.leadingColumns ?? 0)) * 402 + 14)}px`; host.dataset.cascadeLayout = String(levels.length); if (root.current) root.current.style.width = `${levels.length * (Math.min(390, innerWidth - 58) + 12) - 12}px`; };
    resize(); window.addEventListener('resize', resize);
    if (levels.length > 1) root.current?.lastElementChild?.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: 'instant' });
    return () => { window.removeEventListener('resize', resize); host.style.removeProperty('width'); delete host.dataset.cascadeLayout; };
  }, [levels.length, props.leadingColumns]);
  return <><div ref={root} className="folder-cascade" aria-label="逐级目录">
    {levels.map((level, index) => <FolderPane {...props} key={`${generation}:${level.folderId ?? 'root'}`} level={level} index={index} actions={actions}
      onListing={listing => { if (listing) listings.current.set(index, listing); else listings.current.delete(index); }}
      onEnter={(id, name) => enter(index, id, name)}
      onBack={index ? () => { if (!actions.beforeNavigate()) return; props.onPointerCancel(); props.onNavigate?.(); for (const key of listings.current.keys()) if (key >= index) listings.current.delete(key); setLevels(previous => previous.slice(0, index)); } : props.onBackToGroup ? () => { if (actions.beforeNavigate()) props.onBackToGroup?.(); } : undefined}/>) }
  </div>{limit && <p className="directory-limit" role="status">{limit}</p>}</>;
}

function FolderPane({ projectId, item, color, highlight, onOpen, onRunTest, runningTest, level, index, onListing, onEnter, onBack, actions, onBackToGroup: _back, onNavigate: _navigate, leadingColumns: _leading, ...gesture }: Props & { level: Level; index: number; onListing: (listing: FolderListing | null) => void; onEnter: (id: string, name: string) => void; onBack?: () => void; actions: ReturnType<typeof useDirectoryActions> }) {
  const [listing, setListing] = useState<FolderListing | null>(null);
  const [error, setError] = useState(''), [loading, setLoading] = useState(true), [reload, setReload] = useState(0);
  const [testTask, setTestTask] = useState<ProjectTestTask | null>(null), [testError, setTestError] = useState('');
  const report = useRef(onListing); report.current = onListing;
  useEffect(() => {
    let alive = true; setLoading(true); setError(''); setListing(null); report.current(null);
    request('folder.list', { projectId, itemId: item.id, ...(level.folderId ? { folderId: level.folderId } : {}) })
      .then(result => { if (alive) { setListing(result); report.current(result); } })
      .catch(reason => { if (alive) setError((reason as Error).message); }).finally(() => { if (alive) setLoading(false); });
    return () => { alive = false; report.current(null); };
  }, [projectId, item.id, item.path, level.folderId, reload]);
  useEffect(() => {
    let alive = true; setTestTask(null); setTestError('');
    if (listing) void request('project.detectTest', { projectId, itemId: item.id, folderId: listing.folderId })
      .then(result => { if (alive) setTestTask(result.task); }).catch(reason => { if (alive) setTestError((reason as Error).message); });
    return () => { alive = false; };
  }, [listing, projectId, item.id, item.launch]);
  const pan = useRightPan('y');
  return <div className="folder-browser folder-column" data-folder-level={index} aria-label={`${listing?.name ?? level.name} 目录层`}
    onContextMenu={event => { if (listing) actions.openContext(event, listing); }}>
    <div className="folder-browser-path">
      {onBack && <button className="icon-button" aria-label="返回上一层" onClick={onBack}><ArrowLeft size={16}/></button>}
      <FolderOpen size={16}/><strong title={listing?.name ?? level.name}>{listing?.name ?? level.name}</strong>
      <button className="icon-button" aria-label="刷新目录" disabled={loading || actions.running} onClick={() => { if (!actions.beforeNavigate()) return; clearFolderThumbnails(); setReload(n => n + 1); }}><RefreshCw size={14}/></button>
    </div>
    {loading && <p className="folder-browser-message" role="status">正在读取目录…</p>}
    {error && <div className="folder-browser-message" role="alert"><p>{error}</p><button className="text-button" onClick={() => setReload(n => n + 1)}>重新读取</button></div>}
    {listing && <>
      <div className="stack-items directory-items launch-grid" aria-label={`${listing.name} 目录内容`} {...pan}>
        {listing.entries.map(entry => {
          const button = <button data-item-id={entry.id} data-project-id={projectId} data-folder-item-id={item.id} className={`stack-item ${highlight === entry.id ? 'selected' : ''}`} title={entry.name}
            {...gesture} onLostPointerCapture={gesture.onPointerCancel} onClick={event => { if (event.detail === 0) onOpen(entry.id); }}>
            <FolderEntryIcon projectId={projectId} itemId={item.id} entry={entry} color={color}/><span><strong>{entry.name}</strong><small>{entry.kind === 'folder' ? '文件夹' : entry.kind === 'app' ? '软件 / 快捷方式' : '文件'}</small></span>
          </button>;
          return <div className="directory-row" key={entry.id} draggable={!actions.running} onPointerDownCapture={event => actions.trackPress(event, entry)} onDragStart={event => actions.dragStart(event, entry)} onDragEnd={actions.dragEnd}
            onContextMenu={event => actions.openContext(event, listing, entry, entry.kind === 'folder' ? () => onEnter(entry.id, entry.name) : undefined)}>{entry.kind === 'folder' ? <SplitFolderTile name={entry.name} projectId={projectId} entryId={entry.id} folderItemId={item.id} onOpen={() => onOpen(entry.id)} onEnter={() => onEnter(entry.id, entry.name)} gesture={gesture}>{button}</SplitFolderTile> : button}</div>;
        })}
        {!listing.entries.length && <p className="folder-browser-message">此目录为空</p>}
      </div>
      {listing.truncated && <p className="directory-limit">当前显示前 200 项，可打开目录查看全部内容。</p>}
      <footer className="directory-footer"><div className="directory-footer-actions">
        <button data-item-id={listing.folderId} data-project-id={projectId} data-folder-item-id={item.id} className={`text-button directory-open-current ${highlight === listing.folderId ? 'selected' : ''}`} {...gesture} onLostPointerCapture={gesture.onPointerCancel} onClick={event => { if (event.detail === 0) onOpen(listing.folderId); }}><FolderOpen size={14}/>打开当前目录</button>
        {testTask && <button data-item-id={testTask.id} data-project-id={projectId} data-folder-item-id={item.id} data-test-task-id={testTask.id} className={`text-button directory-open-current directory-run-test ${highlight === testTask.id ? 'selected' : ''}`} title={`${testTask.label} · ${testTask.command}`} disabled={runningTest}
          {...gesture} onLostPointerCapture={gesture.onPointerCancel} onClick={event => { if (event.detail === 0) onRunTest(testTask.id); }}><Terminal size={14}/>{runningTest ? '正在打开终端…' : testTask.label === '运行测试' ? '测试软件' : testTask.label === '手动启动' ? '打开项目软件' : testTask.label}</button>}
      </div><span>{listing.entries.length} 项 · 按住滑选，松手打开</span></footer>
      {testTask && <p className="directory-test-command" title={testTask.command}>{testTask.command} · 在终端运行</p>}
      {testError && <p className="directory-test-error" role="status">测试识别：{testError}</p>}
      <DirectoryActions listing={listing} controller={actions} showNotice={index === 0}/>
    </>}
  </div>;
}
