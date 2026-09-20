import { useEffect, useRef, useState } from 'react';
import type { Color, FolderEntry } from '../contracts';
import { ItemIcon } from './ItemIcon';
import { loadFolderThumbnail } from './folder-thumbnail-cache';
import './folder-thumbnail.css';

export function FolderEntryIcon({ projectId, itemId, entry, color, size = 38 }: { projectId: string; itemId: string; entry: FolderEntry; color: Color; size?: number }) {
  const container = useRef<HTMLSpanElement>(null);
  const [image, setImage] = useState<string | null>(null);
  useEffect(() => {
    const element = container.current;
    if (!element || entry.kind !== 'file') return;
    let generation = 0, visible = false;
    const observer = new IntersectionObserver(records => {
      const intersects = records.some(record => record.isIntersecting);
      if (intersects === visible) return;
      visible = intersects; const current = ++generation;
      if (!visible) { setImage(null); return; }
      void loadFolderThumbnail(projectId, itemId, entry.id).then(result => { if (generation === current) setImage(result); });
    });
    setImage(null); observer.observe(element);
    return () => { generation++; observer.disconnect(); };
  }, [projectId, itemId, entry.id, entry]);
  return <span ref={container} className="folder-entry-icon" style={{ width: size, height: size }}>
    {image ? <img className="file-thumbnail" src={image} alt="" draggable={false} onError={() => setImage(null)}/> : <ItemIcon kind={entry.kind} color={color} size={size}/>}
  </span>;
}
