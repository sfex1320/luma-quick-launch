import { test, expect, type Page } from '@playwright/test';
import { defaults } from '../src/data';

const mime = 'application/x-luma-real-entry';
const seed = { schemaVersion: 1, revision: 1, preferences: defaults, projects: [
  { id: 'source', name: '源项目', description: '', color: 'mint', pinned: true, items: [{ id: 'main', name: '源项目', path: 'C:\\Fixture\\Source', kind: 'folder' }] },
  { id: 'target', name: '目标项目', description: '', color: 'blue', pinned: true, items: [{ id: 'main', name: '目标项目', path: 'C:\\Fixture\\Target', kind: 'folder' }] },
] };
async function setup(page: Page) {
  await page.addInitScript(seed => {
    const host = window as any, listeners: Array<(event: any) => void> = [];
    let generation = 0, renamed = '原文件.txt', created = '';
    host.__calls = []; host.__violations = []; host.__copied = []; host.__directoryBusy = [];
    window.addEventListener('luma:directory-operation', (event: any) => host.__directoryBusy.push(event.detail.busy));
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: async (value: string) => { host.__copied.push(value); } } });
    host.chrome ||= {};
    host.chrome.webview = { addEventListener: (_: string, callback: any) => listeners.push(callback), postMessage: (req: any) => {
      host.__calls.push(req);
      const respond = (result: unknown, error?: string) => listeners.forEach(callback => callback({ data: { protocol: 1, type: 'response', id: req.id, ok: !error, ...(error ? { error: { code: 'INVALID_REQUEST', message: error } } : { result }) } }));
      const fields: Record<string, string[]> = {
        'folder.getPath': ['projectId', 'itemId', 'entryId'], 'folder.createFolder': ['projectId', 'itemId', 'folderId', 'name'],
        'folder.rename': ['projectId', 'itemId', 'entryId', 'name'], 'folder.move': ['sourceProject', 'sourceItem', 'sourceEntryIds', 'targetProject', 'targetItem', 'targetFolderId'],
      };
      if (fields[req.method] && Object.keys(req.params).sort().join('|') !== [...fields[req.method]].sort().join('|')) {
        host.__violations.push(req); respond(null, '参数包含任意路径或非协议字段'); return;
      }
      if (req.method === 'app.getState') {
        setTimeout(() => respond(seed), 2);
        setTimeout(() => listeners.forEach(callback => callback({ data: { protocol: 1, type: 'event', event: 'window.visibility', data: { visible: true, visibilityId: 1 } } })), 80); return;
      }
      if (req.method === 'folder.list') {
        const project = req.params.projectId, suffix = `-${generation}`, child = req.params.folderId?.includes('-child-');
        if (req.params.folderId && !req.params.folderId.endsWith(suffix)) { host.__violations.push(req); respond(null, '旧目录令牌已失效'); return; }
        const listing = { folderId: `${project}-${child ? 'child' : 'root'}${suffix}`, name: child ? '子目录' : project === 'source' ? '源项目' : '目标项目', parentId: child ? `${project}-root${suffix}` : null, truncated: false,
          entries: child ? [{ id: `${project}-nested${suffix}`, name: '内部文件.txt', kind: 'file' }] : [
            { id: `${project}-child${suffix}`, name: '子目录', kind: 'folder' }, { id: `${project}-file${suffix}`, name: renamed, kind: 'file' },
            ...(created ? [{ id: `${project}-new${suffix}`, name: created, kind: 'folder' }] : []),
          ] };
        setTimeout(() => respond(listing), 5); return;
      }
      if (req.method === 'folder.getPath') { setTimeout(() => respond({ path: `C:\\Fixture\\${req.params.entryId}` }), 5); return; }
      if (['folder.createFolder', 'folder.rename', 'folder.move'].includes(req.method)) {
        const finish = () => {
          if (host.__mutationError) { respond(null, host.__mutationError); return; }
          if (req.method === 'folder.rename') renamed = req.params.name;
          if (req.method === 'folder.createFolder') created = req.params.name;
          generation++;
          respond(host.__partial ? { changedCount: 1, completed: false, errorCode: 'BUSY', message: '已移动 1 项；其余未移动，请重新查看。' } : { changedCount: 1, completed: true, errorCode: null, message: null });
        };
        if (host.__holdMutation) host.__finishMutation = finish; else setTimeout(finish, 5);
        return;
      }
      const result = req.method === 'window.sync' ? { applied: true } : req.method === 'project.detectTest' ? { task: null } : req.method === 'folder.getThumbnail' || req.method === 'shell.getIcon' ? { dataUrl: null } : { accepted: true };
      setTimeout(() => respond(result), 2);
    } };
  }, seed);
  await page.goto('/?view=dock&mode=native');
  await expect(page.locator('.dock')).toBeVisible();
  await expect.poll(() => page.locator('.dock-wrap').evaluate(el => el.getAnimations().every(animation => animation.playState === 'finished'))).toBe(true);
  await page.getByRole('button', { name: '展开 源项目 堆叠', exact: true }).click();
  await expect(page.locator('.stack-item[data-item-id="source-file-0"]')).toBeVisible();
}
const tile = (page: Page, id: string) => page.locator(`.stack-item[data-item-id="${id}"]`);
const calls = (page: Page, method: string) => page.evaluate(method => (window as any).__calls.filter((entry: any) => entry.method === method), method);
async function context(page: Page, id: string) { await tile(page, id).click({ button: 'right' }); await expect(page.getByRole('menu', { name: '快捷操作' })).toBeVisible(); }
test.afterEach(async ({ page }) => { expect(await page.evaluate(() => (window as any).__violations ?? [])).toEqual([]); });

