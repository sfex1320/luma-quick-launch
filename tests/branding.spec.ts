import { test, expect } from '@playwright/test';

test('settings and favicon load the same graphical product identity', async ({ page }) => {
  await page.goto('/');
  const logo = page.locator('.cp-brand .luma-brand-image');
  await expect(logo).toBeVisible();
  await expect.poll(() => logo.evaluate((image: HTMLImageElement) => image.naturalWidth)).toBe(256);
  await expect(page.locator('.cp-brand-mark')).toHaveCount(0);
  const iconPath = await page.locator('link[rel="icon"]').getAttribute('href');
  const response = await page.request.get(iconPath!);
  expect(response.ok()).toBeTruthy();
  const bytes = await response.body();
  expect(bytes.readUInt16LE(2)).toBe(1);
  expect(bytes.readUInt16LE(4)).toBe(9);
});
