import { defineConfig } from '@playwright/test';

/**
 * The e2e suite (#288): the most important flows in a real browser against a local stack that scripts/e2e.sh starts:
 * a throwaway Postgres loaded by the data generator, the API trusting a stand-in OpenID provider (e2e/idp.mjs), and
 * the web app on the dev server. Run it with `scripts/e2e.sh`.
 */
export default defineConfig({
  testDir: '.',
  timeout: 60_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list']],
  use: {
    baseURL: process.env.E2E_BASE_URL ?? 'http://localhost:4310',
    viewport: { width: 1440, height: 900 },
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  outputDir: '../../../artifacts/e2e',
});
