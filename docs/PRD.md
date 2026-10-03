# n8Tracks Product Requirements Document

**Status:** Build-ready draft 2.2  
**Updated:** 2026-10-03  
**Product name:** n8Tracks (working name)

## Product Overview

n8Tracks is a self-hosted authoring workspace and catalog for AI-assisted music. It is intended to become the durable system of record that Suno is not: preserving lyrics, creation parameters, generated outputs, creative lineage, workflow state, artwork, local media references, albums, playlists, and AI-assisted editing history.

V1 supports Suno as its only generation provider, while keeping provider-specific behavior behind an integration boundary. Users exchange data with Suno through a browser extension and a versioned JSON interchange format. n8Tracks does not directly invoke an unofficial Suno API in V1.

### Core problems

- Suno may lose or inadequately expose lyrics and creation parameters.
- Suno provides weak search, filtering, workflow-state tracking, catalog export, and artwork management.
- Suno does not provide an adequate API or MCP interface for ChatGPT, Claude, and other AI clients.
- Multiple Suno outputs, revisions, experiments, and related songs are difficult to organize as a coherent creative history.

### Primary persona

An individual creator running n8Tracks on a NAS, home server, or other self-hosted environment. V1 is single-user and uses simple HTTP authentication comparable to Sonarr/Radarr.

### V1 value proposition

The user can author a Song, experiment through a tree of named Versions, import and evaluate Suno Generations, select the final Generation for the Song, organize the catalog, associate local master files and artwork, and let AI clients inspect the catalog or propose edits without silently modifying accepted content.

## Core Domain Model

### Song

A Song is the central object from initial idea through release. It is always a Song, regardless of workflow state.

Required and optional fields currently established:

- `title` — required; does not need to be unique; `Working Title` is acceptable.
- `concept` — optional short description, such as “Fast-paced song about running in a pack.”
- configurable workflow state.
- catalog metadata such as genre, creation date, tags, notes, artwork, albums, and playlists.
- one current working Version.
- zero or one Selected Generation representing the chosen Suno output for the Song.

Duplicate titles are allowed without restriction. When viewing a Song whose title is shared, the UI displays an indicator that opens the other Songs with that title. Search results expose distinguishing context such as Concept, genre, creation date, and linked Suno information.

### Configurable Song workflow

- Each Song has exactly one workflow state.
- V1 ships with `Idea`, `Writing`, `Generating`, `Refining`, `Final`, `Released`, and `Archived` defaults.
- Users may add, rename, reorder, recolor, and hide workflow states.
- V1 does not enforce transition rules; a Song may move directly from any state to any other state.
- A workflow state assigned to Songs cannot be deleted until the user chooses a replacement state for affected Songs.
- Song workflow state is independent of Version Active/Archived visibility and Generation Active/Archived provider state.

### Version

A Version represents a particular set of inputs intended for or used in generation. Versions form a tree rather than a strictly linear history.

Current decisions:

- A Version has a stable internal identifier, an automatically assigned hierarchical display number, and a user-friendly optional name such as `Guitar experimentation`.
- A Version may exist without any Generations.
- Copying JSON to Suno does not prove generation occurred and does not itself attach a Generation.
- A Version with no Generations remains until the user deletes it.
- Multiple Generation attempts may use the same Version.
- Changing any creation input or Suno setting, including only Weirdness, requires a new Version.
- Once a Generation is attached, all inputs that produced it are immutable.
- A user can create a new Version from any existing Version.
- The user selects which Version to continue working from; the current Version is the active working Version.
- Creating a later Version does not require a selected Generation on an earlier Version.

#### Hierarchical numbering

Version numbers follow a tree-oriented display convention.

- Top-level sequence: `1`, `2`, `3`, `4`.
- Descendants or alternate paths may be `1.1`, `2.1`, `2.2`, `2.1.4`, and so on.
- When creating from a Version, n8Tracks proposes the next available linear number when that continuation is unambiguous.
- If the proposed next linear number already exists, n8Tracks proposes a child number.
- The user may override the proposed display number, subject to uniqueness within the Song.
- Version numbers are not directly editable.
- When creating from a Version, the user only chooses between system-generated valid continuation options.
- Example: creating from `1` proposes `2` when `2` does not exist.
- If `2` already exists, the user may choose `1.1` or the next available top-level number, such as `3`.
- Example: creating from `1.3.1` proposes `1.3.2` when available.
- If `1.3.2` already exists, the user may choose `1.3.1.1` or the next available sibling, such as `1.3.3`.
- Existing Versions are never renumbered automatically.
- The tree is derived and visualized from the hierarchical Version number.
- A separate Version name and Version notes field allow the user to explain the experiment or distinguish similar Versions.

### Version visibility

- Any number of Versions may be `Active` simultaneously.
- A Version may be marked `Active` or `Archived`.
- Archived Versions remain accessible in the Version tree.
- The main Versions table displays Active Versions by default.
- A `Show Archived` toggle adds Archived Versions to the table.
- The Version tree can display and navigate both Active and Archived Versions.
- Archiving does not delete the Version, its descendants, or its Generations.
- Active/Archived is only a visibility and organization property. It does not indicate finality, selection, mutability, or deletion state.

### Generation

A Generation is one unique Suno output and corresponds to one unique Suno ID.

Current decisions:

- A Version may have any number of Generations.
- Suno commonly produces two Generations per Create action; repeated Create actions add more Generations to the same Version when the inputs are unchanged.
- A Generation belongs to exactly one n8Tracks Song and exactly one Version.
- A Generation may be rated from one to five stars.
- A Generation may have user comments, such as `Good piano intro` or `I really like the beat`.
- Generations may be retained as inspiration or as the source for creating a new Version.
- Selecting a Generation only marks it as the Song’s Selected Generation; it does not implicitly replace other Song or Version data.
- After selection, a cleanup workflow may mark other Generations as deletion candidates.
- A selected Generation may come from any Version; Versions themselves do not become final.
- The Selected Generation is the provider output chosen as the final audio for the Song.
- A Generation may be `Active` or `Archived` independently of its Version’s Active/Archived visibility.
- When a linked Suno song is moved to Suno Trash, the next differential export/import marks the n8Tracks Generation Archived.
- When the Suno song is restored from Trash, the next differential export/import reactivates the n8Tracks Generation.
- Suno Trash does not cause permanent deletion or enter the n8Tracks 30-day deletion-retention process.

The terms `Preferred Generation`, `Final Version`, and `Master Audio File` are not separate V1 concepts unless later requirements establish a need. The working term is `Selected Generation`.

### Internal Generation Event

The UI will not expose a Batch concept. Users care about Versions and Generations, not provider request batches.

n8Tracks should nevertheless retain a lightweight, optional internal Generation Event when reliable correlation is available because it supports:

- grouping the outputs returned by one Suno Create action;
- atomic import and failure recovery;
- provider request IDs and timestamps;
- future providers or future Suno output-count configuration;
- diagnosing partially imported output pairs.

This internal record must not complicate ordinary user workflows. Suno may not expose a reliable batch/request identifier. Generation timestamps require testing: if the timestamp represents completion time, outputs from the same Create action may have different timestamps and cannot safely be grouped by equality. Timestamp proximity alone must not be treated as authoritative correlation.

