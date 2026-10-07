import type { Clock } from './clock.ts';
import type { ObservedMessage, PreparedFormat } from './observed.ts';
import type { Region, Target } from './primitives.ts';
import type { AdapterSession } from './registry.ts';
import { failureText, type RunResult } from './workflow.ts';
import {
  chooseDownload,
  chooseDownloadFormat,
  closeDownloadDialog,
  openClipMenu,
} from './workflows/download.ts';

/**
 * How a file of WAV, MP3, or M4A is obtained on Suno (TS-004, #216), inside the adapter: on the
 * Library page, the clip's row's "More options", then Download, then the format in the Download
 * dialog and its one button, "Unlock & Download"; then the page's own answer to its
 * `GET /api/download/clip/<id>?format=…` gives the signed `download_url`, which is handed to the
 * service worker. The extension adds nothing to any request, reads no cookie or header, and
 * presses nothing that creates, publishes, deletes, or trashes. The streaming-quality M4A needs no
 * step here: its address is in the clip data.
 */

/** The snapshot every target here is taken from (TS-003 and TS-004). */
export const DOWNLOAD_SNAPSHOTS = [
  'library-list',
  'clip-download-menu',
  'download-dialog',
] as const;

/** The clip's link in the Library list, by its address (`page.library-list.html`). */
export function clipLink(sunoId: string): Target {
  return {
    role: 'link',
    address: `/song/${encodeURIComponent(sunoId)}`,
    description: "the clip's link in the Library list",
  };
}

/**
 * The clip's row: from its link outwards to the first element that holds a "More options" button,
 * at most six levels, as the snapshot shows every row (one link and one More options per row).
 */
export function clipRow(sunoId: string): Region {
  return { around: clipLink(sunoId), levels: 6, description: "the clip's row in the Library list" };
}

/** The row's "More options" button, which opens the clip's menu. */
export function clipMoreOptions(sunoId: string): Target {
  return {
    role: 'button',
    name: 'More options',
    popup: 'menu',
    within: clipRow(sunoId),
    description: 'the "More options" button of the clip\'s row',
  };
}

/** The clip's open menu (`page.clip-download-menu.html`: Download has no submenu). */
export const CLIP_MENU: Target = { role: 'menu', description: "the clip's open menu" };

/** The menu's Download item. */
export const DOWNLOAD_ITEM: Target = {
  role: 'menuitem',
  name: 'Download',
  within: CLIP_MENU,
  description: "the Download item of the clip's menu",
};

/** The Download dialog (`page.download-dialog.html`), titled "Download" and the clip's title. */
export const DOWNLOAD_DIALOG_TARGET: Target = {
  role: 'dialog',
  name: /^Download\b/,
  description: "Suno's Download dialog",
};

/** Each format's choice in the dialog, by the name it shows. */
export const FORMAT_CHOICES: Readonly<Record<PreparedFormat, Target>> = {
  wav: {
    role: 'button',
    name: 'WAV',
    within: DOWNLOAD_DIALOG_TARGET,
    description: "the Download dialog's WAV choice",
  },
  mp3: {
    role: 'button',
    name: 'MP3',
    within: DOWNLOAD_DIALOG_TARGET,
    description: "the Download dialog's MP3 choice",
  },
  m4a: {
    role: 'button',
    name: 'M4A',
    within: DOWNLOAD_DIALOG_TARGET,
    description: "the Download dialog's M4A choice",
  },
};

/**
 * The dialog's one button. TS-004 saw it named "Unlock & Download" on a clip not yet unlocked;
 * for an unlocked clip Suno answers the unlock with `already_unlocked` and spends nothing. A
 * dialog whose button is named otherwise stops the step: no other name is pressed.
 */
export const DOWNLOAD_BUTTON: Target = {
  role: 'button',
  name: 'Unlock & Download',
  within: DOWNLOAD_DIALOG_TARGET,
  description: 'the Download dialog\'s "Unlock & Download" button',
};

/** The dialog's Close button. */
export const CLOSE_DOWNLOAD_DIALOG: Target = {
  role: 'button',
  name: 'Close',
  within: DOWNLOAD_DIALOG_TARGET,
  description: "the Download dialog's Close button",
};

