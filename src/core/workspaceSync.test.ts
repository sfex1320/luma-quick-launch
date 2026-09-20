import { describe, expect, it } from 'vitest';
import { initialState } from '../data';
import { fingerprint, reconcile } from './workspaceSync';
describe('multi-window state reconciliation', () => {
  it('compares content independent of property order', () => {
    expect(fingerprint({ a: 1, b: 2, revision: 0 })).toBe(fingerprint({ revision: 3, b: 2, a: 1 }));
  });
  it('keeps local grouping and remote appearance', () => {
    const base = structuredClone(initialState), local = structuredClone(base), remote = structuredClone(base);
    local.projects[0].items.push(...local.projects[1].items.map(i => ({ ...i, id: 'merged-' + i.id }))); local.projects.splice(1, 1);
    remote.revision++; remote.preferences.width = 800;
    const result = reconcile(base, local, remote);
    expect(result.projects).toEqual(local.projects); expect(result.preferences.width).toBe(800);
  });
  it('rejects competing edits to the same field', () => {
    const base = structuredClone(initialState), local = structuredClone(base), remote = structuredClone(base);
    local.projects[0].name = '甲'; remote.projects[0].name = '乙';
    expect(() => reconcile(base, local, remote)).toThrow('同一入口');
  });
});
