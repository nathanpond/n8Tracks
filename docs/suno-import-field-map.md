# Suno import field map

Where each option of the Create screen (`docs/suno-create-field-inventory.json`) appears in the data Suno returns for a clip. Import reads the **feed** column; the observed-Create story reads **Create response** and, where the response lacks a value, the **Create request**. The machine-readable copy is `docs/suno-import-field-map.json`, and `extension/scripts/lib/check-fixtures.test.ts` fails if an inventory key has no entry there.

Evidence: spike TS-003 (`docs/spikes/TS-003.md`), run 2026-10-05 with one finished clip pair in each of Songs Simple, Songs Advanced (every More Options control off its default, Max Mode on), Speech Simple, Speech Advanced, and Sounds (Loop, 120 BPM, A minor). Fixtures are in `extension/fixtures/suno/`.

Paths are dot paths with `[n]` for arrays. **Feed** is one clip in `clips[]` of `POST /api/feed/v3`. **Create** is one clip in `clips[]` of `POST /api/generate/v2-web/`. **Request** is that call's JSON body. `—` means not present.

## What import cannot recover

These values are sent at Create time and never come back in the feed. Import marks them `not returned`; only an observed Create captures them.

| Field | Why |
|---|---|
| `styles`, `speech_tone` | The feed's `metadata.tags` is Suno's **rewritten** description, not the submitted text. The Create response keeps the submitted text. |
| `vocal_gender`, `speech_vocal_gender` | Only in the request (`metadata.vocal_gender`). |
| `duration_mode`, `duration_seconds` | Only in the request (`duration`, present only when Custom). The feed's `metadata.duration` is the measured length. |
| `personalize` | Only in the request (`use_personalization`). |
| `speech_background_music` | Only in the request (`metadata.backing_music`). |
| `sounds_model` | Sound clips carry no model badge, and `model_name` does not follow the selected label. |
| `simple_add_image`, `simple_add_video` | No input field appears anywhere. |

## Songs

| Field | Feed | Create | Request | Encoding | Example |
|---|---|---|---|---|---|
| `model` | `metadata.model_badges.songrow.display_name` | — | `mv` | enum | `V6-MINI` (v6, v6-wild unverified) |
| `simple_prompt` | `metadata.gpt_description_prompt` | same | `gpt_description_prompt` | text | |
| `simple_add_lyrics` | unverified | | | text | expected `metadata.prompt` |
| `simple_add_styles` | unverified | | | text | expected `metadata.tags` (rewritten) |
| `simple_add_playlist` | `metadata.playlist_id` | same | — | text | per TS-002 |
| `simple_add_image` | not returned | — | `user_uploaded_images_b64` | text | |
| `simple_add_video` | not returned | — | — | text | |
| `audio` | `metadata.task` plus the TS-002 source fields | same | `cover_clip_id`, `continue_clip_id`, … | enum | `cover` |
| `voice` | `metadata.persona_id` | same | `persona_id` | text | per TS-002 |
| `inspiration` | `metadata.playlist_clip_ids` | same | — | text | per TS-002 |
| `lyrics` | `metadata.prompt` | same | `prompt` | text | returned unchanged |
| `styles` | not returned (rewritten) | `metadata.tags` | `tags` | text | `acoustic folk, banjo` |
| `exclude_styles` | `metadata.negative_tags` | same | `negative_tags` | text | `electric guitar` |
| `vocal_gender` | not returned | — | `metadata.vocal_gender` | enum | `f` (male unverified) |
| `duration_mode` | not returned | — | presence of `duration` | enum | |
| `duration_seconds` | not returned | — | `duration` | seconds | `30` |
| `max_mode` | `metadata.is_max_mode` | same | `metadata.is_max_mode` | bool | `true` |
| `weirdness` | `metadata.control_sliders.weirdness_constraint` | same | same | percent, scale 0.01 | `0.7` for 70 |
| `style_influence` | `metadata.control_sliders.style_weight` | same | same | percent, scale 0.01 | `0.3` for 30 |
| `variety` | `metadata.control_sliders.aug_creativity` | same | same | enum 0–4 | `2` High, `4` Max |
| `personalize` | not returned | — | `use_personalization` | bool | `true` |
| `title` | `title` | `title` | `title` | text | a blank title is replaced by Suno's own |
| `workspace` | `project.id` | — | `project_id` | text | |

## Speech

| Field | Feed | Create | Request | Encoding | Example |
|---|---|---|---|---|---|
| `speech_prompt` | `metadata.gpt_description_prompt` | same | `gpt_description_prompt` | text | |
| `speech_script` | `metadata.prompt` | same | `prompt` | text | returned unchanged |
| `speech_tone` | not returned (rewritten) | `metadata.tags` | `tags` | text | `cheerful and quick` |
| `speech_vocal_gender` | not returned | — | `metadata.vocal_gender` | enum | `f` |
| `speech_background_music` | not returned | — | `metadata.backing_music` | bool | `false` |
| `speech_variety` | `metadata.control_sliders.aug_creativity` | same | same | enum 0–4 | `2`; Speech Simple sends none |

## Sounds

| Field | Feed | Create | Request | Encoding | Example |
|---|---|---|---|---|---|
| `sounds_model` | not returned | — | `mv` | enum | `chirp-goose` for v6-mini |
| `sound_description` | `metadata.tags` | same | `tags` | text | kept as submitted |
| `sound_type` | `metadata.sound_configs.user_loop` | same | same | bool | `true` for Loop |
| `sound_bpm` | `metadata.sound_configs.user_tempo` | same | same | number | `120` |
| `sound_key`, `sound_scale` | `metadata.sound_configs.user_key` | same | same | enum | `Am` = A minor |

## Telling clips apart

- **Kind:** Speech has `metadata.is_speech: true`. Sound has `metadata.task: "sound"`. A Song has neither.
- **Mode:** a Simple clip has `metadata.gpt_description_prompt` and `metadata.task: "agentic_thinking"`. An Advanced clip has neither (or a TS-002 lineage task). The request's `metadata.create_mode` (`simple` or `custom`) is never returned.
- **Measured, not submitted:** `metadata.duration`, `avg_bpm`/`min_bpm`/`max_bpm`, and `key` (for example `Ab_minor`) are Suno's analysis of the audio. They must not be imported as the user's settings.
