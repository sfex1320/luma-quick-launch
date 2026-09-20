import { useCallback, useEffect, useRef, useState, type DragEvent, type MouseEvent, type PointerEvent } from 'react';
import { createPortal } from 'react-dom';
import { Copy, FolderPlus, MoveRight } from 'lucide-react';
import { request } from '../bridge';
import type { FolderEntry, FolderListing, Methods } from '../contracts';
import { ContextMenu, type MenuAction } from './ContextMenu';
import './directory-actions.css';

export const REAL_ENTRY_MIME = 'application/x-luma-real-entry';
type MoveSource = { projectId: string; itemId: string; entryId: string; name: string };
let preparedMove: MoveSource | null = null;
const preparedEvent = 'luma:prepared-directory-move';
function prepare(source: MoveSource | null) { preparedMove = source; window.dispatchEvent(new Event(preparedEvent)); }
function readSource(text: string): MoveSource | null {
  if (text.length > 1800) return null;
  try {
    const value = JSON.parse(text);
    if (!value || typeof value !== 'object' || Array.isArray(value) || Object.keys(value).sort().join(',') !== 'entryId,itemId,name,projectId') return null;
    if (['projectId', 'itemId', 'entryId', 'name'].some(key => typeof value[key] !== 'string' || !value[key].trim() || value[key].length > (key === 'name' ? 255 : 200))) return null;
    return { projectId: value.projectId, itemId: value.itemId, entryId: value.entryId, name: value.name };
  } catch { return null; }
}
type Form = { owner: string; target: string; targetName: string; type: 'create' | 'rename'; initial: string }
  | { owner: string; target: string; targetName: string; type: 'move'; source: MoveSource };
type Context = { x: number; y: number; listing: FolderListing; entry?: FolderEntry; onEnter?: () => void };
type Result = Methods['folder.move']['result'];

