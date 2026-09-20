import { test, expect, type Page } from '@playwright/test';
import { defaults } from '../src/data';
import type { AppState } from '../src/contracts';

const initial: AppState = { schemaVersion: 1, revision: 12, preferences: defaults, projects: [
  { id: 'apps', name: '软件', description: '', color: 'blue', pinned: true, items: [{ id: 'main', name: '编辑器', path: 'C:\\Apps\\Editor.exe', kind: 'app' }, { id: 'second', name: '画图', path: 'C:\\Apps\\Paint.exe', kind: 'app' }] },
  { id: 'folder', name: '资料', description: '目标描述', color: 'mint', pinned: true, items: [{ id: 'main', name: '资料目录', path: 'C:\\Work', kind: 'folder' }] },
] };

async function attachHost(page: Page, failSave = false) {
  await page.addInitScript(({ initial, failSave }) => {
    const host = window as any;
    let state = structuredClone(initial);
    const listeners: Array<(event: any) => void> = [];
    host.__calls = []; host.__hostState = state;
    host.chrome ||= {};
    host.chrome.webview = { addEventListener: (_: string, listener: any) => listeners.push(listener), postMessage: (request: any) => {
      host.__calls.push(request);
      let result: unknown = { accepted: true };
      if (request.method === 'app.getState') result = state;
      else if (request.method === 'app.saveState' && !failSave) { state = { ...request.params.state, revision: state.revision + 1 }; host.__hostState = state; result = state; }
      else if (request.method === 'window.sync') result = { applied: true };
      const failed = request.method === 'app.saveState' && failSave;
      setTimeout(() => listeners.forEach(listener => listener({ data: { protocol: 1, type: 'response', id: request.id, ok: !failed, ...(failed ? { error: { code: 'REVISION_CONFLICT', message: '配置冲突，请重新载入' } } : { result }) } })), 15);
    } };
  }, { initial, failSave });
  await page.goto('/?mode=native&section=projects');
  await expect(page.getByRole('heading', { name: '快捷项与堆叠' })).toBeVisible();
}

test('settings combines software with a folder using CAS without launch, then splits in member order', async ({ page }) => {
  await attachHost(page);
  const apps = page.locator('.project-card').filter({ has: page.getByRole('button', { name: '修改 软件', exact: true }) });
  await expect(apps.locator('.group-icon .crystal-item-app')).toHaveCount(2);
  await page.getByRole('button', { name: '组合 软件', exact: true }).click();
  await page.getByRole('button', { name: '合并到 资料', exact: true }).click();
  await expect.poll(() => page.evaluate(() => (window as any).__hostState.projects.length)).toBe(1);
  const saved = await page.evaluate(() => (window as any).__hostState);
  expect(saved.projects[0].items.map((i: any) => i.name)).toEqual(['资料目录', '编辑器', '画图']);
  expect(saved.projects[0].items.map((i: any) => i.kind)).toEqual(['folder', 'app', 'app']);
  expect(saved.projects[0].name).toBe('资料');
  expect(saved.projects[0].color).toBe('mint');
  const saves = await page.evaluate(() => (window as any).__calls.filter((call: any) => call.method === 'app.saveState'));
  expect(saves).toHaveLength(1);
  expect(saves[0].params.expectedRevision).toBe(12);
  await page.getByRole('button', { name: '拆分 资料', exact: true }).click();
  await expect.poll(() => page.evaluate(() => (window as any).__hostState.projects.map((p: any) => p.name))).toEqual(['资料目录', '编辑器', '画图']);
  const calls = await page.evaluate(() => (window as any).__calls);
  expect(calls.filter((call: any) => call.method === 'app.saveState').map((call: any) => call.params.expectedRevision)).toEqual([12, 13]);
  expect(calls.some((call: any) => call.method === 'shell.openItem' || call.method === 'folder.open')).toBe(false);
});

test('a failed CAS blocks subsequent split without changing the visible group or issuing another save', async ({ page }) => {
  await attachHost(page, true);
  await page.getByRole('button', { name: '组合 软件', exact: true }).click();
  await page.getByRole('button', { name: '合并到 资料', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('配置冲突');
  await page.getByRole('button', { name: '拆分 资料', exact: true }).click();
  await expect(page.getByRole('status')).toContainText('重新载入');
  await expect(page.locator('.project-card')).toHaveCount(1);
  expect(await page.evaluate(() => (window as any).__calls.filter((call: any) => call.method === 'app.saveState'))).toHaveLength(1);
  expect(await page.evaluate(() => (window as any).__hostState)).toEqual(initial);
});
