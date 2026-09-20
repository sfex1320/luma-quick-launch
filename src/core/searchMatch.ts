const aliases: Record<string, string> = { ai: 'illustrator', ps: 'photoshop', ae: 'afterfx', pr: 'premiere', id: 'indesign', cdr: 'coreldrw' };
export function shortcutMatches(query: string, name: string, path: string, kind: string, appAliases = true, fuzzyNames = false) {
  const q = query.toLowerCase().trim(), text = `${name} ${path}`.toLowerCase();
  if (text.includes(q)) return true;
  if (appAliases && kind === 'app' && aliases[q] && text.includes(aliases[q])) return true;
  if (!fuzzyNames || !['folder','file'].includes(kind)) return false;
  const value = name.toLowerCase(); const words = q.split(/\s+/);
  if (words.length > 1 && words.every(word => value.includes(word))) return true;
  const stem = value.replace(/\.[^.]+$/, '');
  if (q.length < 5 || Math.abs(stem.length - q.length) > 1) return false;
  let a = 0, b = 0, edits = 0;
  while (a < q.length && b < stem.length) {
    if (q[a] === stem[b]) { a++; b++; continue; }
    if (++edits > 1) return false;
    if (q.length === stem.length && q[a] === stem[b+1] && q[a+1] === stem[b]) { a += 2; b += 2; }
    else if (q.length > stem.length) a++;
    else if (q.length < stem.length) b++;
    else { a++; b++; }
  }
  return edits + (q.length - a) + (stem.length - b) <= 1;
}