export function useDirectoryActions({ projectId, itemId, onOpen, onMutated, cancelGesture }: {
  projectId: string; itemId: string; onOpen: (entryId: string) => void; onMutated: () => void; cancelGesture: () => void;
}) {
  const [context, setContext] = useState<Context | null>(null), [form, setForm] = useState<Form | null>(null);
  const [running, setRunning] = useState(false), [dragging, setDragging] = useState(false), [notice, setNotice] = useState('');
  const [prepared, setPrepared] = useState(preparedMove);
  const activeRequest = useRef(false), alive = useRef(true);
  const pressed = useRef<{ id: string; at: number } | null>(null);
  const callbacks = useRef({ onMutated, cancelGesture }); callbacks.current = { onMutated, cancelGesture };
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  useEffect(() => { const changed = () => setPrepared(preparedMove); window.addEventListener(preparedEvent, changed); return () => window.removeEventListener(preparedEvent, changed); }, []);
  const busy = !!context || !!form || running || dragging;
  useEffect(() => {
    window.dispatchEvent(new CustomEvent('luma:directory-operation', { detail: { busy } }));
    return () => { window.dispatchEvent(new CustomEvent('luma:directory-operation', { detail: { busy: false } })); };
  }, [busy]);
  const closeContext = useCallback(() => setContext(null), []);
  const closeForm = () => { if (!activeRequest.current) setForm(null); };
  const beginForm = (next: Form) => { if (activeRequest.current) return; callbacks.current.cancelGesture(); setContext(null); setNotice(''); setForm(next); };
  const confirmMove = (listing: FolderListing, source: MoveSource, target = listing.folderId, targetName = listing.name) =>
    beginForm({ type: 'move', owner: listing.folderId, target, targetName, source });
  const copy = async (entryId: string) => {
    if (activeRequest.current) return;
    activeRequest.current = true; setRunning(true); setNotice('');
    try {
      const result = await request('folder.getPath', { projectId, itemId, entryId });
      if (!alive.current) return;
      await navigator.clipboard.writeText(result.path);
      if (alive.current) setNotice('已复制实际地址');
    } catch (error) { if (alive.current) setNotice(`复制失败：${(error as Error).message}`); }
    finally { activeRequest.current = false; if (alive.current) setRunning(false); }
  };
  const submit = async (name: string) => {
    if (!form || activeRequest.current) return;
    activeRequest.current = true; setRunning(true); setNotice('');
    try {
      let result: Result;
      if (form.type === 'move') result = await request('folder.move', { sourceProject: form.source.projectId, sourceItem: form.source.itemId, sourceEntryIds: [form.source.entryId], targetProject: projectId, targetItem: itemId, targetFolderId: form.target });
      else if (form.type === 'create') result = await request('folder.createFolder', { projectId, itemId, folderId: form.target, name });
      else result = await request('folder.rename', { projectId, itemId, entryId: form.target, name });
      if (!alive.current) return;
      setNotice(result.completed ? '实际目录已更新' : result.message ?? `已更新 ${result.changedCount} 项，其余未完成，请重新查看目录。`);
      if (result.completed || result.changedCount > 0) {
        setForm(null); prepare(null); callbacks.current.onMutated();
      }
    } catch (error) { if (alive.current) setNotice((error as Error).message); }
    finally { activeRequest.current = false; if (alive.current) setRunning(false); }
  };
  const openContext = (event: MouseEvent, listing: FolderListing, entry?: FolderEntry, onEnter?: () => void) => {
    if (event.defaultPrevented || activeRequest.current) return;
    event.preventDefault(); event.stopPropagation(); callbacks.current.cancelGesture(); setForm(null);
    setContext({ x: event.clientX, y: event.clientY, listing, entry, onEnter });
  };
  const actions: MenuAction[] = context ? [
    { label: '打开', action: () => onOpen(context.entry?.id ?? context.listing.folderId) },
    ...(context.entry?.kind === 'folder' && context.onEnter ? [{ label: '进入', action: context.onEnter }] : []),
    { label: '复制地址', action: () => { void copy(context.entry?.id ?? context.listing.folderId); } },
    ...(context.entry ? [
      { label: '重命名', action: () => beginForm({ type: 'rename', owner: context.listing.folderId, target: context.entry!.id, targetName: context.entry!.name, initial: context.entry!.name }) },
      { label: '准备移动此实际目录项', action: () => { prepare({ projectId, itemId, entryId: context.entry!.id, name: context.entry!.name }); setNotice(`已准备移动「${context.entry!.name}」，请在目标实际目录确认。`); } },
    ] : []),
    { label: '新建文件夹（当前实际目录）', action: () => beginForm({ type: 'create', owner: context.listing.folderId, target: context.listing.folderId, targetName: context.listing.name, initial: '' }) },
    ...(!context.entry || context.entry.kind === 'folder' ? [{ label: '粘贴移动到此实际目录', disabled: !prepared, action: () => { if (prepared) confirmMove(context.listing, prepared, context.entry?.id ?? context.listing.folderId, context.entry?.name ?? context.listing.name); } }] : []),
  ] : [];
  return {
    context, actions, closeContext, form, closeForm, running, notice, prepared, submit, copy, confirmMove, openContext,
    create: (listing: FolderListing) => beginForm({ type: 'create', owner: listing.folderId, target: listing.folderId, targetName: listing.name, initial: '' }),
    clearPrepared: () => prepare(null),
    beforeNavigate: () => { if (activeRequest.current) return false; setContext(null); setForm(null); return true; },
    trackPress: (event: PointerEvent, entry: FolderEntry) => { if (event.button === 0) pressed.current = { id: entry.id, at: performance.now() }; },
    dragStart: (event: DragEvent, entry: FolderEntry) => {
      // A settled long press belongs to continuous launch/navigation. Do not let HTML drag cancel its capture.
      if (pressed.current?.id === entry.id && performance.now() - pressed.current.at >= 300) { event.preventDefault(); return; }
      if (activeRequest.current || form) { event.preventDefault(); return; }
      event.stopPropagation(); callbacks.current.cancelGesture(); setContext(null); setDragging(true);
      event.dataTransfer.effectAllowed = 'move';
      event.dataTransfer.setData(REAL_ENTRY_MIME, JSON.stringify({ projectId, itemId, entryId: entry.id, name: entry.name }));
    },
    dragEnd: () => { pressed.current = null; callbacks.current.cancelGesture(); setDragging(false); },
    drop: (event: DragEvent, listing: FolderListing) => {
      if (!event.dataTransfer.types.includes(REAL_ENTRY_MIME)) return;
      event.preventDefault(); event.stopPropagation(); setDragging(false); callbacks.current.cancelGesture();
      if (activeRequest.current) return;
      const source = readSource(event.dataTransfer.getData(REAL_ENTRY_MIME));
      if (!source) { setNotice('无法读取目录项，请从实际目录重新拖入。'); return; }
      confirmMove(listing, source);
    },
  };
}

