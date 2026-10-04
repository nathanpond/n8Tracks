# Suno adapter field map

This is the list the roadmap claim "Populate every supported generation property from the n8Tracks Version" is measured against. One entry is one field of `docs/suno-create-field-inventory.md` in one creation mode. The extension's adapter declares the same entries in code (`extension/src/adapter/fieldMap.ts`), and a test fails if the two differ.

**How** says what Generate on Suno does with the entry:

- `fill`: the adapter sets the control and reads it back. A mismatch is reported as failed in the verification summary.
- `source`: set by opening a source clip or playlist and choosing a Suno action (see `docs/spikes/TS-002.md`), then verified by the source shown on the Create form.
- `manual`: the adapter does not set it. The verification summary tells the user to do it by hand before clicking Create.

38 entries.

## Songs, Simple mode (10)

| Entry | Field | How | Notes |
|---|---|---|---|
| `songs.simple.model` | `model` | fill | — |
| `songs.simple.simple_prompt` | `simple_prompt` | fill | — |
| `songs.simple.simple_add_lyrics` | `simple_add_lyrics` | fill | Adds the lyrics section and fills it from the Version's lyrics when the Version has the section added. |
| `songs.simple.simple_add_styles` | `simple_add_styles` | fill | Adds the styles section and fills it from the Version's styles when the Version has the section added. |
| `songs.simple.simple_add_playlist` | `simple_add_playlist` | source | Inspiration from a playlist. |
| `songs.simple.simple_add_image` | `simple_add_image` | manual | File input. |
| `songs.simple.simple_add_video` | `simple_add_video` | manual | File input. |
| `songs.simple.audio` | `audio` | source | A source that is an existing Suno clip. An uploaded or recorded file is `manual`. |
| `songs.simple.voice` | `voice` | source | `manual` when the persona cannot be selected reliably (TS-002). |
| `songs.simple.workspace` | `workspace` | fill | Chosen with Suno's workspace selector before any field is filled. |

## Songs, Advanced mode (16)

| Entry | Field | How | Notes |
|---|---|---|---|
| `songs.advanced.model` | `model` | fill | — |
| `songs.advanced.audio` | `audio` | source | As in Simple mode. |
| `songs.advanced.voice` | `voice` | source | As in Simple mode. |
| `songs.advanced.inspiration` | `inspiration` | source | Up to four songs, or one playlist. |
| `songs.advanced.lyrics` | `lyrics` | fill | Empty means instrumental. |
| `songs.advanced.styles` | `styles` | fill | — |
| `songs.advanced.exclude_styles` | `exclude_styles` | fill | Inside More Options. |
| `songs.advanced.vocal_gender` | `vocal_gender` | fill | None means neither is selected. |
| `songs.advanced.duration_mode` | `duration_mode` | fill | — |
| `songs.advanced.duration_seconds` | `duration_seconds` | fill | Only when Duration is Custom. |
| `songs.advanced.max_mode` | `max_mode` | fill | — |
| `songs.advanced.weirdness` | `weirdness` | fill | — |
| `songs.advanced.style_influence` | `style_influence` | fill | — |
| `songs.advanced.variety` | `variety` | fill | — |
| `songs.advanced.personalize` | `personalize` | fill | — |
| `songs.advanced.title` | `title` | fill | — |

The workspace is chosen once for a Song in either mode; it is listed under Simple mode and applies to both.

## Speech (6)

| Entry | Field | How | Notes |
|---|---|---|---|
| `speech.simple.speech_prompt` | `speech_prompt` | fill | — |
| `speech.advanced.speech_script` | `speech_script` | fill | — |
| `speech.advanced.speech_tone` | `speech_tone` | fill | — |
| `speech.advanced.speech_vocal_gender` | `speech_vocal_gender` | fill | — |
| `speech.advanced.speech_background_music` | `speech_background_music` | fill | — |
| `speech.advanced.speech_variety` | `speech_variety` | fill | — |

## Sounds (6)

| Entry | Field | How | Notes |
|---|---|---|---|
| `sounds.single.sounds_model` | `sounds_model` | fill | — |
| `sounds.single.sound_description` | `sound_description` | fill | — |
| `sounds.single.sound_type` | `sound_type` | fill | — |
| `sounds.single.sound_bpm` | `sound_bpm` | fill | Empty means Auto. |
| `sounds.single.sound_key` | `sound_key` | fill | — |
| `sounds.single.sound_scale` | `sound_scale` | fill | Only when a key other than Any is chosen. |

## Outside the map

A Version value with no entry here is reported in the verification summary as unsupported. Today that is: the Suno actions n8Tracks does not automate (Crop, Remove Section, Reverse, Speed, Fades, Add Vocal, Replace Section, Stems, Remaster, Add stem).