Generation Event linkage is therefore nullable and may include a correlation source and confidence. Possible sources include a provider request ID, browser-extension observation of the Create action and resulting output cards, or a user-confirmed import grouping. Generations may exist without a known event.

### Song creation from a Generation

From a Generation, the user may choose `Create new Song from Generation`.

Required behavior:

1. Warn that the Generation will be moved, not shared.
2. Create a new Song.
3. Create Version 1 for the new Song using the source inputs.
4. Move the Generation to Version 1 of the new Song.
5. Remove the Generation association from the original Song and Version.
6. Preserve lineage between the new Song and its source Song/Version.

A Generation must never simultaneously belong to two Songs.

## Core Loop and Feature Specifications

### Create and author a Song

1. User creates a Song and supplies a required title.
2. User may enter Concept and other catalog metadata.
3. System creates or activates a working Version according to the Version-lifecycle rules still being finalized.
4. User edits lyrics, style guidance, creation mode, references, and Suno settings.
5. Editor autosaves and maintains recoverable editing history.

### Song and initial Version invariants

- Creating a Song automatically creates mutable Version `1` and makes it the current working Version.
- Song title is required; Concept, Version name, and Version notes are optional.
- Version 1 may remain ungenerated indefinitely.
- Attaching the first Generation freezes the Version’s generation inputs.
- Deleting a Song’s last remaining Version does not delete the Song.
- When the last Version is deleted, the system automatically creates a new blank mutable Version `1`.

### Browser-extension operating model

The V1 browser extension is a two-tab or two-window workflow orchestrator. The user keeps n8Tracks and Suno open concurrently, and the extension transfers and reconciles data between the two authenticated browser contexts.

The extension must not require the user to expose Suno credentials to n8Tracks. It operates through the user’s existing Suno browser session and the authenticated n8Tracks tab or configured n8Tracks endpoint.

### Full Suno export to n8Tracks

The extension shall support a user-initiated library export workflow:

1. Read the user’s available Suno library records.
2. Present a selectable list before importing.
3. Allow selection by individual Suno song, Suno workspace, or Suno playlist.
4. Compare the selected Suno records with existing n8Tracks Provider records using Suno ID.
5. Clearly distinguish new, already linked, changed, ignored, and potentially conflicting records.
6. Allow the user to review and change the proposed n8Tracks Song and Version matches.
7. Import only the confirmed records.
8. On later runs, perform a differential comparison rather than treating all records as new.

If the user chooses not to copy a Suno record, n8Tracks may place its Suno ID on an Ignore List. On later exports, ignored records default to `Don’t copy` but remain visible and selectable so the user can reverse the decision.

Ignore is different from deletion:

- Ignore means the remote Suno record has never been imported or is intentionally excluded from import.
- A provider tombstone means a previously linked n8Tracks record was deleted locally and must not be silently recreated.
- The import review should explain the applicable reason and allow behavior appropriate to each case.

### Generate on Suno from n8Tracks

From a specific n8Tracks Version, the user may initiate `Generate on Suno`.

Required orchestration:

1. Resolve the owning n8Tracks Song and the Version’s current creation inputs.
2. Find the Suno workspace previously associated with the Song, if one exists.
3. If no associated Suno workspace exists, create one using the Song title and retain its immutable Suno workspace ID in n8Tracks.
4. Reuse the associated workspace for later Generations belonging to the same Song.
5. Navigate the Suno tab to the Create page.
6. Select the resolved Suno workspace.
7. Populate every supported generation property from the n8Tracks Version.
8. Surface unsupported, unavailable, or failed field assignments before generation.
9. Capture the effective Suno form state at the actual generation boundary when technically feasible, because the user or Suno may alter values after initial population.

The extension stops after population and presents a verification summary. The user reviews the Suno form and clicks Create manually. The extension observes the user’s Create action, captures the effective form values, and attempts to associate the resulting Suno IDs. Automatic clicking of Create is not required in V1.

### Suno workspace mapping

- A Suno workspace is identified by its immutable Suno workspace ID, not its name.
- One Suno workspace may contain any number of n8Tracks Songs.
- A n8Tracks Song may be associated with at most one Suno workspace.
- A Song may have no Suno workspace before generation or explicit association.
- Creating a new Suno workspace initially uses the Song title, but duplicate workspace names are permitted.
- If an unlinked workspace has the same name, the user may choose it or create another workspace.
- Renaming a workspace in Suno updates the displayed provider metadata without breaking the ID-based association.
- Changing a n8Tracks Song title does not silently rename its Suno workspace; the UI may offer an explicit rename action.
- If the associated workspace is missing or inaccessible, the user is warned and may associate or create a replacement.

### Generate and import

1. User copies versioned JSON from n8Tracks through the browser extension into Suno.
2. Copying/exporting does not mark generation as completed.
3. User generates one or more Suno outputs.
4. Browser extension imports Suno results.
5. n8Tracks uses Suno ID as the unique provider identity for each Generation.
6. Existing Suno IDs are shown as already linked and are not duplicated.
7. If Suno inputs differ from every existing Version, import proposes creation of a new Version.
8. User resolves conflicts when imported Suno data disagrees with n8Tracks data.
9. Imported results are attached to the selected or matched Version.

Creation details may change in Suno between initial population and the actual Create action. The extension should capture the effective Suno form state at generation time when feasible, not merely assume the original n8Tracks values remained unchanged.

### Batch import review

For unmatched or newly discovered Suno outputs:

1. Present a review list.
2. Attempt to match each item to an existing Song and Version.
3. Show the proposed match or indicate that a new Song is proposed.
4. Allow the user to change every proposed match before committing the import.
5. Show already-linked Suno IDs as existing records rather than duplicate candidates.

### Evaluate Generations

1. User listens to Generations attached to a Version.
2. User may assign one to five stars and add comments.
3. User may retain any number of Generations.
4. User may select one Generation as the Song’s Selected Generation.
5. Selecting a Generation does not automatically mark other Generations for deletion in V1.
6. Suno trash/restore state is synchronized to the Generation’s Archived/Active state during differential import.

### Create a new Version

1. User selects `Create New Version From` on any Version or eligible Generation.
2. System copies the relevant immutable inputs into a new mutable Version.
3. System proposes a hierarchical display number and allows an override.
4. User may give the Version a friendly name.
5. User edits the new Version without affecting its source.

### Version lineage and Suno source actions

