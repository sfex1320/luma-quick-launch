// Real OLE32 -> native Hotzone HWND -> real WebView2 file object -> persisted state.
// CDP reads DOM only; no fake bridge, JS DataTransfer or Input.dispatchDragEvent.
import { chromium, expect } from '@playwright/test';
import { spawn, execFileSync } from 'node:child_process';
import { mkdir, writeFile, readFile, readdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import net from 'node:net';
import { randomUUID, createHash } from 'node:crypto';

const root = path.resolve(import.meta.dirname, '../..');
const args = process.argv.slice(2);
const exe = args.includes('--exe') ? path.resolve(args[args.indexOf('--exe') + 1]) : path.join(root, 'APP/Luma/Luma.exe');
const sdk = process.env.DOTNET_EXE || path.join(process.env.LOCALAPPDATA, 'Microsoft/dotnet/dotnet.exe');
const probeProject = path.join(root, 'native/tests/OleDragProbe/OleDragProbe.csproj');
execFileSync(sdk, ['build', probeProject, '-c', 'Release', '--nologo'], { windowsHide: true, stdio: 'inherit' });
const probeExe = path.join(root, 'native/tests/OleDragProbe/bin/Release/net10.0-windows/OleDragProbe.exe');
const data = path.join(tmpdir(), `luma-native-ole-${randomUUID()}`);
await mkdir(data, { recursive: true });
const state = JSON.parse(await readFile(path.join(root, 'docs/contracts/empty-state.json'), 'utf8'));
state.preferences.autoHide = true;
await writeFile(path.join(data, 'state.json'), JSON.stringify(state));
const portServer = net.createServer(); await new Promise(r => portServer.listen(0, '127.0.0.1', r));
const port = portServer.address().port; await new Promise(r => portServer.close(r));
const env = { ...process.env, LUMA_DATA_DIRECTORY: data, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${port}` };
const child = spawn(exe, [], { env, windowsHide: true, stdio: 'ignore' });
const evidence = { date: new Date().toISOString(), pid: child.pid, exe, data, port,
  hostSha256: createHash('sha256').update(await readFile(path.join(path.dirname(exe), 'Luma.dll'))).digest('hex'),
  mechanism: 'Ole32.DoDragDrop / CF_HDROP / real IDropSource; timed physical cursor movement, no injected button state',
  checks: [], monitors: [] };
try { evidence.frontendManifestSha256 = createHash('sha256').update(await readFile(path.join(path.dirname(exe), 'dist/index.html'))).digest('hex'); } catch { }
let browser;
const inspect = () => JSON.parse(execFileSync('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(root, 'native/tests/Inspect-NativeWindow.ps1'), '-TargetProcessId', String(child.pid)], { encoding: 'utf8', windowsHide: true }));
const logs = async () => (await Promise.all((await readdir(path.join(data, 'logs'))).filter(f => f.endsWith('.log')).map(f => readFile(path.join(data, 'logs', f), 'utf8')))).join('\n');
const check = name => { evidence.checks.push(name); console.log(`PASS ${name}`); };
const delay = ms => new Promise(r => setTimeout(r, ms));
try {
  await expect.poll(async () => { try { return (await fetch(`http://127.0.0.1:${port}/json/version`)).ok; } catch { return false; } }, { timeout: 20000 }).toBeTruthy();
  browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`);
  const pages = () => browser.contexts().flatMap(c => c.pages());
  await expect.poll(() => pages().some(p => p.url().includes('view=dock'))).toBe(true);
  const dock = pages().find(p => p.url().includes('view=dock'));
  dock.setDefaultTimeout(12000);
  await delay(1800);
  const hotspots = inspect().filter(w => w.Title === 'Luma Hotzone');
  expect(hotspots.length).toBeGreaterThan(0);
  evidence.hotspots = hotspots;
  for (let index = 0; index < hotspots.length; index++) {
    const hotspot = hotspots[index];
    // Each trial begins genuinely hidden. Empty preload HWND may still exist.
    await expect(dock.getByRole('navigation', { name: '快捷启动面板' })).toHaveCount(0);
    const hidden = inspect().find(w => w.Title === 'Luma Dock');
    expect(!hidden.Visible || hidden.RegionType === 1).toBe(true);
    const fixture = path.join(data, `OLE 双屏夹具 ${index + 1}`);
    await mkdir(fixture);
    const command = path.join(data, `drop-${index}.json`);
    const edgeX = Math.round((hotspot.Bounds.Left + hotspot.Bounds.Right) / 2);
    const edgeY = hotspot.Bounds.Top + 1;
    const trial = { index, hotspot, events: [], before: hidden };
    evidence.monitors.push(trial);
    const beforeLog = await logs();
    const probe = spawn(probeExe, [fixture, String(edgeX), String(edgeY), command], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env, DOTNET_ROOT: path.dirname(sdk) } });
    let pending = ''; let stderr = '';
    probe.stdout.setEncoding('utf8');
    probe.stdout.on('data', chunk => {
      pending += chunk;
      const lines = pending.split('\n'); pending = lines.pop();
      for (const line of lines) if (line.trim()) { const event = JSON.parse(line); trial.events.push({ at: Date.now(), ...event }); console.log(`OLE ${index + 1}: ${line}`); }
    });
    probe.stderr.on('data', chunk => { stderr += chunk; });
    const exited = new Promise((resolve, reject) => { probe.once('exit', resolve); probe.once('error', reject); });
    const watchdog = setTimeout(() => probe.kill(), 28000);
    try {
      await expect.poll(() => trial.events.some(e => e.stage === 'edge-entered'), { timeout: 8000 }).toBe(true);
      await expect(dock.getByRole('navigation', { name: '快捷启动面板' })).toBeVisible();
      // A normal pointer hover could also reveal. Require the native OLE callback log.
      await expect.poll(async () => (await logs()).slice(beforeLog.length).includes('热区 OLE 文件拖入'), { timeout: 6000 }).toBe(true);
      await delay(1400); // Exceeds the normal auto-hide grace period while still dragging.
      await expect(dock.getByRole('navigation', { name: '快捷启动面板' })).toBeVisible();
      const shown = inspect().find(w => w.Title === 'Luma Dock');
      expect((shown.Bounds.Left + shown.Bounds.Right) / 2).toBeCloseTo(edgeX, 0);
      trial.shown = shown;
      const metrics = await dock.locator('.dock').evaluate(el => ({ dpr: devicePixelRatio, rect: el.getBoundingClientRect().toJSON() }));
      // Left padding is blank on both an empty and populated dock: creates a project.
      const x = Math.round(shown.Bounds.Left + (metrics.rect.left + 10) * metrics.dpr);
      const y = Math.round(shown.Bounds.Top + (metrics.rect.top + metrics.rect.height / 2) * metrics.dpr);
      trial.drop = { x, y, metrics };
      await writeFile(command, JSON.stringify({ X: x, Y: y, HoldMilliseconds: 1800 }));
      await expect.poll(() => trial.events.some(e => e.stage === 'webview-entered'), { timeout: 5000 }).toBe(true);
      await expect(dock.locator('.dock-drop-hint')).toBeVisible();
      await delay(1000);
      await expect(dock.getByRole('navigation', { name: '快捷启动面板' })).toBeVisible();
      trial.exitCode = await exited; trial.stderr = stderr;
      expect(trial.exitCode, stderr).toBe(0);
      await expect.poll(async () => JSON.parse(await readFile(path.join(data, 'state.json'), 'utf8')).projects.some(p => p.items.some(i => i.path === fixture && i.kind === 'folder')), { timeout: 10000 }).toBe(true);
      const tail = (await logs()).slice(beforeLog.length); trial.log = tail;
      expect(tail).not.toContain('shell.openItem 启动');
      check(`显示器 ${index + 1}：隐藏 OLE 热区唤出、拖拽保持、真实 WebView 接收目录并保存且不启动`);
    } finally {
      // Allow the driver's own 15-second cancel path to restore the pointer first.
      await exited.catch(() => {}); clearTimeout(watchdog);
      const origin = trial.events.find(e => e.stage === 'started');
      if (origin) execFileSync('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(root, 'native/tests/Move-NativePointer.ps1'), '-X', String(origin.originalX), '-Y', String(origin.originalY)], { windowsHide: true });
    }
    if (await dock.getByRole('button', { name: '收起面板', exact: true }).count()) await dock.getByRole('button', { name: '收起面板', exact: true }).click();
    await expect.poll(() => inspect().find(w => w.Title === 'Luma Dock')?.Visible, { timeout: 5000 }).toBe(false);
  }
  evidence.result = 'passed';
} catch (error) {
  evidence.result = 'failed'; evidence.error = String(error.stack ?? error);
  try { evidence.finalWindows = inspect(); evidence.hostLog = await logs(); } catch { }
  process.exitCode = 1;
} finally {
  await writeFile(path.join(data, 'result.json'), JSON.stringify(evidence, null, 2));
  console.log(JSON.stringify(evidence, null, 2));
  if (browser) await browser.close();
  child.kill();
}
