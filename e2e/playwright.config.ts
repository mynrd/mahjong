import { defineConfig, devices } from '@playwright/test';

/**
 * The suite runs against the real API and the real SQL database, not mocks. The point of these
 * tests is to catch things that only go wrong once a browser, a websocket and a database are all
 * in play: a claim window that never closes, a reconnect that lands in the wrong seat, a hand
 * that renders off the bottom of a phone.
 *
 * The server must already be running, with the web app built into its wwwroot. Start it with
 * run.ps1 from the repo root, or by hand:
 *   cd web; npx ng build
 *   dotnet run --project server/src/Mahjong.Api --urls http://0.0.0.0:5080
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
    baseURL: process.env.WEB_URL ?? 'http://localhost:5080',
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
