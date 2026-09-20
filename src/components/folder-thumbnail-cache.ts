import { nativeMode, request } from '../bridge';

type Entry = { task: Promise<string | null>; settled: boolean; expires: number; image: string | null };
const cache = new Map<string, Entry>();
let active = 0;
const queue: Array<() => void> = [];
export function clearFolderThumbnails() { for (const [key, entry] of cache) if (entry.settled) cache.delete(key); }

export function loadFolderThumbnail(projectId: string, itemId: string, entryId: string): Promise<string | null> {
  if (!nativeMode) return Promise.resolve(null);
  const key = JSON.stringify([projectId, itemId, entryId]);
  const cached = cache.get(key);
  if (cached && (!cached.settled || cached.expires > Date.now())) return cached.task;
  cache.delete(key);
  while (cache.size >= 128) {
    const oldest = [...cache].find(([, value]) => value.settled);
    if (!oldest) return Promise.resolve(null);
    cache.delete(oldest[0]);
  }
  if (queue.length >= 32) return Promise.resolve(null);
  const entry: Entry = { task: Promise.resolve(null), settled: false, expires: Infinity, image: null };
  entry.task = new Promise(resolve => {
    const run = () => {
      active++;
      void request('folder.getThumbnail', { projectId, itemId, entryId, size: 96 }).then(result => result.dataUrl).catch(() => null).then(image => {
        entry.settled = true; entry.image = image; entry.expires = Date.now() + (image ? 30000 : 5000);
        let chars = [...cache.values()].reduce((sum, value) => sum + (value.image?.length ?? 0), 0);
        for (const [oldKey, value] of cache) if (chars > 6_000_000 && value.settled) { cache.delete(oldKey); chars -= value.image?.length ?? 0; }
        resolve(image);
      }).finally(() => { active--; queue.shift()?.(); });
    };
    if (active < 2) run(); else queue.push(run);
  });
  cache.set(key, entry);
  return entry.task;
}
