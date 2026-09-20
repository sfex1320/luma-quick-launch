import { useEffect, useRef, useState, type DragEvent } from 'react';
import { LayoutDashboard, Layers3, SlidersHorizontal, Cpu, Search, Plus, ArrowUpRight, ArrowRight, Settings2, Pin, PinOff, Pencil, Trash2, Check, ExternalLink, Monitor, MousePointer2, Download, Upload, ChevronRight, MoveLeft, MoveRight, Command, X, FolderOpen } from 'lucide-react';
import type { AppState, ImportedShortcut, LaunchItem, Project } from './contracts';
import { StateSchema } from './contracts';
import { nativeMode, request, resetDemoStorage } from './bridge';
import { useWorkspace } from './useWorkspace';
import { CrystalFolder } from './components/CrystalFolder';
import { Dock } from './components/Dock';
import { Appearance } from './components/Appearance';
import { Modal } from './components/Modal';
import { ProjectEditor } from './components/ProjectEditor';
import { addShortcuts } from './core/shortcuts';
import { GroupIcon } from './components/GroupIcon';
import { mergeProjects, ungroupProject } from './core/groups';
import { hasExternalFiles, hasProjectDrag, PROJECT_DRAG_TYPE } from './core/settingsDrop';
import './components/settings-drop.css';

type Page = 'overview' | 'projects' | 'appearance' | 'system';
const PAGES: Array<{ id: Page; icon: typeof LayoutDashboard; name: string }> = [
  { id: 'overview', icon: LayoutDashboard, name: '总览' },
  { id: 'projects', icon: Layers3, name: '项目' },
  { id: 'appearance', icon: SlidersHorizontal, name: '外观' },
  { id: 'system', icon: Cpu, name: '系统' },
];
const PAGE_TITLE: Record<Page, { title: string; sub: string }> = {
  overview: { title: '总览', sub: '项目触手可及' },
  projects: { title: '快捷项与堆叠', sub: '独立的软件、文件夹和文档，也可以组合成项目' },
  appearance: { title: '外观', sub: '面板尺寸与材质' },
  system: { title: '设置与备份', sub: '使用偏好与本机配置' },
};

