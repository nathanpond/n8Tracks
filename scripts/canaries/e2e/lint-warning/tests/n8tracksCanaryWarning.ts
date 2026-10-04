// A canary for scripts/check-canaries.sh; never part of the product. It breaks only a rule set
// to "warn", so linting fails on it only while warnings fail the lint (--max-warnings 0).
import type { Page } from '@playwright/test';

export async function canaryWarning(page: Page): Promise<void> {
  await page.waitForTimeout(1); // canary: warning playwright/no-wait-for-timeout
}
