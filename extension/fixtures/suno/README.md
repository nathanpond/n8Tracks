# Suno fixtures

Sanitized examples of Suno's responses, captured during spike TS-001 on 2026-10-03 for adapter tests.

- `generate-v2-web.response.json`: the response to `POST /api/generate/v2-web/` when Create is clicked (two clips, just submitted).
- `lineage-metadata.examples.json`: from spike TS-002, the lineage-related fields each remix or edit action leaves on a clip. This file is hand-assembled from field-by-field readings, not a verbatim response.
- `feed-v3.completed-clip.response.json`: one of those clips as returned by `POST /api/feed/v3` after it finished.

Sanitizing: every identifier is replaced with a placeholder UUID (the same real ID always maps to the same placeholder across both files); titles, prompts, tags, names, handles, and avatar URLs are replaced with `<redacted ...>`; URL query strings are removed. Field names, structure, and non-identifying values are as Suno returned them. The second clip in the generate fixture was rebuilt from the first, changing only its ID and `batch_index`, which is how the real response differed. The completed clip's `action_config` was not re-read.
