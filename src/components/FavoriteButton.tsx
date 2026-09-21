import { Star } from 'lucide-react';
import { useState } from 'react';
export function FavoriteButton({ name, active, onToggle }: { name: string; active: boolean; onToggle: () => void | Promise<void> }) {
  const [busy, setBusy] = useState(false);
  return <button type="button" className={`favorite-button ${active ? 'is-favorite' : ''}`} aria-label={`${active ? '取消收藏' : '收藏'} ${name}`} aria-pressed={active} disabled={busy} title={active ? '取消收藏，仅移除快捷引用' : '加入收藏区'}
    onPointerDown={event => { event.preventDefault(); event.stopPropagation(); }} onDragStart={event => { event.preventDefault(); event.stopPropagation(); }} onPointerUp={event => event.stopPropagation()} onContextMenu={event => { event.preventDefault(); event.stopPropagation(); }}
    onClick={async event => { event.stopPropagation(); setBusy(true); try { await onToggle(); } finally { setBusy(false); } }}><Star size={13} fill={active ? 'currentColor' : 'none'}/></button>;
}
