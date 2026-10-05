import type { CreateField, CreateFields } from '../api/createFields';
import type { VersionOptions } from '../api/versions';

const BOTH = ['simple', 'advanced'];
const ADVANCED = ['advanced'];
const SIMPLE = ['simple'];

/** A Songs field as `GET /api/v1/suno/create-fields` serves it. */
function songs(field: Omit<CreateField, 'tab' | 'help'> & { help?: string }): CreateField {
  return { tab: 'songs', help: null, ...field };
}

/**
 * The Songs fields the option controls use, as the API serves them from the field inventory
 * (`docs/suno-create-field-inventory.json`), with their options and Suno's tooltips.
 */
export const CREATE_FIELDS: CreateFields = {
  models: ['v6', 'v6-wild', 'v6-mini'],
  fields: [
    songs({
      key: 'model',
      label: 'Model version',
      modes: BOTH,
      type: 'choice',
      values: ['v6', 'v6-wild', 'v6-mini'],
      default: null,
      option: 'model',
    }),
    songs({
      key: 'simple_prompt',
      label: 'Song description',
      modes: SIMPLE,
      type: 'text',
      default: '',
      maxLength: 1000,
      option: 'simplePrompt',
    }),
    songs({
      key: 'simple_add_lyrics',
      label: 'Add: Lyrics',
      modes: SIMPLE,
      type: 'choice',
      values: ['write_new', 'use_existing'],
      option: 'simpleLyricsAdded',
    }),
    songs({
      key: 'simple_add_styles',
      label: 'Add: Styles',
      modes: SIMPLE,
      type: 'choice',
      values: ['write_new', 'use_existing'],
      option: 'simpleStylesAdded',
    }),
    songs({
      key: 'workspace',
      label: 'Save to...',
      modes: ADVANCED,
      type: 'reference',
      option: null,
    }),
    songs({
      key: 'lyrics',
      label: 'Lyrics',
      modes: ADVANCED,
      type: 'text',
      default: '',
      maxLength: 5000,
      option: null,
    }),
    songs({
      key: 'styles',
      label: 'Styles',
      modes: ADVANCED,
      type: 'text',
      default: '',
      maxLength: 1000,
      option: null,
    }),
    songs({
      key: 'exclude_styles',
      label: 'Exclude styles',
      modes: ADVANCED,
      type: 'text',
      default: '',
      maxLength: 1000,
      option: 'excludeStyles',
    }),
    songs({
      key: 'vocal_gender',
      label: 'Vocal Gender',
      modes: ADVANCED,
      type: 'choice',
      values: ['male', 'female'],
      default: null,
      option: 'vocalGender',
      help: 'Change the gender of the generated vocals',
    }),
    songs({
      key: 'duration_mode',
      label: 'Duration',
      modes: ADVANCED,
      type: 'choice',
      values: ['auto', 'custom'],
      default: 'auto',
      option: 'durationMode',
      help: 'Desired song length, 10 seconds to 6 minutes.',
    }),
    songs({
      key: 'duration_seconds',
      label: 'Duration (custom)',
      modes: ADVANCED,
      type: 'range',
      default: 180,
      min: 10,
      max: 360,
      unit: 'seconds',
      option: 'durationSeconds',
    }),
    songs({
      key: 'max_mode',
      label: 'Max Mode',
      modes: ADVANCED,
      type: 'toggle',
      default: false,
      option: 'maxMode',
      help: 'Uses more compute to maximize consistency throughout the song. Costs 2x credits per song.',
    }),
    songs({
      key: 'weirdness',
      label: 'Weirdness',
      modes: ADVANCED,
      type: 'range',
      default: 50,
      min: 0,
      max: 100,
      unit: 'percent',
      option: 'weirdness',
      help: 'Turn it up for wild, unexpected results',
    }),
    songs({
      key: 'style_influence',
      label: 'Style Influence',
      modes: ADVANCED,
      type: 'range',
      default: 50,
      min: 0,
      max: 100,
      unit: 'percent',
      option: 'styleInfluence',
      help: 'Turn it up to match your style description',
    }),
    songs({
      key: 'variety',
      label: 'Variety',
      modes: ADVANCED,
      type: 'choice',
      values: ['off', 'normal', 'high', 'extra', 'max'],
      default: 'normal',
      option: 'variety',
      help: 'At Max: Clips may differ significantly from your style input.',
    }),
    songs({
      key: 'personalize',
      label: 'Personalize (My Taste)',
      modes: ADVANCED,
      type: 'toggle',
      default: false,
      option: 'personalize',
      help: 'Make Variety match your taste',
    }),
    songs({
      key: 'title',
      label: 'Song Title',
      modes: ADVANCED,
      type: 'text',
      default: '',
      maxLength: 100,
      option: 'title',
    }),
  ],
};

/** A new Version's options, as the API gives them: an Advanced Song at Suno's defaults. */
export const DEFAULT_INPUTS: VersionOptions = {
  kind: 'song',
  songMode: 'advanced',
  speechMode: 'advanced',
  model: null,
  simplePrompt: '',
  simpleLyricsAdded: false,
  simpleStylesAdded: false,
  excludeStyles: '',
  vocalGender: null,
  durationMode: 'auto',
  durationSeconds: 180,
  maxMode: false,
  weirdness: 50,
  styleInfluence: 50,
  variety: 'normal',
  personalize: false,
  title: 'Running in a Pack',
};