- n8Tracks supports user-configurable Song relationship types for general catalog organization.
- It also ships with system mappings for Suno lineage actions observed in the provider UI: Cover, Remix, Reuse Prompt, Mashup, Sample This Song, Use as Inspiration, and Voice.
- A Version may reference one or more source Songs or source Generations, with the exact Generation retained whenever known.
- Source relationships used to create a Version are generation inputs and become immutable when the Version receives its first Generation.
- Mashup supports multiple source Songs or Generations.
- Inspiration supports two mutually exclusive source modes: one Suno playlist, or up to four individual source Songs/Generations.
- When an existing Suno playlist is used for Inspiration, the Version stores its Suno playlist ID and a snapshot of the expected source membership.
- When individual Inspiration sources are used, the Version stores the ordered list of up to four exact source Generation IDs when known.
- Provider Inspiration playlists are lineage inputs and remain distinct from ordinary organizational n8Tracks Playlists.
- Custom relationship types remain valid in n8Tracks. They drive Suno automation only when mapped to an action that the extension supports.
- During `Generate on Suno`, the extension opens the applicable source Generation or Suno playlist, invokes the mapped lineage action, verifies that the Create screen contains the intended source context, and then populates the remaining Version inputs.
- n8Tracks may label provider capabilities such as Inspiration or Voice as requiring Suno Pro, but it does not enforce or attempt to infer the user's Suno subscription.
- `Create New Song from Generation` automatically records a `Derived From` relationship while still moving, rather than sharing, the Generation.
- Song relationships explain lineage and organization but never cause Songs to share Versions, Generations, audio files, artwork, or Selected Generations.

### Imported lineage

- Suno library export/import captures every lineage relationship and source identifier the provider exposes.
- When both child and parent exist in n8Tracks, the relationship links their real Song and Generation records.
- If a referenced parent has not been imported, n8Tracks creates a lightweight external reference containing the Suno ID, title, URL, and available metadata and labels it `Not imported`.
- Users may import the parent later, ignore it, or retain the external reference indefinitely.
- A later import resolves the external reference to the real record by Suno ID without creating a duplicate.
- Preserving a child's lineage never forces its parent to be imported.

### Unavailable lineage sources

- Before generation, a mutable Version may remove or replace an unavailable lineage source.
- After the Version receives a Generation, its lineage source configuration remains immutable.
- If a required source is trashed, deleted, inaccessible, or missing from a required provider playlist, the extension blocks automated generation and identifies the exact source problem.
- The user may create a new Version from the affected Version and replace or remove the unavailable source there.
- Existing Generations and historical lineage remain valid when a source later becomes unavailable.
- If the source is restored in Suno, automation becomes eligible again after synchronization confirms availability.

### Delete and retain

Users may delete any Song, Version, Generation, internal Generation Event, editor revision, collection, or asset association after appropriate warnings.

Current retention decision:

- Deletion is presented as permanent to the user.
- Internally, deleted records are retained for 30 days in a trash/retention foundation.
- V1 does not require a user-facing Trash interface.
- Separate retention tables are preferred over a universal `deleted` flag, subject to technical validation.
- After the retention period, records may be physically pruned.
- n8Tracks keeps a non-user-visible provider tombstone when needed so a deleted Suno object is not reimported during the next sync.
- There is no general activity/audit history requirement.

### Version deletion dependencies

- Deleting a Version deletes that Version and its own Generations but does not delete descendant Versions.
- Before confirmation, the warning states the Version number, number of Generations being deleted, and number of descendant Versions that will remain.
- Descendants retain their existing immutable display numbers and ancestry.
- The tree renders a minimal Deleted Version placeholder with the original number wherever a retained descendant's parent was deleted.
- Deleted-parent placeholders do not appear in the normal active or archived Versions table.
- The deleted Version and its Generations remain internally recoverable during the 30-day retention window.

### Generation deletion dependencies

- Albums and Playlists contain Songs, never Generations. Deleting a Generation therefore does not directly modify Album or Playlist membership.
- If the Generation is the Song's Selected Generation, the confirmation explicitly warns that the Generation currently represents the Song's chosen output and that deleting it will return the Song to a working state with no Selected Generation.
- The same confirmation screen requires the user to either select another eligible Generation as the Song's new Selected Generation or choose the workflow state to apply after clearing the selection.
- If the user selects a replacement Generation, that Generation becomes selected and the Song's workflow state does not change automatically.
- If the user chooses a workflow state, n8Tracks clears the Selected Generation and applies the chosen state.
- If no alternative Generation exists, the workflow-state choice is required.
- Artwork owned by the deleted Generation is deleted through the retention process. The confirmation identifies that artwork will be deleted.
- Local audio files are not physically deleted. Their n8Tracks associations become orphaned, and the confirmation identifies the affected audio files and explains that they will remain on disk.
- The Generation is removed from its owning Version and retained internally for 30 days.
- Its Suno ID is added to the internal provider tombstone/suppression data so synchronization does not silently recreate it.
- Deleting a Generation in n8Tracks never deletes, trashes, or otherwise mutates the corresponding item in Suno.

### Song deletion dependencies

- Before deletion, n8Tracks warns with counts of affected Versions, Generations, managed artwork, Album memberships, Playlist memberships, relationships, and associated local audio files.
- The Song, all of its Versions, and all of its Generations enter the 30-day retention foundation.
- Active Album, Playlist, and Song-relationship memberships are removed.
- n8Tracks-managed Song, Version, and Generation artwork is deleted through retention.
- Physical audio files in the mounted media volume are never deleted; after their associations are removed, they appear as Unmatched Files.
- Provider tombstones are retained for every deleted Suno Generation so synchronization cannot silently recreate them.
- Deleting a Song in n8Tracks never deletes or trashes anything in Suno.
- Confirmation requires typing the Song title when the Song has a Selected Generation or multiple dependent records.

## Suno Integration Requirements

### Provider fidelity

- Mirror every available Suno field for Generations and creation metadata.
- Normalize commonly queried fields for filtering and search.
- Preserve raw provider JSON so unknown fields survive import and export.
- Support Simple, Advanced, and Sounds creation modes.
- Support Audio, Voice, and Inspiration inputs where Suno permits them.
- Preserve exact submitted inputs and exact provider-returned data when both are available.

### Import conflict rules

- Imported Suno data does not silently overwrite conflicting n8Tracks data.
- The user decides how conflicts are resolved.
- If lyrics or other creation inputs were changed directly in Suno and used to produce a Generation, importing that Generation creates a new Version.
- Suno ID is the unique identity for a Generation and links the n8Tracks record to its provider object.
- If an existing linked Suno ID is unchanged, no update is required.
- If provider metadata changed, the import review shows a diff and lets the user decide which applicable changes to accept.
- If immutable creation inputs differ, import proposes a new Version rather than rewriting the existing Version.
- A linked Suno ID found in Suno Trash archives its n8Tracks Generation.
- A previously archived linked Suno ID restored in Suno reactivates its n8Tracks Generation.
- A Suno record that cannot be found and is not known to be in Trash is marked `Remote Missing`; it is not automatically deleted.
- Ignored records default to `Don’t Copy` but may be imported explicitly.
- Locally deleted provider IDs require an explicit Restore/Reimport action and are not silently recreated.

### Ignored Suno items

- n8Tracks provides an `Ignored Suno Items` management screen distinct from the local Unmatched Files view.
- It shows each ignored provider item's Suno title, Suno ID, workspace, ignored date, and current provider status when available.
- Users can search and filter the list and remove one or many items from it.
- Removing an item from the Ignore List makes it eligible for selection during a future export/import review; it does not import the item immediately.
- Provider metadata changes do not remove an item from the Ignore List.
- Moving an ignored item to Suno Trash does not remove its ignore record, so restoring it in Suno does not unexpectedly make it eligible again.

