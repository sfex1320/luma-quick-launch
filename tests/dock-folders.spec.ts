import { test, expect, type Page, type Locator } from '@playwright/test';
import { defaults } from '../src/data';
import type { AppState, FolderListing } from '../src/contracts';

const initial: AppState = { schemaVersion: 1, revision: 4, preferences: defaults, projects: [
  { id: 'directory', name: '电梯贴', description: '', color: 'mint', pinned: true, items: [{ id: 'root', name: '电梯贴', path: 'C:\\Fixtures\\Elevator', kind: 'folder' }] },
  { id: 'editor', name: '编辑器', description: '', color: 'blue', pinned: true, items: [{ id: 'main', name: '编辑器', path: 'C:\\Fixtures\\Editor.exe', kind: 'app' }] },
  { id: 'paint', name: '画图', description: '', color: 'peach', pinned: true, items: [{ id: 'main', name: '画图', path: 'C:\\Fixtures\\Paint.exe', kind: 'app' }] },
] };
const root: FolderListing = { folderId: 'token-root', name: '电梯贴', parentId: null, truncated: false, entries: [
  { id: 'token-design', name: '设计稿', kind: 'folder' }, { id: 'token-output', name: '输出稿', kind: 'folder' }, { id: 'token-document', name: '需求说明.docx', kind: 'file' },
] };
const child: FolderListing = { folderId: 'token-design', name: '设计稿', parentId: 'token-root', truncated: false, entries: [{ id: 'token-draft', name: '初稿.pdf', kind: 'file' }] };

async function attachHost(page: Page, seed = initial, listing = root) {
  await page.addInitScript(({ initial, root, child }) => {
    const host = window as any;
    let state = structuredClone(initial);
    const listeners: Array<(event: any) => void> = [];
    host.__calls = []; host.__hostState = state; host.__delayFolderOpen = false;
    host.chrome ||= {};
    host.chrome.webview = { addEventListener: (_: string, listener: any) => listeners.push(listener), postMessage: (req: any) => {
      host.__calls.push(req);
      let result: unknown = { accepted: true };
      if (req.method === 'app.getState') result = state;
      else if (req.method === 'app.saveState') { state = { ...req.params.state, revision: state.revision + 1 }; host.__hostState = state; result = state; }
      else if (req.method === 'window.sync') result = { applied: true };
      else if (req.method === 'folder.list') result = req.params.folderId === 'token-design' ? child : root;
      else if (req.method === 'project.detectTest') result = { task: req.params.folderId === 'token-root' ? host.__testTask ?? null : null };
      else if (req.method === 'project.runTest') result = { opened: true };
      const respond = (fail = false) => listeners.forEach(listener => listener({ data: { protocol: 1, type: 'response', id: req.id, ok: !fail, ...(fail ? { error: { code: 'OPEN_FAILED', message: '旧目录打开失败' } } : { result }) } }));
      if (req.method === 'folder.open' && host.__delayFolderOpen) host.__finishFolderOpen = respond;
      else if (req.method === 'project.runTest' && host.__delayTestRun) host.__finishTestRun = respond;
      else setTimeout(respond, 15);
      if (req.method === 'app.getState') setTimeout(() => listeners.forEach(listener => listener({ data: { protocol: 1, type: 'event', event: 'window.visibility', data: { visible: true, visibilityId: 1 } } })), 90);
    } };
  }, { initial: seed, root: listing, child });
  await page.goto('/?view=dock&mode=native');
  await expect(page.getByRole('navigation', { name: '快捷启动面板' })).toBeVisible();
  await expect.poll(() => page.locator('.dock-wrap').evaluate(el => el.getAnimations().every(animation => animation.playState === 'finished'))).toBe(true);
}

