import { useEffect, useRef, useState } from 'react';
import { Keyboard, Plus, Trash2 } from 'lucide-react';
import { ShortcutListSchema, type Preferences, type Project, type ShortcutBinding, type Methods } from '../contracts';
import { nativeMode, request, subscribeCommittedState } from '../bridge';
import { bindingFromKey, contextActions, formatHotkey, hotkeyActions, shortcutTargetLabel } from '../core/hotkeys';
import './shortcut-settings.css';

export function ShortcutSettings({ preferences, projects, onChange }: { preferences: Preferences; projects: Project[]; onChange: (value: Preferences) => void }) {
  const bindings = preferences.shortcuts ?? [];
  const [action, setAction] = useState<ShortcutBinding['action']>('dock');
  const [projectId, setProjectId] = useState(''), [itemId, setItemId] = useState('');
  const [scope, setScope] = useState<ShortcutBinding['scope']>('panel');
  const [chord, setChord] = useState<ReturnType<typeof bindingFromKey>>(null);
  const [recording, setRecording] = useState(false), [recordingReady, setRecordingReady] = useState(false);
  const [message, setMessage] = useState('');
  const [statuses, setStatuses] = useState<Methods['shortcut.getStatus']['result']['bindings']>([]);
  const refreshStatus = useRef<() => void>(() => {});
  const contextOnly = (contextActions as readonly string[]).includes(action);
  const modifier = chord && (chord.ctrl || chord.alt || chord.shift);
  useEffect(() => {
    let alive = true;
    const refresh = () => { if (nativeMode) void request('shortcut.getStatus', {}).then(result => { if (alive) setStatuses(result.bindings); }).catch(error => { if (alive) setMessage(error.message); }); };
    refreshStatus.current = refresh;
    refresh(); const unsubscribe = subscribeCommittedState(refresh);
    window.addEventListener('focus', refresh);
    return () => { alive = false; unsubscribe(); refreshStatus.current = () => {}; window.removeEventListener('focus', refresh); };
  }, []);
  useEffect(() => {
    if (!recording) return;
    let alive = true;
    const acquire = () => {
      if (!nativeMode) { setRecordingReady(true); return; }
      void request('shortcut.setRecording', { active: true }).then(result => { if (alive) { setRecordingReady(result.accepted); refreshStatus.current(); } }).catch(error => { if (alive) { setRecordingReady(false); setMessage(error.message); } });
    };
    acquire();
    const timer = nativeMode ? setInterval(acquire, 15000) : undefined;
    const blur = () => setRecording(false);
    window.addEventListener('blur', blur);
    return () => { alive = false; clearInterval(timer); setRecordingReady(false); window.removeEventListener('blur', blur); if (nativeMode) void request('shortcut.setRecording', { active: false }).then(() => refreshStatus.current()).catch(() => {}); };
  }, [recording]);
  const save = () => {
    if (!chord) { setMessage('请先录制快捷键'); return; }
    const binding: ShortcutBinding = { id: crypto.randomUUID(), ...chord, action, scope: !modifier || contextOnly ? 'panel' : scope, ...(action === 'item' || action === 'project' ? { projectId } : {}), ...(action === 'item' ? { itemId } : {}) };
    const parsed = ShortcutListSchema.safeParse([...bindings, binding]);
    if (!parsed.success) { setMessage(parsed.error.issues[0].message); return; }
    onChange({ ...preferences, shortcuts: parsed.data }); setChord(null); setMessage('已添加，保存后生效');
  };
  return <section className="cp-card shortcut-settings" aria-label="快捷键设置">
    <header className="cp-card-head"><strong><Keyboard size={18}/>快捷键</strong></header>
    <div className="shortcut-settings-body">
      <p className="shortcut-hint">单键在面板有焦点时生效；组合键可全局使用。输入文字、命名或录制按键时，面板快捷键暂停。</p>
      <div className="shortcut-config-grid">
        <label>功能<select aria-label="快捷键功能" value={action} onChange={event => { setAction(event.target.value as ShortcutBinding['action']); setMessage(''); }}>{Object.entries(hotkeyActions).map(([value, label]) => <option value={value} key={value}>{label}</option>)}</select></label>
        {(action === 'project' || action === 'item') && <label>项目<select aria-label="快捷键项目" value={projectId} onChange={event => { setProjectId(event.target.value); setItemId(''); }}><option value="">选择项目或集合</option>{projects.map(project => <option key={project.id} value={project.id}>{project.name}</option>)}</select></label>}
        {action === 'item' && <label>入口<select aria-label="快捷键入口" value={itemId} onChange={event => setItemId(event.target.value)}><option value="">选择软件、文件夹或文件</option>{projects.find(project => project.id === projectId)?.items.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>}
        <label>按键<input aria-label="录制快捷键" readOnly value={recording ? recordingReady ? '请按下快捷键…' : '正在准备录制…' : chord ? formatHotkey(chord) : ''} placeholder="点击后按下快捷键" onFocus={() => { setMessage(''); setRecording(true); }} onBlur={() => setRecording(false)} onKeyDown={event => {
          event.preventDefault(); event.stopPropagation();
          if (!recordingReady) return;
          const key = bindingFromKey(event.nativeEvent);
          if (key) { setChord(key); setScope(key.ctrl || key.shift || key.alt ? 'global' : 'panel'); event.currentTarget.blur(); }
        }}/></label>
        <label>生效范围<select aria-label="快捷键生效范围" value={!modifier || contextOnly ? 'panel' : scope} disabled={!modifier || contextOnly} onChange={event => setScope(event.target.value as ShortcutBinding['scope'])}><option value="panel">面板有焦点时</option><option value="global">全局</option></select></label>
      </div>
      <button className="cp-secondary" onClick={save}><Plus size={15}/>添加快捷键</button>
      {message && <p className="shortcut-message" role="status">{message}</p>}
      <div className="shortcut-bindings">{bindings.map(binding => {
        const status = statuses.find(status => status.id === binding.id);
        const target = shortcutTargetLabel(binding, projects), unavailable = target === '目标已失效';
        return <div className="shortcut-binding" key={binding.id}>
          <div><strong>{target}</strong><small className={unavailable || status && !status.registered ? 'shortcut-unavailable' : ''}>{unavailable ? '目标已失效，请移除后重新绑定' : status?.message || (nativeMode ? '保存后检查状态' : binding.scope === 'global' ? '全局快捷键需在桌面版生效' : '面板有焦点时生效')}</small></div>
          <kbd>{formatHotkey(binding)}</kbd><span className="shortcut-scope">{binding.scope === 'global' ? '全局' : '面板'}</span><button className="icon-button" aria-label={`移除快捷键 ${formatHotkey(binding)}`} onClick={() => { onChange({ ...preferences, shortcuts: bindings.filter(item => item.id !== binding.id) }); setMessage('已移除快捷键'); }}><Trash2 size={15}/></button>
        </div>;
      })}</div>
      {!bindings.length && <p className="shortcut-hint">尚未添加自定义快捷键。Ctrl + Alt + 空格和 Ctrl + K 保留用于搜索。</p>}
    </div>
  </section>;
}