test('actual entry menu copies host-resolved address and renames only after inline confirmation', async ({ page }) => {
  await setup(page);
  await context(page, 'source-file-0');
  await expect(page.getByRole('menuitem', { name: '移除快捷项', exact: true })).toHaveCount(0);
  await expect(page.getByRole('menuitem', { name: /删除/ })).toHaveCount(0);
  await page.getByRole('menuitem', { name: '复制地址', exact: true }).click();
  await expect.poll(() => page.evaluate(() => (window as any).__copied)).toEqual(['C:\\Fixture\\source-file-0']);
  expect((await calls(page, 'folder.getPath'))[0].params).toEqual({ projectId: 'source', itemId: 'main', entryId: 'source-file-0' });
  await context(page, 'source-file-0'); await page.getByRole('menuitem', { name: '重命名', exact: true }).click();
  const form = page.getByRole('form', { name: '重命名实际目录项' }); await expect(form).toBeVisible(); await expect(form).toContainText('将修改磁盘中的名称');
  expect(await calls(page, 'folder.rename')).toHaveLength(0);
  await form.getByLabel('新名称', { exact: true }).fill('新名称.txt'); await form.getByRole('button', { name: '确认重命名' }).click();
  await expect(tile(page, 'source-file-1')).toContainText('新名称.txt');
  expect((await calls(page, 'folder.rename'))[0].params).toEqual({ projectId: 'source', itemId: 'main', entryId: 'source-file-0', name: '新名称.txt' });
  expect(await calls(page, 'folder.open')).toHaveLength(0); expect(await calls(page, 'app.saveState')).toHaveLength(0);
  await expect(page.getByRole('status')).toContainText('实际目录已更新');
});

test('new actual folder is cancellable and refuses path-shaped names before sending', async ({ page }) => {
  await setup(page); await page.getByRole('button', { name: '新建文件夹', exact: true }).click();
  let form = page.getByRole('form', { name: '新建实际文件夹' });
  await form.getByLabel('新名称', { exact: true }).fill('../escape');
  await expect(form.getByRole('button', { name: '确认新建' })).toBeDisabled();
  expect(await calls(page, 'folder.createFolder')).toHaveLength(0);
  await form.getByRole('button', { name: '取消', exact: true }).click(); await expect(form).toHaveCount(0);
  await page.getByRole('button', { name: '新建文件夹', exact: true }).click(); form = page.getByRole('form', { name: '新建实际文件夹' });
  await form.getByLabel('新名称', { exact: true }).fill('交付稿'); await form.getByRole('button', { name: '确认新建' }).click();
  await expect(tile(page, 'source-new-1')).toContainText('交付稿');
  expect((await calls(page, 'folder.createFolder'))[0].params).toEqual({ projectId: 'source', itemId: 'main', folderId: 'source-root-0', name: '交付稿' });
});

