import type {
  Verification,
  VerificationEntry,
  VerificationOutcome,
  VerificationValue,
} from '../api/generationRequests';

/**
 * How the Version page words the extension's verification summary of Suno's filled Create form
 * (#146, #147 Speech and Sounds): each entry's field, its outcome, and what was expected and found. Text values arrive only
 * as their length and hash, so text is told by its length, and two texts of the same length by
 * whether their hashes match.
 */

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

/** How the summary's heading names the form's mode: Simple, Advanced, or (Sounds' one form) Sounds. */
export function modeLabel(mode: string): string {
  return mode === 'simple' ? 'Simple' : mode === 'single' ? 'Sounds' : 'Advanced';
}

export const OUTCOME_LABELS: Readonly<Record<VerificationOutcome, string>> = {
  set: 'Set',
  failed: 'Differs',
  unavailable: 'Unavailable',
  manual: 'To do by hand',
  not_applicable: 'Not applicable',
  unsupported: 'Unsupported',
};

const OUTCOME_ORDER: readonly VerificationOutcome[] = [
  'set',
  'failed',
  'unavailable',
  'manual',
  'not_applicable',
  'unsupported',
];

const VARIETY_STEPS = ['off', 'normal', 'high', 'extra', 'max'];

/** The field an entry names: `songs.advanced.weirdness` → "Weirdness"; an unknown key as it is. */
export function entryLabel(key: string): string {
  const field = key.split('.').at(-1) ?? key;
  return FIELD_LABELS[field] ?? key;
}

function isHashed(
  value: VerificationValue | undefined,
): value is { length: number; sha256: string } {
  return typeof value === 'object' && value !== null;
}

/** A value as the page shows it: text by its length, a toggle On or Off, Variety by its step. */
export function valueText(key: string, value: VerificationValue | undefined): string {
  if (value === undefined || value === null) {
    return 'none';
  }
  if (isHashed(value)) {
    return value.length === 0 ? 'empty' : `${String(value.length)} characters`;
  }
  if (typeof value === 'boolean') {
    return value ? 'On' : 'Off';
  }
  if (/[._]variety$/.test(key) && typeof value === 'number') {
    return VARIETY_STEPS[value] ?? String(value);
  }
  return String(value);
}

/** What the page says beside an entry's outcome. */
export function entryDetail(entry: VerificationEntry): string {
  const parts: string[] = [];
  if (entry.outcome === 'failed' && entry.expected !== undefined) {
    const expected = valueText(entry.key, entry.expected);
    const found = valueText(entry.key, entry.found);
    parts.push(
      isHashed(entry.expected) && isHashed(entry.found) && expected === found
        ? 'expected the Version’s text, found other text of the same length'
        : `expected ${expected}, found ${found}`,
    );
  }
  if (entry.note !== undefined && entry.note !== '') {
    parts.push(entry.note);
  }
  return parts.join('. ');
}

/** "12 set, 1 differs": the outcomes that occur, in a fixed order. */
export function countsText(verification: Verification): string {
  return OUTCOME_ORDER.flatMap((outcome) => {
    const count = verification.entries.filter((entry) => entry.outcome === outcome).length;
    return count === 0 ? [] : [`${String(count)} ${OUTCOME_LABELS[outcome].toLowerCase()}`];
  }).join(', ');
}

/** Whether anything needs the user before Create: an entry that differs, is unavailable, or is by hand. */
export function needsAttention(verification: Verification): boolean {
  return verification.entries.some(
    (entry) =>
      entry.outcome === 'failed' || entry.outcome === 'unavailable' || entry.outcome === 'manual',
  );
}
