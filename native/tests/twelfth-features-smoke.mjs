// Real delivered Luma + WebView2. Run only in an exclusive GUI acceptance window.
// Required: LUMA_TEST_EXE. Optional: LUMA_RECENT_APP (existing Photoshop exe/lnk).
// No bridge mocks, downloads, user configuration writes, or recent-document opens.
import { chromium, expect as playwrightExpect } from '@playwright/test';
import { spawn, execFileSync } from 'node:child_process';
import { mkdir, writeFile, readFile, copyFile, stat } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import net from 'node:net';
import { randomUUID, createHash } from 'node:crypto';
import { assertExecutableIdle } from './assert-executable-idle.mjs';

const root = path.resolve(import.meta.dirname, '../..');
const timeout = Math.min(30000, Math.max(1000, Number(process.env.LUMA_TEST_TIMEOUT_MS) || 30000));
const expect = playwrightExpect.configure({ timeout });
if (process.platform !== 'win32') throw new Error('This acceptance script requires Windows.');
if (!process.env.LUMA_TEST_EXE) throw new Error('Set LUMA_TEST_EXE to the delivered executable; no implicit GUI target is allowed.');
const exe = path.resolve(process.env.LUMA_TEST_EXE);
const data = path.join(tmpdir(), `luma-twelfth-native-${randomUUID()}`);
const repo = path.join(data, '代码仓库');
const recentApp = process.env.LUMA_RECENT_APP ? path.resolve(process.env.LUMA_RECENT_APP) : null;
const evidence = { date: new Date().toISOString(), result: 'pending', data, timeout, checks: [], screenshots: [], cleanup: {} };
let child, browser, env, hostIdentity, spawnError;
const ps = script => execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-EncodedCommand', Buffer.from(`
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false)
${script}`, 'utf16le').toString('base64')], {
  encoding: 'utf8', windowsHide: true, timeout,
  env: { ...process.env, LUMA_ACCEPTANCE_EXE: exe, LUMA_ACCEPTANCE_PID: String(child?.pid ?? 0), LUMA_ACCEPTANCE_REPO: repo, LUMA_ACCEPTANCE_IDENTITY: hostIdentity ?? '' },
}).trim();
const hostProcess = () => JSON.parse(ps(`
$p=Get-CimInstance Win32_Process -Filter ('ProcessId='+$env:LUMA_ACCEPTANCE_PID)
if ($p -and $p.ExecutablePath -ieq $env:LUMA_ACCEPTANCE_EXE) {
  @{ identity=$p.CreationDate.ToUniversalTime().ToString('o') } | ConvertTo-Json -Compress
} else { 'null' }`));
const assertExclusive = (allowOwned = false) => {
  const pids = JSON.parse(ps(`
$name=[IO.Path]::GetFileName($env:LUMA_ACCEPTANCE_EXE)
$found=@(Get-CimInstance Win32_Process | Where-Object {
  $_.Name -imatch '^Luma(?:[.]Host)?[.]exe$' -or $_.Name -ieq $name -or
  ($_.Name -ieq 'dotnet.exe' -and $_.CommandLine -match '(?i)Luma(?:[.]Host)?[.]dll')
} | ForEach-Object { [int]$_.ProcessId })
ConvertTo-Json -InputObject $found -Compress`));
  if (!Array.isArray(pids) || pids.some(pid => !allowOwned || pid !== child?.pid))
    throw new Error('Exclusive acceptance refused: an existing Luma process is present. No process was stopped.');
  if (allowOwned && (!pids.includes(child?.pid) || hostProcess()?.identity !== hostIdentity))
    throw new Error('The owned test host is no longer the expected process.');
};
const check = name => { evidence.checks.push(name); console.log(`PASS ${name}`); };
const activate = async flag => {
  assertExclusive(true);
  await new Promise((resolve, reject) => {
    const command = spawn(exe, [flag], { env, windowsHide: true, stdio: 'ignore' });
    const timer = setTimeout(() => { command.kill(); reject(new Error(`Activation timed out: ${flag}`)); }, timeout);
    command.once('error', error => { clearTimeout(timer); reject(error); });
    command.once('exit', code => { clearTimeout(timer); code === 0 ? resolve() : reject(new Error(`Activation failed: ${flag} (${code})`)); });
  });
};
// Only the owned host's PowerShell children whose encoded script names this exact
// temporary fixture are eligible. No name-only process termination or /T tree kill.
const closeFixtureTerminals = () => {
  if (!hostIdentity || hostProcess()?.identity !== hostIdentity) return 0;
  return Number(ps(`
