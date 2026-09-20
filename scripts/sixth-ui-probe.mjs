// Isolated browser visual inspection. Does not use or change the native profile.
import { chromium, expect } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
const stage = process.argv[2] ?? 'after';
const output = path.resolve('docs/evidence/sixth', stage);
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ channel: 'msedge' });
const results = [];
try {
  for (const width of [1100, 390]) {
    const page = await browser.newPage({ viewport: { width, height: 800 } });
    await page.goto('http://127.0.0.1:5173/?section=appearance');
    await page.getByRole('button', { name: '深色模式', exact: true }).click();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
    await page.locator('.toggle-row').first().scrollIntoViewIfNeeded();
    results.push(await page.evaluate(() => {
      const toggle = document.querySelector('.toggle-row'), card = toggle.closest('.cp-card');
      const a = toggle.getBoundingClientRect(), b = card.getBoundingClientRect();
      return { width: innerWidth, leftInset: a.left - b.left, rightInset: b.right - a.right,
        rootScheme: getComputedStyle(document.documentElement).colorScheme,
        scrollbar: getComputedStyle(document.documentElement).scrollbarColor,
        slider: getComputedStyle(document.querySelector('input[type=range]')).backgroundImage,
        horizontalOverflow: document.documentElement.scrollWidth > innerWidth };
    }));
    await page.screenshot({ path: path.join(output, `dark-${width}.png`) });
    await page.getByRole('button', { name: '浅色模式', exact: true }).click();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
    results.push(await page.evaluate(() => ({ width: innerWidth, lightScheme: getComputedStyle(document.documentElement).colorScheme, scrollbar: getComputedStyle(document.documentElement).scrollbarColor })));
    await page.screenshot({ path: path.join(output, `light-${width}.png`) });
    await page.close();
  }
} finally { await browser.close(); }
await writeFile(path.join(output, 'metrics.json'), JSON.stringify(results, null, 2));
console.log(JSON.stringify(results, null, 2));
