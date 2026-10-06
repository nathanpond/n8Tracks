/**
 * The adapter's field map: what Generate on Suno does with each field of the Create form, by
 * creation mode. It declares the same entries as `docs/suno-adapter-field-map.md`, and a test
 * fails if the two differ in any entry, field, or `how`.
 *
 * - `fill`: the adapter sets the control and reads it back.
 * - `source`: set by opening a source clip or playlist and choosing a Suno action.
 * - `manual`: not set; the verification summary tells the user to set it by hand.
 */
export type How = 'fill' | 'source' | 'manual';

export interface FieldMapEntry {
  /** `<kind>.<mode>.<field>`: `songs.advanced.styles`. */
  entry: string;
  /** The field of `docs/suno-create-field-inventory.md`. */
  field: string;
  how: How;
}

export const FIELD_MAP: readonly FieldMapEntry[] = [
  { entry: 'songs.simple.model', field: 'model', how: 'fill' },
  { entry: 'songs.simple.simple_prompt', field: 'simple_prompt', how: 'fill' },
  { entry: 'songs.simple.simple_add_lyrics', field: 'simple_add_lyrics', how: 'fill' },
  { entry: 'songs.simple.simple_add_styles', field: 'simple_add_styles', how: 'fill' },
  { entry: 'songs.simple.simple_add_playlist', field: 'simple_add_playlist', how: 'source' },
  { entry: 'songs.simple.simple_add_image', field: 'simple_add_image', how: 'manual' },
  { entry: 'songs.simple.simple_add_video', field: 'simple_add_video', how: 'manual' },
  { entry: 'songs.simple.audio', field: 'audio', how: 'source' },
  { entry: 'songs.simple.voice', field: 'voice', how: 'source' },
  { entry: 'songs.simple.workspace', field: 'workspace', how: 'fill' },
  { entry: 'songs.advanced.model', field: 'model', how: 'fill' },
  { entry: 'songs.advanced.audio', field: 'audio', how: 'source' },
  { entry: 'songs.advanced.voice', field: 'voice', how: 'source' },
  { entry: 'songs.advanced.inspiration', field: 'inspiration', how: 'source' },
  { entry: 'songs.advanced.lyrics', field: 'lyrics', how: 'fill' },
  { entry: 'songs.advanced.styles', field: 'styles', how: 'fill' },
  { entry: 'songs.advanced.exclude_styles', field: 'exclude_styles', how: 'fill' },
  { entry: 'songs.advanced.vocal_gender', field: 'vocal_gender', how: 'fill' },
  { entry: 'songs.advanced.duration_mode', field: 'duration_mode', how: 'fill' },
  { entry: 'songs.advanced.duration_seconds', field: 'duration_seconds', how: 'fill' },
  { entry: 'songs.advanced.max_mode', field: 'max_mode', how: 'fill' },
  { entry: 'songs.advanced.weirdness', field: 'weirdness', how: 'fill' },
  { entry: 'songs.advanced.style_influence', field: 'style_influence', how: 'fill' },
  { entry: 'songs.advanced.variety', field: 'variety', how: 'fill' },
  { entry: 'songs.advanced.personalize', field: 'personalize', how: 'fill' },
  { entry: 'songs.advanced.title', field: 'title', how: 'fill' },
  { entry: 'speech.simple.speech_prompt', field: 'speech_prompt', how: 'fill' },
  { entry: 'speech.advanced.speech_script', field: 'speech_script', how: 'fill' },
  { entry: 'speech.advanced.speech_tone', field: 'speech_tone', how: 'fill' },
  { entry: 'speech.advanced.speech_vocal_gender', field: 'speech_vocal_gender', how: 'fill' },
  {
    entry: 'speech.advanced.speech_background_music',
    field: 'speech_background_music',
    how: 'fill',
  },
  { entry: 'speech.advanced.speech_variety', field: 'speech_variety', how: 'fill' },
  { entry: 'sounds.single.sounds_model', field: 'sounds_model', how: 'fill' },
  { entry: 'sounds.single.sound_description', field: 'sound_description', how: 'fill' },
  { entry: 'sounds.single.sound_type', field: 'sound_type', how: 'fill' },
  { entry: 'sounds.single.sound_bpm', field: 'sound_bpm', how: 'fill' },
  { entry: 'sounds.single.sound_key', field: 'sound_key', how: 'fill' },
  { entry: 'sounds.single.sound_scale', field: 'sound_scale', how: 'fill' },
];
