import { expect, test, type Page } from '@playwright/test';
import { expectAccessibleInLightAndDark } from '../support/a11y.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

/** What the stand-in relay answers, changed by the test between steps. */
interface StubState {
  /** Whether the relay answers at all (an extension that is not installed does not). */
  present: boolean;
  /** The connection status a `pong` reports. */
  status: string;
  /** Every page message the relay received, by type. */
  received: string[];
}

declare global {
  interface Window {
    n8RelayStub: StubState;
    /** The test's claim, exposed by Playwright: claims the request with the extension's token. */
    n8StubClaim: (requestId: string) => Promise<boolean>;
  }
}

/**
 * A page script standing in for the extension's relay content script: it answers the page's
 * `ping` with the connection the test sets, claims a handed-over request with the extension's own
 * token (through the test, as the service worker would), and answers `open-options`. Nothing in it
 * drives Suno.
 */
function installRelayStub() {
  window.n8RelayStub = { present: true, status: 'connected', received: [] };
  window.addEventListener('message', (event: MessageEvent<Record<string, unknown> | null>) => {
    const data = event.data;
    if (event.source !== window || data?.source !== 'n8tracks' || !window.n8RelayStub.present) {
      return;
    }
    const type = String(data.type);
    window.n8RelayStub.received.push(type);
    const reply = (answer: Record<string, unknown>) => {
      window.postMessage(
        { ...answer, source: 'n8tracks-extension', replyTo: type, id: data.id },
        window.location.origin,
      );
    };
    if (type === 'ping') {
      reply({
        type: 'pong',
        extensionVersion: '0.1.0',
        adapterVersion: '3',
        connection:
          window.n8RelayStub.status === 'connected'
            ? {
                status: 'connected',
                credentialName: 'Stub extension',
                scopes: ['suno.generate'],
                compatible: true,
              }
            : { status: window.n8RelayStub.status },
      });
    } else if (type === 'generate') {
      void window.n8StubClaim(String(data.requestId)).then((claimed) => {
        reply(
          claimed
            ? { type: 'generate-accepted', requestId: data.requestId }
            : { type: 'error', error: 'refused', message: 'The claim was refused.' },
        );
      });
    } else if (type === 'open-options') {
      reply({ type: 'options-opened' });
    }
  });
}

async function appBase(page: Page): Promise<URL> {
  await page.goto('./songs');
  return new URL('.', page.url());
}

/**
 * Generate on Suno's first two Demo steps (#144), with a stand-in for the extension's relay: a
 * request handed to a connected extension (which claims it with its own token), and a disconnected
 * extension named with a way to its options, creating nothing. Step 3 (a source in Suno's Trash)
 * and a real extension are covered by the API and component tests and the owner's manual run.
 */