type Controller = ReturnType<typeof useDirectoryActions>;
type ActionGesture = { onPointerDown: (event: PointerEvent<HTMLButtonElement>) => void; onPointerMove: (event: PointerEvent) => void; onPointerUp: (event: PointerEvent) => void; onPointerCancel: () => void };
export function DirectoryActionButtons({ projectId, itemId, listing, controller, highlight, gesture }: { projectId: string; itemId: string; listing: FolderListing; controller: Controller; highlight: string | null; gesture: ActionGesture }) {
  const [over, setOver] = useState(false);
  const actionId = (name: string) => `${name}:${listing.folderId}`;
  const props = (name: string) => ({ 'data-item-id': actionId(name), 'data-project-id': projectId, 'data-folder-item-id': itemId, 'data-entry-action': name, ...gesture, onLostPointerCapture: gesture.onPointerCancel });
  return <>
    <button type="button" {...props('create-directory')} className={`text-button directory-open-current ${highlight === actionId('create-directory') ? 'selected' : ''}`} disabled={controller.running} title="在当前实际目录新建文件夹" onClick={event => { if (event.detail === 0) controller.create(listing); }}><FolderPlus size={13}/>新建文件夹</button>
    <button type="button" {...props('copy-directory')} className={`text-button directory-open-current ${highlight === actionId('copy-directory') ? 'selected' : ''}`} disabled={controller.running} onClick={event => { if (event.detail === 0) void controller.copy(listing.folderId); }}><Copy size={13}/>复制地址</button>
    <button type="button" {...props('move-directory')} className={`directory-move-target ${over ? 'drag-over' : ''} ${highlight === actionId('move-directory') ? 'selected' : ''}`} aria-label={`移动到当前实际目录 ${listing.name}`} title={controller.prepared ? `移动「${controller.prepared.name}」到当前实际目录（需确认）` : '将真实目录项拖到这里，确认后移动'} aria-disabled={controller.running}
      onDragOver={event => { if (!event.dataTransfer.types.includes(REAL_ENTRY_MIME)) return; event.preventDefault(); event.stopPropagation(); event.dataTransfer.dropEffect = controller.running ? 'none' : 'move'; setOver(!controller.running); }}
      onDragLeave={() => setOver(false)} onDrop={event => { setOver(false); controller.drop(event, listing); }}
      onClick={event => { if (event.detail === 0 && controller.prepared && !controller.running) controller.confirmMove(listing, controller.prepared); }}><MoveRight size={14}/></button>
  </>;
}

export function DirectoryActions({ listing, controller, showNotice }: { listing: FolderListing; controller: Controller; showNotice: boolean }) {
  const form = controller.form?.owner === listing.folderId ? controller.form : null;
  const [name, setName] = useState('');
  const input = useRef<HTMLInputElement>(null), confirmation = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    setName(form && form.type !== 'move' ? form.initial : '');
    if (form) requestAnimationFrame(() => { (form.type === 'move' ? confirmation.current : input.current)?.focus({ preventScroll: true }); input.current?.select(); });
  }, [form]);
  const badName = !!name && (!name.trim() || /[<>:"/\\|?*\u0000-\u001f]/.test(name) || /[. ]$/.test(name) || /^(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)/i.test(name));
  return <div className="directory-actual-actions" onKeyDown={event => {
    if (event.key === 'Escape' && form) { event.preventDefault(); event.stopPropagation(); controller.closeForm(); }
  }}>
    {controller.prepared && <button type="button" className="text-button directory-clear-move" disabled={controller.running} onClick={controller.clearPrepared}>取消待移动项</button>}
    {form && <form className="directory-actual-form" aria-label={form.type === 'move' ? '确认实际移动' : form.type === 'create' ? '新建实际文件夹' : '重命名实际目录项'} onSubmit={event => { event.preventDefault(); if (form.type === 'move' || (name && !badName)) void controller.submit(name); }}>
      <strong>{form.type === 'move' ? '确认移动实际文件或文件夹' : form.type === 'create' ? '新建实际文件夹' : '重命名实际文件或文件夹'}</strong>
      {form.type === 'move' ? <p>将「{form.source.name}」移动到实际目录「{form.targetName}」。同名项不会被覆盖。</p> : <>
        <p>{form.type === 'create' ? `创建位置：${form.targetName}` : `实际目录项：${form.targetName} · 将修改磁盘中的名称`}</p>
        <label htmlFor={`directory-name-${listing.folderId}`}>新名称</label><input ref={input} id={`directory-name-${listing.folderId}`} value={name} maxLength={255} disabled={controller.running} onChange={event => setName(event.target.value)} autoComplete="off"/>
        {badName && <p role="alert">请输入合法名称，不包含路径或保留设备名。</p>}
      </>}
      <div className="directory-form-buttons"><button type="button" className="text-button" disabled={controller.running} onClick={controller.closeForm}>取消</button>
        <button ref={confirmation} type="submit" className="directory-confirm" disabled={controller.running || (form.type !== 'move' && (!name || badName))}>{controller.running ? '正在更新实际目录…' : form.type === 'move' ? '确认移动' : form.type === 'create' ? '确认新建' : '确认重命名'}</button></div>
    </form>}
    {showNotice && controller.notice && <p className="directory-operation-notice" role="status">{controller.notice}</p>}
    {controller.context?.listing.folderId === listing.folderId && createPortal(<ContextMenu x={controller.context.x} y={controller.context.y} actions={controller.actions} onClose={controller.closeContext}/>, document.body)}
  </div>;
}