### Unavailable Suno workspaces

- If a linked Suno workspace disappears or becomes inaccessible, n8Tracks retains its Suno workspace ID and all Song associations and marks it `Unavailable`.
- No Songs, Versions, Generations, or local assets are detached or deleted because a workspace is unavailable.
- Local authoring and catalog management continue normally.
- The extension retries workspace discovery during later synchronization runs and automatically returns the workspace to `Available` if it reappears.
- Users may associate affected Songs with another Suno workspace individually or in bulk.
- Workspace identity is always based on Suno workspace ID, never an inferred name match.

### Browser-extension security and resilience

- The extension requests access only to `suno.com` and the user-configured n8Tracks origin, plus the minimum permissions required for tabs, page interaction, downloads, and extension-local settings.
- It stores a revocable scoped n8Tracks token, never the administrator password or Suno credentials.
- It reuses the user's authenticated Suno browser session without capturing or transmitting Suno cookies.
- Sync, generation preparation, bulk download, and other automation require explicit user initiation, with a confirmation summary before bulk work.
- The extension does not click Suno's final Create, Publish, Delete, or similarly consequential controls unless the specific workflow explicitly requires and confirms that action.
- Ordinary logs redact tokens, cookies, lyrics, and raw provider payloads.
- Suno-specific selectors and workflows live behind a versioned adapter that validates expected page state before each action.
- When the page no longer matches expectations, automation stops safely and identifies the failing step; it never guesses by clicking nearby elements.
- Users can download a redacted diagnostic report, and compatibility is tracked between application and extension versions.
- Failure in one automation path does not disable unrelated extension capabilities that remain compatible.
- Sanitized Suno page fixtures support automated adapter tests, supplemented by focused manual smoke testing against the live site.

## Albums, Playlists, Search, Assets, API, and AI

The following previously established V1 areas remain in scope:

- Albums and Playlists are first-class ordered collections of Songs with title, description, artwork, and ordering.
- User-defined Tags support organization.
- Search spans title, Concept, lyrics, styles, prompts, dates, state, provider/model, settings, Albums, Playlists, asset presence, and relationships.
- Mounted directories are scanned for audio files and artwork; n8Tracks associates but does not rename, move, delete, or transcode existing files in V1.
- The web UI, browser extension, API, import/export, and MCP interface use the same application service/business-rule layer.
- Authorized AI clients may read and directly modify supported catalog data through MCP; user review is not universally required.
- MCP-supported writes include creating Songs in bulk; editing Song properties; creating and editing mutable Versions; improving lyrics and other generation inputs; managing Albums, Playlists, Tags, ratings, and comments; and adding or selecting artwork.
- MCP writes use the same application services, validation, authorization, and domain rules as the web UI and API.
- An MCP client cannot mutate immutable Version inputs after a Generation exists. It must create a new Version from the immutable Version and apply changes there.
- Destructive operations such as deleting Songs, Versions, Generations, or physical files are excluded from MCP V1 unless explicitly added later.
- CSV export supports tabular catalog use; JSON import/export preserves the complete model.

### Portable catalog export

- JSON export contains the complete portable catalog model: Songs, Versions, Generations, lineage, relationships, ratings/comments, workflow states, Tags, Albums, Playlists, provider IDs, raw provider metadata, and asset references.
- Export excludes passwords, sessions, API/MCP credentials, and filesystem-internal secrets.
- Audio files are referenced but never embedded.
- An optional ZIP export contains the JSON document plus n8Tracks-managed artwork.
- Exports identify their schema and application versions for validation and migration during import.
- Portable interchange export is separate from operational backups used for full-instance recovery.

### Portable catalog import

- Portable JSON may be imported into an empty instance or merged into an existing catalog.
- n8Tracks validates the package and presents a dry-run summary before applying changes.
- Record matching uses n8Tracks stable IDs first and unique provider IDs where applicable; titles alone never establish identity.
- Identical records are no-ops and new records are created.
- Conflicts require an explicit choice to keep existing data, use imported data, or create a separate record where the model permits it.
- Imported data never rewrites immutable Version inputs; conflicting inputs create or propose a new Version.
- Artwork is staged first, and the accepted import applies transactionally.
- One recovery operation groups the import so it can be undone for 30 days.
- Portable JSON never imports authentication credentials.

### Search and saved views

- One global search spans Song title, Concept, lyrics, Version name/notes, styles, prompts, Tags, comments, Album/Playlist names, and relevant Suno metadata.
- SQLite FTS5 provides full-text indexing and search.
- Filters include workflow state, genre, Tags, Album, Playlist, model, creation date, Generation rating, Selected Generation status, local-audio availability, and archived status.
- Results may be sorted by title, created or updated date, rating, workflow state, and last Generation date.
- Results represent Songs and display Concept, state, genre/Tags, creation date, workspace, Selected Generation, and matching Version context to distinguish duplicate titles.
- Search highlights the matched field or lyric excerpt and paginates results.
- Users may save searches and filtered views in V1.

### Home dashboard

- The dashboard includes recently edited Songs, Songs grouped by workflow state, and Songs without Selected Generations.
- Operational sections include Unmatched Files, pending Suno reviews/conflicts, failed synchronization or unavailable workspaces, and recent undo-eligible MCP operations.
- Quick actions include New Song, Scan Library, Sync with Suno, and opening the last active Song.
- Users may hide and reorder sections, but V1 does not include a general custom-dashboard builder.

### Genres and Tags

- A Song may have multiple Genres selected from a user-managed list with autocomplete; n8Tracks does not impose a universal taxonomy.
- Users may rename, merge, and delete Genres with dependency warnings and reassignment where required.
- Tags are colored, free-form organizational labels for concepts such as mood, theme, audience, project, holiday, language, or instrumentation.
- Tags use autocomplete and may be assigned to Songs.
- Suno style prompts and provider display tags remain Version/Generation data and do not automatically become catalog Genres or Tags.
- Imports and AI clients may suggest Genres or Tags, but assignments occur only through an authorized catalog change.

### Artists and credits

- Artists are reusable first-class catalog records rather than free-text Song fields.
- A Song has one primary Artist and may have multiple featured Artists.
- An Album may have an Album Artist independent of individual Song credits.
- Artist records include display name, optional aliases, notes, artwork, and external links.
- Users may configure a default Artist for newly created Songs.
- Suno creator/display names remain separate provider metadata and do not automatically create or replace n8Tracks Artist records.
- Duplicate Artist names are allowed but surfaced for confirmation.

### Release metadata

- Songs support optional release date, original release date, explicit-content flag, copyright text, publishing text, ISRC, language, and external streaming/distribution links.
- Albums support optional release metadata including UPC/EAN; disc and track numbers remain Album-membership properties.
- Release metadata is editable through the UI, API, and appropriately scoped MCP tools.
- Distribution submission, royalty tracking, and ONCE integration remain outside V1.

### Notifications and diagnostics

