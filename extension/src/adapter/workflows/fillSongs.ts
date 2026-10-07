import { sunoPage } from '../addresses.ts';
import { FILL_LIMIT_MS, modeTab, verifyForm, type EntryResult, type FormJob } from '../fill.ts';
import type { Page, Target } from '../primitives.ts';
import {
  LYRICS_SECTION,
  MORE_OPTIONS_SECTION,
  SONG_DESCRIPTION,
  SONGS_TAB,
  STYLES_SECTION,
} from '../songsForm.ts';
import { expected, OK, present, type Check, type StepContext, type Workflow } from '../workflow.ts';

/**
 * Filling Suno's Songs form from a generation request (#146, TS-003:
 * `page.create-songs-advanced-more-options.html`, `page.create-source-advanced.html`,
 * `page.create-source-simple.html`). The form is first made the Songs form in the Version's mode;
 * then, only when it looks as expected, every entry of the mode is filled and read back
 * (`adapter/fill.ts`). A failed entry does not stop the fill; a form that is not as expected stops
 * it before anything is filled, naming the step. Nothing here presses Create (invariant 4): the
 * user reviews the form and clicks Create.
 */

/** Making the form the Songs form in a mode. */
export interface SwitchFormContext extends StepContext {
  mode: string;
}

/** Filling or checking: what to fill from, the workspace chosen, and where the outcomes go. */
export interface FillContext extends StepContext {
  job: FormJob;
  /** The Song's workspace as the workspace step selected it, for the summary. */
  workspace: string | null;
  /** Filled in by the last step, one per entry. */
  results: EntryResult[];
}

function selected(page: Page, target: Target): Check {
  const result = page.find(target);
  return result.kind === 'found' && page.read(result.found).selected === true
    ? OK
    : expected(`${target.description}, selected`);
}

function expandedSection(page: Page, target: Target): Check {
  const result = page.find(target);
  return result.kind === 'found' && page.read(result.found).expanded === true
    ? OK
    : expected(`${target.description}, open`);
}

/** Presses a tab or a section header when it is not already selected or open. */
function pressUnless(page: Page, target: Target, done: (page: Page) => Check): void {
  if (done(page).ok) {
    return;
  }
  const result = page.find(target);
  if (result.kind === 'found') {
    page.click(result.found);
  }
}

/** The Songs form in `mode`: the Songs tab and the mode's tab both selected. */
function songsFormIn(page: Page, mode: string): Check {
  const songs = selected(page, SONGS_TAB);
  return songs.ok ? selected(page, modeTab(mode)) : songs;
}

/** Selects the Songs tab, then the Version's mode tab, as the user would. */
export const switchForm: Workflow<SwitchFormContext> = {
  id: 'switch-form',
  title: 'Open the Songs form',
  feature: 'generate',
  startsOn: sunoPage('create'),
  needs: [{ step: 'Songs tab', check: (page) => present(page, SONGS_TAB) }],
  steps: [
    {
      name: 'Songs tab',
      expect: ({ page }) => present(page, SONGS_TAB),
      act: ({ page }) => {
        pressUnless(page, SONGS_TAB, (current) => selected(current, SONGS_TAB));
      },
      verify: ({ page }) => selected(page, SONGS_TAB),
    },
    {
      name: 'form mode',
      expect: ({ page, mode }) => present(page, modeTab(mode)),
      act: ({ page, mode }) => {
        pressUnless(page, modeTab(mode), (current) => selected(current, modeTab(mode)));
      },
      verify: ({ page, mode }) => selected(page, modeTab(mode)),
    },
  ],
  fixtures: ['create-songs-advanced-more-options', 'create-source-advanced'],
};

/** The last step of a fill: every entry of the mode filled and read back. */
function fillStep(change: boolean) {
  return {
    name: change ? 'fill' : 'check',
    expect: () => OK,
    act: async ({ page, job, workspace, results }: FillContext) => {
      results.splice(0, results.length, ...(await verifyForm(page, job, workspace, change)));
    },
    verify: () => OK,
    timeoutMs: FILL_LIMIT_MS,
  };
}

/** Fills Simple mode: the model and the Song description (sections: see `fill.ts`, D9). */
export const fillSongsSimple: Workflow<FillContext> = {
  id: 'fill-songs-simple',
  title: 'Fill the Songs form (Simple)',
  feature: 'generate',
  startsOn: sunoPage('create'),
  needs: [{ step: 'Songs form', check: (page) => songsFormIn(page, 'simple') }],
  steps: [
    {
      name: 'Songs form',
      expect: ({ page }) => songsFormIn(page, 'simple'),
      act: () => undefined,
      verify: () => OK,
    },
    {
      name: 'Song description',
      expect: ({ page }) => present(page, SONG_DESCRIPTION),
      act: () => undefined,
      verify: () => OK,
    },
    fillStep(true),
  ],
  fixtures: ['create-source-simple'],
};

/**
 * Fills Advanced mode: the Lyrics, Styles, and More Options sections are opened when closed, then
 * every entry is filled.
 */
export const fillSongsAdvanced: Workflow<FillContext> = {
  id: 'fill-songs-advanced',
  title: 'Fill the Songs form (Advanced)',
  feature: 'generate',
  startsOn: sunoPage('create'),
  needs: [{ step: 'Songs form', check: (page) => songsFormIn(page, 'advanced') }],
  steps: [
    {
      name: 'Songs form',
      expect: ({ page }) => songsFormIn(page, 'advanced'),
      act: () => undefined,
      verify: () => OK,
    },
    {
      name: 'Lyrics section',
      expect: ({ page }) => present(page, LYRICS_SECTION),
      act: ({ page }) => {
        pressUnless(page, LYRICS_SECTION, (current) => expandedSection(current, LYRICS_SECTION));
      },
      verify: ({ page }) => expandedSection(page, LYRICS_SECTION),
    },
    {
      name: 'Styles section',
      expect: ({ page }) => present(page, STYLES_SECTION),
      act: ({ page }) => {
        pressUnless(page, STYLES_SECTION, (current) => expandedSection(current, STYLES_SECTION));
      },
      verify: ({ page }) => expandedSection(page, STYLES_SECTION),
    },
    {
      name: 'More Options',
      expect: ({ page }) => present(page, MORE_OPTIONS_SECTION),
      act: ({ page }) => {
        pressUnless(page, MORE_OPTIONS_SECTION, (current) =>
          expandedSection(current, MORE_OPTIONS_SECTION),
        );
      },
      verify: ({ page }) => expandedSection(page, MORE_OPTIONS_SECTION),
    },
    fillStep(true),
  ],
  fixtures: ['create-songs-advanced-more-options', 'create-source-advanced'],
};

/** Check again: every entry read without changing anything, on the Songs form in the mode. */
export const checkSongsForm: Workflow<FillContext> = {
  id: 'check-songs-form',
  title: 'Check the Songs form',
  feature: 'generate',
  startsOn: sunoPage('create'),
  needs: [{ step: 'Songs form', check: (page) => present(page, SONGS_TAB) }],
  steps: [
    {
      name: 'Songs form',
      expect: ({ page, job }) => songsFormIn(page, job.mode),
      act: () => undefined,
      verify: () => OK,
    },
    fillStep(false),
  ],
  fixtures: ['create-songs-advanced-more-options', 'create-source-advanced'],
};
