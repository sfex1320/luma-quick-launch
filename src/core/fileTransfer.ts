export const SAVED_ENTRY_MIME = 'application/x-luma-entry';
export const FILE_ENTRY_MIME = 'application/x-luma-real-entry';
export type TransferOperation = 'copy' | 'move' | 'link';
export function transferOperation(keys: { altKey: boolean; shiftKey: boolean; ctrlKey?: boolean }): TransferOperation {
  return keys.altKey ? 'link' : keys.shiftKey ? 'move' : 'copy';
}
export const transferLabel = { copy: '复制', move: '移动', link: '创建快捷方式' } as const;
export type InternalSource = { projectId: string; itemId: string; entryId?: string; name?: string };
export function readTransferSource(text: string, real: boolean): InternalSource | null {
  if (text.length > 1800) return null;
  try {
    const value = JSON.parse(text);
    if (!value || typeof value !== 'object' || Array.isArray(value)) return null;
    const fields = real ? ['projectId', 'itemId', 'entryId'] : ['projectId', 'itemId'];
    if (fields.some(key => typeof value[key] !== 'string' || !value[key].trim() || value[key].length > 200)) return null;
    return { projectId: value.projectId, itemId: value.itemId, ...(real ? { entryId: value.entryId } : {}),
      ...(real && typeof value.name === 'string' && value.name.length <= 255 ? { name: value.name } : {}) };
  } catch { return null; }
}