- An in-app notification center reports completed or failed scans, imports, exports, backups, restores, synchronizations, migrations, and MCP bulk operations.
- Dashboard badges identify conflicts, Unmatched Files, and other items requiring attention.
- Success notifications may dismiss automatically; warnings and failures remain until dismissed.
- Failure notifications link to details and a retry action when retry is safe and applicable.
- Users may download redacted diagnostic logs.
- V1 does not send email, SMS, mobile push, or external webhook notifications.

### Album membership

- A Song may belong to any number of Albums but appears at most once within the same Album.
- Album membership stores disc number, track number, and ordering independently of the Song record.
- Reordering or renumbering an Album track does not change the Song itself.
- An Album may contain Songs without Selected Generations and marks those entries as incomplete.
- Changing a Song's Selected Generation automatically changes which Generation the Album entry represents.

### Playlist membership

- A Song may belong to any number of Playlists but appears at most once within the same Playlist.
- Users may drag to reorder Songs within a Playlist.
- Playlist entries reference Songs, never specific Generations.
- A Song without a Selected Generation remains visible with a `No Selected Generation` indicator.
- Deleting a Playlist never deletes its Songs.
- Deleting a Song removes its Album and Playlist memberships after the confirmation warns the user about those dependencies.

### Artwork

- Every Generation retains its Suno-provided artwork.
- A Song has independently selected artwork that initially defaults to the artwork of its Selected Generation.
- After the user explicitly selects or customizes Song artwork, changing the Selected Generation does not silently replace it.
- A user may select Song artwork from any of the Song's Generations or upload a local image.
- Albums and Playlists each have independently selected artwork.
- Selected artwork is copied into n8Tracks-managed storage so the catalog does not depend on expiring Suno URLs or the continued availability of a Generation.
- V1 accepts JPEG, PNG, and WebP artwork up to 25 MB and validates file contents rather than trusting filename extensions.
- n8Tracks preserves the original image and generates optimized thumbnails.
- Users may position a non-destructive square crop without modifying the original.
- Artwork copied or selected for a Song, Album, or Playlist becomes an independent managed asset.
- Deleting a source Generation eventually deletes artwork owned only by that Generation, but does not delete independent Song, Album, or Playlist copies.
- Replaced selected artwork remains recoverable through the 30-day retention foundation.

### Playback

- V1 playback is an editing and review aid, not a full music-player experience.
- Albums and Playlists contain ordered Songs, not Generations.
- Playing a Song resolves to that Song's current Selected Generation.
- If the Song has no Selected Generation, n8Tracks asks the user to choose a Generation instead of guessing.
- A user may play any individual Generation directly without selecting it for the Song.
- Playback provides play/pause, seeking, volume, and fast switching or A/B comparison among a Song's Generations and associated files.
- Playback remains available while the user reviews or edits the current Song.
- V1 does not require queue management, shuffle, repeat, or continuous Album/Playlist playback.
- Playback first honors a Song-level preferred audio file when one is configured; otherwise it resolves the Selected Generation and follows that Generation's Preferred Audio File and format-fallback rules.
- When no local audio file is available, n8Tracks may make a best-effort attempt to stream from the latest synchronized Suno media URL.
- Suno URL playback is not guaranteed and n8Tracks does not proxy or permanently cache provider audio solely to make streaming reliable.
- If Suno streaming fails or its URL has expired, the UI links the user to the corresponding Suno Song page for playback and may recommend refreshing synchronization.
- The player indicates whether the current audio source is local or streamed from Suno.

## Technical Architecture

### Deployment baseline

- The authoritative n8Tracks web application, API, and database run in one local Docker container.
- SQLite database stored under persistent application data.
- Audio and artwork remain outside SQLite.
- One recursively scanned media root mounted into the container; the mount may be read-only.
- Single-user HTTP authentication suitable for reverse-proxy deployment.
- No separately managed database required for V1.
- Target scale: tens of thousands of Songs, Versions, and Generations.

### Backup and restore

- n8Tracks uses SQLite's online backup mechanism to create a consistent backup while the application is running.
- Users may create a backup manually or schedule backups to a configured mounted backup directory.
- A backup contains the database, configuration, managed artwork, and other n8Tracks-managed assets.
- Externally mounted audio files are excluded because n8Tracks does not own them.
- The default schedule is daily with seven retained backups; schedule and retention are configurable.
- Users may download a portable backup archive.
- Restore runs as a maintenance workflow and validates the archive and database schema version before replacing current data.
- n8Tracks creates a safety backup immediately before a restore or database migration.

### Application upgrades and migrations

- Startup detects required schema migrations and creates a verified safety backup before applying them.
- Supported forward migrations run automatically.
- If a migration fails, application startup stops rather than using a partially migrated database.
- When safe, n8Tracks restores the pre-migration database automatically and retains diagnostic logs.
- The UI and health endpoint expose migration and application health status.
- Rolling back to an older container does not automatically downgrade the database; the operator must restore a compatible backup.
- Release documentation identifies any minimum supported upgrade path when skipping many versions is unsafe.

### Media mount

- V1 supports one configured media mount, recursively including any number and arrangement of subdirectories.
- Directory structure has no semantic meaning to n8Tracks and users may organize it however they choose.
- n8Tracks scans supported file types throughout the mount without renaming, moving, or reorganizing them.
- Symbolic links may not escape the mounted root, and UI/API/MCP input may not traverse outside it.
- If the mount becomes unavailable, its known assets are marked unavailable without deleting their records or associations.
- Scanning resumes and assets reactivate when the mount returns.

### External MCP gateway

- The internet-accessible MCP server is a separate, thin wrapper over the n8Tracks API.
- It stores no catalog, media, artwork, or durable application data.
- It translates MCP tool calls into authenticated n8Tracks API requests and returns the API results.
- It connects to the locally hosted n8Tracks application, including Synology deployments, through Tailscale or another private network path.
- The local n8Tracks API remains the authoritative enforcement point for validation, authorization, immutability, and other business rules.
- A connectivity failure returns an explicit unavailable/error response and does not serve stale catalog data.

### Public API contract

- The application exposes versioned REST/JSON endpoints under `/api/v1` and publishes an OpenAPI description generated from the implementation.
- Stable internal IDs remain valid API identifiers; shortcodes and provider IDs are also supported through explicit lookup and resolver operations.
- Listing endpoints use consistent cursor pagination, filtering, sorting, and optional field-selection conventions.
- Errors use structured Problem Details responses.
- Revision numbers or ETags enforce optimistic concurrency, and create/bulk-write operations support idempotency keys.
- Long-running scans, imports, exports, backups, restores, and synchronization use asynchronous job resources with status and progress.
- Generated local API documentation is available from the application.
- Fields and endpoints are deprecated before removal, with backward compatibility maintained within a major API version.

### MCP tool design

- MCP exposes task-oriented tools rather than mechanically mirroring every REST endpoint.
- Tools support discovery and search by title, text, filters, shortcode, stable ID, and provider ID; shortcodes are optional conveniences, not required input.
- Tools can retrieve complete Song, Version, and Generation context; create one or many Songs; update Song metadata/state; create Versions; edit mutable Version inputs; manage lineage; evaluate/select Generations; manage collections and taxonomy; and upload/select artwork.
- Tools expose configurable workflow states, relationship types, schemas, and other values needed to construct valid writes.
- Results include stable IDs, shortcodes, revision numbers, validation details, and operation IDs.
- V1 MCP tools do not expose deletion, credential management, filesystem mutation, or Suno automation.

