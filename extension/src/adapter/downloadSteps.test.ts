// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { fakeClock, snapshotHtml } from '../testing/snapshots.ts';
import { sunoObject } from '../testing/sunoResponses.ts';
import {
  DOWNLOAD_DIALOG_TARGET,
  PREPARE_WAIT_MS,
  prepareDownload,
  type PrepareDeps,
} from './downloadSteps.ts';
import { forwardedBody, OBSERVER_SOURCE, type ObservedMessage } from './observed.ts';
import { nameOf, Page } from './primitives.ts';
import { runWorkflow, type Workflow } from './workflow.ts';

/**
 * Preparing a file on Suno's page (#216, TS-004), over the snapshots: the Library list's row,
 * the clip menu with Download, and the Download dialog. Each workflow runs on the snapshot of the
 * page state it starts from. Fixtures only: nothing reaches Suno, and no unlock is spent.
 */

const CLIP = '00000000-0000-4000-8000-000000000102';
const SNAPSHOT_OF: Readonly<Record<string, string>> = {
  'open-clip-menu': 'library-list',
  'choose-download': 'clip-download-menu',
  'choose-download-format': 'download-dialog',
  'close-download-dialog': 'download-dialog',
};

afterEach(() => {
  document.body.innerHTML = '';
});

/** The page's own answer to `GET /api/download/clip/<id>?format=…`, as the observer forwards it. */
function ready(clipId: string, format: 'wav' | 'mp3' | 'm4a'): ObservedMessage {
  return {
    source: OBSERVER_SOURCE,
    type: 'observed',
    kind: 'download-clip',
    request: { cursor: null, page: null, filters: null, feedId: null },
    body: forwardedBody('download-clip', sunoObject(`download-clip.${format}.ready.response`)),
    download: { clipId, format },
  };
}

/**
 * A stand-in for the content script: each workflow runs on its own snapshot, every click is
 * recorded by the name of what it reached, and the page's answers are the ones given.
 */
function harness(
  options: { answers?: ObservedMessage[]; change?: (snapshot: string) => void } = {},
) {
  const clock = fakeClock();
  const pressed: string[] = [];
  const ran: string[] = [];
  const listener = (event: Event) => {
    const target = (event.target as Element).closest('button, [role="button"], [role="menuitem"]');
    if (target !== null) {
      pressed.push(nameOf(target));
    }
  };
  document.addEventListener('click', listener, { capture: true });
  let dialogOpen = false;
  const answers = [...(options.answers ?? [])];
  const deps: PrepareDeps = {
    session: {
      run: (workflow, values, runOptions) => {
        const id = (workflow as Workflow).id;
        ran.push(id);
        const snapshot = SNAPSHOT_OF[id] ?? 'library-list';
        document.body.innerHTML = snapshotHtml(snapshot);
        options.change?.(snapshot);
        dialogOpen = snapshot === 'download-dialog';
        const page = new Page(document, { address: () => 'https://suno.com/me', clock });
        return runWorkflow(workflow, page, values, runOptions);
      },
    },
    next: async (_kind, accept, timeoutMs) => {
      const index = answers.findIndex(accept);
      if (index < 0) {
        await clock.sleep(timeoutMs);
        return null;
      }
      return answers.splice(index, 1)[0] ?? null;
    },
    dialogOpen: () => dialogOpen,
    clock,
  };
  return { deps, pressed, ran, clock };
}

