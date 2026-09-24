import { test, expect, type Page } from '@playwright/test';
import { defaults } from '../src/data';

const groupName = '很长的软件与目录工作分组名称';
async function setup(page: Page, manyApps = false) {
  await page.addInitScript(({ preferences, groupName, manyApps }) => {
    const w = window as any, listeners: Array<(event: any) => void> = [];
    const items = [
      { id: 'kimi', name: 'Kimi Code.lnk', kind: 'app', path: 'C:\\Fixture\\Kimi Code.lnk' },
      { id: 'adobe', name: 'Adobe Photoshop 长名称.lnk', kind: 'app', path: 'C:\\Fixture\\Photoshop.lnk' },
      { id: 'folder', name: '包含很长名字的项目文件夹', kind: 'folder', path: 'C:\\Fixture\\Projects' },
      ...(manyApps ? Array.from({ length: 20 }, (_, index) => ({ id: `extra-${index}`, name: `软件${index}`, kind: 'app', path: `C:\\Fixture\\app${index}.exe` })) : []),
    ];
    const state = { schemaVersion: 1, revision: 1, preferences: { ...preferences, autoHide: false }, projects: [{ id: 'group', name: groupName, color: 'mint', description: '', pinned: true, items }] };
    w.__calls = []; w.__capActive = 0; w.__capMax = 0;
    w.__replaceAdobe = () => { state.projects[0].items[1].path = 'C:\\Fixture\\momo.exe'; state.revision++; listeners.forEach(fn => fn({ data: { protocol: 1, type: 'event', event: 'app.stateChanged', data: state } })); };
    w.chrome ||= {};
    w.chrome.webview = { addEventListener: (_: string, fn: any) => listeners.push(fn), postMessage(req: any) {
      w.__calls.push(req);
      let result: any = { accepted: true };
      if (req.method === 'app.getState') result = state;
      if (req.method === 'window.sync') result = { applied: true };
      if (req.method === 'project.detectTest') result = { task: null };
      if (req.method === 'shell.getIcon' || req.method === 'folder.getThumbnail') result = { dataUrl: null };
      if (req.method === 'shell.getAppCapabilities') {
        w.__capActive++; w.__capMax = Math.max(w.__capMax, w.__capActive);
        result = { recentSupported: req.params.itemId === 'adobe' && state.projects[0].items[1].path.endsWith('Photoshop.lnk') };
      }
      if (req.method === 'folder.list') result = { folderId: 'root-token', name: '包含很长名字的项目文件夹', parentId: null, truncated: false, entries: [{ id: 'file', name: '这个目录里的文件名称必须保持完整提示且只能一行展示.txt', kind: 'file' }] };
      if (req.method === 'shell.getRecent') result = { entries: [{ id: 'recent', name: '非常长的最近设计文件名称应该单行省略而不是撑高.psd', path: 'C:\\Fixture\\Projects\\非常长的最近设计文件名称应该单行省略而不是撑高.psd', kind: 'file' }], note: 'Adobe 最近项目' };
      setTimeout(() => { if (req.method === 'shell.getAppCapabilities') w.__capActive--; listeners.forEach(fn => fn({ data: { protocol: 1, type: 'response', id: req.id, ok: true, result } })); }, req.method === 'shell.getAppCapabilities' ? 40 : 2);
      if (req.method === 'app.getState') setTimeout(() => listeners.forEach(fn => fn({ data: { protocol: 1, type: 'event', event: 'window.visibility', data: { visible: true, visibilityId: 1 } } })), 80);
    } };
  }, { preferences: defaults, groupName, manyApps });
  await page.goto('/?view=dock&mode=native');
  await expect(page.locator('.dock')).toBeVisible();
  await expect.poll(() => page.locator('.dock-wrap').evaluate(el => el.getAnimations().every(a => a.playState === 'finished'))).toBe(true);
  await page.locator('[data-drop-project="group"] .dock-project').press('ArrowDown');
  await expect(page.locator('.group-column')).toBeVisible();
  await expect.poll(() => page.locator('.stack-panel').evaluate(el => el.getAnimations().every(a => a.playState === 'finished'))).toBe(true);
}

test('unsupported software exposes only software open in split and context menus', async ({ page }) => {
  await setup(page);
  const kimi = page.locator('.group-column .stack-item[data-item-id="kimi"]');
  await kimi.hover();
  const actions = page.locator('.group-entry-row').filter({ has: page.locator('.stack-item[data-item-id="kimi"]') }).locator('.split-folder-actions');
  await expect(actions.getByRole('button')).toHaveCount(1);
  await expect(actions.locator('.split-open')).toContainText('打开软件');
  await expect(actions.locator('.split-open .lucide-app-window')).toHaveCount(1);
  await actions.locator('.split-open').press('ArrowRight');
  await expect(page.locator('.recent-list')).toHaveCount(0);
  await actions.locator('.split-open').click({ button: 'right' });
  await expect(page.getByRole('menuitem', { name: '最近项目', exact: true })).toHaveCount(0);
  expect(await page.evaluate(() => (window as any).__calls.filter((call: any) => call.method === 'shell.getRecent'))).toHaveLength(0);
});

