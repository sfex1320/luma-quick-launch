import type { ImportedShortcut } from '../contracts';
export function parseUrls(text: string): string[] {
  const lines = text.split(/\r?\n/).map(line => line.trim()).filter(line => line && !line.startsWith('#'));
  if (!lines.length || lines.length > 20) throw new Error('每次请拖入 1–20 个 HTTP(S) 网址');
  return [...new Set(lines.map(line => {
    if (/[\s""\\]/.test(line)) throw new Error('网址需要编码空格，不能包含引号或反斜杠');
    const url = new URL(line);
    if (!['https:', 'http:'].includes(url.protocol) || url.username || url.password || line.length > 4096 || /[\x00-\x1f]/.test(line)) throw new Error('只支持不带账户密码的 HTTP(S) 网址');
    return url.href;
  }))];
}
export const urlShortcut = (url: string): ImportedShortcut => ({ name: new URL(url).hostname.slice(0, 120), path: url, kind: 'url' });
export const hasUrlText = (transfer: Pick<DataTransfer, 'types'>) => transfer.types.includes('text/uri-list') || transfer.types.includes('text/plain');
export function referenceKey(path: string): string {
  if (/^https?:\/\//i.test(path)) { try { return new URL(path).href; } catch { return path; } }
  return path.replace(/\//g, '\\').replace(/\\+$/, '').toLowerCase();
}
