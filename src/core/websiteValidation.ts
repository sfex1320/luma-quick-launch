export function isWebsiteUrl(value: string) {
  if (!value || value.length > 4096 || /[\s"\\\x00-\x1f\x7f]/.test(value)) return false;
  try { const url = new URL(value); return ['http:', 'https:'].includes(url.protocol) && !!url.hostname && !url.username && !url.password; } catch { return false; }
}
export function isWebsiteIcon(value: string) {
  if (value.length > 87406 || !/^data:image\/png;base64,[A-Za-z0-9+/]+={0,2}$/.test(value)) return false;
  try {
    const data = atob(value.slice(22));
    if (data.length < 33 || data.length > 65536 || !data.startsWith('\x89PNG\r\n\x1a\n')) return false;
    const u32 = (i: number) => data.charCodeAt(i) * 16777216 + data.charCodeAt(i+1) * 65536 + data.charCodeAt(i+2) * 256 + data.charCodeAt(i+3);
    if (u32(8) !== 13 || data.slice(12,16) !== 'IHDR' || u32(16) < 1 || u32(16) > 256 || u32(20) < 1 || u32(20) > 256) return false;
    let offset = 8, hasData = false;
    while (offset <= data.length - 12) {
      const length = u32(offset), kind = data.slice(offset+4, offset+8);
      if (length > data.length-offset-12 || kind === 'IHDR' && offset !== 8 || ['acTL','fcTL','fdAT','iCCP','zTXt','iTXt'].includes(kind)) return false;
      if (kind === 'IDAT') hasData = true;
      if (kind === 'IEND') return length === 0 && hasData && offset + 12 === data.length;
      offset += length + 12;
    }
  } catch { return false; }
  return false;
}
