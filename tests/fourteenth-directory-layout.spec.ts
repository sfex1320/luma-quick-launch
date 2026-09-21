import { test, expect, type Page } from '@playwright/test';
import { defaults } from '../src/data';

async function setup(page: Page, options: { height?: number; childCount?: number } = {}) {
  await page.addInitScript(({ preferences, childCount }) => {
    const host = window as any, listeners: Array<(event: any) => void> = [];
    host.__calls = []; host.__generation = 0;
    host.chrome ||= {};
    host.chrome.webview = { addEventListener: (_: string, callback: any) => listeners.push(callback), postMessage: (req: any) => {
      host.__calls.push(req);
      const respond = (result: unknown, error?: string) => listeners.forEach(callback => callback({ data: { protocol: 1, type: 'response', id: req.id, ok: !error, ...(error ? { error: { code: 'INVALID_REQUEST', message: error } } : { result }) } }));
      if (req.method === 'app.getState') {
        setTimeout(() => respond({ schemaVersion: 1, revision: 1, preferences, projects: [{ id: 'project', name: '目录布局', description: '', color: 'mint', pinned: true, items: [{ id: 'root', name: '目录布局', path: 'C:\\Fixture', kind: 'folder' }] }] }), 2);
        setTimeout(() => listeners.forEach(callback => callback({ data: { protocol: 1, type: 'event', event: 'window.visibility', data: { visible: true, visibilityId: 1 } } })), 80); return;
      }
      if (req.method === 'folder.list') {
        const child = !!req.params.folderId, childId = host.__generation ? `entry-0-r${host.__generation}` : 'entry-0';
        const finish = () => {
          if (child && (req.params.folderId !== childId || host.__failChild)) { respond(null, '目录令牌失效'); return; }
          respond({ folderId: child ? `child-token-${host.__generation}` : `root-token-${host.__generation}`, name: child ? '子目录' : '目录布局', parentId: child ? `root-token-${host.__generation}` : null, truncated: false, entries: child ? Array.from({ length: childCount }, (_, index) => ({ id: index ? `nested-file-${index}` : 'nested-file', name: `内部${index}.txt`, kind: 'file' })) : Array.from({ length: 17 }, (_, index) => ({ id: index === 0 ? childId : `entry-${index}`, name: index === 0 ? '子目录' : `文件${index}.txt`, kind: index === 0 ? 'folder' : 'file' })).filter(entry => !host.__childMissing || entry.kind !== 'folder') });
        };
        if (child && host.__holdChild) host.__finishChild = finish; else setTimeout(finish, 5); return;
      }
      setTimeout(() => respond(req.method === 'window.sync' ? { applied: true } : req.method === 'project.detectTest' ? { task: { id: 'task', label: '打开项目软件', command: 'npm run dev' } } : req.method === 'shell.getIcon' || req.method === 'folder.getThumbnail' ? { dataUrl: null } : { accepted: true }), 2);
    } };
  }, { preferences: { ...defaults, ...(options.height ? { height: options.height } : {}) }, childCount: options.childCount ?? 1 });
  await page.goto('/?view=dock&mode=native');
  await expect(page.locator('.dock')).toBeVisible();
  await expect.poll(() => page.locator('.dock-wrap').evaluate(el => el.getAnimations().every(animation => animation.playState === 'finished'))).toBe(true);
  await page.getByRole('button', { name: '打开 目录布局 主目录，长按展开堆叠', exact: true }).press('ArrowDown');
  await expect(page.locator('.directory-row')).toHaveCount(17);
}
const calls = (page: Page, method: string) => page.evaluate(method => (window as any).__calls.filter((entry: any) => entry.method === method), method);

test('root header is not repeated and refresh sits before close in the stack header', async ({ page }) => {
  await setup(page);
  await expect(page.locator('[data-folder-level="0"] .folder-browser-path')).toHaveCount(0);
  const refresh = page.locator('.stack-panel>header').getByRole('button', { name: '刷新目录', exact: true });
  await expect(refresh).toBeVisible();
  const refreshBox = (await refresh.boundingBox())!, closeBox = (await page.getByRole('button', { name: '关闭堆叠', exact: true }).boundingBox())!;
  expect(Math.abs(refreshBox.y - closeBox.y)).toBeLessThan(2);
  expect(refreshBox.x).toBeLessThan(closeBox.x);
  await refresh.click();
  await expect.poll(() => calls(page, 'folder.list')).toHaveLength(2);
});

async function enterChild(page: Page) {
  await page.locator('.stack-item[data-item-id="entry-0"]').click({ button: 'right' });
  await page.getByRole('menuitem', { name: '进入', exact: true }).click();
  await expect(page.locator('[data-folder-level="1"] .stack-item')).toBeVisible();
}
async function reopen(page: Page) { await page.getByRole('button', { name: '打开 目录布局 主目录，长按展开堆叠', exact: true }).press('ArrowDown'); }

