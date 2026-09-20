// Run serially after publishing. Uses actual WPF/DWM/WebView2 and an isolated state file.
import { chromium, expect } from '@playwright/test';
import { spawn, execFileSync } from 'node:child_process';
import { mkdir, writeFile, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import net from 'node:net';
import { randomUUID, createHash } from 'node:crypto';

const root = path.resolve(import.meta.dirname, '../..');
const exe = path.join(root, 'APP/native/Luma/Luma.exe');
const userState = path.join(process.env.LOCALAPPDATA, 'Luma', 'state.json');
const userHash = async () => {
  try { return createHash('sha256').update(await readFile(userState)).digest('hex'); }
  catch (error) { if (error.code === 'ENOENT') return null; throw error; }
};
const userHashBefore = await userHash();
const data = path.join(tmpdir(), `luma-native-theme-${randomUUID()}`);
await mkdir(data, { recursive: true });
const state = JSON.parse(await readFile(path.join(root, 'docs/contracts/empty-state.json'), 'utf8'));
state.preferences.theme = 'dark';
await writeFile(path.join(data, 'state.json'), JSON.stringify(state));
const server = net.createServer();
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const port = server.address().port;
await new Promise(resolve => server.close(resolve));
const env = { ...process.env, LUMA_DATA_DIRECTORY: data,
  WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${port}` };
const child = spawn(exe, ['--settings', '--search'], { env, windowsHide: true, stdio: 'ignore' });
const evidence = { date: new Date().toISOString(), pid: child.pid, data,
  hostSha256: createHash('sha256').update(await readFile(path.join(root, 'APP/native/Luma/Luma.dll'))).digest('hex'), checks: [], snapshots: [] };
let browser;
const inspect = () => JSON.parse(execFileSync('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass',
  '-File', path.join(root, 'native/tests/Inspect-NativeWindow.ps1'), '-TargetProcessId', String(child.pid)],
{ encoding: 'utf8', windowsHide: true }));
const activateSearch = () => new Promise((resolve, reject) => {
  const activation = spawn(exe, ['--search'], { env, windowsHide: true, stdio: 'ignore' });
  activation.once('error', reject);
  activation.once('exit', code => code === 0 ? resolve() : reject(new Error(`Activation exit ${code}`)));
});
const check = name => { evidence.checks.push(name); console.log(`PASS ${name}`); };
try {
  await expect.poll(async () => {
    try { return (await fetch(`http://127.0.0.1:${port}/json/version`)).ok; } catch { return false; }
  }, { timeout: 20000 }).toBe(true);
  // Playwright otherwise emulates light media on attach, overriding WebView's real profile.
  // noDefaults preserves the host's media settings, including newly reopened search pages.
  browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`, { noDefaults: true });
  const pages = () => browser.contexts().flatMap(context => context.pages());
  await expect.poll(() => pages().filter(page => page.url().includes('luma.local')).length).toBe(3);
  const settings = pages().find(page => page.url().includes('section='));
  let search = pages().find(page => page.url().includes('view=search'));
  const dock = pages().find(page => page.url().includes('view=dock'));
  settings.setDefaultTimeout(10000);
  await expect(settings.getByRole('navigation', { name: '驾驶舱导航' })).toBeVisible();
  await settings.getByRole('navigation', { name: '驾驶舱导航' }).locator('button', { hasText: '外观' }).click();

  const verify = async (theme, label) => {
    const dark = theme === 'dark';
    await expect.poll(async () => JSON.parse(await readFile(path.join(data, 'state.json'), 'utf8')).preferences.theme).toBe(theme);
    const diagnostic = { label, theme, media: [], windowsBeforeAssertions: inspect() };
    (evidence.themeDiagnostics ??= []).push(diagnostic);
    diagnostic.nativeCaptures = diagnostic.windowsBeforeAssertions.filter(w => ['Luma 设置', 'Luma 搜索'].includes(w.Title)).map(w =>
      ({ title: w.Title, ...JSON.parse(execFileSync('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', path.join(root, 'native/tests/Capture-NativeWindow.ps1'), '-WindowHandle', String(w.Hwnd),
        '-OutputPath', path.join(data, `${label}-${w.Title}-native.png`)], { encoding: 'utf8', windowsHide: true })) }));
    for (const page of [settings, search]) {
      await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
      diagnostic.media.push(await page.evaluate(() => ({ url: location.href,
        dark: matchMedia('(prefers-color-scheme: dark)').matches,
        scheme: getComputedStyle(document.documentElement).colorScheme })));
      await expect.poll(() => page.evaluate(() => matchMedia('(prefers-color-scheme: dark)').matches)).toBe(dark);
      expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe(theme);
    }
    await expect.poll(() => inspect().filter(w => ['Luma 设置', 'Luma 搜索'].includes(w.Title))
      .map(w => [w.ThemeAttributeResult, w.ImmersiveDarkMode]))
      .toEqual(Array(2).fill([0, dark ? 1 : 0]));
    // Caption/text colors are documented setter attributes: current Windows rejects their
    // getters with E_INVALIDARG. Verify actual native caption pixels instead of out-values.
    expect(diagnostic.nativeCaptures).toHaveLength(2);
    for (const capture of diagnostic.nativeCaptures) {
      expect(capture.captionSample).toEqual(dark ? [32, 31, 28] : [247, 246, 242]);
      expect(capture.titleColors[dark ? '242,240,234' : '20,19,17'] ?? 0).toBeGreaterThan(20);
    }
    const windows = inspect();
    expect(windows.find(w => w.Title === 'Luma Dock').Backdrop).toBe(1);
    const appearance = await settings.evaluate(() => {
      const card = document.querySelector('.appearance-behavior').closest('.cp-card').getBoundingClientRect();
      return {
        dpr: devicePixelRatio,
        scrollbar: getComputedStyle(document.documentElement).scrollbarColor,
        slider: getComputedStyle(document.querySelector('input[type=range]')).backgroundImage,
        insets: [...document.querySelectorAll('.appearance-behavior .toggle-row,.appearance-behavior .adaptive-note,.appearance-behavior .undo')]
          .map(element => { const rect = element.getBoundingClientRect(); return { left: rect.left - card.left, right: card.right - rect.right }; }),
        noHorizontalOverflow: document.documentElement.scrollWidth <= innerWidth,
      };
    });
    expect(appearance.scrollbar).toBe(dark ? 'rgb(105, 100, 89) rgb(27, 26, 23)' : 'rgb(167, 162, 151) rgb(237, 235, 229)');
    expect(appearance.slider).toContain(dark ? 'rgb(160, 182, 164)' : 'rgb(20, 19, 17)');
    expect(appearance.slider).toContain(dark ? 'rgb(55, 52, 46)' : 'rgb(226, 223, 214)');
    expect(appearance.noHorizontalOverflow).toBe(true);
    expect(appearance.insets.length).toBe(4);
    for (const inset of appearance.insets) {
      expect(inset.left).toBeGreaterThanOrEqual(14);
      expect(inset.right).toBeGreaterThanOrEqual(14);
    }
    evidence.snapshots.push({ label, theme, windows, appearance });
    await settings.locator('.appearance-behavior').scrollIntoViewIfNeeded();
    await settings.screenshot({ path: path.join(data, `${label}-settings.png`) });
    await search.screenshot({ path: path.join(data, `${label}-search.png`) });
    check(label);
  };

  await verify('dark', 'saved-dark-startup');
  await settings.getByRole('button', { name: '浅色模式', exact: true }).click();
  await verify('light', 'saved-light-both-windows');
  await search.getByRole('combobox').press('Escape');
  await expect.poll(() => pages().some(page => page.url().includes('view=search'))).toBe(false);
  await activateSearch();
  await expect.poll(() => pages().some(page => page.url().includes('view=search'))).toBe(true);
  search = pages().find(page => page.url().includes('view=search'));
  await verify('light', 'reopened-search-light');
  await settings.getByRole('button', { name: '深色模式', exact: true }).click();
  await verify('dark', 'saved-dark-both-windows');
  expect(await dock.evaluate(() => getComputedStyle(document.documentElement).backgroundColor)).toBe('rgba(0, 0, 0, 0)');
  check('dock-remains-transparent-without-system-backdrop');
  evidence.userStateHashBefore = userHashBefore;
  evidence.userStateHashAfter = await userHash();
  expect(evidence.userStateHashAfter).toBe(userHashBefore);
  check('actual-user-state-unchanged');
  evidence.result = 'passed';
} catch (error) {
  evidence.result = 'failed'; evidence.error = String(error.stack ?? error); throw error;
} finally {
  await writeFile(path.join(data, 'result.json'), JSON.stringify(evidence, null, 2));
  console.log(JSON.stringify(evidence, null, 2));
  try { if (browser) await browser.close(); } finally { child.kill(); }
}
