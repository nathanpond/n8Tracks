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
- **Decision:** Property names are written to the JSON in camelCase (first letter lowered) at every depth of a logged object; dictionary keys are written as they are. The request line's properties are `method`, `path`, `status`, and `durationMs`.
  **Why:** The issue fixes `requestId` in camelCase and the API convention is camelCase JSON, while templates and logged objects use PascalCase names; one rule in the formatter keeps the output consistent with the startup lines (`variable`, `reason`).
  **Issue:** #27
- **Decision:** Masking runs in an enricher that walks the finished property tree of every event (structures, dictionaries, sequences), registered last. The destructuring policy only converts `JsonElement`, `JsonDocument`, and `JsonNode`, which Serilog cannot walk itself, and masks as it converts. Depth, string, and collection limits use Serilog's own settings plus the same constants in the JSON policy.
  **Why:** One walker over the event covers template properties, log scopes, the log context, and request-logging properties alike, whichever way they were added; reimplementing Serilog's object reflection in a policy would add a second code path to keep in step.
  **Issue:** #27
- **Decision:** `StartupLog` is removed. The startup lines (invalid settings, unknown variables, a failed bind) go through a Serilog startup logger with the same sink, formatter, and redaction as the application log, always at Information, whatever `N8TRACKS_LOG_LEVEL` says.
  **Why:** The discretion line asks for a bootstrap logger with the same formatter. Holding it at Information keeps #26's behaviour: a strict level must not hide why the app did not start.
  **Issue:** #27
- **Decision:** An exception that escapes startup is logged as one Critical JSON line and the process exits with code 1, instead of the runtime printing an unstructured stack trace.
  **Why:** The bootstrap logger exists to cover startup, and every line an operator sees should be JSON.
  **Issue:** #27
- **Decision:** The application logger is built from the container: its sinks are the registered `ILogEventSink` services (standard output in production, an in-memory capture in tests, both the same `JsonLinesSink` class). Serilog's static `Log.Logger` is never set, and the Serilog console sink is not used: `JsonLinesSink` writes to the `TextWriter` that `Program.RunAsync` is given.
  **Why:** Tests run several hosts in one process, so a static logger would mix their output; a writer-based sink keeps the startup tests able to read real output, and lets the guard capture through exactly the production path.
  **Issue:** #27
- **Decision:** The unhandled-exception handler is a small middleware of our own, not `UseExceptionHandler`. It logs once at Error, answers Problem Details with `code: internal_error` and `requestId`, and cuts the connection if the response had already started. A client abort (`OperationCanceledException` or `IOException` with the request aborted) is logged at Debug and recorded as status 499.
  **Why:** The built-in handler logs under a `Microsoft.*` category, adds a `traceId`, and would need its own suppression to keep "logged once"; the custom one is about forty lines and does exactly what the criteria say.
  **Issue:** #27
- **Decision:** The properties ASP.NET Core attaches on its own (`RequestId`, `RequestPath`, `ConnectionId` from its log scopes, and `EventId`) are dropped unless the message uses them. The request ID is a UUIDv7 and also becomes the request's `TraceIdentifier`.
  **Why:** The discretion line limits enrichment to the source and the request ID; the host's own `RequestId` would also collide with ours once camel-cased.
  **Issue:** #27
- **Decision:** A health request is logged at Debug only when it was answered below 400; a refused (outside the sub-path) or failed one gets the normal level. An exception after the response started logs its completion line at Error even though the recorded status is the one already sent.
  **Why:** Routing matches `/health` before the sub-path check, so a refused request would otherwise be hidden at Debug; a failed request should not look like a success in the log.
  **Issue:** #27
- **Decision:** The guard's probe also logs the request body, every header, and the query string as destructured objects, a log scope, and nested objects, beyond the cases the issue lists. Probe routes are added by a startup filter at the end of the real pipeline, in the test host only.
  **Why:** The header, cookie, query, and body sentinels are otherwise absent only because request logging never reads them; logging them deliberately makes the policy itself answer for them.
  **Issue:** #27
- **Decision:** `appsettings.Development.json` is kept as an empty object after its `Logging` section was removed.
  **Why:** The issue asks for the sections to be removed, not the files.
  **Issue:** #27
