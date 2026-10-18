import { sunoPage } from '../addresses.ts';
import type { Page } from '../primitives.ts';
import {
  SAVE_TO_BUTTON,
  SAVE_TO_SEARCH,
  saveToRow,
  saveToRows,
  saveToShows,
} from '../songsForm.ts';
import { expected, OK, present, type Check, type StepContext, type Workflow } from '../workflow.ts';

/**
 * Create's Save to (#435): loading a source may set it to the source's workspace, and the clips
 * then go there, so after the source is verified the content script sets it back to the Song's
 * workspace when it shows another. The Save to button opens the picker; the workspace's name is
 * typed into its "Search or create..." box, as typing would (the native setter and an `input`
 * event, as `search-workspaces` types, #434), never with Enter; the one row whose name, less its
 * " (N clips)", is the workspace's exactly (white space collapsed, case kept: Suno's names may
 * differ by case alone) is pressed; the picker is closed with Escape if it is still open; and
 * Save to is read back. "+" is never pressed: it, Enter, and a "Create …" row may create a
 * workspace, and the forbidden-control matcher refuses them in this picker (`SAVE_TO_PICKER`).
 * With no such row, or more than one (#335), the step stops and the content script leaves Save to
 * to the user. The picker's markup was not seen live (`page.create-songs-advanced-save-to-picker.html`
 * is built from the owner's screenshot).
 */

/** How long the picker may take to show the workspace's row once its name is typed. */
export const SAVE_TO_ROW_WAIT_MS = 3_000;

/** How long Save to may take to show the workspace once its row is pressed. */
export const SAVE_TO_SHOWN_WAIT_MS = 3_000;

/** Setting Save to: the Song's workspace, by its name. */
export interface ChooseSaveToContext extends StepContext {
  workspaceName: string;
}

/** Whether the picker is open: its text box is on the page. */
function pickerOpen(page: Page): boolean {
  return page.find(SAVE_TO_SEARCH).kind === 'found';
}

/** The picker shows one row of exactly the workspace's name. */
function oneRow(page: Page, workspaceName: string): Check {
  const { count } = saveToRows(page, workspaceName);
  return count === 1
    ? OK
    : expected(
        count === 0
          ? 'a row in the Save to picker named exactly as the Song’s workspace'
          : 'one row in the Save to picker named as the Song’s workspace (found more than one, so none was chosen)',
      );
}

/** Sends Escape to the picker while it is open; presses nothing. */
function dismissPicker(page: Page): void {
  const box = page.find(SAVE_TO_SEARCH);
  if (box.kind === 'found') {
    page.dismiss(box.found);
  }
}

/** Sets Save to to the Song's workspace through its picker. Presses the button and one row only. */
export const chooseSaveTo: Workflow<ChooseSaveToContext> = {
  id: 'choose-save-to',
  title: 'Set Save to to the Song’s workspace',
  feature: 'generate',
  startsOn: sunoPage('create'),
  needs: [
    {
      step: 'Save to picker',
      check: (page) => (pickerOpen(page) ? OK : present(page, SAVE_TO_BUTTON)),
    },
  ],
  steps: [
    {
      name: 'Save to picker',
      expect: ({ page }) => (pickerOpen(page) ? OK : present(page, SAVE_TO_BUTTON)),
      act: ({ page }) => {
        if (pickerOpen(page)) {
          return;
        }
        const button = page.find(SAVE_TO_BUTTON);
        if (button.kind === 'found') {
          page.click(button.found);
        }
      },
      verify: ({ page }) => present(page, SAVE_TO_SEARCH),
    },
    {
      name: 'workspace name',
      expect: ({ page }) => present(page, SAVE_TO_SEARCH),
      act: ({ page, workspaceName }) => {
        const box = page.find(SAVE_TO_SEARCH);
        if (box.kind === 'found' && page.read(box.found).value !== workspaceName) {
          page.set(box.found, workspaceName);
        }
      },
      verify: ({ page, workspaceName }) => {
        const box = page.find(SAVE_TO_SEARCH);
        return box.kind === 'found' && page.read(box.found).value === workspaceName
          ? OK
          : expected(`${SAVE_TO_SEARCH.description} to hold the Song’s workspace’s name`);
      },
    },
    {
      name: 'workspace row',
      expect: ({ page, workspaceName }) => oneRow(page, workspaceName),
      act: ({ page, workspaceName }) => {
        const row = page.find(saveToRow(workspaceName, saveToRows(page, workspaceName).role));
        if (row.kind === 'found') {
          page.click(row.found);
        }
      },
      verify: () => OK,
      timeoutMs: SAVE_TO_ROW_WAIT_MS,
    },
    {
      name: 'Save to',
      expect: () => OK,
      act: ({ page }) => {
        dismissPicker(page);
      },
      verify: ({ page, workspaceName }) =>
        saveToShows(page, workspaceName)
          ? OK
          : expected(`${SAVE_TO_BUTTON.description} to show the Song’s workspace`),
      timeoutMs: SAVE_TO_SHOWN_WAIT_MS,
    },
  ],
  fixtures: ['create-songs-advanced-save-to-picker'],
};

/** Closes the Save to picker with Escape when it is open, as after a choice left to the user. */
export const closeSaveTo: Workflow = {
  id: 'close-save-to',
  title: 'Close the Save to picker',
  feature: 'generate',
  startsOn: sunoPage('create'),
  needs: [
    {
      step: 'picker closed',
      check: (page) => (pickerOpen(page) ? present(page, SAVE_TO_SEARCH) : OK),
    },
  ],
  steps: [
    {
      name: 'picker closed',
      expect: () => OK,
      act: ({ page }) => {
        dismissPicker(page);
      },
      verify: ({ page }) => (pickerOpen(page) ? expected('the Save to picker, closed') : OK),
      timeoutMs: SAVE_TO_SHOWN_WAIT_MS,
    },
  ],
  fixtures: ['create-source-advanced'],
};
