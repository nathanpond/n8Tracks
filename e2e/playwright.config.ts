import { defineConfig, devices } from '@playwright/test';
import {
  OUTPUT_DIR,
  ROOT_STORAGE_STATE,
  ROOT_URL,
  SUB_PATH_STORAGE_STATE,
  SUB_PATH_URL,
} from './support/targets.ts';

/** GitHub Actions sets `CI`. */
const CI = Boolean(process.env.CI);

/**
 * Chromium only, against containers that global setup starts from the application image: once at
 * the root of a hostname and once under a sub-path. The containers' names and ports, the signed-in
 * states, and (locally) the output folder are this run's own, so two runs can share a machine. A test tagged `@root-only` or `@subpath-only`
 * runs in that project alone. Each project starts every test signed in to its container (the
 * storage state global setup saved); a test that needs to be signed out says so.
 *
 * In CI a failing test is retried once, and the retry records a trace. A test that passes on the
 * retry is flaky: the run stays green, and `scripts/summarize.ts` lists it from the JSON report.
 * CI also stops after ten failed tests: a page broken for every test would otherwise spend a
 * minute on each (two attempts, each waiting out its timeout) and run into the job's time limit
 * before the report is written.
 */
export default defineConfig({
  testDir: './tests',
  globalSetup: './global-setup.ts',
  outputDir: OUTPUT_DIR,
  fullyParallel: false,
  workers: 1,
  retries: CI ? 1 : 0,
  maxFailures: CI ? 10 : 0,
  forbidOnly: true,
  reporter: CI
    ? [
        ['list'],
        ['html', { open: 'never', outputFolder: 'playwright-report' }],
        ['json', { outputFile: 'test-results/results.json' }],
      ]
    : [['list']],
  use: {
    ...devices['Desktop Chrome'],
    trace: CI ? 'on-first-retry' : 'retain-on-failure',
  },
  projects: [
    {
      name: 'root',
      use: { baseURL: ROOT_URL, storageState: ROOT_STORAGE_STATE },
      grepInvert: /@subpath-only/,
    },
    {
      name: 'subpath',
      use: { baseURL: SUB_PATH_URL, storageState: SUB_PATH_STORAGE_STATE },
      grepInvert: /@root-only/,
    },
  ],
});
