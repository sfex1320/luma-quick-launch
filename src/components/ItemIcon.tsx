import { AppWindow, FileText, Globe } from 'lucide-react';
import { useEffect, useState, useSyncExternalStore } from 'react';
import type { Color, LaunchItem } from '../contracts';
import { CrystalFolder } from './CrystalFolder';
import { getIconEpoch, loadSoftwareIcon, subscribeIconEpoch } from './software-icon-cache';
import './software-icon.css';

export function ItemIcon({ kind, color = 'mint', size = 40, stack = false, projectId, itemId, pathKey = '' }: { kind: LaunchItem['kind']; color?: Color; size?: number; stack?: boolean; projectId?: string; itemId?: string; pathKey?: string }) {
  const epoch = useSyncExternalStore(subscribeIconEpoch, getIconEpoch);
  const pixels = size * Math.min(window.devicePixelRatio || 1, 2);
  const nativeSize = pixels <= 32 ? 32 : pixels <= 48 ? 48 : pixels <= 64 ? 64 : 96;
  const key = JSON.stringify([kind, stack, projectId, itemId, pathKey, nativeSize]);
  const [icon, setIcon] = useState<{ key: string; dataUrl: string | null } | null>(null);
  useEffect(() => {
    if (!['app','url'].includes(kind) || stack || !projectId || !itemId) return;
    let current = true;
    void loadSoftwareIcon(projectId, itemId, pathKey, nativeSize).then(dataUrl => { if (current && getIconEpoch() === epoch) setIcon({ key, dataUrl }); });
    return () => { current = false; };
  }, [kind, stack, projectId, itemId, pathKey, nativeSize, key, epoch]);
  if (kind === 'folder' || stack) return <CrystalFolder color={color} size={size} stack={stack}/>;
  if (icon?.key === key && icon.dataUrl) return <span aria-hidden="true" className={`native-software-icon dot-${color}`} style={{ width: size, height: size, borderRadius: size * .26 }}><img src={icon.dataUrl} alt="" draggable={false} onError={() => setIcon({ key, dataUrl: null })}/></span>;
  const Icon = kind === 'app' ? AppWindow : kind === 'url' ? Globe : FileText;
  return <span aria-hidden="true" className={`crystal-item crystal-item-${kind} dot-${color}`} style={{ width: size, height: size, borderRadius: size * .26 }}><Icon size={size * .56} strokeWidth={1.5}/></span>;
}
