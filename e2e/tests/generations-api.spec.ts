import { randomUUID } from 'node:crypto';
import { expect, test, type APIRequestContext } from '@playwright/test';
import { seedGeneration } from '../support/seeding.ts';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

/** The fields of a Generation this walk reads. */
interface Generation {
  id: string;
  shortcode: string;
  ordinal: number;
  song: { id: string; shortcode: string };
  version: { id: string; shortcode: string };
  sunoId: string | null;
  sunoUrl: string | null;
  providerStatus: string | null;
  state: string;
  remoteState: string;
  title: string | null;
  durationSeconds: number | null;
  isSelected: boolean;
  revision: number;
}

interface Song {
  id: string;
  shortcode: string;
  currentVersion: { id: string; shortcode: string };
}

async function json<T>(response: Awaited<ReturnType<APIRequestContext['get']>>): Promise<T> {
  expect(response.ok(), `${response.url()}: ${String(response.status())}`).toBe(true);
  return (await response.json()) as T;
}

/**
 * Generations through the API on the built image (#117; the story's Demo is agent-verifiable, so
 * this walks the API): Generations seeded with and without a Suno clip, listed by Version and by
 * Song, read by shortcode, and the raw clip read back unchanged through the session-only provider
 * record. The container is shared, so the walk uses the shortcodes and Suno IDs it makes.
 */
test.describe('Generations API', () => {
  test('lists and reads seeded Generations and answers the raw clip as stored', async ({
    page,
  }, testInfo) => {
    await page.goto('./songs');
    const base = new URL('.', page.url());
    const api = (path: string) => new URL(`api/v1/${path}`, base).toString();
    const request = page.request;

    const song = await json<Song>(
      await request.post(api('songs'), {
        headers: ANTIFORGERY_HEADERS,
        data: { title: `Generated ${randomUUID()}` },
      }),
    );
    const version = song.currentVersion.shortcode;

    // A clip written as Suno would send it, whitespace and an unknown field included.
    const sunoId = randomUUID();
    const clip = `{\n  "id": "${sunoId}", "status": "complete",\n  "title": "Seeded clip", "metadata": {"duration": 61.5, "tags": "lo-fi"},\n  "novel_field": [1, 2]\n}`;
    const withClip = await seedGeneration(testInfo, version, clip);
    const withoutClip = await seedGeneration(testInfo, version);
    expect(withClip).toBe(`${version}-g1`);
    expect(withoutClip).toBe(`${version}-g2`);

    const byVersion = await json<{ items: Generation[] }>(
      await request.get(api(`versions/${version}/generations`)),
    );
    expect(byVersion.items.map((generation) => generation.shortcode)).toEqual([
      withClip,
      withoutClip,
    ]);
    const bySong = await json<{ items: Generation[] }>(
      await request.get(api(`songs/${song.shortcode}/generations`)),
    );
    expect(bySong.items.map((generation) => generation.shortcode)).toEqual([withClip, withoutClip]);

    const read = await json<Generation>(await request.get(api(`generations/${withClip}`)));
    expect(read).toMatchObject({
      shortcode: withClip,
      ordinal: 1,
      song: { id: song.id, shortcode: song.shortcode },
      version: { id: song.currentVersion.id, shortcode: version },
      sunoId,
      sunoUrl: `https://suno.com/song/${sunoId}`,
      providerStatus: 'complete',
      state: 'active',
      remoteState: 'present',
      title: 'Seeded clip',
      durationSeconds: 61.5,
      isSelected: false,
      revision: 1,
    });
    expect(JSON.stringify(read)).not.toContain('novel_field');

    // The provider record is the clip exactly as seeded; a Generation without Suno data has none.
    const record = await request.get(api(`generations/${withClip}/provider-record`));
    expect(record.status()).toBe(200);
    expect(record.headers()['content-type']).toContain('application/json');
    expect(await record.text()).toBe(clip);
    const none = await request.get(api(`generations/${withoutClip}/provider-record`));
    expect(none.status()).toBe(404);
    expect(((await none.json()) as { code: string }).code).toBe('no_provider_record');

    // The Version is frozen by its first Generation.
    const frozen = await json<{ isFrozen: boolean }>(await request.get(api(`versions/${version}`)));
    expect(frozen.isFrozen).toBe(true);
  });
});