### MCP authorization and recovery

- MCP credentials use capability-based scopes: `catalog.read`, `songs.write`, `versions.write`, `collections.write`, `generations.evaluate`, `artwork.write`, and `catalog.bulk-write`.
- V1 does not expose MCP scopes for deletion, authentication administration, application configuration, filesystem management, or Suno synchronization.
- Every MCP write records a lightweight recovery change set containing timestamp, client identity, affected records, and before/after values.
- All changes originating from one agent request share an operation ID, including bulk requests such as creating 20 Songs.
- The web UI provides `Undo MCP Operation` for 30 days.
- Undo reverses catalog changes only. It does not reverse Suno activity or restore overwritten, moved, or deleted physical files.
- This recovery history is not a permanent general-purpose compliance audit log.

### MCP bulk-write semantics

- The API validates an entire bulk-write request before changing catalog data.
- Catalog-only bulk writes execute in one SQLite transaction: all items succeed or none are committed.
- Every bulk request requires an idempotency key. Retrying the same completed request returns its prior result and does not duplicate Songs or other records.
- Validation failures identify the item index, affected record when applicable, field, and actionable error.
- V1 supports up to 100 items in one synchronous bulk operation.
- Binary artwork is staged before the catalog transaction commits. If staging, validation, or commit fails, staged files are discarded and catalog changes are rolled back.
- One completed bulk transaction corresponds to one MCP recovery operation ID.

### Performance baseline

On modest Synology-class hardware with approximately 50,000 Songs and 100,000 mounted files:

- Normal catalog pages become usable within two seconds.
- Search and filter results return within two seconds for typical queries.
- Ordinary catalog create and update operations complete within one second, excluding uploads and external services.
- Playback of an available local audio file begins within two seconds.
- Startup media scanning does not block access to the web UI.
- Media scanning is incremental, runs in the background, and exposes progress.
- Large import/export jobs and long-running bulk operations expose progress and completion status rather than holding a browser request open indefinitely.
- Synchronous MCP bulk writes are capped at 100 records in V1. Agents split larger work into separately idempotent requests and receive a separate recovery operation ID for each chunk.

### Browser and device baseline

- n8Tracks is a desktop-first editor and catalog manager, not a mobile-first listening application.
- The UI uses standards-based web development and is intended to function in current Chrome, Edge, Firefox, and Safari releases.
- V1 does not promise optimized tablet or mobile workflows.
- Automated end-to-end browser testing may target one Chromium browser rather than duplicating the complete suite across every browser engine.
- Cross-browser compatibility is supported through web standards, shared unit/integration coverage, and focused manual smoke testing rather than a full per-browser CI matrix.
- The Chrome extension supports current Chrome and compatible Chromium-based browsers where extension APIs permit.

### Preliminary entities

- `Song`
- `SongState`
- `Version`
- `Generation`
- `GenerationEvent` (internal and optional)
- `SunoWorkspace`
- `ProviderRecord` / raw provider payload
- `ProviderTombstone`
- `EditorRevision`
- `GenerationRatingComment`
- `SongRelationshipType`, `SongRelationship`, `VersionSource`, and external provider-source references
- `Album` and `AlbumSong`
- `Playlist` and `PlaylistSong`
- `Tag` and tag assignments
- `Genre` and Song genre assignments
- `Artist`, Song artist credits, and Album artist credits
- release metadata and external links
- `Notification`
- `Asset` and asset associations
- `ExternalClientCredential` and scoped access grants
- deletion-retention records/tables

### Identity principles

- Internal stable IDs are independent of titles, display version numbers, and provider IDs.
- Suno ID is unique for a Suno Generation.
- Version display numbers are unique within a Song but are not database identity.
- A Generation has exactly one owning Song and Version.
- A Song has zero or one Suno workspace association.
- A Suno workspace has zero or more associated Songs.
- Provider tombstones prevent intentionally deleted remote objects from being reimported.

### Human-facing shortcodes

- GUID-style internal stable IDs remain authoritative database identity, while every Song, Version, and Generation also has a conversational shortcode.
- Song shortcodes use a monotonically increasing local sequence beginning with `n8-1`, then `n8-2`, and so forth, without fixed-width zero padding.
- Version shortcodes append the immutable hierarchical Version number, for example `n8-12345-v1.1`.
- Generation shortcodes append an immutable ordinal within that Version, for example `n8-12345-v1.1-g3`.
- Shortcodes are stored and displayed in lowercase; lookup is case-insensitive, so `N8-1` and `n8-1` resolve identically.
- Song shortcode numbers are never reused, including after permanent deletion. Restored records retain their prior code.
- Generation ordinals are never renumbered after deletion or movement.
- Each Song, Version, and Generation screen displays its shortcode with a one-click copy-to-clipboard control.
- APIs accept stable IDs or complete shortcodes through typed reference parameters and provide a resolver endpoint that returns entity type, canonical ID, canonical shortcode, and current status.
- MCP tools accept references such as `song_ref`, `version_ref`, and `generation_ref`, while also providing search/list tools for natural-language requests such as finding a Song by title and listing its Versions and Generations.
- Imported records receive new local shortcodes when a source code conflicts; original codes may be retained as provenance but not as ambiguous active identifiers.
- When a Generation moves to a new Song, it receives a new canonical shortcode under the destination Song and Version.
- Its prior shortcode remains a permanent historical alias that resolves to the same Generation with a `moved` indicator and the current canonical shortcode; historical aliases are never reused.
- Title-search operations support exact, prefix, and full-text matching and return shortcode, title, Concept, Artist, workflow state, updated date, and Selected Generation status.
- If multiple Songs match a title, read operations may return every candidate, but write operations require a stable ID or shortcode and return an `ambiguous_reference` error with candidate shortcodes rather than choosing silently.

### Prescribed implementation stack

- Backend: ASP.NET Core on the current .NET LTS release.
- Persistence: EF Core for ordinary data access, with explicit SQL for SQLite FTS5 and performance-sensitive queries where appropriate.
- Frontend: React, TypeScript, Vite, and Mantine as the component/application framework.
- Background processing: ASP.NET hosted services with durable SQLite-backed job records.
- Browser extension: TypeScript, Chrome Manifest V3, and standards-compatible WebExtension APIs where practical.
- External MCP gateway: a thin TypeScript or .NET service generated against the OpenAPI contract, containing no duplicated business logic.
- Testing includes backend unit/integration tests, API contract tests, frontend component tests, Chromium-based end-to-end tests, and fixture-based extension adapter tests.
- A multi-stage Docker build produces one local application image containing the backend and compiled frontend assets.

### Observability

