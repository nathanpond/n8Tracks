import { defineConfig, devices } from '@playwright/test';
import { ROOT_URL, SUB_PATH_URL } from './support/targets.ts';

/**
 * Chromium only, against containers that global setup starts from the application image: once at
 * the root of a hostname and once under a sub-path. A test tagged `@root-only` or `@subpath-only`
 * runs in that project alone. These are the local settings; CI adds its own.
 */
export default defineConfig({
  testDir: './tests',
  globalSetup: './global-setup.ts',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  forbidOnly: true,
  reporter: [['list']],
  use: {
    ...devices['Desktop Chrome'],
    trace: 'retain-on-failure',
  },
  projects: [
    {
      name: 'root',
      use: { baseURL: ROOT_URL },
      grepInvert: /@subpath-only/,
    },
    {
      name: 'subpath',
      use: { baseURL: SUB_PATH_URL },
      grepInvert: /@root-only/,
    },
  ],
});
