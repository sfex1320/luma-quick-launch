import { test, expect } from '@playwright/test';

test('search is an independent page with scopes and keyboard selection', async ({ page }) => {
  await page.goto('/?view=search');
  const input = page.getByRole('combobox');
  await expect(input).toBeFocused();
  await input.fill('设计');
  await expect(page.getByRole('option').first()).toBeVisible();
  await input.press('ArrowDown');
  await expect(page.getByRole('option').nth(1)).toHaveAttribute('aria-selected', 'true');
  await input.press('Enter');
  await expect(page.getByRole('status')).toContainText('未访问真实文件');
  await page.getByRole('button', { name: '文件内容', exact: true }).click();
  await expect(page.getByRole('status')).toContainText('浏览器仅预览');
  await expect(page.getByRole('navigation', { name: '驾驶舱导航' })).toHaveCount(0);
});

test('generic shortcut editor supports apps and files; system entry is unique', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('button', { name: '设置与备份', exact: true })).toHaveCount(1);
  await expect(page.getByText('内核状态', { exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: '新建项目', exact: true }).click();
  await page.getByLabel('项目名称', { exact: true }).fill('编辑器');
  await page.getByLabel('入口 1 类型').selectOption('app');
  await page.getByLabel('入口 1 名称').fill('Code');
  await page.getByLabel('入口 1 路径').fill('C:\\Apps\\Code.exe');
  await page.getByRole('button', { name: '创建项目', exact: true }).click();
  await expect.poll(() => page.evaluate(() => JSON.parse(localStorage.getItem('luma.state.v1') || '{}').projects?.at(-1)?.items[0].kind)).toBe('app');
});
