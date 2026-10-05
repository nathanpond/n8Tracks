import { expect, test, type APIRequestContext } from '@playwright/test';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

/** The fields of a Song the API answers with that this walk reads. */
interface Song {
  id: string;
  shortcode: string;
  title: string;
  concept: string | null;
  state: { id: string; name: string; colour: string };
  currentVersion: { id: string; number: string; shortcode: string };
  versionCount: number;
  revision: number;
}

interface SongList {
  items: Song[];
  page: number;
  pageSize: number;
  total: number;
}

interface WorkflowStateList {
  items: { id: string; name: string; colour: string; order: number; hidden: boolean }[];
}

const SHORTCODE = /^n8-[1-9][0-9]*$/;

/** The item at `index`, which the walk needs to exist. */
function at<T>(items: T[], index: number): T {
  const item = items[index];
  if (item === undefined) throw new Error(`No item at ${String(index)}.`);
  return item;
}

async function json<T>(response: Awaited<ReturnType<APIRequestContext['get']>>): Promise<T> {
  expect(response.ok(), `${response.url()}: ${String(response.status())}`).toBe(true);
  return (await response.json()) as T;
}

/**
 * Walks the Demo's data through the API on the project's shared container, signed in: the Songs
 * screen itself is #59's. The container is shared with other specs and retries, so the walk reads
 * the shortcodes it is given instead of expecting `n8-1`.
 */
test.describe('Songs API', () => {
  test('creates Songs with Version 1 and lists, filters, and sorts them', async ({ page }) => {
    await page.goto('./songs');
    // The app's base (the root or the sub-path), as the page itself resolves it.
    const base = new URL('.', page.url());
    const api = (path: string) => new URL(`api/v1/${path}`, base).toString();
    const request = page.request;

    // The seven states, in order; a new Song starts in the first.
    const states = await json<WorkflowStateList>(await request.get(api('workflow-states')));
    expect(states.items.map((state) => state.name)).toEqual([
      'Idea',
      'Writing',
      'Generating',
      'Refining',
      'Final',
      'Released',
      'Archived',
    ]);
    const idea = at(states.items, 0);

    // A title unique to this attempt, so the filter below finds only this walk's Songs.
    const title = `Running in a Pack ${String(Date.now())}`;
    const create = (body: object) =>
      request.post(api('songs'), { data: body, headers: ANTIFORGERY_HEADERS });

    // A blank title is refused with a field error.
    const refused = await create({ title: '   ' });
    expect(refused.status()).toBe(422);
    const problem = (await refused.json()) as { code: string; errors: Record<string, string[]> };
    expect(problem.code).toBe('validation_failed');
    expect(Object.keys(problem.errors)).toEqual(['title']);

    // The first Song: a shortcode, Idea, and Version 1 as its current Version.
    const createdResponse = await create({
      title,
      concept: 'Fast-paced song about running in a pack.',
    });
    expect(createdResponse.status()).toBe(201);
    const first = (await createdResponse.json()) as Song;
    expect(first.shortcode).toMatch(SHORTCODE);
    expect(first.title).toBe(title);
    expect(first.state.name).toBe('Idea');
    expect(first.currentVersion.number).toBe('1');
    expect(first.currentVersion.shortcode).toBe(`${first.shortcode}-v1`);
    expect(first.versionCount).toBe(1);

    // It opens by its shortcode, in any case.
    const opened = await json<Song>(
      await request.get(api(`songs/${first.shortcode.toUpperCase()}`)),
    );
    expect(opened.id).toBe(first.id);

    // Two more with the same title: three Songs, three shortcodes, in creation order.
    const more: Song[] = [];
    for (let count = 0; count < 2; count++) {
      const response = await create({ title });
      expect(response.status()).toBe(201);
      more.push((await response.json()) as Song);
    }
    const shortcodes = [first, ...more].map((song) => song.shortcode);
    expect(new Set(shortcodes).size).toBe(3);
    const numbers = shortcodes.map((code) => Number(code.slice('n8-'.length)));
    expect(numbers).toEqual([...numbers].sort((a, b) => a - b));

    // The list, newest first, has all three in the Idea filter; sorted by title they are in
    // shortcode order (the tie-breaker).
    const byUpdated = await json<SongList>(
      await request.get(api(`songs?state=${idea.id}&pageSize=100`)),
    );
    const listed = byUpdated.items.filter((song) => song.title === title);
    expect(listed.map((song) => song.shortcode)).toEqual([...shortcodes].reverse());
    expect(listed.every((song) => song.state.name === 'Idea' && song.versionCount === 1)).toBe(
      true,
    );

    const byTitle = await json<SongList>(
      await request.get(api(`songs?state=${idea.id}&sort=title&pageSize=100`)),
    );
    expect(
      byTitle.items.filter((song) => song.title === title).map((song) => song.shortcode),
    ).toEqual(shortcodes);

    // Another state holds none of them.
    const writing = at(states.items, 1);
    const inWriting = await json<SongList>(
      await request.get(api(`songs?state=${writing.id}&pageSize=100`)),
    );
    expect(inWriting.items.some((song) => song.title === title)).toBe(false);
  });
});
