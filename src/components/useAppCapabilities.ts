import { useEffect, useState, useSyncExternalStore } from 'react';
import { request, subscribeCommittedState } from '../bridge';
import type { Project } from '../contracts';

type Entry = { task: Promise<boolean>; settled: boolean; expires: number };
const cache = new Map<string, Entry>();
const queue: Array<() => void> = [];
const listeners = new Set<() => void>();
let active = 0, epoch = 0;
const empty = new Set<string>();
const snapshot = () => epoch;
const subscribe = (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener); }; };
export const appCapabilityKey = (projectId: string, itemId: string) => JSON.stringify([projectId, itemId]);

subscribeCommittedState(() => {
  for (const [key, entry] of cache) if (entry.settled) cache.delete(key);
  epoch++;
  listeners.forEach(listener => listener());
});

function pump() { while (active < 4 && queue.length) queue.shift()!(); }
function load(projectId: string, itemId: string, path: string, generation: number): Promise<boolean> {
  const key = JSON.stringify([projectId, itemId, path, generation]);
  const found = cache.get(key);
  if (found && (!found.settled || found.expires > Date.now())) return found.task;
  cache.delete(key);
  if (cache.size >= 256) {
    const oldest = [...cache].find(([, entry]) => entry.settled);
    if (!oldest) return Promise.resolve(false);
    cache.delete(oldest[0]);
  }
  let resolve!: (supported: boolean) => void;
  const entry: Entry = { task: new Promise<boolean>(done => { resolve = done; }), settled: false, expires: Infinity };
  cache.set(key, entry);
  queue.push(() => {
    active++;
    const finish = (supported: boolean) => {
      entry.settled = true; entry.expires = Date.now() + (supported ? 300000 : 5000);
      if (generation !== epoch) cache.delete(key);
      resolve(generation === epoch && supported);
      active--; pump();
    };
    // Obsolete saves never cause queued requests for their former identities.
    if (generation !== epoch) { finish(false); return; }
    void request('shell.getAppCapabilities', { projectId, itemId }).then(result => finish(result.recentSupported), () => finish(false));
  });
  pump();
  return entry.task;
}

/** Unknown and failed capabilities stay hidden; saved identity/path changes invalidate results. */
export function useAppCapabilities(projects: readonly Project[]): ReadonlySet<string> {
  const generation = useSyncExternalStore(subscribe, snapshot);
  const identities = JSON.stringify(projects.flatMap(project => project.items.filter(item => item.kind === 'app').map(item => [project.id, item.id, item.path])));
  const signature = `${generation}:${identities}`;
  const [result, setResult] = useState<{ signature: string; supported: Set<string> } | null>(null);
  useEffect(() => {
    let current = true;
    const entries = JSON.parse(identities) as Array<[string, string, string]>;
    let index = 0;
    const consume = async () => {
      while (current && generation === epoch && index < entries.length) {
        const [projectId, itemId, path] = entries[index++];
        const supported = await load(projectId, itemId, path, generation);
        if (!current || !supported || generation !== epoch) continue;
        setResult(previous => ({ signature, supported: new Set([...(previous?.signature === signature ? previous.supported : []), appCapabilityKey(projectId, itemId)]) }));
      }
    };
    // Feed a small window into the shared queue so large saved groups are not
    // dropped merely because they exceed the cache's admission bound.
    for (let worker = 0; worker < Math.min(4, entries.length); worker++) void consume();
    return () => { current = false; };
  }, [identities, generation, signature]);
  return result?.signature === signature ? result.supported : empty;
}
