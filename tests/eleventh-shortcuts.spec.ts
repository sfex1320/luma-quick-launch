import { test, expect, type Page, type Locator } from '@playwright/test';
import { defaults } from '../src/data';

const icon = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLttAAAAABJRU5ErkJggg==';
const seed: any = { schemaVersion: 1, revision: 1, preferences: { ...defaults, width: 480, recentLimit: 6, autoHide: false }, projects: [
  { id: 'apps', name: '创作', description: '', color: 'mint', pinned: true, items: [
    { id: 'ps', name: 'Photoshop', path: 'C:\\Adobe\\Photoshop.exe', kind: 'app' },
    { id: 'assets', name: '素材', path: 'C:\\Assets', kind: 'folder' },
  ] },
  { id: 'docs', name: '资料', description: '', color: 'gold', pinned: true, items: [{ id: 'doc', name: '文档', path: 'C:\\Docs', kind: 'folder' }] },
] };

async function host(page: Page, initial = seed) {
  await page.addInitScript(({ initial, icon }) => {
    const w = window as any, listeners: any[] = []; let state = structuredClone(initial);
    w.__calls = []; w.__state = () => structuredClone(state);
    const reply = (req: any, result: any) => setTimeout(() => listeners.forEach(fn => fn({ data: { protocol: 1, type: 'response', id: req.id, ok: true, result } })), 5);
    w.chrome ||= {}; w.chrome.webview = { addEventListener: (_: string, fn: any) => listeners.push(fn), postMessage: (req: any) => {
      w.__calls.push(req); let result: any = { accepted: true };
      if (req.method === 'app.getState') result = state;
      else if (req.method === 'app.saveState') { state = { ...req.params.state, revision: state.revision + 1 }; result = state; }
      else if (req.method === 'website.inspect') result = { url: 'https://example.com/work', title: 'Example 工作台', dataUrl: icon };
      else if (req.method === 'shell.getRecent') result = { entries: Array.from({ length: req.params.limit }, (_, i) => ({ id: `r${i}`, name: `项目 ${i}`, path: `C:\\完整路径\\项目 ${i}.psd`, kind: 'file' })), note: 'Windows 最近项目' };
      else if (req.method === 'folder.list') result = { folderId: 'root', name: '目录', parentId: null, truncated: false, entries: Array.from({ length: 20 }, (_, i) => ({ id: `f${i}`, name: `文件 ${i}`, kind: 'file' })) };
      else if (req.method === 'window.sync') result = { applied: true };
      reply(req, result);
      if (req.method === 'app.getState') setTimeout(() => listeners.forEach(fn => fn({ data: { protocol: 1, type: 'event', event: 'window.visibility', data: { visible: true, visibilityId: 2 } } })), 60);
    } };
  }, { initial, icon });
}
const calls = (page: Page, method: string) => page.evaluate(m => (window as any).__calls.filter((x: any) => x.method === m), method);
async function urlDrop(page: Page, target: Locator, type: 'text/plain'|'text/uri-list') {
  const dt = await page.evaluateHandle(({ type }) => { const d = new DataTransfer(); d.setData(type, 'https://example.com/work\r\n'); return d; }, { type });
  await target.dispatchEvent('dragover', { dataTransfer: dt }); await target.dispatchEvent('drop', { dataTransfer: dt }); await dt.dispose();
}

test('URL drops at root and into a collection preserve inspected identity, icon, reference and editable note', async ({ page }) => {
  await host(page); await page.goto('/?mode=native&section=projects');
  await urlDrop(page, page.locator('.cp-content'), 'text/plain');
  await expect.poll(() => page.evaluate(() => (window as any).__state().projects.some((p: any) => p.items[0].path === 'https://example.com/work'))).toBe(true);
  const card = page.locator('.project-card').filter({ has: page.getByRole('button', { name: '修改 资料', exact: true }) });
  await urlDrop(page, card, 'text/uri-list');
  await expect.poll(() => page.evaluate(() => (window as any).__state().projects.find((p: any) => p.id === 'docs').items.length)).toBe(2);
  await page.getByRole('button', { name: '修改 资料', exact: true }).click();
  await expect(page.getByLabel('入口 2 名称')).toHaveValue('Example 工作台');
  await expect(page.getByLabel('入口 2 路径')).toHaveValue('https://example.com/work');
  await page.getByLabel('入口 2 备注').fill('客户参考页'); await page.getByRole('button', { name: '保存项目' }).click();
  await expect.poll(() => page.evaluate(() => (window as any).__state().projects.find((p: any) => p.id === 'docs').items[1])).toMatchObject({ kind: 'url', name: 'Example 工作台', path: 'https://example.com/work', websiteIcon: icon, note: '客户参考页' });
});

