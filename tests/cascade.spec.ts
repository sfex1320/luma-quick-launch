import { test, expect, type Page, type Locator } from '@playwright/test';
import { defaults } from '../src/data';

const seed = { schemaVersion: 1, revision: 1, preferences: defaults, projects: [{ id: 'repo', name: '仓库', description: '', color: 'mint', pinned: true, items: [{ id: 'main', name: '仓库', path: 'C:\\Projects\\Repo', kind: 'folder' }] }] };
const listings = {
  root: { folderId: 'root', name: '仓库', parentId: null, truncated: false, entries: [{ id: 'src', name: '源码', kind: 'folder' }, { id: 'image', name: '图片.png', kind: 'file' }, { id: 'unsupported', name: '设计.cdr', kind: 'file' }] },
  src: { folderId: 'src', name: '源码', parentId: 'root', truncated: false, entries: [{ id: 'components', name: '组件', kind: 'folder' }] },
  components: { folderId: 'components', name: '组件', parentId: 'src', truncated: false, entries: [{ id: 'file', name: 'App.tsx', kind: 'file' }] },
};
const png = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLttAAAAABJRU5ErkJggg==';
async function setup(page: Page, dock = true, grouped = false) {
  const initial = structuredClone(seed);
  if (grouped) initial.projects[0].items.push({ id: 'tool', name: '工具', path: 'C:\\Tools\\tool.exe', kind: 'app' });
  await page.addInitScript(({ seed, listings, png }) => {
    const host = window as any, listeners: Array<(event: any) => void> = [];
    let state = seed; host.__calls = []; host.__state = state;
    host.chrome ||= {}; host.chrome.webview = { addEventListener: (_: string, cb: any) => listeners.push(cb), postMessage: (req: any) => {
      host.__calls.push(req); let result: any = { accepted: true };
      if (req.method === 'app.getState') result = state;
      if (req.method === 'app.saveState') { state = { ...req.params.state, revision: state.revision + 1 }; host.__state = state; result = state; }
      if (req.method === 'window.sync') result = { applied: true };
      if (req.method === 'folder.list') result = (listings as any)[req.params.folderId ?? 'root'];
      if (req.method === 'folder.getThumbnail') result = { dataUrl: req.params.entryId === 'image' ? png : null };
      if (req.method === 'project.detectTest') result = { task: null };
      const send = () => listeners.forEach(cb => cb({ data: { protocol: 1, type: 'response', id: req.id, ok: true, result } }));
      setTimeout(send, 5);
      if (req.method === 'app.getState') setTimeout(() => listeners.forEach(cb => cb({ data: { protocol: 1, type: 'event', event: 'window.visibility', data: { visible: true, visibilityId: 1 } } })), 90);
    } };
  }, { seed: initial, listings, png });
  await page.goto(dock ? '/?view=dock&mode=native' : '/?mode=native');
  if (dock) { await expect(page.locator('.dock')).toBeVisible(); await expect.poll(() => page.locator('.dock-wrap').evaluate(el => el.getAnimations().every(a => a.playState === 'finished'))).toBe(true); }
}
const tile = (page: Page, id: string) => page.locator(`.stack-item[data-item-id="${id}"]`);
async function moveTo(page: Page, element: Locator) { const box = await element.boundingBox(); expect(box).not.toBeNull(); await page.mouse.move(box!.x + box!.width / 2, box!.y + box!.height / 2, { steps: 5 }); }
const calls = (page: Page, method: string) => page.evaluate(method => (window as any).__calls.filter((c: any) => c.method === method), method);

