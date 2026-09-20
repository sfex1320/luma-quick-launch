import { expect, it } from 'vitest';
import { parseUrls, referenceKey } from './urls';
import { shortcutMatches } from './searchMatch';
it('URL references preserve case-sensitive paths and queries while normalizing hostnames', () => {
  expect(parseUrls('#title\nHTTPS://Example.com/Work?q=A')).toEqual(['https://example.com/Work?q=A']);
  expect(referenceKey('https://example.com/Work')).not.toBe(referenceKey('https://example.com/work'));
  expect(referenceKey('C:/WORK/')).toBe(referenceKey('c:\\work'));
});
it('rejects executable schemes and credentials', () => {
  for (const url of ['file:///c:/x', 'javascript:alert(1)', 'https://user:pass@example.com', 'https://example.com/"bad']) expect(() => parseUrls(url)).toThrow();
});
it('aliases apply only to software; fuzzy names require a conservative query length', () => {
  expect(shortcutMatches('AI', 'Illustrator', 'C:/Adobe/Illustrator.exe', 'app')).toBe(true);
  expect(shortcutMatches('AI', 'Illustrator', 'C:/Adobe/Illustrator', 'folder')).toBe(false);
  expect(shortcutMatches('projet', 'project.txt', 'C:/x', 'file', true, true)).toBe(true);
  expect(shortcutMatches('prj', 'project', 'C:/x', 'folder', true, true)).toBe(false);
});
