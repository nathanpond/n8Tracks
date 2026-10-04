# n8Tracks

Product requirements live in `docs/PRD.md`.

## n8SDLC project

This project is managed by the n8SDLC workflow (GitHub Issues = the plan; `/n8-stat` shows where things stand). If a change made in this session deviates from what planned issues assume — different library, provider, architecture, dropped/added scope, or amending a declared invariant below — do two things before finishing:
1. Append an `## Ad-hoc` entry to `.n8/decisions.md` (format documented in that file's header) naming the change, the why, and the milestones/issues likely affected.
2. Tell the user which future milestones may now have stale plans and suggest running `/n8-replan`.

Background work (shells, monitors, background subagents) follows `reference/background.md` in the n8SDLC plugin. Every launch is bounded and announced with what it's for and when it should finish. Overdue work is checked, not waited on. A turn never ends with work still running unless the reply names it. `/n8-subs` lists everything live.

Separately: if a `/n8-*` skill's own instructions failed, misled you, or were silent on something this session, tell the user and offer `/n8-feedback` — it packages the learning as an issue on the plugin repo, stripped of project specifics, and sends nothing until the user has reviewed the exact text.

### Invariants

No story may breach these without an explicit conversation with the user. Changing one is plan drift: log it as an `## Ad-hoc` entry and run `/n8-replan`. All are **test-enforced**; the epic that owns each guard is named.

1. **Version immutability.** A Version's creation inputs and lineage sources cannot change once a Generation is attached, through any path (UI, API, import, MCP). Guard: epic #7. guard: #69 (planned); extended by #122, #140 (planned); the MCP path extends it in M7
2. **Media mount is never written (V1).** n8Tracks never writes, renames, moves, or deletes under `/media`, and no path input escapes the mount root. Expected to change in V2. Guard: epic #16. guard: deferred → M5
3. **Imports never silently overwrite.** Suno import and portable import never change existing catalog data without an explicit user choice. Guard: epics #14, #22. guard: #140 (planned) for Suno import; portable import deferred → M8
4. **Suno is never mutated destructively (V1).** Nothing deletes or trashes Suno content, and the extension never clicks Create, Publish, or Delete on its own. Expected to change in V2. Guard: epic #15. guard: #133 (planned)
5. **One business-rule layer.** The web UI, API, and extension go through one application-service layer. The MCP gateway calls only the REST API and holds no business logic or catalog data. Guard: epics #4, #21. guard: #25, #33 (merged)
6. **Sensitive data is never logged.** Credentials, tokens, cookies, lyrics, prompts, and raw provider payloads are redacted from logs and diagnostics by default. Guard: epic #4. guard: #27 (merged)
7. **No destructive MCP capability (V1).** No MCP tool or scope can delete records, manage credentials, touch the filesystem, or drive Suno. Guard: epic #21. guard: deferred → M7
8. **Warnings are errors.** Analyzers and linters stay on in the backend, frontend, extension, and gateway; any suppression carries a one-line rationale. Guard: epic #5. guard: #43, #44 (planned)