const projectButton = (page: Page, name: string) => page.getByRole('button', { name: `打开 ${name} 主目录，长按展开堆叠`, exact: true });
async function center(target: Locator) { const box = await target.boundingBox(); expect(box).not.toBeNull(); return { x: box!.x + box!.width / 2, y: box!.y + box!.height / 2 }; }
async function hold(page: Page, target: Locator) { const point = await center(target); await page.mouse.move(point.x, point.y); await page.mouse.down(); }
async function calls(page: Page, method: string) { return page.evaluate(method => (window as any).__calls.filter((call: any) => call.method === method), method); }

test('single-folder long press lists actual immediate contents; navigation and back use host tokens', async ({ page }) => {
  await attachHost(page);
  await hold(page, projectButton(page, '电梯贴'));
  await expect(page.locator('.directory-row')).toHaveCount(3);
  await expect(page.getByText('需求说明.docx', { exact: true })).toBeVisible();
  expect(await calls(page, 'folder.open')).toHaveLength(0);
  expect(await calls(page, 'shell.openItem')).toHaveLength(0);
  await page.mouse.up();
  expect((await calls(page, 'folder.list'))[0].params).toEqual({ projectId: 'directory', itemId: 'root' });
  await page.locator('.stack-item[data-item-id="token-design"]').hover();
  await page.getByRole('button', { name: '浏览 设计稿 子目录', exact: true }).click();
  await expect(page.getByText('初稿.pdf', { exact: true })).toBeVisible();
  expect((await calls(page, 'folder.list')).at(-1).params).toEqual({ projectId: 'directory', itemId: 'root', folderId: 'token-design' });
  await page.getByRole('button', { name: '返回上一层', exact: true }).click();
  await expect(page.locator('.directory-row')).toHaveCount(3);
  expect(await calls(page, 'folder.list')).toHaveLength(2); // Returning retains the loaded parent.
  await page.locator('.stack-item[data-item-id="token-design"]').hover();
  await page.getByRole('button', { name: '浏览 设计稿 子目录', exact: true }).click();
  await expect(page.getByText('初稿.pdf', { exact: true })).toBeVisible();
  await page.locator('.folder-column').last().getByRole('button', { name: '打开当前目录', exact: true }).click();
  await expect.poll(() => calls(page, 'folder.open')).toHaveLength(1);
  expect((await calls(page, 'folder.open'))[0].params).toEqual({ projectId: 'directory', itemId: 'root', entryId: 'token-design' });
  await expect(page.locator('.stack-panel')).toHaveCount(0);
  expect(await calls(page, 'app.saveState')).toHaveLength(0);
  expect(await calls(page, 'shell.openItem')).toHaveLength(0);
});

test('code project detection is read-only; Run tests sends only saved identities and its task token', async ({ page }) => {
  await attachHost(page);
  await page.evaluate(() => { (window as any).__testTask = { id: 'test-root', label: '测试软件', command: 'npm test' }; (window as any).__delayTestRun = true; });
  await page.getByRole('button', { name: '展开 电梯贴 堆叠', exact: true }).click();
  const run = page.getByRole('button', { name: '测试软件', exact: true });
  await expect(run).toBeVisible();
  await expect(run).toHaveAttribute('title', '测试软件 · npm test');
  expect((await calls(page, 'project.detectTest'))[0].params).toEqual({ projectId: 'directory', itemId: 'root', folderId: 'token-root' });
  expect(await calls(page, 'project.runTest')).toHaveLength(0);
  await run.click();
  await expect(page.getByRole('button', { name: '正在打开终端…', exact: true })).toBeDisabled();
  expect((await calls(page, 'project.runTest'))[0].params).toEqual({ projectId: 'directory', itemId: 'root', taskId: 'test-root' });
  expect(await calls(page, 'folder.open')).toHaveLength(0);
  await page.evaluate(() => (window as any).__finishTestRun());
  await expect(page.locator('.stack-panel')).toHaveCount(0);
});

