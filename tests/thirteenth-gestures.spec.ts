import { test, expect, type Page, type Locator } from '@playwright/test';
import { defaults } from '../src/data';

async function setup(page: Page) {
  const entry = (id: string, kind = 'app') => ({ id, name: id, kind, path: `C:\\Test\\${id}${kind === 'app' ? '.exe' : ''}` });
  const state = { schemaVersion: 1, revision: 1, preferences: { ...defaults, autoHide: false, recentLimit: 10 }, projects: [
    { id: 'group', name: '软件组', description: '', color: 'mint', pinned: true, items: Array.from({ length: 36 }, (_, i) => entry(`软件${i}`)) },
    { id: 'folder', name: '目录', description: '', color: 'mint', pinned: true, items: [entry('目录', 'folder')] },
    { id: 'single', name: '单软件', description: '', color: 'mint', pinned: true, items: [entry('单软件')] },
  ] };
  await page.addInitScript(state => {
    const w = window as any, listeners: any[] = []; w.__calls = [];
    w.chrome ||= {}; w.chrome.webview = { addEventListener: (_: string, fn: any) => listeners.push(fn), postMessage(req: any) {
      w.__calls.push(req);
      let result: any = { accepted: true };
      if (req.method === 'app.getState') result = state;
      if (req.method === 'window.sync') result = { applied: true };
      if (req.method === 'project.detectTest') result = { task: null };
      if (req.method === 'folder.getThumbnail' || req.method === 'shell.getIcon') result = { dataUrl: null };
      if (req.method === 'folder.list') result = { folderId: req.params.folderId ?? 'root', name: req.params.folderId ?? '目录', parentId: null, truncated: false, entries: Array.from({ length: 40 }, (_, i) => ({ id: `${req.params.folderId ?? 'root'}-${i}`, name: `目录项${i}`, kind: i === 0 ? 'folder' : 'file' })) };
      if (req.method === 'shell.getRecent') result = { entries: Array.from({ length: 10 }, (_, i) => ({ id: `recent${i}`, name: `最近文件${i}`, path: `C:\\Test\\recent${i}.psd`, kind: 'file' })), note: '最近项目' };
      setTimeout(() => listeners.forEach(fn => fn({ data: { protocol: 1, type: 'response', id: req.id, ok: true, result } })), 5);
      if (req.method === 'app.getState') setTimeout(() => listeners.forEach(fn => fn({ data: { protocol: 1, type: 'event', event: 'window.visibility', data: { visible: true, visibilityId: 1 } } })), 60);
    } };
  }, state);
  await page.goto('/?view=dock&mode=native');
  await expect(page.locator('.dock')).toBeVisible();
  await expect.poll(() => page.locator('.dock-wrap').evaluate(node => node.getAnimations().every(a => a.playState === 'finished'))).toBe(true);
}
const project = (page: Page, name: string) => page.getByRole('button', { name: `打开 ${name} 主目录，长按展开堆叠`, exact: true });
const opens = (page: Page) => page.evaluate(() => (window as any).__calls.filter((c: any) => ['shell.openItem', 'folder.open', 'shell.openRecent'].includes(c.method)));

async function blankPoint(list: Locator) {
  return list.evaluate(node => {
    const r = node.getBoundingClientRect();
    // The grid/list left padding remains blank even while its content scrolls.
    for (let x = r.left + 1; x < r.left + 20; x++) for (let y = r.bottom - 20; y > r.top + 80; y--) {
      if (document.elementFromPoint(x, y) === node) return { x, y };
    }
    throw new Error('No blank grab area');
  });
}

async function grab(page: Page, list: Locator) {
  await expect(list).toBeVisible();
  const before = await list.evaluate(node => node.scrollTop);
  const start = await blankPoint(list);
  await page.mouse.move(start.x, start.y); await page.mouse.down();
  await page.mouse.move(start.x, start.y - 5);
  expect(await list.evaluate(node => node.scrollTop)).toBe(before);
  await page.mouse.move(start.x, start.y - 85, { steps: 4 });
  await expect.poll(() => list.evaluate(node => node.scrollTop)).toBeGreaterThan(before + 50);
  await expect(list).toHaveCSS('cursor', 'grabbing');
  await page.mouse.up();
  await expect(list).toHaveCSS('cursor', 'grab');
  expect(await opens(page)).toHaveLength(0);
}

