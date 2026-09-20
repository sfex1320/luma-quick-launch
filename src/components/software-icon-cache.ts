import { nativeMode, request, subscribeCommittedState } from '../bridge';

type Entry = { task: Promise<string | null>; settled: boolean; expires: number; dataUrl: string | null };
const cache = new Map<string, Entry>();
const limit = 128;
const characterBudget = 6_000_000;
let epoch = 0;
const listeners = new Set<() => void>();
export const getIconEpoch = () => epoch;
export function subscribeIconEpoch(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; }
subscribeCommittedState(() => {
  // Keep pending entries counted until completion so rapid saves cannot grow outstanding work.
  for (const [key, entry] of cache) if (entry.settled) cache.delete(key);
  epoch++;
  listeners.forEach(listener => listener());
});

export function loadSoftwareIcon(projectId: string, itemId: string, pathKey: string, size: 32 | 48 | 64 | 96): Promise<string | null> {
  if (!nativeMode) return Promise.resolve(null);
  const key = JSON.stringify([projectId, itemId, pathKey, size, epoch]);
  const existing = cache.get(key);
  if (existing && (!existing.settled || existing.expires > Date.now())) return existing.task;
  cache.delete(key);
  if (cache.size >= limit) {
    const oldest = [...cache].find(([, entry]) => entry.settled);
    if (!oldest) return Promise.resolve(null);
    cache.delete(oldest[0]);
  }
  const entry: Entry = { task: Promise.resolve(null), settled: false, expires: Infinity, dataUrl: null };
  entry.task = request('shell.getIcon', { projectId, itemId, size }).then(result => result.dataUrl).catch(() => null).then(dataUrl => {
    entry.settled = true; entry.dataUrl = dataUrl; entry.expires = Date.now() + (dataUrl ? 300000 : 5000);
    let total = [...cache.values()].reduce((sum, value) => sum + (value.dataUrl?.length ?? 0), 0);
    for (const [oldKey, old] of cache) {
      if (total <= characterBudget) break;
      if (old.settled) { cache.delete(oldKey); total -= old.dataUrl?.length ?? 0; }
    }
    return dataUrl;
  });
  cache.set(key, entry);
  return entry.task;
}
