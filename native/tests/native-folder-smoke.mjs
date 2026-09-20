// Published Windows host, real directory API and saved state. No bridge mocks.
import { chromium, expect } from '@playwright/test';
import { spawn, execFileSync } from 'node:child_process';
import { mkdir, writeFile, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import net from 'node:net';
import { randomUUID, createHash } from 'node:crypto';
const root=path.resolve(import.meta.dirname,'../..'), exe=path.join(root,'APP/native/Luma/Luma.exe');
const data=path.join(tmpdir(),`luma-folder-smoke-${randomUUID()}`), fixture=path.join(data,'目录验证');
await mkdir(path.join(fixture,'子目录一','下一层'),{recursive:true}); await mkdir(path.join(fixture,'子目录二'));
await writeFile(path.join(fixture,'说明.txt'),'fixture');
const userConfig=path.join(process.env.LOCALAPPDATA,'Luma/state.json');
const hash=bytes=>createHash('sha256').update(bytes).digest('hex');
const userBefore=await readFile(userConfig), userState=JSON.parse(userBefore), actual=userState.projects.find(p=>p.name==='电梯贴');
const fifth=process.argv.includes('--fifth');
const actualApp=fifth?userState.projects.find(p=>p.items.some(i=>i.kind==='app'&&/momo/i.test(i.name))):undefined;
const state=JSON.parse(await readFile(path.join(root,'docs/contracts/empty-state.json'),'utf8'));
state.preferences.autoHide=false; state.preferences.width=800;
state.projects=[{id:'folder',name:'目录验证',description:'',color:'mint',pinned:true,items:[{id:'root',name:'目录验证',kind:'folder',path:fixture}]},
 {id:'second',name:'另一图标',description:'',color:'blue',pinned:true,items:[{id:'second-root',name:'另一图标',kind:'folder',path:path.join(fixture,'子目录二')}]},
 ...(actual?[actual]:[]),...(actualApp&&actualApp.id!==actual?.id?[actualApp]:[])];
await writeFile(path.join(data,'state.json'),JSON.stringify(state));
const server=net.createServer();await new Promise(r=>server.listen(0,'127.0.0.1',r));const port=server.address().port;await new Promise(r=>server.close(r));
const env={...process.env,LUMA_DATA_DIRECTORY:data,WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS:`--remote-debugging-port=${port}`};
const child=spawn(exe,['--show-dock'],{env,windowsHide:true,stdio:'ignore'});
const evidence={date:new Date().toISOString(),data,pid:child.pid,hostSha256:hash(await readFile(path.join(root,'APP/native/Luma/Luma.dll'))),frontendSha256:hash(await readFile(path.join(root,'APP/native/Luma/dist/index.html'))),checks:[]};
let browser;
const check=s=>{evidence.checks.push(s);console.log(`PASS ${s}`);};
try{
 await expect.poll(async()=>{try{return(await fetch(`http://127.0.0.1:${port}/json/version`)).ok}catch{return false}},{timeout:20000}).toBe(true);
 browser=await chromium.connectOverCDP(`http://127.0.0.1:${port}`);
 await expect.poll(()=>browser.contexts().flatMap(c=>c.pages()).some(p=>p.url().includes('view=dock'))).toBe(true);
 const dock=browser.contexts().flatMap(c=>c.pages()).find(p=>p.url().includes('view=dock'));dock.setDefaultTimeout(10000);
 const windows=JSON.parse(execFileSync('powershell.exe',['-NoProfile','-ExecutionPolicy','Bypass','-File',path.join(root,'native/tests/Inspect-NativeWindow.ps1'),'-TargetProcessId',String(child.pid)],{encoding:'utf8',windowsHide:true}));
 const hotspot=windows.find(window=>window.Title==='Luma Hotzone'&&window.Visible);
 if(hotspot) execFileSync('powershell.exe',['-NoProfile','-ExecutionPolicy','Bypass','-File',path.join(root,'native/tests/Invoke-HotspotClick.ps1'),'-TargetProcessId',String(child.pid),'-WindowHandle',String(hotspot.Hwnd)],{windowsHide:true});
 await expect(dock.getByRole('navigation',{name:'快捷启动面板'})).toBeVisible();
 await expect.poll(()=>dock.locator('.dock-wrap').evaluate(el=>el.getAnimations().every(a=>a.playState==='finished'))).toBe(true);
 await dock.evaluate(()=>{window.__gestureTrace=[];for(const type of ['pointerdown','pointerup','pointercancel','lostpointercapture','blur'])window.addEventListener(type,e=>window.__gestureTrace.push({type,target:e.target?.className,time:performance.now()}),true)});
 const hold=async locator=>{const b=await locator.boundingBox();await dock.mouse.move(b.x+b.width/2,b.y+b.height/2);await dock.mouse.down();await expect(dock.locator('.stack-panel')).toBeVisible();await dock.mouse.up();};
 await hold(dock.getByRole('button',{name:'打开 目录验证 主目录，长按展开堆叠',exact:true}));
 await expect(dock.locator('.directory-row')).toHaveCount(3);
 await expect(dock.locator('.directory-items')).toContainText('子目录一');await expect(dock.locator('.directory-items')).toContainText('说明.txt');
 check('真实 WebView 长按单文件夹，列出两个子目录及文件');
 if(fifth){
  evidence.tileBounds=await dock.locator('.directory-row').evaluateAll(nodes=>nodes.map(node=>{const r=node.getBoundingClientRect();return {width:r.width,height:r.height}}));
  for(const tile of evidence.tileBounds){expect(Math.abs(tile.width-88)).toBeLessThan(1);expect(Math.abs(tile.height-94)).toBeLessThan(1)}
  evidence.toolbar=await dock.evaluate(()=>{const bar=document.querySelector('.dock').getBoundingClientRect(),tools=document.querySelector('.dock-tools').getBoundingClientRect();return {rightInset:bar.right-tools.right,width:tools.width}});
  expect(Math.abs(evidence.toolbar.rightInset-17)).toBeLessThan(1);check('真实宿主固定格子88×94与右侧固定工具区');
 }

 await dock.getByRole('button',{name:'浏览 子目录一 子目录',exact:true}).click();
 await expect(dock.locator('.directory-items')).toContainText('下一层');
 await dock.getByRole('button',{name:'返回上一层',exact:true}).click();await expect(dock.locator('.directory-row')).toHaveCount(3);
 check('目录逐层浏览与返回，无递归扫描');
 const logFile=path.join(data,'logs',`host-${new Date().toLocaleDateString('sv-SE').replaceAll('-','')}.log`);
 const launches=async()=>((await readFile(logFile,'utf8')).match(/folder.open 启动/g)||[]).length;
 let initialLaunches=await launches();
 if(fifth){
  await dock.getByRole('button',{name:'关闭堆叠',exact:true}).click();
  const button=dock.getByRole('button',{name:'打开 目录验证 主目录，长按展开堆叠',exact:true}), b=await button.boundingBox();
  await dock.mouse.move(b.x+b.width/2,b.y+b.height/2);await dock.mouse.down();
  const action=dock.getByRole('button',{name:'打开当前目录',exact:true});await expect(action).toBeVisible();
  const a=await action.boundingBox();await dock.mouse.move(a.x+a.width/2,a.y+a.height/2,{steps:8});await expect(action).toHaveClass(/selected/);expect(await launches()).toBe(initialLaunches);
  await dock.mouse.move(5,600);await dock.mouse.up();expect(await launches()).toBe(initialLaunches);
  await hold(button);
 }
 const file=dock.locator('.directory-row').filter({hasText:'说明.txt'}).locator('.stack-item'), b=await file.boundingBox();
 await dock.mouse.move(b.x+50,b.y+15);await dock.mouse.down();await new Promise(r=>setTimeout(r,340));await dock.mouse.move(5,600);await dock.mouse.up();
 expect(await launches()).toBe(initialLaunches);await expect(dock.locator('.stack-panel')).toHaveCount(0);
 check('真实目录条目长按后移出松手不启动');
 if(fifth){
   const rootButton=await dock.getByRole('button',{name:'打开 目录验证 主目录，长按展开堆叠',exact:true}).boundingBox();
   await dock.mouse.move(rootButton.x+rootButton.width/2,rootButton.y+rootButton.height/2);await dock.mouse.down();
   const action=dock.getByRole('button',{name:'打开当前目录',exact:true});await expect(action).toBeVisible();
   const point=await action.boundingBox();await dock.mouse.move(point.x+point.width/2,point.y+point.height/2,{steps:8});await expect(action).toHaveClass(/selected/);await dock.mouse.up();
 }else{
   await hold(dock.getByRole('button',{name:'打开 目录验证 主目录，长按展开堆叠',exact:true}));
   await dock.getByRole('button',{name:'浏览 子目录二 子目录',exact:true}).click();
   await expect(dock.getByText('此目录为空',{exact:true})).toBeVisible();
   await dock.getByRole('button',{name:'打开当前目录',exact:true}).click();
 }
 await expect.poll(launches).toBe(initialLaunches+1);
 const shellProbe = "$ProgressPreference='SilentlyContinue'; $folder=$env:LUMA_FOLDER_TEST; $shell=New-Object -ComObject Shell.Application; $found=@($shell.Windows() | Where-Object { $_.LocationURL -and ([uri]$_.LocationURL).LocalPath.TrimEnd('\\') -eq $folder }); Write-Output ($found.Count -gt 0); $found | ForEach-Object { $_.Quit() }";
 await expect.poll(()=>execFileSync('powershell.exe',['-NoProfile','-EncodedCommand',Buffer.from(shellProbe,'utf16le').toString('base64')],{encoding:'utf8',windowsHide:true,env:{...process.env,LUMA_FOLDER_TEST:fifth?fixture:path.join(fixture,'子目录二')}}).trim()).toBe('True');
 initialLaunches=await launches();check('真实 folder.open 令牌经 Windows Shell 打开临时目录，验后关闭测试窗口');
 if(actual){
  await hold(dock.getByRole('button',{name:'打开 电梯贴 主目录，长按展开堆叠',exact:true}));
  await expect(dock.locator('.directory-row')).toHaveCount(3);
  evidence.actualFolderEntries=await dock.locator('.directory-row .stack-item strong').allTextContents();
  expect(evidence.actualFolderEntries).toEqual(['2张电梯延展画面','电梯门原文件','20260907综合楼电梯门贴膜需求.pptx']);
  await dock.screenshot({path:path.join(data,'actual-folder.png'),omitBackground:true});
  await dock.getByRole('button',{name:'关闭堆叠',exact:true}).click();
  check('电梯贴实目录读取验证：两个子文件夹 + PPTX，只读且未打开文件');
 }
 await dock.getByRole('button',{name:'整理图标',exact:true}).click();
 const source=dock.locator('[data-drop-project="second"] .dock-project'),target=dock.locator('[data-drop-project="folder"] .dock-project');
 const a=await source.boundingBox(),t=await target.boundingBox();
 await dock.mouse.move(a.x+a.width/2,a.y+a.height/2);await dock.mouse.down();await dock.mouse.move(t.x+t.width/2,t.y+t.height/2,{steps:12});await dock.mouse.up();
 await expect.poll(async()=>JSON.parse(await readFile(path.join(data,'state.json'),'utf8')).projects.find(p=>p.id==='folder').items.length).toBe(2);
 await expect(dock.locator('[data-drop-project="folder"] .group-icon-count')).toHaveText('2');
 expect(await launches()).toBe(initialLaunches);
 await dock.getByRole('button',{name:'完成整理',exact:true}).click();
 await hold(dock.locator('[data-drop-project="folder"] .dock-project'));
 await expect(dock.locator('.group-entry-row')).toHaveCount(2);
 await dock.getByRole('button',{name:'拆成独立图标',exact:true}).click();
 await expect.poll(async()=>JSON.parse(await readFile(path.join(data,'state.json'),'utf8')).projects.filter(p=>p.items.some(i=>i.path.startsWith(fixture))).every(p=>p.items.length===1)).toBe(true);
 check('原生配置 CAS 落盘：图标拖叠组合、组内完整列表、拆分；操作不启动');
 if(fifth&&actualApp){
   const app=dock.locator(`[data-drop-project="${actualApp.id}"] img`).first();await expect(app).toBeVisible();
   evidence.appIcon=await app.evaluate(img=>({width:img.naturalWidth,height:img.naturalHeight,png:img.src.startsWith('data:image/png;base64,')}));
   expect(evidence.appIcon.png).toBe(true);expect(evidence.appIcon.width).toBeGreaterThanOrEqual(32);
   await dock.screenshot({path:path.join(data,'software-icon.png'),omitBackground:true});check('真实MOMO快捷方式提取原始PNG图标并在浮岛显示');
 }

 expect(hash(await readFile(userConfig))).toBe(hash(userBefore));check('实际用户配置完整保留');
 evidence.result='passed';
}catch(error){evidence.result='failed';evidence.error=String(error.stack??error);const dock=browser?.contexts().flatMap(c=>c.pages()).find(p=>p.url().includes('view=dock'));if(dock){evidence.gestureTrace=await dock.evaluate(()=>window.__gestureTrace);await dock.screenshot({path:path.join(data,'failure.png')});}throw error;}
finally{await writeFile(path.join(data,'result.json'),JSON.stringify(evidence,null,2));console.log(JSON.stringify(evidence,null,2));if(browser)await browser.close();child.kill();}
