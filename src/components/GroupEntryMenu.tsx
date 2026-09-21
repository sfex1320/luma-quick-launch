import { useState, type ReactNode, type PointerEvent } from 'react';
import { FolderOpen, X, ArrowUpRight } from 'lucide-react';
import type { LaunchItem, Project } from '../contracts';
import { ItemIcon } from './ItemIcon';
import { SplitFolderTile } from './SplitFolderTile';
import { useBlankPan } from './useBlankPan';
import { ContextMenu, type MenuAction } from './ContextMenu';

export function GroupEntryMenu({ project, highlight, onOpen, onBrowse, editing, onRemove, onExtract, onMoveItem, onUngroup, renderAccessory, ...gesture }: { renderAccessory?: (item: LaunchItem) => ReactNode; project: Project; highlight: string | null; onOpen: (item: LaunchItem) => void; onBrowse: (item: LaunchItem) => void; editing?: boolean; onRemove?: (itemId: string) => void; onExtract?: (itemId: string) => void; onMoveItem?: (sourceId: string, itemId: string, targetId?: string) => void; onUngroup?: () => void; onPointerDown: (event: PointerEvent<HTMLButtonElement>) => void; onPointerMove: (event: PointerEvent) => void; onPointerUp: (event: PointerEvent) => void; onPointerCancel: () => void }) {
  const pan = useBlankPan();
  const [context, setContext] = useState<{ x: number; y: number; actions: MenuAction[] } | null>(null);
  return <div className="folder-column group-column" data-drop-project={project.id} aria-label={`${project.name} 组内入口`} onDragOver={event => { if (editing && event.dataTransfer.types.includes('application/x-luma-entry')) { event.preventDefault(); event.stopPropagation(); } }} onDrop={event => { if (!editing || !event.dataTransfer.types.includes('application/x-luma-entry')) return; event.preventDefault(); event.stopPropagation(); try { const data = JSON.parse(event.dataTransfer.getData('application/x-luma-entry')); onMoveItem?.(data.projectId, data.itemId, project.id); } catch { /* Ignore malformed external drag payloads. */ } }}>
    <div className="stack-items launch-grid" {...pan}>
      {project.items.map(item => <div className="group-entry-row" key={item.id} onContextMenu={event => { event.preventDefault(); gesture.onPointerCancel(); setContext({ x: event.clientX, y: event.clientY, actions: [
        { label: '打开', action: () => onOpen(item) },
        ...(['folder','app'].includes(item.kind) ? [{ label: item.kind === 'app' ? '最近项目' : '进入子菜单', action: () => onBrowse(item) }] : []),
        { label: '复制地址', action: () => { void navigator.clipboard.writeText(item.path); } },
        { label: '移到主面板', action: () => onExtract?.(item.id), disabled: project.items.length < 2 },
        { label: '移除快捷项', action: () => onRemove?.(item.id), danger: true },
      ] }); }}>
        <SplitFolderTile enabled={!editing && ['folder','app'].includes(item.kind)} software={item.kind === 'app'} name={item.name} projectId={project.id} entryId={item.id} onOpen={() => onOpen(item)} onEnter={() => onBrowse(item)} gesture={gesture}>
          <button data-item-id={item.id} data-project-id={project.id} className={`stack-item ${highlight === item.id ? 'selected' : ''}`} title={item.path} {...(!editing ? gesture : {})} draggable={editing} onDragStart={event => { event.dataTransfer.effectAllowed = 'move'; event.dataTransfer.setData('application/x-luma-entry', JSON.stringify({ projectId: project.id, itemId: item.id })); }} onLostPointerCapture={gesture.onPointerCancel} onClick={event => { if (!editing && event.detail === 0) onOpen(item); }}>
            <ItemIcon projectId={project.id} itemId={item.id} pathKey={item.path} kind={item.kind} color={project.color} size={38}/><span><strong>{item.name}</strong><small>{item.kind === 'folder' ? '文件夹' : item.kind === 'app' ? '软件' : item.kind === 'url' ? '网站' : '文件'}</small></span>
          </button>
        </SplitFolderTile>
        {!editing && renderAccessory?.(item)}
        {editing && <><button className="shortcut-remove" aria-label={`移除快捷项 ${item.name}`} onClick={() => onRemove?.(item.id)}><X size={12}/></button><button className="shortcut-extract" aria-label={`将 ${item.name} 移到主面板`} onClick={() => onExtract?.(item.id)}><ArrowUpRight size={13}/></button></>}
      </div>)}
    </div>
    <footer><button data-item-id={project.items[0].id} data-project-id={project.id} className="text-button directory-open-current" {...gesture} onLostPointerCapture={gesture.onPointerCancel} onClick={event => { if (event.detail === 0) onOpen(project.items[0]); }}><FolderOpen size={14}/>打开默认入口</button>{project.items.length > 1 && <button className="text-button directory-open-current" onClick={onUngroup}>拆成独立图标</button>}</footer>
    {context && <ContextMenu {...context} onClose={() => setContext(null)}/>}
  </div>;
}
