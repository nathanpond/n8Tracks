/** First-run setup through the API, the way global setup gets every shared container past it. */

/** The administrator every shared container is set up with. Fixed test values, not a real credential. */
export const TEST_ADMIN = {
  username: 'e2e-owner',
  password: 'e2e-test-password',
};

/**
 * Completes setup of the app at `appUrl` (its page URL, ending in a slash) with the test
 * administrator. A fresh container answers 201; one that is already set up answers 409
 * `setup_already_complete`, which is fine too. Anything else fails.
 */
export async function completeSetup(appUrl: string): Promise<void> {
  const response = await fetch(new URL('api/v1/setup', appUrl), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
    body: JSON.stringify({
      username: TEST_ADMIN.username,
      password: TEST_ADMIN.password,
      passwordConfirmation: TEST_ADMIN.password,
    }),
    signal: AbortSignal.timeout(10_000),
  });
  if (response.status === 201) {
    return;
  }

  const body = await response.text();
  if (response.status === 409 && body.includes('"setup_already_complete"')) {
    return;
  }
  throw new Error(
    `Setup of ${appUrl} failed with status ${String(response.status)}: ${body.slice(0, 500)}`,
  );
}

/** Reads `complete` from the setup status of the app at `appUrl`. */
export async function isSetupComplete(appUrl: string): Promise<boolean> {
  const response = await fetch(new URL('api/v1/setup/status', appUrl), {
    signal: AbortSignal.timeout(10_000),
  });
  const body: unknown = await response.json();
  return typeof body === 'object' && body !== null && 'complete' in body && body.complete === true;
}
