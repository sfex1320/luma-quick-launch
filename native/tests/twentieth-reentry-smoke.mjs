// Real host visibility events and physical cursor reentry; no simulated host events.
import { chromium, expect } from '@playwright/test';
import { spawn, execFileSync } from 'node:child_process';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import net from 'node:net';
import { randomUUID } from 'node:crypto';
import { createInterface } from 'node:readline';
import { assertExecutableIdle } from './assert-executable-idle.mjs';
const root = path.resolve(import.meta.dirname, '../..');
if (!process.env.LUMA_TEST_EXE) throw new Error('Explicit isolated executable required');
const exe = path.resolve(process.env.LUMA_TEST_EXE);
if (exe.toLowerCase() === path.join(root, 'APP/Luma/Luma.exe').toLowerCase()) throw new Error('Do not test against the live copy');
assertExecutableIdle(exe);
execFileSync('powershell.exe', ['-NoProfile', '-File', path.join(root, 'scripts/assert-delivery-window.ps1')], { windowsHide: true });
const data = path.join(tmpdir(), `luma-reentry-${randomUUID()}`);
await mkdir(data, { recursive: true });
const state = JSON.parse(await readFile(path.join(root, 'docs/contracts/empty-state.json'), 'utf8'));
state.preferences.motionSpeed = 'relaxed';
state.projects = [{ id: 'probe', name: 'Reentry', description: '', color: 'mint', pinned: true,
  items: [{ id: 'one', name: 'One', path: 'https://example.com', kind: 'url' },
    { id: 'two', name: 'Two', path: 'https://example.org', kind: 'url' }] }];
await writeFile(path.join(data, 'state.json'), JSON.stringify(state));
const socket = net.createServer(); await new Promise(r => socket.listen(0, '127.0.0.1', r));
const port = socket.address().port; await new Promise(r => socket.close(r));
const env = { ...process.env, LUMA_DATA_DIRECTORY: data, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${port}` };
const host = spawn(exe, ['--show-dock'], { env, windowsHide: true, stdio: 'ignore' });
const evidence = { at: new Date().toISOString(), exe, data, checks: [], result: 'pending' };
let browser, helper;
try {
  await expect.poll(async () => { try { return (await fetch(`http://127.0.0.1:${port}/json/version`)).ok; } catch { return false; } }, { timeout: 20000 }).toBe(true);
  browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`);
  const dock = browser.contexts().flatMap(c => c.pages()).find(p => p.url().includes('view=dock'));
  await expect(dock.locator('.dock-wrap')).toBeAttached();
  await expect.poll(() => dock.locator('.dock-wrap').evaluate(el => el.getAnimations().every(a => a.playState === 'finished'))).toBe(true);
  await dock.locator('.dock-project').evaluate(el => el.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true })));
  const panel = dock.locator('.stack-panel');
  await expect(panel).toBeVisible();
  await expect.poll(() => panel.evaluate(el => el.getAnimations().every(a => a.playState === 'finished'))).toBe(true);
  const windows = JSON.parse(execFileSync('powershell.exe', ['-NoProfile', '-File', path.join(root, 'native/tests/Inspect-NativeWindow.ps1'), '-TargetProcessId', String(host.pid)], { encoding: 'utf8', windowsHide: true }));
  const bounds = windows.find(w => w.Title === 'Luma Dock').Bounds;
  const metrics = await dock.evaluate(() => ({ scale: devicePixelRatio, bar: document.querySelector('.dock').getBoundingClientRect().toJSON(), panel: document.querySelector('.stack-panel').getBoundingClientRect().toJSON() }));
  helper = spawn('powershell.exe', ['-NoProfile', '-File', path.join(root, 'native/tests/Reentry-Pointer.ps1')], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  let error = ''; helper.stderr.on('data', value => { error += value; });
  const lines = createInterface({ input: helper.stdout })[Symbol.asyncIterator]();
  const reply = async () => { const result = await lines.next(); if (result.done) throw new Error(error || 'Pointer helper stopped'); return result.value; };
  expect(await reply()).toBe('ready');
  const move = async (x, y) => { helper.stdin.write(`${Math.round(x)},${Math.round(y)}\n`); expect(await reply()).toBe('moved'); };
  const into = rect => move(bounds.Left + (rect.x + rect.width / 2) * metrics.scale, bounds.Top + (rect.y + rect.height - 10) * metrics.scale);
  const outside = () => move(bounds.Left + 8, bounds.Top + 500);
  await dock.evaluate(() => {
    window.__reentry = [];
    window.chrome.webview.addEventListener('message', e => { if (e.data.event === 'window.visibility') window.__reentry.push(e.data.data); });
  });
  for (const [name, rect, waitForSubmenu] of [['secondary during primary exit', metrics.panel, false], ['secondary during secondary exit', metrics.panel, true], ['primary during exit', metrics.bar, false]]) {
    await into(metrics.panel);
    await outside();
    await expect(dock.locator('.dock-wrap')).toHaveClass(/dock-closing/, { timeout: 7000 });
    if (waitForSubmenu) await dock.waitForFunction(() => {
      const animation = document.querySelector('.stack-panel')?.getAnimations()[0];
      return animation && Number(animation.currentTime) >= Number(animation.effect.getTiming().delay) + 10;
    });
    await into(rect);
    await expect(dock.locator('.dock-wrap')).not.toHaveClass(/dock-closing/);
    await expect.poll(() => panel.evaluate(el => el.getAnimations().every(a => a.playState === 'finished'))).toBe(true);
    const events = await dock.evaluate(() => window.__reentry);
    expect(events.at(-1).visible).toBe(true);
    evidence.checks.push({ name, events: events.slice(-2) });
  }
  await outside();
  await expect(dock.locator('.dock-wrap')).toHaveCount(0, { timeout: 7000 });
  await into(metrics.panel);
  await new Promise(r => setTimeout(r, 400));
  await expect(dock.locator('.dock-wrap')).toHaveCount(0);
  evidence.checks.push({ name: 'fully hidden submenu area does not wake or cover other applications' });
  evidence.result = 'passed';
} catch (error) { evidence.error = String(error); throw error; }
finally {
  if (helper?.exitCode === null) { helper.stdin.end('quit\n'); await new Promise(resolve => helper.once('exit', resolve)); }
  if (browser) await browser.close();
  host.kill();
  await writeFile(path.join(root, 'docs/evidence/twentieth-delivery/reentry.json'), JSON.stringify(evidence, null, 2));
  console.log(JSON.stringify(evidence, null, 2));
}
