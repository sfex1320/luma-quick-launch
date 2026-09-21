// Real Windows/WebView2 acceptance. Explicit LUMA_TEST_EXE only; never invoke against a running user copy.
// Authoring/static validation does not run this script. The operator must reserve an exclusive non-fullscreen GUI window.
import { chromium, expect as playwrightExpect } from '@playwright/test';
import { spawn, execFileSync } from 'node:child_process';
import { mkdir, readFile, writeFile, copyFile, rename, readdir, stat } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import net from 'node:net';
import { randomUUID, createHash } from 'node:crypto';
import { assertExecutableIdle } from './assert-executable-idle.mjs';

if (process.platform !== 'win32' || !process.env.LUMA_TEST_EXE) throw new Error('Windows and explicit LUMA_TEST_EXE are required. No default executable is allowed.');
const root = path.resolve(import.meta.dirname, '../..');
const exe = path.resolve(process.env.LUMA_TEST_EXE);
const timeout = 30000;
const panelOnly = process.env.LUMA_SHORTCUT_SMOKE_SCOPE === 'panel';
const expectedLaunches = panelOnly ? 1 : 2;
const expect = playwrightExpect.configure({ timeout });
const data = path.join(tmpdir(), `luma-thirteenth-hotkeys-${randomUUID()}`);
const externalDir = path.join(data, 'external');
const targetDir = path.join(data, 'target');
const externalExe = path.join(externalDir, 'ShortcutInputProbe.exe');
const targetExe = path.join(targetDir, 'ShortcutLaunchTarget.exe');
const evidence = { date: new Date().toISOString(), exe, data, result: 'pending', scope: panelOnly ? 'panel-recording' : 'full', checks: [], cleanup: {} };
let child, externalChild, host, external, browser, env;
const targets = new Map();
const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
const check = name => { evidence.checks.push(name); console.log(`PASS ${name}`); };
const ps = (body, values = {}) => execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-EncodedCommand', Buffer.from(`
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
${body}`, 'utf16le').toString('base64')], { encoding: 'utf8', timeout, windowsHide: true, env: { ...process.env, ...values } }).trim();
const input = payload => JSON.parse(execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-File', path.join(root, 'native/tests/Invoke-ShortcutInput.ps1'),
  '-PayloadBase64', Buffer.from(JSON.stringify(payload)).toString('base64')], { encoding: 'utf8', timeout, windowsHide: true }));
const identity = (pid, expectedExe) => JSON.parse(ps(`
$p=Get-Process -Id ([int]$env:LUMA_PROBE_PID) -ErrorAction SilentlyContinue
if(-not $p){'null';exit}
if($p.Path -ine $env:LUMA_PROBE_EXE){throw 'Process path mismatch'}
@{pid=$p.Id;exe=$p.Path;identity=$p.StartTime.ToUniversalTime().Ticks.ToString()}|ConvertTo-Json -Compress`, { LUMA_PROBE_PID: String(pid), LUMA_PROBE_EXE: expectedExe }));
const assertExclusive = (owned = false) => {
  const found = JSON.parse(ps(`