test.describe('Generate on Suno', () => {
  test('hands a request to a connected extension, and says when it is not connected', async ({
    page,
    playwright,
  }, testInfo) => {
    const stamp = `${String(Date.now()).slice(-7)}${testInfo.project.name}`;
    const base = await appBase(page);
    const credential = await page.request.post(new URL('api/v1/credentials', base).toString(), {
      headers: ANTIFORGERY_HEADERS,
      data: { name: `Generate stub ${stamp}`, kind: 'extension', scopes: ['suno.generate'] },
    });
    expect(credential.status()).toBe(201);
    const { token } = (await credential.json()) as { token: string };
    const extension = await playwright.request.newContext({ storageState: undefined });
    const claimed: string[] = [];

    try {
      await page.exposeFunction('n8StubClaim', async (requestId: string) => {
        const response = await extension.post(
          new URL(`api/v1/suno/generation-requests/${requestId}/claim`, base).toString(),
          { headers: { Authorization: `Bearer ${token}`, Accept: 'application/json' } },
        );
        claimed.push(requestId);
        return response.status() === 200;
      });
      await page.addInitScript(installRelayStub);

      const created = await page.request.post(new URL('api/v1/songs', base).toString(), {
        headers: ANTIFORGERY_HEADERS,
        data: { title: `Generate on Suno ${stamp}` },
      });
      expect(created.status()).toBe(201);
      const song = (await created.json()) as {
        shortcode: string;
        currentVersion: { id: string };
      };

      // 1. A connected extension: the request is made and handed over; the page shows it claimed.
      await page.goto(`./songs/${song.shortcode}`);
      const action = page.getByRole('button', { name: 'Generate on Suno' });
      await expect(action).toBeEnabled();
      await action.click();
      const state = page.getByTestId('generation-request-state');
      await expect(state).toHaveText('Generate on Suno: The extension has the request');
      await expect(action).toBeDisabled();
      expect(claimed).toHaveLength(1);
      const versionRequest = new URL(
        `api/v1/versions/${song.currentVersion.id}/generation-request`,
        base,
      ).toString();
      const current = (await (await page.request.get(versionRequest)).json()) as {
        request: { id: string; state: string; claimed: boolean };
      };
      expect(current.request).toMatchObject({ id: claimed[0], state: 'claimed', claimed: true });
      // Nothing was generated: the Version is still mutable.
      const version = (await (
        await page.request.get(
          new URL(`api/v1/versions/${song.currentVersion.id}`, base).toString(),
        )
      ).json()) as { isFrozen: boolean };
      expect(version.isFrozen).toBe(false);
      await expectAccessibleInLightAndDark(page);

      // The user cancels it, so a new one may be started.
      await page.getByRole('button', { name: 'Cancel the request' }).click();
      await expect(state).toHaveText('Generate on Suno: Cancelled');
      await expect(page.getByTestId('generation-request-message')).toHaveText('You cancelled it.');
      await expect(action).toBeEnabled();

      // 2. The extension disconnected: the page says so, offers its options, and makes nothing.
      await page.evaluate(() => {
        window.n8RelayStub.status = 'not-paired';
      });
      await action.click();
      const problem = page.getByTestId('extension-problem');
      await expect(problem.getByRole('alert')).toHaveText(
        'The n8Tracks extension is not connected',
      );
      await expect(problem).toContainText('no request was made');
      await problem.getByRole('button', { name: 'Open the extension’s options' }).click();
      await expect
        .poll(() => page.evaluate(() => window.n8RelayStub.received))
        .toContain('open-options');
      const after = (await (await page.request.get(versionRequest)).json()) as {
        request: { id: string; state: string };
      };
      expect(after.request).toMatchObject({ id: claimed[0], state: 'cancelled' });
      expect(claimed).toHaveLength(1);
      await expectAccessibleInLightAndDark(page);

      // No extension at all: it did not answer, so it is not installed or not paired here.
      await page.evaluate(() => {
        window.n8RelayStub.present = false;
      });
      await action.click();
      await expect(problem.getByRole('alert')).toHaveText('The n8Tracks extension did not answer');
      await expect(
        problem.getByRole('button', { name: 'Open the extension’s options' }),
      ).toBeHidden();
      await expectAccessibleInLightAndDark(page);
    } finally {
      await extension.dispose();
    }
  });

  /**
   * #146's Demo as n8Tracks sees it: the extension (standing in through the API with its own
   * token) fills Suno's form and reports the verification summary, which the Version page shows;
   * after the user changes Weirdness in Suno and checks again, the page marks it as differing.
   * Filling the form on Suno and the Create click are the owner's, on the live site.
   */
  test('shows the extension’s verification summary of Suno’s form, and its Check again', async ({
    page,
    playwright,
  }, testInfo) => {
    const stamp = `${String(Date.now()).slice(-7)}${testInfo.project.name}`;
    const base = await appBase(page);
    const credential = await page.request.post(new URL('api/v1/credentials', base).toString(), {
      headers: ANTIFORGERY_HEADERS,
      data: { name: `Verify stub ${stamp}`, kind: 'extension', scopes: ['suno.generate'] },
    });
    expect(credential.status()).toBe(201);
    const { token } = (await credential.json()) as { token: string };
    const extension = await playwright.request.newContext({ storageState: undefined });
    const headers = { Authorization: `Bearer ${token}`, Accept: 'application/json' };

    try {
      const created = await page.request.post(new URL('api/v1/songs', base).toString(), {
        headers: ANTIFORGERY_HEADERS,
        data: { title: `Verified on Suno ${stamp}` },
      });
      expect(created.status()).toBe(201);
      const song = (await created.json()) as { shortcode: string; currentVersion: { id: string } };
      const made = await page.request.post(
        new URL(`api/v1/versions/${song.currentVersion.id}/generation-requests`, base).toString(),
        { headers: ANTIFORGERY_HEADERS, data: {} },
      );
      expect(made.status()).toBe(201);
      const { id } = (await made.json()) as { id: string };
      const request = new URL(`api/v1/suno/generation-requests/${id}`, base).toString();
      expect((await extension.post(`${request}/claim`, { headers })).status()).toBe(200);
      const lyrics = { length: 23, sha256: 'a'.repeat(64) };
      const report = (weirdness: number, step: string) =>
        extension.patch(request, {
          headers,
          data: {
            state: 'waiting',
            step,
            verification: {
              adapterVersion: 5,
              mode: 'advanced',
              checkedAt: new Date().toISOString(),
              entries: [
                { key: 'songs.advanced.model', outcome: 'set', expected: 'v6-mini' },
                { key: 'songs.advanced.lyrics', outcome: 'set', expected: lyrics },
                weirdness === 70
                  ? { key: 'songs.advanced.weirdness', outcome: 'set', expected: 70 }
                  : {
                      key: 'songs.advanced.weirdness',
                      outcome: 'failed',
                      expected: 70,
                      found: weirdness,
                    },
                { key: 'songs.advanced.variety', outcome: 'set', expected: 2 },
                {
                  key: 'songs.advanced.duration_mode',
                  outcome: 'manual',
                  expected: 'auto',
                  note: 'Set Duration to Auto by hand: the extension cannot read Suno’s Duration mode yet.',
                },
              ],
            },
          },
        });

      // 2. The form is filled: the page shows each entry as set, and waits for the user's Create.
      expect((await report(70, 'review and create')).status()).toBe(200);
      await page.goto(`./songs/${song.shortcode}`);
      await expect(page.getByTestId('generation-request-state')).toHaveText(
        'Generate on Suno: Waiting for you to click Create in Suno',
      );
      const summary = page.getByTestId('verification');
      await expect(summary.getByTestId('verification-entry')).toHaveText([
        'Model: Set',
        'Lyrics: Set',
        'Weirdness: Set',
        'Variety: Set',
        'Duration: To do by hand — Set Duration to Auto by hand: the extension cannot read Suno’s Duration mode yet.',
      ]);
      await expectAccessibleInLightAndDark(page);

      // 3. Weirdness changed by hand in Suno, and Check again: the page marks it as differing.
      expect((await report(55, 'check form')).status()).toBe(200);
      await expect(summary.locator('[data-key="songs.advanced.weirdness"]')).toHaveText(
        'Weirdness: Differs — expected 70, found 55',
        { timeout: 10_000 },
      );
      await expect(summary.getByTestId('verification-counts')).toHaveText(
        '3 set, 1 differs, 1 to do by hand',
      );
      await expectAccessibleInLightAndDark(page);
    } finally {
      await extension.dispose();
    }
  });
});
