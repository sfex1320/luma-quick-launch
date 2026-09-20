import { useState, type ReactNode } from 'react';
import { SlidersHorizontal, RotateCcw, Sun, Moon, Check, Sparkles, Leaf } from 'lucide-react';
import type { Preferences } from '../contracts';
import { defaults } from '../data';
export function Appearance({ value, onChange, preview }: { value: Preferences; onChange: (p: Preferences) => void; preview?: ReactNode }) {
  const [baseline] = useState(value);
  const change = <K extends keyof Preferences>(key: K, next: Preferences[K]) => onChange({ ...value, [key]: next });
  return <div className="appearance" aria-label="外观设置">
    <section className="cp-card">
      <header className="cp-card-head"><strong>实时预览</strong><span className="cp-pill"><i/>随下方设置即时变化</span></header>
      <div className="desktop-scene"><div className="scene-grid"/>{preview}</div>
      <footer className="cp-card-foot"><span>当前 {value.width} × {value.height}px · 图标 {value.iconSize}px · 圆角 {value.radius}px</span></footer>
    </section>
    <section className="cp-card">
      <header className="cp-card-head"><strong><SlidersHorizontal size={14}/>尺寸预设</strong><button className="text-button" title="恢复默认外观" aria-label="恢复默认外观" onClick={() => onChange({ ...value, ...defaults })}><RotateCcw size={13}/>恢复默认</button></header>
      <div className="preset-switch" aria-label="尺寸预设">{[{ name: '紧凑', width: 420, height: 76, iconSize: 28 }, { name: '标准', width: 640, height: 88, iconSize: 40 }, { name: '舒展', width: 800, height: 108, iconSize: 56 }].map(preset => <button key={preset.name} className={value.width === preset.width && value.height === preset.height && value.iconSize === preset.iconSize ? 'active' : ''} onClick={() => onChange({ ...value, width: preset.width, height: preset.height, iconSize: preset.iconSize })}>{preset.name}</button>)}</div>
      <div className="sliders">{([
        ['width', '面板宽度', 320, 1120, 8], ['height', '面板高度', 64, 160, 2], ['iconSize', '图标大小', 24, 64, 2], ['radius', '圆角大小', 12, 32, 1],
      ] as const).map(([key, name, min, max, step]) => <label className="slider-control" key={key}><span>{name}<output>{value[key]} <small>px</small></output></span><input aria-label={name} type="range" min={min} max={max} step={step} value={value[key]} onChange={e => change(key, Number(e.target.value))} style={{ '--progress': `${(value[key] - min) / (max - min) * 100}%` } as React.CSSProperties}/></label>)}</div>
    </section>
    <section className="cp-card">
      <header className="cp-card-head"><strong>面板材质</strong><span className="cp-pill-caption">GLASS FINISH</span></header>
      <div className="material-options">{([{ id: 'frost', name: '强磨砂', mark: 'frost' }, { id: 'soft', name: '轻透', mark: 'soft' }, { id: 'solid', name: '实色', mark: 'solid' }] as const).map(option => <button key={option.id} className={value.material === option.id ? 'selected' : ''} onClick={() => change('material', option.id)}><span className={`material-swatch swatch-${option.mark}`}>{value.material === option.id && <Check size={14}/>}</span>{option.name}</button>)}</div>
      <div className="theme-row"><span>外观模式</span><div className="theme-options"><button aria-label="浅色模式" aria-pressed={value.theme === 'light'} className={value.theme === 'light' ? 'selected' : ''} onClick={() => change('theme', 'light')}><Sun size={15}/></button><button aria-label="深色模式" aria-pressed={value.theme === 'dark'} className={value.theme === 'dark' ? 'selected' : ''} onClick={() => change('theme', 'dark')}><Moon size={15}/></button></div></div>
      <div className="appearance-behavior">
      <label className="toggle-row"><span>自动收起<small>移开鼠标后，让出你的空间</small></span><input type="checkbox" checked={value.autoHide} onChange={e => change('autoHide', e.target.checked)}/><i/></label>
      <label className="toggle-row"><span>减少动态效果<small>保留反馈，减少位移动画</small></span><input type="checkbox" checked={value.reducedMotion} onChange={e => change('reducedMotion', e.target.checked)}/><i/></label>
      <div className="adaptive-note"><Sparkles size={15}/><span>图标与文字自动适配面板空间，更多项目收纳在「更多」中。</span></div>
      <button className="text-button undo" onClick={() => onChange(baseline)}><RotateCcw size={13}/>撤销本次调整</button>
      <div className="energy-note"><Leaf size={12}/>静止时不播放装饰动画</div>
      </div>
    </section>
  </div>;
}