$owner=Get-CimInstance Win32_Process -Filter ('ProcessId='+$env:LUMA_ACCEPTANCE_PID)
if (-not $owner -or $owner.ExecutablePath -ine $env:LUMA_ACCEPTANCE_EXE -or $owner.CreationDate.ToUniversalTime().ToString('o') -ne $env:LUMA_ACCEPTANCE_IDENTITY) { return }
$expected=Join-Path $env:SystemRoot 'System32\\WindowsPowerShell\\v1.0\\powershell.exe'
$literal="Set-Location -LiteralPath '"+$env:LUMA_ACCEPTANCE_REPO.Replace("'","''")+"'"
$closed=0
Get-CimInstance Win32_Process | Where-Object { $_.ParentProcessId -eq $owner.ProcessId -and $_.ExecutablePath -ieq $expected -and $_.CreationDate -ge $owner.CreationDate } | ForEach-Object {
  if ($_.CommandLine -match '(?i)-EncodedCommand\\s+"?([A-Za-z0-9+/=]+)') {
    $body=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($Matches[1]))
    if ($body.Contains($literal)) {
      $current=Get-CimInstance Win32_Process -Filter ('ProcessId='+$_.ProcessId)
      if ($current -and $current.CreationDate -eq $_.CreationDate -and $current.ParentProcessId -eq $owner.ProcessId) {
        Stop-Process -Id $_.ProcessId
        Wait-Process -Id $_.ProcessId -Timeout 5 -ErrorAction SilentlyContinue
        $closed++
      }
    }
  }
}
Write-Output $closed`)) || 0;
};
const exists = target => stat(target).then(() => true, () => false);
const records = () => readFile(path.join(repo, 'executions.jsonl'), 'utf8').then(text => text.trim().split(/\r?\n/).filter(Boolean).map(JSON.parse), () => []);
const manifest = scripts => writeFile(path.join(repo, 'package.json'), JSON.stringify({ name: 'luma-native-fixture', version: '1.0.0', private: true, scripts }));
const screenshot = async (page, name) => {
  const target = path.join(data, `${name}.png`);
  // Recent document names and paths stay private even in failure screenshots.
  await page.screenshot({ path: target, mask: [page.locator('.recent-list')], timeout });
  evidence.screenshots.push(target);
};

await assertExecutableIdle(exe);
assertExclusive();
await mkdir(data, { recursive: true });
try {
  await stat(exe);
  if (recentApp && (!/\.(exe|lnk)$/i.test(recentApp) || !(await exists(recentApp)))) throw new Error('LUMA_RECENT_APP must identify an existing Photoshop executable or shortcut.');
  await mkdir(path.join(repo, '一级', '二级'), { recursive: true });
  await copyFile(path.join(root, 'public/brand/luma.png'), path.join(repo, '图标预览.png'));
  await manifest({ dev: 'node verify.cjs dev' });
  await writeFile(path.join(repo, 'verify.cjs'), `const fs=require('node:fs');
