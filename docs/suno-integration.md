# Suno integration design

Shared design the M4 stories refer to as "the Suno integration design". A story's own acceptance criteria and discretion lines take precedence where they differ. Evidence for Suno's behaviour is in `docs/spikes/TS-001.md` through `TS-004.md`, `docs/suno-import-field-map.md`, and the fixtures under `extension/fixtures/suno/`.

## Terms

- **Clip**: one Suno output, identified by its Suno ID. A **Generation** is the n8Tracks record for one clip.
- **Workspace**: what Suno's data calls `project`.
- **Export**: a set of Suno records the extension sends to n8Tracks for review. Receiving an export changes nothing in the catalog.

## Boundaries

- The n8Tracks server never contacts Suno. Every Suno record, image, and status arrives through the extension. Suno URLs are stored as text and never fetched by the server.
- The extension never constructs its own authenticated request to Suno and never reads Suno cookies or authorization headers. It reads Suno's data by observing the responses to the page's own requests (a wrapper around `fetch` in the page, as spike TS-001 did) and causes those requests by operating the page (navigating, scrolling, opening menus).
- Two exceptions to observing:
  - A clip's cover image is read with a plain request that carries no cookies or authorization, sent only to hosts the adapter lists as Suno image hosts.
  - An audio address is handed to the browser's downloads interface, which fetches it with no credential added by the extension. The address is either one the page's own traffic exposed (a signed `download_url` from `GET /api/download/clip/<id>?format=…`, valid for one hour) or a clip's `media_urls` playback address. It is used only when it is on a listed Suno audio host: `suno-data-uploads.s3.amazonaws.com` and `d2lwuy8qc234o3.cloudfront.net` (spike TS-004). Only the download queue (`extension/src/download/downloader.ts`, #216) uses the downloads interface, and it passes only the address, the name, `conflictAction: "uniquify"`, and `saveAs: false`: the invariant 4 guard's static scan checks the call.
