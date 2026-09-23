// Real packaged WebView2 and native bridge; DOM-only setup, no physical input.
import { chromium, expect } from '@playwright/test';
import { spawn } from 'node:child_process';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import net from 'node:net';
import { randomUUID } from 'node:crypto';
import { assertExecutableIdle } from './assert-executable-idle.mjs';
const root = path.resolve(import.meta.dirname, '../..');
if (!process.env.LUMA_TEST_EXE) throw new Error('Explicit validation executable required');
const exe = path.resolve(process.env.LUMA_TEST_EXE);
if (exe.toLowerCase() === path.join(root, 'APP/Luma/Luma.exe').toLowerCase()) throw new Error('Use an isolated package extraction');
assertExecutableIdle(exe);
const data = path.join(tmpdir(), `luma-motion-switch-${randomUUID()}`);
await mkdir(path.join(data, 'one'), { recursive: true });
await mkdir(path.join(data, 'two'), { recursive: true });
await writeFile(path.join(data, 'one', 'first.txt'), 'fixture');
await writeFile(path.join(data, 'two', 'second.txt'), 'fixture');
const state = JSON.parse(await readFile(path.join(root, 'docs/contracts/empty-state.json'), 'utf8'));
state.preferences.motionSpeed = 'relaxed'; state.preferences.autoHide = false;
state.projects = [
  ...['Group A', 'Group B'].map((name, i) => ({ id: `group-${i}`, name, description: '', color: 'mint', pinned: true,
    items: ['one', 'two'].map((item, j) => ({ id: `url-${j}`, name: item, path: `https://example.com/${item}`, kind: 'url' })) })),
  ...['one', 'two'].map(name => ({ id: name, name, description: '', color: 'mint', pinned: true,
    items: [{ id: 'root', name, path: path.join(data, name), kind: 'folder' }] })),
];
await writeFile(path.join(data, 'state.json'), JSON.stringify(state));
const socket = net.createServer(); await new Promise(r => socket.listen(0, '127.0.0.1', r));
const port = socket.address().port; await new Promise(r => socket.close(r));
const env = { ...process.env, LUMA_DATA_DIRECTORY: data, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${port}` };
const host = spawn(exe, ['--show-dock'], { env, windowsHide: true, stdio: 'ignore' });
const evidence = { at: new Date().toISOString(), exe, data, result: 'pending', checks: [] };
let browser;
try {
  await expect.poll(async () => { try { return (await fetch(`http://127.0.0.1:${port}/json/version`)).ok; } catch { return false; } }, { timeout: 20000 }).toBe(true);
  browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`);
  const dock = browser.contexts().flatMap(c => c.pages()).find(p => p.url().includes('view=dock'));
  const wrap = dock.locator('.dock-wrap'), panel = dock.locator('.stack-panel');
  await expect(wrap).toBeAttached();
  await expect.poll(() => wrap.evaluate(el => el.getAnimations().every(a => a.playState === 'finished'))).toBe(true);
  await dock.evaluate(() => {
    window.__motionRequests = [];
    const post = window.chrome.webview.postMessage.bind(window.chrome.webview);
    window.chrome.webview.postMessage = request => { window.__motionRequests.push(request); post(request); };
  });
  const open = index => dock.locator('.dock-project').nth(index).evaluate(el => el.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true })));
  await open(0);
  await expect.poll(() => panel.evaluate(el => el.getAnimations().every(a => a.playState === 'finished'))).toBe(true);
  for (const index of [1, 2, 3, 0, 2, 1, 3]) {
    await open(index);
    const frame = await panel.evaluate(el => ({ y: new DOMMatrixReadOnly(getComputedStyle(el).transform).m42,
      moving: el.getAnimations().some(a => a.playState !== 'finished' && Number(a.effect.getTiming().duration) > 0) }));
    expect(frame).toEqual({ y: 0, moving: false });
    if (index >= 2) await expect(panel.locator('.directory-row')).toHaveCount(1);
  }
  evidence.checks.push('Seven group/folder switches update content immediately without submenu slide replay');
  const requests = await dock.evaluate(() => window.__motionRequests);
  expect(requests.filter(r => ['folder.open', 'shell.openItem', 'shell.openRecent'].includes(r.method))).toHaveLength(0);
  expect(requests.filter(r => r.method === 'window.sync').every(r => r.params.expanded)).toBe(true);
  evidence.checks.push('Switches keep the real native surface expanded and never launch an item');
  await dock.getByRole('button', { name: '收起面板', exact: true }).evaluate(el => el.click());
  const timing = await dock.evaluate(() => {
    const timingOf = selector => { const t = document.querySelector(selector).getAnimations()[0].effect.getTiming(); return { duration: t.duration, delay: t.delay }; };
    return { bar: timingOf('.dock-wrap'), panel: timingOf('.stack-panel') };
  });
  expect(timing).toEqual({ bar: { duration: 600, delay: 200 }, panel: { duration: 200, delay: 0 } });
  evidence.exit = timing;
  await expect(wrap).toHaveCount(0);
  evidence.checks.push('Secondary exits for 200ms, then primary for 600ms; final collapse removes both');
  await new Promise((resolve, reject) => {
    const signal = spawn(exe, ['--show-dock'], { env, windowsHide: true, stdio: 'ignore' });
    signal.once('exit', code => code === 0 ? resolve() : reject(new Error(`Activation failed ${code}`))); signal.once('error', reject);
  });
  await expect(panel).toBeAttached();
  const entrance = await panel.evaluate(el => el.getAnimations()[0].effect.getTiming());
  expect(entrance.duration).toBe(200); expect(entrance.delay).toBe(650);
  await expect.poll(() => panel.evaluate(el => el.getAnimations().every(a => a.playState === 'finished'))).toBe(true);
  evidence.checks.push('Native reveal preserves the original primary-first entrance and remembered submenu');
  evidence.result = 'passed';
} catch (error) { evidence.error = String(error); throw error; }
finally {
  if (browser) await browser.close(); host.kill();
  await mkdir(path.join(root, 'docs/evidence/twentyfirst-delivery'), { recursive: true });
  await writeFile(path.join(root, 'docs/evidence/twentyfirst-delivery/motion-native.json'), JSON.stringify(evidence, null, 2));
  console.log(JSON.stringify(evidence, null, 2));
}
