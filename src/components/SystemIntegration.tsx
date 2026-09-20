import { useCallback, useEffect, useRef, useState } from 'react';
import { Monitor, RefreshCw, ShieldCheck } from 'lucide-react';
import { nativeMode, request } from '../bridge';
import type { IntegrationStatus } from '../contracts';
import './system-integration.css';

export function SystemIntegration() {
  const [status, setStatus] = useState<IntegrationStatus | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [message, setMessage] = useState('');
  const pending = useRef(false), mounted = useRef(false), generation = useRef(0);
  const run = useCallback(async (operation: 'refresh' | 'shortcut' | 'enable' | 'disable') => {
    if (!nativeMode || pending.current) return;
    pending.current = true;
    const id = ++generation.current;
    setBusy(true); setError(''); setMessage('');
    try {
      const result = operation === 'refresh' ? await request('system.getIntegration', {})
        : operation === 'shortcut' ? await request('system.createDesktopShortcut', {})
          : await request('system.setAutoStart', { enabled: operation === 'enable' });
      if (!mounted.current || id !== generation.current) return;
      setStatus(result);
      if (operation === 'shortcut') setMessage('桌面快捷方式已创建');
      else if (operation !== 'refresh') setMessage(result.autoStartHere ? '开机启动已开启' : '开机启动已关闭');
    } catch (failure) {
      if (mounted.current && id === generation.current) setError((failure as Error).message);
    } finally {
      if (mounted.current && id === generation.current) { pending.current = false; setBusy(false); }
    }
  }, []);
  useEffect(() => {
    mounted.current = true;
    const refresh = () => { void run('refresh'); };
    refresh(); window.addEventListener('focus', refresh);
    return () => { mounted.current = false; pending.current = false; generation.current++; window.removeEventListener('focus', refresh); };
  }, [run]);

  return <section className="cp-card system-integration" aria-label="启动与桌面">
    <header className="cp-card-head"><strong><Monitor size={15}/>启动与桌面</strong>
      <button className="icon-button" aria-label="刷新系统入口" disabled={!nativeMode || busy} onClick={() => void run('refresh')}><RefreshCw size={15}/></button>
    </header>
    <div className="integration-body" aria-busy={busy}>
      <label className="toggle-row integration-toggle"><span><strong>开机启动</strong>
        <small>{status?.autoStart && !status.autoStartHere ? '另一份 Luma 已开启；打开此开关将切换到当前版本。' : '登录 Windows 后在托盘待命，不弹出设置窗口。'}</small></span>
        <input aria-label="开机启动" type="checkbox" checked={status?.autoStartHere ?? false} disabled={!nativeMode || !status || busy} onChange={event => void run(event.target.checked ? 'enable' : 'disable')}/><i/>
      </label>
      <div className="integration-desktop"><div><strong>桌面快捷方式</strong><p>双击桌面图标，直接打开 Luma。</p></div>
        <button className="cp-secondary" disabled={!nativeMode || !status || busy} onClick={() => void run('shortcut')}><Monitor size={15}/>{status?.desktopShortcut ? '重新创建快捷方式' : '创建桌面快捷方式'}</button>
      </div>
      <p className="integration-note"><ShieldCheck size={16}/><span>面板显示在普通窗口上方 · 无需管理员权限</span></p>
      <p className="integration-hint">便携版移动目录后，请重新创建快捷方式并重新开启开机启动。</p>
      {!nativeMode && <p className="integration-hint">请在 Luma 桌面程序中设置；浏览器预览不会修改 Windows。</p>}
      {error && <p className="integration-error" role="alert">{error}</p>}
      {message && <p className="integration-message" role="status">{message}</p>}
    </div>
  </section>;
}
