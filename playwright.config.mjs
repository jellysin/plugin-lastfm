import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './tests/web', testMatch: '**/*.spec.mjs', fullyParallel: true,
  timeout: 30_000, expect: { timeout: 5000 }, forbidOnly: Boolean(process.env.CI),
  retries: 0, workers: process.env.CI ? 2 : 4,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: { browserName: 'chromium', viewport: { width: 1280, height: 900 }, screenshot: 'only-on-failure', trace: 'retain-on-failure' },
});
