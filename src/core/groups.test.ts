import { describe, expect, it } from 'vitest';
import { initialState } from '../data';
import { StateSchema, type AppState, type Project } from '../contracts';
import { mergeProjects, ungroupProject } from './groups';

const project = (id: string, kind: 'app' | 'folder' | 'file' = 'app'): Project => ({ id, name: id, description: `about ${id}`, color: 'blue', pinned: true, items: [{ id: 'main', name: id, path: `C:\\${id}`, kind }] });
const stateWith = (...projects: Project[]): AppState => ({ ...initialState, revision: 7, projects });

describe('project grouping', () => {
  it('appends software and folder members, preserves target metadata and main entry, removes only source', () => {
    const source = project('editor'), target = { ...project('work', 'folder'), color: 'peach' as const, pinned: false }, other = project('other');
    const state = stateWith(source, other, target), before = structuredClone(state);
    const result = mergeProjects(state, source.id, target.id);
    expect(result.projects.map(p => p.id)).toEqual(['other', 'work']);
    expect(result.projects[1]).toMatchObject({ ...target, items: [target.items[0], { ...source.items[0], id: expect.any(String) }] });
    expect(result.projects[1].items[1].id).not.toBe('main');
    expect(result.projects[1].items.map(i => i.kind)).toEqual(['folder', 'app']);
    expect(result.revision).toBe(7);
    expect(state).toEqual(before);
    expect(StateSchema.safeParse(result).success).toBe(true);
  });
  it('deduplicates Windows paths by case, separators and trailing slash before capacity check', () => {
    const target = project('target'), source = project('source');
    target.items = Array.from({ length: 200 }, (_, n) => ({ id: `t${n}`, name: `n${n}`, path: `C:\\Apps\\${n}`, kind: 'app' }));
    source.items = [{ id: 's', name: 'duplicate', path: 'c:/apps/0/', kind: 'app' }];
    const result = mergeProjects(stateWith(target, source), source.id, target.id);
    expect(result.projects).toHaveLength(1);
    expect(result.projects[0].items).toEqual(target.items);
  });
  it('rejects an oversized merge atomically and rejects stale source or target', () => {
    const target = project('target'), source = project('source');
    target.items = Array.from({ length: 200 }, (_, n) => ({ id: `t${n}`, name: `n${n}`, path: `C:\\Apps\\${n}`, kind: 'app' }));
    const state = stateWith(target, source), before = structuredClone(state);
    expect(() => mergeProjects(state, source.id, target.id)).toThrow('200');
    expect(state).toEqual(before);
    expect(() => mergeProjects(state, 'missing', target.id)).toThrow('已删除');
    expect(() => mergeProjects(state, source.id, 'missing')).toThrow('已删除');
    expect(mergeProjects(state, source.id, source.id)).toBe(state);
  });
  it('splits each member in place, preserving item IDs, kinds, paths, color and main position', () => {
    const group = project('group', 'folder');
    group.items.push({ id: 'editor', name: 'Editor', path: 'C:\\Apps\\Editor.exe', kind: 'app' }, { id: 'document', name: 'Brief', path: 'C:\\Brief.pdf', kind: 'file' });
    const state = stateWith(project('before'), group, project('after'));
    const result = ungroupProject(state, group.id);
    expect(result.projects.map(p => p.name)).toEqual(['before', 'group', 'Editor', 'Brief', 'after']);
    expect(result.projects[1].id).toBe(group.id);
    expect(result.projects.slice(1, 4).map(p => p.items[0])).toEqual(group.items);
    expect(result.projects.slice(1, 4).every(p => p.color === group.color && p.pinned === group.pinned)).toBe(true);
    expect(StateSchema.safeParse(result).success).toBe(true);
    expect(state.projects[1].items).toHaveLength(3);
  });
  it('refuses splitting beyond 100 projects without dropping members and truncates project names only', () => {
    const group = project('group');
    group.items.push({ id: 'long', name: 'x'.repeat(120), path: 'C:\\long', kind: 'file' });
    const state = stateWith(group, ...Array.from({ length: 99 }, (_, n) => project(`p${n}`)));
    expect(() => ungroupProject(state, group.id)).toThrow('100');
    expect(state.projects).toHaveLength(100);
    expect(state.projects[0].items).toHaveLength(2);
    const result = ungroupProject(stateWith(group), group.id);
    expect(result.projects[1].name).toHaveLength(60);
    expect(result.projects[1].items[0].name).toHaveLength(120);
    expect(ungroupProject(stateWith(project('single')), 'single').projects).toHaveLength(1);
  });
});