test('software hover opens a complete configured recent list and launches by IDs', async ({ page }) => {
  await host(page); await page.goto('/?view=dock&mode=native'); await expect(page.locator('.dock')).toBeVisible();
  await page.getByRole('button', { name: '打开 创作 主目录，长按展开堆叠', exact: true }).press('ArrowDown');
  await page.locator('.group-entry-row .split-folder').filter({ hasText: 'Photoshop' }).hover();
  await page.getByRole('button', { name: '查看 Photoshop 最近项目' }).click();
  await expect(page.getByLabel('创作 组内入口')).toBeVisible();
  await expect(page.getByText('C:\\完整路径\\项目 5.psd')).toBeVisible();
  const columns = await page.locator('.stack-body-cascade > .folder-column').evaluateAll(nodes => nodes.map(node => { const r = node.getBoundingClientRect(); return { left: r.left, right: r.right }; }));
  expect(columns).toHaveLength(2);
  expect(columns[0].right).toBeLessThanOrEqual(columns[1].left);
  expect(columns[1].right).toBeLessThanOrEqual(await page.evaluate(() => innerWidth));
  expect(columns[1].right).toBeLessThanOrEqual(await page.locator('.stack-panel').evaluate(node => node.getBoundingClientRect().right));
  expect((await calls(page, 'shell.getRecent')).at(-1).params).toEqual({ projectId: 'apps', itemId: 'ps', limit: 6 });
  await page.getByRole('button', { name: /项目 0/ }).click();
  await expect.poll(() => calls(page, 'shell.openRecent')).toHaveLength(1);
  expect((await calls(page, 'shell.openRecent'))[0].params).toEqual({ projectId: 'apps', itemId: 'ps', entryId: 'r0' });
});

test('long-pressing software enters recent items without dismissing the parent group', async ({ page }) => {
  await host(page); await page.goto('/?view=dock&mode=native'); await page.getByRole('button', { name: '打开 创作 主目录，长按展开堆叠', exact: true }).press('ArrowDown');
  const photoshop = page.locator('.group-entry-row .stack-item[data-item-id="ps"]'), box = await photoshop.boundingBox();
  await page.mouse.move(box!.x + box!.width / 2, box!.y + box!.height / 2); await page.mouse.down(); await page.waitForTimeout(360); await page.mouse.up();
  await expect(page.getByLabel('创作 组内入口')).toBeVisible();
  await expect(page.getByText('C:\\完整路径\\项目 5.psd')).toBeVisible();
  expect(await calls(page, 'shell.openRecent')).toHaveLength(0);
});

test('edit X removes only a reference and extracting a group member preserves the source path', async ({ page }) => {
  await host(page); await page.goto('/?view=dock&mode=native'); await page.getByRole('button', { name: '整理图标' }).click();
  await page.getByRole('button', { name: '打开 创作 主目录，长按展开堆叠', exact: true }).press('ArrowDown');
  await page.getByRole('button', { name: '将 Photoshop 移到主面板' }).click();
  await expect.poll(() => page.evaluate(() => (window as any).__state().projects.find((p: any) => p.items.some((i: any) => i.id === 'ps')).items[0].path)).toBe('C:\\Adobe\\Photoshop.exe');
  await page.getByRole('button', { name: '移除快捷项 资料' }).click();
  await expect.poll(() => page.evaluate(() => (window as any).__state().projects.some((p: any) => p.id === 'docs'))).toBe(false);
  expect(await calls(page, 'shell.openItem')).toHaveLength(0);
});

