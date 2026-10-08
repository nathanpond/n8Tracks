# Portable catalog schema: record types

This is the list the claim "JSON export contains the complete portable catalog model" is measured against (milestone M8, epic #22). One entry is one record type in the export. The export format, its field-level schema, and the test that walks the application's data model against this list are built by the first M8 story; this file is the list itself and is amended only with the user's agreement.

Format name `n8tracks.catalog`, format version 1.

## Record types in the export (23)

| # | Record type | What it holds |
|---|---|---|
| 1 | Songs | Title, Concept, notes, workflow state, shortcode and its aliases, Selected Generation, current Version, links, used Version numbers, created and updated times |
| 2 | Versions | Number, name, notes, kind, state, frozen flag, lyrics, styles, prompts, and every Suno option, including voices (Suno personas) and inspiration playlists by Suno ID with their names |
| 3 | Generations | Ordinal, shortcode, state, remote state, normalised Suno fields, Generation Event link |
| 4 | Lineage | Each Version's sources and file-input notes, with relationship type, including sources that are not in the catalog |
| 5 | Relationships | Song-to-Song relationships and the configurable relationship types |
| 6 | Ratings and comments | Each Generation's rating and comments |
| 7 | Workflow states | Name, colour, order, hidden flag |
| 8 | Tags | Name, colour, and the Songs each is on |
| 9 | Albums | Details, release details, artwork reference, ordered entries with disc and track numbers, links |
| 10 | Playlists | Details, artwork reference, ordered entries |
| 11 | Provider IDs | Suno IDs of Generations, workspaces, sources, Suno personas, and Suno playlists |
| 12 | Raw provider metadata | The latest raw clip JSON kept for each Generation |
| 13 | Asset references | Artwork records (with crop positions), local audio file records and their associations, preferred-audio choices, and download records; never audio content |
| 14 | Artists | Name, aliases, links, artwork reference, and credits on Songs |
| 15 | Genres | Name and the Songs each is on |
| 16 | Release metadata | A Song's release details |
| 17 | Workspaces | Suno workspaces and the Songs associated with each |
| 18 | Ignore list | Ignored Suno items |
| 19 | External references | Sources and Suno items referred to but not imported |
| 20 | Saved views | Name, query, order |
| 21 | Dashboard layout | Section order and visibility (the saved arrangement; absent when the user never saved one) |
| 22 | Editor revisions | Each Version's editing-history snapshots |
| 23 | Tombstones | Records of deleted Suno clips that must not be imported again |

## Not in the export

- **The claim's own exclusions:** passwords, sessions, API and MCP credentials, file-system-internal secrets, and audio file contents.
- **Operational state:** background jobs, notifications, attention dismissals, Generate-on-Suno requests, pending file deletions, AI operation and import history with their change sets, idempotency keys, and logs.
- **Instance settings:** the time zone, personal defaults for new Versions, the Suno model list, and the backup, scan, and logging settings. A Version still carries the model it used.
- **Derived data:** the search index (and its version setting), stored counts, and the read-time artwork fallback of #318.
- **Staged or retained data:** a staged import, pending Suno reviews, and the 30-day retention store (its groups, records, and released audio files). A live record's reference to a retained record is exported as an external reference.

The instance-settings exclusions were agreed with the user during M8 planning (2026-10-04).