/**
 * How long a prepared file is waited for after the button is pressed: TS-004 saw 5.4 s for WAV
 * and about 2.5 s for MP3 and M4A, on a small sample; 30 s is the story's margin.
 */
export const PREPARE_WAIT_MS = 30_000;

/** One file to prepare: a clip in a format Suno prepares. */
export interface PrepareJob {
  sunoId: string;
  format: PreparedFormat;
}

/**
 * What preparing a file came to. A failure says how far it reaches: `file` this file only (the
 * clip is not in the list, or Suno did not prepare it in time), `format` every file of this format
 * (a step no longer matches Suno's page), `page` every file that needs the page (the tab is not on
 * the Library). `pressed` says whether "Unlock & Download" was pressed, which may have unlocked
 * the clip. `reason` holds no value from the page (invariant 6).
 */
export type PrepareOutcome =
  | { ok: true; address: string; pressed: true }
  | { ok: false; scope: 'file' | 'format' | 'page'; reason: string; pressed: boolean };

/** What preparing needs from the content script. */
export interface PrepareDeps {
  session: Pick<AdapterSession, 'run'>;
  /** The page's own answers, as the observer forwards them. */
  next(
    kind: 'download-clip',
    accept: (message: ObservedMessage) => boolean,
    timeoutMs: number,
    signal: AbortSignal,
  ): Promise<ObservedMessage | null>;
  /** Whether the Download dialog is still open (read only). */
  dialogOpen(): boolean;
  clock?: Clock;
}

function stopped(result: Extract<RunResult, { ok: false }>, pressed: boolean): PrepareOutcome {
  // A clip that is not in the list stops that file only; any other step stops the format.
  const notListed =
    result.failure.workflowId === openClipMenu.id &&
    result.failure.step === 'clip row' &&
    result.failure.kind === 'check';
  return notListed
    ? {
        ok: false,
        scope: 'file',
        reason:
          "the clip is not in the list on Suno's Library page: scroll to it there, then press Retry failed",
        pressed,
      }
    : { ok: false, scope: 'format', reason: failureText(result.failure), pressed };
}

/**
 * Prepares one file on the Library page and answers its signed address, read from the page's
 * own answer; the dialog is closed afterwards when it is still open. Each file is prepared just
 * before it is downloaded: an address is never kept for later (it expires after an hour).
 */
export async function prepareDownload(
  deps: PrepareDeps,
  job: PrepareJob,
  signal: AbortSignal,
): Promise<PrepareOutcome> {
  const options = deps.clock === undefined ? {} : { clock: deps.clock };
  const menu = await deps.session.run(openClipMenu, { sunoId: job.sunoId }, options);
  if (!menu.ok) {
    return stopped(menu, false);
  }
  const item = await deps.session.run(chooseDownload, {}, options);
  if (!item.ok) {
    return stopped(item, false);
  }
  const chosen = await deps.session.run(chooseDownloadFormat, { format: job.format }, options);
  if (!chosen.ok) {
    return stopped(chosen, chosen.failure.step === 'download' && chosen.failure.phase !== 'expect');
  }
  const ready = await deps.next(
    'download-clip',
    (message) =>
      message.download?.clipId.toLowerCase() === job.sunoId.toLowerCase() &&
      message.download.format === job.format,
    PREPARE_WAIT_MS,
    signal,
  );
  if (deps.dialogOpen()) {
    // Leaves the page as it was; a failure here is not the file's.
    await deps.session.run(closeDownloadDialog, {}, options);
  }
  const body = ready?.body;
  const address =
    typeof body === 'object' && body !== null && 'download_url' in body ? body.download_url : null;
  if (typeof address !== 'string') {
    return {
      ok: false,
      scope: 'file',
      reason: signal.aborted
        ? 'the download was cancelled'
        : `Suno did not prepare the file within ${String(PREPARE_WAIT_MS / 1000)} seconds`,
      pressed: true,
    };
  }
  return { ok: true, address, pressed: true };
}
