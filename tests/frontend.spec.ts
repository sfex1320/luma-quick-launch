import { test, expect } from '@playwright/test';
const openPage = async (page: import('@playwright/test').Page, name: string) => {
  await page.getByRole('navigation', { name: '驾驶舱导航' }).locator('button', { hasText: name }).click();
};
test.beforeEach(async ({ page }) => {
  await page.goto('/'); await expect(page.getByRole('heading', { name: '总览' })).toBeVisible();
  // Manual coordinate gestures must measure the settled target, as locator.click would.
  await expect.poll(() => page.locator('.dock-wrap').evaluate(el => el.getAnimations().every(a => a.playState === 'finished'))).toBe(true);
});
test('long press slides to one child without opening the root', async ({ page }) => {
  const origin = page.getByRole('button', { name: '打开 品牌设计 主目录，长按展开堆叠' });
  const box = await origin.boundingBox();
  await page.mouse.move(box!.x + box!.width / 2, box!.y + box!.height / 2); await page.mouse.down();
  await expect(page.getByRole('region', { name: '品牌设计 文件夹堆叠' })).toBeVisible();
  const target = page.locator('[data-item-id="design"]'); const b = await target.boundingBox();
  await page.mouse.move(b!.x + b!.width / 2, b!.y + b!.height / 2, { steps: 6 });
  await expect(target).toHaveClass(/selected/); await page.mouse.up();
  await expect(page.locator('.toast[role=status]')).toContainText('设计源文件');
  await expect(page.locator('.toast[role=status]')).toContainText('未访问真实文件');
  await expect(page.getByRole('region', { name: '品牌设计 文件夹堆叠' })).toHaveCount(0);
});
test('long press cancellation never launches', async ({ page }) => {
  const b = await page.getByRole('button', { name: '打开 品牌设计 主目录，长按展开堆叠' }).boundingBox();
  await page.mouse.move(b!.x + 25, b!.y + 25); await page.mouse.down();
  await expect(page.getByRole('region', { name: '品牌设计 文件夹堆叠' })).toBeVisible();
  await page.mouse.move(300, 850); await page.mouse.up();
  await expect(page.locator('.toast[role=status]')).toHaveCount(0);
});
test('project editing persists and search finds the new item', async ({ page }) => {
  await openPage(page, '项目');
  await page.getByRole('button', { name: '新建项目', exact: true }).last().click();
  await page.getByLabel('项目名称', { exact: true }).fill('验收项目');
  await page.getByLabel('入口 1 路径').fill('D:\\Acceptance');
  await page.getByRole('button', { name: '创建项目', exact: true }).click();
  await expect(page.locator('.project-grid').getByRole('button', { name: '验收项目', exact: true })).toBeVisible();
  await expect.poll(() => page.evaluate(() => JSON.parse(localStorage.getItem('luma.state.v1') || '{}').projects?.some((p: { name: string }) => p.name === '验收项目'))).toBeTruthy();
  await page.reload();
  await openPage(page, '项目');
  await expect(page.locator('.project-grid').getByRole('button', { name: '验收项目', exact: true })).toBeVisible();
  await page.getByRole('button', { name: '搜索全部入口' }).click();
  await page.getByLabel('搜索项目和文件夹').fill('验收项目');
  await expect(page.locator('.search-results button')).toHaveCount(1);
});
test('appearance, overflow, theme and persisted settings', async ({ page }) => {
  await openPage(page, '外观');
  await page.getByLabel('面板宽度', { exact: true }).fill('320');
  await expect(page.getByRole('button', { name: '更多项目', exact: true })).toBeVisible();
  await page.getByRole('button', { name: '更多项目', exact: true }).click();
  await expect(page.getByRole('region', { name: '更多项目列表' })).toBeVisible();
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: '深色模式', exact: true }).click();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).backgroundColor)).toBe('rgb(27, 26, 23)');
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).color)).toBe('rgb(242, 240, 234)');
  await expect.poll(() => page.evaluate(() => JSON.parse(localStorage.getItem('luma.state.v1') || '{}').preferences?.width)).toBe(320);
  await page.goto('/?section=appearance');
  await expect(page.getByLabel('面板宽度', { exact: true })).toHaveValue('320');
});
test('native mode fails explicitly instead of falling back to demo', async ({ page }) => {
  await page.goto('/?view=dock&mode=native');
  await expect(page.getByText('未连接原生内核，请在 WebView2 宿主中打开')).toBeVisible();
  await expect(page.getByRole('navigation', { name: '快捷启动面板' })).toHaveCount(0);
});
test('keyboard alternative and narrow viewport', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(page.getByRole('heading', { name: '总览' })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.getByRole('button', { name: '更多项目', exact: true }).click();
  await expect(page.getByRole('region', { name: '更多项目列表' })).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('region', { name: '更多项目列表' })).toHaveCount(0);
});
