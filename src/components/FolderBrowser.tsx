import { useEffect, useRef, useState, type PointerEvent } from 'react';
import { ArrowLeft, ChevronRight, FolderOpen, RefreshCw, Terminal } from 'lucide-react';
import type { Color, FolderListing, LaunchItem, ProjectTestTask } from '../contracts';
import { request } from '../bridge';
import { ItemIcon } from './ItemIcon';
import './folder-browser.css';

interface Props {
  projectId: string; item: LaunchItem; color: Color; highlight: string | null;
  onOpen: (entryId: string) => void; onBackToGroup?: () => void;
  onRunTest: (taskId: string) => void; runningTest: boolean;
  onPointerDown: (event: PointerEvent<HTMLButtonElement>) => void;
  onPointerMove: (event: PointerEvent) => void; onPointerUp: (event: PointerEvent) => void; onPointerCancel: () => void;
}

export function FolderBrowser({ projectId, item, color, highlight, onOpen, onRunTest, runningTest, onBackToGroup, ...gesture }: Props) {
  const [folderId, setFolderId] = useState<string>();
  const [listing, setListing] = useState<FolderListing | null>(null);
  const [error, setError] = useState(''), [loading, setLoading] = useState(true), [reload, setReload] = useState(0);
  const generation = useRef(0);
  const [testTask, setTestTask] = useState<ProjectTestTask | null>(null), [testError, setTestError] = useState('');
  useEffect(() => {
    const current = ++generation.current;
    setLoading(true); setError(''); setListing(null);
    request('folder.list', { projectId, itemId: item.id, ...(folderId ? { folderId } : {}) })
      .then(result => { if (generation.current === current) setListing(result); })
      .catch(reason => { if (generation.current === current) setError((reason as Error).message); })
      .finally(() => { if (generation.current === current) setLoading(false); });
    return () => { generation.current++; };
  }, [projectId, item.id, item.path, folderId, reload]);
  useEffect(() => {
    let cancelled = false;
    setTestTask(null); setTestError('');
    if (listing) void request('project.detectTest', { projectId, itemId: item.id, folderId: listing.folderId })
      .then(result => { if (!cancelled) setTestTask(result.task); })
      .catch(reason => { if (!cancelled) setTestError((reason as Error).message); });
    return () => { cancelled = true; };
  }, [listing, projectId, item.id]);
  const back = () => { if (listing?.parentId) setFolderId(listing.parentId); else onBackToGroup?.(); };
  return <div className="folder-browser">
    <div className="folder-browser-path">
      {(listing?.parentId || onBackToGroup) && <button className="icon-button" aria-label="返回上一层" onClick={back}><ArrowLeft size={16}/></button>}
      <FolderOpen size={16}/><strong title={listing?.name ?? item.name}>{listing?.name ?? item.name}</strong>
      <button className="icon-button" aria-label="刷新目录" disabled={loading} onClick={() => setReload(n => n + 1)}><RefreshCw size={14}/></button>
    </div>
    {loading && <p className="folder-browser-message" role="status">正在读取目录…</p>}
    {error && <div className="folder-browser-message" role="alert"><p>{error}</p><button className="text-button" onClick={() => { setFolderId(undefined); setReload(n => n + 1); }}>重新读取</button></div>}
    {listing && <>
      <div className="stack-items directory-items launch-grid" aria-label={`${listing.name} 目录内容`}>
        {listing.entries.map(entry => <div className="directory-row" key={entry.id}>
          <button data-item-id={entry.id} data-project-id={projectId} data-folder-item-id={item.id}
            className={`stack-item ${highlight === entry.id ? 'selected' : ''}`} title={entry.name}
            {...gesture} onLostPointerCapture={gesture.onPointerCancel}
            onClick={event => { if (event.detail === 0) onOpen(entry.id); }}>
            <ItemIcon kind={entry.kind} color={color} size={38}/><span><strong>{entry.name}</strong><small>{entry.kind === 'folder' ? '文件夹' : entry.kind === 'app' ? '软件 / 快捷方式' : '文件'}</small></span>
          </button>
          {entry.kind === 'folder' && <button className="directory-browse icon-button" aria-label={`浏览 ${entry.name} 子目录`} title="浏览子目录" onClick={() => { gesture.onPointerCancel(); setFolderId(entry.id); }}><ChevronRight size={17}/></button>}
        </div>)}
        {!listing.entries.length && <p className="folder-browser-message">此目录为空</p>}
      </div>
      {listing.truncated && <p className="directory-limit">当前显示前 200 项，可打开目录查看全部内容。</p>}
      <footer className="directory-footer"><div className="directory-footer-actions">
        <button data-item-id={listing.folderId} data-project-id={projectId} data-folder-item-id={item.id} className={`text-button directory-open-current ${highlight === listing.folderId ? 'selected' : ''}`} {...gesture} onLostPointerCapture={gesture.onPointerCancel} onClick={event => { if (event.detail === 0) onOpen(listing.folderId); }}><FolderOpen size={14}/>打开当前目录</button>
        {testTask && <button data-item-id={testTask.id} data-project-id={projectId} data-folder-item-id={item.id} data-test-task-id={testTask.id}
          className={`text-button directory-open-current directory-run-test ${highlight === testTask.id ? 'selected' : ''}`}
          title={`${testTask.label} · ${testTask.command}`} disabled={runningTest}
          {...gesture} onLostPointerCapture={gesture.onPointerCancel}
          onClick={event => { if (event.detail === 0) onRunTest(testTask.id); }}>
          <Terminal size={14}/>{runningTest ? '正在打开终端…' : '运行测试'}
        </button>}
      </div><span>{listing.entries.length} 项 · 按住滑选，松手打开</span></footer>
      {testTask && <p className="directory-test-command" title={testTask.command}>{testTask.command} · 在终端运行</p>}
      {testError && <p className="directory-test-error" role="status">测试识别：{testError}</p>}
    </>}
  </div>;
}
