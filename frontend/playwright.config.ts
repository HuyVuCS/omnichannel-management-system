import { defineConfig, devices } from '@playwright/test';

/**
 * End-to-end suite (task 027 Part C): the built frontend in a real Chromium against a running stack. Configuration from
 * the environment only (`e2e/support/env.ts`). One worker, specs in file order: the realtime spec reads dashboard
 * figures that another spec's orders would change, and the login limit is per address.
 *
 * Nothing that holds a token or a password is kept: no trace (it records request headers - the bearer token - and has
 * no way to leave them out), no video, no storage state; a screenshot only when a test fails. Passwords are typed
 * without a `fill` step, whose title would carry the text (`typeSecretly`).
 */
export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  workers: 1,
  forbidOnly: Boolean(process.env.CI),
  retries: 0,
  timeout: 180_000,
  expect: { timeout: 15_000 },
  reporter: [['list'], ['html', { open: 'never', outputFolder: 'playwright-report' }]],
  outputDir: 'test-results',
  globalSetup: './e2e/support/global-setup.ts',
  globalTeardown: './e2e/support/global-teardown.ts',
  use: {
    trace: 'off',
    video: 'off',
    screenshot: 'only-on-failure',
    // axe-core is injected as a script element; the production build's CSP allows scripts from its own origin only.
    bypassCSP: true,
    viewport: { width: 1280, height: 800 },
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 800 } } }],
});
