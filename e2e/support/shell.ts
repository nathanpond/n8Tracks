import { expect, type Locator, type Page, type Route } from '@playwright/test';

export type ColourChoice = 'light' | 'dark' | 'auto';

const choiceLabels: Record<ColourChoice, string> = { light: 'Light', dark: 'Dark', auto: 'Auto' };

/** The colour control in the header. */
export function colourControl(page: Page): Locator {
  return page.getByRole('radiogroup', { name: 'Colour scheme' });
}

/** One option of the colour control. */
export function colourOption(page: Page, choice: ColourChoice): Locator {
  return colourControl(page).getByRole('radio', { name: choiceLabels[choice], exact: true });
}

/**
 * Asserts the scheme the page is drawn in. It is the choice itself for light and dark, and the
 * system's scheme for auto.
 */
export async function expectAppliedScheme(page: Page, scheme: 'light' | 'dark'): Promise<void> {
  await expect(page.locator('html')).toHaveAttribute('data-mantine-color-scheme', scheme);
}

/** Chooses light or dark in the colour control and waits until the page is drawn in it. */
export async function setColourScheme(page: Page, scheme: 'light' | 'dark'): Promise<void> {
  await chooseColour(page, scheme);
  await expectAppliedScheme(page, scheme);
}

/** Chooses an option of the colour control the way a user does: by clicking its label. */
export async function chooseColour(page: Page, choice: ColourChoice): Promise<void> {
  // The radio inputs are visually hidden under their labels, so the label is what gets the click.
  await colourControl(page).getByText(choiceLabels[choice], { exact: true }).click();
  await expect(colourOption(page, choice)).toBeChecked();
}

/**
 * Opens Settings → System, the page that shows the version and health (it replaced the M0 shell
 * page), on the project's container (root or sub-path, from the base URL).
 */
export async function openShell(page: Page): Promise<void> {
  await page.goto('./settings/system');
}

/**
 * Intercepts the page's health request, wherever the app is served from. Other requests, the
 * page and its assets, are untouched.
 */
export async function interceptHealth(
  page: Page,
  handler: (route: Route) => Promise<void>,
): Promise<void> {
  await page.route((url) => url.pathname.endsWith('/health'), handler);
}

/** The overall status badge. */
export function overallStatus(page: Page): Locator {
  return page.getByTestId('overall-status');
}

/** The rows of the components table, without the header row. */
export function componentRows(page: Page): Locator {
  return page.getByRole('table', { name: 'Components' }).locator('tbody tr');
}
