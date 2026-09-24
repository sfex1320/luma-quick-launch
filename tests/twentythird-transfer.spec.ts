import { test, expect, type Page } from '@playwright/test';
import { defaults } from '../src/data';

async function setup(page: Page) {
  await page.addInitScript(preferences => {
    const host = window as any, listeners: any[] = [];
    host.__calls = [];
    const state = { schemaVersion: 1, revision: 1, preferences, projects: ['source', 'target'].map(id => ({id, name:id,description:'',color:'mint',pinned:true,items:[{id:'main',name:id,path:`C:\\Fixture\\${id}`,kind:'folder'}]})) };
    const handle = (req: any, files?: File[]) => {
      host.__calls.push({ ...req, files: files?.map(file => file.name) });
      const respond = (result: unknown) => setTimeout(() => listeners.forEach(callback => callback({data:{protocol:1,type:'response',id:req.id,ok:true,result}})), 1);
      if (req.method === 'app.getState') {
        respond(state); setTimeout(() => listeners.forEach(callback => callback({data:{protocol:1,type:'event',event:'window.visibility',data:{visible:true,visibilityId:1}}})), 80); return;
      }
      if (req.method === 'folder.list') { respond({folderId:`${req.params.projectId}-root`,name:req.params.projectId,parentId:null,truncated:false,entries:[{id:'child-token',name:'子目录',kind:'folder'}]}); return; }
      if (req.method === 'folder.transfer') { host.__finish = () => respond({changedCount:1,completed:true,errorCode:null,message:null}); if (!host.__hold) host.__finish(); return; }
      if (req.method === 'shell.resolveDrop') { respond([{name:'sample.txt',path:'C:\\Fixture\\sample.txt',kind:'file'}]); return; }
      if (req.method === 'app.saveState') { Object.assign(state,req.params.state,{revision:state.revision+1}); respond(state); return; }
      respond(req.method === 'window.sync' ? {applied:true} : req.method === 'project.detectTest' ? {task:null} : req.method === 'shell.getIcon' || req.method === 'folder.getThumbnail' ? {dataUrl:null} : {accepted:true});
    };
    host.chrome ||= {}; host.chrome.webview = { addEventListener: (_: string, callback: any) => listeners.push(callback), postMessage: handle, postMessageWithAdditionalObjects: handle };
  }, {...defaults,reducedMotion:true});
  await page.goto('/?view=dock&mode=native');
  await expect(page.locator('.dock')).toBeVisible();
}
async function externalDrop(page: Page, selector: string, keys: {ctrlKey?:boolean;shiftKey?:boolean;altKey?:boolean} = {}) {
  const dataTransfer = await page.evaluateHandle(() => { const data = new DataTransfer(); data.items.add(new File(['hello'],'sample.txt',{type:'text/plain'})); return data; });
  await page.locator(selector).dispatchEvent('drop',{dataTransfer,...keys});
}
const transfers = (page: Page) => page.evaluate(() => (window as any).__calls.filter((call:any)=>call.method==='folder.transfer'));

for (const [name,keys,operation,label] of [
  ['default',{},'copy','复制'],['Ctrl',{ctrlKey:true},'copy','复制'],['Shift',{shiftKey:true},'move','移动'],['Alt',{altKey:true},'link','创建快捷方式'],
] as const) test(`${name} external drop confirms correct operation with real File objects`,async({page})=>{
  await setup(page); await externalDrop(page,'.dock-slot[data-drop-project="target"]',keys);
  await expect(page.getByRole('dialog')).toBeVisible(); expect(await transfers(page)).toHaveLength(0);
  await page.getByRole('button',{name:`确认${label}`,exact:true}).click();
  await expect.poll(async()=> (await transfers(page)).length).toBe(1);
  const [call]=await transfers(page); expect(call.params).toEqual({operation,targetProject:'target',targetItem:'main',targetFolderId:'target-root'}); expect(call.files).toEqual(['sample.txt']);
  await expect(page.getByRole('status')).toContainText(`已${label}`);
});
test('folder tile and column blank target the correct issued token',async({page})=>{
  await setup(page); await page.locator('.dock-slot[data-drop-project="target"] .dock-project').press('ArrowDown');
  await expect(page.locator('.directory-row')).toBeVisible();
  await externalDrop(page,'.directory-row'); await page.getByRole('button',{name:'确认复制',exact:true}).click();
  await expect.poll(async()=> (await transfers(page))[0]?.params.targetFolderId).toBe('child-token');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await externalDrop(page,'.folder-column'); await page.getByRole('button',{name:'确认复制',exact:true}).click();
  await expect.poll(async()=> (await transfers(page))[1]?.params.targetFolderId).toBe('target-root');
});
test('blank main menu requires explicit directory choice; cancel has no writes',async({page})=>{
  await setup(page); await externalDrop(page,'.dock-tools');
  await expect(page.getByLabel('目标文件夹')).toHaveValue(''); await expect(page.getByRole('button',{name:'确认复制',exact:true})).toBeDisabled();
  await page.getByRole('button',{name:'取消',exact:true}).click(); expect(await transfers(page)).toHaveLength(0);
});
test('editing external drop only adds a reference',async({page})=>{
  await setup(page); await page.getByRole('button',{name:'整理图标',exact:true}).click(); await externalDrop(page,'.dock-slot[data-drop-project="target"]',{shiftKey:true});
  await expect.poll(()=>page.evaluate(()=>(window as any).__calls.filter((call:any)=>call.method==='shell.resolveDrop').length)).toBe(1);
  await expect(page.getByRole('dialog')).toHaveCount(0); expect(await transfers(page)).toHaveLength(0);
});
test('normal primary drag copies saved entry and keeps pending mutation alive beyond eight seconds',async({page})=>{
  await setup(page); await page.evaluate(()=>{(window as any).__hold=true;});
  const source=await page.locator('.dock-slot[data-drop-project="source"] .dock-project').boundingBox(), target=await page.locator('.dock-slot[data-drop-project="target"] .dock-project').boundingBox();
  await page.mouse.move(source!.x+20,source!.y+20); await page.mouse.down(); await page.mouse.move(target!.x+20,target!.y+20,{steps:4}); await page.mouse.up();
  await expect(page.getByRole('dialog')).toBeVisible(); await page.getByRole('button',{name:'确认复制',exact:true}).click();
  await expect.poll(async()=> (await transfers(page)).length).toBe(1);
  expect((await transfers(page))[0].params).toMatchObject({sourceProject:'source',sourceItem:'main',operation:'copy'});
  await page.waitForTimeout(8300); await expect(page.getByRole('button',{name:'正在处理…',exact:true})).toBeDisabled();
  await page.evaluate(()=>(window as any).__finish()); await expect(page.getByRole('status')).toContainText('已复制 1 项');
});

test('hovered primary split action can also be dragged without launching',async({page})=>{
  await setup(page);
  const slot=page.locator('.dock-slot[data-drop-project="source"]');
  await slot.hover(); await expect(slot.locator('.split-open')).toBeVisible();
  const source=await slot.locator('.split-open').boundingBox(), target=await page.locator('.dock-slot[data-drop-project="target"]').boundingBox();
  await page.mouse.move(source!.x+10,source!.y+10); await page.mouse.down(); await page.mouse.move(target!.x+20,target!.y+20,{steps:4}); await page.mouse.up();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByRole('button',{name:'确认复制',exact:true}).click();
  await expect.poll(async()=> (await transfers(page)).length).toBe(1);
  expect(await page.evaluate(()=>(window as any).__calls.filter((call:any)=>call.method==='shell.openItem'))).toHaveLength(0);
});