test('session trail restores by fresh parent tokens and header refresh targets the deepest directory', async ({ page }) => {
  await setup(page); await enterChild(page);
  await page.getByRole('button', { name: '关闭堆叠', exact: true }).click();
  await page.evaluate(() => { (window as any).__generation = 1; }); await reopen(page);
  await expect(page.locator('[data-folder-level="1"] .stack-item')).toBeVisible();
  const restored = await calls(page, 'folder.list');
  expect(restored.at(-1).params).toEqual({ projectId: 'project', itemId: 'root', folderId: 'entry-0-r1' });
  await expect(page.locator('[data-folder-level="0"] .folder-browser-path')).toHaveCount(0);
  await expect(page.locator('[data-folder-level="1"] .folder-browser-path')).toContainText('子目录');
  await expect(page.getByRole('button', { name: '刷新目录', exact: true })).toHaveCount(1);
  await page.getByRole('button', { name: '刷新目录', exact: true }).click();
  await expect.poll(async () => (await calls(page, 'folder.list')).length).toBe(restored.length + 1);
  expect((await calls(page, 'folder.list')).at(-1).params.folderId).toBe('entry-0-r1');
  const firstRow = await page.locator('[data-folder-level="0"] .directory-row').evaluateAll(rows => rows.slice(0, 4).map(row => row.getBoundingClientRect().y));
  expect(new Set(firstRow).size).toBe(1);
});

test('removed remembered child truncates the trail and tells the user where browsing stopped', async ({ page }) => {
  await setup(page); await enterChild(page);
  await page.getByRole('button', { name: '关闭堆叠', exact: true }).click();
  await page.evaluate(() => { (window as any).__childMissing = true; }); await reopen(page);
  await expect(page.locator('.folder-column')).toHaveCount(1);
  await expect(page.locator('.directory-limit')).toContainText('记忆目录「子目录」已不可用');
  const before = (await calls(page, 'folder.list')).length;
  await page.getByRole('button', { name: '关闭堆叠', exact: true }).click(); await reopen(page);
  await expect(page.locator('.directory-row')).toHaveCount(16);
  expect((await calls(page, 'folder.list')).length).toBe(before + 1);
  await expect(page.locator('.directory-limit')).toHaveCount(0);
});

test('failed remembered child falls back to its valid parent and ignores replies after closing', async ({ page }) => {
  await setup(page); await enterChild(page);
  await page.getByRole('button', { name: '关闭堆叠', exact: true }).click();
  await page.evaluate(() => { (window as any).__holdChild = true; }); await reopen(page);
  await expect(page.locator('[data-folder-level="1"]')).toContainText('正在读取目录');
  await page.getByRole('button', { name: '关闭堆叠', exact: true }).click();
  await page.evaluate(() => { (window as any).__finishChild(); (window as any).__holdChild = false; (window as any).__failChild = true; });
  await expect(page.locator('.stack-panel')).toHaveCount(0); await reopen(page);
  await expect(page.locator('.directory-limit')).toContainText('已停留在可用的上级目录');
  await expect(page.locator('.folder-column')).toHaveCount(1);
});

test('directory grid shows four fixed tiles per row and exactly two complete rows', async ({ page }) => {
  await setup(page);
  const geometry = await page.locator('.directory-items').evaluate(grid => {
    const frame = grid.getBoundingClientRect(), rows = [...grid.querySelectorAll('.directory-row')].map(row => { const rect = row.getBoundingClientRect(); return { x: rect.x, y: rect.y, bottom: rect.bottom, width: rect.width, height: rect.height }; });
    return { visible: rows.filter(row => row.y >= frame.y && row.bottom <= frame.bottom), rows, overflow: grid.scrollHeight > grid.clientHeight, horizontal: grid.scrollWidth > grid.clientWidth };
  });
  expect(geometry.visible).toHaveLength(8);
  expect(new Set(geometry.rows.slice(0, 4).map(row => row.y)).size).toBe(1);
  expect(geometry.rows.every(row => row.width === 88 && row.height === 94)).toBe(true);
  expect(geometry.overflow).toBe(true); expect(geometry.horizontal).toBe(false);
  const positions = await page.locator('.directory-footer-actions>button').evaluateAll(buttons => buttons.map(button => button.getBoundingClientRect().y));
  expect(Math.max(...positions) - Math.min(...positions)).toBeLessThan(2);
});

test('scaled native-height viewport retains eight complete tiles in root and child columns', async ({ page }) => {
  await page.setViewportSize({ width: 2030, height: 508 });
  await setup(page, { height: 88, childCount: 17 });
  const verify = async (level: number) => {
    const column = page.locator(`[data-folder-level="${level}"]`);
    const geometry = await column.locator('.directory-items').evaluate(grid => {
      const frame = grid.getBoundingClientRect();
      const visible = [...grid.querySelectorAll('.directory-row')].filter(row => { const rect = row.getBoundingClientRect(); return rect.top >= frame.top && rect.bottom <= frame.bottom; });
      return { visible: visible.length, height: frame.height, overflow: grid.scrollHeight > grid.clientHeight };
    });
    expect(geometry.visible).toBe(8); expect(geometry.height).toBe(218); expect(geometry.overflow).toBe(true);
    const footer = (await column.locator('.directory-footer').boundingBox())!;
    expect(footer.y + footer.height).toBeLessThanOrEqual(508);
  };
  await verify(0);
  await page.locator('.stack-item[data-item-id="entry-0"]').click({ button: 'right' });
  await page.getByRole('menuitem', { name: '进入', exact: true }).click();
  await expect(page.locator('[data-folder-level="1"] .directory-row')).toHaveCount(17);
  await verify(0); await verify(1);
  const panel = (await page.locator('.stack-panel').boundingBox())!;
  expect(panel.y + panel.height).toBeLessThanOrEqual(508);
});

