// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import type { DownloadClip } from '../download/clips.ts';
import { expectNoAxeViolations } from '../testing/a11y.ts';
import {
  DownloadView,
  DRAWN_ROWS,
  inN8TracksText,
  ROW_HEIGHT,
  VIRTUALISE_ABOVE,
  type DownloadViewOptions,
} from './DownloadView.ts';

function clip(index: number): DownloadClip {
  return {
    sunoId: `clip-${String(index)}`,
    title: `Song ${String(index)}`,
    displayName: 'Maker',
    durationSeconds: 61,
    createdAt: '2026-10-01T00:00:00.000Z',
    workspace: { id: 'w1', name: 'Studio' },
    unlocked: false,
    hidden: false,
    unavailable: null,
    hasStream: true,
  };
}

function render(options: Partial<DownloadViewOptions> = {}) {
  document.title = 'Suno';
  document.documentElement.lang = 'en';
  const view = new DownloadView(document, {
    load: () => undefined,
    cancel: () => undefined,
    retryLookup: () => undefined,
    formatsChanged: () => undefined,
    ...options,
  });
  const main = document.createElement('main');
  main.append(view.element);
  document.body.append(main);
  const rows = () => [...view.element.querySelectorAll<HTMLElement>('.dl-row')];
  return { view, rows };
}

afterEach(() => {
  document.body.innerHTML = '';
});

describe('the Download view', () => {
  it('starts with Load library only, saying that nothing changes, and is accessible', async () => {
    let loads = 0;
    const { view, rows } = render({
      load: () => {
        loads += 1;
      },
    });

    expect(rows()).toEqual([]);
    expect(view.element.querySelector('.dl-empty')?.textContent).toBe(
      'Load your library to choose clips.',
    );
    expect(view.element.querySelector('#n8-dl-intro')?.textContent).toContain(
      'change nothing in Suno and import nothing into n8Tracks',
    );
    view.element.querySelector<HTMLButtonElement>('.dl-actions button')?.click();
    expect(loads).toBe(1);
    await expectNoAxeViolations(document);
  });

  it('shows counts and Cancel while it reads, and turns Load library into Refresh after', () => {
    let cancels = 0;
    const { view } = render({
      cancel: () => {
        cancels += 1;
      },
    });
    const [load, cancel] = [
      ...view.element.querySelectorAll<HTMLButtonElement>('.dl-actions button'),
    ];

    view.setRead({ kind: 'reading', count: 120 });
    expect(view.element.querySelector('.dl-status')?.textContent).toBe(
      'Reading your Suno library: 120 clips so far. Keep this tab open.',
    );
    expect(load?.disabled).toBe(true);
    expect(cancel?.hidden).toBe(false);
    cancel?.click();
    expect(cancels).toBe(1);

    view.setRead({ kind: 'read', count: 120 });
    expect(load?.textContent).toBe('Refresh');
    expect(load?.disabled).toBe(false);
    expect(cancel?.hidden).toBe(true);
  });

  it(`draws only the rows in view above ${String(VIRTUALISE_ABOVE)} clips, and every row up to it`, () => {
    const { view, rows } = render();

    view.addClips(Array.from({ length: VIRTUALISE_ABOVE }, (_, i) => clip(i)));
    expect(rows()).toHaveLength(VIRTUALISE_ABOVE);

    view.addClips([clip(VIRTUALISE_ABOVE), clip(VIRTUALISE_ABOVE + 1), clip(999)]);
    expect(rows()).toHaveLength(DRAWN_ROWS);
    expect(rows()[0]?.getAttribute('aria-posinset')).toBe('1');
    expect(rows()[0]?.getAttribute('aria-setsize')).toBe(String(VIRTUALISE_ABOVE + 3));

    // Scrolling draws the rows further down.
    const box = view.element.querySelector<HTMLElement>('.dl-list');
    if (box !== null) {
      box.scrollTop = 150 * ROW_HEIGHT;
      box.dispatchEvent(new Event('scroll'));
    }
    const first = Number(rows()[0]?.getAttribute('aria-posinset'));
    expect(first).toBeGreaterThan(100);
    expect(rows().some((row) => row.dataset.sunoId === 'clip-150')).toBe(true);
    expect(rows()).toHaveLength(DRAWN_ROWS);
  });

  it('keeps a selection made in the drawn rows when they are drawn again', () => {
    const { view } = render();
    view.addClips(Array.from({ length: VIRTUALISE_ABOVE + 10 }, (_, i) => clip(i)));
    view.setRead({ kind: 'read', count: VIRTUALISE_ABOVE + 10 });

    const box = view.element.querySelector<HTMLInputElement>('input[data-suno-id="clip-3"]');
    if (box !== null) {
      box.checked = true;
      box.dispatchEvent(new Event('change'));
    }
    view.setLookup({ kind: 'checking' });

    expect(
      view.element.querySelector<HTMLInputElement>('input[data-suno-id="clip-3"]')?.checked,
    ).toBe(true);
    expect(view.selection.selectedIds()).toEqual(['clip-3']);
  });

  it('says per row whether n8Tracks has the clip, and unknown when it cannot say', () => {
    const rows = new Map([
      [
        'a',
        {
          sunoId: 'a',
          generation: { id: 'g', shortcode: 'n8-2-v1-g1' },
          artist: null,
          deleted: false,
          downloadedFormats: [],
        },
      ],
      ['b', { sunoId: 'b', generation: null, artist: null, deleted: true, downloadedFormats: [] }],
    ]);

    expect(inN8TracksText({ kind: 'found', rows }, 'a')).toBe('In n8Tracks: n8-2-v1-g1');
    expect(inN8TracksText({ kind: 'found', rows }, 'b')).toBe('Deleted in n8Tracks');
    expect(inN8TracksText({ kind: 'found', rows }, 'c')).toBe('Not in n8Tracks');
    expect(inN8TracksText({ kind: 'unavailable', message: 'x' }, 'a')).toBe('In n8Tracks: unknown');
    expect(inN8TracksText({ kind: 'failed', message: 'x' }, 'a')).toBe('In n8Tracks: unknown');
  });
});
