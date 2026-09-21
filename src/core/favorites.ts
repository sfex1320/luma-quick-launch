import { StateSchema, type AppState, type Color, type LaunchItem, type Project } from '../contracts';
import { referenceKey } from './urls';
export const favoriteSignature = (items: LaunchItem[]) => JSON.stringify(items.map(item => [item.kind, referenceKey(item.path)]).sort((a,b) => JSON.stringify(a).localeCompare(JSON.stringify(b))));
export function favoriteFor(projects: Project[], items: LaunchItem[]) {
  const key = favoriteSignature(items);
  return projects.find(project => project.favorite && favoriteSignature(project.items) === key);
}
/** Snapshot only saved shortcut references. No filesystem mutation or launch. */
export function toggleFavorite(state: AppState, name: string, color: Color, items: LaunchItem[]): AppState {
  const existing = favoriteFor(state.projects, items);
  if (existing) return { ...state, projects: state.projects.filter(project => project.id !== existing.id) };
  if (state.projects.length >= 100) throw new Error('最多100个集合，请先取消不再需要的收藏或快捷项。');
  return StateSchema.parse({ ...state, projects: [...state.projects, { id: crypto.randomUUID(), name: name.slice(0,60), description: '', color, pinned: true, favorite: true, items: items.map(item => ({...item,id:crypto.randomUUID()})) }] });
}
