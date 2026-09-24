import { useRef, useState, type DragEvent } from 'react';
import { request } from '../bridge';
import type { Project } from '../contracts';
import { FILE_ENTRY_MIME, SAVED_ENTRY_MIME, readTransferSource, transferLabel, transferOperation, type InternalSource, type TransferOperation } from '../core/fileTransfer';
import './file-transfer.css';

type Target = { projectId: string; itemId: string; folderId?: string; name: string };
type Pending = { files?: File[]; source?: InternalSource; operation: TransferOperation; targets: Target[]; target: string; names: string };

export function useFileTransfer(projects: Project[], cancelGesture: () => void) {
  const [pending, setPending] = useState<Pending | null>(null), [running, setRunning] = useState(false), [notice, setNotice] = useState('');
  const active = useRef(false);
  const collect = (element: Element | null): Target[] => {
    const hit = element?.closest<HTMLElement>('[data-transfer-project]');
    if (hit) {
      const { transferProject: projectId, transferItem: itemId, transferFolder: folderId, transferName: name } = hit.dataset;
      if (projectId && itemId) return [{ projectId, itemId, folderId, name: name ?? '目标目录' }];
    }
    const projectId = element?.closest<HTMLElement>('[data-drop-project]')?.dataset.dropProject;
    const chosen = projectId ? projects.filter(project => project.id === projectId) : projects;
    return chosen.flatMap(project => project.items.filter(item => item.kind === 'folder').map(item => ({ projectId: project.id, itemId: item.id, name: `${project.name} · ${item.name}` })));
  };
  const queue = (files: File[] | undefined, source: InternalSource | undefined, target: Element | null, keys: { altKey: boolean; shiftKey: boolean }) => {
    if (active.current || pending) return;
    cancelGesture(); setNotice('');
    if (source) {
      const entry = target?.closest('.directory-row')?.querySelector<HTMLElement>('[data-item-id]') ?? target?.closest<HTMLElement>('[data-item-id]');
      if (entry?.dataset.projectId === source.projectId && entry.dataset.itemId === (source.entryId ?? source.itemId)) return;
      if (!source.entryId && target?.closest<HTMLElement>('.dock-slot')?.dataset.dropProject === source.projectId) return;
    }
    const targets = collect(target);
    if (!targets.length) { setNotice('此处没有目标文件夹。请拖到文件夹，或在整理模式中添加快捷入口。'); return; }
    const saved = source && projects.find(project => project.id === source.projectId)?.items.find(item => item.id === source.itemId);
    if (!files?.length && !saved) { setNotice('拖动来源已失效，请重新拖入。'); return; }
    setPending({ files, source, targets, target: targets.length === 1 ? '0' : '', operation: transferOperation(keys), names: files?.map(file => file.name).join('、') ?? source?.name ?? saved?.name ?? '目录项' });
  };
  const submit = async () => {
    if (!pending || pending.target === '' || active.current) return;
    const target = pending.targets[Number(pending.target)]; if (!target) return;
    active.current = true; setRunning(true); setNotice('');
    try {
      const folderId = target.folderId ?? (await request('folder.list', { projectId: target.projectId, itemId: target.itemId })).folderId;
      const result = await request('folder.transfer', { operation: pending.operation, targetProject: target.projectId, targetItem: target.itemId, targetFolderId: folderId,
        ...(pending.source ? { sourceProject: pending.source.projectId, sourceItem: pending.source.itemId, ...(pending.source.entryId ? { sourceEntryId: pending.source.entryId } : {}) } : {}) }, pending.files);
      setNotice(result.completed ? `已${transferLabel[pending.operation]} ${result.changedCount} 项` : `已处理 ${result.changedCount} 项；${result.message ?? '其余未完成，请检查目录。'}`);
      setPending(null);
      if (result.changedCount > 0) window.dispatchEvent(new Event('luma:files-transferred'));
    } catch (error) { setNotice((error as Error).message); }
    finally { active.current = false; setRunning(false); }
  };
  return {
    busy: !!pending || running,
    queue,
    accepts: (types: readonly string[]) => types.some(type => ['Files', SAVED_ENTRY_MIME, FILE_ENTRY_MIME].includes(type)),
    drop: (event: DragEvent) => {
      const files = Array.from(event.dataTransfer.files);
      const real = event.dataTransfer.types.includes(FILE_ENTRY_MIME);
      const source = readTransferSource(event.dataTransfer.getData(real ? FILE_ENTRY_MIME : SAVED_ENTRY_MIME), real);
      if (!files.length && !source) { setNotice('无法读取拖动来源，请重新拖入。'); return; }
      queue(files.length ? files : undefined, source ?? undefined, event.target as Element, event);
    },
    ui: (pending || notice) && <aside data-native-hit className="file-transfer-panel glass" onKeyDown={event => { if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); if (!active.current) { setPending(null); setNotice(''); } } }}>
      {pending && <div role="dialog" aria-modal="true" aria-label="确认文件操作" className="directory-actual-form">
        <strong>{transferLabel[pending.operation]}{pending.files ? ` ${pending.files.length} 项` : ''}</strong>
        <p className="transfer-source" title={pending.names}>{pending.names}</p>
        <label>目标文件夹<select aria-label="目标文件夹" value={pending.target} disabled={running} onChange={event => setPending({ ...pending, target: event.target.value })}>
          <option value="" disabled>请选择目标文件夹</option>{pending.targets.map((target, index) => <option key={`${target.projectId}/${target.itemId}/${target.folderId}`} value={index}>{target.name}</option>)}
        </select></label>
        <p>{pending.operation === 'move' ? '文件将从原位置移至目标文件夹。' : pending.operation === 'link' ? '在目标文件夹创建 .lnk，原文件保留。' : '将文件复制到目标文件夹，原文件保留。'}</p>
        <div className="directory-form-buttons"><button className="text-button" disabled={running} onClick={() => setPending(null)}>取消</button><button className="directory-confirm" disabled={running || pending.target === ''} onClick={() => void submit()}>{running ? '正在处理…' : `确认${transferLabel[pending.operation]}`}</button></div>
      </div>}
      {notice && <p role="status" className="directory-operation-notice">{notice}{!pending && <button className="text-button" onClick={() => setNotice('')}>关闭</button>}</p>}
    </aside>,
  };
}
