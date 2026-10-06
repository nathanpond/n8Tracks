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

Raw objects are sent as Suno returned them. It is uploaded in parts: `POST /api/v1/suno/exports` (header fields, returns an ID), `POST /api/v1/suno/exports/{id}/parts` (up to 200 clips per part), `POST /api/v1/suno/exports/{id}/complete`. An export moves through `receiving → ready → committing → committed`, or `discarded`, `failed`, or `expired` (seven days after it became ready). An unknown `formatVersion` is refused with `unsupported_format`.

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
