// Real delivered host + owned WebView2 only. No mock bridge, physical input,
// application/document launch, dependency installation, or user configuration.
// Run only during an exclusive acceptance window with LUMA_TEST_EXE explicit.
import { chromium, expect as playwrightExpect } from '@playwright/test';
import { spawn, execFileSync } from 'node:child_process';
import { mkdir, writeFile, readFile, readdir, stat } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import net from 'node:net';
import { randomUUID, createHash } from 'node:crypto';
import { assertExecutableIdle } from './assert-executable-idle.mjs';

const root = path.resolve(import.meta.dirname, '../..');
const timeout = Math.min(30000, Math.max(1000, Number(process.env.LUMA_TEST_TIMEOUT_MS) || 30000));
const expect = playwrightExpect.configure({ timeout });
if (process.platform !== 'win32') throw new Error('Windows is required.');
if (!process.env.LUMA_TEST_EXE) throw new Error('LUMA_TEST_EXE must identify the exclusive test executable.');
const exe = path.resolve(process.env.LUMA_TEST_EXE);
const protectedExecutables = [path.join(root, 'APP/Luma/Luma.exe'),
  ...(process.env.LOCALAPPDATA ? [path.join(process.env.LOCALAPPDATA, 'Programs/Luma/Luma.exe')] : [])];
if (protectedExecutables.some(target => path.resolve(target).toLowerCase() === exe.toLowerCase()))
  throw new Error('Use an internal delivery-validation copy, not the user portable or installed executable.');
const data = path.join(tmpdir(), `luma-fourteenth-native-${randomUUID()}`);
const fixture = path.join(data, '目录夹具');
const nested = path.join(fixture, '子目录');
const statePath = path.join(data, 'state.json');
const evidence = { date: new Date().toISOString(), result: 'pending', exe, data, timeout, checks: [], screenshots: [], hosts: [], cleanup: {}, limits: [
  'CDP DOM events do not validate physical mouse, keyboard, OLE or Windows message ordering.',
  'Short resource snapshots do not establish five-hour stability or the 50–150 MB target.',
  'Unexpired native folder capabilities may be reused; reacquisition is checked through real listing responses, not forced token expiry.',
] };
let child, hostIdentity, spawnError, browser, env, port;
const ps = script => execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-EncodedCommand', Buffer.from(`
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false)
${script}`, 'utf16le').toString('base64')], { encoding: 'utf8', windowsHide: true, timeout,
  env: { ...process.env, LUMA_ACCEPTANCE_EXE: exe, LUMA_ACCEPTANCE_PID: String(child?.pid ?? 0), LUMA_ACCEPTANCE_IDENTITY: hostIdentity ?? '' },
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
    throw new Error('Exclusive acceptance refused: another Luma exists. No unrelated process was stopped.');
  if (allowOwned && (!pids.includes(child?.pid) || hostProcess()?.identity !== hostIdentity))
    throw new Error('Owned host identity no longer matches.');
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
const readState = () => readFile(statePath, 'utf8').then(JSON.parse);
const hash = file => readFile(file).then(bytes => createHash('sha256').update(bytes).digest('hex'));
const favorites = state => state.projects.filter(project => project.favorite === true);
const pages = () => browser.contexts().flatMap(context => context.pages()).filter(page => page.url().includes('luma.local'));
// DOM-only activation keeps OS focus and the physical pointer untouched.
const click = async locator => { await expect(locator).toHaveCount(1); await expect(locator).toBeEnabled(); await locator.evaluate(element => element.click()); };
const screenshot = async (page, name) => {
  const target = path.join(data, `${name}.png`);
  await page.screenshot({ path: target, timeout });
  evidence.screenshots.push(target);
};
const rpc = (page, method, params = {}) => page.evaluate(({ method, params, timeout }) => new Promise((resolve, reject) => {
  const id = `r14-${crypto.randomUUID()}`, webview = window.chrome.webview;
  const finish = () => { clearTimeout(timer); webview.removeEventListener('message', listener); };
  const listener = event => {
    const response = event.data;
    if (response?.type !== 'response' || response.id !== id) return;
    finish(); response.ok ? resolve(response.result) : reject(new Error(`${method}: ${response.error?.code}: ${response.error?.message}`));
  };
  const timer = setTimeout(() => { finish(); reject(new Error(`${method} timed out`)); }, timeout);
  webview.addEventListener('message', listener);
  webview.postMessage({ protocol: 1, type: 'request', id, method, params });
}), { method, params, timeout });
const settingsSync = async (favoriteCount, phase) => {
  const settings = pages().find(page => !page.url().includes('view=dock') && !page.url().includes('view=search'));
  evidence.settings ??= [];
  if (!settings) {
    evidence.settings.push({ phase, skipped: true, reason: '--startup does not create a settings window; no window was opened or activated for this check.' });
    return;
  }
  // Read existing real-host state in the background. Also verify its rendered
  // project list when that page is already present; never navigate its UI.
  await expect.poll(async () => favorites(await rpc(settings, 'app.getState')).length).toBe(favoriteCount);
  const cards = settings.locator('.project-grid .project-card');
  const renderedProjects = await cards.count();
  if (renderedProjects) await expect(cards).toHaveCount(1 + favoriteCount);
  evidence.settings.push({ phase, nativeStateMatches: true, renderedProjectListChecked: renderedProjects > 0 });
};
const sourceSnapshot = async () => ({
  entries: (await readdir(fixture)).sort(), nestedEntries: (await readdir(nested)).sort(),
  files: await Promise.all(Array.from({ length: 9 }, async (_, index) => ({ name: `文件${index + 1}.txt`, sha256: await hash(path.join(fixture, `文件${index + 1}.txt`)) }))),
  nestedSha256: await hash(path.join(nested, '内部.txt')),
});
const resourceSnapshot = () => {
  assertExclusive(true);
  return JSON.parse(ps(`
