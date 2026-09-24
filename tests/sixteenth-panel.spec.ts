import { test, expect, type Page } from '@playwright/test';
import { defaults } from '../src/data';

async function setup(page: Page, options: { grouped?: boolean; shortcuts?: Array<Record<string, unknown>>; material?: string } = {}) {
  await page.addInitScript(({ preferences, grouped, shortcuts, material }) => {
    if (shortcuts) (preferences as any).shortcuts = shortcuts;
    if (material) (preferences as any).material = material;
    const host = window as any, listeners: Array<(event: any) => void> = [];
    host.__calls = [];
    host.chrome ||= {};
    host.chrome.webview = { addEventListener: (_: string, callback: any) => listeners.push(callback), postMessage: (req: any) => {
      host.__calls.push(req);
      const respond = (result: unknown) => listeners.forEach(callback => callback({ data: { protocol: 1, type: 'response', id: req.id, ok: true, result } }));
      if (req.method === 'app.getState') {
        const items: Array<Record<string, unknown>> = [{ id: 'root', name: '目录布局', path: 'C:\\Fixture', kind: 'folder' }];
        if (grouped) items.push({ id: 'app', name: '编辑器', path: 'C:\\Tools\\editor.exe', kind: 'app' });
        setTimeout(() => respond({ schemaVersion: 1, revision: 1, preferences, projects: [{ id: 'project', name: '目录布局', description: '', color: 'mint', pinned: true, items }] }), 2);
        setTimeout(() => listeners.forEach(callback => callback({ data: { protocol: 1, type: 'event', event: 'window.visibility', data: { visible: true, visibilityId: 1 } } })), 80); return;
      }
      if (req.method === 'folder.list') {
        const child = !!req.params.folderId;
        setTimeout(() => respond({ folderId: child ? 'child-token' : 'root-token', name: child ? '子目录' : '目录布局', parentId: child ? 'root-token' : null, truncated: false, entries: child ? [{ id: 'nested', name: '内部.txt', kind: 'file' }] : [{ id: 'child', name: '子目录', kind: 'folder' }, ...Array.from({ length: 16 }, (_, i) => ({ id: `file-${i + 1}`, name: `文件${i + 1}.txt`, kind: 'file' }))] }), 5); return;
      }
      if (req.method === 'shell.getAppCapabilities') { setTimeout(() => respond({ recentSupported: true }), 1); return; }
      if (req.method === 'shell.getRecent') { setTimeout(() => respond({ entries: [{ id: 'r1', name: '近期设计.psd', path: 'C:\\Fixture\\近期设计.psd', kind: 'file' }], note: 'Windows 最近项目' }), 5); return; }
      setTimeout(() => respond(req.method === 'window.sync' ? { applied: true } : req.method === 'project.detectTest' ? { task: null } : req.method === 'shell.getIcon' || req.method === 'folder.getThumbnail' ? { dataUrl: null } : { accepted: true }), 2);
    } };
  }, { preferences: defaults, grouped: !!options.grouped, shortcuts: options.shortcuts ?? null, material: options.material ?? null });
  await page.goto('/?view=dock&mode=native');
  await expect(page.locator('.dock')).toBeVisible();
  await expect.poll(() => page.locator('.dock-wrap').evaluate(el => el.getAnimations().every(animation => animation.playState === 'finished'))).toBe(true);
  await page.getByRole('button', { name: '打开 目录布局 主目录，长按展开堆叠', exact: true }).press('ArrowDown');
  await expect(page.locator('.stack-panel')).toBeVisible();
  await expect.poll(() => page.locator('.stack-panel').evaluate(el => el.getAnimations().every(animation => animation.playState === 'finished'))).toBe(true);
}
const calls = (page: Page, method: string) => page.evaluate(method => (window as any).__calls.filter((entry: any) => entry.method === method), method);

