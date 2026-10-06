import { Page } from '../adapter/primitives.ts';
import type { Clock } from '../adapter/clock.ts';

/** The TS-003 page snapshots in `fixtures/suno/`, by name: `create-songs-advanced-more-options`. */
const pages = import.meta.glob<string>('../../fixtures/suno/page.*.html', {
  query: '?raw',
  import: 'default',
  eager: true,
});

export const SNAPSHOT_NAMES: readonly string[] = Object.keys(pages)
  .map((path) => /page\.(.+)\.html$/.exec(path)?.[1] ?? '')
  .filter((name) => name !== '')
  .toSorted();

export function snapshotHtml(name: string): string {
  const html = pages[`../../fixtures/suno/page.${name}.html`];
  if (html === undefined) {
    throw new Error(`There is no snapshot page.${name}.html.`);
  }
  return html;
}

/**
 * Loads a snapshot as the live document's body (jsdom), and returns a {@link Page} over it at
 * `address`. No network: the snapshot is the whole page the adapter sees.
 */
export function loadSnapshot(
  name: string,
  address = 'https://suno.com/create',
  clock?: Clock,
): Page {
  document.body.innerHTML = snapshotHtml(name);
  return new Page(document, { address: () => address, ...(clock ? { clock } : {}) });
}

/** A clock that moves only when the code under test sleeps, so polling takes no real time. */
export function fakeClock(): Clock & { slept: number[] } {
  let now = 0;
  const slept: number[] = [];
  return {
    slept,
    now: () => now,
    sleep: (ms) => {
      slept.push(ms);
      now += ms;
      return Promise.resolve();
    },
  };
}
