import { test, expect, type Page } from '@playwright/test';
import { defaults } from '../src/data';

async function setup(page: Page) {
  await page.addInitScript(preferences => {
    const w = window as any, listeners: any[] = []; w.__calls = []; w.__serial = 0;
    let state: any = {schemaVersion:1,revision:1,preferences,projects:[
      {id:'root',name:'工作',color:'mint',pinned:true,description:'',items:[{id:'folder',name:'工作',kind:'folder',path:'C:\\Work'}]},
      {id:'apps',name:'应用组',color:'mint',pinned:true,description:'',items:[{id:'app',name:'编辑器',kind:'app',path:'C:\\Apps\\Editor.exe'},{id:'web',name:'网站',kind:'url',path:'https://example.com/Docs'}]},
    ]};
    const respond = (req: any, result: any) => listeners.forEach(fn => fn({data:{protocol:1,type:'response',id:req.id,ok:true,result}}));
    w.__emitVisible = (visible: boolean) => listeners.forEach(fn => fn({data:{protocol:1,type:'event',event:'window.visibility',data:{visible,visibilityId:++w.__serial}}}));
    w.__state = () => state;
    w.__remote = (next: any) => { state = next; listeners.forEach(fn => fn({data:{protocol:1,type:'event',event:'app.stateChanged',data:state}})); };
    w.chrome ||= {}; w.chrome.webview = {addEventListener:(_:string, fn:any)=>listeners.push(fn), postMessage(req:any) {
      w.__calls.push(req); let result:any = {accepted:true};
      if(req.method==='app.getState') result=state;
      if(req.method==='app.saveState') { state={...req.params.state,revision:state.revision+1}; result=state; }
      if(req.method==='window.sync') result={applied:true};
      if(req.method==='shell.getIcon'||req.method==='folder.getThumbnail') result={dataUrl:null};
      if(req.method==='project.detectTest') result={task:null};
      if(req.method==='folder.getPath') result={path:req.params.entryId==='child'?'C:\\Work\\子目录':'C:\\Work\\设计.psd'};
      if(req.method==='folder.list') result={folderId:'root-token',name:'工作',parentId:null,truncated:false,entries:[{id:'child',name:'子目录',kind:'folder'},{id:'file',name:'设计.psd',kind:'file'}]};
      if(req.method==='shell.getRecent') result={entries:[{id:'recent',name:'最近.psd',path:'C:\\Design\\最近.psd',kind:'file'}],note:''};
      setTimeout(()=>respond(req,result),5);
    }};
  }, {...defaults,autoHide:false});
  await page.goto('/?view=dock&mode=native');
  await expect.poll(()=>page.evaluate(()=>(window as any).__calls.some((c:any)=>c.method==='window.sync'))).toBe(true);
  await page.evaluate(()=>(window as any).__emitVisible(true));
  await expect(page.locator('.dock')).toBeVisible();
  await expect.poll(()=>page.locator('.dock-wrap').evaluate(el=>el.getAnimations().every(a=>a.playState==='finished'))).toBe(true);
}
async function saved(page: Page, favorites: number) {
  await expect.poll(()=>page.evaluate(()=>(window as any).__state().projects.filter((p:any)=>p.favorite).length)).toBe(favorites);
}
const calls = (page:Page) => page.evaluate(()=>(window as any).__calls.filter((c:any)=>/^(shell\.open|folder\.(open|move|rename|createFolder)|project\.run)/.test(c.method)));

test('stars make a separate tinted collection; cancel changes references only and sync preserves it', async({page})=>{
  await setup(page);
  const root = page.locator('.dock-launchers > [data-drop-project="root"]');
  await root.getByRole('button',{name:'收藏 工作',exact:true}).click(); await saved(page,1);
  await expect(page.getByRole('separator',{name:'收藏分隔线'})).toBeVisible();
  const favorite = page.getByRole('group',{name:'收藏区'});
  await expect(favorite.locator('.dock-slot')).toHaveCount(1);
  expect(await calls(page)).toHaveLength(0);
  await page.screenshot({path:'docs/evidence/fourteenth-favorites-preview.png'});
  await page.evaluate(()=>{const w=window as any; const s=structuredClone(w.__state()); s.revision++; s.preferences.theme='dark';w.__remote(s);});
  await expect(favorite).toBeVisible(); await expect(root.getByRole('button',{name:'取消收藏 工作',exact:true})).toHaveAttribute('aria-pressed','true');
  await favorite.getByRole('button',{name:'取消收藏 工作',exact:true}).click();await saved(page,0);
  await expect(root).toBeVisible(); expect(await calls(page)).toHaveLength(0);
});

test('automatic width includes the divided favorite frame before the width cap',async({page})=>{
  await setup(page);
  await page.evaluate(()=>{ const w=window as any, s=structuredClone(w.__state()); s.revision++;
    s.projects=Array.from({length:7},(_,i)=>({...s.projects[0],id:`p${i}`,name:`入口${i}`,favorite:i>=4,items:[{...s.projects[0].items[0],id:`i${i}`,path:`C:\\Test\\${i}`}]}));w.__remote(s); });
  await expect(page.locator('.dock-slot')).toHaveCount(7);
  await expect.poll(()=>page.locator('.dock').evaluate(el=>el.dataset.widthAnimating??'')).toBe('');
  const sizes=await page.locator('.dock-launchers').evaluate(el=>({width:el.clientWidth,scroll:el.scrollWidth}));
  expect(sizes.scroll).toBeLessThanOrEqual(sizes.width+1);
});

test('directory files, subfolders, group apps, URLs and recent files can all be favorited',async({page})=>{
  await setup(page);
  await page.locator('[data-drop-project="root"] .dock-project').press('ArrowDown');
  await page.getByRole('button',{name:'收藏 子目录',exact:true}).click();await saved(page,1);
  await page.getByRole('button',{name:'收藏 设计.psd',exact:true}).click();await saved(page,2);
  await page.locator('[data-drop-project="apps"] .dock-project').press('ArrowDown');
  await page.getByRole('button',{name:'收藏 网站',exact:true}).click();await saved(page,3);
  await page.getByRole('button',{name:'收藏 编辑器',exact:true}).click();await saved(page,4);
  await page.locator('.group-column [data-item-id="app"]').first().hover();
  await page.getByRole('button',{name:'查看 编辑器 最近项目',exact:true}).click();
  await page.getByRole('button',{name:'收藏 最近.psd',exact:true}).click();await saved(page,5);
  expect(await calls(page)).toHaveLength(0);
  const kinds = await page.evaluate(()=>(window as any).__state().projects.filter((p:any)=>p.favorite).map((p:any)=>p.items[0].kind));
  expect(kinds).toEqual(['folder','file','url','app','file']);
});

test('favorite launch uses its saved identity and collapses; wake restores previous menu',async({page})=>{
  await setup(page); await page.locator('[data-drop-project="root"] .dock-project').press('ArrowDown');
  await page.getByRole('button',{name:'收藏 设计.psd',exact:true}).click();await saved(page,1);
  const favorite = page.getByRole('group',{name:'收藏区'});
  await favorite.locator('.dock-project').click();
  await expect(page.locator('.dock')).toHaveCount(0);
  expect(await calls(page)).toMatchObject([{method:'shell.openItem',params:{projectId:expect.any(String),itemId:expect.any(String)}}]);
  await page.evaluate(()=>(window as any).__emitVisible(true));
  await expect(page.getByLabel('工作 文件夹堆叠', {exact:true})).toBeVisible();
  await expect(page.getByRole('button',{name:'取消收藏 设计.psd',exact:true}).first()).toBeVisible();
});
