// Isolated real WebView2 observation; never moves the physical pointer or changes user data.
import { chromium, expect } from '@playwright/test';
import { spawn, execFileSync } from 'node:child_process';
import { mkdir, writeFile, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import net from 'node:net';
import { randomUUID, createHash } from 'node:crypto';
const root = path.resolve(import.meta.dirname, '../..');
const executable = process.argv[2] || path.join(root, 'APP/native/Luma/Luma.exe');
const output = process.argv[3] || path.join(root, 'test-results/fifth-motion-native-before.json');
const data = path.join(tmpdir(), `luma-motion-probe-${randomUUID()}`);
await mkdir(data, { recursive: true });
const state = JSON.parse(await readFile(path.join(root, 'docs/contracts/empty-state.json'), 'utf8'));
state.preferences.autoHide = false;
state.preferences.reducedMotion = false;
await writeFile(path.join(data, 'state.json'), JSON.stringify(state));
const userConfig = path.join(process.env.LOCALAPPDATA, 'Luma/state.json');
const hash = value => createHash('sha256').update(value).digest('hex');
const before = hash(await readFile(userConfig));
const socket = net.createServer();
await new Promise(resolve => socket.listen(0, '127.0.0.1', resolve));
const port = socket.address().port;
await new Promise(resolve => socket.close(resolve));
const environment = { ...process.env, LUMA_DATA_DIRECTORY: data,
  WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${port}` };
const child = spawn(executable, ['--show-dock'], { env: environment, windowsHide: true, stdio: 'ignore' });
const evidence = { date: new Date().toISOString(), executable, pid: child.pid, data };
let browser;
try {
  await expect.poll(async () => { try { return (await fetch(`http://127.0.0.1:${port}/json/version`)).ok; } catch { return false; } }, { timeout: 20000 }).toBe(true);
  browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`);
  await expect.poll(() => browser.contexts().flatMap(c => c.pages()).some(p => p.url().includes('view=dock'))).toBe(true);
  const dock = browser.contexts().flatMap(c => c.pages()).find(p => p.url().includes('view=dock'));
  await expect(dock.locator('.dock-wrap')).toBeAttached({ timeout: 15000 });
  evidence.webview = await dock.evaluate(() => ({ userAgent: navigator.userAgent, reducedMedia: matchMedia('(prefers-reduced-motion: reduce)').matches,
    animations: document.querySelector('.dock-wrap').getAnimations().map(a => ({ duration: a.effect.getTiming().duration, state: a.playState,
      frames: a.effect.getKeyframes().map(f => ({ transform: f.transform, opacity: f.opacity })) })) }));
  evidence.windowsMinAnimate = execFileSync('powershell.exe', ['-NoProfile', '-Command', "(Get-ItemProperty -LiteralPath 'HKCU:\\Control Panel\\Desktop\\WindowMetrics').MinAnimate"], { windowsHide: true, encoding: 'utf8' }).trim();
  evidence.softwareReducedMotion = state.preferences.reducedMotion;
  await expect.poll(() => dock.locator('.dock-wrap').evaluate(el => el.getAnimations().every(a => a.playState === 'finished'))).toBe(true);
  await dock.evaluate(() => {
    window.__nativeMotionSamples = [];
    const animate = Element.prototype.animate;
    Element.prototype.animate = function (...args) {
      const animation = animate.apply(this, args);
      if (!this.matches('.dock-wrap')) return animation;
      const element = this;
      const record = { duration: animation.effect.getTiming().duration, frames: animation.effect.getKeyframes(), samples: [] };
      window.__nativeMotionSamples.push(record);
      const start = performance.now();
      function sample() {
        const computed = getComputedStyle(element), rect = element.getBoundingClientRect();
        record.samples.push({ elapsed: performance.now() - start, currentTime: animation.currentTime,
          y: computed.transform === 'none' ? 0 : new DOMMatrixReadOnly(computed.transform).m42,
          opacity: Number(computed.opacity), bottom: rect.bottom, attached: element.isConnected });
        if (element.isConnected && animation.playState !== 'finished' && performance.now() - start < 1800) requestAnimationFrame(sample);
      }
      requestAnimationFrame(sample);
      return animation;
    };
  });
  // Escape follows the real frontend event path and collapsed sync; activation
  // uses the native instance signal for this unique data directory only.
  await dock.keyboard.press('Escape');
  await expect(dock.locator('.dock-wrap')).toHaveCount(0);
  const signal = spawn(executable, ['--show-dock'], { env: environment, windowsHide: true, stdio: 'ignore' });
  await new Promise((resolve, reject) => { signal.once('exit', resolve); signal.once('error', reject); });
  await expect(dock.locator('.dock-wrap')).toBeAttached();
  await expect.poll(() => dock.locator('.dock-wrap').evaluate(el => el.getAnimations().every(a => a.playState === 'finished'))).toBe(true);
  evidence.cycles = await dock.evaluate(() => window.__nativeMotionSamples);
  evidence.userConfigUnchanged = before === hash(await readFile(userConfig));
  expect(evidence.userConfigUnchanged).toBe(true);
} finally {
  if (browser) await browser.close();
  child.kill();
  await mkdir(path.dirname(output), { recursive: true });
  await writeFile(output, JSON.stringify(evidence, null, 2));
  console.log(JSON.stringify({ ...evidence, cycles: evidence.cycles?.map(cycle => ({ duration: cycle.duration,
    sampleCount: cycle.samples.length, first: cycle.samples[0], last: cycle.samples.at(-1) })), output }, null, 2));
}
