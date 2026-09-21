import { describe, it, expect } from 'vitest';
import { initialState } from '../data';
import { favoriteFor, toggleFavorite } from './favorites';
import { StateSchema, type LaunchItem } from '../contracts';
const item: LaunchItem = { id: 'one', name: 'Folder', path: 'C:\\Work', kind: 'folder' };
describe('favorite references', () => {
  it('makes independent saved references and cancels only the favorite', () => {
    const base = { ...initialState, projects: [{ id: 'source', name: 'Work', description: '', color: 'mint' as const, pinned: true, items: [item] }] };
    const next = toggleFavorite(base, 'Work', 'mint', [item]);
    expect(base.projects).toHaveLength(1); expect(next.projects).toHaveLength(2);
    const favorite = favoriteFor(next.projects, [{ ...item, path: 'c:/work/' }])!;
    expect(favorite.id).not.toBe('source'); expect(favorite.items[0].id).not.toBe(item.id);
    expect(StateSchema.parse(next).projects[1].favorite).toBe(true);
    expect(toggleFavorite(next, 'Work', 'mint', [item]).projects).toEqual(base.projects);
  });
  it('keeps software launch metadata and URL path case, rejects capacity atomically', () => {
    const items: LaunchItem[] = [{...item, launch: {command:'npm run dev', workingDirectory:'C:\\Work'}}, { id: 'url', name: 'Web', path:'https://example.com/Case', kind:'url' }];
    const next = toggleFavorite({...initialState,projects:[]}, 'Set', 'blue', items);
    expect(next.projects[0].items[0].launch).toEqual(items[0].launch);
    expect(favoriteFor(next.projects,[items[0],{...items[1],path:'https://example.com/case'}])).toBeUndefined();
    const full = {...next,projects:Array.from({length:100}, (_,i)=>({...next.projects[0],id:String(i),favorite:false}))};
    expect(()=>toggleFavorite(full,'Set','mint',items)).toThrow('100'); expect(full.projects).toHaveLength(100);
  });
});
