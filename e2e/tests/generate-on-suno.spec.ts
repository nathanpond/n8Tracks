import { randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { expect, test, type Page } from '@playwright/test';
import { expectAccessibleInLightAndDark } from '../support/a11y.ts';
import { solidPng } from '../support/images.ts';
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

/** A TS-003 Create fixture (`generate-v2-web.<name>.json`). */
function createFixture(name: string): Record<string, unknown> {
  return JSON.parse(
    readFileSync(
      new URL(`../../extension/fixtures/suno/generate-v2-web.${name}.json`, import.meta.url),
      'utf8',
    ),
  ) as Record<string, unknown>;
}

/** The Songs › Advanced Create request as the extension forwards it: never the three secrets. */
function forwardedRequest(): Record<string, unknown> {
  const sent = createFixture('songs-advanced.request');
  const metadata = { ...(sent.metadata as Record<string, unknown>) };
  delete metadata.user_tier;
  delete metadata.create_session_token;
  const forwarded: Record<string, unknown> = { ...sent, metadata };
  delete forwarded.token;
  return forwarded;
}

/** The Songs › Advanced Create response with a fresh request ID and clips, Weirdness as given. */
function createResponse(weirdness = 0.7): { id: string; clips: Record<string, unknown>[] } {
  const response = createFixture('songs-advanced.response') as {
    clips: Record<string, unknown>[];
  };
  return {
    ...response,
    id: randomUUID(),
    clips: response.clips.map((clip) => {
      const metadata = clip.metadata as Record<string, unknown>;
      return {
        ...clip,
        id: randomUUID(),
        metadata: {
          ...metadata,
          control_sliders: {
            ...(metadata.control_sliders as Record<string, unknown>),
            weirdness_constraint: weirdness,
          },
        },
      };
    }),
  };
}

async function appBase(page: Page): Promise<URL> {
  await page.goto('./songs');
  return new URL('.', page.url());
}

/** Suno's finished clip (TS-001, `feed-v3.completed-clip`) as `id`, with `status`. */
function finishedClip(id: string, status = 'complete'): Record<string, unknown> {
  const feed = JSON.parse(
    readFileSync(
      new URL(
        '../../extension/fixtures/suno/feed-v3.completed-clip.response.json',
        import.meta.url,
      ),
      'utf8',
    ),
  ) as { clips: Record<string, unknown>[] };
  return { ...feed.clips[0], id, status };
}

/** A Generation an observed Create made, as n8Tracks answers it. */
interface MadeGeneration {
  id: string;
  shortcode: string;
  sunoId: string;
}

/** The two Generations an observed Create made. */
function twoGenerations(made: MadeGeneration[] | undefined): [MadeGeneration, MadeGeneration] {
  const [first, second] = made ?? [];
  if (first === undefined || second === undefined) {
    throw new Error('The observed Create made no two Generations.');
  }
  return [first, second];
}

/** Opens a Version's Generations in the Versions table unless they are open already. */
async function openGenerationsOf(page: Page, number: string): Promise<void> {
  const chevron = page
    .getByRole('region', { name: 'Versions and Generations' })
    .getByRole('button', { name: `Generations of Version ${number}` });
  await expect(chevron).toBeVisible();
  if ((await chevron.getAttribute('aria-expanded')) !== 'true') {
    await chevron.click();
  }
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
      // Exact: the Song's own Play control (#219) is named "Play <title>", and this title has
      // "Generate on Suno" in it.
      const action = page.getByRole('button', { name: 'Generate on Suno', exact: true });
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
   * Filling the form on Suno and the Create click are the owner's, on the live site. The Song is
   * titled with a word of the extension's own Duration note, which n8Tracks once refused (#379).
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
        data: { title: 'Duration' },
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

  /**
   * #149's Demo as n8Tracks sees it: the user's own Create click in Suno, which the extension
   * (standing in through the API with its own token, sending the TS-003 fixtures as the page
   * observer passes them on) reports. The Version gets both clips as Generating and freezes; a
   * second Create after Weirdness was changed by hand makes a new child Version. The Create clicks on
   * the live site are the owner's.
   */
  test('records the user’s Create on its Version, and a changed form as a new Version', async ({
    page,
    playwright,
  }, testInfo) => {
    const stamp = `${String(Date.now()).slice(-7)}${testInfo.project.name}`;
    const base = await appBase(page);
    const credential = await page.request.post(new URL('api/v1/credentials', base).toString(), {
      headers: ANTIFORGERY_HEADERS,
      data: { name: `Create stub ${stamp}`, kind: 'extension', scopes: ['suno.generate'] },
    });
    expect(credential.status()).toBe(201);
    const { token } = (await credential.json()) as { token: string };
    const extension = await playwright.request.newContext({ storageState: undefined });
    const headers = { Authorization: `Bearer ${token}`, Accept: 'application/json' };

    try {
      const created = await page.request.post(new URL('api/v1/songs', base).toString(), {
        headers: ANTIFORGERY_HEADERS,
        data: { title: `Observed Create ${stamp}` },
      });
      expect(created.status()).toBe(201);
      const song = (await created.json()) as { shortcode: string; currentVersion: { id: string } };
      const versionPath = new URL(`api/v1/versions/${song.currentVersion.id}`, base).toString();

      // Version 1 holds what the fixture's form submitted (the extension filled it from the Version).
      const response = createResponse();
      const clip = response.clips[0] as { metadata: Record<string, unknown> };
      const submitted = forwardedRequest();
      const edited = await page.request.patch(versionPath, {
        headers: { ...ANTIFORGERY_HEADERS, 'If-Match': '"1"' },
        data: {
          lyrics: clip.metadata.prompt,
          styles: clip.metadata.tags,
          inputs: {
            songMode: 'advanced',
            excludeStyles: clip.metadata.negative_tags,
            vocalGender: 'female',
            durationMode: 'custom',
            durationSeconds: 30,
            maxMode: true,
            weirdness: 70,
            styleInfluence: 30,
            variety: 'high',
            personalize: true,
          },
        },
      });
      expect(edited.status(), await edited.text()).toBe(200);

      const made = await page.request.post(`${versionPath}/generation-requests`, {
        headers: ANTIFORGERY_HEADERS,
        data: {},
      });
      expect(made.status()).toBe(201);
      const { id } = (await made.json()) as { id: string };
      const request = new URL(`api/v1/suno/generation-requests/${id}`, base).toString();
      expect((await extension.post(`${request}/claim`, { headers })).status()).toBe(200);
      expect(
        (
          await extension.patch(request, {
            headers,
            data: { state: 'waiting', step: 'review and create' },
          })
        ).status(),
      ).toBe(200);
      // The requested Version's own page: a new current Version made later does not move it.
      await page.goto(`./songs/${song.shortcode}/v/1`);
      await expect(page.getByTestId('generation-request-state')).toHaveText(
        'Generate on Suno: Waiting for you to click Create in Suno',
      );

      // 1. The user clicks Create in Suno: within seconds both clips are on Version 1, Generating, and it is frozen.
      const first = await extension.post(`${request}/observed-create`, {
        headers,
        data: { response, request: submitted },
      });
      expect(first.status(), await first.text()).toBe(200);
      const observed = page.getByTestId('observed-create');
      await expect(observed).toHaveCount(1, { timeout: 10_000 });
      await expect(observed.first()).toHaveAttribute('data-outcome', 'attached');
      await expect(observed.first()).toContainText(
        '2 Generations recorded on this request’s Version 1, which is now frozen.',
      );
      await expect(page.getByTestId('frozen-notice')).toBeVisible();
      const row = page.locator('tr[data-version-row="1"]');
      await expect(row.getByTestId('generation-count')).toHaveText('2');
      const generations = (await (
        await page.request.get(
          new URL(`api/v1/songs/${song.shortcode}/generations`, base).toString(),
        )
      ).json()) as { items: { providerStatus: string }[] };
      expect(generations.items.map((item) => item.providerStatus)).toEqual([
        'submitted',
        'submitted',
      ]);
      await expectAccessibleInLightAndDark(page);

      // 2. Weirdness changed by hand in Suno before the next Create: a new child Version holds it.
      const second = await extension.post(`${request}/observed-create`, {
        headers,
        data: { response: createResponse(0.4), request: submitted },
      });
      expect(second.status(), await second.text()).toBe(200);
      await expect(observed).toHaveCount(2, { timeout: 10_000 });
      const branched = observed.nth(1);
      await expect(branched).toHaveAttribute('data-outcome', 'branched');
      await expect(branched.getByTestId('observed-differing')).toHaveText(
        'Options that differed: Weirdness.',
      );
      await expect(branched.getByTestId('observed-assumed')).toContainText('Model');
      await expect(
        page.locator('tr[data-version-row="1.1"]').getByTestId('generation-count'),
      ).toHaveText('2');
      await expectAccessibleInLightAndDark(page);

      await branched.getByRole('link', { name: 'Open Version 1.1' }).click();
      await expect(page.getByRole('heading', { name: 'Version 1.1' })).toBeVisible();
      await expect(page.getByRole('textbox', { name: 'Notes' })).toHaveValue(
        'Created from what was submitted to Suno',
      );
      const current = (await (
        await page.request.get(new URL(`api/v1/songs/${song.shortcode}`, base).toString())
      ).json()) as { currentVersion: { id: string } };
      const child = (await (
        await page.request.get(
          new URL(`api/v1/versions/${current.currentVersion.id}`, base).toString(),
        )
      ).json()) as { number: string; isFrozen: boolean; inputs: { weirdness: number } };
      expect(child).toMatchObject({ number: '1.1', isFrozen: true, inputs: { weirdness: 40 } });
    } finally {
      await extension.dispose();
    }
  });

  test('fills in the Generations when Suno finishes them, and shows a clip that ended in error as Failed', async ({
    page,
    playwright,
  }, testInfo) => {
    const stamp = `${String(Date.now()).slice(-7)}${testInfo.project.name}`;
    const base = await appBase(page);
    const credential = await page.request.post(new URL('api/v1/credentials', base).toString(), {
      headers: ANTIFORGERY_HEADERS,
      data: { name: `Completion stub ${stamp}`, kind: 'extension', scopes: ['suno.generate'] },
    });
    expect(credential.status()).toBe(201);
    const { token } = (await credential.json()) as { token: string };
    const extension = await playwright.request.newContext({ storageState: undefined });
    const headers = { Authorization: `Bearer ${token}`, Accept: 'application/json' };

    try {
      const created = await page.request.post(new URL('api/v1/songs', base).toString(), {
        headers: ANTIFORGERY_HEADERS,
        data: { title: `Completed Create ${stamp}` },
      });
      expect(created.status()).toBe(201);
      const song = (await created.json()) as { shortcode: string; currentVersion: { id: string } };
      const versionPath = new URL(`api/v1/versions/${song.currentVersion.id}`, base).toString();
      const made = await page.request.post(`${versionPath}/generation-requests`, {
        headers: ANTIFORGERY_HEADERS,
        data: {},
      });
      expect(made.status()).toBe(201);
      const { id } = (await made.json()) as { id: string };
      const request = new URL(`api/v1/suno/generation-requests/${id}`, base).toString();
      expect((await extension.post(`${request}/claim`, { headers })).status()).toBe(200);
      expect(
        (
          await extension.patch(request, {
            headers,
            data: { state: 'waiting', step: 'review and create' },
          })
        ).status(),
      ).toBe(200);

      // 1. (The owner's step: Create clicked in Suno.) The observed Create's two Generations are Generating.
      const response = createResponse();
      const recorded = await extension.post(`${request}/observed-create`, {
        headers,
        data: { response, request: forwardedRequest() },
      });
      expect(recorded.status(), await recorded.text()).toBe(200);
      const { observed } = (await recorded.json()) as {
        observed: {
          version: { number: string };
          generations: { id: string; shortcode: string; sunoId: string }[];
        }[];
      };
      const [made1, made2] = twoGenerations(observed[0]?.generations);
      await page.goto(`./songs/${song.shortcode}`);
      await openGenerationsOf(page, observed[0]?.version.number ?? '1');
      const first = page.locator(`tr[data-generation="${made1.shortcode}"]`);
      const second = page.locator(`tr[data-generation="${made2.shortcode}"]`);
      await expect(first.getByTestId('generation-duration')).toHaveText('Generating');

      // 2. Suno finishes them: the extension reports the finished clips, and the first one's cover.
      const completed = await extension.post(`${request}/clips`, {
        headers,
        data: { clip: finishedClip(made1.sunoId) },
      });
      expect(completed.status(), await completed.text()).toBe(200);
      expect(await completed.json()).toMatchObject({
        outcome: 'completed',
        generation: { id: made1.id, shortcode: made1.shortcode, providerStatus: 'complete' },
      });
      const cover = await extension.put(
        new URL(`api/v1/generations/${made1.id}/artwork`, base).toString(),
        {
          headers,
          multipart: {
            file: {
              name: 'cover.png',
              mimeType: 'image/png',
              buffer: solidPng(64, 64, `${stamp} cover`, [200, 120, 40]),
            },
          },
        },
      );
      expect(cover.status(), await cover.text()).toBe(200);
      const failed = await extension.post(`${request}/clips`, {
        headers,
        data: { clip: finishedClip(made2.sunoId, 'error') },
      });
      expect(failed.status(), await failed.text()).toBe(200);
      expect(await failed.json()).toMatchObject({ outcome: 'failed' });

      // Without reloading, the page reads the Generations again while one is Generating.
      await expect(first.getByTestId('generation-duration')).toHaveText('2:24', {
        timeout: 15_000,
      });
      await expect(
        first.getByRole('img', { name: `Artwork for ${made1.shortcode}` }),
      ).toBeVisible();
      await expect(second.getByTestId('generation-failed')).toHaveText('Failed');
      await expectAccessibleInLightAndDark(page);

      // A second report changes nothing: anything later is the import review's.
      const again = await extension.post(`${request}/clips`, {
        headers,
        data: { clip: { ...finishedClip(made1.sunoId), title: 'Changed later' } },
      });
      expect(again.status()).toBe(409);
      expect(await again.json()).toMatchObject({ code: 'already_complete' });
    } finally {
      await extension.dispose();
    }
  });
});
