import { test, expect, type Page } from '@playwright/test';
import { initialState } from '../src/data';
async function attachHost(page: Page, echo = false, initial = initialState) {
  await page.addInitScript(({ initial, echo }) => {
    let state = initial;
    const listeners: Array<(e: { data: unknown }) => void> = [];
    const calls: Array<{ method: string; params: any }> = [];
    const host = window as unknown as { chrome: any; __calls: typeof calls; __hostState: typeof state };
    host.__calls = calls; host.__hostState = state;
    let visibilityId = 0;
    (window as any).__emitVisibility = (visible: boolean) => { const id = ++visibilityId; listeners.forEach(listener => listener({ data: { protocol: 1, type: 'event', event: 'window.visibility', data: { visible, visibilityId: id } } })); };
    host.chrome = host.chrome || {};
    host.chrome.webview = {
      addEventListener: (_: string, listener: typeof listeners[number]) => listeners.push(listener),
      postMessage: (req: { method: string; params: any; id: string }) => {
        calls.push(req);
        if (req.method === 'window.sync') (req as any).domExpanded = !!document.querySelector('.dock');
        let result: unknown;
        if (req.method === 'app.getState') result = state;
        else if (req.method === 'app.saveState') {
          state = { ...req.params.state, revision: state.revision + 1 }; result = state; host.__hostState = state;
          if (echo) listeners.forEach(listener => listener({ data: { protocol: 1, type: 'event', event: 'app.stateChanged', data: state } }));
        } else if (req.method === 'window.sync') result = { applied: true };
        else if (req.method === 'shell.pickFolder') result = null;
        else if (req.method === 'shell.pickFiles') result = (window as any).__pickedFiles ?? [];
        else if (req.method === 'shell.resolveDrop') result = (window as any).__resolvedDrop ?? [];
        else result = { accepted: true };
        setTimeout(() => listeners.forEach(listener => listener({ data: { protocol: 1, type: 'response', id: req.id, ok: true, result } })), req.method === 'shell.resolveDrop' ? ((window as any).__dropDelay ?? 20) : 20);
        if (req.method === 'app.getState') setTimeout(() => (window as any).__emitVisibility(true), 100);
      },
      postMessageWithAdditionalObjects: (req: any) => host.chrome.webview.postMessage(req),
    };
  }, { initial, echo });
}