$owner=Get-CimInstance Win32_Process -Filter ('ProcessId='+$env:LUMA_ACCEPTANCE_PID)
if (-not $owner -or $owner.ExecutablePath -ine $env:LUMA_ACCEPTANCE_EXE -or $owner.CreationDate.ToUniversalTime().ToString('o') -ne $env:LUMA_ACCEPTANCE_IDENTITY) { throw 'Owner changed' }
$all=@(Get-CimInstance Win32_Process)
$ids=[Collections.Generic.HashSet[int]]::new(); [void]$ids.Add([int]$owner.ProcessId)
for ($depth=0; $depth -lt 8; $depth++) {
  $added=$false
  foreach ($p in $all) { if ($p.CreationDate -ge $owner.CreationDate -and $ids.Contains([int]$p.ParentProcessId)) { if ($ids.Add([int]$p.ProcessId)) { $added=$true } } }
  if (-not $added) { break }
}
$rows=@($all | Where-Object { $ids.Contains([int]$_.ProcessId) } | ForEach-Object {
  $live=Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
  if ($live) { [pscustomobject]@{ pid=[int]$_.ProcessId; name=$_.Name; workingSetBytes=[long]$live.WorkingSet64; privateBytes=[long]$live.PrivateMemorySize64; cpuSeconds=$live.CPU } }
})
@{ at=[DateTime]::UtcNow.ToString('o'); processes=$rows; workingSetBytes=($rows | Measure-Object workingSetBytes -Sum).Sum; dwm=$null } | ConvertTo-Json -Depth 5 -Compress`));
};
const startHost = async () => {
  const server = net.createServer();
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
  port = server.address().port; await new Promise(resolve => server.close(resolve));
  env = { ...process.env, LUMA_DATA_DIRECTORY: data, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${port}` };
  await assertExecutableIdle(exe); assertExclusive();
  hostIdentity = undefined; spawnError = undefined;
  child = spawn(exe, ['--startup'], { env, windowsHide: true, stdio: 'ignore' });
  child.on('error', error => { spawnError = error; });
  await expect.poll(() => { if (spawnError) throw spawnError; return hostProcess()?.identity ?? null; }).not.toBeNull();
  hostIdentity = hostProcess().identity;
  evidence.hosts.push({ pid: child.pid, identity: hostIdentity, port });
  await expect.poll(async () => { if (spawnError) throw spawnError; try { return (await fetch(`http://127.0.0.1:${port}/json/version`, { signal: AbortSignal.timeout(1000) })).ok; } catch { return false; } }).toBe(true);
  assertExclusive(true);
  browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`, { timeout });
  await expect.poll(() => pages().some(page => page.url().includes('view=dock'))).toBe(true);
  const dock = pages().find(page => page.url().includes('view=dock'));
  dock.setDefaultTimeout(timeout); dock.setDefaultNavigationTimeout(timeout);
  await expect.poll(async () => (await rpc(dock, 'app.getState')).projects.length).toBeGreaterThan(0);
  return dock;
};
const stopHost = async () => {
  if (browser) { await browser.close().catch(() => {}); browser = undefined; }
  if (!child) return;
  if (!hostIdentity) {
    if (!spawnError && child.exitCode === null) throw new Error('No verified creation identity; refusal to terminate an unidentified process.');
    return;
  }
  if (hostProcess()?.identity === hostIdentity) {
    try { await activate('--shutdown'); await expect.poll(() => hostProcess()?.identity ?? null, { timeout: 5000 }).toBeNull(); }
    catch (error) {
      // Fallback is confined to our PID + executable path + creation identity.
      if (hostProcess()?.identity === hostIdentity) ps(`
