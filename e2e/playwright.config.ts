import { defineConfig, devices } from '@playwright/test';

/**
 * The suite runs against the real API and the real SQL database, not mocks. The point of these
 * tests is to catch things that only go wrong once a browser, a websocket and a database are all
 * in play: a claim window that never closes, a reconnect that lands in the wrong seat, a hand
 * that renders off the bottom of a phone.
 *
 * Both servers must already be running. Start them with run.ps1 from the repo root, or by hand:
 *   server:  dotnet run --project src/Mahjong.Api --urls http://0.0.0.0:5080
 *   web:     npx ng serve --host 0.0.0.0 --port 4200 --allowed-hosts
 */
export default defineConfig({
  testDir: './specs',
  outputDir: './test-results',
  globalSetup: './global-setup.ts',
  timeout: 90_000,
  expect: { timeout: 15_000 },

  // Several specs drive four browser contexts against one table, and the ticker moves bots on a
  // shared clock. Running files in parallel makes failures hard to attribute for very little
  // wall-clock gain on a suite this size.
  fullyParallel: false,
  workers: 1,

  reporter: [['list'], ['html', { outputFolder: './playwright-report', open: 'never' }]],

  use: {
    baseURL: process.env.WEB_URL ?? 'http://localhost:4200',
    trace: 'retain-on-failure',
    video: 'off',
    screenshot: 'only-on-failure',
    actionTimeout: 15_000,
  },

  projects: [
    {
      name: 'desktop',
      use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } },
    },
    {
      name: 'tablet',
      use: { ...devices['Desktop Chrome'], viewport: { width: 768, height: 1024 }, isMobile: false },
      testMatch: /(responsive|hand-layout|claim-timer)\.spec\.ts/,
    },
    {
      name: 'phone',
      use: { ...devices['Pixel 7'] },
      testMatch: /(responsive|hand-layout|claim-timer)\.spec\.ts/,
    },
  ],
});
