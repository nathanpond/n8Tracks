import { AxeBuilder } from '@axe-core/playwright';
import { expect, type Page } from '@playwright/test';
import { setColourScheme } from './shell.ts';

/** WCAG 2.1 levels A and AA, the project's accessibility target. */
const WCAG_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'];

/**
 * Waits until the page is at rest, so colours are measured as they settle and not half-way: every
 * finite animation and transition has finished, and none has started over two checks in a row,
 * each two frames apart. A transition a component starts from script is not running yet when the
 * state it belongs to is first reached: Mantine mounts a tooltip at its start styles (opacity 0)
 * and starts the fade a frame or two later, so a single look at `getAnimations()` can find nothing
 * and let axe run while the fade is under way, measuring text blended with the background (#296).
 * After `MAXIMUM_ROUNDS` rounds the scan goes ahead with whatever is on the page.
 */
async function animationsFinished(page: Page): Promise<void> {
  await page.evaluate(async () => {
    const MAXIMUM_ROUNDS = 100;
    const frame = () =>
      new Promise<void>((resolve) => {
        requestAnimationFrame(() => {
          resolve();
        });
      });
    let quiet = 0;
    for (let round = 0; round < MAXIMUM_ROUNDS && quiet < 2; round++) {
      await frame();
      await frame();
      const running = document
        .getAnimations()
        // An endless animation (the loading indicator) never finishes; it is not a colour change.
        .filter((animation) => animation.effect?.getComputedTiming().endTime !== Infinity);
      if (running.length === 0) {
        quiet++;
      } else {
        quiet = 0;
        await Promise.allSettled(running.map((animation) => animation.finished));
      }
    }
  });
}

/**
 * Scans the page as it is now with axe and fails on any WCAG 2.1 A or AA violation. Call it after
 * every state a test reaches.
 */
export async function expectNoA11yViolations(page: Page): Promise<void> {
  await animationsFinished(page);
  const results = await new AxeBuilder({ page }).withTags(WCAG_TAGS).analyze();
  const violations = results.violations.map((violation) => ({
    rule: violation.id,
    impact: violation.impact,
    help: violation.help,
    elements: violation.nodes.map((node) => node.target.join(' ')),
  }));
  expect(violations, 'accessibility violations (axe, WCAG 2.1 A and AA)').toEqual([]);
}

/**
 * Scans the current state twice: with the colour control set to light, then to dark. The control
 * is left on dark.
 */
export async function expectAccessibleInLightAndDark(page: Page): Promise<void> {
  for (const scheme of ['light', 'dark'] as const) {
    await setColourScheme(page, scheme);
    await expectNoA11yViolations(page);
  }
}

/**
 * Scans an open modal dialog in light and in dark. The dialog is modal, so the colour control
 * behind it cannot be clicked: the scheme is switched the way the control switches it, on the root
 * element, and the scheme the page was in is put back.
 */
export async function expectModalAccessibleInBothSchemes(page: Page): Promise<void> {
  const html = page.locator('html');
  const before = await html.getAttribute('data-mantine-color-scheme');
  for (const scheme of ['light', 'dark'] as const) {
    await html.evaluate((element, value) => {
      element.setAttribute('data-mantine-color-scheme', value);
    }, scheme);
    await expectNoA11yViolations(page);
  }
  await html.evaluate((element, value) => {
    element.setAttribute('data-mantine-color-scheme', value ?? 'dark');
  }, before);
}
