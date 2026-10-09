// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ImageProgress, SyncScope } from '../messages.ts';
import { expectNoAxeViolations } from '../testing/a11y.ts';
import { countsText, imagesText, summaryText, SyncView, type SyncViewOptions } from './SyncView.ts';

const COUNTS = { clips: 140, trashed: 1, workspaces: 12, playlists: 0 };

function view(listed: ReturnType<SyncViewOptions['choices']> = { workspaces: [], playlists: [] }) {
  const options = {
    choices: () => listed,
    preview: vi.fn<(scope: SyncScope) => void>(),
    start: vi.fn<(scope: SyncScope) => void>(),
    cancel: vi.fn<() => void>(),
  };
  const sync = new SyncView(document, options);
  const main = document.createElement('main');
  main.append(sync.element);
  document.body.append(main);
  const button = (name: string) =>
    [...sync.element.querySelectorAll('button')].find(
      (candidate) => (candidate.getAttribute('aria-label') ?? candidate.textContent) === name,
    );
  const input = (value: string) =>
    sync.element.querySelector<HTMLInputElement>(`input[value="${value}"]`);
  const choose = (value: string) => {
    const radio = input(value);
    if (radio === null) {
      throw new Error(`No input ${value}`);
    }
    radio.checked = true;
    radio.dispatchEvent(new Event('change'));
  };
  return { sync, options, button, input, choose };
}

beforeEach(() => {
  document.title = 'Suno';
  document.documentElement.lang = 'en';
});

afterEach(() => {
  document.body.innerHTML = '';
});