test('partial move refreshes the whole cascade and never requests a stale child token', async ({ page }) => {
  await setup(page); await context(page, 'source-child-0'); await page.getByRole('menuitem', { name: '进入', exact: true }).click();
  await expect(tile(page, 'source-nested-0')).toBeVisible(); await expect(page.locator('.folder-column')).toHaveCount(2);
  await context(page, 'source-nested-0'); await page.getByRole('menuitem', { name: '准备移动此实际目录项', exact: true }).click();
  await page.getByRole('button', { name: '移动到当前实际目录 源项目', exact: true }).click();
  const form = page.getByRole('form', { name: '确认实际移动' });
  await expect(form).toContainText('内部文件.txt'); await expect(form).toContainText('源项目');
  expect(await calls(page, 'folder.move')).toHaveLength(0);
  await page.evaluate(() => { (window as any).__partial = true; });
  await form.getByRole('button', { name: '确认移动', exact: true }).click();
  await expect(page.locator('.folder-column')).toHaveCount(1); await expect(tile(page, 'source-file-1')).toBeVisible();
  await expect(page.getByRole('status')).toContainText('其余未移动');
  expect((await calls(page, 'folder.move'))[0].params).toEqual({ sourceProject: 'source', sourceItem: 'main', sourceEntryIds: ['source-nested-0'], targetProject: 'source', targetItem: 'main', targetFolderId: 'source-root-0' });
  expect((await calls(page, 'folder.list')).at(-1).params).toEqual({ projectId: 'source', itemId: 'main' });
});

test('real-entry drag does nothing on ordinary grid and only the explicit footer proposes a confirmed move', async ({ page }) => {
  await setup(page);
  const data = await page.evaluateHandle(() => new DataTransfer());
  await tile(page, 'source-file-0').locator('..').dispatchEvent('dragstart', { dataTransfer: data });
  expect(await data.evaluate((transfer, type) => JSON.parse(transfer.getData(type)), mime)).toEqual({ projectId: 'source', itemId: 'main', entryId: 'source-file-0', name: '原文件.txt' });
  await page.locator('.directory-items').dispatchEvent('drop', { dataTransfer: data }); expect(await calls(page, 'folder.move')).toHaveLength(0);
  await page.getByRole('button', { name: '移动到当前实际目录 源项目', exact: true }).dispatchEvent('drop', { dataTransfer: data });
  const form = page.getByRole('form', { name: '确认实际移动' }); await expect(form).toBeVisible();
  expect(await calls(page, 'folder.move')).toHaveLength(0); await form.getByRole('button', { name: '取消', exact: true }).click();
  await tile(page, 'source-file-0').locator('..').dispatchEvent('dragend', { dataTransfer: data });
  await page.getByRole('button', { name: '关闭堆叠', exact: true }).click();
  await page.getByRole('button', { name: '展开 目标项目 堆叠', exact: true }).click();
  await page.getByRole('button', { name: '移动到当前实际目录 目标项目', exact: true }).dispatchEvent('drop', { dataTransfer: data });
  await expect(form).toContainText('原文件.txt'); await expect(form).toContainText('目标项目');
  await form.getByRole('button', { name: '确认移动', exact: true }).click();
  await expect.poll(() => calls(page, 'folder.move')).toHaveLength(1);
  expect((await calls(page, 'folder.move'))[0].params).toEqual({ sourceProject: 'source', sourceItem: 'main', sourceEntryIds: ['source-file-0'], targetProject: 'target', targetItem: 'main', targetFolderId: 'target-root-0' });
  expect(await calls(page, 'app.saveState')).toHaveLength(0);
});

test('prepared context move targets the selected child folder and requires confirmation', async ({ page }) => {
  await setup(page); await context(page, 'source-file-0'); await page.getByRole('menuitem', { name: '准备移动此实际目录项', exact: true }).click();
  await context(page, 'source-child-0'); await page.getByRole('menuitem', { name: '粘贴移动到此实际目录', exact: true }).click();
  const form = page.getByRole('form', { name: '确认实际移动' }); await expect(form).toContainText('子目录');
  expect(await calls(page, 'folder.move')).toHaveLength(0);
  await form.getByRole('button', { name: '确认移动', exact: true }).click();
  await expect.poll(() => calls(page, 'folder.move')).toHaveLength(1);
  expect((await calls(page, 'folder.move'))[0].params.targetFolderId).toBe('source-child-0');
});

