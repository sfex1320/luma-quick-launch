import { useCallback, useEffect, useRef, useState } from 'react';
import type { AppState } from './contracts';
import { request, subscribeHost } from './bridge';
import { fingerprint, reconcile } from './core/workspaceSync';

export function useWorkspace() {
  const [state, setState] = useState<AppState | null>(null), [error, setError] = useState(''), [status, setStatus] = useState('正在连接');
  const [tick, setTick] = useState(0);
  const current = useRef<AppState | null>(null), saved = useRef<AppState | null>(null), saving = useRef(false), blocked = useRef(false), alive = useRef(true);
  const deferred = useRef<AppState | null>(null);
  const commit = useCallback((next: AppState) => { current.current = next; setState(next); }, []);
  const accept = useCallback((remote: AppState) => {
    if (!alive.current || saved.current && remote.revision <= saved.current.revision) return;
    if (saving.current) { if (!deferred.current || deferred.current.revision < remote.revision) deferred.current = remote; return; }
    try {
      const next = saved.current && current.current ? reconcile(saved.current, current.current, remote) : remote;
      saved.current = remote; commit(next); setStatus(fingerprint(next) === fingerprint(remote) ? '已保存' : '待保存');
    } catch (reason) { blocked.current = true; setError((reason as Error).message); setStatus('保存冲突'); }
  }, [commit]);
  useEffect(() => {
    alive.current = true;
    let visibility = '';
    const refresh = () => void request('app.getState', {}).then(accept).catch(reason => alive.current && setError(reason.message));
    const unsubscribe = subscribeHost(event => {
      if (event.event === 'app.stateChanged') accept(event.data);
      else { const key = `${event.data.visible}/${event.data.visibilityId ?? ''}`; if (key === visibility) return; visibility = key; if (event.data.visible) refresh(); }
    });
    const focus = () => { if (!document.hidden) refresh(); };
    window.addEventListener('focus', focus); document.addEventListener('visibilitychange', focus); refresh();
    return () => { alive.current = false; unsubscribe(); window.removeEventListener('focus', focus); document.removeEventListener('visibilitychange', focus); };
  }, [accept]);
  useEffect(() => {
    if (!state || !saved.current || fingerprint(saved.current) === fingerprint(state) || blocked.current) return;
    setStatus('待保存');
    const timer = setTimeout(async () => {
      if (saving.current || !current.current || !saved.current) return;
      saving.current = true; setStatus('保存中');
      const base = saved.current, submitted = { ...current.current, revision: base.revision };
      try {
        const result = await request('app.saveState', { state: submitted, expectedRevision: base.revision });
        if (!alive.current) return;
        const next = { ...current.current!, revision: result.revision };
        saved.current = result; commit(next); setStatus(fingerprint(next) === fingerprint(result) ? '已保存' : '待保存');
      } catch (reason) {
        if (!alive.current) return;
        try {
          const remote = await request('app.getState', {});
          if (remote.revision <= base.revision) throw reason;
          commit(reconcile(base, current.current!, remote)); saved.current = remote;
        } catch (failure) { blocked.current = true; setError((failure as Error).message); setStatus('保存失败'); }
      } finally {
        saving.current = false;
        if (alive.current) { const remote = deferred.current; deferred.current = null; if (remote) accept(remote); setTick(t => t + 1); }
      }
    }, 200);
    return () => clearTimeout(timer);
  }, [state, tick, commit, accept]);
  const update = useCallback((change: (s: AppState) => AppState) => { if (current.current && !blocked.current) commit(change(current.current)); }, [commit]);
  return { state, update, status, error };
}
