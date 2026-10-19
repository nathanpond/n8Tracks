# Suno fixtures

Sanitized examples of Suno's pages and responses for adapter tests, captured in the maintainer's signed-in browser by spikes TS-001 and TS-002 (2026-10-03), TS-003 (#127) and TS-004 (#214) (2026-10-05), TS-005 (2026-10-08, for #146, #147, #148), TS-006 (2026-10-08, the model menu, for #146 and #147), and TS-007 (2026-10-08, Suno's `Request`-object fetches and the Library pager, for #134 and #149), with sanitized additions for #422 (2026-10-11, a workspace's songs on `/create?wid=`). The findings are in `docs/spikes/`.

## Sanitizing

Every identifier is replaced with a placeholder UUID `00000000-0000-4000-8000-…`. Within one capture session the same real ID always maps to the same placeholder, across all of that session's files (TS-001/002 files use `…000000000001` upward; TS-003/004 files use `…000000000101` upward; TS-005 files use `…000000000201` upward; TS-006 files use `…000000000301` upward, though its two snapshots hold no identifier; TS-007 files use `…000000000401` upward). Titles, prompts, tags, lyrics, names, handles, and descriptions are replaced with `<redacted N chars>`, one per original line so line counts are kept (TS-001/002 files say `<redacted field>` instead). Avatar addresses are `<redacted url>`. Every address has its query string removed. The Create request's `token`, `create_session_token`, and `user_tier`, and every paging cursor, are redacted.

Page snapshots keep element structure, roles, labels, test attributes, and class names. Scripts and styles are removed. User text is redacted wherever it matches captured user content (including truncated text). In the snapshots of the library, trash, playlist, workspace selector, and Voice and Inspo pickers, every text node is redacted except Suno's own labels, durations, badges, counts, and dates. The TS-005, TS-006, and TS-007 snapshots are redacted that way throughout, text nodes and the `aria-label`, `title`, `alt`, `placeholder`, and `value` attributes alike: what is not one of Suno's own labels is `<redacted N chars>`, and a label built around user text keeps Suno's words only (`Play "<redacted 13 chars>"`, `Cover art for <redacted 13 chars>`, `Remove <redacted 16 chars>`). Text the maintainer typed during the capture (lyrics, styles, the prompt) is redacted too. A profile link is `/@<redacted>` and a style link `/style/<redacted>`.

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

### TS-007: Request-object fetches and the pager

- `feed-v3.workspace-create-wid.request.json`: the body of `POST /api/feed/v3` for a workspace's songs on `/create?wid=<workspace ID>`, where opening a workspace from Library › Workspaces now leads: `{ cursor: null, limit: 20, filters: { workspace: { presence: "True", workspaceId }, disliked, trashed, stem, … } }`. Suno's page sent it as `fetch(request, { headers })`, the body on the `Request`.
- `feed-v3-offset.response.json`: `POST /api/feed/v3/offset` (`{ offset, filters }`, body not recorded) → `{ offset, clip_id }`, sent by the Library pager's › before the next `POST /api/feed/v3`. Not a page of the list.

### #422: a workspace's songs on `/create?wid=`

Sanitized on 2026-10-11 from the TS-007 captures of `/create?wid=<workspace ID>` (the workspace keeps TS-007's `…427`; the clips of page 1 are the library snapshots' `…401` and `…402`; other identifiers continue from `…428`). Trimmed to two clips a page.

- `feed-v3.workspace-create-wid.page-1.response.json`: the answer to `feed-v3.workspace-create-wid.request.json` (`cursor: null`): `{ clips, next_cursor, has_more: true }`. The clips' `project.id` is the workspace.
- `feed-v3.workspace-create-wid.page-2.request.json` / `.response.json`: the next page, asked for with page 1's `next_cursor` (redacted), the same `workspace` filter, and `has_more: true`. Seen live on 2026-10-11: scrolling the song list asks for each next page until `has_more: false` (pages of 20, 20, and 5 for 45 clips); the last page is not captured, and the reader's tests set `has_more: false` on a copy.

### TS-007-15 / #434: the workspace list's search box (constructed, not captured)

- `project-me.search.response.json`: **constructed, not a capture.** What Generate on Suno takes after typing a name into the workspace list's search box (`input[aria-label="Search workspaces"]`, already in `page.workspace-selector.html`). It is shaped as a `GET /api/project/me` page (`num_total_results`, `current_page`, `projects`), on the assumption that Suno searches on the server; that, the query parameter it sends, and whether it filters the rows it holds instead, need live confirmation. Two workspaces of the same redacted name, as a search by name would find, and made-up IDs. The extension takes any workspace-list answer that follows the typing, so a different shape of answer only finds fewer workspaces.

### TS-007-16 / #435: Create's Save to picker (constructed, not captured)

- `page.create-songs-advanced-save-to-picker.html`: **constructed, not a capture.** `page.create-source-advanced.html` (a Cover on the Advanced form) with its Save to button showing "My Workspace" and open (`aria-expanded`, `aria-controls`), and a Save to picker added at the end of the body as Base UI renders a popover: a portal holding an untitled `role="dialog"` with an `input` whose placeholder is "Search or create...", an icon-only "+" `button` with no name, and a list of `button` rows, each a name `span` and a count `span` ("My Workspace (81 clips)", "Practice Room Sketches (18 clips)", "Country Farming (137 clips)", "Sound test (2 clips)"). Built from the owner's screenshot of 2026-10-17; the names are generic and no identifier was added. The picker's roles, the rows' role, how "+" is named, and what follows a choice need live confirmation.

### TS-007-17, TS-007-18 / #438: the "Add a song to mashup" dialog (constructed from the live markup)

- `page.create-source-mashup-add-song-dialog.html`: **constructed from the owner's console capture of 2026-10-19 (TS-007-18), not a snapshot.** `page.create-source-mashup-one-song.html` (a Mashup with its first song) with Suno's "Add a song to mashup" dialog added at the end of the body as Base UI renders a modal: a portal holding a `role="dialog"` whose `aria-labelledby` points to a `div` with the text "Add a song to mashup" (no heading), a Close `button` (`aria-label="Close"`), a `tablist` with the tabs "My Library" (selected) and "Everything", an `input` named "Search clips" (`aria-label`, placeholder "Search for a song in your library..."), buttons "(3)", "New", and "Filter to show only liked clips", and a `role="rowgroup"` of five rows. Each row is a `div[role=button]` named "<title>. Double-tap or press Enter to add to mashup.", holding a play `div[role=button]` "Play <title>" with an `img` "<title> artwork" (`image_<ID>.jpeg`), `<a href="/song/<ID>">` with the title, an "Edit title" button, the badges `V6` and a kind (`MASHUP`, `COVER`, or none), a redacted description, "Like <title>", and "Add <title> to mashup" (its text "Select"). The titles are made up (the owner's own titles are redaction terms), and the IDs are placeholders (`…0401` to `…0405`). Two rows are "The Long Thaw" (`…0404` and `…0405`), as Suno makes two clips per Create, so only a row's link tells them apart. Suno's class names are invented.
- `page.create-source-mashup-add-song-search.html`: **constructed.** The same after typing "Long Thaw": three rows, the two "The Long Thaw" among them. Whether Suno filters locally or asks the server, and what follows an Add, need live confirmation.

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
| `page.create-songs-advanced-save-to-picker.html` | Constructed (#435): `page.create-source-advanced.html` with the Save to picker open (see above). |
| `page.overwrite-lyrics-styles-dialog.html` | The dialog shown by Remix › Cover when the form already has lyrics and styles. |
| `page.create-source-simple.html` | Songs › Simple, then a clip's Remix › Cover: the source chip above the prompt. |
| `page.voice-picker.html` | Songs › Advanced › "+ Voice": My Voices list. |
| `page.inspo-picker.html` | Songs › Advanced (no audio source) › "+ Inspo": the playlist list. |
| `page.library-list.html` | Library (`/me`) › Songs. |
| `page.library-trash.html` | Library › the Trash button (`/me/trash`). |
| `page.playlist.html` | Library › Playlists › a playlist (`/playlist/<id>`). |
| `page.create-songs-simple-add-menu.html` | TS-005. Create › Songs › Simple, the "+" (Add) menu open: Lyrics, Styles, Playlist, Image, Video, Audio, Voice. The form and the menu. |
| `page.create-songs-simple-lyrics-submenu.html` | TS-005. + › Lyrics: the submenu, "Write new" and "Use existing". |
| `page.create-songs-simple-lyrics-dialog.html` | TS-005. + › Lyrics › Write new: the "Lyrics" dialog (the Lexical editor, two lines typed; its Close; the Cowriter). |
| `page.create-songs-simple-with-lyrics.html` | TS-005. The Lyrics dialog closed: the lyrics chip above the Song description. |
| `page.create-songs-simple-styles-submenu.html` | TS-005. With lyrics, + › Styles: the submenu. |
| `page.create-songs-simple-styles-dialog.html` | TS-005. + › Styles › Write new: the "Styles" dialog with a style typed. Closing it showed Suno's toast "Prompt saved." (not in the snapshot). |
| `page.create-songs-simple-with-lyrics-and-styles.html` | TS-005. Both dialogs closed: the lyrics and styles chips. |
| `page.create-songs-advanced-duration-auto.html` | TS-005. Create › Songs › Advanced, More Options open, Duration on Auto: Custom and Auto buttons. |
| `page.create-songs-advanced-duration-custom.html` | TS-005. The same after Custom: the Duration slider at 3:00 and its text box. |
| `page.create-sounds-key-any.html` | TS-005. Create › Sounds, Advanced Options open, Key Any (popover closed). |
| `page.create-sounds-key-popover-fsharp-minor.html` | TS-005. The Key popover open (untitled dialog): notes C to B with sharps, Any, Major/Minor, Apply; F# and Minor chosen. |
| `page.create-sounds-key-applied-fsharp-minor.html` | TS-005. After Apply: the Key button reads "F# min". |
| `page.clip-page.html` | TS-005. A clip's own page (`/song/<id>`, clip `…204`): its header (cover image, "More options" closed) and one row of the list below it (that page has twelve "More options"). |
| `page.clip-page-remix-menu.html` | TS-005. The same, More options › Remix open (Cover, Reuse Prompt, Mashup, Sample this song, Use as Inspiration, Voice disabled). |
| `page.clip-not-found.html` | TS-005. `/song/<an ID that does not exist>`: Suno's 404, "Page not found". A clip in the Trash shows its page as any other (not committed). |
| `page.create-source-extend.html` | TS-005. Songs › Advanced after a clip's Edit › Extend (clip `…201`): the Audio section with Keep/Recreate and "Extend from 00:54.0". |
| `page.create-source-sample.html` | TS-005. After Remix › Sample this song: the Audio section, its Selection 00:00.0–01:00.0. |
| `page.create-source-mashup-one-song.html` | TS-005. After Remix › Mashup: "Mashup songs. 1 of 2 songs selected." and "Add another song to Mashup". |
| `page.create-source-mashup-add-song-dialog.html` | Constructed (#438, TS-007-18): the same with "Add another song to Mashup" pressed: the "Add a song to mashup" dialog as the owner's console capture shows it (see above). |
| `page.create-source-mashup-add-song-search.html` | Constructed (#438, TS-007-18): the dialog after a search, three rows, two of one title. |
| `page.create-source-inspo-one-song.html` | TS-005. After Remix › Use as Inspiration: the Audio section named Inspo, one song, "(1/4)". |
| `page.overwrite-styles-dialog.html` | TS-005. Remix › Cover onto a form with lyrics and styles, from an instrumental clip: "Overwrite Styles?" with Overwrite and Keep Current (Suno hides the form, `aria-hidden`, while it is open). |
| `page.voice-picker-with-source.html` | TS-005. Songs › Advanced with an Inspo source, + Voice: the Voice picker (two voices; persona `…203` is the second). |
| `page.create-voice-selected.html` | TS-005. A voice chosen by pressing its title: the voice row on the form, a link to `/voice/<persona ID>`, and "Remove selected Voice". |
| `page.create-songs-model-menu.html` | TS-006. Create › Songs › Advanced, the model button ("v6") pressed: the model menu open, labelled by the button. "Create Custom Model" (a `menuitem`, Beta, 100 credits), then v6 (checked), v6-wild, and v6-mini (`menuitemradio`). The form and the menu. |
| `page.create-sounds-model-menu.html` | TS-006. Create › Sounds, the same model button pressed: the same menu, v6 checked. |
| `page.create-songs-advanced-model-dialog.html` | #427 (TS-007-10). Create › Songs › Advanced with the Model dialog open. Not a capture: built on 2026-10-11 from `page.create-songs-model-menu.html` and the structure seen live that day (no snapshot was taken). The model button (still "v6", at the same place) has `aria-haspopup="dialog"`, and the TS-006 menu's portal is replaced by a portal holding `div[role=dialog]`, labelled by its `h2` "Model", with a `div[role=grid]` of `div[role=row]`s: "Create Custom Model" (Beta, its description), the "All Models" header, then v6 (`aria-selected="true"`), v6-wild, and v6-mini, each starting with a `span` of its name, then a badge and a description. The rows hold no buttons. Suno's class names in the dialog are invented. No identifier. |
| `page.library-songs-page-1.html` | TS-007. Library › Songs (`/me`) on its first page: the list, then the new pager below it, `Page`, ‹ (disabled), a `#` box reading 1, and ›. The two buttons have no name, only a chevron icon. |
| `page.library-songs-page-2.html` | TS-007. The same after › was pressed once: the box reads 2, ‹ is enabled, a third icon-only button follows ›, and "Showing one page of 20 songs" sits above the pager. |
| `page.create-workspace-songs.html` | #422. The Create page with its song list (the right-hand pane: one `rowgroup` of `clip-row`s inside `.clip-browser-list-scroller`) beside Suno's Create form (Create song, Publish clip on each row). Sanitized on 2026-10-11 from the TS-006 capture of Create › Songs › Advanced, the whole page body, with the model menu's portal (and the players, frames, and overlays outside the app) removed and the model button shown closed; placeholders from `…301` (TS-006's range, unused by its other snapshots). This is the page a workspace opens on (`/create?wid=<id>`, TS-007); `/create?wid=` itself was not snapshotted, and its header's ‹ 1 › control (seen live on 2026-10-11) is not in this capture. |
