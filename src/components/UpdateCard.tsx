import { useState } from 'react';
import { Check, Download, RefreshCw } from 'lucide-react';
import { request } from '../bridge';
import type { UpdateCheckResult } from '../contracts';
import { version as appVersion } from '../../package.json';

type Phase = 'idle' | 'checking' | 'downloading' | 'applying';
/** In-app update flow: check GitHub Releases, download with SHA-256 verification,
    then replace the portable files or run the installer. Native-only. */
export function UpdateCard() {
  const [phase, setPhase] = useState<Phase>('idle');
  const [info, setInfo] = useState<UpdateCheckResult | null>(null);
  const [message, setMessage] = useState('');
  const busy = phase !== 'idle';
  const check = async () => {
    setPhase('checking'); setMessage(''); setInfo(null);
    try { setInfo(await request('update.check', {})); }
    catch (error) { setMessage((error as Error).message); }
    finally { setPhase('idle'); }
  };
  const downloadAndApply = async () => {
    if (!info?.asset) return;
    setPhase('downloading'); setMessage('');
    try {
      const result = await request('update.download', { url: info.asset.url, fileName: info.asset.name, ...(info.asset.sha256Url ? { sha256Url: info.asset.sha256Url } : {}) });
      if (!result.verified) throw new Error('下载内容未通过校验，已取消。');
      const modeText = info.installMode === 'portable' ? '应用将退出，替换便携版文件后自动重启。' : '将启动安装程序覆盖安装，配置与快捷方式保留。';
      if (!confirm(`已下载 v${info.latestVersion} 并通过校验。${modeText}是否继续？`)) { setPhase('idle'); return; }
      setPhase('applying');
      await request('update.apply', { path: result.path });
      // portable: the host exits and the updater restarts it; installer: setup takes over.
    } catch (error) { setMessage((error as Error).message); setPhase('idle'); }
  };
  return <section className="cp-card update-card" aria-label="软件更新">
    <header className="cp-card-head"><strong>软件更新</strong><span className="cp-badge">当前 v{appVersion}</span></header>
    <div className="cp-kv"><span>更新来源</span><strong>GitHub Releases</strong></div>
    {info && <div className="cp-kv"><span>最新版本</span><strong>{info.hasUpdate ? `v${info.latestVersion} · 可更新` : `v${info.latestVersion} · 已是最新`}</strong></div>}
    {info?.hasUpdate && info.asset && <>
      <div className="cp-kv"><span>更新方式</span><strong>{info.installMode === 'portable' ? '便携版 · 下载校验后直接替换文件' : '安装版 · 下载校验后重新安装'}</strong></div>
      {info.notes && <p className="update-notes">{info.notes}</p>}
    </>}
    <div className="settings-actions">
      {!info?.hasUpdate && <button className="cp-secondary" disabled={busy} onClick={() => void check()}><RefreshCw size={15}/>{phase === 'checking' ? '正在检查…' : '检查更新'}</button>}
      {info?.hasUpdate && !!info.asset && <button className="cp-primary" disabled={busy} onClick={() => void downloadAndApply()}><Download size={15}/>{phase === 'downloading' ? '正在下载并校验…' : phase === 'applying' ? '正在应用更新…' : `下载并更新到 v${info.latestVersion}`}</button>}
      {info?.hasUpdate && !info.asset && <span className="muted">该版本没有适用于当前安装方式的包。</span>}
      {info && !info.hasUpdate && <span className="cp-health"><span className="status-dot ok"/><Check size={13}/>已是最新</span>}
    </div>
    {message && <p className="shortcut-message" role="alert">{message}</p>}
  </section>;
}
