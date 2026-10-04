# Decision log

Append-only log of decisions made during planning and execution. Record real decisions (choices between alternatives, assumptions made, deviations from plan), not routine actions.

One `##` section per skill run, entries appended chronologically:

```markdown
## /n8-exec M1 — 2026-08-27

- **Decision:** Used SQLite for local dev database instead of running Postgres in Docker.
  **Why:** Plan specified "local relational DB" without naming one; SQLite needs no daemon and the ORM abstracts the difference. Low cost if wrong.
  **Issue:** #14
```

Changes made outside the n8SDLC commands that deviate from planned issues get an `## Ad-hoc` section:

```markdown
## Ad-hoc — 2026-08-27

- **Change:** Auth provider switched from Google to Okta.
  **Why:** Company standardized on Okta for SSO.
  **Affects:** M4 (auth stories), M6 (admin roles) — plans may be stale.
```

`/n8-replan` appends `— reconciled by /n8-replan <date>` to an ad-hoc entry once it has been processed.

---

## /n8-init — 2026-10-03

- **Decision:** Scaffolded the backend and xUnit test project only; frontend, browser extension, MCP gateway, and Docker are left to the roadmap.
  **Why:** User's choice; those components deserve planning in M0 rather than empty placeholders.
- **Decision:** `area:*` labels cover components that do not exist yet (web, extension, mcp, db, infra).
  **Why:** The PRD prescribes them, and roadmap issues need an area from day one.
- **Decision:** Moved the PRD to `docs/PRD.md`.
  **Why:** User left the location open; `docs/` is the conventional home and matches `area:docs`.
- **Decision:** Raised `Microsoft.AspNetCore.OpenApi` from the template's 10.0.5 to 10.0.12.
  **Why:** 10.0.5 pulls `Microsoft.OpenApi` 2.0.0, flagged by GHSA-v5pm-xwqc-g5wc; the build treats that warning as an error.
- **Decision:** Security findings are logged as draft advisories, not public issues.
  **Why:** User's choice; n8Tracks is deployed on users' own servers, so open findings would advertise exploits against live installs.

## /n8-roadmap — 2026-10-03

- **Decision:** Requirements source is `docs/PRD.md` (build-ready draft 2.2); its V1 Release Gate and Explicit V1 Exclusions are adopted unchanged.
- **Decision:** Environments are images only: `edge` on push to main, versioned on `v*` tag, published to GHCR; no stage.
  **Why:** User's choice; the product is self-hosted, so there is nothing to deploy to.
- **Decision:** MCP gateway is a .NET container; extension ships as a release zip loaded unpacked.
  **Why:** User's choices among the PRD's open options.
- **Decision:** Order is authoring core first (M2), with TS-001/TS-002 run early in the same milestone by the agent driving the user's Chrome.
  **Why:** User's choices; the spikes inform Generation Event and lineage schemas before they harden.
- **Decision:** Migration safety and backup/restore land in M2.
  **Why:** User will keep real catalog data from the first usable build.
- **Decision:** Accessibility target is WCAG 2.1 AA with automated checks plus keyboard walkthroughs (resolves PRD open decision 8).
- **Decision:** Four coverage claims are project-defined lists: "Mirror every available Suno field" means every option on the Suno Create screen (Simple, Advanced, Sounds), per the user; "every supported generation property" means the adapter field map; "every lineage relationship" means the TS-002 list; "complete portable catalog model" means the PRD list plus Artists, Genres, release metadata, workspaces, ignore list, external references, saved views, dashboard layout, editor revisions, and tombstones.
- **Decision:** Eight test-enforced invariants recorded in CLAUDE.md. Invariants 2 and 4 are V1-scoped; the user expects them to change in V2.
- **Decision:** M10 (Testing & Bug Fixes) and M11 (Release) were added as placeholders after M9 (Audit) at the user's request.
  **Why:** User's instruction. This departs from the n8SDLC convention that Audit is the final milestone; skills that assume Audit is last should match it by its `M9:` prefix and title.
- **Deferred to planning:** PRD open decisions 4 (retention/tombstone schemas), 5 (MCP auth protocol), 6 (audio extensions), 7 (API response schemas).

## /n8-plan M0 — 2026-10-03

