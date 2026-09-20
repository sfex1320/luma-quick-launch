import { test, expect, type Page } from '@playwright/test';
import { defaults } from '../src/data';
import type { AppState } from '../src/contracts';

const base: AppState = {
  schemaVersion: 1, revision: 0, preferences: defaults,
  projects: [{ id: 'p', name: '基准项目', description: '', color: 'mint', pinned: true,
    items: [{ id: 'main', name: '主目录', path: 'C:\\Work', kind: 'folder' }] }],
};

async function attachHost(page: Page, initial: AppState = base) {
  await page.addInitScript(initialState => {
    const host = window as any;
    let state = structuredClone(initialState);
    const listeners: Array<(event: any) => void> = [];
    const calls: any[] = [], saves: any[] = [];
    const respond = (id: string, result: unknown, ok = true, message = '配置冲突') =>
      listeners.forEach(listener => listener({ data: { protocol: 1, type: 'response', id, ok, ...(ok ? { result } : { error: { code: 'REVISION_CONFLICT', message } }) } }));
    host.__calls = calls; host.__saves = saves;
    host.__state = () => structuredClone(state);
    host.__setState = (next: AppState) => { state = structuredClone(next); };
    host.__emitState = (next: AppState) => {
      state = structuredClone(next);
      listeners.forEach(listener => listener({ data: { protocol: 1, type: 'event', event: 'app.stateChanged', data: structuredClone(state) } }));
    };
    host.__emitVisibility = () => listeners.forEach(listener => listener({ data: { protocol: 1, type: 'event', event: 'window.visibility', data: { visible: true, visibilityId: state.revision + 1 } } }));
    host.__replySave = (index: number, override?: AppState) => {
      const request = saves[index];
      const next = override ?? { ...request.params.state, revision: request.params.expectedRevision + 1 };
      state = structuredClone(next); respond(request.id, state);
    };
    host.chrome ||= {};
    host.chrome.webview = {
      addEventListener: (_: string, listener: any) => listeners.push(listener),
      postMessage: (request: any) => {
        calls.push(request);
        if (request.method === 'app.getState') setTimeout(() => respond(request.id, structuredClone(state)), 5);
        else if (request.method === 'app.saveState') saves.push(request);
        else if (request.method === 'window.sync') setTimeout(() => respond(request.id, { applied: true }), 5);
        else setTimeout(() => respond(request.id, { accepted: true }), 5);
      },
    };
  }, initial);
}

const saveCount = (page: Page) => page.evaluate(() => (window as any).__saves.length);

test('editing again while a save is pending schedules a second CAS save', async ({ page }) => {
  await attachHost(page); await page.goto('/?mode=native&section=appearance');
  await expect(page.getByRole('slider', { name: '初始宽度' })).toBeVisible();
  await page.getByRole('slider', { name: '初始宽度' }).fill('720');
  await expect.poll(() => saveCount(page)).toBe(1);
  await page.getByRole('slider', { name: '面板高度' }).fill('120');
  await page.evaluate(() => (window as any).__replySave(0));
  await expect.poll(() => saveCount(page)).toBe(2);
  const second = await page.evaluate(() => (window as any).__saves[1]);
  expect(second.params.expectedRevision).toBe(1);
  expect(second.params.state.preferences).toMatchObject({ width: 720, height: 120 });
  await page.evaluate(() => (window as any).__replySave(1));
  await expect.poll(() => page.evaluate(() => (window as any).__state().revision)).toBe(2);
  await expect(page.getByText('已保存 · 本机配置')).toBeVisible();
});

test('same-tick remote events merge with a local edit and settings matches the dock', async ({ page, context }) => {
  await attachHost(page); await page.goto('/?mode=native&section=appearance');
  await page.getByRole('slider', { name: '初始宽度' }).fill('720');
  const remote1 = structuredClone(base); remote1.revision = 1; remote1.preferences.theme = 'dark';
  const remote2 = structuredClone(remote1); remote2.revision = 2; remote2.preferences.iconSize = 52;
  await page.evaluate(([one, two]) => { (window as any).__emitState(one); (window as any).__emitState(two); }, [remote1, remote2]);
  await expect(page.getByRole('slider', { name: '初始宽度' })).toHaveValue('720');
  await expect(page.getByRole('slider', { name: '图标大小' })).toHaveValue('52');
  await expect(page.getByLabel('深色模式')).toHaveAttribute('aria-pressed', 'true');
  await expect.poll(() => saveCount(page)).toBe(1);
  await page.evaluate(() => (window as any).__replySave(0));
  const final = await page.evaluate(() => (window as any).__state());

  const dock = await context.newPage(); await attachHost(dock, final); await dock.goto('/?view=dock&mode=native');
  await dock.evaluate(() => (window as any).__emitVisibility());
  await expect(dock.getByRole('navigation', { name: '快捷启动面板' })).toBeVisible();
  expect((await dock.locator('.dock').boundingBox())!.width).toBeCloseTo(720, 0);
  await expect(dock.getByRole('button', { name: /打开 基准项目 主目录/ })).toBeVisible();
});

test('a true same-field conflict preserves the local settings view and remote dock data', async ({ page, context }) => {
  await attachHost(page); await page.goto('/?mode=native&section=appearance');
  await page.getByRole('slider', { name: '初始宽度' }).fill('720');
  const remote = structuredClone(base); remote.revision = 1; remote.preferences.width = 800;
  await page.evaluate(value => (window as any).__emitState(value), remote);
  await expect(page.getByRole('alert')).toContainText('未覆盖另一窗口的配置');
  await expect(page.getByRole('slider', { name: '初始宽度' })).toHaveValue('720');
  expect(await saveCount(page)).toBe(0);

  const dock = await context.newPage(); await attachHost(dock, remote); await dock.goto('/?view=dock&mode=native');
  await dock.evaluate(() => (window as any).__emitVisibility());
  await expect(dock.getByRole('navigation', { name: '快捷启动面板' })).toBeVisible();
  expect((await dock.locator('.dock').boundingBox())!.width).toBeCloseTo(800, 0);
});

test('a visible wake event re-reads host state', async ({ page }) => {
  await attachHost(page); await page.goto('/?mode=native&section=projects');
  await expect(page.getByRole('button', { name: '修改 基准项目', exact: true })).toBeVisible();
  const remote = structuredClone(base); remote.revision = 1; remote.projects[0].name = '唤醒后项目';
  const before = await page.evaluate(() => (window as any).__calls.filter((call: any) => call.method === 'app.getState').length);
  await page.evaluate(value => { (window as any).__setState(value); (window as any).__emitVisibility(); }, remote);
  await expect.poll(() => page.evaluate(() => (window as any).__calls.filter((call: any) => call.method === 'app.getState').length)).toBeGreaterThan(before);
  await expect(page.getByRole('button', { name: '修改 唤醒后项目', exact: true })).toBeVisible();
});
