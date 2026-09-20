import { referenceKey as key } from './urls';
import type { ImportedShortcut, Project } from '../contracts';

export function appendDraftShortcuts(project: Project, items: ImportedShortcut[], placeholderId?: string) {
  if (!items.length) throw new Error('没有可添加的入口，请拖入本机文件夹、软件或文件。');
  const kept = project.items.filter(item => !(item.id === placeholderId && item.name === '主目录' && !item.path && item.kind === 'folder'));

  const existing = new Set(kept.filter(item => item.path).map(item => key(item.path)));
  const unique = items.filter(item => { const path = key(item.path); if (existing.has(path)) return false; existing.add(path); return true; });
  if (!unique.length) throw new Error('这些快捷项已经存在，本次未添加。');
  if (kept.length + unique.length > 200) throw new Error('每个堆叠最多 200 个入口，本次未添加，请减少选择数量。');
  return {
    project: { ...project, name: project.name || unique[0].name.slice(0, 60), items: [...kept, ...unique.map(item => ({ ...item, id: crypto.randomUUID() }))] },
    skipped: items.length - unique.length,
    added: unique.length,
  };
}