test('move arrow explains unprepared click, keyboard activation, and held release without disk requests', async ({ page }) => {
  await setup(page);
  const arrow = page.getByRole('button', { name: '移动到当前实际目录 目录布局', exact: true });
  await arrow.click();
  await expect(page.locator('.directory-operation-notice')).toContainText('准备移动');
  await page.getByRole('button', { name: '关闭堆叠', exact: true }).click(); await reopen(page);
  await expect(page.locator('.directory-operation-notice')).toHaveCount(0);
  await arrow.press('Enter');
  await expect(page.locator('.directory-operation-notice')).toContainText('拖到此处');
  await page.getByRole('button', { name: '关闭堆叠', exact: true }).click(); await reopen(page);
  await expect(page.locator('.directory-operation-notice')).toHaveCount(0);
  const tile = (await page.locator('.stack-item[data-item-id="entry-1"]').boundingBox())!;
  await page.mouse.move(tile.x + 25, tile.y + 25); await page.mouse.down(); await page.waitForTimeout(360);
  const target = (await arrow.boundingBox())!;
  await page.mouse.move(target.x + target.width / 2, target.y + target.height / 2, { steps: 4 }); await page.mouse.up();
  await expect(page.locator('.directory-operation-notice')).toContainText('准备移动');
  await enterChild(page);
  await page.getByRole('button', { name: '移动到当前实际目录 子目录', exact: true }).click();
  await expect(page.locator('[data-folder-level="1"] .directory-operation-notice')).toContainText('准备移动');
  expect(await calls(page, 'folder.move')).toHaveLength(0); expect(await calls(page, 'folder.open')).toHaveLength(0); expect(await calls(page, 'app.saveState')).toHaveLength(0);
});

test('narrow viewport preserves tile dimensions and footer access', async ({ page }) => {
  await page.setViewportSize({ width: 340, height: 640 }); await setup(page);
  const grid = page.locator('.directory-items');
  expect(await grid.evaluate(node => node.scrollWidth > node.clientWidth)).toBe(false);
  const box = (await page.locator('.directory-row').first().boundingBox())!;
  expect(box.width).toBe(88); expect(box.height).toBe(94);
  await expect(page.locator('.directory-footer-actions')).toHaveCSS('flex-wrap', 'nowrap');
  await page.getByRole('button', { name: '移动到当前实际目录 目录布局', exact: true }).click();
  await expect(page.locator('.directory-operation-notice')).toContainText('准备移动');
});

test('dragging a favorite star cannot start its ancestor real-directory drag', async ({ page }) => {
  await setup(page);
  await page.evaluate(() => { (window as any).__dragStarts = 0; window.addEventListener('dragstart', () => (window as any).__dragStarts++, true); });
  const star = page.getByRole('button', { name: '收藏 文件1.txt', exact: true });
  const target = page.getByRole('button', { name: '移动到当前实际目录 目录布局', exact: true });
  const sourceBox = (await star.boundingBox())!, targetBox = (await target.boundingBox())!;
  const source = { x: sourceBox.x + sourceBox.width / 2, y: sourceBox.y + sourceBox.height / 2 };
  await page.mouse.move(source.x, source.y); await page.mouse.down();
  await page.mouse.move(targetBox.x + 12, targetBox.y + 12, { steps: 4 }); await page.mouse.up();
  expect(await page.evaluate(() => (window as any).__dragStarts)).toBe(0);
  await expect(page.getByRole('form', { name: '确认实际移动' })).toHaveCount(0);
  // The ancestor also rejects a forwarded dragstart after a star-origin press,
  // even if browser/platform event ordering bypasses the button drag handler.
  await page.mouse.move(source.x, source.y); await page.mouse.down();
  const forwarded = await star.locator('..').evaluate(row => {
    const dataTransfer = new DataTransfer();
    const accepted = row.dispatchEvent(new DragEvent('dragstart', { bubbles: true, cancelable: true, dataTransfer }));
    return { accepted, types: [...dataTransfer.types] };
  });
  expect(forwarded).toEqual({ accepted: false, types: [] });
  await page.mouse.move(targetBox.x + 12, targetBox.y + 12, { steps: 4 }); await page.mouse.up();
  expect(await calls(page, 'folder.move')).toHaveLength(0); expect(await calls(page, 'folder.open')).toHaveLength(0);
});
