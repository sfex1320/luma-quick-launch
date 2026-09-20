import { StateSchema, type AppState } from '../contracts';

import { referenceKey as pathKey } from './urls';
function uniqueId(preferred: string, used: Set<string>): string {
  let id = preferred;
  for (let n = 2; used.has(id); n += 1) id = `${preferred}-${n}`;
  used.add(id);
  return id;
}

/** Only shortcut references change. Target order, identity and main entry remain intact. */
export function mergeProjects(state: AppState, sourceId: string, targetId: string): AppState {
  const source = state.projects.find(p => p.id === sourceId), target = state.projects.find(p => p.id === targetId);
  if (!source || !target) throw new Error('来源或目标图标已删除，请重新选择。');
  if (sourceId === targetId) return state;
  const paths = new Set(target.items.map(item => pathKey(item.path)));
  const ids = new Set(target.items.map(item => item.id));
  const additions = source.items.filter(item => {
    const key = pathKey(item.path);
    if (paths.has(key)) return false;
    paths.add(key); return true;
  }).map(item => ({ ...item, id: uniqueId(item.id, ids) }));
  if (target.items.length + additions.length > 200) throw new Error('组合后超过 200 个入口，本次未合并。');
  return StateSchema.parse({ ...state, projects: state.projects.filter(p => p.id !== sourceId).map(p => p.id === targetId ? { ...p, items: [...p.items, ...additions] } : p) });
}

/** Replace the group in place. A failed capacity/validation check leaves the input intact. */
export function ungroupProject(state: AppState, projectId: string): AppState {
  const group = state.projects.find(p => p.id === projectId);
  if (!group) throw new Error('目标图标已删除，请重新选择。');
  if (group.items.length === 1) return state;
  if (state.projects.length + group.items.length - 1 > 100) throw new Error('拆分后超过 100 个图标，本次未拆分。');
  const ids = new Set(state.projects.map(p => p.id));
  const independent = group.items.map((item, index) => ({ ...group, id: index === 0 ? group.id : uniqueId(`${group.id}-${item.id}`, ids), name: item.name.slice(0, 60), description: index === 0 ? group.description : '', items: [item] }));
  return StateSchema.parse({ ...state, projects: state.projects.flatMap(p => p.id === projectId ? independent : [p]) });
}

export function reorderProject(state: AppState, sourceId: string, targetId: string, after = false): AppState {
  if (sourceId === targetId) return state;
  const source = state.projects.find(p => p.id === sourceId);
  if (!source || !state.projects.some(p => p.id === targetId)) return state;
  const projects = state.projects.filter(p => p.id !== sourceId);
  projects.splice(projects.findIndex(p => p.id === targetId) + Number(after), 0, source);
  return { ...state, projects };
}

/** Moving between collections changes references only, never filesystem storage. */
export function moveReference(state: AppState, sourceId: string, itemId: string, targetId?: string): AppState {
  const source = state.projects.find(p => p.id === sourceId), item = source?.items.find(i => i.id === itemId);
  if (!source || !item || sourceId === targetId) return state;
  const remaining = source.items.filter(i => i.id !== itemId);
  let projects = state.projects.flatMap(p => p.id === sourceId ? remaining.length ? [{ ...p, items: remaining }] : [] : [p]);
  if (targetId) {
    const target = projects.find(p => p.id === targetId);
    if (!target) throw new Error('目标集合已删除');
    if (target.items.length >= 200) throw new Error('每个集合最多 200 个入口');
    projects = projects.map(p => p.id !== targetId || p.items.some(i => pathKey(i.path) === pathKey(item.path)) ? p : { ...p, items: [...p.items, { ...item, id: uniqueId(item.id, new Set(p.items.map(i => i.id))) }] });
  } else {
    if (!remaining.length) return state;
    const independent = { ...source, id: uniqueId(`${source.id}-${item.id}`, new Set(projects.map(p => p.id))), name: item.name.slice(0, 60), description: '', items: [item] };
    projects.splice(projects.findIndex(p => p.id === sourceId) + 1, 0, independent);
  }
  return StateSchema.parse({ ...state, projects });
}

export function removeReference(state: AppState, projectId: string, itemId?: string): AppState {
  return { ...state, projects: state.projects.flatMap(p => p.id !== projectId ? [p] : itemId && p.items.some(i => i.id !== itemId) ? [{ ...p, items: p.items.filter(i => i.id !== itemId) }] : []) };
}
