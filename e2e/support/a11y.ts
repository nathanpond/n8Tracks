import { AxeBuilder } from '@axe-core/playwright';
import { expect, type Page } from '@playwright/test';
import { setColourScheme } from './shell.ts';

/** WCAG 2.1 levels A and AA, the project's accessibility target. */
const WCAG_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'];

/** Lets running transitions finish, so colours are measured at rest and not half-way. */
async function animationsFinished(page: Page): Promise<void> {
  await page.evaluate(async () => {
    await Promise.allSettled(
      document
        .getAnimations()
        // An endless animation (the loading indicator) never finishes; it is not a colour change.
        .filter((animation) => animation.effect?.getComputedTiming().endTime !== Infinity)
        .map((animation) => animation.finished),
    );
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