test('software group hover has one full-width enter action and no corner enter controls in either mode', async ({ page }) => {
  await setup(page);
  await project(page, '软件组').hover();
  const overlay = page.locator('[data-drop-project="group"] .split-folder-actions');
  await expect(overlay).toBeVisible();
  await expect(overlay.getByRole('button')).toHaveCount(1);
  const enter = overlay.getByRole('button', { name: '进入 软件组 子菜单', exact: true });
  await expect(enter).toContainText('进入');
  expect(await enter.evaluate(node => Math.abs(node.getBoundingClientRect().width - node.parentElement!.getBoundingClientRect().width))).toBeLessThan(1);
  await enter.click(); await expect(page.getByLabel('软件组 组内入口')).toBeVisible();
  expect(await opens(page)).toHaveLength(0);
  await expect(page.locator('.stack-chevron,.shortcut-enter')).toHaveCount(0);
  await page.getByRole('button', { name: '整理图标', exact: true }).click();
  await project(page, '软件组').press('ArrowDown');
  await expect(page.getByRole('button', { name: '移除快捷项 软件0', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: '将 软件0 移到主面板', exact: true })).toBeVisible();
  await expect(page.locator('.stack-chevron,.shortcut-enter')).toHaveCount(0);
  await project(page, '软件组').click({ button: 'right' });
  await page.getByRole('menuitem', { name: '进入子菜单', exact: true }).click();
  await expect(page.getByLabel('软件组 组内入口')).toBeVisible();
});

test('single software and folder keep their two hover actions', async ({ page }) => {
  await setup(page);
  await project(page, '单软件').hover();
  await expect(page.getByRole('button', { name: '查看 单软件 最近项目', exact: true })).toContainText('最近');
  await expect(page.getByRole('button', { name: '打开 单软件', exact: true })).toContainText('打开软件');
  await project(page, '目录').hover();
  await expect(page.getByRole('button', { name: '浏览 目录 子目录', exact: true })).toContainText('进入');
  await expect(page.getByRole('button', { name: '打开 目录', exact: true })).toBeVisible();
});

test('quick click on a software group enters without launching its first software', async ({ page }) => {
  await setup(page); await project(page, '软件组').click();
  await expect(page.getByLabel('软件组 组内入口')).toBeVisible();
  expect(await opens(page)).toHaveLength(0);
});

test('left blank grab pans group, every retained directory level, and recent list without launching', async ({ page }) => {
  await setup(page);
  await project(page, '软件组').press('ArrowDown'); await grab(page, page.locator('.group-column .launch-grid'));
  await project(page, '目录').press('ArrowDown');
  await expect(page.locator('[data-folder-level="0"] .directory-items')).toBeVisible();
  await page.locator('.stack-item[data-item-id="root-0"]').press('ArrowRight');
  await grab(page, page.locator('[data-folder-level="0"] .directory-items'));
  await grab(page, page.locator('[data-folder-level="1"] .directory-items'));
  await project(page, '单软件').press('ArrowDown'); await grab(page, page.locator('.recent-list'));
});

test('blank grab releases on blur, lost capture and cancellation; right drag keeps directory context menu', async ({ page }) => {
  await setup(page); await project(page, '目录').press('ArrowDown');
  const list = page.locator('.directory-items'); await expect(list).toBeVisible();
  for (const event of ['blur', 'pointercancel', 'lostpointercapture']) {
    await list.evaluate(node => { node.scrollTop = 0; });
    const p = await blankPoint(list);
    await page.mouse.move(p.x, p.y); await page.mouse.down(); await page.mouse.move(p.x, p.y - 30);
    await expect.poll(() => list.evaluate(node => node.scrollTop)).toBeGreaterThan(20);
    if (event === 'blur') await page.evaluate(() => window.dispatchEvent(new Event('blur')));
    else await list.dispatchEvent(event, { pointerId: 1 });
    const stopped = await list.evaluate(node => node.scrollTop);
    await page.mouse.move(p.x, p.y - 80); await page.mouse.up();
    expect(await list.evaluate(node => node.scrollTop)).toBe(stopped);
    await expect(list).toHaveCSS('cursor', 'grab');
  }
  await list.evaluate(node => { node.scrollTop = 0; });
  const p = await blankPoint(list); await page.mouse.move(p.x, p.y); await page.mouse.down({ button: 'right' });
  await page.mouse.move(p.x, p.y - 70); await page.mouse.up({ button: 'right' });
  expect(await list.evaluate(node => node.scrollTop)).toBe(0);
  await expect(page.getByRole('menu', { name: '快捷操作' })).toBeVisible();
  expect(await opens(page)).toHaveLength(0);
});

test('grab does not steal a file press or a wheel scroll', async ({ page }) => {
  await setup(page); await project(page, '目录').press('ArrowDown');
  const list = page.locator('.directory-items'); await expect(list).toBeVisible();
  const tile = page.locator('.stack-item[data-item-id="root-1"]'), p = await tile.boundingBox();
  await page.mouse.move(p!.x + 30, p!.y + 30); await page.mouse.down(); await page.mouse.move(p!.x + 30, p!.y + 10); await page.mouse.up();
  expect(await list.evaluate(node => node.scrollTop)).toBe(0);
  expect(await opens(page)).toHaveLength(0);
  await page.mouse.wheel(0, 150);
  await expect.poll(() => list.evaluate(node => node.scrollTop)).toBeGreaterThan(0);
});
