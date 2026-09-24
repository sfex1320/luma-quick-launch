// Final EXE, real WebView AdditionalObjects and temporary files. No foreground activation or physical input.
import { chromium, expect } from '@playwright/test';
import { spawn, execFileSync } from 'node:child_process';
import { mkdtemp, mkdir, writeFile, readFile, access } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import net from 'node:net';
import assert from 'node:assert/strict';
import { assertExecutableIdle } from './assert-executable-idle.mjs';
const root = path.resolve(import.meta.dirname, '../..');
if (!process.env.LUMA_TEST_EXE) throw new Error('Use an isolated final EXE');
const exe = path.resolve(process.env.LUMA_TEST_EXE);
assert.notEqual(exe.toLowerCase(), path.join(root,'APP/Luma/Luma.exe').toLowerCase());
assertExecutableIdle(exe);
const data = await mkdtemp(path.join(tmpdir(),'luma-transfer-exe-'));
const source = path.join(data,'source'), target = path.join(data,'target');
await mkdir(source); await mkdir(target);
const state = JSON.parse(await readFile(path.join(root,'docs/contracts/empty-state.json'),'utf8'));
state.projects = [{id:'p',name:'target',description:'',color:'mint',pinned:true,items:[{id:'target',name:'target',path:target,kind:'folder'},{id:'app',name:'unsupported',path:exe,kind:'app'}]}];
await writeFile(path.join(data,'state.json'),JSON.stringify(state));
for (const operation of ['copy','move','link']) await writeFile(path.join(source,`${operation}.txt`),operation);
const socket = net.createServer(); await new Promise(done=>socket.listen(0,'127.0.0.1',done)); const port=socket.address().port; await new Promise(done=>socket.close(done));
const env={...process.env,LUMA_DATA_DIRECTORY:data,WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS:`--remote-debugging-port=${port}`};
const host=spawn(exe,['--startup'],{env,windowsHide:true,stdio:'ignore'});
const evidence={exe,data,checks:[],result:'pending'};
let browser;
const inspect=()=>JSON.parse(execFileSync('powershell.exe',['-NoProfile','-File',path.join(root,'native/tests/Inspect-NativeWindow.ps1'),'-TargetProcessId',String(host.pid)],{encoding:'utf8',windowsHide:true}));
try {
  await expect.poll(async()=>{try{return (await fetch(`http://127.0.0.1:${port}/json/version`)).ok;}catch{return false;}},{timeout:20000}).toBe(true);
  browser=await chromium.connectOverCDP(`http://127.0.0.1:${port}`);
  const pages=()=>browser.contexts().flatMap(context=>context.pages());
  await expect.poll(()=>pages().some(page=>page.url().includes('view=dock'))).toBe(true);
  const page=pages().find(page=>page.url().includes('view=dock'));
  page.setDefaultTimeout(10000);
  // Navigation resumes a suspended WebView without revealing its HWND.
  await page.goto('https://luma.local/index.html?view=dock&mode=native');
  await page.evaluate(()=>{
    window.__rpc=(method,params,files)=>new Promise((resolve,reject)=>{
      const id=crypto.randomUUID(), timer=setTimeout(()=>reject(new Error('Native transfer response timeout')),15000);
      const callback=event=>{const message=event.data;if(message.id!==id)return;clearTimeout(timer);window.chrome.webview.removeEventListener('message',callback);message.ok?resolve(message.result):reject(new Error(message.error.message));};
      window.chrome.webview.addEventListener('message',callback);
      const message={protocol:1,type:'request',id,method,params};
      if(files)window.chrome.webview.postMessageWithAdditionalObjects(message,files);else window.chrome.webview.postMessage(message);
    });
    const input=document.createElement('input');input.type='file';input.id='transfer-fixture';document.body.append(input);
  });
  const cdp=await page.context().newCDPSession(page);
  const document=await cdp.send('DOM.getDocument');
  const {nodeId}=await cdp.send('DOM.querySelector',{nodeId:document.root.nodeId,selector:'#transfer-fixture'});
  for(const operation of ['copy','move','link']) {
    const file=path.join(source,`${operation}.txt`);
    // CDP points at actual on-disk files; Playwright's remote setInputFiles can
    // substitute memory-backed File objects, which WebView rightly rejects.
    await cdp.send('DOM.setFileInputFiles',{nodeId,files:[file]});
    const result=await page.evaluate(async operation=>{
      const listing=await window.__rpc('folder.list',{projectId:'p',itemId:'target'});
      return window.__rpc('folder.transfer',{operation,targetProject:'p',targetItem:'target',targetFolderId:listing.folderId},Array.from(document.querySelector('#transfer-fixture').files));
    },operation);
    assert.equal(result.completed,true);assert.equal(result.changedCount,1);
    if(operation==='link') { const link=await readFile(path.join(target,'link.txt.lnk'));assert.equal(link.readUInt32LE(0),0x4c);await access(file); }
    else {assert.equal(await readFile(path.join(target,`${operation}.txt`),'utf8'),operation);if(operation==='move')await assert.rejects(access(file));else await access(file);}
    evidence.checks.push(`${operation}: real AdditionalObjects committed and verified on disk`);
  }
  assert.equal((await page.evaluate(()=>window.__rpc('shell.getAppCapabilities',{projectId:'p',itemId:'app'}))).recentSupported,false);
  evidence.checks.push('Unsupported saved executable has no recent capability');
  assert(!inspect().some(window=>window.Title==='Luma Dock'&&window.Visible&&window.RegionType!==1));
  evidence.checks.push('Dock remained hidden; no foreground or physical input actions');
  evidence.result='passed';
} catch(error) { evidence.error=String(error);throw error; }
finally {
  if(host.exitCode===null) {const shutdown=spawn(exe,['--shutdown'],{env,windowsHide:true,stdio:'ignore'});await new Promise(done=>shutdown.once('exit',done));await Promise.race([new Promise(done=>host.once('exit',done)),new Promise(done=>setTimeout(done,3000))]);if(host.exitCode===null)host.kill();}
  await browser?.close().catch(()=>{});
  await writeFile(path.join(data,'result.json'),JSON.stringify(evidence,null,2));console.log(JSON.stringify(evidence,null,2));
}
