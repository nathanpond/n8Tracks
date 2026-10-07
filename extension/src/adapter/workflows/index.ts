import type { Workflow } from '../workflow.ts';
import {
  checkSongsForm,
  fillSongsAdvanced,
  fillSongsSimple,
  switchForm,
  type FillContext,
  type SwitchFormContext,
} from './fillSongs.ts';
import { checkSoundsForm, fillSounds, switchSoundsForm } from './fillSounds.ts';
import {
  checkSpeechForm,
  fillSpeechAdvanced,
  fillSpeechSimple,
  switchSpeechForm,
} from './fillSpeech.ts';
import {
  chooseDownload,
  chooseDownloadFormat,
  closeDownloadDialog,
  openClipMenu,
} from './download.ts';
import { loadMore } from './loadMore.ts';
import { recogniseSuno } from './recognise.ts';
import { refreshLibrary } from './watchCompletion.ts';
import {
  answerOverwrite,
  chooseSourceAction,
  openSourceMenu,
  verifySourceAdvanced,
  verifySourceSimple,
} from './sources.ts';
import { createWorkspace, moreWorkspaces, openWorkspaces, selectWorkspace } from './workspace.ts';

/**
 * Every workflow the adapter ships, in the order the panel lists them. A story that adds a
 * workflow adds its module under this folder and its entry here.
 */
export const ADAPTER_WORKFLOWS: readonly Workflow[] = [
  recogniseSuno,
  loadMore,
  openWorkspaces,
  moreWorkspaces,
  selectWorkspace,
  createWorkspace,
  switchForm,
  fillSongsSimple,
  fillSongsAdvanced,
  checkSongsForm,
  switchSpeechForm,
  fillSpeechSimple,
  fillSpeechAdvanced,
  checkSpeechForm,
  switchSoundsForm,
  fillSounds,
  checkSoundsForm,
  openSourceMenu,
  chooseSourceAction,
  answerOverwrite,
  verifySourceAdvanced,
  verifySourceSimple,
  refreshLibrary,
  openClipMenu,
  chooseDownload,
  chooseDownloadFormat,
  closeDownloadDialog,
];

/** The workflows that fill one kind of Version in one mode (#147). */
export interface FormWorkflows {
  /** Makes the Create form the kind's form in the mode. */
  open: Workflow<SwitchFormContext>;
  /**
   * Whether the kind loads the Version's source after `open` (#148; Songs only). `open` always runs
   * first, with or without a source: a source applies only in the mode active when its action was
   * chosen (TS-002).
   */
  loadsSources: boolean;
  fill: Workflow<FillContext>;
  /** Check again: reads every entry without changing anything. */
  check: Workflow<FillContext>;
}

/** The form workflows by `<kind>.<mode>` of the generation request. */
export const FORM_WORKFLOWS: Readonly<Record<string, FormWorkflows>> = {
  'song.simple': {
    open: switchForm,
    loadsSources: true,
    fill: fillSongsSimple,
    check: checkSongsForm,
  },
  'song.advanced': {
    open: switchForm,
    loadsSources: true,
    fill: fillSongsAdvanced,
    check: checkSongsForm,
  },
  'speech.simple': {
    open: switchSpeechForm,
    loadsSources: false,
    fill: fillSpeechSimple,
    check: checkSpeechForm,
  },
  'speech.advanced': {
    open: switchSpeechForm,
    loadsSources: false,
    fill: fillSpeechAdvanced,
    check: checkSpeechForm,
  },
  'sound.single': {
    open: switchSoundsForm,
    loadsSources: false,
    fill: fillSounds,
    check: checkSoundsForm,
  },
};

/** The workflows for a request of `kind` in `mode`, or null when the adapter has none. */
export function formWorkflowsFor(kind: string, mode: string): FormWorkflows | null {
  return Object.hasOwn(FORM_WORKFLOWS, `${kind}.${mode}`)
    ? (FORM_WORKFLOWS[`${kind}.${mode}`] ?? null)
    : null;
}
