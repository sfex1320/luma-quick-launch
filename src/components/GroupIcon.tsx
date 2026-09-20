import type { Color, LaunchItem } from '../contracts';
import { ItemIcon } from './ItemIcon';
import './group-icon.css';

export function GroupIcon({ items, color = 'mint', size = 40, projectId }: { items: LaunchItem[]; color?: Color; size?: number; projectId?: string }) {
  if (items.length < 2) return <ItemIcon kind={items[0]?.kind ?? 'folder'} color={color} size={size} projectId={projectId} itemId={items[0]?.id} pathKey={items[0]?.path}/>;
  return <span aria-hidden="true" className={`group-icon dot-${color}`} style={{ width: size, height: size, borderRadius: size * .26 }}>
    <span className="group-icon-grid">{items.slice(0, 4).map(item => <ItemIcon key={item.id} kind={item.kind} color={color} size={size * .36} projectId={projectId} itemId={item.id} pathKey={item.path}/>)}</span>
    <span className="group-icon-count" style={{ fontSize: Math.max(9, size * .2) }}>{items.length}</span>
  </span>;
}