test('run tests participates in slide release, outside release cancels and folders without a test hide it', async ({ page }) => {
  await attachHost(page);
  await page.evaluate(() => { (window as any).__testTask = { id: 'test-root', label: '测试软件', command: 'npm test' }; });
  await hold(page, projectButton(page, '电梯贴'));
  const run = page.getByRole('button', { name: '测试软件', exact: true });
  await expect(run).toBeVisible();
  let target = await center(run); await page.mouse.move(target.x, target.y);
  await expect(run).toHaveClass(/selected/);
  await page.mouse.move(10, 650); await page.mouse.up();
  expect(await calls(page, 'project.runTest')).toHaveLength(0);
  await page.getByRole('button', { name: '展开 电梯贴 堆叠', exact: true }).click();
  await page.locator('.stack-item[data-item-id="token-design"]').hover();
  await page.getByRole('button', { name: '浏览 设计稿 子目录', exact: true }).click();
  await expect(page.getByText('初稿.pdf', { exact: true })).toBeVisible();
  await expect(page.locator('.folder-column').last().getByRole('button', { name: '测试软件', exact: true })).toHaveCount(0);
  await expect(run).toBeVisible(); // The retained parent keeps its own action.
  await page.getByRole('button', { name: '返回上一层', exact: true }).click();
  await expect(run).toBeVisible();
  await run.focus(); await page.keyboard.press('Enter');
  await expect.poll(() => calls(page, 'project.runTest')).toHaveLength(1);
});

test('failed test launch keeps directory contents and allows retry', async ({ page }) => {
  await attachHost(page);
  await page.evaluate(() => { (window as any).__testTask = { id: 'test-root', label: '测试软件', command: 'npm test' }; (window as any).__delayTestRun = true; });
  await page.getByRole('button', { name: '展开 电梯贴 堆叠', exact: true }).click();
  await page.getByRole('button', { name: '测试软件', exact: true }).click();
  await page.evaluate(() => (window as any).__finishTestRun(true));
  await expect(page.getByRole('alert')).toBeVisible();
  await expect(page.locator('.directory-row')).toHaveCount(3);
  await expect(page.getByRole('button', { name: '测试软件', exact: true })).toBeEnabled();
});

test('a pending test response cannot close a subsequently browsed child directory', async ({ page }) => {
  await attachHost(page);
  await page.evaluate(() => { (window as any).__testTask = { id: 'test-root', label: '测试软件', command: 'npm test' }; (window as any).__delayTestRun = true; });
  await page.getByRole('button', { name: '展开 电梯贴 堆叠', exact: true }).click();
  await page.getByRole('button', { name: '测试软件', exact: true }).click();
  await page.locator('.stack-item[data-item-id="token-design"]').hover();
  await page.getByRole('button', { name: '浏览 设计稿 子目录', exact: true }).click();
  await expect(page.getByText('初稿.pdf', { exact: true })).toBeVisible();
  await page.evaluate(async () => {
    (window as any).__finishTestRun();
    await new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));
  });
  await expect(page.getByText('初稿.pdf', { exact: true })).toBeVisible();
  expect(await calls(page, 'project.runTest')).toHaveLength(1);
});

for (const viewport of [{ width: 340, height: 500 }, { width: 360, height: 640 }, { width: 640, height: 360 }]) test(`test actions stay accessible with truncated contents at ${viewport.width}x${viewport.height}`, async ({ page }) => {
  await page.setViewportSize(viewport);
  const seed = structuredClone(initial); seed.preferences.height = 160;
  const listing = { ...root, truncated: true, entries: Array.from({ length: 200 }, (_, i) => ({ id: `entry-${i}`, name: `文档 ${i}.pdf`, kind: 'file' as const })) };
  await attachHost(page, seed, listing);
  await page.evaluate(() => { (window as any).__testTask = { id: 'test-root', label: '测试软件', command: 'npm run test' }; });
  await page.getByRole('button', { name: '展开 电梯贴 堆叠', exact: true }).click();
  const run = page.getByRole('button', { name: '测试软件', exact: true });
  await expect(run).toBeVisible();
  const bounds = await page.locator('.stack-panel').boundingBox();
  expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(viewport.height);
  expect(bounds!.x).toBeGreaterThanOrEqual(0);
  expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(viewport.width);
  // On very short displays the entire bounded panel can scroll instead of clipping actions.
  await run.scrollIntoViewIfNeeded();
  const button = await run.boundingBox();
  expect(button!.y + button!.height).toBeLessThanOrEqual(viewport.height);
  await run.click();
  await expect.poll(() => calls(page, 'project.runTest')).toHaveLength(1);
});

