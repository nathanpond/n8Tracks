import { sunoPage } from '../addresses.ts';
import type { Page } from '../primitives.ts';
import { ADD_VOICE, VOICE_DIALOG } from '../sources.ts';
import { expected, OK, present, type Check, type Workflow } from '../workflow.ts';
import { VOICE_CLOSE } from './sources.ts';

/**
 * The sync's voice list (#439). On Create (Songs, Advanced: `switch-form` runs first), "+ Voice"
 * opens Suno's Voice picker, and opening it makes the page ask Suno for the user's voices
 * (`GET /api/persona/get-personas/?page=1`), which the observer forwards and the library reader
 * takes. The picker is then closed with its own Close, else with Escape, choosing nothing. Nothing
 * here presses a voice, Create Voice, or anything that creates, publishes, or deletes (invariant 4):
 * the forbidden-control matcher allows only Close, My Voices, Favorites, and a voice's title in
 * the picker, and refuses "Create Voice" by its name; these workflows press only "+ Voice" and
 * Close.
 */

function pickerOpen(page: Page): boolean {
  return page.find(VOICE_DIALOG).kind === 'found';
}

/** The picker is open, or "+ Voice" is there to open it. */
function canOpen(page: Page): Check {
  return pickerOpen(page) ? OK : present(page, ADD_VOICE);
}

/**
 * Opens the Voice picker with "+ Voice" (a picker already open is left as it is). Whether Suno sent
 * the voice list is the library reader's to judge: it waits for the answer, and when none comes the
 * sync goes on without the voices.
 */
export const openVoiceList: Workflow = {
  id: 'open-voice-list',
  title: 'Open the Voice picker for your voices',
  feature: 'sync',
  startsOn: sunoPage('create'),
  // The sync opens Songs › Advanced first, where "+ Voice" is.
  after: 'switch-form',
  needs: [{ step: 'voice picker', check: canOpen }],
  steps: [
    {
      name: 'voice picker',
      expect: ({ page }) => canOpen(page),
      act: ({ page }) => {
        if (pickerOpen(page)) {
          return;
        }
        const button = page.find(ADD_VOICE);
        if (button.kind === 'found') {
          page.click(button.found);
        }
      },
      verify: ({ page }) => (pickerOpen(page) ? OK : expected(VOICE_DIALOG.description)),
    },
  ],
  fixtures: ['voice-picker-with-source'],
};

/**
 * Asks for the next page of the voice list as a user would: every region of the page that scrolls
 * is scrolled to its end, the picker's list among them. It presses nothing. Suno may load a next
 * page only on the user's own scroll, as it does the workspace list (TS-007-15); the reader then
 * reports the list as read so far, incomplete.
 */
export const scrollVoiceList: Workflow = {
  id: 'scroll-voice-list',
  title: 'Scroll the Voice picker',
  feature: 'sync',
  startsOn: sunoPage('create'),
  // The picker is open only once `open-voice-list` opened it.
  after: 'open-voice-list',
  needs: [{ step: 'voice list', check: (page) => present(page, VOICE_DIALOG) }],
  steps: [
    {
      name: 'voice list',
      expect: ({ page }) => present(page, VOICE_DIALOG),
      act: ({ page }) => {
        page.scrollToEnd();
      },
      verify: () => OK,
    },
  ],
  fixtures: ['voice-picker-with-source'],
};

/**
 * Closes the Voice picker without choosing: its Close, else Escape. A picker already closed is left
 * as it is.
 */
export const closeVoiceList: Workflow = {
  id: 'close-voice-list',
  title: 'Close the Voice picker after reading your voices',
  feature: 'sync',
  startsOn: sunoPage('create'),
  needs: [{ step: 'picker closed', check: () => OK }],
  steps: [
    {
      name: 'picker closed',
      expect: () => OK,
      act: ({ page }) => {
        const dialog = page.find(VOICE_DIALOG);
        if (dialog.kind !== 'found') {
          return;
        }
        const close = page.find(VOICE_CLOSE);
        if (close.kind === 'found') {
          page.click(close.found);
        } else {
          page.dismiss(dialog.found);
        }
      },
      verify: ({ page }) => (pickerOpen(page) ? expected('the Voice picker, closed') : OK),
      timeoutMs: 3_000,
    },
  ],
  fixtures: ['create-voice-selected', 'create-songs-advanced-more-options'],
};
