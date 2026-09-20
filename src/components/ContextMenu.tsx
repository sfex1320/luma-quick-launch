import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
export type MenuAction = { label: string; action: () => void; danger?: boolean; disabled?: boolean };
export function ContextMenu({ x, y, actions, onClose }: { x: number; y: number; actions: MenuAction[]; onClose: () => void }) {
  const element = useRef<HTMLDivElement>(null), [position, setPosition] = useState({ x, y });
  const closeRef = useRef(onClose); closeRef.current = onClose;
  useLayoutEffect(() => { const r = element.current?.getBoundingClientRect(); if (r) setPosition({ x: Math.max(8, Math.min(x, innerWidth - r.width - 8)), y: Math.max(8, Math.min(y, innerHeight - r.height - 8)) }); }, [x, y]);
  useEffect(() => {
    window.dispatchEvent(new CustomEvent('luma:context-menu', { detail: { open: true } }));
    const close = (e: Event) => { if (!element.current?.contains(e.target as Node)) closeRef.current(); };
    const key = (e: KeyboardEvent) => { if (e.key === 'Escape') { e.stopPropagation(); closeRef.current(); } };
    const blur = () => closeRef.current();
    document.addEventListener('pointerdown', close); window.addEventListener('keydown', key, true); window.addEventListener('blur', blur);
    element.current?.querySelector<HTMLButtonElement>('button')?.focus({ preventScroll: true });
    return () => { window.dispatchEvent(new CustomEvent('luma:context-menu', { detail: { open: false } })); document.removeEventListener('pointerdown', close); window.removeEventListener('keydown', key, true); window.removeEventListener('blur', blur); };
  }, []);
  return createPortal(<div ref={element} data-native-hit role="menu" aria-label="快捷操作" className="luma-context-menu glass" style={{ left: position.x, top: position.y }} onContextMenu={event => event.preventDefault()} onKeyDown={event => {
    if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return;
    event.preventDefault(); const buttons = [...event.currentTarget.querySelectorAll<HTMLButtonElement>('button:not(:disabled)')]; const index = buttons.indexOf(document.activeElement as HTMLButtonElement); buttons[(index + (event.key === 'ArrowDown' ? 1 : -1) + buttons.length) % buttons.length]?.focus();
  }}>{actions.map(entry => <button key={entry.label} role="menuitem" className={entry.danger ? 'danger' : ''} disabled={entry.disabled} onClick={() => { onClose(); entry.action(); }}>{entry.label}</button>)}</div>, document.getElementById('root')!);
}