test('long-press slide from the folder icon opens the released file once, never on pointer down', async ({ page }) => {
  await attachHost(page);
  await hold(page, projectButton(page, '电梯贴'));
  const file = page.locator('[data-item-id="token-document"]');
  await expect(file).toBeVisible();
  const destination = await center(file);
  await page.mouse.move(destination.x, destination.y, { steps: 8 });
  await expect(file).toHaveClass(/selected/);
  expect(await calls(page, 'folder.open')).toHaveLength(0);
  await page.mouse.up();
  await expect.poll(() => calls(page, 'folder.open')).toHaveLength(1);
  await expect(page.locator('.stack-panel')).toHaveCount(0);
  expect((await calls(page, 'folder.open'))[0].params).toEqual({ projectId: 'directory', itemId: 'root', entryId: 'token-document' });
  expect(await calls(page, 'shell.openItem')).toHaveLength(0);
});

test('pressing a directory member delays launch until release and a long press released outside cancels', async ({ page }) => {
  await attachHost(page);
  await page.getByRole('button', { name: '展开 电梯贴 堆叠', exact: true }).click();
  const file = page.locator('[data-item-id="token-document"]');
  await expect(file).toBeVisible();
  await hold(page, file);
  // Cross the defined 300 ms gesture threshold while staying on the member.
  await page.waitForTimeout(340);
  expect(await calls(page, 'folder.open')).toHaveLength(0);
  await page.mouse.move(20, 700, { steps: 8 });
  await page.mouse.up();
  await expect(page.locator('.stack-panel')).toHaveCount(0);
  expect(await calls(page, 'folder.open')).toHaveLength(0);
  await page.getByRole('button', { name: '展开 电梯贴 堆叠', exact: true }).click();
  await expect(file).toBeVisible();
  await hold(page, file);
  await page.waitForTimeout(340);
  expect(await calls(page, 'folder.open')).toHaveLength(0);
  await page.mouse.up();
  await expect.poll(() => calls(page, 'folder.open')).toHaveLength(1);
  await expect(page.locator('.stack-panel')).toHaveCount(0);
  expect(await calls(page, 'shell.openItem')).toHaveLength(0);
});

