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
- **Decision:** The shell is a fallback middleware (`UseFrontend`, called in `Program.cs` after the endpoints are mapped, inside the path base) that answers only when routing selected no endpoint. It is not a mapped catch-all route.
  **Why:** A catch-all route was tried first and broke two things. Under a sub-path, the router that runs ahead of the path base matched it with the full path, so `/n8tracks/health` got the shell. And with GET/HEAD metadata it turns every unknown POST, `POST /api/unknown` included, into a 405 with `Allow: GET, HEAD`; without that metadata it turned `POST /health` from 405 into 404. The middleware keeps unknown API paths at 404 and leaves existing 405 answers alone.
  **Issue:** #30
- **Decision:** The shell is decided before the static files are tried, and `index.html` itself counts as a shell request.
  **Why:** Otherwise `GET <base>/index.html` serves the raw file, placeholder and all, without the base tag (a test caught this). Nothing is lost: a shell request has no file extension, and the static file middleware does not serve files without a known extension.
  **Issue:** #30
- **Decision:** "Has a file extension" means the last path segment contains a dot. The reserved first segments (`api`, `health`, `openapi`, `assets`) are matched in any letter case, and `assets` is reserved as a whole first segment, so `/assets` alone is 404 too.
  **Why:** The dot rule is the framework's own `nonfile` rule for SPA fallbacks. Routing matches in any letter case, so `/API/x` must not get the shell either. A client route named `assets` would collide with the build's directory anyway.
  **Issue:** #30
- **Decision:** Files outside `assets/` (a favicon, a manifest) are served with `Cache-Control: no-cache`. The not-built 404 is `no-store`. The 308 `Location` is the path only (`<base>/`, in the letter case the client sent), with the query string kept, and applies to GET and HEAD.
  **Why:** Only hashed files may be cached forever; the criteria name no header for the rest, and revalidating is the safe choice. A path-only `Location` is right behind a reverse proxy whatever host it uses.
  **Issue:** #30
- **Decision:** An `index.html` with neither the placeholder nor a `<head>` tag is served unchanged. Only the first placeholder is replaced. The path is HTML-encoded in the tag. Nothing about the frontend is logged at startup.
  **Why:** There is no safe place for a `<base>` tag in such a document, and the discretion line covers the two real cases. A startup line (or a Warning when not built) would break the earlier tests that count startup log lines, and the 404 body already says what is wrong.
  **Issue:** #30
