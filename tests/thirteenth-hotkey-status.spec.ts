import { expect, test } from '@playwright/test';
import { defaults } from '../src/data';

test('refresh registration failure when a chord becomes occupied during recording', async ({ page }) => {
  await page.addInitScript(preferences => {
    const host = window as any;
    const listeners: Array<(event: any) => void> = [];
    let recording = false, recorded = false;
    host.__shortcutStatusCalls = [];
    host.chrome ||= {};
    host.chrome.webview = {
      addEventListener: (_: string, listener: (event: any) => void) => listeners.push(listener),
      postMessage: (request: any) => {
        host.__shortcutStatusCalls.push({ method: request.method, params: request.params });
        let result: any = { accepted: true };
        if (request.method === 'app.getState') result = {
          schemaVersion: 1, revision: 1, projects: [],
          preferences: { ...preferences, shortcuts: [{ id: 'global-search', code: 'KeyJ', ctrl: true, shift: false, alt: false, scope: 'global', action: 'search' }] },
        };
        if (request.method === 'system.getIntegration') result = { autoStart: false, autoStartHere: false, desktopShortcut: false };
        if (request.method === 'shortcut.setRecording') {
          recording = request.params.active;
          if (recording) recorded = true;
        }
        if (request.method === 'shortcut.getStatus') result = { bindings: [{
          id: 'global-search', registered: !recording && !recorded,
          message: recording ? '录制按键期间暂停。' : recorded ? '注册失败：组合键已被其他应用占用。' : '全局快捷键已注册。',
        }] };
        setTimeout(() => listeners.forEach(listener => listener({ data: { protocol: 1, type: 'response', id: request.id, ok: true, result } })), 2);
      },
    };
  }, defaults);
  await page.goto('/?mode=native');
  await page.getByRole('button', { name: '设置与备份', exact: true }).click();
  const status = page.locator('.shortcut-binding small');
  await expect(status).toHaveText('全局快捷键已注册。');
  const recorder = page.getByLabel('录制快捷键');
  await recorder.click();
  await expect(recorder).toHaveValue('请按下快捷键…');
  await expect(status).toHaveText('录制按键期间暂停。');
  await recorder.press('Control+b');
  await expect(recorder).toHaveValue('Ctrl + B');
  await expect(status).toHaveText('注册失败：组合键已被其他应用占用。');
  await expect(status).toHaveClass('shortcut-unavailable');
  const calls = await page.evaluate(() => (window as any).__shortcutStatusCalls);
  expect(calls.filter((call: any) => call.method === 'shortcut.setRecording').map((call: any) => call.params.active)).toEqual([true, false]);
  expect(calls.filter((call: any) => call.method === 'shortcut.execute' || call.method === 'app.saveState')).toEqual([]);
});