$name=[IO.Path]::GetFileName($env:LUMA_PROBE_EXE)
$found=@(Get-CimInstance Win32_Process|Where-Object{$_.Name -imatch '^Luma(?:[.]Host)?[.]exe$' -or $_.Name -ieq $name -or ($_.Name -ieq 'dotnet.exe' -and $_.CommandLine -match '(?i)Luma(?:[.]Host)?[.]dll')}|ForEach-Object{[int]$_.ProcessId})
ConvertTo-Json -InputObject $found -Compress`, { LUMA_PROBE_EXE: exe }));
  if (!Array.isArray(found) || found.some(pid => !owned || pid !== host?.pid)) throw new Error('Another Luma process exists; no input or shutdown was sent.');
  if (owned && (!found.includes(host.pid) || identity(host.pid, exe)?.identity !== host.identity)) throw new Error('Owned host identity is no longer valid.');
};
const safe = () => { if (input({ op: 'safety' }).fullscreen) throw new Error('Fullscreen foreground detected; GUI acceptance refused.'); };
const inputOwnership = () => {
  const current = input({ op: 'safety' });
  if (current.fullscreen) throw new Error('Fullscreen foreground detected; GUI acceptance refused.');
  const owned = [host?.pid, external?.pid, ...targets.keys()];
  if (!owned.includes(current.foregroundPid)) throw new Error(`Foreground belongs to another application (${current.foregroundPid}); stop physical acceptance without refocusing.`);
};
const inspect = owner => input({ op: 'inspect', ...owner });
const windowOf = (owner, title) => {
  const result = inspect(owner);
  const window = result.windows.find(w => w.visible && (title ? w.title === title : w.title.startsWith('Luma shortcut fixture')));
  if (!window) throw new Error(`Owned visible window not found: ${title ?? owner.exe}`);
  return window;
};
const focus = (owner, title) => { inputOwnership(); const window = windowOf(owner, title); input({ op: 'focus', ...owner, hwnd: window.hwnd }); return window; };
const keys = (owner, key, modifiers = 0, repeats = 1, title) => {
  assertExclusive(true); inputOwnership(); const window = windowOf(owner, title);
  input({ op: owner === external ? 'fixtureKeys' : 'keys', ...owner, hwnd: window.hwnd, key, modifiers, repeats });
};
const probe = (key, modifiers = 0) => input({ op: 'probe', key, modifiers }).registered;
const readRows = async directory => (await readFile(path.join(directory, 'launches.jsonl'), 'utf8').catch(() => '')).trim().split(/\r?\n/).filter(Boolean).map(JSON.parse);
const readEvents = async () => (await readFile(path.join(externalDir, 'events.jsonl'), 'utf8').catch(() => '')).trim().split(/\r?\n/).filter(Boolean).map(JSON.parse);
const command = async (directory, op, args = {}) => {
  const serial = randomUUID(); const temp = path.join(directory, 'command.tmp');
  await writeFile(temp, JSON.stringify({ serial, op, ...args })); await rename(temp, path.join(directory, 'command.json'));
  const reply = path.join(directory, `reply-${serial}.json`);
  await expect.poll(() => readFile(reply, 'utf8').then(JSON.parse, () => null)).not.toBeNull();
  const response = JSON.parse(await readFile(reply, 'utf8')); if (!response.ok) throw new Error(`Fixture command failed: ${op}`);
};
const captureTargets = async () => {
  for (const row of await readRows(targetDir)) if (!targets.has(row.pid)) {
    const current = identity(row.pid, targetExe); if (current) targets.set(row.pid, current);
  }
};
const closeOwned = async owner => {
  if (!owner) return;
  ps(`
