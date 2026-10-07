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
- `suno.generate`: claim and read a pending generation request, report its progress, record the workspace the user chose for the Song, and report an observed Create and its finished clips (which attaches Generations to the requested Version, or to a new child Version when the submitted inputs differ) with their artwork.

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

- **Discovery:** `PUT /api/v1/suno/workspaces/discovered` (`suno.sync`) takes `{ complete, workspaces: [<raw project>] }`. It reads `id`, `name`, `description`, and `is_trashed`.
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
- **One under review:** completing an export discards any earlier export under review, whoever created it. An export being committed makes a new one wait with 409 `import_in_progress`. `POST .../discard` discards an export.
- **Expiry:** the daily retention job's second step expires a ready export after seven days. It discards an export still receiving after 24 hours, and removes a committed export's staged rows after 24 hours.
- **Reading:** `GET /api/v1/suno/exports/{id}` returns the state and the counts per class. `GET .../records` lists the records, filtered by `class`, `workspace`, and `playlist`, and paged (`page`, `pageSize`, at most 200). The records list is session-only. A credential reaches only the exports it created.
- **Cover images:** `PUT /api/v1/suno/exports/{id}/artwork/{sunoId}` stages a cover image with a record of a ready export. The image is checked like any artwork, and the commit gives it to the Generation.

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