export default function App() {
  const { state, update, status, error } = useWorkspace();
  const section = new URLSearchParams(location.search).get('section');
  const [page, setPage] = useState<Page>(section === 'appearance' ? 'appearance' : section === 'projects' ? 'projects' : 'overview'), [query, setQuery] = useState(''), [editor, setEditor] = useState<Project | 'new' | null>(null), [searchOpen, setSearchOpen] = useState(section === 'search'), [help, setHelp] = useState(false), [toast, setToast] = useState('');
  const toastTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined), fileInput = useRef<HTMLInputElement>(null), launchBusy = useRef(false);
  const workspace = useRef({ state, error }); workspace.current = { state, error };
  const importing = useRef(false);
  const [dropTarget, setDropTarget] = useState<string | null>(null);
  const [groupSourceId, setGroupSourceId] = useState<string | null>(null);
  const draggingProject = useRef<string | null>(null);
  const overlay = new URLSearchParams(location.search).get('view') === 'dock';
  const pendingSearch = useRef(false);
  useEffect(() => {
    if (!nativeMode || overlay) return;
    const navigate = () => {
      const target = new URLSearchParams(location.search).get('section');
      if (target === 'search') { if (editor) pendingSearch.current = true; else setSearchOpen(true); }
      else if (target === 'appearance' || target === 'projects') setPage(target);
    };
    window.addEventListener('popstate', navigate);
    if (!editor && pendingSearch.current) { pendingSearch.current = false; setSearchOpen(true); }
    return () => window.removeEventListener('popstate', navigate);
  }, [overlay, editor]);
  const notify = (message: string) => { clearTimeout(toastTimer.current); setToast(message); toastTimer.current = setTimeout(() => setToast(''), 4500); };
  useEffect(() => () => clearTimeout(toastTimer.current), []);
  useEffect(() => {
    if (overlay) return;
    // Prevent WebView's default navigation even outside an active drop zone.
    const preventFileNavigation = (event: globalThis.DragEvent) => { if (event.dataTransfer && hasExternalFiles(event.dataTransfer)) event.preventDefault(); };
    window.addEventListener('dragover', preventFileNavigation);
    window.addEventListener('drop', preventFileNavigation);
    return () => { window.removeEventListener('dragover', preventFileNavigation); window.removeEventListener('drop', preventFileNavigation); };
  }, [overlay]);
  useEffect(() => { document.documentElement.dataset.theme = state?.preferences.theme ?? 'light'; document.documentElement.dataset.motion = state?.preferences.reducedMotion ? 'reduced' : 'full'; document.body.classList.toggle('overlay-body', overlay); document.documentElement.classList.toggle('overlay-html', overlay); }, [state?.preferences.theme, state?.preferences.reducedMotion, overlay]);
  useEffect(() => { const handler = (e: KeyboardEvent) => { if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); if (nativeMode) void request('window.openSettings', { section: 'search' }).catch(error => notify(error.message)); else setSearchOpen(true); } }; window.addEventListener('keydown', handler); return () => window.removeEventListener('keydown', handler); }, [overlay]);
  const openItem = async (project: Project, item: LaunchItem) => {
    if (launchBusy.current) return;
    if (status !== '已保存') { notify('正在保存配置，请稍后再打开。'); return; }
    launchBusy.current = true;
    try { const result = await request('shell.openItem', { projectId: project.id, itemId: item.id }); if (!result.accepted) throw new Error('系统未接受启动请求'); notify(nativeMode ? `已请求打开「${item.name}」` : `演示：打开「${item.name}」 · 未访问真实文件`); }
    catch (e) { notify((e as Error).message); }
    finally { launchBusy.current = false; }
  };
  const openSettings = () => { if (overlay && nativeMode) void request('window.openSettings', { section: 'appearance' }).catch(e => notify(e.message)); else { setPage('appearance'); document.querySelector('.appearance')?.scrollIntoView({ behavior: 'smooth', block: 'nearest' }); } };
  const openSearch = () => { if (nativeMode) void request('window.openSettings', { section: 'search' }).catch(e => notify(e.message)); else setSearchOpen(true); };
  if (!state) return <div className="loading-screen"><div className="brand-mark">L</div><h2>{error ? '暂时无法载入 Luma' : '正在准备 Luma'}</h2><p>{error || '正在读取项目和外观配置…'}</p>{error && <><button className="primary-button" onClick={() => location.reload()}>重新连接</button>{!nativeMode && <button className="text-button" onClick={() => { if (confirm('清除浏览器预览配置并恢复示例项目？')) resetDemoStorage(); }}>恢复预览默认配置</button>}</>}</div>;
  const projects = state.projects.filter(p => `${p.name} ${p.description} ${p.items.map(i => i.name).join(' ')}`.toLowerCase().includes(query.toLowerCase()));
  const change = (fn: (s: AppState) => AppState) => { if (error) { notify('配置存在冲突，请重新载入后编辑。'); return; } update(fn); };
  const changeGrouping = (transform: (s: AppState) => AppState, message: string) => {
    try {
      const current = workspace.current;
      if (!current.state || current.error) throw new Error('配置存在冲突，请重新载入后编辑。');
      const next = transform(current.state);
      workspace.current = { ...current, state: next };
      update(() => next);
      setGroupSourceId(null);
      notify(message);
    } catch (e) { notify((e as Error).message); }
  };
  const groupProjects = (sourceId: string, targetId: string) => changeGrouping(s => mergeProjects(s, sourceId, targetId), '已组合图标，正在保存');
  const splitProject = (projectId: string) => changeGrouping(s => ungroupProject(s, projectId), '已拆成独立图标，正在保存');
  const exportConfig = () => { const blob = new Blob([JSON.stringify(state, null, 2)], { type: 'application/json' }); const url = URL.createObjectURL(blob); const link = document.createElement('a'); link.href = url; link.download = 'luma-workspace.json'; link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000); };
  const importConfig = async (file: File) => { try { if (file.size > 2_000_000) throw new Error('配置文件不能超过 2 MB'); const candidate = StateSchema.parse(JSON.parse(await file.text())); if (!confirm(`将替换为导入的 ${candidate.projects.length} 个项目，是否继续？`)) return; change(s => ({ ...candidate, revision: s.revision })); notify('已导入配置，正在保存'); } catch { notify('配置格式不正确，或文件超过 2 MB。原配置保持不变。'); } };
  const shift = (id: string, delta: number) => change(s => { const list = [...s.projects]; const index = list.findIndex(p => p.id === id); const target = index + delta; if (target < 0 || target >= list.length) return s; [list[index], list[target]] = [list[target], list[index]]; return { ...s, projects: list }; });
  const importShortcuts = async (load: () => Promise<ImportedShortcut[]>, projectId?: string) => {
    if (importing.current) { notify('正在添加上一批入口，请稍后重试本次拖入。'); return; }
    importing.current = true;
    try {
      const items = await load(); if (!items.length) { notify('没有可添加的入口，请拖入本机文件夹、软件或文件。'); return; }
      const current = workspace.current;
      if (!current.state || current.error) throw new Error('配置存在冲突，请重新载入后添加。');
      const next = addShortcuts(current.state, items, projectId);
      update(() => next);
      const before = current.state.projects.reduce((sum, p) => sum + p.items.length, 0);
      const added = next.projects.reduce((sum, p) => sum + p.items.length, 0) - before;
      notify(`已加入 ${added} 个快捷项${items.length > added ? `，已跳过 ${items.length - added} 个重复入口` : ''}，正在保存`);
    } catch (e) { notify((e as Error).message); }
    finally { importing.current = false; }
  };
  const dragOver = (event: DragEvent, projectId?: string) => {
    if (!hasExternalFiles(event.dataTransfer) && !(projectId && hasProjectDrag(event.dataTransfer))) return;
    event.preventDefault(); event.stopPropagation();
    event.dataTransfer.dropEffect = hasExternalFiles(event.dataTransfer) ? 'copy' : 'move';
    setDropTarget(projectId ?? 'blank');
  };
  const dropFiles = (event: DragEvent, projectId?: string) => {
    if (!hasExternalFiles(event.dataTransfer) && !hasProjectDrag(event.dataTransfer)) return;
    event.preventDefault(); event.stopPropagation(); setDropTarget(null);
    if (hasExternalFiles(event.dataTransfer)) {
      const files = Array.from(event.dataTransfer.files);
      if (!nativeMode) { notify('请在 Luma 桌面程序中拖入文件夹、软件和文件，浏览器预览无法读取真实路径。'); return; }
      if (!files.length) { notify('未收到文件对象，请从资源管理器重新拖入。'); return; }
      if (files.length > 100) { notify('每次最多拖入 100 个入口，本次未添加；每个堆叠可分批添加至 200 个入口。'); return; }
      void importShortcuts(() => request('shell.resolveDrop', {}, files), projectId);
    } else {
      const source = draggingProject.current;
      draggingProject.current = null;
      if (!source || !projectId || source === projectId || event.dataTransfer.getData(PROJECT_DRAG_TYPE) !== source) return;
      change(s => {
        const list = [...s.projects], from = list.findIndex(p => p.id === source), to = list.findIndex(p => p.id === projectId);
        if (from < 0 || to < 0) return s;
        list.splice(to, 0, ...list.splice(from, 1)); return { ...s, projects: list };
      });
    }
  };
  const dock = <Dock projects={state.projects} preferences={state.preferences} onOpen={openItem} onSettings={openSettings} onSearch={openSearch} overlay={overlay} onGroup={groupProjects} onUngroup={splitProject} onDropFiles={(files, projectId) => importShortcuts(() => request('shell.resolveDrop', {}, files), projectId)}/>;
  const entries = state.projects.reduce((sum, p) => sum + p.items.length, 0), pinned = state.projects.filter(p => p.pinned).length;
  const health = `${pinned}/${state.projects.length} 置顶`;
  return <>
    {overlay ? <main className="native-surface">{dock}</main> : <div className="cockpit">
      <div className="cp-topbar"><span className="cp-topbar-left"><i className="cp-beacon"/>Luma · {nativeMode ? '快捷启动' : '浏览器预览'}</span><span className="cp-topbar-right">Local User</span></div>
      <div className="cp-body">
        <aside className="cp-sidebar">
          <a className="cp-brand" href="#" onClick={e => { e.preventDefault(); setPage('overview'); }}><span className="cp-brand-mark">L</span><span className="cp-brand-name">LUMA</span></a>
          <div className="cp-nav-caption">常规</div>
          <nav className="cp-nav" aria-label="驾驶舱导航">{PAGES.slice(0, 3).map(tab => <button key={tab.id} className={page === tab.id ? 'active' : ''} onClick={() => setPage(tab.id)}><tab.icon size={15}/>{tab.name}{tab.id === 'projects' && <span className="cp-nav-count">{state.projects.length}</span>}{page === tab.id && <i/>}</button>)}</nav>
          <div className="cp-nav-caption">系统</div>
          <nav className="cp-nav" aria-label="系统导航"><button className={page === 'system' ? 'active' : ''} onClick={() => setPage('system')}><Settings2 size={15}/>设置与备份{page === 'system' && <i/>}</button></nav>
          <div className="cp-nav-caption">入口</div>
          <nav className="cp-nav" aria-label="快捷入口"><button aria-label="搜索全部入口" onClick={openSearch}><Search size={15}/>搜索全部<kbd>Ctrl K</kbd></button></nav>
          <div className="cp-sidebar-bottom">
            <button onClick={() => setHelp(true)}><Command size={14}/>使用指南<ArrowUpRight size={12}/></button>

            <div className="cp-version"><span className={`status-dot ${nativeMode ? 'ok' : 'demo'}`}/>{nativeMode ? '桌面模式' : '浏览器预览'}</div>
          </div>
        </aside>
        <main className={`cp-content ${dropTarget === 'blank' ? 'settings-drop-active' : ''}`} onDragOver={e => dragOver(e)} onDrop={e => dropFiles(e)} onDragLeave={e => { if (!(e.relatedTarget instanceof Node) || !e.currentTarget.contains(e.relatedTarget)) setDropTarget(null); }}>
          <header className="cp-pagehead">
            <div>
              <div className="cp-breadcrumb">Luma <ChevronRight size={12}/><strong>{PAGE_TITLE[page].title}</strong></div>
              <h1>{PAGE_TITLE[page].title}<span className="cp-title-dot"/></h1>
              <p className="cp-page-sub">{PAGE_TITLE[page].sub}</p>
            </div>
            <div className="cp-pagehead-actions">
              <span className="cp-health"><span className="status-dot ok"/>{health}</span>
              {nativeMode && <button className="cp-secondary" onClick={() => void importShortcuts(() => request('shell.pickFiles', {}))}><Plus size={15}/>软件 / 文件</button>}
              <button className="cp-primary" onClick={() => setEditor('new')}><Plus size={15}/>新建项目</button>
            </div>
          </header>
          {error && <div className="error-banner" role="alert">{error}<button onClick={() => location.reload()}>重新载入</button></div>}

          {page === 'overview' && <>
            <div className="cp-stats">
              <div className="cp-stat"><small>项目</small><strong>{state.projects.length}</strong><span>PROJECTS</span></div>
              <div className="cp-stat"><small>入口</small><strong>{entries}</strong><span>LAUNCH ITEMS</span></div>
              <div className="cp-stat"><small>置顶</small><strong>{pinned}</strong><span>PINNED</span></div>
              <div className="cp-stat"><small>配置修订</small><strong>r{state.revision}</strong><span>REVISION</span></div>
            </div>
            <section className="cp-card">
              <header className="cp-card-head"><strong><Monitor size={14}/>顶边面板</strong><span className="cp-pill"><i/>实时预览</span></header>
              <div className="desktop-scene"><div className="scene-grid"/>{dock}<div className="scene-center"><span className="scene-line"/><span>长按项目 · 滑向目标 · 松手打开</span></div></div>
              <footer className="cp-card-foot"><span><MousePointer2 size={13}/><strong>单击</strong>打开主目录 · <strong>长按</strong>展开堆叠滑选</span><button className="text-button" onClick={() => window.open(`${location.pathname}?view=dock`, '_blank', 'noopener,noreferrer')}>独立预览<ExternalLink size={12}/></button></footer>
            </section>
            <section className="cp-card">
              <header className="cp-card-head"><strong><Layers3 size={14}/>项目速览</strong><button className="text-button" onClick={() => setPage('projects')}>管理全部项目<ArrowRight size={13}/></button></header>
              <div className="cp-quick-list">{state.projects.slice(0, 5).map(project => <button key={project.id} onClick={() => setEditor(project)}><GroupIcon items={project.items} color={project.color} size={32}/><span className="cp-quick-name">{project.name}</span><span className="cp-quick-path">{project.items[0]?.path}</span><span className="cp-badge">{project.items.length} 入口</span><ChevronRight size={14}/></button>)}{!state.projects.length && <p className="empty-message">还没有项目。点右上角「新建项目」，把常用文件夹收进来。</p>}</div>
            </section>
          </>}

          {page === 'projects' && <>
            <div className="settings-drop-guide"><strong>项目是一个逻辑分组，不是磁盘上的文件夹。</strong><p>一个项目可绑定 5 个目录，也可混合软件和文件，最多 200 个入口。单击打开第一个默认入口，长按展开其他入口；实际文件不会移动。</p><p>{nativeMode ? '拖到此页空白处：添加独立快捷项；拖到已有项目卡片：加入堆叠。拖动卡片右上的排序手柄可调整顺序。' : '浏览器预览不能读取真实路径；请在 Luma 桌面程序中拖入目录、软件和文件。'}</p></div>
            <section className="cp-card">
              <header className="cp-card-head"><strong>全部项目</strong><label className="project-filter"><Search size={14}/><input aria-label="筛选项目" placeholder="查找项目…" value={query} onChange={e => setQuery(e.target.value)}/>{query && <button aria-label="清空筛选" onClick={() => setQuery('')}><X size={13}/></button>}</label></header>
              <div className="project-grid">{projects.map(project => <article className={`project-card card-${project.color} ${dropTarget === project.id ? 'settings-drop-active' : ''}`} key={project.id} onDragOver={e => dragOver(e, project.id)} onDrop={e => dropFiles(e, project.id)}>
                <div className="project-card-top"><button className="card-folder-button" aria-label={`编辑 ${project.name}`} onClick={() => setEditor(project)}><GroupIcon items={project.items} color={project.color} size={52}/></button>
                  <div className="cp-card-tools">
                    <button className="project-drag-handle" aria-label={`拖动排序 ${project.name}`} title="拖到另一张卡片排序，也可使用下方前后移动按钮" draggable onDragStart={e => { draggingProject.current = project.id; e.dataTransfer.setData(PROJECT_DRAG_TYPE, project.id); e.dataTransfer.effectAllowed = 'move'; }} onDragEnd={() => { draggingProject.current = null; setDropTarget(null); }}>⠿</button>
                    <button className={`pin-button ${project.pinned ? 'pinned' : ''}`} aria-label={`${project.pinned ? '取消置顶' : '置顶'} ${project.name}`} onClick={() => change(s => ({ ...s, projects: s.projects.map(p => p.id === project.id ? { ...p, pinned: !p.pinned } : p) }))}>{project.pinned ? <Pin size={14}/> : <PinOff size={14}/>}</button>
                    <button aria-label={`修改 ${project.name}`} onClick={() => setEditor(project)}><Pencil size={14}/></button>
                    <button aria-label={`移除 ${project.name}`} onClick={() => { if (confirm(`移除「${project.name}」的快捷入口？实际文件不会删除。`)) change(s => ({ ...s, projects: s.projects.filter(p => p.id !== project.id) })); }}><Trash2 size={14}/></button>
                  </div></div>
                <button className="project-title" onClick={() => setEditor(project)}>{project.name}<ArrowUpRight size={15}/></button>
                <p>{project.description || '为这个项目留一个专属空间。'}</p>
                <span className="cp-path"><FolderOpen size={11}/>{project.items[0]?.path}</span>
                <div className="project-group-actions"><button aria-label={`组合 ${project.name}`} disabled={state.projects.length < 2} onClick={() => setGroupSourceId(project.id)}>合并到其他图标…</button>{project.items.length > 1 && <button aria-label={`拆分 ${project.name}`} onClick={() => splitProject(project.id)}>拆成独立图标</button>}</div>
                <footer><span className="cp-badge">{project.items.length} 个入口</span><div className="cp-shift"><button aria-label={`向前移动 ${project.name}`} disabled={state.projects[0].id === project.id} onClick={() => shift(project.id, -1)}><MoveLeft size={13}/></button><button aria-label={`向后移动 ${project.name}`} disabled={state.projects.at(-1)?.id === project.id} onClick={() => shift(project.id, 1)}><MoveRight size={13}/></button></div></footer>
              </article>)}
              <button className="add-project-card" onClick={() => setEditor('new')}><span><Plus size={20}/></span><strong>新建项目</strong><small>文件夹、软件和文件，随手打开</small></button></div>
              {!projects.length && query && <p className="empty-message">没有找到「{query}」，试试其他关键词。</p>}
            </section>
          </>}

          {page === 'appearance' && <Appearance value={state.preferences} preview={dock} onChange={preferences => change(s => ({ ...s, preferences }))}/>}

          {page === 'system' && <>
            <section className="cp-card">
              <header className="cp-card-head"><strong>内核连接</strong><span className="cp-health"><span className={`status-dot ${nativeMode ? 'ok' : 'demo'}`}/>{nativeMode ? '已连接原生内核' : '浏览器预览模式'}</span></header>
              <div className="cp-kv"><span>全局搜索快捷键</span><strong>Ctrl + Alt + Space</strong></div>
              <div className="cp-kv"><span>配置存储</span><strong>{nativeMode ? '本机 %LOCALAPPDATA%\\Luma\\state.json' : '当前浏览器 localStorage'}</strong></div>
              <div className="cp-kv"><span>保存状态</span><strong>{status}{state.revision > 0 && ` · 修订 r${state.revision}`}</strong></div>
              <div className="cp-kv"><span>面板唤出</span><strong>任意屏幕顶边居中 · 驻留 180ms</strong></div>
            </section>
            <section className="cp-card">
              <header className="cp-card-head"><strong>偏好与备份</strong></header>
              <div className="settings-actions"><button className="cp-secondary" onClick={exportConfig}><Download size={15}/>导出配置</button><button className="cp-secondary" onClick={() => fileInput.current?.click()}><Upload size={15}/>导入配置</button><input hidden ref={fileInput} type="file" accept=".json,application/json" onChange={e => { const file = e.target.files?.[0]; if (file) void importConfig(file); e.target.value = ''; }}/></div>
              <div className="info-box"><Monitor size={18}/><p>{nativeMode ? '文件夹、软件和文件均可添加；拖到浮岛空白处独立置顶，拖到已有图标加入堆叠。' : '当前为前端预览。系统热区、真实文件启动和桌面背景磨砂由原生内核接入。预览不会修改你的文件。'}</p></div>
              <button className="text-button" onClick={() => { setPage('overview'); setHelp(true); }}>查看操作指南<ArrowRight size={14}/></button>
            </section>
          </>}

          <div className="cp-footer"><span><Check size={12}/>{status} · {nativeMode ? '本机配置' : '保存在此浏览器'}</span><span>LUMA · ALWAYS WITHIN REACH</span></div>
        </main>
      </div>
    </div>}
    {editor && <ProjectEditor project={editor === 'new' ? undefined : editor} onClose={() => setEditor(null)} onSave={project => { change(s => ({ ...s, projects: s.projects.some(p => p.id === project.id) ? s.projects.map(p => p.id === project.id ? project : p) : [...s.projects, project] })); setEditor(null); notify('项目已更新'); }}/>}
    {searchOpen && <Modal title="搜索项目与入口" onClose={() => { setSearchOpen(false); setQuery(''); }}><div className="search-input"><Search size={20}/><input autoFocus aria-label="搜索项目和文件夹" placeholder="项目名称、文件夹、路径…" value={query} onChange={e => setQuery(e.target.value)}/><kbd>ESC</kbd></div><div className="search-results">{state.projects.flatMap(project => project.items.filter(item => `${project.name} ${item.name} ${item.path}`.toLowerCase().includes(query.toLowerCase())).map(item => <button key={`${project.id}-${item.id}`} onClick={() => { void openItem(project, item); setSearchOpen(false); setQuery(''); }}><CrystalFolder color={project.color} size={30}/><span><strong>{item.name}</strong><small>{project.name} · {item.path}</small></span><ArrowUpRight size={16}/></button>))}{!state.projects.some(p => p.items.some(i => `${p.name} ${i.name} ${i.path}`.toLowerCase().includes(query.toLowerCase()))) && <p className="empty-message">没有匹配的入口</p>}</div></Modal>}
    {help && <Modal title="一点小手势，大一点的专注" onClose={() => setHelp(false)}><div className="help-steps"><div><MousePointer2/><span><strong>长按 · 滑动 · 松手</strong><p>在面板上长按项目约 300 毫秒，滑向其他文件夹，松手打开。滑出面板松手取消。</p></span></div><div><FolderOpen/><span><strong>单击，直达主目录</strong><p>点击项目进入第一个目录；小箭头和右键也能展开堆叠。</p></span></div><div><SlidersHorizontal/><span><strong>留出恰好的空间</strong><p>在外观页调整宽高、图标与材质。尺寸不足时自动适配，超出入口收进「更多」。</p></span></div><div><Command/><span><strong>键盘也一样顺手</strong><p>Ctrl K 搜索，Tab 移动焦点，项目上按下方向键展开，Esc 关闭。</p></span></div></div></Modal>}
    {groupSourceId && <Modal title="合并到其他图标" onClose={() => setGroupSourceId(null)}><p className="group-description">将「{state.projects.find(p => p.id === groupSourceId)?.name}」的入口加入下列目标。目标保留名称、颜色、置顶状态及默认入口；重复路径只保留一份，实际文件不会移动。</p><div className="group-targets">{state.projects.filter(p => p.id !== groupSourceId).map(project => <button key={project.id} aria-label={`合并到 ${project.name}`} onClick={() => groupProjects(groupSourceId, project.id)}><GroupIcon items={project.items} color={project.color} size={36}/><span><strong>{project.name}</strong><small>{project.items.length} 个入口{project.pinned ? ' · 已置顶' : ' · 未置顶'}</small></span></button>)}</div></Modal>}
    {toast && <div role="status" data-native-hit={overlay ? true : undefined} className="toast"><Check size={16}/>{toast}<button aria-label="关闭提示" onClick={() => setToast('')}><X size={14}/></button></div>}
  </>;
}
