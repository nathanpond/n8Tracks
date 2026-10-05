import { mkdir, writeFile } from 'node:fs/promises';
import { dirname } from 'node:path';
import { expect, type APIRequestContext, type Page } from '@playwright/test';
import { TEST_ADMIN } from './setup.ts';

/** Signing in through the API and the page, the way global setup and the tests get a session. */

export const SESSION_COOKIE = 'n8tracks_session';

/** The anti-forgery header every state-changing browser request carries. */
export const ANTIFORGERY_HEADERS = { 'X-N8Tracks-Request': '1' };

interface StoredCookie {
  name: string;
  value: string;
  domain: string;
  path: string;
  expires: number;
  httpOnly: boolean;
  secure: boolean;
  sameSite: 'Lax';
}

/** Reads the session cookie out of a `Set-Cookie` header, for the host of `appUrl`. */
function sessionCookie(setCookie: string, appUrl: string): StoredCookie {
  const [pair = '', ...attributes] = setCookie.split(';').map((part) => part.trim());
  const [name, value = ''] = pair.split('=', 2);
  if (name !== SESSION_COOKIE || value === '') {
    throw new Error(`Signing in at ${appUrl} set no session cookie (got "${setCookie}").`);
  }

  const attribute = (key: string) =>
    attributes.find((part) => part.toLowerCase().startsWith(`${key}=`))?.slice(key.length + 1);
  const expires = attribute('expires');
  return {
    name,
    value,
    domain: new URL(appUrl).hostname,
    path: attribute('path') ?? '/',
    expires: expires === undefined ? -1 : Math.floor(Date.parse(expires) / 1000),
    httpOnly: attributes.some((part) => part.toLowerCase() === 'httponly'),
    secure: attributes.some((part) => part.toLowerCase() === 'secure'),
    sameSite: 'Lax',
  };
}

/**
 * Signs in to the app at `appUrl` (its page URL, ending in a slash) as the test administrator and
 * writes a Playwright storage state holding the session cookie to `path`: the signed-in fixture
 * the suite's projects start every test from.
 */
export async function writeSignedInState(appUrl: string, path: string): Promise<void> {
  const response = await fetch(new URL('api/v1/session', appUrl), {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Accept: 'application/json',
      ...ANTIFORGERY_HEADERS,
    },
    body: JSON.stringify(TEST_ADMIN),
    signal: AbortSignal.timeout(10_000),
  });
  if (response.status !== 201) {
    throw new Error(
      `Signing in at ${appUrl} failed with status ${String(response.status)}: ${(await response.text()).slice(0, 500)}`,
    );
  }

  const cookie = response.headers
    .getSetCookie()
    .find((header) => header.startsWith(`${SESSION_COOKIE}=`));
  if (cookie === undefined) {
    throw new Error(`Signing in at ${appUrl} set no session cookie.`);
  }

  await mkdir(dirname(path), { recursive: true });
  await writeFile(
    path,
    JSON.stringify({ cookies: [sessionCookie(cookie, appUrl)], origins: [] }, null, 2),
  );
}

/**
 * Signs in through the API with the request context of a page, so the page's browser context
 * holds the session cookie of the app at `appUrl` from then on.
 */
export async function signInThroughApi(request: APIRequestContext, appUrl: string): Promise<void> {
  const response = await request.post(new URL('api/v1/session', appUrl).toString(), {
    headers: ANTIFORGERY_HEADERS,
    data: TEST_ADMIN,
  });
  expect(response.status(), `signing in at ${appUrl}`).toBe(201);
}

/** Fills in and submits the sign-in form on the page. */
export async function signInWithTheForm(
  page: Page,
  username: string,
  password: string,
): Promise<void> {
  const form = page.getByRole('form', { name: 'Sign in' });
  await form.getByRole('textbox', { name: 'Username' }).fill(username);
  await form.getByRole('textbox', { name: 'Password' }).fill(password);
  await form.getByRole('button', { name: 'Sign in' }).click();
}

/** The sign-in page's heading. */
export function signInHeading(page: Page) {
  return page.getByRole('heading', { level: 2, name: 'Sign in' });
}

/** The user menu's button in the header, labelled with the username. */
export function userMenu(page: Page) {
  return page.getByTestId('user-menu');
}