- The extension changes Suno in exactly two ways:
  - It may create a workspace.
  - It may unlock a clip for download by clicking the Download dialog's "Unlock & Download". This spends one of the user's plan downloads, and only after the user has confirmed, before the run, how many unlocks the run uses against the allowance remaining. It never buys download packs. (Maintainer's decision on TS-004, 2026-10-05.)
- It never clicks Create, Publish, Delete, Trash, or Remove controls (invariant 4).
- Filling lyrics into the Create form makes Suno save a draft in the user's Saved lyrics (`POST /api/lyrics-projects`, spike TS-003). This is Suno's own behaviour, not an extension action.
- Nothing reaches the catalog from a library export until the signed-in user confirms it on the review page (invariant 3). The extension's token can stage an export; it cannot commit one.

## Scopes

Two scopes join the list from #56:

- `suno.sync`: create and upload an export, upload Generation artwork for it, read the state of exports this credential created, report workspace discovery, and look up which Suno clips are Generations for the Download view (#215).
- `suno.generate`: claim and read a pending generation request, report its progress, report Suno's workspace list as it reads it, record the workspace the user chose for the Song, and report an observed Create and its finished clips (which attaches Generations to the requested Version, or to a new child Version when the submitted inputs differ) with their artwork.

Committing an import, resolving diffs, managing the ignore list, deleting Generations, and reassigning workspaces are session-only. Rating, commenting, archiving, and selecting use `generations.evaluate`.

## Data on a Generation

A Generation (the minimal record from #69) gains:

- `sunoId` (unique among live Generations; null for a seeded test Generation).
- `providerStatus`: `submitted`, `streaming`, `complete`, or `error`, as Suno reported.
- `remoteState`: `present`, `trashed`, or `missing`.
- `state`: `active` or `archived` (the user-facing state).
- Normalized fields for lists and filters: Suno title, duration in seconds, reported model (`major_model_version` and `model_name`, as returned, never the form label), tags as returned, minimum/maximum/average BPM, key, Suno's `created_at`, the Suno page address (`https://suno.com/song/<id>`), audio and image addresses as returned, workspace ID, and `batchIndex`.
- The latest raw clip JSON, kept whole in `provider_records` (Suno ID, kind, payload, captured time, export). Earlier payloads are replaced, not kept. Raw payloads are never logged.
- An optional Generation Event: `generation_events` (provider request ID, source `observed`, `inferred`, or `user`, confidence `high` or `medium`, batch size, time). Events are internal: no screen or public response names them.

## Workspaces

Workspaces are kept in `suno_workspaces`, one record per Suno workspace ID (#129). Each record holds the latest name and description, Available or Unavailable, when the workspace was first and last seen, and the raw project object. Records are never deleted in V1.

- **Discovery:** `PUT /api/v1/suno/workspaces/discovered` (`suno.sync` or `suno.generate`) takes `{ complete, workspaces: [<raw project>] }`. A sync reports the list it reads, and so does Generate on Suno whenever it has read Suno's complete list (#145). It reads `id`, `name`, `description`, and `is_trashed`.
  - An entry without an `id` refuses the whole body with 422.
  - If an ID is repeated, the last entry wins.
  - A blank name never overwrites a known one.
- **Availability:** only a complete list changes it. A known workspace that the list leaves out, or that it reports as trashed, becomes Unavailable. A workspace listed untrashed becomes Available again. Nothing about any Song changes.
- **New workspaces:** a workspace first seen is Available, unless it is first seen trashed. Imports record the workspaces their clips name as an incomplete list.
- **Listing:** `GET /api/v1/suno/workspaces` (`catalog.read`) lists every workspace by name, with blank names shown as "(unnamed)", plus its live Song count.
- **A Song's workspace:** a Song lives in at most one workspace, always named by Suno ID. Its `sunoWorkspace` is `{ id, name, state }` or null.
  - It is changed with the Song's PATCH (`sunoWorkspaceId`, `songs.write`) under the Song's revision. Only an Available workspace can be chosen. Resending the Unavailable workspace a Song already has counts as unchanged.
  - It is not a creation input and never freezes. A Version's `effectiveInputs.workspace` reports it for Generate on Suno.
- **Bulk move:** `POST /api/v1/suno/workspaces/{id}/move-songs` (session only) takes `{ songIds | all, targetWorkspaceId }`.
  - It moves at most 5,000 Songs, all or nothing, and raises each moved Song's revision.
  - The target must be another workspace, and Available.
  - Errors are `too_many_songs` and `song_not_in_workspace`.
- **A workspace's Songs:** `GET /api/v1/songs?workspace=<Suno ID>` lists only the Songs in that workspace, whether it is Available or not. A blank or unknown ID is 400 `invalid_request`.
- **Settings → Suno workspaces** (`/settings/suno-workspaces`) lists the workspaces with their state and Song counts. An Unavailable workspace shows the date it was last seen. Each workspace opens its own page (`/settings/suno-workspaces/<Suno ID>`), which lists its Songs and moves some or all of them with the bulk move, after a confirmation that states how many Songs will move. The "Workspace unavailable" badge on a Song links to that page.

## Export format

JSON, `format: "n8tracks.suno-export"`, `formatVersion: 1`:

```
{ format, formatVersion, extensionVersion, adapterVersion, capturedAt,
  scope: { kind: "library" | "workspaces" | "playlists" | "clips", ids: [] },
  libraryComplete: bool, trashedComplete: bool,
  clips: [ <raw clip> ], trashedClips: [ <raw clip> ],
  workspaces: [ <raw project> ], workspacesComplete: bool,
  playlists: [ { id, name, clipIds: [] } ] }
```

Raw objects are sent as Suno returned them. It is uploaded in parts: `POST /api/v1/suno/exports` (the header fields, without clips; returns an ID), `POST /api/v1/suno/exports/{id}/parts` (`{ partNumber, clips, trashedClips, playlists? }`), `POST /api/v1/suno/exports/{id}/complete`. An export moves through `receiving → classifying → ready → committing → committed`, or `discarded`, `failed`, or `expired` (seven days after it became ready). An unknown `formatVersion` is refused with `unsupported_format` (#131).

- **Limits:** 200 clips and 20 MB per part (413 `export_too_large`), 50,000 clips per export. Parts come in any order; a repeated `partNumber` replaces the earlier part. A clip without a string `id` refuses the whole part with 422 `invalid_export`, which names the field.
- **Staging:** an export is held in `suno_exports`, `suno_export_parts`, `suno_export_records` (one per Suno ID), and `suno_export_record_playlists`. None of these is a catalog table. When a Suno ID appears more than once, the copy from the Trash list wins; within one list, the last copy wins. Playlists from the header and the parts are merged.
- **Classification:** it runs at completion, inline for up to 2,000 clips and as the `suno-export-classify` job above that. Each record is `new`, `linked`, `changed`, `conflict`, `ignored`, or `deleted`, by Suno ID alone. A live Generation decides first, then a provider tombstone, then the ignore list (`suno_ignored_items`). Receiving and classifying change nothing in the catalog. A linked clip is `conflict` when its mapped creation inputs differ from its Version's (below); Suno's title and options Suno does not return take no part. A complete workspace list (`workspacesComplete`) updates the workspaces' names and availability at completion.
- **Mapping a clip's inputs (#135):** `ClipInputMapper` reads a clip's kind, mode, lyrics, and options by `docs/suno-import-field-map.json` (each entry's feed path and encoding), embedded in the build. An option the map marks `notReturned` holds the n8Tracks default and is listed in the Version's `imported.notReturned`. A value outside n8Tracks' limits is kept as Suno returned it and listed in `imported.outOfRange`, with an unknown choice kept in `imported.rawValues`: nothing is refused or cut. The model is the model-list entry whose reported-as name (`suno_models.reported_as`), or else whose name, matches the clip's badge, ignoring case. An unknown model is only proposed, and a commit adds it as a discovered entry. Text is compared with line endings and trailing whitespace normalised.
- **Speech and Sounds (#136):** the clip's kind comes from the map's `kindMarkers`: `metadata.is_speech: true` for a Speech, `metadata.task: "sound"` for a Sound, and a Song when neither is present. Only the fields of the kind's own tab are read; the others stay in the raw clip. A Speech with a description prompt but no Simple marker is Advanced when its script is not blank. A Sound's key and scale come from the one `user_key` (`Am` is A minor, read through each entry's `pattern`); no key is Any, with the scale unset. When the markers conflict or are unrecognised, the clip is imported as a Song and its staged record carries `flags: ["unknown_kind"]` for the review. The flag does not block Confirm.
- **Proposals (#138):** classification also proposes, in the same job, where each record goes, and stores it on the record as `proposal` (`{ choice, basis, group, freezesVersion }`). The record's `choice` starts as the proposed choice.
  - **Grouping:** clips Suno made in one Create request are grouped by TS-001's rule: the same workspace, `created_at` within one second of the group's earliest clip, and distinct `batch_index` values that include 0. A clip that fits no group stands alone. Timestamps alone never group. `group` is a number shared by the clips of one group; the review shows groups only as clips proposed for the same target.
  - **What is proposed:** each group's new clips are split by inputs (the mapped options and the lineage). Each set is proposed for one of these targets, the first that applies:
    1. A group-mate that is already a Generation: its Version when that Version holds the same inputs, or else a new Version of its Song.
    2. The one live Song associated with the workspace: the Song's Version with the same inputs (one with Generations first, then the lowest number), or else a new top-level Version of that Song, numbered by #61.
    3. Otherwise, a new Song per Create request, titled with the first clip's Suno title ("Untitled" when it is empty) and associated with the workspace. Each further set of inputs becomes a new Version of that Song.

    Ignored, deleted and already-linked records are proposed `skip`.
  - **Choices:** a choice is `{ action: skip | ignore }` or `{ action: import, target }`. The target is one of these:
    - `{ kind: newSong, key, title, workspaceId }`
    - `{ kind: newVersion, key, song, parentVersion, number }`
    - `{ kind: version, version }`

    New targets are named by temporary keys (`new:<n>`), so several records can share one. They are created and numbered only at the commit.
  - **Changing choices:** `PATCH /api/v1/suno/exports/{id}/records` (session only, If-Match on the export's `revision`, ready exports only) takes `{ sunoIds, choice }`, for at most 1,000 records. The server checks every changed record:
    - A clip goes to an existing Version only when its inputs are that Version's.
    - A new Version's number must be one #61 allows for its parent, or, at the top level, a number above every top-level number the Song has used.
    - All records sharing a key must describe the same target, with the same inputs.

    Any refusal refuses the whole change with 422 `invalid_choices`, which gives the reasons for each record. A change raises the export's revision. Proposals and choices never change the catalog; the commit (#140) checks every choice again.
- **One under review:** completing an export discards any earlier export under review, whoever created it. An export being committed makes a new one wait with 409 `import_in_progress`. `POST .../discard` discards an export. Its optional body says why (#229): `{ reason: "cancelled" }` when the user cancels the sync, or `{ reason: "failed", step }` when the extension stops at a step (the step as the panel names it, at most 200 characters). A tab closed or taken off Suno sends no body. n8Tracks records `replaced` for an export a newer one replaced and `abandoned` for one never completed. A failed sync (one discarded as failed, or one whose classification failed on the server, at step `classifying`) is shown on the dashboard and the Suno import page until a later export becomes ready.
- **Expiry:** the daily retention job's second step expires a ready export after seven days. It discards an export still receiving after 24 hours, and removes a committed export's staged rows after 24 hours.
- **Reading:** `GET /api/v1/suno/exports/{id}` returns the state and the counts per class. `GET .../records` lists the records, filtered by `class`, `workspace`, and `playlist`, and paged (`page`, `pageSize`, at most 200). The records list is session-only. A credential reaches only the exports it created.
- **Cover images:** `PUT /api/v1/suno/exports/{id}/artwork/{sunoId}` stages a cover image with a record of a ready export. The image is checked like any artwork, and the commit gives it to the Generation.
- **Commit (#140):** `POST /api/v1/suno/exports/{id}/commit` (session-only, If-Match on the export's revision, no body) moves a ready export to `committing` and queues the `suno-import-commit` job, which applies the choices saved on it and then marks it `committed` (once; 409 `export_committed` after, `import_in_progress` during).
    - Each target is one transaction: a new Song with its Version 1 and Generations, a new Version, or the Generations attached to an existing Version. It is created whole or not at all; a failing target is reported and undoes nothing else.
    - Every choice is checked again. A record a Generation holds now is reported linked and skipped; a clip goes to an existing Version only while its inputs are still that Version's (`inputs_differ` otherwise); a taken Version number becomes the next valid one (`number_taken`).
    - New Versions hold the clip's mapped inputs and its lineage (#137), with sources already imported linked; once attached, references to the clip elsewhere resolve to it. New Songs get the clip's workspace and no Artist.
    - A Reimport restores the deleted Generation from retention while its group (a Generation deleted alone) is retained; otherwise it attaches afresh. Its tombstone goes either way.
    - Afterwards: one Generation Event per group of two or more clips attached, then staged cover images, which never replace an image a Generation already has.
    - The job's result lists each record's `outcome` (`created`, `linked`, `skipped`, `ignored`, `failed`) and `reason`, what was created, and the Songs. A commit interrupted by a restart returns the export to `ready`, reclassified.
- **Ignore list (#143):** `suno_ignored_items` holds one entry per Suno ID: Suno's title and workspace, when it was ignored, its status as last seen (`present`, `trashed`, `missing`, `not_seen`), and when a sync last included it.
    - Only a commit adds to it: each Don't copy record, in its own transaction (idempotent; a record linked or deleted since the review is reported so and not added). Don't copy for a `deleted` record is refused with `tombstoned`, and a change by filter to Don't copy passes over deleted records.
    - Importing an ignored record removes its entry in the transaction that imports it. Skip leaves it on the list. Suno's changes, its Trash included, never remove an entry.
    - At each commit, the entries the export contains take its title, workspace, and status (`trashed` or `present`) and are last seen at the export's capture time. A whole-library sync (scope `library`, `libraryComplete`) marks the other entries `missing` when `trashedComplete`, else `not_seen`. Nothing changes at upload.
    - `GET /api/v1/suno/ignored` (session only) lists it newest ignored first, 50 to a page, with `q` (a case-insensitive substring of the title or the start of the Suno ID), `workspace`, `status`, and `page`, plus the workspaces of the whole list. A tombstoned Suno ID is never listed.
    - `POST /api/v1/suno/ignored/remove` (session only) takes `{ sunoIds }` (1 to 1,000), skips unknown IDs, and answers `{ removed, unknown }`. Nothing is imported; a ready export's records are reclassified, so a removed one is `new` there. More than 1,000 is 422 `too_many_items`.
    - The web screen is Ignored Suno items, at `/suno/ignored`, in the sidebar after Suno import.

## Generate on Suno

- **A request (#144):** Generate on Suno on a Version, mutable or frozen, makes a request in `suno_generation_requests`. The request holds a snapshot of what the Version is to be generated with. It is not a Generation: making one attaches nothing, freezes nothing, and changes nothing else in the catalog.
- **Endpoints:**
  - `POST /api/v1/versions/{reference}/generation-requests` (`versions.write`) makes the request.
  - `GET /api/v1/versions/{reference}/generation-request` (`catalog.read`) answers the newest request, without its snapshot.
  - `GET /api/v1/suno/generation-requests/{id}` (`suno.generate`) answers it with its snapshot.
  - `POST .../claim` (`suno.generate`) lets the extension take the request; a session gets 403 `credential_required`. The claim binds the request to that credential, and only it may report.
  - `PATCH` (`suno.generate`, no If-Match) takes the claimer's progress, `{ state: opening | workspace | filling | waiting | done | stopped, step, message }`; a stop says why. It may carry `verification`, the summary of the filled form (#146; below). It may also carry `resolvedWorkspace: { sunoId, name, how: created | picked }`, the workspace the user chose for the Song in the extension's panel (#145). It becomes the Song's workspace in the same transaction, but only when the Song has none or its workspace is Unavailable; otherwise the answer is 409 `workspace_already_set` and nothing changes. Sending the Song's own workspace again changes nothing, so a report sent again after a lost answer is harmless. A workspace n8Tracks has not seen is recorded as Available. An Unavailable one cannot be chosen (422).
  - `POST .../observed-create` (`suno.generate`, the claimer only) records the user's own Create click (#149; below).
  - `POST .../cancel` (`versions.write`) cancels it.
  - Refusals: 409 `request_ended` out of a terminal state, 409 `request_claimed` for another credential's claim (403 on a report), 409 `request_not_claimed` for a report before the claim.
- **Snapshot:** `{ schemaVersion: 1, kind, mode, entries: [{ key, value }], sources, fileInputs, workspace: { sunoId, name, state } | null, song: { title, shortcode }, version: { shortcode }, unsupported }`. It is built from the same `effectiveInputs` the Version answer shows.
  - Each entry is keyed by its adapter field-map entry (`docs/suno-adapter-field-map.md`). A value with no entry is listed under `unsupported`.
  - In Simple mode, the added lyrics and styles are the value of `songs.simple.simple_add_lyrics` and `songs.simple.simple_add_styles` (null when not added).
  - Each source carries its entry, group, position, Suno action, target (`{ kind, id, sunoId }`), title, shortcode, and availability.
  - The snapshot is on the redaction list.
- **Blocking:** a source the Version needs that is deleted, in Suno's Trash, or Remote Missing blocks the request with 422 `sources_unavailable`. The answer names each source and gives `lastSyncAt`, the capture time of the last committed export (Trash and missing are as of then). A Suno clip never imported does not block, and neither does a file input.
- **States:** `pending`, `claimed`, `opening`, `workspace`, `filling`, and `waiting` are active; `done`, `stopped`, `cancelled`, and `expired` are terminal. One request per Version is active: a new one cancels the active one as replaced. A request is settled whenever it is read:
  - Unclaimed 15 seconds after it was made, it is `stopped` with "The extension did not respond".
  - With no report for an hour, it is `expired`.
  - When the Version's effective content has changed since it was made, it is `cancelled` and the user is told to start again. A name, notes, the Song's workspace, or a source's availability do not count.
- **The Song's workspace (#145):** after the claim, the extension reuses the most recently active suno.com tab in the n8Tracks tab's window, or opens a new tab beside it, on /create. Before each step it reads the request and stops if it is no longer active, then reports the step. The steps are a fixed list: `open Suno`, `check sign-in`, `read workspace list`, `select workspace`, `choose workspace`, `create workspace`, and `workspace selected`.
  - **Not signed in:** the extension stops and says so. There is no snapshot of a signed-out page, so being signed in is told by Suno's profile menu, and also by the Create page loading at all.
  - **The workspace list:** it opens the list with the "Workspaces" breadcrumb (never the workspace-name breadcrumb, which renames) and reads it to its end (TS-003 paging). It then reports the complete list, which keeps n8Tracks' record current and marks a workspace Suno no longer lists Unavailable. A list that does not load stops the request without marking anything.
  - **Selecting:** when Suno's list holds the Song's workspace, the extension selects it by its row. Suno's rows carry no ID, so the row is found by the name Suno's list gives for that ID, and two rows that match are never chosen between. The page must then load that workspace's songs.
  - **Choosing:** otherwise the panel offers to create a workspace named with the Song's title, or to use an existing one (same-name ones with no Song first, then the rest with their Song counts). Nothing is created until the user chooses, and the Version page shows "Waiting for you in Suno". Creating goes through Suno's own inline row ("Create new workspace", the name, Confirm), which is invariant 4's one permitted change. The new workspace's ID is read from Suno's answer to `POST /api/project`. The choice goes to n8Tracks as `resolvedWorkspace`; the report is retried, but the creation never is.
- **Filling the form (#146):** with the workspace selected, the extension makes the form the Songs form in the Version's mode (Songs tab, then Simple or Advanced, before any source is loaded, since a source applies only in the mode chosen before its action). It then fills every `fill` entry of the mode (`docs/suno-adapter-field-map.md`), one filler per entry (`extension/src/adapter/fill.ts`). The steps continue the fixed list: `open Songs form` and `fill form` (state `filling`), then `review and create` (state `waiting`), and `check form` for Check again.
  - **Each entry** is set only when the control differs, as the user would set it: the native setter for text boxes, arrow keys for sliders (Duration by 5 seconds, to the nearest step), the Off/On or Male/Female buttons, the model menu, and the browser's editing commands for the Lexical lyrics editor. It is then read back for up to three times 300 ms. A value that does not show is `failed` (with what was expected and found), and the next entry is still filled. A control that is absent or disabled is `unavailable`. A form that is not as expected (no Songs or mode tab, a section that will not open) stops the request before anything is filled, naming the step.
  - **Outcomes:** `set`, `verified` (a source loaded and seen on the form, #148), `failed`, `unavailable`, `manual` (file inputs with the Version's note, and sources the extension does not load), `not_applicable`, and `unsupported` (a Version value with no map entry). The workspace entry is `set` by the workspace step.
  - **Not set yet (D9):** Simple mode's Add Lyrics and Add Styles sections and Duration's Auto or Custom mode have no TS-003 page snapshot, so the adapter does not set them. The summary tells the user to do them by hand until the owner captures those page states (#146).
  - **The summary** is shown in the panel with Check again, which reads every entry without changing anything; it says to review the form and click Create, which the extension never does (invariant 4). It is reported on the PATCH as `verification: { adapterVersion, mode, checkedAt, entries: [{ key, outcome, expected, found, note }] }`. Text values (lyrics, styles, prompts, titles) are sent only as `{ length, sha256 }` of the normalised text. Each summary replaces the last; it is stored in `suno_generation_requests.verification_json` and answered on both GETs. The Version page shows it below the request's state.
- **The user's Create (#149):** while the request waits, the page observer in the Suno tab passes on Suno's answer to `POST /api/generate/v2-web/` (the user's own click; the extension never clicks Create) and the request values at the import field map's `createRequest` paths plus `metadata.create_mode`, never `token`, `create_session_token`, `user_tier`, or an uploaded image (`adapter/observed.ts` `CREATE_REQUEST_PATHS`). An answer without its request ID and clips sends nothing: the panel and the request (step `record Create`, still waiting) say the clips were not recorded and that a sync will bring them in. Otherwise the service worker posts `{ response, request }` to `observed-create` (sent again while n8Tracks cannot be reached).
  - **Mapping:** each option is taken from the response where Suno echoes it (`paths.create`), else from the request (`paths.createRequest`; `absentInCreateRequest: "default"` makes an absent Vocal Gender none and an absent `duration` Auto, and `presentInCreateRequest` makes a sent `duration` Custom), else from the requested Version, listed as assumed. The model is always assumed: the response has no badge and `mv` names Suno's engine. A value the map cannot decode is assumed too. Without the request body, the request-only options are assumed and the result says the request was not read.
  - **Attach or branch:** what was submitted is compared with the requested Version as import compares a clip (options, then sources by Suno ID). The same: the clips are attached to it as Generations (Generating), which freezes it. Different: a new child Version (the first free child number) holds what was submitted, with the note "Created from what was submitted to Suno", becomes the Song's current Version, and takes the clips; the requested Version is unchanged, mutable or frozen. A later Create with the same changed form goes to that Version. One Generation Event (`observed`, `high`, Suno's request ID, never answered) links the clips. A clip a live Generation holds or one deleted from n8Tracks is skipped and reported. Branch, attach, event, and result are one transaction; the same Create sent again answers what it came to. 409 `create_not_recorded` when the clips cannot be attached (nothing stored).
  - **The request** stays `waiting` with step `Create recorded` and `observed: [{ observedAt, outcome: attached | branched | none, version, differing, assumed, requestRead, generations, skipped }]` (`suno_generation_requests.observed_json`). It is `done` when the tab leaves the Create page or 30 minutes after the last Create. After a recorded Create a load of the tab never fills the form again. The Version page lists what each Create came to and reads its Versions and Generations again.
- **Completion (#154):** each Generation a recorded Create made is watched for ten minutes (a `chrome.alarms` alarm ends the watch; it is kept in session storage and ends when the tab closes). The Suno tab reads every feed answer its page gets (`POST /api/feed/v3`) for the watched clip IDs. When none has named them for 15 seconds, it prompts the Create page to read its library pane again: it presses the Song's workspace row (opening the list by its breadcrumb), which only chooses what the pane shows. No library filter is toggled, so there is no filter state to restore. Each clip Suno shows finished (`complete`, or `error`) goes to `POST /api/v1/suno/generation-requests/{id}/clips` as `{ clip }` (`suno.generate`, the claimer only, even once the request has ended). The cover image then goes to `PUT /api/v1/generations/{id}/artwork`, which never replaces an image.
  - **What n8Tracks fills in:** only a Generation that this request's observed Creates made, and only while it has never been complete. Never complete means: its status is not final, no change of it was declined or conflict kept, and its raw clip did not come from an export. Every clip column (status, title, duration, model, tempo, key, addresses) and the raw clip are written, once, and nothing else: not the rating, comments, state, archiver, revision, or artwork. A clip that ended in error is recorded with status `error` and shown as Failed; it can be deleted. Answers are 200 `{ outcome: completed | failed, generation: { id, shortcode, sunoId, providerStatus } }`, 409 `already_complete`, 409 `not_provisional` (a Generation made by import, or one an import review decided about), 404, and 422 (not a finished clip). This is the one change to a Generation that no import review confirms (invariant 3). Anything later arrives through a sync, for the import review.
  - **After ten minutes** an unfinished clip is left as it is. Its row says "Still generating in Suno: sync to update", and the next sync brings it.
- **The web app:** the button sits beside Create New Version From. It pings the relay first and makes nothing unless the extension answers connected, compatible, and with `suno.generate`. Otherwise the page says which, and when the relay answered, it offers to open the extension's options (`open-options`).
  - It then makes the request and posts `{ type: "generate", requestId }`. The extension checks its connection afresh, claims the request, and answers `generate-accepted`. A refused hand-off cancels the request.
  - The page reads the request every two seconds while it is active and offers Cancel.

## Sync with Suno from the dashboard (#230)

- **The page:** the dashboard's Sync with Suno pings the relay (500 ms). When nothing answers, it says the extension is not installed or not paired, links to the pairing instructions (Settings → Credentials), and sends nothing more. Otherwise it posts `{ type: "open-sync" }` and waits two seconds for the answer; no answer in time is shown as not connected too.
- **The extension:** the service worker checks its pairing afresh (the handshake, bypassing the cache) and answers `{ type: "open-sync", ok: true, warning? }`, or `{ type: "open-sync", ok: false, reason, message }` with `reason` one of `unpaired` (not paired, paired with another n8Tracks, or a host permission removed), `revoked`, `missing_scope` (no `suno.sync`), or `unreachable`. A version mismatch is the `warning`, and the tab still opens. An extension older than this answers `unknown_type`, which the page shows as "update the extension".
- **What opens:** the user's library (`https://suno.com/me`) in a new tab beside the n8Tracks tab. The service worker remembers that tab in session storage (`syncPanel`), and the tab's first page load (`sync-resume`, answered `{ session: null, open: true }` once) opens the panel on the Sync view's first step. Nothing is read, staged, or pressed: the user chooses what to sync and presses Start sync, as from the toolbar (invariant 4).
- Only the relay may send it: `open-sync` from a content script on Suno is answered `unknown_type`.

## Download from Suno (#215)

The panel on Suno has a Download view. The user loads the library, narrows it by workspace and by text in the title, selects clips, and chooses formats from WAV, MP3, M4A, and M4A (streaming quality). They see what would be downloaded before anything is fetched, and Start downloads the files (#216, below).

- **Reading:** Load library and Refresh first ask the service worker (`download-begin`). It refuses while a sync or Generate on Suno runs, and says why. Then Library › Songs (`/me`) is opened again, and on that page load (`download-resume`) the library feed is read to its end with the reader a sync uses (`libraryReader.ts` `readLibrary`, scrolling with `load-more`). The list covers every workspace. Suno leaves trashed clips out of the feed, and a clip marked trashed is shown disabled. The selection is carried across the page load. A read that stops or is cancelled keeps what it read under an "incomplete" banner, and Select all stays off.
- **What each clip shows:** title ("Untitled" when blank), duration, created date, workspace, whether it is unlocked for download on Suno (`is_download_unlocked`), and whether n8Tracks has it.
  - A clip that is still generating, has failed, is in the Trash, or has no audio is shown disabled with the reason.
  - A hidden clip can be selected.
- **Already in n8Tracks:** `POST /api/v1/suno/clips/lookup` (`suno.sync`, 1 to 500 Suno IDs; the extension sends batches) answers `{ items: [{ sunoId, generation { id, shortcode } | null, artist, deleted, downloadedFormats }] }`.
  - `generation` is the live Generation holding the clip, archived or not.
  - `artist` is the Song's primary Artist.
  - `deleted` is true for a provider tombstone. An ignored clip has no Generation and is not deleted.
  - `downloadedFormats` lists the formats the clip's download records name (#222, below), in the order offered.
  - The lookup changes nothing. When the extension is not connected, or lacks `suno.sync`, the column says it is unavailable and why. When the lookup fails while connected, the column shows unknown with a Retry.
- **Unlocks:** the summary counts the selected clips not yet unlocked when WAV, MP3, or M4A is chosen. One unlock covers all three formats, and the stream needs none. The allowance comes from the page's own `GET /api/billing/info/`, which the page requests when a clip's Download dialog opens. The observer forwards only its `download_usage` counts. Start is refused, with the reason, when the run needs more unlocks than remain or when no reading has been seen yet.
- **Plan handed to #216:** `[{ sunoId, title, displayName, artist, format, unlocked, streamAddress }]`, with formats `wav`, `mp3`, `m4a`, and `m4a-stream`. `artist` is the lookup's Artist only for a clip that is a Generation in n8Tracks. The stream is offered only for a clip whose data has `media_urls[0]`, whose address is `streamAddress`. The formats chosen are remembered in `chrome.storage.local`.

## Downloading the files (#216)

Start downloads every file of the plan to the browser's download folder, into its `n8Tracks` subfolder, without a save prompt per file. The files go nowhere near n8Tracks: the user copies them into the media folder, and n8Tracks never writes there (invariant 2). Downloading imports, syncs, and changes nothing in the catalog; the queue itself calls nothing in n8Tracks, and each saved file is then reported as a download record (#222, below).

- **Start:** a run that unlocks clips needs the user to tick "Use N Suno download unlocks for this run" first. The tick is asked again when the count changes. The summary always says to turn off the browser's "Ask where to save each file before downloading" setting, which the extension cannot read. Start sends `download-start { files, unlocks }`. The service worker refuses it while a sync or Generate on Suno runs, and when `unlocks` is not the plan's count of clips not yet unlocked in WAV, MP3, or M4A. Pressing Start while a run is under way adds to it, and a clip and format already queued is not added twice.
- **File names** (`extension/src/download/fileName.ts`): `<Artist> - <Title> (suno-<Suno ID>).<ext>`, or `<Title> (suno-<Suno ID>).<ext>` with no Artist. The streaming-quality M4A ends ` stream.m4a`.
  - The Artist is the Song's primary Artist when the clip is a Generation in n8Tracks, else the Suno display name, else none.
  - Text is normalised to NFC. Control and bidirectional characters are removed, and `< > : " / \ | ? *` become `_`.
  - Leading and trailing dots and spaces are removed, an empty title is "Untitled", and a Windows device name gets a leading `_`.
  - A name is at most 180 UTF-16 units and 240 bytes of UTF-8. Cuts fall on whole characters, the title first and then the Artist, never the token or the extension. The ID is written in lower case.
  - The browser numbers a name that is taken (`… (1).wav`). `extension/fixtures/filenames.json` holds names the downloader produces, read by both the extension's tests and the server's matcher test (#206), the numbered form included.
- **The queue** (`extension/src/download/downloader.ts`) lives in the service worker and is kept in `chrome.storage.local` (`downloadQueue`). Two files are worked on at a time, in plan order, and one that fails never stops the rest.
  - **WAV, MP3, M4A** are prepared in the run's Suno tab (the queue sends it `download-prepare`), one at a time, just before each is downloaded, because a signed address expires after an hour. On the Library page, the adapter (`extension/src/adapter/downloadSteps.ts`, workflows `open-clip-menu`, `choose-download`, `choose-download-format`, `close-download-dialog`) does the following:
    1. It finds the clip's row by its `/song/<id>` link and presses the row's "More options", then the menu's Download.
    2. In the Download dialog it presses the format, then "Unlock & Download".
    3. It waits up to 30 seconds for the page's own `GET /api/download/clip/<id>?format=…` answer with `status: "ready"`. The observer forwards only `status` and `download_url`, and the clip and format the address names.
    4. It closes the dialog if it is still open.
  - **The Download dialog's controls** (the formats, "Unlock & Download", Close) are invariant 4's second named exception (`download-clip` in `adapter/forbidden.ts`). Only `Page.downloadDialogClick`, called from `adapter/workflows/download.ts`, presses them. "MP4 video asset", Stems & MIDI's Manage, and every forbidden control stay refused.
  - **Unlocks:** a clip not yet unlocked is unlocked only when the user confirmed it at Start, and once per clip; a run never unlocks more. For a clip already unlocked, Suno answers the button with `already_unlocked` and spends nothing (TS-004's `download-authorize` fixture). TS-004 captured the button only as "Unlock & Download". A dialog whose button is named otherwise stops the step, and no other name is pressed.
  - **The stream** is downloaded from the clip data's `media_urls[0]`, with no page step.
  - **Failures:** a clip not in the list on the page stops that file, saying to scroll to it and Retry. Suno not preparing the file in 30 seconds also stops that file. A step that no longer matches Suno's page stops every waiting file of that format, naming the step, and other formats go on. A signed address that has expired (`SERVER_FORBIDDEN`, `SERVER_UNAUTHORIZED`) is prepared afresh once. Any other interruption fails the file with the browser's reason.
  - **Cancel** stops queued files and cancels the files in flight with `chrome.downloads.cancel`. **Retry failed downloads** runs only the files that failed, from the start.
  - **Tabs and restarts:** if the run's Suno tab is closed, or is not on the Library, the files that need the page wait for Resume, and the stream goes on. A restarted service worker carries on with the downloads in the browser. After a browser restart, which the queue detects because `chrome.storage.session` is empty, the queue waits for Resume, and files in flight start again from zero.
- **After each file:** the final name is read back from the downloads interface (`chrome.downloads.search`).
  - When it is not the name asked for (or the browser's numbering of it), or not in the `n8Tracks` folder, the file is reported as saved under a different name, with that name. TS-004 saw another extension apply the address's own name, `<id>.wav` or `<id>_lyrics.mp3`, which the matcher still finds.
  - An M4A saved as `.mp4` is flagged to be renamed to `.m4a` before n8Tracks scans it.
- **Progress:** the queue pushes each change to the run's tab (`download-progress`), and a page load reads it (`download-run`). The panel shows each file (waiting, being prepared, downloading with a percentage, saved, failed with its reason, or cancelled) and the whole run, with Cancel downloads, Retry failed downloads, and Resume.
- **Suno's own copy:** when the maintainer downloaded through the Download dialog in TS-004, Suno's page saved the file itself, as `<title>.<ext>`. So a run may leave that copy too. That copy carries no Suno ID, so the summary says n8Tracks cannot match it. The extension does not remove it.

## Download records (#222)

n8Tracks keeps a record of each file the extension downloaded, so the user can see which outputs they already have, and in which formats. A record says a file reached the user's computer, not that it is in the media folder: that is an audio file, which a scan finds.

- **Reporting** (`extension/src/background/downloadRecords.ts`): when a file of the queue reaches `saved`, it gets a record ID of its own, and the service worker sends `POST /api/v1/suno/downloads` (`suno.sync`) with `{ id, sunoId, format, fileName, completedAt, sizeBytes, spentUnlock }`. `fileName` is the base name the browser saved it under, its ` (n)` numbering included, never a path. `spentUnlock` says whether the run spent a Suno unlock on the clip. No address and nothing of Suno's data is sent.
  - A failed or cancelled file is not reported. Reporting never holds up or fails a download.
  - A report is tried three times. One that still fails is kept in `chrome.storage.local` (`downloadRecords`) for the paired n8Tracks, at most 500, the oldest dropped first, and sent when the connection next works (a service worker start, the next saved file, or the panel reading the run). Pairing with another n8Tracks discards them.
  - A report refused for good (422, or 403 without `suno.sync`) is dropped and counted. A file saved while the extension holds no token is not recorded. The Download view says how many of the run's files are not yet recorded, could not be recorded, or were not recorded because it was not connected.
- **n8Tracks** stores each report in `download_records` by Suno ID, with no link to a Generation: a record is kept for a clip that is not a Generation yet (and creates nothing), for a deleted clip, and indefinitely. A clip imported later shows its earlier records. A repeated download is a new record; a report sent again under the same ID changes nothing and answers the stored record (200 instead of 201). It is refused with 422 unless the format is `wav`, `mp3`, `m4a`, or `m4a-stream`, the Suno ID is a UUID, and the name is 1 to 255 characters without a folder. The extension's completion time is stored with n8Tracks' own receipt time, and is never later than it.
- **The Generation panel** reads `GET /api/v1/generations/{reference}/downloads` (`catalog.read`, newest first, at most 100). Each record shows its format, file name, and time, and "In media folder" when a scanned file of that name is attached to this Generation, "Found, not attached to this Generation" when one exists elsewhere or unmatched, or "Not found in media folder". Names are compared without regard to case and without the browser's ` (n)` numbering; a Missing file still counts as found, shown with its state.
- **The Download view** gains "Not yet downloaded", which shows only the clips lacking a record in at least one chosen format (in any format when none is chosen), and "Skip files already downloaded", ticked by default, which leaves those files out of the plan and names them in the summary. Files saved in the current run count as downloaded too. While n8Tracks cannot say what was downloaded (not connected, or no `suno.sync`), the filter is off and nothing is skipped.

## Streaming from Suno (#221)

When a Generation has no available local file, the player streams it from Suno.

- **The stored address:** a Generation keeps one audio address. It is the first `media_urls` entry a browser plays: MP3, then M4A (Suno's `m4a-opus` among them), then OGG, matched by `content_type` or by the address's path. With none of those, it is `audio_url`. The migration `20261008060000_RederiveGenerationAudioUrls` re-derived every stored address from its raw clip by this rule. The address is not a compared field, so a sync after it proposes no change.
- **The server's host list:** `SunoAudioHosts` (`src/n8Tracks.Domain/Suno/`) holds by default only `d2lwuy8qc234o3.cloudfront.net`, the playback host. The adapter also lists the signed-download host and Suno's API host, and the server leaves both out. The default must stay a subset of the adapter's `SUNO_AUDIO_HOSTS`, which a test checks. The operator can replace the list with `N8TRACKS_SUNO_AUDIO_HOSTS` (see the README's Configuration) in case Suno moves its audio; that list is outside the subset check, and it is what both the policy below and the address check use. Any other address counts as no address. So does an address that is not plain HTTPS on the default port, or one that carries a user name.
- **The rule (`PlaybackResolver`, `SunoStream`):**
  - A local file always wins.
  - The stream is tried only for a clip whose status is `complete` and whose remote state is `present`. Otherwise Play is disabled, with the reason (`suno_not_complete`, `suno_not_present`, `nothing_available`).
  - A Song with a Selected Generation that has no local file streams that Generation. It never plays another Generation's file.
  - The playback answers carry `source: "suno"`, `sunoAudioUrl`, and `sunoPageUrl`. The playback sources list a stream-only Generation with no files and its `sunoAudioUrl`.
- **The browser plays it:**
  - The server never requests, proxies, or stores the audio. The architecture tests check that no server type can reach the network.
  - The audio element sets `referrerpolicy="no-referrer"` and no `crossorigin`, so the request to Suno carries no n8Tracks cookie, token, or referrer path.
  - Every response sends `Content-Security-Policy: media-src 'self' https://d2lwuy8qc234o3.cloudfront.net` (with the default host list; one `https://` origin per configured host otherwise) and no other directive. The full policy belongs to the audit milestone.
  - Every response also sends `Referrer-Policy: strict-origin-when-cross-origin`, the browsers' default made explicit. Browsers do not yet honour the attribute on media elements, so this header is what keeps the path out.
- **Failure:** a stream that errors, has not started 15 seconds after the play request, or stalls for 30 seconds while playing has failed. The bar says so, offers Open in Suno (a new tab), and suggests syncing again. There is no retry, and the player never falls back to another source.

## Extension structure

- A service worker holds state and is the only part that calls the n8Tracks API.
- On `suno.com`: a content script, plus a script in the page's own context that wraps `fetch` and passes copies of matching responses to the content script. It observes only.
- On the configured n8Tracks origin: a relay content script. The web app talks to the extension with `window.postMessage({ source: "n8tracks", type, ... })` and receives `{ source: "n8tracks-extension", ... }`. The web app detects the extension with a `ping` that times out after 500 ms. The page messages are `generate`, `open-options` (#144), and `open-sync` (#230).
- The Suno adapter lives in `extension/src/adapter/` with an `ADAPTER_VERSION` constant. Every selector, address pattern, and workflow step for Suno is inside it.
- The extension's own interface on the Suno page is a panel the content script injects in a shadow root; settings are on an options page. The popup shows connection state.
- Pairing: the options page takes the n8Tracks address and a token, asks the browser for the two host permissions, and the service worker checks the token with `GET /api/v1/extension/handshake` before saving it. Any valid token may make the handshake, whatever its scopes. Each call sends `X-N8Tracks-Extension-Version` and `X-N8Tracks-Adapter-Version`. The answer is `{ applicationVersion, credentialName, scopes, compatible }`, and the reported versions are recorded on the credential. The handshake is cached for 60 seconds and is re-run before each sync or generation. A 401 `invalid_token` forgets the token.
- Manifest: `permissions` are `storage`, `scripting`, `tabs`, `alarms` (the completion watch's ten minutes, #154), and `downloads` (the download queue, #216; required, because the panel is a content script and cannot show a permission prompt). `optional_host_permissions` are `https://suno.com/*`, `https://*/*`, and `http://*/*`; at pairing the extension requests exactly `https://suno.com/*` and the one n8Tracks origin the user entered, and nothing else is ever requested.
- Extension logs follow invariant 6: tokens, lyrics, prompts, and raw payloads are never written to the console or to the diagnostic report.

## Testing

- Adapter and reader tests run against the sanitized fixtures in `extension/fixtures/suno/` (JSON responses and page snapshots). No automated test touches the live site.
- Each story that operates the live Suno page carries a Demo for the maintainer to run by hand in their signed-in browser.