test('bulk file replacement rejects capacity overflow without deleting existing shortcuts', async ({ page }) => {
  const initial = structuredClone(initialState);
  initial.projects[0].items = Array.from({ length: 200 }, (_, n) => ({ id: `item-${n}`, name: `file-${n}`, path: `C:\\file-${n}.txt`, kind: 'file' }));
  await attachHost(page, false, initial); await page.goto('/?mode=native&section=projects');
  await page.evaluate(() => { (window as any).__pickedFiles = [{ name: 'new1', path: 'C:\\one.txt', kind: 'file' }, { name: 'new2', path: 'C:\\two.txt', kind: 'file' }]; });
  await page.getByRole('button', { name: '修改 品牌设计', exact: true }).click();
  await page.getByRole('button', { name: '选择入口 1 文件或软件', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('本次未添加');
  await expect(page.getByLabel('入口 200 名称', { exact: true })).toHaveValue('file-199');
  await expect(page.getByLabel('入口 1 名称', { exact: true })).toHaveValue('file-0');
  await page.getByRole('button', { name: '取消', exact: true }).click();
  expect(await page.evaluate(() => (window as any).__hostState.projects[0].items.length)).toBe(200);
});
test('native bridge accepts self-save echo without false conflict', async ({ page }) => {
  await attachHost(page, true); await page.goto('/?mode=native&section=appearance');
  await page.getByLabel('初始宽度', { exact: true }).fill('720');
  await expect.poll(() => page.evaluate(() => (window as any).__hostState.preferences.width)).toBe(720);
  await page.getByLabel('初始宽度', { exact: true }).fill('800');
  await expect.poll(() => page.evaluate(() => (window as any).__hostState.preferences.width)).toBe(800);
  await expect(page.getByRole('alert')).toHaveCount(0);
});

test('native repeated hide and show never emits stale visibility for committed DOM', async ({ page }) => {
  await attachHost(page); await page.goto('/?view=dock&mode=native');
  const dock = page.getByRole('navigation', { name: '快捷启动面板' });
  await expect(dock).toBeVisible();
  for (let i = 0; i < 5; i++) {
    await page.evaluate(() => (window as any).__emitVisibility(false));
    await expect(dock).toHaveCount(0);
    await page.evaluate(() => (window as any).__emitVisibility(true));
    await expect(dock).toBeVisible();
  }
  await expect.poll(() => page.evaluate(() => (window as any).__calls.filter((c: any) => c.method === 'window.sync').length)).toBeGreaterThan(10);
  expect(await page.evaluate(() => (window as any).__calls.filter((c: any) => c.method === 'window.sync' && c.params.expanded !== c.domExpanded))).toEqual([]);
});
test('native overlay transparent root, compact hit region, final animation bounds and search route', async ({ page }) => {
  await attachHost(page); await page.goto('/?view=dock&mode=native');
  await expect(page.getByRole('navigation', { name: '快捷启动面板' })).toBeVisible();
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).backgroundColor)).toBe('rgba(0, 0, 0, 0)');
  await expect.poll(() => page.locator('.dock-wrap').evaluate(el => el.getAnimations({ subtree: true }).filter(animation => animation.playState === 'running').length)).toBe(0);
  await expect.poll(() => page.evaluate(() => {
    const call = (window as any).__calls.filter((c: any) => c.method === 'window.sync').at(-1);
    const r = document.querySelector('.dock')?.getBoundingClientRect();
    return call && r ? Math.abs(call.params.rects[1].y - r.y) : 999;
  })).toBeLessThan(0.5);
  const rects = await page.evaluate(() => (window as any).__calls.filter((c: any) => c.method === 'window.sync').at(-1).params.rects);
  expect(rects[0].width).toBe(116); expect(rects[1].width).toBe(640);
  await page.keyboard.press('Control+k');
  await expect.poll(() => page.evaluate(() => (window as any).__calls.some((c: any) => c.method === 'window.openSettings' && c.params.section === 'search'))).toBeTruthy();
  await expect(page.getByRole('dialog')).toHaveCount(0);
});

test('closing retains the native surface and reverses from its current frame', async ({ page }) => {
  await attachHost(page); await page.goto('/?view=dock&mode=native');
  const surface = page.locator('.dock-wrap');
  await expect(surface).toBeVisible();
  await expect.poll(() => surface.evaluate(el => el.getAnimations().some(a => a.playState === 'running'))).toBe(false);
  await page.evaluate(() => (window as any).__emitVisibility(false));
  await expect(surface).toHaveClass(/dock-closing/);
  const before = await surface.evaluate(el => {
    const animation = el.getAnimations()[0]; animation.pause(); animation.currentTime = 70;
    const style = getComputedStyle(el); return { opacity: Number(style.opacity), transform: style.transform };
  });
  expect(before.opacity).toBe(1); // The panel stays visible while physically sliding out.
  expect(before.transform).not.toBe('matrix(1, 0, 0, 1, 0, 0)');
  expect(await page.evaluate(() => (window as any).__calls.filter((c: any) => c.method === 'window.sync').at(-1).params.expanded)).toBe(true);
  expect(await page.evaluate(() => (window as any).__calls.filter((c: any) => c.method === 'window.sync').at(-1).params.visibilityId)).toBe(2);
  await page.evaluate(() => (window as any).__emitVisibility(true));
  await expect(surface).not.toHaveClass(/dock-closing/);
  const first = await surface.evaluate(el => (el.getAnimations()[0].effect as KeyframeEffect).getKeyframes()[0]);
  expect(Number(first.opacity)).toBeCloseTo(before.opacity, 3);
  expect(first.transform).toBe(before.transform);
  await expect.poll(() => surface.evaluate(el => Number(getComputedStyle(el).opacity))).toBe(1);
  expect(await page.evaluate(() => (window as any).__calls.filter((c: any) => c.method === 'window.sync').at(-1).params.visibilityId)).toBe(3);
  await page.evaluate(() => (window as any).__emitVisibility(false));
  await expect(surface).toHaveCount(0);
  await expect.poll(() => page.evaluate(() => (window as any).__calls.filter((c: any) => c.method === 'window.sync').at(-1).params.expanded)).toBe(false);
  expect(await page.evaluate(() => (window as any).__calls.filter((c: any) => c.method === 'window.sync').at(-1).params.visibilityId)).toBe(4);
});

