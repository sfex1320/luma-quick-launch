import { Terminal, RotateCcw } from 'lucide-react';
import type { LaunchItem } from '../contracts';
import './launch-command.css';

export function LaunchCommandEditor({ item, index, onChange }: { item: LaunchItem; index: number; onChange: (launch: LaunchItem['launch']) => void }) {
  if (item.kind !== 'folder') return null;
  return <div className="launch-command-editor">
    <label className="launch-command-toggle"><input type="checkbox" checked={!!item.launch} onChange={event => onChange(event.target.checked ? { command: '', workingDirectory: item.path } : undefined)}/><Terminal size={15}/><span>入口 {index + 1} · 手动启动命令</span></label>
    {item.launch ? <div className="launch-command-fields">
      <label className="form-label">工作目录<input aria-label={`入口 ${index + 1} 启动工作目录`} required maxLength={4096} value={item.launch.workingDirectory} placeholder="例如 D:\Projects\MyApp" onChange={event => onChange({ ...item.launch!, workingDirectory: event.target.value })}/></label>
      <label className="form-label">启动 / 测试命令<input aria-label={`入口 ${index + 1} 启动命令`} required maxLength={1000} value={item.launch.command} placeholder="例如 npm run dev、npm test、python app.py" onChange={event => onChange({ ...item.launch!, command: event.target.value })}/></label>
      <div className="launch-command-note"><small>保存后覆盖该根目录的自动识别。点击「手动启动」才在 CMD 中执行。</small><button type="button" className="text-button" onClick={() => onChange(undefined)}><RotateCcw size={12}/>恢复自动识别</button></div>
    </div> : <p>当前自动识别；若识别不准，可指定目录和自己的启动命令。</p>}
  </div>;
}
