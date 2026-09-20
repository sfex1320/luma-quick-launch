// Real published Windows host + real WebView2. No injected bridge or mock launcher.
import { chromium, expect } from '@playwright/test';
import { spawn, execFileSync } from 'node:child_process';
import { mkdir, writeFile, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import net from 'node:net';
import { randomUUID, createHash } from 'node:crypto';
const root = path.resolve(import.meta.dirname, '../..');
const exe = process.env.LUMA_TEST_EXE ? path.resolve(process.env.LUMA_TEST_EXE) : path.join(root, 'APP/native/Luma/Luma.exe');
const appDirectory = path.dirname(exe);
const data = path.join(tmpdir(), `luma-native-smoke-${randomUUID()}`);
await mkdir(path.join(data, '项目 主目录', '素材'), { recursive: true });
const state = JSON.parse(await readFile(path.join(root, 'docs/contracts/empty-state.json'), 'utf8'));
state.preferences.autoHide = false;
state.projects = [{ id: 'smoke', name: '原生验证项目', description: '', color: 'mint', pinned: true,
  items: [{id:'main',name:'主目录',path:path.join(data,'项目 主目录'),kind:'folder'},
    {id:'assets',name:'素材',path:path.join(data,'项目 主目录','素材'),kind:'folder'}]}];
await writeFile(path.join(data, 'state.json'), JSON.stringify(state));
const portServer = net.createServer(); await new Promise(r => portServer.listen(0, '127.0.0.1', r));
const port = portServer.address().port; await new Promise(r => portServer.close(r));
const env = {...process.env, LUMA_DATA_DIRECTORY: data, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS:`--remote-debugging-port=${port}`};
const child = spawn(exe, ['--settings'], {env, windowsHide:true, stdio:'ignore'});
const evidence = { date:new Date().toISOString(), pid:child.pid, data, port,
  frontendManifestSha256:createHash('sha256').update(await readFile(path.join(appDirectory,'dist/index.html'))).digest('hex'),
  hostSha256:createHash('sha256').update(await readFile(path.join(appDirectory,'Luma.dll'))).digest('hex'),checks:[] };
let browser;
const inspect = () => JSON.parse(execFileSync('powershell.exe', ['-NoProfile','-ExecutionPolicy','Bypass','-File',path.join(root,'native/tests/Inspect-NativeWindow.ps1'),'-TargetProcessId',String(child.pid)], {encoding:'utf8',windowsHide:true}));
const movePointer = (x,y) => JSON.parse(execFileSync('powershell.exe', ['-NoProfile','-ExecutionPolicy','Bypass','-File',path.join(root,'native/tests/Move-NativePointer.ps1'),'-X',String(Math.round(x)),'-Y',String(Math.round(y))], {encoding:'utf8',windowsHide:true}));
const check = name => { evidence.checks.push(name); console.log(`PASS ${name}`); };
const activate = flag => new Promise((resolve,reject) => {
  const next=spawn(exe,[flag],{env,windowsHide:true,stdio:'ignore'});next.once('exit',code=>code===0?resolve():reject(new Error(`Activation exit ${code}`)));next.once('error',reject);
});
try {
  await expect.poll(async()=>{try{return (await fetch(`http://127.0.0.1:${port}/json/version`)).ok;}catch{return false;}},{timeout:20000}).toBeTruthy();
  browser=await chromium.connectOverCDP(`http://127.0.0.1:${port}`);
  const pages=()=>browser.contexts().flatMap(c=>c.pages());
  await expect.poll(()=>pages().filter(p=>p.url().includes('luma.local')).length).toBe(2);
  const dock=pages().find(p=>p.url().includes('view=dock'));
  const settings=pages().find(p=>!p.url().includes('view=dock'));
  dock.setDefaultTimeout(10000);settings.setDefaultTimeout(10000);
  await expect(settings.getByRole('heading',{name:'快捷项与堆叠',exact:true})).toBeVisible();
  await new Promise(r=>setTimeout(r,2200)); // Let preload settle: no initial animation can accidentally acknowledge reveal.
  const hotspots=inspect().filter(w=>w.Title==='Luma Hotzone');
  expect(hotspots.length).toBeGreaterThanOrEqual(2);
  evidence.hotspots=hotspots;
  evidence.activationMode=process.argv.includes('--pointer-hover')?'physical-pointer-hover':'native-hotspot-click-message';
  for (const hotspot of hotspots) {
    const x=(hotspot.Bounds.Left+hotspot.Bounds.Right)/2, y=hotspot.Bounds.Top+2;
    for (let cycle=0;cycle<3;cycle++) {
      if (process.argv.includes('--pointer-hover')) {
        movePointer(x,y+180);
        await new Promise(r=>setTimeout(r,100));
        const pointer=movePointer(x,y); evidence.lastPointer=pointer;
        expect(Math.abs(pointer.X-x)).toBeLessThan(10); expect(pointer.Y).toBeGreaterThanOrEqual(hotspot.Bounds.Top); expect(pointer.Y).toBeLessThan(hotspot.Bounds.Bottom);
      } else execFileSync('powershell.exe',['-NoProfile','-ExecutionPolicy','Bypass','-File',path.join(root,'native/tests/Invoke-HotspotClick.ps1'),'-TargetProcessId',String(child.pid),'-WindowHandle',String(hotspot.Hwnd)],{windowsHide:true});
      await expect(dock.getByRole('navigation',{name:'快捷启动面板'})).toBeVisible();
      await expect.poll(()=>inspect().find(w=>w.Title==='Luma Dock')?.RegionType).toBe(3);
      const shown=inspect().find(w=>w.Title==='Luma Dock');
      expect((shown.Bounds.Left+shown.Bounds.Right)/2).toBeCloseTo(x,0);
      await dock.getByRole('button',{name:'收起面板',exact:true}).click();
      await expect.poll(()=>inspect().find(w=>w.Title==='Luma Dock')?.Visible).toBe(false);
    }
  }
  check(`${evidence.activationMode}：冷启动与双屏各三次反复唤出、定位、收起`);
  await activate('--show-dock');
  await expect(dock.getByRole('navigation',{name:'快捷启动面板'})).toBeVisible();
  await expect(dock.getByRole('button',{name:'打开 原生验证项目 主目录，长按展开堆叠',exact:true})).toBeVisible();
  check('首次启动唤出等待真实前端首帧');
  const snapshot=inspect(); const nativeDock=snapshot.find(w=>w.Title==='Luma Dock');
  expect(nativeDock.Backdrop).toBe(1);
  expect(nativeDock.RegionType).toBe(3);
  expect(nativeDock.Region.Right-nativeDock.Region.Left).toBeLessThan(nativeDock.Bounds.Right-nativeDock.Bounds.Left);
  const metrics=await dock.evaluate(()=>({dpr:devicePixelRatio,width:innerWidth,rect:document.querySelector('.dock').getBoundingClientRect().toJSON()}));
  expect(Math.abs((nativeDock.Region.Left+nativeDock.Region.Right)/2-(nativeDock.Bounds.Right-nativeDock.Bounds.Left)/2)).toBeLessThan(2);
  expect(Math.abs(nativeDock.Region.Left-(metrics.rect.left-28)*metrics.dpr)).toBeLessThan(2);
  expect(Math.abs(nativeDock.Region.Right-(metrics.rect.right+28)*metrics.dpr)).toBeLessThan(2);
  evidence.webViewMetrics=metrics;
  expect(nativeDock.OutsideHitProcess).not.toBe(child.pid);
  evidence.expandedWindows=snapshot; check('DWM 整窗背景禁用，局部区域命中，面板外命中下层进程');
  await dock.getByRole('button',{name:'收起面板',exact:true}).click();
  await expect.poll(()=>inspect().find(w=>w.Title==='Luma Dock')?.Visible).toBe(false);
  await activate('--show-dock');
  await expect(dock.getByRole('navigation',{name:'快捷启动面板'})).toBeVisible();
  await expect.poll(()=>inspect().find(w=>w.Title==='Luma Dock')?.Visible).toBe(true);
  check('前端收起同步原生状态，重复启动参数再次唤出');
  await settings.getByRole('button',{name:'修改 原生验证项目',exact:true}).click();
  await settings.getByLabel('项目名称',{exact:true}).fill('尚未保存的草稿');
  await dock.getByRole('button',{name:'面板设置',exact:true}).click();
  await expect(settings.getByLabel('项目名称',{exact:true})).toHaveValue('尚未保存的草稿');
  await settings.getByRole('button',{name:'取消',exact:true}).click();
  await expect(settings.getByRole('heading',{name:'外观',exact:true})).toBeVisible();
  check('跨原生窗口切换栏目保留项目草稿');
  await settings.getByLabel('面板宽度',{exact:true}).fill('800');
  await expect.poll(async()=>JSON.parse(await readFile(path.join(data,'state.json'),'utf8')).preferences.width).toBe(800);
  await expect.poll(()=>dock.locator('.dock').evaluate(el=>Math.round(el.getBoundingClientRect().width))).toBe(800);
  check('真实配置落盘并同步浮岛宽度');
  await dock.getByRole('button',{name:'展开 原生验证项目 堆叠',exact:true}).click();
  await expect(dock.getByRole('region',{name:'原生验证项目 文件夹堆叠'})).toBeVisible();
  await dock.locator('[data-item-id="assets"]').click();
  await expect(dock.getByRole('status')).toContainText('已请求打开');
  await expect.poll(()=>inspect().find(w=>w.Title==='Luma Dock')?.Region.Bottom).toBeGreaterThan(500);
  check('原生启动反馈包含在实际窗口可见区域');
  const logPath=path.join(data,'logs',`host-${new Date().toLocaleDateString('sv-SE').replaceAll('-','')}.log`);
  await expect.poll(async()=>(await readFile(logPath,'utf8')).includes('shell.openItem 启动 project=smoke item=assets')).toBe(true);
  const shellInspection = `$folder=[Environment]::GetEnvironmentVariable('LUMA_SMOKE_FOLDER'); $shell=New-Object -ComObject Shell.Application; $found=@($shell.Windows() | Where-Object { $_.LocationURL -and ([uri]$_.LocationURL).LocalPath.TrimEnd('\\') -eq $folder }); Write-Output ($found.Count -gt 0)`;
  await expect.poll(()=>execFileSync('powershell.exe',['-NoProfile','-EncodedCommand',Buffer.from(shellInspection,'utf16le').toString('base64')],
    {encoding:'utf8',windowsHide:true,env:{...process.env,LUMA_SMOKE_FOLDER:path.join(data,'项目 主目录','素材')}}).trim(),{timeout:8000}).toBe('True');
  check('真实 WebView 点击经 native Shell 打开测试素材目录');
  // CDP input cannot grant Windows foreground permission. An existing window may
  // correctly return foreground=False; that is one handled attempt, not a new launch.
  const launches=async()=>{
    const log=await readFile(logPath,'utf8');
    return (log.match(/shell.openItem 启动 project=smoke/g)??[]).length
      + (log.match(/Shell 复用窗口 hwnd=\d+ foreground=False/g)??[]).length;
  };
  const before=await launches();
  const origin=await dock.getByRole('button',{name:'打开 原生验证项目 主目录，长按展开堆叠',exact:true}).boundingBox();
  await dock.mouse.move(origin.x+origin.width/2,origin.y+origin.height/2);
  await dock.mouse.down();
  await expect(dock.locator('[data-item-id="assets"]')).toBeVisible();
  await expect.poll(()=>dock.locator('.stack-panel').evaluate(el=>el.getAnimations().filter(a=>a.playState==='running').length)).toBe(0);
  const item=await dock.locator('[data-item-id="assets"]').boundingBox();
  await dock.mouse.move(item.x+item.width/2,item.y+item.height/2,{steps:5});
  await expect(dock.locator('[data-item-id="assets"]')).toHaveClass(/selected/);
  await dock.mouse.up();
  await expect.poll(launches).toBe(before+1);
  await new Promise(r=>setTimeout(r,350));
  expect(await launches()).toBe(before+1);
  const afterReleaseLog=await readFile(logPath,'utf8');
  if (afterReleaseLog.includes('foreground=False')) {
    await expect(dock.getByRole('status')).toContainText('暂未允许切到前台');
  }
  const countShellWindows=shellInspection.replace('($found.Count -gt 0)', '$found.Count');
  expect(Number(execFileSync('powershell.exe',['-NoProfile','-EncodedCommand',Buffer.from(countShellWindows,'utf16le').toString('base64')],
    {encoding:'utf8',windowsHide:true,env:{...process.env,LUMA_SMOKE_FOLDER:path.join(data,'项目 主目录','素材')}}).trim())).toBe(1);
  check('真实 WebView 长按滑选松手只处理一次；已有目录不重复创建，前置受限有反馈');
  await dock.mouse.move(origin.x+origin.width/2,origin.y+origin.height/2);
  await dock.mouse.down();
  await expect(dock.locator('[data-item-id="assets"]')).toBeVisible();
  await dock.mouse.move(20,300,{steps:5});await dock.mouse.up();
  await expect(dock.locator('[data-item-id="assets"]')).toHaveCount(0);
  expect(await launches()).toBe(before+1);
  check('长按滑出取消，不误启动主目录');
  const dropFile=path.join(data,'测试文档.txt'); await writeFile(dropFile,'Luma native drop integration fixture');
  const dropFolder=path.join(data,'拖入文件夹'); await mkdir(dropFolder);
  const cdp=await dock.context().newCDPSession(dock);
  const dropAt=async(files,x,y)=>{
    const data={items:[],files,dragOperationsMask:1};
    for(const type of ['dragEnter','dragOver','drop']) await cdp.send('Input.dispatchDragEvent',{type,x,y,data});
  };
  const dockBounds=await dock.locator('.dock').boundingBox();
  await dropAt([dropFile],dockBounds.x+dockBounds.width-5,dockBounds.y+dockBounds.height/2);
  await expect.poll(async()=>JSON.parse(await readFile(path.join(data,'state.json'),'utf8')).projects.some(p=>p.items.some(i=>i.path===dropFile&&i.kind==='file'))).toBe(true);
  const groupBounds=await dock.locator('[data-drop-project="smoke"]').boundingBox();
  await dropAt([dropFolder],groupBounds.x+groupBounds.width/2,groupBounds.y+groupBounds.height/2);
  await expect.poll(async()=>JSON.parse(await readFile(path.join(data,'state.json'),'utf8')).projects.find(p=>p.id==='smoke').items.some(i=>i.path===dropFolder&&i.kind==='folder')).toBe(true);
  expect(await launches()).toBe(before+1);
  check('真实 WebView 文件对象拖入：文档独立置顶、文件夹加入堆叠、保存不启动');
  await activate('--search');
  await expect.poll(()=>pages().some(p=>p.url().includes('view=search'))).toBe(true);
  const search=pages().find(p=>p.url().includes('view=search')); search.setDefaultTimeout(10000);
  const searchInput=search.getByRole('combobox');
  await expect(searchInput).toBeFocused();
  await search.getByRole('button',{name:'文件',exact:true}).click();
  const started=Date.now(); await searchInput.fill('Windows');
  await expect(search.getByRole('option').first()).toBeVisible();
  await expect(search.getByRole('status')).not.toContainText('不可用');
  evidence.filenameSearchMilliseconds=Date.now()-started;
  await search.getByRole('button',{name:'文件内容',exact:true}).click();
  await expect(search.getByRole('option').first()).toBeVisible();
  await expect(search.getByRole('status')).not.toContainText('不可用');
  check('独立原生搜索窗连接真实 Windows 文件名与正文索引');
  await search.getByRole('button',{name:'快捷项',exact:true}).click(); await searchInput.fill('原生验证项目');
  await expect(search.getByRole('option')).toHaveCount(3);
  await search.screenshot({path:path.join(data,'search-webview.png')});
  await search.getByRole('option').filter({hasText:'主目录'}).first().click();
  await expect.poll(()=>pages().filter(p=>p.url().includes('view=search')).length).toBe(0);
  await expect.poll(()=>inspect().some(w=>w.Title==='Luma 搜索')).toBe(false);
  await activate('--search');
  await expect.poll(()=>pages().some(p=>p.url().includes('view=search'))).toBe(true);
  await pages().find(p=>p.url().includes('view=search')).getByRole('combobox').press('Escape');
  await expect.poll(()=>pages().some(p=>p.url().includes('view=search'))).toBe(false);
  check('搜索结果可打开保存入口，独立窗口可关闭并再次唤出');
  await dock.screenshot({path:path.join(data,'dock-webview.png'),omitBackground:true});
  await settings.screenshot({path:path.join(data,'settings-webview.png')});
  await activate('--settings');
  await expect(settings.getByRole('heading',{name:'快捷项与堆叠',exact:true})).toBeVisible();
  check('已有实例正确处理 --settings 参数');
  const beforeSettingsDrop = await launches(); // Search intentionally launched the primary item above.
  // The settings WebView must receive real AdditionalObjects too; editor changes
  // remain a draft until Save, unlike the management page's automatic persistence.
  const settingsCdp = await settings.context().newCDPSession(settings);
  const settingsDrop = async (files, target) => {
    await target.scrollIntoViewIfNeeded(); const bounds = await target.boundingBox();
    const payload = { items: [], files, dragOperationsMask: 1 };
    for (const type of ['dragEnter','dragOver','drop']) await settingsCdp.send('Input.dispatchDragEvent', {
      type, x: bounds.x + bounds.width / 2, y: bounds.y + bounds.height / 2, data: payload,
    });
  };
  const directories = Array.from({length:5},(_,i)=>path.join(data,`五目录验证-${i+1}`));
  await Promise.all(directories.map(dir=>mkdir(dir)));
  await settings.getByRole('button',{name:'新建项目',exact:true}).first().click();
  await settingsDrop(directories, settings.locator('.editor-drop-guide'));
  await expect(settings.getByLabel('入口 5 路径',{exact:true})).toHaveValue(directories[4]);
  expect(JSON.parse(await readFile(path.join(data,'state.json'),'utf8')).projects.some(p=>p.items.some(i=>i.path===directories[0]))).toBe(false);
  await settings.getByLabel('项目名称',{exact:true}).fill('五个目录堆叠');
  await settings.getByRole('button',{name:'创建项目',exact:true}).click();
  await expect.poll(async()=>JSON.parse(await readFile(path.join(data,'state.json'),'utf8')).projects.find(p=>p.name==='五个目录堆叠')?.items.length).toBe(5);
  check('真实管理 WebView：批量拖入五个目录到草稿，明确保存后才落盘');
  const sixth=path.join(data,'第六个目录'); await mkdir(sixth);
  await settingsDrop([sixth], settings.locator('.project-card').filter({has:settings.getByRole('button',{name:'五个目录堆叠',exact:true})}));
  await expect.poll(async()=>JSON.parse(await readFile(path.join(data,'state.json'),'utf8')).projects.find(p=>p.name==='五个目录堆叠')?.items.length).toBe(6);
  const standalone=path.join(data,'设置页独立文档.txt'); await writeFile(standalone,'Native settings drop');
  await settingsDrop([standalone], settings.locator('.settings-drop-guide'));
  await expect.poll(async()=>JSON.parse(await readFile(path.join(data,'state.json'),'utf8')).projects.some(p=>p.items.length===1&&p.items[0].path===standalone)).toBe(true);
  expect(await launches()).toBe(beforeSettingsDrop);
  check('真实管理 WebView：卡片追加、空白独立创建、导入不启动文件');
  await settings.getByRole('navigation',{name:'驾驶舱导航'}).locator('button',{hasText:'外观'}).click();
  await settings.getByRole('checkbox',{name:/自动收起/}).check();
  await expect.poll(async()=>JSON.parse(await readFile(path.join(data,'state.json'),'utf8')).preferences.autoHide).toBe(true);
  // CDP mouse events do not move the OS cursor left at the second hotzone by
  // --pointer-hover. A real leave is required: staying at the edge must stay open.
  if (process.argv.includes('--pointer-hover')) {
    const outside = hotspots.at(-1).Bounds;
    evidence.leavePointer = movePointer(outside.Left + 20, outside.Top + 1100);
    expect(evidence.leavePointer.HitProcess).not.toBe(child.pid);
  }
  await expect.poll(()=>inspect().find(w=>w.Title==='Luma Dock')?.Visible,{timeout:5000}).toBe(false);
  check('启用自动收起后原生窗口隐藏');
  await settings.getByRole('navigation',{name:'驾驶舱导航'}).locator('button',{hasText:'总览'}).click();
  await settings.getByRole('button',{name:'独立预览',exact:true}).click();
  await expect(dock.getByRole('navigation',{name:'快捷启动面板'})).toBeVisible();
  check('管理窗独立预览唤出真实浮岛');
  evidence.result='passed';
} catch(error) { evidence.result='failed'; evidence.error=String(error.stack??error); throw error; }
finally {
  await writeFile(path.join(data,'result.json'),JSON.stringify(evidence,null,2));
  console.log(JSON.stringify(evidence,null,2));
  if(browser) await browser.close();
  if(evidence.result!=='passed'||!process.argv.includes('--keep-open')) child.kill(); else child.unref();
}
