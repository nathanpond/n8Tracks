// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { SyncScope } from '../messages.ts';
import { expectNoAxeViolations } from '../testing/a11y.ts';
import { countsText, summaryText, SyncView, type SyncViewOptions } from './SyncView.ts';

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

  it('chooses playlists by name, and says where to find them when Suno has listed none', async () => {
    const empty = view();
    empty.sync.setAvailable(true, null);
    empty.choose('playlists');
    expect(empty.sync.element.textContent).toContain(
      'No playlists yet: open Library › Playlists on Suno, then choose again.',
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
});
