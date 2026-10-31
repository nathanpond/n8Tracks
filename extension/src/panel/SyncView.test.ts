// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ImageProgress, SyncScope } from '../messages.ts';
import { expectNoAxeViolations } from '../testing/a11y.ts';
import {
  countsText,
  imagesText,
  summaryText,
  SyncView,
  TRASH_HINT,
  type SyncViewOptions,
} from './SyncView.ts';

const COUNTS = { clips: 140, trashed: 1, workspaces: 12, playlists: 0 };

function view(listed: ReturnType<SyncViewOptions['choices']> = { workspaces: [], playlists: [] }) {
  const options = {
    choices: () => listed,
    preview: vi.fn<(scope: SyncScope) => void>(),
    start: vi.fn<(scope: SyncScope) => void>(),
    cancel: vi.fn<() => void>(),
    wantWorkspaces: vi.fn<() => void>(),
    refresh: vi.fn<() => void>(),
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
    expect(options.preview).toHaveBeenCalledWith({
      kind: 'workspaces',
      ids: ['w2'],
      readTrash: false,
    });
  });

  it('with no workspace known, says so and offers only Refresh from Suno (#448)', async () => {
    const empty = view();
    empty.sync.setAvailable(true, null);
    empty.choose('workspaces');

    // Opening the choice asks for the workspaces n8Tracks knows.
    expect(empty.options.wantWorkspaces).toHaveBeenCalledTimes(1);
    const text = empty.sync.element.textContent;
    expect(text).toContain(
      'n8Tracks knows no workspaces yet. Refresh from Suno to read your workspace list.',
    );
    // No instruction to click Suno's own tabs, and nothing to tick.
    expect(text).not.toContain('click the Workspaces tab');
    expect(empty.sync.element.querySelectorAll('.sync-list input[type="checkbox"]')).toHaveLength(
      0,
    );
    await expectNoAxeViolations(document);

    empty.button('Refresh from Suno')?.click();
    expect(empty.options.refresh).toHaveBeenCalledTimes(1);
    expect(empty.options.preview).not.toHaveBeenCalled();
    expect(empty.options.start).not.toHaveBeenCalled();
  });

  it('says where the workspace list stands, and offers Refresh only when nothing is under way (#448)', () => {
    const listed = { workspaces: [{ id: 'w1', name: 'Harbour Lights' }], playlists: [] };
    const { sync, button, choose } = view(listed);
    sync.setAvailable(true, null);
    choose('workspaces');
    const line = () => sync.element.querySelector('.sync-workspaces');
    const refresh = () => button('Refresh from Suno');

    sync.setWorkspaceList({ kind: 'loading' });
    expect(line()?.textContent).toBe('Asking n8Tracks for the workspaces it knows…');
    expect(refresh()?.disabled).toBe(true);

    sync.setWorkspaceList({ kind: 'ready' });
    expect(line()?.textContent).toBe(
      'These are the workspaces n8Tracks knows, with any Suno has shown on this page. If one is missing, refresh from Suno.',
    );
    expect(refresh()?.disabled).toBe(false);
    expect(sync.element.textContent).toContain('Harbour Lights');

    sync.setWorkspaceList({ kind: 'refreshing' });
    expect(line()?.textContent).toBe('Reading your workspace list on Suno…');
    expect(refresh()?.disabled).toBe(true);

    sync.setWorkspaceList({ kind: 'refreshed', count: 1, complete: true });
    expect(line()?.textContent).toBe('Read 1 workspace from Suno.');
    sync.setWorkspaceList({ kind: 'refreshed', count: 20, complete: false });
    expect(line()?.textContent).toContain('Suno sent no further page, so some may be missing.');

    sync.setWorkspaceList({ kind: 'failed', message: 'Not refreshed: no answer.' });
    expect(line()?.getAttribute('role')).toBe('alert');
    expect(line()?.textContent).toBe('Not refreshed: no answer.');
    expect(refresh()?.disabled).toBe(false);

    // Not connected: nothing to press.
    sync.setAvailable(false, 'Connect the extension to n8Tracks to sync.');
    expect(refresh()?.disabled).toBe(true);
  });

  it('keeps the workspaces chosen and focus in the list when the list is drawn again (#448)', () => {
    const { sync, input, choose } = view({
      workspaces: [
        { id: 'w1', name: 'Harbour Lights' },
        { id: 'w2', name: 'Paper Moons' },
      ],
      playlists: [],
    });
    sync.setAvailable(true, null);
    choose('workspaces');
    const box = input('w2');
    if (box === null) {
      throw new Error('No checkbox for w2');
    }
    box.focus();
    box.checked = true;
    box.dispatchEvent(new Event('change'));

    sync.setWorkspaceList({ kind: 'ready' });

    expect(input('w2')?.checked).toBe(true);
    expect(sync.element.contains(document.activeElement)).toBe(true);
  });

  it('comes back to the Workspaces choice when shown so (#448)', () => {
    const { sync, input } = view();
    sync.setAvailable(true, null);
    sync.show({ kind: 'choose', scope: 'workspaces' });
    expect(input('workspaces')?.checked).toBe(true);
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
      readTrash: false,
    });
  });

  it('offers "Also check Suno\'s Trash", off, only for workspaces and playlists, with its hint (#420)', async () => {
    const { sync, options, button, input, choose } = view({
      workspaces: [{ id: 'w1', name: 'Studio' }],
      playlists: [{ id: 'p1', name: 'Morning' }],
    });
    sync.setAvailable(true, null);
    const trash = () => sync.element.querySelector<HTMLInputElement>('input[name="n8-sync-trash"]');

    // A whole-library sync always reads the Trash: there is nothing to choose.
    expect(trash()).toBeNull();
    expect(sync.element.textContent).not.toContain(TRASH_HINT);

    for (const kind of ['workspaces', 'playlists'] as const) {
      choose(kind);
      const box = trash();
      expect(box?.checked).toBe(false);
      expect(box?.closest('label')?.textContent.trim()).toBe("Also check Suno's Trash");
      const hint = sync.element.querySelector(`#${box?.getAttribute('aria-describedby') ?? ''}`);
      expect(hint?.textContent).toBe(TRASH_HINT);
      expect(TRASH_HINT).toContain('archived in n8Tracks at the next sync that reads the Trash');
      await expectNoAxeViolations(document);
    }

    choose('library');
    expect(trash()).toBeNull();

    // Turned on, it goes into the scope the sync carries.
    choose('workspaces');
    const box = trash();
    if (box === null) {
      throw new Error('No Trash checkbox');
    }
    box.checked = true;
    box.dispatchEvent(new Event('change'));
    const workspace = input('w1');
    if (workspace === null) {
      throw new Error('No checkbox for w1');
    }
    workspace.checked = true;
    workspace.dispatchEvent(new Event('change'));
    button('Next')?.click();
    expect(options.preview).toHaveBeenLastCalledWith({
      kind: 'workspaces',
      ids: ['w1'],
      readTrash: true,
    });
  });

  it('says in the summary whether the Trash is read (#420)', () => {
    expect(summaryText({ kind: 'library' })).toContain('plus the Trash list');
    expect(summaryText({ kind: 'workspaces', ids: ['w1'], readTrash: true })).toContain(
      'plus the Trash list',
    );
    expect(summaryText({ kind: 'workspaces', ids: ['w1'], readTrash: false })).toContain(
      'not the Trash',
    );
    expect(
      summaryText({ kind: 'playlists', playlists: [{ id: 'p1', name: 'M' }], readTrash: false }),
    ).toContain('not the Trash');
  });

  it('says every scope reads your voices, counts them, and shows the note about them at the end (#439)', async () => {
    const scopes: SyncScope[] = [
      { kind: 'library' },
      { kind: 'workspaces', ids: ['w1'], readTrash: false },
      { kind: 'playlists', playlists: [{ id: 'p1', name: 'M' }], readTrash: true },
    ];
    for (const scope of scopes) {
      expect(summaryText(scope)).toContain('your voices');
    }
    expect(countsText({ ...COUNTS, voices: 1 })).toBe(
      '12 workspaces, 140 clips, 1 clip in the Trash, 0 playlists, 1 voice',
    );
    const { sync } = view();
    sync.show({
      kind: 'finished',
      counts: { ...COUNTS, voices: 3 },
      note: 'Your voices were not read: step ’voice picker’ expected the Voice picker.',
    });
    expect(sync.element.querySelector('.sync-voices')?.textContent).toBe(
      'Your voices were not read: step ’voice picker’ expected the Voice picker.',
    );
    await expectNoAxeViolations(document);
    // Complement: without a note there is no such line.
    sync.show({ kind: 'finished', counts: COUNTS });
    expect(sync.element.querySelector('.sync-voices')).toBeNull();
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