test('menu filter hides non-matching tiles with fuzzy matching and escape clears before closing', async ({ page }) => {
  await setup(page);
  await expect(page.locator('.directory-row')).toHaveCount(17);
  const filter = page.getByLabel('筛选当前菜单', { exact: true });
  await filter.fill('件2');
  await expect(page.locator('.directory-row')).toHaveCount(2);
  await expect(page.locator('.directory-row .stack-item').first()).toContainText('文件2.txt');
  await filter.fill('文4');
  await expect(page.locator('.directory-row')).toHaveCount(2);
  await filter.press('Escape');
  await expect(page.locator('.directory-row')).toHaveCount(17);
  await expect(page.locator('.stack-panel')).toBeVisible();
  await filter.press('Escape');
  await expect(page.locator('.stack-panel')).toHaveCount(0);
});

test('group entries filter by fuzzy name', async ({ page }) => {
  await setup(page, { grouped: true });
  await expect(page.locator('.group-entry-row')).toHaveCount(2);
  await page.getByLabel('筛选当前菜单', { exact: true }).fill('编辑');
  await expect(page.locator('.group-entry-row')).toHaveCount(1);
  await expect(page.locator('.group-entry-row .stack-item')).toContainText('编辑器');
});

test('right drag pans the grid and suppresses the menu; a stationary right click opens it', async ({ page }) => {
  await setup(page);
  const grid = page.locator('.directory-items').first();
  const before = await grid.evaluate(el => el.scrollTop);
  const box = (await grid.boundingBox())!;
  await page.mouse.move(box.x + 200, box.y + 170);
  await page.mouse.down({ button: 'right' });
  await page.mouse.move(box.x + 200, box.y + 60, { steps: 5 });
  await page.mouse.up({ button: 'right' });
  expect(await grid.evaluate(el => el.scrollTop)).toBeGreaterThan(before);
  await expect(page.locator('.luma-context-menu')).toHaveCount(0);
  await page.waitForTimeout(600);
  await page.locator('.stack-item[data-item-id="file-1"]').click({ button: 'right' });
  await expect(page.locator('.luma-context-menu')).toBeVisible();
});

test('panel header shows the current directory name and back walks up', async ({ page }) => {
  await setup(page);
  await expect(page.locator('.stack-current-name')).toHaveText('目录布局');
  await page.locator('.stack-item[data-item-id="child"]').click({ button: 'right' });
  await page.getByRole('menuitem', { name: '进入', exact: true }).click();
  await expect(page.locator('.stack-current-name')).toHaveText('子目录');
  await page.getByRole('button', { name: '返回上一层', exact: true }).click();
  await expect(page.locator('.stack-current-name')).toHaveText('目录布局');
});

test('main bar favorite star is compact while submenu stars keep their size', async ({ page }) => {
  await setup(page);
  await page.getByRole('button', { name: '整理图标', exact: true }).click();
  const dockStar = (await page.locator('.dock-launchers .favorite-button').first().boundingBox())!;
  const menuStar = (await page.locator('.folder-column .favorite-button').first().boundingBox())!;
  expect(dockStar.width).toBe(17);
  expect(menuStar.width).toBe(23);
  for (const star of [page.locator('.dock-launchers .favorite-button').first(), page.locator('.folder-column .favorite-button').first()]) {
    const box = (await star.boundingBox())!;
    const glyph = (await star.locator('svg').boundingBox())!;
    expect(Math.abs(box.x + box.width / 2 - (glyph.x + glyph.width / 2))).toBeLessThan(0.5);
    expect(Math.abs(box.y + box.height / 2 - (glyph.y + glyph.height / 2))).toBeLessThan(0.5);
  }
});

test('recents column has no in-column header and reports its name and refresh to the panel header', async ({ page }) => {
  await setup(page, { grouped: true });
  await page.locator('.group-column .stack-item[data-item-id="app"]').click({ button: 'right' });
  await page.getByRole('menuitem', { name: '最近项目', exact: true }).click();
  await expect(page.locator('.recent-row')).toHaveCount(1);
  await expect(page.locator('.stack-body-cascade .folder-browser-path')).toHaveCount(0);
  await expect(page.locator('.stack-current-name')).toHaveText('最近项目 · 编辑器');
  const before = (await calls(page, 'shell.getRecent')).length;
  await page.getByRole('button', { name: '刷新目录', exact: true }).click();
  await expect.poll(() => calls(page, 'shell.getRecent')).toHaveLength(before + 1);
});