test('card center merges while an edge reorders when dragging from either icon or handle', async ({ page }) => {
  await host(page); await page.goto('/?mode=native&section=projects');
  const sourceIcon = page.getByRole('button', { name: '编辑 创作' });
  const target = page.locator('.project-card').filter({ has: page.getByRole('button', { name: '修改 资料', exact: true }) });
  await sourceIcon.dragTo(target);
  await expect.poll(() => page.evaluate(() => (window as any).__state().projects.length)).toBe(1);

  const three = structuredClone(seed); three.projects.push({ id: 'more', name: '更多', description: '', color: 'blue', pinned: true, items: [{ id: 'm', name: 'M', path: 'C:\\M', kind: 'folder' }] });
  const p2 = await page.context().newPage(); await host(p2, three); await p2.goto('/?mode=native&section=projects');
  const handle = p2.getByRole('button', { name: '拖动排序 创作' });
  const edge = p2.locator('.project-card').filter({ has: p2.getByRole('button', { name: '修改 更多', exact: true }) });
  const box = await edge.boundingBox(); await handle.hover(); await p2.mouse.down(); await p2.mouse.move(box!.x + box!.width / 2, box!.y + 2); await p2.mouse.up();
  await expect.poll(() => p2.evaluate(() => (window as any).__state().projects.map((p: any) => p.id))).toEqual(['docs', 'apps', 'more']);
});

test('right drag pans without a context menu while a stationary right click keeps the menu', async ({ page }) => {
  const many = structuredClone(seed); many.preferences.width = 320;
  many.projects = Array.from({ length: 12 }, (_, i) => ({ id: `p${i}`, name: `入口${i}`, description: '', color: 'mint', pinned: true, items: [{ id: `i${i}`, name: `目录${i}`, path: `C:\\D${i}`, kind: 'folder' }] }));
  await host(page, many); await page.goto('/?view=dock&mode=native');
  const strip = page.locator('.dock-launchers'); await expect(strip).toBeVisible();
  const box = await strip.boundingBox();
  await page.mouse.move(box!.x + box!.width - 10, box!.y + 20); await page.mouse.down({ button: 'right' });
  await page.mouse.move(box!.x + 20, box!.y + 20, { steps: 4 }); await page.mouse.up({ button: 'right' });
  await strip.dispatchEvent('contextmenu', { clientX: box!.x + 20, clientY: box!.y + 20, button: 2 });
  await expect(page.getByRole('menu', { name: '快捷操作' })).toHaveCount(0);
  const first = page.getByRole('button', { name: /打开 入口0 主目录/ });
  await first.click({ button: 'right' });
  await expect(page.getByRole('menu', { name: '快捷操作' })).toBeVisible();
});

test('a group entry can be dragged out and then dragged into another group without changing its reference', async ({ page }) => {
  const grouped = structuredClone(seed); grouped.projects[1].items.push({ id: 'doc2', name: '文档二', path: 'C:\\Docs2', kind: 'folder' });
  await host(page, grouped); await page.goto('/?view=dock&mode=native'); await page.getByRole('button', { name: '整理图标' }).click();
  await page.getByRole('button', { name: '打开 创作 主目录，长按展开堆叠', exact: true }).press('ArrowDown');
  const source = page.locator('.group-entry-row .stack-item[data-item-id="ps"]');
  await source.dragTo(page.locator('.dock-launchers'));
  await expect.poll(() => page.evaluate(() => (window as any).__state().projects.some((p: any) => p.items.length === 1 && p.items[0].id === 'ps'))).toBe(true);
  await page.getByRole('button', { name: '打开 资料 主目录，长按展开堆叠', exact: true }).press('ArrowDown');
  const detached = page.locator('.dock-slot').filter({ hasText: 'Photoshop' }).locator('.dock-project');
  await detached.dragTo(page.getByLabel('资料 组内入口'));
  await expect.poll(() => page.evaluate(() => (window as any).__state().projects.find((p: any) => p.id === 'docs').items.some((i: any) => i.id === 'ps'))).toBe(true);
  expect(await page.evaluate(() => (window as any).__state().projects.find((p: any) => p.id === 'docs').items.find((i: any) => i.id === 'ps').path)).toBe('C:\\Adobe\\Photoshop.exe');
});

test('directory right-drag no longer pans and preserves the context menu', async ({ page }) => {
  await host(page); await page.goto('/?view=dock&mode=native'); await page.getByRole('button', { name: '打开 资料 主目录，长按展开堆叠', exact: true }).press('ArrowDown');
  const list = page.locator('.directory-items'); await expect(list).toBeVisible(); const box = await list.boundingBox();
  await page.mouse.move(box!.x + 40, box!.y + box!.height - 8); await page.mouse.down({ button: 'right' });
  await page.mouse.move(box!.x + 40, box!.y + 8, { steps: 4 }); await page.mouse.up({ button: 'right' });
  expect(await list.evaluate(node => node.scrollTop)).toBe(0);
  await expect(page.getByRole('menu', { name: '快捷操作' })).toBeVisible();
});
