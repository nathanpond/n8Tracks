# Suno fixtures

Sanitized examples of Suno's pages and responses for adapter tests, captured in the maintainer's signed-in browser by spikes TS-001 and TS-002 (2026-10-03), TS-003 (#127) and TS-004 (#214) (2026-10-05). The findings are in `docs/spikes/`.

## Sanitizing

Every identifier is replaced with a placeholder UUID `00000000-0000-4000-8000-…`. Within one capture session the same real ID always maps to the same placeholder, across all of that session's files (TS-001/002 files use `…000000000001` upward; TS-003/004 files use `…000000000101` upward). Titles, prompts, tags, lyrics, names, handles, and descriptions are replaced with `<redacted N chars>`, one per original line so line counts are kept (TS-001/002 files say `<redacted field>` instead). Avatar addresses are `<redacted url>`. Every address has its query string removed. The Create request's `token`, `create_session_token`, and `user_tier`, and every paging cursor, are redacted.

Page snapshots keep element structure, roles, labels, test attributes, and class names. Scripts and styles are removed. User text is redacted wherever it matches captured user content (including truncated text). In the snapshots of the library, trash, playlist, workspace selector, and Voice and Inspo pickers, every text node is redacted except Suno's own labels, durations, badges, counts, and dates.

`extension/scripts/lib/check-fixtures.test.ts` (part of `npm test`) fails on any UUID that is not a placeholder and any address with a query string. Locally it also fails on any term in `docs/.notes/suno/redaction-terms.txt`, the ignored list of the maintainer's titles, names, and lyric lines. CI does not have that list and checks the first two only.

## Responses

Request shapes give the method, path, and the shape of the body; no headers were recorded.

### TS-001 / TS-002

- `generate-v2-web.response.json`: `POST /api/generate/v2-web/` when Create is clicked (two clips, just submitted). The second clip was rebuilt from the first, changing only its ID and `batch_index`.
- `feed-v3.completed-clip.response.json`: one of those clips from `POST /api/feed/v3` after it finished.
- `lineage-metadata.examples.json`: the lineage fields each remix or edit action leaves on a clip. Hand-assembled from readings, not a verbatim response.

### TS-003: Create, by mode

One pair of clips was made in each mode in a new workspace. The modes are `songs-simple`, `songs-advanced`, `speech-simple`, `speech-advanced`, and `sounds`.

- `generate-v2-web.<mode>.request.json`: the JSON body the page sent to `POST /api/generate/v2-web/` (`studio-api-prod.suno.com`) when the maintainer clicked Create.
- `generate-v2-web.<mode>.response.json`: its response: `{ id, clips[2], metadata, major_model_version, status, created_at, batch_size }`.
- `feed-v3.<mode>.response.json`: the same two clips once finished, taken from one `POST /api/feed/v3` response for the workspace (body `{ cursor: null, limit: 20, filters: { workspace: { presence: "True", workspaceId }, trashed: "False", … } }`) and split by mode. Each file is `{ clips[2], has_more: false }`.

### TS-003: lists and paging

- `feed-v3.library-page-1.request.json` / `.response.json` and `feed-v3.library-page-2.…`: two consecutive pages of `POST /api/feed/v3` from Library › Songs, requested as the list was scrolled to its end. The body is `{ cursor, limit: 20, filters: { user: { presence: "True", userId }, disliked, trashed, stem, … } }`. The response is `{ clips, next_cursor, has_more }`, and page 2's request `cursor` is page 1's `next_cursor` (both redacted here). Trimmed to two clips each.
- `feed-v3.workspace-last-page.request.json` / `.response.json`: the end of a list: `has_more: false` and no `next_cursor` (the spike workspace, 10 clips; trimmed to two).
- `clips-trashed-v2.response.json`: `GET /api/clips/trashed_v2?limit=20` from Library › Trash (`/me/trash`). The response is `{ clips, num_total_results, next_cursor }`; the next page is `?limit=20&cursor=<next_cursor>`, requested on scroll. `num_total_results` was 0 although 20 clips came back. Trimmed to two clips.
- `project-me.page-1.response.json`, `project-me.last-page.response.json`: `GET /api/project/me?page=N&sort=max_created_at_last_&show_trashed=false&exclude_shared=false`, the workspace list. Pages are numbered, 20 per page, with `num_total_results`; the next page is requested as the list is scrolled (pages 1 and 5 of 5 here, trimmed to three workspaces).
- `project.create.response.json`: `POST /api/project` from the inline "Create new workspace" row (body not captured).
- `playlist-me.response.json`: `GET /api/playlist/me?page=1&show_trashed=false&show_sharelist=false`, the playlist list, requested when the Create form's Inspo picker opens. Trimmed to two.
- `unified-feed.playlist.request.json` / `.response.json`: `POST /api/unified/feed` with `{ feed_id: "generic_playlist:<id>", cursor: null, page_size: 50, request_metadata: { sort_by: "default" } }`, a playlist's contents, requested when a playlist page opens. Songs are in `feed.items[].content_item`. No paging field appeared for an 8-song playlist. Trimmed to two items.
- `persona-get-personas.response.json`: `GET /api/persona/get-personas/?page=1`, requested when the Voice picker opens.

### TS-004: downloads

- `billing-info.download-excerpt.response.json`: the `download_usage` and `download_credit_packs` members of `GET /api/billing/info/`, requested when the Download dialog opens.
- `download-authorize.request.json` / `.response.json`: `POST /api/download/authorize` `{ item_id, item_type: "clip" }` → `{ ok, already_unlocked, credit_deducted }`, sent once when "Unlock & Download" is clicked.
- `download-clip.<wav|mp3|m4a>.processing.response.json` and `….ready.response.json`: `GET /api/download/clip/<id>?format=<wav|mp3|m4a>`, polled by the page until `status` is `ready` and `download_url` is present. The URL is signed (query removed here: `AWSAccessKeyId`, `Expires`, `Signature`, `response-content-type`).
- `download-file.<fmt>.headers.json`: the status and selected headers of a credential-less `GET` (`Range: bytes=0-0`) of each signed address. No audio is committed.

## Page snapshots

Each `page.<name>.html` is the HTML of the relevant region, taken in the maintainer's browser on suno.com.

| File | How the page was reached |
|---|---|
| `page.create-songs-simple.html` | Create › Songs › Simple, prompt filled. |
| `page.create-songs-advanced-more-options.html` | Create › Songs › Advanced, lyrics, styles, and title filled, More Options open with every control changed. |
| `page.create-speech-simple.html` | Create › Speech › Simple, prompt filled. |
| `page.create-speech-advanced.html` | Create › Speech › Advanced, Script and Tone filled, Advanced section open (Female, Background music Off, Variety High). |
| `page.create-sounds-advanced-options.html` | Create › Sounds, description filled, Advanced Options open (Loop, 120 BPM), Key picker open with A and Minor chosen. |
| `page.workspace-selector.html` | Workspaces breadcrumb above the library pane on /create: the workspace list open. Whole page body. |
| `page.create-workspace-dialog.html` | "Create new workspace" in that list: an inline row with a "New workspace name" input and Confirm. |
| `page.clip-remix-menu.html` | A clip's "More options" › Remix (both menus). |
| `page.clip-edit-menu.html` | A clip's "More options" › Edit (both menus). |
| `page.clip-download-menu.html` | A clip's "More options" with Download hovered: Download has no submenu. |
| `page.download-dialog.html` | "More options" › Download: the format dialog with "Unlock & Download" and the plan's download allowance. |
| `page.create-source-advanced.html` | Songs › Advanced, then a clip's Remix › Cover, then "Overwrite": the Audio section with the source. |
| `page.overwrite-lyrics-styles-dialog.html` | The dialog shown by Remix › Cover when the form already has lyrics and styles. |
| `page.create-source-simple.html` | Songs › Simple, then a clip's Remix › Cover: the source chip above the prompt. |
| `page.voice-picker.html` | Songs › Advanced › "+ Voice": My Voices list. |
| `page.inspo-picker.html` | Songs › Advanced (no audio source) › "+ Inspo": the playlist list. |
| `page.library-list.html` | Library (`/me`) › Songs. |
| `page.library-trash.html` | Library › the Trash button (`/me/trash`). |
| `page.playlist.html` | Library › Playlists › a playlist (`/playlist/<id>`). |