$p=Get-CimInstance Win32_Process -Filter ('ProcessId='+$env:LUMA_ACCEPTANCE_PID)
if ($p -and $p.ExecutablePath -ieq $env:LUMA_ACCEPTANCE_EXE -and $p.CreationDate.ToUniversalTime().ToString('o') -eq $env:LUMA_ACCEPTANCE_IDENTITY) { Stop-Process -Id $p.ProcessId }`);
      await expect.poll(() => hostProcess()?.identity ?? null, { timeout: 5000 }).toBeNull();
      evidence.cleanup.fallbackUsed = true;
      evidence.cleanup.shutdownError = String(error.message ?? error);
    }
  }
  evidence.cleanup.hostStopped = hostProcess() === null;
  if (!evidence.cleanup.hostStopped) throw new Error('Could not confirm owned test process exited.');
  child = undefined; hostIdentity = undefined;
};
const showDock = async dock => {
  await activate('--show-dock');
  await expect(dock.locator('.dock')).toBeVisible();
  await expect.poll(() => dock.locator('.dock-wrap').evaluate(element => element.getAnimations().every(animation => animation.playState === 'finished'))).toBe(true);
  await expect.poll(() => dock.locator('.dock').evaluate(element => element.getBoundingClientRect().top)).toBeGreaterThanOrEqual(0);
};
const openRoot = async dock => {
  await dock.getByRole('button', { name: '打开 目录夹具 主目录，长按展开堆叠', exact: true }).dispatchEvent('keydown', { key: 'ArrowDown', code: 'ArrowDown', bubbles: true });
  await expect(dock.locator('[data-folder-level="0"] .directory-row')).toHaveCount(10);
};
const currentToken = (dock, level) => dock.locator(`[data-folder-level="${level}"] .directory-open-current:not(.directory-run-test)`).first().getAttribute('data-item-id');
const resolveToken = (dock, entryId) => rpc(dock, 'folder.getPath', { projectId: 'fixture', itemId: 'main', entryId });
const listings = dock => dock.evaluate(() => window.__lumaR14Listings);
const observeListings = dock => dock.evaluate(() => {
  window.__lumaR14Listings = [];
  // Passive response listener: never replace postMessage or synthesize responses.
  window.chrome.webview.addEventListener('message', event => {
    const message = event.data, result = message?.result;
    if (message?.type === 'response' && message.ok && typeof result?.folderId === 'string' && Array.isArray(result.entries))
      window.__lumaR14Listings.push({ folderId: result.folderId, name: result.name, parentId: result.parentId, entries: result.entries });
  });
});
const assertRuntimePresent = async () => {
  const candidates = [process.env.WEBVIEW2_BROWSER_EXECUTABLE_FOLDER, ...[
    process.env['ProgramFiles(x86)'], process.env.ProgramFiles, process.env.LOCALAPPDATA,
  ].filter(Boolean).map(base => path.join(base, 'Microsoft/EdgeWebView/Application'))].filter(Boolean);
  for (const candidate of candidates) {
    if (await stat(path.join(candidate, 'msedgewebview2.exe')).then(value => value.isFile(), () => false)) return;
    const versions = await readdir(candidate, { withFileTypes: true }).catch(() => []);
    for (const version of versions.filter(entry => entry.isDirectory()).slice(0, 32))
      if (await stat(path.join(candidate, version.name, 'msedgewebview2.exe')).then(value => value.isFile(), () => false)) return;
  }
  throw new Error('Existing WebView2 runtime not found in known locations; refused before host dependency bootstrap. No download was started.');
};

await mkdir(data, { recursive: true });
try {
  await stat(exe); await assertRuntimePresent(); await assertExecutableIdle(exe); assertExclusive();
  await mkdir(nested, { recursive: true });
  await Promise.all(Array.from({ length: 9 }, (_, index) => writeFile(path.join(fixture, `文件${index + 1}.txt`), `Luma R14 fixture ${index + 1}\n`)));
  await writeFile(path.join(nested, '内部.txt'), 'Luma R14 nested fixture\n');
  const baseline = await sourceSnapshot();
  const state = JSON.parse(await readFile(path.join(root, 'docs/contracts/empty-state.json'), 'utf8'));
  Object.assign(state.preferences, { autoHide: false, width: 640, height: 88, iconSize: 40 });
  state.projects = [{ id: 'fixture', name: '目录夹具', description: '', color: 'mint', pinned: true, items: [{ id: 'main', name: '目录夹具', path: fixture, kind: 'folder' }] }];
  await writeFile(statePath, JSON.stringify(state));
  evidence.hostSha256 = await hash(path.join(path.dirname(exe), 'Luma.dll'));
  evidence.frontendSha256 = await hash(path.join(path.dirname(exe), 'dist/index.html'));
  let dock = await startHost(); await observeListings(dock); await showDock(dock); await openRoot(dock);

  const geometry = await dock.locator('[data-folder-level="0"] .directory-items').evaluate(grid => {
    const frame = grid.getBoundingClientRect();
    const rows = [...grid.querySelectorAll('.directory-row')].map(row => { const rect = row.getBoundingClientRect(); return { x: rect.x, y: rect.y, bottom: rect.bottom, width: rect.width, height: rect.height }; });
    return { frame: { width: frame.width, height: frame.height }, rows, visibleCount: rows.filter(row => row.y >= frame.y - .5 && row.bottom <= frame.bottom + .5).length, overflow: grid.scrollHeight > grid.clientHeight, horizontal: grid.scrollWidth > grid.clientWidth };
  });
  expect(geometry.rows.every(row => Math.abs(row.width - 88) < .5 && Math.abs(row.height - 94) < .5)).toBe(true);
  expect(new Set(geometry.rows.slice(0, 4).map(row => row.y)).size).toBe(1);
  expect(geometry.visibleCount).toBe(8); expect(geometry.overflow).toBe(true); expect(geometry.horizontal).toBe(false);
  const refresh = dock.locator('.stack-panel>header').getByRole('button', { name: '刷新目录', exact: true });
  const close = dock.getByRole('button', { name: '关闭堆叠', exact: true });
  const refreshBox = await refresh.boundingBox(), closeBox = await close.boundingBox();
  expect(Math.abs(refreshBox.y - closeBox.y)).toBeLessThan(2); expect(refreshBox.x).toBeLessThan(closeBox.x);
  await expect(dock.locator('[data-folder-level="0"] .folder-browser-path')).toContainText('目录夹具');
  evidence.geometry = geometry; await screenshot(dock, 'root-grid'); check('real fixture shows four 88×94 tiles per row, two visible rows, and refresh before close');
  const rootListingCount = (await listings(dock)).length; await click(refresh);
  await expect.poll(async () => (await listings(dock)).length).toBe(rootListingCount + 1);
  expect(await currentToken(dock, 0)).toBe((await listings(dock)).at(-1).folderId);
  expect((await resolveToken(dock, await currentToken(dock, 0))).path.toLowerCase()).toBe(fixture.toLowerCase());
  check('header refresh re-reads the real directory and uses the returned native capability');

  await click(dock.getByRole('button', { name: '移动到当前实际目录 目录夹具', exact: true }));
  await expect(dock.locator('.directory-operation-notice')).toContainText('准备移动');
  await expect(dock.getByRole('form', { name: '确认实际移动', exact: true })).toHaveCount(0);
  expect(await sourceSnapshot()).toEqual(baseline); check('empty move target explains preparation without changing source fixtures');

  await click(dock.getByRole('button', { name: '收藏 子目录', exact: true }));
  await expect.poll(async () => favorites(await readState()).length).toBe(1);
  const saved = favorites(await readState())[0];
  expect(saved.id).not.toBe('fixture'); expect(saved.items[0].path.toLowerCase()).toBe(nested.toLowerCase());
  await expect(dock.getByRole('group', { name: '收藏区', exact: true })).toBeVisible();
  await expect.poll(async () => favorites(await rpc(dock, 'app.getState')).length).toBe(1);
  await settingsSync(1, 'favorite-added');
  await screenshot(dock, 'favorite-added'); check('child star saves a persistent native favorite reference with a new saved ID');
  await click(dock.locator('[data-folder-level="0"]').getByRole('button', { name: '取消收藏 子目录', exact: true }));
  await expect.poll(async () => favorites(await readState()).length).toBe(0);
  await settingsSync(0, 'favorite-removed');
  expect(await sourceSnapshot()).toEqual(baseline); check('unfavorite removes only its saved reference; source names and bytes stay intact');
  await click(dock.locator('[data-folder-level="0"]').getByRole('button', { name: '收藏 子目录', exact: true }));
  await expect.poll(async () => favorites(await readState()).length).toBe(1);
  const favoriteBeforeRestart = favorites(await readState())[0];

  const childRow = dock.locator('[data-folder-level="0"] .directory-row').filter({ has: dock.getByText('子目录', { exact: true }) });
  await childRow.dispatchEvent('contextmenu', { button: 2, clientX: 80, clientY: 150, bubbles: true, cancelable: true });
  await click(dock.getByRole('menuitem', { name: '进入', exact: true }));
  await expect(dock.locator('[data-folder-level="1"] .stack-item')).toHaveCount(1);
  const beforeHide = { root: await currentToken(dock, 0), child: await currentToken(dock, 1) };
  expect((await resolveToken(dock, beforeHide.child)).path.toLowerCase()).toBe(nested.toLowerCase());
  await screenshot(dock, 'nested-before-hide');
  await click(dock.getByRole('button', { name: '收起面板', exact: true }));
  await expect(dock.locator('.dock-wrap')).toHaveCount(0);
  const beforeRestoreCount = (await listings(dock)).length;
  await showDock(dock);
  await expect(dock.locator('[data-folder-level="1"] .stack-item')).toHaveCount(1);
  const afterShow = { root: await currentToken(dock, 0), child: await currentToken(dock, 1) };
  const restoredListings = (await listings(dock)).slice(beforeRestoreCount);
  expect(restoredListings.map(listing => listing.name)).toEqual(['目录夹具', '子目录']);
  expect(afterShow.root).toBe(restoredListings[0].folderId); expect(afterShow.child).toBe(restoredListings[1].folderId);
  expect(restoredListings[1].parentId).toBe(restoredListings[0].folderId);
  expect(restoredListings[0].entries.find(entry => entry.name === '子目录').id).toBe(restoredListings[1].folderId);
  expect((await resolveToken(dock, afterShow.child)).path.toLowerCase()).toBe(nested.toLowerCase());
  evidence.sessionRestore = { rootThenChildRelisted: true, usesNewListingCapabilities: true, resolvedNestedFixture: true, tokenStringsReused: afterShow.root === beforeHide.root && afterShow.child === beforeHide.child };
  await screenshot(dock, 'nested-restored'); check('handle collapse and native re-show restore the child chain by re-reading native listings and resolving returned capabilities');
  const beforeDeepRefresh = (await listings(dock)).length; await click(dock.getByRole('button', { name: '刷新目录', exact: true }));
  await expect.poll(async () => (await listings(dock)).length).toBe(beforeDeepRefresh + 1);
  expect((await listings(dock)).at(-1).name).toBe('子目录');
  expect(await currentToken(dock, 1)).toBe((await listings(dock)).at(-1).folderId);
  expect(await currentToken(dock, 0)).toBe(afterShow.root);
  check('header refresh acts on the deepest open directory');
  evidence.resources = [resourceSnapshot()];
  await new Promise(resolve => setTimeout(resolve, 1000));
  evidence.resources.push(resourceSnapshot());

  await stopHost();
  dock = await startHost(); await showDock(dock);
  await expect(dock.locator('.stack-panel')).toHaveCount(0);
  expect(favorites(await readState())[0]).toEqual(favoriteBeforeRestart);
  await expect(dock.getByRole('group', { name: '收藏区', exact: true })).toBeVisible();
  expect(favorites(await rpc(dock, 'app.getState'))[0]).toEqual(favoriteBeforeRestart);
  await openRoot(dock); await expect(dock.locator('.folder-column')).toHaveCount(1);
  await expect(dock.locator('[data-folder-level="1"]')).toHaveCount(0);
  await screenshot(dock, 'restart-root-only');
  expect(await sourceSnapshot()).toEqual(baseline);
  evidence.persistedStateKeys = Object.keys(await readState()).sort();
  check('favorite survives process restart, directory session does not, and source fixture bytes remain unchanged');
  evidence.result = 'passed';
} catch (error) {
  evidence.result = 'failed'; evidence.error = String(error.stack ?? error); process.exitCode = 1;
  if (browser) for (const page of pages().filter(page => page.url().includes('view=dock'))) await screenshot(page, 'failure').catch(() => {});
} finally {
  try { await stopHost(); } catch (error) { evidence.cleanup.error = String(error.stack ?? error); process.exitCode = 1; }
  if (process.exitCode && evidence.result === 'passed') evidence.result = 'failed-cleanup';
  evidence.cleanup.fixtureRetainedForEvidence = true;
  await writeFile(path.join(data, 'result.json'), JSON.stringify(evidence, null, 2));
  console.log(JSON.stringify(evidence, null, 2));
}
