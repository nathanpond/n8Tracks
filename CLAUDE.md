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

1. **Version immutability.** A Version's creation inputs and lineage sources cannot change once a Generation is attached, through any path (UI, API, import, MCP). Guard: epic #7. guard: #69 (planned); extended by #122, #140 (planned), and on the MCP path by #254 (planned)
2. **Media mount is never written (V1).** n8Tracks never writes, renames, moves, or deletes under `/media`, and no path input escapes the mount root. Expected to change in V2. Guard: epic #16. guard: #205 (planned); extended to imported audio paths by #259 (planned)
3. **Imports never silently overwrite.** Suno import and portable import never change existing catalog data without an explicit user choice. Guard: epics #14, #22. guard: #140 (planned) for Suno import; #263 (planned) for portable import
4. **Suno is never mutated destructively (V1).** Nothing deletes or trashes Suno content, and the extension never clicks Create, Publish, or Delete on its own. Expected to change in V2. Guard: epic #15. guard: #133 (planned)
5. **One business-rule layer.** The web UI, API, and extension go through one application-service layer. The MCP gateway calls only the REST API and holds no business logic or catalog data. Guard: epics #4, #21. guard: #25, #33 (merged); the gateway half is extended by #254 (planned)
6. **Sensitive data is never logged.** Credentials, tokens, cookies, lyrics, prompts, and raw provider payloads are redacted from logs and diagnostics by default. Guard: epic #4. guard: #27 (merged)
7. **No destructive MCP capability (V1).** No MCP tool or scope can delete records, manage credentials, touch the filesystem, or drive Suno. Guard: epic #21. guard: #254 (planned)
8. **Warnings are errors.** Analyzers and linters stay on in the backend, frontend, extension, and gateway; any suppression carries a one-line rationale. Guard: epic #5. guard: two checks in the `guards` job of `.github/workflows/ci.yml` (merged). `scripts/check-suppressions.sh` reads configuration: tracked text and XML, for the listed forms of a suppression without a rationale and of a weakened setting (tested by `scripts/tests/check-suppressions/run.sh`; #43, #44, #194, #199). `scripts/check-canaries.sh` tests the effect: in a temporary copy, a known-bad file in every .NET project must fail the build with CS8618 and CA2200 as errors, and known-bad files in `web/`, `extension/`, and `e2e/` must fail `npm run lint` and `npm run typecheck` with the marked rules and error codes (tested by `scripts/tests/check-canaries/run.sh`; #199). Neither covers: a suppression scoped to files the canary is not in and written in a form the scanner does not list, a rule or diagnostic that has no canary switched off by a route the scanner does not read (a package, a computed configuration), a workflow or Dockerfile command line that takes its arguments or environment from outside the repository, a setting name assembled at run time, whether CI runs the gate and over which projects, which ESLint rule sets apply to which files (#197), or whether a rationale is true; the full list is under "Not covered by either check" in `scripts/check-suppressions.py`