describe('Sync to n8Tracks in the panel', () => {
  it('offers the three scopes, the whole library first, with labelled controls', async () => {
    const { sync, input } = view();
    sync.setAvailable(true, null);

    expect(input('library')?.checked).toBe(true);
    expect(
      [...sync.element.querySelectorAll('label')].map((label) => label.textContent.trim()),
    ).toEqual(['Whole library', 'Workspaces', 'Playlists']);
    await expectNoAxeViolations(document);
  });

  it('asks for the summary of the whole library on Next, starting nothing', () => {
    const { sync, options, button } = view();
    sync.setAvailable(true, null);

    button('Next')?.click();

    expect(options.preview).toHaveBeenCalledWith({ kind: 'library' });
    expect(options.start).not.toHaveBeenCalled();
  });

  it('reads only the workspaces chosen, from those Suno has listed', () => {
    const { sync, options, button, input, choose } = view({
      workspaces: [
        { id: 'w1', name: 'Studio' },
        { id: 'w2', name: '' },
      ],
      playlists: [],
    });
    sync.setAvailable(true, null);
    choose('workspaces');

    button('Next')?.click();
    expect(options.preview).not.toHaveBeenCalled();
    expect(sync.element.querySelector('#n8-sync-problem')?.textContent).toBe(
      'Choose at least one workspace.',
    );

    expect(sync.element.textContent).toContain('(unnamed)');
    const box = input('w2');
    if (box === null) {
      throw new Error('No checkbox for w2');
    }
    box.checked = true;
    box.dispatchEvent(new Event('change'));
    button('Next')?.click();
    expect(options.preview).toHaveBeenCalledWith({ kind: 'workspaces', ids: ['w2'] });
  });

  it('says where to find the workspaces when Suno has listed none (TS-007: by the tab, not a refresh)', () => {
    const empty = view();
    empty.sync.setAvailable(true, null);
    empty.choose('workspaces');
    expect(empty.sync.element.textContent).toContain(
      'No workspaces yet: open Library on Suno, then click the Workspaces tab (refreshing the page does not load the list), then choose again.',
    );
  });

  it('chooses playlists by name, and says where to find them when Suno has listed none', async () => {
    const empty = view();
    empty.sync.setAvailable(true, null);
    empty.choose('playlists');
    expect(empty.sync.element.textContent).toContain(
      'No playlists yet: open Library on Suno, then click the Playlists tab (refreshing the page does not load the list), then choose again.',
    );
    await expectNoAxeViolations(document);
    document.body.innerHTML = '';

    const { sync, options, button, input, choose } = view({
      workspaces: [],
      playlists: [{ id: 'p1', name: 'Morning' }],
    });
    sync.setAvailable(true, null);
    choose('playlists');
    const box = input('p1');
    if (box === null) {
      throw new Error('No checkbox for p1');
    }
    box.checked = true;
    box.dispatchEvent(new Event('change'));
    button('Next')?.click();

    expect(options.preview).toHaveBeenCalledWith({
      kind: 'playlists',
      playlists: [{ id: 'p1', name: 'Morning' }],
    });
  });

  it('shows the summary, warns of a replaced export, and starts only on Start sync', async () => {
    const { sync, options, button } = view();
    sync.setAvailable(true, null);

    sync.show({ kind: 'summary', scope: { kind: 'library' }, replacesReady: true });

    expect(sync.element.querySelector('.sync-summary')?.textContent).toBe(
      summaryText({ kind: 'library' }),
    );
    expect(sync.element.querySelector('.warning')?.textContent).toContain('replaces it');
    expect(document.activeElement).toBe(button('Start sync'));
    await expectNoAxeViolations(document);

    button('Back')?.click();
    expect(options.start).not.toHaveBeenCalled();
    expect(button('Next')).toBeDefined();

    sync.show({ kind: 'summary', scope: { kind: 'library' }, replacesReady: false });
    expect(sync.element.querySelector('.warning')).toBeNull();
    button('Start sync')?.click();
    expect(options.start).toHaveBeenCalledWith({ kind: 'library' });
  });

  it('is disabled when a sync cannot start, saying why', () => {
    const { sync, button, input } = view();

    sync.setAvailable(false, 'This credential lacks suno.sync');

    expect(button('Next')?.disabled).toBe(true);
    expect(input('library')?.disabled).toBe(true);
    expect(sync.element.querySelector('.detail')?.textContent).toBe(
      'This credential lacks suno.sync',
    );
  });

  it('shows counts while reading, in a status, with Cancel', async () => {
    const { sync, options, button } = view();

    sync.show({ kind: 'reading', step: 'Read the library', counts: COUNTS });

    const status = sync.element.querySelector('[role="status"]');
    expect(status?.textContent).toBe(
      `Reading: Read the library. Read so far: ${countsText(COUNTS)}.`,
    );
    expect(countsText(COUNTS)).toBe('12 workspaces, 140 clips, 1 clip in the Trash, 0 playlists');
    expect(document.activeElement).toBe(button('Cancel sync'));
    await expectNoAxeViolations(document);

    button('Cancel sync')?.click();
    expect(options.cancel).toHaveBeenCalledTimes(1);
  });

  it('names the step that failed, and Try again goes back to the choice', async () => {
    const { sync, button } = view();
    sync.setAvailable(true, null);

    sync.show({
      kind: 'stopped',
      report: "step 'Read the Trash' expected a page of the Trash list",
    });

    expect(sync.element.querySelector('[role="alert"]')?.textContent).toBe(
      "Sync stopped: step 'Read the Trash' expected a page of the Trash list. Nothing was sent for review.",
    );
    await expectNoAxeViolations(document);
    button('Try again: Sync to n8Tracks')?.click();
    expect(sync.current).toEqual({ kind: 'choose' });
  });

  it('says when a sync was cancelled or finished', () => {
    const { sync } = view();

    sync.show({ kind: 'cancelled' });
    expect(sync.element.textContent).toContain('Sync cancelled. Nothing more was read');

    sync.show({ kind: 'finished', counts: COUNTS });
    expect(sync.element.textContent).toContain('The review is open in n8Tracks.');
  });

  it('shows the cover images of a finished sync in a status of its own, updated in place', async () => {
    const { sync, button } = view();
    const images = (change: Partial<ImageProgress>): ImageProgress => ({
      exportId: 'e1',
      tabId: 7,
      state: 'sending',
      total: 40,
      sent: 12,
      failed: 0,
      ignored: 0,
      ...change,
    });
    sync.show({ kind: 'finished', counts: COUNTS });
    const line = () => sync.element.querySelector<HTMLElement>('.sync-images');
    // Nothing to say until the service worker answers.
    expect(line()?.hidden).toBe(true);
    expect(line()?.getAttribute('role')).toBe('status');

    const again = button('Sync again');
    again?.focus();
    sync.setImages(images({}));
    expect(line()?.textContent).toBe(
      'Cover images: 12 of 40 sent so far. You can start the review meanwhile.',
    );
    // Only the line changed: the focus stays where the user left it.
    expect(document.activeElement).toBe(again);
    expect(button('Sync again')).toBe(again);

    sync.setImages(images({ state: 'finished', sent: 38, failed: 2 }));
    expect(line()?.textContent).toBe(
      'Cover images: 38 of 40 sent. 2 images could not be read or sent; those Generations are imported without artwork.',
    );
    expect(sync.current).toMatchObject({ kind: 'finished', images: { sent: 38, failed: 2 } });
    await expectNoAxeViolations(document);

    // Not after the user moved on.
    sync.show({ kind: 'choose' });
    sync.setImages(images({ state: 'finished', sent: 40 }));
    expect(sync.current).toEqual({ kind: 'choose' });
  });

  it('says how cover images are going, and when they are skipped', () => {
    const base: ImageProgress = {
      exportId: 'e1',
      tabId: 7,
      state: 'waiting',
      total: 1,
      sent: 0,
      failed: 0,
      ignored: 0,
    };
    expect(imagesText(null)).toBeNull();
    expect(imagesText({ ...base, state: 'collecting' })).toBeNull();
    expect(imagesText(base)).toBe(
      'Cover images: waiting for n8Tracks to get the export ready (1 image to send).',
    );
    expect(imagesText({ ...base, state: 'finished', total: 0 })).toBe('No cover images to send.');
    expect(imagesText({ ...base, state: 'finished', sent: 0, failed: 1 })).toBe(
      'Cover images: 0 of 1 sent. 1 image could not be read or sent; that Generation is imported without artwork.',
    );
    expect(imagesText({ ...base, state: 'stopped', failed: 1 })).toContain(
      'Cover images stopped: 0 of 1 sent',
    );
    expect(imagesText({ ...base, state: 'skipped' })).toBe(
      'Cover images are not brought along: Suno does not let the extension read them without signing in, so the Generations are imported without artwork.',
    );
  });
});