fs.appendFileSync('executions.jsonl',JSON.stringify({mode:process.argv[2],cwd:process.cwd(),cargoOffline:process.env.CARGO_NET_OFFLINE??null,pid:process.pid})+'\\n');
console.log('LUMA_TWELFTH_FIXTURE_OK');\n`);
  const state = JSON.parse(await readFile(path.join(root, 'docs/contracts/empty-state.json'), 'utf8'));
  Object.assign(state.preferences, { autoHide: false, width: 640, height: 88, iconSize: 40, recentLimit: 8 });
  const folder = (id, name, folderPath = repo) => ({ id, name, path: folderPath, kind: 'folder' });
  state.projects = [
    { id: 'fixture', name: '代码仓库', description: '', color: 'mint', pinned: true, items: [folder('main', '代码仓库')] },
    { id: 'app-label', name: 'MOMO 标签夹具', description: '', color: 'blue', pinned: true, items: [{ id: 'main', name: '无害图标夹具', path: process.execPath, kind: 'app' }] },
    { id: 'group-label', name: 'Adobe 标签集合', description: '', color: 'violet', pinned: true, items: [folder('main', '一级', path.join(repo, '一级')), folder('second', '二级', path.join(repo, '一级', '二级'))] },
    { id: 'reference-label', name: 'momo智能画布', description: '', color: 'mint', pinned: true, items: [folder('main', '参考文件夹', path.join(repo, '一级'))] },
    ...(recentApp ? [{ id: 'recent', name: 'Photoshop 最近验收', description: '', color: 'blue', pinned: true, items: [{ id: 'main', name: 'Photoshop', path: recentApp, kind: 'app' }] }] : []),
  ];
  await writeFile(path.join(data, 'state.json'), JSON.stringify(state));
  evidence.hostSha256 = createHash('sha256').update(await readFile(path.join(path.dirname(exe), 'Luma.dll'))).digest('hex');
  evidence.frontendSha256 = createHash('sha256').update(await readFile(path.join(path.dirname(exe), 'dist/index.html'))).digest('hex');
  const server = net.createServer();
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
  const port = server.address().port; await new Promise(resolve => server.close(resolve));
  const inheritedCargo = process.env.CARGO_NET_OFFLINE ?? 'false';
  env = { ...process.env, CARGO_NET_OFFLINE: inheritedCargo, LUMA_DATA_DIRECTORY: data,
    PATH: `${path.dirname(process.execPath)};${process.env.PATH ?? ''}`, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${port}` };
  // Recheck immediately before the only GUI-host spawn, after fixture preparation.
  await assertExecutableIdle(exe); assertExclusive();
  child = spawn(exe, ['--settings'], { env, windowsHide: true, stdio: 'ignore' });
  child.on('error', error => { spawnError = error; });
  evidence.pid = child.pid;
  await expect.poll(() => { if (spawnError) throw spawnError; return hostProcess()?.identity ?? null; }).not.toBeNull();
  hostIdentity = hostProcess().identity;
  await expect.poll(async () => { if (spawnError) throw spawnError; try { return (await fetch(`http://127.0.0.1:${port}/json/version`, { signal: AbortSignal.timeout(1000) })).ok; } catch { return false; } }).toBe(true);
  browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`, { timeout });
  const pages = () => browser.contexts().flatMap(context => context.pages()).filter(page => page.url().includes('luma.local'));
  await expect.poll(() => pages().length).toBe(2);
  const dock = pages().find(page => page.url().includes('view=dock'));
  if (!dock) throw new Error('The real dock WebView was not found.');
  dock.setDefaultTimeout(timeout); dock.setDefaultNavigationTimeout(timeout);
  const showDock = async () => {
    await activate('--show-dock'); await expect(dock.locator('.dock')).toBeVisible();
    await expect.poll(() => dock.locator('.dock-wrap').evaluate(element => element.getAnimations().every(animation => animation.playState === 'finished'))).toBe(true);
    await expect.poll(() => dock.locator('.dock').evaluate(element => element.getBoundingClientRect().top)).toBeGreaterThanOrEqual(0);
  };
  const closeStack = async () => { const close = dock.getByRole('button', { name: '关闭堆叠', exact: true }); if (await close.count()) await close.click(); };
  const openRepo = async () => { await showDock(); await closeStack(); await dock.locator('.dock-project').filter({hasText:'代码仓库'}).press('ArrowDown'); await expect(dock.locator('.directory-footer')).toBeVisible(); };
  await showDock();
  await expect(dock.locator('.dock-label')).toHaveCount(state.projects.length);
  const labels = await dock.locator('.dock-label').evaluateAll(nodes => nodes.map(node => {
    const style = getComputedStyle(node), rect = node.getBoundingClientRect(), range = document.createRange(); range.selectNodeContents(node);
    const glyph = range.getBoundingClientRect(), rail = node.closest('.dock-launchers'), railRect = rail.getBoundingClientRect();
    return { top: rect.top, bottom: rect.bottom, height: rect.height, glyphTop: glyph.top, glyphBottom: glyph.bottom, font: style.fontSize, line: style.lineHeight,
      railBottom: railRect.top + rail.clientTop + rail.clientHeight };
  }));
  expect(Math.max(...labels.map(row => row.top)) - Math.min(...labels.map(row => row.top))).toBeLessThanOrEqual(0.5);
  for (const row of labels) {
    expect(row.font).toBe('12px'); expect(row.line).toBe('18px'); expect(row.height).toBeGreaterThanOrEqual(17.5);
    expect(row.glyphTop).toBeGreaterThanOrEqual(row.top - 0.5); expect(row.glyphBottom).toBeLessThanOrEqual(row.bottom + 0.5);
    expect(row.bottom).toBeLessThanOrEqual(row.railBottom + 0.5);
  }
  evidence.labels = labels; await screenshot(dock, 'labels'); check('real native folder/app/group labels share complete 12px/18px baselines');
  await openRepo();
  const launch = dock.getByRole('button', { name: '打开项目软件', exact: true }); await expect(launch).toBeVisible();
  const footerNames = ['打开当前目录', '打开项目软件', '新建文件夹', '复制地址'];
  const footerBoxes = [];
  for (const name of footerNames) { const button = dock.getByRole('button', { name, exact: true }); await expect(button).toBeVisible(); footerBoxes.push(await button.boundingBox()); }
  expect(Math.max(...footerBoxes.map(box => box.y)) - Math.min(...footerBoxes.map(box => box.y))).toBeLessThan(2);
  await expect(dock.locator('.directory-footer-actions')).toHaveCSS('flex-wrap', 'nowrap');
  evidence.footer = { actionCount: footerNames.length, height: (await dock.locator('.directory-footer').boundingBox()).height };
  expect(evidence.footer.height).toBeLessThanOrEqual(42);
  await screenshot(dock, 'directory-footer'); check('four directory text actions occupy one compact row');
  const thumbnail = dock.locator('.directory-row').filter({ has: dock.getByText('图标预览.png', { exact: true }) }).locator('img.file-thumbnail');
  await expect(thumbnail).toBeVisible();
  evidence.thumbnail = await thumbnail.evaluate(image => ({ width: image.naturalWidth, height: image.naturalHeight }));
  expect(evidence.thumbnail.width).toBeGreaterThan(0); check('real PNG fixture has a Windows Shell thumbnail');
  // Use the real host RPC with the DOM-issued capability, leaving the user's clipboard untouched.
  const entryId = await dock.locator('.directory-footer .directory-open-current').first().getAttribute('data-item-id');
  const copied = await dock.evaluate(({ entryId, timeout }) => new Promise((resolve, reject) => {
    const id = `r12-getpath-${crypto.randomUUID()}`, webview = window.chrome.webview;
    const finish = () => { clearTimeout(timer); webview.removeEventListener('message', listener); };
    const listener = event => { const response = event.data; if (response?.type !== 'response' || response.id !== id) return; finish(); response.ok ? resolve(response.result.path) : reject(new Error('Real folder.getPath returned an error.')); };
    const timer = setTimeout(() => { finish(); reject(new Error('Real folder.getPath timed out.')); }, timeout);
    webview.addEventListener('message', listener);
    webview.postMessage({ protocol: 1, type: 'request', id, method: 'folder.getPath', params: { projectId: 'fixture', itemId: 'main', entryId } });
  }), { entryId, timeout });
  if (String(copied).toLowerCase() !== repo.toLowerCase()) throw new Error('folder.getPath did not resolve the fixture capability.');
  check('real folder.getPath resolves only the issued folder token; clipboard preserved');
  const source = await dock.locator('.directory-row').filter({ has: dock.getByText('图标预览.png', { exact: true }) }).locator('.stack-item').boundingBox();
  await dock.mouse.move(source.x + source.width / 2, source.y + source.height / 2); await dock.mouse.down();
  await new Promise(resolve => setTimeout(resolve, 360));
  const create = await dock.getByRole('button', { name: '新建文件夹', exact: true }).boundingBox();
  await dock.mouse.move(create.x + create.width / 2, create.y + create.height / 2, { steps: 5 }); await dock.mouse.up();
  const form = dock.getByRole('form', { name: '新建实际文件夹', exact: true }); await expect(form).toBeVisible();
  const created = path.join(repo, '长按新建验收'); expect(await exists(created)).toBe(false);
  await form.getByLabel('新名称', { exact: true }).fill('长按新建验收'); await form.getByRole('button', { name: '确认新建', exact: true }).click();
  await expect.poll(() => exists(created)).toBe(true); await expect(form).toHaveCount(0);
  await expect(dock.locator('.directory-row').filter({ has: dock.getByText('长按新建验收', { exact: true }) })).toBeVisible();
  await screenshot(dock, 'created-folder'); check('held pointer slides to New Folder; disk changes only after inline confirmation');
  expect(await records()).toHaveLength(0);
  await expect(launch).toBeVisible(); await launch.click();
  await expect.poll(async () => (await records()).length).toBe(1);
  const dev = (await records())[0];
  if (dev.mode !== 'dev' || dev.cwd.toLowerCase() !== repo.toLowerCase() || dev.cargoOffline !== inheritedCargo) throw new Error('Development fixture did not preserve cwd/environment.');
  evidence.development = { executions: 1, cwdMatches: true, inheritedCargoMatches: true, inheritedCargoWasSet: process.env.CARGO_NET_OFFLINE !== undefined };
  await openRepo(); await launch.click(); await expect(dock.getByRole('alert')).toContainText('已打开');
  await new Promise(resolve => setTimeout(resolve, 500)); expect(await records()).toHaveLength(1);
  check('automatic development launch preserves cwd/CARGO_NET_OFFLINE; repeated click reuses the same terminal');
  const closed = closeFixtureTerminals(); expect(closed).toBe(1);
  await manifest({ dev: 'node verify.cjs dev', test: 'node verify.cjs test' });
  await openRepo(); const test = dock.getByRole('button', { name: '测试软件', exact: true }); await expect(test).toBeVisible(); await test.click();
  await expect.poll(async () => (await records()).length).toBe(2);
  const tested = (await records())[1];
  if (tested.mode !== 'test' || tested.cwd.toLowerCase() !== repo.toLowerCase() || tested.cargoOffline !== 'true') throw new Error('Test fixture did not preserve cwd/apply offline policy.');
  evidence.testing = { executions: 1, cwdMatches: true, cargoOffline: true };
  check('explicit test script takes precedence after re-detection and runs offline in the fixture cwd');
  closeFixtureTerminals(); await closeStack();
  if (recentApp) {
    await showDock(); await dock.locator('.dock-project').filter({hasText:'Photoshop 最近验收'}).press('ArrowDown');
    await expect.poll(() => dock.locator('.recent-list>button').count()).toBeGreaterThan(0);
    evidence.recent = await dock.locator('.recent-list>button').evaluateAll(buttons => ({ count: buttons.length, fullLocationDisplayed: buttons.every(button => {
      const location = button.querySelector('small'), style = getComputedStyle(location);
      return !!location.textContent && location.textContent === button.title && style.whiteSpace !== 'nowrap' && style.textOverflow !== 'ellipsis' && location.scrollHeight <= location.clientHeight + 1 && location.scrollWidth <= location.clientWidth + 1;
    }) }));
    expect(evidence.recent.fullLocationDisplayed).toBe(true);
    await screenshot(dock, 'photoshop-recent-redacted'); check('real Photoshop recent list is non-empty and shows full locations; no document opened');
  } else evidence.recent = { skipped: true, reason: 'LUMA_RECENT_APP was not supplied; Photoshop recent acceptance was not performed.' };
  evidence.result = 'passed';
} catch (error) {
  evidence.result = 'failed';
  // Assertions intentionally avoid document text. Still redact the optional saved app path.
  evidence.error = String(error.stack ?? error).replaceAll(recentApp ?? '\u0000', '[private app path]');
  process.exitCode = 1;
  if (browser) for (const page of browser.contexts().flatMap(context => context.pages()).filter(page => page.url().includes('view=dock'))) await screenshot(page, 'failure').catch(() => {});
} finally {
  try { evidence.cleanup.terminalsClosed = closeFixtureTerminals(); } catch { evidence.cleanup.terminalError = true; process.exitCode = 1; }
  if (browser) await browser.close().catch(() => {});
  if (child && hostIdentity) {
    try {
      if (hostProcess()?.identity === hostIdentity) {
        await activate('--shutdown');
        await expect.poll(() => hostProcess()?.identity ?? null, { timeout: 5000 }).toBeNull();
      }
      evidence.cleanup.hostStopped = hostProcess() === null;
    } catch {
      // PID is ours, and creation time + exact executable must still agree before fallback kill.
      try {
        if (hostProcess()?.identity === hostIdentity) child.kill();
        await expect.poll(() => hostProcess()?.identity ?? null, { timeout: 5000 }).toBeNull();
        evidence.cleanup.hostStopped = true;
      } catch { evidence.cleanup.hostStopped = false; }
      if (!evidence.cleanup.hostStopped) process.exitCode = 1;
    }
  } else if (child && !spawnError) {
    // Inspection failed before an identity was captured. Preserve evidence rather
    // than sending executable-wide shutdown or terminating an unverified PID.
    evidence.cleanup.hostStopped = child.exitCode !== null;
    if (!evidence.cleanup.hostStopped) { evidence.cleanup.identityUnavailable = true; process.exitCode = 1; }
  }
  if (process.exitCode && evidence.result === 'passed') evidence.result = 'failed-cleanup';
  await writeFile(path.join(data, 'result.json'), JSON.stringify(evidence, null, 2));
  console.log(JSON.stringify(evidence, null, 2));
}
