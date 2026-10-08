// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import type { DownloadClip } from '../download/clips.ts';
import type { DownloadFile, DownloadRun } from '../download/downloader.ts';
import { expectNoAxeViolations } from '../testing/a11y.ts';
import {
  DownloadView,
  DRAWN_ROWS,
  fileStatusText,
  inN8TracksText,
  recordingLines,
  ROW_HEIGHT,
  startRefusal,
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

function file(key: string, change: Partial<DownloadFile> = {}): DownloadFile {
  const [sunoId = key, format = 'wav'] = key.split(':');
  return {
    key,
    sunoId,
    title: 'Song',
    displayName: 'maker',
    artist: null,
    format: format as DownloadFile['format'],
    unlocked: true,
    streamAddress: null,
    fileName: `Song (suno-${sunoId}).${format === 'm4a-stream' ? 'm4a' : format}`,
    state: 'queued',
    paused: null,
    downloadId: null,
    received: 0,
    total: null,
    reason: null,
    savedName: null,
    renamed: false,
    renameToM4a: false,
    fetchedAgain: false,
    recordId: null,
    savedAt: null,
    ...change,
  };
}

function run(files: DownloadFile[]): DownloadRun {
  return { files, tabId: 7, unlocks: { confirmed: [], spent: [] } };
}

describe('Start and the run (#216)', () => {
  it('starts once the unlocks are confirmed, with the count the user confirmed', () => {
    const started: number[] = [];
    const { view } = render({
      start: (unlocks) => {
        started.push(unlocks);
      },
    });
    view.addClips([clip(1), clip(2)]);
    view.setRead({ kind: 'read', count: 2 });
    view.selection.toggle('clip-1', true);
    view.setFormats(['wav']);
    view.setUsage({ used: 0, limit: 60, additional: 0 });
    const start = [...view.element.querySelectorAll('button')].find(
      (button) => button.textContent === 'Start download',
    );
    const confirm = view.element.querySelector<HTMLInputElement>('.dl-confirm input');

    expect(start?.disabled).toBe(true);
    expect(startRefusal(view.selection, { used: 0, limit: 60, additional: 0 })).toBe(
      'Confirm the 1 Suno download unlock this run uses.',
    );
    if (confirm !== null) {
      confirm.checked = true;
      confirm.dispatchEvent(new Event('change'));
    }
    expect(start?.disabled).toBe(false);
    start?.click();

    expect(started).toEqual([1]);
    // A run's confirmation is not kept for the next one.
    expect(confirm?.checked).toBe(false);
  });

  it('shows each file and the whole run, with Cancel, Retry failed, and Resume, and is accessible', async () => {
    const asked: string[] = [];
    const { view } = render({
      control: (action) => {
        asked.push(action);
      },
    });
    const section = () => view.element.querySelector<HTMLElement>('.dl-run');
    const lines = () =>
      [...view.element.querySelectorAll('.dl-file')].map((line) => line.textContent);
    const button = (name: string) =>
      [...view.element.querySelectorAll('button')].find(
        (candidate) => (candidate.getAttribute('aria-label') ?? candidate.textContent) === name,
      );
    expect(section()?.hidden).toBe(true);

    view.setRun(
      run([
        file('a:wav', { state: 'saved', savedName: 'Song (suno-a).wav' }),
        file('b:mp3', { state: 'downloading', received: 250, total: 1000 }),
        file('c:wav', {
          state: 'failed',
          reason: 'Suno did not prepare the file within 30 seconds',
        }),
        file('d:m4a', {
          state: 'saved',
          savedName: 'd_lyrics.mp4',
          renamed: true,
          renameToM4a: true,
        }),
        file('e:m4a-stream', { state: 'queued' }),
      ]),
    );

    expect(section()?.hidden).toBe(false);
    expect(view.element.querySelector('.dl-run-status')?.textContent).toBe(
      '2 of 5 files saved, 1 failed, 2 to go.',
    );
    expect(lines()).toEqual([
      'Song (suno-a).wav (WAV): Saved',
      'Song (suno-b).mp3 (MP3): Downloading, 25%',
      'Song (suno-c).wav (WAV): Failed: Suno did not prepare the file within 30 seconds.',
      'Song (suno-d).m4a (M4A): Saved, saved under a different name: d_lyrics.mp4; rename it from .mp4 to .m4a before n8Tracks scans it.',
      'Song (suno-e).m4a (M4A (streaming quality)): Waiting',
    ]);
    expect(button('Resume downloads')?.hidden).toBe(true);
    await expectNoAxeViolations(document);

    button('Cancel downloads')?.click();
    button('Retry failed downloads')?.click();
    expect(asked).toEqual(['cancel', 'retry']);

    view.setRun(
      run([
        file('c:wav', {
          paused: "The Suno tab was closed. Open Suno's Library in a tab and press Resume.",
        }),
      ]),
    );
    expect(lines()).toEqual([
      "Song (suno-c).wav (WAV): Waiting for Resume. The Suno tab was closed. Open Suno's Library in a tab and press Resume.",
    ]);
    expect(view.element.querySelector('.dl-run-status')?.textContent).toBe(
      '0 of 1 file saved, 1 waiting for Resume.',
    );
    expect(button('Retry failed downloads')?.disabled).toBe(true);
    button('Resume downloads')?.click();
    expect(asked).toEqual(['cancel', 'retry', 'resume']);
  });

  it('says what the browser did with the chosen name', () => {
    expect(
      fileStatusText(file('a:wav', { state: 'saved', savedName: 'Song (suno-a) (1).wav' })),
    ).toBe('Song (suno-a).wav (WAV): Saved, saved as Song (suno-a) (1).wav.');
  });
});

describe('files already downloaded and their records (#222)', () => {
  function lookup(downloaded: Record<string, string[]>) {
    return {
      kind: 'found' as const,
      rows: new Map(
        Object.entries(downloaded).map(([sunoId, downloadedFormats]) => [
          sunoId,
          { sunoId, generation: null, artist: null, deleted: false, downloadedFormats },
        ]),
      ),
    };
  }

  function loaded() {
    const rendered = render();
    rendered.view.addClips([clip(1), clip(2), clip(3)]);
    rendered.view.setRead({ kind: 'read', count: 3 });
    return rendered;
  }

  const box = (view: DownloadView, label: string) =>
    [...view.element.querySelectorAll('label')]
      .find((item) => item.textContent.trim().startsWith(label))
      ?.querySelector('input');
  const summary = (view: DownloadView) =>
    [...view.element.querySelectorAll('.dl-summary p')].map((line) => line.textContent);

  it('filters to the clips not yet downloaded once n8Tracks says what was, and is accessible', async () => {
    const { view, rows } = loaded();
    const notDownloaded = box(view, 'Not yet downloaded');

    expect(notDownloaded?.disabled).toBe(true);
    expect(view.element.querySelector('.dl-not-downloaded-why')?.textContent).toBe(
      'Needs n8Tracks to say what was already downloaded.',
    );

    view.setLookup(lookup({ 'clip-1': ['wav'], 'clip-2': ['m4a-stream'], 'clip-3': [] }));
    expect(notDownloaded?.disabled).toBe(false);
    if (notDownloaded != null) {
      notDownloaded.checked = true;
      notDownloaded.dispatchEvent(new Event('change'));
    }
    expect(rows().map((row) => row.dataset.sunoId)).toEqual(['clip-3']);

    // A chosen format narrows it to the clips lacking that format.
    view.setFormats(['wav']);
    box(view, 'WAV')?.dispatchEvent(new Event('change'));
    expect(rows().map((row) => row.dataset.sunoId)).toEqual(['clip-2', 'clip-3']);
    await expectNoAxeViolations(document);
  });

  it('skips files already downloaded unless told not to, naming them in the summary', () => {
    const { view } = loaded();
    view.setLookup(lookup({ 'clip-1': ['wav'], 'clip-2': [], 'clip-3': [] }));
    view.setFormats(['wav', 'm4a-stream']);
    view.selection.toggle('clip-1', true);
    view.selection.toggle('clip-2', true);
    view.setUsage({ used: 0, limit: 60, additional: 0 });
    const skip = box(view, 'Skip files already downloaded');

    expect(skip?.checked).toBe(true);
    expect(summary(view)).toContain('Files: 3.');
    expect(summary(view)).toContain('Skipped, already downloaded: Song 1 (WAV).');

    if (skip != null) {
      skip.checked = false;
      skip.dispatchEvent(new Event('change'));
    }
    expect(summary(view)).toContain('Files: 4.');
    expect(summary(view).some((line) => line.startsWith('Skipped'))).toBe(false);
  });

  it('counts a file saved in this run as downloaded', () => {
    const { view } = loaded();
    view.setLookup(lookup({}));
    view.setFormats(['wav']);
    view.selection.toggle('clip-2', true);
    view.setRun(run([file('clip-2:wav', { state: 'saved', savedName: 'x.wav', recordId: 'r' })]));

    expect(summary(view)).toContain('Skipped, already downloaded: Song 2 (WAV).');
  });

  it('says nothing is skipped while what was downloaded is not known', () => {
    const { view } = loaded();
    view.setFormats(['wav']);
    view.selection.toggle('clip-1', true);
    view.setLookup({ kind: 'unavailable', message: 'The extension is not connected to n8Tracks.' });
    view.setUsage({ used: 0, limit: 60, additional: 0 });

    expect(summary(view)).toContain(
      'What was already downloaded is not known, so no file is skipped.',
    );
    expect(view.element.querySelector('.dl-lookup')?.textContent).toContain(
      'The clips can still be downloaded, but they are not recorded in n8Tracks.',
    );
  });

  it('says how recording the run in n8Tracks stands', () => {
    const { view } = loaded();
    view.setRun(run([file('clip-1:wav', { state: 'saved', savedName: 'x.wav', recordId: 'r' })]));
    const line = () => view.element.querySelector<HTMLElement>('.dl-records');

    view.setRecords({ connected: true, pending: 0, refused: 0, unrecorded: 0 });
    expect(line()?.hidden).toBe(true);

    view.setRecords({ connected: true, pending: 2, refused: 1, unrecorded: 0 });
    expect(line()?.hidden).toBe(false);
    expect(line()?.textContent).toContain(
      '2 downloads are not yet recorded in n8Tracks; they are sent when the connection works.',
    );
    expect(line()?.textContent).toContain(
      '1 download could not be recorded: n8Tracks refused the report.',
    );
    expect(recordingLines({ connected: false, pending: 0, refused: 0, unrecorded: 3 })).toEqual([
      'The extension is not connected to n8Tracks: downloads still work, but they are not recorded there.',
      '3 downloads were not recorded in n8Tracks: the extension was not connected.',
    ]);
    expect(recordingLines(null)).toEqual([]);
  });
});
