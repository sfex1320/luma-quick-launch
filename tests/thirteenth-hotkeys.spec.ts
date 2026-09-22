import { test, expect, type Page } from '@playwright/test';
import { defaults } from '../src/data';
const base = { id: 'search', code: 'KeyA', ctrl: false, shift: false, alt: false, scope: 'panel', action: 'search' };
const seed = { schemaVersion: 1, revision: 1, preferences: { ...defaults, autoHide: false, shortcuts: [base] }, projects: [{ id: 'p', name: '项目', description: '', color: 'mint', pinned: true, items: [{ id: 'i', name: '项目目录', path: 'C:\\Fixture', kind: 'folder' }] }] };
async function preview(page: Page, shortcuts = [base]) {
  await page.addInitScript(state => { if (!localStorage.getItem('luma.state.v1')) localStorage.setItem('luma.state.v1', JSON.stringify(state)); }, { ...seed, preferences: { ...seed.preferences, shortcuts } });
  await page.goto('/'); await expect(page.locator('.dock-project')).toBeVisible();
}
async function nativeFixture(page: Page) {
  await page.addInitScript(seed => {
    const w = window as any, listeners: Array<(event: any) => void> = [];
    const shortcuts = [
      { ...seed.preferences.shortcuts[0], id: 'item', action: 'item', projectId: 'p', itemId: 'i' },
      { ...seed.preferences.shortcuts[0], id: 'copy', code: 'KeyC', action: 'copyAddress' },
      { ...seed.preferences.shortcuts[0], id: 'new', code: 'KeyN', action: 'newFolder' },
      { ...seed.preferences.shortcuts[0], id: 'group', code: 'KeyG', ctrl: true, action: 'project', scope: 'global', projectId: 'p' },
    ];
    w.__calls = []; w.__copied = [];
    w.__event = (event: string, data: any) => listeners.forEach(listener => listener({ data: { protocol: 1, type: 'event', event, data } }));
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: async (text: string) => w.__copied.push(text) } });
    w.chrome ||= {}; w.chrome.webview = { addEventListener: (_: string, cb: any) => listeners.push(cb), postMessage: (req: any) => {
      w.__calls.push(req); let result: any = { accepted: true };
      if (req.method === 'app.getState') result = { ...seed, preferences: { ...seed.preferences, shortcuts } };
      if (req.method === 'window.sync') result = { applied: true };
      if (req.method === 'shell.getIcon' || req.method === 'folder.getThumbnail') result = { dataUrl: null };
      if (req.method === 'project.detectTest') result = { task: null };
      if (req.method === 'folder.getPath') result = { path: `C:\\Fixture\\${req.params.entryId}` };
      if (req.method === 'folder.list') result = { folderId: req.params.folderId ? 'nested' : 'root', name: '项目目录', parentId: req.params.folderId ? 'root' : null, truncated: false, entries: [{ id: 'child', name: '子文件夹', kind: 'folder' }] };
      setTimeout(() => listeners.forEach(cb => cb({ data: { protocol: 1, type: 'response', id: req.id, ok: true, result } })), 2);
      if (req.method === 'app.getState') setTimeout(() => w.__event('window.visibility', { visible: true, visibilityId: 1 }), 25);
    } };
  }, seed);
  await page.goto('/?view=dock&mode=native'); await expect(page.locator('.dock-project')).toBeVisible(); await page.waitForTimeout(700);
}
test('record single and modifier shortcuts, duplicate detection, persistence and removal', async ({ page }) => {
  await preview(page, []); await page.getByRole('button', { name: '快捷键', exact: true }).click();
  const recorder = page.getByLabel('录制快捷键');
  await recorder.click(); await expect(recorder).toHaveValue('请按下快捷键…'); await recorder.press('a');
  await expect(recorder).toHaveValue('A'); await expect(page.getByLabel('快捷键生效范围')).toBeDisabled();
  await page.getByRole('button', { name: '添加快捷键', exact: true }).click();
  await expect(page.locator('.shortcut-binding')).toHaveCount(1);
  await recorder.click(); await expect(recorder).toHaveValue('请按下快捷键…'); await recorder.press('a'); await page.getByRole('button', { name: '添加快捷键', exact: true }).click();
  await expect(page.getByText('该按键组合已分配，请先移除原绑定')).toBeVisible();
  await recorder.click(); await expect(recorder).toHaveValue('请按下快捷键…'); await recorder.press('Control+Shift+Alt+b');
  await expect(recorder).toHaveValue('Ctrl + Shift + Alt + B'); await expect(page.getByLabel('快捷键生效范围')).toHaveValue('global');
  await page.getByRole('button', { name: '添加快捷键', exact: true }).click(); await expect(page.locator('.shortcut-binding')).toHaveCount(2);
  await expect.poll(() => page.evaluate(() => JSON.parse(localStorage.getItem('luma.state.v1')!).preferences.shortcuts.length)).toBe(2);
  await page.reload(); await page.getByRole('button', { name: '快捷键', exact: true }).click(); await expect(page.locator('.shortcut-binding')).toHaveCount(2);
  await page.getByRole('button', { name: '移除快捷键 A', exact: true }).click(); await expect(page.locator('.shortcut-binding')).toHaveCount(1);
});
test('single keys require panel focus and do not fire in search input or on repeat', async ({ page }) => {
  await preview(page); await page.locator('body').click({ position: { x: 5, y: 5 } }); await page.keyboard.press('a'); await expect(page.getByRole('dialog')).toHaveCount(0);
  await page.locator('.dock-container').focus(); await page.keyboard.press('a'); await expect(page.getByRole('dialog')).toBeVisible();
  const input = page.getByRole('dialog').locator('input').first(); await input.fill(''); await input.press('a'); await expect(input).toHaveValue('a');
});
test('native panel key sends saved binding id once; global combination is not dispatched twice by frontend', async ({ page }) => {
  await nativeFixture(page); await page.locator('.dock-container').focus(); await page.keyboard.down('a'); await page.keyboard.down('a'); await page.keyboard.up('a');
  await expect.poll(() => page.evaluate(() => (window as any).__calls.filter((call: any) => call.method === 'shortcut.execute'))).toEqual([expect.objectContaining({ params: { id: 'item' } })]);
  await page.keyboard.press('Control+g'); await expect(page.locator('.stack-panel')).toHaveCount(0);
  await page.evaluate(() => (window as any).__event('shortcut.activated', { id: 'group', serial: 8 })); await expect(page.locator('.stack-panel')).toBeVisible();
  await page.evaluate(() => (window as any).__event('shortcut.activated', { id: 'group', serial: 7 }));
  await expect.poll(() => page.evaluate(() => (window as any).__calls.filter((call: any) => call.method === 'shortcut.execute').length)).toBe(1);
});
test('directory single keys use current directory token and new folder opens confirmation only', async ({ page }) => {
  await nativeFixture(page); await page.locator('.dock-project').press('ArrowDown');
  await expect(page.getByRole('button', { name: '打开当前目录', exact: true })).toBeVisible(); await page.locator('.dock-container').focus(); await page.keyboard.press('c');
  await expect.poll(() => page.evaluate(() => (window as any).__copied)).toEqual(['C:\\Fixture\\root']);
  await page.keyboard.press('n'); await expect(page.getByRole('textbox', { name: '新名称' })).toBeVisible();
  await expect.poll(() => page.evaluate(() => (window as any).__calls.filter((call: any) => call.method === 'folder.createFolder').length)).toBe(0);
});
test('built-in search does not open a second dialog during modal keyboard navigation', async ({ page }) => {
  await preview(page); await page.getByRole('button', { name: '使用指南' }).click();
  const dialog = page.getByRole('dialog'); await expect(dialog).toBeVisible();
  await dialog.getByRole('button', { name: '关闭对话框' }).focus(); await page.keyboard.press('Control+k');
  await expect(page.getByRole('dialog')).toHaveCount(1); await expect(dialog).toContainText('一点小手势，大一点的专注');
});