- **Decision:** Guard-bite proof: with the redaction registration removed from `WithRedaction`, `LogRedactionGuardTests` failed (9 of 11 sentinels found with only the enricher removed) along with the masking unit tests; with exception scrubbing removed, the two exception tests failed; with the framework hold removed, the level tests failed. All were restored and pass.
  **Why:** The brief asks for each guard to be seen failing against a broken state.
  **Issue:** #27
- **Decision:** snake_case names come from a small convention of our own (`SnakeCaseNamingConvention`, added in the context's `ConfigureConventions`), not from `EFCore.NamingConventions`.
  **Why:** `EFCore.NamingConventions` 10.0.1 supports EF Core 10 but also renames the columns of `__EFMigrationsHistory` to `migration_id` and `product_version`, and the discretion line says that table keeps its default name and columns. A convention on the context's own model leaves EF Core's separately built history model alone.
  **Issue:** #28
- **Decision:** `DatabaseStartup` is a static class called as `DatabaseStartup.RunAsync(app.Services, startupLog, cancellationToken)`. The failure line goes to the startup logger; the per-migration and summary Information lines go to the application logger.
  **Why:** The startup logger is never filtered by `N8TRACKS_LOG_LEVEL`, so the reason the app did not start is always visible (as in #26 and #27), while the success lines honour the configured level like any other Information line.
  **Issue:** #28
- **Decision:** The Error line attaches the exception when there is one (`open`, `configure`, `history`, a migration). The `schema-version` and `migration-lock` refusals are decisions of our own and carry a `Step` and a message, with no exception.
  **Why:** A stack trace made up for a refusal that nothing threw would only be noise.
  **Issue:** #28
- **Decision:** A new step, `migration-lock`: when migrations are pending and EF Core's `__EFMigrationsLock` table holds a row, startup fails with a message saying how to recover, instead of calling the migrator.
  **Why:** EF Core's SQLite migration lock is a table row that it waits on with no time limit. A process killed in the middle of an upgrade leaves the row behind, and the next start would hang silently forever, which breaks "a failed upgrade leaves the container stopped, not half-working". A migration that throws releases the lock normally (tested).
  **Issue:** #28
- **Decision:** `__EFMigrationsLock` does not count as a table for the "tables but no migration history" check, and a view does not either.
  **Why:** EF Core creates the lock table before the first migration, so a first start that failed would otherwise make the file permanently "not ours". The criterion says tables.
  **Issue:** #28
- **Decision:** When the history holds an unknown migration and also has a gap, the database is reported as newer than the application.
  **Why:** An unknown migration is the more likely and more actionable cause (start a newer image); the gap message would send the operator the wrong way.
  **Issue:** #28
- **Decision:** EF Core's own `CommandError` and `ConnectionError` events are logged at Debug instead of Error.
  **Why:** EF logs them at Error and then throws; the exception is logged once by database startup or by the request's exception handler, so EF's line would break "one Error log line" here and "logged once" from #27. It also keeps SQL text out of the log.
  **Issue:** #28
- **Decision:** Database startup runs right after the configuration is validated, before the port probe. `APortInUseExitsWithCodeOneAndOneErrorLine` now asserts one non-Information line instead of one line in total.
  **Why:** The database must be up to date before anything can listen; a start that then fails to bind has already written the two database Information lines.
  **Issue:** #28
- **Decision:** The connection pool of `Microsoft.Data.Sqlite` is left on, and the pool is not cleared at shutdown.
  **Why:** Pooling is the provider default and avoids reopening the file per request; durability does not depend on a final checkpoint, because committed data is in the WAL file and is read back on the next start (tested across hosts).
  **Issue:** #28
- **Decision:** The `dotnet-ef` tool manifest is `dotnet-tools.json` at the repository root, pinned to 10.0.12. Migrations are added with `dotnet ef migrations add <Name> --project src/n8Tracks.Infrastructure --startup-project src/n8Tracks.Infrastructure -o Persistence/Migrations`.
  **Why:** The .NET 10 SDK writes the manifest at the root rather than under `.config/`; the Infrastructure project holds the design-time factory, so the tools need no running app.
  **Issue:** #28
- **Decision:** Guard-bite proof: with `Program` ignoring the result of `DatabaseStartup`, all five failure tests failed (the app listened); with the interceptor, the WAL pragma, and the schema check removed, the connection-settings test and the two `schema-version` tests failed. All were restored and pass.
  **Why:** The brief asks for each guard to be seen failing against a broken state.
  **Issue:** #28
- **Decision:** The version in the health response is read from the Api assembly (`typeof(Program).Assembly`), not from `Assembly.GetEntryAssembly()`.
  **Why:** In production they are the same assembly; under the test host the entry assembly is the test runner, whose version is not ours.
  **Issue:** #29
- **Decision:** The model lives in Application (`HealthReport`, `HealthComponent`, `MigrationsHealthComponent`, `HealthStatus`, `HealthDetails`); `version` and `timeZone` are added by the endpoint's response record, not carried in `HealthReport`.
  **Why:** The discretion line has the version read in Api, and the time zone is already on the options the endpoint has; the report stays about components.
  **Issue:** #29
- **Decision:** Enums are serialised as camelCase strings for every endpoint (`ConfigureHttpJsonOptions` with `JsonStringEnumConverter`, integers refused), not by an attribute on the health types.
  **Why:** The conventions ask for camelCase JSON everywhere, the OpenAPI document picks the names up from the same options, and Application stays free of serialisation attributes.
  **Issue:** #29
- **Decision:** The connection-factory seam is `IDatabaseConnectionFactory.CreateForExistingDatabase()` in Infrastructure (internal). Its connection is read-write, no-create, and outside the connection pool.
  **Why:** #28 defined no such seam. A pooled connection keeps the file open, and on Unix a deleted file stays readable through an open handle, so a pooled health connection would report a deleted database as reachable.
  **Issue:** #29
- **Decision:** The media check goes through a second small seam, `IMediaMountProbe`, with the same deadline mechanism as the database check (`DeadlineCheck`).
  **Why:** The 2-second media timeout cannot be tested against a real directory; a probe the test host can make hang is the counterpart of the fault-injecting connection factory. It only reads (invariant 2).
  **Issue:** #29
- **Decision:** A request that arrives while another request's check is still in flight (not yet timed out) waits for that same worker, with its own 2-second deadline, instead of starting a second one.
  **Why:** The discretion line allows at most one outstanding check per component; starting one per concurrent request would let several pile up on a dead mount. A request that arrives with no check in flight always starts a new one, so nothing is cached.
  **Issue:** #29
- **Decision:** If the migration state is not available (database startup has not completed), `migrations` is `unhealthy` with detail `unknown` and `lastApplied` null.
  **Why:** The issue defines only the healthy case. The app does not listen before database startup succeeds, so this cannot happen today; reporting it as a fault is safer than throwing from an endpoint that must always answer.
  **Issue:** #29
- **Decision:** The status-change log lines carry `Component`, `HealthStatus`, and `Reason`, and the Warning attaches the exception when the check threw. A 503 health answer still gets its request-completion line at Error on every poll.
  **Why:** The cause (path, SQLite message) belongs in the log, which is the only place it goes. The property is `HealthStatus`, not `Status`, because the request-completion line already uses `Status` for the HTTP status. The Error completion line is #27's rule for any 5xx and was left alone.
  **Issue:** #29
- **Decision:** `N8TracksApiFactory` now gives every test host an existing, empty media directory of its own, and a `TestServices` hook for replacing services.
  **Why:** The default `/media` does not exist on a development machine, so every test host would report `degraded` and log a Warning inside the first health request, which breaks the earlier tests that count a request's log lines.
  **Issue:** #29
- **Decision:** The read-only and unreadable media tests use a `UnixFact` attribute that skips them on Windows; the unreadable test expects `healthy` when the process is privileged.
  **Why:** The discretion line says the `chmod` test is skipped on Windows; permissions do not bind root, which is what a container CI job may run as.
  **Issue:** #29
- **Decision:** Guard-bite proof: each of these was applied, seen to fail the named tests, and restored. Aggregation ignoring `degraded` (7 failed); the database detail carrying the exception message (3 failed, including the complement); the health connection opened in create mode (the deleted-file test failed); always 200 with no `Cache-Control` (7 failed); a 6-second deadline (both timeout tests failed); the abandoned worker not remembered (both timeout tests failed).
  **Why:** The brief asks for each guard to be seen failing against a broken state.
  **Issue:** #29
