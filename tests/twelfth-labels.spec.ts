import { test, expect, type Page } from '@playwright/test';
import { defaults } from '../src/data';

const projects = [
  { id: 'folder', name: '电梯贴', description: '', color: 'mint', pinned: true, items: [{ id: 'main', name: '电梯贴', path: 'C:\\Labels\\Folder', kind: 'folder' }] },
  { id: 'app', name: 'MOMO 智能画布.lnk', description: '', color: 'blue', pinned: true, items: [{ id: 'main', name: 'MOMO', path: 'C:\\Labels\\MOMO.lnk', kind: 'app' }] },
  { id: 'group', name: 'Adobe Photoshop 2026.lnk', description: '', color: 'violet', pinned: true, items: [{ id: 'main', name: 'Photoshop', path: 'C:\\Labels\\Adobe.lnk', kind: 'app' }, { id: 'extra', name: 'Illustrator', path: 'C:\\Labels\\AI.lnk', kind: 'app' }] },
  { id: 'reference', name: 'momo智能画布', description: '', color: 'mint', pinned: true, items: [{ id: 'main', name: 'momo智能画布', path: 'C:\\Labels\\Reference', kind: 'folder' }] },
];
async function setup(page: Page, height: number, iconSize: number, theme: 'light' | 'dark', system = false) {
  const state = { schemaVersion: 1, revision: 1, projects, preferences: { ...defaults, height, iconSize, theme, width: 640, material: 'solid' } };
  await page.addInitScript(state => {
    const host = window as any, listeners: Array<(event: any) => void> = [];
    host.chrome ||= {}; host.__labelCalls = [];
    host.chrome.webview = { addEventListener: (_: string, callback: any) => listeners.push(callback), postMessage: (req: any) => {
      host.__labelCalls.push(req);
      const result = req.method === 'app.getState' ? state : req.method === 'window.sync' ? { applied: true } : req.method === 'system.getIntegration' ? { autoStart: false, autoStartHere: false, desktopShortcut: false } : req.method === 'shell.getIcon' ? { dataUrl: 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLttAAAAABJRU5ErkJggg==' } : { accepted: true };
      setTimeout(() => listeners.forEach(callback => callback({ data: { protocol: 1, type: 'response', id: req.id, ok: true, result } })), 1);
      if (req.method === 'app.getState') setTimeout(() => listeners.forEach(callback => callback({ data: { protocol: 1, type: 'event', event: 'window.visibility', data: { visible: true, visibilityId: 1 } } })), 40);
    } };
  }, state);
  await page.goto(system ? '/?mode=native&section=system' : '/?view=dock&mode=native');
  if (system) { await page.getByRole('button', { name: '设置与备份', exact: true }).click(); return; }
  await expect(page.locator('.dock-label')).toHaveCount(4);
  await expect.poll(() => page.locator('.dock-wrap').evaluate(el => el.getAnimations().every(animation => animation.playState === 'finished'))).toBe(true);
  await expect(page.locator('.dock-project>.native-software-icon')).toHaveCount(1);
}
async function measurements(page: Page) {
  return page.locator('.dock-label').evaluateAll(labels => labels.map(label => {
    const style = getComputedStyle(label), box = label.getBoundingClientRect();
    const button = label.closest('.dock-project')!.getBoundingClientRect();
    const rail = label.closest<HTMLElement>('.dock-launchers')!, railBox = rail.getBoundingClientRect();
    const range = document.createRange(); range.selectNodeContents(label); const glyph = range.getBoundingClientRect();
    return { name: label.textContent, font: style.fontSize, line: style.lineHeight, weight: style.fontWeight,
      top: box.top, bottom: box.bottom, height: box.height, glyphTop: glyph.top, glyphBottom: glyph.bottom,
      railTop: railBox.top + rail.clientTop, railBottom: railBox.top + rail.clientTop + rail.clientHeight, buttonTop: button.top, buttonBottom: button.bottom };
  }));
}

for (const viewport of [1440, 340]) for (const theme of ['light', 'dark'] as const) for (const [height, icon] of [[64, 24], [64, 64], [88, 40], [88, 64], [160, 24], [160, 64]]) {
  test(`labels share a complete baseline: viewport ${viewport}, ${theme}, height ${height}, icon ${icon}`, async ({ page }, info) => {
    await page.setViewportSize({ width: viewport, height: 900 });
    await setup(page, height, icon, theme);
    const rows = await measurements(page);
    await info.attach('label-bounds.json', { body: JSON.stringify(rows, null, 2), contentType: 'application/json' });
    expect(new Set(rows.map(row => `${row.font}/${row.line}/${row.weight}`)).size).toBe(1);
    expect(Math.max(...rows.map(row => row.top)) - Math.min(...rows.map(row => row.top))).toBeLessThanOrEqual(0.5);
    for (const row of rows) {
      expect(row.height, `${row.name}: full line box`).toBeGreaterThanOrEqual(parseFloat(row.line) - 0.5);
      expect(row.glyphTop, `${row.name}: glyph top`).toBeGreaterThanOrEqual(row.top - 0.5);
      expect(row.glyphBottom, `${row.name}: glyph bottom`).toBeLessThanOrEqual(row.bottom + 0.5);
      expect(row.top, `${row.name}: rail top`).toBeGreaterThanOrEqual(row.railTop - 0.5);
      expect(row.bottom, `${row.name}: rail bottom`).toBeLessThanOrEqual(row.railBottom + 0.5);
      expect(row.top, `${row.name}: button top`).toBeGreaterThanOrEqual(row.buttonTop - 0.5);
      expect(row.bottom, `${row.name}: button bottom`).toBeLessThanOrEqual(row.buttonBottom + 0.5);
    }
    expect(rows[3].font).toBe('12px');
    if (viewport === 340) {
      await page.locator('.dock-label').last().scrollIntoViewIfNeeded();
      await expect(page.locator('.dock-label').last()).toBeInViewport();
    }
    expect(await page.evaluate(() => (window as any).__labelCalls.filter((call: any) => ['app.saveState', 'shell.openItem'].includes(call.method)))).toEqual([]);
  });
}

for (const theme of ['light', 'dark'] as const) test(`system guide uses the same card inset as its content: ${theme}`, async ({ page }) => {
  await setup(page, 88, 40, theme, true);
  const guide = page.getByRole('button', { name: '查看操作指南', exact: true });
  await expect(guide).toBeVisible();
  const bounds = await guide.evaluate(button => {
    const card = button.closest('.cp-card')!, content = card.querySelector('.info-box')!;
    const b = button.getBoundingClientRect(), c = card.getBoundingClientRect(), i = content.getBoundingClientRect();
    return { leftInset: b.left - c.left, bottomInset: c.bottom - b.bottom, contentLeft: i.left, buttonLeft: b.left };
  });
  expect(bounds.leftInset).toBeGreaterThanOrEqual(14); expect(bounds.bottomInset).toBeGreaterThanOrEqual(14);
  expect(Math.abs(bounds.buttonLeft - bounds.contentLeft)).toBeLessThanOrEqual(0.5);
  await guide.click(); await expect(page.getByRole('dialog')).toBeVisible();
});
