import { test, expect, type Page } from '@playwright/test';
import { defaults } from '../src/data';

async function setup(page: Page, update: { fail?: string; hasUpdate?: boolean }) {
  await page.addInitScript(({ update, preferences }) => {
    const host = window as any, listeners: Array<(event: any) => void> = [];
    host.__calls = [];
    host.chrome ||= {};
    host.chrome.webview = { addEventListener: (_: string, callback: any) => listeners.push(callback), postMessage: (req: any) => {
      host.__calls.push(req);
      const respond = (result: unknown, error?: string) => listeners.forEach(callback => callback({ data: { protocol: 1, type: 'response', id: req.id, ok: !error, ...(error ? { error: { code: 'INTERNAL_ERROR', message: error } } : { result }) } }));
      if (req.method === 'app.getState') { setTimeout(() => respond({ schemaVersion: 1, revision: 1, preferences, projects: [] }), 2); return; }
      if (req.method === 'system.getIntegration') { setTimeout(() => respond({ autoStart: false, autoStartHere: false, desktopShortcut: false }), 2); return; }
      if (req.method === 'update.check') {
        if (update.fail) { setTimeout(() => respond(null, update.fail), 2); return; }
        setTimeout(() => respond({ currentVersion: '0.4.0', latestVersion: update.hasUpdate ? '9.9.9' : '0.4.0', hasUpdate: !!update.hasUpdate, notes: '修复与改进', publishedAt: '2026-09-23T00:00:00Z', installMode: 'portable', asset: update.hasUpdate ? { name: 'luma-quick-launch-9.9.9-win-x64.zip', url: 'https://github.com/sfex1320/luma-quick-launch/releases/download/v9.9.9/luma-quick-launch-9.9.9-win-x64.zip', size: 73792323, sha256Url: 'https://github.com/sfex1320/luma-quick-launch/releases/download/v9.9.9/luma-quick-launch-9.9.9-win-x64.zip.sha256' } : null }), 2); return;
      }
      if (req.method === 'update.download') { setTimeout(() => respond({ path: 'C:\\TEMP\\Luma\\updates\\9.9.9\\pkg.zip', verified: true, bytes: 73792323 }), 2); return; }
      setTimeout(() => respond(req.method === 'update.apply' ? { accepted: true, mode: 'portable' } : { accepted: true }), 2);
    } };
  }, { update, preferences: defaults });
  await page.goto('/?mode=native');
  await page.getByRole('button', { name: '设置与备份', exact: true }).click();
}
const calls = (page: Page, method: string) => page.evaluate(method => (window as any).__calls.filter((entry: any) => entry.method === method), method);

test('update card checks, downloads with verification and applies for portable mode', async ({ page }) => {
  await setup(page, { hasUpdate: true });
  page.on('dialog', dialog => void dialog.accept());
  const card = page.locator('.update-card');
  await expect(card).toBeVisible();
  await card.getByRole('button', { name: '检查更新', exact: true }).click();
  await expect(card).toContainText('v9.9.9 · 可更新');
  await expect(card).toContainText('便携版 · 下载校验后直接替换文件');
  await card.getByRole('button', { name: '下载并更新到 v9.9.9', exact: true }).click();
  await expect.poll(() => calls(page, 'update.download')).toHaveLength(1);
  await expect.poll(() => calls(page, 'update.apply')).toHaveLength(1);
  expect((await calls(page, 'update.apply'))[0].params.path).toContain('pkg.zip');
});

test('up to date state and unreachable releases show honest states without applying anything', async ({ page }) => {
  await setup(page, {});
  const card = page.locator('.update-card');
  await card.getByRole('button', { name: '检查更新', exact: true }).click();
  await expect(card).toContainText('已是最新');
  await page.reload();
  await page.getByRole('button', { name: '设置与备份', exact: true }).click();
  await card.getByRole('button', { name: '检查更新', exact: true }).click();
  await expect(card).toContainText('已是最新');
  expect(await calls(page, 'update.apply')).toHaveLength(0);
});

test('a release check failure shows the message and never downloads', async ({ page }) => {
  await setup(page, { fail: '无法获取发布信息，请确认仓库已公开或网络可用' });
  const card = page.locator('.update-card');
  await card.getByRole('button', { name: '检查更新', exact: true }).click();
  await expect(card.getByRole('alert')).toContainText('仓库已公开');
  expect(await calls(page, 'update.download')).toHaveLength(0);
  expect(await calls(page, 'update.apply')).toHaveLength(0);
});