test.describe('narrow panel with high DPR and larger inherited text', () => {
  test.use({ viewport: { width: 480, height: 850 }, deviceScaleFactor: 2 });
  test('header controls fit and group/directory names remain one line with full tooltips', async ({ page }) => {
    await setup(page);
    await page.addStyleTag({ content: ':root,body { font-size:24px }' });
    await expect(page.locator('.stack-entry-count')).toHaveCSS('font-size', '11px');
    await expect(page.locator('.stack-entry-count')).toHaveCSS('line-height', '18px');
    await expect(page.locator('.stack-entry-count')).toHaveCSS('white-space', 'nowrap');
    await expect(page.locator('.stack-project-name')).toHaveAttribute('title', groupName);
    const strong = page.locator('.group-column .stack-item[data-item-id="kimi"] strong');
    await expect(strong).toHaveCSS('white-space', 'nowrap');
    await expect(strong).toHaveCSS('text-overflow', 'ellipsis');
    await expect(strong).toHaveAttribute('title', 'Kimi Code.lnk');
    expect((await strong.boundingBox())!.height).toBe(18);
    await page.locator('.group-column .stack-item[data-item-id="folder"]').press('ArrowRight');
    const file = page.locator('.directory-row .stack-item');
    await expect(file).toHaveAttribute('title', '这个目录里的文件名称必须保持完整提示且只能一行展示.txt');
    await expect(file.locator('strong')).toHaveCSS('white-space', 'nowrap');
    expect((await file.locator('strong').boundingBox())!.height).toBe(18);
    const geometry = await page.locator('.stack-panel>header').evaluate(header => {
      const h = header.getBoundingClientRect();
      const heading = header.querySelector('.stack-heading')!.getBoundingClientRect();
      const actions = header.querySelector('.stack-header-actions')!.getBoundingClientRect();
      return { overlap: heading.right > actions.left, fits: [...header.querySelectorAll('.icon-button')].every(el => { const r = el.getBoundingClientRect(); return r.width >= 28 && r.left >= h.left && r.right <= h.right; }), overflow: header.scrollWidth > header.clientWidth };
    });
    expect(geometry).toEqual({ overlap: false, fits: true, overflow: false });
    await page.screenshot({ path: 'docs/evidence/twentythird/narrow-header.png' });
  });
});

test('supported software enables recents and recent names and paths truncate to one line', async ({ page }) => {
  await setup(page);
  const app = page.locator('.group-column .stack-item[data-item-id="adobe"]');
  await app.hover();
  await expect(page.getByRole('button', { name: '查看 Adobe Photoshop 长名称.lnk 最近项目', exact: true })).toBeVisible();
  await app.press('ArrowRight');
  const row = page.locator('.recent-row');
  await expect(row).toHaveCount(1);
  for (const selector of ['strong', 'small']) {
    await expect(row.locator(selector)).toHaveCSS('white-space', 'nowrap');
    await expect(row.locator(selector)).toHaveCSS('text-overflow', 'ellipsis');
    await expect(row.locator(selector)).toHaveAttribute('title', await row.locator(selector).innerText());
  }
});

test('capability requests are shared across menus and use at most four active requests', async ({ page }) => {
  await setup(page, true);
  await expect.poll(() => page.evaluate(() => (window as any).__calls.filter((call: any) => call.method === 'shell.getAppCapabilities').length)).toBe(22);
  expect(await page.evaluate(() => (window as any).__capMax)).toBeLessThanOrEqual(4);
  const ids = await page.evaluate(() => (window as any).__calls.filter((call: any) => call.method === 'shell.getAppCapabilities').map((call: any) => `${call.params.projectId}:${call.params.itemId}`));
  expect(new Set(ids).size).toBe(ids.length);
});

test('saving a new software path invalidates recent capability for the same saved IDs', async ({ page }) => {
  await setup(page);
  const app = page.locator('.group-column .stack-item[data-item-id="adobe"]');
  await app.hover();
  await expect(page.getByRole('button', { name: '查看 Adobe Photoshop 长名称.lnk 最近项目', exact: true })).toBeVisible();
  await page.evaluate(() => (window as any).__replaceAdobe());
  await expect(page.locator('.group-column .stack-item[data-item-id="adobe"]')).toHaveAttribute('title', /momo\.exe$/);
  await expect.poll(() => page.evaluate(() => (window as any).__calls.filter((call: any) => call.method === 'shell.getAppCapabilities' && call.params.itemId === 'adobe').length)).toBe(2);
  await expect(page.getByRole('button', { name: '查看 Adobe Photoshop 长名称.lnk 最近项目', exact: true })).toHaveCount(0);
  await page.locator('.group-column .stack-item[data-item-id="adobe"]').press('ArrowRight');
  await expect(page.locator('.recent-list')).toHaveCount(0);
});