test('organize toolbar enables icon-on-icon merging and split preserves all members without launching', async ({ page }) => {
  await attachHost(page);
  await page.getByRole('button', { name: '整理图标', exact: true }).click();
  await expect(page.getByRole('button', { name: '完成整理', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await hold(page, projectButton(page, '编辑器'));
  const target = await center(projectButton(page, '画图'));
  await page.mouse.move(target.x, target.y, { steps: 12 });
  await expect(page.locator('[data-drop-project="paint"]')).toHaveClass(/group-drop-target/);
  await page.mouse.up();
  await expect.poll(() => page.evaluate(() => (window as any).__hostState.projects.length)).toBe(2);
  await expect(projectButton(page, '编辑器')).toHaveCount(0);
  await expect(page.locator('[data-drop-project="paint"] .group-icon .crystal-item-app')).toHaveCount(2);
  expect(await page.evaluate(() => (window as any).__hostState.projects.find((project: any) => project.id === 'paint').items.map((item: any) => item.name))).toEqual(['画图', '编辑器']);
  expect(await calls(page, 'shell.openItem')).toHaveLength(0);
  expect(await calls(page, 'folder.open')).toHaveLength(0);
  await projectButton(page, '画图').click();
  await page.getByRole('button', { name: '拆成独立图标', exact: true }).click();
  await expect.poll(() => page.evaluate(() => (window as any).__hostState.projects.map((project: any) => project.name))).toEqual(['电梯贴', '画图', '编辑器']);
  expect((await calls(page, 'app.saveState')).map((call: any) => call.params.expectedRevision)).toEqual([4, 5]);
  expect(await calls(page, 'shell.openItem')).toHaveLength(0);
  expect(await calls(page, 'folder.open')).toHaveLength(0);
  await page.getByRole('button', { name: '完成整理', exact: true }).click();
  await expect(page.getByRole('button', { name: '整理图标', exact: true })).toHaveAttribute('aria-pressed', 'false');
});

test('mixed groups include the first entry, browse folder members and launch a software member only on release', async ({ page }) => {
  const mixed = structuredClone(initial);
  mixed.projects = [{ ...mixed.projects[0], items: [...mixed.projects[0].items, { ...mixed.projects[1].items[0], id: 'editor-member' }] }];
  await attachHost(page, mixed);
  await page.getByRole('button', { name: '展开 电梯贴 堆叠', exact: true }).click();
  await expect(page.locator('.group-entry-row')).toHaveCount(2);
  await expect(page.locator('.group-entry-row').first().locator('.stack-item')).toHaveAttribute('data-item-id', 'root');
  await page.locator('.group-column .stack-item[data-item-id="root"]').hover();
  await page.getByRole('button', { name: '浏览 电梯贴 子目录', exact: true }).click();
  await expect(page.locator('.directory-row')).toHaveCount(3);
  await page.getByRole('button', { name: '返回上一层', exact: true }).click();
  await expect(page.locator('.group-entry-row')).toHaveCount(2);
  const software = page.locator('.group-entry-row [data-item-id="editor-member"]');
  await hold(page, software);
  await page.waitForTimeout(340);
  expect(await calls(page, 'shell.openItem')).toHaveLength(0);
  await page.mouse.up();
  await expect(page.getByLabel('编辑器 最近项目')).toBeVisible();
  expect(await calls(page, 'shell.openItem')).toHaveLength(0);
  await page.locator('.group-entry-row .stack-item[data-item-id="editor-member"]').click();
  await expect.poll(() => calls(page, 'shell.openItem')).toHaveLength(1);
  expect((await calls(page, 'shell.openItem'))[0].params).toEqual({ projectId: 'directory', itemId: 'editor-member' });
  await expect(page.locator('.stack-panel')).toHaveCount(0);
  expect(await calls(page, 'folder.open')).toHaveLength(0);
  expect(await calls(page, 'app.saveState')).toHaveLength(0);
});

test('Escape cancels a root press before 300 ms and releasing afterward never launches', async ({ page }) => {
  await attachHost(page);
  // Freeze the threshold timer so this remains an early-release test even on a busy runner.
  await page.clock.install();
  await page.clock.pauseAt(new Date());
  await hold(page, projectButton(page, '电梯贴'));
  await page.keyboard.press('Escape');
  await page.mouse.up();
  await page.clock.runFor(800);
  await expect(page.locator('.stack-panel')).toHaveCount(0);
  expect(await calls(page, 'shell.openItem')).toHaveLength(0);
  expect(await calls(page, 'folder.open')).toHaveLength(0);
  expect(await calls(page, 'folder.list')).toHaveLength(0);
});

for (const fail of [false, true]) {
  test(`a delayed folder.open ${fail ? 'failure' : 'success'} cannot change a subsequently opened menu`, async ({ page }) => {
    await attachHost(page);
    await page.evaluate(() => { (window as any).__delayFolderOpen = true; });
    await page.getByRole('button', { name: '展开 电梯贴 堆叠', exact: true }).click();
    await page.locator('[data-item-id="token-document"]').click();
    await expect.poll(() => calls(page, 'folder.open')).toHaveLength(1);
    await page.getByRole('button', { name: '展开 编辑器 堆叠', exact: true }).click();
    const currentMenu = page.getByRole('region', { name: '编辑器 文件夹堆叠', exact: true });
    await expect(currentMenu).toBeVisible();
    await page.evaluate(async fail => {
      (window as any).__finishFolderOpen(fail);
      // Flush the response microtask and React's commit before testing absence of side effects.
      await new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));
    }, fail);
    await expect(currentMenu).toBeVisible();
    await expect(currentMenu.getByLabel('编辑器 最近项目')).toBeVisible();
    await expect(page.getByRole('alert')).toHaveCount(0);
    expect(await calls(page, 'folder.open')).toHaveLength(1);
    expect(await calls(page, 'shell.openItem')).toHaveLength(0);
  });
}

for (const fromMore of [true, false]) {
  test(`narrow viewport organize mode merges ${fromMore ? 'from the scrolled rail to its beginning' : 'from the beginning to a scrolled target'}`, async ({ page }) => {
    await page.setViewportSize({ width: 340, height: 720 });
    const narrow = structuredClone(initial);
    narrow.preferences.width = 320;
    await attachHost(page, narrow);
    await page.getByRole('button', { name: '整理图标', exact: true }).click();
    const more = page.locator('.dock-launchers');
    await expect(more).toBeVisible();
    const source = fromMore ? more.locator('[data-drop-project="editor"] .dock-project') : projectButton(page, '电梯贴');
    const target = fromMore ? page.locator('.dock-slot[data-drop-project="directory"]') : more.locator('[data-drop-project="editor"]');
    await source.scrollIntoViewIfNeeded(); await hold(page, source);
    await expect(more).toBeVisible();
    await target.scrollIntoViewIfNeeded(); const destination = await center(target);
    await page.mouse.move(destination.x, destination.y, { steps: 12 });
    await expect(target).toHaveClass(/group-drop-target/);
    await page.mouse.up();
    await expect.poll(() => page.evaluate(() => (window as any).__hostState.projects.length)).toBe(2);
    const expectedTarget = fromMore ? 'directory' : 'editor';
    const expectedMembers = fromMore ? ['电梯贴', '编辑器'] : ['编辑器', '电梯贴'];
    expect(await page.evaluate(id => (window as any).__hostState.projects.find((project: any) => project.id === id)?.items.map((item: any) => item.name), expectedTarget)).toEqual(expectedMembers);
    expect((await calls(page, 'app.saveState'))[0].params.expectedRevision).toBe(4);
    expect(await calls(page, 'shell.openItem')).toHaveLength(0);
    expect(await calls(page, 'folder.open')).toHaveLength(0);
    await expect(more).toBeVisible();
  });
}

for (const [width, expectedWidth] of [[320, 376], [640, 640], [1000, 1000]]) test(`toolbar remains right-aligned and directory tiles stay fixed at dock width ${width}`, async ({ page }) => {
  const seed = structuredClone(initial); seed.preferences.width = width;
  await attachHost(page, seed);
  const alignment = await page.evaluate(() => {
    const dock = document.querySelector('.dock')!.getBoundingClientRect();
    const tools = document.querySelector('.dock-tools')!.getBoundingClientRect();
    return { right: dock.right - tools.right, tools: tools.width, dock: dock.width };
  });
  expect(alignment.right).toBeCloseTo(17, 0); expect(alignment.tools).toBe(126); expect(alignment.dock).toBe(expectedWidth);
  await page.getByRole('button', { name: '展开 电梯贴 堆叠', exact: true }).click();
  await expect(page.locator('.directory-row')).toHaveCount(3);
  const boxes = await page.locator('.directory-row').evaluateAll(elements => elements.map(el => { const b = el.getBoundingClientRect(); return { width:b.width,height:b.height }; }));
  expect(boxes).toEqual(Array(3).fill({width:88,height:94}));
  const grid = await page.locator('.directory-items').evaluate(el => ({ display:getComputedStyle(el).display,overflow:el.scrollWidth>el.clientWidth }));
  expect(grid).toEqual({display:'grid',overflow:false});
});

test('dock grows for pinned top-level projects', async ({ page }) => {
  const many = structuredClone(initial);
  many.preferences.width = 320;
  many.projects = Array.from({ length: 6 }, (_, index) => ({
    ...many.projects[0], id: `project-${index}`, name: `项目 ${index}`,
    items: [{ ...many.projects[0].items[0], id: `item-${index}` }],
  }));
  await attachHost(page, many);
  await expect(page.locator('.dock')).toHaveCSS('width', '592px');
  await expect(page.getByRole('button', { name: '更多项目', exact: true })).toHaveCount(0);
});

test('grouped children do not consume adaptive dock width', async ({ page }) => {
  const grouped = structuredClone(initial);
  grouped.preferences.width = 320;
  grouped.projects = [{ ...grouped.projects[0], items: Array.from({ length: 6 }, (_, index) => ({ ...grouped.projects[0].items[0], id: `member-${index}` })) }];
  await attachHost(page, grouped);
  await expect(page.locator('.dock')).toHaveCSS('width', '320px');
});

test('adaptive width stops at 1120 and keeps the remaining pinned projects in a horizontal scroll rail', async ({ page }) => {
  await page.setViewportSize({ width: 1400, height: 700 });
  const many = structuredClone(initial);
  many.preferences.width = 320;
  many.projects = Array.from({ length: 20 }, (_, index) => ({
    ...many.projects[0], id: `project-${index}`, name: `项目 ${index}`,
    items: [{ ...many.projects[0].items[0], id: `item-${index}` }],
  }));
  await attachHost(page, many);
  await expect(page.locator('.dock')).toHaveCSS('width', '1120px');
  await expect.poll(() => page.locator('.dock-launchers').evaluate(el => el.scrollWidth > el.clientWidth)).toBe(true);
  await expect(page.locator('.dock-slot')).toHaveCount(20);
  await page.locator('.dock-slot').last().scrollIntoViewIfNeeded();
  await expect.poll(() => page.locator('.dock-launchers').evaluate(el => el.scrollLeft)).toBeGreaterThan(0);
});

test('removing a top-level project uses one FLIP width animation and bounded native sync', async ({ page }) => {
  const seed = structuredClone(initial);
  seed.preferences.width = 320;
  await attachHost(page, seed);
  await expect(page.locator('.dock')).toHaveCSS('width', '376px');
  await page.getByRole('button', { name: '整理图标', exact: true }).click();
  const source = projectButton(page, '编辑器');
  const target = page.locator('.dock-slot[data-drop-project="paint"]');
  await hold(page, source);
  const destination = await center(target);
  await page.mouse.move(destination.x, destination.y, { steps: 8 });
  await page.evaluate(() => { (window as any).__calls = []; });
  await page.mouse.up();
  await expect(page.locator('.dock')).toHaveCSS('width', '320px');
  await expect.poll(() => page.locator('.dock').evaluate(el => el.getAnimations().some(animation => Number(animation.effect?.getTiming().duration) === 220))).toBe(true);
  await expect.poll(() => page.locator('.dock').evaluate(el => el.getAnimations().every(animation => animation.playState === 'finished'))).toBe(true);
  const syncs = await calls(page, 'window.sync');
  expect(syncs.length).toBeLessThanOrEqual(5);
  expect(syncs.some((call: any) => call.params.rects.some((rect: any) => rect.width >= 376))).toBe(true);
  await page.getByRole('button', { name: '完成整理', exact: true }).click();
  await projectButton(page, '画图').click();
  await expect.poll(() => calls(page, 'shell.openItem')).toHaveLength(1);
});

test('reduced motion applies an adaptive width change without a FLIP animation', async ({ page }) => {
  const seed = structuredClone(initial);
  seed.preferences.width = 320;
  seed.preferences.reducedMotion = true;
  await attachHost(page, seed);
  await page.getByRole('button', { name: '整理图标', exact: true }).click();
  await hold(page, projectButton(page, '编辑器'));
  const destination = await center(page.locator('.dock-slot[data-drop-project="paint"]'));
  await page.mouse.move(destination.x, destination.y, { steps: 8 });
  await page.mouse.up();
  await expect(page.locator('.dock')).toHaveCSS('width', '320px');
  expect(await page.locator('.dock').evaluate(el => el.getAnimations().some(animation => Number(animation.effect?.getTiming().duration) === 220))).toBe(false);
});

test('holding the folder icon and releasing over Open current directory opens its token once', async ({ page }) => {
  await attachHost(page);
  await hold(page, projectButton(page, '电梯贴'));
  const action=page.getByRole('button',{name:'打开当前目录',exact:true});
  await expect(action).toBeVisible();
  const target=await center(action);await page.mouse.move(target.x,target.y,{steps:6});
  await expect(action).toHaveClass(/selected/);
  expect(await calls(page,'folder.open')).toHaveLength(0);
  await page.mouse.up();
  await expect.poll(()=>calls(page,'folder.open')).toHaveLength(1);
  expect((await calls(page,'folder.open'))[0].params.entryId).toBe('token-root');
  await expect(page.locator('.stack-panel')).toHaveCount(0);
});

test('leaving Open current directory before release cancels, and keyboard activation remains available', async ({ page }) => {
  await attachHost(page);
  await hold(page, projectButton(page,'电梯贴'));
  const action=page.getByRole('button',{name:'打开当前目录',exact:true});await expect(action).toBeVisible();
  const target=await center(action);await page.mouse.move(target.x,target.y);await expect(action).toHaveClass(/selected/);
  await page.mouse.move(10,650);await page.mouse.up();expect(await calls(page,'folder.open')).toHaveLength(0);
  await page.getByRole('button',{name:'展开 电梯贴 堆叠',exact:true}).click();await action.focus();await page.keyboard.press('Enter');
  await expect.poll(()=>calls(page,'folder.open')).toHaveLength(1);
});

test('an idle submenu permits native retraction after leaving, while a held gesture keeps it open', async ({ page }) => {
  await attachHost(page);
  await page.getByRole('button',{name:'展开 电梯贴 堆叠',exact:true}).click();
  await expect(page.locator('.directory-row')).toHaveCount(3);
  const interactive=()=>page.evaluate(()=>(window as any).__calls.filter((c:any)=>c.method==='window.sync').at(-1).params.interacting);
  await expect.poll(interactive).toBe(true);
  await page.mouse.move(10,650);await expect.poll(interactive).toBe(false);
  await expect(page.locator('.stack-panel')).toBeVisible(); // Native host owns its exit request.
  await hold(page,page.locator('[data-item-id="token-document"]'));
  await page.waitForTimeout(340);await page.mouse.move(10,650);
  await expect.poll(interactive).toBe(true);
  await page.mouse.up();await expect.poll(interactive).toBe(false);expect(await calls(page,'folder.open')).toHaveLength(0);
});

test('large directories scroll inside fixed tiles while Open current directory remains inside a short native viewport', async ({ page }) => {
  await page.setViewportSize({width:1100,height:500});
  const seed=structuredClone(initial);seed.preferences.height=160;
  const listing={...root,entries:Array.from({length:60},(_,i)=>({id:`entry-${i}`,name:`文档 ${i}.pdf`,kind:'file' as const}))};
  await attachHost(page,seed,listing);
  await page.getByRole('button',{name:'展开 电梯贴 堆叠',exact:true}).click();await expect(page.locator('.directory-row')).toHaveCount(60);
  const grid=await page.locator('.directory-items').evaluate(el=>({scroll:el.scrollHeight>el.clientHeight,overflow:el.scrollWidth>el.clientWidth}));
  expect(grid).toEqual({scroll:true,overflow:false});
  const footer=await page.getByRole('button',{name:'打开当前目录',exact:true}).boundingBox();
  expect(footer!.y+footer!.height).toBeLessThanOrEqual(500);
});
