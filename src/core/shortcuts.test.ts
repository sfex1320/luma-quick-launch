import { describe, expect, it } from 'vitest';
import { addShortcuts } from './shortcuts';
import { initialState } from '../data';
describe('shortcut imports', () => {
  it('adds standalone files and apps without replacing existing project groups', () => {
    const state = addShortcuts(initialState, [{ name: '编辑器', path: 'C:\\Apps\\editor.exe', kind: 'app' }, { name: '方案', path: 'D:\\方案.pdf', kind: 'file' }]);
    expect(state.projects.slice(0, initialState.projects.length)).toEqual(initialState.projects);
    expect(state.projects.slice(-2).map(p => [p.pinned, p.items[0].kind])).toEqual([[true, 'app'], [true, 'file']]);
    expect(state.revision).toBe(initialState.revision);
  });
  it('drops into one group and deduplicates case insensitive Windows paths', () => {
    const original = initialState.projects[0];
    const state = addShortcuts(initialState, [{ name: 'one', path: 'D:\\a.pdf', kind: 'file' }, { name: 'two', path: 'd:/A.pdf', kind: 'file' }], original.id);
    expect(state.projects).toHaveLength(initialState.projects.length);
    expect(state.projects[0].items).toHaveLength(original.items.length + 1);
  });
  it('rejects stale group targets and capacity overflow before changing state', () => {
    const item = { name: 'file', path: 'D:\\a.pdf', kind: 'file' as const };
    expect(() => addShortcuts(initialState, [item], 'removed')).toThrow('已删除');
    const full = { ...initialState, projects: Array.from({ length: 100 }, (_, i) => ({ ...initialState.projects[0], id: `${i}` })) };
    expect(() => addShortcuts(full, [item])).toThrow('最多 100');
  });
});
