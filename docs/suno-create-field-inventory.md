# Suno Create-screen field inventory

This is the list of options on Suno's Create screen that an n8Tracks Version must be able to store. It is the measure for the roadmap claim "Mirror every available Suno field". A machine-readable copy is in `suno-create-field-inventory.json`; tests enumerate that file.

- **Captured:** 2026-10-03, from `https://suno.com/create`.
- **Method:** Read from the live Create screen through the maintainer's signed-in browser session (DOM attributes, screenshots, tooltips). No generation was started.
- **Interface version:** Not shown on the page; models offered were v6, v6-wild, and v6-mini.

The Create screen has three tabs. Songs and Speech each have a Simple and an Advanced mode; Sounds has one form. The PRD names Simple, Advanced, and Sounds; **Speech is a tab the PRD does not mention; the maintainer has confirmed it is in scope for V1.**

## Songs

23 fields.

| Key | Label | Modes | Type | Values or range | Default | Condition | Notes |
|---|---|---|---|---|---|---|---|
| `model` | Model version | simple, advanced | choice | v6, v6-wild, v6-mini | none | — | Plan: v6 Pro, v6-wild Pro, v6-mini free. Dropdown also offers 'Create Custom Model (Beta)', built from the user's uploads for 100 credits; a custom model, once created, is presumably selectable here (not verified). Default not determined: the form remembers the last choice. |
| `simple_prompt` | Song description | simple | text | up to 1000 characters | empty | — | Single free-text prompt. Suno enforces no limit in the form; n8Tracks uses 1,000 characters for now (maintainer's decision). |
| `simple_add_lyrics` | Add: Lyrics | simple | choice | write_new, use_existing | none | Opened from the + menu in the prompt box | Adds a lyrics input to the Simple form. |
| `simple_add_styles` | Add: Styles | simple | choice | write_new, use_existing | none | Opened from the + menu in the prompt box | — |
| `simple_add_playlist` | Add: Playlist | simple | reference | — | none | + menu | Playlist as inspiration; mechanics belong to TS-002. |
| `simple_add_image` | Add: Image | simple | file | — | none | + menu or the Image button | 'Add an image to your creation.' Not offered in Advanced mode. |
| `simple_add_video` | Add: Video | simple | file | — | none | + menu | Not offered in Advanced mode. |
| `audio` | Audio | simple, advanced | reference | browse, upload, record | none | — | 'Add audio - Browse, upload, or record audio'. Mechanics belong to TS-002. |
| `voice` | Voice | simple, advanced | reference | — | none | — | 'Add Voice'. Sub-options not determined. Mechanics belong to TS-002. |
| `inspiration` | Inspo | advanced | reference | — | none | — | 'Add inspiration from a playlist'. Mechanics belong to TS-002. |
| `lyrics` | Lyrics | advanced | text | up to 5000 characters | empty | — | Rich text box; empty means instrumental ('leave this empty for instrumental'). Has undo, redo, new draft, saved lyrics, full-screen editor, and an AI 'Help me write lyrics' cowriter. Suno's limit is 5,000 characters (confirmed by the maintainer). |
| `styles` | Styles | advanced | text | up to 1000 characters | empty | — | Free text; suggestion chips append to it; saved style prompts can be loaded; a 'personalize to your taste' toggle rewrites suggestions. |
| `exclude_styles` | Exclude styles | advanced | text | up to 1000 characters | empty | Inside More Options | — |
| `vocal_gender` | Vocal Gender | advanced | choice | male, female | none | Inside More Options | Neither is selected by default. Tooltip: 'Change the gender of the generated vocals'. |
| `duration_mode` | Duration | advanced | choice | auto, custom | auto | Inside More Options | Tooltip: 'Desired song length, 10 seconds to 6 minutes.' |
| `duration_seconds` | Duration (custom) | advanced | range | 10 to 360 seconds | 180 | duration_mode = custom | — |
| `max_mode` | Max Mode | advanced | toggle | — | off | Inside More Options | Tooltip: 'Uses more compute to maximize consistency throughout the song. Costs 2x credits per song.' |
| `weirdness` | Weirdness | advanced | range | 0 to 100 percent | 50 | Inside More Options | Tooltip: 'Turn it up for wild, unexpected results'. |
| `style_influence` | Style Influence | advanced | range | 0 to 100 percent | 50 | Inside More Options | Tooltip: 'Turn it up to match your style description'. |
| `variety` | Variety | advanced | choice | off, normal, high, extra, max | normal | Inside More Options | A five-step slider (0 to 4). Tooltip at Max: 'Clips may differ significantly from your style input.' Default taken from the untouched Speech form; the Songs form remembers the last value. |
| `personalize` | Personalize (My Taste) | advanced | toggle | — | off | Inside More Options | Tooltip: 'Make Variety match your taste'. |
| `title` | Song Title | advanced | text | up to 100 characters | empty | — | Optional. Suno enforces no limit in the form; n8Tracks uses 100 characters for now (maintainer's decision). |
| `workspace` | Save to... | advanced | reference | — | none | — | Workspace the result is saved to; shows the current workspace. Not shown in Simple mode. |

## Speech (beta)

6 fields.

| Key | Label | Modes | Type | Values or range | Default | Condition | Notes |
|---|---|---|---|---|---|---|---|
| `speech_prompt` | Speech description | simple | text | up to 1000 characters | empty | — | Single free-text prompt. Speech is labelled BETA and has no model dropdown. Suno enforces no limit in the form; n8Tracks uses 1,000 characters for now (maintainer's decision). |
| `speech_script` | Script | advanced | text | up to 5000 characters | empty | — | — |
| `speech_tone` | Tone | advanced | text | up to 1000 characters | empty | — | 'Describe the delivery: tone, pacing, mood and setting'. |
| `speech_vocal_gender` | Vocal Gender | advanced | choice | male, female | none | Inside Advanced | — |
| `speech_background_music` | Background music | advanced | toggle | — | on | Inside Advanced | — |
| `speech_variety` | Variety | advanced | choice | off, normal, high, extra, max | normal | Inside Advanced | — |

## Sounds

6 fields.

| Key | Label | Modes | Type | Values or range | Default | Condition | Notes |
|---|---|---|---|---|---|---|---|
| `sounds_model` | Model version | single | choice | v6, v6-wild, v6-mini | none | — | Same dropdown as Songs. Sounds has no Simple/Advanced switch. |
| `sound_description` | Sound | single | text | up to 500 characters | empty | — | 'Describe the sound you want'. Sounds are short samples: effects, loops, and one-shots. |
| `sound_type` | Type | single | choice | one_shot, loop | one_shot | Inside Advanced Options | — |
| `sound_bpm` | BPM | single | number | 1 to 300 | none | Inside Advanced Options | Empty means Auto. |
| `sound_key` | Key | single | choice | any, C, C#, D, D#, E, F, F#, G, G#, A, A#, B | any | Inside Advanced Options | Combined with a scale choice. |
| `sound_scale` | Key scale | single | choice | major, minor | none | A key other than Any is chosen | — |

## Not determined

These could not be read from the page and are left for spike TS-002 or for execution to probe:

- Sub-options of the Voice input, and the full flows for Audio, Voice, Inspo, Playlist, Image, and Video inputs (covered by spike TS-002).
- Whether Max Mode, Personalize, Custom Models, Voice, and Inspo require a paid plan beyond the Pro badges shown on v6 and v6-wild.
- Default model for a new account (the form remembers the last choice).