test('pointer press, stack navigation and file dragging explicitly hold the dock open', async ({ page }) => {
  await attachHost(page); await page.goto('/?view=dock&mode=native');
  const button = page.getByRole('button', { name: '打开 品牌设计 主目录，长按展开堆叠' });
  const held = () => page.evaluate(() => (window as any).__calls.filter((c: any) => c.method === 'window.sync').at(-1)?.params.interacting);
  await expect(button).toBeVisible(); await expect.poll(held).toBe(false);
  await expect.poll(() => page.locator('.dock-wrap').evaluate(el => el.getAnimations().every(a => a.playState === 'finished'))).toBe(true);
  const box = await button.boundingBox(); await page.mouse.move(box!.x + box!.width / 2, box!.y + box!.height / 2); await page.mouse.down();
  await expect.poll(held).toBe(true);
  await expect(page.getByRole('region', { name: '品牌设计 文件夹堆叠' })).toBeVisible();
  await page.mouse.move(20, 650); await page.mouse.up();
  await expect.poll(held).toBe(false);
  await button.focus(); await page.keyboard.press('ArrowDown'); await expect.poll(held).toBe(true);
  await page.keyboard.press('Escape');
  await page.locator('.dock-container').evaluate(el => {
    const dataTransfer = new DataTransfer(); dataTransfer.items.add(new File(['demo'], 'demo.txt'));
    el.dispatchEvent(new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer }));
  });
  await expect(page.locator('.dock-drop-hint')).toBeVisible(); await expect.poll(held).toBe(true);
  await page.locator('.dock-container').dispatchEvent('dragleave', { relatedTarget: null });
  await page.evaluate(() => (document.activeElement as HTMLElement)?.blur());
  await expect.poll(held).toBe(false);
});

test('file import stays interactive after mouse release until the host responds', async ({ page }) => {
  await attachHost(page); await page.goto('/?view=dock&mode=native');
  await expect(page.locator('.dock')).toBeVisible();
  await page.evaluate(() => {
    (window as any).__dropDelay = 1000;
    (window as any).__resolvedDrop = [{ name: '新文件', path: 'C:\\new.txt', kind: 'file' }];
    const dataTransfer = new DataTransfer(); dataTransfer.items.add(new File(['demo'], 'new.txt'));
    document.querySelector('.dock')!.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer }));
  });
  const held = () => page.evaluate(() => (window as any).__calls.filter((c: any) => c.method === 'window.sync').at(-1)?.params.interacting);
  await expect.poll(held).toBe(true);
  await expect(page.locator('.dock-drop-hint')).toHaveCount(0);
  await expect.poll(() => page.evaluate(() => (window as any).__hostState.projects.some((p: any) => p.items.some((i: any) => i.path === 'C:\\new.txt')))).toBe(true);
  await expect.poll(held).toBe(false);
  expect(await page.evaluate(() => (window as any).__calls.some((c: any) => c.method === 'shell.openItem'))).toBe(false);
});

test('clicking the handle stays closed with the pointer parked over it', async ({ page }) => {
  await attachHost(page); await page.goto('/?view=dock&mode=native');
  for (let i = 0; i < 3; i++) {
    await expect(page.locator('.dock')).toBeVisible();
    await page.mouse.move(20, 400);
    await page.getByRole('button', { name: '收起面板', exact: true }).click();
    await expect(page.locator('.dock')).toHaveCount(0);
    // A native hit-region change can deliver a fresh enter without cursor motion.
    // Only the host's physical reentry gate may reopen the native overlay.
    await page.locator('.dock-handle').dispatchEvent('pointerover', { pointerId: 1, pointerType: 'mouse', bubbles: true });
    // Advancing beyond the hover dwell must not reopen an explicitly closed dock.
    await page.waitForTimeout(240);
    await expect(page.locator('.dock')).toHaveCount(0);
    if (i < 2) await page.evaluate(() => (window as any).__emitVisibility(true));
  }
});