- **Decision:** M0 is thirteen stories (#25–#37) under epic #4.
  **Why:** Coverage check asked for three splits (frontend serving / shell page; app image / gateway image; ServiceDefaults / AppHost).
- **Decision:** Defaults from the user: port 8787; `PUID`/`PGID`; amd64 and arm64 images; reverse-proxy sub-path supported; missing media is "degraded" with the container healthy; database or migration failure exits the container.
- **Decision:** Versioning: one product version; compatibility by major.minor; patch releases independent; gateway (M0) and extension (M4) warn on mismatch.
  **Why:** User does not want to re-release the extension for every app or gateway update.
- **Decision:** Aspire from the start (user, option 1): AppHost for local development only; ServiceDefaults registers no OpenTelemetry component unless `OTEL_EXPORTER_OTLP_ENDPOINT` is set.
  **Why:** User asked about image size and memory; this keeps production weight to the assemblies alone. Pulls part of epic #19's OpenTelemetry criterion forward from M6.
- **Decision:** Colour scheme follows the system with a light/dark/auto control.
- **Decision:** Invariant guards: 5 → #25 and #33; 6 → #27; 8 deferred to M1; 1, 2, 3, 4, 7 deferred to the milestones where their subjects first exist.
- **Decision:** Planner calls shown at the gate and not overridden: unknown `N8TRACKS_*` names warn; port-in-use exits; `X-Request-ID` on every response; foreign SQLite files refused; a deleted database file reports unhealthy; 2-second media check; stale health kept with a notice; unknown upstream version makes the gateway degraded; ownership-fix failure warns and continues; supplementary groups cleared; `restart: unless-stopped` in the Compose example; Aspire dashboard URL printed, not opened.
- **Decision:** The three stories produced by splitting were not put through a third executor simulation.
  **Why:** Their content had been through both passes; noted as a departure from the skill's "simulate any new story once".

## /n8-plan M1 — 2026-10-03

- **Decision:** M1 is eleven stories (#39–#49) under epic #5.
- **Decision:** From the user: the tag is the approval to publish; a release publishes `X.Y.Z`, `X.Y`, and `latest`; pre-release tags publish only the exact version as a GitHub pre-release; images carry provenance and an SBOM.
- **Decision:** Invariant 8 guard is #43 (rationale scanner) and #44 (configuration checks). EF Core generated migration files under `Migrations/` are exempt.
- **Decision:** #32 (M0) amended: `format:check` script and `N8TRACKS_VERSION` override.
  **Why:** M1's gate and edge builds depend on both; found by the coverage check.
- **Decision:** Release rules live in a tested script (#47); bad-tag cases are not tested by pushing real tags.
  **Why:** After #49, tags cannot be deleted, so each live test would spend a version number.
- **Decision:** A real pre-release, `v0.1.0-rc.1`, is published while verifying #48 and stays public.
- **Decision:** Planner calls shown at the gate and not overridden: not every commit gets an `edge-<sha>`; interrupted releases are completed by re-run, not rolled back; stable notes start from the previous stable tag; rationales under ten characters or starting with `TODO` do not count; Dependabot tracks the .NET SDK and groups minor/patch per ecosystem; admins can still delete the tag rulesets.
- **Decision:** The three split stories were not put through a third executor simulation (same departure as M0).

## /n8-plan M2 — 2026-10-03

- **Decision:** M2 is 25 stories and 3 spikes (#52–#79) under epics #6–#9.
- **Decision:** From the user: password reset by a container command; 30-day sliding sessions; backups fall back to a folder under the data path with a warning; Version tree beside the editor; automatic snapshots, last 50 per Version; Songs as a table; a Version stays frozen for good once it has had a Generation.
- **Decision (deviates from the PRD):** When a Song's last Version is deleted, the automatically created Version takes the next never-used top-level number, not `1`.
  **Why:** User's call: shortcodes embed the Version number, so reusing `1` would be confusing. Affects M3's deletion story.
- **Decision:** Version numbers are never reused within a Song, even after purge.
- **Decision (deviates from the PRD):** Conflict responses return the current record; the client identifies the conflicting fields.
  **Why:** The server does not know what the client last saw. Shown at the gate; not overridden.
- **Decision (deviates from the PRD):** "Make current" carries no revision; the last request wins.
  **Why:** Treated as a command, not an edit of stale content. Shown at the gate; not overridden.
- **Decision:** The PRD's Versions table is deferred to M4 (with Generations). M4's planning must pick it up; no epic criterion names it yet.
- **Decision:** The story that makes a Version store every Suno Create option is not filed. It is written by re-running `/n8-plan M2` after the inventory spike (#79) closes.
  **Why:** The inventory is that story's specification and does not exist yet.
- **Decision:** Added #75, an offline restore command, found missing by the coverage check.
- **Decision:** Invariant 1 guard is #69; it covers UI and API paths, and M4 (import) and M7 (MCP) extend it.
- **Decision:** A test-only variable, `N8TRACKS_ENABLE_TEST_SEEDING`, enables the Generation seeding command in the end-to-end containers.
- **Decision:** Split stories and #75 were not put through a further executor simulation (same departure as M0 and M1).

## /n8-plan M3 — 2026-10-03

- **Decision:** M3 is 23 stories (#83–#105) under epics #10–#12.
- **Decision:** From the user: catalog details live in a collapsible Details panel on the Song page; deleted items can be recovered for 30 days with a container command; a primary Artist is optional and filled from a default; dragging an Album track renumbers its disc; release dates accept a year, a year and month, or a full date.
- **Decision:** #82 (password reset from the container) was filed late under M2's epic #6.
  **Why:** The story was drafted and reviewed during M2 planning, then dropped by mistake when the sign-in stories were split. M3's coverage check caught it.
- **Decision:** Retention uses separate tables holding JSON documents of removed rows, with shape versions and upgraders (settles PRD open decision 4). Live tables carry no deleted flag.
- **Decision:** #67's note that retained Songs are reassigned on workflow-state deletion is superseded: a Song restored after its state was deleted gets the first visible state.
- **Decision:** Server-side changes to a Song's Genre, Tag, or credit lists increment that Song's revision.
  **Why:** Those lists are written whole; without it a stale client could silently write a merged or deleted ID back.
- **Decision:** Deleting a record is session-only; removing a membership, credit, relationship, or attachment is an ordinary scoped edit.
- **Decision:** Song notes added to the Details panel (#83); the PRD lists them and no story had them.
- **Decision:** `docs/conventions.md` was committed (PR #81) so executors can read the conventions the stories cite.
- **Deferred to M4:** artwork from a Generation; Song artwork defaulting to the Selected Generation's; Generation deletion and provider tombstones; mapping user relationship types to Suno actions; the PRD's Versions table.
- **Deferred to M7:** whether Tag management (rename, recolour, merge, delete) opens to an MCP scope; the PRD lists managing Tags among MCP writes and M3 keeps it session-only.
- **Decision:** Split stories were not put through a further executor simulation (same departure as earlier milestones).

## Spikes run before M0 — 2026-10-03

- **Decision:** The three Suno spikes (#77 TS-001, #78 TS-002, #79 field inventory) were moved from M2 into M0 and run first, at the user's direction, so that M2's field-storage story and M4 onward can be planned against evidence.
- **Decision:** Raw captures stay in the gitignored `docs/.notes/`; only sanitized findings and fixtures are committed.
  **Why:** The repository is public; the user asked that their identifiers never reach it.
- **Decision (from #79):** Speech is in scope for V1 alongside Songs and Sounds; a Version can be any of the three. The PRD does not mention Speech.
- **Decision (from #79):** Limits: Lyrics 5,000 (Suno's); Song Title 100 and Simple prompt 1,000 (the user's choice, since Suno enforces none).
- **Decision (from #77):** Generation Events are correlated by the request ID in Suno's Create response when the extension observes the Create, otherwise proposed from workspace, `created_at`, and `batch_index` for the user to accept, otherwise absent.
- **Decision (from #78):** Lineage is captured on import from `metadata.task`, its source-ID fields, and `clip_roots`. Reuse Prompt leaves no trace in Suno and is recorded only when n8Tracks started it.
- **Decision (from #78):** System relationship types become Cover, Extend, Reuse Prompt, Mashup, Sample This Song, Use as Inspiration, Voice, plus a general Remix type for unrecognised actions and Derived From. #92 amended.
  **Why:** "Remix" is Suno's menu name, not an action; Extend exists and leaves lineage. Deviates from the PRD's list of seven.
- **Decision (from #78):** Mashup takes exactly two sources; Inspiration takes up to four songs or one playlist.

## /n8-plan M2 (addition after the spikes) — 2026-10-03

- **Decision:** Five stories added to M2 under epic #7: #111–#115. A Version has a kind (Song, Speech, Sound) and stores the 28 value fields of the inventory; a coverage test walks the inventory file.
- **Decision:** From the user: one mode per Version with the other mode's values kept; personal defaults falling back to Suno's; a model list the user can add to; a new Version's Suno title is pre-filled from the Song's title and independent afterwards.
- **Decision:** #64's limits amended to lyrics 5,000 and styles 1,000 (Suno's). Speech prompt limit 1,000 recorded in the inventory.
- **Decision:** The seven reference and file inputs (audio, voice, inspiration, playlist, image, video, workspace) are excluded from M2 and must be claimed by an M4 epic. Epic #7's criterion amended accordingly.
  **Why:** They point at other clips, files, or a Suno workspace; they are not plain values. The coverage check found no M4 epic claims them yet.
- **Decision:** The inventory is embedded in the application at build time as the single source of limits, ranges, and defaults.
- **Decision:** Speech scripts and Sound descriptions have no snapshot history in V1 (shown at the gate; not overridden).

## /n8-plan M4 — 2026-10-03

- **Decision:** M4 is 38 stories (#117–#154) under epics #13–#15, including spike TS-003 (#127), which needs the maintainer's signed-in Suno session and runs first.
- **Decision:** From the user: imports are reviewed in the n8Tracks web app; clips that match nothing become one new Song per Create request, and clips in a workspace that belongs to exactly one Song are proposed for that Song; file inputs (audio, image, video) are notes attached by hand in Suno; TS-003 runs as M4's first story, not now; Reimport of a deleted clip restores the original while it is still retained.
- **Decision:** The server never contacts Suno; the extension reads Suno by observing the page's own traffic, with one exception (a credential-less cover-image request). Shared design is in `docs/suno-integration.md`.
- **Decision:** `docs/suno-adapter-field-map.md` (38 entries) was written during planning as the measure for the "populate every supported generation property" claim.
- **Decision:** The extension's token can stage a sync (`suno.sync`) but cannot commit it; committing is session-only. `suno.generate` covers generation requests and observed Creates.
- **Decision (carve-out from invariant 3, shown at the gate):** Suno's own workspace name and availability are applied when a sync is uploaded, before review.
- **Decision:** Epic #15 gained a criterion for Version sources and file inputs (the seven inventory fields deferred from M2). Generation artwork and Generation deletion (deferred from M3's epics #11 and #12) are filed under epic #13.
- **Decision:** Invariant guards: 4 → #133; 3 (Suno import) → #140; 1 extended by #122 and #140.
- **Decision:** An observed Create whose submitted inputs differ from the Version's creates a new child Version, which becomes the Song's current Version.
- **Decision:** Executor simulations ran on a smaller model to limit cost; the coverage check ran on the session's model. The five split stories were not re-simulated (same departure as earlier milestones).
- **Correction:** Round one's outcomes question was asked without the outcomes having been shown; they were shown and confirmed in round two.
- **Deferred to M5:** local audio counts in the Generation deletion warning; audio associations moving with a Generation; listening from the Versions table.
- **Deferred to M6:** filtering Songs by Selected Generation; searching comments; notifications for import results.

## /n8-exec M0 — 2026-10-03

- **Decision:** Architecture tests use NetArchTest.eNhancedEdition 1.4.5 for type-level rules and plain reflection over referenced assemblies for the assembly-level rules.
  **Why:** The issue prefers the maintained package; it is netstandard2.0 and runs on net10.0. Assembly references are simplest to read with reflection.
  **Issue:** #25
- **Decision:** The Api rule runs over every type in the Api assembly and then drops the composition root by full name (`Program`, its nested types, and the `n8Tracks.Api.DependencyInjection` namespace), with a test for the exemption itself and one proving `HealthEndpoint` is examined.
  **Why:** Filtering by name after the fact covers the compiler-generated types nested in `Program` without relying on how the library treats them, and keeps a rule that examines nothing from passing.
  **Issue:** #25
- **Decision:** The VERSION file is validated by a target in a new root `Directory.Build.targets` (before restore and before build); it must be exactly `major.minor.patch`, with no pre-release suffix. `-p:Version=` still overrides the built version.
  **Why:** An MSBuild error needs a target, and without one a broken file falls back silently to the SDK's 1.0.0. A pre-release label, if ever wanted, can come from `-p:Version=`.
  **Issue:** #25
- **Decision:** Added `ProductVersionTests`, which checks each backend assembly's informational version against VERSION by major.minor (the compatibility rule), not by equality, and that it carries no `+sha` suffix.
  **Why:** The issue asks that every component take its version from the file but names no test; equality would break a build that overrides the version.
  **Issue:** #25
- **Decision:** `AssemblyMarker` is a sealed class with a private constructor, not a static class.
  **Why:** A static class cannot be a generic type argument, which assembly-scanning APIs commonly take.
  **Issue:** #25
- **Decision:** Test packages were moved to the latest stable versions in both test projects (Microsoft.NET.Test.Sdk 18.10.1, xunit.runner.visualstudio 4.0.0, coverlet.collector 10.1.0; xunit stays 2.9.3, its latest).
  **Why:** The issue asks for the latest stable versions and for the new test project to mirror the existing one.
  **Issue:** #25
- **Decision:** The guard-bite proof also covered the csproj test (a package reference added to Domain and an Api to Domain project reference) and the three VERSION failure modes.
  **Why:** The brief asks for each guard to be seen failing against a broken state.
  **Issue:** #25
- **Decision:** `Program` is now an explicit class: `Main` reads the process environment once and calls `Program.RunAsync(args, environment, output, cancellationToken)`, which returns the exit code. Startup-failure tests call `RunAsync` with their own values instead of invoking `Main`.
  **Why:** Tests must not touch the process environment, and top-level statements take no parameters. Keeping the body in `Program` keeps it inside the composition-root exemption of the layering guard.
  **Issue:** #26
- **Decision:** `N8TracksOptions` is registered as a singleton built on first use from an `EnvironmentSnapshot` singleton (all variables plus the working directory), not as a pre-built instance. `Program` resolves it right after the host is built and before anything listens.
  **Why:** `WebApplicationFactory` can only replace services, and only once the host is built; this lets each test host supply its own environment and temp data path while running the real entry point. Departs from the discretion line's "singleton instance" in mechanism only: there is still exactly one instance.
  **Issue:** #26
- **Decision:** Before starting the host, `Program` binds and releases the listen port itself (`ListenPortProbe`, only when the server is Kestrel) and reports a failure as the one startup line; a failure in the host's own bind is still caught and reported.
  **Why:** When Kestrel's bind fails, the generic host also logs "Hosting failed to start" with a stack trace, which breaks "one error line". The alternative was a log filter on the host's category, which would also hide background-service failures.
  **Issue:** #26
- **Decision:** `urls`, `http_ports`, and `https_ports` are blanked in the host configuration, and Kestrel gets one endpoint in code (`ListenAnyIP(port)`). `--urls` on the command line is still ignored but makes Kestrel log its own "Overriding address(es)" warning.
  **Why:** Makes `N8TRACKS_PORT` the only source and avoids a warning on every container start (the ASP.NET base image sets `ASPNETCORE_HTTP_PORTS`). A `Kestrel:Endpoints` configuration section was left alone: it is not one of the sources the issue names.
  **Issue:** #26
- **Decision:** In the base URL, exactly one trailing slash is trimmed; `//` anywhere in the path, including at the end, is invalid. A backslash or whitespace anywhere in the value is invalid.
  **Why:** The discretion lines say both "trim trailing slashes" and "repeated slashes are invalid"; the strict reading catches typos and keeps one rule.
  **Issue:** #26
- **Decision:** An `N8TRACKS_` variable with an empty or whitespace-only value is not reported as unknown. Unknown-name matching on the prefix ignores case; known names match exactly. Unknown-variable warnings are written before validation errors, so they appear even when startup fails.
  **Why:** Empty counts as unset everywhere else. A lower-case typo of the prefix is still a likely mistake worth a warning.
  **Issue:** #26
- **Decision:** Startup lines go to standard output, with echoed values cut at 200 characters and JSON written without HTML-safe escaping.
  **Why:** Standard output is where the application log will go, so the logging story can take over the writer; the cap bounds a line; the relaxed encoder keeps quotes readable in a log.
  **Issue:** #26
- **Decision:** A Debug build of the Api creates `src/n8Tracks.Api/.localdata`, and `.localdata/` is git-ignored. `applicationUrl` was removed from the launch profile.
  **Why:** The data path must exist or startup fails, so `dotnet run` would not work on a fresh clone. `applicationUrl` is ignored now and only caused an override warning.
  **Issue:** #26
- **Decision:** The Api project exposes its internals to `n8Tracks.Api.Tests` (`InternalsVisibleTo`).
  **Why:** The loader and `RunAsync` stay internal like the rest of the Api, and the tests need to call them directly.
  **Issue:** #26
