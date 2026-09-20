import { StateSchema, type AppState, type ImportedShortcut } from '../contracts';

export function addShortcuts(state: AppState, items: ImportedShortcut[], projectId?: string): AppState {
  const target = projectId ? state.projects.find(p => p.id === projectId) : undefined;
  if (projectId && !target) throw new Error('目标堆叠已删除，请重新拖入。');
  const pathKey = (path: string) => path.replace(/\//g, '\\').replace(/[\\]+$/, '').toLowerCase();
  const existing = new Set((target ? target.items : state.projects.flatMap(p => p.items)).map(i => pathKey(i.path)));
  const unique = items.filter(item => { const key = pathKey(item.path); if (existing.has(key)) return false; existing.add(key); return true; });
  if (!unique.length) throw new Error('这些快捷项已经存在。');
  if (target && target.items.length + unique.length > 200) throw new Error('每个堆叠最多 200 个入口。');
  if (!target && state.projects.length + unique.length > 100) throw new Error('最多 100 个快捷项或堆叠，可拖到已有图标中合并。');
  const entries = unique.map(item => ({ ...item, id: crypto.randomUUID() }));
  return StateSchema.parse({ ...state, projects: target ? state.projects.map(p => p.id === target.id ? { ...p, items: [...p.items, ...entries] } : p) : [...state.projects, ...entries.map(item => ({ id: crypto.randomUUID(), name: item.name.slice(0, 60), description: '', color: 'mint', pinned: true, items: [item] }))] });
}