- Application logs are structured JSON with configurable log levels, retention, and maximum disk usage.
- Correlation IDs span the MCP gateway, API requests, background jobs, and extension operations where technically possible.
- Health endpoints cover application availability, database access, migration state, media-mount availability, and background-job processing.
- A UI diagnostics page shows component versions, connectivity, recent job failures, database health, and storage availability.
- Credentials, cookies, authorization headers, lyrics, prompts, and raw provider payloads are redacted from logs by default.
- No external analytics or telemetry is enabled by default.
- Users may configure optional OpenTelemetry export to an endpoint they control.

## Local Audio and Metadata Assets

### Audio cardinality

- A Song may have multiple local audio files.
- Those files may represent different encodings or formats of the same selected output, including WAV, MP3, M4A, and other configured supported formats.
- A local audio file belongs to exactly one Song and must never be associated with two Songs.
- An audio file may optionally reference the specific Generation from which it was downloaded or derived.
- A Generation may have multiple associated local audio files.
- One audio file may be designated the Song’s primary local audio file without introducing a separate Master entity.
- Changing the Selected Generation does not automatically delete or relink existing audio files.

### Discovery and matching

- Exact provider identity takes precedence over title or filename matching.
- A downloaded filename containing the complete Suno Track ID is sufficient to match the file directly to that Generation and its owning Song.
- V1 automatically associates a file only when its filename contains a complete Suno ID that uniquely identifies an existing, non-tombstoned Generation.
- A standard human-readable filename should include artist, title, and an explicit Suno ID token, for example `Song Artist - Song Title (suno-0c90d621-e30c-4c76-814a-e1fdeb500582).wav`.
- When no provider ID is available, n8Tracks may use filename, title, directory, embedded media metadata, duration, and other evidence to suggest candidate matches in the Unmatched Files view, but it does not associate them automatically.
- Title-only matching is never sufficient for an automatic high-confidence match because duplicate Song titles are allowed.
- Ambiguous matches require user confirmation.
- Missing files change the asset-association status but do not delete catalog or Generation records.
- A file whose Suno ID refers to a locally deleted or tombstoned Generation remains unmatched and explains that its former Generation was deleted.

### Observed Suno metadata sidecar

The supplied `Candy_Cane_Shake.txt` example demonstrates that a download companion file may contain enough information for deterministic association and provider fidelity, including:

- Human-readable title, artist, prompt/style text, lyrics, and generated timestamp.
- `Track ID`, which matches the raw provider `id` and uniquely identifies the Generation.
- `created_at` and provider update timestamps.
- `is_trashed` and `is_hidden` state.
- Model information such as `major_model_version` and `model_name`.
- Duration, media URLs, cover-art URLs, caption, tags, and display tags.
- Full raw provider response for forward-compatible preservation.
- `batch_index`, which may indicate an output position but does not by itself identify the Create request.

The sidecar’s `Generated` timestamp differs from the provider `created_at` timestamp and must not be assumed to represent the same event. Matching should use Track ID, not timestamp equality.

### Extension-managed bulk downloads

- The browser extension provides bulk download capabilities comparable to a dedicated Suno track-export extension.
- It reads the user’s Suno library and lets the user select multiple Generations for download.
- The user may select the desired available audio format or formats, including WAV, MP3, and M4A when Suno exposes them.
- The extension downloads every selected file automatically and applies the standard deterministic filename containing the complete Suno Track ID.
- V1 downloads files through the browser to the user’s computer. The user is responsible for copying or moving those files into a mounted volume that n8Tracks can read.
- Direct extension-to-n8Tracks transfer and ingestion are deferred to V2.
- The Suno Track ID in the filename provides the durable association to the n8Tracks Generation and Song.
- A companion metadata file is not required. The extension retrieves current metadata from Suno and synchronizes it into n8Tracks rather than duplicating it beside every audio file.
- Metadata synchronization and audio downloading are separable operations: a Generation may be synchronized without downloading audio, and audio may be redownloaded without creating another Generation.
- The downloaded asset record stores its provider ID, format, filename, download time, and optional file hash.
- The existing metadata-file format illustrated by `Candy_Cane_Shake.txt` is useful as research evidence but is not a required n8Tracks interchange artifact.

### Mounted-library scanning

- n8Tracks scans configured mounted media roots during application startup.
- A user can start an immediate scan with a `Scan Library` action.
- n8Tracks runs a configurable scheduled scan, defaulting to every 15 minutes.
- Correctness must not depend on filesystem change notifications because Docker, NAS, and network-mounted filesystems may not reliably deliver them.
- n8Tracks catalogs each distinct file path. It does not rename, move, overwrite, delete, or silently deduplicate user-managed files.
- When a registered file disappears, n8Tracks marks the asset `Missing` without deleting its record or association.
- If the same path becomes readable again, the next scan automatically returns that asset to `Available`.
- The user remains responsible for filesystem conflicts, duplicate downloads, overwrites, and manual filenames. n8Tracks only observes the resulting mounted files.

### Preferred playback file

- Each Generation may have one explicitly selected Preferred Audio File.
- A Song may also have a preferred Song-level audio file that is not associated with a specific Generation, supporting mastered, edited, or externally produced files.
- Until the user selects one, n8Tracks chooses the highest-ranked available format in this order: WAV, M4A, MP3, then another supported browser-playable format.
- The user may override the automatic choice with any available audio asset associated with that Generation.
- If the explicitly preferred file is missing, n8Tracks temporarily plays the highest-ranked available alternative without changing the stored preference.
- When the preferred path returns, n8Tracks automatically resumes using it.
- Multiple assets of the same format remain cataloged as distinct paths and are not automatically deleted or collapsed.

### Unmatched files

- Each library scan lists every supported audio file that cannot be confidently associated with an existing Song or Generation as an Unmatched File.
- From the Unmatched Files list, the user must select a Song and may optionally select one of that Song's Generations.
- A Song-only association is valid for mastered, edited, or externally produced audio that does not correspond exactly to a Suno Generation.
- A Generation association is preferred when the source Generation is known.
- A Song-level file may be selected as the Song's preferred playback file.
- V1 does not maintain an ignored-file registry. If the user takes no action, the file remains visible in the Unmatched Files list on subsequent scans.
- Hiding or dismissing unmatched files may be introduced in V2.
- Matching or leaving a file unmatched never renames, moves, or modifies the physical file.

### Lyrics editor

- Lyrics are stored as plain Unicode text with normalized line endings.
- The editor syntax-highlights structural and performance tags such as `[Verse]`, `[Chorus]`, `[Bridge]`, and other Suno instructions.
- Parenthetical backing-vocal text receives distinct highlighting.
- Tag autocomplete assists the user but does not enforce a closed vocabulary.
- Unmatched brackets and malformed tags produce non-blocking warnings.
- Unknown tags are preserved and are never silently rewritten, reformatted, or removed.
- Apart from line-ending normalization, the text sent to Suno or returned through the API/MCP preserves the user's content exactly.
- MCP may directly update lyrics only on a mutable Version. Editing lyrics derived from an immutable Version creates a new Version and leaves the source unchanged.

## Edge Cases and Security

### Established edge cases

