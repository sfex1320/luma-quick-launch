import type { CSSProperties } from 'react';
import type { Color } from '../contracts';
export function CrystalFolder({ color = 'mint', size = 48, stack = false }: { color?: Color; size?: number; stack?: boolean }) {
  return <span aria-hidden="true" className={`crystal crystal-${color} ${stack ? 'crystal-stack' : ''}`} style={{ '--size': `${size}px` } as CSSProperties}>
    {stack && <><i className="crystal-layer layer-back"/><i className="crystal-layer layer-mid"/></>}
    <i className="folder-back"/><i className="folder-paper"/><i className="folder-front"><i className="folder-glint"/><svg viewBox="0 0 24 24"><path d="m8 13 3 3 5-7"/></svg></i>
  </span>;
}
