import { expect, test } from '@playwright/test';
import { openShell } from '../support/shell.ts';

// Bite proof for #42 only: fails on the first attempt and passes on the retry.
test('bite proof: passes only on the retry', async ({ page }, testInfo) => {
  await openShell(page);
  expect(testInfo.retry).toBe(1);
});
