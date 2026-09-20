import { test, expect, type Page } from '@playwright/test';
import { initialState } from '../src/data';

async function attachIntegration(page: Page, failMutation = false) {
  await page.addInitScript(({ state, failMutation }) => {
    const listeners: Array<(e: { data: unknown }) => void> = [];
    const api = window as any;
    api.__integration = { autoStart: false, autoStartHere: false, desktopShortcut: false };
    api.__writes = [];
    api.chrome = { webview: {
      addEventListener: (_: string, listener: typeof listeners[number]) => listeners.push(listener),
      postMessage: (req: any) => {
        let result: unknown = { applied: true };
        let error: string | undefined;
        if (req.method === 'app.getState') result = state;
        if (req.method === 'app.saveState') api.__writes.push(req);
        if (req.method === 'system.getIntegration') result = { ...api.__integration };
        if (req.method === 'system.setAutoStart') {
          api.__writes.push(req);
          if (failMutation) error = '无法写入 Windows 启动项';
          else result = api.__integration = { ...api.__integration, autoStart: req.params.enabled, autoStartHere: req.params.enabled };
        }
        if (req.method === 'system.createDesktopShortcut') {
          api.__writes.push(req);
          result = api.__integration = { ...api.__integration, desktopShortcut: true };
        }
        setTimeout(() => listeners.forEach(listener => listener({ data: { protocol: 1, type: 'response', id: req.id, ok: !error, result, error: { message: error } } })), req.method.startsWith('system.set') ? 250 : 20);
      },
    } };
  }, { state: initialState, failMutation });
  await page.goto('/?mode=native');
  await page.getByRole('button', { name: '设置与备份', exact: true }).click();
}

test('browser preview cannot change desktop or login startup', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: '设置与备份', exact: true }).click();
  await expect(page.getByRole('checkbox', { name: '开机启动' })).toBeDisabled();
  await expect(page.getByRole('button', { name: '创建桌面快捷方式', exact: true })).toBeDisabled();
});

test('startup switch and desktop shortcut use system operations without saving or launching projects', async ({ page }) => {
  await attachIntegration(page);
  const startup = page.getByRole('checkbox', { name: '开机启动' });
  await expect(startup).toBeEnabled();
  await expect(startup).not.toBeChecked();
  await startup.click();
  await expect(startup).toBeDisabled();
  await expect(startup).toBeChecked();
  await expect(startup).toBeEnabled();
  await page.getByRole('button', { name: '创建桌面快捷方式', exact: true }).click();
  await expect(page.getByText('桌面快捷方式已创建', { exact: true })).toBeVisible();
  await startup.click();
  await expect(startup).not.toBeChecked();
  await expect(startup).toBeEnabled();
  expect(await page.evaluate(() => (window as any).__writes.map((r: any) => [r.method, r.params]))).toEqual([
    ['system.setAutoStart', { enabled: true }], ['system.createDesktopShortcut', {}], ['system.setAutoStart', { enabled: false }],
  ]);
});

test('a failed OS write leaves the switch unchanged and shows the error', async ({ page }) => {
  await attachIntegration(page, true);
  const startup = page.getByRole('checkbox', { name: '开机启动' });
  await startup.click();
  await expect(page.getByRole('alert')).toContainText('无法写入 Windows 启动项');
  await expect(startup).not.toBeChecked();
  await expect(startup).toBeEnabled();
});

test('returning from Windows settings refreshes actual startup state', async ({ page }) => {
  await attachIntegration(page);
  const startup = page.getByRole('checkbox', { name: '开机启动' });
  await expect(startup).toBeEnabled();
  await page.evaluate(() => {
    (window as any).__integration = { autoStart: true, autoStartHere: true, desktopShortcut: false };
    window.dispatchEvent(new Event('focus'));
  });
  await expect(startup).toBeChecked();
  expect(await page.evaluate(() => (window as any).__writes)).toEqual([]);
});