describe('preparing a file on Suno (TS-004)', () => {
  it("opens the clip's row menu, chooses Download, the format, and Unlock & Download, takes the page's address, and closes the dialog", async () => {
    const { deps, pressed, ran } = harness({ answers: [ready(CLIP, 'mp3'), ready(CLIP, 'wav')] });

    const outcome = await prepareDownload(
      deps,
      { sunoId: CLIP, format: 'wav' },
      new AbortController().signal,
    );

    expect(outcome).toEqual({
      ok: true,
      address:
        'https://suno-data-uploads.s3.amazonaws.com/studio/uploads/00000000-0000-4000-8000-000000000139.wav',
      pressed: true,
    });
    expect(ran).toEqual([
      'open-clip-menu',
      'choose-download',
      'choose-download-format',
      'close-download-dialog',
    ]);
    expect(pressed).toEqual(['More options', 'Download', 'WAV', 'Unlock & Download', 'Close']);
  });

  it.each(['mp3', 'm4a'] as const)(
    'chooses %s in the dialog and waits for its own answer',
    async (format) => {
      const { deps, pressed } = harness({ answers: [ready(CLIP, 'wav'), ready(CLIP, format)] });

      const outcome = await prepareDownload(
        deps,
        { sunoId: CLIP, format },
        new AbortController().signal,
      );

      expect(outcome.ok).toBe(true);
      expect(pressed).toContain(format.toUpperCase());
      expect(pressed).not.toContain('WAV');
    },
  );

  it('stops that file only when the clip is not in the list, pressing nothing', async () => {
    const { deps, pressed, ran } = harness();

    const outcome = await prepareDownload(
      deps,
      { sunoId: '00000000-0000-4000-8000-0000000009ff', format: 'wav' },
      new AbortController().signal,
    );

    expect(outcome).toEqual({
      ok: false,
      scope: 'file',
      reason:
        "the clip is not in the list on Suno's Library page: scroll to it there, then press Retry failed",
      pressed: false,
    });
    expect(ran).toEqual(['open-clip-menu']);
    expect(pressed).toEqual([]);
  });

  it('stops the format, naming the step, when the dialog no longer offers it, before pressing Unlock & Download', async () => {
    const { deps, pressed } = harness({
      change: (snapshot) => {
        if (snapshot === 'download-dialog') {
          // Suno renamed the WAV choice.
          for (const button of document.querySelectorAll('button')) {
            if (button.textContent === 'WAV') {
              button.textContent = 'WAV (lossless)';
            }
          }
        }
      },
    });

    const outcome = await prepareDownload(
      deps,
      { sunoId: CLIP, format: 'wav' },
      new AbortController().signal,
    );

    expect(outcome).toEqual({
      ok: false,
      scope: 'format',
      reason:
        "Prepare a file in the Download dialog: step 'format' expected the Download dialog's WAV choice. The page may be partly changed.",
      pressed: false,
    });
    expect(pressed).toEqual(['More options', 'Download']);
  });

  it('stops the format when the dialog\'s button is not "Unlock & Download": no other name is pressed', async () => {
    const { deps, pressed } = harness({
      change: (snapshot) => {
        if (snapshot === 'download-dialog') {
          for (const span of document.querySelectorAll('span')) {
            if (span.textContent === 'Unlock & Download') {
              span.textContent = 'Buy more downloads';
            }
          }
        }
      },
    });

    const outcome = await prepareDownload(
      deps,
      { sunoId: CLIP, format: 'wav' },
      new AbortController().signal,
    );

    expect(outcome.ok).toBe(false);
    expect(outcome).toMatchObject({ scope: 'format', pressed: false });
    expect(pressed).toEqual(['More options', 'Download', 'WAV']);
  });

  it(`fails the file when Suno has not prepared it within ${String(PREPARE_WAIT_MS / 1000)} seconds, and closes the dialog`, async () => {
    const { deps, pressed, clock } = harness({ answers: [ready(CLIP, 'mp3')] });

    const outcome = await prepareDownload(
      deps,
      { sunoId: CLIP, format: 'wav' },
      new AbortController().signal,
    );

    expect(outcome).toEqual({
      ok: false,
      scope: 'file',
      reason: 'Suno did not prepare the file within 30 seconds',
      pressed: true,
    });
    expect(clock.slept).toContain(PREPARE_WAIT_MS);
    expect(pressed.at(-1)).toBe('Close');
  });

  it('finds the dialog by its title, "Download" and the clip\'s title', () => {
    document.body.innerHTML = snapshotHtml('download-dialog');
    const page = new Page(document);
    expect(page.find(DOWNLOAD_DIALOG_TARGET).kind).toBe('found');
  });
});