- **Decision:** The web root is reached through a small `FrontendFiles` service (the host's web root file provider). `N8TracksApiFactory` replaces it with a temporary directory of its own (`WebRootPath`), empty unless a test writes files there before the first request.
  **Why:** Tests must not depend on whether a developer has a built frontend in `src/n8Tracks.Api/wwwroot`. Changing the web root of a minimal-hosting app from a test host is not supported by the framework, so a seam was needed.
  **Issue:** #30
- **Decision:** The log-test probe routes moved from `/probe/...` to `/api/probe/...`.
  **Why:** Probes run after the application's pipeline, for requests nothing answered; the shell now answers any unreserved GET first. Under `/api/` the shell never does.
  **Issue:** #30
- **Decision:** No JavaScript project was created; `wwwroot` is ignored in `.gitignore` and the README describes the serving rules.
  **Why:** The issue's tests use a fixture web root, and the discretion line says nothing in the .NET build invokes npm. The `web/` project is #31.
  **Issue:** #30
- **Decision:** Guard-bite proof: each of these was applied, seen to fail the named tests, and restored. Reserved segments ignored (9 failed); extension rule removed (3 failed); any method allowed (7 failed); no base injection (4 failed); redirect removed (2 failed); assets served `no-cache` (2 failed); `index.html` not treated as the shell (4 failed); the "no endpoint selected" check removed (1 failed, after a test was added for it because the first run passed).
  **Why:** The brief asks for each guard to be seen failing against a broken state.
  **Issue:** #30
- **Decision:** Versions, looked up on 2026-10-04: React 19.3.0, React Router 8.4.0, Mantine 9.6.3, Vite 8.3.2, Vitest 5.0.3, ESLint 10.12.0, typescript-eslint 8.71.0, Prettier 3.9.9, jsdom 30.1.1, Testing Library React 16.3.3. Node is pinned to 24 (`.nvmrc`, `engines: >=24`).
  **Why:** These are the current stable releases. Node 24 is the newest LTS line in the Node release index today.
  **Issue:** #31
- **Decision:** TypeScript is 6.0.3, not the latest 7.0.2.
  **Why:** typescript-eslint 8.71.0, which the strict typed lint rules need, declares `typescript >=4.8.4 <6.1.0`. 6.0.3 is the newest release it supports.
  **Issue:** #31
- **Decision:** ESLint 10 is used with an npm `overrides` entry that lets `eslint-plugin-jsx-a11y` 6.10.2 accept it.
  **Why:** The plugin's latest release (2024) declares a peer range that stops at ESLint 9, and npm reports ESLint 9 as no longer supported. The plugin was checked under ESLint 10: a file with an `img` without `alt` and a click handler on it got three jsx-a11y errors. Remove the override when the plugin declares ESLint 10.
  **Issue:** #31
- **Decision:** Every colour pair that carries text is defined once in `web/src/theme/palette.ts` and put on the page as CSS variables by the Mantine CSS variables resolver (body background and text, secondary text, the out-of-date notice, the three status badges). The contrast test reads the same object and also checks that the resolver emits it. The Retry button uses Mantine's `default` variant.
  **Why:** A contrast test is only worth something if it tests the colours actually used. Mantine's own defaults do not all pass AA (dimmed text on white is about 3.3:1, the filled blue button about 3.6:1), so those are not used. Badges are also held to 3:1 against the page.
  **Issue:** #31
- **Decision:** The health type guard requires `status`, `version`, and `components` whose values each have a string `status` (and a string `detail` if present). It does not require `timeZone` or restrict the status and component names.
  **Why:** The page does not show the time zone, and the discretion lines say an unexpected component and an unrecognised status are shown, not rejected.
  **Issue:** #31
- **Decision:** The first load is made even when the tab is hidden; only the 30-second refresh pauses. Becoming visible refreshes at once. Refreshing continues in the error state, so the page recovers by itself; Retry shows the loading state and loads immediately. A non-JSON body and a timeout are the error state too.
  **Why:** A tab opened in the background should be ready when the user gets to it, and data shown after a long hidden spell should not be up to 30 seconds older than it looks. The criteria do not say whether the error state keeps polling; recovering unprompted is the friendlier reading.
  **Issue:** #31
- **Decision:** In `npm run dev` only, a small Vite plugin replaces `<!--n8tracks-base-->` with `<base href="/">`. The build leaves the placeholder for the backend.
  **Why:** With `base: './'` and no base tag, a deep link on the dev server would resolve the entry script and `health` against its own path. The router's `basename` and the health URL both come from `document.baseURI`.
  **Issue:** #31
- **Decision:** `index.html` carries an empty `data:` icon link.
  **Why:** Without any icon the browser asks for `/favicon.ico` at the hostname root, outside the base path, and logs a 404 in the console. A real icon is not in this story.
  **Issue:** #31
- **Decision:** The colour scheme is stored under the local storage key `n8tracks-color-scheme` through Mantine's local storage manager. No inline script sets the scheme before the bundle loads.
  **Why:** An app-named key does not collide with another Mantine app on the same host. An inline script would have to be allowed by any later content security policy; the cost is a possible brief light flash on a dark page load.
  **Issue:** #31
- **Decision:** Guard-bite proof: each of these was applied, seen to fail tests, and restored (46 passing afterwards). Any HTTP status accepted as data (1 failed); 503 treated as an error (1); body validation skipped (1); polling while hidden (1); a failed refresh dropping the data (1); a refresh showing the loading state (2); no timeout (1); an absolute `/health` path (1); default scheme light (1); scheme not stored under the app key (1); a low-contrast light badge (1); low-contrast dark secondary text (1); Retry doing nothing (1). For lint, a file with `any`, an `img` without `alt`, and a click handler on it produced 5 errors.
  **Why:** The brief asks for each guard to be seen failing against a broken state.
  **Issue:** #31
- **Decision:** The extension is built by plain multi-entry Vite (no web-extension plugin), driven by `extension/scripts/build.ts`, which Node 24 runs directly as TypeScript. Vite's root is `extension/src`, so the popup is `dist/popup/popup.html`; the service worker is `dist/service-worker.js`; icons come from `extension/public/icons`. The source manifest names source paths and the build rewrites them.
  **Why:** The later discretion line chose plain Vite with a small build script. Running the script with Node's built-in type stripping avoids a second TypeScript runner, and the scripts are typechecked and linted like the rest.
  **Issue:** #32
- **Decision:** Versions, looked up on 2026-10-04: Vite 8.3.2, Vitest 5.0.3, ESLint 10.12.0, typescript-eslint 8.71.0, Prettier 3.9.9, `@types/chrome` 0.3.4, fflate 0.8.3 (the zip library), jsdom 30.1.1, TypeScript 6.0.3 (held below 7 for typescript-eslint, as in `web/`).
  **Why:** Current stable releases, matching `web/` where both use a package.
  **Issue:** #32
- **Decision:** A version is `major.minor.patch` with an optional `-suffix` of letters, digits, dots, and hyphens; each number at most 65535 without leading zeros; build metadata (`+...`) is rejected. A blank `N8TRACKS_VERSION` counts as unset, and a malformed one fails the build instead of falling back to the file. `version_name` is written only for a pre-release version.
  **Why:** Chrome's manifest `version` allows only such numbers. Blank-is-unset matches the application's configuration rule; a silent fallback would stamp an edge build with the wrong version.
  **Issue:** #32
- **Decision:** The build writes the version into `extension/package.json` and `package-lock.json` from the `VERSION` file only, never from `N8TRACKS_VERSION`.
  **Why:** An edge build in CI would otherwise change tracked files. A test holds `package.json` equal to `VERSION`.
  **Issue:** #32
- **Decision:** The manifest validator is stricter than the issue lists: it also refuses `optional_permissions`, a present-but-empty `host_permissions`, a name other than `n8Tracks`, the `0.0.0` placeholder version, a non-module service worker, and icon sets other than 16, 48, and 128. The build itself runs the validator and fails, so a widened manifest never reaches `dist/`; the tests then check the real `dist/manifest.json` independently.
  **Why:** `optional_permissions` is another way to gain a permission, so "limited to `storage`" has to cover it. Failing the build keeps a bad manifest out of a packaged zip even when tests are not run.
  **Issue:** #32
- **Decision:** `npm run package` typechecks and builds, then zips; the zip and the popup use the full version string. Prettier ignores `extension/fixtures/`; `extension/coverage/` is ignored by git along with `dist/` and the zips.
  **Why:** The committed Suno fixtures must stay byte-for-byte as captured, so no formatter may touch them.
  **Issue:** #32
- **Decision:** The popup has its own small stylesheet with a `prefers-color-scheme: dark` block and `color-scheme: light dark`; the text "Not connected to n8Tracks" is set by the popup script, not written in the HTML. A jsdom test renders the real `popup.html`.
  **Why:** No UI framework yet (discretion). Setting the state from script is where the real connection state will go in M4.
  **Issue:** #32
- **Decision:** The built extension was loaded in Chromium (Playwright's Chrome for Testing build 1234, `--load-extension`) as a one-off check, not a committed test: the service worker registered, the popup showed the name, version, and "Not connected to n8Tracks" in light and dark, and Chrome reported 0 manifest errors, 0 runtime errors, and 0 install warnings.
  **Why:** "Chrome loads it without errors" cannot be shown by parsing JSON. A committed browser test belongs with the end-to-end suite, which does not exist yet.
  **Issue:** #32
- **Decision:** Guard-bite proof: each of these was applied, seen to fail, and restored (69 passing afterwards). `tabs` added to the source manifest (build fails: permissions); `host_permissions` added (build fails); a second optional host (build fails); the build's own validation bypassed with an extra permission and host (3 tests on the real `dist/manifest.json` failed); the version label without `v` (4 failed); the validator ignoring permissions (4 failed). For lint and typecheck, a file with `any` and an unused local gave 3 ESLint errors and TS6133.
  **Why:** The brief asks for each guard to be seen failing against a broken state.
  **Issue:** #32
- **Decision:** The gateway references no NuGet package and no project. The official MCP C# SDK was looked up on 2026-10-04 (`ModelContextProtocol.AspNetCore` 2.2.0, with `ModelContextProtocol` and `ModelContextProtocol.Core` at the same version) but is not referenced yet.
  **Why:** The story says the gateway has no MCP endpoint and no tools. An unused package would only add to the dependency graph the isolation guard reads; M7 adds it with the first endpoint and should look the version up again then.
  **Issue:** #33
- **Decision:** `Program` is `public sealed partial` with a private constructor and `RunAsync(args, environment, cancellationToken)`; the settings are loaded from an `EnvironmentSnapshot` in the container, as in the app. The gateway has its own copies of the snapshot, the loader, the port probe, and the product-version reader.
  **Why:** The analyzers reject a public class with only static members unless it is sealed with a private constructor. The snapshot in the container is the seam `WebApplicationFactory` tests replace. Copies, not shared code, per the discretion line.
  **Issue:** #33
- **Decision:** Startup-failure lines are written through a separate logger factory (the same JSON console formatter, at Information) that is disposed, and so flushed, before the process exits. The host's own log takes its level from `N8TRACKS_LOG_LEVEL`, falling back to Information while the value is invalid.
  **Why:** With `N8TRACKS_LOG_LEVEL=Critical` the Error line that explains the exit would otherwise be filtered out, and the console logger writes on a background queue that must be flushed. `None` is refused as a level, as in the app.
  **Issue:** #33
- **Decision:** Framework categories (`Microsoft`, `System`) are held at Warning, or at the configured level when that is higher. The gateway therefore writes its own Information line when it starts listening. The HTTP client's built-in request logging is removed.
  **Why:** A fixed Warning rule would let framework warnings through at `Error`. The framework's "Now listening" line is below Warning. The client's own log lines carry the request URL, which the story says not to log.
  **Issue:** #33
- **Decision:** The gateway does not warn about unknown `N8TRACKS_` variables.
  **Why:** The app and the gateway share the prefix, so an environment shared by both would warn about every app setting. The reverse does happen: the app warns about `N8TRACKS_GATEWAY_PORT` and `N8TRACKS_API_URL` if it is given them; left as is, since they run as separate containers.
  **Issue:** #33
- **Decision:** `N8TRACKS_API_URL` is normalised to end in a slash and used as the typed client's `BaseAddress`; the probe requests the relative path `health`. Unlike `N8TRACKS_BASE_URL`, its path segments are not restricted to a character set.
  **Why:** A relative request against a base address without a trailing slash would replace the last path segment and lose the sub-path. The gateway only sends requests to this URL and never routes on it, so the app's path rules are not needed.
  **Issue:** #33
- **Decision:** A response whose headers arrive but whose body does not finish within the 3 seconds counts as unreachable. A body over 64 KB, or one that is not a JSON object with a string `version` starting `<digits>.<digits>`, is reachable with `compatible: null`. The status code plays no part: a 503 with a matching version is `healthy`, `reachable`, `compatible: true`.
  **Why:** The budget covers headers and body (discretion), so an unfinished response is not "a response within 3 seconds". The gateway's status describes the gateway's link to n8Tracks, not n8Tracks' own health, which its health URL reports.
  **Issue:** #33
- **Decision:** One tracker holds the last upstream state (compatible, mismatched, version unreadable, unreachable) and writes one line per change: Warning for the three bad states, Information for a return to compatible, nothing when the first probe is compatible. A failed probe is therefore logged when the upstream becomes unreachable, not on every request. The lines carry a short reason (`ConnectionError`, `timed out`) and the upstream's major.minor as numbers, never the URL, the exception message, or the upstream's raw version text.
  **Why:** A health URL polled every few seconds would otherwise write a Warning per poll, the same reasoning as the app's component health. The exception message names the host and port, and the raw version is text from another process.
  **Issue:** #33
- **Decision:** The port is checked with a bind probe before the host starts (a copy of the app's), and a failure is one Error line naming `N8TRACKS_GATEWAY_PORT` with exit code 1.
  **Why:** Without the probe the host logs its own multi-line stack trace before the gateway's line. Not in the acceptance criteria; added as startup error handling.
  **Issue:** #33
- **Decision:** The guard is three tests: the project file (`ProjectReference`, `PackageReference`, `Reference`, `FrameworkReference`), `obj/project.assets.json` (every library under `libraries` and each target; the file must exist and be the gateway's own), and the built assembly's references followed transitively. The gateway is also in the architecture tests' table with no allowed project reference, which will need `n8Tracks.ServiceDefaults` added when that story lands. Besides the stub-handler tests, three tests use the real network handler against a loopback server, and six run the built gateway as a process.
  **Why:** The compiler drops unused references, so the assembly walk alone misses a declared but unused reference, and the project file alone misses transitive packages. A fake handler cannot show that redirects are not followed, and only a real process shows the exit code and what reaches standard output.
  **Issue:** #33
- **Decision:** Guard-bite proof: each of these was applied, seen to fail, and restored (145 gateway tests passing afterwards). A `ProjectReference` to `n8Tracks.Application` (project-file and dependency-graph tests failed, naming Application and Domain; the architecture table test failed too); the same reference used in code (all three guard tests failed); a `PackageReference` to `Microsoft.EntityFrameworkCore.Sqlite` (2 failed, listing 11 EF Core and SQLite packages); `AllowAutoRedirect = true` (the real-redirect test failed); a budget of 8 seconds (the timeout test failed).
  **Why:** The brief and the test plan ask for the guard to be seen failing against a broken state.
  **Issue:** #33
- **Decision:** Package versions looked up on 2026-10-04 with `dotnet package search`: `OpenTelemetry.Extensions.Hosting` and `OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.19.1, `OpenTelemetry.Instrumentation.AspNetCore` and `OpenTelemetry.Instrumentation.Http` 1.19.0, `Serilog.Sinks.OpenTelemetry` 4.2.0 (Api only). Aspire is at 13.6.0; `n8Tracks.ServiceDefaults` has the shape of its service-defaults project (`IsAspireSharedProject`, the ASP.NET Core framework reference) but references no Aspire package, since none is needed until the AppHost exists. Runtime instrumentation, service discovery, HTTP resilience, and health endpoints from the template are left out.
  **Why:** The acceptance criteria name ASP.NET Core and outgoing HTTP only, and the discretion lines leave out discovery and resilience. Fewer packages also keeps the gateway's dependency graph, which its isolation guard reads, small.
  **Issue:** #34
- **Decision:** `AddServiceDefaults(serviceName, serviceVersion, exportLogsFromLoggingProviders)` checks `OTEL_EXPORTER_OTLP_ENDPOINT` first and returns without touching the service collection when it is unset or blank. Everything that names an OpenTelemetry type is in a separate private method, so with export off the OpenTelemetry assemblies are not loaded either. Exporters are added per signal (`AddOtlpExporter`), not with `UseOtlpExporter`.
  **Why:** The maintainer's requirement is that no OpenTelemetry component is registered unless the variable is set. `UseOtlpExporter` would also turn on log export through a logging provider in the app, where logs must leave only through the redacted Serilog pipeline.
  **Issue:** #34
- **Decision:** The endpoint is read from the host's configuration (`builder.Configuration`), which holds the process environment, not from the `EnvironmentSnapshot` the `N8TRACKS_` settings come from. A value given as a command-line argument or in `appsettings.json` would therefore turn export on too.
  **Why:** The OpenTelemetry SDK reads the endpoint and its companion variables (protocol, headers) from that same configuration, so the switch and the exporter always agree, and the discretion line says the companions are honoured as the SDK provides them. It is also the seam a test host can set per host without changing the test process's environment. A gateway test runs the real process with the real environment variable to show that path.
  **Issue:** #34
- **Decision:** The app's log export is a second sink of the application logger (`Serilog.Sinks.OpenTelemetry`, wrapped in a sub-logger registered in the container as an `ILogEventSink`), added in `LoggingRegistration.AddN8TracksLogExport` only when the endpoint is set. It reads the standard OTLP variables from the host configuration, except `OTEL_SERVICE_NAME`. Its own HTTP requests are kept out of the traces. The startup logger (invalid settings, bind failures) is not exported.
  **Why:** Redaction is an enricher of the logger, so every sink receives events already masked; a container-registered sink is disposed, and flushed, with the host. The service names are fixed by the acceptance criteria, and tracing and metrics already ignore `OTEL_SERVICE_NAME` because the name set in code wins. Startup failures happen before or without a host and are on standard output.
  **Issue:** #34
- **Decision:** The gateway exports logs through the OpenTelemetry logging provider with the formatted message and without scopes. `AddServiceDefaults` is called after `AddGatewayLogging`, which clears the providers.
  **Why:** The gateway's console log leaves scopes out too; they carry request details its messages were written to omit.
  **Issue:** #34
- **Decision:** Health requests are traced like any other request (the Aspire template filters them out), and the gateway's client span for its upstream probe carries the URL in `N8TRACKS_API_URL`, which its log never writes. The README states both the query-string masking and the URL in traces.
  **Why:** The gateway has no other endpoint yet, so a filter would leave nothing to trace; a path filter would also need to know the app's base path. The URL of an outgoing request is the standard content of a client span, it cannot hold user info or a query string (the setting is validated), and it is not in invariant 6's list. Worth revisiting if the maintainer wants the upstream address kept out of traces as well.
  **Issue:** #34
- **Decision:** The gateway guard's exemption list is now exactly `n8Tracks.ServiceDefaults`; the gateway's own name is excluded separately. Two tests were added: the gateway's only project reference is the exempt one, and the exempt project references no project. The architecture tests also check that ServiceDefaults' types depend on no n8Tracks layer, routing, endpoint building, health checks, or Entity Framework Core.
  **Why:** The acceptance criteria give the exemption list exactly. "Maps no health endpoints" is otherwise only true by omission; a dependency rule makes adding one fail a test.
  **Issue:** #34
- **Decision:** Tests use an in-process stub collector (`tests/Shared/StubOtlpCollector.cs`, linked into both test projects) built on `HttpListener` with a small protobuf field reader, and hosts are pointed at it with `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`. Both test factories now force the endpoint to blank unless a test sets it, and the gateway process tests strip `OTEL_` variables. Tests that turn export on or assert it is off are in a `Telemetry` collection that does not run in parallel with anything; `LogRedactionGuardTests` joined it.
  **Why:** An ASP.NET Core stub would itself be traced by the instrumentation under test, since activity listeners are process-wide; for the same reason a host with export on would make a parallel "export off" host's requests recorded. No protobuf or collector package was added for a few field reads. A developer's own `OTEL_` variables must not change test results.
  **Issue:** #34
- **Decision:** "No recorded Activity" is asserted as `Activity.Current?.Recorded != true` inside the request, not as "no Activity".
  **Why:** With export off ASP.NET Core still creates an unsampled activity for its own log scopes when logging is enabled; that is framework behaviour without OpenTelemetry, and nothing listens to or exports it.
  **Issue:** #34
- **Decision:** The redaction guard's collector test checks all three signals, not only logs: the sentinels sent in the Authorization and Cookie headers, the query string, and the body must be absent from the trace and metric payloads as well.
  **Why:** Invariant 6 covers diagnostics, and the instrumentation is a second path a query-string token could leave by. The ASP.NET Core instrumentation masks query values by default (`token=Redacted`), which the test pins.
  **Issue:** #34
- **Decision:** Guard-bite proof: each of these was applied, seen to fail, and restored. The endpoint check in `AddServiceDefaults` removed (8 failed: nothing-registered, blank-endpoint, and no-recorded-activity tests in both services); the check in `AddN8TracksLogExport` removed (3 failed); `WithRedaction()` removed from the application logger (both redaction guard tests failed, the collector one included); the service names changed and the app given a logging-provider export (4 failed, the real-process gateway test included); a `ProjectReference` from ServiceDefaults to Application (3 failed: the architecture table, the exempt-project test, and the gateway's dependency-graph test).
  **Why:** The brief and the test plan ask for each guard to be seen failing against a broken state.
  **Issue:** #34
- **Decision:** The "assemblies on disk only" claim was checked once by hand against the built binaries, not as a committed test: with the variable unset, the running app and the running gateway had no OpenTelemetry file open (`lsof`, 0 files each); with it set, 24 and 21.
  **Why:** A test process loads those assemblies itself, so it cannot observe this; the committed tests assert the registrations, and this run backs the wording in the README.
  **Issue:** #34
- **Decision:** The `--healthcheck` mode the issue's key link names did not exist; it was added to the app binary in this story (`HealthCheckCommand`, called first thing in `Program.RunAsync`). It reads only the port and the base URL path (`EnvironmentOptionsLoader.LoadListenAddress`), requests `<base path>/health` on 127.0.0.1 with a 4-second timeout and no proxy, and exits 0 only for 200. It writes one line in the application log shape, which Docker keeps in the container's health log.
  **Why:** The image installs no `curl` or `wget`, and the URL has to follow `N8TRACKS_PORT` and the base path. The full settings loader was not reused because it write-tests the data path, and the check runs as root every 30 seconds. 4 seconds sits inside Docker's 5-second timeout, so a hang is reported rather than killed.
  **Issue:** #35
- **Decision:** Base images: `node:24-slim` (Node 24 "Krypton" is the current LTS line, and matches `.nvmrc`), `mcr.microsoft.com/dotnet/sdk:10.0` (10.0.401 today), and `mcr.microsoft.com/dotnet/aspnet:10.0` (Ubuntu 24.04, runtime 10.0.12). The Node image is pinned to the major, not to the floating `lts-slim` tag.
  **Why:** Looked up on 2026-10-04. `lts-slim` would move to Node 26 by itself later this month, away from `.nvmrc`; a numbered tag is also one Dependabot's new `docker` entry can propose updates for.
  **Issue:** #35
- **Decision:** The runtime stage removes the base image's empty `/media` (`rmdir /media`).
  **Why:** Ubuntu ships `/media`, so without this an unmounted media folder exists, lists as empty, and health says `healthy` instead of `degraded`. The acceptance criteria require an unmounted `/media` to be absent; the smoke script failed on exactly this before the change.
  **Issue:** #35
- **Decision:** The entrypoint fixes ownership of the literal paths `/data` and `/backup` only, not of whatever `N8TRACKS_DATA_PATH` or `N8TRACKS_BACKUP_PATH` point at, and uses `chown -R -h`.
  **Why:** A recursive chown as root of an operator-supplied path could hand over `/` or the media mount. `-h` changes a symbolic link itself and never its target, so a link inside `/data` that points into `/media` cannot make the entrypoint write there (invariant 2). The README says a relocated data path must be made writable by hand.
  **Issue:** #35
- **Decision:** `PUID`/`PGID`: empty counts as unset (default 1000); otherwise digits only, at most 4294967294, leading zeros dropped; each invalid variable gets its own Error line with `variable` and `reason` properties, like the app's configuration errors, and the exit code is 1. The value is echoed after being reduced to printable ASCII and 200 characters and JSON-escaped. The warning for running as root is tied to `PUID=0` (a `PGID` of 0 alone is not root). Before the real `exec`, the entrypoint tries `setpriv ... true` and reports a failure as a JSON Error line.
  **Why:** "Non-negative integer" needs an upper bound that `setpriv` accepts; 4294967295 is the kernel's "no change" ID. The trial run keeps even a missing `SETUID`/`SETGID` capability from producing a non-JSON line. Entrypoint lines go to standard output with `sourceContext` `n8Tracks.Entrypoint`, the stream the application log uses.
  **Issue:** #35
- **Decision:** The entrypoint also writes an Information line when it changes an owner, runs the app as `dotnet /app/n8Tracks.Api.dll` (published with `UseAppHost=false`), and does not pass container arguments on to the app.
  **Why:** A recursive ownership change of an operator's folder should leave a trace. One way of starting the binary serves both the entrypoint and the `HEALTHCHECK`. The app is configured by environment only, and its listen flags are ignored anyway.
  **Issue:** #35
- **Decision:** The frontend is copied into the runtime stage as `/app/wwwroot` (not into the build stage before publish), and `.dockerignore` additionally excludes `src/n8Tracks.Api/wwwroot`, `src/n8Tracks.Gateway`, `scripts/`, `e2e/`, `.idea/`, `web/coverage`, `.localdata`, Compose files, and the Compose example's `data/`, `media/`, and `backup/` folders. `.gitignore` gained `/data/`, `/media/`, `/backup/`, and `/docker-compose.yml`.
  **Why:** A frontend-only change then reuses the publish layer. A developer's local `wwwroot` copy must not leak into the image, and running the Compose example from the repository must never send the operator's media or database into the build context or into git.
  **Issue:** #35
- **Decision:** The commented-out gateway service in the Compose example uses a placeholder image name (`n8tracks-gateway:dev`) and says the gateway is not packaged yet.
  **Why:** No gateway image or Dockerfile exists; the issue asks only for the commented block, and image names are M1's concern.
  **Issue:** #35
- **Decision:** The smoke script goes beyond the test plan: it also checks the version against `VERSION`, a non-default `N8TRACKS_PORT` together with the sub-path, `PUID=0` (warning, runs as root), Docker's `--user` with invalid `PUID`/`PGID` (skipped), four more invalid ID forms, that `/media` and `/backup` are absent when unmounted, and a named volume: owner and mode of the database, a recursive hand-over when `PUID`/`PGID` change, and no entrypoint line when the owner already matches. The seeded row is read from a copy of the database files. On macOS the host-side owner assertion prints a skip notice; the named-volume checks cover ownership there.
  **Why:** Each is an acceptance criterion with no other automated check. Docker Desktop's bind mounts do not keep container ownership (inside the container the file shows as 0:0), so only a volume on the Linux VM can show it on a Mac. SQLite in WAL mode should not be opened from the host across a bind mount while the container has it open.
  **Issue:** #35
- **Decision:** Guard-bite proof: each of these was applied, seen to fail, and restored. `HealthCheckCommand.Target` ignoring the base path (4 of the 14 new tests failed). Three image variants run through `scripts/smoke-docker.sh`: an entrypoint without the `setpriv` drop (failed "the app process user ID: expected '1234', got '0'"); a `HEALTHCHECK` that ignores `N8TRACKS_PORT` and `N8TRACKS_BASE_URL` (failed "Docker did not report ...-subpath as healthy within 60s"); an entrypoint that echoes a plain-text line (failed the JSON-lines check). The image before `rmdir /media` failed "health status without media: expected 'degraded', got 'healthy'".
  **Why:** The brief and the test plan ask for each guard to be seen failing against a broken state.
  **Issue:** #35
- **Decision:** The two-platform build was verified on a temporary `docker-container` builder (removed afterwards), without `--load` or `--push`; in addition the `linux/amd64` image was loaded once, run under emulation on the arm64 host, and built with `--build-arg VERSION=9.8.7`, which `/health` reported.
  **Why:** Docker Desktop's default `docker` driver refuses multi-platform builds here. Running the cross-compiled image shows the `TARGETARCH` publish really works on the other architecture, and the override run covers the build-argument half of the version criterion.
  **Issue:** #35
- **Decision:** The gateway binary gained a `--healthcheck` mode (`n8Tracks.Gateway.Health.HealthCheckCommand`, called first thing in `Program.RunAsync`), since the discretion note names it and it did not exist. It reads only `N8TRACKS_GATEWAY_PORT` (new `GatewayOptionsLoader.LoadPort`), requests `/health` on 127.0.0.1 with a 4-second timeout and no proxy, exits 0 only for 200, and writes one line in the gateway's log shape through the startup logger (so `N8TRACKS_LOG_LEVEL` cannot hide it). It does not read `N8TRACKS_API_URL` and never contacts n8Tracks itself.
  **Why:** The image installs no `curl` or `wget`, and the check has to follow the port setting. The gateway keeps its own copy because it may not reference the app's code (invariant 5). 4 seconds sits above the gateway's 3-second upstream budget, so a gateway waiting on a silent upstream still passes as degraded, and inside Docker's 5-second timeout.
  **Issue:** #36
- **Decision:** The gateway build has its own context list, `src/n8Tracks.Gateway/Dockerfile.dockerignore` (everything excluded, then the four root build files, the gateway, and ServiceDefaults re-included). The root `.dockerignore` still excludes the gateway.
  **Why:** The build context is the repository root, where the root `.dockerignore` hides the gateway's sources; BuildKit prefers a `<Dockerfile>.dockerignore` next to the Dockerfile. Re-including the gateway in the root file would put it into the app image's `COPY src/` layer and rebuild the app on every gateway change. The gateway's context is 45 KB.
  **Issue:** #36
- **Decision:** The gateway runtime stage is `mcr.microsoft.com/dotnet/aspnet:10.0` with nothing installed (no `tzdata`), `USER $APP_UID` (the base image's `app` user, 1654, written numerically so an orchestrator can verify it is not root), no volume, no entrypoint script, and `ENTRYPOINT ["dotnet", "/app/n8Tracks.Gateway.dll"]`. `TZ` was dropped from the gateway service in the Compose example.
  **Why:** Same base images as the app image (looked up for #35 today), so one Dependabot proposal covers both. The gateway writes UTC timestamps and shows no local time, so time zone data and `TZ` would do nothing. A second `docker` entry in `.github/dependabot.yml` covers the new Dockerfile's directory.
  **Issue:** #36
- **Decision:** The Compose example's gateway service uses plain `depends_on: [n8tracks]`, not `condition: service_healthy`.
  **Why:** The gateway probes n8Tracks afresh on every health request and is designed to run degraded while the app is away; waiting for the app's health would only delay it, and would keep the gateway from starting at all when the app is unhealthy.
  **Issue:** #36
- **Decision:** The smoke script's gateway part goes beyond the test plan: it also checks the image's user and process IDs (1654), that the image declares no volume and holds no app assembly, the reported version, a non-default `N8TRACKS_GATEWAY_PORT` (the health check follows it), the gateway returning to `healthy` when the app is started again, and the gateway log shape. "The container stays healthy" is asserted on a health check Docker itself ran after the app stopped (up to 30 seconds' wait), not on the status left over from before. `json_field` now prints non-string values as JSON (`true`, `null`). New variables: `N8TRACKS_SMOKE_GATEWAY_IMAGE`, `N8TRACKS_SMOKE_GATEWAY_PORT` (host port 18788).
  **Why:** Each is an acceptance criterion or a documented behaviour with no other automated check; a status read right after stopping the app would pass whatever the health check does.
  **Issue:** #36
- **Decision:** The uncommented Compose service was verified by hand, not by the smoke script: the block was uncommented into a temporary `docker-compose.yml` (git-ignored, removed afterwards) and started with `docker compose up -d --build`; both containers reported `healthy` and the gateway's health was `healthy` / `reachable` / `compatible: true`.
  **Why:** The example uses the real host ports 8787 and 8788 and `./data` and `./media` next to the file; a script that runs it would need to rewrite the file it is meant to prove.
  **Issue:** #36
- **Decision:** Guard-bite proof: each of these was applied, seen to fail, and restored or removed. `HealthCheckCommand.Target` ignoring the port setting (11 of the 14 new tests failed). Four gateway image variants run through `scripts/smoke-docker.sh`: `USER root` (failed "the gateway image's user: expected '1654', got 'root'"); a `HEALTHCHECK` pinned to port 8788 (failed "Docker did not report ...-gateway-port as healthy within 60s"); a `HEALTHCHECK` that also needs the app to answer (failed "exit code of Docker's next health check of the degraded gateway: expected '0', got '1'"); built with `--build-arg VERSION=9.8.7` (failed "gateway health status: expected 'healthy', got 'degraded'", and its `/health` reported 9.8.7).
  **Why:** The brief and the test plan ask for each guard to be seen failing against a broken state. The last one also covers the build-argument half of the version criterion.
  **Issue:** #36
- **Decision:** The two-platform gateway build was verified on a temporary `docker-container` builder (removed afterwards) without `--load` or `--push`; in addition the `linux/amd64` image was loaded once and run under emulation on the arm64 host, where `/health` answered and `--healthcheck` exited 0.
  **Why:** Docker Desktop's default driver refuses multi-platform builds here, and running the cross-compiled image shows the `TARGETARCH` publish works on the other architecture.
  **Issue:** #36
- **Decision:** The AppHost is `src/n8Tracks.AppHost` on project SDK `Aspire.AppHost.Sdk/13.6.0`, with `Aspire.Hosting.JavaScript` 13.6.0 for the frontend and `Aspire.Hosting.Testing` 13.6.0 in the tests (all looked up today; the old `Aspire.Hosting.NodeJs` stopped at 9.5.2). `AspireUseCliBundle` is `false` and its notice `ASPIRE010` is suppressed with a rationale in the project file, where the current template sets it to `true`.
  **Why:** With the bundle on, `dotnet run` hands over to the Aspire CLI: on the first run here it downloaded the CLI, wrote an `aspire.config.json` into the project, and stopped at a macOS keychain prompt to trust a development certificate. With it off, the orchestrator and dashboard come from NuGet with the restore and `dotnet run` starts the project directly, which is the one-command, fresh-clone behaviour the story asks for. Aspire's documentation names this as the supported way to keep NuGet-restored orchestration.
  **Issue:** #37
- **Decision:** The AppHost's only launch profile is plain HTTP on localhost (`ASPIRE_ALLOW_UNSECURED_TRANSPORT=true`): dashboard 15187, its OTLP collector 15188, its resource service 15189, and `launchBrowser` off.
  **Why:** No development certificate has to be trusted on a fresh clone, and the services' exporters reach the collector without one. The dashboard keeps its login token and no browser opens, as the discretion notes say. Aspire still prints a warning that no trusted development certificate was found; the README says it can be ignored.
  **Issue:** #37
- **Decision:** The API and gateway are project resources without their launch profiles, each with one fixed, unproxied HTTP endpoint whose port is written to `N8TRACKS_PORT` / `N8TRACKS_GATEWAY_PORT`. The AppHost sets only `N8TRACKS_*` variables (and forwards `TZ`); it reads each from its own configuration first (the developer's environment, or a `NAME=value` argument), so every default can be replaced without a code change. A relative path a developer sets is resolved against the directory the command was run in. As a result the services run in the `Production` environment under the AppHost, as they do in a container (no OpenAPI document).
  **Why:** The acceptance criteria ask for the container's variables and nothing else. Passing values explicitly, instead of relying on the child processes inheriting the AppHost's environment, makes the override testable in the app model. The services resolve a relative path against their own working directory (their project folder), which is not where the developer typed the command.
  **Issue:** #37
- **Decision:** "`./.localdata`" is `src/n8Tracks.AppHost/.localdata` (and `.localdata/media`), next to the AppHost's project file, not the working directory and not the API's own `src/n8Tracks.Api/.localdata`. They are created only when the developer has not set `N8TRACKS_DATA_PATH` / `N8TRACKS_MEDIA_PATH`. `N8TRACKS_BACKUP_PATH` is left unset unless the developer sets it.
  **Why:** The folder is then the same wherever the command is run from, and a stack started by the AppHost never shares a database with an API started on its own (two instances must not share a data path). A folder the developer names is theirs to create. The backup path may be missing and is not part of health.
  **Issue:** #37
- **Decision:** The frontend is `AddViteApp` on `web/` with `WithNpm(install: false)` and a fixed, unproxied endpoint on 5173. Before the resource starts, the AppHost checks for `web/node_modules/vite`; when it is missing it writes the "run `npm install` in web/" message to the resource's log and to its own console and fails the resource (state `FailedToStart`). Aspire still lists a `frontend-installer` resource that starts only when clicked. `web/vite.config.ts` reads the backend address from `N8TRACKS_API_URL` (blank counts as unset), default `http://localhost:8787`.
  **Why:** The acceptance criterion wants a missing install reported, not repaired, with the API and gateway unaffected; the Vite integration's default is to install automatically. The variable name is the one the gateway already uses for the same address.
  **Issue:** #37
- **Decision:** The gateway and the frontend do not wait for the API (`WaitFor` is not used); both services have an HTTP health check on `/health` so the dashboard shows health, not just "running".
  **Why:** The gateway is built to run degraded while the app is away (same reasoning as the Compose example in #36), and the dev server proxies per request.
  **Issue:** #37
- **Decision:** The startup test passes its own free ports and a temporary data folder as `N8TRACKS_*` overrides instead of using 8787 and 8788; the fixed default ports are asserted on the app model without starting it. The model tests give the unstarted endpoints their known localhost addresses so the environment can be resolved. The override complement sets `N8TRACKS_LOG_LEVEL` in the test process's real environment; the tests that build the model share one non-parallel collection.
  **Why:** A developer who has the stack running must still be able to run `dotnet test`, and the test doubles as proof that the overrides work end to end. Resolving an endpoint reference waits for the orchestrator unless the endpoint is allocated.
  **Issue:** #37
- **Decision:** "Not part of any Docker image" is guarded beyond the test plan: `ImageIsolationGuardTests` (both build-context lists, both Dockerfiles, every other project file, and the resolved dependency graphs of the API and the gateway), a row for the AppHost in the architecture tests' reference table, and a new check in `scripts/smoke-docker.sh` that neither built image holds an AppHost file or an `Aspire.*` assembly. No Dockerfile or `.dockerignore` needed changing: #35 and #36 already left `src/n8Tracks.AppHost/` out.
  **Why:** It is the maintainer's stated requirement for this story and had no automated check.
  **Issue:** #37
- **Decision:** Guard-bite proof: each of these was applied, seen to fail, and restored. The AppHost ignoring the developer's `N8TRACKS_LOG_LEVEL` (the override test failed); the gateway pointed at `http://localhost:9` (the startup test and the model test failed); the local folders not created (1 failed); `src/n8Tracks.AppHost/` removed from `.dockerignore` (1 failed); an `Aspire.Hosting` package reference added to the API (2 failed); an image with `/app/Aspire.Hosting.dll` run through the smoke script (failed "holds the AppHost or an Aspire assembly").
  **Why:** The brief and the test plan ask for each guard to be seen failing against a broken state.
  **Issue:** #37
- **Decision:** The frontend resource and the dashboard were verified by the Demo, by hand: all three resources `Running`, health traces of `n8tracks` and `n8tracks-gateway` and their log records in the dashboard, the frontend proxying `/health` to the API, and, with `web/node_modules` moved aside, `frontend` in `FailedToStart` with the message while the API and gateway answered healthy.
  **Why:** The test plan assigns these to the Demo; an automated test of the missing install would need a second switch in the AppHost just for the test.
  **Issue:** #37

## /n8-exec M1 — 2026-10-04


- **Decision:** Action versions in `.github/workflows/ci.yml`, each the major of the latest release looked up today: `actions/checkout@v7` (v7.0.1), `actions/setup-dotnet@v6` (v6.0.0), `actions/setup-node@v7` (v7.0.0), `actions/upload-artifact@v7` (v7.0.1). Checkout runs with `persist-credentials: false`.
  **Why:** The milestone rule is a lookup, not memory, pinned at the major. No job pushes, so the token does not need to stay in the checkout's git configuration.
  **Issue:** #39
- **Decision:** The pull-request trigger is `branches: [main]` with the default activity types (opened, synchronize, reopened) and no path filter. The concurrency group is `ci-<pull request number>`, or `ci-<run id>` when there is no pull request, and `cancel-in-progress` is on only for the `pull_request` event.
  **Why:** The default types already include drafts. In a called workflow the `github` context is the caller's, so a publish on `main` or a tag has no pull request number and gets a group of its own; it is never cancelled by this workflow.
  **Issue:** #39
- **Decision:** "Every check runs even after an earlier one fails" is done with a per-step condition (`!cancelled()` and the prerequisite step's outcome is `success`), not `continue-on-error`. Prerequisites: `dotnet restore` for `dotnet format`; the build for `dotnet test`; `npm ci` for every npm check. The failure summary is one generic step per job that prints the ids of the steps whose outcome is `failure` from the `steps` context, so every step that can fail has an `id`.
  **Why:** `continue-on-error` would leave the job green. Reading the `steps` context means a step added later is reported without touching the summary.
  **Issue:** #39
- **Decision:** The `ci` job checks `toJSON(needs)` with `jq`: it fails unless there is at least one needed job and every result is `success`. A job a later story adds only has to be added to `needs:`; the file's header and the README say so.
  **Why:** One expression covers failed, cancelled, and skipped dependencies and does not have to be edited per job. Nothing in this story checks that a new job was added to `needs:`; that is left to review (the suppression-guard story may add a check).
  **Issue:** #39
- **Decision:** `global.json` is used as it is (`10.0.201`, `rollForward: latestFeature`): on the runner `actions/setup-dotnet` resolved it to SDK 10.0.401, already on the image. The NuGet cache key hashes `**/*.csproj`, `Directory.Build.props`, `Directory.Build.targets`, and `global.json`.
  **Why:** That is what the pin in `global.json` means, and it is the same resolution a developer's machine does. Changing the roll-forward policy is not this story's to decide. `Directory.Build.targets` was added to the key files because it is a props-like file that can change restore.
  **Issue:** #39
- **Decision:** The ESLint warning in the bite test was a `console.log` in a new `web/src` file under an inline `/* eslint no-console: "warn" */` comment.
  **Why:** Every rule the project's configuration turns on is at `error`; an inline rule comment is the smallest real warning-level finding, and it is `--max-warnings 0` in the `lint` script that turns it into a failure.
  **Issue:** #39
- **Decision:** Guard-bite proof, on throwaway pull request #163 (branch `throwaway/39-gate-bites`, made in a temporary worktree, closed without merging, branch deleted). Each commit held one fault and `ci` went red each time: (a) C# unused variable: `dotnet build` failed with CS0219, `dotnet format` still ran, `dotnet test` was skipped (run 37183757768); (b) ESLint warning in `web/`: only `npm run lint` failed, the other web checks ran and passed (run 37183823275); (c) formatting change in `extension/`: only `npm run format:check` failed and the zip upload was skipped (run 37183929815); (d) failing test in `n8Tracks.Api.Tests`: `dotnet test` failed, 1 failed and 345 passed in that project (run 37184026795). Two more pushes 25 seconds apart showed the superseded run cancelled (run 37184131485).
  **Why:** The test plan asks for the four faults to be seen turning `ci` red; the cancellation is an acceptance criterion with no other check.
  **Issue:** #39
- **Decision:** `e2e/` versions, each looked up today: `@playwright/test` 1.63.0, `@axe-core/playwright` 4.13.0, `eslint-plugin-playwright` 2.12.0; ESLint 10.12.0, typescript-eslint 8.71.0, Prettier 3.9.9, and `@types/node` 24.19.1 as in `web/`. TypeScript stays on 6.0.x although 7.0.2 is current. The project has no `build` script: Playwright runs the TypeScript directly, and `npm run typecheck` is the compile check.
  **Why:** The milestone rule is a lookup, not memory. TypeScript 6.0.x is the limit typescript-eslint supports, the same reason `web/` gives (#31). `@types/node` follows the Node 24 the project pins, not the newest major.
  **Issue:** #41
- **Decision:** The two Playwright projects, `root` and `subpath`, differ only in `baseURL`; tests open `./` and intercept by path suffix, so one spec serves both. A test that belongs to one project carries the tag `@root-only` or `@subpath-only`, and the other project's `grepInvert` leaves it out: the degraded test (the no-media container is at the root) and the sub-path complement. Result: 7 tests in each project, 14 in all.
  **Why:** Running the degraded test in both projects would run the same container twice, and a skipped test would trip the lint rule against skipped tests. Tags keep the suite free of conditionals.
  **Issue:** #41
- **Decision:** The accessibility helper is two functions in `e2e/support/a11y.ts`: `expectNoA11yViolations(page)` (one axe scan, tags `wcag2a`, `wcag2aa`, `wcag21a`, `wcag21aa`) and `expectAccessibleInLightAndDark(page)`, which sets the colour control to light, scans, sets it to dark, and scans. Before a scan the helper waits for running transitions to finish. The colour-control tests themselves do not scan: every state they pass through is the healthy page in light or dark, which the first test scans.
  **Why:** "Once with the control set to light and once to dark" is the same two steps after every state, so it is one call. A scan in the middle of a colour transition measures colours that are never shown at rest and would make the contrast rule flaky.
  **Issue:** #41
- **Decision:** Global setup waits for each container to report the status it is meant to have (`healthy`, and `degraded` for the one without media) at its own page URL, not just for a listening port; it also checks that Docker is running, and it removes the containers and the temporary directory if starting fails half-way. The sub-path container keeps port 8787 inside and gets `N8TRACKS_BASE_URL=http://localhost:18788/n8tracks/`.
  **Why:** A container that is up but in the wrong state would fail every test with a less useful message. A sub-path container that ignores its base URL never becomes healthy under `/n8tracks/health`, so the run stops in setup; the complement test (404 for `/`, `/health`, `/index.html`, and a deep link outside the sub-path) is the second guard.
  **Issue:** #41
- **Decision:** The suite found no accessibility violation in `web/`, so `web/` is unchanged. The suite was green on its first run against `n8tracks:dev` in all 14 tests.
  **Why:** #31 already held the palette to AA contrast by test and used labelled Mantine components. The bite proofs below show the green result is not an empty scan (axe reported 21 passing rules on the page, `color-contrast` among them).
  **Issue:** #41
- **Decision:** Guard-bite proof, with a deviation from the test plan. (a) As the plan says, the `aria-label` was removed from the colour control and the image rebuilt (as `n8tracks:e2e-bite`): the suite went red, 13 of 14 failed, but not through axe. axe-core 4.13 has no rule that requires a `radiogroup` to have a name (its `aria-input-field-name` rule does not cover that role), and it reported no violation with every rule on. The failures came from the suite's own locators, which find the control by role and name. (b) To see the axe scan itself fail, the dark body text colour in `web/src/theme/palette.ts` was changed to `#4a4a4f` and the image rebuilt: 9 of 14 failed, each with a `color-contrast` violation listing the elements, in the dark scan only (the light scan before it passed); the 5 that do not scan passed. Both changes were restored and the bite image removed. (c) Setup messages seen: a missing image, a port in use (with a leftover container from an "aborted run" removed first), and the sub-path container started without its base URL (setup failed after 60 seconds naming the container).
  **Why:** The test plan asks to watch the axe scan fail; the planned fault turned out not to be one axe detects, so a second fault that it does detect was used, and the control's name is guarded by the locators instead.
  **Issue:** #41
- **Decision:** `e2e/` was already excluded from the application image's build context (`.dockerignore`, since #35) and the gateway's context is an allow-list, so neither changed. `.gitignore` gets `e2e/test-results/` and `e2e/playwright-report/`; `.github/dependabot.yml` gets a weekly npm entry for `/e2e`. No CI job is added.
  **Why:** The discretion lines ask for the two entries; CI wiring is #42.
  **Issue:** #41
- **Decision:** The `container` job uses `docker/setup-qemu-action@v4`, `docker/setup-buildx-action@v4`, and `docker/build-push-action@v7` (latest releases today: v4.4.0, v4.4.1, v7.4.0). The two images those actions pull at run time are pinned to exact releases: `tonistiigi/binfmt:qemu-v10.2.3-68` (the action's default is `latest`) and `moby/buildkit:v0.33.1` (the default is a moving `buildx-stable-1`). Builds go through `build-push-action` with `context: .`, `push: false`, and `provenance: false`; its build-record artifact is turned off.
  **Why:** The milestone rule: versions from a lookup, major tag for actions, exact release for anything downloaded at run time. The build action is what gives BuildKit the token for the GitHub Actions cache backend. Without `context: .` the action builds from a Git checkout of its own, not the checked-out tree. The build record would be a second artifact nobody asked for.
  **Issue:** #40
- **Decision:** The smoke script keeps its existing switch, `N8TRACKS_SMOKE_SKIP_BUILD=1` (from #35); no second `SMOKE_SKIP_BUILD` name was added.
  **Why:** The mode the discretion line asks for already exists under the project's `N8TRACKS_` prefix; two names for one switch would only add a way to get it wrong.
  **Issue:** #40
- **Decision:** `scripts/smoke-docker.sh` now prints `PASS <name>` / `FAIL <name>` and no longer stops at the first failure. Each section is a function run in a subshell: a failed assertion is recorded and the section carries on; only a container that never answers (or never exits) stops its own section, and the next section still runs. The failed lines are repeated at the end and the exit code is 1. A container's log is printed when it fails an assertion (once for each container) and for any container a stopped section left behind, as well as from the exit trap; all output is one stream (`exec 2>&1`). Waiting for Docker's health status ends as soon as Docker says `unhealthy`.
  **Why:** "Runs every assertion" cannot be literal for a container that never came up: every later check of it would fail for the same reason. Logs cannot wait for the exit trap alone, because sections share host ports and a section's containers must be removed before the next one starts. One stream keeps a FAIL line next to the log it belongs to in the CI log.
  **Issue:** #40
- **Decision:** In the `container` job the two-architecture builds run even when the smoke script failed (they need only QEMU and Buildx), and the job's failure summary lists the failed smoke assertions. The image artifact is saved and uploaded only after a green smoke run.
  **Why:** The gate's rule from #39: one run shows every failure. An image that failed the smoke script must not reach the end-to-end job.
  **Issue:** #40
- **Decision:** The smoke step names `shell: bash`. The first bite run (throwaway pull request #164, run 37186180438) showed the gap: the script printed three FAIL lines and exited 1, yet the step, the `container` job, and `ci` were green, because the step pipes the script through `tee` (to keep the log for the failure summary) and the default shell of a `run` step has no `pipefail`.
  **Why:** GitHub adds `-o pipefail` only when the shell is named. The bite test is what found it; the fix was pushed and the same throwaway pull request re-run.
  **Issue:** #40
- **Decision:** The `container` job's failure summary reads each step's outcome by name instead of `toJSON(steps)` as the other jobs do. On the red bite run (37186603738) the summary step itself could not start: "Argument list too long".
  **Why:** `toJSON(steps)` includes every step's outputs, and the build action's outputs hold the whole build metadata, which is more than one environment variable may hold. The job was red either way, but the summary was missing.
  **Issue:** #40
- **Decision:** Guard-bite proof, on two throwaway draft pull requests made in temporary worktrees, both closed without merging and their branches deleted. The fault each time: the Dockerfile's `HEALTHCHECK` pointed at `/app/wrong/n8Tracks.Api.dll`. #164, run 37186180438: the script failed but CI stayed green (the missing `pipefail`, above). #164 with the fix, run 37186603738: `container` and `ci` red, 3 failed and 93 passed, but the failure summary could not start (above). #165 with both fixes, run 37186984530: `container` and `ci` red, the log names the three failed assertions ("Docker reports … as healthy within 60s" for the main, no-media, and sub-path containers) with Docker's health-check output and the containers' logs, the failure summary ran, and no `app-image` artifact was uploaded. The two-architecture builds still ran and passed in the red runs.
  **Why:** The test plan asks for the smoke script and `ci` to be seen going red on a wrong health check path, on a throwaway pull request.
  **Issue:** #40
- **Decision:** The host-side ownership assertion, skipped on macOS since #35, ran for the first time on the Linux runner and passed unchanged (`PASS owner of n8tracks.db on the host: 1234:1235`); the script reports 96 checks on Linux and 95 on macOS.
  **Why:** M0 left this as an open risk for this story; no fix was needed.
  **Issue:** #40
- **Decision:** The `e2e` job uses `actions/download-artifact@v8` and `actions/cache@v6` (latest releases today: v8.0.1, v6.1.0), next to the pins the gate already has. It runs `e2e/`'s lint, typecheck, and format check, then loads `n8tracks:dev` from the `app-image` artifact (`gunzip -c … | docker load`, with `shell: bash` for `pipefail`) and runs `npm test`. The Chromium download is cached under a key made of the installed Playwright version; `npx playwright install --with-deps chromium` runs every time, because the system libraries are not in the cache.
  **Why:** The milestone rule for versions. Keying on the Playwright version, not on the lock file, keeps the browser cached across unrelated dependency updates.
  **Issue:** #42
- **Decision:** The CI settings live in `e2e/playwright.config.ts`, switched on by the `CI` variable the runner sets: `retries: 1`, `trace: on-first-retry`, and the HTML (`playwright-report/`) and JSON (`test-results/results.json`) reporters beside the list reporter. Local runs are unchanged.
  **Why:** One configuration file, and a local `CI=1 npm test` reproduces exactly what the job does.
  **Issue:** #42
- **Decision:** The flaky list comes from `e2e/scripts/summarize.ts`, which reads the JSON report, writes the failed and the flaky tests (and errors outside any test, and a missing report) to the job summary, and sets `flaky` and `failed` step outputs. It has six unit tests run with Node's own test runner (`npm run test:scripts`, a step of the job); no test library was added.
  **Why:** A flaky test leaves the run green, so the summary is the only place it shows; logic that decides what a maintainer sees deserves tests, and `node --test` runs TypeScript directly on Node 24.
  **Issue:** #42
- **Decision:** The `e2e-report` artifact (7 days) is uploaded when the test step failed or the summary counted a flaky test, and holds `e2e/playwright-report/` and `e2e/test-results/` (traces, `results.json`, and `container-logs/`). The containers' logs are written by the suite's own teardown when `N8TRACKS_E2E_LOG_DIR` is set, before it removes the containers.
  **Why:** Teardown removes the containers, so a later workflow step has nothing to read. The teardown also runs when setup fails half-way, which is when the logs matter most.
  **Issue:** #42
- **Decision:** In CI the run stops after ten failed tests (`maxFailures: 10`); not in the issue's list of settings. The summary then shows Playwright's "Testing stopped early after 10 maximum allowed failures."
  **Why:** The first bite run (below) took 12 minutes of the job's 20 with 14 tests, because every test waited out its 30-second timeout twice. A larger suite with the same fault would be cancelled at the limit before the report and the artifact exist. Ten failures are enough to see that the page is broken everywhere.
  **Issue:** #42
- **Decision:** Guard-bite proof on throwaway draft pull request #166 (made in a temporary worktree, closed without merging, branch deleted), three runs. (a) A temporary test that passes only on its retry, run 37188035955: `e2e` and `ci` green, the job summary lists it as flaky in both projects, and `e2e-report` was uploaded with the report, both retry traces, and the three container logs. (b) The fault the test plan names, the colour control without its `aria-label`, run 37188508597: `e2e` and `ci` red, 13 of 14 failed, `e2e-report` uploaded. As #41 recorded, axe has no rule for an unnamed `radiogroup`; the failures are the suite's locators. (c) So that an axe violation itself is seen turning the gate red, the dark body text set to `#4a4a4f`, run 37189470902: `e2e` and `ci` red, 9 failed and 5 passed, each failure a `color-contrast` violation, `e2e-report` uploaded with nine traces. In (b) and (c) the `web` job was red as well (its component test and its palette contrast test catch the same faults), so `ci` had two reasons; the `e2e` job is red on its own. Complement: the green run on #157 (37187933104) has no `e2e-report` artifact, only `extension-zip` and `app-image`, and ran 7 tests in each project.
  **Why:** The test plan; (c) is the same substitution #41 made, for the same reason.
  **Issue:** #42
- **Decision:** `GatewayProcessTests.TheEndpointVariableMakesTheGatewayExportItsTraces` failed once in the `dotnet` job (run 37189463416) on a commit that touched only `e2e/playwright.config.ts`: `StubOtlpCollector.DisposeAsync` threw "Address already in use". Filed as #167 and the failed job re-run; not fixed here.
  **Why:** An intermittent fault in M0 test support, outside this story.
  **Issue:** #42
- **Decision:** The guard is `scripts/check-suppressions.sh`, a POSIX wrapper that runs `scripts/check-suppressions.py` (Python 3, standard library only). No set-up step is added to CI: the runner image already has `python3`.
  **Why:** The issue allows Python "if the rules become unwieldy", and they do: an XML comment or a `[SuppressMessage(...)]` can span lines, and a rationale has to be read out of string literals and three comment syntaxes. The `.sh` name the issue and the invariant refer to stays the entry point.
  **Issue:** #43
- **Decision:** Run against the repository before any fix, the script found four suppressions without a rationale: the `eslint-config-prettier` entry in `web/`, `extension/`, and `e2e/eslint.config.js`, and `#nullable disable` in `Migrations/20261004042959_InitialCreate.cs` (EF Core writes the migration itself without the `<auto-generated` header; only its `.Designer.cs` and the model snapshot carry it). Each got a one-line comment; nothing was removed. `ASPIRE010` in the AppHost project and the one `eslint-disable-next-line` in `e2e/scripts/summarize.ts` already carried a rationale and passed unchanged.
  **Why:** The test plan asks for the pre-change result on record. A consequence for later stories: every new EF Core migration needs a comment line directly above its `#nullable disable` (noted in `docs/conventions.md`).
  **Issue:** #43
- **Decision:** What counts as a rationale, beyond the issue's list: the comment must be on the same line or the line directly above (a blank line in between breaks it); a comment that runs over several lines is read as a whole, so the ten-character minimum applies to the whole comment; leading punctuation (`--`, `:`, `<`) is not counted. A `[SuppressMessage]` passes with a `Justification` string (literals joined with `+` are read; a constant reference is not a rationale) or with a comment. In MSBuild files, text inside an XML comment is never a suppression, and a comment that shares its line with another suppression explains only that one.
  **Why:** The issue fixes the accepted forms but not these edges; each is the stricter reading that still lets the existing AppHost comment pass on its merits.
  **Issue:** #43
- **Decision:** Patterns added to the issue's list while writing the closed list: `[UnconditionalSuppressMessage]`, `WarningsNotAsErrors` as an attribute, the `option = value:severity` form of an `.editorconfig` severity (as well as `dotnet_diagnostic.*.severity` and `dotnet_analyzer_diagnostic.*.severity`; below warning means `none`, `silent`, `suggestion`, and their aliases `hidden`, `refactoring`, `info`), an inline `/* eslint rule: "off" */` comment (needs a trailing `-- reason`, like `eslint-disable`), and legacy `.eslintrc.js`/`.eslintrc.cjs` beside `eslint.config.*`. Not covered, and left for a later amendment if wanted: `.eslintrc.json`, `.ruleset` files, `<MSBuildWarningsAsMessages>`, and `prettier-ignore`.
  **Why:** Each added pattern is another spelling of something the issue already lists, and would otherwise be the obvious way round the guard. The ones left out are a different mechanism or not a warning at all.
  **Issue:** #43
- **Decision:** The fixture test (`scripts/tests/check-suppressions/run.sh`) has 17 cases under `fixtures/<case>/` with `bad/`, an `expected` list of `file:line`, and `good/`; the bad tree must be reported at exactly the expected lines (no more, no fewer). Five further checks build a throwaway git repository to prove what is not scanned (`bin/`, `obj/`, `node_modules/`, `dist/`, untracked files, the fixtures themselves) and that a root which is not a directory or not in a repository is exit code 2, not a pass. The `guards` job runs the fixture test, then the script, with no dependency on the other jobs.
  **Why:** "Exactly" catches a rule that starts firing where it should not, as well as one that stops firing. A scan that silently finds no files would otherwise look like a clean repository.
  **Issue:** #43
- **Decision:** The end-to-end suite (`npm test` in `e2e/`) was not run locally for this story; its lint, typecheck, format check, and script tests were, and the `e2e` job on #157 ran the suite.
  **Why:** The only change under `e2e/` is a comment in `eslint.config.js`.
  **Issue:** #43
- **Decision:** Guard-bite proof on throwaway draft pull request #168 (made in a temporary worktree, closed without merging, branch deleted), run 37190630418. One unexplained suppression in each of the four components: `#pragma warning disable` in a new file of the backend, `#nullable disable` in a new file of the gateway, a rule set to `'off'` in `web/eslint.config.js`, and the extension's `eslint-config-prettier` entry with its comment removed (its state before this story). `guards` and `ci` were red; `dotnet`, `web`, `extension`, `container`, and `e2e` were all green, so nothing else would have caught any of the four. The log names each file and line. Complement: on #157 at d04d5c1 (run 37190616313) `guards` and `ci` are green. The fixture test bites too: with `TODO` taken out of the script's placeholder list locally, `run.sh` reported `FAIL placeholder-rationales` (38 passed, 1 failed, exit 1); restored, 39 passed.
  **Why:** The must-have is a red check for a suppression without a rationale in any of the four components; the milestone rule is to prove a CI failure on a throwaway pull request, not on #157.
  **Issue:** #43
