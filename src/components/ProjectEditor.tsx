import { hasUrlText, parseUrls } from '../core/urls';
import { Fragment, useEffect, useRef, useState } from 'react';
import { Plus, FolderOpen, Trash2, Check } from 'lucide-react';
import { ProjectSchema, type Project, type Color, type LaunchItem } from '../contracts';
import { nativeMode, request } from '../bridge';
import { Modal } from './Modal';
import { CrystalFolder } from './CrystalFolder';
import { appendDraftShortcuts } from '../core/draftShortcuts';
import { hasExternalFiles } from '../core/settingsDrop';
import { LaunchCommandEditor } from './LaunchCommandEditor';
export function ProjectEditor({ project, onSave, onClose }: { project?: Project; onSave: (p: Project) => void; onClose: () => void }) {
  const [draft, commitDraft] = useState<Project>(() => project ? structuredClone(project) : { id: crypto.randomUUID(), name: '', description: '', color: 'mint', pinned: true, items: [{ id: crypto.randomUUID(), name: '主目录', path: '', kind: 'folder' }] });
  const currentDraft = useRef(draft), alive = useRef(true), busy = useRef(false);
  const placeholderId = useRef(project ? undefined : draft.items[0].id);
  const setDraft = (next: Project | ((p: Project) => Project)) => { const value = typeof next === 'function' ? next(currentDraft.current) : next; currentDraft.current = value; commitDraft(value); };
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  const close = () => { alive.current = false; onClose(); };
  const [error, setError] = useState(''), [picking, setPicking] = useState(false), [dropActive, setDropActive] = useState(false), [notice, setNotice] = useState('');
  const runImport = async (action: () => Promise<void>) => {
    if (busy.current) { setError('正在添加上一批入口，请稍后重试本次操作。'); return; }
    busy.current = true; setPicking(true); setError(''); setNotice('');
    try { await action(); } catch (e) { if (alive.current) setError((e as Error).message); }
    finally { busy.current = false; if (alive.current) setPicking(false); }
  };
  const updateItem = (index: number, key: 'name' | 'path', value: string) => setDraft(p => ({ ...p, items: p.items.map((item, i) => i === index ? { ...item, [key]: value } : item) }));
  const assertUnchanged = (item: LaunchItem) => { if (JSON.stringify(currentDraft.current.items.find(entry => entry.id === item.id)) !== JSON.stringify(item)) throw new Error('等待选择期间，此入口已编辑或移除。本次未替换，请重新选择。'); };
  const pick = (index: number) => { const selected = currentDraft.current.items[index]; return runImport(async () => {
    const folder = await request('shell.pickFolder', {}); if (!alive.current || !folder) return;
    assertUnchanged(selected);
    setDraft(p => ({ ...p, name: p.name || folder.name.slice(0, 60), items: p.items.map(item => item.id === selected.id ? { ...item, ...folder, kind: 'folder' } : item) }));
  }); };
  const pickFiles = (index: number) => { const selected = currentDraft.current.items[index]; return runImport(async () => {
    const items = await request('shell.pickFiles', {}); if (!alive.current || !items.length) return;
    assertUnchanged(selected);
    const current = currentDraft.current;
    if (current.items.length - 1 + items.length > 200) throw new Error('每个堆叠最多 200 个入口，本次未添加，请减少选择数量。');
    setDraft({ ...current, name: current.name || items[0].name.slice(0, 60), items: current.items.flatMap(item => item.id === selected.id ? items.map((picked, n) => ({ ...picked, id: n === 0 ? item.id : crypto.randomUUID() })) : [item]) });
  }); };
  const appendFiles = (files: File[]) => runImport(async () => {
    if (!nativeMode) throw new Error('请在 Luma 桌面程序中拖入文件夹、软件和文件，浏览器预览无法读取真实路径。');
    if (!files.length) throw new Error('未收到文件对象，请从资源管理器重新拖入。');
    if (files.length > 100) throw new Error('每次最多拖入 100 个入口，本次未添加；可分批添加至 200 个入口。');
    const items = await request('shell.resolveDrop', {}, files); if (!alive.current) return;
    const result = appendDraftShortcuts(currentDraft.current, items, placeholderId.current);
    setDraft(result.project); setNotice(`已加入 ${result.added} 个入口${result.skipped ? `，已跳过 ${result.skipped} 个重复入口` : ''}；保存项目后生效。`);
  });
  return <Modal title={project ? '编辑项目堆叠' : '把新项目放到手边'} onClose={close} wide>
    <form className={dropActive ? 'editor-drop-active' : ''} onDragOver={event => { if (!hasExternalFiles(event.dataTransfer) && !hasUrlText(event.dataTransfer)) return; event.preventDefault(); event.stopPropagation(); event.dataTransfer.dropEffect = 'copy'; setDropActive(true); }} onDragLeave={event => { if (!(event.relatedTarget instanceof Node) || !event.currentTarget.contains(event.relatedTarget)) setDropActive(false); }} onDrop={event => { if (!hasExternalFiles(event.dataTransfer) && !hasUrlText(event.dataTransfer)) return; event.preventDefault(); event.stopPropagation(); setDropActive(false); if (hasExternalFiles(event.dataTransfer)) void appendFiles(Array.from(event.dataTransfer.files)); else { const text = event.dataTransfer.getData('text/uri-list') || event.dataTransfer.getData('text/plain'); void runImport(async () => { const entries = []; for (const url of parseUrls(text)) { const m = await request('website.inspect', { url }); entries.push({ kind: 'url' as const, name: m.title, path: m.url, ...(m.dataUrl ? { websiteIcon: m.dataUrl } : {}) }); } if (!alive.current) return; const result = appendDraftShortcuts(currentDraft.current, entries, placeholderId.current); setDraft(result.project); }); } }} onSubmit={event => { event.preventDefault(); if (busy.current) { setError('正在解析入口，请完成后再保存。'); return; } const latest = currentDraft.current; const candidate = { ...latest, name: latest.name.trim(), items: latest.items.map(item => ({ ...item, name: item.name.trim(), path: item.path.trim() })) }; const parsed = ProjectSchema.safeParse(candidate); if (!parsed.success) { setError(parsed.error.issues.find(issue => issue.path.includes('launch'))?.message ?? '请填写项目名称，以及每个入口的名称和路径。'); return; } alive.current = false; onSave(parsed.data); }}>
      <div className="editor-top"><CrystalFolder color={draft.color} size={60} stack/><div className="color-options">{(['mint', 'blue', 'violet', 'peach', 'gold'] as Color[]).map(color => <button key={color} type="button" aria-label={`选择 ${color} 色`} className={`color-choice dot-${color}`} onClick={() => setDraft({ ...draft, color })}>{draft.color === color && <Check size={15}/>}</button>)}</div></div>
      <label className="form-label">项目名称<input autoFocus required maxLength={60} placeholder="例如：品牌设计" value={draft.name} onChange={e => setDraft({ ...draft, name: e.target.value })}/></label>
      <label className="form-label">一句描述 <small>可选</small><input maxLength={160} placeholder="给这个项目留一句话" value={draft.description} onChange={e => setDraft({ ...draft, description: e.target.value })}/></label>
      <div className="editor-folder-heading"><strong>快捷入口</strong><span>一个入口独立显示，多个入口自动堆叠</span></div>
      <div className="editor-drop-guide"><strong>{picking ? '正在读取入口…' : '把文件夹、软件或文件拖到这里，批量加入堆叠'}</strong><p>项目是逻辑分组，可绑定 5 个目录或混合入口，最多 200 个；只保存快捷入口，不移动文件。第一个为单击默认入口，长按可选其他入口。</p></div>
      <div className="editor-folders">{draft.items.map((item, index) => <Fragment key={item.id}><div className="folder-form"><div><input aria-label={`入口 ${index + 1} 名称`} required maxLength={120} placeholder="文件夹、软件或文件名称" value={item.name} onChange={e => updateItem(index, 'name', e.target.value)}/><input aria-label={`入口 ${index + 1} 路径`} required maxLength={4096} placeholder="D:\Projects\..." value={item.path} onChange={e => updateItem(index, 'path', e.target.value)}/></div><select aria-label={`入口 ${index + 1} 类型`} value={item.kind} onChange={e => setDraft(p => ({ ...p, items: p.items.map((entry, i) => i === index ? { ...entry, kind: e.target.value as LaunchItem['kind'], ...(e.target.value !== 'folder' ? { launch: undefined } : {}), ...(e.target.value !== 'url' ? { websiteIcon: undefined } : {}) } : entry) }))}><option value="folder">文件夹</option><option value="app">软件</option><option value="file">文件</option><option value="url">网址</option></select><button title={nativeMode ? '选择入口' : '浏览器演示请填写路径'} aria-label={`选择入口 ${index + 1} ${item.kind === 'folder' ? '文件夹' : '文件或软件'}`} type="button" className="icon-button" disabled={picking || (!nativeMode && item.kind !== 'url')} onClick={() => item.kind === 'url' ? void runImport(async () => { const urls = parseUrls(item.path); const metadata = await request('website.inspect', { url: urls[0] }); assertUnchanged(item); setDraft(p => ({ ...p, items: p.items.map(entry => entry.id === item.id ? { ...entry, name: metadata.title, websiteIcon: metadata.dataUrl ?? undefined } : entry) })); }) : item.kind === 'folder' ? pick(index) : pickFiles(index)}><FolderOpen size={17}/></button>{index > 0 && <button type="button" className="icon-button danger" aria-label={`移除入口 ${index + 1}`} onClick={() => setDraft({ ...draft, items: draft.items.filter((_, i) => i !== index) })}><Trash2 size={16}/></button>}</div>{item.kind === 'url' && <label className="form-label">网站备注<input aria-label={`入口 ${index + 1} 备注`} maxLength={240} value={item.note ?? ''} onChange={event => setDraft(p => ({ ...p, items: p.items.map(entry => entry.id === item.id ? { ...entry, note: event.target.value } : entry) }))}/></label>}<LaunchCommandEditor item={item} index={index} onChange={launch => setDraft(p => ({ ...p, items: p.items.map(entry => entry.id === item.id ? { ...entry, launch } : entry) }))}/></Fragment>)}</div>
      <button type="button" className="text-button" disabled={draft.items.length >= 200} onClick={() => setDraft({ ...draft, items: [...draft.items, { id: crypto.randomUUID(), name: '', path: '', kind: 'folder' }] })}><Plus size={15}/>添加快捷入口</button>
      {!nativeMode && <p className="form-hint">浏览器预览只保存入口，不会打开或移动你的真实文件。</p>}
      {notice && <p className="form-hint" role="status">{notice}</p>}
      {error && <p className="form-error" role="alert">{error}</p>}
      <footer className="modal-actions"><button type="button" className="secondary-button" onClick={close}>取消</button><button type="submit" className="primary-button" disabled={picking}>{project ? '保存项目' : '创建项目'}<Check size={15}/></button></footer>
    </form>
  </Modal>;
}
