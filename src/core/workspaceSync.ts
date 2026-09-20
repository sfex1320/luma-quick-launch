import { StateSchema, type AppState } from '../contracts';

export function fingerprint(value: unknown): string {
  if (Array.isArray(value)) return `[${value.map(fingerprint).join(',')}]`;
  if (value && typeof value === 'object') return `{${Object.entries(value).filter(([key]) => key !== 'revision').sort(([a], [b]) => a.localeCompare(b)).map(([key, child]) => `${JSON.stringify(key)}:${fingerprint(child)}`).join(',')}}`;
  return JSON.stringify(value);
}
/** Merge independent fields; conflicting edits never silently overwrite the other window. */
export function reconcile(base: AppState, local: AppState, remote: AppState): AppState {
  const merge = (a: any, b: any, c: any): any => {
    if (fingerprint(a) === fingerprint(b)) return c;
    if (fingerprint(a) === fingerprint(c) || fingerprint(b) === fingerprint(c)) return b;
    if (Array.isArray(a) && Array.isArray(b) && Array.isArray(c)) {
      const ids = (list: any[]) => list.map(x => x.id);
      const order = merge(ids(a).join('\0'), ids(b).join('\0'), ids(c).join('\0')).split('\0').filter(Boolean);
      return order.map((id: string) => merge(a.find(x => x.id === id), b.find(x => x.id === id), c.find(x => x.id === id)));
    }
    if (a && b && c && typeof a === 'object' && typeof b === 'object' && typeof c === 'object')
      return Object.fromEntries([...new Set([...Object.keys(a), ...Object.keys(b), ...Object.keys(c)])].map(key => [key, key === 'revision' ? c[key] : merge(a[key], b[key], c[key])]));
    throw new Error('同一入口已在另一窗口编辑，请重新载入后再调整；未覆盖另一窗口的配置。');
  };
  return StateSchema.parse({ ...merge(base, local, remote), revision: remote.revision });
}
