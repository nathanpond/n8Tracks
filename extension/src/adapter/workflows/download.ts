import { sunoPage } from '../addresses.ts';
import {
  clipLink,
  clipMoreOptions,
  CLOSE_DOWNLOAD_DIALOG,
  DOWNLOAD_BUTTON,
  DOWNLOAD_DIALOG_TARGET,
  DOWNLOAD_ITEM,
  FORMAT_CHOICES,
} from '../downloadSteps.ts';
import type { PreparedFormat } from '../observed.ts';
import type { Page } from '../primitives.ts';
import { OK, present, type StepContext, type Workflow } from '../workflow.ts';
import { SONG_ROWS } from './loadMore.ts';

/**
 * Having a clip's file prepared on Suno (#216, TS-004): its row's "More options" on the Library
 * page, the menu's Download, then the format and "Unlock & Download" in the Download dialog. The
 * dialog's controls are invariant 4's second permitted change: they are pressed only here, and
 * only through `Page.downloadDialogClick`. `adapter/downloadSteps.ts` runs these in turn and reads
 * the page's own answer; the service worker decides whether a run may unlock a clip at all.
 */

/** Opening a clip's menu: which clip. */
export interface ClipMenuContext extends StepContext {
  sunoId: string;
}

/** Choosing a format in the Download dialog. */
export interface DownloadFormatContext extends StepContext {
  format: PreparedFormat;
}

function songRows(page: Page) {
  return present(page, SONG_ROWS);
}

/** The clip's row in the Library list, and its "More options" pressed. */
export const openClipMenu: Workflow<ClipMenuContext> = {
  id: 'open-clip-menu',
  title: "Open a clip's menu in the Library",
  feature: 'download',
  startsOn: sunoPage('library'),
  needs: [{ step: 'clip row', check: songRows }],
  steps: [
    {
      name: 'clip row',
      expect: ({ page, sunoId }) => present(page, clipLink(sunoId)),
      act: () => undefined,
      verify: () => OK,
    },
    {
      name: 'more options',
      expect: ({ page, sunoId }) => present(page, clipMoreOptions(sunoId)),
      act: ({ page, sunoId }) => {
        const found = page.find(clipMoreOptions(sunoId));
        if (found.kind === 'found') {
          page.click(found.found);
        }
      },
      // The menu opening is the next workflow's first check.
      verify: () => OK,
    },
  ],
  fixtures: ['library-list'],
};

/** The open clip menu's Download item pressed. */
export const chooseDownload: Workflow = {
  id: 'choose-download',
  title: "Choose Download in a clip's menu",
  feature: 'download',
  startsOn: sunoPage('library'),
  after: 'open-clip-menu',
  needs: [{ step: 'download item', check: (page) => present(page, DOWNLOAD_ITEM) }],
  steps: [
    {
      name: 'download item',
      expect: ({ page }) => present(page, DOWNLOAD_ITEM),
      act: ({ page }) => {
        const found = page.find(DOWNLOAD_ITEM);
        if (found.kind === 'found') {
          page.click(found.found);
        }
      },
      // The dialog opening is the next workflow's first check.
      verify: () => OK,
    },
  ],
  fixtures: ['clip-download-menu'],
};

/** In the Download dialog: the format, then "Unlock & Download". */
export const chooseDownloadFormat: Workflow<DownloadFormatContext> = {
  id: 'choose-download-format',
  title: 'Prepare a file in the Download dialog',
  feature: 'download',
  startsOn: sunoPage('library'),
  after: 'choose-download',
  needs: [{ step: 'dialog', check: (page) => present(page, DOWNLOAD_DIALOG_TARGET) }],
  steps: [
    {
      name: 'dialog',
      expect: ({ page }) => present(page, DOWNLOAD_DIALOG_TARGET),
      act: () => undefined,
      verify: () => OK,
    },
    {
      name: 'format',
      expect: ({ page, format }) => present(page, FORMAT_CHOICES[format]),
      act: ({ page, format }) => {
        const found = page.find(FORMAT_CHOICES[format]);
        if (found.kind === 'found') {
          page.downloadDialogClick(found.found);
        }
      },
      // The dialog shows the choice by its colour only (TS-004): nothing to read back.
      verify: () => OK,
    },
    {
      name: 'download',
      expect: ({ page }) => present(page, DOWNLOAD_BUTTON),
      act: ({ page }) => {
        const found = page.find(DOWNLOAD_BUTTON);
        if (found.kind === 'found') {
          page.downloadDialogClick(found.found);
        }
      },
      // The page's own answer with the file's address is what tells it worked (downloadSteps.ts).
      verify: () => OK,
    },
  ],
  fixtures: ['download-dialog'],
};

/** The Download dialog closed, when it is still open after its file was prepared. */
export const closeDownloadDialog: Workflow = {
  id: 'close-download-dialog',
  title: 'Close the Download dialog',
  feature: 'download',
  startsOn: sunoPage('library'),
  after: 'choose-download-format',
  needs: [{ step: 'close', check: (page) => present(page, CLOSE_DOWNLOAD_DIALOG) }],
  steps: [
    {
      name: 'close',
      expect: ({ page }) => present(page, CLOSE_DOWNLOAD_DIALOG),
      act: ({ page }) => {
        const found = page.find(CLOSE_DOWNLOAD_DIALOG);
        if (found.kind === 'found') {
          page.downloadDialogClick(found.found);
        }
      },
      verify: () => OK,
    },
  ],
  fixtures: ['download-dialog'],
};