test('inflight mutation holds interaction, prevents double submit, and reports no-overwrite failures inline', async ({ page }) => {
  await setup(page); await page.evaluate(() => { (window as any).__holdMutation = true; (window as any).__mutationError = '目标已有同名文件，不会覆盖。'; });
  await context(page, 'source-file-0'); await page.getByRole('menuitem', { name: '重命名', exact: true }).click();
  const form = page.getByRole('form', { name: '重命名实际目录项' });
  await form.getByLabel('新名称', { exact: true }).fill('existing.txt'); await form.getByRole('button', { name: '确认重命名' }).click();
  await expect(form.getByRole('button', { name: '正在更新实际目录…' })).toBeDisabled();
  await expect.poll(() => page.evaluate(() => (window as any).__directoryBusy.at(-1))).toBe(true);
  await form.dispatchEvent('submit'); expect(await calls(page, 'folder.rename')).toHaveLength(1);
  await page.evaluate(() => (window as any).__finishMutation());
  await expect(page.getByRole('status')).toContainText('不会覆盖'); await expect(form).toBeVisible();
  await expect(tile(page, 'source-file-0')).toContainText('原文件.txt');
  await form.getByRole('button', { name: '取消', exact: true }).click();
  await expect.poll(() => page.evaluate(() => (window as any).__directoryBusy.at(-1))).toBe(false);
});

test('a committed disk operation remains pending beyond eight seconds and processes its final response', async ({ page }) => {
  await setup(page); await page.evaluate(() => { (window as any).__holdMutation = true; });
  await page.getByRole('button', { name: '新建文件夹', exact: true }).click();
  await page.getByLabel('新名称', { exact: true }).fill('迟到完成');
  await page.getByRole('button', { name: '确认新建', exact: true }).click();
  await page.clock.install(); await page.clock.fastForward(10000);
  await expect(page.getByRole('button', { name: '正在更新实际目录…' })).toBeDisabled();
  expect(await calls(page, 'folder.createFolder')).toHaveLength(1);
  await page.evaluate(() => (window as any).__finishMutation()); await page.clock.runFor(100);
  await expect(page.locator('.stack-item').filter({ hasText: '迟到完成' })).toBeVisible();
  expect(await page.evaluate(() => (window as any).__violations)).toEqual([]);
});

test('a forged drag with a raw path is not accepted by the actual move target', async ({ page }) => {
  await setup(page);
  await page.getByRole('button', { name: '移动到当前实际目录 源项目', exact: true }).evaluate((element, type) => {
    const dataTransfer = new DataTransfer(); dataTransfer.setData(type, JSON.stringify({ projectId: 'source', itemId: 'main', entryId: 'source-file-0', name: 'file', path: 'C:\\outside' }));
    element.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer }));
  }, mime);
  await expect(page.getByRole('form', { name: '确认实际移动' })).toHaveCount(0);
  await expect(page.getByRole('status')).toContainText('重新拖入'); expect(await calls(page, 'folder.move')).toHaveLength(0);
});

test('right-button panning suppresses the entry context menu', async ({ page }) => {
  await setup(page); const grid = page.locator('.directory-items'); const box = await grid.boundingBox();
  await page.mouse.move(box!.x + 30, box!.y + 30); await page.mouse.down({ button: 'right' });
  await page.mouse.move(box!.x + 30, box!.y + 55, { steps: 4 }); await page.mouse.up({ button: 'right' });
  await tile(page, 'source-file-0').dispatchEvent('contextmenu', { clientX: box!.x + 60, clientY: box!.y + 50 });
  await expect(page.getByRole('menu', { name: '快捷操作' })).toHaveCount(0);
  expect(await calls(page, 'folder.open')).toHaveLength(0);
});

test('a quick physical drag reaches only the explicit move confirmation', async ({ page }) => {
  await setup(page);
  await tile(page, 'source-file-0').locator('..').dragTo(page.getByRole('button', { name: '移动到当前实际目录 源项目', exact: true }));
  await expect(page.getByRole('form', { name: '确认实际移动' })).toBeVisible();
  expect(await calls(page, 'folder.move')).toHaveLength(0); expect(await calls(page, 'folder.open')).toHaveLength(0);
});

test('returning from a child cancels its inline form without leaving a busy hold', async ({ page }) => {
  await setup(page); await context(page, 'source-child-0'); await page.getByRole('menuitem', { name: '进入', exact: true }).click();
  await expect(tile(page, 'source-nested-0')).toBeVisible();
  await context(page, 'source-nested-0'); await page.getByRole('menuitem', { name: '重命名', exact: true }).click();
  await expect(page.getByRole('form', { name: '重命名实际目录项' })).toBeVisible();
  await page.getByRole('button', { name: '返回上一层', exact: true }).click();
  await expect(page.locator('.folder-column')).toHaveCount(1);
  await expect(page.getByRole('form', { name: '重命名实际目录项' })).toHaveCount(0);
  await expect.poll(() => page.evaluate(() => (window as any).__directoryBusy.at(-1))).toBe(false);
  expect(await calls(page, 'folder.rename')).toHaveLength(0);
});
