import { expect, test, type Page } from '@playwright/test';
import { defaults } from '../src/data';

const png = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aWZkAAAAASUVORK5CYII=';
async function host(page: Page, delayed = false, payload: string | null = png) {
  await page.addInitScript(({ defaults, delayed, payload }) => {
    const w = window as any;
    const listeners: any[] = [];
    let state = { schemaVersion: 1, revision: 0, preferences: { ...defaults, reducedMotion: true }, projects: [{ id: 'software', name: '软件', description: '', color: 'mint', pinned: true, items: [{ id: 'momo', name: 'MOMO', kind: 'app', path: 'C:\\MOMO.lnk' }] }] };
    w.__calls = []; w.__icons = [];
    w.__saveIcon = () => import('/src/bridge.ts').then(bridge => bridge.request('app.saveState', { state, expectedRevision: state.revision }));
    w.__refreshIcon = () => { state = structuredClone(state); state.revision++; emit({ type: 'event', event: 'app.stateChanged', data: state }); };
    const emit = (data: any) => listeners.forEach(l => l({ data: { protocol: 1, ...data } }));
    w.__replaceIcon = () => { state = structuredClone(state); state.revision++; state.projects[0].items[0].path = 'C:\\new.exe'; emit({ type: 'event', event: 'app.stateChanged', data: state }); };
    w.__respondIcon = (index: number, dataUrl: string | null) => emit({ type: 'response', id: w.__icons[index].id, ok: true, result: { dataUrl } });
    w.chrome ||= {};
    w.chrome.webview = { addEventListener: (_: string, listener: any) => listeners.push(listener), postMessage: (req: any) => {
      w.__calls.push(req);
      if (req.method === 'shell.getIcon') { w.__icons.push(req); if (delayed) return; }
      if (req.method === 'app.saveState') { state = structuredClone(req.params.state); state.revision++; }
      const result = req.method === 'app.getState' || req.method === 'app.saveState' ? state : req.method === 'shell.getIcon' ? { dataUrl: payload } : { applied: true };
      setTimeout(() => emit({ type: 'response', id: req.id, ok: true, result }), 5);
      if (req.method === 'app.getState') setTimeout(() => emit({ type: 'event', event: 'window.visibility', data: { visible: true, visibilityId: 1 } }), 50);
    } };
  }, { defaults, delayed, payload });
  await page.goto('/?view=dock&mode=native');
  await expect(page.locator('.dock')).toBeVisible();
}

test('saved software renders a native PNG inside glass without sending its path', async ({ page }) => {
  await host(page);
  const image = page.locator('.native-software-icon img');
  await expect(image).toHaveAttribute('src', png);
  await expect.poll(() => image.evaluate((img: HTMLImageElement) => img.naturalWidth)).toBeGreaterThan(0);
  const calls = await page.evaluate(() => (window as any).__icons);
  expect(calls).toHaveLength(1);
  expect(calls[0].params).toEqual({ projectId: 'software', itemId: 'momo', size: 48 });
  expect(await page.evaluate(() => (window as any).__calls.some((c: any) => c.method === 'shell.openItem'))).toBe(false);
});

test('late old icon completion cannot replace a changed saved entry', async ({ page }) => {
  await host(page, true);
  await expect.poll(() => page.evaluate(() => (window as any).__icons.length)).toBe(1);
  await page.evaluate(() => (window as any).__replaceIcon());
  await expect.poll(() => page.evaluate(() => (window as any).__icons.length)).toBe(2);
  await page.evaluate(png => (window as any).__respondIcon(0, png), png);
  await expect(page.locator('.native-software-icon img')).toHaveCount(0);
  await page.evaluate(png => (window as any).__respondIcon(1, png), png);
  await expect(page.locator('.native-software-icon img')).toHaveAttribute('src', png);
});

test('missing or invalid native icon stays on the generic fallback without retry loops', async ({ page }) => {
  await host(page, false, 'data:image/png;base64,AAAA');
  await expect.poll(() => page.evaluate(() => (window as any).__icons.length)).toBe(1);
  await expect(page.locator('.crystal-item-app')).toBeVisible();
  await expect(page.locator('.native-software-icon img')).toHaveCount(0);
  await page.waitForTimeout(250);
  expect(await page.evaluate(() => (window as any).__icons.length)).toBe(1);
});

for (const trigger of ['__saveIcon', '__refreshIcon']) test(`previously missing icon refreshes after committed state ${trigger}`, async ({ page }) => {
  await host(page, true);
  await expect.poll(() => page.evaluate(() => (window as any).__icons.length)).toBe(1);
  await page.evaluate(() => (window as any).__respondIcon(0, null));
  await expect(page.locator('.crystal-item-app')).toBeVisible();
  await page.evaluate(trigger => (window as any)[trigger](), trigger);
  await expect.poll(() => page.evaluate(() => (window as any).__icons.length)).toBe(2);
  await page.evaluate(png => (window as any).__respondIcon(1, png), png);
  await expect(page.locator('.native-software-icon img')).toHaveAttribute('src', png);
});

test('successful brand icon remains visible during an unrelated committed refresh', async ({ page }) => {
  await host(page, true);
  await expect.poll(() => page.evaluate(() => (window as any).__icons.length)).toBe(1);
  await page.evaluate(png => (window as any).__respondIcon(0, png), png);
  await expect(page.locator('.native-software-icon img')).toHaveAttribute('src', png);
  await page.evaluate(() => (window as any).__saveIcon());
  await expect.poll(() => page.evaluate(() => (window as any).__icons.length)).toBe(2);
  await expect(page.locator('.native-software-icon img')).toHaveAttribute('src', png);
  await page.evaluate(png => (window as any).__respondIcon(1, png), png);
});
