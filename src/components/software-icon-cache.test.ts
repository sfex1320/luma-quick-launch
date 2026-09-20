import { afterEach, expect, it, vi } from 'vitest';
const bridge = vi.hoisted(() => ({ request: vi.fn(), committed: (() => {}) as () => void }));
vi.mock('../bridge', () => ({ nativeMode: true, request: bridge.request, subscribeHost: (callback: (event: unknown) => void) => { bridge.committed = () => callback({ event: 'app.stateChanged' }); }, subscribeCommittedState: (callback: () => void) => { bridge.committed = callback; } }));
afterEach(() => { vi.resetModules(); bridge.request.mockReset(); });

it('coalesces duplicate icon requests while they are pending', async () => {
  bridge.request.mockReturnValue(new Promise(() => {}));
  const { loadSoftwareIcon } = await import('./software-icon-cache');
  const a = loadSoftwareIcon('p', 'item', 'path', 64);
  expect(loadSoftwareIcon('p', 'item', 'path', 64)).toBe(a);
  expect(bridge.request).toHaveBeenCalledTimes(1);
});

it('pending requests remain bounded across repeated committed state changes', async () => {
  bridge.request.mockReturnValue(new Promise(() => {}));
  const { loadSoftwareIcon } = await import('./software-icon-cache');
  for (let i = 0; i < 128; i++) void loadSoftwareIcon('p', String(i), 'path', 64);
  bridge.committed();
  void loadSoftwareIcon('p', 'another', 'new-path', 64);
  expect(bridge.request).toHaveBeenCalledTimes(128);
});