test('edit mode keeps the open stack and shows remove, favorite and shortcut badges on group tiles', async ({ page }) => {
  await setup(page, { grouped: true, shortcuts: [
    { id: 'b1', code: 'KeyB', ctrl: true, shift: false, alt: false, scope: 'global', action: 'item', projectId: 'project', itemId: 'app' },
    { id: 'b2', code: 'KeyC', ctrl: true, shift: true, alt: false, scope: 'global', action: 'search' },
  ] });
  await expect(page.locator('.group-entry-row')).toHaveCount(2);
  await page.getByRole('button', { name: '整理图标', exact: true }).click();
  await expect(page.locator('.dock')).toHaveClass(/dock-editing/);
  await expect(page.locator('.stack-panel')).toBeVisible();
  await expect(page.locator('.group-column .shortcut-remove')).toHaveCount(2);
  const badge = page.locator('.group-column .shortcut-badge');
  await expect(badge).toHaveCount(1);
  await expect(badge).toHaveText('CB');
  await expect(page.locator('.dock-tools .shortcut-badge')).toHaveText('CSC');
  await expect(page.locator('.stack-panel .favorite-button').first()).toBeVisible();
  await expect.poll(() => page.locator('.stack-panel').evaluate(el => el.getAnimations().every(a => a.playState === 'finished'))).toBe(true);
  for (const button of await page.locator('.group-column .shortcut-remove').all()) {
    const box = (await button.boundingBox())!, glyph = (await button.locator('svg').boundingBox())!;
    expect(Math.abs(box.x + box.width / 2 - (glyph.x + glyph.width / 2))).toBeLessThan(0.5);
    expect(Math.abs(box.y + box.height / 2 - (glyph.y + glyph.height / 2))).toBeLessThan(0.5);
  }
  await page.getByRole('button', { name: '完成整理', exact: true }).click();
  await expect(page.locator('.dock')).not.toHaveClass(/dock-editing/);
  await expect(page.locator('.stack-panel')).toBeVisible();
});

test('directory footer action buttons carry their shortcut badges in edit mode', async ({ page }) => {
  await setup(page, { shortcuts: [
    { id: 'b3', code: 'KeyN', ctrl: true, shift: false, alt: false, scope: 'panel', action: 'newFolder' },
    { id: 'b4', code: 'KeyO', ctrl: true, shift: false, alt: false, scope: 'panel', action: 'openDirectory' },
  ] });
  await page.getByRole('button', { name: '整理图标', exact: true }).click();
  await expect(page.locator('[data-entry-action="create-directory"] .shortcut-badge')).toHaveText('CN');
  await expect(page.locator('.directory-open-current').first().locator('.shortcut-badge')).toHaveText('CO');
});

test('solid and frost materials restyle the submenu panel in both themes', async ({ page }) => {
  await setup(page, { material: 'solid' });
  const solid = await page.locator('.stack-panel').evaluate(el => { const s = getComputedStyle(el); return { backdrop: s.backdropFilter, bg: s.backgroundColor, image: s.backgroundImage }; });
  expect(solid.backdrop).toBe('none');
  expect(solid.bg).toBe('rgba(238, 242, 233, 0.96)');
  await page.evaluate(() => { document.documentElement.dataset.theme = 'dark'; });
  const darkSolid = await page.locator('.stack-panel').evaluate(el => { const s = getComputedStyle(el); return { backdrop: s.backdropFilter, bg: s.backgroundColor }; });
  expect(darkSolid.backdrop).toBe('none');
  expect(darkSolid.bg).toBe('rgba(43, 61, 44, 0.965)');
});

test('frost material keeps the grain overlay on the submenu panel', async ({ page }) => {
  await setup(page, { material: 'frost' });
  const frost = await page.locator('.stack-panel').evaluate(el => { const s = getComputedStyle(el); return { backdrop: s.backdropFilter, image: s.backgroundImage }; });
  expect(frost.backdrop).toContain('blur(28px)');
  expect(frost.image).toContain('data:image/svg');
});
