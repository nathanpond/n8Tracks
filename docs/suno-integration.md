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
  - An audio address is handed to the browser's downloads interface, which fetches it with no credential added by the extension. The address is either one the page's own traffic exposed (a signed `download_url` from `GET /api/download/clip/<id>?format=…`, valid for one hour) or a clip's `media_urls` playback address. It is used only when it is on a listed Suno audio host: `suno-data-uploads.s3.amazonaws.com` and `d2lwuy8qc234o3.cloudfront.net` (spike TS-004).
- The extension changes Suno in exactly two ways:
  - It may create a workspace.
  - It may unlock a clip for download by clicking the Download dialog's "Unlock & Download". This spends one of the user's plan downloads, and only after the user has confirmed, before the run, how many unlocks the run uses against the allowance remaining. It never buys download packs. (Maintainer's decision on TS-004, 2026-10-05.)
- It never clicks Create, Publish, Delete, Trash, or Remove controls (invariant 4).
- Filling lyrics into the Create form makes Suno save a draft in the user's Saved lyrics (`POST /api/lyrics-projects`, spike TS-003). This is Suno's own behaviour, not an extension action.
- Nothing reaches the catalog from a library export until the signed-in user confirms it on the review page (invariant 3). The extension's token can stage an export; it cannot commit one.

## Scopes

Two scopes join the list from #56:

- `suno.sync`: create and upload an export, upload Generation artwork for it, read the state of exports this credential created, and report workspace discovery.
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
- **One under review:** completing an export discards any earlier export under review, whoever created it. An export being committed makes a new one wait with 409 `import_in_progress`. `POST .../discard` discards an export.
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
  - `PATCH` (`suno.generate`, no If-Match) takes the claimer's progress, `{ state: opening | workspace | filling | waiting | done | stopped, step, message }`; a stop says why. It may also carry `resolvedWorkspace: { sunoId, name, how: created | picked }`, the workspace the user chose for the Song in the extension's panel (#145). It becomes the Song's workspace in the same transaction, but only when the Song has none or its workspace is Unavailable; otherwise the answer is 409 `workspace_already_set` and nothing changes. Sending the Song's own workspace again changes nothing, so a report sent again after a lost answer is harmless. A workspace n8Tracks has not seen is recorded as Available. An Unavailable one cannot be chosen (422).
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
- **The web app:** the button sits beside Create New Version From. It pings the relay first and makes nothing unless the extension answers connected, compatible, and with `suno.generate`. Otherwise the page says which, and when the relay answered, it offers to open the extension's options (`open-options`).
  - It then makes the request and posts `{ type: "generate", requestId }`. The extension checks its connection afresh, claims the request, and answers `generate-accepted`. A refused hand-off cancels the request.
  - The page reads the request every two seconds while it is active and offers Cancel.

## Extension structure

- A service worker holds state and is the only part that calls the n8Tracks API.
- On `suno.com`: a content script, plus a script in the page's own context that wraps `fetch` and passes copies of matching responses to the content script. It observes only.
- On the configured n8Tracks origin: a relay content script. The web app talks to the extension with `window.postMessage({ source: "n8tracks", type, ... })` and receives `{ source: "n8tracks-extension", ... }`. The web app detects the extension with a `ping` that times out after 500 ms.
- The Suno adapter lives in `extension/src/adapter/` with an `ADAPTER_VERSION` constant. Every selector, address pattern, and workflow step for Suno is inside it.
- The extension's own interface on the Suno page is a panel the content script injects in a shadow root; settings are on an options page. The popup shows connection state.
- Pairing: the options page takes the n8Tracks address and a token, asks the browser for the two host permissions, and the service worker checks the token with `GET /api/v1/extension/handshake` before saving it. Any valid token may make the handshake, whatever its scopes. Each call sends `X-N8Tracks-Extension-Version` and `X-N8Tracks-Adapter-Version`. The answer is `{ applicationVersion, credentialName, scopes, compatible }`, and the reported versions are recorded on the credential. The handshake is cached for 60 seconds and is re-run before each sync or generation. A 401 `invalid_token` forgets the token.
- Manifest: `permissions` are `storage`, `scripting`, and `tabs`. `optional_host_permissions` are `https://suno.com/*`, `https://*/*`, and `http://*/*`; at pairing the extension requests exactly `https://suno.com/*` and the one n8Tracks origin the user entered, and nothing else is ever requested.
- Extension logs follow invariant 6: tokens, lyrics, prompts, and raw payloads are never written to the console or to the diagnostic report.

## Testing

- Adapter and reader tests run against the sanitized fixtures in `extension/fixtures/suno/` (JSON responses and page snapshots). No automated test touches the live site.
- Each story that operates the live Suno page carries a Demo for the maintainer to run by hand in their signed-in browser.