- Duplicate Song titles are allowed and surfaced contextually.
- A Version may have no Generations indefinitely.
- A Version may contain Generations from repeated Create actions.
- A selected Generation may originate from any Version.
- A user may create a new Version from an old branch.
- Direct Suno edits that produce a new Generation create a corresponding new Version during import.
- An unmatched import is reviewed rather than silently creating or attaching records.
- Reimporting an existing Suno ID does not create a duplicate Generation.
- Suno Trash archives a Generation; Suno restore reactivates it.
- One Song may own multiple audio files, but one audio file may never belong to multiple Songs.
- Moving a Generation into a new Song removes it from the original Song.
- Deleted remote-linked records require tombstones to avoid unwanted resurrection.

### Security baseline

- Credentials and secrets are never logged or exposed through ordinary API/MCP responses.
- Destructive actions require clear warnings and disclose affected dependencies.
- Provider failures do not block access to the local catalog.
- Mounted media may be read-only.
- Initial setup creates one administrator account; there is no public registration, email password reset, OAuth login, or multi-user support in V1.
- Passwords are stored only as Argon2id hashes, and browser sessions use secure HTTP-only cookies when HTTPS is available.
- API, browser-extension, and MCP-gateway access use independently named and revocable credentials rather than the administrator password.
- Credential scopes distinguish read access from supported catalog-write capabilities; destructive MCP scopes are not provided in V1.
- n8Tracks supports HTTPS reverse-proxy deployment while allowing local HTTP operation on trusted networks.

### Concurrent editing

- Each editable record has a revision number used for optimistic concurrency.
- Web UI, API, extension, and MCP writes identify the revision originally read.
- A stale write is rejected rather than resolved with last-write-wins behavior.
- Conflict responses identify conflicting fields and return the current record revision and values.
- The web UI supports reloading, comparing, and manually reapplying changes.
- MCP receives a structured conflict response so the agent can reread and retry deliberately.
- A bulk operation fails atomically if any included record has a revision conflict.

## V1 Release Gate

V1 is releasable when the following end-to-end capabilities meet their acceptance criteria:

- Install the local container and complete first-run administration and storage setup.
- Create, edit, branch, archive, and delete Songs and Versions while enforcing Version immutability after generation.
- Export/import the Suno library with differential matching, ignored items, Trash/archive synchronization, workspace mappings, and available lineage.
- Prepare a Version for Suno generation through the extension, including supported lineage inputs, while leaving the final Create action to the user.
- Import resulting Generations and retain the exact Version inputs that produced them.
- Rate, comment on, compare, select, move, and delete Generations with correct dependency handling.
- Bulk-download Suno audio with deterministic provider IDs, then scan and match files from the mounted media volume.
- Manage artwork, Artists, Genres, Tags, Albums, Playlists, configurable workflow states, relationships, and release metadata.
- Search, use saved views, and operate the dashboard at the target catalog scale.
- Use the documented API and external thin MCP gateway for scoped direct catalog discovery and editing.
- Back up, restore, export, import, migrate, and diagnose the application safely.
- Complete TS-001 and TS-002 with working automation or explicitly documented degraded/manual fallbacks where Suno does not expose reliable data or controls.

## Installation and Operations

- Publish a supported Docker image and Docker Compose example.
- Require persistent `/data`, one `/media` mount, and an optional `/backup` mount.
- Environment variables configure port, public/base URL, timezone, log level, and initial filesystem paths.
- A first-run wizard creates the administrator, verifies writable application storage, checks media availability, and configures backup defaults.
- An extension-pairing flow creates a scoped credential without manual token copying where technically feasible and shows the exact n8Tracks origin requiring permission.
- Container health checks support Docker, Portainer, Synology Container Manager, and common reverse proxies.
- Documentation covers installation, extension pairing, upgrades, rollback, backups, restore, and disaster recovery.
- Kubernetes and OKD manifests are not V1 deliverables, although the image remains portable to container orchestrators.

## Licensing and Provider Boundaries

- The n8Tracks application, browser extension, and MCP gateway are released under Apache License 2.0.
- n8Tracks is an independent, unofficial project and is not affiliated with or endorsed by Suno.
- `Suno` is used descriptively for compatibility and is not part of n8Tracks branding; the project does not bundle Suno logos or imply official API access.
- Users provide and maintain their own legitimate Suno account and authenticated browser session.
- n8Tracks does not bypass subscription gates, access controls, rate limits, or provider restrictions.
- Suno DOM automation is best-effort and subject to provider changes and applicable terms.
- Provider-specific behavior remains isolated behind an integration boundary so future providers can be added without redefining the core catalog model.

## Explicit V1 Exclusions

- Direct unofficial Suno API automation.
- ONCE distribution integration.
- Multiple generation providers.
- Multi-user collaboration and RBAC.
- General-purpose file moving, renaming, deletion, transcoding, or mastering outside the extension-managed Suno download workflow.
- User-facing Trash/recovery UI.
- General immutable audit logging.

## Open Decisions

1. Feasibility of correlating outputs from one Suno Create action through provider IDs, DOM observation, network observation, timestamps, or user confirmation; resolve through TS-001.
2. Suno field inventory, types, defaults, mode/model applicability, and JSON mappings.
3. Suno limits and exact automation mechanics for Cover, Remix, Reuse Prompt, Mashup, Sample, Inspiration, and Voice; resolve through TS-002.
4. Exact provider-tombstone and 30-day retention table schemas.
5. External MCP authentication protocol and final mapping of the accepted capability scopes.
6. Final supported browser-playable audio extensions beyond WAV, M4A, and MP3.
7. Exact API response schemas for MCP bulk edits, workspace reassignment, and long-running jobs.
8. Detailed accessibility acceptance criteria beyond the established semantic HTML, keyboard, labeling, focus, and contrast baseline.
9. Future user-facing Trash restoration behavior; no Trash UI is required for V1.

## Required Technical Spikes

### TS-001: Suno generation correlation

Determine what Suno exposes for one Create action and its resulting outputs.

Test and document:

- Whether a request/batch identifier exists in the DOM, page state, network traffic, or exported provider data.
- Whether output records share any reliable creation identifier.
- Whether `batch_index` is stable, what its values mean, and whether it can be combined with another field to correlate outputs from one Create action.
- Whether the available timestamp represents request start, generation start, completion, publication, or another event.
- Whether two outputs from one Create action receive equal or materially different timestamps.
- Whether the browser extension can observe the Create action and reliably associate the resulting Suno IDs without invoking the action itself.
- Behavior when one output succeeds and another fails or arrives later.
- Behavior across page refreshes, navigation, multiple browser tabs, and concurrent Create actions.

The data model must permit no event association, confidently observed association, and user-confirmed association without changing Generation ownership.

### TS-002: Suno lineage extraction and automation

Determine what lineage Suno exposes for existing library items and how reliably the extension can initiate each supported source action.

Test and document:

- Provider fields and page state identifying parent Songs, Generations, playlists, and relationship/action types.
- Source-count limits for Mashup and playlist-backed Inspiration.
- Whether Remix, Cover, Reuse Prompt, Sample, Inspiration, and Voice produce distinguishable lineage metadata after generation.
- How to verify that the intended source Song or playlist is selected on the Create page before populating other Version inputs.
- Behavior when a source is trashed, unavailable, not imported, outside the current workspace, or requires a subscription feature the user does not have.
