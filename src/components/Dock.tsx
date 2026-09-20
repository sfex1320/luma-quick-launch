import { hasUrlText } from '../core/urls';
import { useEffect, useLayoutEffect, useRef, useState, type CSSProperties, type PointerEvent } from 'react';
import { ChevronDown, Search, Settings2, X, Pencil, Check } from 'lucide-react';
import type { LaunchItem, Preferences, Project } from '../contracts';
import { computeLayout } from '../core/layout';
import { releaseGesture } from '../core/gesture';
import { GroupIcon } from './GroupIcon';
import { FolderBrowser } from './FolderBrowser';
import { SplitFolderTile } from './SplitFolderTile';
import { GroupEntryMenu } from './GroupEntryMenu';
import { useDockMotion } from './useDockMotion';
import { nativeMode, request, subscribeHost } from '../bridge';
import { RecentProjects } from './RecentProjects';
import { useRightPan } from './useRightPan';
import { ContextMenu, type MenuAction } from './ContextMenu';
interface Props { projects: Project[]; preferences: Preferences; onOpen: (p: Project, item: LaunchItem) => void; onSettings: () => void; onSearch: () => void; onDropFiles?: (files: File[], projectId?: string) => Promise<void>; onDropText?: (text: string, projectId?: string) => Promise<void>; onGroup?: (sourceId: string, targetId: string) => void; onUngroup?: (projectId: string) => void; onReorder?: (sourceId: string, targetId: string, after: boolean) => void; onRemove?: (projectId: string, itemId?: string) => void; onMoveItem?: (sourceId: string, itemId: string, targetId?: string) => void; overlay?: boolean }
export function Dock({ projects, preferences: p, onOpen, onSettings, onSearch, onDropFiles, onDropText, onGroup, onUngroup, onReorder, onRemove, onMoveItem, overlay = false }: Props) {
  const pan = useRightPan('x');
  const [directoryBusy, setDirectoryBusy] = useState(false), [contextOpen, setContextOpen] = useState(false);
  useEffect(() => { const listener = (event: Event) => setContextOpen(!!(event as CustomEvent).detail?.open); window.addEventListener('luma:context-menu', listener); return () => window.removeEventListener('luma:context-menu', listener); }, []);
  useEffect(() => { const listener = (event: Event) => setDirectoryBusy(!!(event as CustomEvent).detail?.busy); window.addEventListener('luma:directory-operation', listener); return () => window.removeEventListener('luma:directory-operation', listener); }, []);
  const [context, setContext] = useState<{ x: number; y: number; actions: MenuAction[] } | null>(null);
  const container = useRef<HTMLDivElement>(null);
  const dock = useRef<HTMLElement>(null);
  const previousDockWidth = useRef<number | undefined>(undefined);
  const widthAnimation = useRef<{ animation: Animation; x: number; width: number; height: number } | null>(null);
  const [space, setSpace] = useState(900), [visible, setVisible] = useState(!(overlay && nativeMode)), [stack, setStack] = useState<string | null>(null), [more, setMore] = useState(false), [highlight, setHighlight] = useState<string | null>(null);
  const [visibilityEpoch, setVisibilityEpoch] = useState(0);
  // A renderer reload has no host event history yet; omit the id so native can accept
  // the fresh document's first collapsed layout before replaying the current sequence.
  const [hostVisibilityId, setHostVisibilityId] = useState<number | undefined>(undefined);
  const [editing, setEditing] = useState(false), [browseItemId, setBrowseItemId] = useState<string | null>(null), [menuError, setMenuError] = useState('');
  const [groupSource, setGroupSource] = useState<string | null>(null), [groupTarget, setGroupTarget] = useState<string | null>(null);
  const [groupIntent, setGroupIntent] = useState('merge');
  const menuGeneration = useRef(0);
  const lastVisibility = useRef<number | undefined>(undefined);
  const testRequest = useRef(false);
  const [runningTest, setRunningTest] = useState(false);
  const groupDrag = useRef<{ source: string; x: number; y: number; moved: boolean; button: HTMLButtonElement; pointer: number } | null>(null);
  const [pressing, setPressing] = useState(false), [keyboard, setKeyboard] = useState(false), [importing, setImporting] = useState(false);
  const dragLeaveTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const press = useRef<{ project: Project; origin: 'project' | 'entry'; long: boolean; x: number; y: number; lastX: number; lastY: number; needsMove: boolean; timer: ReturnType<typeof setTimeout>; button: HTMLButtonElement; pointer: number } | null>(null);
  const hideTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined), showTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const pinned = projects.filter(project => project.pinned);
  const layout = computeLayout(p.width, p.height, p.iconSize, space, 1, 136, pinned.length);
  const overflow = pinned.length > layout.capacity;
  const shown = pinned;
  const active = projects.find(project => project.id === stack);
  const browseItem = active?.items.find(item => item.id === browseItemId) ?? (active?.items.length === 1 && ['folder','app'].includes(active.items[0].kind) ? active.items[0] : undefined);
  const [dragging, setDragging] = useState(false), [pointerInside, setPointerInside] = useState(false);
  const clearPress = () => { const prev = press.current; if (prev) { clearTimeout(prev.timer); press.current = null; if (prev.button.hasPointerCapture(prev.pointer)) prev.button.releasePointerCapture(prev.pointer); } setPressing(false); setHighlight(null); };
  const clearGroupDrag = () => { const record = groupDrag.current; groupDrag.current = null; if (record?.button.hasPointerCapture(record.pointer)) record.button.releasePointerCapture(record.pointer); setGroupSource(null); setGroupTarget(null); };
  const close = () => { menuGeneration.current++; clearPress(); clearGroupDrag(); setStack(null); setBrowseItemId(null); setMenuError(''); setMore(false); };
  const showStack = (id: string | null) => { menuGeneration.current++; setBrowseItemId(null); setMenuError(''); setStack(id); setMore(false); };
  const { present, wrap, getTranslationY, resumeEntrance } = useDockMotion({ visible, reducedMotion: p.reducedMotion, waitForLayout: overlay && nativeMode, onExited: close });
  useLayoutEffect(() => {
    const element = dock.current;
    const fromWidth = previousDockWidth.current;
    previousDockWidth.current = layout.width;
    widthAnimation.current?.animation.cancel();
    widthAnimation.current = null;
    if (!element || fromWidth === undefined || fromWidth === layout.width || p.reducedMotion) return;
    const rect = element.getBoundingClientRect();
    const sweptWidth = Math.max(fromWidth, layout.width);
    const animation = element.animate(
      [{ transform: `scaleX(${fromWidth / layout.width})` }, { transform: 'scaleX(1)' }],
      { duration: 220, easing: 'cubic-bezier(.22,1,.36,1)' },
    );
    widthAnimation.current = { animation, x: rect.left + (rect.width - sweptWidth) / 2, width: sweptWidth, height: rect.height };
    element.dataset.widthAnimating = 'true';
    const finish = () => {
      if (widthAnimation.current?.animation !== animation) return;
      widthAnimation.current = null;
      delete element.dataset.widthAnimating;
    };
    animation.addEventListener('finish', finish, { once: true });
    animation.addEventListener('cancel', finish, { once: true });
    return () => animation.cancel();
  }, [layout.width, p.reducedMotion]);
  useEffect(() => {
    const element = container.current; if (!element) return;
    let previousWidth = -1;
    const observer = new ResizeObserver(entries => { const width = entries[0].contentRect.width; if (width !== previousWidth) { previousWidth = width; clearPress(); setSpace(width); } });
    observer.observe(element);
    return () => { observer.disconnect(); clearTimeout(hideTimer.current); clearTimeout(showTimer.current); clearTimeout(dragLeaveTimer.current); if (press.current) clearTimeout(press.current.timer); };
  }, []);
  useEffect(() => {
    const key = (event: KeyboardEvent) => { if (event.key === 'Escape') { clearTimeout(showTimer.current); if (editing) { clearPress(); clearGroupDrag(); setEditing(false); } else if (stack || more) close(); else { close(); setVisible(false); } } };
    const blur = () => { clearGroupDrag(); clearPress(); setKeyboard(false); };
    const cascade = () => { if (press.current) press.current.needsMove = true; };
    window.addEventListener('keydown', key); window.addEventListener('blur', blur); window.addEventListener('luma:cascade-layout', cascade);
    return () => { window.removeEventListener('keydown', key); window.removeEventListener('blur', blur); window.removeEventListener('luma:cascade-layout', cascade); };
  }, [stack, more, editing]);
  useEffect(() => subscribeHost(event => { if (overlay && event.event === 'window.visibility') { if (event.data.visibilityId !== undefined && lastVisibility.current !== undefined && event.data.visibilityId < lastVisibility.current) return; lastVisibility.current = event.data.visibilityId; clearTimeout(hideTimer.current); clearTimeout(showTimer.current); clearPress(); setVisible(event.data.visible); setHostVisibilityId(event.data.visibilityId); setVisibilityEpoch(epoch => epoch + 1); if (!event.data.visible) { clearGroupDrag(); setEditing(false); setDragging(false); setKeyboard(false); } } }), [overlay]);
  useEffect(() => {
    if (!active || active.items.length < 2 || !highlight || !press.current?.long) return;
    const item = active.items.find(item => item.id === highlight && ['folder','app'].includes(item.kind));
    if (!item || browseItemId === item.id) return;
    const timer = setTimeout(() => { menuGeneration.current++; if (press.current) press.current.needsMove = true; setBrowseItemId(item.id); }, 450);
    return () => clearTimeout(timer);
  }, [active, highlight, browseItemId]);
  // Commit-time cleanup is essential: a passive effect can send yesterday's
  // `visible=false` with today's expanded DOM and immediately hide the HWND.
  useLayoutEffect(() => {
    if (!overlay || !nativeMode || !container.current) return;
    let previous = '';
    let disposed = false;
    const sync = () => {
      const rects = [...document.querySelectorAll<HTMLElement>('[data-native-hit]')].map(el => { const r = el.getBoundingClientRect(); const y = r.y - (wrap.current?.contains(el) ? getTranslationY() : 0); return { x: r.x, y, width: r.width, height: r.height }; });
      // Reserve the upward path once, including reversals with an open, wider stack.
      if (present && wrap.current) for (const el of wrap.current.querySelectorAll<HTMLElement>('[data-native-hit]')) {
        const r = el.getBoundingClientRect(); const bottom = r.bottom - getTranslationY();
        rects.push({ x: r.x, y: 0, width: r.width, height: Math.max(0, bottom) });
      }
      const widthSweep = widthAnimation.current;
      if (widthSweep && dock.current) {
        const r = dock.current.getBoundingClientRect();
        rects.push({ x: widthSweep.x, y: r.y, width: widthSweep.width, height: widthSweep.height });
      }
      const data = { expanded: present, rects, visibilityId: hostVisibilityId, interacting: visible && present && (editing || directoryBusy || contextOpen || !!context || dragging || importing || runningTest || pressing || keyboard || (pointerInside && (!!stack || more))) };
      const text = JSON.stringify(data); if (text === previous) return; previous = text;
      void request('window.sync', data).then(result => { if (!disposed && visible && present && result.applied) resumeEntrance(); }).catch(() => { /* The host owns reconnection status. No polling loop. */ });
    };
    const observer = new ResizeObserver(sync); container.current.querySelectorAll('[data-native-hit]').forEach(el => observer.observe(el));
    // 原生 toast 在 Dock 外渲染；必须随出现/消失更新 HWND 裁剪，否则错误提示不可见。
    const mutations = new MutationObserver(() => { document.querySelectorAll('[data-native-hit]').forEach(el => observer.observe(el)); sync(); });
    mutations.observe(document.getElementById('root')!, { childList: true, subtree: true, attributes: true, attributeFilter: ['data-width-animating', 'data-cascade-layout'] });
    sync(); window.addEventListener('resize', sync);
    const element = container.current; element.addEventListener('animationend', sync);
    return () => { disposed = true; mutations.disconnect(); observer.disconnect(); window.removeEventListener('resize', sync); element.removeEventListener('animationend', sync); };
  }, [visible, present, visibilityEpoch, hostVisibilityId, stack, more, p, overlay, projects, dragging, importing, runningTest, pressing, keyboard, editing, directoryBusy, contextOpen, context, pointerInside, getTranslationY, resumeEntrance, wrap]);
  const begin = (event: PointerEvent<HTMLButtonElement>, project: Project, origin: 'project' | 'entry' = 'project') => {
    if (event.button !== 0) return;
    clearPress(); clearTimeout(hideTimer.current);
    if (editing && origin === 'project') {
      clearGroupDrag(); event.currentTarget.setPointerCapture(event.pointerId);
      groupDrag.current = { source: project.id, x: event.clientX, y: event.clientY, moved: false, button: event.currentTarget, pointer: event.pointerId };
      setGroupSource(project.id); return;
    }
    setMore(false);
    event.currentTarget.setPointerCapture(event.pointerId);
    const record = { project, origin, long: false, x: event.clientX, y: event.clientY, lastX: event.clientX, lastY: event.clientY, needsMove: false, button: event.currentTarget, pointer: event.pointerId, timer: setTimeout(() => {}, 0) };
    record.timer = setTimeout(() => { record.long = true; if (origin === 'project') showStack(project.id); else setHighlight(event.currentTarget?.dataset.itemId ?? record.button.dataset.itemId ?? null); }, 300);
    press.current = record;
    setPressing(true);
  };
  const move = (event: PointerEvent) => {
    const drag = groupDrag.current;
    if (drag) { drag.moved ||= Math.hypot(event.clientX - drag.x, event.clientY - drag.y) > 6; const tile = document.elementFromPoint(event.clientX, event.clientY)?.closest<HTMLElement>('[data-drop-project]'); const target = tile?.dataset.dropProject; if (tile) { const r = tile.getBoundingClientRect(), ratio = (event.clientX - r.left) / r.width; setGroupIntent(ratio < .22 ? 'before' : ratio > .78 ? 'after' : 'merge'); } setGroupTarget(drag.moved && target !== drag.source ? target ?? null : null); return; }
    const record = press.current; if (!record) return;
    if (event.clientX !== record.lastX || event.clientY !== record.lastY) record.needsMove = false;
    record.lastX = event.clientX; record.lastY = event.clientY;
    if (!record.long && Math.hypot(event.clientX - record.x, event.clientY - record.y) > 6) { clearPress(); return; }
    const hit = document.elementFromPoint(event.clientX, event.clientY)?.closest<HTMLElement>('[data-item-id]');
    setHighlight(record.long && hit?.dataset.projectId === record.project.id ? hit.dataset.itemId ?? null : null);
  };
  const activateEntry = (project: Project, child: HTMLElement) => {
    if (child instanceof HTMLButtonElement && child.disabled) return;
    if (child.dataset.entryAction === 'enter') child.click();
    else if (child.dataset.recentItemId && child.dataset.itemId) void openRecent(project.id, child.dataset.recentItemId, child.dataset.itemId);
    else if (child.dataset.testTaskId && child.dataset.folderItemId) void runProjectTest(project.id, child.dataset.folderItemId, child.dataset.testTaskId);
    else if (child.dataset.folderItemId && child.dataset.itemId) void openDirectory(project.id, child.dataset.folderItemId, child.dataset.itemId);
    else { const item = project.items.find(item => item.id === child.dataset.itemId); if (item) open(project, item); }
  };
  const openRecent = async (projectId: string, itemId: string, entryId: string) => { try { const result = await request('shell.openRecent', { projectId, itemId, entryId }); if (result.accepted) close(); } catch (reason) { setMenuError((reason as Error).message); } };
  const runProjectTest = async (projectId: string, itemId: string, taskId: string) => {
    if (testRequest.current) return;
    testRequest.current = true; setRunningTest(true); setMenuError('');
    const generation = menuGeneration.current;
    const stillCurrent = () => generation === menuGeneration.current && [...container.current?.querySelectorAll<HTMLElement>('[data-test-task-id]') ?? []].some(element => element.dataset.testTaskId === taskId);
    try {
      const result = await request('project.runTest', { projectId, itemId, taskId });
      if (result.opened && stillCurrent()) close();
      else if (!result.opened && stillCurrent()) setMenuError('终端未打开，请重试。');
    } catch (error) { if (stillCurrent()) setMenuError((error as Error).message); }
    finally { testRequest.current = false; setRunningTest(false); }
  };
  const openDirectory = async (projectId: string, itemId: string, entryId: string) => {
    const generation = menuGeneration.current;
    setMenuError('');
    try { const result = await request('folder.open', { projectId, itemId, entryId }); if (result.accepted && generation === menuGeneration.current) close(); }
    catch (error) { if (generation === menuGeneration.current) setMenuError((error as Error).message); }
  };
  const end = (event: PointerEvent) => {
    const drag = groupDrag.current;
    if (drag) {
      const tile = document.elementFromPoint(event.clientX, event.clientY)?.closest<HTMLElement>('[data-drop-project]');
      const target = tile?.dataset.dropProject;
      clearGroupDrag();
      if (drag.moved && target && target !== drag.source && tile) { const r = tile.getBoundingClientRect(), ratio = (event.clientX - r.left) / r.width; if (ratio < .22 || ratio > .78) onReorder?.(drag.source, target, ratio > .78); else onGroup?.(drag.source, target); close(); }
      else if (!drag.moved) showStack(drag.source);
      return;
    }
    const record = press.current; if (!record) return;
    if (record.needsMove) { clearPress(); return; }
    const element = document.elementFromPoint(event.clientX, event.clientY);
    const child = element?.closest<HTMLElement>('[data-item-id]');
    const target = child?.dataset.projectId === record.project.id ? 'child' : record.button.contains(element ?? null) ? 'origin' : 'outside';
    const action = record.origin === 'entry' ? (target === 'child' && (record.long || record.button.contains(element)) ? 'child' : 'cancel') : releaseGesture(record.long, target, false);
    clearPress();
    if (action === 'root') { onOpen(record.project, record.project.items[0]); setStack(null); }
    else if (action === 'child' && child) activateEntry(record.project, child);
    else if (action === 'cancel') setStack(null);
  };
  const open = (project: Project, item: LaunchItem) => { onOpen(project, item); close(); };
  return <div ref={container} className={`dock-container ${overlay ? 'overlay-dock' : ''} ${dragging ? 'drop-active' : ''}`}
    onKeyDownCapture={event => { if (event.key !== 'Escape') { setKeyboard(true); clearTimeout(hideTimer.current); } }}
    onFocusCapture={event => { if (event.target.matches(':focus-visible')) { setKeyboard(true); clearTimeout(hideTimer.current); } }}
    onPointerDownCapture={() => setKeyboard(false)}
    onBlurCapture={event => { if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setKeyboard(false); }}
    onDragOver={event => { if (!event.dataTransfer.types.includes('Files') && !hasUrlText(event.dataTransfer)) return; event.preventDefault(); event.stopPropagation(); event.dataTransfer.dropEffect = 'copy'; clearTimeout(hideTimer.current); clearTimeout(dragLeaveTimer.current); setDragging(true); setVisible(true); }}
    onDragLeave={event => { if (!event.currentTarget.contains(event.relatedTarget as Node | null)) { clearTimeout(dragLeaveTimer.current); dragLeaveTimer.current = setTimeout(() => setDragging(false), 120); } }}
    onDrop={async event => { event.preventDefault(); event.stopPropagation(); clearTimeout(dragLeaveTimer.current); setDragging(false); const files = Array.from(event.dataTransfer.files); const projectId = (event.target as HTMLElement).closest<HTMLElement>('[data-drop-project]')?.dataset.dropProject; if (importing) return; const text = event.dataTransfer.getData('text/uri-list') || event.dataTransfer.getData('text/plain'); if (!files.length && !text) return; setImporting(true); try { if (files.length) await onDropFiles?.(files, projectId); else await onDropText?.(text, projectId); } finally { setImporting(false); } }}>
    <div className="dock-hotzone" onPointerEnter={() => { clearTimeout(hideTimer.current); if (overlay && nativeMode) return; clearTimeout(showTimer.current); showTimer.current = setTimeout(() => setVisible(true), 180); }} onPointerLeave={() => clearTimeout(showTimer.current)}>
      <button data-native-hit aria-label={visible ? '收起面板' : '展开面板'} className="dock-handle" onClick={() => { clearTimeout(showTimer.current); clearTimeout(hideTimer.current); close(); setVisible(!visible); }}/>
    </div>
    {present && <div ref={wrap} className={`dock-wrap ${visible ? '' : 'dock-closing'}`} onPointerEnter={() => { setPointerInside(true); clearTimeout(hideTimer.current); }} onPointerMove={event => { if (!(overlay && nativeMode) && !visible && (event.movementX || event.movementY)) setVisible(true); }} onPointerLeave={() => { setPointerInside(false); if (!(overlay && nativeMode) && p.autoHide && !press.current && !dragging && !importing && !runningTest && !directoryBusy && !contextOpen && !context && !keyboard && !editing) hideTimer.current = setTimeout(() => setVisible(false), 650); }}>
      <nav ref={dock} aria-label="快捷启动面板" data-native-hit className={`dock glass material-${p.material} ${editing ? 'dock-editing' : ''}`} style={{ width: layout.width, height: layout.height, borderRadius: p.radius, transformOrigin: 'center top', '--dock-icon': `${layout.icon}px`, '--dock-font': `${layout.font}px` } as CSSProperties}>
        <div className={`dock-launchers ${overflow ? 'has-overflow' : ''}`} {...pan} onWheel={event => { if (event.currentTarget.scrollWidth > event.currentTarget.clientWidth) event.currentTarget.scrollLeft += event.deltaY || event.deltaX; }}
          onDragOver={event => { if (editing && event.dataTransfer.types.includes('application/x-luma-entry')) { event.preventDefault(); event.stopPropagation(); } }}
          onDrop={event => { if (!editing || !event.dataTransfer.types.includes('application/x-luma-entry')) return; event.preventDefault(); event.stopPropagation(); try { const { projectId, itemId } = JSON.parse(event.dataTransfer.getData('application/x-luma-entry')); onMoveItem?.(projectId, itemId, (event.target as HTMLElement).closest<HTMLElement>('[data-drop-project]')?.dataset.dropProject); } catch { setMenuError('无法读取快捷引用，请重新拖入。'); } }}>
        {shown.map(project => <div className={`dock-slot ${groupSource === project.id ? 'group-drag-source' : ''} ${groupTarget === project.id ? 'group-drop-target drop-' + groupIntent : ''}`} data-drop-project={project.id} key={project.id} style={{ width: layout.cell }} onContextMenu={event => { if (!(event.target as HTMLElement).closest('.split-folder-actions')) return; event.preventDefault(); clearPress(); setContext({ x: event.clientX, y: event.clientY, actions: [{ label: '打开', action: () => open(project, project.items[0]) }, { label: '进入子菜单', action: () => showStack(project.id) }, { label: '复制地址', action: () => { void navigator.clipboard.writeText(project.items[0].path); } }, { label: '移除快捷项', action: () => onRemove?.(project.id), danger: true }] }); }}>
          <SplitFolderTile enabled={!editing && (project.items.length > 1 || ['folder','app'].includes(project.items[0].kind))} software={project.items.length === 1 && project.items[0].kind === 'app'} className="dock-split" name={project.name} projectId={project.id} entryId={project.items[0].id} onOpen={() => open(project, project.items[0])} onEnter={() => showStack(project.id)} gesture={{ onPointerDown: event => begin(event, project, 'entry'), onPointerMove: move, onPointerUp: end, onPointerCancel: clearPress }}>
          <button className={`dock-project ${stack === project.id ? 'active' : ''}`} title={`${project.name} · 单击主目录，长按展开`} aria-label={`打开 ${project.name} 主目录，长按展开堆叠`} onPointerDown={event => begin(event, project)} onPointerMove={move} onPointerUp={end} onPointerCancel={() => { clearPress(); clearGroupDrag(); }} onLostPointerCapture={() => { clearPress(); clearGroupDrag(); }} onContextMenu={event => { event.preventDefault(); clearPress(); setContext({ x: event.clientX, y: event.clientY, actions: [ { label: '打开', action: () => open(project, project.items[0]) }, { label: '进入子菜单', action: () => showStack(project.id) }, { label: '复制地址', action: () => { void navigator.clipboard.writeText(project.items[0].path).catch(() => setMenuError('复制地址失败')); } }, { label: '整理快捷项', action: () => setEditing(true) }, { label: '移除快捷项', action: () => onRemove?.(project.id), danger: true } ] }); }} onClick={event => { if (event.detail === 0) { if (editing) showStack(project.id); else open(project, project.items[0]); } }} onKeyDown={event => { if (event.key === 'ArrowDown') { event.preventDefault(); showStack(project.id); } }}>
            <GroupIcon projectId={project.id} items={project.items} color={project.color} size={layout.icon}/>
            <span className="dock-label">{project.name}</span>
          </button>
          </SplitFolderTile>
          {editing && <button className="shortcut-remove" aria-label={`移除快捷项 ${project.name}`} title="仅移除快捷引用" onClick={() => onRemove?.(project.id)}><X size={12}/></button>}
          <button className="stack-chevron" aria-label={`展开 ${project.name} 堆叠`} onClick={() => { clearTimeout(hideTimer.current); showStack(stack === project.id ? null : project.id); }}><ChevronDown size={11}/></button>
        </div>)}
        {!pinned.length && <span className="dock-empty">拖入文件夹、软件或文件</span>}
        </div>
        <div className="dock-tools" role="group" aria-label="面板工具">
        <button className="dock-action" aria-label="搜索项目" onClick={onSearch}><Search size={19}/></button>
        <button className="dock-action" aria-label={editing ? '完成整理' : '整理图标'} aria-pressed={editing} onClick={() => { close(); setEditing(!editing); }}>{editing ? <Check size={19}/> : <Pencil size={18}/>}</button>
        <button className="dock-action" aria-label="面板设置" onClick={onSettings}><Settings2 size={19}/></button>
        </div>
      </nav>
      {editing && <div data-native-hit className="dock-edit-hint">把图标拖到另一个图标上成组 · 完成整理后恢复快捷启动</div>}
      {dragging && <div data-native-hit className="dock-drop-hint">松手添加快捷项 · 拖到已有图标可加入堆叠</div>}
      {active && <section aria-label={`${active.name} 文件夹堆叠`} data-native-hit className={`stack-panel glass material-${p.material}`} style={{ '--menu-available-height': `calc(100vh - ${layout.height + 220}px)`, maxHeight: `calc(100vh - ${layout.height + 40}px)` } as CSSProperties}>
        <header><div className="stack-heading"><span className={`project-dot dot-${active.color}`}/><strong>{active.name}</strong><span>{active.items.length} 个入口</span></div><button className="icon-button" aria-label="关闭堆叠" onClick={close}><X size={16}/></button></header>
        {menuError && <p className="folder-browser-message" role="alert">{menuError}</p>}
        <div className="stack-body-cascade">
        {(active.items.length > 1 || !browseItem) && <GroupEntryMenu project={active} editing={editing} onRemove={itemId => onRemove?.(active.id, itemId)} onExtract={itemId => onMoveItem?.(active.id, itemId)} onMoveItem={onMoveItem} onUngroup={() => { onUngroup?.(active.id); close(); }} highlight={highlight} onOpen={item => open(active, item)} onBrowse={item => { menuGeneration.current++; setBrowseItemId(item.id); }} onPointerDown={event => begin(event, active, 'entry')} onPointerMove={move} onPointerUp={end} onPointerCancel={clearPress}/>}
        {browseItem?.kind === 'app' && <RecentProjects leadingColumns={active.items.length > 1 ? 1 : 0} projectId={active.id} item={browseItem} limit={p.recentLimit ?? 8} onOpen={entryId => void openRecent(active.id, browseItem.id, entryId)} onPointerDown={event => begin(event, active, 'entry')} onPointerMove={move} onPointerUp={end} onPointerCancel={clearPress}/>}
        {browseItem?.kind === 'folder' && <FolderBrowser key={`${active.id}/${browseItem.id}`} projectId={active.id} item={browseItem} color={active.color} highlight={highlight} leadingColumns={active.items.length > 1 ? 1 : 0}
          onNavigate={() => { menuGeneration.current++; setMenuError(''); }}
          onRunTest={taskId => void runProjectTest(active.id, browseItem.id, taskId)} runningTest={runningTest}
          onOpen={entryId => void openDirectory(active.id, browseItem.id, entryId)} onBackToGroup={active.items.length > 1 ? () => setBrowseItemId(null) : undefined}
          onPointerDown={event => begin(event, active, 'entry')} onPointerMove={move} onPointerUp={end} onPointerCancel={clearPress}/>}
        </div>
      </section>}
      {more && <section className={`more-panel glass material-${p.material}`} data-native-hit aria-label="更多项目列表">{pinned.slice(shown.length).map(project => <button key={project.id} data-drop-project={project.id}
        className={groupTarget === project.id ? 'group-drop-target drop-' + groupIntent : ''}
        onPointerDown={event => { if (editing) begin(event, project); }} onPointerMove={move} onPointerUp={end} onPointerCancel={clearGroupDrag} onLostPointerCapture={clearGroupDrag}
        onClick={event => { if (!editing || event.detail === 0) showStack(project.id); }}><GroupIcon projectId={project.id} items={project.items} color={project.color} size={30}/>{project.name}<ChevronDown size={14}/></button>)}</section>}
    </div>}
    {context && <ContextMenu {...context} onClose={() => setContext(null)}/>}
  </div>;
}
