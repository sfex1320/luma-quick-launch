import { beforeEach, expect, it, vi } from 'vitest';
const bridge = vi.hoisted(() => ({ request: vi.fn() }));
vi.mock('../bridge', () => ({ nativeMode: true, request: bridge.request }));
beforeEach(() => { vi.resetModules(); bridge.request.mockReset(); });

it('thumbnail requests share pending work, keep two active workers and bound overflow', async () => {
  const pending: Array<(value: { dataUrl: string | null }) => void> = [];
  bridge.request.mockImplementation(() => new Promise(resolve => pending.push(resolve)));
  const { loadFolderThumbnail } = await import('./folder-thumbnail-cache');
  const first = loadFolderThumbnail('p', 'i', '0');
  expect(loadFolderThumbnail('p', 'i', '0')).toBe(first);
  const rest = Array.from({ length: 39 }, (_, i) => loadFolderThumbnail('p', 'i', String(i + 1)));
  expect(bridge.request).toHaveBeenCalledTimes(2);
  await expect(rest[38]).resolves.toBeNull();
  for (let i = 0; i < 34; i++) {
    await vi.waitFor(() => expect(pending.length).toBeGreaterThan(i), { interval: 1 });
    pending[i]({ dataUrl: null });
  }
  await Promise.all([first, ...rest]);
  expect(bridge.request).toHaveBeenCalledTimes(34);
});

it('refresh invalidates completed images and errors fall back without repeated requests', async () => {
  bridge.request.mockResolvedValue({ dataUrl: 'old' });
  const { loadFolderThumbnail, clearFolderThumbnails } = await import('./folder-thumbnail-cache');
  await expect(loadFolderThumbnail('p', 'i', 'a')).resolves.toBe('old');
  await expect(loadFolderThumbnail('p', 'i', 'a')).resolves.toBe('old');
  expect(bridge.request).toHaveBeenCalledTimes(1);
  clearFolderThumbnails(); bridge.request.mockRejectedValue(new Error('provider unavailable'));
  await expect(loadFolderThumbnail('p', 'i', 'a')).resolves.toBeNull();
  await expect(loadFolderThumbnail('p', 'i', 'a')).resolves.toBeNull();
  expect(bridge.request).toHaveBeenCalledTimes(2);
});
