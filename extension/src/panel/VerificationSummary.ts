import {
  characterCount,
  VARIETY_STEPS,
  type EntryOutcome,
  type EntryResult,
  type FormValue,
} from '../adapter/fill.ts';

/**
 * The verification summary in the panel (#146): every entry of the Version's mode as set, differs
 * (with what was expected and found), unavailable, to do by hand, not applicable, or unsupported,
 * and the reminder that the user reviews the form and clicks Create. Plain DOM in the panel's
 * shadow root; Check again re-reads the form without changing it.
 */

/** How each entry's field is named on Suno's form. */
const FIELD_LABELS: Readonly<Record<string, string>> = {
  model: 'Model',
  simple_prompt: 'Song description',
  simple_add_lyrics: 'Lyrics section',
  simple_add_styles: 'Styles section',
  simple_add_playlist: 'Inspiration playlist',
  simple_add_image: 'Image',
  simple_add_video: 'Video',
  audio: 'Audio',
  voice: 'Voice',
  workspace: 'Workspace',
  inspiration: 'Inspo',
  lyrics: 'Lyrics',
  styles: 'Styles',
  exclude_styles: 'Exclude styles',
  vocal_gender: 'Vocal Gender',
  duration_mode: 'Duration',
  duration_seconds: 'Duration (seconds)',
  max_mode: 'Max Mode',
  weirdness: 'Weirdness',
  style_influence: 'Style Influence',
  variety: 'Variety',
  personalize: 'Personalize',
  title: 'Song Title',
  speech_prompt: 'Speech description',
  speech_script: 'Script',
  speech_tone: 'Tone',
  speech_vocal_gender: 'Vocal Gender',
  speech_background_music: 'Background music',
  speech_variety: 'Variety',
  sounds_model: 'Model',
  sound_description: 'Sound',
  sound_type: 'Type',
  sound_bpm: 'BPM',
  sound_key: 'Key',
  sound_scale: 'Key scale',
};

/** How the summary's heading names the form: "Songs, Advanced", "Speech, Simple", "Sounds". */
export function formName(kind: string, mode: string): string {
  if (kind === 'sound') {
    return 'Sounds';
  }
  return `${kind === 'speech' ? 'Speech' : 'Songs'}, ${mode === 'simple' ? 'Simple' : 'Advanced'}`;
}

export const OUTCOME_LABELS: Readonly<Record<EntryOutcome, string>> = {
  set: 'Set',
  failed: 'Differs',
  unavailable: 'Unavailable',
  manual: 'To do by hand',
  not_applicable: 'Not applicable',
  unsupported: 'Unsupported',
};

/** The order the counts are given in. */
const OUTCOMES: readonly EntryOutcome[] = [
  'set',
  'failed',
  'unavailable',
  'manual',
  'not_applicable',
  'unsupported',
];

export const REVIEW_TEXT =
  'Review the form in Suno, then click Create yourself. The extension does not click Create.';

/** The field an entry key names: `songs.advanced.weirdness` → "Weirdness". */
export function entryLabel(key: string): string {
  const field = key.split('.').at(-1) ?? key;
  return FIELD_LABELS[field] ?? key;
}

/** A value as the summary shows it: text by its length, a toggle as On or Off. */
export function valueText(result: EntryResult, value: FormValue | undefined): string {
  if (value === undefined || value === null) {
    return 'none';
  }
  if (result.text === true) {
    const length = characterCount(String(value));
    return length === 0 ? 'empty' : `${String(length)} characters`;
  }
  if (typeof value === 'boolean') {
    return value ? 'On' : 'Off';
  }
  if (/[._]variety$/.test(result.key) && typeof value === 'number') {
    return VARIETY_STEPS[value] ?? String(value);
  }
  return String(value);
}

/** What the summary says of an entry beside its outcome. */
export function entryDetail(result: EntryResult): string {
  const parts: string[] = [];
  if (result.outcome === 'failed' && result.expected !== undefined) {
    const sameLength =
      result.text === true &&
      valueText(result, result.expected) === valueText(result, result.found);
    parts.push(
      sameLength
        ? `expected the Version’s text, found other text of the same length`
        : `expected ${valueText(result, result.expected)}, found ${valueText(result, result.found)}`,
    );
  }
  if (result.note !== undefined) {
    parts.push(result.note);
  }
  return parts.join('. ');
}

/** "12 set, 1 differs, 2 to do by hand": the outcomes that occur, in a fixed order. */
export function countsText(results: readonly EntryResult[]): string {
  return OUTCOMES.flatMap((outcome) => {
    const count = results.filter((result) => result.outcome === outcome).length;
    return count === 0 ? [] : [`${String(count)} ${OUTCOME_LABELS[outcome].toLowerCase()}`];
  }).join(', ');
}

export interface VerificationSummaryOptions {
  /** Check again: re-read every entry without changing anything. */
  checkAgain(): void;
}

/**
 * Builds the summary in `page` (the panel's document) for `results` of a Version of `kind` (a
 * Song unless given) in `mode`.
 */
export function verificationSummary(
  page: Document,
  mode: string,
  results: readonly EntryResult[],
  checkedAt: Date,
  options: VerificationSummaryOptions,
  kind = 'song',
): { element: HTMLElement; firstControl: HTMLButtonElement } {
  const make = <K extends keyof HTMLElementTagNameMap>(
    tag: K,
    attributes: Record<string, string> = {},
    text?: string,
  ): HTMLElementTagNameMap[K] => {
    const element = page.createElement(tag);
    for (const [name, value] of Object.entries(attributes)) {
      element.setAttribute(name, value);
    }
    if (text !== undefined) {
      element.textContent = text;
    }
    return element;
  };

  const element = make('section', {
    class: 'verification',
    'aria-labelledby': 'n8-verification-title',
  });
  element.append(
    make('h4', { id: 'n8-verification-title' }, `Verification: ${formName(kind, mode)}`),
    make('p', { role: 'status', class: 'verification-review' }, REVIEW_TEXT),
    make('p', { class: 'detail verification-counts' }, countsText(results)),
  );
  const list = make('ul', { class: 'verification-entries' });
  for (const result of results) {
    const item = make('li', { 'data-outcome': result.outcome, 'data-key': result.key });
    const detail = entryDetail(result);
    item.append(
      make('span', { class: 'verification-entry' }, entryLabel(result.key)),
      ': ',
      make('strong', { class: 'verification-outcome' }, OUTCOME_LABELS[result.outcome]),
      ...(detail === '' ? [] : [' — ', make('span', { class: 'detail' }, detail)]),
    );
    list.append(item);
  }
  const checkAgain = make('button', { type: 'button', class: 'verification-check' }, 'Check again');
  checkAgain.setAttribute('aria-describedby', 'n8-verification-checked');
  checkAgain.addEventListener('click', () => {
    checkAgain.disabled = true;
    options.checkAgain();
  });
  element.append(
    list,
    make(
      'p',
      { class: 'detail', id: 'n8-verification-checked' },
      `Checked at ${checkedAt.toLocaleTimeString()}. Check again reads the form without changing it.`,
    ),
    checkAgain,
  );
  return { element, firstControl: checkAgain };
}