for (const half of ['split-open', 'split-enter']) test(`holding the visible ${half} enters root and child without breaking capture`, async ({ page }) => {
  await setup(page);
  await page.locator('.dock-project').hover();
  const button = page.locator(`.dock-split .${half}`);
  await expect(button).toBeVisible(); await moveTo(page, button); await page.mouse.down();
  await expect(tile(page, 'src')).toBeVisible();
  await moveTo(page, tile(page, 'src')); await expect(tile(page, 'components')).toBeVisible();
  await moveTo(page, tile(page, 'components')); await expect(tile(page, 'file')).toBeVisible();
  await moveTo(page, tile(page, 'file')); await page.mouse.up();
  expect((await calls(page, 'folder.open')).map((call: any) => call.params.entryId)).toEqual(['file']);
  expect(await calls(page, 'shell.openItem')).toHaveLength(0);
});

test('continuous hold expands three retained directory levels and release launches the final file once', async ({ page }) => {
  await setup(page);
  await moveTo(page, page.locator('.dock-project')); await page.mouse.down();
  await expect(tile(page, 'src')).toBeVisible();
  await moveTo(page, tile(page, 'src'));
  await expect(tile(page, 'components')).toBeVisible();
  await expect(page.locator('.folder-column')).toHaveCount(2);
  await expect(tile(page, 'src')).toBeVisible();
  await moveTo(page, tile(page, 'components'));
  await expect(tile(page, 'file')).toBeVisible();
  await expect(page.locator('.folder-column')).toHaveCount(3);
  expect(await calls(page, 'folder.open')).toHaveLength(0);
  await moveTo(page, tile(page, 'file')); await page.mouse.up();
  await expect.poll(() => calls(page, 'folder.open')).toHaveLength(1);
  expect((await calls(page, 'folder.open'))[0].params).toEqual({ projectId: 'repo', itemId: 'main', entryId: 'file' });
});

test('hover divides a folder into large Open and Enter targets; quick click stays a direct open', async ({ page }) => {
  await setup(page);
  await page.locator('.dock-project').click();
  expect(await calls(page, 'shell.openItem')).toHaveLength(1);
  await page.getByRole('button', { name: '打开 仓库 主目录，长按展开堆叠', exact: true }).press('ArrowDown');
  await tile(page, 'src').hover();
  const enter = page.getByRole('button', { name: '浏览 源码 子目录', exact: true });
  await expect(enter).toBeVisible();
  await expect.poll(async () => (await enter.boundingBox())!.width).toBe(44);
  await enter.click();
  await expect(page.locator('.folder-column')).toHaveCount(2);
  await expect(tile(page, 'src')).toBeVisible();
  expect(await calls(page, 'folder.open')).toHaveLength(0);
  await tile(page, 'components').click();
  await expect.poll(() => calls(page, 'folder.open')).toHaveLength(1);
  expect((await calls(page, 'folder.open'))[0].params.entryId).toBe('components');
});

test('file content is confined to static icon thumbnails with unsupported files falling back', async ({ page }) => {
  await setup(page);
  await page.getByRole('button', { name: '打开 仓库 主目录，长按展开堆叠', exact: true }).press('ArrowDown');
  await expect(tile(page, 'image').locator('img.file-thumbnail')).toBeVisible();
  await expect(tile(page, 'unsupported').locator('img')).toHaveCount(0);
  expect(await calls(page, 'folder.open')).toHaveLength(0);
  await expect(page.locator('video,dialog,[role=dialog]')).toHaveCount(0);
  expect((await calls(page, 'folder.getThumbnail')).every((c: any) => !('path' in c.params))).toBe(true);
});

