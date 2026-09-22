import { describe, expect, test } from 'vitest';
import { fuzzyIncludes } from './searchMatch';

describe('fuzzyIncludes', () => {
  test('empty or blank query matches everything', () => {
    expect(fuzzyIncludes('', 'anything')).toBe(true);
    expect(fuzzyIncludes('   ', 'anything')).toBe(true);
  });
  test('literal substring matches case-insensitively', () => {
    expect(fuzzyIncludes('侧边', 'Smart Sidebar Tool 侧边栏')).toBe(true);
    expect(fuzzyIncludes('SIDEBAR', 'smart sidebar')).toBe(true);
  });
  test('ordered subsequence matches without adjacency', () => {
    expect(fuzzyIncludes('文4', '文件4.txt')).toBe(true);
    expect(fuzzyIncludes('sst', 'Smart Sidebar Tool')).toBe(true);
    expect(fuzzyIncludes('文4', '文件2.txt')).toBe(false);
    expect(fuzzyIncludes('42', '文件4.txt')).toBe(false);
  });
  test('every whitespace-separated word must match', () => {
    expect(fuzzyIncludes('smart tool', 'Smart Sidebar Tool')).toBe(true);
    expect(fuzzyIncludes('smart other', 'Smart Sidebar Tool')).toBe(false);
  });
});
