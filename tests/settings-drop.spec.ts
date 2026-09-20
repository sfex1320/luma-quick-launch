import { test, expect, type Page, type Locator } from '@playwright/test';
import { initialState } from '../src/data';

async function attachHost(page: Page, initial = initialState) {
  await page.addInitScript(initial => {
    const host = window as any;
    let state = structuredClone(initial);
    const listeners: Array<(event: any) => void> = [];
    host.__calls = []; host.__hostState = state; host.__delayDrop = false;
    const send = (req: any, files?: File[]) => {
      host.__calls.push({ ...req, files: files?.map(file => file.name) });
      let result: unknown;
      if (req.method === 'app.getState') result = state;
      else if (req.method === 'app.saveState') { state = { ...req.params.state, revision: state.revision + 1 }; host.__hostState = state; result = state; }
      else if (req.method === 'shell.resolveDrop') result = files?.map(file => ({ name: file.name, path: `C:\\Drop\\${file.name}`, kind: 'folder' }));
      else if (req.method === 'window.sync') result = { applied: true };
      else result = { accepted: true };
      const respond = () => listeners.forEach(listener => listener({ data: { protocol: 1, type: 'response', id: req.id, ok: true, result } }));
      if (req.method === 'shell.resolveDrop' && host.__delayDrop) host.__finishDrop = respond;
      else setTimeout(respond, 20);
    };
    host.chrome ||= {};
    host.chrome.webview = { addEventListener: (_: string, listener: any) => listeners.push(listener), postMessage: send, postMessageWithAdditionalObjects: send };
  }, initial);
  await page.goto('/?mode=native&section=projects');
  await expect(page.getByRole('heading', { name: '快捷项与堆叠' })).toBeVisible();
}
async function drop(page: Page, target: Locator, names: string[]) {
  const data = await page.evaluateHandle(names => { const data = new DataTransfer(); names.forEach(name => data.items.add(new File([''], name))); return data; }, names);
  await target.dispatchEvent('dragover', { dataTransfer: data });
  await target.dispatchEvent('drop', { dataTransfer: data });
  await data.dispose();
}
test('management blank and project card drops save once without launching', async ({ page }) => {
  await attachHost(page);
  await drop(page, page.locator('.cp-content'), ['独立目录']);
  await expect.poll(() => page.evaluate(() => (window as any).__hostState.projects.some((p: any) => p.name === '独立目录'))).toBe(true);
  const originalCount = initialState.projects[0].items.length;
  await drop(page, page.locator('.project-card').filter({ has: page.getByRole('button', { name: '修改 品牌设计', exact: true }) }), ['目录一', '目录二', '目录三', '目录四', '目录五']);
  await expect.poll(() => page.evaluate(() => (window as any).__hostState.projects[0].items.length)).toBe(originalCount + 5);
  const calls = await page.evaluate(() => (window as any).__calls);
  expect(calls.filter((c: any) => c.method === 'shell.resolveDrop')).toHaveLength(2);
  expect(calls.filter((c: any) => c.method === 'shell.openItem')).toHaveLength(0);
});
test('editor drop replaces blank placeholder, deduplicates and preserves edits while resolving', async ({ page }) => {
  await attachHost(page);
  await page.getByRole('button', { name: '新建项目', exact: true }).first().click();
  await page.evaluate(() => { (window as any).__delayDrop = true; });
  await drop(page, page.locator('dialog form'), ['一', '二', '三', '四', '五', '五']);
  await page.getByLabel('项目名称', { exact: true }).fill('我的五个目录');
  await page.getByLabel('一句描述', { exact: false }).fill('等待期间编辑');
  await page.evaluate(() => (window as any).__finishDrop());
  await expect(page.getByLabel('入口 5 名称', { exact: true })).toHaveValue('五');
  await expect(page.getByLabel('入口 6 名称', { exact: true })).toHaveCount(0);
  await expect(page.getByLabel('项目名称', { exact: true })).toHaveValue('我的五个目录');
  await page.getByRole('button', { name: '创建项目', exact: true }).click();
  await expect.poll(() => page.evaluate(() => (window as any).__hostState.projects.at(-1).name)).toBe('我的五个目录');
  expect(await page.evaluate(() => (window as any).__hostState.projects.at(-1).description)).toBe('等待期间编辑');
});
test('closing a draft during resolve does not create or modify a project', async ({ page }) => {
  await attachHost(page);
  await page.getByRole('button', { name: '新建项目', exact: true }).first().click();
  await page.evaluate(() => { (window as any).__delayDrop = true; });
  await drop(page, page.locator('dialog form'), ['迟到的目录']);
  await page.getByRole('button', { name: '取消', exact: true }).click();
  await page.getByRole('button', { name: '新建项目', exact: true }).first().click();
  await page.evaluate(() => (window as any).__finishDrop());
  await expect(page.getByLabel('入口 1 路径', { exact: true })).toHaveValue('');
  expect(await page.evaluate(() => (window as any).__hostState.projects.length)).toBe(initialState.projects.length);
});
test('browser preview explains desktop requirement and blocks file navigation', async ({ page }) => {
  await page.goto('/?section=projects');
  await drop(page, page.locator('.cp-content'), ['目录']);
  await expect(page.getByRole('status')).toContainText('桌面程序');
  await expect(page).toHaveURL(/section=projects/);
});
test('editor append preserves saved IDs and rejects overflow without losing any entry', async ({ page }) => {
  const initial = structuredClone(initialState);
  initial.projects[0].items = Array.from({ length: 199 }, (_, n) => ({ id: `item-${n}`, name: `entry-${n}`, path: `C:\\entry-${n}`, kind: 'folder' }));
  await attachHost(page, initial);
  await page.getByRole('button', { name: '修改 品牌设计', exact: true }).click();
  await drop(page, page.locator('dialog form'), ['过量一', '过量二']);
  await expect(page.getByRole('alert')).toContainText('本次未添加');
  await expect(page.getByLabel('入口 199 名称', { exact: true })).toHaveValue('entry-198');
  await expect(page.getByLabel('入口 200 名称', { exact: true })).toHaveCount(0);
  await drop(page, page.locator('dialog form'), ['最后一个']);
  await expect(page.getByLabel('入口 200 名称', { exact: true })).toHaveValue('最后一个');
  await page.getByRole('button', { name: '保存项目', exact: true }).click();
  await expect.poll(() => page.evaluate(() => (window as any).__hostState.projects[0].items.length)).toBe(200);
  expect(await page.evaluate(() => (window as any).__hostState.projects[0].items.slice(0, 199))).toEqual(initial.projects[0].items);
});
test('project drag handle reorders without file import and arrow buttons remain usable', async ({ page }) => {
  await attachHost(page);
  const first = initialState.projects[0], second = initialState.projects[1];
  const target = page.locator('.project-card').filter({ has: page.getByRole('button', { name: `修改 ${second.name}`, exact: true }) });
  await page.getByRole('button', { name: `拖动排序 ${first.name}`, exact: true }).dragTo(target);
  await expect.poll(() => page.evaluate(() => (window as any).__hostState.projects[1].id)).toBe(first.id);
  await page.getByRole('button', { name: `向前移动 ${first.name}`, exact: true }).click();
  await expect.poll(() => page.evaluate(() => (window as any).__hostState.projects[0].id)).toBe(first.id);
  expect(await page.evaluate(() => (window as any).__calls.filter((call: any) => call.method === 'shell.resolveDrop'))).toHaveLength(0);
});
test('dragging text paths never creates shortcuts', async ({ page }) => {
  await attachHost(page);
  const data = await page.evaluateHandle(() => { const data = new DataTransfer(); data.setData('text/plain', 'C:\\not-a-file-object'); return data; });
  await page.locator('.cp-content').dispatchEvent('drop', { dataTransfer: data });
  await data.dispose();
  expect(await page.evaluate(() => (window as any).__hostState.projects.length)).toBe(initialState.projects.length);
  expect(await page.evaluate(() => (window as any).__calls.filter((call: any) => call.method === 'shell.resolveDrop'))).toHaveLength(0);
});