test('manual command and working directory are saved literally without launching and can return to automatic', async ({ page }) => {
  await setup(page, false);
  await page.getByRole('navigation', { name: '驾驶舱导航' }).getByRole('button', { name: /项目/ }).click();
  await page.getByRole('button', { name: '修改 仓库', exact: true }).click();
  await page.getByRole('checkbox', { name: '入口 1 · 手动启动命令', exact: true }).check();
  await page.getByLabel('入口 1 启动工作目录', { exact: true }).fill('D:\\Work\\Other');
  await page.getByLabel('入口 1 启动命令', { exact: true }).fill('npm run dev && echo ready ');
  await page.getByRole('button', { name: '保存项目', exact: true }).click();
  await expect.poll(() => page.evaluate(() => (window as any).__state.projects[0].items[0].launch)).toEqual({ command: 'npm run dev && echo ready ', workingDirectory: 'D:\\Work\\Other' });
  expect(await calls(page, 'project.runTest')).toHaveLength(0);
  expect(await calls(page, 'shell.openItem')).toHaveLength(0);
  await page.getByRole('button', { name: '修改 仓库', exact: true }).click();
  await page.getByRole('button', { name: '恢复自动识别', exact: true }).click();
  await page.getByRole('button', { name: '保存项目', exact: true }).click();
  await expect.poll(() => page.evaluate(() => (window as any).__state.projects[0].items[0].launch)).toBeUndefined();
});

test('a held group member retains capture while its directory expands beside the group', async ({ page }) => {
  await setup(page, true, true);
  await page.getByRole('button', { name: '打开 仓库 主目录，长按展开堆叠', exact: true }).press('ArrowDown');
  await moveTo(page, page.locator('.group-column .stack-item[data-item-id="main"]')); await page.mouse.down();
  await page.waitForTimeout(330);
  const source = await page.locator('.group-column .stack-item[data-item-id="main"]').boundingBox();
  await page.mouse.move(source!.x + source!.width / 2 + 2, source!.y + source!.height / 2);
  await expect(tile(page, 'src')).toBeVisible();
  await expect(page.locator('.group-entry-row')).toHaveCount(2);
  await moveTo(page, tile(page, 'image')); await page.mouse.up();
  await expect.poll(() => calls(page, 'folder.open')).toHaveLength(1);
  expect((await calls(page, 'folder.open'))[0].params.entryId).toBe('image');
  expect(await calls(page, 'shell.openItem')).toHaveLength(0);
});

test('releasing outside a cascade cancels; a layout shift alone never launches a different tile', async ({ page }) => {
  await setup(page);
  await moveTo(page, page.locator('.dock-project')); await page.mouse.down();
  await expect(tile(page, 'src')).toBeVisible(); await moveTo(page, tile(page, 'src'));
  await expect(tile(page, 'components')).toBeVisible();
  await page.mouse.up(); // No new physical movement after columns changed.
  expect(await calls(page, 'folder.open')).toHaveLength(0);
  await moveTo(page, tile(page, 'components')); await page.mouse.down();
  await page.waitForTimeout(340);
  await page.mouse.move(10, 900); await page.mouse.up();
  expect(await calls(page, 'folder.open')).toHaveLength(0);
  await expect(page.locator('.stack-panel')).toHaveCount(0);
});

test('narrow cascades scroll to children, retain ancestors and keep native hit regions on screen', async ({ page }) => {
  await page.setViewportSize({ width: 340, height: 640 }); await setup(page);
  await page.getByRole('button', { name: '打开 仓库 主目录，长按展开堆叠', exact: true }).press('ArrowDown');
  await tile(page, 'src').press('ArrowRight'); await expect(tile(page, 'components')).toBeVisible();
  await tile(page, 'components').press('ArrowRight'); await expect(tile(page, 'file')).toBeVisible();
  await expect(page.locator('.folder-column')).toHaveCount(3);
  const panel = await page.locator('.stack-panel').boundingBox();
  expect(panel!.x).toBeGreaterThanOrEqual(0); expect(panel!.x + panel!.width).toBeLessThanOrEqual(340);
  await expect.poll(async () => (await calls(page, 'window.sync')).at(-1).params.rects.every((r: any) => r.x >= 0 && r.x + r.width <= 340)).toBe(true);
  await tile(page, 'src').scrollIntoViewIfNeeded();
  await tile(page, 'src').hover();
  await page.getByRole('button', { name: '打开 源码', exact: true }).click();
  await expect.poll(() => calls(page, 'folder.open')).toHaveLength(1);
  expect((await calls(page, 'folder.open'))[0].params.entryId).toBe('src');
});
