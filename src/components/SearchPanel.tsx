import { useEffect, useRef, useState } from 'react';
import { ArrowUpRight, Search, Settings2, X, LoaderCircle } from 'lucide-react';
import { nativeMode, request, subscribeHost } from '../bridge';
import type { SearchResult, SearchScope } from '../contracts';
import { ItemIcon } from './ItemIcon';

const scopes: { id: SearchScope; title: string }[] = [
  { id: 'all', title: '全部' }, { id: 'shortcuts', title: '快捷项' }, { id: 'files', title: '文件' }, { id: 'content', title: '文件内容' }, { id: 'settings', title: '设置' },
];
export function SearchPanel() {
  const [query, setQuery] = useState(''), [scope, setScope] = useState<SearchScope>('all');
  const [appAliases, setAppAliases] = useState(true), [fuzzyNames, setFuzzyNames] = useState(true);
  const [results, setResults] = useState<SearchResult[]>([]), [note, setNote] = useState(''), [error, setError] = useState('');
  const [loading, setLoading] = useState(true), [opening, setOpening] = useState(false), [selected, setSelected] = useState(0), [version, setVersion] = useState(0);
  const input = useRef<HTMLInputElement>(null), resultList = useRef<HTMLDivElement>(null), openingRef = useRef(false);
  const close = () => { if (nativeMode) void request('window.closeSearch', {}).catch(e => setError(e.message)); else window.close(); };
  useEffect(() => {
    document.title = 'Luma 搜索'; document.body.classList.add('search-body');
    const applyTheme = (theme: string) => { document.documentElement.dataset.theme = theme; };
    void request('app.getState', {}).then(s => applyTheme(s.preferences.theme)).catch(e => setError(e.message));
    const unsubscribe = subscribeHost(e => { if (e.event === 'app.stateChanged') { applyTheme(e.data.preferences.theme); setVersion(v => v + 1); } });
    const focus = () => { input.current?.focus(); input.current?.select(); setVersion(v => v + 1); };
    window.addEventListener('focus', focus);
    return () => { unsubscribe(); document.body.classList.remove('search-body'); window.removeEventListener('focus', focus); };
  }, []);
  useEffect(() => {
    let cancelled = false;
    setLoading(true); setError(''); setResults([]); setSelected(0);
    const timer = setTimeout(() => {
      void request('search.query', { query: query.trim(), scope, appAliases, fuzzyNames }).then(data => {
        if (cancelled) return;
        setResults(data.results); setNote(data.note); setLoading(false);
      }).catch(e => { if (!cancelled) { setError(e.message); setLoading(false); } });
    }, query ? 160 : 0);
    return () => { cancelled = true; clearTimeout(timer); };
  }, [query, scope, version, appAliases, fuzzyNames]);
  useEffect(() => { resultList.current?.querySelector('[aria-selected="true"]')?.scrollIntoView({ block: 'nearest' }); }, [selected]);
  const open = async (item: SearchResult) => {
    if (openingRef.current) return;
    openingRef.current = true; setOpening(true); setError('');
    try {
      const result = await request('search.open', { resultId: item.id });
      if (!result.accepted) throw new Error('系统未接受打开请求');
      if (nativeMode) close(); else setNote('浏览器演示：未访问真实文件。');
    } catch (e) { setError((e as Error).message); }
    finally { openingRef.current = false; setOpening(false); }
  };
  return <main className="search-surface" onKeyDown={e => {
    if (e.key === 'Escape') { e.preventDefault(); close(); }
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); setSelected(i => Math.max(0, Math.min(results.length - 1, i + (e.key === 'ArrowDown' ? 1 : -1)))); }
    if (e.key === 'Enter' && e.target === input.current && results[selected]) { e.preventDefault(); void open(results[selected]); }
  }}>
    <header className="search-top"><span className="search-brand">L<span>UMA</span></span><span>随时找到，直接打开</span><button className="icon-button" aria-label="关闭搜索" onClick={close}><X size={18}/></button></header>
    <div className="search-hero-input"><Search size={25}/><input ref={input} autoFocus maxLength={200} role="combobox" aria-label="搜索快捷项、文件、设置和内容" aria-controls="global-search-results" aria-expanded={results.length > 0} aria-activedescendant={results[selected] ? `result-${selected}` : undefined} placeholder="文件、软件、设置，或文档里的一句话…" value={query} onChange={e => setQuery(e.target.value)}/>{loading && <LoaderCircle size={18} className="search-spinner"/>}</div>
    <nav className="search-scopes" aria-label="搜索范围">{scopes.map(s => <button key={s.id} aria-pressed={scope === s.id} onClick={() => setScope(s.id)}>{s.title}</button>)}</nav>
    <div className="search-options"><label><input type="checkbox" checked={appAliases} onChange={e => setAppAliases(e.target.checked)}/>软件别名（AI、PS）</label><label><input type="checkbox" checked={fuzzyNames} onChange={e => setFuzzyNames(e.target.checked)}/>已保存文件 / 目录轻度模糊匹配</label></div>
    <div className="global-search-results" ref={resultList} id="global-search-results" role="listbox" aria-label="搜索结果" aria-busy={loading || opening}>
      {results.map((item, i) => <button key={item.id} id={`result-${i}`} role="option" aria-selected={i === selected} disabled={opening} onPointerMove={() => setSelected(i)} onClick={() => void open(item)}>
        {item.kind === 'setting' ? <span className="search-setting-icon"><Settings2 size={22}/></span> : <ItemIcon kind={item.kind} size={38}/>}
        <span className="search-result-label"><strong>{item.title}</strong><small>{item.subtitle}</small></span><span className="search-source">{item.source === 'shortcut' ? '快捷项' : item.source === 'settings' ? '设置' : '索引'}</span><ArrowUpRight size={16}/>
      </button>)}
      {!loading && !results.length && <p className="search-empty">{scope === 'content' && !query ? '输入文档内的关键词，搜索已索引的文件内容。' : '当前范围没有匹配结果，试试其他关键词或范围。'}</p>}
    </div>
    {error && <p className="search-error" role="alert">{error}</p>}
    <footer className="search-bottom"><p role="status">{note || '已保存的快捷项、Windows 设置与系统索引'}</p><span><kbd>↑ ↓</kbd>选择 <kbd>Enter</kbd>打开 <kbd>Esc</kbd>关闭</span></footer>
  </main>;
}
