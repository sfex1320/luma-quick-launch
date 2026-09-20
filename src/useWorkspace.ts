import { useCallback, useEffect, useRef, useState } from 'react';
import type { AppState } from './contracts';
import { request, subscribeHost } from './bridge';
const content = (s: AppState) => JSON.stringify({ ...s, revision: 0 });
export function useWorkspace() {
  const [state, setState] = useState<AppState | null>(null);
  const [error, setError] = useState('');
  const [status, setStatus] = useState('正在连接');
  const [tick, setTick] = useState(0);
  const revision = useRef(0), saved = useRef(''), current = useRef(state), saving = useRef(false), blocked = useRef(false), inFlight = useRef('');
  current.current = state;
  useEffect(() => {
    let alive = true;
    request('app.getState', {}).then(data => { if (alive) { revision.current = data.revision; saved.current = content(data); setState(data); setStatus('已保存'); } }).catch(e => alive && setError(e.message));
    return () => { alive = false; };
  }, []);
  useEffect(() => subscribeHost(event => {
    if (event.event !== 'app.stateChanged' || event.data.revision <= revision.current) return;
    if (saving.current && content(event.data) === inFlight.current) return;
    if (saving.current || current.current && content(current.current) !== saved.current) {
      blocked.current = true; setError('配置已在另一窗口更改。请重新载入后再编辑。'); return;
    }
    revision.current = event.data.revision; saved.current = content(event.data); setState(event.data);
  }), []);
  useEffect(() => {
    if (!state || saved.current === content(state) || blocked.current) return;
    setStatus('待保存');
    const timer = setTimeout(async () => {
      if (saving.current) return;
      saving.current = true; inFlight.current = content(state); setStatus('保存中');
      try {
        const result = await request('app.saveState', { state: { ...state, revision: revision.current }, expectedRevision: revision.current });
        revision.current = result.revision; saved.current = content(result); setStatus(blocked.current ? '保存冲突' : '已保存');
      } catch (e) { blocked.current = true; setError((e as Error).message); setStatus('保存失败'); }
      finally { saving.current = false; inFlight.current = ''; setTick(t => t + 1); }
    }, 450);
    return () => clearTimeout(timer);
  }, [state, tick]);
  const update = useCallback((change: (s: AppState) => AppState) => setState(s => s ? change(s) : s), []);
  return { state, update, status, error };
}
