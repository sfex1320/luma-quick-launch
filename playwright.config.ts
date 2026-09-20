import { defineConfig } from '@playwright/test';
export default defineConfig({ testDir: './tests', timeout: 30000, use: { baseURL: 'http://127.0.0.1:5173', channel: 'msedge', viewport: { width: 1440, height: 1100 }, trace: 'retain-on-failure' }, webServer: { command: 'npm run dev', url: 'http://127.0.0.1:5173', reuseExistingServer: true }, workers: 1 });
