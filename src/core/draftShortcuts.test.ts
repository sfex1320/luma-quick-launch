import { expect, it } from 'vitest';
import { appendDraftShortcuts } from './draftShortcuts';
import { initialState } from '../data';

it('appends distinct paths preserving existing IDs and removes only the untouched initial placeholder', () => {
  const original = initialState.projects[0];
  const item = { name: '文件', path: 'C:\\test.txt', kind: 'file' as const };
  const result = appendDraftShortcuts(original, [item, { ...item, path: 'c:/TEST.txt' }]);
  expect(result.project.items.slice(0, original.items.length)).toEqual(original.items);
  expect(result.project.items).toHaveLength(original.items.length + 1);
  expect(result.skipped).toBe(1);
  const draft = { ...original, name: '', items: [{ id: 'blank', name: '主目录', path: '', kind: 'folder' as const }] };
  expect(appendDraftShortcuts(draft, [item], 'blank').project.items.map(i => i.path)).toEqual([item.path]);
  expect(appendDraftShortcuts({ ...draft, items: [{ ...draft.items[0], name: '保留编辑' }] }, [item], 'blank').project.items).toHaveLength(2);
});
it('rejects overflow atomically and reports all-duplicate requests', () => {
  const draft = { ...initialState.projects[0], items: Array.from({ length: 200 }, (_, n) => ({ id: `i${n}`, name: `n${n}`, path: `C:\\${n}`, kind: 'folder' as const })) };
  expect(() => appendDraftShortcuts(draft, [{ name: 'new', path: 'C:\\new', kind: 'folder' }])).toThrow('本次未添加');
  expect(draft.items).toHaveLength(200);
  expect(() => appendDraftShortcuts(draft, [draft.items[0]])).toThrow('已经存在');
});