$p=Get-Process -Id ([int]$env:LUMA_PROBE_PID) -ErrorAction SilentlyContinue
if(-not $p){exit}
if($p.Path -ine $env:LUMA_PROBE_EXE -or $p.StartTime.ToUniversalTime().Ticks.ToString() -ne $env:LUMA_PROBE_IDENTITY){throw 'Cleanup identity mismatch'}
$null=$p.CloseMainWindow()
if(-not $p.WaitForExit(3000)){
 $again=Get-Process -Id $p.Id -ErrorAction SilentlyContinue
 if($again -and $again.Path -ieq $env:LUMA_PROBE_EXE -and $again.StartTime.ToUniversalTime().Ticks.ToString() -eq $env:LUMA_PROBE_IDENTITY){Stop-Process -Id $again.Id}
}`, { LUMA_PROBE_PID: String(owner.pid), LUMA_PROBE_EXE: owner.exe, LUMA_PROBE_IDENTITY: owner.identity });
  await expect.poll(() => identity(owner.pid, owner.exe)).toBeNull();
};
const activate = async flag => {
  assertExclusive(true); safe();
  await new Promise((resolve, reject) => {
    const command = spawn(exe, [flag], { env, windowsHide: true, stdio: 'ignore' });
    const timer = setTimeout(() => reject(new Error(`Activation timed out: ${flag}`)), timeout);
    command.once('error', error => { clearTimeout(timer); reject(error); });
    command.once('exit', code => { clearTimeout(timer); code === 0 ? resolve() : reject(new Error(`Activation exited ${code}`)); });
  });
};
// Real native RPC is used only for configuration/status. No shortcut.execute, fake events, or synthetic DOM keyboard events.
const rpc = (page, method, parameters = {}) => page.evaluate(({ method, parameters, timeout }) => new Promise((resolve, reject) => {
  const id = `r13-${crypto.randomUUID()}`, webview = window.chrome.webview;
  const done = () => { clearTimeout(timer); webview.removeEventListener('message', listener); };
  const listener = event => { const value = event.data; if (value?.type !== 'response' || value.id !== id) return; done(); value.ok ? resolve(value.result) : reject(new Error(JSON.stringify(value.error))); };
  const timer = setTimeout(() => { done(); reject(new Error(`${method} timed out`)); }, timeout);
  webview.addEventListener('message', listener); webview.postMessage({ protocol: 1, type: 'request', id, method, params: parameters });
}), { method, parameters, timeout });
const logs = async () => {
  const names = await readdir(path.join(data, 'logs')).catch(() => []);
  return (await Promise.all(names.filter(name => /^host-.*\.log$/.test(name)).map(name => readFile(path.join(data, 'logs', name), 'utf8')))).join('\n');
};
const physicalClick = async (page, locator, title, fraction = { x: 0.5, y: 0.5 }) => {
  assertExclusive(true); inputOwnership(); await locator.scrollIntoViewIfNeeded(); const box = await locator.boundingBox();
  if (!box) throw new Error('No visible click target');
  const ratio = await page.evaluate(() => window.devicePixelRatio); const window = windowOf(host, title);
  input({ op: 'click', ...host, hwnd: window.hwnd, x: Math.round(window.origin.X + (box.x + box.width * fraction.x) * ratio), y: Math.round(window.origin.Y + (box.y + box.height * fraction.y) * ratio) });
};

await assertExecutableIdle(exe); assertExclusive(); safe();
await mkdir(externalDir, { recursive: true }); await mkdir(targetDir, { recursive: true });
try {
  await stat(exe);
  ps(`Add-Type -TypeDefinition (Get-Content -LiteralPath $env:LUMA_PROBE_SOURCE -Raw) -ReferencedAssemblies System.Windows.Forms,System.Drawing,System.Web.Extensions -OutputAssembly $env:LUMA_PROBE_OUTPUT -OutputType WindowsApplication`,
    { LUMA_PROBE_SOURCE: path.join(root, 'native/tests/ShortcutInputProbe.cs'), LUMA_PROBE_OUTPUT: externalExe });
  await copyFile(externalExe, targetExe);
  await mkdir(path.join(data, 'group-a')); await mkdir(path.join(data, 'group-b'));
  const state = JSON.parse(await readFile(path.join(root, 'docs/contracts/empty-state.json'), 'utf8'));
  Object.assign(state.preferences, { autoHide: false, shortcuts: [
    { id: 'global-item', code: 'KeyG', ctrl: true, shift: true, alt: true, scope: 'global', action: 'item', projectId: 'launcher', itemId: 'app' },
    { id: 'panel-item', code: 'F8', ctrl: false, shift: false, alt: false, scope: 'panel', action: 'item', projectId: 'launcher', itemId: 'app' },
    { id: 'global-project', code: 'KeyP', ctrl: true, shift: true, alt: true, scope: 'global', action: 'project', projectId: 'group' },
    { id: 'panel-edit', code: 'F9', ctrl: false, shift: false, alt: false, scope: 'panel', action: 'edit' },
  ] });
  state.projects = [
    { id: 'launcher', name: '快捷键测试软件', description: '', color: 'mint', pinned: true, items: [{ id: 'app', name: '独立测试窗口', path: targetExe, kind: 'app' }] },
    { id: 'group', name: '快捷键分组', description: '', color: 'blue', pinned: true, items: ['a', 'b'].map(id => ({ id, name: `测试目录 ${id}`, path: path.join(data, `group-${id}`), kind: 'folder' })) },
  ];
  await writeFile(path.join(data, 'state.json'), JSON.stringify(state));
  evidence.hostSha256 = createHash('sha256').update(await readFile(path.join(path.dirname(exe), 'Luma.dll'))).digest('hex');
  evidence.frontendSha256 = createHash('sha256').update(await readFile(path.join(path.dirname(exe), 'dist/index.html'))).digest('hex');
  assertExclusive(); safe();
  externalChild = spawn(externalExe, [], { windowsHide: false, stdio: 'ignore' });
  externalChild.on('error', error => { evidence.fixtureSpawnError = String(error); });
  await expect.poll(() => identity(externalChild.pid, externalExe)).not.toBeNull(); external = identity(externalChild.pid, externalExe);
  await expect.poll(() => { const snapshot = inspect(external); evidence.fixtureWindowSnapshot = snapshot; return snapshot.windows.some(w => w.visible && w.title.startsWith('Luma shortcut fixture')); }).toBe(true);
  await command(externalDir, 'hold', { key: 0x47, modifiers: 7 });
  const server = net.createServer(); await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
  const port = server.address().port; await new Promise(resolve => server.close(resolve));
  env = { ...process.env, LUMA_DATA_DIRECTORY: data, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${port}` };
  await assertExecutableIdle(exe); assertExclusive(); safe();
  child = spawn(exe, ['--settings'], { env, windowsHide: true, stdio: 'ignore' });
  child.on('error', error => { evidence.hostSpawnError = String(error); });
  await expect.poll(() => identity(child.pid, exe)).not.toBeNull(); host = identity(child.pid, exe); evidence.pid = host.pid;
  await expect.poll(async () => { try { return (await fetch(`http://127.0.0.1:${port}/json/version`, { signal: AbortSignal.timeout(1000) })).ok; } catch { return false; } }).toBe(true);
  browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`, { timeout });
  const pages = () => browser.contexts().flatMap(context => context.pages()).filter(page => page.url().includes('luma.local'));
  await expect.poll(() => pages().length).toBe(2);
  const dock = pages().find(page => page.url().includes('view=dock')); const settings = pages().find(page => !page.url().includes('view=dock'));
  if (!dock || !settings) throw new Error('Real dock/settings WebViews were not found.');
  const status = async id => (await rpc(settings, 'shortcut.getStatus')).bindings.find(binding => binding.id === id);
  const save = async edit => { const current = await rpc(settings, 'app.getState'); edit(current); await rpc(settings, 'app.saveState', { state: current, expectedRevision: current.revision }); };
  await expect.poll(async () => (await status('global-item'))?.registered).toBe(false);
  expect((await status('global-item')).message).toMatch(/占用|失败/);
  evidence.occupiedStatus = await status('global-item');
  expect(probe(0x77)).toBe(true);
  await command(externalDir, 'release'); await save(() => {});
  await expect.poll(async () => (await status('global-item'))?.registered).toBe(true);
  check('real RegisterHotKey occupation is visible; release plus save re-registers; panel F8 is not globally registered');

  if (!panelOnly) {
  keys(external, 0x47, 7, 10);
  await expect.poll(async () => (await readRows(targetDir)).length).toBe(1); await captureTargets();
  const firstTarget = [...targets.values()].at(-1);
  await expect.poll(() => inspect(firstTarget).foregroundPid).toBe(firstTarget.pid);
  await wait(350); keys(external, 0x47, 7);
  await expect.poll(() => inspect(firstTarget).foregroundPid).toBe(firstTarget.pid); expect(await readRows(targetDir)).toHaveLength(1);
  await closeOwned(firstTarget);
  const beforeKeys = (await readEvents()).filter(event => event.type === 'key' && (event.code === 0x51 || event.code === 229)).length;
  keys(external, 0x51, 0, 3); await wait(500);
  expect((await readEvents()).filter(event => event.type === 'key' && (event.code === 0x51 || event.code === 229)).length).toBeGreaterThan(beforeKeys);
  expect(await readRows(targetDir)).toHaveLength(1);
  evidence.externalKeyEvents = await readEvents();
  keys(external, 0x77); await wait(400); expect(await readRows(targetDir)).toHaveLength(1);
  check('physical global chord launches one dedicated process and reuses its window; external Q/IME input and panel-bound F8 do not launch an app');

  await activate('--show-dock'); await expect(dock.locator('.dock')).toBeVisible();
  await dock.evaluate(() => { window.__r13Activations = []; window.chrome.webview.addEventListener('message', event => { if (event.data?.type === 'event' && event.data.event === 'shortcut.activated') window.__r13Activations.push(event.data.data); }); });
  const suspendCount = async () => ((await logs()).match(/浮岛 WebView 已暂停/g) ?? []).length;
  const collapse = async () => {
    const before = await suspendCount(); await physicalClick(dock, dock.getByRole('button', { name: '收起面板', exact: true }), 'Luma Dock');
    await expect.poll(suspendCount).toBeGreaterThan(before);
  };
  await collapse(); keys(external, 0x50, 7, 10);
  await expect(dock.getByRole('region', { name: '快捷键分组 文件夹堆叠', exact: true })).toBeVisible();
  await wait(400);
  await expect.poll(async () => { evidence.activations = await dock.evaluate(() => window.__r13Activations); return evidence.activations.length; }).toBe(1);
  const firstActivation = await dock.evaluate(() => window.__r13Activations[0]); expect(firstActivation.id).toBe('global-project');
  await collapse(); keys(external, 0x50, 7);
  await expect(dock.getByRole('region', { name: '快捷键分组 文件夹堆叠', exact: true })).toBeVisible();
  const activations = await dock.evaluate(() => window.__r13Activations); expect(activations).toHaveLength(2); expect(activations[1].serial).toBeGreaterThan(activations[0].serial);
  evidence.activations = activations;
  check('physical global project chord wakes a confirmed suspended dock, opens saved group, emits one activation per held chord with increasing serials');

  } else {
    await activate('--show-dock'); await expect(dock.locator('.dock')).toBeVisible();
    await dock.evaluate(() => { window.__r13Activations=[]; window.chrome.webview.addEventListener('message', e=>{if(e.data?.event==='shortcut.activated')window.__r13Activations.push(e.data.data);}); });
    // Set up the submenu; shortcut acceptance below uses physical input only.
    await dock.locator('.dock-project').filter({hasText:'快捷键分组'}).press('ArrowDown');
  }
  await physicalClick(dock, dock.locator('.stack-heading'), 'Luma Dock');
  await expect.poll(() => inspect(host).foregroundPid).toBe(host.pid);
  expect(await dock.evaluate(() => document.hasFocus() && !!document.activeElement?.closest('.dock-container'))).toBe(true);
  await dock.evaluate(() => { window.__r13KeyEvents = []; window.addEventListener('keydown', e => window.__r13KeyEvents.push({ key:e.key, code:e.code, repeat:e.repeat, composing:e.isComposing, focused:document.hasFocus(), target:e.target?.tagName }), true); });
  keys(host, 0x78, 0, 10, 'Luma Dock'); await expect(dock.locator('.dock')).toHaveClass(/dock-editing/); await wait(400);
  expect(await dock.evaluate(() => window.__r13Activations.filter(event => event.id === 'panel-edit').length)).toBe(1);
  check('physical focused panel F9 held down toggles edit once with one native activation');
  await wait(350); keys(host, 0x78, 0, 1, 'Luma Dock'); await expect(dock.locator('.dock')).not.toHaveClass(/dock-editing/);
  keys(host, 0x77, 0, 1, 'Luma Dock');
  await expect.poll(async () => (await readRows(targetDir)).length).toBe(expectedLaunches); await captureTargets();
  const secondTarget = [...targets.values()].at(-1); await expect.poll(() => inspect(secondTarget).foregroundPid).toBe(secondTarget.pid);
  check('physical click grants real dock/document focus; held panel F9 toggles once and panel F8 launches one new test instance');

  const settingsTitle = inspect(host).windows.find(w => w.title.includes('设置'))?.title;
  if (!settingsTitle) throw new Error('Settings HWND not found.');
  focus(host, settingsTitle); await physicalClick(settings, settings.getByRole('button', { name: '设置与备份', exact: true }), settingsTitle);
  const recorder = settings.getByRole('textbox', { name: '录制快捷键', exact: true });
  await physicalClick(settings, recorder, settingsTitle); await expect(recorder).toHaveValue('请按下快捷键…');
  await expect.poll(async () => (await status('global-item'))?.registered).toBe(false);
  evidence.recordingStatus = await status('global-item');
  expect(probe(0x47, 7)).toBe(true);
  keys(host, 0x47, 7, 1, settingsTitle); await expect(recorder).toHaveValue(/G/);
  await expect.poll(async () => (await status('global-item'))?.registered).toBe(true);
  expect(inspect(host).foregroundPid).toBe(host.pid); expect(await readRows(targetDir)).toHaveLength(expectedLaunches);
  check('actual recorder focus releases global registration; recording its chord does not activate target; blur restores registration');

  await save(current => { current.preferences.shortcuts.find(binding => binding.id === 'global-item').code = 'KeyH'; });
  await expect.poll(async () => (await status('global-item'))?.registered).toBe(true);
  expect(probe(0x47, 7)).toBe(true); expect(probe(0x48, 7)).toBe(false);
  if (!panelOnly) {
  keys(external, 0x47, 7); await wait(500); expect(inspect(external).foregroundPid).toBe(external.pid);
  keys(external, 0x48, 7); await expect.poll(() => inspect(secondTarget).foregroundPid).toBe(secondTarget.pid); expect(await readRows(targetDir)).toHaveLength(expectedLaunches);
  check('saved rebind releases old G chord; old physical chord stays in external textbox and new H chord reuses the saved target');
  } else check('saved rebind releases G and registers H via real OS availability probes');

  await browser.close(); browser = null; await activate('--shutdown');
  await expect.poll(() => identity(host.pid, exe)).toBeNull(); evidence.cleanup.hostStopped = true;
  expect(probe(0x48, 7)).toBe(true); expect(probe(0x50, 7)).toBe(true); expect(probe(0x77)).toBe(true);
  check('graceful exit releases all test global registrations'); evidence.result = 'passed';
} catch (error) {
  if(browser) { const page=browser.contexts().flatMap(c=>c.pages()).find(p=>p.url().includes('view=dock')); if(page) evidence.keyEvents=await page.evaluate(()=>window.__r13KeyEvents).catch(()=>null); }
  evidence.result = 'failed'; evidence.error = String(error.stack ?? error); process.exitCode = 1;
} finally {
  if (browser) await browser.close().catch(() => {});
  const cleanup = async (name, work) => { try { await work(); } catch (error) { evidence.cleanup[`${name}Error`] = String(error); process.exitCode = 1; } };
  await cleanup('targetInspection', captureTargets);
  await cleanup('host', async () => {
    if (host && identity(host.pid, exe)?.identity === host.identity) {
      try { await activate('--shutdown'); await expect.poll(() => identity(host.pid, exe)).toBeNull(); }
      catch { await closeOwned(host); }
    }
    evidence.cleanup.hostStopped = host ? identity(host.pid, exe) === null : !child || child.exitCode !== null;
  });
  for (const target of targets.values()) await cleanup(`target-${target.pid}`, () => closeOwned(target));
  if (external) await cleanup('fixture', () => closeOwned(external));
  await cleanup('verify', async () => {
    evidence.cleanup.fixtureStopped = external ? identity(external.pid, externalExe) === null : !externalChild || externalChild.exitCode !== null;
    evidence.cleanup.targetsStopped = [...targets.values()].every(target => identity(target.pid, targetExe) === null);
  });
  if ((!host && child?.exitCode === null) || !evidence.cleanup.fixtureStopped || !evidence.cleanup.targetsStopped) { evidence.cleanup.incomplete = true; process.exitCode = 1; }
  if (process.exitCode && evidence.result === 'passed') evidence.result = 'failed-cleanup';
  await writeFile(path.join(data, 'result.json'), JSON.stringify(evidence, null, 2)); console.log(JSON.stringify(evidence, null, 2));
}
