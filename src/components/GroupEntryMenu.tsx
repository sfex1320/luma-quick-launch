import type { PointerEvent } from 'react';
import { FolderOpen } from 'lucide-react';
import type { LaunchItem, Project } from '../contracts';
import { ItemIcon } from './ItemIcon';
import { SplitFolderTile } from './SplitFolderTile';

export function GroupEntryMenu({ project, highlight, onOpen, onBrowse, ...gesture }: { project: Project; highlight: string | null; onOpen: (item: LaunchItem) => void; onBrowse: (item: LaunchItem) => void; onPointerDown: (event: PointerEvent<HTMLButtonElement>) => void; onPointerMove: (event: PointerEvent) => void; onPointerUp: (event: PointerEvent) => void; onPointerCancel: () => void }) {
  return <div className="folder-column group-column" aria-label={`${project.name} 组内入口`}>
    <div className="stack-items launch-grid">
      {project.items.map(item => <div className="group-entry-row" key={item.id}>
        <SplitFolderTile enabled={item.kind === 'folder'} name={item.name} projectId={project.id} entryId={item.id} onOpen={() => onOpen(item)} onEnter={() => onBrowse(item)} gesture={gesture}>
          <button data-item-id={item.id} data-project-id={project.id} className={`stack-item ${highlight === item.id ? 'selected' : ''}`} title={item.path} {...gesture} onLostPointerCapture={gesture.onPointerCancel} onClick={event => { if (event.detail === 0) onOpen(item); }}>
            <ItemIcon projectId={project.id} itemId={item.id} pathKey={item.path} kind={item.kind} color={project.color} size={38}/><span><strong>{item.name}</strong><small>{item.kind === 'folder' ? '文件夹' : item.kind === 'app' ? '软件' : '文件'}</small></span>
          </button>
        </SplitFolderTile>
      </div>)}
    </div>
    <footer><button data-item-id={project.items[0].id} data-project-id={project.id} className="text-button directory-open-current" {...gesture} onLostPointerCapture={gesture.onPointerCancel} onClick={event => { if (event.detail === 0) onOpen(project.items[0]); }}><FolderOpen size={14}/>打开默认入口</button><span>按住滑向文件夹，继续展开</span></footer>
  </div>;
}
