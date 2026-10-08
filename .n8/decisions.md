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
- **Decision:** The configuration half lives in the same `scripts/check-suppressions.py`, run by the same invocation and the same `guards` step; there is no flag that selects one half. Its findings go to the same output (`file:line: what`, or `file: what` for a missing file or folder) with their own closing line, "No comment excuses these".
  **Why:** The issue's key link is "the same script invocation the rationale checks use". A switch for one half would itself be a way to weaken the guard.
  **Issue:** #44
- **Decision:** Run against the repository before any change, the new checks found nothing (exit 0, no output): the root `Directory.Build.props` sets the four pinned properties once and unconditionally, no other MSBuild file declares them, no command line weakens them, and all three lint scripts and seven `tsconfig*.json` files pass. No fix was needed.
  **Why:** The test plan asks for the pre-change result on record.
  **Issue:** #44
- **Decision:** The fixture test now lays every case over one clean tree, `scripts/tests/check-suppressions/base/` (root props, a project, a workflow, a Dockerfile, a shell script, and the three JavaScript folders), in a throwaway git repository, instead of each case being a complete root. A case holds only the files that differ (`bad/`), an optional `remove` list for the "missing" cases, `expected` (place, then text the finding must contain, which is how the named setting is asserted), and `excused/`: the same files with a rationale comment added, which must be reported exactly as `bad/` is. The 17 rationale cases are laid over the same base, unchanged. 34 `config-*` cases, 30 with `excused/` (the four without are a deleted file or folder, where there is nowhere to put a comment); the base alone must pass with no output. 104 checks.
  **Why:** The issue asks for each fixture to be "a small root with a root props file and the three JS folders". Complete roots would be about 350 near-identical files, and the 17 existing cases would all have needed the same files added to stay clean. Laid over the base, each case is that small root when it runs.
  **Issue:** #44
- **Decision:** Stricter readings of the criteria. In the root props each pinned property must appear exactly once, with no condition on it or on its `PropertyGroup`, and not inside a `Choose` or `Target`. A pinned property is also caught outside the root in an attribute or a property list (`Properties="TreatWarningsAsErrors=false"`). `Nullable` must be `enable` in the root, and elsewhere any value but `enable` fails (not only `disable`). The lint script fails on any `--max-warnings` value other than 0 and on `||` (`eslint . --max-warnings 0 || true`). A `tsconfig` that cannot be parsed fails. A solution-style `tsconfig.json` (`"files": []`, no `include`; `web/` and `extension/` have one) compiles nothing and is exempt from resolving to strict, but every file it lists under `references` is checked whatever its name. tsconfig option names are read without regard to case, as `tsc` reads them.
  **Why:** Each is the obvious way round the literal rule. The exemption is needed for the repository to pass at all, and following `references` closes the hole it would open.
  **Issue:** #44
- **Decision:** Added to the closed list beyond the issue: the MSBuild properties `CodeAnalysisTreatWarningsAsErrors`, `WarningLevel`, `AnalysisMode*`, and `AnalysisLevel<Category>` (declared nowhere, like `RunAnalyzers`); on command lines `-err-`, `-warnasmessage`, `-warnnotaserror`, `-noerr`, `WarningsNotAsErrors=`, an analyzer property set to false, `AnalysisLevel=`, `AnalysisMode=`, `WarningLevel=`, `Nullable=` other than enable, and a strict-family flag passed as `false` (`tsc --strict false`); and two more places command lines are read: MSBuild response files (`*.rsp`; `Directory.Build.rsp` is applied to every build without appearing on any command line) and every `.yml`/`.yaml` under `.github/` (so composite actions too). Not covered, beyond the two the issue names: a tsconfig reached only through a `-p`/`--project` argument.
  **Why:** Other spellings of what the issue lists. Tightening one of these settings later (for example `AnalysisMode=All`) means changing the guard in the same pull request, which is the point of pinning.
  **Issue:** #44
- **Decision:** On a command-line file, a line that is only a `#` comment is not scanned; a comment after a command on the same line is. Matching ignores case.
  **Why:** The criterion says "on a command line", and the workflow's own comments must be able to name what is forbidden. Scanning the trailing comment costs nothing and avoids parsing shell quoting to find where a comment starts. MSBuild reads switches and property names without regard to case.
  **Issue:** #44
- **Decision:** The end-to-end suite (`npm test` in `e2e/`) was not run locally for this story; its lint, typecheck, format check, and script tests were, and the `e2e` job on #157 runs the suite.
  **Why:** Nothing under `e2e/`, `web/`, `extension/`, or `src/` changed.
  **Issue:** #44
- **Decision:** Guard-bite proof on throwaway draft pull request #169 (made in a temporary worktree, closed without merging, branch deleted), run 37191906870. One weakened setting in each of the four components, each with a rationale comment beside it: `<TreatWarningsAsErrors>false</TreatWarningsAsErrors>` in the backend's `n8Tracks.Api.csproj`, `-p:TreatWarningsAsErrors=false` on the publish command of the gateway's Dockerfile, `--max-warnings 0` taken out of `web/`'s lint script, and `noUncheckedIndexedAccess` set to `false` in `extension/tsconfig.app.json`. `guards` and `ci` were red; `dotnet`, `web`, `extension`, `container`, and `e2e` were all green, so nothing else would have caught any of the four. The log names each file, line, and setting. Complement: on #157 at 1e38836 (run 37191886761) `guards` and `ci` are green. The fixture test bites too: with `nowarn` taken out of the script's command-line pattern locally, `run.sh` reported 8 FAIL lines (the four command-line cases, `bad/` and `excused/`; 96 passed, 8 failed, exit 1); restored, 104 passed. Four of the same faults made in the working tree were each reported by the script locally and reverted.
  **Why:** The must-have is a red check for a pull request that turns off warnings-as-errors or strict checking in any component, whatever comment accompanies it; the milestone rule is to prove a CI failure on a throwaway pull request, not on #157.
  **Issue:** #44
- **Decision:** `scripts/release-tags.sh` is POSIX shell with no dependencies beyond `grep`, `sed`, and `tr` (no Python wrapper). Its inputs are four required options, `--tag`, `--version` (the contents of `VERSION`), `--in-main true|false`, and `--published "<tags>"`; `--published` may be empty but must be given, and a missing, repeated, or unknown option is exit code 2. A refused release is exit code 1 with the reason on stderr and nothing on stdout.
  **Why:** The comparison is three integers, which the shell does exactly; the issue prefers shell unless it becomes unwieldy. Requiring `--published` means a workflow whose registry lookup was left out stops, instead of treating every release as the first one and moving `latest`.
  **Issue:** #47
- **Decision:** Output is five lines in a fixed order: `version`, `push_exact` (`true`/`false`), `image_tags` (space-separated, the tags to push), `prerelease`, `latest`. When the exact version is already published, `push_exact=false`, the exact tag is left out of `image_tags`, the floating tags it is still entitled to stay in, and stderr says "X.Y.Z is already published, skip push"; the exit code is 0. `image_tags` can be empty (a re-run of a pre-release, or of a patch that a later patch superseded).
  **Why:** The issue asks for `key=value` lines for `$GITHUB_OUTPUT`, and for a re-run to complete the floating tags and skip the exact push without being an error. A boolean is what a workflow `if:` can test; the sentence is for the person reading the log.
  **Issue:** #47
- **Decision:** `X.Y` is added unless a higher stable `X.Y.*` is already published, and `latest` (the image tag and the GitHub release flag, always the same answer) unless a higher stable version of any minor is published. Published pre-releases never count as higher. So a patch on an older minor moves `X.Y` only, and a patch below its own minor's highest publishes only its exact tag.
  **Why:** This is the issue's "higher than or equal to the highest stable version already published in that scope", with the scope read as the minor line for `X.Y` and everything for `latest`; the test plan's "patch on an older minor (moves `X.Y`, not `latest`)" fixes that reading.
  **Issue:** #47
- **Decision:** `--published` takes the application image's tag list as it comes (spaces, commas, or newlines; a leading `v` is dropped). Only exact versions are read; `latest`, `edge`, `1.4`, and anything else are ignored without complaint. The script makes one decision and has no notion of which image it is for: the release workflow applies the same output to the gateway image.
  **Why:** The workflow can pass the registry's tag list without filtering it, so no tag logic leaks into the workflow. One decision for both images is the planner's rule that the pair never diverges.
  **Issue:** #47
- **Decision:** Stricter readings of the tag rule: the `v` is lower-case; `X`, `Y`, and `Z` have no leading zeros and at most nine digits; a pre-release identifier that is all digits has no leading zero (`rc.01` is refused, `rc.0a` and `rc.0` are not); any character outside letters, digits, dots, and hyphens is refused (so `_`, spaces, and `+`). Checks run in the order tag shape, `VERSION`, main, and the first failure is the one reported. `VERSION` is compared after dropping carriage returns only.
  **Why:** Leading zeros are not semantic versioning and would make `1.02.3` and `1.2.3` two tags for one version. Nine digits keeps the shell's integer comparison exact. A Windows line ending in `VERSION` is not a different version; any other stray character is reported as a mismatch with both values quoted.
  **Issue:** #47
- **Decision:** The fixture test (`scripts/tests/release-tags/run.sh`) has 47 cases under `fixtures/<case>/`: an `input` file (`tag=`, `version=`, `in_main=`, `published=`) and either `expected` (the exact output; 21 cases) or `error` (text the message must contain, exit code 1, empty stdout; 26 cases), plus 13 further checks (case count at least 47, ten usage errors, inputs as a workflow passes them, and the repository's own `VERSION` being acceptable). It runs as step `release_tags_test` in the `guards` job. No release workflow was written, no tag pushed, and no release created: the workflow that calls the script belongs to the release stories.
  **Why:** The must-have is that the maintainer can read, from a test, which tags any release would publish; the `expected` files are that. The key link (a workflow step that runs the script) cannot exist before the release workflow does.
  **Issue:** #47
- **Decision:** Fixture-bite proof, each mutation made to the script locally, run, and restored (60 passed afterwards): the minor-scope test removed (6 FAIL), the `VERSION` check removed (5), the main check removed (3), build metadata allowed (2), an already published exact version still pushed (5), published pre-releases counted as higher (6), "equal" treated as superseded (3), upper-case allowed (2). The end-to-end suite (`npm test` in `e2e/`) was not run locally; its lint, typecheck, and format check were, and the `e2e` job on #157 runs the suite. Nothing under `src/`, `web/`, `extension/`, or `e2e/` changed.
  **Why:** A fixture test that cannot fail proves nothing about the rules.
  **Issue:** #47
- **Decision:** The `main-pr-required` ruleset (id 24414108) was updated in place on 2026-10-04 while M1 was still on its branch, not after the story's changes merged: a `required_status_checks` rule naming the `ci` context from the GitHub Actions app (integration 15368), "up to date" off, was added beside the unchanged `pull_request` rule (0 approvals, all three merge methods). Bypass actors stay empty, so the owner cannot bypass. It was applied with `scripts/apply-rulesets.sh` from this branch and read back with `--check` and `rules/branches/main`. To undo: delete the `required_status_checks` rule from `.github/rulesets/main-pr-required.json` and run the script.
  **Why:** The orchestrator's instruction for this story was to change the live setting now; the planner's order (merge first, then apply) assumed the story had its own pull request. The risk of applying early is a gate nobody can pass: `main` has no `ci` workflow until #157 merges, but #157 carries the workflow itself, so its `ci` check reports and it stays mergeable (confirmed `CLEAN` after the change).
  **Issue:** #45
- **Decision:** `scripts/apply-rulesets.sh` is POSIX shell over `gh` and `jq`. It matches rulesets by name, updates with PUT or creates with POST, compares GitHub's answer with the file after every write, has a `--check` mode that changes nothing, validates every definition before sending anything, refuses a definition that carries server fields, and never deletes a ruleset that has no file. It is tested by `scripts/tests/apply-rulesets/run.sh` (42 checks, step `apply_rulesets_test` in the `guards` job) against a stand-in `gh`; the same test asserts that the committed definition requires a pull request with 0 approvals and the `ci` check from integration 15368 with no bypass.
  **Why:** The issue asks for a script that applies the committed definitions. Comparing the answer catches a field GitHub silently drops; `--check` gives a way to see drift made in the web UI. Deleting unlisted rulesets would be a destructive default nobody asked for. The test keeps the definition from losing the gate in a later edit.
  **Issue:** #45
- **Decision:** The committed definition keeps every parameter GitHub returned for the existing `pull_request` rule (including `require_extra_approval_for_unattributed_changes: true` and `required_reviewers: []`) and states `do_not_enforce_on_create: false` explicitly.
  **Why:** The file is sent back as it is and compared field for field with the live ruleset, so it has to say what the server says; leaving a parameter out would show as drift or reset it to a default.
  **Issue:** #45
- **Decision:** `.github/dependabot.yml` has five entries: `github-actions`, `nuget`, `dotnet-sdk` (for `global.json`), one `npm` entry with `directories` `/web`, `/extension`, `/e2e`, and one `docker` entry with `directories` `/` and `/src/n8Tracks.Gateway`. Each is weekly with no day set, has `open-pull-requests-limit: 5` written out (it is also the default), `commit-message.prefix: chore(deps)` with no separate development prefix, and one group limited to `update-types: [minor, patch]`, so a major update matches no group and gets its own pull request. Options were checked against GitHub's Dependabot options reference on 2026-10-04.
  **Why:** These are the planner's decisions. Without `prefix-development`, development dependencies take the same `chore(deps)` prefix (the open npm pull requests are titled `chore(deps-dev)` today). The new file takes effect only once it is on `main`.
  **Issue:** #45
- **Decision:** Secret scanning, push protection, Dependabot alerts, and Dependabot security updates were all found enabled and were not changed. No Dependabot pull request was rebased, merged, closed, or commented on in this story: eight are open (#1, #2, #3, #158 to #162), and rebasing them is left until M1 is on `main`.
  **Why:** The settings already met the criterion. The `ci` workflow is not on `main` yet, so a Dependabot branch rebased now would still have no `ci` run and could not pass the gate; an `@dependabot rebase` only becomes useful after the merge. The orchestrator's instruction was not to merge or close them.
  **Issue:** #45
- **Decision:** Block proof on throwaway pull request #170 (ready for review, not a draft, so that the only thing in the way was the rule; made in a temporary worktree with one release-tags fixture deliberately wrong; closed without merging, branch deleted), run 37193574808. `gh pr merge 170 --squash` as the repository owner was refused twice with "Pull request nathanpond/n8Tracks#170 is not mergeable: the base branch policy prohibits the merge.": once while `ci` was still running and once after `guards` and `ci` went red. The REST merge call gave the reason: `Repository rule violations found`, `Required status check "ci" is failing.` (HTTP 405). Before each attempt the pull request's merge state was read and was `BLOCKED`; `--admin` and `--auto` were never used. Complement, as far as this story may go: #157 at 3b3731c has `ci` green (run 37193565541), no approvals, and merge state `CLEAN`; the merge itself is the orchestrator's. The script's test bites too: with the script made to always create, to skip comparing GitHub's answer, or to leave bypass actors out of the comparison, and with the committed definition stripped of the `ci` rule or given a bypass actor, `run.sh` reported 5, 2, 1, 2, and 1 FAIL lines; restored, 42 passed.
  **Why:** The acceptance criterion is that a red or running pull request cannot be merged by the owner through the normal merge path; the test plan asks for the refusal text. A draft would have been refused for being a draft, which proves nothing about the rule. Nothing was merged to `main` by this story, so "a green pull request merges without approval" is shown by state, not by a merge.
  **Issue:** #45
- **Decision:** `.github/workflows/publish-edge.yml` has two jobs: `gate` (`uses: ./.github/workflows/ci.yml`) and `publish` (`needs: gate`), the only job with `packages: write`. `publish` builds the extension zip first (before the registry login), builds each image once for both platforms and pushes it by digest with no tag (`outputs: type=image,push-by-digest=true,name-canonical=true,push=true`), asks GitHub whether its commit is still the head of `main`, and then runs `scripts/publish-tags.sh` once. The concurrency group (`publish-edge-<ref>`, `cancel-in-progress: false`) is on the `publish` job only. Nothing in this story was published, and the workflow has not run: it can only start once it is on `main`.
  **Why:** These are the planner's decisions. One multi-platform build per image, pushed by digest, gives the same result as per-platform builds merged afterwards (one index digest, untagged until the final step) with half the steps; a local run against a throwaway registry showed the index is pushed untagged with both platforms and both attestations. Building the zip before the login keeps `npm ci` away from a machine holding registry credentials.
  **Issue:** #46
- **Decision:** Names come from a new tested script, `scripts/edge-version.sh --version "$(cat VERSION)" --sha <full commit ID>`, which prints `short_sha`, `version`, and `tag` (`edge-<short sha>`). It refuses an abbreviated commit ID and a `VERSION` that is not `X.Y.Z` or `X.Y.Z-<prerelease>`. Test: `scripts/tests/edge-version/run.sh` (23 checks, step `edge_version_test` in `guards`).
  **Why:** The conventions keep naming rules out of workflows, and the `.edge.<sha>` rule for a pre-release `VERSION` cannot be seen working until a pre-release exists unless a test shows it.
  **Issue:** #46
- **Decision:** Both Dockerfiles now stamp the version with `-p:InformationalVersion="$version"` instead of `-p:Version="$version"`. `/health` and the page report the same string as before; the assembly and file versions stay the plain `VERSION`.
  **Why:** A bug found while testing this story: `-p:Version` must be a valid NuGet version, and `0.1.0-edge.0123456` (a short sha that is all digits with a leading zero, about 1 commit in 270) is not, so the image build failed with `NETSDK1018` and that commit could never be published. Reproduced with a real two-platform build, fixed, and rebuilt: both images report `0.1.0-edge.0123456` from `/health`. The edge-version test asserts both Dockerfiles keep the informational-version stamp.
  **Issue:** #46
- **Decision:** `scripts/publish-tags.sh --image REPO@DIGEST (once per image) --fixed "<tags>" --floating "<tags>"`. It first reads where each floating tag points (stopping before any change if a tag cannot be read for a reason other than "not found"), creates the fixed tags on every image, then moves the floating tags one at a time and reads each back. On any failure it puts every floating tag it touched, including the one that failed, back to its previous digest, or removes it if there was none, and exits 1; fixed tags are never rolled back. If a tag cannot be put back it says so and prints the command to do it by hand. A superseded run passes an empty `--floating`.
  **Why:** The issue's all-or-nothing rule for `edge`, with `edge-<sha>` left in place. Reading the tag back catches a move the registry accepted but did not apply; noting a tag as touched before the attempt covers a move that happened although its answer was lost. Treating only "not found" as absent keeps a network or login error from being read as "first run", which would make a failed run delete a tag it should restore.
  **Issue:** #46
- **Decision:** Removing a tag on GHCR is done in two steps: the tag is moved to a placeholder index (the same image index with one extra annotation, made with `docker buildx imagetools create --annotation`, so it has a digest of its own), and that placeholder version is deleted through GitHub's packages API with `gh` (`GH_TOKEN`, `packages: write`). Removal is implemented for `ghcr.io` only; elsewhere the script reports that it cannot remove the tag. This path has been exercised only against the stand-in registry, never against GHCR.
  **Why:** A registry has no "untag", GHCR does not accept manifest deletion through the registry API, and deleting the image's own package version would also delete its `edge-<sha>` tag (and GitHub refuses to delete the last tagged version of a package, which a first run's only version is). The placeholder carries only `edge`, so deleting it removes exactly that tag. It can be seen for real only when a first publish fails after tagging one image.
  **Issue:** #46
- **Decision:** The tag script is tested by `scripts/tests/publish-tags/run.sh` (86 checks, step `publish_tags_test` in `guards`) against stand-ins for `docker` and `gh` in `fake-bin/`, which keep tags and manifests as files, can be told to fail, fail after taking effect, or silently do nothing for one call, and refuse to delete the last tagged version as GitHub does. Bite proof with mutated copies of the script (`PUBLISH_TAGS_SCRIPT=<copy>`): without the restore logic 15 checks fail (among them "the application's edge is restored to its previous digest" and, on a first run, "the application's edge is removed"); without removal 6; without reading tags back 3; any read error taken as "absent" 2; a tag noted as touched only after it moved 3. The real script: 86 passed. It was also run for real against a throwaway local registry with two-platform images carrying provenance and an SBOM: a publish, a second publish whose second image failed (the first image's `edge` went back to its previous digest), and a third that succeeded.
  **Why:** The issue's test plan asks for the failure case, the first-run variant, and a run against a script without the restore logic.
  **Issue:** #46
- **Decision:** The SBOM generator is pinned (`sbom: generator=docker/buildkit-syft-scanner:1.12.0`) instead of `sbom: true`; provenance is `mode=max`. OCI labels are `source`, `revision`, `version` (the edge version, replacing the base image's own `24.04`), and `licenses=Apache-2.0`. The publish builds read the gate's layer cache (`scope=app`, `scope=gateway`) and do not write it. The extension zip is uploaded as `extension-zip-edge` (14 days) only after tagging succeeded.
  **Why:** `sbom: true` pulls the scanner at a floating tag, and this milestone pins everything fetched at run time. Not writing the cache keeps the stamped publish layer out of the scopes pull requests read. A zip from a run that published no images would name a build nobody can pull.
  **Issue:** #46
- **Decision:** A manual run from a ref other than `main` runs the gate, builds the extension and both images (to the build cache only), and skips the login, both pushes, the tagging, and the upload; its summary says nothing was published. The head-of-`main` check uses `gh api repos/<repo>/git/ref/heads/main`; if it cannot be answered the step fails before any tag exists. A re-run of an old run therefore also leaves `edge` alone.
  **Why:** The planner's decision for other refs; failing closed on the head check is the only answer that cannot move `edge` backwards.
  **Issue:** #46
- **Decision:** Left for after the milestone merges, with the acceptance boxes unticked: the first real run, the two-platform inspection of both images, the anonymous pull, the `/health` version of the published images, the summary, the artifact on a real run, and making both packages public. GitHub has no REST endpoint for changing a package's visibility, so if the packages are created private the owner changes each one once in its package settings (Danger Zone, Change visibility), and the issue gets the `needs-owner-action` label until then. The label was not added now: until the first run there is no package to change.
  **Why:** The workflow triggers only on `main`; the orchestrator's instruction was to publish nothing from the branch.
  **Issue:** #46
- **Decision:** `.github/workflows/release.yml` runs on push of a `v*` tag only and has three jobs: `plan` (looks up whether the tagged commit is in `main` and which tags the application image already has, then runs `scripts/release-tags.sh`; a refusal fails the job and is written to the summary), `gate` (`needs: plan`, `uses: ./.github/workflows/ci.yml`), and `publish` (`needs: [plan, gate]`, the only job with `packages: write` and `contents: write`; `plan` has `packages: read`). The concurrency group `release` with `cancel-in-progress: false` is on the workflow. `publish` builds the zip before the registry login, pushes each image by digest, tags with `scripts/publish-tags.sh`, and runs `scripts/release-github.sh` last. The build steps, pins, labels, provenance (`mode=max`), SBOM generator, and cache scopes are those of `publish-edge.yml`. Nothing was tagged, released, or published by this story, and the workflow has not run: it starts only on a tag.
  **Why:** These are the planner's decisions. There is no manual trigger because a failed run is completed by re-running it, which keeps the tag as the only way in.
  **Issue:** #48
- **Decision:** `scripts/publish-tags.sh` (from #46) is extended instead of copied: `--once "<tags>"` for tags that are created once and never moved, a read-only `--digest REPOSITORY:TAG` mode, and a floating tag that already points at the wanted digest is now left alone (reported as `kept` instead of `tagged`). A once-only tag that is absent is created; one that points at the digest given is left with no write; one that points anywhere else makes the script stop before anything changed. The release workflow asks `--digest` for the exact version of each image before building: an image that has it is not built again, and its existing digest is what the floating tags are completed from. The workflow does not use `push_exact` from `release-tags.sh` for this.
  **Why:** `push_exact` is worked out from the application image's tags alone, and a run that failed between the two images leaves the gateway without the tag the application has; only a per-image read completes that. Skipping writes that would change nothing is what makes a re-run of a finished release push nothing. The edge workflow is unaffected except that re-publishing an identical digest no longer rewrites `edge`.
  **Issue:** #48
- **Decision:** On a failure after some floating tags moved, the floating tags are put back (the behaviour `publish-tags.sh` already had for `edge`), although the issue says such a failure "is not rolled back; the re-run finishes it". The exact version tags stay, and the re-run still finishes the release.
  **Why:** Reusing the one tagging script was the instruction, and its rollback leaves both images agreeing on `latest` and `X.Y` between the failed run and the re-run, which is at least as safe as leaving them half-moved. A separate no-rollback path would have been a second copy of the tagging logic.
  **Issue:** #48
- **Decision:** New script `scripts/published-tags.sh ghcr.io/OWNER/NAME` lists an image's tags through GitHub's packages API (`gh`). A 404 is read as "nothing published"; any other failure is exit 1. Its checks are in `scripts/tests/publish-tags/run.sh` (the stand-in `gh` now answers 404 for a package nothing was pushed to and can be told to fail).
  **Why:** `docker` cannot list tags. GitHub answers 404 both for a missing package and for one the token may not see; reading both as empty is safe because a token that cannot see a package cannot push to it either, so the run fails at its first push, and an existing exact version is protected by the registry read in `--once` whatever the list says.
  **Issue:** #48
- **Decision:** New script `scripts/release-github.sh --tag --prerelease --latest --zip`. No release: one `gh release create --verify-tag --generate-notes --latest=<true|false>` with both files (the title is the tag; `--prerelease` and `--notes-start-tag` as needed). A release exists: nothing about it is edited and no file is replaced (never `--clobber`); a missing zip or checksum is attached; a missing checksum is computed from the zip downloaded from the release, not from the zip this run built; a zip is not attached beside an existing checksum it does not match (the run fails and names the command to delete the checksum). A draft left by a killed run is completed and published with the given flags, keeping its notes. Only gh's exact answer `release not found` counts as "no release". The checksum file is one line, `<sha256>  <name>`.
  **Why:** The acceptance criteria ask that a re-run adds only what is absent and leaves an existing release's notes untouched. The extension zip is not known to be byte-for-byte reproducible, so a checksum must describe the zip people can download. `--latest` is always passed explicitly because GitHub otherwise decides "latest" by date and version, which is not the rule in `release-tags.sh`.
  **Issue:** #48
- **Decision:** The tag the generated notes start from is the next lower version among the version tags contained in the tagged commit (`git tag --merged`, sorted with `versionsort.suffix=-`): for a stable release the next lower stable tag, for a pre-release the next lower tag of any kind, and none (notes from the beginning) when there is no such tag.
  **Why:** The planner's rule, made precise. Ordering by version and not by date gives a patch on an older minor (`v1.4.3` tagged after `v1.5.0-rc.1`) notes since `v1.4.2`; limiting to contained tags keeps a tag on another line of history out.
  **Issue:** #48
- **Decision:** `Directory.Build.targets` now accepts a `VERSION` of `major.minor.patch` with an optional lower-case pre-release suffix (`0.1.0-rc.1`); it accepted only `major.minor.patch`. `ProductVersionTests` asserts the new shape, that the build guard uses the same pattern, and accepted and refused examples; the edge-version test's check of the repository's own `VERSION` now expects `.edge.<sha>` when it holds a pre-release.
  **Why:** A bug found in this story, and a blocker for its Demo: with `VERSION` set to `0.1.0-rc.1`, as the Demo and `release-tags.sh` require for a pre-release tag, `dotnet build` failed in every project and `scripts/tests/edge-version/run.sh` failed one check, so the `VERSION` bump pull request could never have passed `ci`. With the fix and `VERSION` temporarily `0.1.0-rc.1` the whole gate was run locally: build 0 warnings, 575 .NET tests, extension 69, web 46, every script test, both images built, the container smoke script (95 checks; both `/health` endpoints report `0.1.0-rc.1`), and the end-to-end suite (14). The extension zip was `n8tracks-extension-0.1.0-rc.1.zip` with `version` `0.1.0` and `version_name` `0.1.0-rc.1`. `VERSION` was restored to `0.1.0` afterwards; this story does not bump it.
  **Issue:** #48
- **Decision:** Tests: `scripts/tests/publish-tags/run.sh` grew from 86 to 142 checks (release, re-run, interrupted release, a published version that would be overwritten, the read mode, the tag list); `scripts/tests/release-github/run.sh` is new (82 checks, stand-in `gh` plus a throwaway git repository with real tags; step `release_github_test` in `guards`). Bite proof with mutated copies outside the repository (`PUBLISH_TAGS_SCRIPT`, `PUBLISHED_TAGS_SCRIPT`, `RELEASE_GITHUB_SCRIPT`): a once-only tag overwritten instead of refused, 6 checks fail; a once-only tag rewritten when already in place, 4; a floating tag rewritten when already in place, 2; any listing error read as "nothing published", 2; the error body printed as tags, 2; the checksum taken from this run's zip, 1; any failure to read the release taken as "no release", 2; `--latest` left to GitHub, 4; notes of a stable release started from a pre-release, 2; a zip attached beside a checksum of another build, 4; an existing release edited, 4. The real scripts: 142 and 82 passed. The publish job's own shell (parsing the `--digest` answer, splitting the floating tags) was rehearsed against the stand-in registry with one image already published.
  **Why:** The test plan puts the mismatch, bad-format, and not-in-`main` cases on the release-tag fixtures (#47) and everything observable only on a real tag after the merge; what a re-run may and may not touch is the part a fixture test can prove before any tag exists.
  **Issue:** #48
- **Decision:** `actionlint` was not installed on this machine, so the workflows were linted with its container image at the release looked up now (`rhysd/actionlint:1.7.12`, which carries `shellcheck`); `shellcheck` was also run on every script touched. Both "GitHub Actions lessons" greps print nothing.
  **Why:** The brief says the tools are available locally; only `shellcheck` was.
  **Issue:** #48
- **Decision:** Left for the orchestrator after the milestone merges, with the acceptance boxes unticked: the `VERSION` bump to `0.1.0-rc.1` (with `extension/package.json` and its lockfile), the tag `v0.1.0-rc.1`, and everything observed on that run (both platforms of both images, attestations, the pre-release with both files, the checksum, `/health`, no `latest`, `0.1`, or `0` tag, the summary, and the idempotent re-run). Two things about GHCR are still unproven because nothing has been published: what `docker buildx imagetools inspect` answers, with the workflow's token, for a tag of a package that does not exist yet (`publish-tags.sh` treats only `ERROR: ...: not found` as absent; a tag missing from an existing public package does answer that, checked against another project's image), and whether the packages are created private. The first edge run on `main` meets both before any tag is pushed.
  **Why:** The workflow triggers only on a tag, and the orchestrator's instruction was to push no tag, create no release, and publish nothing from the branch.
  **Issue:** #48
- **Decision:** The two tag rulesets are committed as `.github/rulesets/release-tags-create.json` (`creation` on `refs/tags/v*`, bypass list: the repository admin role, `actor_id` 5, `always`) and `.github/rulesets/release-tags-immutable.json` (`update` and `deletion` on `refs/tags/v*`, empty bypass list), both `active`. They were not applied in this story and no tag was pushed; the live repository was only read (`scripts/apply-rulesets.sh --check` reports both as "not on nathanpond/n8Tracks" and `main-pr-required` as matching). The orchestrator applies them after `v0.1.0-rc.1` is published.
  **Why:** The planner's split (one ruleset has one bypass list). The issue says the story is blocked when the pre-release tag does not exist; the orchestrator's instruction replaced that with "build and test on the branch, apply after the tag", because once the rulesets are on, a release tag that the first real run refuses could not be deleted. The rule shapes (`{"type": "update"}` with no parameters for a tag target, admin as `RepositoryRole` 5) were checked against tag rulesets other public repositories exported from the API, since nothing could be sent to GitHub to see its answer.
  **Issue:** #49
- **Decision:** `scripts/apply-rulesets.sh` is unchanged: it already creates or updates every definition in the directory by name and compares the answer. Running it with no option applies all three rulesets, so it must not be run between the merge and the publication of the first tag. No `--only` option was added.
  **Why:** The issue says to reuse the script; a selective mode would be a second way to leave GitHub and the committed files apart, and the ordering is a one-time concern.
  **Issue:** #49
- **Decision:** `scripts/tests/apply-rulesets/run.sh` grew from 42 to 101 checks: the two committed definitions (name, target, pattern equal to the release workflow's only trigger, rules, bypass lists), that `docs/releasing.md` names both, applying all three to an empty stand-in repository, `--check` on the result, re-applying (three updates, no create), and nine drift cases that `--check` must report against the right ruleset (bypass actor added, `update` or `deletion` removed, disabled, evaluate-only, pattern changed, tag excluded, write role added, creation rule removed). The stand-in `gh` now gives each created ruleset its own id. Bite proof, each restored from a copy outside the repository: admin bypass on the immutable definition, 4 checks fail; `deletion` removed, 4; pattern changed, 4; no bypass on the create definition, 1; write role instead of admin, 1; create definition disabled, 1; release workflow trigger changed, 1; the script ignoring bypass actors, 7; the script ignoring `enforcement`, 6; the ruleset name missing from the docs, 1. Restored: 101 passed.
  **Why:** The refusal of a real force-move or delete can only be observed after the rulesets are applied; what the fixtures can prove beforehand is that the definitions say what the issue asks and that any later weakening on GitHub is reported.
  **Issue:** #49
- **Decision:** `docs/releasing.md` gained "Who can release" (admin role only, the tag is the approval, link to Roll back, the two rulesets with a placeholder `_not applied yet_` in the ID column, the commands that show them, and the three consequences the issue names). Its earlier advice to delete a refused tag and tag again is replaced: a refused tag stays and its version is spent, the fix is the next patch version (or the next pre-release number). README's "Branch rules" section is now "Branch and tag rules".
  **Why:** The earlier text contradicted the immutable ruleset. The IDs exist only once the rulesets are created, so that acceptance box stays unticked until the orchestrator fills them in.
  **Issue:** #49
- **Decision:** After the M1 merge the orchestrator ran the parts of #45, #46, #48, and #49 that need `main` or a tag: the first edge publish (run 37198243587), two manual edge runs (main: republished; another branch: published nothing), the `VERSION` bump to `0.1.0-rc.1` (#173), the tag `v0.1.0-rc.1` and its release run (37199318581, re-run once to prove nothing changes), and then the two tag rulesets (IDs 24454301 and 24454302).
  **Why:** Those criteria cannot be observed on a branch; the tag rulesets were applied last because release tags cannot be deleted once they are in force.
  **Issue:** #45, #46, #48, #49
- **Decision:** The GHCR packages needed no visibility change: both were public after the first publish.
  **Why:** #46 expected an owner action; `gh api users/nathanpond/packages/container/<name>` reported `public` and an anonymous pull worked.
  **Issue:** #46
- **Decision:** `v0.1.0-rc.1` is published and permanent. A force-move and a delete of the tag were both refused by the `release-tags-immutable` ruleset ("Cannot update this protected ref." / "Cannot delete this tag").
  **Why:** Approved by the maintainer at the M1 gate and again before execution.
  **Issue:** #48, #49
- **Decision:** The M0 pull request (#156) was merged while GitHub's CodeQL default-setup check was still running; it later passed on `main`.
  **Why:** No workflow file existed, so the skill's gate was the local suite. Logged because a check was pending at merge time.
  **Issue:** #25

## /n8-exec M0 (fix pass after verification) — 2026-10-04

- **Decision:** The telemetry switch is read from the hosts' environment snapshot: `IsTelemetryExportConfigured` and `AddServiceDefaults` in `n8Tracks.ServiceDefaults` take the environment variables (`IReadOnlyDictionary<string, string>`) instead of `IConfiguration`, and so does the app's log export (`AddN8TracksLogExport`), whose Serilog sink now reads its OTLP settings from the same snapshot. The name is matched exactly (`OTEL_EXPORTER_OTLP_ENDPOINT`).
  **Why:** The maintainer's requirement is that nothing is registered unless that environment variable itself is set. Host configuration also holds the command line, `ASPNETCORE_`/`DOTNET_` prefixed variables, and `appsettings.json`; reproduced before the fix with the issue's steps (7 OpenTelemetry assemblies loaded with the argument, 0 without).
  **Issue:** #175
- **Decision:** When export is on, `AddServiceDefaults` adds a last configuration source (`EnvironmentOnlyTelemetrySettings`) that answers every key starting with `OTEL_` from the environment snapshot and from nothing else (a key the environment lacks reads as absent, and writes to it are ignored). No other part of the host's configuration is touched.
  **Why:** Fixing the switch alone is not enough: the OpenTelemetry SDK reads its own settings (endpoint, protocol, headers, `OTEL_SDK_DISABLED`, batch settings) from the host's `IConfiguration`, where the command line outranks the environment, so with the variable set a `--OTEL_EXPORTER_OTLP_ENDPOINT=` argument sent the telemetry elsewhere and `OTEL_SDK_DISABLED` from any source stopped it. Setting each exporter option by hand was the alternative; it would miss settings the SDK adds later and changes how the SDK appends the signal path. Under the Aspire AppHost every `OTEL_` setting is a real environment variable, so that keeps working.
  **Issue:** #175
- **Decision:** The in-memory test hosts (`N8TracksApiFactory`, `GatewayFactory`) now turn export on by setting the real process environment variable while the host is built (`tests/Shared/TelemetryEnvironment.cs`: one host at a time under a lock, every `OTEL_` variable removed first and restored after), not with `UseSetting`. No test hook was added to production code. New tests run the built app and gateway as processes (`tests/Shared/ServiceProcess.cs`) for the command line, both prefixes, and `appsettings.json`.
  **Why:** `UseSetting` was the very route the bug is about, and the entry point reads the environment before any test service can replace it. Tests that pass their own snapshot or start a child process are unaffected by the temporary variable.
  **Issue:** #175
- **Decision:** In the "no other source redirects" tests the other source carries the endpoint, the three per-signal endpoints, and `OTEL_SDK_DISABLED=true`. Against the unfixed code all 18 new tests fail (9 per host; run in a copy outside the repository with the four source files at `7afea40`). Without the `OTEL_SDK_DISABLED` entry only the command-line row of the redirect test fails on the unfixed code: there the environment already outranks prefixed variables and `appsettings.json` for the endpoint, and `AddOtlpExporter` in SDK 1.19.1 does not read the per-signal endpoint keys.
  **Why:** Recorded so the redirect rows are not read as proving more than they do.
  **Issue:** #175
- **Decision:** Query-string redaction in traces cannot be turned off in either service. `EnvironmentOnlyTelemetrySettings` (the last configuration source, added by #175) answers every `OTEL_` key ending in `_DISABLE_URL_QUERY_REDACTION` as absent, whatever the environment says, and `AddServiceDefaults` sets the runtime switch `System.Net.Http.DisableUriRedaction` to off in code when export is on.
  **Why:** The instrumentation's `DisableUrlQueryRedaction` option is internal and is filled only from the host's configuration, so the source that already owns the `OTEL_` keys is the one place that covers both services and every way of setting the variable. The alternative, overriding the two variables to `false` in the AppHost only, would leave any other environment able to turn redaction off. Reproduced first: with both variables set, the app's and the gateway's spans carried the query values in clear.
  **Issue:** #176
- **Decision:** The runtime switch is part of the fix although the issue names only the two `OTEL_` variables. On .NET 10 the URL of an outgoing request is written into the span by the runtime, which masks the whole query (`?*`) unless `DOTNET_SYSTEM_NET_HTTP_DISABLEURIREDACTION` is set; `OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION` changed nothing for outgoing requests in the unfixed code. A switch set in code outranks the variable (checked with a throwaway console program outside the repository: variable set, `?token=SECRET`; variable set and switch pinned, `?*`).
  **Why:** The requirement is that no variable turns query redaction off; without the pin one still could, for outgoing requests. The README now says outgoing queries show as `*` and names all three variables as having no effect.
  **Issue:** #176
- **Decision:** The AppHost also removes every variable whose name contains `QUERY_REDACTION` from what Aspire gives the app and the gateway (an environment callback added after Aspire's own), instead of setting them to `false`.
  **Why:** The issue allows either; removal keeps the services' environment to what the README describes. A developer who exports one of these variables in their own shell still passes it on by inheritance, which is harmless because the services ignore it.
  **Issue:** #176
- **Decision:** Tests: `NoServiceIsHandedAVariableThatTurnsQueryRedactionOff` (AppHost model, both services); the app and the gateway each run as a real process with both variables set to `true` and a stub collector, asserting `?token=Redacted&password=Redacted` arrives and neither sentinel does on any OTLP path; and `AnOutgoingRequestsQueryValuesAreRedactedWhateverTheEnvironmentSays` on a host that has only the shared wiring. Against the unfixed code (a copy outside the repository with the two source files at `a45b3b1`) the first four fail. The outgoing test's sentinel assertions pass on the unfixed code, for the reason in the entry above; what bites there is its assertion that the wiring set the runtime switch (fails with the `SetSwitch` line removed). No automated test sets `DOTNET_SYSTEM_NET_HTTP_DISABLEURIREDACTION` for real: neither service sends a request with a query string, and the runtime reads the switch once per process, so an in-process test cannot turn it on safely beside other tests.
  **Why:** Recorded so the outgoing test is not read as proving more than it does.
  **Issue:** #176
- **Decision:** Filed #179 and changed nothing for it: Aspire also gives both services `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true`.
  **Why:** Outside this bug's scope and without effect today (no GenAI instrumentation exists), but it touches invariant 6 (prompts) once such a library is added.
  **Issue:** #176
- **Decision:** "`N8TRACKS_PORT` is the only listen source" is tested on the app and on the gateway as real processes (`ListenSourceTests`, `GatewayListenSourceTests`): one run per source (`ASPNETCORE_URLS`, `ASPNETCORE_HTTP_PORTS`, `ASPNETCORE_HTTPS_PORTS`, `DOTNET_URLS`, `--urls`, `urls` in `appsettings.json`) and one with all six at once, each naming its own port; the product port answers `/health` and nothing accepts a connection on any other. A further test asserts a start with the two port variables writes no warning. The gateway is included although the bug names only #26, because it has the same rule and the same code.
  **Why:** The process is where these sources are real; in-process they would have to be set for the whole test run. Proved in a copy outside the repository: with the host's addresses honoured beside the product port, 7 of 8 tests fail per service; with the three lines that clear the host's settings removed, only the no-warning test fails, because the endpoint set in code (`ListenAnyIP`) is what keeps the other ports closed.
  **Issue:** #177
- **Decision:** The no-warning assertion covers `ASPNETCORE_HTTP_PORTS` and `ASPNETCORE_HTTPS_PORTS` only. Filed #180 and changed nothing for it: a `urls` setting from any source is not listened on, but Kestrel still logs "Overriding address(es)", so the line in both `Program.cs` files that clears `urls` has no effect.
  **Why:** Found while writing the test. It is log noise, not the listen behaviour this bug covers, and the fix pass changes production code only where a test needs a seam.
  **Issue:** #177
- **Decision:** The "port not permitted" branch is tested at the unit level only (`ListenPortProbeTests` in both test projects: the exact text for `AccessDenied`, for "in use", and for any other error, plus the probe's result for a free and a taken port).
  **Why:** A bind refused for lack of permission cannot be produced on every machine: macOS lets any user bind a low port, and on Linux it depends on the user and on `net.ipv4.ip_unprivileged_port_start`. A test that runs only where it can would be a skipped test elsewhere. Proved by removing the `AccessDenied` arm in the copy: one test fails per service.
  **Issue:** #177
- **Decision:** "Nothing else reads the environment" is a source guard, `EnvironmentReadGuardTests` in `n8Tracks.Architecture.Tests`, over every `.cs` file of every project under `src/` except the AppHost (found on disk, so a new project is covered without listing it). It allows the process-environment APIs on exactly two lines (the `ProcessEnvironment.cs` readers), allows `ProcessEnvironment.Read()` to be called from exactly the two `Main` methods, and forbids reading a setting from the host's `IConfiguration` (assignments to it are allowed).
  **Why:** The host's configuration holds the `ASPNETCORE_`/`DOTNET_` variables, the command line, and `appsettings.json`, so reading a setting from it is the same drift by another route (it is how #175 happened). The AppHost is left out because it is the developer's orchestrator, is in no image, and reads the developer's environment by design. Out of the guard's reach: what the framework reads for itself (the environment name behind `IsDevelopment()`), and a read through reflection. Proved in the copy with a `GetEnvironmentVariable` call, a `builder.Configuration["…"]` read, and a second `ProcessEnvironment.Read()`: three tests fail.
  **Issue:** #177
- **Decision:** One production change, as a seam for the test: the AppHost's missing-dependencies check looks for `node_modules/vite` under the frontend resource's own working directory (`resource.WorkingDirectory`) instead of the `web/` path captured beside it. By default the two are the same folder, which a test pins.
  **Why:** The check could not be reached from a test without moving the real `web/node_modules`. `MissingFrontendDependenciesTests` points the resource at an empty temporary folder (`WithWorkingDirectory`) and then (a) raises the before-start event and reads the refusal and its message, with a complement that has the dependencies in place, and (b) starts the whole model: the frontend reaches "FailedToStart" with the `npm install` line in its own log while the app and the gateway become healthy and answer `/health`. Proved in the copy with the check removed: (a) and (b) fail.
  **Issue:** #177

## /n8-exec M0 (second fix pass after verification) — 2026-10-04


- **Decision:** #181 and #182 are fixed as one class, not as two keys: both hosts are built with `WebApplication.CreateEmptyBuilder`, which adds no configuration source. Neither service now reads a command-line argument, an `ASPNETCORE_`/`DOTNET_`-prefixed or unprefixed environment variable through the configuration system, or an `appsettings*.json`; the host's configuration holds exactly `applicationName`, `environment`, and `contentRoot`, all set in code (plus the `OTEL_` keys the shared wiring answers from the environment snapshot when export is on). What the default builder supplied is added in code: Kestrel through `UseKestrelCore()` (the server without the loader that binds the `Kestrel` section), `AddRouting()`, the application name, and the content root.
  **Why:** Verification found "another configuration source can do X" three times (#175, #181, #182), and a one-key defence each time. With no source there is no `Kestrel:Endpoints`, `Logging:`, `urls`, `AllowedHosts`, or future framework key to defend against. The targeted alternative (clear the `Kestrel` section, pin the OpenTelemetry provider's filter) would have left every other key open; on the unfixed code `AllowedHosts`, a Kestrel limit, `contentRoot`, `--environment`, and `Logging:LogLevel` (which changed the gateway's standard output too, beyond what #182 reports) all took effect.
  **Issue:** #181, #182
- **Decision:** Simplified as redundant: the three lines in each `Program.cs` that blanked `urls`, `http_ports`, and `https_ports`, and `src/n8Tracks.Api/appsettings.json` and `appsettings.Development.json` (deleted; they held only `AllowedHosts: *`, which is the behaviour without host filtering). Kept: `EnvironmentOnlyTelemetrySettings`, which is now how the OpenTelemetry SDK gets its `OTEL_` settings at all (it reads the host's configuration, which is otherwise empty) and still hides the query-redaction switches; the Dockerfiles' `ENV` lines (harmless, and `ASPNETCORE_ENVIRONMENT=Production` is still read by the app). All earlier tests stay; `AnEndpointInHostConfigurationRegistersNothing` is renamed (`AnEndpointHandedToTheHostAsAnArgumentRegistersNothing`) and now asserts the key is absent from the host's configuration, where it used to assert it was present and ignored.
  **Why:** The task allows simplifying one-key defences that the class fix makes redundant, keeping the tests that prove the behaviour. This also removes the cause of #180 (Kestrel's "Overriding address(es)" warning for a `urls` setting): the listen-source tests now assert no warning with every source set at once.
  **Issue:** #181, #182
- **Decision:** The application's environment name comes from the variable `ASPNETCORE_ENVIRONMENT` in its environment snapshot (`EnvironmentOptionsLoader.HostEnvironmentName`; `Production` when unset or blank) and from nothing else: not `--environment`, not `DOTNET_ENVIRONMENT`. It is documented in the README as the one variable of .NET's own that the app honours, for development. The gateway's environment is fixed at `Production`; it has no behaviour that depends on it.
  **Why:** The environment decides one thing, whether the OpenAPI document is mapped. The launch profiles (the app's own, and the AppHost's, whose `ASPNETCORE_ENVIRONMENT=Development` the services inherit as child processes; the AppHost's model itself sets no environment name, checked by reading the model), the Dockerfile, and a test (`TheOpenApiDocumentDeclaresBothAnswersAndTheStatusNames`) rely on it. Dropping the environment would have removed or always-exposed the document, which is a product decision; reading it explicitly from the snapshot keeps today's behaviour by a documented means. `DOTNET_ENVIRONMENT` is dropped because one name is enough: the only place the repository sets it is the AppHost's launch profile, which sets both. Aspire also hands every project `LOGGING__CONSOLE__FORMATTERNAME=simple`; like every other framework key it now has no effect on the services.
  **Issue:** #181, #182
- **Decision:** The application's content root is the working directory from the environment snapshot (so the web root stays `wwwroot` under the working directory: `/app/wwwroot` in the image, `src/n8Tracks.Api/wwwroot` in a local run); the gateway's is its binary's directory, since it reads nothing from it. `--contentRoot` and `--webroot` are no longer honoured. `--healthcheck` is still found in the arguments by `HealthCheckCommand.IsRequested`, and is the only argument with a meaning.
  **Why:** The working directory was already the content root by default and is already an explicit input (relative paths resolve against it). The binary's directory would have broken local runs, where `wwwroot` is not copied to the build output. The startup-failure test that used `--contentRoot` now uses a snapshot whose working directory does not exist.
  **Issue:** #181, #182
- **Decision:** Both hosts validate the container (`ValidateScopes` and `ValidateOnBuild`) in every environment.
  **Why:** The default builder did this in `Development` only, which is what the in-memory test hosts ran as. An empty builder does not do it at all, and the test host can no longer name the environment by argument, so keeping it "in Development" would have depended on the environment name again. Always on, tests and production check the same registrations; nothing is constructed by the check.
  **Issue:** #181, #182
- **Decision:** Test hosts: `WebApplicationFactory` hands its environment name, content root, and `UseSetting` values to the entry point as arguments, which are now ignored. `N8TracksApiFactory` gets `EnvironmentName` (default `Development`, as before) and puts it in the process environment while the host is built, through the existing `TelemetryEnvironment.BuildHost` lock; `HostSettings` on both factories still arrive as arguments and are described as such.
  **Why:** The entry point reads the environment name where it reads the telemetry switch, the process environment, so the test host has to set it the same way. Checked: `FrameworkSettingsTests.TheHostsConfigurationHoldsOnlyWhatTheAppSet` builds a host with `EnvironmentName = "Staging"` and finds that value, not the factory's `Development` argument.
  **Issue:** #181, #182
- **Decision:** Tests added. Listen sources (both services): five `Kestrel:Endpoints` rows (`ASPNETCORE_`, `DOTNET_`, and unprefixed variable, argument, `appsettings.json`), also part of the all-sources-at-once run. Logging (both services, as processes with the stub collector): `Logging:OpenTelemetry:LogLevel:*`, `Logging:Console:LogLevel:Default`, and `Logging:LogLevel:*` at `Trace` and at `None`, by argument, variable, and file; a query sentinel reaches no OTLP path and no output line, every line on standard output is the service's own and arrives as a log record, and no framework record is exported. General (both services): `AllowedHosts`, `Kestrel:Limits:MaxRequestLineSize`, `contentRoot`, and `urls` from each of five sources leave `/health` at 200; the host's configuration holds only the three keys set in code; for the app, only `ASPNETCORE_ENVIRONMENT` names the environment. Source guard (`EnvironmentReadGuardTests`): the only host created under `src/` (AppHost aside) is the empty builder on one line of each `Program.cs`, and no code adds a file, command-line, or logging-configuration source, `UseKestrel`/`ConfigureKestrel`, or `Args =`.
  **Why:** Proved against the unfixed code in a copy outside the repository (the two `Program.cs` files and the two settings files at `364037f`): 37 tests fail, among them all five Kestrel rows and all five general rows for both services, both configuration-content tests, the guard, and all six gateway logging rows (`Trace`: the sentinel is found in the exported logs; `None`: the gateway's startup line never arrives). The six application logging rows pass on the unfixed code, as #182 says they should. The environment rows for a plain variable and for `appsettings.json` also pass there: a default host does not take its environment name from those either.
  **Issue:** #181, #182
- **Decision:** Not covered by the rule, and said so in the README: variables the .NET runtime reads for itself before any project code runs (garbage collector and diagnostics switches, startup hooks).
  **Why:** They are not configuration of the services and cannot be turned off from inside the process. The README's "only these variables configure the service" is otherwise strictly true now.
  **Issue:** #181, #182
- **Decision:** #183: every test port that is handed to something else now comes from one helper, `tests/Shared/TestPorts.cs` (linked into the Api, Gateway, and AppHost test projects), which replaces eleven `FreePort()` copies and the probe-then-listen loops in `StubOtlpCollector` and the health-check stub. A port comes from 20000 to 29999 (below the range any operating system assigns from, so nothing that binds port 0 or opens an outgoing connection is given one), is claimed with a lock file in the temp directory that the test process holds exclusively until it exits (so another test process cannot be given it), is handed out once per process, and is skipped if something already accepts connections on it.
  **Why:** The issue's options were: hand the listener over (not possible across a process boundary to a service that binds its own port), bind port 0 in the service and read the port back (the services refuse port 0 by design, and changing that is a product change this bug does not call for), or retry on a collision (cannot work for "nothing must listen on this port" assertions, where a collision looks like a product failure). A reserved, locked range needs none of them. Checked: three processes asking for 3000 ports each at the same time got 9000 distinct numbers.
  **Issue:** #183
- **Decision:** #183 had a second cause, found while proving the fix: with ports no other process could be given, `ListenPortProbeTests` still failed in 6 of 20 concurrent rounds, at the "free again after `Stop()`" assertion. A child process that another test is starting holds a copy of every open socket until it has exec'd, so a port a test binds and releases stays taken for a moment in a test process that also starts processes. So `TestPorts` checks a port by connecting, never by binding, and the probe test uses one never-bound port for "free" and a second one, held by its own listener, for "in use"; it no longer releases a port and asks again.
  **Why:** "Free again after release" cannot be made deterministic in a process that forks, short of serialising every process start against every socket in the test process. What the test is for (the probe says nothing for a free port and "in use" for a taken one) is fully kept.
  **Issue:** #183
- **Decision:** `StubOtlpCollector.DisposeAsync` calls `Close()` only (it was `Stop()`, wait, `Close()`).
  **Why:** On the managed `HttpListener` (Linux, macOS) `Close()` after `Stop()` looks the endpoint up again, finds it removed, and binds the port a second time only to release it; that bind throws "Address already in use" whenever anything else holds the port by then. Reproduced in a scratch program with the exact stack of #167; `Close()` alone does not rebind. With this and the reserved ports, both halves of #167's cause are gone; #167 is left open for the maintainer with the evidence.
  **Issue:** #183
- **Decision:** Left as it is, and reported: the application tests that run `Program.RunAsync` in the test process on a real port (`StartupTests`, `HealthCheckCommandTests`, `DatabaseStartupFailureTests`) go through the product's own probe (bind, release) and then Kestrel's bind. A child process forked by another test inside the probe's lifetime (tens of microseconds) and not yet exec'd when Kestrel binds would make that start fail.
  **Why:** It is not port selection, it was not observed in any run, and the only deterministic fix is a gate between every process start and every in-process start, which with xUnit's bounded thread pool can deadlock (blocked starters holding every thread while the holder waits for one). Filed as a `needs-triage` issue instead.
  **Issue:** #183
- **Decision:** #184: the guard no longer judges how the host's configuration is read. Any code line under `src/` (AppHost excepted, as before) that names it at all fails: the identifier `Configuration` (member, property pattern, or the `Microsoft.Extensions.Configuration` namespace; the projects' own `n8Tracks.*.Configuration` namespaces excepted), any `IConfiguration…` type, `ConfigurationManager`/`Binder`/`Builder`/`Provider`/`Root`/`Section`, `ConfigureAppConfiguration`/`ConfigureHostConfiguration`, and the methods `GetValue` (generic or not), `GetSection`, `GetRequiredSection`, `GetConnectionString`, `GetChildren`, `AsEnumerable`, `BindConfiguration`, `GetSetting`, `UseSetting`. The five lines in `n8Tracks.ServiceDefaults` that install `EnvironmentOnlyTelemetrySettings` are allow-listed exactly (path and text), and the list must stay exact.
  **Why:** Since #181/#182 no project code has a use for the host's configuration, so "no mention" is the simplest rule that holds: an alias, a field, or an injected `IConfiguration` is caught on the line that declares it, which a rule about read expressions cannot do. Proved in a copy outside the repository: the old guard passed both bypasses from the issue (67 of 67); the new one fails on each of them and on five variants (non-generic `GetValue(Type, …)`, a bare alias, `WebHost.GetSetting`, `GetRequiredService<IConfiguration>`, a constructor-injected `IConfiguration` in a new file), naming the file and the line, and passes again when the line is removed. Still a text rule: reflection and `dynamic` are out of its reach, as the class comment already says.
  **Issue:** #184

## /n8-exec M1 (fix pass after verification) — 2026-10-04


- **Decision:** #194: the guard was changed in structure, not by adding patterns for the listed bypasses. It now reads every tracked file whatever its folder is called; fails on a tracked text file that is not UTF-8, on a symbolic link, and on a submodule; treats `\r`, U+0085, U+2028, and U+2029 as line ends; decides that a file is an MSBuild file by its name or by its `<Project>` root, and reads it with an XML parser (`xml.parsers.expat`, standard library, with line numbers), so CDATA, entities, and attributes are what MSBuild sees. Setting names live in one table (suppressions, pinned, switches) used for element names, attribute names, property lists, values, command lines, and environment variables alike.
  **Why:** The issue's 42 bypasses fell into a few causes (folders skipped by name, bytes not decoded, XML read with regular expressions, a closed list of file kinds, lint scripts checked for one substring). Closing the causes also closed bypasses the issue did not list: of the 54 new fixture cases, 30 are for bypasses found while attacking the fix.
  **Issue:** #194
- **Decision:** What a rationale can still excuse is unchanged in kind: a direct, visible suppression (`<NoWarn>`, `<WarningsNotAsErrors>`, and now `<MSBuildWarningsAsMessages>`, `<MSBuildWarningsNotAsErrors>`, `<NuGetAuditSuppress>`, and analyzer exclusion options in `.editorconfig`). Everything that hides a suppression, moves where settings come from, or cannot be read is in the configuration half, where no comment helps: indirect property setting (`CreateProperty`, `Properties=`), `Features`, `CodeAnalysisRuleSet`, `GlobalAnalyzerConfigFiles`, `MSBuildTreatWarningsAsErrors`, `<Analyzer Remove>`, generated-code markers, unknown SDKs, unreadable imports.
  **Why:** Invariant 8 allows a suppression with a one-line rationale, and the existing `good/` fixtures for `<NoWarn>` must keep passing. The issue's "treat as a finding" is met for the suppression names by requiring the rationale and for the rest by failing outright.
  **Issue:** #194
- **Decision:** Common words are handled apart from distinctive names. `NoWarn`, `TreatWarningsAsErrors`, `CodeAnalysisRuleSet` and the like fail wherever they are written outside Markdown (any text file, any environment form). `Features`, `CodePage`, `WarningLevel`, and `Nullable` fail only where they are assigned (`Name=`, a key under `env:`/`environment:`/`args:`, a Dockerfile `ENV`/`ARG`, `-p:Name=` in any file, a property name in an MSBuild task).
  **Why:** A rule that fails on the word "features" in any YAML or source file would be switched off within a week. The header says which names are which.
  **Issue:** #194
- **Decision:** The expected scripts are a table in the script (`SCRIPT_PROJECTS`): `lint` is `eslint . --max-warnings 0` in all three projects; `typecheck` is `tsc -b` in `web/` and `extension/` and `tsc --noEmit` in `e2e/`, compared as whole strings. With `tsc --noEmit` a `tsconfig.json` that only points at references fails, because that command does not follow them. A `package.json` anywhere else with a `lint` script, an ESLint or TypeScript dependency, `workspaces`, `overrides`, or `resolutions` fails as an unknown project.
  **Why:** These are the three projects' scripts as they are today. A fourth project has to be added to the table on purpose, with its scripts.
  **Issue:** #194
- **Decision:** A JavaScript or TypeScript file outside the three projects that belongs to no `package.json` is not a finding, and neither is a source file whose extension no ESLint rule set matches. Both are listed under "Not covered"; the second is filed as #197.
  **Why:** The issue asks for unknown projects, defined by a `package.json`. Failing on loose files would have meant rewriting eight existing fixture cases and deciding which extensions each project lints, which is the lint configuration's job. Confirmed with the real ESLint in a copy: `web/src/probe.js` with `debugger` and `eval` passes `eslint . --max-warnings 0`.
  **Issue:** #194
- **Decision:** After a gate command (`dotnet build/test`, `npm run`, `eslint`, `tsc`, a `.sh` script, `docker build`, and so on), `||` may lead only to an exit or return that is not a literal 0, `false`, a block that exits so, or a shell function of the same file whose body exits so. In workflows also: no `continue-on-error` but `false`, no custom `shell:` line, no pipe without `shell: bash`, no `&&` after a gate command before the last line of a step, no `set +e`, no `!`, no `$(...)` that is not an assignment, no `trap` that exits 0.
  **Why:** A list of things that swallow a failure (`|| true`, `|| echo`) misses `|| ls`. The inverse list is short and was checked against every script in the repository: `scripts/smoke-docker.sh` uses `docker build ... || stop "..."`, and `stop` exits with a variable, which is why "not a literal 0" is the rule. `bash -e -c 'false && echo x; echo end'` exits 0, confirmed.
  **Issue:** #194
- **Decision:** The stricter guard found one real thing in the repository: the `guards` job's summary step re-ran the guard as `scripts/check-suppressions.sh 2>&1 || true`. The `suppressions` step now writes its output to `$RUNNER_TEMP/suppressions.log` (`shell: bash`, through `tee`) and the summary prints that file. No product code changed.
  **Why:** The guard was not weakened to let its own workflow through; a gate command with its failure ignored is exactly what the new rule is for, and the summary did not need a second run.
  **Issue:** #194
- **Decision:** The EF Core exemption is narrower. A file is exempt only if it is a `*.Designer.cs` or `*Snapshot.cs` under a `Migrations` folder, carries the header, and holds one partial class whose only member is `BuildModel` or `BuildTargetModel`; in it only `#nullable disable` and `#pragma warning disable 612, 618` go unexplained. Any other `<auto-generated` header, generated file name (`*.g.cs`, `*.Designer.cs`, `*.generated.cs`), `[GeneratedCode]`, or `generated_code = true` fails.
  **Why:** The old exemption skipped any file in a `Migrations` folder with the header, so a hand-written class there was exempt from everything. Confirmed by build: a file named `V.g.cs`, or one with `// <auto-generated/>`, compiles a CS8618 and a CA2200 violation cleanly under `-warnaserror`.
  **Issue:** #194
- **Decision:** Existing fixtures were adapted where the new rules made their input invalid for another reason, without changing what each case tests: `base/` (the `e2e` typecheck script is `tsc --noEmit`, the `extension` lint script uses a space, not `=`); `config-lint-script-missing` and `config-lint-max-warnings-weakened` (the same typecheck script); `config-command-line-package-script` (its `tools/package.json` had a `lint` script, which now makes it an unknown project; the line is a `format` script instead, same line numbers); `ef-core-migrations` (the exempt files have the shape EF Core writes, and the three files that carry the header outside the exemption are also reported at line 1). In `run.sh` the check "bin/, obj/, node_modules/, dist/ are not scanned" became its opposite, a case may now have both `good/` and `excused/`, and a case may carry a `setup` fragment (used for a submodule entry).
  **Why:** The issue requires scanning those folders and failing on generated-code headers, so two existing checks had to say something different. The count of checks went from 104 to 237 with none removed.
  **Issue:** #194
- **Decision:** Proof. Every new fixture case was run against the unfixed script kept outside the repository: all 54 `bad/` trees fail to be reported there, 48 of them with exit 0. In a git copy with a probe project, built with `dotnet build -warnaserror`: the control fails on CS8618 and CA2200; these build clean and pass the unfixed guard, and the fixed guard reports each: a file in `dist/`, UTF-16 source, `<Import Project="relax.xml">`, `<Features>`, `CreateProperty` setting `NoWarn`, `<NoWarn>` between CDATA sections, `// <auto-generated/>`, `generated_code = true`, `<GlobalAnalyzerConfigFiles>`, and (new) `Probe.csproj.user`, `V.g.cs`, `[GeneratedCode]`, an alias of `SuppressMessageAttribute` in C# and in `<Using Alias>`, an `<AssemblyAttribute>` suppression, `SuppressMessage`, a directive after U+2028, a directive after U+FEFF or U+001A, `<Compile Include="notes.txt">`. With real `eslint` and `npm`: a directive on the second line of a block comment, and `script-shell=/usr/bin/true` in `web/.npmrc` (`npm run lint` exits 0 on a file with an error).
  **Why:** The brief asks for each new fixture to be shown failing on the unfixed code, and the issue's bypasses to be reproduced first.
  **Issue:** #194
- **Decision:** Tried and found not to be bypasses, or already caught by something else: `<ImportDirectoryBuildProps>false` inside a csproj is read too late to matter (the build still fails), though it works as an environment variable (nullable warnings vanish), which the guard reports in workflows and Dockerfiles, and the element is refused anyway; `// eslint-disable` after a no-break space is honoured by ESLint, but this repository's `no-irregular-whitespace` reports the comment (the guard reports it too).
  **Why:** Recorded so the next attempt does not repeat them.
  **Issue:** #194
- **Decision:** What the guard cannot catch is stated in three places with the same content: the header of `scripts/check-suppressions.py` ("Not covered"), `docs/conventions.md`, and the `guard:` annotation of invariant 8 in `CLAUDE.md` (the invariant's own sentence is unchanged). In short: anything not in the repository or created during the build, what packages bring, names assembled at run time, whether CI runs the gate and over what, failure handling inside scripts beyond the listed forms, the meaning of an ESLint configuration, loose JavaScript, language features that avoid a warning, and the truth of a rationale.
  **Why:** The issue asks for it, and the old header's three-line "Not covered" understated the limits.
  **Issue:** #194
- **Decision:** The check "is this file's root element `<Project>`?" is a small one-pass function, not a regular expression.
  **Why:** The first push used a regular expression for it, and CodeQL reported three `py/redos` alerts on the pull request: a tracked file made of repeated comment openers could have made the guard run for a very long time. The function reads 200,000 such repetitions in 0.04 s.
  **Issue:** #194

## /n8-exec M1 (second fix pass: canaries) — 2026-10-04

- **Decision:** The canary check is one script, `scripts/check-canaries.sh [ROOT]`, with its sources under `scripts/canaries/`. It copies the tree (tracked files and untracked files git does not ignore, as they are on disk) to a temporary folder, does all its work there, and removes the folder in an `EXIT` trap (`INT`, `TERM`, and `HUP` exit through it). It takes no option: nothing narrows what it checks.
  **Why:** A copy needs no clean-up logic in the working tree, cannot leave a canary behind after an interruption, and exercises restore and `npm ci` as CI does. Copying the working tree rather than `HEAD` lets a developer check a change before committing it. An option that skipped a half would be one more thing for the scanner to forbid in workflows.
  **Issue:** #199
- **Decision:** .NET: every `*.csproj` in the copy outside `scripts/tests/` is canaried (found, not listed; that is wider than `src/` and `tests/`). One project at a time: the canary is placed, `dotnet build <project> --configuration Release` runs, the canary is removed. The check passes only when the build exits non-zero and its errors are exactly the canary's marked lines with their ids (CS8618 on line 8, CA2200 on line 19). A build that succeeds, reports either as a warning, reports only one, or reports any other error fails, naming the project. A `.vbproj` or `.fsproj`, or no project at all, fails.
  **Why:** With the canary in one project at a time the projects it references build clean, so each project is compiled once (plus one failing compile) and no separate baseline build is needed; a canary in every project at once would stop at the first. Release is what the `dotnet` job and the images build. No `-warnaserror` on the command line, so it is the project's own settings that must make the warnings errors (a build with the switch can only be stricter).
  **Issue:** #199
- **Decision:** JavaScript: `web`, `extension`, and `e2e` each get `npm ci` in the copy, then up to three runs, each with only its own canary files: `lint-warning` (web: `react-hooks/exhaustive-deps`; e2e: `playwright/no-wait-for-timeout`; the extension has no rule at `warn`), `lint-error` (`no-debugger`, `@typescript-eslint/no-floating-promises`, `no-explicit-any`, `array-type`, and `jsx-a11y/alt-text` in web), and `typecheck` (thirteen lines, one per strictness setting the tsconfigs turn on: TS7006, TS18048, TS2564, TS18046, TS2683, TS2345, TS2322 three times, TS6133 twice, TS7029, TS4114). Expectations are markers in the canary sources (`// canary: <id>` on the line the tool reports), not a list in the script. Each run must exit non-zero and report every marker on its line; other errors are allowed there.
  **Why:** A warning-only run is the only way to see `--max-warnings 0` work: with an error in the same run the exit code proves nothing. One canary line per strict flag is what catches a single flag turned off (N18, N19). Markers keep the expectation next to the code that produces it. `node_modules` is installed fresh rather than borrowed from the working tree, so a stale or doctored install is not what gets tested and `tsc -b` cannot write its build info into the real tree.
  **Issue:** #199
- **Decision:** A consequence, documented in `docs/conventions.md`: the canaries' diagnostics and rules cannot be switched off for a whole project even with a rationale (a `<NoWarn>CS8618</NoWarn>` with a comment passes the scanner and fails the canary). Suppressions stay scoped to the code that needs them.
  **Why:** An effect check cannot tell an explained project-wide switch-off from an unexplained one, and invariant 8 is better served by the stricter reading. Shown in the table below (F05, F09, F13).
  **Issue:** #199
- **Decision:** The canary script is tested by `scripts/tests/check-canaries/run.sh` (53 checks, 20 cases): a base tree with one real C# project and three JavaScript folders whose `lint` and `typecheck` scripts are stand-ins that report the canaries' marked lines in ESLint's and tsc's formats. The .NET half uses the real SDK; nothing is downloaded. Each case must fail with exactly the expected FAIL lines and leave its tree unchanged and no temporary copy behind; an interrupted run (SIGTERM) is checked the same way. The scanner now leaves `scripts/tests/check-canaries/` unscanned, as it does its own fixtures, and forbids naming that folder on a command line.
  **Why:** Real ESLint and TypeScript in the fixture would need a second `npm ci` with network access in the `guards` job for no more coverage of the script's own logic; the real tools are covered by the run against the repository and by the bypass table. The fixture trees are deliberately broken (a nested `Directory.Build.props`, `<NoWarn>` without a rationale), so the scanner cannot scan them.
  **Issue:** #199
- **Decision:** Proof against the real bypasses, in copies of the repository outside it (real `dotnet`, ESLint, and tsc). "Scanner before" is `scripts/check-suppressions.py` at `45b78e7`; "scanner now" is this commit's; "canary" is `scripts/check-canaries.sh`. PASS means the bypass got through.

  | Bypass | Scanner before | Scanner now | Canary |
  | --- | --- | --- | --- |
  | control (no change) | pass | pass | pass (19 checks) |
  | N01 `<CompilerResponseFile>` at `docs/relax.md` with `-nowarn` | PASS | fails | fails (Domain: build succeeded) |
  | N02 the same, a binary-named file with a non-UTF-8 byte | PASS | fails | fails |
  | N03 workflow `dotnet build @docs/relax.md` | PASS | fails | PASS (not its build) |
  | N04 `<PotentialEditorConfigFiles Include="relax.cfg">` | PASS | fails | fails |
  | N05 `<AssemblyAttribute>` GeneratedCode (CA2200 gone, CS8618 stays) | PASS | fails | fails (Domain: CA2200 missing) |
  | N06 `[\U00000053uppressMessage]` | PASS | fails | per file: not seen |
  | N07 `/**/ [SuppressMessage]` | PASS | fails | per file: not seen |
  | N08 `/**/ [GeneratedCode]` | PASS | fails | per file: not seen |
  | N11 `web/eslint-suppressions.json` | PASS | fails | PASS (run: it lists existing files, the canary is new) |
  | N12 `const rules = {…: OFF}`, shorthand `{ rules }` | PASS | fails | fails (web lint-error), only because the rules switched off have a canary |
  | N13 inline `processor` returning no messages, for all files | PASS | PASS | fails (web lint-warning, lint-error) |
  | N14 `import relax from '#relax'` | PASS | fails | fails (web lint-error), as N12 |
  | N18 `strictBuiltinIteratorReturn: false` | PASS | fails | fails (web typecheck) |
  | N19 `noUnusedLocals`, `noFallthroughCasesInSwitch`, `noImplicitOverride` false | PASS | PASS | fails (web typecheck) |
  | N22 `web/.npmrc` `node-options=--import=./quiet.mjs` | PASS | fails | fails (web lint and typecheck exit 0) |
  | N23 workflow `env: NODE_OPTIONS` (run in a shell: `npm run lint` exits 0) | PASS | fails | per step: not seen |
  | N24 `npm rum lint -- --ignore-pattern src` (run: exit 0) | PASS | fails | per step: not seen |
  | N25 folded scalar carrying `-- --quiet` | PASS | fails | per step: not seen |
  | N26 `if npm run lint; then …; fi` (run: exit 0) | PASS | fails | per step: not seen |
  | N27, N28 `\|\| exit 00`, `\|\| exit $((0))` (run: exit 0; `exit 256` too) | PASS | fails | per step: not seen |
  | N30 `shell: pwsh` | PASS | fails | per step: not seen |
  | #194 `<Features>run-nullable-analysis=never</Features>` | fails | fails | fails |
  | #194 `<GlobalAnalyzerConfigFiles>` | fails | fails | fails |
  | #194 `.editorconfig` `generated_code = true` | fails | fails | fails (all 11 projects) |
  | #194 `<TreatWarningsAsErrors>false` in a csproj | fails | fails | fails |
  | #194 `<Nullable>disable` in the root props | fails | fails | fails (all 11) |
  | #194 `<RunAnalyzers>false` | fails | fails | fails |
  | #194 `web/.npmrc` `script-shell=/usr/bin/true` | fails | fails | fails |
  | #194 `"strict": false` in `web/tsconfig.app.json` | fails | fails | fails |
  | #194 `lint` script without `--max-warnings 0` | fails | fails | fails (web lint-warning only) |
  | `<NoWarn>CS8618;CA2200</NoWarn>` with a rationale | PASS | PASS | fails |
  | ESLint `'no-debugger': 'off'` with a rationale (extension) | PASS | PASS | fails |
  | `.editorconfig` CA2200 `none` with a rationale | PASS | PASS | fails (all 11) |
  | `NoWarn` set as an environment variable where the check runs | PASS | PASS | fails (all 11) |

  In every run the copy's content hash was the same before and after the canary check.
  **Why:** The issue asks for the canary to be shown failing where the scanner passes. Caught by neither, stated plainly: nothing in this table; but N12 and N14 are caught by the canary only because the rules I switched off are ones it trips, N13 only because the processor covered every file, and N19 only for the tsconfig the canary's folder is in. The same routes aimed at a rule with no canary, or at some files only, pass the canary; the scanner now closes N12 and N14 as written, and N13 scoped to some files stays open (listed under "Not covered by either check").
  **Issue:** #199
- **Decision:** Scanner fixes, each with a fixture case shown to pass the unfixed script (16 new cases, 39 new checks; 276 in all, none of the 237 changed or removed): N05 (`GeneratedCode` named in an MSBuild value), N06 (`\UXXXXXXXX` escapes), N07 and N08 (C# comments are blanked before attributes are looked for, so an attribute after `/**/` is found and one inside a comment is not), N24 (npm's `rum` and `urn`, options before the script name), N25 (a folded or multi-line `run:` is read as the one line YAML makes of it), N26 (a gate command as an `if`, `elif`, `while`, or `until` condition), N27 and N28 (the status after `|| exit` must be 1 to 255 or one plain variable), N23 (`NODE_OPTIONS`, which also covers N22's `.npmrc`), N30 (`shell:` may only be `bash` or `sh`), N03 (`@file` on a dotnet command line). Beyond the issue's list, because a canary sees them only for the two diagnostics or the handful of rules it trips: N01 and N04 (`CompilerResponseFile` and `PotentialEditorConfigFiles` join the forbidden names), N11 (`eslint-suppressions.json` and the bulk-suppression flags), N12 (`rules` as a shorthand property), N14 (`#name` imports), N18 (`strictBuiltinIteratorReturn` joins the strict family).
  **Why:** The issue's item 2 asks for the confirmed per-file and per-step forms. The extra six are one-line additions for routes that can silence any rule, not only a canaried one, and four of them make a sentence of the header true that was false (analyzer configuration under another name, rule severities are literal, no local file is imported).
  **Issue:** #199
- **Decision:** `if ! <gate command>; then … fi` stays allowed when the branch holds an `exit` or `return` with a failing status (or calls a function of the file that does); every other use of a gate command as a condition fails.
  **Why:** `.github/workflows/release.yml` and two existing `good/` fixtures use that form to print a message before failing. Forbidding it would have changed existing checks; allowing any `if !` would have left `if ! npm run lint; then echo oops; fi` open.
  **Issue:** #199
- **Decision:** Not fixed in the scanner, and listed under "Not covered by either check": N10 (`<ILLinkTreatWarningsAsErrors>`), N13 scoped to some files, N15 (an aliased `eslint-config-prettier` import), N16 and N17 (`@ts-ignore` in `/*/ … */`, which the lint rule still bans), and N19's options outside the canary's tsconfig.
  **Why:** The maintainer's decision is that scanner gaps remaining after this pass are documented and carried, not chased.
  **Issue:** #199
- **Decision:** The `guards` job now sets up the .NET SDK and Node (the same actions, pins, and cache keys as the `dotnet`, `web`, `extension`, and `e2e` jobs), runs `scripts/tests/check-canaries/run.sh` and then `scripts/check-canaries.sh`, and has a 20-minute limit instead of 5. The summary step prints the canary FAIL lines. `ci` already needs `guards`.
  **Why:** The canary check builds every project and installs three npm projects; locally it takes about 31 s, its fixture test about 80 s.
  **Issue:** #199
- **Decision:** Documentation: the scanner's header now ends with "What the canary check adds" and "Not covered by either check"; `docs/conventions.md` summarises both and names the header as the authority; the `guard:` annotation of invariant 8 in `CLAUDE.md` names both checks and what neither covers (the invariant's sentence is unchanged); the README's CI table gains the `e2e` and `guards` rows and how to run the canaries locally. The five overclaims: `[GeneratedCode]` and un-applied `SuppressMessage` (true now: N05 to N08 fixed), "no local file is imported" (reworded to the forms that are found, with `#name` added), literal rule severities (reworded, shorthand added), "no `||` leading to anything but a non-zero exit" (the rule is now stated exactly, and "a variable that holds 0 is not seen"), an analyzer configuration under another name (the name is forbidden, and the canary is named as what catches the rest).
  **Why:** Item 3 of the issue: no sentence may claim more than the tests show.
  **Issue:** #199
- **Decision:** Proof that the canary step turns CI red: throwaway draft pull request #201 (closed unmerged, branch deleted) switched `noUnusedLocals` off in `web/tsconfig.app.json`. The scanner step, the `web` job, and every other job passed; `scripts/check-canaries.sh` failed (`FAIL web typecheck … did not report: src/n8tracksCanaryTypes.ts:53 error TS6133`), so `guards` and `ci` failed (run 37239056845). On pull request #200 the step takes 76 s and its fixture test 87 s; the `guards` job takes about three and a half minutes in all.
  **Why:** The brief asks for a failure that must fail CI to be shown failing CI, on a throwaway pull request rather than on #200.
  **Issue:** #199

## /n8-plan M5 — 2026-10-04

- **Decision:** M5 is 20 stories (#203–#222) under epics #16 and #17, including spike TS-004 (#214), which needs the maintainer's signed-in Suno session.
- **Decision:** From the user: scanned formats are WAV, M4A, MP3, FLAC, OGG, Opus, and AAC (settles PRD open decision 6); the player is a bar on every page; clips for bulk download are picked in the extension's panel on Suno.
- **Decision:** From the user: the extension may click Suno's own Download menu to have a file prepared and may hand audio addresses to the browser's downloader without credentials. This amends `docs/suno-integration.md` with a second exception to observing and adds the `downloads` permission; the amendment lands with #214 and #216.
- **Decision:** From the user: TS-004 is its own spike in M5, not an extension of #127; a Song whose Selected Generation has no local file plays that Generation from Suno, never another Generation's local file.
- **Decision:** Invariant 2's guard is #205. Starting a scan is session-only, keeping file-system actions away from MCP scopes (invariant 7).
- **Decision:** The four untriaged captures #167, #179, #186, and #197 were moved to M10 as bugs, as proposed in round one and not objected to.
- **Decision:** Outcome 1 was reworded at the gate: files appear after the next scheduled scan (15 minutes by default, measured from the end of the previous scan), not "within 15 minutes".
- **Decision:** Scanned formats are fixed in V1, although the PRD says "other configured supported formats"; shown at the gate and accepted.
- **Deferred to M6:** filtering the Songs table by local audio count.

## /n8-plan M6 — 2026-10-04

- **Decision:** M6 is 16 stories (#223–#238) under epics #18 and #19.
- **Decision:** From the user: search results are shown in the Songs table itself, with a search box in the header; the dashboard is the home page; notifications live in a bell with a panel and toasts; search covers archived and trashed records by default, marked; the diagnostic bundle replaces the sign-in name, IP addresses, host names, and Album and Playlist names with stable codes; a hidden dashboard section stays hidden while badges still count.
- **Decision:** Full-text search is a new `search` parameter on the Songs list; `q` keeps the picker lookup #90 gave it.
- **Decision:** Telemetry off by default (#34) and structured, redacted logs (#27) were delivered in M0 and are recorded as established, not re-planned.
- **Decision:** A failed import commit that changed nothing returns its export to `ready` (#231 extends #140), and discarding an export gains an optional reason (#229 extends #131).
- **Deferred to M7:** notifications and the dashboard section for MCP bulk operations; correlation IDs on MCP requests; the gateway credential's validity on Diagnostics.
- **Deferred to M8:** notifications for portable export and import.

## /n8-plan M7 — 2026-10-04

- **Decision:** M7 is 16 stories (#239–#254) under epics #20 and #21.
- **Decision:** From the user: AI clients authenticate to the gateway with a token (an `mcp-gateway` credential); OAuth sign-in for web connectors is out of V1 (settles PRD open decision 5). #250 files an unscheduled issue for it.
- **Decision:** From the user: Undo restores what is untouched and reports the rest; AI clients may create, assign, rename, and recolour Tags, and may perform removals that delete no record; the API compatibility promise takes effect at version 1.0.0.
- **Decision:** PRD open decision 7 is settled by #247 (bulk writes: one endpoint, up to 100 operations, one transaction) and #244 (job resources). A restore keeps its own status resource because it replaces the jobs table.
- **Decision:** Invariant 7's guard is #254, which also extends the guards of invariants 1 and 5. An API operation is forbidden to MCP credentials unless classified allowed, and refusing by credential kind extends #56.
- **Decision:** Names (workflow states, Tags, Genres, Artists, titles) are resolved by the API, never by the gateway (invariant 5); found by the coverage check.
- **Decision:** Cursor paging is the public convention; the Songs table, audio files, and export records keep page numbers as well.

## /n8-plan M8 — 2026-10-04

- **Decision:** M8 is 16 stories (#255–#270) under epics #22 and #23. #266, #269, and #270 each need a hands-on run by the maintainer.
- **Decision:** `docs/portable-catalog-schema.md` was written during planning as the measure for the "complete portable catalog model" claim: 23 record types, matching the roadmap's list. Instance settings (time zone, personal defaults, the Suno model list, schedules) are excluded, agreed with the user in round two.
- **Decision:** From the user: CSV export is the Songs table as shown; import conflicts are decided per record with apply-to-all; the performance test runs in a constrained container only; raw Suno data is exported verbatim with a warning; a missed performance target blocks a release unless waived in writing.
- **Decision:** From the user ("Ask me every release, I may choose to waive it"): hands-on checks (four browsers, screen reader, Synology) are asked for at every release and may be waived. Planner's reading, shown at the gate: this applies to stable releases; pre-release tags run the automated checks only.
- **Decision:** Invariant 3's portable-import guard is #263. Seeded reference records match by well-known ID, not by name, so an import into an empty instance replaces nothing without a choice; found by the coverage check.
- **Decision:** #255, #256, #259, #261, and #263 each cover the 23 record types under grouped criteria and own more items than criteria; per-type and per-field tests enforce each type. The stories were not split further.
- **Decision:** PRD open decision 8 (detailed accessibility criteria) is settled by #269's walkthroughs.

## Whole-project analysis — 2026-10-04

- **Decision:** Every feature milestone (M2–M8) is now planned. Audit emphases were written to the M9 milestone description. M10 and M11 remain unplanned placeholders by the user's earlier instruction.
- **Decision:** No story checks the PRD's V1 Release Gate as a whole; each of its twelve lines has a home in M2–M8. A full release-gate walkthrough is recorded as an audit emphasis.
- **Decision:** Executor simulations for M5–M8 ran one fresh agent per story for both passes, on the session's model; each milestone's coverage check ran as one fresh agent.


## Ad-hoc — 2026-10-05

- **Change:** Bulk download may spend the user's Suno download allowance. The extension may click Suno's "Unlock & Download" for clips not yet unlocked, after the user confirms the number of unlocks a run will use against the allowance remaining, and never buys download packs. The free playback stream (m4a-opus) is added as a fourth format, "M4A (streaming quality)".
  **Why:** Spike TS-004 (#214) found that Suno meters downloads. WAV, MP3, and M4A exist only after a per-clip unlock that deducts one plan download (60 per period on Premier, extra packs for sale). The plan's rule that a credit-spending step makes a format "not automatable" would have limited bulk download to already-unlocked clips. The maintainer chose to allow unlocking with a confirmed count, and to offer the free stream.
  **Affects:** M5 #215 (formats, unlock count and allowance in the summary), #216 (unlock step, 30 s preparation, one-hour signed addresses, chosen file name may be overridden by the browser, two audio hosts), #222 (`m4a-stream` format, records whether an unlock was spent), #203 (scans `m4a` but not the `.mp4` a browser may give an M4A; #216 flags the rename, widening #203 is open). `docs/suno-integration.md` amended in the same change. — reconciled by /n8-replan 2026-10-05
- **Change:** The Suno import map gains a `createRequest` path, and the fixture check is a TypeScript module with a vitest test rather than `extension/scripts/check-fixtures.mjs`.
  **Why:** Spike TS-003 (#127) found six Create options (vocal gender, duration, Personalize, background music, and others) only in the Create request, and that the feed rewrites styles and Speech tone. A `.mjs` script would sit outside the extension's ESLint and `tsc` gate (invariant 8).
  **Affects:** M4 import and observed-Create stories that read `docs/suno-import-field-map.json` (the observed-Create story must read the request body for those fields, never copying the request's `token`); the adapter must answer Suno's "Overwrite Lyrics & Styles?" dialog when loading a source. — reconciled by /n8-replan 2026-10-05

## /n8-replan M4,M5 — 2026-10-05

- **Decision:** Reconciled M4 and M5 with spikes TS-003 (#127) and TS-004 (#214).
  **Why:** The two 2026-10-05 Ad-hoc entries; git history since planning touched only M0/M1 infrastructure, and no adapter code exists yet.
  **Issues:** Contract changes: epic #14 and M4 coverage claim A (options Suno does not return are recorded as such), #135 (truth qualified), #149 (non-echoed options from the Create request body, not a form read-back), #133 (Overwrite dialog recognised; #216's two exceptions anticipated), #148 (Overwrite dialog AC). Implementation details: #117, #134, #136, #145, #146, #147, #150, #154, #216, #221. M5 description outcome 6. Earlier this session, #215, #216, #222 were edited from the TS-004 decision.
- **Decision:** #203 keeps scanning `m4a` only, not `mp4`.
  **Why:** Maintainer's choice; #216 flags an M4A the browser saved as `.mp4` so the user can rename it, and `.mp4` is usually video.
  **Issue:** #203

## /n8-exec M2 — 2026-10-05

- **Decision:** Argon2id through Geralt 4.4.0 (libsodium 1.0.22), m=19 MiB, t=2, p=1, stored as libsodium's encoded `$argon2id$v=19$m=19456,t=2,p=1$…` string; the hasher normalises the password to NFC for both hashing and verifying.
  **Why:** The story delegated "a maintained .NET library". Geralt was released 2026-07 and wraps libsodium's `crypto_pwhash_str`, which writes the parameters into the hash. Konscious (2024) and Isopoh (2023) are older and Konscious has no encoded format. libsodium always uses one lane, which matches p=1. Normalising inside the hasher means the sign-in story cannot forget it.
  **Issue:** #52
- **Decision:** The single-administrator rule is a `slot` column that is always 1 (`CHECK (slot = 1)` plus a unique index), alongside a UUIDv7 `id` primary key and a unique `username_key`. `settings` (key, JSON value with `CHECK (json_valid(value))`) is created in the same migration (`AddAdministratorsAndSettings`) but is not used yet.
  **Why:** The story asks for a database constraint and for both tables, and the conventions call for UUIDv7 IDs. A constant unique slot enforces "one row" without making the ID a magic value. The race loser's unique violation (SQLITE_CONSTRAINT_UNIQUE, 2067) maps to 409 `setup_already_complete`.
  **Issue:** #52
- **Decision:** The POST checks run in this order: already complete → 409; validation → 422; data path not writable → 409 `storage_not_writable`; then create. The data-path check commits an upsert of `app_metadata.storage_checked_utc` on a separate unpooled connection, then creates, fsyncs, and deletes `.n8tracks-write-check-<guid>` in the data folder, within the health checks' 2-second deadline.
  **Why:** The story gives neither the order nor the check's mechanics. Validation is cheap and the write is not. A rolled-back write may never reach the disk under WAL, so a committed write is what proves the database is writable.
  **Issue:** #52
- **Decision:** Lengths count code points of the NFC form. Unpaired surrogates are refused before normalising. "Printable" for usernames excludes Control, Line and Paragraph separators, Surrogate, and unassigned code points. Format characters such as ZWJ are allowed so emoji names work. The comparison key is NFC plus `ToUpperInvariant()`, which is the ordinal-ignore-case comparison.
  **Why:** The discretion lines say "code points" and "printable" without defining which form or which categories.
  **Issue:** #52
- **Decision:** Problem Details plumbing in `src/n8Tracks.Api/Problems/ApiProblem.cs`: every error carries `code` and `requestId`. 422 `validation_failed` has `errors` keyed by field name. An unknown `/api/v1` route is 404 `not_found`, through a fallback endpoint. A body that cannot be read is 400 `invalid_request`: `RouteHandlerOptions.ThrowOnBadRequest` is on in every environment, and the exception middleware answers `BadHttpRequestException`. The 503 `setup_required` gate exempts everything under `/api/v1/setup` and logs its completion line at Information, not Error.
  **Why:** This is the first `/api/v1` story, so it owns the shared plumbing. Without the fallback and the bad-request handling, the framework answers with empty bodies, or with a 500 in Development.
  **Issue:** #52
- **Decision:** POST `/api/v1/setup` answers 201 `{ id, username }` with no Location header. Once setup is complete, the status endpoint returns exactly `{ "complete": true }`.
  **Why:** The story names the 409s but not the success body. There is no administrator resource URL yet.
  **Issue:** #52
- **Decision:** Rule 1: `SetupChecks` yields (`await Task.Yield()`) after each shared deadline check. Rule 1: EF Core's `SaveChangesFailed` event is logged at Debug, like the existing command-error rule.
  **Why:** The race test showed that callers sharing one run of a `DeadlineCheck` are handed the result one after another on the worker thread. A caller that continued synchronously into Argon2 hashing pushed the other past the 2 s deadline, which gave a false 409 `storage_not_writable`. With the yield, the regression test (two concurrent submissions, real checks) gives exactly one 201 and one 409 `setup_already_complete`. EF logged the expected race loss at Error even though the caller handles it.
  **Issue:** #52
- **Decision:** Rule 2 (accessibility): theme `primaryShade` is 8. Input descriptions and Stepper descriptions use the secondary text colour. A new palette token `errorText` (light `#c92a2a`, dark `#ffa8a8`, contrast-tested) is mapped onto `--mantine-color-error`. PasswordInput gets an explicit `aria-invalid`.
  **Why:** axe flagged white on Mantine's default blue shade 6, the dimmed grey, and the red field errors as below 4.5:1, the first time the app showed a filled button or a form. Mantine's PasswordInput does not set `aria-invalid` itself.
  **Issue:** #52
- **Decision:** The wizard has four Stepper steps: Storage, Media, Backups ("Skipped for now", never opened; Next on Media goes to the administrator), and Administrator. Retry and Check again refetch the status. A 409 `storage_not_writable` returns to the storage step. A 409 `setup_already_complete` says so and offers Continue. Field rules are left to the API's 422 and not duplicated in the browser.
  **Why:** The story leaves the presentation open; one source of the rules keeps the browser and the API from disagreeing.
  **Issue:** #52
- **Decision:** e2e: the container helpers moved into `e2e/support/containers.ts`. Global setup completes setup on the three shared containers with fixed test credentials (`e2e/support/setup.ts`, `completeSetup`). The Demo spec (`tests/setup.spec.ts`, `@root-only`) starts a fresh container of its own on port 18790 for each attempt and removes it afterwards. The smoke script's `mounted` section asserts the 503, completes setup, and checks that it survives a restart and a recreate.
  **Why:** A shared un-set-up container would already be set up when CI retried the test. The sub-path project does not walk the wizard because base-URL resolution is already covered by the shell tests there.
  **Issue:** #52
- **Decision:** Unit tests for the rules and the hasher live in `tests/n8Tracks.Api.Tests/Setup/`; there is no new Domain or Application test project. The rules live in Application (`AdministratorRules`), not Domain.
  **Why:** No such test project exists, and adding one is build infrastructure the story does not need. The rules are application-service validation; there is no domain entity yet.
  **Issue:** #52
- **Decision:** The sign-in throttle is one row of the existing `settings` table (key `signIn.throttle`, JSON `{ failuresUtc, lockedUntilUtc }`), not a new table. No row means no failures. Deleting the row clears a lockout at once, which is what the reset command (#82) will do. A row that cannot be read counts as no failures.
  **Why:** The story says "a database row" and names a new table only for sessions. `settings` already exists, and #82 already plans a state row there (`account.lastPasswordReset`). A broken row must not lock the owner out for good.
  **Issue:** #53
- **Decision:** A sign-in runs in one SQLite transaction: read the throttle, check the password, record the failure or clear it, end the old session, create the new one. Microsoft.Data.Sqlite begins transactions `IMMEDIATE`, so concurrent attempts are counted one at a time. A test sends eight wrong attempts in parallel and gets exactly five 401s and three 429s. While the throttle refuses, nothing is checked. The fifth failure is still a 401; the refusal starts with it and clears the failure count, so after it ends a fresh window of five begins. Missing fields are 422 `validation_failed` and are not counted.
  **Why:** If the read and the write were separate, parallel guesses could pass the limit. The story leaves open what the fifth answer is and what happens after a refusal ends.
  **Issue:** #53
- **Decision:** Sign-in answers 201 `{ username, expiresAt }`. 429 carries `Retry-After` in seconds, `retryAt` (UTC), and `detail` "Too many failed sign-in attempts. Try again at HH:mm.", with the time in the configured time zone rounded up to the minute. Every session answer is `no-store`.
  **Why:** The story fixes the codes and fields but not the success status or the message format. Rounding up means the message never gives a time that is too early.
  **Issue:** #53
- **Decision:** An ended session's row is deleted at once (sign-out, sign-out everywhere, signing in again); there is no `revoked` column. A hosted service purges expired rows when the app starts and every 24 hours after that. Expiry is 30 days from the recorded last use. The last use is written, and the cookie re-issued, only when at least a minute has passed since the last write.
  **Why:** Deleting at once is simpler, and a deleted row cannot be reused. The daily purge therefore only has expired rows left to remove.
  **Issue:** #53
- **Decision:** Authentication and authorization run on the app itself, after the path base and the setup gate. The fallback policy requires a session. A custom `IAuthorizationMiddlewareResultHandler` lets through requests that reached no endpoint (the shell, its files, and 404s) and routing's own 405, and the authentication handler does not look up a session for them. The anti-forgery check runs after authorization, so a request without a session gets 401 first. The handler logs nothing (`NullLoggerFactory`); sign-in and sign-out write their own lines, without the username.
  **Why:** The framework applies the fallback policy to endpoint-less requests too. That turned unknown paths and the frontend into 401s. Calling `UseAuthentication` only in a `UseWhen` branch made the host insert its own copy ahead of the path base. The handler's base class would have added "not authenticated" and "challenged" lines at the app's log level on every request.
  **Issue:** #53
- **Decision:** The OpenAPI document (Development only) now needs a session: anonymous requests get 401. `FrameworkSettingsTests` checks 401 instead of 200 for the Development case, and the OpenAPI health test signs in first. The smoke script expects 401 `not_authenticated` after setup, signs in, and checks the session after `docker restart`.
  **Why:** The story's allow-list is health, setup status, setup, and sign-in, and the OpenAPI document is not on it.
  **Issue:** #53
- **Decision:** The cookie handler tries up to four `n8tracks_session` values from the `Cookie` header, not only the first. `session`, `sessionid`, and `idhash` were added to the redaction names.
  **Why:** Two instances on one host under different paths each set a cookie with that name, and the browser sends both. The redaction names cover the cookie name and the identifier and its hash wherever they are logged.
  **Issue:** #53
- **Decision:** `403 session_required` for bearer requests to the session endpoints is not implemented yet.
  **Why:** No bearer scheme exists until #55. Until then a bearer request carries no session and gets 401. #55 adds the `SessionOnly` marker and must apply it to `/api/v1/session` and `/api/v1/sessions`.
  **Issue:** #53
- **Decision:** The UI is a session gate under the setup gate and a sign-in page at `/sign-in` with `returnTo`. `returnTo` is accepted only as a local path: no `//`, no backslash, no control characters. The header has a minimal user menu: the username, "Sign out", and "Sign out everywhere". After setup the new administrator signs in through the form; setup does not sign them in. The Mantine menu is used with `withInitialFocusPlaceholder={false}`.
  **Why:** The Demo needs both sign-out actions, and the full shell is #54, which moves this menu. axe reports Mantine's focusable `role="presentation"` placeholder inside `role="menu"` as `aria-required-children`.
  **Issue:** #53
- **Decision:** e2e: global setup signs in to the root and sub-path containers and saves each session as the Playwright storage state of its project, in `$TMPDIR/n8tracks-e2e-auth/`. Specs that need a signed-out start use an empty storage state. The no-media test signs in for itself through `page.request`. The Demo spec (`tests/sign-in.spec.ts`, `@root-only`) runs on the fresh container, because it restarts that container.
  **Why:** Cookies ignore ports, so the root and no-media containers (both `localhost`, path `/`) cannot share one stored cookie. Saved sessions are kept out of the repository.
  **Issue:** #53
- **Decision:** `POST /api/v1/account/password` takes `{ currentPassword, newPassword, newPasswordConfirmation }` and answers 204. The new password is held to the setup rule (`AdministratorRules`), and the confirmation is checked by the API, as setup does. A wrong current password is 422 `validation_failed` on `currentPassword`, never a 401. On success one transaction stores the new hash, ends every other session of the administrator, and keeps the current one. Logic is in a new `AccountService`, with ports `IAccountPasswords` and `ISessionStore.DeleteAllExceptAsync`, both implemented by `SessionStore`. No schema change.
  **Why:** The key link names only the current and the new password. Adding the confirmation keeps the API as the one source of the rules, which is the setup wizard's decision. A 401 would set off the re-sign-in prompt for a session that is fine.
  **Issue:** #54
- **Decision:** A wrong current password counts as a failed sign-in in the shared throttle. While the throttle refuses, the change is 429 `sign_in_throttled`, with the same shape sign-in uses. A successful change clears the throttle.
  **Why:** Without this, anyone holding a session could guess the password faster than the sign-in form allows. The story is silent on it.
  **Issue:** #54
- **Decision:** Added a small `SessionOnly()` endpoint marker (`src/n8Tracks.Api/Auth/SessionOnly.cs`). A request to a marked endpoint that carries `Authorization: Bearer` gets 403 `session_required` before authentication runs, even if a valid cookie comes with it. It is applied to the password change, GET/DELETE `/api/v1/session`, and DELETE `/api/v1/sessions`. Sign-in stays anonymous.
  **Why:** The story's test plan requires the bearer refusal now, but no bearer scheme exists until #55. Refusing on the header alone means the test bites now, and a cookie is never used in place of a token. #55 folds this marker into its `ScopeRequirement` markers. This settles the `session_required` item #53 deferred.
  **Issue:** #54
- **Decision:** The shared fetch helper is `web/src/api/client.ts` (`apiFetch`). Its one 401 interceptor fires only on `code: not_authenticated`, and only while a handler is registered: the signed-in app's `ReauthPrompt`, mounted in the session gate. Every request that meets the 401 while the prompt is open waits on the same prompt and is replayed after sign-in. The 10-second timeout covers each network attempt, not the wait for the user. Choosing "Sign out" in the prompt returns the held 401s and goes to the sign-in page. Sign-in, sign-out, setup, and health keep their own `fetch`: their 401s mean something else, or they are anonymous.
  **Why:** The key link asks for a single interceptor. Keying it on the code keeps sign-in's `invalid_credentials` apart from an ended session. The prompt is a Mantine modal with no close button and no Escape, because closing it would leave the held requests hanging. It reuses the sign-in form, now split out as `SignInForm`.
  **Issue:** #54
- **Decision:** Routes are `/` → `/songs`, `/songs` (a placeholder: "There are no songs yet."), `/settings` → `/settings/account`, `/settings/account`, `/settings/system`, and a "Page not found" page inside the shell for any other path. The sidebar is Mantine's `AppShell.Navbar`, labelled "Main", and collapses behind a burger below `sm`. Its links are Songs, then a "Settings" group holding Account and System, and the current page is marked `aria-current="page"`. The M0 health panel became Settings → System (h2 "System", h3 "Health"). Its component tests moved from `App.test.tsx` to `settings/SystemPage.test.tsx`, and the e2e shell spec's `openShell` now opens `settings/system`. The user menu moved to `web/src/shell/UserMenu.tsx`. The plain pages (setup, sign-in) have no menu.
  **Why:** The discretion lines fix the sidebar contents and say the M0 shell's tests move with it. A Songs route is needed for the sidebar to point at before the catalog stories exist. Deep links that are no page used to show the health panel, so the specs that deep-linked to `/library/deep/link` now use real pages.
  **Issue:** #54
- **Decision:** e2e: `tests/account.spec.ts` (`@root-only`) walks the Demo on the fresh container, because it changes the password, and scans every state with axe. The prompt is modal, so its light and dark scans switch `data-mantine-color-scheme` on the root element instead of clicking the colour control behind the overlay.
  **Why:** Changing the shared containers' password would break every other spec. The colour control cannot be clicked while the modal's overlay covers it.
  **Issue:** #54
- **Decision:** Credential storage is a new `credentials` table (migration `AddCredentials`): UUIDv7 `id`, `name`, `kind`, `scopes` (space-separated, in the PRD's scope order, `CHECK length > 0`), unique `token_hash` (SHA-256 hex), `created_utc`, nullable `last_used_utc` and `revoked_utc`, and `revision` (starts at 1). No name-uniqueness index yet.
  **Why:** The story says it adds credential storage, so the new table is in scope. `revoked_utc` and `revision` are there so that #56 (revoke, rename) needs no second migration. The rule that names are unique among non-revoked credentials belongs to #56, along with its rename and list screens, so its partial index is left for #56.
  **Issue:** #55
- **Decision:** The application layer has `CredentialScopes`/`CredentialKinds`, `CredentialToken` (`n8t_` + 32 random bytes as 43 zero-padded base62 digits; SHA-256 hash; `CryptographicOperations.FixedTimeEquals`), a minimal `CredentialService.CreateAsync` (name trimmed 1–100, known kind, at least one known scope, repeats collapsed), and `CredentialVerifier` (hash lookup; revoked counts as unknown; last-used written on first use and then at most once a minute, even when the request is then refused for scope). #56 adds list, rename, and revoke to `CredentialService`.
  **Why:** The test plan creates credentials "through the application service". The verifier artifact is named by the story. Splitting the two keeps the per-request path small.
  **Issue:** #55
- **Decision:** Authentication goes through a selector policy scheme (`SessionOrBearer`). Any request whose `Authorization` header starts with `Bearer` goes to the new `Bearer` handler, and every other request goes to the session handler. The two are never combined, so an invalid token is 401 `invalid_token` (with `WWW-Authenticate: Bearer error="invalid_token"`) even when a valid cookie comes with it. On an anonymous endpoint, or a request that reaches no endpoint, the Bearer handler returns no result. The header is accepted only as one value of the form `Bearer <token>`.
  **Why:** This follows the discretion lines "never falls back to the cookie" and "a Bearer header on an anonymous endpoint is ignored". The fallback policy names only the selector scheme, so the policy evaluator cannot merge a cookie identity into a token request.
  **Issue:** #55
- **Decision:** `src/n8Tracks.Api/Auth/ScopeRequirement.cs` holds the markers `RequireScope(...)`, `SessionOnly()` (moved there from #54's `SessionOnly.cs`, which is deleted, with the same metadata and middleware), and `AllowAnonymous()` from the framework. It also holds `ScopeMiddleware`, which runs after authorization. A session passes every check. For a credential, a missing scope is 403 `insufficient_scope`. `requiredScope` is a string when the endpoint requires one scope and a list of the missing scopes when it requires several. An endpoint with no marker refuses every token with 403 `session_required` (fail closed). Bearer requests stay exempt from the anti-forgery header, because the existing check applies only to session principals.
  **Why:** This follows the discretion line for the two-scope list. A single scope stays a plain string, which is the shape #56's spec names. Failing closed means a forgotten marker can never widen what a token can do. The enumeration guard catches the forgotten marker before it ships.
  **Issue:** #55
- **Decision:** The API's own 404 fallback (`/api/v1/{**path}`) carries a fourth, internal marker, `AnyCaller()`: any valid session or token gets 404 `not_found`, and an invalid token gets 401. The guard test fails if `AnyCaller()` appears on any other endpoint, or if an endpoint carries two markers.
  **Why:** The fallback is not part of the API. Giving it a real scope would send a token with a typo in its path to 403 `insufficient_scope` and an unrelated scope name. Making it anonymous would change #53's rule that an anonymous caller gets 401 there.
  **Issue:** #55
- **Decision:** Setup submission (`POST /api/v1/setup`) stays `AllowAnonymous`, not session-only.
  **Why:** The discretion line lists "setup submission" among the session-only endpoints. But setup is the step that creates the administrator, so no session can exist when it runs, and no credential can exist before it either. A token sent after setup is ignored on this endpoint and gets the same 409 as anyone else.
  **Issue:** #55
- **Decision:** No real endpoint requires a scope yet, so the bearer tests add endpoints under `/api/v1/test/` to the test host only (`tests/n8Tracks.Api.Tests/Auth/CredentialApi.cs`, `TestEndpoints`). An `IStartupFilter` adds them to the app's own endpoint route builder after the pipeline is configured, so they pass through the real middleware and show up in the enumeration. The guard's bite was proven twice: once with an unmarked test-host endpoint (a permanent test), and once by removing `.SessionOnly()` from the password endpoint, which made the test fail naming `POST /api/v1/account/password` (then restored).
  **Why:** The test plan asks for both a success case and a refusal case per scope, and for the guard to be shown biting in the test host.
  **Issue:** #55
- **Decision:** `tokenhash` was added to the redaction policy's sensitive names (the normalised `token_hash`/`TokenHash` ends in "hash", which no existing entry caught). The log-redaction guard has a new case: at Debug, a valid token (allowed, and refused for scope) and an unknown token never reach the log, and neither does the stored hash.
  **Why:** Conventions: a new field holding tokens joins the name list in the same story. This guards invariant 6.
  **Issue:** #55
- **Decision:** The reset is `AccountService.ResetPasswordAsync`, the service the web UI's password change already uses. In one transaction it replaces the hash, deletes every session of the administrator, deletes the `signIn.throttle` row, and writes `account.lastPasswordReset`. The command reaches it through a small container of its own, built in `n8Tracks.Api.DependencyInjection.CommandServices`, with the application and infrastructure layers only. The schema check sits behind a new application port, `IDatabaseSchemaCheck`, so that `src/n8Tracks.Api/Cli/ResetPasswordCommand.cs` (the path the story names) depends on no Infrastructure type, as the layering guard requires.
  **Why:** The key link asks for "the same application service the web UI uses". The other way to satisfy the layering test was to widen its composition-root exemption to `n8Tracks.Api.Cli`, which would weaken a guard.
  **Issue:** #82
- **Decision:** "Restore or upgrade in progress" is checked as the EF migration lock (`__EFMigrationsLock` holds a row) only. No maintenance state or upgrade marker exists yet: the backup and restore stories have not landed. "Schema does not match" means the applied migrations are not exactly this build's migrations, whether they are older, newer, or missing. The command reads the migration history without opening a migration and never sets WAL. A missing database file is reported as "setup has never been completed", and the command creates nothing: it checks for the file before it opens a connection.
  **Why:** The discretion line names markers that do not exist yet. The restore story should add its maintenance-state check to `DatabaseSchemaCheck`.
  **Issue:** #82
- **Decision:** The "it is always written, whatever the log level" in the discretion line is read as applying to the `account.lastPasswordReset` row. The command writes that row every time. It also writes its Information line to stderr through an unfiltered logger shaped like the startup logger (source context `n8Tracks.ResetPassword`). The running server reads the row after each successful sign-in and logs it at Information through the normal logger. A process-wide `PasswordResetNotice` makes sure each reset time is logged once per process, so after a restart the most recent reset is logged once more, at the first sign-in.
  **Why:** A reset ends every session, so sign-in is the server's next certain read. Reading the row on every request would cost a query per request. Logging once per process means no "already reported" flag is needed, and the discretion line asks for the row to hold the time only.
  **Issue:** #82
- **Decision:** An unexpected argument is refused with a usage message that never repeats what was typed. The wrapper `docker/n8tracks` accepts only `reset-password`; with no argument it refuses, so it can never start a second server. Run as root, it drops to PUID:PGID with `setpriv` and no supplementary groups, like the entrypoint; run as any other user, it runs the command as that user.
  **Why:** The likeliest mistake is `n8tracks reset-password <the password>`, and echoing that argument would print the password, which the AC forbids.
  **Issue:** #82
- **Decision:** `Program.RunAsync` gained an overload that takes a `CommandConsole` (stdin, stderr, is-terminal, and an unechoed line read). `Main` passes `SystemCommandConsole`, which reads key by key with `TreatControlCAsInput`, so Ctrl-C and Ctrl-D at the prompt exit 1. The old four-argument overload passes a console with no input. The allow-list line in `EnvironmentReadGuardTests` for `Main` was updated to the new call.
  **Why:** The tests run the command in-process, through the binary's entry point, with typed or piped input. Stdout stays empty: the command writes nothing to `output`.
  **Issue:** #82
- **Decision:** Case-insensitive name uniqueness is enforced by the database: a new migration `AddCredentialNameKey` adds `credentials.name_key` (trimmed, NFC, `ToUpperInvariant`, as `administrators.username_key`) and a partial unique index `ix_credentials_name_key WHERE revoked_utc IS NULL`, so a revoked credential's name is free again. Rows from before the migration are back-filled with SQLite's `upper(trim(name))`.
  **Why:** The discretion line asks for names unique ignoring case among non-revoked credentials, and #55 left this index for this story. A column plus an index on an existing table (the story's own table, unreleased) is the smallest schema change that makes the rule hold under concurrent writes; not a Rule 4 change because the story requires the constraint.
  **Issue:** #56
- **Decision:** Rename is `PATCH /api/v1/credentials/{id}` with `If-Match: "<revision>"` per the conventions (no header 428 `revision_required`, malformed 400 `invalid_revision`, stale 409 `revision_conflict` with `current`); the check and write are one conditional `UPDATE`. A revoked credential is 409 `credential_revoked` with `current`; a taken name is 422 `validation_failed` on `name`. Revoke (`POST .../revoke`, no revision) also raises the revision and is idempotent: revoking again answers 200 with the first revocation time. Every response that carries a credential sets `ETag` to its revision. The small `If-Match` reader is `src/n8Tracks.Api/Problems/Revisions.cs`, for later editable records to reuse.
  **Why:** First editable record with a revision; the conventions fix the codes except the revoked-rename case, for which a 409 with the current record lets the client show why.
  **Issue:** #56
- **Decision:** Names are validated like usernames: trimmed, 1 to 100 code points, printable only (no control, line or paragraph separators, unpaired surrogates). `AdministratorRules.TryNormalise`/`IsPrintable` became `internal` to share them.
  **Why:** Rule 2: the name key normalises the name, and `string.Normalize` throws on an unpaired surrogate, which JSON can carry; without the check such a name was a 500.
  **Issue:** #56
- **Decision:** `GET /api/v1/credentials` lists credentials in force first, newest first, then revoked ones; `?kind=` filters (an unknown kind is 400 `invalid_request`). The page filters the loaded list by kind on the client with a segmented control.
  **Why:** "The kind ... filters the list": the API filter serves non-browser callers; the page already holds every credential, so filtering locally needs no refetch.
  **Issue:** #56
- **Decision:** Dates on the Credentials page are shown in the configured time zone, read once from the health report's `timeZone` (`web/src/api/timeZone.ts`, `useConfiguredTimeZone`), falling back to the browser's zone, formatted with the browser's locale.
  **Why:** The conventions say times are shown in the configured time zone; nothing in the UI read it yet, and the health report already carries it.
  **Issue:** #56
- **Decision:** The e2e Demo step 3 calls `GET /api/v1/jobs` with the token from a cookie-less client and expects 404 `not_found` (the API's fallback, which any valid token reaches) before revocation and 401 `invalid_token` after. #57 adds the jobs endpoint and should change the expected 404 to 200.
  **Why:** No `catalog.read` endpoint exists yet; the fallback still proves the token authenticates (an invalid token gets 401 there).
  **Issue:** #56
- **Decision:** The modal accessibility helper moved from `e2e/tests/account.spec.ts` to `e2e/support/a11y.ts` as `expectModalAccessibleInBothSchemes`; every Mantine `Modal` on the Credentials page gives its close button `aria-label="Close"` (axe `button-name` failed without it).
  **Why:** Two specs need it; the close button has no text of its own.
  **Issue:** #56
- **Decision:** The job model, the ports (`IJobStore`), `IJobQueue`, `IJobHandler`, `IJobContext`, `JobService`, `JobQueue`, and the in-process `JobSignal` live in `src/n8Tracks.Application/Jobs/`. The worker (`src/n8Tracks.Infrastructure/Jobs/JobWorker.cs`), its `JobContext`, and `JobScrubber` live in Infrastructure, because scrubbing reuses `RedactionPolicy`, which is in Infrastructure. Infrastructure gained `Microsoft.Extensions.Hosting.Abstractions` 10.0.12 (the latest 10.0.x on nuget.org, the same version as the other Microsoft.Extensions packages) for `BackgroundService`. The worker is added by `AddJobWorker()`, which only `Program` calls. `CommandServices` calls only `AddInfrastructure`, so a command run in the container starts nothing.
  **Why:** These are the artifact paths the story names, and the layering guard holds. `CommandServices` promises "nothing that starts on its own".
  **Issue:** #57
- **Decision:** Enqueue order is a `jobs.sequence` column with a unique index. On insert it is set to one more than the current maximum, inside an IMMEDIATE transaction. The worker claims the lowest queued sequence, and the list shows the highest first. Handlers are keyed scoped DI services, registered with `services.AddJobHandler<T>(type)`. Enqueue checks `IServiceProviderIsKeyedService` and throws `ArgumentException` for an unknown type. It stores the row through a DI scope of its own, so the row is committed in its own transaction whatever the caller has pending.
  **Why:** Two other orderings were possible, and neither keeps enqueue order. `created_utc` and UUIDv7 have millisecond resolution and are random within a millisecond, so three jobs enqueued together could run out of order. SQLite's rowid can be renumbered by VACUUM on a table with a TEXT primary key. The story asks for a new table, so the extra column is not a Rule 4 change.
  **Issue:** #57
- **Decision:** `IJobContext.Report(progress, message)` only records the latest report in memory. While the handler runs, the worker writes the pending report at most once per second, then writes the final state when the job ends. A null message keeps the previous one. A progress value outside 0–100 throws, which fails the job. A job that succeeds is stored at progress 100 and keeps its last message. Messages are scrubbed and cut to 500 characters. An error is `<exception full type name>: <scrubbed message>`, cut to 1000 characters. A result has every string scrubbed, and the value of every property whose name is sensitive is replaced by `[REDACTED]`. A JSON null stays null.
  **Why:** This follows the discretion lines on throttling and scrubbing. Handlers never wait on the database to report progress. Without a cap, the "short" message and error could be any length.
  **Issue:** #57
- **Decision:** In the worker's `StartAsync`, one UPDATE marks every `running` row failed, with `error` "interrupted by restart". This runs before the first claim. A handler that honours the stop request during a graceful shutdown is marked failed at once with the same `error` text. If the job is still running after the 30-second grace, the worker writes nothing more for it, and the next start marks it. The host's `ShutdownTimeout` is raised to 40 s so that the grace is not cut short. `docker-compose.example.yml` gains `stop_grace_period: 45s`, and the README's `docker run` gains `--stop-timeout 45`. A queued job whose type has no handler fails with `error` "unknown job type". Finished jobs are pruned when the worker starts and every 24 h after that, 30 days after they finished.
  **Why:** These are the AC and the discretion lines ("fixed failure texts go in error"). Docker's default stop timeout is 10 s, which would kill the process before the 30-second grace the AC promises.
  **Issue:** #57
- **Decision:** `GET /api/v1/jobs` (the 50 most recently enqueued jobs) and `GET /api/v1/jobs/{id}` both require `catalog.read`. Their answers are `no-store`. A session's answer includes `result`, which is null until the job succeeds. A bearer caller's answer has no `result` property. No answer ever includes `payload`. An unknown or pruned job is 404 `not_found`. `payload` was added to the redaction policy's sensitive names.
  **Why:** These are the AC and the discretion lines. The conventions require that a new field which may hold prompts, lyrics, or tokens join the name list in the same story. A payload can hold anything its enqueuer passed.
  **Issue:** #57
- **Decision:** The story adds no new e2e spec, because its Demo is "none — agent-verifiable". `e2e/tests/credentials.spec.ts` now expects `GET /api/v1/jobs` with a `catalog.read` token to answer 200, where it expected 404 before this story.
  **Why:** #56 left this change for this story. The behaviour is covered by API integration tests that use a test-only job type.
  **Issue:** #57
- **Decision:** #58 is the API and domain half of "Create and list Songs". The ACs about the New Song dialog opening the Song, and about truncating a long concept in the table, are left for #59, as are the component tests and the UI Demo walk. The e2e spec `e2e/tests/songs-api.spec.ts` walks the Demo's data through the API, signed in, and has no accessibility scans because it visits no new screen. It reads the shortcodes it is given instead of expecting `n8-1`, because the e2e containers are shared between specs and retries.
  **Why:** The story's last discretion line moves these criteria and test-plan lines to #59 ("The Songs table, New Song dialog, and Song page"). That story's own ACs repeat them.
  **Issue:** #58
- **Decision:** The domain types are in `src/n8Tracks.Domain/Songs/`: `Song` (whose `Create` is the creation rule), `SongVersion`, `VersionVisibility`, `WorkflowState`, `DefaultWorkflowStates`, `StateColours`, `SongRules`, `Shortcodes`, and `VersionNumbers`. The Version entity is named `SongVersion`, not `Version`.
  **Why:** A type called `Version` would collide with `System.Version`, which implicit usings bring into every file.
  **Issue:** #58
- **Decision:** The schema is one migration, `AddSongs`. It adds four tables. `workflow_states` has `name_key`, which is unique and NFC upper-cased, and `position`, which is unique; it is seeded with the seven states using fixed UUIDv7 IDs. `shortcode_sequence` has one row, with `slot = 1` and `last_value`. `songs` has `shortcode_number` (unique), `title_sort_key`, `current_version_id` (nullable), and restrict foreign keys. `versions` has `number`, `number_sort_key`, `name`, `notes`, `visibility`, `lyrics`, `styles`, the times, and `revision`, and is unique on `(song_id, number)`. The creating transaction writes the Song, then Version 1, then the current-version pointer. That is why `songs.current_version_id` is nullable: it is null only inside that transaction. Creation runs in the existing `IExclusiveTransaction` (BEGIN IMMEDIATE), which also takes the next number from `shortcode_sequence`.
  **Why:** The tables are the ones the discretion line names. The sort keys support the case-insensitive title sort with the shortcode tie-break, and #61's tree order (parts padded to 10 digits). The Song and Version point at each other, and EF Core cannot defer a foreign key check on SQLite, so one of the two pointers has to be nullable.
  **Issue:** #58
- **Decision:** Two SQLite triggers on `versions`, one after INSERT and one after UPDATE, set `songs.updated_utc = max(updated_utc, NEW.updated_utc)`. The Song's revision is left alone. The entity declares them with `HasTrigger`.
  **Why:** AC "last-updated moves when the Song or any of its Versions is changed" has to hold for every future Version write path, which later stories (#62–#66, #69, #111) add, without each of them having to remember it. The rule stays in one place, and it is tested now by writing directly to a Version row. Risk: an EF Core table rebuild of `versions` drops the triggers. `SongEndpointTests.ChangingAVersionMovesItsSongsUpdatedTimeButNotItsRevision` catches that.
  **Issue:** #58
- **Decision:** The API behaves as follows.
  - `POST /api/v1/songs` needs `songs.write` and answers 201 with `Location` and `ETag`.
  - `GET /api/v1/songs` and `GET /api/v1/songs/{reference}` need `catalog.read`. A reference is a hyphenated ID in any case, or `n8-<n>` in any case. Anything else is 404 `not_found`.
  - `GET /api/v1/workflow-states` needs `catalog.read` and answers `{ "items": [...] }`, an object rather than a bare array, so #67 can add the workflow `revision` without breaking the contract.
  - The list defaults to `sort=updated` newest first; `sort=title` defaults to A to Z. Ties are broken by shortcode number, in the same direction. Repeated `state` values are de-duplicated. A repeated `sort`, `direction`, `page`, or `pageSize` is 400 `invalid_request`, and so is a parameter value with a sign, a decimal, or the wrong case.
  - Every answer is `no-store`.
  **Why:** These are the discretion lines, with #59's default view (newest first, titles ignoring case, shortcode as tie-breaker). The other choices are the strictest readings of "an unknown sort or an out-of-range value is 400".
  **Issue:** #58
- **Decision:** These are the title and concept rules. Lengths count UTF-16 units after trimming. A title refuses any control character, line breaks included, and any unpaired surrogate. A concept has its line endings converted to `\n` before it is trimmed and counted. It may contain line breaks but no other control character, tabs included, and no unpaired surrogate. A concept that is only whitespace is stored as null. The state palette has twelve names: gray, red, pink, grape, violet, indigo, blue, cyan, teal, green, yellow, and orange. Each colour has a light and a dark hex value, and a test checks 4.5:1 text contrast on #ffffff and #f8f9fa for light and on #242424 and #2e2e2e for dark. The defaults are Idea yellow, Writing blue, Generating violet, Refining orange, Final green, Released teal, and Archived gray. All seven states are visible. `styles` was added to the redaction policy's sensitive names, because `style` does not match the plural.
  **Why:** Discretion lines: limits count UTF-16 units, and the concept refuses control characters other than line breaks. #59 replaces line breaks in a title client-side, so the API refuses them. Line endings are converted as in #64, so every client stores the same text. The conventions require new lyric and style fields to be redacted.
  **Issue:** #58
- **Decision:** The web mirrors the domain's twelve state colours in `web/src/theme/palette.ts` (`stateColours`) instead of the API serving hex values. The theme emits one `--n8-state-<name>` CSS variable per colour and scheme, and `StateBadge` draws the state's name in it as an outline badge. `WebStateColoursTests` (Architecture.Tests) reads `palette.ts` and fails when it differs from `StateColours`; the web palette test checks 4.5:1 text contrast on the shell's own page backgrounds.
  **Why:** #58 left the choice open. The palette is fixed by the domain (states store only a name), so a copy guarded by a drift test keeps the workflow-states contract unchanged and switches colour with the scheme in CSS without a re-render. Vitest cannot import files outside `web/` (Vite denies the path), so the drift check lives in .NET.
  **Issue:** #59
- **Decision:** The table view is the list API's own query parameters in the page URL (`sort`, `direction`, repeated `state`, `page`), with defaults left out; unknown values in a hand-edited URL fall back to the default. Sorting a column starts it in its default direction (Updated newest first, Title A to Z) and a second click reverses it; changing sort or filter goes back to page 1. The state filter is a group of chips (checkboxes) over every state, hidden ones included, with "Show every state" to clear it. Page size is the API default (50), with Mantine `Pagination` given labelled controls. A Song page link carries the list's query string in router state, so "← Songs" returns to the same view; browser Back and reload restore it from the URL.
  **Why:** Discretion line: sort, filter, and page live in the URL. Reusing the API's parameter names keeps one mapping. Chips are native checkboxes, so they pass axe without extra ARIA.
  **Issue:** #59
- **Decision:** The full concept and the absolute updated time show in a Mantine `Tooltip` on hover and keyboard focus. The target is a `<span tabIndex={0}>`, carrying the project's first `eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex`, with its rationale. The relative time also has the absolute time as visually hidden text for screen readers. The concept cell is cut to one line with CSS (`nowrap`, `ellipsis`), and line breaks in the concept are shown as spaces there.
  **Why:** The AC requires the text on focus as well as hover, which is the WAI tooltip pattern and needs a focusable target. A visible, rationale-carrying suppression is preferred to hiding the same thing behind a Mantine component that jsx-a11y does not inspect. Mantine's tooltip opens only on focus-visible, so the e2e reaches it by pressing Tab, as a keyboard user does.
  **Issue:** #59
- **Decision:** The New Song dialog checks the title (required; at most 300 after trimming) and the concept (at most 2,000 after line endings become `\n` and trimming) as `SongRules` does before sending, and shows the API's 422 field errors the same way. A title has line breaks turned into spaces on paste and on change. The concept is a plain `Textarea` (5 rows, resizable vertically), not `autosize`: Mantine's autosize textarea throws in jsdom. Any other failure keeps the dialog open with the typed text and a notice. On success the dialog navigates to `/songs/<shortcode>`. With no Songs, the empty state holds the only New Song button.
  **Why:** Discretion lines (single-line title with line breaks as spaces, multi-line concept), plus the Test plan complement (a failed create leaves the dialog open with the typed text). Client checks match the server's rules, so a refusal never costs a shortcode, although the server would not consume one anyway.
  **Issue:** #59
- **Decision:** The Demo's e2e walk (`e2e/tests/songs.spec.ts`) runs on its own fresh container (`@root-only`), like `account.spec.ts`. That way the empty state and `n8-1` are real. A second test on the shared containers (root and sub-path) creates a Song through the dialog, reloads its `/songs/<shortcode>` page, and opens a missing shortcode.
  **Why:** The shared containers already hold Songs from other specs, so the Demo's first two steps can only be shown on a fresh one. The sub-path test covers the new nested route under a base path.
  **Issue:** #59
- **Decision:** Rule 1: `AppShell.test.tsx` answered every request with one shared `Response` (`mockResolvedValue`). Once the Songs page made its own requests, that shared body was read twice and the test failed. Each request now gets a fresh `Response`.
  **Why:** That fixture was a test bug, and this story exposed it. No product code was involved.
  **Issue:** #59
- **Decision:** #58's two UI halves are done here. New Song opens the new Song (#58 AC 1). The table cuts a long concept to one line and shows the full text on hover and focus (#58 AC 8). Both boxes on #58 are ticked, and #58 links to this commit.
  **Why:** #58's discretion line moved them to this story, as the orchestrator's brief says.
  **Issue:** #59, #58
- **Decision:** PRD deviation, as the planner recorded it. The PRD says a conflict response identifies the conflicting fields. Instead, the 409 `revision_conflict` carries `current`, the full current Song, and the web client works out which fields differ. It compares the values it loaded with `current`, one field at a time, over title, concept, and state.
  **Why:** The server does not know what the client last saw, so only the client can tell which fields changed since then. This follows the story's discretion line and the project conventions.
  **Issue:** #60
- **Decision:** `PATCH /api/v1/songs/{id:guid}` takes only a Song ID, while GET also takes a shortcode. A PATCH to `/songs/n8-1` falls through to the API's 404. The body is a partial merge. Each field binds as raw `JsonElement`, so a missing field (left as it is) and `null` (concept: cleared; title or state: refused) are told apart. A field of the wrong JSON type is 422 `validation_failed` for that field. The order of checks is: If-Match (428/400), then fields (422), then not found (404), then stale revision (409), then the no-op check. A stale request that also changes nothing still gets 409.
  **Why:** The AC names `{id}`, and the web client always holds the ID. System.Text.Json reads a missing field and `null` the same way for a plain `string?`. Checking the revision before the no-op test gives one rule: a stale revision always conflicts.
  **Issue:** #60
- **Decision:** `SongService.UpdateAsync` runs inside `IExclusiveTransaction`. That one transaction validates the state ID, reads the Song, compares the revision, checks for a no-op, and then does the conditional `ExecuteUpdateAsync` (`ISongStore.TryUpdateAsync`, `WHERE id AND revision`). The edit sets `updated_utc` to now and raises `revision` by one. Any existing state is accepted, hidden ones included.
  **Why:** One transaction means the chosen state still exists when the Song moves into it. The conditional statement keeps the revision check atomic, as `CredentialStore.TryRenameAsync` does. Accepting hidden states follows the Test plan: the API allows a hidden state, and the menu simply does not offer one.
  **Issue:** #60
- **Decision:** The shared save path has three parts. `web/src/api/saves.ts` holds `patchWithRevision`, which sends If-Match and maps the answer to saved, conflict, invalid, or failed. It also holds `differingFields` and `createSaveQueue`. `web/src/common/useRevisionedSave.tsx` keeps a per-record queue and the latest record, and routes conflicts to `web/src/common/ConflictDialog.tsx`. The dialog is a table with Field, Yours, and Current columns, and a note on each row: changed elsewhere, your change, or both. Its buttons are Keep editing, Reload, and Reapply. The Reapply button says "Reapply my change, replacing the current <field>" when both sides changed that field. On a conflict, the page takes the current record straight away, for every choice. When no compared field differs, the save is retried silently, at most 5 times in a row; after that it fails.
  **Why:** The discretion line says the conflict dialog and save helper are written once here and reused by every later editing story. Taking the current record at once is what both Reload and Keep editing ("the fresh revision loaded") need. The retry cap prevents an endless loop if a server misbehaves.
  **Issue:** #60
- **Decision:** In-place editing has no Save buttons. An "Edit" button (`aria-label` "Edit title" / "Edit concept") opens the field and focuses it through a callback ref, not `autoFocus`, so no lint suppression is needed. The title saves on Enter or blur. The concept saves on blur or Ctrl/Cmd+Enter, and Enter adds a new line. Escape cancels. A blur while a save is in flight is ignored, because the conflict dialog takes focus. The state is a Mantine `Menu` whose button is named "State: <name>". It offers the visible states, plus the current one while that one is hidden. It has `hideDetached={false}`, because in jsdom the default closes the dropdown as soon as it opens.
  **Why:** These follow the discretion lines (blur/Enter, Escape, Ctrl/Cmd+Enter) and AC 9. An explicit Edit button keeps editing reachable from the keyboard and lets the title stay a real h2.
  **Issue:** #60
- **Decision:** The Demo's e2e walk (`e2e/tests/song-edit.spec.ts`) runs on the shared containers, at the root and under the sub-path. It uses two pages of one browser context as the two tabs, on a Song of its own created through the API. It scans with axe the page in light and dark, the open title field, the open state menu, the conflict dialog in both schemes, and the title error.
  **Why:** The Demo does not need an empty catalog. Running in both projects covers the PATCH under a base path.
  **Issue:** #60
- **Decision:** Every assigned number is recorded in the new `used_version_numbers` table (key `(song_id, number)`, which is the unique index on Song plus number; cascade-deleted only with its Song) by an `AFTER INSERT` trigger on `versions`, not by an explicit write in `SongStore.AddAsync`. The migration backfills a row for every existing Version. A second trigger (`BEFORE UPDATE OF number, song_id`) aborts any change to a Version's number or Song.
  **Why:** The planner said to update Song creation to write the row. A trigger does that for Song creation and for every later way of adding a Version (the next story, imports) without each one having to remember. Because the insert into the keyed table fails, the database itself refuses a number used before, even by a Version since removed. Neither trigger needs a rebuild of `versions`. Both are declared with `HasTrigger`, and `DatabaseStartupTests` lists all four `versions` triggers, so a later table rebuild that drops them fails a test.
  **Issue:** #61
- **Decision:** The proposal is the sibling when the number right after the source's is unused (and fits). When one option is left out, because the child is over 64 characters or the sibling's last part is past `int.MaxValue`, the remaining one is the proposal even if it is not the number right after the source's. When both are left out, the answer is 409 `version_number_too_deep`.
  **Why:** The story's AC says how to choose between two options, and a response needs exactly one proposal. The discretion lines say to leave out a too-long or overflowing option. Proposing the only option left is the reading that still lets the user pick something.
  **Issue:** #61
- **Decision:** `VersionNumbering.NextTopLevel(used)` returns one more than the highest top-level part ever used (`6` after `1`, `2`, `5.3`), `1` when nothing was ever used, and null when the top level is exhausted. Gaps below the highest part are never filled.
  **Why:** The user's discretion line says "next never-used top-level number... after Versions up to 5.x, it is 6". Filling a gap (for example `3` after `1`, `2`, `5`) would give the new Version a number lower than numbers that came before it.
  **Issue:** #61
- **Decision:** `VersionNumber` is a sealed class: an immutable array of parts, with value equality and tree ordering (a parent sorts before its children). `VersionNumbers.SortKey` now delegates to it, so a part above `int.MaxValue` or text over 64 characters, which the old 10-digit check accepted, now throws. The endpoint route is `/api/v1/versions/{id:guid}/next-numbers`, by ID only; anything else falls through to the 404 fallback. The service is a new `VersionService` over a new `IVersionStore` port.
  **Why:** One parser holds the rules. A guid-only route matches the Song PATCH route. Shortcode lookup for Versions is #68's job. The next story's Version creation belongs with `VersionService`.
  **Issue:** #61
- **Decision:** Added `e2e/tests/versions-api.spec.ts`. It is an API check against the real image, in both projects: Version 1 of a new Song offers `2` (proposed) and `1.1`, and an unknown Version is 404.
  **Why:** The Demo is "none — agent-verifiable", so there is no screen to walk. Even so, an e2e check confirms that the migration and endpoint work in the shipped image, including under the sub-path.
  **Issue:** #61
- **Decision:** #62 takes only the branch-and-switch criteria. Archive and unarchive, name and notes editing, the keyboard tree (ARIA tree pattern), the per-node actions menu, and the persisted "Show archived" preference are left to #63. The e2e Demo walk covers steps 1–3 and 5 and leaves out step 4 (archive), because #62 has no way to archive. #62 still draws archived Versions: they are hidden until "Show archived" is on, then shown dimmed (secondary text colour, italic, "(archived)"). The current Version is always drawn. A link to an archived Version turns the toggle on. Component tests cover these cases with fixture data.
  **Why:** The last discretion line moves those criteria and their test-plan lines to #63. AC 7 (an archived link turns "Show archived" on) stays here, so the toggle and the dimmed drawing had to exist now.
  **Issue:** #62
- **Decision:** The API has three new endpoints in `VersionsEndpoints`. `GET /api/v1/songs/{reference}/versions` (`catalog.read`) returns a flat `{items:[...]}` in tree order. Each item has `id, songId, number, shortcode, name, notes, archived, current, createdAt, updatedAt, revision` and no lyrics or styles. `POST /api/v1/songs/{id:guid}/versions` (`versions.write`) takes `{sourceVersionId, number, name?}` and answers 201 with the Version and no Location, because no single-Version GET exists yet. `PUT /api/v1/songs/{reference}/current-version` (`versions.write`) takes `{versionId}` and answers 200 with the Song.
  **Why:** The paths and scopes come from the discretion lines. The tree does not need creation inputs, and they can be large, so the list leaves them out. The editor story (#64) can add a single-Version read.
  **Issue:** #62
- **Decision:** A refused number is answered as 409 `version_number_taken` when it is now used and would have been one of the source's options: a later sibling or a child of the source. Every other number that is not an option is answered as 422 `version_number_not_offered`, including the source's own number and text that does not parse. Both answers carry fresh `options`. A missing number or source, a source from another Song, or a bad name is 422 `validation_failed`. The number check runs inside `IExclusiveTransaction`, so no create can slip between the check and the insert. The used-numbers row is written by the existing trigger.
  **Why:** The discretion line names the two codes. The server cannot know which options a client saw, so "taken meanwhile" is read as "used now, and shaped like an option".
  **Issue:** #62
- **Decision:** Making a Version current, whether by create-from or by "Make current", changes `songs.current_version_id` and moves the Song's `updated_utc`. The Song's revision stays the same. Making the already-current Version current again writes nothing. Any Version, archived ones included, can be made current.
  **Why:** The discretion line says set-current neither uses nor changes the Song's revision. A change of working Version is still a change to the Song, so the Songs table's "updated" sort should show it.
  **Issue:** #62
- **Decision:** A Version name may be up to 200 characters after trimming, on one line, with no control characters and no unpaired surrogates. A blank name is stored as none. The rule is `VersionRules.NameErrors`, and the web mirror is `nameError`. `VersionRules.CreateFrom` copies lyrics and styles, never the name or notes, and gives the new Version revision 1 and active visibility.
  **Why:** #63's discretion sets the 200 limit, the trimming, and blank as none. Create-from takes a name now, so the rule had to land here. The one-line and control-character rules match the title rule.
  **Issue:** #62
- **Decision:** In the web, the Song page puts the tree on the left and the selected Version's pane on the right (`SongVersions.tsx`), using a Mantine Grid that stacks on narrow screens. The tree is nested lists of links to `/songs/<shortcode>/v/<number>`, and the selected link has `aria-current="true"`. "Create New Version From <n>" and "Make current" are buttons in the right pane. The page keeps its own copy of the Versions after the first load and updates it from each write's answer, so it does not reload them. An unknown number shows "Version not found" with a link back to the Song. The tree's pure nesting module is `versionNesting.ts`, because `versionTree.ts` would collide with `VersionTree.tsx` on case-insensitive file systems.
  **Why:** The URL scheme and the nearest-ancestor rule come from the discretion lines. #63 will turn the list into an ARIA tree with a per-node actions menu, so the actions only needed to exist somewhere reachable for now.
  **Issue:** #62
- **Decision:** `PATCH /api/v1/versions/{id:guid}` (`versions.write`, `If-Match` revision) takes any of `name`, `notes`, and `archived`. Only the fields sent change. Its answers: 200 with the Version and its ETag; 409 `revision_conflict` with `current`; 428/400 for a missing or malformed revision; 422 `validation_failed` (where `archived` must be `true` or `false`, so `null` is refused); 404. An edit that changes nothing is not written. `VersionStore.TryUpdateAnnotationsAsync` makes the revision check and the write one conditional `ExecuteUpdateAsync`, and it sets only the name, notes, visibility, updated time, and revision. Lyrics, styles, the number, descendants, and the Song's current Version cannot change through it, and the existing trigger moves the Song's updated time. There is no frozen-inputs check, because name and notes are annotations and not creation inputs (AC 1). No freezing exists yet anyway (Generations are M3+).
  **Why:** The key link names this route and the revision. Invariant 1 protects creation inputs and lineage, and this path cannot write either.
  **Issue:** #63
- **Decision:** Version notes are optional. They are trimmed, line endings become `\n`, and blank is stored as none. They may contain line breaks but no other control characters, and no unpaired surrogates. The limit is **10,000 characters** (`VersionRules.NotesMaximumLength`, mirrored in the web as `notesError`).
  **Why:** Neither the PRD nor the story gives a notes limit. Notes are meant for longer explanations than the 2,000-character Song concept, and the cap keeps the field bounded. The rules match the concept's.
  **Issue:** #63
- **Decision:** Archive and Undo send `PATCH {archived}` through `setVersionArchived` in `web/src/api/versions.ts`. When the server answers with a revision conflict, the client retries on the current revision without asking, up to 3 times. If the current record already has the wanted flag, that record is the answer. Name and notes edits go through `useRevisionedSave` and its conflict dialog, like the Song header.
  **Why:** Archiving is a command and has no text to merge. A name or notes edit made elsewhere should not stop it, and should not be overwritten by it either, because the PATCH sends only `archived`.
  **Issue:** #63
- **Decision:** The tree is a `div role="tree"` with `div role="none"` wrappers. Each node is a `div role="treeitem"` that owns its child `div role="group"` through `aria-owns`. Nodes carry `aria-level`, `aria-setsize`, `aria-posinset`, `aria-expanded` on branches, `aria-selected` on the selection, and `aria-current` on the working Version. Each node's accessible name is an `aria-label`, for example "Version 1.1, Guitar experimentation, current working Version, archived". Focus uses a roving tabindex. Up/Down, Home/End, Left/Right (collapse/expand, or move to the parent or first child), and Enter/Space select. Shift+F10 or the context-menu key opens a node's actions menu: Create New Version From, Make current, and Archive/Unarchive. The menu's "⋯" button is the next tab stop after the focused node, and closing the menu returns focus to the node. Nodes are no longer links, so selecting a node navigates in code. e2e and component tests now find nodes by `[role="treeitem"][data-version-number]`.
  **Why:** The discretion line requires the ARIA tree pattern. jsx-a11y strict does not allow `role="tree"` on a `ul`. Keeping the focusable node to the row (not the whole subtree) gives a clean focus ring, using Mantine's `mantine-focus-auto` class. The action button inside the treeitem is allowed, because treeitem children are not presentational.
  **Issue:** #63
- **Decision:** "Show archived" is stored per browser in `localStorage` key `n8tracks-show-archived-versions`; every read and write is guarded, so blocked storage means the default (off). A link to an archived Version still turns the toggle on for that visit only, without storing it, and only when the URL changes to it. When the selected Version becomes hidden (archived in place, or the toggle turned off), the page replaces the URL with the Song's and the current Version is selected. Archive shows an inline `role="status"` notice with Undo and Dismiss; failures show the existing `role="alert"` "Not changed" line. The in-place edit hook moved from `SongHeader.tsx` to `web/src/common/useInPlaceEdit.ts` so `VersionDetails.tsx` reuses it.
  **Why:** The key link says "stored per browser"; the discretion lines fix Undo with no confirmation and the selection rule. Only arrival by URL counts as the link case, so archiving the selected Version in place cannot switch the toggle on.
  **Issue:** #63
- **Decision:** Rule 1: a direct load or reload of a Version link with a dotted number (`/songs/n8-1/v/1.1`) answered 404. The shell skipped every last path segment that contains a dot, treating it as a file. `FrontendHosting.IsShellRequest` now serves the shell when the last segment is a valid `VersionNumber` after a `v` segment, and `/songs/n8-1/v/1.png` is still a 404. Regression cases were added to `FrontendHostingTests` (deep links and the not-found complement).
  **Why:** #62's URL scheme was broken for any non-top-level Version on reload. #63's e2e Demo opens `/v/1.1` directly, which exposed it.
  **Issue:** #63
- **Decision:** Lyrics and styles are written through the same `PATCH /api/v1/versions/{id:guid}` (`versions.write`, `If-Match`), as the AC names it, but on their own path. `VersionService.UpdateAsync` validates them (`VersionRules.LyricsErrors`/`StylesErrors`: text only, `null` refused, U+0000 and unpaired surrogates refused, 5,000/1,000 UTF-16 code units after line-ending conversion) and, when either input changes, writes through the private `StoreAsync` → `IVersionStore.TryUpdateInputsAsync`, the one write of creation inputs after creation. Annotation-only edits still use `TryUpdateAnnotationsAsync`, which cannot touch inputs. No freeze check exists yet: Generations do not exist until #69, which adds `version_frozen` at `StoreAsync`.
  **Why:** #63's note asked for a separate input path rather than widening the annotations write, so #69 has one seam to guard. One edit that changes inputs and annotations still raises the revision once (one conditional statement).
  **Issue:** #64
- **Decision:** Added `GET /api/v1/versions/{id:guid}` (`catalog.read`, ETag = revision) answering the Version with `lyrics` and `styles` (empty strings when none). The PATCH answer and a conflict's `current` now carry `lyrics` and `styles` too (`VersionDetailResponse`, a superset of `VersionResponse`). The Song's Versions list and the create answer stay without them.
  **Why:** The editor needs the stored text, and the conflict dialog needs the current text to compare. The list is drawn for the tree and stays lean. The changes are additive; no existing field changed.
  **Issue:** #64
- **Decision:** Rule 1: `PATCH /api/v1/versions/{id}` answered 500 for a JSON string escaping half a surrogate pair (`"\ud83c"`) in `name`, `notes`, `lyrics`, or `styles`, because `JsonElement.GetString()` throws. It now answers 422 with a field error (`VersionsEndpoints.TryGetString`); regression cases added to `VersionEditEndpointTests` and `VersionInputsEndpointTests`. The same bug on the Song PATCH is out of scope and filed as #274.
  **Why:** The discretion line says unpaired surrogates are a field error.
  **Issue:** #64
- **Decision:** Highlighting and warnings come from one hand-written per-line tokeniser (`web/src/editor/lyricsLanguage.ts`) drawn as CodeMirror mark decorations and a custom warning gutter, not a Lezer/StreamLanguage grammar or `@codemirror/lint`. Packages added at the registry's current versions: `@codemirror/state` 6.7.6, `@codemirror/view` 6.43.13, `@codemirror/autocomplete` 6.20.3, `@codemirror/commands` 6.11.1.
  **Why:** The rules are line-local and need exact delimiter offsets for warnings, which a tiny tokeniser gives directly and unit-tests easily. `@codemirror/lint`'s gutter markers cannot take keyboard focus (the discretion line wants the explanation on hover and focus of the marker) and its checks are debounced. The planner's line named "its autocomplete and lint extensions"; autocomplete is used as planned, lint is replaced.
  **Issue:** #64
- **Decision:** Warning markers are `<button>`s in the gutter, labelled "Warning, line N: …", with the explanation shown on hover and focus and clicking moving the cursor to the problem; CodeMirror's `aria-hidden` on `.cm-gutters` is removed so they are reachable by assistive technology. The warnings are also listed below the editor (in the editor's `aria-describedby`) under a `role="status"` count line that announces changes. Tags are bold and coloured, parentheticals italic and coloured, warnings wavy-underlined; the three colours are in the palette (`lyrics.*`) and contrast-tested in light and dark. The tag list never scrolls (13 tags), so axe's scrollable-region rule holds.
  **Why:** "Usable by keyboard and screen reader", "warnings are announced", and "never relies on colour alone" (AC 7).
  **Issue:** #64
- **Decision:** Escape then Tab leaves the editor via an explicit `setTabFocusMode(2000)` on every Escape keydown. Tab otherwise inserts a tab character.
  **Why:** CodeMirror's built-in Escape-then-Tab only runs when no key binding handled the Escape, and in practice (with the completion keymap) the e2e walk showed Tab inserting a tab after Escape.
  **Issue:** #64
- **Decision:** One "Save lyrics and styles" button (and Ctrl/Cmd+S anywhere in the panel) sends only the inputs that changed, in one PATCH, through `useRevisionedSave`, which now has `saveFields(edit)` for several fields at once (`send(base, edit)`; `differingFields` takes the edit map; `save(key, value)` is unchanged for callers). The Version pane loads the Version with its lyrics and keeps one record for name, notes, lyrics, and styles, so all edits share one revision queue and the conflict dialog. Save is disabled while nothing changed or a field is over its limit; text over the limit can still be typed or pasted, with a counter and a message.
  **Why:** Discretion lines (one Save, Ctrl/Cmd+S, disabled rules); one record avoids a lyrics save conflicting with the same pane's name edit.
  **Issue:** #64
- **Decision:** The web app now runs on a data router (`createBrowserRouter` in `main.tsx`, `createMemoryRouter` in `renderApp`), with the existing `<Routes>` under one catch-all route, so the editor can use `useBlocker`. Leaving the Version, the Song, or any page with unsaved lyrics or styles (any pathname change) opens "Unsaved lyrics or styles" with Stay, Discard, and Save and continue; `beforeunload` covers closing or reloading the tab.
  **Why:** `useBlocker` only works in a data router; this is the smallest change that keeps every route where it was.
  **Issue:** #64
- **Decision:** `styles` was already on the redaction list (added ahead of this story with `style`); this story adds a guard test that a Version PATCH carrying lyrics and styles sentinels, saved and refused, never puts either in the log at Debug.
  **Why:** AC "styles is added to the log redaction name list in this story" was already true; the test proves the new field path is covered.
  **Issue:** #64
- **Decision:** #65 delivers autosave only. Snapshots, comparison, restore, and `EditorRevisionService`/`HistoryPanel.tsx` (named in the story's must-haves) are left to #66.
  **Why:** The story's last Discretion line moves every editing-history criterion, test-plan line, and must-have to the sibling story "Editing history: snapshots, comparison, and restore" (#66).
  **Issue:** #65
- **Decision:** Autosave lives in `web/src/editor/useAutosave.ts` over the existing `useRevisionedSave.saveFields`: a 1.5 s debounce, one combined PATCH of every field (name, notes, lyrics, styles) whose draft differs from the stored Version, and one save in flight at a time; what is typed meanwhile is sent when it returns, on its revision. A new keystroke while retrying re-arms the 1.5 s pause but keeps the backoff count; a success resets it.
  **Why:** Discretion lines (debounce, one queue per Version, no self-conflicts); reusing the shared helper keeps the conflict dialog and the silent same-value retry.
  **Issue:** #65
- **Decision:** A failed save now says why: `SaveResult`/`SaveOutcome` `failed` carries an optional `reason` (`unreachable`, `server`, `signed-out`, `frozen`, `gone`, `refused`). No answer, a 5xx, 408, 429, or an unreadable success are retried without limit (2, 4, 8, 16, 30, 30… s); 401 (sign-in prompt dismissed), 404, 409 `version_frozen`, any other 4xx, and 422 stop autosave with a message saying what to do. A stop clears on the next edit or the indicator's "Try again". `version_frozen` is assumed from #69's planned code.
  **Why:** AC 4. An optional field keeps every other caller of `patchWithRevision` unchanged.
  **Issue:** #65
- **Decision:** Name and notes are now always-editable fields that autosave like the lyrics (the #63 Edit buttons, Enter/blur saving, and Escape cancelling are gone). The explicit "Save lyrics and styles" button is removed; Ctrl/Cmd+S stays as "save now" (flush) so the browser's save-page dialog never opens over the editor.
  **Why:** AC 1 covers name and notes; an Escape that "cancels" cannot undo text already autosaved. The Discretion line removes the Save action; keeping the shortcut as a flush costs nothing and avoids a surprise.
  **Issue:** #65
- **Decision:** Leaving inside the app (another page, Song, or Version) while anything is not stored first saves it at once and goes on if that works; only a save that does not go through (or a paused conflict or a refusal) opens "Your changes are not saved" with Stay or Leave without saving. Closing or reloading the tab uses `beforeunload` whenever anything is not stored, and the keepalive PATCH (anti-forgery header, last known revision) is sent on `pagehide`, not `beforeunload`.
  **Why:** Discretion lines (leave covers navigation, Version switches, tab close; keepalive with anti-forgery header, failure accepted). Sending on `pagehide` means a user who chooses to stay never races their own keepalive save into a self-conflict.
  **Issue:** #65
- **Decision:** A conflict left undecided ("Keep editing", Escape) pauses autosave; the indicator then says the Version changed elsewhere and offers Reload and "Reapply my change". Typing on does not resume it.
  **Why:** AC 3: autosave stops on a conflict; after "Keep editing" the record has already moved to the current revision, so resuming silently would overwrite the other change without a choice.
  **Issue:** #65
- **Decision:** Rule 1: when the stored Version moves (a save returns, a conflict is reapplied, the tree archives it), a field whose draft the user has not changed takes the new value; fields of the save in flight never do.
  **Why:** Without it, reapplying over a change made elsewhere left the other field's old value in its draft, and the next autosave reverted the other client's change; and a field typed back to its old value during a save would be overwritten by the save's answer. Regression test: "sends what was typed during a save after it returns, even typing back to the old text".
  **Issue:** #65
- **Decision:** The e2e walk plays "stop the server" (Demo step 5) by refusing every Version PATCH in the browser (`page.route` abort), not by stopping a container; Playwright's clock is not used (it is for #66's 30-second snapshot pause).
  **Why:** The shared containers serve every other spec; a browser-side refusal is the same "no answer" the client sees from a stopped server.
  **Issue:** #65
- **Decision:** The Song-page component tests' `openSong` now waits for the Version pane to finish loading before clicking.
  **Why:** With the pane drawing more fields, clicks sometimes landed on the loading pane's buttons as they were replaced (a flake seen 3 times in 12 runs; 0 in 10 after).
  **Issue:** #65
- **Decision:** `editor_revisions` has a `sequence` column (unique, max+1 inside the exclusive transaction, as `jobs` does) alongside the planned Version, lyrics, styles, and created time. "Newest" means the latest `created_utc`, then the highest `sequence`.
  **Why:** Two snapshots of one Version can share a millisecond (a tool's edit right after an editor snapshot), and UUIDv7 IDs minted in the same millisecond do not sort reliably. Without a tie-breaker, the duplicate check and pruning compared against the wrong snapshot. The API tests caught this.
  **Issue:** #66
- **Decision:** `editor_revisions.version_id` is a foreign key to `versions` with ON DELETE CASCADE.
  **Why:** A Version's history has no meaning without the Version, and the test helper `SongApi.RemoveDirectly` deletes Versions. M3's deletion work still owns deleting individual snapshots and any audit around it; the cascade only stops orphans.
  **Issue:** #66
- **Decision:** Snapshot text has the same limits as a Version's lyrics and styles (5,000 and 1,000 characters, no U+0000), and the editor does not snapshot text that is over a limit. `capturedAt` must be a date and time (`T`, read as UTC when it has no offset), and a time in the future is stored as now.
  **Why:** A snapshot must be restorable, and an over-limit one could not be. A client clock running ahead must not put a snapshot above ones taken later.
  **Issue:** #66
- **Decision:** Before the first change is snapshotted, the editor also snapshots the text the Version had when the pane opened, unless it is empty. The capture time is when the pane opened, and the server drops it when it equals the newest snapshot. After the conflict dialog's Reload, the reloaded text is treated the same way.
  **Why:** Text that reached the Version from elsewhere (a tool's edit, another tab) is otherwise lost from history as soon as the user rewrites it. This supports the must-have "edits made by a tool through the API are recoverable too", and costs nothing when the text is already the newest snapshot.
  **Issue:** #66
- **Decision:** Restore runs in one exclusive transaction in `EditorRevisionService`. It checks that the Version exists, then that the snapshot is the Version's own (both 404), then the revision (409). It snapshots the current text, then writes through `VersionService.StoreInputsAsync`, which is the existing single inputs write, so #69's freeze check also covers restore. Restoring text the Version already holds changes nothing. Credential detection is `CredentialPrincipal.IsCredential`, passed to `VersionService.UpdateAsync` as `VersionEditSource`, and the credential's snapshot is taken in the same transaction as the edit.
  **Why:** Business rules stay in the one application layer (invariant 5). A refused edit or restore must leave nothing behind. One write path keeps invariant 1 enforceable in one place.
  **Issue:** #66
- **Decision:** Web: snapshots that cannot be sent wait in one module-level, in-memory queue shared by every editor on the page, so a leave snapshot outlives its pane. The queue keeps up to 10, dropping the oldest first. It is retried every 15 s and on the browser's `online` event, and a 4xx other than 408/429 is dropped. When the page closes, held snapshots go out as keepalive requests after the keepalive save. Unmounting the Version pane counts as leaving the Version. History is collapsed by default ("Show history"). The comparison runs from the snapshot to the editor's current text, line by line, and marks each changed line with a visible "Added"/"Removed" word and a +/− sign. Restore is off whenever the autosave status is not "Saved", and a separate message says why for a conflict.
  **Why:** These follow the planner's discretion lines (memory only, ten, capture time, confirm, line diff with text labels). Collapsing History by default keeps the list from loading for every Version a user opens. The save goes before the snapshots at page close because the browser limits keepalive payloads.
  **Issue:** #66
- **Decision:** The workflow revision is the `settings` row `workflow`, value `{"revision": n}`, created on the first change. Until then, reads answer revision 1. No migration was needed. Every change to the states reads the list, compares the revision, writes, and raises the revision in one exclusive transaction. A change that changes nothing (same name and colour, same order) is not written and does not raise the revision.
  **Why:** The discretion line says to create the single workflow record at revision 1 if it does not exist, and the `settings` table already exists for instance-wide JSON values. Creating the row on the first change avoids a data migration and gives the same answers.
  **Issue:** #67
- **Decision:** `WorkflowStateService` stays in `src/n8Tracks.Application/Songs/` (namespace `n8Tracks.Application.Songs`), where #58 created it. The story's artifact path `Application/Workflow/WorkflowStateService.cs` was not used. The store moved into its own file, `Infrastructure/Persistence/WorkflowStateStore.cs`, and the endpoints into `Api/Endpoints/WorkflowStatesEndpoints.cs`. GET `/workflow-states` moved there from `SongsEndpoints`.
  **Why:** Moving the service would change namespaces in `SongService`, the endpoints, and the tests for no behavioural gain.
  **Issue:** #67
- **Decision:** API shapes. Every change answers the whole list `{revision, items:[{id,name,colour,order,hidden,songCount}]}`, with the revision as the ETag; POST answers 201 with no Location. POST takes `name` and an optional `colour`. PATCH fields are read as raw JSON, so a non-text name or colour, or a non-boolean hidden, is 422. A reorder whose IDs are not exactly the current states is 409 `order_mismatch` with `current`. A missing `ids` is 422. A replacement that is not a UUID, is the state itself, or names no state is 422 on `replacement`. Checks run in this order: field shape, then revision (409 `revision_conflict`), not found (404), name taken (422), last visible (409 `last_visible_state`), and in use without a replacement (409 `state_in_use` with `songCount`). After a delete, positions are renumbered 1..n.
  **Why:** The discretion lines require a 409 for a mismatched reorder but give no code. A distinct code lets a client tell a bug from a stale view. Answering the whole list after every change keeps the client copy and the revision exact without a refetch.
  **Issue:** #67
- **Decision:** Web: Settings → Workflow is a list of rows. Each row has Move up and Move down, Edit (a dialog for name and colour), Hide or Show, and Delete. Dragging uses native HTML5 drag and drop, with no new dependency. The `<li>` drag handlers carry a `jsx-a11y/no-noninteractive-element-interactions` suppression with a rationale, because the Move buttons are the keyboard path. After a keyboard move, focus goes back to the same button, or to the other one when the state reaches an end. The last visible state's Hide and Delete are disabled, with a line explaining why. The replacement is a native select, and hidden states are offered with "(hidden)". The Songs filter shows visible states, hidden states with Songs, and any chosen state.
  **Why:** This follows the discretion lines: Move up and Move down, Undo for hide, immediate empty delete, and a replacement dialog that offers hidden states. Native drag and drop is enough for one list and avoids adding a package.
  **Issue:** #67
- **Decision:** Rule 1: the Song page's state menu label ("Move to") used Mantine's dimmed grey, which fails WCAG AA contrast on the dark dropdown. It now uses the theme's checked secondary-text colour (`Menu.extend` in `web/src/theme/theme.ts`). A regression assertion was added to `SongHeader.test.tsx`. The Demo's axe scan of the open state menu found it.
  **Why:** The Demo walk requires an accessibility scan of the state menu, and this bug from #60 failed that scan.
  **Issue:** #67
- **Decision:** Song counts include every Song in the `songs` table. There is no retention state yet, so the M3 comment's "exclude retained Songs" has nothing to exclude. M3's retention story must filter retained Songs out of `WorkflowStateStore.ListWithUsageAsync` and out of the reassignment in `DeleteAsync`.
  **Why:** The owner's planning update of 2026-10-03 replaced the discretion line about counting and reassigning retained Songs.
  **Issue:** #67
- **Decision:** The typed reference binder is `CatalogReference` (in `src/n8Tracks.Application/References/ReferenceResolver.cs`). It reads a stable ID (hyphenated, any letter case), a Song shortcode, or a Version shortcode. Its `TryParse` always succeeds: text that is not a reference is `Malformed`, so each endpoint answers its own 404 `not_found` instead of the framework's 400. Endpoints resolve the reference to an ID through `ReferenceResolver` and then call the existing services by ID. An ID is passed on without a lookup, so the service's own lookup still decides not-found. Body fields (`sourceVersionId`, `versionId`) are resolved inside `VersionService`'s transaction, and an unknown reference, a reference of the other kind, or a Version of another Song is 422 on that field. The Version-shortcode lookup is the new `IVersionStore.FindIdByShortcodeAsync`; nothing new is stored.
  **Why:** The story's key link asks for "a typed reference binder used by every endpoint that takes a Song or Version". Resolving to an ID at the edge keeps the services and their tests unchanged and keeps the rules in the application layer (invariant 5).
  **Issue:** #68
- **Decision:** Route templates changed from `{id:guid}` to `{reference}`: `PATCH /api/v1/songs/{reference}`, `POST /api/v1/songs/{reference}/versions`, and every `/api/v1/versions/{reference}…` route, the snapshot routes included (`{snapshotId:guid}` stays). The contract only widens, because IDs still work. Three existing tests asserted that a shortcode was refused (`NextNumbersEndpointTests.AnUnknownVersionIs404`, `VersionEditEndpointTests.EditingAMissingVersionIs404`, `VersionEndpointTests.AMissingOrWrongFieldIsRefusedAndNothingIsStored`). They now assert the wrong-kind and unknown-shortcode cases instead.
  **Why:** This is the acceptance criterion "every API parameter that identifies a Song or a Version accepts either its stable ID or its shortcode". It is in the story's scope, so it is not a Rule 4 contract change.
  **Issue:** #68
- **Decision:** `GET /api/v1/resolve/{reference}` answers `{entityType, id, shortcode, status}`, plus `song: {id, shortcode}` for a Version only; the member is left out for a Song rather than sent as null. A stable ID is looked up as a Song first, then as a Version. The API does not trim whitespace. The Go to box and the `/go/` page trim before they resolve.
  **Why:** The discretion line says that `song` is "the last for Versions". Trimming at the API would make a reference with spaces valid in a URL path, which is not a shortcode.
  **Issue:** #68
- **Decision:** The guard is `tests/n8Tracks.Api.Tests/References/ReferenceParameterGuardTests.cs`. It reads every `/api/v1` endpoint's handler and fails if a handler parameter or a request-body field named for a Song or Version is a UUID (other IDs are in an allow-list), or if the first route parameter under `/songs/` or `/versions/` is not bound as `CatalogReference`. It also keeps one call per endpoint that binds a reference, requires that set to equal the enumerated endpoints, and checks that each call answers the same by ID and by mixed-case shortcode, and 404 for the other kind. Query parameters are not read structurally; none name a Song or Version today. The resolver's "unit tests" cover parsing (`ReferenceResolverTests`); lookup is covered through the resolve endpoint, not with fake stores.
  **Why:** A structural check alone cannot prove that a shortcode works, and a call table alone could miss a new endpoint. Requiring the table to equal the enumerated set makes a new reference endpoint fail until it has a call.
  **Issue:** #68
- **Decision:** Web. `ShortcodeBadge` shows the shortcode in lower case with a "Copy" button labelled "Copy shortcode <code>". "Copied" appears in an `aria-live="polite"` status beside it for 2 s. Where there is no clipboard, or the browser refuses it, the shortcode becomes a read-only field, focused, with its text selected, and a message to press Ctrl+C or Cmd+C. `GoToBox` sits in the signed-in header and acts on Enter. Its not-found or failure message is the field's error, drawn as a small panel under the box, because the 56 px header has no room for a line of text. A Version page URL resolves its Song first and then `<song shortcode>-v<number>`, so a URL that names the Song by ID works too. `/go/:reference` replaces its history entry. The Song page's `data-testid="shortcode"` stays, and the Version panel's is `version-shortcode`.
  **Why:** These follow the discretion lines (aria-live "Copied" for two seconds, Enter not paste, clear on success, resolve endpoint only, replace history). Making the message the field's error lets Mantine set `aria-invalid` and `aria-describedby`, which a separate element could not: Mantine overrides `aria-describedby`.
  **Issue:** #68
- **Decision:** Rule 3: `FrontendHosting.IsShellRequest` now serves the shell for any two-segment `/go/<reference>`. Before this, `/go/n8-1-v1.1` was a 404 on reload, because its dots looked like a file extension.
  **Why:** Without it the AC "opening `/go/<shortcode>` in the web UI" fails for every dotted Version number. `FrontendHostingTests.ADeepLinkReturnsTheSameShell` covers it.
  **Issue:** #68
- **Decision:** The freeze rule is inside the entity. `SongVersion` moved to `src/n8Tracks.Domain/Songs/Version.cs`, and every one of its properties is now get-only, so `with` cannot set one. It gained `IsFrozen` and `LastGenerationOrdinal`, as optional positional parameters so existing constructions still compile. Changes go through `WithInputs` (which calls `EnsureMutable()` and throws `VersionFrozenException` unless the inputs are byte-identical), `WithAnnotations`, and `AttachGeneration`. `VersionService.StoreAsync`, the one inputs write (restore included), reads the stored entity and applies `WithInputs` before anything is written or snapshotted. The Frozen outcome is a result, not an exception, at the service boundary.
  **Why:** The must-haves name `Version.cs` and `EnsureMutable()`, and the test plan wants "every input-changing method on a frozen Version throws". Running the check before the snapshot callback means a refused credential edit or restore keeps nothing in history.
  **Issue:** #69
- **Decision:** There are three layers. The entity check is the rule. `VersionStore.TryUpdateInputsAsync` also matches only `NOT is_frozen`. The migration `AddGenerations` adds the trigger `tr_versions_frozen_inputs_never_change`, which refuses changing a frozen row's lyrics or styles, clearing `is_frozen`, or lowering `last_generation_ordinal`, and `tr_generations_identity_never_changes`. Both columns were added with `ALTER TABLE ADD COLUMN`, with no table rebuild, so the earlier `versions` triggers survive (`DatabaseStartupTests` lists all five).
  **Why:** The rule must hold "through any path", and later import and MCP paths may write without the service. The guard asserts 409 `version_frozen` and not just unchanged bytes, so it still fails when the entity check is removed even though the store refuses the write: removing `EnsureMutable()` from `WithInputs` failed 3 of the 5 guard tests (entity: no exception; API: `revision_conflict` instead of `version_frozen`; service: `Conflict` instead of `Frozen`). The check was then restored.
  **Issue:** #69
- **Decision:** Generations get a minimal `generations` table (id, version_id, song_id, ordinal, created_utc; unique on version and ordinal; FKs RESTRICT). Ordinals come from `versions.last_generation_ordinal`, which never goes down, so they are never reused even after a deletion. Attaching a Generation bumps the Version's revision and `updated_utc`. The resolver accepts Generation IDs and `n8-<n>-v<number>-g<ordinal>` (new `ReferenceKind.Generation`) and answers `entityType: "generation"` with `song` and a new `version` member. The web Go to box opens the Generation's Version.
  **Why:** The AC needs ordinals that are never reused and a shortcode the resolver accepts. The schema is sanctioned by the story's discretion line on the Generation table. RESTRICT rather than CASCADE means nothing removes a Version's Generations by accident before M3 decides deletion.
  **Issue:** #69
- **Decision:** `isFrozen` is on `VersionResponse` (the list) as well as `VersionDetailResponse`. 409 `version_frozen` carries `title` "…Create a new Version from it to change them.", `versionId`, and `versionShortcode`. The revision check (428/400/409 `revision_conflict`) comes first. The web `Version` TypeScript type does not read `isFrozen` yet; #70 adds it with the read-only editor.
  **Why:** The tree in #70 may want to mark frozen Versions without loading each detail. Following the discretion lines on the problem body and on ordering.
  **Issue:** #69
- **Decision:** `seed-generation <version shortcode>` is dispatched in `Program.RunAsync` beside `reset-password` and passed through by the `docker/n8tracks` wrapper. Without `ASPNETCORE_ENVIRONMENT=Development` (any letter case, as `IsDevelopment()` reads it) or `N8TRACKS_ENABLE_TEST_SEEDING` set to exactly `1`, it exits 1 with a message and never starts the server. The variable is a known setting (no "unknown setting" warning), is documented in the README as test-only and kept out of the settings table, and is set by `e2e/support/containers.ts` on every e2e container. The wrapper's usage line still shows only `reset-password`.
  **Why:** Following the discretion line. Refusing, rather than ignoring the argument and starting a second server, matters under `docker exec`.
  **Issue:** #69
- **Decision:** The write-path guard (`tests/n8Tracks.Api.Tests/Invariants/VersionImmutabilityGuardTests.cs`) enumerates every unsafe `/api/v1` endpoint and every public method of the public non-record classes in `Application.Songs` and `Application.References`. Each one needs an exerciser or a stated reason, and the set must match exactly. Services in other namespaces may not take a catalog type. Property classification covers both `SongVersion` and the EF model of `VersionRecord` (which adds `NumberSortKey` as a system field), and checks that each creation input's column is compared in the freeze trigger. CLAUDE.md was not edited: its invariant-1 line already names the import and MCP extensions (#122, #140, #254).
  **Why:** Covering every unsafe endpoint, not only Song and Version routes, means a new endpoint anywhere must be classified. The CLAUDE.md wording already says what the test plan asked it to say.
  **Issue:** #69
- **Decision:** `POST /api/v1/songs/{reference}/versions` takes optional `lyrics` and `styles`. A missing or null field copies the source's text, as before, and text replaces it. Both are read as raw JSON, so a non-string or a lone-surrogate escape is 422 on that field, and are checked with the edit rules (`LyricsErrors`/`StylesErrors`). `VersionRules.CreateFrom` and `VersionCreateRequest` take the two as optional trailing parameters. The web carries text into a new Version with one create, not a create followed by a PATCH.
  **Why:** The discretion line describes "a normal create-from of the frozen Version, with lyrics and styles replaced by the carried-over text". One create is atomic, so carried text cannot be left behind by a half-done branch. The change is additive: existing requests behave exactly as before, so it is not a Rule 4 contract change. Invariant 1 is unaffected, because the frozen source is only read; the guard's create exerciser already sends inputs and asserts that the source does not change.
  **Issue:** #70
- **Decision:** The web learns of a freeze in three ways, and all of them carry unsaved text. (1) A 409 `revision_conflict` whose `current.isFrozen` is true, on a save that changes lyrics or styles: `useRevisionedSave` has a new optional `refuses(current, edit)` check that ends such a save as `failed: frozen` without the conflict dialog. (2) A 409 `version_frozen` on the page's own revision: the record is marked frozen locally. (3) A record that becomes frozen any other way. In each case `VersionDetails` takes the unsaved lyrics and styles as "carried" text, snapshots them into History, puts the stored text back in the read-only editor, and saves the rest of the edit (name, notes) as usual. The autosave indicator therefore says "Saved", and the frozen notice explains what happened. `editOf` never sends a frozen Version's inputs.
  **Why:** Test plan: "shows the frozen notice directly (not the ordinary conflict dialog) and offers the unsaved text as the new Version's content". Saving the name and notes typed in the same burst keeps the truth that "text typed just as a Version froze is not lost". The History snapshot is a second copy, in case the user leaves without branching.
  **Issue:** #70
- **Decision:** The notice (`web/src/editor/FrozenNotice.tsx`) sits above the inputs inside a polite `role="status"` region. It is titled "Lyrics and styles are locked" and uses the discretion-line text verbatim. Its action is "Create New Version From <n>". When text was carried, a second line says so, and both that action and the header's same-named button open the branching dialog with that text. The tree's per-node menu still copies the stored text. "Restore into a new Version" opens the same dialog with the snapshot's text and needs no extra confirmation, because the dialog is the confirmation. The CodeMirror editor is read only through a `Compartment` (`EditorState.readOnly` plus `aria-readonly="true"`), so it stays focusable and selectable. Styles uses the native `readonly`. The tree's lock is a `role="img"` 🔒 labelled "Frozen: has a Generation", and the treeitem's name ends with ", frozen".
  **Why:** These follow the AC and the discretion lines: the proposal is preselected, nothing changes until the user confirms, and the new Version becomes current on confirm, as every create does. Read only, rather than disabled, keeps the generated lyrics readable and copyable by keyboard and screen-reader users.
  **Issue:** #70
- **Decision:** An existing component test, `VersionInputs.test.tsx` "stops on a refusal retrying cannot fix…", used a 409 `version_frozen` as its example of a refusal that stops autosave. It now uses a 403. Under #70 a freeze no longer stops autosave: it shows the frozen notice, which `FrozenVersion.test.tsx` covers.
  **Why:** The behaviour that test pinned is the one this story replaces. The test still covers the stopped-refusal path with a refusal that remains one.
  **Issue:** #70
- **Decision:** The online backup copies in steps of 1,024 pages through SQLitePCL's `sqlite3_backup_*` calls on Microsoft.Data.Sqlite's handles, under a deferred read transaction held on the source connection for the whole copy. The copy is switched to `journal_mode = DELETE` afterwards.
  **Why:** Stepping gives the page-count percentage the discretion line asks for. The held read transaction fixes the snapshot under WAL, so writers carry on and the copy is one point in time instead of restarting at each write. `SqliteConnection.BackupDatabase` copies in one step and reports no progress. No new package: SQLitePCLRaw already comes with Microsoft.Data.Sqlite.
  **Issue:** #71
- **Decision:** "Already queued or running" is checked and enqueued under one process-wide `BackupStartLock` (a singleton semaphore) with a new `IJobStore.FindActiveAsync(type)`. Every kind of backup runs as the job type `backup`, with the kind in the payload.
  **Why:** `IJobQueue.EnqueueAsync` commits in its own scope, so a database-only check could race. There is one process per data path, so an in-process lock is enough. Scheduled and safety backups (later stories) share the same refusal.
  **Issue:** #71
- **Decision:** The backup mount is used only when it exists, is outside the media mount, and a write probe (`.n8tracks-write-probe-<guid>`, deleted on close) succeeds. A backup path at or inside the media path is never probed, written, or listed (invariant 2). A name from a request is never joined to a path: it must look like `n8tracks-backup-*.zip` with no separator or `..`, and must match an entry of the folder's own listing exactly (ordinal). Symbolic links and files in subfolders are never candidates. A missing name, an unknown location (including case variants), and a traversal attempt all answer 404 `not_found`.
  **Why:** This follows invariant 2 and the discretion lines ("only files named `n8tracks-backup-*.zip` directly inside the two folders"; "a bad name or location is 404"). Matching against the listing makes traversal impossible by construction rather than by sanitising.
  **Issue:** #71
- **Decision:** API shapes. `GET /api/v1/backups` answers `{destination, sharesDiskWithData, activeJobId, items:[{location, name, size, createdAt, applicationVersion, kind, status}]}`, where `status` is `valid`, `newer`, or `invalid`, newest first by the manifest's time (by the file's write time for an invalid one). `POST` answers 202 `{jobId}` with `Location: /api/v1/jobs/{id}`, or 409 `backup_in_progress` with `jobId`. Downloading an invalid archive is 409 `backup_invalid`. `DELETE` answers 204. All four are `SessionOnly()`, so `EndpointScopeGuardTests` now counts 16 session-only methods, and the immutability guard lists POST and DELETE with their reasons. The job's result is `{location, name, size}`.
  **Why:** `activeJobId` lets the page follow a backup that was started elsewhere or before a reload. The story names only 404 for a bad name. An invalid file exists, so 409 says why it cannot be downloaded.
  **Issue:** #71
- **Decision:** Verification has two halves. The finished ZIP is read back: each manifest checksum and size is recomputed from the archive's own entries, and any entry the manifest does not list fails it. The database entry is extracted to the temporary folder and must answer `PRAGMA integrity_check` with exactly `ok`. A copy SQLite cannot read, whether while `settings.json` is built or at the check, is also a verification failure. The test hook (`BackupTestHooks.AfterDatabaseCopy`, internal, empty in the app) corrupts the copy after it is made and before it is archived. The same hook pauses a backup in the "catalog keeps working" test.
  **Why:** This follows the discretion line on verification. Corrupting before archiving makes the checksums match, so it is the integrity check, not a checksum, that has to catch it.
  **Issue:** #71
- **Decision:** `settings.json` is `{settings:{<key>: <JSON value>}, environment:{baseUrl, timeZone, logLevel}}`, with the settings read by plain SQL from the database copy, so they are from the same point in time. `BackupWriter.SecretSettingKeys` is empty. The application version reaches the writer as an `ApplicationVersion` singleton registered by both composition roots (Program and CommandServices). Leftover `.tmp-<job guid>` folders are removed by a hosted service, registered with the job worker so that it runs on the server only. A `.tmp-` folder whose suffix is not a GUID is left alone. The test factory now gives every host a backup path that does not exist, never the machine's own `/backup`.
  **Why:** Reading the copy keeps settings and database consistent and works on an older schema, as the discretion line requires. Only the server cleans up, because a container command must not touch backup folders.
  **Issue:** #71
- **Decision:** Not done here: "deleting an archive that a restore is reading is refused with 409" has nothing to check against until the restore story, which must add that refusal to `BackupService.DeleteAsync`. The setup wizard's Backups step stays skipped; it belongs to the scheduled-backup story.
  **Why:** No restore exists yet. The wizard step configures schedule defaults, which are not in this story's scope.
  **Issue:** #71
- **Decision:** Rule 3: `web/src/common/ShortcodeBadge.test.tsx` "is on the Song page and the Version panel…" failed every time the full web suite ran with the new Backups tests. It clicked the copy button before the Version pane had finished loading, so the click could land on the loading pane's button as that pane was replaced. The test now waits for "Loading the lyrics and styles…" to go first, as `openSong` in `SongVersions.test.tsx` does.
  **Why:** The full suite has to pass. The race was already in the test; the extra load from new test files only exposed it. The component itself is unchanged.
  **Issue:** #71
- **Decision:** Rule 3: `.gitignore` (the Visual Studio template) ignores `Backup*/`, which hid the new `Backups/` source folders from git. Two negations follow it, `!src/**/Backups/` and `!tests/**/Backups/`. The template line still covers Visual Studio's upgrade backups anywhere else.
  **Why:** Without them, the commit would leave out the backup service, the infrastructure, and the tests, and CI would fail to build.
  **Issue:** #71
- **Decision:** The schedule is the `settings` row `backups.schedule`, which holds `{revision, enabled, frequency, time, keep, armedUtc, changedUtc}`. The latest scheduled attempt is a second row beside it, `backups.scheduleAttempt` (`{jobId, startedUtc, retry, outcome, finishedUtc, error}`). There is no migration. A missing schedule row reads as the defaults at revision 1. Setup writes the row with the wizard's choice, armed at setup time. For an instance set up earlier, the scheduler's first look writes the defaults, armed then.
  **Why:** The discretion lines put the setting in `settings` under `backups.schedule`, with the attempt "beside it". Two rows let the job record an attempt without racing an administrator's PUT on the revisioned row. A row already in the table needs no schema change.
  **Issue:** #72
- **Decision:** "Missed" is read as: the latest planned time is later than both the last attempt's start and `armedUtc`. `armedUtc` is set at setup, when scheduling is switched from off to on, and at the first look for an instance that had no row. Changing only the time or the frequency keeps it. As a result, moving the time to one already past today runs within a minute when nothing was attempted since that time and since scheduling was armed. Moving it to before a re-enable does not run.
  **Why:** This one rule satisfies "no run at first start or on finishing the wizard or re-enabling", "missed runs are not replayed more than once", and "a time moved into the past today runs within five minutes".
  **Issue:** #72
- **Decision:** The one-hour retry applies only to a first failure that this process saw end, after the latest schedule change, and whose error is not "interrupted by restart". It is computed from the stored attempt and the process start time (`BackupScheduleProcess`) and is not stored itself. A job the restart interrupted is therefore not retried; the missed-run rule decides, and since that attempt started after the planned time, the next run is the next planned time.
  **Why:** The discretion line says a restart drops a pending retry. An interruption is caused by the restart itself, so it is treated the same way.
  **Issue:** #72
- **Decision:** The scheduler (`BackupScheduler`, Infrastructure, server only, registered beside the job worker) looks once a minute, 2 seconds after each whole minute. That puts the first look after a start within about 61 seconds, inside the 5-minute "shortly after start". Each look calls `BackupScheduleService.TickAsync`. While any `backup` job is queued or running, the look waits, so a due backup runs at the first look after that job finishes. The outcome of a `running` attempt is taken from its job, both when the page reads it and at the next look, so the handler never writes a failure itself. Test hosts switch the scheduler off (`BackupSchedulerOptions.Enabled = false` in `N8TracksApiFactory`), and tests call `TickAsync` on a `TestClock`.
  **Why:** Looking just after the minute makes a time "on the minute" due at once. Reusing the job's scrubbed error keeps redaction in one place. Without switching it off, a suite running across 03:00 could start a backup inside an unrelated test.
  **Issue:** #72
- **Decision:** Retention runs in the `backup` job handler after a scheduled success. It reads `keep` at that moment and counts valid archives whose manifest kind is `scheduled`, in both folders. Deletion goes through the new `IBackupStorage.DeleteForRetention`, which logs a warning on a failure and carries on, because Application has no logger. Every backup run now removes leftover `.tmp-<guid>` folders before it starts.
  **Why:** These are the discretion lines (manifest kind, warning without failing) and the AC that leftovers are removed by the next run. Only one backup runs at a time, so a temporary folder found at the start of a run is always a leftover.
  **Issue:** #72
- **Decision:** API and UI. `GET`/`PUT /api/v1/settings/backup-schedule` are session-only. PUT takes If-Match and gives 422 per field (keep 1–365, time `HH:mm`, frequency daily/weekly, every field required); `EndpointScopeGuardTests` now counts 18. `GET /api/v1/backups` adds `lastSuccessAt` (the newest valid archive of any kind) and `schedule {enabled, frequency, time, keep, nextAt, lastAttempt{outcome, startedAt, finishedAt, error, retryAt}}`. `POST /api/v1/setup` takes an optional `backupSchedule`, whose errors come back as `backupSchedule.<field>`. Setup status adds `backups {destination, sharesDiskWithData, defaults}`, never a path. The wizard's Backups step shows the folder as fixed text ("the backup folder (/backup)" or "the data folder (/data/backups)"), not the configured path. The Backups table shows each archive's kind.
  **Why:** The anonymous setup status must not leak paths, as the existing comment on it says. Showing the kind lets the Demo's "a scheduled backup appears" be seen on the page.
  **Issue:** #72
- **Decision:** Rule 1: `BackupService.StartAsync` serialised the job payload as `{"Kind": ...}`, and the handler reads `kind`, so every backup was recorded as `manual` whatever kind was asked for. The payload is now camelCase (`{"kind", "retry"}`). The API tests that check a scheduled backup's manifest kind are the regression test.
  **Why:** Without the fix, retention would never find a scheduled backup to delete.
  **Issue:** #72
- **Decision:** e2e covers the Demo as follows. Step 1 is covered by `setup.spec.ts`, with a11y scans of the step, its open fields, and the Backups page. Step 2 is covered by the new `scheduled-backups.spec.ts`: on the root container, it sets the time to the next whole minute and waits for a real scheduled backup, about a minute. Step 3 (keep 1 after a second scheduled run) is covered by the API test `RetentionKeepsTheNewestScheduledBackupsAndNeverAManualOne` on a controlled clock, not by e2e.
  **Why:** A second real scheduled run would add another minute or more of waiting to every CI run. The retention rule is fully exercised against real archives in the API test.
  **Issue:** #72
- **Decision:** The restore flow follows the discretion lines. `POST /api/v1/restores/validate` takes `{location, name}`, and `POST /api/v1/restores/uploads` takes a multipart upload, read with `MultipartReader` and streamed to `<data>/restore-uploads/<id>.zip` under the 20 GB cap. Both answer `{validationId, expiresAt, archive{name, location|null, size, createdAt, applicationVersion, kind}}`. `POST /api/v1/restores` takes `{validationId, confirmation}`, checks for exactly `RESTORE` on the server (otherwise 422 `validation_failed` on `confirmation`), and answers 202 with the maintenance state and `Location: /api/v1/maintenance`. An unknown, expired, or used validation is 404 `not_found`. A refused start leaves the validation usable. All three endpoints are session-only, and `EndpointScopeGuardTests` now counts 21.
  **Why:** These are the planned flow and codes. Reading the multipart body by hand streams it without buffering, and it also matches the guard test's empty JSON body, which binding an `IFormFile` would not.
  **Issue:** #73
- **Decision:** The refusal statuses: 422 `backup_invalid` with `reason` (`not-a-zip`, `missing-manifest`, `unreadable-manifest`, `missing-database`, `unlisted-entry`, `checksum-mismatch`, `corrupt-database`, `unknown-schema`); 422 `backup_newer_schema` with `reason` and `neededVersion`, the manifest's `applicationVersion`; 413 `upload_too_large`; 507 `insufficient_space` with `requiredBytes` and `availableBytes`. A newer manifest format, or a kind this build does not know, is also `backup_newer_schema` (`reason: newer-format`). The schema check reuses `SchemaVersionCheck` on the extracted database's own `__EFMigrationsHistory`. That history must also end at the manifest's `lastMigration`, and an archive with no history is refused.
  **Why:** Both newer cases mean the same thing to the user: they need the version that made the archive. Reading the history from the database, not from the manifest, checks the thing that will actually be restored.
  **Issue:** #73
- **Decision:** The free-space rule is: the unpacked size of the archive's entries plus the live data (the database, its WAL, and `assets/`) must fit in the free space on the data path's disk (`IDiskSpace`, which tests fake). It is checked before anything is extracted. An upload's declared length is checked against the cap and the free space before anything is written.
  **Why:** A restore unpacks the archive beside the live data and takes a safety backup of that data, so both have to fit. The rule is conservative but cheap to compute.
  **Issue:** #73
- **Decision:** In this story a confirmed restore enters maintenance under the backup start lock. It then drains in-flight API requests (10 s, then `HttpContext.Abort()`), validates the archive again, and takes and verifies a safety backup (kind `safety`, through `IBackupWriter`, not the job queue). It then ends maintenance with outcome `failed` and the log line "replacing the instance's data is not available in this build. Nothing was changed". The swap, migration, rollback, the three-safety-backup limit, and showing the outcome on the Backups page belong to #74, which replaces the end of `RestoreService.RunAsync`.
  **Why:** The AC needs the ordering (maintenance before the safety backup), which can only be tested with the safety backup in place. Replacement is the sibling story, and stopping there leaves the instance exactly as it was.
  **Issue:** #73
- **Decision:** `MaintenanceMiddleware` sits after the path base and before the setup gate and session authentication, because both read the database. It answers every `/api/v1` request except `GET /api/v1/maintenance` with 503 `maintenance`, `Retry-After: 5`, and `no-store`. As a result, a second `POST /api/v1/restores` during a restore gets 503, not 409. The service's own 409 `backup_in_progress` covers a request that passed the gate just before maintenance began, and is tested at service level. `GET /api/v1/maintenance` is anonymous and joins the allow-list. It answers only `{active, stage, percent, outcome}`, with stages and outcomes in kebab-case.
  **Why:** An authenticated 409 during maintenance would mean reading the sessions table that a restore may be replacing. The invariant that nothing reads the catalog during maintenance takes priority.
  **Issue:** #73
- **Decision:** Health gets a fifth component, `maintenance` (`off`/healthy, `restoring a backup`/degraded). During maintenance the database is not queried, and is reported degraded as `not checked`. The response stays HTTP 200, so the container health check (exit 0 on 200) stays healthy. The System page labels the component "Maintenance". The e2e shell and account specs now count 5 rows.
  **Why:** In #74 the database file is swapped during maintenance, and an unhealthy database check would get the container restarted under the restore.
  **Issue:** #73
- **Decision:** The maintenance state is held by `MaintenanceMode` (Application, a singleton) and mirrored in `<data>/maintenance.json`, written atomically on each stage change. At startup, an active state at `validating` or `safety-backup` is recorded as `failed` and the API opens, since nothing could have changed yet. At `replacing` or later the instance stays in maintenance for #74 to put right. `IDatabaseSchemaCheck` returns the new `DatabaseCondition.Maintenance` while the file says active, and `reset-password` and `seed-generation` refuse to run.
  **Why:** The discretion line is "in memory and in a small state file". The #82 note asked for the maintenance refusal in `DatabaseSchemaCheck`.
  **Issue:** #73
- **Decision:** Work that comes due during maintenance is deferred. `JobWorker` claims nothing and prunes nothing, `BackupScheduleService.TickAsync` returns `None` (the missed-run rule runs the backup at the first look afterwards), and `BackupService.StartAsync` returns `Deferred` under the same lock the restore takes. A restore is refused with 409 `restore_blocked_by_jobs` while any job is queued or running (new `IJobStore.AnyActiveAsync`), or with 409 `backup_in_progress` while a backup job is.
  **Why:** These are the AC and the discretion line on deferral. The shared lock closes the race between a scheduled backup being queued and maintenance beginning.
  **Issue:** #73
- **Decision:** Following the #71 note, `BackupService.DeleteAsync` now returns `Deleted`/`NotFound`/`InUse`, and the endpoint answers 409 `backup_in_use` while a restore is reading the archive. `RestoreReads` is held during a listed validation and for the whole restore run.
  **Why:** A delete in the middle of the read would remove the archive the restore is reading.
  **Issue:** #73
- **Decision:** Validations are kept only in memory (`RestoreValidations`). `RestoreHousekeeping` (server only) removes `restore-uploads/` and `restore-work/` at startup, and every minute deletes uploads whose validation has expired (after 1 hour). A failed upload is deleted at once. The database is extracted for its checks into `restore-work/<id>/` under a fixed name and removed afterwards. No other entry is ever written out.
  **Why:** These are the discretion lines on upload lifetime. Using a fixed name rules out any entry name escaping the folder (invariant 2: nothing is written under the media mount, and no path input escapes).
  **Issue:** #73
- **Decision:** Web. `MaintenanceGate` is the outermost gate. Any 503 `maintenance` that `apiFetch`, the setup status, or the upload receives replaces the app with `MaintenancePage`, which polls the status every second and shows the stage label and a progress bar. After `succeeded`, or when no restore ran, it returns to the app by itself. After `failed` or `rolled-back`, it says what happened and waits for "Continue", then remounts the setup and session gates. The upload calls `fetch` directly, with no 10 s timeout, because an archive can be large. Restore is offered only on `valid` rows. "Restore from a file…" is a Mantine `FileButton` accepting `.zip`.
  **Why:** The setup status is the first request after a reload, so a reload during a restore goes straight to the maintenance page without an extra request on every load.
  **Issue:** #73
- **Decision:** e2e `restore.spec.ts` walks Demo steps 1 and 2 on the shared containers, with axe scans of both dialogs, and does not confirm. Step 3 (the maintenance page after confirming) is covered by API integration tests and component tests.
  **Why:** The test plan asks e2e for steps 1 and 2. Confirming on a shared container would leave a safety backup and a failed restore outcome where other specs would see them. #74's e2e walks the full restore with a scan of the maintenance page.
  **Issue:** #73
- **Decision:** Replacement is a journalled swap under the data path (`Infrastructure/Backups/LiveDataReplacement`, port `ILiveDataReplacement`). The journal `restore-previous/restore.json` is written before the stage becomes `replacing`, and is replaced atomically again when the swap begins, recording which live items existed (the database, `-wal`, `-shm`, `-journal`, and `assets/`). The live items are renamed into `restore-previous/`, companions before the database, and the staged items are moved in. Putting back moves every item still in `restore-previous/` home, deleting what the swap brought in, and is idempotent from any point. Tests stop the swap and the put-back after every move. The archive is unpacked and re-hashed into `restore-work/<id>/` first (`IRestoreArchives.StageAsync`), and asset paths must be plainly relative and inside that folder.
  **Why:** This is the discretion line "rename aside, rollback renames back, no second unpack". Every step is a same-disk rename, so a crash at any point leaves a state the journal can undo. #75's offline command can reuse the same routine.
  **Issue:** #74
- **Decision:** Pooled connections are closed with `SqliteConnection.ClearAllPools()` before the swap, before migrating, and before putting back. `ClearPool` on a single connection string is not used.
  **Why:** No handle may stay on a file that is about to move. Clearing every pool only costs other hosts in the same test process a reconnect. In production there is one database per process.
  **Issue:** #74
- **Decision:** The run reports Replacing → Migrating → Finishing. Migrating applies the restored database's pending migrations one at a time: WAL, the schema-history check, then the migration-lock check (`DatabaseStartup.MigrationLockIsHeldAsync`, now internal), and then it sets `MigrationStateHolder`. No second safety backup is taken. Finishing deletes every session and marks the archive's queued and running jobs `failed` with "superseded by restore". `SetupCompletion.Forget()` is then called, so a restored database without an administrator asks for setup again. Any exception after the journal (shutdown cancellation included) puts the data back and ends `rolled-back`.
  **Why:** These follow the AC and the discretion lines on migration, jobs, and sessions. `SetupCompletion` cached "complete" for the life of the process, which is wrong once the database is another one.
  **Issue:** #74
- **Decision:** If putting back fails, `MaintenanceMode.Stall()` keeps maintenance on with the new outcome `rollback-failed`, which is persisted and survives restarts. `RestoreRunner` logs at Critical with the safety backup's path, the folder holding the previous files, and the command `n8tracks restore <path>`. That command is #75's; until #75 lands, the line also gives the manual steps. At startup, `RestoreStartup.Recover` (run before `DatabaseStartup`) puts back any journal whose swap began, whether maintenance is active or the state file was lost. If the instance is still in maintenance after that, the database is neither opened nor migrated.
  **Why:** AC4 and the discretion line on staying in maintenance across restarts. An unmigrated, half-swapped database must never be "upgraded" at startup.
  **Issue:** #74
- **Decision:** The last restore's outcome is kept in `last-restore.json` under the data path (`ILastRestoreStore`), written only for attempts that reached `replacing`. It holds the outcome, time, archive name, failed stage, a fixed per-stage detail (never an exception message), and the safety backup's location, name, and path. `GET /api/v1/backups` returns it as `lastRestore`. The Backups page shows it in a `data-testid="last-restore"` note. The maintenance page names the stage a rolled-back restore failed at, and shows a "could not be undone" notice with no way forward for `rollback-failed`.
  **Why:** These follow the discretion lines on the state file and on what the maintenance page shows. A generic detail keeps provider or SQL text out of the UI (invariant 6).
  **Issue:** #74
- **Decision:** Safety backups are kept to the newest three (`BackupRetention.SelectSafety`), applied after a successful restore. The archive being restored from is never deleted. Scheduled retention still ignores safety backups.
  **Why:** This is the discretion line. Deleting the source would remove the only copy of what was just restored.
  **Issue:** #74
- **Decision:** Rule 1: `GET /api/v1/maintenance` is now answered by `MaintenanceMiddleware` itself and never reaches the setup gate or session authentication. Before this change, a poll carrying a session cookie looked the session up in the database while the restore was moving the database file. That opened it during the swap, and could have created an empty file, or 500'd after restoring an archive with no `sessions` table, which left the maintenance page stuck. Regression test: `TheMaintenanceStatusNeverOpensTheDatabase` (it fails without the fix), plus a pre-setup check in `SetupEndpointTests`.
  **Why:** Maintenance must keep every request away from the database while files move.
  **Issue:** #74
- **Decision:** e2e `restore.spec.ts` walks the full Demo on the fresh container (`@root-only`). While the page is being scanned, the maintenance-status route passes the server's answers through, but turns an "ended" answer into "finishing, 100%" until the axe scan of the maintenance page is done. It then unroutes and lets the real end through.
  **Why:** Restoring a tiny instance takes about a second, too short to scan the real progress page reliably. Only the status shown is held: the page, the restore, and everything after it are real.
  **Issue:** #74
- **Decision:** Invariant check. A restore is a whole-instance rewind with a typed confirmation, not an edit path. It can bring back an earlier state in which a Version had no Generation, but it never changes a frozen Version's inputs while its Generation is attached (invariant 1). It writes only under the data path and the backup destination, and asset paths are confined to the work folder (invariant 2). It is not an import (invariant 3).
  **Why:** I am recording why no invariant needs a conversation with the user.
  **Issue:** #74
- **Decision:** The "failed-upgrade marker" that `n8tracks restore` clears is `<data>/upgrade-state.json`, the file #76's plan names. It is behind a new port, `IFailedUpgradeMarker` (`UpgradeMarkerFile`), which only deletes it. #76 owns the file's format and adds the write and read sides. The command clears the marker only after a successful restore, never on a refusal or a rollback. Nothing in the code wrote such a marker yet: the only upgrade state was the migration lock row inside the database, and a restore replaces the database anyway.
  **Why:** The AC needs the command to clear it, and #76's plan fixes the name. Clearing on success matters because #76 restores its safety backup at start while the marker shows an unfinished upgrade, which would overwrite what was just restored.
  **Issue:** #75
- **Decision:** The data path lock is a new port, `IDataPathLock`, implemented by `DataPathLockFile`: `.n8tracks.lock` opened with `FileShare.None`, so .NET takes an exclusive `flock`. It is opened read-only, so a lock file created by another user still works. The server takes it right after configuration is valid and before restore recovery. It holds it through a DI singleton that the container disposes, so a test host releases it when disposed, and refuses to start (exit 1, one Error line naming `N8TRACKS_DATA_PATH`) when the lock is taken. `reset-password`, `seed-generation`, and `list-backups` do not take it, because they run beside the server.
  **Why:** This follows the discretion line (an OS lock, so no stale marker). Making it a singleton means the WebApplicationFactory hosts that tests start one after another on the same data path release it reliably. The full suite passed with it.
  **Issue:** #75
- **Decision:** The command's restore is a new `OfflineRestoreService` (Application/Backups). It reuses the web restore's `RestoreValidator`, through a new `ValidateFileAsync` and `RestoreSource.File(FullPath)`, which only the command makes. It also reuses `IRestoreArchives.StageAsync` and `ILiveDataReplacement` (`Prepare`, `Swap`, `FinishAsync`, `PutBack`). It is not a method on `RestoreService`, because that service needs `IRequestDrain` and `IRestoreRunner`, which exist only in the server. It enters maintenance at "replacing" before moving anything. A crash mid-swap is then put back by the server's existing start-up recovery.
  **Why:** One business-rule layer (invariant 5) with the same validation and replacement as the web path. The maintenance state makes a killed command recoverable without any new code.
  **Issue:** #75
- **Decision:** No safety backup archive is taken by the command (`RestoreJournalEntry.SafetyBackup` is now nullable). The previous database and assets are moved aside. On success they are kept in `<data>/before-restore-<UTC yyyyMMddTHHmmssZ>/` (`ILiveDataReplacement.KeepPrevious`), which nothing deletes automatically, and the message names it. The command does not write `last-restore.json`, so the Backups page's "last restore" note stays on the last web restore. `RecoverAtStart` skips that note for a journal without a safety backup.
  **Why:** The AC asks for a copy aside, not a backup. The database being replaced may be the broken one, so an online backup of it could fail. Keeping the copy after success gives the operator the data from before without a safety archive. Writing the note would need a nullable `safetyBackup` in the API and the web type, which this story does not otherwise touch.
  **Issue:** #75
- **Decision:** Before its own restore, the command deals with an earlier restore's journal the way `RecoverAtStart` does. If that restore ended or never moved anything, it discards the leftovers. Otherwise it puts the moved data back and ends maintenance as rolled back, then restores. If the instance was stuck with no journal and the command's own restore fails, maintenance stays on (`Stall`), because the data's state is unknown. Otherwise a failure ends maintenance as rolled back. Sessions are ended and queued or running jobs superseded (`FinishAsync`), as in the web restore. No migration is run (discretion).
  **Why:** This covers the "restore that could not be rolled back" case the #74 log sends operators to, without guessing at data whose state is unknown.
  **Issue:** #75
- **Decision:** The archive argument can be any path in the container. It is refused when it is inside the media mount (invariant 2: nothing under `/media` is used for backups) or is a folder. `list-backups` and `restore` read the backup and media mounts through a new `EnvironmentOptionsLoader.LoadPathsOnly`, so `N8TRACKS_BACKUP_PATH` is honoured. `IBackupStorage` gains `FolderPath(location)` so the listing can print restorable paths: Api/Cli may not name Infrastructure's `BackupFolders`. Its columns are created (UTC), kind, version, size, and path.
  **Why:** A downloaded backup can then be mounted anywhere and restored, and the listing's paths can be pasted into the restore command.
  **Issue:** #75
- **Decision:** The entrypoint now passes `n8tracks <command> ...` (the container's arguments) through the `n8tracks` wrapper as the PUID/PGID user, after `fix_owner`. Without that first word it still ignores arguments and starts the app. The wrapper accepts `list-backups` and `restore`. The log lines of the web restore and the start-up recovery now say to run the command "from the image with the same volumes (docker run --rm)" instead of "in it".
  **Why:** This is the discretion line (files restored with the right owner). The old wording told operators to run a server-stopped command inside the stopped container.
  **Issue:** #75
- **Decision:** Tests. `tests/n8Tracks.Api.Tests/Cli/RestoreCommandTests.cs` (21 tests) runs both commands in-process against the data path of a disposed host. It covers every web refusal (`RestoreEndpointTests.Break` is now internal) with byte-identical files after the refusal, too little space, a missing file, a folder, and an archive inside the media mount. It covers a running host's lock, and the server refusing to start while the lock is held. It covers the stuck maintenance state and the marker being cleared, recovery from a rollback-failed web restore, a failure after the swap through a hook (`RestoreCommand.RunAsync(..., testServices)` and `CommandServices.Build(options, testServices)`), a put-back failure, and the listing. `scripts/smoke-docker.sh` has a new "Disaster recovery" section: back up through the API, refused while running, stop, list, restore, start, catalog and owner checked. It uses a named volume for `/data`, because a desktop VM's bind-mount file sharing need not carry `flock` between containers. The e2e `offline-restore.spec.ts` (`@root-only`, fresh container) walks the Demo, ending with a11y scans of the sign-in page and the restored Songs page.
  **Why:** The test plan, plus a browser-level walk of Demo step 3 per the conventions.
  **Issue:** #75
- **Decision:** Invariant check. The command writes only under the data path: the moved-aside and kept folders, the journal, `maintenance.json`, the lock file, and the marker's deletion. It never reads or writes the media mount (invariant 2), and archives inside it are refused. Its messages carry paths, the archive's name, and exception messages from file and archive handling. The archive's name and version are the only logged properties, so no secret reaches the log (invariant 6). It is not an import (invariant 3). It goes through the Application layer (invariant 5).
  **Why:** I am recording why no invariant needs a conversation with the user.
  **Issue:** #75
- **Decision:** The upgrade's safety backup is taken by `DatabaseStartup` calling `IBackupWriter.CreateAsync(storage.ResolveDestination(), ..., BackupKind.Safety, ...)` directly, synchronously, not through `BackupService.StartAsync`, which enqueues a job. The destination comes from `IBackupStorage.ResolveDestination()` (mount if writable, else `<data>/backups`), never a database setting. Leftover `.tmp-*` folders are removed just before it.
  **Why:** It is the same create-and-verify routine the backup job runs (the key link), and the job queue does not run before the database is migrated.
  **Issue:** #76
- **Decision:** The marker `upgrade-state.json` has three stages, not two: `migrating`, `restoring`, and `restored`. `restored` means the safety backup was put back. It is the state in which a later start of the same version refuses with one line (step `failed-upgrade`), and another version clears the marker and upgrades. `migrating` and `restoring` mean the database may be half-migrated. Any start that finds one of them puts the safety backup back first. The marker also records `failedMigration` and a `restoreId`, so a repeated put-back undoes its own earlier attempt. It is written after the backup is verified and removed after success. An unreadable marker stops startup (step `upgrade-marker`) and changes nothing.
  **Why:** The discretion line "a different version that finds the marker still showing a half-migrated database restores ... first" needs a stage saying the database is no longer half-migrated. Guessing about a marker that cannot be read could cost data.
  **Issue:** #76
- **Decision:** Marker recovery (`DatabaseStartup.RecoverFailedUpgradeAsync`) runs in Program right after the data-path lock, before the restore story's `RestoreStartup.Recover`. The put-back (`Infrastructure/Backups/UpgradeSafetyRestore`) reuses `IRestoreArchives.StageAsync` (checksums checked again) and `ILiveDataReplacement` (Prepare, Swap, PutBack, KeepPrevious), with the journal's `RestoreId` taken from the marker. Staged assets are dropped, so the live `assets` folder is moved aside and back unchanged. The restored file must pass `PRAGMA integrity_check`, or the half-migrated file is put back (the restore-failure case, step `safety-restore`). The half-migrated database and its `-wal`/`-shm`/`-journal` are kept as `n8tracks.db.failed-upgrade` with SQLite's companion names, or with `-<UTC stamp>` (then `-2`...) added when one exists.
  **Why:** "Before doing anything else" (AC). If recovery ran after `RestoreStartup.Recover`, that would mistake an interrupted upgrade put-back for an interrupted web restore and report a rolled-back restore. The rest follows the discretion lines: the restore story's routine, `-wal`/`-shm` included, and integrity checked before "restored" is logged.
  **Issue:** #76
- **Decision:** Health `migrations` gains `lastOutcome` (`none`/`succeeded`) and `lastSafetyBackupAt` (newest valid `safety` archive in the folder listing, read on each health call; null if none or unreadable). A web restore that migrates the restored database also sets `succeeded`, and otherwise keeps the outcome it had. Settings → System shows both under the schema row ("Upgraded at this start" / "Nothing to upgrade at this start", "Last safety backup <time in TZ>" / "No safety backup yet"), only when the backend reports them.
  **Why:** Discretion lines. The listing is local and cheap, and it counts safety backups of any origin, as specified.
  **Issue:** #76
- **Decision:** Test seam: `Program.RunAsync` gains an internal overload taking `Action<IServiceCollection>? testServices` (the app passes null). The tests swap EF Core's `IMigrationsAssembly` through `ConfigureDbContext(...ReplaceService<IMigrationsAssembly, TScenario>())`. The scenarios are the real migrations plus test-only ones (`tests/.../Persistence/UpgradeMigrations.cs`: one that adds a table and a row, one that fails part way, and one that also corrupts the safety archive). Nothing test-only ships in the app.
  **Why:** The exit code and the single Error line can only be observed by running the entry point. The discretion line asks for an injected migrations assembly.
  **Issue:** #76
- **Decision:** Rule 2: `BackupWriter` read `settings` unconditionally, so a database from before `AddAdministratorsAndSettings` (an `InitialCreate`-only instance) could never be backed up. That made its upgrade's safety backup fail and blocked the upgrade. It now checks that the table exists first. Rule 1 consequences: the container commands' read-only schema check (`DatabaseSchemaCheck`) returns `Upgrading` while a marker exists, and their message says so. `RestoreCommandTests.AFailureAfterTheReplacementBeganPutsTheFilesMovedAsideBack` deletes its stand-in `{}` marker before starting the server, which now refuses an unreadable marker.
  **Why:** The upgrade must work from every schema this app has shipped. A password reset written to a database the next start replaces would be lost.
  **Issue:** #76
- **Decision:** Watched the main test fail before the change. With only the seam and test migrations in place, `AFailedMigrationPutsTheDatabaseFromBeforeTheUpgradeBackAndStopsTheApp` failed. The old code logged "Applied database migration 29990101000000_TestChangesData", failed at `29990101000001_TestFails`, said nothing about a restore, and left the first test migration applied.
  **Why:** The test plan asks for it.
  **Issue:** #76
- **Decision:** Invariant check. The upgrade writes only under the data path (the marker, the work and `restore-previous` folders, the `.failed-upgrade` files) and in the backup destination. `ResolveDestination` never picks a folder inside the media mount, so invariant 2 holds. The log lines carry migration IDs, versions, paths, and exception messages from SQLite and file handling. They carry no credentials, lyrics, prompts, or payloads (invariant 6). It deletes only safety archives beyond the newest three, and never the one the current marker names.
  **Why:** I am recording why no invariant needs a conversation with the user.
  **Issue:** #76
- **Decision:** The options are a typed Domain record, `VersionInputs` (`src/n8Tracks.Domain/Songs/VersionInputs.cs`), with one property per stored inventory key. Each property is tagged `[SunoField("<inventory key>")]`, and `kind`, `songMode`, and `speechMode` are enums. `SongVersion` gains `Inputs`, and `WithInputs(lyrics, styles, inputs)` stays the one freeze check. The Application record of the same name (lyrics and styles only) is renamed `VersionText`. That is the text the editing history snapshots and restores.
  **Why:** This follows the story's artifact path and its discretion line ("a typed property per inventory key"). Two `VersionInputs` types would have been ambiguous inside `Application.Songs`.
  **Issue:** #111
- **Decision:** Storage: `kind` and `model` are plain columns, and every other option is one JSON document in a TEXT column, `versions.inputs`, keyed by the API names. It is not an EF owned or complex type mapped with `ToJson`. The store converts the document through `VersionInputsColumns`, which uses the same serializer as the API (`VersionInputRules.ToJson`/`FromJson`, with every key required on read). The migration `AddVersionInputs` only adds columns, so the table is not rebuilt and the four other triggers survive. It gives existing rows the defaults (Advanced Song, inventory defaults, no model, empty Suno title) through the column default, so no frozen row is rewritten. It drops and recreates `tr_versions_frozen_inputs_never_change` to compare `kind`, `model`, and `inputs` too.
  **Why:** Inputs are written in one conditional `ExecuteUpdateAsync` (revision check plus write). That is certain to work for a scalar column, whereas the owned-JSON mapping would need checking. One serializer means one spelling of every option. Existing Versions predate the options, so there is nothing to backfill but defaults.
  **Issue:** #111
- **Decision:** Limits, ranges, value lists, defaults, labels, and tab/mode applicability are read from the embedded inventory (`CreateFieldInventory`: an `EmbeddedResource` linking `docs/suno-create-field-inventory.json`). Nothing restates them. Validation follows each property's type: text uses `maxLength` with the lyrics rules (no U+0000, no lone surrogates, `\r\n` → `\n`); an int is a whole number in `min..max`; a choice must be in `values`; bool must be true or false. `null` is accepted only where the inventory default is null. `effectiveInputs` holds the kind, the kind's mode, and every stored field on the kind's tab whose `modes` contain the current mode, in the inventory's order. Two rules are in code because the inventory's conditions are prose: custom seconds only when Duration is custom, and lyrics and styles in Simple mode only when their section is added. Errors are keyed `inputs.<key>`, and messages use the inventory label. An unknown key and a non-object `inputs` are 422.
  **Why:** This is the story's key_link ("not restated in code"). A re-captured inventory changes the accepted values, and the coverage test reports the difference.
  **Issue:** #111
- **Decision:** `simple_add_lyrics`/`simple_add_styles` (an inventory choice of write_new/use_existing with no default) are stored as booleans `simpleLyricsAdded`/`simpleStylesAdded`, default false. The coverage test maps inventory keys to API names by camelCase, with those two as its only named renames. It checks them as true/false switches.
  **Why:** The AC says "whether the Simple form's lyrics and styles sections are added", and the discretion names the two keys. Both inventory values add a section whose text is the Version's existing lyrics and styles.
  **Issue:** #111
- **Decision:** The model is validated against a new port, `ISunoModelList` (Application/Suno). Until #114 replaces the registration with the managed list, it is backed by `InventoryModelList`, the inventory's three models. A new Version's model is null (the inventory default) until #114/#115 choose one.
  **Why:** The AC says the model is checked "against the model list, not the inventory", but #114 (the list) depends on this story. The seam keeps that rule's call site final, and today's list equals #114's seed.
  **Issue:** #111
- **Decision:** `kind` is inside `inputs`, beside `songMode`/`speechMode`, and is not a top-level field. The detail response (`GET`, `PATCH` answer, conflict `current`) carries `inputs` and `effectiveInputs`. The tree list and the create answer stay lean. The Suno title pre-fill uses the normalised Song title cut to the inventory's 100, one code unit shorter when the cut would split a surrogate pair. `POST .../versions` copies every option but takes no `inputs` overrides (unlike lyrics/styles). The editing history still covers lyrics and styles only: a credential's options-only edit takes no snapshot. The frozen message now says "lyrics, styles, and Suno options". `simpleprompt` and `excludestyles` are added to the redaction list. They were already covered by `prompt` and `styles`, but are listed explicitly.
  **Why:** The kind is changeable "while the Version is mutable" like every other option, so one merge path covers it. Carrying unsaved options into a new Version is for the panel story (#112) to add if it needs it.
  **Issue:** #111
- **Decision:** The image build now reads `docs/suno-create-field-inventory.json`. In `.dockerignore`, `docs/` is replaced by `docs/*.md`, `docs/spikes/`, and `docs/suno-import-field-map.json`; no `!` re-include is used, since `ImageIsolationGuardTests` forbids it. The Dockerfile copies the one file into the build stage.
  **Why:** The Application project embeds the inventory at build. The image is published from the same sources, so it must see the file.
  **Issue:** #111
- **Decision:** Invariant check. Invariant 1: every option is a creation input (entity `WithInputs`, the store's one input write, and the DB trigger), and the #69 guard now classifies `Inputs`/`kind`/`model`/`inputs`. It changes every key of the options document alone and all together through every endpoint and service method, refused on a frozen Version and applied on a mutable one, and it asserts the entity, the rules, and the API agree on the key set. Invariant 6: the new prompt-like fields are redacted, and the redaction guard sends sentinels in `inputs`. No other invariant is touched.
  **Why:** I am recording why no invariant needs a conversation with the user.
  **Issue:** #111
- **Decision:** `GET /api/v1/suno/create-fields` (`catalog.read`, `SunoEndpoints.cs`) serves each inventory field as captured, plus `option` (the camelCase `inputs` key that stores it; null for lyrics, styles, and fields no option stores) and `help`, and the response also carries `models` from `ISunoModelList`. So the web never restates the option-name mapping, and #114 only swaps the registration.
  **Why:** The discretion line says the endpoint "returns the inventory's fields". Adding the option name and the model list keeps every limit, list, and name on the server.
  **Issue:** #112
- **Decision:** Help text is Suno's tooltip as the inventory notes quote it (`Tooltip: '…'`; `Tooltip at Max: '…'` becomes "At Max: …"), extracted by `CreateField.Help`. Fields with no captured tooltip (model, Song description, Exclude styles, custom duration, Song Title, lyrics, styles) show no help text, and nothing is invented.
  **Why:** The AC asks for "Suno's own explanation". The rest of the notes are the capture's remarks, not Suno's words, and the inventory has no tooltip for those fields.
  **Issue:** #112
- **Decision:** Option edits ride the existing `useRevisionedSave`/`useAutosave` as `inputs.<key>` edit fields whose values are the option's JSON (`optionText`). `versionEditOf` turns them into the PATCH body's `inputs` object (and the page-close keepalive does the same). Each option is a compared field in the conflict dialog, labelled from the inventory. On a freeze, unsaved option changes go back to the stored ones (only lyrics and styles are carried to a new Version). The create API copies the source's stored options and takes no overrides.
  **Why:** This keeps one save queue and one conflict path without widening `FieldValue`. #111 left carrying unsaved options to #112, and the create API has no `inputs` field, so carrying them would change an API contract (Rule 4).
  **Issue:** #112
- **Decision:** Layout and controls: kind selector (Song, Speech, Sound; Speech and Sound disabled) and the Simple/Advanced switch are Mantine SegmentedControls. The model is a NativeSelect with "Not chosen" for null. Weirdness, Style Influence, and the custom duration are Sliders with `aria-valuetext` in their unit; Variety is a five-step slider announced by name. Vocal Gender is a segmented None/Male/Female. Max Mode and Personalize are Switches, with the help outside the label so it does not enter the name. More Options is a disclosure button (collapsed at first, then open while the page is), in Suno's order, with the model above and Song Title below it. Labels are the inventory's ("Model version", "Song Title"…). On a frozen Version the text fields are `readOnly`, the segmented controls are `readOnly`, the sliders, switches, and select are `disabled`, and the Simple add/remove buttons are hidden.
  **Why:** These mirror Suno's own controls. Mantine's Slider and Switch have no read-only state, so disabled is the read-only form there.
  **Issue:** #112
- **Decision:** Rule 3: `e2e/tests/song-edit.spec.ts` now finds the Song title box with `{ name: 'Title', exact: true }`. The new "Song Title" field also matched the non-exact name.
  **Why:** The new field blocked an existing test's locator. Only the locator changed; the test checks the same thing.
  **Issue:** #112
- **Decision:** Invariant check. Invariant 1: no new write path. Options are saved only through `PATCH /api/v1/versions/{reference}` (already guarded by #111), and the new endpoint is a GET. Invariant 5: the web goes through the REST API. Invariant 8: no suppressions were added. No other invariant is touched.
  **Why:** I am recording why no invariant needs a conversation with the user.
  **Issue:** #112
- **Decision:** The 12 Speech and Sounds fields are new `VersionInputs` properties named after their inventory key in camelCase (`speechPrompt`, `speechScript`, `speechTone`, `speechVocalGender`, `speechBackgroundMusic`, `speechVariety`, `soundsModel`, `soundDescription`, `soundType`, `soundBpm`, `soundKey`, `soundScale`). Each kind's Vocal Gender, Variety, and model is its own key, so nothing is shared. `soundBpm` is a nullable int (null is Auto), and `VersionInputRules` now accepts null for a number only when the inventory default is null. `soundsModel` is checked against `ISunoModelList` like a Song's model ("same dropdown as Songs" in the inventory). The `model` column keeps the Song's model only. The Sound's model lives in `inputs`.
  **Why:** The coverage test derives option names from the inventory key, and keeping one naming rule means #114 swaps one model list for both kinds. A second model column would be a schema change the story does not ask for.
  **Issue:** #113
- **Decision:** `effectiveInputs` uses the inventory's tab and mode, as before. A Speech sends its mode and either its prompt (Simple) or its five Advanced options. A Sound sends its six options, and `soundScale` only while `soundKey` is not `any`. Lyrics and styles are a Song's only, so a Speech or Sound never sends them.
  **Why:** AC 6 and the discretion line on the scale.
  **Issue:** #113
- **Decision:** The migration `AddSpeechAndSoundInputs` (hand-written; `dotnet-ef` is not installed. The Designer is the previous model copied, since the model is unchanged) drops `tr_versions_frozen_inputs_never_change`, `json_set`s the 12 keys at the inventory defaults into every row's `inputs`, frozen rows included, and re-creates the trigger exactly as `AddVersionInputs` had it. Down removes the keys the same way. The `inputs` column default (only used by raw SQL inserts) is still #111's document. Changing it would mean a table rebuild, so the test helper `SongApi.AddVersionDirectly` now writes full default inputs.
  **Why:** `FromJson` requires every key. A frozen Version never held Speech or Sound options, so giving it the defaults changes no input it was created from (invariant 1 holds: the options did not exist, and they do not apply to its kind).
  **Issue:** #113
- **Decision:** The Song response's `currentVersion` gains `kind`, and the Version list, create, and detail responses gain a top-level `kind` (from the `kind` column). The Songs table has a "Kind" column, and the Song page shows "Kind: Song/Speech/Sound" beside the shortcode. The web keeps the Song's kind in step when the current Version's kind changes or a new Version becomes current.
  **Why:** AC 8 needs the kind in the list and on the page without reading every Version's detail. These fields are additive, so no client breaks.
  **Issue:** #113
- **Decision:** Web: `OptionsPanel.tsx` draws the kind selector and picks `SongOptions`, `SpeechOptions`, or `SoundOptions` by the Version's kind. Each gets the selector for its own header row, and shared control builders sit in `optionParts.tsx`. Speech options are shown directly (no collapsible section); in Advanced mode they are Script, Tone, Vocal Gender (None/Male/Female), Background music, and Variety (steps). Sound options are the model, Sound (description), Type (One-Shot/Loop), BPM (a NumberInput with an empty value meaning Auto and out-of-range values not taken), and Key: Any plus a 4-to-6-column grid of 12 Chip radio buttons sharing a name, so the arrow keys move between them. The Key scale (None/Major/Minor) appears once the key is not Any. The lyrics, styles, and History panel show for a Song only, and conflict-dialog labels say "Speech:" or "Sound:" for those tabs' options.
  **Why:** This follows the must-have key link ("one component per kind") and the discretion lines on the key picker, the hidden mode switch for Sound, and History for Song only. Suno groups some of these under "Advanced" disclosures, but the AC lists them as offered, and showing them keeps every control one step away.
  **Issue:** #113
- **Decision:** The `speech_prompt` 1,000-character limit the discretion line asks for was already recorded in the inventory by #116 (commit 7f05e8a), so the inventory file is unchanged here. A unit test now asserts the limit.
  **Why:** I checked the file before editing it.
  **Issue:** #113
- **Decision:** Invariant check. Invariant 1: the new options are creation inputs on the existing guarded path (`WithInputs`, the one inputs write, the DB trigger covering `inputs`). The #69 guard changes each new key automatically: `InputValues.ChangedJson` now changes a null number. The migration re-creates the trigger, and a test proves it refuses afterwards. Invariant 6: `speechprompt`, `speechscript`, `speechtone`, and `sounddescription` are on the redaction list, with a sentinel test. No other invariant is touched.
  **Why:** I am recording why no invariant needs a conversation with the user.
  **Issue:** #113
- **Decision:** Rule 3: `e2e/tests/songs.spec.ts` now expects the new Kind column ("Song") at cell 3 and the Version count at cell 4. The new column had moved the count one cell along. A separate follow-up commit records this entry, because the story commit was already pushed.
  **Why:** The new column broke an existing test's locator, which the full e2e run caught. The test checks the same things plus the kind.
  **Issue:** #113
- **Decision:** The model list is a new `suno_models` table (id, name, name_key, note, position, retired, discovered), seeded with v6, v6-wild, and v6-mini (fixed IDs, the inventory's order). One `settings` row, `suno.models`, holds the revision for the whole list, the same pattern as the workflow states. The migration `AddSunoModels` was generated with `dotnet-ef` (after `dotnet tool restore`), and its ID was moved to `20261005170000` so that it sorts after #113's hand-dated `20261005163000_AddSpeechAndSoundInputs`.
  **Why:** The story's must-haves and routes ask for a managed list with one revision. A generated ID (16:15) would have sorted before #113's migration and run out of order.
  **Issue:** #114
- **Decision:** `ModelCatalogService` (Application/Suno) implements `ISunoModelList` and replaces `InventoryModelList`, which was removed. The registration is scoped because the service reads the database. `ISunoModelList` gained `OfferedAsync`, which lists the models that are not retired, in order. A Version's model is still checked with `ListAsync`, which includes retired models.
  **Why:** The key link says "a model value must be in the list, retired or not". Picking from the offered list is a UI concern.
  **Issue:** #114
- **Decision:** `GET /api/v1/suno/create-fields` `models` now lists only the offered models, in the list's order. Before this, it was every model a Version may name. The web `ModelControl` already showed a current value missing from the list; it now labels that value "<name> (retired)".
  **Why:** The pickers need exactly the offered list (AC 4), and a Version keeps showing its retired model (AC 3). The field is unreleased and only this milestone uses it, so there is no other client to break. #112's notes expected #114 to change it.
  **Issue:** #114
- **Decision:** Refusals. Renaming or deleting a model that any Version names (as `model` or as `inputs.soundsModel`, each Version counted once) is 409 `model_in_use` with `versionCount`. Retiring or deleting the last model that is not retired is 409 `last_offered_model`. A wrong reorder is 409 `order_mismatch`. A taken name, ignoring case and NFC, is 422. Notes are optional, one line, at most 200 characters, and a blank note is none. In a PATCH, `note: null` removes the note.
  **Why:** The AC allows renaming and deleting only a model no Version uses, and forbids retiring the last non-retired one. I extended "last offered" to deletion, because deleting it would also leave nothing to offer. The discretion line fixes names at 1 to 50 characters but gives no note limit, so I chose a short one-line note.
  **Issue:** #114
- **Decision:** A new Song's Version 1 gets the first offered model as both `model` and `soundsModel`. Before this, both were null, the inventory default. The tests that asserted null defaults now expect "v6", and `InventoryCoverageTests` treats the two model keys like the title: they start from the list, not the inventory.
  **Why:** The discretion line says a new Version's model is the first non-retired model until the default story lands. Suno's Sounds tab uses the same dropdown, so a Sound's model follows the same rule.
  **Issue:** #114
- **Decision:** Rule 2: when an edit changes a Version's model or Sound model, `VersionService.UpdateAsync` checks the model against the list again inside its transaction. The list's changes run in the same kind of exclusive transaction.
  **Why:** Before this, the model was checked only before the transaction. A model renamed or deleted between that check and the write could have left a Version naming a model that is no longer on the list. The extra check closes that gap.
  **Issue:** #114
- **Decision:** Invariant check. Invariant 1: a list change never writes `versions`. A model a Version names cannot be renamed or deleted, so a frozen Version's model never changes. The #69 guard now runs `PATCH` and `DELETE /api/v1/suno/models/{id}` against the frozen Version's own model (each answers 409 `model_in_use`, and the inputs are byte-identical afterwards). It exempts POST and PUT order with a reason. Invariant 5: every rule is in `ModelCatalogService`, and the endpoints only map outcomes. Invariant 7: managing the list is `SessionOnly()`, so no token can use it. No other invariant is touched.
  **Why:** I am recording why no invariant needs a conversation with the user.
  **Issue:** #114
- **Decision:** Web: the page is Settings → Suno (`settings/SunoPage.tsx`), and its "Models" section is `SunoModelsSection.tsx`. The sidebar is now Account, Credentials, Workflow, Suno, Backups, System. Models are reordered with Move up and Move down only, with no dragging. The edit dialog disables the name of a model that Versions use, and Delete is disabled for such a model and for the last offered one.
  **Why:** The AC asks only for an order the user controls. Buttons work by keyboard and need no extra lint exception, and the workflow page already established their focus behaviour.
  **Issue:** #114
- **Decision:** The defaults are one `settings` row, `suno.versionDefaults`, holding `{"revision", "defaults"}` behind `IVersionDefaultsStore`. There is no new table and no migration. A missing row means no defaults, at revision 1. `GET /api/v1/settings/version-defaults` needs `catalog.read`, and `PUT` is `SessionOnly()` with If-Match. The answer is `{revision, defaults, ignored, keys, models}`. `keys` lists the options that take a default, and `models` lists the models offered, so the web form needs nothing restated. `EndpointScopeGuardTests` now counts 26 session-only methods.
  **Why:** The discretion line names one settings record with a revision at GET and PUT `/api/v1/settings/version-defaults`. Serving `keys` and `models` lets the form be generated and refreshed from the API.
  **Issue:** #115
- **Decision:** These options take a default: kind, Song mode, Speech mode, and every `VersionInputs` option whose inventory field is a choice, toggle, range, or number (both models included). They are derived as `VersionInputRules.DefaultableKeys`. The Simple form's "add lyrics/styles section" flags are excluded, even though the inventory types them as choices. A PUT naming a text option, such as `title` or `simplePrompt`, or one of those flags, gets 422 `defaults.<key>`.
  **Why:** The AC says text options and reference inputs have no defaults. Those two flags only say whether the Version's own lyrics and styles are attached in Simple mode, so they are about per-Version text, and their inventory values (`write_new`/`use_existing`) do not match the stored boolean.
  **Issue:** #115
- **Decision:** A default is checked the way a Version option is (`VersionInputRules.Errors`), except that a model must be offered (not retired). A value the record already holds is kept without being checked again, so saving other changes never drops a retired model. On read, each stored default that is no longer valid is reported in `ignored` with a reason. At Song creation those defaults are skipped, so the model falls back to the first offered model and other options to the inventory default.
  **Why:** The AC says such a default is kept but ignored, with a notice until it is valid again. Re-validating unchanged values would block every save after a model is retired.
  **Issue:** #115
- **Decision:** `POST /api/v1/songs` takes an optional `inputs` object. Each option sent wins over the defaults, is checked against the whole model list as a Version edit is (422 `inputs.<key>`), and is validated inside the transaction before a shortcode number is taken. `VersionDefaultsService.NewVersionInputsAsync` builds Version 1 for every kind: inventory defaults, the first offered model, then the valid user defaults. `SongService` now takes `VersionDefaultsService`. Creating a Version from another Version is unchanged and copies the source.
  **Why:** The AC says options supplied explicitly at creation (web, API, or MCP) win over defaults. This is additive to the API contract: a request without `inputs` behaves as before. Using the full model list matches the Version edit rule (#114).
  **Issue:** #115
- **Decision:** Web: `settings/SunoDefaultsSection.tsx` sits under the model list on Settings → Suno, in four regions: Every new Song (kind), Song, Speech, and Sound. Each option shows the user's default or Suno's, with a "Use Suno's default" button, and the form saves explicitly with "Save defaults". Kind and the modes use segmented controls, the models `ModelControl`, other choices a native select, toggles a switch, ranges a slider, and BPM a number. Duration (custom) shows only when the Duration default is custom, and Key scale only when the Key default is not Any. A hidden conditional default is kept, not removed. `SunoModelsSection` takes `onChanged`, so the defaults read `ignored`/`models` again after a model is retired, and the drafts are kept.
  **Why:** A whole-record PUT with a revision fits an explicit save better than autosave. The two conditions are the only ones Suno's form has, and the backend's `Effective` spells them out the same way. Keeping a hidden value means switching the condition back finds it again, which is how Version options behave.
  **Issue:** #115
- **Decision:** Invariant check. Invariant 1: defaults are applied only when a Song is created, so they never write an existing Version. The #69 guard exempts `PUT /api/v1/settings/version-defaults` ("one settings row"), and its `POST /api/v1/songs` exerciser now also sends `inputs` (201 on a new Song). `VersionDefaultsService` is in Application/Suno, and its public methods take no catalog type. Invariant 5: the rules are in `VersionDefaultsService`/`VersionInputRules`, and the endpoints only map outcomes. Invariant 7: changing defaults is session-only. No other invariant is touched.
  **Why:** I am recording why no invariant needs a conversation with the user.
  **Issue:** #115
- **Decision:** CI fix for the `web` job on #276 (7 component tests that passed locally). The autosave tests in `VersionInputs.test.tsx` now run timeouts on a fake clock (`fakeTimeouts()` in `test/helpers.tsx`: `setTimeout`/`clearTimeout` faked, a stub global `jest` so Testing Library's waits move the fake clock, user-event gets `advanceTimers`). Their real-time sleeps became `advance(ms)`, and their `timeout`s are now fake time. The two tests before them stay on real time, because CodeMirror's completion list reads the real clock before it accepts Enter. The over-limit test's timeout went from 10 s to 20 s: typing 997 keys takes about 5 s of real work on a loaded machine.
  **Why:** On a loaded runner the 1.5 s pause, the 2 s retry, and the tests' own 4 s windows were all real time, so a timed wait could run out before the save came due. On the fake clock the order of events is fixed. No assertion changed, and no retries or global timeout were added.
  **Issue:** #276
- **Decision:** `test/setup.ts` gives jsdom's `Range` an empty `getClientRects` and `getBoundingClientRect`.
  **Why:** CodeMirror measures text through a Range on an animation frame, and jsdom has no Range geometry. On CI that threw 4 uncaught errors, attributed to whichever test was running. Elements in jsdom already answer this way.
  **Issue:** #276
- **Decision:** Rule 1: `UserMenu` sets `hideDetached={false}`, as the menus in `SongHeader` and `VersionTree` already do. This fixes `SessionGate.test.tsx` "Sign out" (on CI) and "Sign out everywhere" / "stays signed in when signing out fails" (under local CPU stress).
  **Why:** Floating UI's hide check marks the reference as hidden once it positions the open menu in jsdom, where nothing is laid out. Mantine then sets `display: none`, and the menu items become inaccessible. Whether the test clicked first was a race. In a browser the header button is always on screen, so nothing changes for users.
  **Issue:** #276
- **Decision:** `BackupsPage.test.tsx` "shows the schedule…" waits for the `backup-schedule-status` element itself (5 s explicit timeout) and for "Times are in UTC." before it reads the times.
  **Why:** The Schedule heading renders at once. The status follows the backup list, and its times follow the configured zone, which is a separate `/health` request. The test read the status synchronously after the heading (#275).
  **Issue:** #275

## /n8-exec M2 fix pass — 2026-10-05

- **Decision:** #277 is fixed with a test only (`ATokenNeverExpiresLongAfterEverySessionLifetime` in `CredentialEndpointTests`). It uses the token once, advances the `TestClock` 365 days, checks that the session has lapsed (401), and then gets 200 with the token.
  **Why:** The behaviour was already correct. The token is used once first because an idle-expiry change keyed on `last_used_utc` would not fire on a token that was never used, and the bite proof showed that the never-used version missed it. The session check makes sure the 200 can only come from the token.
  **Issue:** #277 (story #56)
- **Decision:** #278 is fixed with a test only (`AnArchivedSongStaysInTheListUnlessAStateFilterLeavesItOut` in `SongEndpointTests`). It archives the Song through `PATCH /api/v1/songs/{id}` rather than SQL, then checks that the unfiltered list still has it and that an Idea filter leaves it out.
  **Why:** The behaviour was already correct. Archiving through the API exercises the path users take. The existing list test only archived through SQL, and its only unfiltered checks after that were paging totals.
  **Issue:** #278 (story #59)
- **Decision:** #283 is fixed with a test only (`EffectiveInputsIsReadOnlyAndSendingItChangesNothing` in `VersionOptionsEndpointTests`). It keeps the current behaviour, where a PATCH carrying `effectiveInputs` answers 200 and changes nothing, and does not change it to a refusal.
  **Why:** The issue accepts either. Answering 200 is how the API already treats unknown members, and changing it to a refusal would change an existing API contract (Rule 4). The test asserts that the inputs, `effectiveInputs`, and revision are unchanged. As a complement, the same option sent in `inputs` is stored.
  **Issue:** #283 (story #111)
- **Decision:** #287 is fixed with a test only (`ThereIsNoLimitOnStatesAndColoursAreReusedOnceAllTwelveAreTaken` in `WorkflowStateEndpointTests`). It adds 8 states through the API, for 15 in all, and each answers 201. States 8–12 take the five free colours in palette order, and states 13–15 take the first palette colour (gray).
  **Why:** The issue asked for 13 or more. Going to 15 also shows that reuse continues past the first repeat. Two bite proofs were run: capping at 12 states, and changing the fallback colour.
  **Issue:** #287 (story #67)
- **Decision:** #279 is fixed with a test only. `BackUpNowWritesAVerifiedArchiveThatRestoresToTheSameRows` now writes `song.mp3` and `album/track 01.flac` into the test host's media path before the backup, and checks that no entry is audio and that both files are unchanged.
  **Why:** The behaviour was already correct. A file in a sub-folder covers a recursive copy of the media mount as well as a flat one. The test only writes under the test's own temporary media path, so the product still never writes there (invariant 2). Bite proof: a `BackupWriter` that also archived the media folder failed the test's exact entry-set check.
  **Issue:** #279 (story #71)
- **Decision:** #288 is a real defect, fixed in product code. `BackupManifest.Validity` gets `[JsonIgnore]`, so manifest.json is written with only format v1's six fields. The format version stays 1, and the reader is unchanged. The regression check is in `BackUpNowWritesAVerifiedArchiveThatRestoresToTheSameRows`, which asserts the manifest's exact property names; it failed before the fix with an extra `validity`. The new `AFormatV1ManifestWithTheStrayValidityFieldStillReadsAndRestores` keeps existing archives readable.
  **Why:** `Validity` is computed on reading and is not part of the format. Removing it from the writer is not a format change, so the version is not bumped. System.Text.Json ignores unknown and `[JsonIgnore]` members on reading, so archives that earlier builds wrote with `"validity": 0` still list as valid, validate, and restore, and the new test proves that.
  **Issue:** #288 (story #71)
- **Decision:** #280 is fixed with a test only (`ARestoreWhoseSafetyBackupFailsChangesNothingAndReopens` in `RestoreRunTests`). It is a Theory with two cases: the safety backup fails its verification (`BackupVerificationException`), or it cannot be written (`IOException`). Both come from `BackupTestHooks.AfterDatabaseCopy`, which throws only while maintenance is active, so only the safety backup fails.
  **Why:** The behaviour was already correct. `RestoreService` has a separate catch for each kind of failure, so each needs its own case. The test checks outcome failed at `safety-backup`, unchanged row fingerprint, sessions, and assets, no `lastRestore`, no safety archive, journal, or work files, and the same session working again. Bite proof: a `RestoreService` that went on past a failed safety backup made both cases fail ("succeeded" instead of "failed").
  **Issue:** #280 (story #74)
- **Decision:** #289 is fixed with one test, `TheLastRestoreNoteOutlivesRefusedAndFailedRestoresAndTheNextRestoreReplacesIt` in `RestoreRunTests`, which covers both cases the issue names. After a successful restore, three restores are tried, and after each one the note must be byte-identical: a start refused for a wrong confirmation (422), a restore whose listed archive was gone when it began (failed at `validating`), and a restore whose safety backup failed (failed at `safety-backup`). Then a restore of a different archive (an upload) must replace the note.
  **Why:** The behaviour was already correct. All three failure paths end before any data is replaced, which is when the note is written. Comparing the raw JSON catches any rewrite of the note, not only a change of outcome. Two bite proofs were run: writing a note when a restore fails (it failed the first equality check), and a `LastRestoreFile` that never overwrites (it failed the replacement check).
  **Issue:** #289 (story #74)
- **Decision:** #281 is fixed with a test only (`NeitherCommandEverPrintsASecret` in `RestoreCommandTests`). Before its backup, the instance gets these secrets:
  - the administrator's password
  - a session cookie
  - an API token
  - the stored password, token, and session hashes
  - lyrics and styles sentinels on a Version
  - a secret environment variable (`N8TRACKS_TEST_SECRET`), now in every run's environment

  The test then runs `list-backups` (success and refusal) and `restore` (each broken-archive refusal, a failure after the swap that is put back, and success). It asserts that no stdout or stderr contains any of the secrets, ignoring case, and that every run printed something.
  **Why:** The behaviour was already correct. The archive is made after the secrets exist, so `restore` handles data that contains them. Two bite proofs were run: `list-backups` dumping its environment failed the test ("list-backups printed a secret of 32 characters"), and `restore` printing the stored token hashes failed it ("restore refused (not-a-zip) printed a secret of 64 characters"). A first restore bite printed `hex()` of a text column, which never matched, and was corrected; it was a mistake in the bite, not a gap in the test.
  **Issue:** #281 (story #75)
- **Decision:** #282 is fixed with a test only (`WhenThePutBackDatabaseFailsItsIntegrityCheckBothFilesStayAndNothingClaimsItWasRestored` in `UpgradeSafetyTests`). It uses a new scenario, `UpgradeThatFailsAndCorruptsTheSafetyBackup`. That scenario's failing migration rewrites the safety archive so its database opens but fails `PRAGMA integrity_check`. Using `writable_schema`, it removes one index's row from `sqlite_master`, which leaves that index's page unused. The manifest checksums are recomputed so that staging passes.
  **Why:** The behaviour was already correct. Removing a schema row gives a file that opens and reports "Page N: never used", so the test reaches the integrity branch of `UpgradeSafetyRestore`, not the "cannot be opened" branch or staging's checksum refusal. The test checks that precondition on the archive itself. It also checks: exit 1, one `safety-restore` error naming the integrity check, and no line saying "was restored" or "passed its integrity check". The half-migrated database is live and sound, there is no `.failed-upgrade` and nothing is left aside, and the marker is at `restoring`. A second start tries again and changes nothing. Bite proof: removing the `CheckIntegrity` call made the test fail, because the start then logged the migration failure as if the backup had been restored.
  **Issue:** #282 (story #76)
- **Decision:** In the #280 test, the byte-for-byte check of the live files (database and `-wal`) is replaced by a check of the assets' names and contents.
  **Why:** In the Release run, the `-wal` bytes changed between snapshots without any change to the data, because SQLite checkpoints the write-ahead log, so the test failed when nothing was wrong. The row fingerprint and session IDs already prove the database is unchanged. With the new check, the test passed three runs in a row.
  **Issue:** #280 (story #74)
- **Decision:** #284 is fixed in `OptionControls.tsx`: a read-only slider (RangeControl and StepsControl) passes `styles.thumb` that keeps the thumb shown (`display: flex`) and draws it in Mantine's disabled colours with a not-allowed cursor. The visible value beside the label stays `aria-hidden`, because the slider's `aria-valuetext` is now what announces it.
  **Why:** Mantine 9.6.3's Slider CSS hides a `[data-disabled]` thumb with `display: none`, which removed the only `role="slider"` element (name, `aria-valuenow`, `aria-valuetext`, `aria-disabled`) from the accessibility tree. Keeping the thumb is a smaller change than un-hiding the label text. Un-hiding the label text would also have announced the value twice whenever the slider is enabled. The regression test is an e2e test, because jsdom applies no Mantine CSS. It is in `song-options.spec.ts`: a Version with options set through the API is frozen with `seed-generation`, Weirdness, Custom duration, and Variety are each found by role and name, visible, disabled, and carrying their values, and axe passes in light and dark. Bite proof: against the image built before the fix, the test failed with `getByRole('slider', { name: 'Weirdness' })` not found. After the fix, it passed in both projects.
  **Issue:** #284 (story #112)
- **Decision:** #291 is fixed with tests only, in `SongOptions.test.tsx`. The frozen test now uses Duration Custom at 2:00 and also checks that the Custom duration slider is disabled and not focusable. It tries to choose Auto and to move the slider with the keyboard, and it checks that Custom and 120 are kept. A new sibling test, "are read only in Simple mode on a frozen Version", checks that the Song description is read only, the Lyrics are read only, the model is disabled, and there are no Add or Remove section buttons. It types into the description, tries Advanced, and checks that nothing changed and 0 writes were sent.
  **Why:** Simple mode cannot be reached on a frozen Version, because the mode cannot be switched, so it needs its own frozen fixture. Bite proof: making the Duration choice, the Custom duration slider, the Simple prompt, and the section buttons editable on a frozen Version failed both tests. After the change was restored, all 7 tests passed.
  **Issue:** #291 (story #112)
- **Decision:** #286 is fixed with a test only. `SongsPage.test.tsx` gains "shows all of a long concept on hover": hovering the concept cell shows a tooltip with the full concept, without the cell having focus, and moving away hides it. The test uses a component test rather than e2e.
  **Why:** Hover is the Tooltip's `events.hover`, which jsdom drives faithfully through user-event. The issue asks for one test of the hover path. Bite proof: `events.hover: false` failed it with `Unable to find role="tooltip"`. After the change was restored, it passed.
  **Issue:** #286 (story #58)
- **Decision:** #290 is fixed with a test only. `BackupsPage.test.tsx` gains "labels each backup with the kind it was made as, safety backups included". It renders safety, scheduled, manual, and unknown-kind rows and checks each `backup-kind` label, including "Safety".
  **Why:** All four branches of the label are covered in one place. Bite proof: changing `BACKUP_KIND_LABELS.safety` to "Safety backup" failed the test with `expected 'Safety backup' to be 'Safety'`. After the change was restored, it passed.
  **Issue:** #290 (story #76)
- **Decision:** A one-off failure of `FrozenVersion.test.tsx`, seen under full-suite load, was filed as #293 and not fixed.
  **Why:** The failure is out of scope and unrelated to the slider styling. The file passed 3 of 3 runs on its own, and the next full suite passed.
  **Issue:** #284
- **Decision:** #293 is fixed in the test only: the four tests of "a Version that freezes while its editor is open" in `FrozenVersion.test.tsx` run on `fakeTimeouts()` (as `VersionInputs.test.tsx` does), and `openVersion` gives user-event `advanceTimers` when the clock is fake. No assertion was changed, loosened, or skipped.
  **Why:** The cause is a race in the test, not in the product: the frozen notice is drawn in one commit and `LyricsEditor` puts the stored text back in a passive effect. On real time, a loaded machine makes React's scheduler run out its 5 ms slice and yield between the two, so `findByTestId('frozen-notice')` (a MutationObserver wait) could end before the editor text was restored. On the fake clock each wait is a step taken inside `act`, which runs the effects. The plain stress recipe (the file 8 and 16 at once beside 16, then 48, `yes` burners: 56 runs) did not reproduce it on this 16-core machine, so it was reproduced deterministically with a scratch Vitest config whose `performance.now()` jumps 6 ms per read (the scheduler always yields): before, 3 of 3 runs failed with the issue's exact assertion at line 206, and also at line 266 (the sibling "409 version_frozen on its own revision" test, the same race); after, 3 of 3 passed, and 16 of 16 at once beside 16 burners passed with and without the skewed clock. The scratch config was removed.
  **Issue:** #293 (story #70)
- **Decision:** A different web flake seen while stressing the full suite (`KindOptions.test.tsx`, the Sound options test timing out at 5 s in 3 of 3 loaded full runs) was filed as #294 and not fixed.
  **Why:** Out of scope for #293.
  **Issue:** #293
- **Decision:** #285 is fixed in product code: `SetupService.CompleteAsync` creates the administrator and writes the backup schedule inside one `IExclusiveTransaction` (the request's DbContext, `BEGIN IMMEDIATE`), and marks setup complete only once that has committed. `SetupService` takes `IExclusiveTransaction` (already registered, scoped). A failed schedule write now rolls the administrator back and the request answers 500; a lost race still answers 409 `setup_already_complete`.
  **Why:** The issue offers "one transaction, or default the schedule when absent"; one transaction keeps "the administrator exists" as the only record of completion and needs no reader to know about a missing row. Regression test `WhenTheBackupScheduleCannotBeWrittenNoAdministratorIsCreatedAndSetupStaysIncomplete` decorates the real store so `WriteAsync` throws; before the fix it failed (`administrators` count "1", expected "0"); after, it passes, with the complement that the same host then completes setup with both rows stored. The concurrent-submissions test still passes.
  **Issue:** #285 (story #52)
- **Decision:** #292 is fixed in the e2e harness. `e2e/support/targets.ts` gives each run an ID (random 8 hex characters, or `N8TRACKS_E2E_RUN_ID` when it is set) and four host ports that the operating system reports free (one synchronous child process listens on port 0 four times, because the config cannot wait). Both values are stored in the environment (`N8TRACKS_E2E_RUN_ID`, `N8TRACKS_E2E_PORTS`) by the first process that loads the module, which is the Playwright runner reading the config, so global setup and the workers inherit the same values. Containers are named `n8tracks-e2e-<role>-<run ID>`. The signed-in states go in `$TMPDIR/n8tracks-e2e-auth-<run ID>/`, which teardown removes. Locally, Playwright's `outputDir` is `test-results/run-<run ID>`, and global setup prunes other runs' folders older than a day. The README section is updated.
  **Why:** Unique names, ports, and output make two runs independent with no lock or waiting. CI is unchanged: the workflow is untouched, `outputDir` is still `test-results` when `CI` is set, the JSON and HTML reports and `N8TRACKS_E2E_LOG_DIR` keep their paths, and retries and `maxFailures` are as before. The only CI-visible change is that container names, and so the container-log file names, end in the run ID. The fixed ports 18787–18790 were dropped rather than kept for CI, so CI exercises the same code path as local runs. A run that is killed outright (SIGKILL) can leave containers behind under its own name; they no longer block later runs, and the README says how to list them. Verification: two `npm test` runs at once against the same `n8tracks:dev` image both passed (62/62 each, 5.2 min), and again on the final code (62/62 each, 6.0 min). The runs used distinct names and ports (for example `n8tracks-e2e-root-268f6d1c` on 62481 and `n8tracks-e2e-root-5eac9ae1` on 62452), left no containers, and pruned a planted day-old `run-oldtest` folder.
  **Issue:** #292 (found by /n8-verify M2; e2e harness of epic M2)

## /n8-exec M3 — 2026-10-05

- **Decision:** One migration, `AddGenresAndSongNotes`, adds `genres` (unique `name_key` = normalised, NFC, upper invariant, with a `length(name) > 0` check), `song_genres` (primary key `(song_id, genre_id)`, an index on `genre_id`), and a nullable `songs.notes` column. `song_genres` cascades with the Song and is RESTRICT on the Genre.
  **Why:** The story names both tables, and Song notes need a column. A Song's assignments have no meaning without it, but a Genre on any Song must never vanish by accident. Removing or merging Genres is #84's, which must move each affected Song's revision on, so the database refuses a bare delete. The column was added with `ALTER TABLE ADD COLUMN`, so no table rebuild was needed and the `versions` triggers are untouched.
  **Issue:** #83
- **Decision:** Genres live in new namespaces `Domain.Catalog` (`Genre`, `GenreRules`) and `Application.Catalog` (`GenreService`, `IGenreStore`); assignment goes through `SongService.UpdateAsync`, which checks the IDs with `GenreService`'s internal `ReadAssignmentAsync` inside the Song's transaction and writes them with `ReplaceSongGenresAsync` after the conditional revision update.
  **Why:** The story's must-haves name `Application/Catalog/GenreService.cs`, and its discretion lines put assignment in the Song PATCH under the Song's revision. Keeping the assignment helpers internal means the invariant 1 guard sees no new public catalog method, and `GenreService`'s public methods take only a name and a token, so the guard's "services elsewhere take no catalog type" rule holds. The guard gained one entry: `POST /api/v1/genres` touches no Version.
  **Issue:** #83
- **Decision:** A Genre ID named twice in `genreIds` counts once; a malformed or unknown ID, `null`, or a non-array is 422 `validation_failed` on `genreIds`. A PATCH whose Genres equal the Song's current set (in any order) changes nothing and keeps the revision.
  **Why:** The AC says "each at most once", so a duplicate is folded rather than refused, and a no-op edit stays a no-op as the other Song fields already behave. The planner's 422 for a vanished ID is extended to every unreadable value, because they are the same mistake from the client's side.
  **Issue:** #83
- **Decision:** Song notes reuse the Version notes rule (`SongRules.NotesErrors` and `NormaliseNotes` delegate to `VersionRules`): up to 10,000 characters, line breaks allowed, trimmed, blank is none. They are edited in place in the panel (an Edit button, then blur or Ctrl/Cmd+Enter saves, and Escape cancels), like the Concept, and they are not added to the redaction list.
  **Why:** The story gives only the limit, and the Version notes already define what the same free-form notes may hold. The convention's redaction list covers lyrics, styles, prompts, tokens, and passwords, and Version notes are not on it either.
  **Issue:** #83
- **Decision:** The Song page now has one `useRevisionedSave` (in `SongPage.tsx`'s `LoadedSong`) for the header and the Details panel; `SongHeader` takes `save`, `states`, and `actions` (the Details button) as props. `SavedField` gained an optional `merge(base, current, mine)`: on a conflict, a mergeable field in the edit is reapplied onto the current value and is not a reason for the dialog. Genres are one such field whose value is the set as JSON sorted by ID (`songs/genreField.ts`), turned into `genreIds` on send.
  **Why:** Two save queues on one record would race each other's revisions. The planner asks that a Genres-only conflict be merged and retried silently while Genres still join the compared fields, and the field-level hook does that without a Genres special case in the shared helper. When another field differs too, the dialog shows Genres with the merged set as "yours".
  **Issue:** #83
- **Decision:** `TokenPicker` (`web/src/common/TokenPicker.tsx`) is a Mantine `Combobox` with `PillsInput`: word-start matching and ordering in `common/tokenMatching.ts`, the first match selected as the user types (Enter takes it), the suggestions closed after each choice, each pill's remove button reachable with Tab (Mantine hides it from Tab and from assistive technology by default), and Backspace in the empty field removing the last token. While a change is saving, the field is read-only and choices wait.
  **Why:** The story asks for a reusable, keyboard-usable picker. Holding choices while a save is in flight keeps a quick second add from being based on the Song's Genres before the first add landed. Closing after a choice was needed because the open list covered the controls below it (the e2e Demo hit this).
  **Issue:** #83
- **Decision:** The Details panel is a 340 px `<aside>` beside the page content at 1100 px and wider, and a right-hand Mantine `Drawer` (titled "Details", close button "Close details") below that. The open state is in `localStorage` `n8tracks.songs.detailsOpen`, which is read and written only beside the editor. Crossing the breakpoint starts again from that rule. The Details button carries `aria-expanded`, and carries `aria-controls` only while the panel exists. Closing the side panel puts focus back on the button; the Drawer does this itself.
  **Why:** These are the planner's breakpoint and width. Ignoring the stored state below the breakpoint, and not storing it there, means a phone never opens a modal by itself. `aria-controls` that names a missing element is an axe failure.
  **Issue:** #83
- **Decision:** The Songs filter bar has a searchable Mantine `MultiSelect` labelled "Genre", whose options are "No Genre" (`none`) and then every Genre. It is mirrored in the URL as repeated `genre=` parameters. When a Genre filter matches nothing, the page says "No Songs match the chosen filters." with "Show every Song", which clears both filters. The state-only wording is unchanged.
  **Why:** The planner calls for a multi-select in the filter bar mirrored in the URL by ID. Keeping the existing state-only message unchanged leaves the M2 tests and e2e assertions as they were.
  **Issue:** #83
- **Decision:** `test/setup.ts` stubs `Element.prototype.scrollIntoView`; the comboboxes set `hideDetached={false}`.
  **Why:** jsdom has no `scrollIntoView`, and Mantine's Combobox calls it when it selects an option. Without `hideDetached={false}`, jsdom hides the dropdown at once, the same issue as #60's Menu.
  **Issue:** #83
- **Decision:** e2e `tests/genres.spec.ts` runs on the shared containers with Genre names that end in a per-run stamp, and types "ind" and "fol" before it creates the stamped names.
  **Why:** Genres apply to the whole instance and the containers are shared, so creating exactly "Indie Rock" would collide on a retry. It runs the Demo steps in order, with axe scans at each state, and adds a narrow-window test of the overlay.
  **Issue:** #83
- **Decision:** Migration `AddGenreRevisions` adds `genres.revision` (INTEGER NOT NULL, default 1 for existing rows). Rename and a merge into a Genre raise it; Genre responses carry `revision`, and rename, merge, and delete answer the ETag and take If-Match on it.
  **Why:** The story's discretion lines have merge and delete send "the target's (or deleted Genre's) revision" and merge "incrementing the target's revision", and Genres had no revision. The column is the minimum the story asks for (Rule 4 does not apply: the story names the revision). `ALTER TABLE ADD COLUMN`, so no table rebuild.
  **Issue:** #84
- **Decision:** Merge refusals are 422 `validation_failed` keyed `sourceIds` (empty, malformed, missing, or containing the target) or `id` (the target does not exist). Delete: 404 for a missing Genre, 409 `genre_in_use` with `songCount`, 422 keyed `reassignTo`/`removeFromSongs` for both choices, a choice on an unused Genre, a malformed, missing, or self `reassignTo`, or a `removeFromSongs` other than `true`/`false`. `removeFromSongs=false` counts as not sent. A successful delete is 204. A rename collision is 409 `genre_name_taken` with `genreId` and the full `genre`. The checks run in this order: revision, then name.
  **Why:** The planner fixed the codes but not every field key, the delete answer, or `false`. 204 matches a DELETE with nothing left to show. `false` read as "not sent" is the least surprising reading of a boolean query flag. The full `genre` saves the client a lookup for the merge offer.
  **Issue:** #84
- **Decision:** A merge or delete moves every Song that had any affected Genre, even one that already had the target (it lost a Genre). A rename moves no Song, and the same name again keeps the Genre's revision. Song counts have no deletion-retention filter yet, because Song deletion (retention) does not exist. The story that adds it must exclude retained Songs in `GenreStore.ListAsync`/`FindAsync` and in `MoveSongsAsync`'s count.
  **Why:** Each such Song's Genre list really changed, so a stale client must get the conflict. The retention clause in the discretion depends on a later story.
  **Issue:** #84
- **Decision:** Settings → Genres (`web/src/settings/GenresPage.tsx`, sidebar after Workflow) is a table with row checkboxes, sortable Name (A→Z first) and Songs (most first) headers, and per-row Rename and Delete. The merge confirmation counts Songs through `GET /api/v1/songs?genre=…&pageSize=1` (`total`). When only one Genre is merged, it uses that Genre's count instead. A rename to a name already on the page goes straight to the merge offer without sending. The API's `genre_name_taken` does the same. An unused Genre is deleted with no dialog. After every change the page reads the list again.
  **Why:** The AC asks for a count of Songs that change. With several sources, the sum of their counts overcounts Songs that have two of them, and the Songs list's any-of-Genres filter already gives the exact number without a new endpoint. A full reread keeps counts right after merges and deletes.
  **Issue:** #84
- **Decision:** Tags copy the Genre shape: `Domain/Catalog/Tag.cs` + `TagRules`, `Application/Catalog/TagService.cs` over `ITagStore`, `Infrastructure/Persistence/TagStore.cs`, and assignment through `SongService.UpdateAsync` (`tagIds`, a full replacement list under the Song's revision, checked by `TagService`'s internal `ReadAssignmentAsync`). `TagRules` delegates the name rules to `GenreRules`; its palette is the twelve `StateColours` names in order, and `NextColour` takes the first one no Tag has, otherwise the one the fewest Tags have (ties by palette order), counting Tags, not Songs. Create runs in the exclusive transaction, so two creates see each other's colour.
  **Why:** The discretion lines give the name rule (same as Genres), the palette ("the twelve named colours from the Songs API story"), and the least-used rule. Reusing the Genre code paths keeps the invariant 1 guard's rules intact (one new exempt entry: `POST /api/v1/tags`).
  **Issue:** #85
- **Decision:** Migration `AddTags`: `tags` (`id`, `name`, unique `name_key`, `colour` with a CHECK that it is a palette name, `revision`) and `song_tags` (PK `song_id, tag_id`; cascade on the Song, RESTRICT on the Tag). `revision` is created now although nothing in this story raises it.
  **Why:** The story authorises the tables. #86 (rename, recolour, merge, delete) needs a per-Tag revision as Genres did in #84; adding it with the table avoids a second migration. The CHECK makes a non-palette colour impossible even from raw SQL.
  **Issue:** #85
- **Decision:** Alphabetical order for Tags is computed in C# (`TagStore.Alphabetical`: `StringComparer.InvariantCultureIgnoreCase`, then ordinal), for the Tag list and for each Song's Tags; the web sorts newly added Tags with `localeCompare('en', { sensitivity: 'base' })`.
  **Why:** The discretion asks for "case-insensitive, invariant culture"; SQLite has no culture-aware collation, and the lists are small.
  **Issue:** #85
- **Decision:** A Tag is drawn as `TagLabel` (`songs/SongParts.tsx`): an outline badge whose name text, outline, and a leading swatch dot are the palette colour's scheme value (`paletteColour`, now in `theme/palette.ts`). The name is always written. The Song page shows them under the title (group "Tags") and in the Details panel's `TokenPicker`, which gained `colourOf` for swatches. The Songs table has a "Tags" column after "Versions" (before "Updated") showing the first three and a focusable "+N" whose tooltip (on the page background colour) shows the rest as labels and whose hidden text names them.
  **Why:** The state colours are already checked as text on the page in both schemes, so a label drawn as coloured text on the page background keeps that contrast (the new `TagLabel.test.tsx` checks each colour as text and as a graphic). A dark default tooltip would have broken it. Putting the column after Versions keeps the existing e2e and component cell indexes unchanged.
  **Issue:** #85
- **Decision:** The Songs filter bar has a "Tag" MultiSelect ("No Tags" = `none`, then every Tag with its swatch), mirrored as repeated `tag=` parameters and combined with the state and Genre filters by AND. "Show every Song" now clears Tags too. An unknown or malformed `tag` is 400 `invalid_request`.
  **Why:** These are the discretion lines (repeatable `tag` of IDs, `none`, OR within the filter, 400 for an unknown ID), with the same UI as the Genre filter.
  **Issue:** #85
- **Decision:** This story adds no Tag management endpoint; the AC's "managing them requires a signed-in session" is satisfied by #86, which must mark its rename/recolour/merge/delete endpoints `.SessionOnly()`. Demo step 3 (Settings → Tags) and the must-have `web/src/settings/TagsPage.tsx` are also #86's. Tag Song counts have no deletion-retention filter yet; the Song deletion story must exclude retained Songs in `TagStore.ListAsync`/`FindAsync`, as for Genres.
  **Why:** The discretion line moves the management criteria, test-plan lines, and must-haves to the sibling story. Song deletion does not exist yet.
  **Issue:** #85
- **Decision:** Tag management copies #84's Genre routes on `/api/v1/tags`, all `.SessionOnly()` under the Tag's own revision in `If-Match`: `PATCH /{id}` takes `name` and/or `colour` (only what is sent changes; neither is 422 on `name`; a non-palette colour, including a wrong letter case or a hex value, is 422 on `colour`), `POST /{id}/merge {sourceIds}`, and `DELETE /{id}?removeFromSongs=true`. A `reassignTo` on delete is 422 on `reassignTo` rather than ignored. `TagService.UpdateAsync/MergeAsync/DeleteAsync` over new `ITagStore.TryUpdateAsync/TryRaiseRevisionAsync/MoveSongsAsync/DeleteAsync`; merge and delete move `song_tags` and raise each affected Song's revision and updated time in the same transaction. No migration: `tags.revision` came with #85.
  **Why:** The discretion line says "same routes, refusals, and merge interaction as the Genre management story" and "no reassignment option for Tags". Refusing `reassignTo` loudly tells a client that expects the Genre behaviour that its Songs were not reassigned. One PATCH for both fields matches the workflow-state edit and keeps rename and recolour under one revision.
  **Issue:** #86
- **Decision:** A merge keeps the target's name and colour and raises only its revision; a name clash on PATCH (another Tag's name in any case) is 409 `tag_name_taken` with `tagId` and `tag`, and the colour sent alongside is not applied either.
  **Why:** AC 2 ("the surviving Tag keeps its colour"); a half-applied edit would be surprising and the UI turns the clash into a merge offer.
  **Issue:** #86
- **Decision:** Settings → Tags (`web/src/settings/TagsPage.tsx`, sidebar after Genres) mirrors Settings → Genres: a table with row checkboxes, sortable Name and Songs, a Colour column naming the colour in words beside the coloured label, and per-row **Edit** (name plus a twelve-colour radio group drawn as labels) and **Delete**. The merge confirmation counts Songs via `songs?tag=…&pageSize=1` and says which colour the survivor keeps. Deleting a Tag in use opens a confirmation stating how many Songs lose it and pointing to merge for reassignment; an unused Tag is deleted directly.
  **Why:** Same warnings as Genres (AC 1). One Edit dialog instead of separate Rename and Recolour buttons follows the workflow-state edit pattern and keeps rows compact; the colour named in words keeps colour from being the only cue.
  **Issue:** #86
- **Decision:** #85's deferred Demo step 3 (recolour "summer", merge "runing" into "running" in Settings → Tags) is walked by this story's e2e `tests/tag-management.spec.ts`, together with #86's Demo step 3 (delete a Tag in use).
  **Why:** #85's discretion line moved Tag management to this story.
  **Issue:** #86
- **Decision:** Artists live in `Domain/Catalog/Artist.cs` + `ArtistRules`, `Application/Catalog/ArtistService` over `IArtistStore`, and `Infrastructure/Persistence/ArtistStore` with one migration `AddArtists` creating the three tables the story names: `artists` (name, `name_key` not unique, notes, created/updated UTC text, revision), `artist_aliases` and `artist_links` (each keyed `(artist_id, position)` for the user's order, cascade on the Artist; aliases unique per Artist on `name_key`; a CHECK keeps link URLs http(s)). An edit rewrites both lists under the Artist's revision (one conditional `ExecuteUpdateAsync` on `artists`, then delete-and-insert the lists).
  **Why:** The discretion lines name the tables and say aliases and links are whole arrays under the Artist's revision; positions give the "user-controlled order" without a sort column; a shared name is allowed after confirmation, so the key cannot be unique.
  **Issue:** #87
- **Decision:** API: `GET /api/v1/artists?search=&page=&pageSize=` (`{items,page,pageSize,total}`, 50 per page by default, at most 100) and `GET /api/v1/artists/{id:guid}` need `catalog.read`; `POST /api/v1/artists` and `PATCH /api/v1/artists/{id:guid}` (If-Match) need `collections.write` (not session-only). A response is `{id,name,aliases[],notes,links[{label,url}],songCount,albumCount,createdAt,updatedAt,revision}`. The 409 `duplicate_artist_name` lists `matches[{id,name,matchedText,matchedOn:"name"|"alias"}]`; `confirmDuplicate` is a body boolean. In a PATCH, a null name is 422, null aliases/links/notes clear them.
  **Why:** AC 5 and the key_links line; `matchedText`/`matchedOn` answer "which name or alias matched". The route uses the GUID only, since Artists have no shortcode (added to `ReferenceParameterGuardTests.OtherIds`).
  **Issue:** #87
- **Decision:** The duplicate check compares only the names and aliases the Artist did not already have (by name key) against every other Artist's display name and aliases, after the revision check. So an edit that only changes notes or links, or re-cases the Artist's own name, never asks again, even when the Artist already shares a name.
  **Why:** Discretion lines: "fires only when the name or aliases change", "an edit that changes neither name nor aliases never re-prompts", "a stale revision is reported first". Checking only new keys avoids re-asking about a duplicate the user already confirmed when they add an unrelated alias.
  **Issue:** #87
- **Decision:** Name sort is by `name_key` (NFC + upper-invariant) in SQL, then `created_utc`, then ID; search is a substring (`instr`) of the search text's name key in the Artist's `name_key` or any alias's `name_key`. A blank search lists every Artist.
  **Why:** Paging needs the order in SQL. Comparing the stored keys gives "ignoring case" and the creation-time tie-breaker the discretion line asks for. It differs from the Tag list's in-memory culture-aware order for non-ASCII initials (É sorts after Z).
  **Issue:** #87
- **Decision:** `songCount` and `albumCount` are always 0, and the Artist page's Songs and Albums sections are headed regions saying "No Songs/Albums are credited to this Artist yet." No list endpoints for the sections were added.
  **Why:** No credit or Album tables exist yet. The discretion line says the credit and Album stories fill the sections, and they will define the queries and response shape (role shown). Endpoints added now that always return empty lists would fix a contract before those stories exist.
  **Issue:** #87
- **Decision:** Web: an "Artists" sidebar entry under Songs; `/artists` (search box and page in the URL as `search`/`page`, the list API's own parameters; typing replaces the history entry) and `/artists/:id`. The page edits name, aliases (rows with Remove), notes, and links (label + URL rows with Move up/down and Remove) in one form with an explicit Save, through `useRevisionedSave` (aliases and links compared as JSON text). A duplicate on save opens a confirmation dialog listing the matches ("Save anyway" resends with `confirmDuplicate`). On create, the New Artist dialog asks for the name only and shows the matches inline, with "Create anyway". `failureOf` in `api/saves.ts` is now exported for `updateArtist`.
  **Why:** AC 1 and 7, and "Create is a dialog that opens the new Artist's page". An explicit Save keeps one duplicate prompt per change rather than one per keystroke or blur. Reusing the shared save helper keeps the conflict dialog the same as on every other editing screen.
  **Issue:** #87
- **Decision:** One migration `AddSongArtistCredits` adds `song_artist_credits(song_id, artist_id, role, position)`:
  - The primary key is `(song_id, artist_id)`, so an Artist is credited once per Song, which also rules out being both primary and featured.
  - It is unique on `(song_id, role, position)`. A CHECK allows only `primary` at position 0, or `featured` at position 0 or more.
  - It cascades with the Song and RESTRICTs on the Artist. The Artist deletion story decides what happens to an Artist's credits.
  The rules are in `Domain/Catalog/SongCreditRules` (at most 50 featured). `Application/Catalog/SongCreditService` sits over `ISongCreditStore` and `ICatalogSettingsStore`.
  **Why:** The discretion line names the table and its columns, and #88 is the story that creates it. Putting the uniqueness in the key enforces the AC rule in the database as well as in the service.
  **Issue:** #88
- **Decision:** API:
  - `PUT /api/v1/songs/{reference}/credits` (`songs.write`, If-Match on the Song's revision) takes `primaryArtistId` and `featuredArtistIds`. Both are required, the first may be null, and a missing or wrongly typed one is 422.
  - An unchanged set is 200 without a new revision; any other write raises the Song's revision and `updatedAt`.
  - Song responses (list rows included) carry `credits{primary,featured[]}`, each Artist as `{id,name}`.
  - The list takes a repeatable `artist` (an ID or `none`, OR-combined). An unknown Artist ID is 400, as for `genre` and `tag`.
  - `POST /api/v1/songs` takes `primaryArtistId`, bound as raw JSON so missing and null differ.
  - Artists' `songCount` is now real.
  **Why:** These are the discretion lines. Requiring both fields keeps the whole-set write unambiguous.
  **Issue:** #88
- **Decision:** The default Artist is stored in the settings row `catalog.defaultArtistId` as `{"revision":n,"artistId":"<id>"|null}`. `GET`/`PUT /api/v1/settings/catalog` are SessionOnly and answer `{revision, defaultArtist{id,name}|null}`. A stored Artist that no longer exists shows as null and is not applied. `SongService.CreateAsync` reads the default inside its creation transaction, before the shortcode is taken.
  **Why:** These are the discretion lines (settings key, session-only, record revision, ignored when gone) and the key_link ("in the same transaction"). An import from Suno must send an explicit null: it does not exist yet (M4), so `SongRequest.PrimaryArtistId` documents this.
  **Issue:** #88
- **Decision:** Deletion retention does not exist yet. A default whose Artist row is gone is already ignored and shown as none. The Artist deletion story must also treat an Artist in retention as gone in `SongCreditService.ViewAsync`/`PrimaryForNewSongAsync`.
  **Why:** The discretion line about retention and restore cannot be implemented before retention exists. The hook is a single place.
  **Issue:** #88
- **Decision:** Web:
  - A Credits section (Primary Artist, Featured Artists) heads the Details panel.
  - The new `common/ArtistPicker` searches `GET /artists?search=` as the user types (10 suggestions), leaving out the Artists already credited.
  - With `allowCreate` it always offers "Create Artist “X”", even when a suggestion has that name: Artist names are not unique. A 409 duplicate shows an inline group with "Use <match>", "Create another “X”", and "Cancel".
  - Featured Artists are reordered with Move up and Move down buttons only, not by dragging. Make primary and Remove are on each row.
  - Credits are one field of the Song page's single `useRevisionedSave`, sent with `PUT …/credits`. There is no merge function, so a credits conflict shows in the conflict dialog.
  **Why:**
  - Artist lists can be large, so suggestions are fetched rather than loaded whole.
  - The discretion line allows "drag or Move up and Move down", and buttons are the accessible form.
  - The order of featured Artists is the user's, so reapplying a change silently could reorder someone else's edit.
  - The e2e Demo showed that hiding create for an exact name match made the duplicate confirmation unreachable for the most common duplicate, the same name.
  **Issue:** #88
- **Decision:** Web, continued:
  - Settings → Catalog is a new page with sidebar order Account, Credentials, Workflow, Catalog, Genres, Tags, Suno, Backups, System. It has a picker that saves at once, and "Clear the default".
  - The New Song dialog reads the default each time it opens and always sends what it shows (the default, another Artist, or null). When the settings cannot be read it sends nothing, so the API applies the default.
  - The Songs table gets an Artist column (the primary Artist) after Title, and an Artist MultiSelect filter whose suggestions come from the API.
  - The Artist page's Songs section is a table of Song, shortcode, and role (Primary or Featured), read from `GET /songs?artist=<id>&sort=title`. When there are more than 50 Songs it links to the filtered Songs table.
  **Why:**
  - AC 3, 4, 6, and 9.
  - Reusing the list endpoint avoids a new read contract, because rows already carry credits and the role follows from them.
  - Placing the Artist column after Title shifted the e2e `songs.spec.ts` cell indices, which were updated.
  **Issue:** #88
- **Decision:** One migration `AddAlbums` adds `albums` and `album_links`. `albums` holds the title (plus `title_key`, NFC and upper-cased, for sorting), the description, `album_artist_id` (optional; FK to `artists` with RESTRICT), the release date and original release date as text, `upc` (digits) and `upc_key` (the 13-digit form, indexed but not unique), copyright, publishing, the created and updated times, and `revision`. CHECKs require a non-empty title and a UPC of 12 or 13 digits. `album_links` is keyed `(album_id, position)` and cascades with the Album. The rules are in `Domain/Catalog/AlbumRules`; `Application/Catalog/AlbumService` sits over `IAlbumStore`.
  **Why:** The story's must-haves name `AlbumService`, and the Album is a new record, so the story implies its table. RESTRICT on the Album Artist follows the credit table: the Artist deletion story decides what happens. Links reuse the Artist link rules through an internal `ArtistRules.LinkErrors(links, owner)` overload, so the count message names the Album.
  **Issue:** #89
- **Decision:** API:
  - `GET /api/v1/albums?sort=title|releaseDate|artist&direction=asc|desc&page=&pageSize=&artist=<id>` and `GET /api/v1/albums/{id:guid}` need `catalog.read`.
  - `POST /api/v1/albums` takes `{title}` only, and `PATCH /api/v1/albums/{id:guid}` (If-Match) takes `title`, `description`, `albumArtistId`, `releaseDate`, `originalReleaseDate`, `upc`, `copyright`, `publishing`, and `links`. Both need `collections.write`, and neither is session-only.
  - A response is `{id,title,description,albumArtist{id,name}|null,releaseDate,originalReleaseDate,upc,copyright,publishing,links[],songCount,createdAt,updatedAt,revision,warnings[]}`. A shared UPC/EAN gives `warnings: [{code:"duplicate_upc", field:"upc", message, albums:[{id,title}]}]` on GET, PATCH, and the list rows.
  - An unknown `albumArtistId` is 422 on that field. An `artist` filter naming no Artist lists nothing, rather than 400.
  **Why:** These are the AC and discretion lines: a title-only create dialog, per-field saves, `warnings` on GET and PATCH, and `catalog.read` for reads. Putting `warnings` on every Album answer, list rows included, keeps one response shape. The Artist page's Albums section reads the list's `artist` filter instead of using a new endpoint.
  **Issue:** #89
- **Decision:** List order. Titles sort by `title_key`, then created time. Release date sorts by the release date, else the original release date, compared as text. Text order matches the earliest possible day ("2026" < "2026-01" < "2026-01-02"), so the sort runs in SQL. Artist sorts by the Album Artist's `name_key`. Missing values go last in both directions, with title and then created time as tie-breakers.
  **Why:** These are the discretion lines. Comparing as text is exact for the three stored forms, and needs no extra column.
  **Issue:** #89
- **Decision:** A UPC/EAN is checked with the GS1 mod-10 check digit after removing spaces and hyphens. The 12- and 13-digit forms share a key: a 12-digit code is prefixed with "0". Deletion retention does not exist yet, so every Album counts toward the duplicate warning. The Album deletion story must leave retained Albums out of `AlbumStore.DetailsAsync`'s same-UPC query. Partial dates are validated as `YYYY`, `YYYY-MM`, or `YYYY-MM-DD`, years 1000 to 9999, and a real day. They are stored trimmed, as entered.
  **Why:** These are the AC and discretion lines. The retention hook is a single query.
  **Issue:** #89
- **Decision:** Artists' `albumCount` now counts the Albums the Artist is Album Artist of. The Artist page's Albums section, previously a placeholder, is now a table of those Albums (title link and shown date), read from `GET /albums?artist=<id>`. Its empty text is now "This Artist is not the Album Artist of any Album yet.", and e2e `artists.spec.ts` was updated to match.
  **Why:** AC 8. The track story may later widen the count to Albums holding the Artist's Songs; for now the count matches the section it summarises.
  **Issue:** #89
- **Decision:** Web:
  - The sidebar order is Songs, Artists, **Albums**, then Settings, asserted in `AppShell.test.tsx` and e2e `account.spec.ts`. The routes are `/albums` (sort, direction, and page in the URL) and `/albums/:id`.
  - The Album page saves each text field on its own when it loses focus, or on Enter in a one-line field, through one `useRevisionedSave`.
  - The Album Artist is a field holding `{id,name}` as JSON. It is chosen with `ArtistPicker allowCreate` and has a "Clear the Album Artist" button.
  - Links are one JSON list field, saved with a "Save links" button.
  - Dates are shown localised with the browser's locale through `Intl.DateTimeFormat`: "March 1, 2026" in US English, "1 March 2026" in British English.
  - `RowControls` and `move` moved from `ArtistPage.tsx` to `common/RowControls.tsx` and `common/listMove.ts`, to be shared.
  **Why:**
  - The discretion line asks for individual saves through the shared save helper.
  - A list of links is edited as a whole, the way the Artist page edits them, so it is not saved row by row.
  - The discretion line gives example formats without a locale, and the app has no locale setting.
  **Issue:** #89
- **Decision:** Playlists are the tables `playlists` (`title_key` for the order, `revision`) and `playlist_songs` (PK `(playlist_id, song_id)`, which enforces one entry per Song, and a unique `(playlist_id, position)` with positions from 0 and no gaps). One migration, `AddPlaylists`. Both foreign keys cascade: a Playlist's entries go with it, and a Song's entries go with the Song. Every membership change rewrites the Playlist's entries as a whole, under a conditional revision bump, and never writes a `songs` row.
  **Why:** The discretion lines name both tables and an integer position. An entry is membership only, so it should not block deleting a Song; the Song deletion story can still change this when it adds retention. Rewriting the whole list avoids unique-position clashes while renumbering, and a Playlist holds at most 1,000 Songs. Song revision and updated time stay unchanged (AC 6), and a test checks this.
  **Issue:** #90
- **Decision:** API:
  - `GET /playlists` returns summaries, by title and paged (50, max 100), without `songs`.
  - `GET /playlists/{id}` and every write return the whole Playlist with `songs[{id, shortcode, title, primaryArtist, state, hasSelectedGeneration}]` in order.
  - `POST .../songs` takes `songId` as an ID or a shortcode. Removal is `DELETE .../songs/{reference}`, where the route parameter is a `CatalogReference`. `PUT .../songs` takes `songIds` by ID or shortcode.
  - Refusals: a duplicate is 409 `song_already_on_playlist`. A 1,001st Song is 409 `playlist_full`. A reorder that does not name exactly the members, each once, is 409 `order_mismatch` (the workflow-states code), and a reference that names no Song counts as a mismatch. Each of these carries `current`.
  - An unknown Song on add is 422 `songId`. On remove, an unknown Song is 404, and a Song that exists but is not on the Playlist is 200 with no change and no revision bump.
  **Why:** These follow the discretion lines. A Song is named by its ID or shortcode everywhere, which `ReferenceParameterGuardTests` enforces. A removal that is a no-op is idempotent, which lets the guard's by-ID and by-shortcode call answer the same way.
  **Issue:** #90
- **Decision:** "Selected Generation" does not exist yet: it arrives in M4. `hasSelectedGeneration` is always false for now (set in `PlaylistStore.FindAsync`), so every Song on a Playlist shows the "No Selected Generation" indicator, and the UI hides it when the flag is true. The flag is not a stored column.
  **Why:** AC 4 needs the indicator now. Selection is M4 work, which will only need to compute the flag in that one place.
  **Issue:** #90
- **Decision:** `GET /api/v1/songs?q=` matches when the title contains the text (case-insensitive, compared on `title_sort_key`: NFC, lower invariant) or when the shortcode `n8-<n>` starts with it (case-insensitive; "n", "n8", and "n8-" match every Song). It combines with the other parameters by AND. With `q`, the default page size is 10, and an explicit `pageSize` up to 100 is still allowed. A blank `q` answers an empty page with total 0. `q` given twice is 400. `SongListRequest`/`SongListQuery` gained trailing optional `Query`/`Search`.
  **Why:** These are the discretion lines. Keeping `pageSize` lets the Album-tracks and relationship stories ask for more if they need to. The shortcode rule is a literal prefix match on the full shortcode.
  **Issue:** #90
- **Decision:** Song responses embed `playlists[{id,title}]`, by title, on every Song answer, list included. `SongSummary` has a trailing `Playlists`, and the web `Song` requires `playlists` (fixtures: `playlists: []`). The Details panel's new last section, "Playlists", lists them as links, or says "Not on any Playlist."
  **Why:** AC 5 and the discretion line ("embedded in the Song response"). The list loads them in one extra batched query per page.
  **Issue:** #90
- **Decision:** Web:
  - There is a sidebar entry Playlists, after Albums, asserted in `AppShell.test.tsx` and e2e `account.spec.ts`. Routes are `/playlists` (`?page=`) and `/playlists/:id`.
  - The shared Song search is `common/SongSearch.tsx`: a combobox over `?q=`, showing shortcode, title, and primary Artist. `unavailable` IDs are shown disabled with a note.
  - `SavedTextField` moved from `AlbumPage.tsx` to `common/SavedTextField.tsx`, so the Album and Playlist pages share it.
  - Song rows reorder by HTML drag-and-drop, or by "Move <title> up/down" with focus kept. Each add, remove, and reorder saves at once. On `revision_conflict`/`order_mismatch`, the Playlist is replaced with `current` and a "Not changed" notice is shown. A duplicate shows "<title> is on this Playlist already."
  **Why:** These are the discretion lines. Drag-and-drop follows the Workflow page's pattern, with no new dependency.
  **Issue:** #90
- **Decision:** One migration `AddAlbumTracks` adds `album_songs` (`album_id`, `song_id`, `disc`, `track`). The PK `(album_id, song_id)` keeps a Song on an Album once. A unique `(album_id, disc, track)` stops two tracks on a disc sharing a number. CHECKs hold disc and track to 1–999. Both FKs cascade, as `playlist_songs` does. Every change rewrites the Album's rows as a whole, under a conditional bump of the Album's revision and `updated_utc`, and never writes a `songs` row. The numbering rules are pure functions in `Domain/Catalog/AlbumTrackRules` (`NextPlace`, `CloseDiscGaps`, `Renumber`, `Without`, `FindClash`). `AlbumTrackService` sits over the new `IAlbumTrackStore` and reads the Album through `IAlbumStore.FindAsync`.
  **Why:** The discretion lines name the table and its columns and say there is no position column. Rewriting the list as a whole avoids clashing with the unique index halfway through a renumber. Song revision and updated time stay unchanged (AC 5), and a test checks this.
  **Issue:** #91
- **Decision:** API:
  - Add is `POST /albums/{id}/tracks {songId}`, where `songId` is an ID or a shortcode. Remove is `DELETE /albums/{id}/tracks/{reference}`, not `{songId}`: the reference guard requires a Song in a route to bind `CatalogReference`, as the Playlist DELETE does. The full list is `PUT /albums/{id}/tracks {tracks:[{songId,disc,track}]}`. All three need `collections.write`, none is session-only, and each uses and raises the Album's revision.
  - Refusals: 409 `song_already_on_album`; 409 `track_number_taken`, whose title names the holder ("Track 1 on disc 1 is held by First (n8-1).") and which carries `heldBy`; 409 `disc_full` past track 999 on the last disc; 409 `order_mismatch` (the Playlists code) for a list that is not exactly the members. Each carries `current`. Numbers outside 1–999, non-integers, or missing fields are 422 on `tracks`. An unknown Song on add is 422 `songId`. On remove, an unknown Song is 404, and a Song that is not on the Album is 200 with no change.
  - The holder of a clashing number is the entry that held it before the request. When neither did, it is the earlier one in the list.
  **Why:** These are the discretion lines. The DELETE route follows #90's precedent and the guard. Reusing `order_mismatch` keeps one code for "the list must name exactly the members".
  **Issue:** #91
- **Decision:** The server applies a PUT as sent, except that it closes disc gaps. It does not renumber a disc a track left during a PUT. The client renumbers before sending: drag, Move up/down, Move to disc, and Renumber. Removal renumbers on the server, because DELETE carries no list.
  **Why:** The discretion line makes the PUT the explicit full list. If the server renumbered, it would override numbers an API client typed on purpose. AC 12 holds for the UI and for DELETE, and both the component and API tests check it.
  **Issue:** #91
- **Decision:** Tracks are embedded in every Album answer, including list rows, as `tracks[{songId, shortcode, title, primaryArtist, state, disc, track, hasSelectedGeneration}]`. `songCount` is now the number of tracks. `AlbumDetails` has a trailing `Tracks`. `hasSelectedGeneration` is always false until M4 and is set in `AlbumTrackStore.ForAlbumsAsync`. Song answers embed `albums[{id,title,disc,track}]`, ordered by Album title (`SongSummary` has a trailing `Albums`; the web `Song` requires `albums`, and fixtures use `albums: []`).
  **Why:** The discretion line says tracks are embedded in `GET /albums/{id}`. PATCH and the conflict `current` use the same response, so the Album page never loses its tracks. Putting them in list rows too keeps the single Album shape #89 chose. The Song membership shape is the discretion line's.
  **Issue:** #91
- **Decision:** Web:
  - `albums/TrackList.tsx` sits below the Album's fields and shows one ordered list per disc (named "Disc N"), in track-number order.
  - Each row has: a track-number field (saved on blur or Enter; 1–999 is checked before sending); the shortcode link, title, primary Artist, and state; an "Incomplete: no Selected Generation" badge; Move up/Move down; a "Move to disc" menu offering the other discs and "New disc N+1" (hidden when the track is alone on the last disc); and Remove.
  - Drag-and-drop works only within a disc.
  - "Renumber" is disabled while every disc is already 1, 2, 3….
  - The pure reordering helpers are in `albums/trackOrder.ts`.
  - On `revision_conflict`/`order_mismatch` the Album is replaced with `current` and a notice is shown. A refused typed number restores the field and shows the API's message, which names the holder.
  - The Song Details panel gains an "Albums" section before "Playlists": "<Album link>, disc D, track T".
  **Why:** These are the discretion lines: Move up/Move down for the keyboard, drag within a disc, and the shared Song search with the disabled state computed in the client. Showing a disc heading even for a single disc makes "disc 1" visible as the Demo needs.
  **Issue:** #91
- **Decision:** The nine system relationship types follow #92's AC as amended by spike TS-002: Cover, Extend, Reuse Prompt, Mashup, Sample This Song, Use as Inspiration, Voice (Suno action keys `cover`, `extend`, `reuse_prompt`, `mashup`, `sample`, `inspiration`, `voice`), then Remix (the general type for imported clips whose action is not recognised; no key) and Derived From (no key). They are seeded by the migration with fixed IDs (`Domain/Catalog/SystemRelationshipTypes`, `01a10a6e-de0N-…`), and are listed in that order before the user's types (alphabetical).
  **Why:** The AC, the owner-approved TS-002 planning comment, and the discretion's reverse names and action keys all agree. With fixed IDs, M4's import and the tests can name a system type without looking it up.
  **Issue:** #92
- **Decision:** Two tables, from the discretion line, in migration `AddSongRelationships`:
  - `song_relationship_types`: `name`, `name_key`, `reverse_name`, `reverse_name_key` (each unique), `is_system`, `suno_action` (allowed only on system types), and `revision`.
  - `song_relationships`: `id`, `type_id` (RESTRICT), `from_song_id`, `to_song_id` (both CASCADE with the Song), and `created_utc`. A CHECK makes the two Songs differ.
  - A relationship is stored in the direction its forward name reads. The unordered pair is unique per type through a SQL expression index, `ux_song_relationships_pair` on `(type_id, min(from,to), max(from,to))`. EF cannot express that index, so the migration writes it, and a migration that later rebuilds the table must create it again.
  **Why:** The AC says "unordered for uniqueness", and the database should refuse a reversed duplicate even if a later path writes rows without going through the service. Cascading on the Song matches Playlist and Album memberships, and the Song deletion story decides about retention. RESTRICT on the type means a type is deleted only by the service, which also moves the affected Songs.
  **Issue:** #92
- **Decision:** Type names:
  - A reverse name equal to the forward name, ignoring case, makes the type symmetric. It is stored with one name, shown once in pickers, and shown as "Same both ways" in Settings.
  - A name taken by any type in either direction, system types included, is 422 `validation_failed` on `name` or `reverseName`. The message names the holder. It is not a 409.
  - PATCH takes `name` and/or `reverseName`. A field not sent keeps its value, except that a symmetric type renamed without `reverseName` stays symmetric.
  **Why:** The discretion line makes names unique across forward and reverse names, and lets the reverse name equal the forward name. Treating a case-only difference as symmetric avoids a pair of names that differ only in case. The story defines refusal codes only for relationships and for system types, and a taken name is a field error in a form, so 422 lets the UI show it beside the field.
  **Issue:** #92
- **Decision:** Type endpoints are `GET /api/v1/relationship-types` (`catalog.read`), plus `POST`, `PATCH /{id:guid}`, and `DELETE /{id:guid}`. The three writes are SessionOnly, so the session-only count is now 37. PATCH and DELETE take If-Match on the type's revision.
  - A system type gets 409 `system_type` (with `current`) on PATCH or DELETE, whatever the revision.
  - Deleting a type in use without `?removeRelationships=true` is 409 `relationship_type_in_use` with `relationshipCount`. That is the count the UI's confirmation states.
  - `removeRelationships=true` on an unused type just deletes it. Tags give 422 there, but a race should not turn a confirmed delete into an error.
  - Deleting a type removes exactly its relationships and moves each affected Song's `updated_utc`, not its revision.
  **Why:** These are the discretion's routes and the `system_type` code. The confirmation mirrors Tags' `removeFromSongs`, which turns the AC "asks for confirmation, stating how many relationships" into an API contract. Moving the Songs' updated times on removal follows the discretion line "removing a relationship moves both Songs'".
  **Issue:** #92
- **Decision:** Relating Songs:
  - `POST /api/v1/songs/{reference}/relationships` takes `{typeId, direction, otherSong}`. `otherSong` is an ID or a shortcode.
  - It answers 201 with this Song as `SongResponse`, now carrying `relationships[{id,typeId,name,direction,song{id,shortcode,title}}]`, and a Location ending in `/relationships/{id}`.
  - `DELETE .../relationships/{id:guid}` answers 200 with this Song, and works from either of the relationship's two Songs.
  - Both need `songs.write`, and neither takes or raises a revision. Both move both Songs' `updated_utc`.
  - Refusals: self is 422 on `otherSong`; a duplicate either way round is 409 `relationship_exists` with `current`; an unknown type or other Song is 422; an unknown Song or relationship is 404.
  - `SongSummary` has a trailing `Relationships`, sorted by the name seen from that Song (ignoring case), then by the other Song's title.
  **Why:** These are the discretion's routes, codes, and the "moves last-updated times but no revision" rule. Answering with the Song lets the Details panel replace its copy without a second request. No revision is taken because the Song's revision does not change.
  **Issue:** #92
- **Decision:** Web:
  - `settings/RelationshipsPage.tsx` (Settings → Relationships, between Tags and Suno in the sidebar) has a table of types. System rows show a "System type" badge and no actions. User types have Rename and Delete, and the page has an "Add a type" form. Name clashes are checked in the client against every type's names in both directions.
  - Deleting a type in use opens a dialog stating "N relationships use …".
  - `songs/RelatedSection.tsx` is the Details panel's "Related" group, placed after Notes. It groups relationships by name and links each other Song. A Relationship picker lists every type in each direction, a symmetric type once, grouped as "System types" and "Your types". The shared `SongSearch` then finds the other Song: it is disabled until a type is chosen, leaves out this Song (new `exclude` prop), and disables Songs already related under the chosen type. Removing a relationship needs a confirmation dialog.
  - Web `Song` requires `relationships` (fixtures `relationships: []`). `SongSearch` gained `exclude` and `disabled`. The `songServer` fake serves types, search, and relationship writes.
  **Why:** These are the discretion lines: the shared search, excluding this Song, disabled only under the chosen type, direction picked by name, grouping and order. The AC asks for confirmation before removal. "Not related to any Song." matches the other Details sections.
  **Issue:** #92
- **Decision:** Release details API:
  - `PATCH /api/v1/songs/{reference}` takes a `release` object. Each member (`releaseDate`, `originalReleaseDate`, `explicit`, `copyright`, `publishing`, `isrc`, `language`, `links`) is optional; null or blank clears it; `links` replaces the whole list.
  - Errors are keyed `release.<member>`. A `release` that is not an object is 422 keyed `release`.
  - Every Song answer (GET, PATCH, list rows, and a conflict's `current`) has `release`, with nulls for unset members, and `warnings`.
  - `warnings` is `[{code:'duplicate_isrc', field:'release.isrc', message, songs[{id,shortcode,title}]}]`, with the other Songs by title.
  **Why:** The story's key link is "a `release` object in the Song payload". Keying errors by member path follows the `inputs.<key>` precedent, so the web save helper can use the same key. `SongResponse` is one shape everywhere, so the warning rides on every answer, not only GET and PATCH. The extra query is a single one per page.
  **Issue:** #93
- **Decision:** Storage: nullable columns on `songs` (`release_date`, `original_release_date`, `explicit_content` as `explicit`/`clean`, `copyright`, `publishing`, `isrc`, `language`), a non-unique index on `isrc`, and a `song_links` table (`song_id`, `position`; cascade; URL CHECK like `album_links`). Migration `AddSongRelease`. The new `songs` columns carry no CHECK constraints.
  **Why:** The discretion line gives columns plus a `song_links` table. In SQLite, EF can add a CHECK constraint only by rebuilding `songs`, which many tables reference, so the rules stay in `SongReleaseRules` and the plain `ADD COLUMN` migration avoids a rebuild.
  **Issue:** #93
- **Decision:** ISRC input: case, whitespace, and hyphens are ignored, and a leading "ISRC" is dropped only when more than 12 characters remain with it. A wrong length gets its own message, which names the count.
  **Why:** A valid ISRC can itself begin with "ISRC" (country IS, registrant "RC…"), and stripping it unconditionally would break that code. The Demo's eleven-character case is clearer with the count named.
  **Issue:** #93
- **Decision:** Language: the bundled list is `Domain/Catalog/Languages` (183 ISO 639-1 codes with English names, plus `zxx` "No linguistic content", sorted by name). It is served by `GET /api/v1/languages` (`catalog.read`; `EndpointScopeGuardTests` updated) straight from that list. Input is accepted in any case and stored lower case.
  **Why:** The discretion line requires one server-side list for both validation and the picker. It is static reference data with no rules of its own, so the endpoint reads the same Domain list `SongReleaseRules` validates against, and no extra application service is needed. Case-insensitive input costs nothing, and the stored code stays standard.
  **Issue:** #93
- **Decision:** Web:
  - `songs/details/ReleaseSection.tsx` is a "Release" group in the Details panel, after Notes.
  - Dates and rights text reuse the Album helpers in `albums/albumRules.ts`. Dates show "Shown as …" as Albums do.
  - Explicit is a three-way Radio group (Not set, Explicit, Clean).
  - Language is a `NativeSelect` with "Not set" first.
  - Explicit and Language show the choice as made while it saves, and fall back to the Song's own value if the save fails.
  - ISRC shows the duplicate warning, which links to each other Song.
  - Album's links editor moved to `common/LinksEditor.tsx`, and both pages now use it.
  - The save helper's keys are `release.<member>` (`songs/details/releaseField.ts`), and `SongPage`'s `songEditOf` folds them into the `release` object. The conflict dialog names each member.
  **Why:** One save path per Song is the existing contract. A native select is keyboard type-ahead accessible, works the same in jsdom and the browser, and needs no Combobox workarounds for 184 fixed options. The pending display came from the e2e walk: a controlled radio that only changed after the save made `check()` fail and felt unresponsive.
  **Issue:** #93
- **Decision:** #296 is fixed in the e2e accessibility helper, not in the Tag colours. `animationsFinished` in `e2e/support/a11y.ts` now waits until no finite animation is running on two checks in a row, two frames apart, with a cap of 100 rounds. Before, it took a single look at `document.getAnimations()`. The axe rules and scan scope are unchanged.
  **Why:** The cause is a transition artefact, not low contrast. Mantine mounts a tooltip at opacity 0 and starts its fade a frame or two later. In a probe of 40 openings of the "+N" Tag tooltip, the old wait returned 8 times with nothing running and the tooltip at opacity 0. Axe then measured the labels part-way through the fade, blended with the background, and the red and pink labels, which have the smallest contrast margin, fell below 4.5:1. With the new wait the probe found the fade every time, and the tooltip was at opacity 1 when the wait returned (40 of 40). At rest, the palette's contrast is already test-enforced in both schemes. Fixing it in the shared helper covers every spec that scans a state that has just opened.
  **Issue:** #296
- **Decision:** The stored key is `songs.title_key` (indexed, not unique). It is written on create and on every title edit from `SongRules.TitleKey`, which delegates to `GenreRules.NameKey`: trim, collapse inner white space, NFC, and upper-case invariantly, so diacritics and width stay significant. The migration `AddSongTitleKey` backfills existing rows with the same C# rule. It does so through a SQLite function, `n8_title_key(title)`, which `ConnectionSettingsInterceptor` registers on every connection the app opens. Migrations at startup, during a restore, and from the design-time factory all open their connections that way.
  **Why:** The discretion line requires a key computed in application code and backfilled by the migration. SQLite's own `upper()` folds ASCII only and it has no NFC, so a pure-SQL backfill (the `AddCredentialNameKey` approach) would key non-ASCII titles differently from new ones. A deterministic SQL function is the one way a migration can run the application's rule. `UpgradingKeysEveryExistingSongByTheApplicationsRule` tests it.
  **Issue:** #94
- **Decision:** API: the Songs list takes `title` (raw text; a blank one after normalising is 400 `invalid_request`) and `excludeId` (a UUID in `D` format; anything else, empty included, is 400). Each may be given once, and both combine with the other filters by AND. `excludeId` also works without `title`. There is no new endpoint, so the scope guards are unchanged.
  **Why:** This follows the discretion lines ("takes `title` and `excludeId`; the count is the list response's `total`"). Accepting `excludeId` on its own is harmless and keeps the parameters independent.
  **Issue:** #94
- **Decision:** Web: `songs/DuplicateTitleIndicator.tsx` sits beside the title's Edit button in `SongHeader` and is keyed by Song ID, so it resets on navigation. It is a default-variant button, "N other(s)", with the visually hidden suffix "with this title". It opens a Mantine Popover (a dialog named by its heading) holding a table: Shortcode (a link), Concept, Artist, Genres, State, Created (date only, configured zone, new `formatDate`). Below the table, "Showing the 20 most recently updated of N." appears when there are more. The link "Show every Song with this title in the Songs table" is always shown. The indicator refetches when the saved title changes (the title is in the request path) and on page load. While loading, or when the list fails, it shows nothing.
  **Why:** A table lets a screen reader tie each value to its column. The link to the table is useful at any count, not only for overflow. The `light` button variant failed axe colour contrast in its hover state while the popover was open, which the e2e walk caught, so it uses `default`. `hideDetached={false}` is needed for jsdom, as with the state menu.
  **Issue:** #94
- **Decision:** The Songs table has a `title` URL parameter (`SongQuery.title`, optional; a blank one is ignored). It shows as a "Title is “…”" group with a "Clear the title filter" button and counts as a filter for the empty-result message and "Show every Song". There is no free-text title input: the filter is set only from a Song page's list.
  **Why:** The discretion line asks for a visible, clearable filter that the overflow link opens. A typed title search already exists as `q` (Song search), so a second input would duplicate it.
  **Issue:** #94
- **Decision:** "Songs in deletion retention are not counted" cannot be enforced yet, because no Song deletion exists. The Song deletion story must exclude retained Songs from the `TitleKey` filter in `SongStore.ListAsync`. The note is in the M3 cross-story notes.
  **Why:** No retention state exists to filter on, and adding one is that story's job.
  **Issue:** #94
- **Decision:** The retention store is schema-driven. A retained type (`Infrastructure/Retention/RetainedTypes.cs`) declares only its record-type name, its table, a noun, its shape version, upgraders, and optional hooks. The store reads each row with `SELECT *` and writes it to the `retention_records.document` JSON column, keyed by column name, with values exactly as stored (integers, reals, text, BLOBs tagged `$blob`). Parent references come from `PRAGMA foreign_key_list`, keys from `PRAGMA table_info`, and unique keys from `PRAGMA index_list`. Restore inserts the same values back, with `revision` incremented where the table has one.
  **Why:** The discretion line says "each retainable type declares its parent references". Reading them from the database's own foreign keys means a declaration can never drift from the schema, and the same goes for the serialiser. Raw values round-trip byte for byte (GUID text case included) in a way an EF entity round trip does not guarantee. Later deletion stories register a table in one line.
  **Issue:** #95
- **Decision:** Retaining a root also collects every row the database would remove with it. The store follows each CASCADE foreign key, breadth first, and refuses (throws, nothing changed) when such a row's table is not a registered retained type. It also refuses when a RESTRICT, SET NULL, or NO ACTION row refers to something being retained, unless that row is itself in the group. Nullable references between rows of the group are cleared just before the deletes (the documents keep the originals). Restore runs with `PRAGMA defer_foreign_keys = ON`, so a Song and its current Version can refer to each other both ways, and ends with `PRAGMA foreign_key_check` on every restored table.
  **Why:** This is the AC "with everything deleted alongside it": no cascade may silently destroy a row that was never retained. The cycle handling covers `songs.current_version_id` and `versions.song_id`, which are both RESTRICT, so the Song deletion story does not have to work around them.
  **Issue:** #95
- **Decision:** A restore checks up front that every foreign-key parent is live or restored in the same group. When one is missing, it refuses with a message naming the parent's noun, unless the record's type is `Optional`, in which case the record is left out with a note. It also refuses when a live row holds a record's primary key or a non-partial, non-expression unique key, naming the columns and values. Partial and expression indexes are caught at insert as SQLite constraint errors and reported the same way. The whole restore runs in one transaction, so a refusal changes nothing. `Optional` is not used yet; it exists for memberships whose Album, Playlist, or Genre is gone (#102/#103).
  **Why:** These are the discretion lines on parent checks and ID/unique-key clashes. #102 and #103 require "memberships are restored where the other side still exists", which a whole-group refusal could not express.
  **Issue:** #95
- **Decision:** The editor snapshot's `sequence` (a global unique tie-breaker) is a declared `StorageOrderColumn`. It is restored as it was when free; otherwise it gets `MAX + 1`, and the restore reports this in its notes. A restore into a Version that already has 50 entries runs the cap's own trim afterwards (`AfterRestoreAsync`).
  **Why:** `EditorRevisionStore.AddAsync` takes `MAX(sequence) + 1` of the live rows. After the newest entry is retained, the next new snapshot reuses its number, so the generic clash rule would refuse almost every history restore. The column only orders entries captured in the same millisecond; it is not data anyone sees. "Lets that rule's own trimming apply" is the discretion line.
  **Issue:** #95
- **Decision:** Shape guard: `RetainedShapes.Hash` is the SHA-256 of the table's columns (name, declared type, NOT NULL), sorted by name, read from the migrated database rather than the EF model, so raw-SQL migrations count. The baseline is `tests/n8Tracks.Api.Tests/Retention/retained-shapes.json` (`{recordType: {shapeVersion, hash}}`), copied to the test output. `RetainedTypeRegistry` refuses to start when a type at shape N lacks an upgrader from any shape below N. A restore throws, never inserts, when a document's columns do not match the table after upgrading.
  **Why:** These are the discretion lines (hash of column names, types, and nullability; a checked-in JSON baseline). Hashing the real schema catches changes that the EF model does not describe.
  **Issue:** #95
- **Decision:** The M2 `BackupScheduler` is now `Infrastructure/Scheduling/DailyTaskScheduler`. Its options are `DailyTaskSchedulerOptions`, still switched off in every test host. Once a minute it asks each registered `Application.Scheduling.IDailyTask` (`AddDailyTask<T>()`), each in its own scope with failures isolated. The backup tick is `BackupScheduleTask`. `DailyTaskRules` holds the planned-time, missed-run, and daylight-saving logic that `BackupScheduleRules` now delegates to. The prune is `RetentionPruneTask`: job type `retention-prune` at 04:00 in the configured zone, armed on its first look, queued once after any number of missed planned times, and never while a prune or `backup` job is active or maintenance (a restore) runs. Its record is the settings row `retention.prune` `{armedUtc, lastStartedUtc, lastFinishedUtc}`, written by the job handler.
  **Why:** This is the discretion line "the backup scheduler … is generalised into a shared daily-task scheduler … inheriting its missed-run and daylight-saving rules; the prune waits while a backup or restore is running". The artwork sweep (#97) registers as another `IDailyTask`. The job worker runs one job at a time, so a queued prune and a backup never overlap either.
  **Issue:** #95
- **Decision:** Prune: in one transaction, groups with `prune_after_utc <= now` are removed (their records cascade) and their files are queued in `pending_file_deletions`. After that, every pending file is deleted unless an unpruned group lists it or a registered `ILiveFileReferences` reports it in use, in which case it is dropped from the queue and kept. A failed deletion stays queued with `attempts`, `last_error`, and `last_attempt_utc`, and the next run retries it. A group's files are relative paths under `<data>/assets` (`RetentionService.IsManagedFilePath`). `ManagedFiles` refuses any path that resolves outside that folder or passes through a link, so nothing under `/media` can be deleted (invariant 2). A partial or failed run is logged at Warning by `RetentionPruneJobHandler`.
  **Why:** These are the discretion lines (rows then files; retried through `pending_file_deletions`; nothing found by scanning storage; still-referenced files kept). `<data>/assets` is the folder backups already archive, and #97's `ManagedAssetStore` names it. There is no live file source until #97, which registers one.
  **Issue:** #95
- **Decision:** The invariant 1 guard now also enumerates the public methods of `Application.Retention`. Each `RetentionService` method is exercised against the frozen and the mutable target (retain a new history entry, restore it, prune). `RetentionPruneTask.TickAsync` and `IsManagedFilePath` are exempt with reasons. A new fact fails when a retained type over `versions` is registered without an exerciser in `RetainedVersionTypes()` that retains and restores a frozen Version through every shape upgrader. That list is empty until #101.
  **Why:** This is the AC "the invariant 1 guard is extended to exercise restore and the shape upgraders". Versions are not retainable in this story, so the upgrader half is a tripwire for #101 rather than a no-op.
  **Issue:** #95
- **Decision:** Deferred to the Song deletion story (#102): "A Song restored after its workflow state was deleted gets the first visible state." With the generic check, a missing `workflow_states` parent would refuse the restore. #102 should add a per-type parent fallback, or set the state in a hook before the parent check. Also deferred: the deleted-shortcode resolver statuses (#101/#102). This story provides `RetentionService.FindByShortcodeAsync` and the indexed `retention_groups.shortcode` column.
  **Why:** Songs and Versions are not retainable here (#101 and #102 register them). Building their special cases without the types would be untested code.
  **Issue:** #95
- **Decision:** The redaction list gains `document` (the retention document column). `RetainedTextNeverReachesTheLogThroughRetainRestoreOrPrune` checks the log at Debug level through retain, a refused restore, a restore, and a prune job. No retention endpoint exists: restore is application-code only, and the recovery listing and user-facing deletes belong to the sibling stories.
  **Why:** This is the discretion line on redaction, plus the AC "restored by application code".
  **Issue:** #95
- **Decision:** `EditorRevisionService.DeleteAsync(versionId, snapshotId)` checks that the entry belongs to the Version, then calls `RetentionService.RetainWithinAsync` inside the same exclusive transaction. The group has kind `editor-snapshot`, one root, and no files. Its label is `History entry of <Version shortcode> at <entry time as UTC ISO 8601, to the second>`, for example `History entry of n8-4-v1.2 at 2026-10-05T09:30:00Z`. The group's `Shortcode` is null. The 50-entry cap still removes rows directly, so they are never retained. `EditorRevisionService` now takes `RetentionService`.
  **Why:** The key link is "the snapshot becomes a one-record retention group labelled with its Version shortcode and time". The time is the entry's capture time, the one the History list shows, not the deletion time, which the group already records. UTC keeps the label free of the viewer's time zone. The group shortcode is left null because `FindByShortcodeAsync` feeds the resolver's future `deleted` status, and the Version itself is still live.
  **Issue:** #96
- **Decision:** `DELETE /api/v1/versions/{reference}/snapshots/{snapshotId:guid}` is `.SessionOnly()` and takes no revision. It answers 204, or 404 `not_found` for an unknown Version or a snapshot of another Version. A second delete of the same entry is also 404. Guards: `EndpointScopeGuardTests` (session-only count is now **38**), the invariant 1 guard (the endpoint and `EditorRevisionService.DeleteAsync` are exercisers that delete a fresh entry and must leave a frozen Version's inputs byte-identical), and `ReferenceParameterGuardTests` (`snapshotId` in `OtherIds`, plus a call that deletes a fresh entry each time).
  **Why:** These are the discretion lines (route, shortcode, no revision, session-only). Each exerciser takes its own new entry, so the guard's shared target keeps the snapshots its other exercisers use.
  **Issue:** #96
- **Decision:** Web: the selected entry's view (`HistoryPanel.tsx` `SnapshotView`) has a "Delete this snapshot" button. It is always enabled, including on frozen Versions, while there are unsaved or conflicted changes, and when the entry's text could not be loaded. It opens the modal "Delete this snapshot?": "The snapshot from <time> is deleted permanently. This cannot be undone. The lyrics and styles in the editor are not changed." Cancel is focused first and Delete is red. After a delete, the entry leaves the list at once, the newest remaining entry is selected and focused, a polite status reads "Deleted the snapshot from <time>.", and the list is read again. An entry already gone (404) counts as deleted. A failure keeps the dialog open with an alert.
  **Why:** The AC requires a confirmation that calls the deletion permanent without mentioning 30 days, and requires the newest remaining entry to be selected. Delete lives on the selected entry because the Demo is "select an entry, choose Delete". Focus moves to the new selection because the button that opened the dialog is gone after the delete. The label "Delete this snapshot" matches "Restore this snapshot" beside it.
  **Issue:** #96
- **Decision:** The imaging library is SkiaSharp 4.153.1 with `SkiaSharp.NativeAssets.Linux.NoDependencies` 4.153.1 (MIT; Skia's codecs are libjpeg-turbo, libpng, and libwebp), added to Infrastructure only. Both versions were read from the NuGet registry. `dotnet list package --vulnerable --include-transitive` reports nothing. The only GitHub advisory for SkiaSharp (GHSA-j7hp-h8jx-5ppr, libwebp) affects versions before 2.88.6. The publish places `libSkiaSharp.so` in `/app` of the image, and the new e2e spec decodes and encodes inside the container.
  **Why:** The discretion line asks for a maintained library with a licence compatible with Apache-2.0. ImageSharp's Six Labors Split License grants Apache-2.0 only under conditions (open source, transitive use, revenue under $1M) and is not an OSI licence, so it was rejected. Magick.NET (ImageMagick) and NetVips (libvips, LGPL) bring much larger native attack surfaces. SkiaSharp is MIT, and the codecs it uses are the ones Chromium fuzzes. It supports WebP encoding with quality and alpha, EXIF origins, ICC-to-sRGB conversion on decode, header-only dimensions, and scaled JPEG/WebP decoding.
  **Issue:** #97
- **Decision:** Validation runs in this order: (1) a size check (25 × 1,024 × 1,024 bytes, enforced while the multipart body streams, plus a Kestrel body limit of that size + 1 MiB) gives 413 `artwork_too_large`; (2) magic bytes (JPEG `FF D8 FF`, the PNG signature, or `RIFF…WEBP` followed by a `VP8 `/`VP8L`/`VP8X` chunk) give 415 `artwork_type_not_supported`; (3) the codec must report the same format, and the header dimensions are checked before any pixel buffer is allocated, giving 422 `artwork_dimensions_exceeded` over 12,000 on a side; (4) the first frame is decoded into sRGB, and anything other than `Success` (including the incomplete input of a truncated file) gives 422 `artwork_undecodable`. The file name and the declared part type are never read. Refusals write nothing.
  **Why:** These are the AC and the discretion codes. Header-first checking makes the decode bounded, so a decompression bomb is refused before allocation.
  **Issue:** #97
- **Decision:** The 512 MB decode cap counts 4 bytes per pixel. An image within 12,000 on a side but over the cap is decoded at the largest eighth-step scale the codec supports (JPEG and WebP scale while decoding). When no scale fits, which is the case for PNG at about 11,600 pixels square and above, it is refused as 422 `artwork_dimensions_exceeded` with the title "…pixels, too many to process". Decodes are serialised by a gate in the singleton imaging service, so the cap holds for the whole server.
  **Why:** A 12,000 × 12,000 RGBA decode needs 576 MB, more than the planner's 512 MB cap. Scaled decoding keeps every JPEG and WebP within the AC's 12,000-pixel limit acceptable. Skia cannot scale PNG while decoding, so refusing such a PNG is the only way to keep the cap. The refusal reuses the existing dimensions code rather than adding one.
  **Issue:** #97
- **Decision:** Storage: a new table `assets` (`id`, unique `content_hash` SHA-256 hex, `media_type` CHECK in the three types, `bytes`, `width`/`height` with orientation applied, `thumbnail_sizes` as a JSON array, `created_utc`, `uploaded_utc` indexed), from migration `AddAssets`. Files are stored at `<data>/assets/artwork/<hash[0..2]>/<hash>/original.<jpg|png|webp>` and `<size>.webp`. Each file is written in full to `<data>/assets-staging/<guid>.tmp` (outside the folder backups read), flushed, and moved into place without overwriting. All of this happens inside the upload's exclusive transaction, so a sweep of the same bytes cannot interleave. Paths resolve through `ManagedFiles.Resolve`, which refuses anything outside `<data>/assets` and any path through a link (invariant 2). Deletion goes only through `IManagedFiles`.
  **Why:** The story allows the `assets` table. Content-addressed folders store identical bytes once. The upload stays atomic for backups and crashes. The sweep removes staging files left by a crash once they are an hour old.
  **Issue:** #97
- **Decision:** Thumbnails are drawn on an sRGB surface with Skia's EXIF-origin matrix, using linear + mipmap sampling. They are written as lossy WebP at q85, alpha kept, for each of 96/320/1024 no larger than the oriented long side. A requested size that was not made is served as the largest made size below it. When no size was made (original long side under 96), every size serves the original.
  **Why:** This follows the discretion line ("the next smaller one, or the original"), read as: the next smaller thumbnail when there is one, otherwise the original. Re-encoding means no upload metadata (EXIF, XMP) reaches a thumbnail. The original is kept byte for byte, as the AC requires.
  **Issue:** #97
- **Decision:** API: `POST /api/v1/artwork` (`artwork.write`) answers 201 (`Location` is the original) for new bytes, or 200 with the same asset for bytes already stored. Either way the body is `{id, mediaType, bytes, width, height, sizes, urls{original,"96","320","1024"}, uploadedAt}`, and the URLs include the path base. `GET /api/v1/artwork/{assetId:guid}` and `/{size}` (`catalog.read`) stream with n8Tracks' own media type, `X-Content-Type-Options: nosniff`, `Content-Security-Policy: default-src 'none'; sandbox`, `Cache-Control: private, max-age=31536000, immutable`, and an ETag (304 on If-None-Match). A 404 is `no-store`. None of the three is session-only (the count stays 38). The POST is in the invariant 1 exempt table, and `assetId` is in `ReferenceParameterGuardTests.OtherIds`.
  **Why:** The discretion lines give the routes and scopes. Every asset ID and size maps to fixed bytes, so the responses can be cached forever, but only privately, because they sit behind sign-in. Re-upload answers 200 because "identical bytes are stored once".
  **Issue:** #97
- **Decision:** "Attached" is a new port, `IArtworkAttachments.IsAttachedAsync(assetId)`, in `Application/Assets`. It has no production implementation yet: #98 creates the attachments table and registers one, and #100 adds owner types. An asset is *live* while some attachment holds it, or within 24 hours of its latest upload. Live assets are served; anything else answers 404, including an asset only a retention group still lists. `ArtworkFileReferences` (an `ILiveFileReferences`) tells the retention prune to keep a live asset's files.
  **Why:** The test plan wants 404 "for an asset attached to nothing live" and needs "attached" before #98 exists. Tests use a stand-in, as #95's retention tests did. Serving a fresh upload during its unattached day lets #98's picker preview the server's thumbnail.
  **Issue:** #97
- **Decision:** The sweep is `ArtworkService.SweepAsync`, run by `ArtworkSweepTask`, an `IDailyTask` on the shared scheduler. It runs at the first look after a start and then hourly, with the last run kept in memory (`ArtworkSweepSchedule`). It waits during maintenance, before setup, and while a backup or retention-prune job is queued or running. It removes assets whose latest upload was at least 24 h ago, that nothing attaches, and whose files no unpruned retention group lists (`IRetentionStore.IsFileRetainedAsync`). Each removal runs in its own transaction: files go first, so a failed delete leaves the row for the next sweep, then the row, then empty folders. After a retention group is pruned, the prune deletes the files of an asset that is not live, and the next sweep removes its row. Re-uploading the same bytes restarts the clock and restores any missing files.
  **Why:** The discretion line says the sweep "registers with the shared daily-task scheduler and also runs hourly", and an hourly cadence covers the daily one. The sweep is inline rather than a job because it is cheap and hourly jobs would crowd the jobs list. Skipping it during backups avoids deleting files a backup has already listed.
  **Issue:** #97
- **Decision:** No web or e2e Demo walk, because the story says "Demo: none — agent-verifiable". One e2e API spec (`e2e/tests/artwork-api.spec.ts`, shared containers, a stamped PNG) uploads, reads the original and a WebP thumbnail, and checks a renamed text file gets 415 against the built image, which proves the native library loads in the container.
  **Why:** The UI arrives with #98. The container check is the one thing API tests on macOS cannot prove.
  **Issue:** #97
- **Decision:** Rule 1: `e2e/tests/backups.spec.ts` expected the downloaded archive to hold exactly `assets/`, `manifest.json`, `n8tracks.db`, and `settings.json`. That is false on the shared containers once any spec has stored artwork; the new artwork spec runs before it in the full suite, and it failed. The spec now checks the same non-asset entries and manifest paths, and requires that every archived `assets/…` file is listed in the manifest. It already verified each listed file's size and SHA-256.
  **Why:** The test assumed an empty asset store on shared containers. The fix keeps every original check and adds proof that artwork reaches backups, which is this story's key link to `<data>/assets`.
  **Issue:** #97
- **Decision:** Attachments live in one new table, `artwork_attachments`: `id` PK, `owner_type` (CHECK in song/album/playlist/artist), `owner_id`, `asset_id` (FK RESTRICT to `assets`), the nullable `crop_x`/`crop_y`/`crop_size` (CHECK all null or all set, non-negative, size > 0), and `attached_utc`. It is unique on `(owner_type, owner_id)`, indexed on `asset_id`, and has no FK to the owner. Migration `AddArtworkAttachments`. `ArtworkAttachmentStore` is the production `IArtworkAttachments` (an asset any row names is attached), so attached assets are live and never swept.
  **Why:** The discretion line says "keyed by owner type and owner ID with a unique index, used by every owner type", and a generic owner cannot carry an FK. Adding #100's owner types and #99's crop columns now means neither story rebuilds a SQLite table to change a CHECK, and the retained shape (hashed by columns) does not change under #99. The RESTRICT FK to `assets` makes the sweep fail safe on an attached asset.
  **Issue:** #98
- **Decision:** Attaching, replacing, or removing artwork is the Song's `PATCH` (`artworkAssetId`: an asset ID or null), checked and written in the Song's transaction under its revision. The revision goes up even when only the artwork changes, and sending the same asset again changes nothing. The asset must be *live* (attached somewhere, or uploaded within 24 h). Text that is not an ID, an unknown ID, or a stale or swept upload gets 422 keyed `artworkAssetId` ("There is no such artwork, or it was removed. Upload the image again."). The logic is `ArtworkAttachmentService` (Application/Assets): internal methods that take only IDs, called by `SongService` as Genres/Tags are, so the invariant 1 guard's namespaces are unaffected and #100 can call it from the other owners' services.
  **Why:** The key link in the story says so ("attaching it is a normal revisioned edit of the Song"), and so does the discretion's `artworkAssetId` 422. A stale upload would 404 when served, so it must not be attached. The web uploads right before attaching, which refreshes an identical asset's clock.
  **Issue:** #98
- **Decision:** A replaced or removed attachment is retained through `RetentionService.RetainWithinAsync` as a new retained type `artwork-attachment` (`RetainedTypes.ArtworkAttachment`; baseline added to `retained-shapes.json`). The label is "Artwork of n8-N", the shortcode is null (the Song is still live), and the files are `ArtworkPaths.Files(asset)`. The asset's files are kept while the group is unpruned (sweep) or the asset is live (prune, via #97's `ArtworkFileReferences`). After the prune deletes a non-live asset's files, the next sweep removes its row.
  **Why:** The discretion line says "what is retained is the attachment (owner, asset, crop); the asset's files stay until no live attachment and no retention group refers to them". Every rule above already existed from #95 and #97, so no new deletion code was needed. The test covers both halves: the files are pruned after 30 days, and an asset still attached to another Song is never pruned.
  **Issue:** #98
- **Decision:** `artwork.write` is checked in the PATCH handler, only when `artworkAssetId` is sent, through `ScopeMiddleware.Lacking(context, scope)`. The answer is 403 `insufficient_scope` with `requiredScope: "artwork.write"`, the same shape the marker gives. The endpoint's marker stays `songs.write`, so a Song edit without artwork still needs only `songs.write`, and no endpoint was added (the scope-guard counts do not change).
  **Why:** The AC says "attaching or removing a Song's artwork requires both". A marker-level requirement would have made every title or notes edit need `artwork.write`.
  **Issue:** #98
- **Decision:** Every Song answer (list, full, conflict `current`, and the Song answers of the Version and relationship endpoints) carries `artwork: {assetId, urls{original,"96","320","1024"}, crop}` or null. The URLs start with the PathBase, as the upload's do (`ArtworkResponse.UrlsOf`). For that, `SongResponse.From`/`SongListResponse.From` now take the `PathString`.
  **Why:** This is the discretion shape. A URL relative to the app root would break under the sub-path deployment.
  **Issue:** #98
- **Decision:** Web: `common/ArtworkPicker.tsx` (generic: `title`, `noun`, `artwork`, `save(assetId|null)`) heads the Details panel. It uploads, then saves `artworkAssetId` through the page's one `useRevisionedSave`, so conflicts go to the shared dialog ("Artwork" row). It shows the 320 thumbnail at 200 px, and activating it opens a modal with the 1,024 image and a "View original" link. Removing opens a "Remove the artwork?" confirmation. `common/ArtworkImage.tsx` draws the square (object-fit cover, the centred-square default #99 builds on), or one neutral placeholder named "No artwork". The header shows the 320 thumbnail at 96 px. The Songs table shows the 96 thumbnail at 40 px inside the Title cell, not as a new column. The upload uses `fetch` directly with a 120 s timeout, not `apiFetch` (whose 10 s per attempt is too short for 25 MB). A file over 25 MB is refused without being sent, and the API's refusal `title` is shown with "The artwork was not changed."
  **Why:** The discretion gives the sizes. Keeping the Table's cell indexes avoids rewriting every test that reads cells by position, and the title cell's text is unchanged because an `img` adds no text. `uploadBackup` already uploads this way.
  **Issue:** #98
- **Decision:** `e2e/support/images.ts` (`solidPng`) now holds #97's PNG builder, shared by `artwork-api.spec.ts` and the new `song-artwork.spec.ts`.
  **Why:** Both specs need fresh, stamped image bytes on the shared containers.
  **Issue:** #98
- **Decision:** The crop goes through the owner's own PATCH as `artworkCrop` (`{x, y, size}` or null), next to `artworkAssetId`. It is under the Song's revision, and from a token it needs `artwork.write`. There is no crop endpoint of its own. The crop is checked after the revision check: a stale crop answers 409 with `current` before its fit is judged. A crop sent with a new `artworkAssetId` applies to the new image (422 if it does not fit). A new asset sent without a crop resets it to null. A null crop on a Song without artwork changes nothing, and a non-null one is 422 "There is no artwork to crop".
  **Why:** The discretion line says "the crop write is guarded by the owner's revision; reset writes null". The replace-with-crop combination is what "Keep crop" needs. #100 copies the pattern through `ArtworkEndpoints.ReadCrop` and `ArtworkAttachmentService.CropErrorAsync`/`SetCropAsync`/`ReplaceAsync(…, crop, ct)`.
  **Issue:** #99
- **Decision:** "Not square" is refused at the request shape. The body may give `size`, or `width` and `height`, and sides that differ are 422 "A crop is square…". Values that are not whole numbers, or that are missing, are 422 for the shape. Bounds and the 64 px minimum come from `ArtworkCropRules.Errors` against the asset's oriented dimensions.
  **Why:** The crop is "a square by construction". The Test plan still asks for a not-square refusal, and accepting `width`/`height` makes that testable without a second storage shape.
  **Issue:** #99
- **Decision:** The square thumbnails are `crop-<key>-<size>.webp` in the asset's folder. `<key>` is the first 12 hex digits of SHA-256(`x,y,size`), and the sizes are 96/320/1024 up to the crop's side, or just the crop's side when it is under 96. They are made from the original in the save request (`IArtworkImaging.CropAsync`; Skia draws the crop in oriented, full-size pixels, so EXIF and scaled decodes are handled). They are served at `GET /api/v1/artwork/{assetId}/crops/{cropKey}/{size}` (`catalog.read`), and only while a live attachment sets that crop. A missing thumbnail is made again on request. A previous crop's thumbnails are deleted when no other live attachment sets the same crop on the asset, and that includes replacement. They are never listed in retention groups. The sweep deletes any left in a removed asset's folder (`IManagedAssetStore.Files`).
  **Why:** The discretion says "regenerated in the crop-save request; URLs include a short hash". Regenerating on demand covers a restored attachment whose crop thumbnails were already deleted, and a transaction that rolled back after a delete, without adding them to retention.
  **Issue:** #99
- **Decision:** Artwork answers now also carry `width`, `height` (the oriented original, joined from `assets`) and `squareUrls` {96, 320, 1024}. With a crop set, those are the crop's thumbnails; with none, they are the plain thumbnails, which the web cuts to their centred square with object-fit cover (no server-made centred squares). `urls` still means the whole image, so "View larger" and "View original" show the uncropped image. `AttachedArtwork` gained `Width`/`Height`, and `ArtworkAttachment.Artwork` was removed.
  **Why:** AC 3 says "everywhere artwork is shown as a square, the crop is applied". The crop dialog needs the original's dimensions to work in its pixels.
  **Issue:** #99
- **Decision:** Web: `common/CropDialog.tsx` is a modal over the 1,024 thumbnail scaled to fit 440 px. It has a square selection (`role="application"`, `aria-roledescription="crop selection"`) that is dragged to move and has a corner handle to resize. The arrows move it 1 %/10 % (Shift) of the shorter side, and +/= and -/_ resize it about its centre. A "+" typed as Shift+= is the small step; Shift with the keypad plus is the large step. The polite live region `crop-position` is updated on keyup and at the end of a drag. There are "Centre", "Cancel", and "Save crop" buttons. `ArtworkPicker` gained "Crop artwork", "Reset crop" (saves null), and a "Keep crop" checkbox, off by default, that is shown only while a crop is set. A kept crop that does not fit the new image (by the mirrored `cropRules.ts`) is left out of the edit, so the server resets it, and the status says so. `ArtworkPicker.save` now takes `{assetId?, crop?}`. The keys and helpers are in `common/artworkField.ts`.
  **Why:** The discretion line puts the crop control behind a Crop action on existing artwork, with the keys and Keep crop it describes. On most layouts "+" is Shift+=, so treating that as Shift would make the small grow step unreachable.
  **Issue:** #99
- **Decision:** Two `eslint-disable-next-line` lines with rationale are on the crop selection: `jsx-a11y/no-noninteractive-element-interactions` and `jsx-a11y/no-noninteractive-tabindex`.
  **Why:** No ARIA widget role fits a 2-D crop selection, and jsx-a11y counts `application` as non-interactive. The AC requires that it take focus and keys. The repo already has the same pattern for drag rows and focus tooltips.
  **Issue:** #99
- **Decision:** Albums, Playlists, and Artists take their artwork exactly as Songs do. Each owner's PATCH takes `artworkAssetId` and `artworkCrop` (`ArtworkEndpoints.ReadOwnerArtwork`). From a token, sending either also needs `artwork.write` (`ArtworkEndpoints.LackingArtworkScope`), on top of the endpoint's `collections.write` marker. The rules are shared in two new internal methods: `ArtworkAttachmentService.CheckAsync`, which runs after the owner's revision check and handles asset liveness, crop fit, and reset-on-replace, and `ApplyAsync`, which runs after the owner's own conditional write. A change of artwork alone still raises the owner's revision: the owner's `TryUpdateAsync` rewrites its unchanged fields. `SongService` keeps its own inline copy of these rules.
  **Why:** The discretion says each owner's PATCH carries `artworkAssetId` and Artist edits sit under `collections.write`. AC 6 says the change is an edit of the owner under its revision. Leaving `SongService` alone kept #98/#99's tested path untouched.
  **Issue:** #100
- **Decision:** Owner reads carry `AttachedArtwork? Artwork` (`AlbumDetails`, `PlaylistSummary`, `ArtistDetails`, each a new trailing positional member), read with `ArtworkAttachmentStore.ForOwnersAsync` in each store's details or list query. Every answer and list item carries `artwork` as `AttachedArtworkResponse`, the same shape a Song answer uses. `AlbumResponse.From`, `PlaylistResponse.From`, `PlaylistSummaryResponse.From`, `ArtistResponse.From`, and their list counterparts now take the `PathString`. There is no migration: #98's `artwork_attachments` already allows the three owner types.
  **Why:** The discretion says list items embed the thumbnail URL keyed by attachment and revision. The asset ID plus the crop key in `squareUrls` changes whenever the attachment or its crop changes, which gives that keying without a second scheme.
  **Issue:** #100
- **Decision:** The retention label for a replaced or removed owner's artwork is "Artwork of the Album <title>" (likewise "the Playlist <title>" and "the Artist <name>"). A Song's label stays "Artwork of n8-N".
  **Why:** These owners have no shortcode, and the recovery listing needs to say whose artwork it was.
  **Issue:** #100
- **Decision:** Creating an Artist ignores any `artworkAssetId`/`artworkCrop` in its body: an Artist is created without artwork, as Albums and Playlists are, whose create bodies have no such fields. The fields are not refused.
  **Why:** Create and edit share `ArtistRequest`. Refusing unknown fields is not done anywhere else in the API.
  **Issue:** #100
- **Decision:** Web: `ArtworkPicker` goes on the Album and Playlist pages at the top of the field stack, and on the Artist page above the details form, outside it, so it saves as soon as an image is chosen while the form keeps its explicit Save. Each saves through its page's one `useRevisionedSave` (`saveFields`), with the shared `artworkFields<T>()`, `artworkValues`, and `artworkPatchOf` helpers. `ARTWORK_KEY` moved from `ArtworkPicker.tsx` into `common/artworkField.ts`. The Albums, Playlists, and Artists lists get a leading thumbnail column whose header is a visually hidden "Artwork". It shows the 96 square at 40 px, or the shared "No artwork" placeholder. The cell-text assertions in `AlbumsPage.test.tsx`, `ArtistsPage.test.tsx`, `albums.spec.ts`, and `playlists.spec.ts` gained the leading empty cell.
  **Why:** The discretion asks for "a thumbnail column in the existing lists" and one neutral placeholder. The Artist form's Save would otherwise hold an uploaded image back until the user pressed it.
  **Issue:** #100
- **Decision:** Test fakes: `web/src/test/artworkFake.ts` holds `testAssetId`, `testArtwork`, and `TEST_ARTWORK_SIZE`, moved out of `songServer.ts`, which re-exports them. It also has `artworkFake()` (`upload`, `apply`) and `withoutArtwork`, which `albumServer`, `playlistServer`, and `artistServer` use as `server.artwork`.
  **Why:** Importing the helpers from `songServer` closed a module cycle (`artistServer` → `songServer` → `artistServer`) that left a `let` uninitialised when the suites loaded.
  **Issue:** #100
- **Decision:** The second half of AC 8 ("when an owner is deleted its artwork attachment goes into retention with it and returns if the owner is restored") is not implemented here, because there is no Album, Playlist, or Artist deletion yet (#103, #104). This story tests the mechanism those stories will use: a retained owner's attachment is restored by `RetentionService.RestoreAsync` and the owner shows it again. I commented on #103 and #104 that they must name the owner's attachment as a retention root, since there is no FK from an attachment to its owner.
  **Why:** Deleting an owner is those stories' scope. #103's AC already lists "artwork attachments"; #104's does not, so it gets the note.
  **Issue:** #100
- **Decision:** Rule 1: `RetentionPruneTask.TickAsync` now looks for an active prune job before it reads the prune state. Before, it read the state first, so a prune that started and finished between the two reads left a state from before its start: the task saw the prune as due again and queued a second one. That race is the timing dependency in `RetentionPruneTests.ThePruneIsQueuedDailyAtFourOnceAfterMissedRunsAndNeverDuringABackup`, which failed once under load in #99: its `Assert.Null(TickAsync)` right after a queue, and its job count, both see an extra job when the worker finishes between the two reads. The #99 log of that failure was not kept, so this is the cause the code allows, not one read from the log. The new regression test `ALookWhileThePruneRunsNeverQueuesASecondOneHoweverTheRunEndsAroundIt` holds the job at its first state write with a gated `IRetentionPruneStateStore` decorator, and lets it finish only if the task reads the state. It fails on the old order (a second job is queued) and passes on the new one. The existing test is unchanged and is now deterministic.
  **Why:** The job records its start before it can finish. Once no prune is active, the state read next therefore has the last start. That is the ordering the timing-dependent test relied on.
  **Issue:** #100
- **Decision:** A deleted Version keeps its `used_version_numbers` row: that table's FK is to `songs`, so retaining a Version leaves the row in place, and the number is never offered or taken again. For restore, the insert trigger `tr_versions_record_number_after_insert` is unchanged. The new `RetainedType.BeforeRestoreAsync` hook (`VersionRestore.FreeNumberAsync`) deletes the Version's own used-number row just before its row is inserted again, and the trigger writes the row back. There is no migration.
  **Why:** Changing the trigger (for example to `INSERT OR IGNORE`) would let a new Version take a used number, which breaks the "never reused" guarantee the database enforces today. The hook runs inside the restore's transaction, so a failed restore rolls the row back.
  **Issue:** #101
- **Decision:** Restore recognises the blank Version that a last-Version delete created by two checks. First, it is a Version of the same Song created at exactly the group's `deleted_utc`: the delete clears `current_version_id`, retains, and then creates the blank with `created_utc = group.DeletedUtc`. Second, it is still untouched: revision 1, no name or notes, active, not frozen, top-level, no children, no Generations, and no history entries. The history-entry check goes beyond the discretion line. When the blank is removed (a hard delete; its number stays used) and it was current, the restored Version becomes current. Every Version restore raises the Song's revision (`VersionRestore.RemoveAutoCreatedBlankAsync`).
  **Why:** Rule 4 forbids a schema change, so the blank's ID cannot be stored on the group. Only the deletion creates a Version at that instant, inside the exclusive transaction. Removing a Version that had history entries would silently cascade them away. The Song's Versions change on restore, so open clients should refetch.
  **Issue:** #101
- **Decision:** Reads of a deleted Version need a lookup by ID, so `RetentionService.FindByRecordAsync(recordType, id)` is new (store: `retention_records` by `record_type` and `original_id`). It is exercised in the invariant 1 guard. `VersionDeletionService.FindDeletedAsync` treats only groups of kind `version` whose `Shortcode` is a Version shortcode, and whose `PruneAfterUtc` is still in the future, as "deleted". `GET`, `PATCH`, and `DELETE /versions/{ref}`, `POST .../snapshots`, and `deletion-impact` answer 404 `version_deleted` with `versionId`, `versionShortcode`, `number`, and `deletedAt`. The resolver answers `status: "deleted"` with the Song. After 30 days the answer is a plain 404 or not found.
  **Why:** AC 9 asks that the shortcode, the page URL, and API reads say it was deleted for 30 days. This mirrors #102's planned `song_deleted`. A Version deleted with its Song resolves through the Song's group, which is #102's work.
  **Issue:** #101
- **Decision:** `GET /api/v1/versions/{reference}/deletion-impact` is SessionOnly, like the DELETE, so the session-only count is now **40**. The impact is read inside the exclusive transaction. The DELETE re-points the current Version only when the deleted Version was current. Every delete raises the Song's revision and sets its `updated_utc`. The blank Version starts with `VersionDefaultsService.NewVersionInputsAsync(song title)`, the same options a new Song's Version 1 gets.
  **Why:** #101's discretion makes only the DELETE explicitly session-only. #102 makes both of its endpoints session-only, and the impact read exists only for the web UI's confirmation. "Blank" means no text, but every Version needs a valid options document.
  **Issue:** #101
- **Decision:** Web placeholders. `nestVersions(versions, shown, placeholders)` draws a placeholder node `{key: 'deleted-<n>', version: undefined}` in place of the deleted Version, with its descendants under it. The node is dropped while nothing under it is drawn (for example, its only descendants are archived and hidden). In the tree, a placeholder is a `treeitem` named "Deleted Version N" with `aria-disabled` and `data-deleted-number`. It can be reached with the arrow keys, but clicking it and pressing Enter do nothing, and it has no actions menu. Delete is offered from the tree menu ("Delete", not red: Mantine's red menu text failed axe contrast in `versions.spec.ts`) and as a "Delete" button in the pane header. `DeleteVersionDialog` reads the impact when it opens and deletes on the impact's revision. A 409 there re-reads the counts. After a delete, the page reads the list and the Song again, navigates to the current Version, and shows the notice `version-deleted`.
  **Why:** The discretion lines put placeholders in the tree only, unselectable, and put Delete in the tree menu and the pane header, with the new current Version selected afterwards.
  **Issue:** #101
- **Decision:** Web, for an open client whose Version was deleted. `failureOf` maps 404 `version_deleted` to a new `FailureReason` `'deleted'`. An autosave that fails with it hands the unsaved lyrics and styles (when they differ from the stored ones) to `SongVersions`. A detail load that finds no Version does the same, without text. `SongVersions` then navigates to that Version's URL and reads the list again. That page says "Version N was deleted" (from the resolver's `deleted` status) and offers "Create a new Version with my text", which opens `CreateVersionDialog` from the current Version with that content.
  **Why:** AC 10 asks that unsaved text be offered as a new Version's content. A deleted Version cannot be a source, so the source is the Song's current Version.
  **Issue:** #101
- **Decision:** Registering `versions` as a retained type with the noun "Version" changes one existing message. A history entry whose Version is gone is now refused with "The Version this history entry belongs to no longer exists." Before, the message used the derived "version". `RetentionServiceTests` was updated to match. The `versionServer` fake now lists Versions in tree order and keeps `usedNumbers`.
  **Why:** The noun is how messages name a retained row, and the catalog spells Version with a capital V.
  **Issue:** #101
- **Decision:** A Song's deletion is one retention group of kind `song`, labelled "Song n8-N (Title)", with the Song's shortcode. Its roots are the Song, every live Version, every Generation of the Song, and the Song's artwork attachment (with the asset's files), in that order. Everything else comes by cascade: history, used Version numbers, release links, Genre and Tag assignments, credits, Album and Playlist memberships, and relationships (both directions). Nine record types are new and registered: `song`, `used-version-number`, `song-link`, `song-genre`, `song-tag`, `song-credit`, `album-track`, `playlist-entry`, and `song-relationship`. Assignments, credits, memberships, and relationships are `Optional`. Items deleted earlier on their own (a Version, a history entry, replaced artwork) keep their own groups and are not counted.
  **Why:** AC 3 and 4, the discretion line "memberships and relationships are recorded inside the retention group", and #95's retention checklist. RESTRICT FKs (generations, versions.song_id, songs.current_version_id) require the Generations and Versions to be roots. Nothing ties the artwork attachment to its owner by FK.
  **Issue:** #102
- **Decision:** `RetainedType` gained `PrepareRestoreAsync`, which returns `RestorePreparation`: the values to restore, or "left out" with a reason, and runs before the up-front checks. `RestoredRow` gained `GroupKind`, `RestoredUtc`, and `GroupKeys`/`InGroup`. `IRetentionStore.RestoreAsync` takes the restore time, and `RecordFieldsAsync` (internal on `RetentionService`) reads named fields of retained documents. A Song whose workflow state was deleted is restored into the first visible state, with a note. Album memberships return at the end of the Album's last disc (via `AlbumTrackRules.NextPlace`), or are left out when that disc is full. Playlist entries return at the end of the Playlist, or are left out when it is full. After a restore, the Album's or Playlist's revision and updated time move, and so do the other Song's for a restored relationship (never the restored Song's own). `used-version-number` rows remove the row the Versions' insert trigger already wrote before going back, so the net result is the same rows.
  **Why:** AC "memberships and relationships are restored where the other side still exists", the discretion line "memberships return at the end of each Album's last disc and each Playlist", and #95's note 7 (workflow-state fallback). Without the hook, the generic unique-key check refuses a position clash.
  **Issue:** #102
- **Decision:** Rule 1: `VersionRestore.RemoveAutoCreatedBlankAsync` now runs only for groups of kind `version`. Before, restoring a Song's group would hard-delete any of its untouched top-level Versions whose `created_utc` equalled the group's deleted time: with a frozen test clock, that is the Song's Version 1. Regression test: `AVersionCreatedAtTheMomentItsSongIsDeletedComesBackWithIt`.
  **Why:** Only a Version's own deletion ever creates a blank, so only its group may remove one. Anything else is data loss.
  **Issue:** #102
- **Decision:** On delete, the Song's Albums, Playlists, and related Songs get revision + 1 and `updated_utc` = the time of the deletion (`ISongDeletionStore.TouchAsync`). Track numbers stay as they are. When the Song was alone on its disc, the later discs move down by one so the discs stay gapless; that is the domain rule `AlbumTrackRules.CloseDiscGaps`. Playlist positions are left with their gap, because writes rewrite them whole.
  **Why:** AC 4 and the discretion lines "track numbers are left as they are" and "removing the memberships increments the affected Albums' and Playlists' revisions". A disc gap would break the "discs are numbered 1, 2, 3… with no gaps" invariant that `AlbumTrackRules` states. Closing it changes no track number.
  **Issue:** #102
- **Decision:** `DELETE /api/v1/songs/{reference}` reads `If-Match` first, then an optional JSON body `{confirmTitle}`. A non-object body is 400. A non-text `confirmTitle` is 422 `validation_failed`. A stale revision is a 409 conflict with `current` = the Song, reported before the title check. A missing or wrong title, when the title is required now, is 422 `confirmation_required` with `impact`. Success is 204. `GET .../deletion-impact` answers `{id, shortcode, title, versionCount, generationCount, artworkCount, albumCount, playlistCount, relationshipCount, audioFileCount, titleRequired, revision}`. Both endpoints are SessionOnly, so the session-only count is **42**. `GET /songs/{ref}`, `deletion-impact`, and `DELETE` answer 404 `song_deleted` (`songId`, `shortcode`, `title`, `deletedAt`) for 30 days. The resolver answers `deleted` for the Song, for its Versions and Generations (by shortcode or ID, including a Version deleted on its own earlier), and for the Song's 30 days. PATCH and credits on a deleted Song stay a plain 404.
  **Why:** These are the discretion lines. The 204 has no body to send, and the web goes to the Songs table. The AC names only "direct reads", so the writes were left as they were.
  **Issue:** #102
- **Decision:** Web. "Delete Song" is a default-variant button beside Details in the Song header, because the red variants fail axe. `DeleteSongDialog` reads the impact on open and lists all seven counts, zeros included. It says the deletion "is permanent". When the title is required it shows a "Type the Song’s title to confirm" field, and Delete is disabled until the typed title matches after trimming. A 422 refreshes the counts from the answer, and a 409 reads them again. After a delete, the page navigates to `/songs` (keeping the table view it came from), with router state `{deletedSong}`. The Songs page shows a dismissible notice, `song-deleted-notice`, "Deleted n8-N “Title”." Dismissing it clears the history state. `LoadState`'s `not-found` now carries the 404 answer (`problem`), and the Song page shows "This Song was deleted" from it.
  **Why:** AC 2, 6, 9, and 10, and the discretion lines on the banner and the disabled button.
  **Issue:** #102
- **Decision:** Deleting an Album or a Playlist is `AlbumService.DeleteAsync(id, revision)` / `PlaylistService.DeleteAsync(id, revision)` (the must-haves name those services; both now take `RetentionService`). Each makes one retention group of kind `album`/`playlist`, labelled "Album <title>"/"Playlist <title>", with no shortcode. The roots are the record and its artwork attachment (`ArtworkAttachmentService.RetentionOfAsync`), and the group lists the asset's files. Tracks, entries, and Album links come by cascade. Three new retained types are registered: `album`, `album-link`, and `playlist`. No Song row is retained or deleted.
  **Why:** AC 2 and 3, and the #100 comment that an owner's attachment has no FK and must be a root.
  **Issue:** #103
- **Decision:** AC 5: after the retain, the Songs that were on the record get `updated_utc` = the deletion time, and their revision stays the same (`IAlbumStore`/`IPlaylistStore.TouchSongsAsync` over `Persistence/SongTouch`). This differs from #102, where a Song deletion raises its neighbours' revisions. The tests compare every column of `songs` (except `updated_utc`), `versions`, `generations`, and `song_artist_credits`, before and after, and compare the Song count.
  **Why:** AC 5 says exactly that: "moves its Songs' last-updated times and does not change their revisions". It also covers AC 3, that no Song's Versions change.
  **Issue:** #103
- **Decision:** `DELETE /api/v1/albums/{id}` and `DELETE /api/v1/playlists/{id}` take `If-Match` and answer 204. A stale revision is 409 `revision_conflict` with `current` (the record, its `songCount` included). They also answer 404, 428, and 400. Both are SessionOnly, so the session-only count is **44**. They are in the invariant 1 exempt table and in `ReferenceParameterGuardTests.OtherIds`. There is no deletion-impact endpoint, because the record's own `songCount` is the count the confirmation states. Reads of a deleted Album or Playlist are a plain 404, with no `album_deleted` code.
  **Why:** The discretion line says "Deletes carry the record's revision", and the conventions say a stale write is a 409 with `current`. On "proceed on current state if counts changed since the dialog opened": a membership change raises the record's revision, so the server cannot silently delete a different set of Songs. The web takes `current` from the 409 instead, shows the new count with a notice, and the user chooses Delete again, which then deletes the record as it is now. The AC asks for no deleted-read status for collections, unlike #102.
  **Issue:** #103
- **Decision:** Restore. The `album-track`/`playlist-entry` hooks now check whether their Album or Playlist is in the group being restored. If it is, the row comes back at its own disc/track or position, rather than at the end. `AfterRestore` then moves only that Song's updated time, and does not bump the record's revision again. If it is not, the hooks behave as #102 made them. The `album` type clears an Album Artist that has since been deleted, with a note, because #104's discretion says such an Album "is left with none". Once the tracks are back, it also closes disc gaps left by Songs deleted meanwhile, whose tracks are left out with the Optional note. A left-out Playlist entry leaves a position gap, as #102's deletions do. A restored title equal to a live one is allowed.
  **Why:** These follow the discretion lines "puts back memberships whose Songs still exist and reports the rest" and "a restored record whose title matches a live one is allowed". Without the Album Artist fallback, a restore after #104 deletes the Artist would be refused as `MissingParent` on the RESTRICT FK.
  **Issue:** #103
- **Decision:** Web. "Delete Album" and "Delete Playlist" are default-variant buttons beside each page's h2. They open the shared `common/DeleteCollection.tsx` `DeleteCollectionDialog`, which says "Deleting the <noun> “<title>” is permanent", what goes with the record, and "It holds N Songs. The Songs themselves are not deleted…" ("It holds no Songs. No Song is affected." when it is empty). After a delete, the page navigates to the list, keeping the view it came from, with router state `{deletedCollection}`. The list shows the dismissible notice `collection-deleted-notice`. Non-component helpers are in `common/collectionDeletion.ts`, and the API is in `api/collectionDeletion.ts` (`deleteAlbum`, `deletePlaylist`).
  **Why:** AC 1 and 4. This mirrors #102's notice pattern, and the default button variant follows #102's axe finding.
  **Issue:** #103
- **Decision:** The e2e spec `collection-delete.spec.ts` walks Demo steps 1 and 3, the Playlist and the Album. Step 2, deleting an Artist with reassignment, is left to #104, whose AC carries it.
  **Why:** The discretion line moved the Artist criteria to the sibling story.
  **Issue:** #103
- **Decision:** Artist deletion is `ArtistService.DeleteAsync(id, revision, ArtistCreditChoice)`. It runs in one exclusive transaction. The service now takes `ICatalogSettingsStore` and `RetentionService`. It makes one group of kind `artist`, labelled "Artist <name>", with no shortcode. The roots are the Artist and its artwork attachment, and aliases and links come by cascade. Four retained types are new: `artist`, `artist-alias`, `artist-link`, and `album-artist`. Every Song and Album whose credits change has its revision raised and its `updated_utc` moved (`IArtistStore.TouchCreditedAsync`). A default Artist that is this one is cleared, at the next settings revision.
  **Why:** The must-haves name `ArtistService` and "one transaction; … affected Songs' and Albums' revisions are incremented". It is also the #95 retention checklist and #100's owner-artwork note.
  **Issue:** #104
- **Decision:** Reassigning: `ArtistDeletionRules.Reassigned`. On a Song that does not credit the target, the credit keeps its role and place. On a Song that already credits the target, one credit stays: primary when either credit was primary, otherwise the target's own featured place. Featured places are not renumbered, so a gap can remain. Reads order by place, and the next credits PUT rewrites them 0…n. Reassigned credits and Album Artists are not in the group, so a restore leaves them with the target.
  **Why:** AC 3 and 4. "The target's existing position" only applies while the surviving credit is featured, because a primary credit is always at place 0. Leaving the gaps means fewer writes, and it keeps a removed credit's place free for a restore.
  **Issue:** #104
- **Decision:** Retention extension (no schema change). `RetentionRequest` gained an optional `Referring` list of record types: live rows of those types that refer to a deleted row through a non-cascading key go with it instead of refusing the deletion. A row type, here `song-credit` (the RESTRICT FK to artists), is retained and removed. `RetainedType.ReferenceColumns` makes a *reference type*, here `album-artist` over `albums.album_artist_id`. Its records hold the live row's key and the reference. Retaining clears the reference on the live row. Restoring sets it back where the row still exists and the column is still empty, otherwise it adds a note. Its shape in `retained-shapes.json` is the key plus the reference columns only (`RetentionStore.ShapeColumnsAsync`). `removeCredits=true` names both types, while a reassignment names neither, so any credit left over would still refuse.
  **Why:** AC 4 says removed credits, Album Artists included, are put back on restore. A root needs a single-column key, but `song_artist_credits` has a composite key. The Album must not be deleted, and the planned scope allows no new table. This is the smallest generic change. The opt-in keeps the "refuse unless dealt with" safety for every other deletion.
  **Issue:** #104
- **Decision:** On restore, a credit removed with its Artist comes back only if the Song still exists and the place is free. A primary credit needs the Song to have no primary now. A featured credit needs its place to be free and the Song to be under the 50-featured cap. Otherwise the credit is left out, with a note: "the Song has another primary Artist now", "another featured Artist holds its place on the Song now", or the generic note that the Song no longer exists. A restored credit raises its Song's revision. Credits restored with their own Song (#102) behave as before.
  **Why:** The test plan line "removed credits return where the Song still exists and that role or position is free, and are reported otherwise". Without this check, the generic unique-key check would refuse the whole restore as a Clash.
  **Issue:** #104
- **Decision:** Rule 1. Restore notes now use "An" before a noun starting with A, E, I, or O. Before this, "A Artist credit was not restored" could appear (from #102's Optional song-credit note). This is `RetentionStore.Indefinite`, and the new restore test asserts "An Artist credit…".
  **Why:** Messages are shown to users through the recovery listing.
  **Issue:** #104
- **Decision:** API: `DELETE /api/v1/artists/{id:guid}?reassignTo=<id>|removeCredits=true` takes If-Match and is SessionOnly, which brings the session-only count to **45**. It answers 204. An Artist that something credits, deleted with neither option, is 409 `artist_in_use` with `songCount`, `albumCount`, `isDefaultArtist`, and `current`. A 422 keyed `reassignTo`/`removeCredits` covers both options sent together, a malformed target, the Artist itself as target, a target that no longer exists, and a `removeCredits` that is not true or false. A repeated parameter is 400. The endpoint also answers 404, 409 `revision_conflict` with `current`, 428, and 400. An Artist nothing credits may be deleted with no choice, or with either one. Reads of a deleted Artist are a plain 404.
  **Why:** The discretion line gives the route, the codes, and session-only. The in-use check runs after the revision check, so a stale page reloads first.
  **Issue:** #104
- **Decision:** Web: `artists/DeleteArtistDialog.tsx` is opened by "Delete Artist" beside the Artist page's h2. When it opens, it reads whether another Artist exists (`searchArtists('', …, 2)`) and the catalog settings, to learn whether this Artist is the default. Its messages:
  - It always states "permanent" and that no Song or Album is deleted.
  - Credited: it gives the counts ("It is credited on 2 Songs and 1 Album.") and a radio group: "Reassign them to another Artist" (ArtistPicker "Reassign to", `allowCreate`, excludes itself) or "Remove them". Delete stays disabled until a choice is complete.
  - No other Artist: it says the credits will be removed.
  - Default: it says the default will be cleared.
  - 409 `artist_in_use` (credits added elsewhere, with no revision change) and `revision_conflict`: it takes in `current` and asks again.
  
  The Artists list then shows the shared `collection-deleted-notice`. `CollectionNoun` now includes `'Artist'`, and its state carries an optional `note`, such as "Its credits went to “X”. No Song or Album was deleted."
  **Why:** AC 1 and 2, and the discretion lines on the picker, removal only, and "permanent". Reusing #103's notice keeps one pattern.
  **Issue:** #104
- **Decision:** e2e: `artist-delete.spec.ts` walks Demo steps 1 and 3 on the shared containers. Step 1 starts from the Album page's Album Artist link, which covers #103's Demo step 2, and checks that both Songs and the Album credit the other Artist. Demo step 2, the default Artist, runs `@root-only` on its own fresh container, because the default is instance-wide (as in `credits.spec.ts`).
  **Why:** The conventions require an e2e Demo walk with axe. Setting a default on a shared container would credit the Songs that other tests create.
  **Issue:** #104
- **Decision:** The commands go through a new application service, `DeletedItemsService` in `Application/Retention`, with `ListAsync(includeExpired)` and `RestoreAsync(reference)`. `src/n8Tracks.Api/Cli/DeletedCommands.cs` holds both commands, and Program dispatches `list-deleted` and `restore-deleted` before the server starts. The commands pass the test-services hook to `CommandServices.Build`, as `restore` does.
  **Why:** This is invariant 5 (one business-rule layer). The key link asks for "the same restore routine the tests use, against the same database file". The invariant 1 guard sees the new public methods: `RestoreAsync` has an exerciser that deletes the frozen Version and restores it by shortcode, and `ListAsync` is listed as read-only.
  **Issue:** #105
- **Decision:** `IRetentionStore.RestoreAsync` now returns `RetentionRestoreResult(Notes, PutBack)`, and `RetentionRestoreOutcome.Restored` gained a third member, `PutBack`. `IRetentionStore` gained `NounOf(recordType)`.
  **Why:** AC 2 and the discretion line ask the report to itemise kinds and counts put back. Counting the group's records would also count what was left out. The nouns come from the type registry, so the listing's KIND and the counts use the same names the restore notes do.
  **Issue:** #105
- **Decision:** The listing's GROUP ID is the shortest start of the group's ID, at least 8 characters, that no other unpruned group shares. It is computed over all unpruned groups and never ends on a hyphen. `--json` adds `groupId` (whole), `shortId`, `kindName`, and `contents[{recordType, noun, count}]`. Its `kind` is the stored record type (`song`, `editor-snapshot`), not the display name. Times in JSON are ISO 8601 with the `TZ` offset. The table shows them to the minute in `TZ`.
  **Why:** IDs are UUIDv7, so groups deleted within about a minute share their first 8 characters, and a fixed 8 would not always be restorable. The stored kind is stable for scripts. The planner's six columns stay as they are, and the counts appear only in JSON.
  **Issue:** #105
- **Decision:** A shortcode names only a group within the 30 days. An expired group by shortcode is refused, and the message points to `list-deleted --all` and the group ID. A Version or Generation shortcode with no group of its own is refused with "<sc> was deleted as part of <label> … restore-deleted <ref>", but only when a Version group or Song group for it holds that Version number. A refused restore whose parent is missing names the group that holds the parent, when there is one. This covers a Version whose Song was deleted, a history entry whose Version was deleted, and artwork whose owner was deleted.
  **Why:** These are AC 3 and AC 8 and the discretion lines on newest-by-shortcode and old-by-ID. A history entry has no shortcode, so "asking for it alone" can only mean its own group, refused for its missing Version.
  **Issue:** #105
- **Decision:** Rule 2: restoring artwork first checks that its owner exists, and refuses if it is gone. It then retires the owner's current artwork into a new retention group under the restored group's label, through the new internal `ArtworkAttachmentService.RetireCurrentAsync`, which `ReplaceAsync` also uses now. Last, it raises the owner's revision and updated time. `IArtworkAttachmentStore` gained `OwnerExistsAsync` and `TouchOwnerAsync`.
  **Why:** This is AC 9. `artwork_attachments` has no FK to its owner, so the store's generic parent check let a restore insert a dangling attachment for a deleted owner. The one-per-owner key would otherwise refuse with a Clash. The tests "RestoredArtworkGoesBackOnItsOwner…" and "ArtworkWhoseOwnerWasDeleted…" cover it.
  **Issue:** #105
- **Decision:** The command options come from a new `EnvironmentOptionsLoader.LoadDataPathAndTimeZone`, which reads the data path and `TZ` and nothing else. The checks for schema, upgrade, maintenance, and setup are `reset-password`'s (#82). The commands take no data-path lock and rely on SQLite's IMMEDIATE transaction and 5 s busy timeout beside the server.
  **Why:** These are the discretion lines. A bad `N8TRACKS_PORT`, or another setting the commands do not use, cannot stop them.
  **Issue:** #105
- **Decision:** Tests:
  - In-process command tests in `tests/n8Tracks.Api.Tests/Cli/DeletedCommandsTests.cs` (27 cases).
  - A `smoke-docker.sh` section, "Recovering a deleted Song with the container commands", which deletes a Song through the API, lists it, restores it with `docker exec`, reads it back, and checks that a second restore is refused.
  - An e2e `deleted-recovery.spec.ts` that walks the Demo with `docker exec` on the project's container.
  **Why:** These cover the test plan, plus the conventions' e2e Demo rule with axe on every state visited.
  **Issue:** #105
- **Decision:** Raised the CI e2e job's `timeout-minutes` from 20 to 45 (with a comment in the workflow).
  **Why:** Rule 3 (blocker): on PR #297 the e2e job was cancelled after 20 minutes during `npm test`, because the one-worker suite (112 tests) now takes longer than that on a hosted runner. 45 matches the container job. Splitting the suite into parallel jobs is filed as a follow-up rather than redesigned mid-run.
  **Issue:** #297
- **Decision:** Put every test in `web/src/songs/inputs/KindOptions.test.tsx` on the fake clock (`fakeTimeouts()` with `userEvent.setup({ advanceTimers })` in `openVersion`), and turned the frozen test's 1.7 s real sleep into a 1.7 s advance of the fake clock. No per-test timeout was raised.
  **Why:** Rule 1: the Sound test waited out two real 1.5 s autosave pauses (3 s of its 5 s budget) on top of ~25 user events. On a loaded runner the events took more than the 2 s left, and it timed out at 5019 ms on PR #297's CI. Reproduced locally with 24 concurrent runs beside 32 `yes` burners: 8 of 24 timed out at 5011–5021 ms. After the change the Sound test takes 343 ms unloaded, and under the same stress 72 of 72 runs passed, the slowest in 3014 ms. The assertions are unchanged. I moved the whole file rather than just the one test, because the other tests wait on the same pause.
  **Issue:** #294
- **Decision:** `MissingFrontendDependenciesTests.WithoutItsDependenciesTheFrontendFailsToStartWhileTheApiAndTheGatewayBecomeHealthy` now opens its watch on the frontend's log after the frontend reaches `FailedToStart`, not before `StartAsync`. When a deadline cancels the read, it now throws a `TimeoutException` that names the awaited line, the deadline, and the number of lines read. The 3-minute deadline and every assertion are unchanged.
  **Why:** Rule 1. This was never a slow runner. `WatchAsync(IResource)` resolves the resource's DCP instance name (`frontend-<suffix>`) eagerly, and that name is assigned on `BeforeStartEvent`. The AppHost's own `RunAsync` can raise that event before or after the test reaches `StartAsync`. When the watch opened first, it resolved the bare `frontend` and listened to a log nothing writes to, until the deadline. Run alone the test failed 3 of 3 times, each after 3m06s. Reflection on the logger keys showed a `frontend` state next to `frontend-<suffix>`. Lines written through `ResourceLoggerService.GetLogger` are kept in memory and replayed to a later watch, so a read after the failure is deterministic. After the fix: 5 of 5 runs alone, 3 of 3 alone beside 32 `yes` burners, and 2 of 2 whole-project runs (23/23) under the same load all passed. Mutation check: with "npm install" taken out of the AppHost's message, the test fails at the deadline with the new message ("2 lines read").
  **Issue:** #299
- **Decision:** The context's connection string now opens the database read-write without creating it (`Mode=ReadWrite`, was `ReadWriteCreate`). `SqliteDatabase.CreateIfMissing` makes an empty file, and database startup's open step is the one product caller. Four test fixtures that migrate a brand-new file call it first. `HealthEndpointTests.ADatabaseFileDeletedAfterStartupIsUnhealthyAndIsNotCreatedAgain` now waits for two finished job-worker polls after the delete before it checks anything. It uses a claim-counting `IJobStore` decorator and a 50 ms poll. The test also asserts that no file came back. New test: `DatabaseStartupTests.AfterStartupTheContextNeverCreatesADatabaseFileThatWasDeleted`.
  **Why:** Rule 1, a product defect and not test timing. The JobWorker polls `ClaimNextAsync` through EF every second. Its pooled `ReadWriteCreate` connection recreated a deleted `n8tracks.db` as an empty file, and the health check's `SELECT 1` then passed against it. That is the 200 seen on PR #297's CI: a poll fell between the test's delete and its `/health` call. Any request or other background task through the context would do the same. Reproduced deterministically by adding a 2.5 s wait after the delete: it failed with OK. With the JobWorker removed, the same test passed. Red-green with only the mode flipped back: both new tests fail ("collection was not empty"; SQLite error 1 "no such table" instead of 14 CANTOPEN). With the fix: 25/25 plain loops of the health suite and 20/20 loops beside 32 `yes` burners. The DailyTaskScheduler was not involved, because the test host disables it. There is no health-result cache. `DatabaseSchemaCheck` already returned Missing for an absent file before opening; only its comment changed. The design-time factory is left to fail on a missing `.localdata` file, since `migrations add` never opens it.
  **Issue:** #300

## /n8-exec M3 fix pass — 2026-10-06
- **Decision:** The no-bulk-delete complement is two tests. The API test sends `DELETE` to `/api/v1/songs` and `/api/v1/songs/` (with and without a body of IDs and with a query of shortcodes), expects 404 or 405 each time, and checks that every Song stays live with no retention group. The `SongsPage` test checks for no checkbox in the table, no button or menu item in any row, and no Delete, Remove, bulk, or "selected" control anywhere on the page.
  **Why:** AC8 of #102 holds today only because nothing maps the collection route and the page has no selection. Bite proof: a stub `MapDelete(SongsPath)` makes the API test fail (`DELETE /api/v1/songs: 204`). A row Delete button, and separately a row checkbox, each make the web test fail.
  **Issue:** #301
- **Decision:** The default-Artist complement deletes three non-default Artists in turn: one with credits reassigned to the default Artist, one with credits removed, and one uncredited. After each delete, the whole `GET /settings/catalog` answer must be byte-equal to the answer read before, so the revision cannot move either.
  **Why:** #311 asks for both paths. Comparing the revision as well catches a clear-and-reset. Bite proof: `isDefault = stored is not null` fails the test. So does a variant that clears the default only on `removeCredits`, which proves the remove path on its own.
  **Issue:** #311
- **Decision:** `restore-deleted` now writes "Nothing was changed." on a line of its own after any refusal or not-found message. Before, it was appended to the message. The service messages are unchanged.
  **Why:** Several refusals end in a bare command to run. A line break keeps the command copyable whole, where a full stop after the shortcode would end up in the copy. It also leaves the e2e and smoke expectations, which match on substrings, valid.
  **Issue:** #303
- **Decision:** The `--json` output of `list-deleted` and `restore-deleted` uses `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`. The timestamps still carry the configured zone's offset; they are not switched to UTC `Z`.
  **Why:** The output goes to a terminal or a pipe, never into HTML. `JsonLogFormatter` already uses the same encoder. Keeping the offset leaves the JSON contract (#105's decision: ISO 8601 with the `TZ` offset) unchanged.
  **Issue:** #304
- **Decision:** The `songServer` fake's title filter now applies only the sort it is asked for, and otherwise keeps the stored order. `useSongsTitled` sends `sort=updated&direction=desc`.
  **Why:** #306 says the fake hid a missing sort. With the explicit parameters removed, the test still fails on row order, not only on the recorded query.
  **Issue:** #306
- **Decision:** #310 reproduced. Moving a track to a disc that ends at 999 sent track 1000, and the page showed the API's 422 text, "Disc and track numbers are whole numbers from 1 to 999." The fix refuses the move on the client before anything is sent, with "Disc N already has track 999, so <title> cannot go at its end. Choose another disc or a new one, or renumber disc N if it has gaps." The disc stays enabled in the menu. The `albumServer` fake now answers an out-of-range PUT with 422, as the API does. I added the `confirmed` label.
  **Why:** This matches how a typed out-of-range number is refused before sending. The API's `disc_full` is defined for adding a Song only, so changing the PUT contract (Rule 4) was not needed. Keeping the item enabled means the user is told why the move fails, rather than finding a disabled option with no explanation.
  **Issue:** #310
- **Decision:** For each bug I ran the touched suites and a bite or regression proof before committing. I ran the full gate (dotnet build/format/test, web, extension, e2e, check-suppressions, check-canaries) once over the whole branch before the single push.
  **Why:** The brief says not to push until every bug is done, so nothing is checked by CI in between. One full gate over the final tree covers every commit's combined effect.
  **Issue:** #301, #311, #302, #303, #304, #306, #307, #308, #309, #310

## /n8-exec M4 — 2026-10-06

- **Decision:** D1 applied. `GenerationService` moved from `Application.Songs` to the new namespace `Application.Generations`, as #117 planned (with `IGenerationStore`, `GenerationAttachOptions`, and the event types). The invariant 1 guard (`VersionImmutabilityGuardTests`) now has one explicit list, `CatalogServiceNamespaces`. It names `Application.Songs`, `Application.References`, `Application.Retention`, and `Application.Generations`, each by type, with no prefix or wildcard. Both the "every public method is exercised or excused" check and the "services elsewhere may not take a catalog type" complement read this list. Every `GenerationService` method has an exerciser on a frozen Version. A later story that adds a catalog namespace (`Application.Suno.Import`, `Application.Suno.Generate`, `Application.Artwork`) must add it to the list.
  **Why:** This is orchestrator decision D1. An enumerated list means a new write path cannot slip out of the guard because it sits in another namespace. The complement test still catches a catalog type taken anywhere else.
  **Issue:** #117
- **Decision:** `ClipFields` (the normalized clip) is in `Domain.Suno`, not `Domain.Songs`. `ClipReader` is a static class in `Application.Suno`.
  **Why:** If they were in a catalog namespace, a future Suno import service in `Application.Suno.*` could not take a `ClipFields` without tripping the guard's complement. They hold Suno's data, not catalog state. A static reader is not a service, so the guard does not enumerate it.
  **Issue:** #117
- **Decision:** One migration, `AddGenerationProviderData`. It adds the `generations` columns, creates `provider_records` (keyed by `generation_id`, cascading from the Generation, with a JSON-object CHECK on `payload`), creates `generation_events`, and creates `generation_event_links` (keyed by `generation_id`, which cascades; the event FK is RESTRICT; indexed on `event_id`). The partial unique index `ix_generations_suno_id` covers `WHERE suno_id IS NOT NULL`. Four columns that carry CHECKs (`state`, `remote_state`, `revision`, `suno_id`) are added with hand-written `ALTER TABLE ... ADD COLUMN ... CHECK` statements. Down drops every column in place.
  **Why:** EF Core's `AddCheckConstraint` rebuilds `generations` at the end of the migration. That drops `tr_generations_identity_never_changes` after any SQL that tries to re-create it, and it splits the migration across three transactions. Adding the columns in place keeps the trigger and keeps the migration in one transaction. The model snapshot still declares the named checks, so a later EF rebuild produces the same constraints. `has-pending-model-changes` reports none.
  **Issue:** #117
- **Decision:** The event link is a separate table, `generation_event_links`, not a nullable `event_id` column on `generations`.
  **Why:** A column FK would also have forced a table rebuild. As its own retained type (`generation-event-link`, Optional), the link goes into retention with its Generation. If the event is gone at restore, the Generation comes back without the link, which is what #124 asks for ("its event link is restored if the event still exists"). A Generation still has at most one event, because the link's key is the Generation.
  **Issue:** #117
- **Decision:** Retention, done now for #124: the `provider-record` and `generation-event-link` types are registered. Each cascades from `generations`, so any group that retains a Generation also retains them. `generation` moved to shape 2, with the upgrader `GenerationShape1To2` (a shape-1 record restores as active, present, revision 1, no Suno data). `retained-shapes.json` is updated. A test deletes a Version, then restores it: the provider record comes back byte for byte and the event link comes back. A second test restores a shape-1 record through the upgrader. Backup and restore need no change, because both copy the whole SQLite database.
  **Why:** This follows the m4-plan risk note for #117. Without the registered types, deleting a Version or a Song that has a provider record would be refused.
  **Issue:** #117
- **Decision:** Suno ID uniqueness is checked inside the attach's `IExclusiveTransaction` (BEGIN IMMEDIATE), before the insert. The partial unique index is the backstop, and a test shows it refusing a raw SQL duplicate. A restore that would bring back a Suno ID that is now live gets `Clash`, and a test covers it.
  **Why:** IMMEDIATE serialises every writer, including the separate `seed-generation` process, so a race between the check and the insert cannot happen. The index still refuses any other writer.
  **Issue:** #117
- **Decision:** No HTTP endpoint attaches Generations in this story. The attach outcome `SunoIdExists` carries the existing Generation. `GenerationsEndpoints.AttachRefusal` maps the refusals to problems (409 `suno_id_exists` with `shortcode` and `generationId`, 422 `invalid_clip`, 404) for the import and observed-Create endpoints to reuse, and a test covers the mapping.
  **Why:** AC 6 makes the application-service method the only way to create a Generation, and the story names no attach endpoint. Fixing the 409 shape now keeps AC 2's wording true for whichever endpoint exposes the attach.
  **Issue:** #117
- **Decision:** Attach options are `GenerationAttachOptions(EventId?, ExportId?)`. The discretion line's `reimport` flag is left to #124.
  **Why:** In this story, reimport has no behaviour to give it. Live uniqueness already allows a deleted Generation's Suno ID to be attached again, and restoring the original is #124's work. An option that changes nothing would be dead API.
  **Issue:** #117
- **Decision:** Field names. The response has `title`, `durationSeconds`, `modelVersion` (`major_model_version`), `modelName`, `modelLabel` (`metadata.model_badges.songrow.display_name`), `styleTags` (`metadata.tags`), `minimumBpm`, `maximumBpm`, `averageBpm`, `key`, `sunoCreatedAt`, `audioUrl`, `imageUrl`, `workspaceId`, `batchIndex`, and `sunoUrl` (derived, null without a Suno ID). It also has `sunoId`, `providerStatus`, `state`, `remoteState`, `revision`, and `isSelected` (always false). The column for the key is `musical_key`. Blank strings in a clip are read as none.
  **Why:** The design calls `metadata.tags` "tags as returned", but TS-003 shows the field is Suno's rewritten style text. Naming it `styleTags` keeps it from being confused with catalog Tags and puts it under the redaction policy: `styletags` was added to the policy, together with `providerrecord`, `rawclip`, and `clipjson`. The fixtures send `""` for a Sound's `major_model_version` and for a submitted clip's `audio_url`; neither is a value.
  **Issue:** #117
- **Decision:** In the fixtures, every `media_urls` entry is `m4a-opus`, so the audio address falls back to `audio_url` (`…/api/forbidden` on a finished clip, `""` on a submitted one). An entry counts as MP3 if its `content_type` contains "mp3" or its address ends in `.mp3`.
  **Why:** The discretion line says "where a fixture disagrees, the fixture wins". The rule is implemented as written, and the fixtures decide what it reads.
  **Issue:** #117
- **Decision:** The resolver now answers `archived` for a Generation whose state is archived (it was always `active`), and `GET /generations/{reference}` returns a deleted Generation as a plain 404 `not_found`. The provider-record endpoint answers 404 `no_provider_record` for a Generation that has no Suno data.
  **Why:** The state now exists, so the resolver can read it at no cost. A specific code for a deleted Generation is #124's (Generation deletion). The no-record code tells a client apart from a missing Generation.
  **Issue:** #117
- **Decision:** `seed-generation` takes an optional second argument, the path to a clip JSON file. The command reads the file as text and passes it to `GenerationService.AttachAsync`. It exits 1 for an unreadable file, an invalid clip, or a duplicate Suno ID. The e2e helper `seedGeneration(testInfo, shortcode, clip?)` writes the clip into the container's `/tmp` before calling the command. The README documents the argument.
  **Why:** This is the story's key link ("the command accepts an optional path to a clip JSON file"). The command is test-only (it refuses unless test seeding is on), only reads the file, and never touches `/media`, so invariant 2 is unaffected.
  **Issue:** #117
- **Decision:** I added an API-level e2e spec, `generations-api.spec.ts`: it seeds Generations with and without a clip, lists and reads them, and reads the provider record back byte for byte from the built image. There is no UI walk and no axe scan.
  **Why:** The Demo is "none — agent-verifiable" and the story adds no screen, so there is no visited state to scan. The spec still exercises the seed command's clip path and the endpoints on the real image.
  **Issue:** #117
- **Decision:** D4 applied: the manifest validator (`extension/scripts/lib/manifest.ts`) and `test/dist.test.ts` now hold an explicit new allow-list. `permissions` must be exactly `storage`, `scripting`, `tabs`. `optional_host_permissions` must be exactly `https://suno.com/*`, `https://*/*`, `http://*/*`. `options_ui` must be exactly the options page with `open_in_tab`. `host_permissions`, `optional_permissions`, `content_scripts`, and `externally_connectable` must still be absent. The complement tests still fail on any extra permission (`downloads`, `cookies`, `webRequest`, `activeTab`), on `host_permissions`, on a missing permission, and on the M1 lists. The relay is registered with `chrome.scripting.registerContentScripts` for the paired origin and never declared in the manifest. `downloads` stays out until #150.
  **Why:** Orchestrator decision D4 and the manifest in `docs/suno-integration.md`. An exact list keeps "anything wider fails" true.
  **Issue:** #128
- **Decision:** The handshake is marked with the existing `AnyCaller()` marker. The scope guard now allows that marker only on an explicit list of two routes, the API's 404 fallback and `GET /api/v1/extension/handshake`, and asserts that exactly two endpoints carry it. A browser session calling the handshake gets 403 `credential_required`, because there is no credential to report or record.
  **Why:** The story says the handshake "needs any valid token; it is the one endpoint that needs no particular scope". `AnyCaller()` already means "any authenticated caller, whatever its scopes". A new marker type would duplicate it, and naming the routes keeps the guard explicit.
  **Issue:** #128
- **Decision:** The handshake records the sighting on `credentials.last_extension_version`, `last_adapter_version`, and `last_seen_at` (migration `AddCredentialExtensionSightings`) and does not raise the credential's revision. A header value is kept trimmed only when it is 1–64 visible ASCII characters; otherwise, or when it is missing or repeated, it is stored as null and the handshake answers `compatible: false`. Compatibility is major.minor equality of `major.minor[.patch][-suffix]`, and anything else counts as unreadable.
  **Why:** The story names the columns and says "missing headers store null" and "give compatible: false". Bounding the stored text keeps a client from writing arbitrary strings into Settings. A sighting is an observation, not an edit, so it must not cause rename conflicts.
  **Issue:** #128
- **Decision:** The credential API answer gains `lastExtensionVersion`, `lastAdapterVersion`, and `lastSeenAt` (null until a handshake). On the Credentials screen, these show as a line under "Last used" for extension credentials ("Extension 0.1.0, adapter 1, seen …" or "Extension not connected yet"). This is not a new column. The scope list now has nine scopes, `suno.sync` and `suno.generate` last, and the dialog describes each.
  **Why:** AC 7. Keeping the table's columns unchanged leaves the existing tests and the layout as they are. The two scopes go after the PRD's seven because the PRD lists those as the MCP scopes.
  **Issue:** #128
- **Decision:** The extension stores only `pairing: {address, token}` in `chrome.storage.local`, plus `notice: "revoked"` after n8Tracks refuses the token. In that case the token is removed and the address is kept, so the user sees why and can reconnect. The handshake result is cached in the service worker's memory for 60 s and is never stored. The relay and the panel get a state that never carries the token, and the relay passes on only a summary.
  **Why:** AC 8 ("only the address, the token, and its own settings"), and the discretion line that a 401 removes the token. The notice is the extension's own state.
  **Issue:** #128
- **Decision:** The options page calls `chrome.permissions.request` for exactly `https://suno.com/*` and the entered origin's pattern, synchronously inside the Connect click (or the Replace click). Only after the permissions are granted does it send `connect` to the service worker, which checks the permissions, runs the handshake, and only then saves. A failed connect to a new origin gives back that origin's permission and keeps suno.com. The pattern includes the port when there is one (`http://host:8080/*`); the docs say a port in a pattern restricts it to that port. Only extension pages may send `connect` and `disconnect`; content scripts may send only `state` and `relay`.
  **Why:** The browser shows a permission prompt only for a user gesture. The extension can call n8Tracks across origins only with host permission. The sender check stops a page script from ending the pairing through the relay.
  **Issue:** #128
- **Decision:** A confirmation is asked before replacing a pairing that still holds a token (connected, unreachable, or permission removed). A revoked pairing is replaced without one. The relay answers `ping` within 300 ms: if the service worker has not answered by then, it reports `connection.status: "checking"`, so the web app's 500 ms ping still gets an answer while a handshake runs.
  **Why:** A revoked pairing has nothing left to lose. The ping timeout comes from the design (500 ms).
  **Issue:** #128
- **Decision:** The two features show in the popup as list items: "Library sync: Ready" or "…: This credential lacks suno.sync", with the missing one `aria-disabled`. `FeatureState` (`available`, `reason`) is part of the connected state, so the sync and generate stories can disable their own buttons with that reason. The version warning for the Suno panel is `warningFor(state)` in `extension/src/ui/connectionView.ts`. The panel itself is #132's, so this story's AC 5 is left unticked for the panel: the popup and options page show the warning.
  **Why:** There are no sync or generate buttons yet, and no panel (#132 builds it). Inventing either would be scope creep.
  **Issue:** #128
- **Decision:** Added `axe-core` ^4.14.0 (from `npm view`) as an extension dev dependency. The popup and options pages are checked with axe in jsdom, with color-contrast off because jsdom computes no styles. `ADAPTER_VERSION = 1` is placed in `extension/src/adapter/version.ts` for #132 to own.
  **Why:** The discretion line says "checked with the extension's unit-level accessibility checks". The extension had none. The handshake sends the adapter version now.
  **Issue:** #128
- **Decision:** The Versions table reads `rating` and the comment count from the Generation list answer when they are there. Until #119 adds them, they read as null and 0: "Not rated" and 0 comments. `generationOf` in `web/src/api/generations.ts` accepts either a `commentCount` number or an embedded `comments` array. No API field was added in this story.
  **Why:** #119 owns ratings and comments, and its plan embeds the comments in the Generation answer. Adding placeholder fields to the API now would fix a contract #119 is meant to design. The column, the highest-rating rule ("Not rated" when nothing is rated), and the count are built and tested on fixtures.
  **Issue:** #118
- **Decision:** The Generation panel's address is `/songs/<song>/generations/<shortcode>`. That route also selects the Generation's Version in the tree and the editor, and closing the panel goes to `/songs/<song>/v/<number>`. The resolver's `pageFor` and the Go to box now send a Generation shortcode there. A pasted Generation page URL is read as its shortcode. `FrontendHosting.IsShellRequest` serves that path when its last segment parses as a Generation shortcode, because the dots in the shortcode would otherwise read as a file extension and give a 404 on reload.
  **Why:** This is the discretion line: "has its own address … where the resolver sends a Generation shortcode". Showing the Generation's own Version keeps the tree, the table, and the editor in step.
  **Issue:** #118
- **Decision:** The table hides an archived Version while Show archived Versions is off, even when that Version is current. The tree still always draws the current Version. The table's two toggles are kept in the URL (`archived=1`, `archivedGenerations=1`) and stay there as the user moves between Versions and Generations. The tree's own "Show archived" stays a per-browser choice. The table's switches are labelled "Show archived Versions" and "Show archived Generations". `e2e/tests/versions.spec.ts` now matches the tree's switch with `exact: true`.
  **Why:** AC 1 and 2 say the table lists Active Versions and that the toggle adds Archived ones, and Demo step 4 has an archived Version leave the table. The tree's behaviour (#62/#63) is unchanged. The labels avoid three switches all named "Show archived".
  **Issue:** #118
- **Decision:** The section's heading is "Versions and Generations", and it sits below the tree and the editor, collapsible with a Hide/Show button (not remembered). Each Version row has a chevron button (`aria-expanded`, named "Generations of Version N"), and its Version number is a link that selects the Version. The Generation rows are a nested `<table>` in a full-width cell. Each row has the `ShortcodeBadge` copy control, and its Suno title (or "Untitled") is a link that opens the panel. "Open in Suno" goes to `https://suno.com/song/<Suno ID>` in a new tab. State shows as badges: Active or Archived, "Remote Missing" with a focusable tooltip, and "Selected". When a Version's only Generations are hidden archived ones, the table says how many are hidden instead of "No Generations yet".
  **Why:** This follows the discretion lines: a real table with nested Generation rows; choosing a row does not expand it; the chevron with `aria-expanded`; Remote Missing as a badge with a tooltip. The copy control reuses #68's component. A link per row keeps the rows keyboard-operable without click handlers on `<tr>`.
  **Issue:** #118
- **Decision:** `useSongGenerations` reads the Song's Generations again 10 s after each answer while any Generation is `submitted` or `streaming`. It uses `setTimeout`, not `setInterval`, so the test's fake timeouts can drive it. A read again that fails keeps the list already shown. A refresh after a Version deletion reads the Generations again too.
  **Why:** This follows the discretion line on Generating and the 10-second refresh. Keeping the old list avoids flashing an error while polling.
  **Issue:** #118
- **Decision:** Rule 3: `HandshakeEndpoint` reads its claims with `FindFirst(type)!.Value` instead of `ClaimsPrincipal.FindFirstValue`.
  **Why:** An `AssemblyLoad` probe over a full Api run showed what loaded `Microsoft.Extensions.Identity.Core`: the server's own handshake handler (#117), not a test. `FindFirstValue` is defined in that Identity assembly, so the server depended on ASP.NET Core Identity, which the setup guard forbids. The flake was a real breach. It surfaced only when a handshake test ran before the guard. `SessionAuthenticationHandler` already uses `FindFirst`.
  **Issue:** #313
- **Decision:** Rule 3: `SetupEndpointTests.ThereIsNoRegistrationPasswordResetOrOAuth` checks the server's transitive referenced-assembly closure from `typeof(Program).Assembly`, the same walk as `GatewayIsolationGuardTests`, instead of `AppDomain.GetAssemblies()`. The route check and the forbidden-name filter are unchanged, and complement asserts show that the walk reached the server projects and the framework.
  **Why:** The loaded-assemblies check depended on which tests had already run in the process, and it passed when run alone even with the breach present. The reference walk fails deterministically on the old handshake code, which was verified when the test ran alone, and other tests cannot affect it.
  **Issue:** #313
- **Decision:** The rating is a nullable `generations.rating` column (CHECK 1–5). It is added in place by hand-written `ALTER TABLE` in migration `AddGenerationEvaluations`, as #117 did for its checked columns, so the identity trigger survives. Comments are the new table `generation_comments`, as the discretion line names. The story implies both schema changes, so neither is treated as Rule 4. The retained `generation` type moves to shape 3, with the upgrader `GenerationShape2To3` (unrated). The new retained type `generation-comment` (cascade from its Generation) means a Version or Song deletion keeps comments and restores them. Deleting one comment alone is final.
  **Why:** The discretion lines fix the table and say a comment deleted alone is not retained. The retention store refuses to cascade into a table that is not registered, so registering the type is required, and it also keeps comments with their Generation.
  **Issue:** #119
- **Decision:** `GenerationEvaluationService` lives in `Application.Generations`, a namespace already in the invariant-1 guard's list. It is the only writer of the rating and the comments. Each of its four public methods, and each of the four new unsafe endpoints, has an exerciser in `VersionImmutabilityGuardTests`, which sends the Version's inputs alongside, unread. It looks Generations up through `GenerationService.FindAsync`. `IGenerationStore.TryRateAsync` writes only `rating` and `revision`.
  **Why:** D1: new write paths are enumerated by the guard, not left to escape it. Keeping one store method that writes the rating means no clip-update path can touch it.
  **Issue:** #119
- **Decision:** Every Generation answer (list, read, and the 409 `current`) carries `rating` and `comments`, oldest first, each `{id, text, createdAt, editedAt, revision}`. There is no `commentCount`; the web counts the array. `GenerationSummary.Comments` is filled by `GenerationRows.SummariesAsync` in one extra query per read.
  **Why:** The discretion line embeds comments in the Generation response, and the stale-rating `current` must include them. One shape for the list and the read keeps the row and the panel on one cached value.
  **Issue:** #119
- **Decision:** Comment writes (add, edit, delete) also set the Song's updated time, as a rating change does. None of them raises the Song's revision or the Generation's revision. A missing `rating`, or the value it already has, stores nothing, but a stale revision is still a 409. A comment edit whose trimmed text is unchanged stores nothing and is not marked edited. A comment's length is counted in UTF-16 units, the same as the web counter, and the database CHECK (`length(text) BETWEEN 1 AND 2000`) counts characters, which is never more.
  **Why:** The discretion lines name only the rating for the updated time. A comment is the user's own edit to the Song's data, so its last-updated time should move too. The no-op rules follow the Tag edit pattern and "Edited shows only when the text actually changed".
  **Issue:** #119
- **Decision:** Comment text is added to the redaction names as `comment`, `comments`, and `commenttext`. The bare word `text` is not added, because it is too common to mask everywhere. The endpoints log only comment and Generation IDs. A new `LogRedactionGuardTests` case checks that sentinel comment text never reaches a Debug log.
  **Why:** This follows the discretion line ("added to the log redaction name list") and the conventions.
  **Issue:** #119
- **Decision:** Web rating control:
  - `generations/StarRating.tsx` is a custom radio group named "Rating of <shortcode>". Each star is a `role="radio"` button ("4 stars"), with one tab stop.
  - The arrow keys change the rating at once, and focus follows. Down from one star clears it, Home and End go to 1 and 5, and Delete clears it.
  - Clicking the checked star, or pressing Space or Enter on it, clears the rating.
  - The control is used in the table row and in the panel. Both read the Song's one Generation list (`useSongGenerations().update`), so the highest rating is recomputed from that list.
  - `useRateGeneration` sends one write at a time per Generation, and the latest value wins. A 409 is retried once with the current revision. A second 409 shows n8Tracks's value with a message, and a failure reloads the list.

  **Why:** These follow the discretion lines. Mantine's `Rating` cannot clear by clicking the current star. Custom buttons make the keyboard behaviour exact and testable in jsdom.
  **Issue:** #119
- **Decision:** Web comments:
  - `generations/GenerationComments.tsx` is in the panel. It has an ordered list, and each comment shows "Written <time>" and an "Edited <time>" badge when edited.
  - Editing happens in place, and the edit box takes focus.
  - Deleting needs an inline confirmation: "Delete comment N" or "Keep it", and "Keep it" takes focus.
  - The text box has a counter of trimmed characters. Text that is too long gets an error message and a disabled button, and is never truncated.
  - A 404 on a comment write removes the comment silently. A 409 shows the current comment and a message.
  - The textarea uses fixed rows instead of `autosize`, because Mantine's autosize needs `document.fonts`, which jsdom lacks.
  - The table's Comments column now counts `comments.length` (testid `generation-comment-count`).

  **Why:** These follow the discretion lines on the counter and on comments deleted elsewhere, and AC 2's confirmation. An inline confirmation avoids stacking a modal on the drawer.
  **Issue:** #119
- **Decision:** In the web tests, `VersionsTable.test.tsx` waits for the Generation panel's opening transition, with `openedPanel()`, before it checks visibility.
  **Why:** With more content in the panel, `findByRole('dialog')` could return while the drawer was still at opacity 0. The test was timing-dependent, and no behaviour changed.
  **Issue:** #119
- **Decision:** The Selected Generation is a nullable `songs.selected_generation_id` with a foreign key to `generations` (ON DELETE RESTRICT) and an index. Migration `AddSelectedGeneration` adds the column by a hand-written `ALTER TABLE songs ADD COLUMN ... CONSTRAINT fk_songs_generations_selected_generation_id REFERENCES generations (id) ON DELETE RESTRICT`; the generated `AddForeignKey` was replaced.
  **Why:** The discretion lines call for a nullable column with restrict on delete and a service-level same-Song check. EF Core's SQLite `AddForeignKey` rebuilds `songs`, a table many others refer to, and splits the migration across transactions. SQLite accepts a REFERENCES column in `ADD COLUMN` when its default is NULL. `DatabaseStartupTests` now asserts the FK, the column, and the index.
  **Issue:** #120
- **Decision:** Retention of the circular reference (m4-plan risk note):
  - **Song deletion:** no code change. The selected Generation is in the Song's own group, so the generic retain clears the in-group reference before removing the rows. Restore defers foreign keys and puts the selection back.
  - **Version deletion:** passes `Referring: [selected-generation]`. This is a new reference type over `songs.selected_generation_id`, like #104's `album-artist`. When the deleted Version holds the selected Generation, the selection is cleared and remembered with the group. A restore sets it again when the Song has chosen none meanwhile, raising the Song's revision; otherwise it is left out with a note.
  - **Shape:** `song` retention moves to shape 2 (upgrader `SongShape1To2`: no selection).
  - **Tests:** round-trip tests for both paths and for "chosen another meanwhile"; the M3 deletion and restore suites re-run green.

  **Why:** The plan's risk note says deletion clears the selection first and asks for a restore round-trip. The `Referring` mechanism does the clearing and remembers the value, so a restore brings the selection back rather than losing it. #123 (move) and #124 (delete a Generation) must resolve the selection themselves.
  **Issue:** #120
- **Decision:** Archive and activate go through `GenerationEvaluationService`, not `GenerationSelectionService`. `RateAsync` became `UpdateAsync(reference, GenerationEdit(Rating, State), revision)`, which returns `GenerationUpdateOutcome`, and the store's `TryRateAsync` became `TryUpdateAsync(id, rating, state, revision)`. `GenerationSelectionService` (`Application.Generations`) holds `SelectAsync` and `ClearAsync`.
  **Why:** The issue's artifact lists archive and activate under the selection service. But the discretion line has the PATCH accept `state` and `rating` together, under one Generation revision. Doing both in one check-and-write means one service method. Both services are in the invariant-1 guard's exercisers.
  **Issue:** #120
- **Decision:** Song answers, list rows included, carry `hasSelectedGeneration` and `selectedGeneration {id, shortcode, state, remoteState}`. The discretion line names `{id, shortcode}`; the two states are added. The header uses them for the Archived, In Suno Trash, and Remote Missing badges without a second request.
  **Why:** The header must show those badges, and the Song read is what it has. The extra fields only add to the contract.
  **Issue:** #120
- **Decision:** API details:
  - **Order of checks in `PUT .../selected-generation`:** Song found (404, or `song_deleted`); `generation` named as text (422 `validation_failed` on `generation`); Generation found (404 `not_found`); Generation belongs to the Song (422 `generation_not_in_song`, with `generationId` and `shortcode`); then the Song's revision (409 with the Song as `current`).
  - **Invalid state:** 422 on `state`, case-sensitive: only `active` or `archived`.
  - **No-ops:** clearing when nothing is selected stores nothing, and so does setting a state the Generation already has. A stale revision is still a 409 in both cases.
  - **Logging:** selection changes log only IDs.

  **Why:** These follow the discretion lines. Other refusals come before the revision check, as in the other Song sub-resources.
  **Issue:** #120
- **Decision:** Web:
  - A `useGenerationChoices` hook backs the Select/Clear and Archive/Reactivate controls in the Generation panel and in a new per-row actions menu ("Actions for <shortcode>").
  - On a 409, a choice is sent again once with the current revision. The user's explicit choice does not depend on, for example, a title autosave in between.
  - `useSongGenerations` gained `markSelected`.
  - The header shows "Selected Generation: <shortcode link to its panel>" with badges, or "None".
  - The Songs table gets a last column, "Selected", with a check mark ("Yes" or "No" for screen readers). It is last so existing cell indexes stay as they are.
  - `writeWithRevision` accepts `DELETE` without a body.

  **Why:** The AC asks for the panel or row, and for the header and Songs table indicators. The discretion line says a check mark. A menu keeps the row narrow.
  **Issue:** #120
- **Decision:** Carry 1 is done. `hasSelectedGeneration` is computed from `songs.selected_generation_id` in `AlbumTrackStore.ForAlbumsAsync` and `PlaylistStore.FindAsync`. `GenerationResponse.isSelected` comes from `GenerationRows`, which joins the Song's selection. Tests cover Albums (detail and list) and Playlists.
  **Why:** This is the orchestrator's Carry 1 (m4-plan drift row).
  **Issue:** #120
- **Decision:** A Generation's cover image is a nullable `generations.artwork_asset_id`, a foreign key to `assets` (RESTRICT) with an index. Migration `AddGenerationArtwork` adds it in place with a hand-written `ALTER TABLE ... REFERENCES`. It is not an `artwork_attachments` row with a new owner type. `GenerationStore` is registered as a second `IArtworkAttachments`, so an image a Generation names stays live and the sweep keeps it.
  **Why:**
  - `artwork_attachments` has no foreign key to its owner, so deleting a Version or Song would leave the row behind.
  - Its owner-type CHECK could only change by rebuilding the table.
  - A column goes into retention with the Generation's own row.
  - EF's `AddForeignKey` would rebuild `generations` and drop its identity trigger.
  **Issue:** #121
- **Decision:** Retention changes:
  - Generation retention is now **shape 4**, with the upgrader `GenerationShape3To4`, which gives an earlier record no image.
  - A `PrepareRestoreAsync` hook restores a Generation without its image, with a note, when the asset has gone all the same.
  - Rule 2: Version deletion and Song deletion now list the files of their Generations' images in the group (`GenerationArtworkService.RetainedFilesAsync`, internal). Without this, the sweep could remove an image 24 hours after its Generation was deleted, and the restore would then fail on the foreign key. Regression tests cover a round trip and the vanished-asset note.
  **Why:** Otherwise a deleted Generation's image would be lost before the 30-day retention ends.
  **Issue:** #121
- **Decision:** "Copies it into a managed asset owned by the Song" is done as a new Song attachment of the same asset. The store is content-addressed (#97/#98: identical bytes are one asset), so a byte copy would be the same asset anyway.
  - The copy is independent of the Generation: the asset stays while the Song's attachment names it.
  - Tested: replacing the Generation's image leaves the Song's artwork byte-identical, and deleting the Generation's Version leaves the Song's cover served.
  - Picking goes through `ArtworkAttachmentService.ReplaceAsync`, so the Song's earlier artwork is retained and the crop is reset.
  - Picking the image the Song already owns uncropped stores nothing. Owned with a crop, the crop is reset and the revision is raised.
  **Why:** This follows the discretion line and #98's replacement rule without duplicating bytes the store would merge anyway.
  **Issue:** #121
- **Decision:** API shapes:
  - The Song's `artwork` keeps #98/#99's shape `{assetId, width, height, urls, crop, squareUrls}` and adds `source: own | selectedGeneration` (`SongArtworkResponse`).
  - The default comes from a new trailing `SongSummary.SelectedGenerationArtwork`, joined in the same query as the Selected Generation (left join on `assets`). It is computed at read time and never stored.
  - A Generation's `artwork` has the same shape (`AttachedArtworkResponse`), with `crop` always null. `GenerationResponse.From` / `GenerationListResponse.From` now take the `PathString`.
  - The planner's `{assetId, url, thumbnails}` is `urls.original` plus the size keys of `urls` and `squareUrls`.
  **Why:** Renaming fields would break the contract that #98's web client and the e2e tests read. The new field only adds to it.
  **Issue:** #121
- **Decision:** `PUT /api/v1/generations/{reference}/artwork` uses a new any-of scope marker, `RequireAnyScope(suno.sync, suno.generate, artwork.write)`.
  - `RequiredScopes.AnyOf`: when the token holds none of them, the answer is 403 `insufficient_scope` with `requiredScope` as the list of all three.
  - A session or an `artwork.write` token may replace an image (`ScopeMiddleware.Holds`). A `suno.*` token gets 409 `artwork_exists` (with `generationId` and `shortcode`) when the Generation already has a different image.
  - Identical bytes are a 200 no-op for anyone.
  - Both refusals and the no-op are decided from the content hash before anything is stored.
  - The session-only count stays 46.
  **Why:** The AC says one of three scopes. The existing marker requires every scope it lists.
  **Issue:** #121
- **Decision:** Generations have no last-updated column. An image upload moves the Song's updated time and raises no revision, as comments and archiving do.
  **Why:** The discretion line says "moves the Generation's last-updated time" and "bumps no revision". Adding a column only for this was not worth a schema change.
  **Issue:** #121
- **Decision:** A replaced Generation image leaves the store in the same transaction (`ArtworkService.RemoveNowIfUnusedAsync`, internal), whatever its upload time. It stays only if a live record attaches it or an unpruned retention group lists its files.
  **Why:** The discretion line says the image is removed at once and not retained. Accepted race: identical bytes uploaded moments earlier for another owner, but not yet attached, would be removed. That upload's PATCH then gets #98's 422 "upload the image again".
  **Issue:** #121
- **Decision:** `POST /api/v1/songs/{reference}/artwork/from-generation` (`artwork.write` only, per the AC) checks in this order:
  1. 404 for the Song (or `song_deleted`).
  2. 422 `validation_failed` on `generation`.
  3. 404 for the Generation.
  4. 422 `generation_not_in_song`.
  5. 422 `generation_has_no_artwork`.
  6. 409 `revision_conflict`.
  7. 409 `artwork_unavailable` when the original is missing from the store.
  **Why:** This matches #120's selection order: refusals before the revision check.
  **Issue:** #121
- **Decision:** Where the default shows: the Song header, the Details panel, and the Songs table (96-pixel thumbnail) all read the Song's computed `artwork`. Album and Playlist track lists show no Song artwork today, so nothing changes there.
  **Why:** The discretion line covers every place Song artwork is shown, and the track lists are not among them yet. A later story that adds artwork to the track lists should reuse `SongSummary.SelectedGenerationArtwork` / `SongArtworkResponse`.
  **Issue:** #121
- **Decision:** Web:
  - `ArtworkPicker` gains `inherited` (the note shown when the artwork is not the owner's own; Crop, Remove, and Keep crop are hidden then), `afterRemoval` (text for the confirmation), and `choose`, a render prop for a further control.
  - `songs/GenerationArtworkChooser.tsx` opens "Choose a Generation's image". It loads the Song's Generations when it opens, lists those with images ("Use the image of <sc>"), and retries a 409 once with the current revision.
  - `SongPage`'s artwork save fields read only `ownArtwork(song.artwork)`.
  - A Generation's image sits in its row's title cell (32 px), so cell indexes do not change. The panel shows it at 160 px, or "No image from Suno yet."
  - `testArtwork` now returns a `SongArtwork` (`source: 'own'` by default). `testGeneration` gives `artwork: null`.
  **Why:** A defaulted image is not the Song's own, so it cannot be cropped or removed. Treating it as the Song's own field would make the conflict check see changes nobody made.
  **Issue:** #121
- **Decision:** D8, as applied in #122. Each lineage table (`version_sources`, `version_inspiration_playlists`, `version_voices`, `version_file_inputs`) has its own `BEFORE INSERT`, `BEFORE UPDATE` and `BEFORE DELETE` freeze trigger. Each one refuses a change while its Version row exists with `is_frozen = 1`. On `version_sources`, the update trigger lets exactly one change through: the system rewrite of a deleted Generation's pointer into the external reference with the same Suno ID, with every other column unchanged. `external_suno_references` gets a trigger that refuses changing a Suno ID or kind.
  **Why:** This keeps #69's three layers (entity `WithLineage` → `EnsureMutable`, the store writing only after the guarded `versions` update, and the database) for the new tables. The rewrite is the discretion's "system rewrite of the pointer, not of the input": the source's identity is its Suno ID either way.
  **Issue:** #122
- **Decision:** Retention gains `RetainedType.FrozenWithParent`, set on the four lineage types. When their Version is deleted, these rows are removed after the Version, by its cascade, instead of before it. On restore they are inserted before the Version, while the restore's foreign keys are deferred.
  **Why:** A frozen Version's lineage triggers refuse any insert or delete while the Version row is present. Ordering around the parent lets Version and Song deletion and restore keep the lineage without any bypass in the triggers. The guard's retention exerciser compares the restored lineage byte for byte.
  **Issue:** #122
- **Decision:** A source names its target Generation or Song by ID with no foreign key. When Generations are deleted (Version or Song deletion now; #124 later), `IVersionStore.RewriteSourcesOfDeletedGenerationsAsync` runs first. It repoints each source in another Version whose Generation has a Suno ID to the shared external reference for that ID, labelled "Deleted" and carrying the Suno title. A source whose Generation has no Suno ID keeps the Generation's ID and reads as `missing: true`, and so does a Song target that was deleted.
  **Why:** The discretion makes a source outlive its target: a moved Generation is followed, and a deleted one becomes an external reference. A Generation without a Suno ID has nothing to rewrite to, so its ID stays as the frozen identity. That is the identity rule the discretion gives the guard: the Suno ID when there is one, otherwise the Generation ID.
  **Issue:** #122
- **Decision:** API shape:
  - `inputs` gains `sources`, `inspiration` (`{sources:[…]}` or `{playlist:{sunoPlaylistId,name,clipIds}}` or null), `voice` (`{personaId,name}` or null) and `fileInputs` (`[{kind,description}]`).
  - A source has `typeId`, exactly one of `generation`, `song` or `external`, `continueAtSeconds` and `secondaryIds`.
  - On write, `generation` and `song` take an ID or shortcode, as text or as an object with `id`. On read they are objects with `shortcode`, `title` and `missing`, and the read also gives `sunoAction`. Read-only fields are ignored when sent back, so a read can be sent back as it is (no change, no revision).
  - Inspiration sources take no type: it is always Use as Inspiration. File inputs are sorted by kind.
  - A rule violation is a 422 `validation_failed` with a new `rules` member (`field → [rule code]`) beside `errors`.
  **Why:** The AC says sources round-trip in `inputs` and a rule violation is "a field error naming the rule". A separate `rules` member keeps `errors` messages-only, as every other 422 has it.
  **Issue:** #122
- **Decision:** Write-time rule checks report only the rules that read a part the edit sent. The cross-part rules (Inspiration with Cover, audio file with an audio action) are also reported when `sources` is sent. An image or video on a Song that is not in Simple mode is refused on write. Individual Inspiration sources are not refused in Simple mode; they are only left out of `effectiveInputs`.
  **Why:** "Changing kind or mode never refuses." So a part kept as it is (say, an image left over after switching to Advanced) must not block an edit of another part. The test plan names only image or video on an Advanced-mode Song as a refusal; individual Inspiration in Simple mode is named only as omitted from `effectiveInputs`.
  **Issue:** #122
- **Decision:** `GenerationService.AttachAsync` refuses an incomplete lineage with the new outcome `GenerationAttachOutcome.IncompleteSources`: a Mashup without two sources, an Extend without its position, or a position past the source's known length. Only the rules a complete check adds are applied, not the write-time ones. `GenerationsEndpoints.AttachRefusal` maps it to 422 with `rules`; `seed-generation` prints the rule codes.
  **Why:** The discretion puts the complete-set rules at attach and at Generate on Suno. Applying the write rules again would let a later change of mode block an attach, against "changing kind or mode never refuses".
  **Issue:** #122
- **Decision:** The general Remix type is allowed only through `LineageOrigin.Import`: any number of Remix sources, never together with an audio action. The PATCH is `LineageOrigin.Edit` and refuses Remix with `source_type_import_only`. No public import method was added. #140 calls the domain rules with `Import` when it writes imported lineage.
  **Why:** "They cannot be chosen in the editor and are never automated", and there is no import path yet. A public service method now would widen the guard with no caller.
  **Issue:** #122
- **Decision:** A user relationship type is refused as a source type with `source_type_not_mapped` when its `SunoAction` is null, which is true of every user type until #126 adds the mapping. `version_sources.type_id` references `song_relationship_types` with RESTRICT. Automatic Song relationships are created only for the parts an edit sent (`sources` and/or `inspiration`), under the source's type (Inspiration uses Use as Inspiration), with the Version's Song as "from", and only when the two Songs are not already related under that type either way round. Create New Version From copies the lineage but makes no relationships.
  **Why:** These follow the discretion lines. Relating only on a sent part means editing the Voice cannot bring back a relationship the user removed. Copies stay in the same Song, so a relationship would point at itself.
  **Issue:** #122
- **Decision:** Inventory coverage: the exclusion list is now `["workspace"]`, owned by #129. The six reference and file fields map to lineage keys through `VersionLineageInputs.InventoryFields` (`audio`→`sources`, `inspiration` and `simple_add_playlist`→`inspiration`, `voice`→`voice`, `simple_add_image` and `simple_add_video`→`fileInputs`). The coverage test round-trips a value of each field and refuses a wrong one with a named rule. Deleting any mapping fails the test, which a six-case theory proves.
  **Why:** The AC requires a declared mapping that the coverage test reads, and requires the test to fail when one mapping is deleted.
  **Issue:** #122
- **Decision:** Web: `api/versions.ts` gains `LINEAGE_KEYS` and `isLineageKey`. `isVersionDetail` accepts lineage keys holding objects, arrays or null, and `VersionDetails` leaves them out of option keys and the conflict list. No editor UI was added; that is #125.
  **Why:** Without this the web reader would reject every Version answer, since `isOptions` required scalar values. #125 owns the sources editor.
  **Issue:** #122
- **Decision:** No e2e spec was added (Demo: none, agent-verifiable). The existing e2e suite was rerun against an image built from this change.
  **Why:** The story has no UI and no Demo walk. API integration tests cover the behaviour.
  **Issue:** #122
- **Decision:** #122 lands as one commit, not four (model, triggers, guard, API) as the M4 plan's risk note suggested.
  **Why:** None of the four compiles or passes on its own. The entity's new `Lineage` parameter changes every construction site and the store. The guard's `inputs` key check fails until the API returns the lineage keys. The retained-shape and startup tests fail until the migration exists. Splitting would leave intermediate commits with a red gate on a shared milestone branch.
  **Issue:** #122
- **Decision:** D2, applied. A new migration, `AddShortcodeAliases` (`20261006220000`), adds `shortcode_aliases` (alias PK, lower case by CHECK; `generation_id` with no FK, so an alias outlives deletion and purge; `created_utc`). It drops `tr_generations_identity_never_changes` and adds two triggers:
  - `tr_generations_move_only_leaving_an_alias`: a change of a Generation's Version, Song or ordinal is refused unless all of these hold. It goes to another Version. That Version is frozen and belongs to the Song named. The new ordinal equals that Version's `last_generation_ordinal`, so it was just given and an ordinal is never reused. The old shortcode is recorded as this Generation's alias. The new place is not another Generation's alias.
  - `tr_generations_aliases_stay_reserved`, on insert: no Generation, a restored one included, may take a shortcode that is another Generation's alias.

  `DatabaseStartupTests`, the `Generation.cs` doc and the `GenerationStore` doc are updated.
  **Why:** This is the orchestrator's D2: forbid unsafe identity changes and allow only the move path. Checking the alias inside the trigger makes "a move leaves an alias" a database rule, not just a service rule. Comparing against the newest ordinal enforces "never reused" without a separate table.
  **Issue:** #123
- **Decision:** One shared move path. `SongVersion.ReceiveGeneration` (domain) gives the next ordinal, freezes the Version and raises both revisions. `IVersionStore.TryMoveGenerationAsync` writes the alias, the target's freeze and the Generation's place in one call. `GenerationMoveService.MoveWithinAsync` is internal and #141 reuses it. Its only public method is `MoveToNewSongAsync`. It refuses to move a Generation that is still selected out of its Song.
  **Why:** This follows the M4 plan's risk note for #123/#141: one move service. Internal keeps the guard's public surface to the one user path. #141 calls it inside its commit transaction with a child Version it creates.
  **Issue:** #123
- **Decision:** The selection choice is shared with #124 and lives in `GenerationSelectionService`:
  - Public record `SelectionChoice(ReplacementGeneration, WorkflowState)`.
  - Internal `CheckChoiceAsync` and `ApplyChoiceAsync`.
  - Constants `selection_choice_required`, `replacementGeneration` and `workflowState`.

  The rules:
  - The replacement may be any other Generation of the Song, whatever its state.
  - Sending both choices is a 422 under `replacementGeneration`.
  - A choice sent for a Generation that is not selected is a 422 under the field sent.
  - A workflow state clears the selection, then sets the state. Each write raises the Song's revision.

  `GenerationSelectionService` now also takes `IWorkflowStateStore`.
  **Why:** The story says the choice is made "exactly as when deleting it" and the refusal code is "shared with the deletion story". #124 can call the same two internal methods.
  **Issue:** #123
- **Decision:** The moved Generation always becomes the new Song's Selected Generation, not only when it was the old Song's.
  **Why:** AC 6 requires this when it was selected. The discretion says "the new Song … shows the moved Generation's" artwork, and a Song shows a Generation's image only through its Selected Generation. It is also the Song's only Generation.
  **Issue:** #123
- **Decision:** A move raises the Generation's revision by one. Rating, state, comments, image, Suno data, provider record and event link do not change. If-Match carries the Generation's revision, and the 201 sets the ETag to the new revision.
  **Why:** Another client holding the old revision should get a conflict and not act on the old place. The AC lists what must stay unchanged, and the revision is not on that list.
  **Issue:** #123
- **Decision:** The new Version 1 is written through `Song.Create`, then a direct `SongVersion` copy. The copy takes the source's name, lyrics, styles, inputs (kind and modes included) and lineage. Its notes are "Created from Version <source shortcode> when Generation <old shortcode> moved to this Song." This text is how "the new Version records the source Version it came from"; no new column was added. The lineage is written with `ReplaceLineageAsync` before the move freezes the Version. The source Version's lineage stays as it is, and the lineage tables' freeze triggers allow inserts only while the parent is unfrozen. No automatic Song relationships are made from the copied lineage. The only one recorded is Derived From, new Song to old.
  **Why:** The discretion says "its notes say which Version it came from". A column would be a schema change the story does not name. Relationships from a copied lineage would duplicate the original Song's own.
  **Issue:** #123
- **Decision:** "The original Song's Suno workspace" is not copied: Songs have no workspace field yet (#129 adds workspaces). The new Song starts in the first visible workflow state, with no primary Artist, Genres, Tags, memberships or artwork.
  **Why:** There is nothing to copy today. #129 should copy `workspace` in `GenerationMoveService.MoveToNewSongAsync` when it adds the field.
  **Issue:** #123
- **Decision:** Resolution:
  - An alias resolves with `status: "moved"`, `shortcode` and `canonicalShortcode` set to where the Generation is now, and the Song and Version where it is now.
  - Every Generation lookup by shortcode (`GenerationService.FindAsync`, used by every `/generations/{reference}` endpoint) falls back to the alias. An alias of a deleted Generation resolves as its ID would (deleted).
  - A purged alias (`generation_id` null) names nothing, so it is a 404.

  Web: the Go to box, `/go/` and an old Generation URL (`/songs/<old>/generations/<old shortcode>`, which resolves and redirects) open the Generation where it is now. The panel then says "<old> moved: this Generation is now <new>." The router state is `{movedFrom}`.
  **Why:** The key link and the must-have say "an old shortcode pasted anywhere still finds the Generation".
  **Issue:** #123
- **Decision:** The endpoint is `POST /api/v1/generations/{reference}/move-to-new-song`, `SessionOnly()`. The session-only count is now 47. Answers:
  - 201 with `{song, version, generation, alias}`, with `Location` set to the new Song.
  - 404 when the Generation is not found.
  - 409 `revision_conflict`, with `current` set to the Generation.
  - 428 or 400 when the revision is missing or invalid.
  - 422 `validation_failed` for the title or the choice.
  - 422 `selection_choice_required`, with `generationId` and `shortcode`.

  Guards: the invariant 1 guard has an API exerciser and a service exerciser. Each moves a fresh Generation off the frozen target, then asserts that the target's stored inputs are byte-identical and that the new Version's stored inputs equal them. `ReceiveGeneration` is classified as "copies the inputs as they are, and freezes". The scope guard and `ReferenceParameterGuardTests.Calls` are extended.
  **Why:** The route and the session-only requirement are in the story and its AC. Invariant 1 requires every new write path to be listed in the guard.
  **Issue:** #123
- **Decision:** The atomicity test swaps `IVersionStore` for a `DispatchProxy` that throws at `TryMoveGenerationAsync`, after the new Song, Version 1, lineage, relationship and selection choice have been written. It asserts that the whole-database fingerprint (`RestoreApi.Fingerprint`) is unchanged, and that the next Song still takes the next shortcode number.
  **Why:** The story's test plan says to force a failure after the Song is created and assert that nothing changed. A proxy needs no production test hook.
  **Issue:** #123
- **Decision:** Rule 3: `e2e/tests/generation-artwork.spec.ts` (#121) had a `playwright/no-conditional-in-test` warning, so `npm run lint` failed in `e2e/` on the branch. The conditional chevron click now sits in a helper, `openVersionOne`, which is the same pattern as its `openDetails`.
  **Why:** The gate must pass. This is a one-line move with no change in behaviour.
  **Issue:** #123
- **Decision:** #124 covers only deleting, retaining, and restoring a Generation, with the Song's selection resolved first. Its tombstone criteria (the attach refusal `suno_id_tombstoned`, tombstones that outlive the prune, one tombstone per Generation from Version and Song deletion) are left to #130, and Reimport to #140, as the story's own discretion lines say.
  **Why:** The planner moved those criteria to the sibling stories. #130 depends on #124.
  **Issue:** #124
- **Decision:** `GenerationDeletionService` (`Application.Generations`) puts the Generation into one retention group:
  - Kind `generation`, label `Generation <shortcode>`, and the group shortcode is the Generation's own shortcode, so `restore-deleted <shortcode>` finds it.
  - The only root is the Generation. Its comments, provider record and event link follow by cascade, through the types #117 and #119 registered.
  - The group's files are those of its own image, from `GenerationArtworkService.RetainedFilesAsync`.
  - The selection is resolved first, with #123's `CheckChoiceAsync` and `ApplyChoiceAsync`, so the group carries no `selected-generation` reference. A restore therefore never brings back the selection or the Song's state.
  - When the Generation is not selected, only the Song's updated time moves (`TouchSongAsync`) and its revision stays. With a choice, the Song's revision goes up, as #123's does.
  **Why:** The discretion lines say "restoring never restores the selection or changes the workflow state", that the retention group includes the provider record, and that the event link comes back if the event still exists (`generation-event-link` is Optional).
  **Issue:** #124
- **Decision:** Before retaining, the service calls `IVersionStore.RewriteSourcesOfDeletedGenerationsAsync([id], [], now)` (#122). Another Version's source that pointed at the Generation then points at the external reference for its Suno ID, labelled "Deleted". A Generation without a Suno ID stays pointed at by ID and reads as missing.
  **Why:** This is the orchestrator note for #124. It also matches the discretion line "keeps pointing at its Suno ID as an external reference labelled Deleted". #122 built this rewrite to keep the source's identity (its Suno ID), so the frozen Version's stored lineage is byte-identical. The test asserts this with the guard's `Stored()`.
  **Issue:** #124
- **Decision:** The API has two endpoints, both `SessionOnly()`, which brings the session-only count to **49**:
  - `GET /api/v1/generations/{reference}/deletion-impact` answers `{id, shortcode, isSelected, replacements[], commentCount, artworkCount, sourceVersionCount, revision}`:
    - `replacements` are full Generation answers: the Song's other live Generations in any state, and only when this one is selected.
    - `artworkCount` is 0 or 1.
    - `sourceVersionCount` counts distinct Versions, from the new `IGenerationStore.SourceVersionCountAsync`.
  - `DELETE /api/v1/generations/{reference}` takes If-Match and an optional body `{replacementGeneration | workflowState}`, and answers 200 `{song}`. Its errors are:
    - 422 `selection_choice_required`, with `generationId` and `shortcode`.
    - 422 `invalid_replacement`, with `errors`, for every choice #123's check refuses: both choices sent, a Generation that is not another of this Song's, an unknown state, or a choice sent for an unselected Generation.
    - 422 `validation_failed` for a field of the wrong type.
    - 400 `invalid_request` for a body that is not a JSON object.
    - 409, 428, and 404.

  The body is read by hand, as the Song DELETE reads its body.
  **Why:** The route and error codes come from the story. A bound body parameter on a DELETE makes a request without a body miss the endpoint and fall through to the 404 fallback, which was seen in the first test run. #123's check already validates the replacement and the state, so this story reuses it and maps its refusals to the story's one code.
  **Issue:** #124
- **Decision:** Restore uses #105's `DeletedItemsService`, unchanged except in two places:
  - `HolderHintAsync` has a Generation case: when the Generation's Version or Song is in a retention group, the refusal names that group and how to restore it first.
  - The "not a shortcode" message gives a Generation example.

  A restore puts back the rating, comments, image and provider record. Like every restored record (#95), it raises the comments' revisions. A Generation deleted on its own does not resolve as `deleted` through `/resolve`; a GET of it is 404 `not_found`.
  **Why:** `FindByShortcodeAsync` already looked up Generation shortcodes, and the generic parent check already refuses with `MissingParent`, which is the story's `parent_missing` rule. No AC asks for a resolver status. It can be added with #130 or later if the import review needs it.
  **Issue:** #124
- **Decision:** Guards:
  - The invariant 1 guard has an API exerciser (`DELETE /generations/{reference}`) and service exercisers (`GenerationDeletionService.DeleteAsync` and `ImpactAsync`). Each deletes a fresh Generation off the frozen target and restores it.
  - The scope guard and `ReferenceParameterGuardTests.Calls` cover both new routes.
  - The new architecture test `DeletingAGenerationReachesNeitherTheNetworkNorTheExtension` walks the constructor-dependency closure of `GenerationDeletionService`, following ports into their Application and Infrastructure implementations. It asserts that nothing in the closure depends on `System.Net.Http`, `System.Net.Sockets`, `System.Net.WebSockets` or `n8Tracks.Application.Credentials`, where the extension handshake lives. Its complements: the closure reaches `GenerationStore` and `RetentionStore`, and the rule flags `ExtensionHandshakeService`.
  **Why:** The test plan asks for "no dependency that can reach the extension or the network (asserted by the layering guard)". A check of direct dependencies only would miss the stores behind the ports.
  **Issue:** #124
- **Decision:** Web:
  - The Generation panel has a "Delete Generation" button, beside "Create new Song from Generation". The row actions menu does not get one.
  - `generations/DeleteGenerationDialog.tsx` reads the impact when it opens. Its summary (`deletionRules.ts`, testid `delete-generation-summary`) names the shortcode, the rating, the comment count, the Suno artwork, and the Versions that use it as a source, and says that nothing in Suno changes and that it can be restored for 30 days.
  - For a selected Generation, the dialog shows radio groups ("Instead"; "Generation to select instead"; "Workflow state once the selection is cleared"). Nothing is pre-selected and nothing needs typing. When the Song has no other Generation, only the state group is shown. Only visible states are listed. Delete stays disabled until a choice is complete.
  - A conflict, a `choice-required` answer or a refused choice reads the impact again.
  - After a delete, the panel closes onto the Version and the notice `generation-deleted` is shown.
  **Why:** The AC puts the control in the Generation panel. The discretion lines ask for every visible state with none pre-selected and no typing. Radio groups make "none chosen" explicit, where a select would show its first option.
  **Issue:** #124
- **Decision:** The Suno playlist and persona read models are new tables made by this story: `suno_playlists` (`suno_id` PK, `name`, `clip_ids` JSON, `last_seen_utc`) and `suno_personas` (`suno_id` PK, `name`, `last_seen_utc`), in migration `AddSunoPlaylistsAndPersonas` (`20261006230000`, renamed from EF's generated timestamp so it sorts after #123's). They are empty until #137/#153 fill them. `Application/Suno/SunoLibraryService` lists them over `ISunoLibraryStore`.
  **Why:** The story's discretion says it defines the two read endpoints "against empty tables", and no earlier story made them. #137 records their shape ("ID, name, member clip IDs as last seen" and "ID, name"); `last_seen_utc` is added for the playlist answer's `lastSeen`. Keyed by the Suno ID, because a playlist or persona is identified by it everywhere else (`InspirationPlaylist.SunoPlaylistId`, `VersionVoice.PersonaId`).
  **Issue:** #125
- **Decision:** `GET /api/v1/suno/playlists` and `GET /api/v1/suno/personas` (`catalog.read`) answer `{items:[…]}`, not a bare array. Each playlist item is `{id, name, memberCount, clipIds, lastSeen}` and each persona item is `{id, name}`, sorted by name (ignoring case, then by ID) and unpaged.
  **Why:** Every other list endpoint answers `{items}` (`relationship-types`, `workflow-states`, `songs/{ref}/generations`), so the discretion's `[{…}]` is read as the item shape. `clipIds` is added because a Version keeps the playlist's clip snapshot (`inspiration.playlist.clipIds`, #122), and the picker has nowhere else to take it from.
  **Issue:** #125
- **Decision:** Each source in a Version answer has `availability`: `ok`, `not_imported`, `deleted`, `trashed` or `missing`. It is computed by `SourceTargetView.Availability` from the target as read:
  - A Generation or Song no longer in the catalog, or an external reference labelled "Deleted" (#122's rewrite), is `deleted`.
  - Any other external reference is `not_imported`.
  - A Generation's `remote_state` gives `trashed` or `missing`.
  - Everything else is `ok`.
  A Generation target also gains `title` (its Suno title) and `durationSeconds`.
  **Why:** The discretion asks for the server-computed value. The duration lets the editor bound Extend's position as the server's `continue_at_beyond_source` rule does. What a read adds is ignored when it is sent back, so a read sent back is still no change.
  **Issue:** #125
- **Decision:** A source sent as `external` whose Suno ID a live Generation has is resolved to that Generation (`IVersionStore.FindSourceGenerationBySunoIdAsync`). This happens only when the group does not already name that Suno ID as an external reference, and never to the Version's own Generation.
  **Why:** The discretion line reads: "an ID n8Tracks already has resolves to that Generation". The exception keeps the invariant 1 property "a read sent back is no change": a frozen Version's external source stays external, even after an import brings in a Generation with that ID.
  **Issue:** #125
- **Decision:** Web structure:
  - The section is `web/src/versions/SourcesSection.tsx`, at the must-have path. Beside it are `SourcePicker.tsx` and the pure rules in `sourcesRules.ts`. The API side is in `api/lineage.ts`.
  - The editor keeps each lineage key in `drafts.inputs`, as the API reads it. Edits and conflicts compare them by `lineageText`, the write form with what a read adds left out and empty forms unified, through `inputText(key, value)`.
  - `inputKeys` always includes the four lineage keys.
  - After a successful save, each lineage part that was sent is replaced by the API's stored value, unless the user changed it again (`adoptSaved`). This brings in the shortcodes and titles, and a pasted ID resolved to a Generation, without sending it again.
  - The conflict dialog lists the four parts as "Sources", "Inspiration", "Voice" and "Files to attach in Suno".
  **Why:** One autosave queue and one revisioned save for the whole Version, as #65 requires. Adopting the answer only after a save, not in `follow`, keeps a conflict's other-client value from silently replacing the user's unsaved change.
  **Issue:** #125
- **Decision:** Interaction choices where the discretion leaves room:
  - The audio action is a native select, "Action", listing the relationship types whose `sunoAction` is an audio action, so a #126-mapped user type appears without change. Before a source exists, the chosen action is local state.
  - The Inspiration form and the Voice also use native selects. The picker uses the shared `SongSearch`, a table of the Song's Generations (the Version's own left out; those already in the group shown as "Already a source") and a field for a pasted address or ID.
  - A confirmation is asked whenever a change would also remove something: the second Mashup source, Inspiration on Cover, the audio file note when an action is chosen, or every source when the action is set to None.
  - The audio-file-note dialog says what it replaces; saving the note is the confirmation.
  - Individual Inspiration songs stay visible in Simple mode, with a note that Suno takes them only in Advanced.
  - The playlist chooser is offered only while no song is chosen, and the reverse.
  **Why:** Native selects are keyboard-operable as they are ("every picker is operable by keyboard"). Showing the record of the other form, rather than switching between two forms, makes the exclusivity visible. The discretion asks for a confirmation for each case it names; the None case removes sources in the same way.
  **Issue:** #125
- **Decision:** In component tests, dialogs are found by role and name and are not waited on to become visible.
  **Why:** Mantine's opening transition advances on animation frames, which the fake clock (`fakeTimeouts`) does not move, so a `toBeVisible` wait timed out at random. user-event acts on the dialog either way.
  **Issue:** #125
- **Decision:** Rule 3: #92 left the CHECK `ck_song_relationship_types_suno_action` as `suno_action IS NULL OR is_system = 1`, which forbids the user mapping this story stores in that column (a 500 before this fix). Migration `AllowUserTypeSunoAction` (`20261006233000`) widens it to also allow a user type with one of the five audio actions. The migration edits the stored table definition in place inside the migration transaction (`PRAGMA writable_schema`, then `RESET`). This is the procedure SQLite documents for a change that every row already satisfies. Down clears user mappings first, then narrows the CHECK again.
  **Why:** The story names this column as the storage, so no table or key changes. EF Core's rebuild switches foreign keys off outside the transaction. It then logs a startup warning, which made the no-warning startup and logging tests fail. Renaming the table away with foreign keys on would repoint the FKs of `song_relationships` and `version_sources` at the old table, which was checked in sqlite3.
  **Issue:** #126
- **Decision:** The mapping is set on the type's PATCH as `sunoAction`, read as raw JSON: omitted means unchanged, null clears, and text must be one of `cover`, `extend`, `mashup`, `sample`, `reuse_prompt`; anything else is 422 on `sunoAction`. The service checks in this order:
  1. 404 or 409 `system_type`.
  2. 409 `revision_conflict`.
  3. A no-op when nothing differs.
  4. 409 `mapping_in_use` with `versionCount` (distinct live Versions with a source of the type, frozen or not).
  5. Taken names.
  These checks and the write share one transaction. POST does not take `sunoAction`: a type is mapped after it is added. A rename is allowed while the type is in use.
  **Why:** This follows the story's discretion. A rename changes no source, because sources name their type by ID and keep their own `suno_action`.
  **Issue:** #126
- **Decision:** Deleting a type is refused with 409 `type_in_use` and `versionCount` when any Version source is of it, even with `removeRelationships=true`. The count also includes deleted Versions that can still be restored: the store reads `type_id` and `version_id` out of the retained `version-source` documents. `mapping_in_use` counts only live Versions.
  **Why:** A retained source names its type by FK RESTRICT. If the type were deleted, restoring that Version would be refused. A mapping change is harmless for a retained source, which keeps the action it was written with.
  **Issue:** #126
- **Decision:** Web: Settings → Relationships has a "Suno action" column. A system type shows its fixed action as text (Cover, …, Inspiration, Voice, None). A user type has a native select, "Suno action for <name>" (Not mapped plus the five actions), which saves when it changes. The in-use refusals appear in the page's status area as a "Not changed" notice that gives the Version count. In the Sources "Action" picker, a user type is labelled with its action, for example "Reimagining of (Cover)". System types keep their bare names.
  **Why:** One control per row matches the page's existing row actions. Labelling the action tells apart several types that map to the same action.
  **Issue:** #126
- **Decision:** Invariant 1: the guard's exemption reasons for PATCH and DELETE `relationship-types` now name #126. A dedicated API test freezes a Version that has a source of a mapped type, tries to change and clear the mapping and to delete the type, and compares `VersionImmutabilityGuardTests.Stored` and the `version_sources` rows. No new Application namespace was added. `Application.Catalog` and `Application.Suno` are not in `CatalogServiceNamespaces`, and the complement test confirms that neither takes a catalog type.
  **Why:** The orchestrator asked for confirmation. `RelationshipService` takes only IDs and text.
  **Issue:** #126
- **Decision:** A workspace is identified by its Suno ID everywhere: it is the `suno_workspaces` primary key, as for `suno_playlists` (#125); the value of `songs.suno_workspace_id`, a FK with RESTRICT; the API's `id`; the Song PATCH's `sunoWorkspaceId`; and the bulk-move route's `{id}`, a plain string because Suno's default workspace has the ID `default`. There is no separate n8Tracks UUID.
  **Why:** The AC says association is always by workspace ID, and the discretion line asks for the Suno ID to be unique. A surrogate key would add a second identity that every client would have to map back to Suno's. The default workspace's ID is not a UUID, so the route could not use `{id:guid}`.
  **Issue:** #129
- **Decision:** Schema: migration `AddSunoWorkspaces` (`20261006234000`; EF's generated timestamp was renamed so it sorts after `AllowUserTypeSunoAction`) creates `suno_workspaces`, with suno_id, name, description, state, first/last seen, and raw_json, and CHECKs on state and on the ID's length. It adds `songs.suno_workspace_id` with a hand-written `ALTER TABLE … ADD COLUMN … REFERENCES`, as #120 did. Song retention moves to shape 3, with upgrader `SongShape2To3`, which gives a Song "no workspace".
  **Why:** EF Core would have added the FK by rebuilding `songs`, and the M4 notes forbid rebuilding a table on SQLite. The CHECKs are in `CreateTable`, so no table is rebuilt for them. Workspace records are never deleted, so a restored Song's FK always holds.
  **Issue:** #129
- **Decision:** The discovery rules, beyond the discretion lines:
  - The extension's PUT returns every workspace plus the Suno IDs that were added, renamed, became unavailable, or became available.
  - In an incomplete list, a known workspace keeps its state even when reported as trashed. A workspace seen for the first time takes its state from `is_trashed` either way.
  - Limits: at most 1,000 workspaces per report, a name of at most 500 characters, a description of at most 5,000, and a raw project of at most 64 KB. Exceeding any of them is a 422 for the whole body.
  - The description is overwritten only when one is sent.
  **Why:** The key link says availability changes only when `complete` is true, so an incomplete report never moves a known workspace's state; the first-seen rule comes from the discretion. The limits stop a token from growing the database without bound. They are well beyond Suno's 20-per-page list.
  **Issue:** #129
- **Decision:** In the Song PATCH, an Unavailable workspace other than the Song's own is refused with 422 on `sunoWorkspaceId`. That check runs after the revision check, because it needs the Song's current workspace. The workspace is part of `SongDetails` (now a required sixth member) and of `SongStore.TryUpdateAsync`. `GenerationSelectionService` passes the Song's current workspace through.
  **Why:** The association is part of the Song's revision, per the discretion, so it travels with the Song's other details in one conditional write.
  **Issue:** #129
- **Decision:** `effectiveInputs.workspace` (`{id, name, state}`) is reported for every kind and mode whenever the Song has a workspace. It is never added to `inputs`. `VersionDetail` gains a trailing `Workspace`, which `VersionStore.FindDetailAsync` fills.
  **Why:** The discretion says `effectiveInputs` reports it for Generate on Suno. A result is saved to a workspace whatever the form, even though Suno shows the "Save to…" control only in Advanced mode.
  **Issue:** #129
- **Decision:** The bulk move is implemented here, as the API only, even though its page is #151's. `POST /api/v1/suno/workspaces/{id}/move-songs` is session-only, so the session-only count goes from 49 to 50. It is served by `Application.Songs.SongWorkspaceService.MoveSongsAsync`, which accepts each Song by ID or shortcode. Exactly one of `songIds` (not empty) and `all: true` must be sent. Error codes are 422 `too_many_songs` (with `limit` and `count`), 422 `song_not_in_workspace` (with `songs`, as sent), 422 `validation_failed` on `targetWorkspaceId` (unknown, the same workspace, or unavailable), and 404 for an unknown source workspace. Discovery and the list are `Application.Suno.SunoWorkspaceService`.
  **Why:** AC 6 makes the bulk move session-only and the Test plan tests it here, and #151 links to "the command described in #129's discretion". The move changes Songs, so it belongs in a catalog namespace that the invariant-1 guard enumerates (API and service exercisers added). Discovery touches no catalog type, so it is in the guard's exempt table.
  **Issue:** #129
- **Decision:** Web: the Details panel has a "Suno workspace" section after Tags, with a native "Workspace" select: None, then every Available workspace, plus the Song's own even when it is unavailable, labelled "(unavailable)". The select saves as soon as a choice is made, through the page's one `useRevisionedSave` under the key `sunoWorkspaceId`. While the Song's workspace is unavailable, a "Workspace unavailable" badge shows in both the header and the Details, with a notice in the Details. When a choice is refused, the workspace list is read again. There is no Settings page; that is #151.
  **Why:** This matches the Language field's choose-to-save pattern. The discretion puts the badge in the Song header and Details only.
  **Issue:** #129
- **Decision:** The inventory coverage test no longer has an exclusion list. It maps `workspace` to the Song association `sunoWorkspaceId` and checks it end to end:
  - a workspace reported with a `suno.sync` token can be set and is read back on the Song and in `effectiveInputs.workspace`;
  - an unknown ID is refused with a field error and changes nothing;
  - null clears it.
  The bite tests are "mapping removed" and "mapped to a field that does not store it".
  **Why:** This is AC 7. The checker still knows nothing of how n8Tracks stores the association.
  **Issue:** #129
- **Decision:** Provider tombstones are stored in a new table, `provider_tombstones`, created by migration `AddProviderTombstones` (`20261006235000`).
  - Columns: `suno_id` (the PK, `length > 0` as on `generations.suno_id`), `kind` (`clip` only), `deleted_utc`, and `title` (nullable).
  - The table has no foreign key, so the retention prune never reaches it.
  - The domain record is `Domain/Suno/ProviderTombstone.cs` (`ProviderTombstoneKind.Clip`). The service is `Application/Suno/TombstoneService.cs`, with `IProviderTombstoneStore`.
  - The service's public method is the check, `FindAsync(sunoId)`. `RecordForAsync(generationIds, deletedUtc)` and `RemoveAsync(sunoId)` are internal and run inside the caller's transaction.
  - The service takes no catalog type, so it stays outside the invariant-1 guard's catalog namespaces.
  **Why:** The new table is the one #124's and #130's discretion name. Keying by Suno ID alone makes a tombstone independent of the retention group, so it outlives the prune.
  **Issue:** #130
- **Decision:** A tombstone is recorded inside each deletion's transaction, after the sources are rewritten and just before `RetainWithinAsync`, using the deletion's own time. This happens in `GenerationDeletionService.DeleteAsync`, in `VersionDeletionService.RetainAsync` (both the plain path and the path that creates a blank Version), and in `SongDeletionService.DeleteAsync`. The Suno IDs and titles are read from the live `generations` rows of the IDs being retained, so a Generation without a Suno ID writes nothing. Recording an existing Suno ID again replaces that tombstone.
  **Why:** This is where #124 left the hook. The deletion is then atomic: either the group and the tombstone both exist, or neither does.
  **Issue:** #130
- **Decision:** Removal on restore is an `AfterRestoreAsync` on `RetainedTypes.Generation`. It deletes the tombstone for each restored row's `suno_id`, through `ProviderTombstoneStore.RemoveAsync(context, …)`. I did not put it in `DeletedItemsService.RestoreWithinAsync`.
  **Why:** Every restore path removes the tombstones of exactly the Generations that came back, and nothing else. That includes the container command and `RetentionService.RestoreAsync`, which #140's Reimport-through-retention uses. A refused restore rolls the removal back with everything else. Generations deleted earlier in groups of their own are not in the group, so their tombstones stay. This follows the existing restore-rule pattern (`EditorSnapshot` prunes through `EditorRevisionStore(row.Context)`).
  **Issue:** #130
- **Decision:** `GenerationAttachOptions` gains a trailing `bool Reimport = false`.
  - Without it, `GenerationService.AttachAsync` returns `GenerationAttachOutcome.SunoIdTombstoned(ProviderTombstone)` for a tombstoned Suno ID. The code is `GenerationService.SunoIdTombstonedCode = "suno_id_tombstoned"`, and `GenerationsEndpoints.AttachRefusal` answers it as 409 with `sunoId` and `deletedAt`.
  - With it, the clip attaches and its tombstone is removed in the same transaction.
  - The check runs after the `suno_id_exists` check, so a live Generation wins.
  - The `seed-generation` command reports the refusal and never reimports.
  **Why:** This is the key link in #130 ("refuses a tombstoned Suno ID with `suno_id_tombstoned` unless the reimport option is set"), and invariant 3 needs it. The precedence (live first) matches #131's classifier rule. No attach endpoint exists yet, so the 409 is unit-tested through `AttachRefusal`.
  **Issue:** #130
- **Decision:** `GenerationServiceTests.TheSunoIdIsUniqueAmongLiveGenerationsOnly` (#117) now attaches the deleted Version's Suno ID again only with `Reimport: true`. First it asserts that the plain attach is refused with `SunoIdTombstoned`.
  **Why:** The test assumed a retained Generation's Suno ID could simply be attached again. #130 deliberately changes that (invariant 3). The test's own comment had deferred reimport to "the deletion story", which is this one.
  **Issue:** #130
- **Decision:** The branch head 03bc122 was checked before any change by a full `dotnet test` (Release). Results: Architecture 138, Gateway 215, AppHost 23, and Api 1,898 passed, with none failing. No Rule 3 fix was needed.
  **Why:** The task asked for the baseline to be confirmed green first.
  **Issue:** #131
- **Decision:** Export staging uses five new tables, all from one migration, `AddSunoExportStaging` (`20261006235500`). EF's generated timestamp was renamed so that it sorts after `AddProviderTombstones`.
  - `suno_exports`: the export's state, its creator credential (null for a session), the header fields, the raw workspaces and playlists, the times, the job, and the `revision`.
  - `suno_export_parts`: each part's body as received, keyed `(export_id, part_number)`.
  - `suno_export_records`: one row per Suno ID, keyed `(export_id, suno_id)`. It holds the raw JSON, the trashed flag, the list fields, the class, the `flags` and `changed_fields` JSON, the `generation_id` (no FK), the `artwork_asset_id` (FK to `assets`, RESTRICT), and the empty `proposal_json` and `choice_json`.
  - `suno_export_record_playlists`: the playlist join table.
  - `suno_ignored_items`: created empty, in the shape #143 gives (`suno_id` PK, `title`, `workspace_id`, `ignored_utc`, `last_status`, `last_seen_utc`, no FK).
  Rows cascade from the export.
  **Why:** The story names `suno_exports`, `suno_export_records`, the playlist join table, and the empty ignore table. `suno_export_parts` is my addition inside the staging subsystem the story creates. It holds no catalog data. Keeping each part whole makes "a repeated partNumber replaces" a delete-and-insert, and keeps the trashed-wins and last-wins collapse deterministic whatever order the parts arrive in. Collapsing at completion, one part at a time, keeps memory to one part (at most 20 MB) for an export of 50,000 clips.
  **Issue:** #131
- **Decision:** An export has a state the design doc did not list, `classifying`, between `receiving` and `ready`. Completion moves the export there. An export of at most 2,000 clips (counted as received) is classified inline and answers `ready`. A larger one answers `classifying` with `jobId` (job type `suno-export-classify`), and becomes `ready` when the job ends. Each step of classification runs in its own transaction, which first checks that the export is still classifying, so discarding it midway stops the job. A failure marks the export `failed` and removes its staged rows. `docs/suno-integration.md` now describes the states and endpoints.
  **Why:** "Completing twice is 409 `export_not_receiving`" needs a state that is no longer receiving while a large export is classified in the background, and `ready` would be untrue until classification finishes.
  **Issue:** #131
- **Decision:** These choices fill in what the story left open about errors and the API shape:
  - The `formatVersion` refusal is 422 `unsupported_format`, carrying `formatVersion` and `supported`.
  - The header must have `format`, `formatVersion`, `capturedAt`, `scope.kind`, `libraryComplete`, and `trashedComplete`. `workspaces`, `workspacesComplete`, and `playlists` are optional. Clips in the header are refused.
  - A body that is not UTF-8 JSON is 400 `invalid_request`.
  - Both size limits are 413 `export_too_large`, carrying `limit` and `count`.
  - Discarding an export that is being committed, or has been committed, is 409 `export_not_discardable`. Discarding one that has already ended answers 200 with the export as it is.
  - Staging artwork on an export that is not ready is 409 `export_not_ready`, because #134/#152 send images after the export is ready.
  - The records list returns 400 `invalid_request` for an unknown, repeated, or out-of-range parameter.
  **Why:** These are low-cost choices. They match the existing problem codes and the order the extension uses.
  **Issue:** #131
- **Decision:** Exports are visible according to who asks. A credential's calls reach only the exports it created. For another credential's export, every route answers 404, so the export's existence is not revealed. A signed-in session reaches every export. Workspaces from a complete list (`workspacesComplete: true`) are applied through `SunoWorkspaceService.RecordAsync(..., complete: true)` when the export becomes ready, not when it is created. An incomplete list waits for the commit (#140).
  **Why:** The AC says "a credential can read only the exports it created" and "a signed-in session can read every export". Applying the workspaces at completion means a sync cancelled midway (which the extension discards, #134) changes nothing.
  **Issue:** #131
- **Decision:** The classifier compares title, tags, duration, `major_model_version`, `model_name`, the three BPM values, key, and the image address without its query or fragment. It does not compare the model label, the status, the audio address, the workspace, or the batch index. A changed record carries the fields that differ in `changedFields`. The record `flags` are `repeated` (the ID appeared more than once) and `alsoInLibrary` (the ID was in both lists). `conflict` is never produced yet: `Classify` takes an `inputsDiffer` flag, which the classifier passes as false until the mapping stories (#135–#137) can compute it, and a unit test covers the rule. The tombstone lookup is batched through a new `TombstoneService.TombstonedAsync` and `IProviderTombstoneStore.TombstonedAsync`. Live Generations and the ignore list are batched through `ISunoClipLookup` (`SunoExportStore`). Batches hold 500 records.
  **Why:** The story's discretion lines name these fields and the order of precedence. #130's note asked for a batch lookup for large exports. "Reported model" is defined in the design doc as `major_model_version` and `model_name`.
  **Issue:** #131
- **Decision:** Every table is classified as catalog or not. The invariant 3 guard (`SunoExportStagingGuardTests`) puts each one in exactly one of two lists, and a table in neither list fails the guard.
  - **Catalog:** every M2–M4 catalog table, plus `provider_tombstones`, `suno_ignored_items`, `suno_playlists`, `suno_personas`, `suno_models`, the retention tables, and `settings`.
  - **Not catalog:** the four staging tables, `suno_workspaces`, `assets`, `credentials`, `sessions`, `jobs`, `administrators`, `app_metadata`, and the EF history and lock.
  `suno_workspaces` is not catalog data, because its names and availability are provider state that a complete list applies at once. The Song association it supports is catalog data, and lives in `songs.suno_workspace_id`. The guard's bite test makes the clip lookup write `provider_records` during classification, and asserts that the guard reports that table.
  **Why:** The discretion line says the guard names the catalog tables explicitly and fails on a new table in neither list. A staged image is an asset row, stored content that nothing in the catalog references until the commit.
  **Issue:** #131
- **Decision:** Expiry is a second step of `RetentionPruneJobHandler`, the daily job from #95, through `ExportStagingService.ExpireAsync`. It runs even if the prune step fails. Its counts go into the job result (`exportsExpired`, `exportsDiscarded`, `exportsFailed`, `committedExportsCleared`). Each export it ends keeps its row, so its final state can still be read.
  - A ready export is expired 7 days after it became ready.
  - An export still receiving is discarded 24 hours after it was created.
  - An export still classifying is failed 24 hours after it was completed.
  - A committed export has its staged rows removed 24 hours after the commit.
  **Why:** The key link names "a second step in the same scheduled job". A job interrupted by a restart would otherwise leave an export stuck in `classifying`.
  **Issue:** #131
- **Decision:** A staged cover image is uploaded through `ArtworkService.UploadAsync`, which validates it like any artwork, and is held on the record as `artwork_asset_id`. `SunoExportStore` is registered as an `IArtworkAttachments`, so the sweep keeps the asset while a staged record holds it. Once the export's rows are gone, the sweep removes the image. A second image replaces the first. The answer is `{sunoId, artwork}`.
  **Why:** This is AC 9: "held with the export, and changes nothing in the catalog". Using the attachment port that already exists means no new sweep rule is needed.
  **Issue:** #131
- **Decision:** The panel opens from the toolbar popup. On a suno.com tab, while the extension is connected, the popup shows "Show the panel on Suno". The button sends `{type:'toggle-panel'}` to the tab with `chrome.tabs.sendMessage` and then closes the popup. If the tab has no content script yet, because it was opened before pairing, the popup first injects `suno.js` with `chrome.scripting.executeScript`.
  **Why:** The discretion says "opened from the extension's toolbar button". The manifest has `default_popup`, so `action.onClicked` never fires. Removing the popup would lose the connection view that #128 built. `tabs` and `scripting` are already allowed (D4), so no permission is added.
  **Issue:** #132
- **Decision:** The Suno content script `suno.js` is registered for `https://suno.com/*` at `document_idle`. It is registered beside the relay, under the ID `n8tracks-suno`, when the extension connects. It is checked again on every handshake, which re-registers a missing script, and unregistered on disconnect. Like the relay, it is a separate classic IIFE build in `scripts/lib/build.ts`.
  **Why:** D4 allows registered content scripts, and the manifest still declares none. Registering at pairing matches how the relay works. The script only reads the page until a workflow runs.
  **Issue:** #132
- **Decision:** The panel is an open shadow root on its own `<n8tracks-panel>` host, which carries `data-n8tracks-panel`. `find` never looks inside a host with that attribute.
  **Why:** An open root lets the unit-level axe checks reach the panel. A closed root would hide it from them. The host attribute keeps the panel's own controls, such as its "Close" button, out of the adapter's searches, so they can never make a Suno control ambiguous or be clicked by a workflow. A test proves this against the Download dialog snapshot.
  **Issue:** #132
- **Decision:** The DOM-access ban is ESLint's built-in `no-restricted-syntax`. It is set to error for `src/adapter/**`, `src/content/suno*.ts`, and `src/panel/**`. It forbids calling query methods (`querySelector`, `getElementById`, `getElementsBy*`, `closest`, and others), `.click()`, and `dispatchEvent`, in both dotted and bracket form. A second block turns the rule off for `src/adapter/primitives.ts` and `src/**/*.test.ts`, with a rationale comment.
  **Why:** The suppression guard allows `ignores:` only for build output, so the exemption has to be a rule set to `off`, which needs a rationale. The panel stays inside the ban: it keeps references to the elements it created and needs none of these calls. `test/dom-access-lint.test.ts` lints sample code as five in-scope files, expecting every finding, and as three exempt files, expecting none. `scripts/check-canaries.sh` passed after the change (19 passed).
  **Issue:** #132
- **Decision:** The panel lists the registered workflows in three groups: "Suno page", "Library sync", and "Generate on Suno". A group with no registered workflow is left out, so this version shows only "Suno page › Recognise the Suno page". Fill and sync workflows are test-only stand-ins until their stories register real ones.
  **Why:** The discretion says the list is whatever is registered and that this story registers only the recognition check. AC 6 is proven by a component test that renders sync and generate parts in every state. The Demo step 2 test uses stand-ins on the Advanced snapshot.
  **Issue:** #132
- **Decision:** The recognition workflow needs Suno's navigation Library link (`role=link`, `data-testid="navbar-library-tab"`). Its fixtures are `library-list`, `library-trash`, and `workspace-selector`.
  **Why:** The Suno logo appears twice on every page, so `find` would rightly call it ambiguous. The Library link appears once, in the three snapshots that are whole pages. The other snapshots are regions without navigation, and there the check correctly reports "not working".
  **Issue:** #132
- **Decision:** `Target` is `{role, name?, testId?, within?, description}`. `within` may be another Target or a bare `{testId, description}` region. `description` is the plain-words "expected …" text. For example, Suno's Styles box has no accessible name (placeholder only), so it is `{role:'textbox', within:{testId:'create-form-styles-wrapper'}}`, described as "a text box labelled Styles".
  **Why:** The discretion prefers role and label, then a stable test attribute, and never a class name. The TS-003 Styles box can be reached only through its wrapper's test attribute. Keeping the report text on the target makes every stop name what was expected in the same words as the Demo.
  **Issue:** #132
- **Decision:** These runner details were not specified, so I chose them:
  - `verify` is polled like `expect`, every 100 ms up to the step timeout. React updates after an event, so a single read-back would fail spuriously.
  - A failure of a check after polling has kind `check`, and a hung `act` has kind `timeout`. Both name the step.
  - `pageMayBeChanged` is true once any `act` has run.
  - The run's `Page` is bound to an `AbortSignal` that is aborted at the first failure and at the end. After that, set, choose, and click throw `StoppedError`, so an `act` still running after a timeout cannot change the page.
  - An exception's message is never copied into the report (invariant 6). A `PrimitiveError` reports its own plain words.
  **Why:** AC 3 says the run stops and changes nothing further on the page. The abort makes that structural, and a test proves it: a late click is refused and records no mutations.
  **Issue:** #132
- **Decision:** `set` handles text boxes and textareas with the prototype's native setter (`Reflect.set` with the element as receiver), followed by `input` and `change` events. It handles a slider through its range input if it has one. Otherwise it presses ArrowRight or ArrowLeft and stops when a key changes nothing or would pass the value. It never sends Enter. A contenteditable region, such as Suno's Lexical lyrics editor, is refused as "to take a typed value". #146 must add a primitive for it.
  **Why:** This follows the discretion on React inputs and sliders. Suno's sliders in TS-003 are `role=slider` divs with no input. Lexical needs `beforeinput` handling, and no story before #146 fills lyrics.
  **Issue:** #132
- **Decision:** `ADAPTER_VERSION` stays 1.
  **Why:** No adapter shipped before this story. Version 1 is the first one that has selectors, and nothing has reported another number. Later stories raise it whenever they change a selector or step.
  **Issue:** #132
- **Decision:** The field-map parity test lives in `extension/test/field-map.test.ts`. It reads `docs/suno-adapter-field-map.md` with node `fs`. It compares entry, field, and `how`, and fails on a missing, extra, or repeated entry and on a wrong `N entries.` line.
  **Why:** The `src/` tests are typed without node, and the document is outside the vite root. A change to one `how` in a copy of the document makes the test fail, as the plan requires.
  **Issue:** #132
- **Decision:** `SUNO_ORIGIN_PATTERN` moved to `src/adapter/addresses.ts`. `src/address.ts` re-exports it, so existing imports still work.
  **Why:** AC 1 says every Suno address pattern is inside the adapter.
  **Issue:** #132
- **Decision:** The guard lives in `extension/test/invariants/` (D7), not the `extension/tests/invariants/` path the story names: `sunoNeverMutated.guard.test.ts` (runtime and static), with the harness `workflowGuard.ts` and the TypeScript-compiler-API scan `sourceScan.ts` (tested in `sourceScan.test.ts` against fixture files in `test/invariants/fixtures/*.ts.txt`, which no build, lint, or format step reads).
  **Why:** Vitest only includes `src/**`, `scripts/**`, and `test/**`. Keeping the scan and the harness as separate tested modules follows the plan's risk note.
  **Issue:** #133
- **Decision:** `tsconfig.node.json` gains the `DOM` and `DOM.Iterable` libs and the `chrome` and `vite/client` types, rather than a separate tsconfig for `test/invariants`.
  **Why:** The runtime guard imports the adapter and the snapshot loader, which need DOM and `import.meta.glob` types. A separate project would need `test/invariants` excluded from the node project, and `scripts/check-suppressions.sh` rightly refuses any `exclude` except build output. Adding libs strengthens nothing and weakens nothing; strict settings are unchanged.
  **Issue:** #133
- **Decision:** The matcher (`src/adapter/forbidden.ts`) is pure: it classifies `ControlFacts` that `primitives.ts` gathers (role, accessible name plus `aria-label`, text, and `title`, a CSS-selector test, the nearest dialog's title, the inline field label, form submission). Names are read lazily, because reading one walks the subtree. The press check covers the element and every element it sits in, since the click reaches them too.
  **Why:** Only `primitives.ts` touches the page (the ESLint rule from #132). Classifying every element of a 160 KB snapshot was about 50 s with eager names and about 1 s with lazy ones.
  **Issue:** #133
- **Decision:** The create-workspace exception covers two controls: the inline row's Confirm (recognised by the row's "New workspace name" field, the inline "dialog's" title, not by its own text), and the list's "Create new workspace" entry that opens the row. Only `Page.createWorkspaceClick` presses them. The static scan allows that primitive only in `src/adapter/workflows/workspace.ts`, the file #145 plans. The runtime guard allows an exception's activation only for a workflow whose recipe names it.
  **Why:** TS-003 shows the create-workspace "dialog" is an inline row with no `role=dialog`, and `POST /api/project` came "from the inline row". The opener is part of the same permitted change, and #145 cannot reach Confirm without it. Its name starts with "Create", so without the exception the matcher would refuse it.
  **Issue:** #133
- **Decision:** Every dialog except "Overwrite Lyrics & Styles?" fails closed. In that dialog only Overwrite and Keep Current may be pressed; its Close is refused. So the Voice and Inspo pickers and the Download dialog are refused for now: #146 must add the pickers to `RECOGNISED_DIALOGS` with their allowed controls, and #216 adds the Download dialog through its own primitive.
  **Why:** The discretion says an unrecognised dialog title fails closed, and the TS-003 replan recognises only the create-workspace row and the Overwrite dialog. The AC names Overwrite and Keep Current only.
  **Issue:** #133
- **Decision:** A refusal is a new `StepFailure.kind`, `refused`. `failureText` reads "<title>: step '<name>' refused: forbidden control (<target description>: <reason>)". The refusal poisons the run's page handle, and the runner checks it after `act` and on timeout, so a step that catches the refusal itself is still stopped. Report words are the adapter's own, never the page's: control names can hold a song title.
  **Why:** The discretion says "refused: forbidden control" and "there is no override". Invariant 6 forbids page values in reports.
  **Issue:** #133
- **Decision:** "Try again" in the panel appears on a stopped workflow. It calls `AdapterSession.forget(id)` and re-runs the self-check. It does not re-run the workflow.
  **Why:** Workflows run with values that come from n8Tracks (#134, #145+), and the panel has none to give. Forgetting the stop returns the workflow to its self-check state, and the user starts it again from where it was started. Re-running from the panel can come with the stories that start runs.
  **Issue:** #133
- **Decision:** The static scan resolves symbols with the type checker. It flags a reference only when it is declared by the browser's or a package's types, so the `fetch` option of `Connection` is not flagged. Page context is the `adapter/`, `page/`, and `panel/` folders, `content/suno*`, and everything they import. The scan names `apiClient.ts` (paired origin), `adapter/imageReader.ts` (#152's credential-less read, the one Suno exemption), and `page/observe.ts` (#134; wraps only: no address literal, `Request`, or `URL` of its own). It also flags any use of `chrome.downloads`, which #216 adds.
  **Why:** These are the file names #152 and #134 plan. Resolving symbols catches aliases (`globalThis.fetch`, `window['fetch']`, destructuring) that a text search misses. The string `'submit'` is left to the reference check, because it is also an input type and an event name.
  **Issue:** #133
- **Decision:** Rule 1: `labelsOf` in `primitives.ts` failed with "labels is not iterable" on a hidden input, whose `labels` is null rather than undefined. It now treats null as no labels. Regression test: "names a hidden input without failing".
  **Why:** The matcher names every element of every snapshot, and the playlist and library snapshots have hidden inputs.
  **Issue:** #133
- **Decision:** Rule 3: `element(page, id)` moved from `ui/connectionView.ts` to a new `ui/element.ts`. Only the popup and the options page import it.
  **Why:** `connectionView.ts` is built into the Suno content script, through the panel. Its `getElementById` was a way to query Suno's page from page-context code, and the static scan rightly found it.
  **Issue:** #133
- **Decision:** CLAUDE.md is unchanged. Invariant 4's line keeps "guard: #133 (planned)".
  **Why:** In CLAUDE.md, a guard reads "(merged)" only once it is on main, and #122's guard, done on this branch, still reads "(planned)". The milestone merge updates it.
  **Issue:** #133
- **Decision:** A sync is read in legs, one list per Suno page. The legs are the workspace list (`/me/workspaces`), then the library (`/me`) or each chosen playlist (`/playlist/<id>`), then the Trash (`/me/trash`). Each page is reached by address (`Page.go`, suno.com only), never by pressing a link. Between page loads the service worker holds the sync in `chrome.storage.session` (`SyncSession`). Each new content script asks `sync-resume` and reads its leg. This deviates from the discretion line "read progress is held in the content script ... if the content script is lost the read fails".
  **Why:** The Trash is reachable only by its address: #133's matcher refuses the library's "Trash" button, and changing that would weaken invariant 4's guard. Suno is a Next.js app, and a foreign history entry makes it reload. So at least one full page load per sync is unavoidable, and the read must survive it. One mechanism for every list is simpler than mixing in-app clicks with a reload. It also needs no page structure that TS-003 did not capture: there is no snapshot of the workspace list or playlist list pages. The forbidden-control matcher already says "the adapter reaches pages by address". A tab that is closed, leaves suno.com, or is on the wrong page when its leg loads still fails as "Suno tab lost" and discards the export.
  **Issue:** #134
- **Decision:** A workspace-scoped read scrolls the whole Library › Songs feed and keeps the clips whose `project.id` is a chosen workspace. It does not drive the workspace selector on /create.
  **Why:** The TS-003 replan says the library feed covers every workspace and gives each clip's `project`. Paging a workspace feed beyond page 1 is unverified (D11), and the snapshot shows no pager. The library feed's paging is verified. The cost is reading the whole library. The library feed's default filters also leave out disliked clips, which the workspace feed's filters do not, and the export records those filters. This needs the owner's live check.
  **Issue:** #134
- **Decision:** The export header's `libraryComplete`, `trashedComplete`, and `workspacesComplete` are what the read sets out to read: true, true, and true for a whole-library read, and `libraryComplete` false for scoped reads. An export is completed only if every list it read was seen to its end. Any other end (a malformed, repeated, or missing page, cancel, or a lost tab) discards it.
  **Why:** #131's API takes these flags in the header at creation, and `complete` takes no body. Changing that contract would be Rule 4. Since nothing partial is ever completed (AC 6), a completed export's flags are true statements, and "a half-read library is never mistaken for a full one" holds without an API change.
  **Issue:** #134
- **Decision:** The export is created when its first part is ready. The workspace list read in leg 1 travels in the session until then. The library filters Suno sent with the first library page go in the header as `libraryFilters`.
  **Why:** The header carries `workspaces`, and parts cannot. The filters are known only once the library's first request is seen. #131's `ReadHeader` ignores unknown fields, so `libraryFilters` is accepted but not stored yet. #139 (the review says which kinds were excluded) must persist it, which is noted for #139.
  **Issue:** #134
- **Decision:** The page observer (`src/page/observe.ts`) is a third registered script: `n8tracks-observer`, in the MAIN world at `document_start`, built as `dist/observe.js`. It wraps only `fetch`, which matches TS-001; XHR is not wrapped. It forwards only the five TS-003 list responses, on suno.com hosts over HTTPS, matched by exact path and method. Request bodies are reduced to `cursor`, `limit`, `filters`, `feed_id`, and `page_size`, plus the query's `page` and `cursor`. `token`, `create_session_token`, and `user_tier` are stripped at any depth. It keeps the last 20 responses and replays them when the content script, which starts at `document_idle`, posts `observer-ready`. Both sides check `event.source` and `event.origin`.
  **Why:** The plan's risk note for #134 asks for fixture-replay tests that prove the token never leaves (invariant 6). The replay closes the race between the page's first request and the content script starting. The path patterns live in `adapter/observed.ts`, because #133's static scan forbids any address literal in `observe.ts`.
  **Issue:** #134
- **Decision:** A new `load-more` workflow (feature `sync`) scrolls the list on screen, using the new primitive `Page.scrollToEnd()`. That primitive sets `scrollTop` on the page and on every region that scrolls its own content; it presses nothing and dispatches no event. The workflow expects the song `rowgroup` on the Library and Trash pages, the "N songs" list on a playlist page, and Suno's navigation on the workspace list page, which has no snapshot. It is registered with an entry in `RUN_RECIPES`, and its fixtures are library-list, library-trash, and playlist. `Role` gains `rowgroup` and `list`, and `ul`/`ol` map to `list`.
  **Why:** TS-003 says every list the reader reads loads more on scroll. The reader's own check of each response is what detects a page that does not look as expected, so the page checks stay minimal and invent no structure.
  **Issue:** #134
- **Decision:** `ADAPTER_VERSION` is raised from 1 to 2.
  **Why:** Its contract is "raised whenever a selector, address pattern, or workflow step for Suno changes". This story adds address patterns, a page kind, and a workflow.
  **Issue:** #134
- **Decision:** The content script now asks the service worker one question on every Suno page load (`sync-resume`) before the panel is opened. #132's test "asks nothing until it is opened" now expects only that question.
  **Why:** A sync must continue after its own navigation without the user re-opening anything. The question starts nothing. If the answer is "no sync", the panel stays closed and nothing is read. AC 7 is proven by "never syncs by itself".
  **Issue:** #134
- **Decision:** In the panel, the workspace and playlist choices list what Suno has already shown on this page. The observer passively sees `project/me` and `playlist/me` as the user browses. If nothing has been seen, the panel says where to open the list. Choosing does not read anything new.
  **Why:** The AC requires confirmation before anything starts, so the extension may not operate the page to fetch the lists before the user confirms. The extension token has no `catalog.read` for n8Tracks' copy.
  **Issue:** #134
- **Decision:** In the service worker, a part is sent again up to 3 times on a network error or a 5xx. Any 4xx stops the read at once, and a 401 `invalid_token` also forgets the token (`Connection.call`). A failed `complete`, such as 409 `import_in_progress`, discards the export. The review opens at `<address>/suno/imports/<id>` (#139's planned route), in an n8Tracks tab of the Suno tab's window if there is one, otherwise in a new tab there. The "replaces an export waiting for review" warning checks only the last export this extension created (`lastSunoExport` in local storage).
  **Why:** Sending a refused part again would not change the answer. An export that cannot complete must not be left half-offered. An extension token can read only its own exports.
  **Issue:** #134
- **Decision:** Not built here: the notice on n8Tracks' Suno page for a discarded export. That notice and the review page itself belong to #139. Images belong to #152.
  **Why:** The review page and the Suno entry point are #139's. The story moved its image criteria to #152.
  **Issue:** #134

Story #151 (built in parallel; merged into the milestone branch):

- **Decision:** Added an optional `workspace` parameter (a Suno workspace ID) to `GET /api/v1/songs` so a workspace's page can list its Songs. A blank, unknown, or repeated value is 400 `invalid_request`, like the other filters. It is additive: no schema change, no migration, and no existing parameter changes.
  **Why:** #151 AC 1 needs a workspace's Songs to be listed, and no endpoint could do that. Fetching every Song and filtering in the browser would not scale. `SongService` already takes `ISunoWorkspaceStore`.
  **Issue:** #151
- **Decision:** Two routes: `/settings/suno-workspaces` (the list) and `/settings/suno-workspaces/:id` (one workspace, by Suno ID, URL-encoded). There is one sidebar link, "Suno workspaces", placed after "Suno". `SUNO_WORKSPACES_PATH` and `workspacePath` are in `api/sunoWorkspaces.ts` because the react-refresh lint rule keeps component files to components only.
  **Why:** the must-have names `settings/SunoWorkspacesPage.tsx`. The conventions require new screens in the sidebar. The discretion links the page from Settings and from the badge.
  **Issue:** #151
- **Decision:** The Song's "Workspace unavailable" badge, in the header and in Details, is now a link to the workspace's page. Its accessible name starts with its visible text: "Workspace unavailable: <name>. Open it in Settings".
  **Why:** the discretion says "The page is linked from Settings and from a Song's workspace badge". The badge only shows for an Unavailable workspace, which is exactly when a move is needed.
  **Issue:** #151
- **Decision:** There are two ways to select. The header checkbox "Select all N Songs in this workspace" sends `{all: true}`, so it covers every page. Row checkboxes send `songIds`. The confirmation states the count frozen when it opened: the total for all, or the number ticked. The workspace's page shows 100 Songs at a time, which is the list's largest page.
  **Why:** AC 2 asks for "some or all … in one action". With `all`, a workspace of up to 5,000 Songs moves in one command, without paging through it.
  **Issue:** #151
- **Decision:** A refused move (`song_not_in_workspace`, or `validation_failed` on the target) keeps the dialog open with "Nothing moved: …", clears the selection, and reads the Songs and workspaces again. While a move reads the list again, the page keeps showing the last list it read.
  **Why:** the move is all or nothing, so the user must see that nothing changed. Reading the list again avoids stale state.
  **Issue:** #151
- **Decision:** The API test-plan items (all-or-nothing, an Unavailable or identical target, a Song not in the workspace, and 403 `session_required` for a bearer token) are covered by #129's existing `SunoWorkspaceEndpointTests`, which are reused unchanged. Only tests for the new list filter were added.
  **Why:** the orchestrator note for #129 says the bulk-move API and its tests already exist.
  **Issue:** #151

Story #135 (built in parallel; merged into the milestone branch):

- **Decision:** D5 applied. In `docs/suno-import-field-map.json` (and its `.md`), `simple_add_lyrics` and `simple_add_styles` get `notReturned` with the reason "Unverified in TS-003". An imported Version therefore lists `simpleLyricsAdded` and `simpleStylesAdded` as not returned and holds `false` for both.
  **Why:** Without this the coverage test fails as written: both entries had `feed: null` and only an `unverified` note. AC 2 allows "not returned when the map says so".
  **Issue:** #135
- **Decision:** The map is made machine-readable where the mapper needs it, as data rather than code:
  - Variety's value table is now numbers 0–4. `unverifiedValues` lists Off, Normal and Extra.
  - The model entry gains `feedFallbacks: [major_model_version, model_name]`.
  - Any other value an enumeration returns is kept raw and marked out of range.
  **Why:** The table's strings ("0 (unverified)") could not be decoded. TS-003 saw only 2 (High) and 4 (Max); 0, 1 and 3 follow the form's order. Without them a default Advanced clip (Normal = 1) would always be marked out of range.
  **Issue:** #135
- **Decision:** D6 is one migration, `20261007000000_AddImportedInputsAndModelReportedAs`. It adds two nullable columns in place (no table rebuild, no CHECK):
  - `suno_models.reported_as`. HasData seeds it on v6-mini only (`V6-MINI`, the one label TS-003 saw).
  - `versions.imported_inputs`, a JSON column for the import marks.
  Version retention is now **shape 2** (`VersionShape1To2`), and the `retained-shapes.json` baseline is updated.
  **Why:** The story's discretion says to add the column if #114 lacks it. The out-of-range mark is "a flag on the Version" (system metadata). The reported-as names of v6 and v6-wild were not verified, so they are left null; matching falls back to the model's name, ignoring case, which covers them.
  **Issue:** #135
- **Decision:** Model matching is `SunoModelRules.Match`. It compares exactly, ignoring case (NameKey), first against each model's reported-as name and then against its name. Retired models match. The reported name is the songrow badge when the clip has one, else `major_model_version`, else `model_name`. A clip with no model at all lists `model` as not returned.
  An unknown model is only proposed: `ClipModel(Reported, Matched: null)`, with the reported name in `inputs.model`. `ModelCatalogService.EnsureReportedAsync(reported)` (public, runs inside the caller's transaction) adds it once per commit for #140. The entry is Discovered, named and reported as Suno reported it, and raises the list revision.
  **Why:** This follows the discretion and the TS-003 replan line. The name fallback stops a commit from adding a duplicate of a model the user typed in by hand.
  **Issue:** #135
- **Decision:** Suno's title is read and stored on the Version, but takes no part in input comparisons (`ClipInputMapper.NotCompared`).
  **Why:** A user can rename a clip in Suno after creating it, which is a `changed` record (ChangedFields already reports `title`), not a `conflict`. Suno also writes a title of its own for a blank one (the TS-003 map note), so two clips of one Create request can differ there. Deviation from the literal AC 5 wording ("every returned option").
  **Issue:** #135
- **Decision:** `RecordClassifier` now computes `inputsDiffer` (#131's note). `ISunoClipLookup.LiveGenerationsAsync` returns each linked Generation's Version inputs (`LinkedClip.Version`, a `LinkedVersionInputs`). The clip is mapped against the model list (read only) and compared with `ClipInputMapper.Differs`. Options the clip does not return, and those the Version's own marks list as not returned, take no part.
  The existing #131 tests that expected `linked` for a library clip on a blank Version 1 now use an imported Version: test helper `ImportedVersions.AttachAsync`. Such a clip on a blank Version is now, correctly, `conflict`.
  **Why:** This is AC 5's key link ("conflict is decided by comparing mapped inputs with the linked Version's").
  **Issue:** #135
- **Decision:** Every clip maps as a Song until #136. A clip is Simple when `metadata.gpt_description_prompt` is present and `metadata.task` is `agentic_thinking`; otherwise it is Advanced. Lineage tasks are left to #137. Reference and file fields go to #137, and the workspace to #140; they are listed in `ClipInputMapper.ReadElsewhere`, and the coverage test asserts each one is a reference or file field.
  **Why:** This follows the story's discretion lines.
  **Issue:** #135
- **Decision:** The import marks are API-spelled keys: `lyrics`, `styles`, and the keys of `inputs`.
  - They live on `SongVersion.Imported` (`ImportedInputMarks`), which is classified as a SystemField in the invariant 1 guard, with `imported_inputs` as a SystemColumn.
  - `GenerationMoveService` carries them to the copied Version.
  - The Version answer has `imported: {notReturned, outOfRange, rawValues} | null`.
  - The web shows `ImportedNotice` ("Imported from Suno") above the options.
  - The model list API does not expose `reportedAs`.
  **Why:** The out-of-range mark is a flag on the Version, shown in the read-only editor (discretion). The not-returned list shares the same notice, so the defaults are not read as what produced the Generation. Keeping `reportedAs` out of the model list API avoids changing an API contract that no AC asks for.
  **Issue:** #135
- **Decision:** `docs/suno-import-field-map.json` is embedded in the Application assembly as `n8Tracks.Application.Suno.Import.suno-import-field-map.json`. Its line comes out of `.dockerignore`, and the Dockerfile copies it next to the inventory.
  **Why:** This follows the discretion ("linked into the Application project as an embedded resource like the inventory"). The image build would fail without the file in the build context.
  **Issue:** #135

Story #150 (built in parallel; merged into the milestone branch):

- **Decision:** The diagnostic report is saved through a Blob link, and the manifest is unchanged: no `downloads` permission. The panel offers an `<a download>` with a Blob address that the user's own click saves. The options page clicks a Blob link inside the user's click.
  **Why:** The story's key link says "a Blob link the page itself saves; no `downloads` permission is added in this milestone". The #128 note ("#150 adds `downloads` to `allowedPermissions`") contradicts it, and the story's text wins. `downloads` stays for #216, which the #133 scan already anticipates, so D4's allow-list and its "fails on any extra" complement are untouched.
  **Issue:** #150
- **Decision:** The panel's link is never clicked by code. The content script prepares the report on every panel refresh and hands the panel a fresh Blob address, revoking the old one.
  **Why:** The page-context lint rule and the invariant 4 static scan forbid `.click()` and `dispatchEvent` outside `primitives.ts`. A link the user clicks needs neither, and adds no exemption to either guard.
  **Issue:** #150
- **Decision:** The log lives in the service worker, as a `Diagnostics` class in `extension/src/diagnostics/report.ts`, over `chrome.storage.session`. The Suno content script reports to it with two additive messages: `diagnostics-record` (a run that ended, and the self-check states) and `diagnostic-report`. Both are allowed from content scripts. `route` takes an optional fifth argument, `diagnostics`, and `disconnect` clears the log after unpairing.
  **Why:** Content scripts cannot reach `chrome.storage.session` at its default access level, and the report needs the connection's versions, which only the service worker holds. An optional argument keeps the existing router callers and tests unchanged.
  **Issue:** #150
- **Decision:** Redaction is by construction, in three layers.
  1. The service worker accepts only workflow IDs in the registry and step names those workflows declare. Phases and outcomes come from fixed lists.
  2. `expected` is redacted. A double-quoted value becomes `"…"`, and addresses, `@` handles, UUIDs, hex and digit runs, and secret-shaped words (an underscore, more than 24 characters, or letters mixed with digits) are removed.
  3. A source test (`extension/test/diagnostics-source.test.ts`) fails any workflow module whose `id`, `title`, `name`, `step`, `description`, `expected(...)` or `new PrimitiveError(...)` text is not written in the source. A template may hold only another `.description` or a quoted value.
  **Why:** The planner wanted `expected` to be "a string-literal union type". That would retype `Check`, `Target.description` and every workflow while #134 is changing the same code in parallel. The source test enforces the same rule ("compile-time constants in the adapter") without changing any type. A careless quoted value and every primitive's own interpolation (`to offer "<option>"`) are then caught by the quote rule.
  **Issue:** #150
- **Decision:** The page-structure capture is `Page.structureAround()` in `primitives.ts`, the only DOM-reading file. The page remembers the last target a run looked for, in a trail shared with its `withSignal` handles. The capture is anchored on that element if it is present once, else on its `within` container, else on `main`/`role=main`, else on `body`. The capture holds:
  - the anchor's subtree, breadth-first to depth 6 below the anchor and at most 300 nodes, with a `truncated` flag;
  - ancestors (up to 6) and siblings (up to 50), by tag only.
  `AdapterSession` captures when a run stops, and reports every run to an optional `onRun` listener.
  **Why:** The story says "depth of six around the failing element" and "siblings by tag name only". This is the simplest reading that stays within the 300-node cap.
  **Issue:** #150
- **Decision:** A capture keeps:
  - `role` only from the ARIA role list;
  - `type` only from the input/button types;
  - `data-testid` only if it matches `^[a-z0-9_-]{1,40}$` and is not ID-shaped;
  - `aria-*` attribute names only, sorted.
  The service worker rebuilds every node through the same rules before storing it.
  **Why:** Allow-lists rather than patterns keep a free-text `role` or `type` (for example, a user handle) out. The rebuild means a forged capture from page context cannot add text.
  **Issue:** #150
- **Decision:** The report's fields:
  - `browser` is "<brand> <major>" from `navigator.userAgentData`, skipping "Not A Brand" entries and preferring a named brand over Chromium. Otherwise it is `unknown`.
  - `versions.application` is kept only if it parses as a version, so it can be null even when connected.
  - `connection` holds only `status` and the address's `scheme`. There is no credential name and no scopes.
  - Step entries add `phase` to the planned `{workflow, step, outcome, ms, expected?}`. Outcomes are `ok`, `failed`, `timed_out`, `error` and `refused`. `ms` is the time since the previous entry of the same run.
  **Why:** These are low-cost choices the story leaves open. `phase` is an enumerated value and tells a maintainer whether the check before or after the action failed.
  **Issue:** #150
- **Decision:** A failed self-check, as opposed to a stopped run, records its state but not a page-structure capture.
  **Why:** The story's capture is "around the most recent failure" of a run's step. The self-check reads every workflow in turn, so its last target is ambiguous. Runs already capture.
  **Issue:** #150

Merge of #134 and #150:

- **Decision:** `route` keeps `diagnostics` as its optional fifth argument and takes #134's `sync` as a sixth (`route(connection, message, sender, id, diagnostics?, sync?)`). `CONTENT_SCRIPT_TYPES` is `state`, `relay`, the two diagnostics types, and `SYNC_TYPES`. The sync's runs go through `session.run`, so they reach the step log, and the load-more workflow is in the report like every other workflow.
  **Why:** Both stories added a fifth argument in parallel. #150's notes asked later stories to keep the fifth for `diagnostics`; only #134's three router-test calls and the service worker needed the change.
  **Issue:** #134, #150

#152 (cover images with a sync):

- **Decision:** The service worker reads and sends the images, not the Suno tab. The read (`adapter/imageReader.ts`) is a CORS GET with `credentials: 'omit'`, no headers, `referrerPolicy: 'no-referrer'` and `redirect: 'error'`, made to `https://cdn2.suno.ai` only. The host list (`SUNO_IMAGE_HOSTS`, `isSunoImageAddress`) is in `adapter/addresses.ts`, because the scan forbids a Suno address in a file that sends requests. Neither the manifest nor a permission changes.
  **Why:** The key link sends images "through the service worker's apiClient", and bytes cannot cross runtime messaging as a Blob. TS-003 found `Access-Control-Allow-Origin: *`, so the extension origin can read the image without a host permission. A redirect could leave the listed hosts, so redirects are refused.
  **Issue:** #152
- **Decision:** A clip's cover is `image_large_url` when it is on a listed host, otherwise `image_url`. Covers are collected from every part the sync uploads, library and Trash alike, de-duplicated by Suno ID, and kept in `chrome.storage.local` (`sunoImages`) with the counts. The sender resumes when the service worker starts.
  **Why:** The large image suits artwork, and the small one is the fallback. Local storage outlives a stopped service worker, so a long send carries on.
  **Issue:** #152
- **Decision:** Images wait until the export is `ready`: `GET` every 2 s, for at most 15 min. They are then sent four at a time. Each outcome is counted as `sent`, `failed` (could not be read, or refused with a 4xx/5xx; never retried, per #134), or `ignored` (no such record, a record that became no Generation, or a Generation that already has an image). A discarded, expired or failed export, or a 401, stops the send, and the images not yet sent count as failed.
  **Why:** The story leaves the wait and the counting open. #134 decided that failures are not retried. A Generation that already has an image is "refused (and ignored)" in the story, so it is not shown as a failure.
  **Issue:** #152
- **Decision:** Rule 2: the late-image path needs the record's Generation, and a `suno.sync` credential had no way to find it: `GET .../records` is session-only, and Generation references take no Suno ID. For a **committed** export, `PUT /api/v1/suno/exports/{id}/artwork/{sunoId}` now answers its 409 `export_not_ready` with `generationId`, the live Generation that has the record's Suno ID, when there is one. `ExportStagingService` takes `ISunoClipLookup`, and `NotReady` has an optional `GenerationId`. The extension then calls `PUT /api/v1/generations/{generationId}/artwork`, whose `artwork_exists` refusal for `suno.*` tokens (#121) keeps an existing image. During `committing` the image waits and is sent again.
  **Why:** This is the smallest additive change that lets the key link (late images go to the Generations endpoint) work. It adds a member to an existing problem and changes no status, route or schema, and it needs no migration. The import still never writes `artwork_asset_id` itself.
  **Issue:** #152
- **Decision:** AC 5 is a constant, `IMAGES_READABLE = true` (TS-003), and the sender takes a `readable` option. When it is false, nothing is read or sent and the panel says that images are not brought along.
  **Why:** TS-003 found that images can be read this way, so the skip path exists only for a future spike result. It is tested through the option.
  **Issue:** #152
- **Decision:** The panel follows the images with a new tab-bound message, `sync-images` (in `SYNC_TYPES`, answered only to the sync's own tab), polled every second after a finished sync, for at most 30 min. Only the finished state's `.sync-images` status line is rewritten, so focus stays put. `ADAPTER_VERSION` goes from 2 to 3 because an address pattern was added.
  **Why:** The router signature stays the same, with no seventh argument. `addresses.ts` says that a new pattern raises the adapter version.
  **Issue:** #152

Story #136 (built in parallel; merged into the milestone branch):

- **Decision:** The kind markers are data. `kindMarkers` in `docs/suno-import-field-map.json` now holds `{path, equals}` for Speech (`metadata.is_speech` = true) and Sound (`metadata.task` = "sound"). `ImportFieldMap.KindMarkers` reads them, and `ClipInputMapper.KindOf` applies them.
  **Why:** AC 1 says "by the marker the import field map names". Keeping the map as data, with no field code in the mapper, follows #135's approach.
  **Issue:** #136
- **Decision:** A clip's kind is undetermined in two cases:
  - Both markers match.
  - A marker holds a value of another JSON type than the map's, for example `is_speech: "yes"` or a numeric `task`.

  The clip then maps as a Song with `KindUnknown`, and its staged record gets `flags: ["unknown_kind"]` (`SunoExportRules.UnknownKindFlag`) at staging time. When a repeated record keeps a later copy, the flag follows that copy. A clip with no `metadata`, or with `is_speech: false`, is a determined Song.
  **Why:** The discretion fixes the flag name and the fallback. "Unrecognised marker" has to be decidable without listing every lineage task. Treating a missing marker as unknown would flag every minimal or older clip (and break #131's flag assertions). The flag name `unknown_kind` follows the discretion literally, though the other flags are camelCase.
  **Issue:** #136
- **Decision:** Map data, as #135 did for Variety:
  - `speech_variety` gets the numeric 0–4 table; only High (2) is verified.
  - `sound_type` becomes `enum` over the bool (`one_shot`: false, `loop`: true). Absent is the default, One-shot.
  - `sound_key` and `sound_scale` get a `pattern`, a regex with one capturing group, over the shared `user_key`. Key `^([A-G]#?)m?$` gives the note. Scale `^[A-G]#?(m?)$` gives `m` for minor or nothing for major.
  - A flat or other unmatched key is kept raw and marked out of range for both. An absent key is Any, with the scale unset (null).
  **Why:** The inventory holds `sound_type` as a choice, and key and scale as two choices, while Suno returns a bool and one combined string. A generic `pattern` keeps the mapper free of per-field code. Only `Am` and Loop were verified in TS-003.
  **Issue:** #136
- **Decision:** Only the fields of the clip's own tab are read. The Songs `title` and `model` are therefore not read for a Speech or a Sound: they keep their defaults, and `MappedClipInputs.Model` is null, so a commit adds no model. Compared keys are `kind`, the kind's mode (`songMode` or `speechMode`; none for a Sound), and the tab's options.
  **Why:** This follows the discretion: "Options belonging to another tab are ignored (they remain in the raw JSON)". In the inventory, `title` is a Songs-tab field. A kind mismatch with the linked Version makes the record a `conflict`.
  **Issue:** #136
- **Decision:** Speech mode:
  - Simple by #135's marker.
  - Advanced when `gpt_description_prompt` is absent.
  - With the prompt but another task (no mode marker), Advanced when the script (`speech_script`'s feed path) is not blank after trimming, else Simple.

  Speech Simple's Variety, which Suno does not send and which is not on the Simple form, stays at the default and is not marked not returned. Songs Simple treats absent sliders the same way.
  **Why:** This follows the discretion ("Advanced when it has a script", "a whitespace-only script counts as absent") and keeps parity with #135.
  **Issue:** #136
- **Decision:** The coverage test now walks every tab. It asserts all 35 inventory keys: 28 in `ReadKeys`, plus the 7 in `ReadElsewhere`, checked against an explicit owner list in the test (6 for #137, 1 for #140). The new bite test adds a made-up `sound_swing` field to a copy of the inventory. Redaction needed no change: #113 already lists `speechprompt`, `speechscript`, `speechtone`, and `sounddescription`. There is no migration and no web change, because the review page belongs to #139.
  **Why:** This follows AC 5 and the discretion's owner-list line. The attention mark is a staged-record flag, not a Version field.
  **Issue:** #136

Story #137 (built in parallel; merged into the milestone branch):

- **Decision:** Rule 3: migration `20261007030000_AllowResolvedExternalSources` replaces `tr_version_sources_frozen_update`. On a frozen Version it now allows two pointer rewrites: the existing Generation → external reference (#122), and the reverse, external reference → Generation. Each is allowed only when the Suno ID on both sides is the same and every other column is unchanged. No table is rebuilt.
  **Why:** Imported Versions are frozen as soon as their Generation is attached, so without this the resolver could not point a "Not imported" source at its parent's Generation. The source's identity (its Suno ID) is unchanged, which keeps invariant 1. The guard asserts the new clause, refuses the rewrite to a Generation with another Suno ID, and checks that `Stored()` is byte-identical before and after resolution.
  **Issue:** #137
- **Decision:** Lineage takes part in the same-inputs comparison through a new trailing `MappedClipInputs.Lineage` (`ImportedLineage`) that `SameInputs` compares by `LineageReader.ComparisonKeyOf`. It is not a key of `Compared`, and `Differs` (clip vs Version, the classifier's conflict) ignores lineage.
  **Why:** Tests assert the exact `Compared` key set, and a lineage key there would make `Differs` read it from `VersionInputs` and call every linked clip with lineage a conflict. The issue's discretion gives "lineage differs from its Version's" to the diff story.
  **Issue:** #137
- **Decision:** `metadata.task` decides only the audio action. Inspiration (`playlist_id`/`playlist_clip_ids`) and the Voice (`persona_id` + `persona.name`) are read from their own fields whatever the task. `playlist_condition`, `vox_playlist_condition` and `agentic_thinking` are recognised tasks that have no audio action. An unknown task gets Remix sources from `clip_roots`, and a missing task (Reuse Prompt) gets none.
  **Why:** TS-002 saw Voice combined with Inspiration under one task. Reading the fields directly keeps any combination Suno reports.
  **Issue:** #137
- **Decision:** Secondary IDs are keyed by Suno's field name with its index (`edited_clip_id`, `history[i]`, `clip_roots[i]`, `mashup_clip_ids[i]`). Each ID appears once, up to the 10 the rules allow. A `clip_roots` ID that is already a direct source in the group is not repeated. Extend's direct source is `edited_clip_id`, else the last `history` entry, else the first `clip_roots` entry. Its position comes from the matching `history` entry, else `metadata.continue_at`, rounded to hundredths. A Cover or Mashup clip with no source ID falls back to Remix sources from `clip_roots`.
  **Why:** The issue gives the fields but not the encoding. These choices keep every identifier, in order, and the result passes `VersionLineageRules` (Complete, Import) for every TS-002 example.
  **Issue:** #137
- **Decision:** An uploaded or recorded audio file is marked by a new `fileInput` sub-entry on the `audio` entry of `docs/suno-import-field-map.json` (`notReturned`; parsed as `ImportFieldEntry.FileInput`). Image and video keep their existing `notReturned` entries. The reader turns any file input the map does locate into a note saying "Imported from Suno". With the shipped map, no file note is ever produced.
  **Why:** AC 7 asks for "not returned" in the map where Suno does not report the file. The `audio` key's own feed path is the lineage task, so it needed a separate marker.
  **Issue:** #137
- **Decision:** `ExternalReferenceResolver` (`Application.Suno`) has three methods: `LinkAsync(ImportedLineage)` at import, public `ResolveAsync(sunoId)` in its own transaction, and internal `ResolveWithinAsync(sunoId)` for #140's commit transaction. Resolving relates child Song → parent Song under the source's type (the sources-story rule). `LinkAsync` does not relate: #140's import write should relate linked sources as `VersionService.RelateSourcesAsync` does. The resolver takes no catalog type, so it is not in the guard's namespace list; a dedicated guard fact covers it. Filling `suno_playlists`/`suno_personas` and the review text are left to #153, as the issue's moved-criteria line says. The inspiration playlist's name is blank at read, because the clip carries none.
  **Why:** This keeps the reader pure and the resolver callable inside or outside a transaction.
  **Issue:** #137
- **Decision:** Rule 1: the replaced trigger wraps its "allowed rewrite" test in `COALESCE(..., 0)`, so a comparison SQLite cannot decide counts as a change and is refused. Example: a source pointed at a Generation ID that no longer exists, whose Suno ID subquery gives NULL. The guard has a regression case.
  **Why:** Without it, `NOT (… = NULL)` is NULL, the trigger's WHEN does not fire, and pointing a frozen source at a deleted Generation got through. This was caught by `VersionSourcesEndpointTests.ASourceWhoseGenerationIsDeletedKeepsItsSunoIdAndAFrozenVersionIsUnchanged`.
  **Issue:** #137

Story #138:

- **Decision:** Proposals are computed by `ProposalService.ProposeWithinAsync` (`Application.Suno.Import`) as the last step of `ExportStagingService.ClassifyAsync`, in its own still-classifying transaction before the move to `ready`. Every record gets `proposal_json` (`{choice, basis, group, freezesVersion}`), and `choice_json` starts as the proposed choice. Linked, changed and conflict records are proposed `skip` with basis `linked`; ignored and deleted records are proposed `skip` with basis `ignored` or `deleted`.
  **Why:** The discretion says "same job as classification". Writing the starting choice means #139 and #140 read one field, the choice, while the proposal stays as it was for display. Giving every record a proposal leaves no nulls for the review to special-case. #141 and #142 refine the linked, changed and conflict defaults.
  **Issue:** #138
- **Decision:** Grouping (`Domain/Suno/ClipGrouping`) puts every new, linked, changed and conflict record into groups, so a new clip can follow a group-mate that is already a Generation. The anchor is the earliest clip (by time, then `batch_index`, then Suno ID). Members are taken in time order while within 1 s of the anchor, skipping any repeated `batch_index`. A set that has no index 0, or has a single clip, leaves the anchor alone, and the remaining clips are grouped again. `group` is numbered only for groups of two or more.
  **Why:** This follows the TS-001 rule and the anchoring discretion. The complement cases (proximity without `batch_index`, or without index 0) never group.
  **Issue:** #138
- **Decision:** The clip-vs-Version comparison is `!ClipInputMapper.Differs(...)` plus an equal lineage comparison key. In the Version's key, each source Generation is counted by its Suno ID (`IVersionStore.FindSourceGenerationAsync`). A Song-target source never matches a clip. A source typed with a user relationship type mapped to the same action also does not match, because the key uses the type ID.
  **Why:** The discretion says sources are part of the inputs. `Differs` deliberately ignores lineage (#137), and the clip's key names Suno IDs.
  **Issue:** #138
- **Decision:** A new Version on an existing Song reuses the earlier proposal's temporary key when an earlier group of the same export has the same inputs. A new Song is still one per Create request (the user's rule). Proposed numbers are `NextTopLevel` over the Song's used numbers plus the numbers already proposed for it in this export.
  **Why:** Two Create requests with the same settings belong in one Version. The discretion allows several groups to target one new Version, and merging new Songs is left to the user.
  **Issue:** #138
- **Decision:** `PATCH /api/v1/suno/exports/{id}/records` takes `{sunoIds (1–1,000, de-duplicated), choice}`, which is the shape #139's discretion uses. It is SessionOnly and needs If-Match on the export revision. It works only on a ready export; otherwise it answers 409 `export_not_ready`. It answers 200 with the export at its new revision plus an ETag, and the GET of the export now sends an ETag too. Malformed bodies get 422 `validation_failed` with errors by field; refusals get 422 `invalid_choices` with `records: {sunoId: [reasons]}`. The `filter`+exclusions form ("select all that match") is left to #139.
  **Why:** One choice per request matches the review's bulk-apply interaction. The filter form needs #139's `q` search, which does not exist yet.
  **Issue:** #138
- **Decision:** These are the validation rules.
  - Import and ignore are allowed only for new, ignored and deleted records; other records answer `already_linked`. Skip is allowed for any record.
  - `version`: the clip's inputs must be that Version's, frozen or mutable.
  - New targets: every record naming a key must describe the same target (`target_conflict`) and hold clips with the same inputs (`inputs_differ`).
  - `newVersion` of a new Song: the Song is named by its key, must be top-level, and must exist in the resulting choices (`target_missing`). This is checked across the whole export, so a change that removes a new Song still named by another record's new Version is refused.
  - Numbers: with a parent, the number must be one of `VersionNumbering.Options(parent, used ∪ other keys' numbers)`. Top-level, it must be one part, at least `NextTopLevel(used)` (a new Song has used `1`), and not claimed by another key on that Song.
  - Catalog-dependent checks run only for the changed records.
  **Why:** The top-level rule is symmetric, so a skip elsewhere never invalidates another key's number. Revalidating unrelated records against a catalog that moved would block harmless changes. The commit (#140) revalidates everything.
  **Issue:** #138
- **Decision:** No migration: `proposal_json`, `choice_json` and `revision` exist since #131. The invariant 3 staging guard (`SunoExportStagingGuardTests`) now also changes choices, including to a catalog target, before the after-snapshot. The invariant 1 guard lists the PATCH as touching no Version. The session-only count is 52.
  **Why:** The test plan requires the invariant 3 guard to run over proposing and changing choices.
  **Issue:** #138
- **Decision:** The review's reads are new session-only endpoints in `SunoExportsEndpoints`: `GET /suno/exports/current` (`{waiting, last}`), `GET /suno/exports/{id}/summary`, and `GET /suno/exports/{id}/records/{sunoId}/targets?song=&parent=`. The records list gains `q` and, per record, `target` (the choice's Song and Version named) and `generation` (shortcode and Song shortcode, for the link). Reads live in a new `ImportReviewService` (`Application.Suno.Import`); `ProposalService` gains `ValidateAsync` and `TargetsAsync`. The session-only count is 55.
  **Why:** The planned `/summary` and `/targets` endpoints, plus a "current" read so the sidebar entry, the empty state, the discarded notice, and Settings can find the waiting export without a credential. Names in the records answer let the page group rows under a heading for each target without one request per row.
  **Issue:** #139
- **Decision:** The bulk PATCH's filter form is `{ filter: {class, workspace, playlist, q}, except: [≤1,000], choice }`. The server resolves it inside the change's transaction to the matching records whose class the review can change (new, ignored, deleted), never linked/changed/conflict ones, and refuses a filter matching none with 422 `validation_failed` on `filter`. A body cannot mix `sunoIds` and `filter`.
  **Why:** "Select all that match" must work across pages without IDs. A record a Generation holds has no import control, so a selection never includes it; otherwise one linked record in the filter would refuse the whole change with `already_linked`.
  **Issue:** #139
- **Decision:** The summary checks every stored choice again (target still exists, numbers still free, a new Song's key still created, keys agreeing, and a clip's inputs against an existing Version that may have been edited) and lists the reasons by Suno ID (at most 1,000, with the full count). It does not re-compare inputs among the clips of one new target, which were checked at the change and cannot change in staging. Counts: Songs = new-Song keys; Versions = new-Song keys + new-Version keys; Generations = imported records; `ignored` = Don't copy plus records already on the ignore list that are not imported; `skipped` = the rest; `nothingToDo` when nothing is imported or newly ignored.
  **Why:** The AC asks the page to report whether every choice is valid, and the catalog moves while an export waits up to seven days. #138 proposes Skip for an ignored record, and its ignore entry is kept, so it is shown and counted as Don't copy (the AC's "Ignored records default to Don't copy"). Mapping every new-target clip again on each summary would cost seconds for large exports.
  **Issue:** #139
- **Decision:** One migration (`AddExportLibraryFilters`, `20261007040000`) adds nullable `suno_exports.library_filters_json`. `ExportReader.ReadHeader` keeps the header's `libraryFilters` without its `user`, `workspace`, and `trashed` members (anything but an object or null is 422 `invalid_export`; a kept object over 4,000 characters is dropped). The export answer has `libraryExcluded` (filter members that are `"False"` or `{presence: "False"}`), which the page words as disliked songs, stems, and clips made in Studio projects.
  **Why:** #134's binding note: #139 persists the filters and shows which kinds were excluded. `user` and `workspace` are identifiers, not kinds of clip, and the owner's Suno user ID must not be stored where it is not needed; Trash is read as its own list. A column is the smallest change, and the note names it.
  **Issue:** #139
- **Decision:** Web: the main sidebar gains "Suno import" (after Playlists) at `/suno/imports`, which redirects to `/suno/imports/<id>` when an export is waiting and otherwise explains how to start a sync and what became of the last one (discarded, failed, expired, or confirmed). The page is `web/src/suno/ImportReviewPage.tsx` with `ChoiceEditor.tsx`, `SunoImportsPage.tsx`, and `importReviewRules.ts`. Settings → Suno shows an "Import" section with a link. The Confirm button is shown disabled, its description saying why (nothing to do, invalid choices, or the commit arriving later). Selection is IDs (ticks) or a filter (all that match, a workspace, a playlist) with unticked exceptions. The choice editor asks the server's `/targets` for the first selected record on the page.
  **Why:** "Suno" alone is taken by Settings → Suno, and exact link names in existing tests rely on it. The AC moves Confirm itself to #140, while the test plan asks for a disabled Confirm that says when there is nothing to do.
  **Issue:** #139
- **Decision:** Rule 1: `e2e/tests/account.spec.ts` lists the sidebar with "Suno workspaces" (missing since #151) and the new "Suno import"; `e2e/tests/suno-models.spec.ts` opens Settings → Suno with an exact link name, as "Suno workspaces" and now "Suno import" also contain "Suno".
  **Why:** Both specs would fail in the milestone's full e2e run. Playwright matches link names by substring unless `exact` is set.
  **Issue:** #139
- **Decision:** The e2e Demo uses a record deleted in n8Tracks in place of the ignored one.
  **Why:** Nothing writes the ignore list before #143, and the e2e containers have no SQL access. Ignored rows are covered by component tests and the API tests (`SunoExportApi.Ignore`).
  **Issue:** #139
- **Decision:** Rule 1: the sidebar (`AppShell.Navbar`) scrolls on its own (`overflowY: auto`).
  **Why:** With "Suno import" added, the list is taller than a 720-pixel window and "System" could not be reached (e2e `account.spec.ts` could not click it: "element is outside of the viewport"). Regression test: that spec.
  **Issue:** #139
- **Decision:** The invariant 3 guard for the commit (`tests/n8Tracks.Api.Tests/Invariants/ImportNeverOverwritesGuardTests.cs`) reads every row of every table in #131's `SunoExportStagingGuardTests.CatalogTables`, keyed by primary key, before and after a commit. Each added, changed, or removed row must be one the confirmed choices name, checked against the job's result: new Songs, Versions, and Generations; their provider records, events, and used numbers; the Versions attached to; a reported model and the model list's revision (`settings` `suno.models`); the shortcode counter; and for a Reimport the restored Generation, its retention group, and its tombstone. Complements: an all-Skip commit and an uploaded-then-discarded export change nothing. A bite test makes the tombstone store retitle an existing Generation inside the commit. The guard was landed and run first; a temporary change making the commit raise every Song's revision failed it ("songs: changed …"), and it passed again once reverted.
  **Why:** The story's test plan and the m4-plan risk note ask for the guard first, whole rows of every catalog table, expectations computed from the choices, and proof that it bites. `provider_tombstones` was already in the catalog list (#130's note).
  **Issue:** #140
- **Decision:** The commit attaches through `GenerationService.AttachWithinAsync` (internal; `AttachAsync` now calls it inside its own transaction). Each target runs in a service scope of its own (`ImportTargetWriter`), so a rolled-back target leaves nothing tracked in the next one's `DbContext`. An unexpected database failure inside a target fails that target with `invalid_clip`; it does not fail the job.
  **Why:** `IExclusiveTransaction` does not nest, the story requires one transaction per target, and "one failing target does not undo the others". The reason list is fixed by the story, and Application has no logger to name anything else.
  **Issue:** #140
- **Decision:** The commit request (`POST /api/v1/suno/exports/{id}/commit`, session-only) takes If-Match on the export's revision (428 without; 409 `revision_conflict` when stale) and answers 202 with the export, now `committing` and naming the job in `jobId` (`Location` is the job). Invalid choices do not refuse the request: the job checks every choice again and fails those targets with their reason. The web keeps Confirm disabled while any choice is invalid.
  **Why:** Convention: every write sends the revision it read, so Confirm commits exactly the choices whose numbers the user was shown. The catalog can change between the request and the job, so the job's check is the one that counts; the story makes the UI the place that blocks invalid choices.
  **Issue:** #140
- **Decision:** A Reimport (a `deleted` record chosen Import) is first tried as a restore: the newest retention group holding a retained Generation whose stored `suno_id` is the clip's (new `IRetentionStore.FindByStoredValueAsync`, which uses `json_extract` in SQL so the document never leaves the database) is restored, in its own transaction, when its kind is `generation` (deleted alone) and its prune time has not passed. The Generation then goes back where it was, whatever target the choice named; the restore removes the tombstone, and references to the clip resolve to it again. A Generation deleted with its Version or Song, or past its 30 days, or whose restore is refused, is attached afresh to the chosen target with `Reimport: true`.
  **Why:** The user's decision is to restore while it is still retained and otherwise import fresh. Restoring a whole Version or Song group to bring back one clip would change rows no choice names (invariant 3).
  **Issue:** #140
- **Decision:** Order inside the job: Reimport restores, then new Songs (by key), new Versions of existing Songs, new Versions of new Songs, and existing Versions. Each target's clips are taken in `batch_index` order, then Suno creation time. Records a Generation now holds are reported `linked` with `already_linked`. A non-Reimport record whose Suno ID was tombstoned since is reported `skipped` with `tombstoned`. Neither fails its target. The new Version is built from the first clip's mapped inputs, and every other clip of a new target must have the same inputs (`inputs_differ` otherwise). An unknown model is added through `ModelCatalogService.EnsureReportedAsync` before the Version names it. The Mashup and Extend `IncompleteSources` attach refusal is reported as `invalid_clip`.
  **Why:** A new Version of a new Song needs its Song made first. The reason list is fixed by the story; `invalid_clip` is the closest code for a clip that cannot be kept as a Generation.
  **Issue:** #140
- **Decision:** Carry 2: a new Song from the commit is built with `Song.Create` and stored through `ISongStore.AddAsync` without `SongCreditService`. So it has no primary Artist, never the default one, and no personal defaults. Version 1 holds exactly the clip's mapped inputs, lineage, and import marks. The workspace goes on through `ISongWorkspaceStore.MoveAsync`, after the clips' unknown workspaces are recorded as an incomplete report (from the export's workspace list when it names them). Test: `ANewSongIsCreditedToNoArtistEvenWithADefaultArtist`.
  **Why:** m4-notes Carry 2 says import sends an explicit null primary Artist, and the story says personal defaults are not applied to imported Versions. `SongService.CreateAsync` would apply the defaults and validate inputs that imports keep out of range on purpose (#135).
  **Issue:** #140
- **Decision:** Group events: after all targets, one `GenerationEvent` per proposal group with two or more clips attached (across targets), `source` Inferred, `confidence` Medium, `batchSize` = the number attached, `occurredUtc` = the earliest Suno creation time among them. Staged artwork goes through a new internal `GenerationArtworkService.AttachStagedAsync` after the target commits, in its own transaction. It links the staged asset (already in the managed store) only when the Generation has no image, and reports `artwork_missing` as a note when the asset or its original is gone.
  **Why:** TS-001's grouping is an inference, not an observation. #121/#152 require artwork only through `GenerationArtworkService` and never replacing an image (invariant 3). The image was checked when it was staged, so nothing is re-uploaded.
  **Issue:** #140
- **Decision:** Interruption: the job puts the export back to `ready` and reclassifies it (`ExportStagingService.ReclassifyAsync`) when it throws or is cancelled. A new hosted service `ImportCommitRecovery`, registered before `JobWorker`, does the same at startup for an export left `committing` whose job is not queued. Reclassifying sets the proposal and choice of a record a Generation now holds to Skip (linked); every other choice stays as the user left it. Returning to `ready` restamps `ready_utc`, so the seven days start again.
  **Why:** Discretion: the job is not resumable, and committed records come back classified `linked` for the user to confirm again. Registering before the worker means a running job is seen as interrupted before the worker marks it failed or claims a queued one.
  **Issue:** #140
- **Decision:** The invariant 1 guard's `CatalogServiceNamespaces` gains `Application.Suno.Import` and `Application.Suno`. `ExternalReferenceResolver.LinkAsync` takes `ImportedLineage`, so adding Import alone made the "services elsewhere take no catalog type" complement fail. New exercisers: the commit endpoint and `ImportCommitService.CommitAsync` (a choice attaching a clip whose inputs are not the Version's, written on the staged record as if the Version changed after the choice: the target fails `inputs_differ` and the Version is byte-identical), `RecoverInterruptedAsync`, `ExternalReferenceResolver.ResolveAsync`, and `ModelCatalogService.UpdateAsync`/`DeleteAsync` on the target's model (refused `InUse`). Every other public method of the two namespaces is listed with why it takes no Version (staging, reads, pure).
  **Why:** The story's key link adds the import path to the guard's write-path list. Each namespace is enumerated by name (D1).
  **Issue:** #140
- **Decision:** Web: Confirm is enabled when every choice is valid and there is something to do. It opens "Confirm this import?", which shows the same three summary lines (`SummaryNumbers`), then posts the commit with the summary's revision. The page then shows `ImportCommitView`: progress follows the job through `useCommitJob` (the `useBackupJob` pattern, every 1 s, `COMMIT_POLL_MS`). The result shows created counts, outcome counts, links to the Songs, and a "Not imported as chosen" table. A confirmed export opened later reads the job's result again (finished jobs are kept 30 days, then only the state is shown). A job that fails sends the page back to the review. `e2e/tests/import-review.spec.ts` now expects Confirm enabled.
  **Why:** AC 13. The job's result is kept with the job (discretion). #57's hook is the backup page's, so the pattern was copied for the commit job's own result shape rather than coupled to backups.
  **Issue:** #140
- **Decision:** Rule 3: `ImportCommitApi.CommitAsync`, the test helper, accepted only `committing` in the 202 answer. A commit whose records are all Skip can finish before the export is read back, so the full Api run on 693598f failed `ACommitWithEveryRecordSetToSkipChangesNothing` once ("committed"). The helper now accepts `committing` or `committed`. The product behaviour is unchanged, and the job result and the final `committed` state are still asserted.
  **Why:** The answer is read after the job is queued, so either state is correct. The test raced the worker.
  **Issue:** #141
- **Decision:** The story's PATCH shape `{ choice: "apply", acceptFields }` is sent as #138's `choice.action`: `{ action: "apply", acceptFields: [...] }`, `{ action: "moveToNewVersion" }`, and `{ action: "keep" }`. `moveToNewVersion` and `keep` also take an optional `acceptFields`, the metadata diff shown beneath a conflict. `ImportAction` gains `Apply`, `MoveToNewVersion`, and `Keep`. `ImportChoice` gains a trailing `AcceptFields`. Refusals: `not_changed` (apply on a record that is not Changed), `not_conflict`, and `field_not_changed` (a field the record does not differ in). An unknown field name, or `acceptFields` on another action, is 422 `validation_failed`.
  **Why:** The records PATCH already nests the choice under `choice`. The discretion says a record can be both changed and in conflict, so a conflict's choice has to carry fields too.
  **Issue:** #141
- **Decision:** The diffed fields are #131's `SunoExportRules.ComparedFields`: title, tags, duration, model version and name, the three BPMs, key, and the image address without its query string. `providerStatus`, the remote state, and audio addresses are left out. The classifier now keeps a Conflict's changed fields as well (it used to keep a Changed record's only). `GET /api/v1/suno/exports/{id}/records/{sunoId}/diff` is SessionOnly (count now 57) and answers `{sunoId, class, generationId, fields:[{field,current,incoming}], inputs:[{field,current,incoming}]}`. The inputs come from the new `ClipInputMapper.DifferingInputs`; `Differs` now calls it. Any other class gets 422 `record_not_changed`.
  **Why:** These are the planner's fields, and the classifier already compares exactly these. Lyrics and styles in a conflict's diff are the user's own text, answered only on the session-only route and never logged (invariant 6).
  **Issue:** #141
- **Decision:** At commit, Changed and Conflict choices go to `CommitPlan.Resolutions`. They are applied after the targets, each in its own scope and transaction, by the internal `ChangeResolutionWriter` (in `ChangeResolutionService.cs`; the public `ChangeResolutionService` only reads diffs). It writes only accepted fields that still differ, through the new `IGenerationStore.RefreshClipFieldsAsync`, which touches no rating, state, revision, or artwork, and then touches the Song. It replaces `provider_records` only when a field was accepted or a move was made. A move creates the child Version through `ImportTargetWriter.CreateAsync` (now internal) with the first `Child` option of `VersionNumbering.Options`, then calls `GenerationMoveService.MoveWithinAsync`. The Generation keeps its Suno ID, rating, comments, artwork, event link, and selection. An accepted image address replaces the artwork with the staged image through the new internal `GenerationArtworkService.ReplaceWithStagedAsync`. New outcomes: `updated`, `declined`, `kept`, `moved`. A moved Version counts in `created.versions`. The summary gains `resolved`; a decided diff counts as something to do and is no longer counted as skipped.
  **Why:** m4-notes #123/#140 say to reuse the move service and the commit's handling. An accepted image is an explicit choice, so it may replace (invariant 3). Without the summary change, an import whose only choices were diffs could not be confirmed.
  **Issue:** #141
- **Decision:** Guards. Invariant 3: the scenario gains a Changed record (title accepted, tags declined, rated and commented) and a Conflict record (moved), both on the bystander Song. The column-precise rules allow `suno_title` only on the first, and `version_id`/`ordinal`/`revision` only on the second. They also allow that Generation's `provider_records` change, its `shortcode_aliases` row, and the child Version. The Version it left is not named, so any change to it is unexplained. The all-Skip complement now includes both classes. Invariant 1: the commit exercisers (API and service) also move a conflict off the frozen target Version, and the guard compares that Version afterwards. `ChangeResolutionService.DiffAsync` is listed as reads only.
  **Why:** The story's test plan says to extend both guards with this path. Keeping the changes inside the existing scenario keeps #142/#143's edits to `Unexplained` mergeable.
  **Issue:** #141
- **Decision:** Web: `web/src/suno/RecordDiff.tsx` is a dialog opened from "Review differences" on a Changed or Conflict row. It has a side-by-side table (Field, In n8Tracks, On Suno, Take Suno's), and each checkbox is labelled Take or Keep, so the diff reads without colour. It has "Take all from Suno" and "Keep all as they are", plus a summary line. A Conflict also gets an inputs table and a radio group (move, keep, decide later); Decide later is the default and saves Skip. A Changed record has a "Decide later" button. Saving PATCHes that one record by ID.
  **Why:** The AC and discretion: per-field or all at once, the conflict's two choices, Skip by default.
  **Issue:** #141
- **Decision:** BLOCKER, partial: AC 3 (a declined change is remembered) and AC 6 (a keep is remembered the same way) are not built. The planner's design (discretion) stores two SHA-256 hashes as columns on `generations`. That needs a migration and Generation retention shape 5. The orchestrator gave M4's migration slot to #142, which runs in parallel. Everything else is built: the diff, partial acceptance, the move, Skip by default, and the raw-JSON rule. A declined or kept record shows as Changed or Conflict again at the next sync until the follow-up lands.
  **Why:** No existing table fits without bending the design. `settings` is shared and not retained with the Generation, and `provider_records.payload` must stay Suno's raw clip byte for byte. A schema change is Rule 4.
  **Issue:** #141

Story #143 (built in parallel; merged into the milestone branch):

- **Decision:** No migration: #131's `suno_ignored_items` (`suno_id`, `title`, `workspace_id`, `ignored_utc`, `last_status`, `last_seen_utc`) is used as created. `last_status` holds `present | trashed | missing | not_seen` (null until a sync saw the clip); `last_seen_utc` is the export's capture time of the last confirmed sync that included it.
  **Why:** The shape planned for #143 was already in place; the parallel addendum forbids migrations.
  **Issue:** #143
- **Decision:** Status rules (`SunoIgnoreListRules.StatusAfter`): a confirmed sync including the clip sets `trashed` or `present`; a whole-library sync (scope `library` and `libraryComplete`) without it sets `missing` when `trashedComplete`, else `not_seen`; any other sync leaves it. "Missing" therefore matches #142's Remote Missing rule, and "not seen since" is the weaker whole-library case.
  **Why:** The story names four statuses with precedence "in Trash, present, missing, not seen" but defines only "not seen"; #142 defines missing as library and Trash both read to the end.
  **Issue:** #143
- **Decision:** Commit hooks: `CommitPlan.Of` collects the Don't copy records (`plan.Ignores`); after the targets the job adds each in its own transaction (`IgnoreListService.IgnoreAsync`, idempotent, reports `linked` / `skipped: tombstoned` when that changed since the review), then refreshes the list in one transaction. `ImportTargetWriter` calls `ForgetWithinAsync` after each attach, in the target's transaction (new last constructor parameter).
  **Why:** "Adding happens in the record's transaction"; removal belongs with the import. Kept additive for the parallel #141/#142 edits of the same code.
  **Issue:** #143
- **Decision:** Don't copy on a `deleted` record is refused with the new reason `tombstoned` (`ImportChoiceRules.Tombstoned`), at PATCH and in `ProposalService.ValidateAsync`; a change by filter to Don't copy passes over deleted records instead of refusing the whole change. The list also never shows a Suno ID with a tombstone.
  **Why:** Discretion "an ignore choice for a tombstoned ID is refused in validation"; refusing a whole "select all" for one deleted record would make the filter form unusable (the #131 staging guard does exactly that).
  **Issue:** #143
- **Decision:** Removal reclassifies every `ready` export with `ExportStagingService.ReclassifyAsync` (class becomes `new`, the stored choice — Skip — kept, revision unchanged).
  **Why:** Discretion "removing an entry while an export is ready reclassifies that export's record as New"; reusing #140's reclassify keeps choices intact.
  **Issue:** #143
- **Decision:** Deferred to #142: "importing an ignored clip that is in Suno's Trash imports it Archived". Today a trashed clip imports Active, as every trashed new clip does after #140.
  **Why:** Archiving by sync needs #142's `archivedBy: sync` (and its migration); archiving here through the user's path would mark it user-archived and break #142's restore rule. Flagged for the orchestrator to apply after #142 merges.
  **Issue:** #143
- **Decision:** API: `GET /api/v1/suno/ignored` (`q`, `workspace`, `status`, `page`; answer `{items, page, pageSize: 50, total, workspaces:[{id,name,count}]}`) and `POST /api/v1/suno/ignored/remove` (`{sunoIds}` 1–1,000 → `{removed, unknown}`; 422 `validation_failed` / `too_many_items`), both SessionOnly (session-only count 56 → 58). The workspace facet covers the whole list so the filter offers every workspace.
  **Why:** Story discretion (paged at 50, search title substring or Suno ID prefix, bulk removal up to 1,000, skipping unknown with counts, 403 `session_required` for a bearer).
  **Issue:** #143
- **Decision:** Web: `IgnoredItemsPage` at `/suno/ignored` with its own sidebar entry "Ignored Suno items" after "Suno import"; search applies on submit (Search button), filters at once, all in the URL; selection is per checked item across pages; removal confirmed in a modal.
  **Why:** "Linked from the Suno sidebar entry": `/suno/imports` redirects to a waiting review, so a link only on that page would often be unreachable.
  **Issue:** #143

Story #142 (built in parallel; merged into the milestone branch):

- **Decision:** #142 does not store Suno state changes ("remote-state rows"). `RemoteStateService` works them out from the export (records classed linked, changed, or conflict, plus the header's scope and completeness flags) and from the catalog every time they are listed, counted, or applied. Only the rows set to Skip are stored, as a JSON array in a new nullable `suno_exports.remote_skips_json`.
  **Why:** The story's discretion says rows are re-evaluated at Confirm and applied to each Generation as it then is. A Remote Missing row has no staged record to hang a choice on. A new staging table would be a Rule 4 schema addition the story does not name; one column on a staging table is enough.
  **Issue:** #142
- **Decision:** Migration `20261007050000_FollowSunoRemoteState` (the parallel migration slot) adds `generations.archived_by` (`user` | `sync`, null while active) and `suno_exports.remote_skips_json`. `archived_by` is added by a hand-written `ALTER ... CHECK`, because EF's `AddCheckConstraint` would rebuild `generations` and drop #123's triggers. Generation retention moves to **shape 5** (`GenerationShape4To5`: no archiver).
  **Why:** The story's discretion requires `archivedBy: user | sync`. An archived Generation with no archiver reads as the user's, so existing archives are never undone by a restore.
  **Issue:** #142
- **Decision:** The user's archive is set by the existing `PATCH /generations/{ref}` and only when `state` is sent. `archived` makes `archivedBy: user`, even on a Generation that sync archived. `active` clears it. An edit of the rating alone keeps the archiver. `IGenerationStore.TryUpdateAsync` takes the archiver, and Generation answers carry `archivedBy`.
  **Why:** This is the discretion line "A user archiving a Generation that sync archived makes it archivedBy: user" without breaking sync's archive on a rating change.
  **Issue:** #142
- **Decision:** Remote Missing leaves out Generations attached after the export's `capturedAt`, and Suno IDs on the ignore list (`suno_ignored_items`).
  **Why:** A library read taken before a clip was attached cannot say that clip is missing. This errs toward never marking a clip missing wrongly. Ignored clips get no rows (discretion). It also keeps the existing tests and the shared e2e containers safe, since their headers carry a fixed or earlier `capturedAt`.
  **Issue:** #142
- **Decision:** New session-only endpoints `GET` and `PATCH /api/v1/suno/exports/{id}/remote-states` live in their own file, `Api/Endpoints/SunoRemoteStateEndpoints.cs`. The PATCH takes `{sunoIds: 1–1000, apply}` with If-Match on the export revision and raises the revision. Its refusals are 422 `unknown_rows`, 422 `validation_failed`, 409 `export_not_ready`, 409 `revision_conflict`, and 428. The export summary gains `remoteChanges` and `remoteChangesTotal`, and `nothingToDo` is false while any change is applied. The commit result gains `remoteStates: [{sunoId, change, outcome: applied|skipped, generation}]`. **The session-only count is now 58.**
  **Why:** This keeps the shared `SunoExportsEndpoints` and `PATCH .../records` (which #141 and #143 also touch) to small, additive edits. Apply/Skip is independent of any diff choice (discretion).
  **Issue:** #142
- **Decision:** The commit job applies the remote-state rows after the targets and artwork, in one transaction (`RemoteStateService.ApplyAsync`). Each applied row raises the Generation's revision and touches neither its Song nor the selection nor retention. `CommitPlan.Of` and `ImportTargetWriter` are unchanged.
  **Why:** Rows are independent of record choices, and the brief asked for small edits there.
  **Issue:** #142
- **Decision:** In the invariant 3 guard, the scenario becomes a whole-library sync (`capturedAt` 2099) with the frozen Version's clip in the Trash (applied) and the bystander missing (set to Skip). An existing Generation may now change only for a row left to apply, and only in `remote_state`, `state`, `archived_by`, and `revision`. The everything-skipped complement also skips every row. A new bite test makes the store forget the skips and expects the bystander reported.
  **Why:** This is the test plan's "remote-state changes happen only for rows left selected at Confirm".
  **Issue:** #142
- **Decision:** The e2e `import-commit.spec.ts` export header now says `libraryComplete: false`. The new `suno-remote-state.spec.ts` sets to Skip, through the API, every Suno state change but its own two before walking the Demo.
  **Why:** On the shared containers, a complete whole-library sync marks every other test's clip Remote Missing. That would break import-commit's step 3 ("nothing to do") and touch other specs' data.
  **Issue:** #142
- **Decision:** On the web, the Class filter gains "Suno state changes (N)" (`?class=remote-state`), which lists the rows in `suno/RemoteStateChanges.tsx` with an "Apply: <title>" checkbox each. Generation rows and the panel gain an "In Suno Trash" badge (`in-suno-trash`).
  **Why:** The story's key_link names the class filter. The badge covers the truth "User sees in n8Tracks which outputs they have trashed in Suno"; Remote Missing already had one.
  **Issue:** #142
- **Decision:** The Generate on Suno routes:
  - `POST /api/v1/versions/{reference}/generation-requests` (`versions.write`) makes a request.
  - `GET /api/v1/versions/{reference}/generation-request` (`catalog.read`, no snapshot) is what the page polls.
  - `GET`, `POST .../claim`, and `PATCH /api/v1/suno/generation-requests/{id}` need `suno.generate`; the snapshot is answered only here.
  - `POST .../cancel` needs `versions.write`.

  A session that claims gets 403 `credential_required`.
  **Why:** The story fixes the PATCH path and the scopes, but not the create or read paths. Hanging creation off the Version reuses reference binding, the 404/deleted answers, and the reference guard. A session claim would bind no credential, so it is refused.
  **Issue:** #144
- **Decision:** `suno_generation_requests` (migration `20261007060000_AddSunoGenerationRequests`) names the Version and the claiming credential without foreign keys. It is listed as not catalog in the invariant 3 guard's `OtherTables`.
  **Why:** A cascade from `versions` would interfere with retention and restore. A request only needs to read as cancelled once its Version is gone, and the settle-on-read does that. The story's Discretion allows the table.
  **Issue:** #144
- **Decision:** Requests are settled lazily on every read (page poll, extension read, claim, report, cancel, create). Unclaimed after 15 s it becomes stopped. With no report for 1 h it becomes expired. When the Version's content key differs it becomes cancelled as stale; a Version that is gone also makes it cancelled. There is no background sweep.
  **Why:** State is only ever observed through reads, so a sweep adds nothing. The content key is a hash of kind, mode, entries, unsupported values, file inputs, and source targets. It leaves out the name, notes, workspace, and availability, so freezing at an observed Create (#149) and a rename do not cancel a request (Discretion: workspace and availability changes do not cancel). Comparing the revision instead would have cancelled on every freeze.
  **Issue:** #144
- **Decision:** `effectiveInputs` is now built in one place, `Application.Songs.VersionEffectiveInputs.Of`. The Version answer and the snapshot both use it.
  **Why:** The test plan says the snapshot equals `effectiveInputs`. One builder makes that true by construction rather than by two copies kept in step.
  **Issue:** #144
- **Decision:** The snapshot shape is the Discretion's, with these details:
  - The entries are keyed `<tab>.<mode>.<inventory field>`. `AdapterFieldMap.Entries` in code is checked against `docs/suno-adapter-field-map.md` by `AdapterFieldMapTests`, so the Docker image needs no Markdown.
  - In Simple mode, the added lyrics or styles are the value of `simple_add_lyrics`/`simple_add_styles` (null when not added).
  - The workspace is only the top-level `workspace`.
  - Each source has `{key, group, position, typeId, sunoAction, target{kind,id,sunoId}, title, shortcode, availability, continueAtSeconds, secondaryIds}`. The Suno ID comes from the Generation.
  - `unsupported` holds `{key, value}`.
  **Why:** The field map gives Simple mode no lyrics entry ("fills it from the Version's lyrics"). The extension (#146, #148) needs the clip's Suno ID to open a source.
  **Issue:** #144
- **Decision:** Every effective source with availability deleted, trashed, or missing blocks the request with 422 `sources_unavailable` (`sources[{group, position, title, shortcode, availability}]`, `lastSyncAt`). This covers Song targets too, and Generation sources deleted into an external "Deleted" reference. `lastSyncAt` is the newest committed export's `captured_utc`. Remix sources that are never sent do not block.
  **Why:** The Discretion says every source that points at a Generation is required. "Deleted" covers a missing Song target just as well, and the page needs a time for "as of the last confirmed sync".
  **Issue:** #144
- **Decision:** The web flow:
  - `web/src/extension/bridge.ts` pings with a 500 ms timeout and gives absent, disconnected (with status), incompatible, no-scope, or ready.
  - Nothing is made unless the answer is ready.
  - When the relay answered, a button "Open the extension's options" sends a new page message, `open-options`. The service worker opens the options page.
  - If the extension does not answer `generate-accepted`, the page cancels the request it just made and shows the extension's words.
  - The Generate button is disabled while a request is active; Cancel is beside the progress line.
  **Why:** A web page cannot link to a `chrome-extension://` options page, so Demo step 2's "links to its options" goes through the relay. When the relay is absent (the extension is not installed, or the page was loaded before Disconnect, which unregisters the relay), the page can only say so and explain. Cancelling a refused hand-off avoids a 15 s pending request that nobody will claim.
  **Issue:** #144
- **Decision:** On the extension side:
  - `messages.ts` gains `PageRequest` (`generate`, `open-options`), `GenerateFailure`, the widened `RelayReply`, and `GenerationHandOff`.
  - `background/generate.ts` (`GenerateCoordinator`) handles them. It takes a fresh handshake, checks that the sender's origin is the paired address, then compatibility, then `suno.generate`. It then claims through `Connection.call` and keeps only `{requestId, claimedAt}` in `chrome.storage.session` under `generation`.
  - `route(...)` gains a seventh, optional `generate` argument. Relay page messages reach it only from a tab that is not on suno.com.
  **Why:** This follows the one typed message union and the tab-bound pattern from the M4 notes. The snapshot holds lyrics and prompts, so it is never stored; the Suno steps (#145+) read the request again before each step, as the Discretion requires.
  **Issue:** #144

Story #141 follow-up, with #143's deferred Trash line (built in parallel; merged into the milestone branch):

- **Decision:** Migration `20261007070000_RememberDeclinedSunoChanges` adds the nullable TEXT columns `generations.declined_hash` and `generations.kept_inputs_hash` with EF `AddColumn`. That is a plain `ALTER TABLE ADD COLUMN` with no CHECK, so `generations` is not rebuilt and #123's triggers stay. Generation retention is now **shape 6**, and `GenerationShape5To6` restores an older record with neither hash.
  **Why:** #141's discretion places both hashes on the Generation. The orchestrator gave this follow-up the 070000 migration slot.
  **Issue:** #141
- **Decision:** The declined hash is SHA-256 (lower-case hex) over a canonical JSON object of Suno's incoming value of **every** compared field (`SunoExportRules.ComparedFields`, in order; the image address without its query; numbers as round-trip text). It does not cover only the fields that differed. The kept hash is SHA-256 over the clip's `MappedClipInputs.Compared`, keys in ordinal order, without lineage, as `ClipInputMapper.Differs` compares. Both are in `Domain/Suno/RememberedChoiceRules.cs`.
  **Why:** With every field covered, any later change in Suno, to a declined field or to another one, gives a new hash, which is what "until Suno's data changes again" asks. With a subset, a partly accepted change would also be remembered. An image signature (query string) alone never counts as a change, matching the comparison.
  **Issue:** #141
- **Decision:** The writer (`ChangeResolutionWriter`) sets `declined_hash` after every `apply`, `keep`, or `moveToNewVersion`: to the incoming hash when any shown field is left unaccepted, and to null when nothing is left. It sets `kept_inputs_hash` on `keep` while the inputs still differ, clears it on a move (or when they no longer differ), and leaves it alone on `apply`. Neither write raises the revision or touches a clip column, the raw clip, the rating, or comments. A hash is cleared only at a commit that writes the Generation. A linked record never writes anything at commit, so a stale hash stays until the next decision.
  **Why:** This follows the discretion's "cleared when the incoming data equals what is stored", limited to the confirmed-choice path, because invariant 3 forbids writes for records the user made no choice on. The only cost is in an edge case: Suno reverts to the stored values and later returns to exactly the declined values. That stays Already linked, which matches the user's decision.
  **Issue:** #141
- **Decision:** `RecordClassifier.ChangedFieldsOf(linked, incoming)` (internal) is the one rule for "changed fields shown": none while the incoming values hash to the remembered decline. Both the classifier and `ChangeResolutionService.DiffAsync` use it. So a Conflict brought back only by its inputs does not re-offer metadata the user already declined. The PATCH's `field_not_changed` check (stored changed fields) agrees with the diff.
  **Why:** Without a shared rule, the diff would list a field that the records PATCH then refuses to accept.
  **Issue:** #141
- **Decision:** #143's deferred line: an `ignored` record that came from Suno's Trash and is chosen Import is archived by sync right after its attach, in the target's transaction. It is written through #142's own `RemoteStateRules.Transition(..., RemoteSighting.Trashed)` and `IRemoteStateStore.TryApplyAsync` (`ImportTargetWriter.ArchiveAsTrashedAsync`; `ImportTargetWriter` gets a new last constructor parameter, `IRemoteStateStore`). The result is `state` archived, `archived_by` sync, `remote_state` trashed, revision 2. `CommitRecord` has a trailing `Trashed`. A trashed clip that was not ignored still imports Active, as after #140.
  **Why:** Reusing #142's transition keeps one archiving rule: a later restore in Suno reactivates the clip, because only what sync archived is undone. The attach path (`GenerationService.AttachWithinAsync`) stays unchanged. The discretion names only ignored clips.
  **Issue:** #143
- **Decision:** Invariant 3 guard: the scenario gains `declined-1` (Changed, every field declined), `kept-1` (Conflict kept, its retitle declined), and `ignored-trashed` (on the ignore list, in Suno's Trash, imported to `n8-1-v3`). Under the column-precise rules, a declined record may change only `declined_hash`, a kept one only `declined_hash` and `kept_inputs_hash`, and `changed-1` only `suno_title` and `declined_hash`. `provider_records` may change only for the accepted record and the moved one (the rule was "any resolved record" before). `suno_ignored_items` may also lose `ignored-trashed`. The created counts are now `(1, 3, 7)`. A new bite test installs a trigger that writes Suno's title when the declined hash is set, and expects that Generation to be unexplained.
  **Why:** This is the orchestrator's ask: a declined change must leave the clip columns untouched, and only the remembered hash may change. Both new behaviours are covered.
  **Issue:** #141, #143

Story #153 (built in parallel; merged into the milestone branch):

- **Decision:** A commit records into `suno_playlists`/`suno_personas` only what the clips it imports (created or restored) touch: each playlist the export lists (header and parts) that holds an imported clip, each imported clip's Inspiration playlist, and each imported clip's Voice. Skip, Don't copy, linked, and resolved records add nothing, so an all-Skip commit changes neither table.
  **Why:** AC 3 says "imported playlists and personas". Invariant 3's guard allows a row change only where a confirmed choice names it, so recording the export's whole playlist list would break "a commit with every record set to Skip changes nothing".
  **Issue:** #153
- **Decision:** Upsert rules, by Suno ID (`ISunoLibraryStore.RecordAsync`, internal `SunoLibraryService.RecordAsync` in its own transaction, called by `ImportCommitJob` after the events): a new row is added. A stored row takes a non-blank name, and for a listed playlist its clip IDs, with `last_seen_utc` set to the commit time. A blank name never replaces a known one. A playlist known only as a clip's Inspiration (not in the export's list) is added with a blank name and the clip's snapshot, and never overwrites a stored row.
  **Why:** A clip's playlist snapshot is the playlist as it was when the clip was made, and a clip without a `persona` object names no Voice. Neither should overwrite what a later or fuller sighting recorded.
  **Issue:** #153
- **Decision:** Ignoring a Not imported source is a new session-only `POST /api/v1/suno/ignored` `{sunoId}` → public `IgnoreListService.IgnoreReferenceAsync`. The #143 note suggested an internal method. It answers 200 `{sunoId, added}`, 404 when no external clip reference has that ID, 409 `already_imported` (a live Generation holds the ID), 409 `tombstoned`, and 422 `validation_failed`. The entry has the reference's title, no workspace, and status null. The reference and the sources are untouched. The invariant 1 guard lists the endpoint and the method as touching no Version. The session-only count is now 60 on this branch.
  **Why:** The Api assembly cannot call an internal Application method (there is no InternalsVisibleTo). Requiring an existing reference keeps the endpoint to its purpose, and refusing linked or deleted clips matches #143's commit-path rules.
  **Issue:** #153
- **Decision:** The review computes lineage on read. `ImportReviewService.RecordsAsync` runs `LineageReader` over each page record's raw clip and gives each source a place: `generation` (a live Generation, with shortcode and Suno title), `export` (a record of this export, with its class, choice, and proposal, read through the new `ISunoExportStore.RecordsNamedAsync`), or `not_imported`. An Inspiration playlist is named from the export's list, which is read only when a clip names one. The record answer has a trailing `lineage`, and `ImportGenerationView` has a trailing `title`.
  **Why:** There is no migration and no stored lineage per staged record. Reading on demand stays correct as choices change.
  **Issue:** #153
- **Decision:** The "Include" control in the Lineage column changes only the parent record's choice (PATCH `{sunoIds:[parent]}`). It sets the parent's proposal when the proposal is an import; otherwise it sets a new Song with the parent's title, in its workspace, under the summary's `nextKey`. The control appears only for a source that is a record of this export and is not chosen for import.
  **Why:** The proposal is n8Tracks' best target (for example, beside a group mate). A record proposed Don't copy (ignored) still needs a sensible import target.
  **Issue:** #153
- **Decision:** The wording of the Lineage column: "Cover of / Extension of (at m:ss) / Mashup of A + B / Sample of / Prompt reused from / <Type> of" for audio sources, "Inspired by …" for Inspiration songs, "Inspired by playlist <name> (n clips)", and "Voice: <name>". When all sources are in one place, the place is said once (": not in this sync", ": in this sync", ": in this sync, not chosen for import"). Otherwise it follows each title in parentheses. A Generation source gets a link instead.
  **Why:** This follows #137's Discretion ("Mashup of A + B", "Voice: X") and the Demo's "Cover of <title>: not in this sync".
  **Issue:** #153
- **Decision:** An imported Version's Inspiration playlist name is not filled from the export's list at commit, so the stored lineage keeps the reader's blank name. Only the review and the `suno_playlists` read model carry the name.
  **Why:** Filling it would change `ImportTargetWriter.CreateAsync` (also used by #141's move, and being edited in parallel by #142) for no AC. The Sources section shows the playlist ID when the name is blank.
  **Issue:** #153
- **Decision:** The invariant 3 extension is a separate guard test, `ThePlaylistsAndPersonasChangeOnlyWithTheClipsImported`. It covers an all-Skip/Don't-copy commit (no change) and a one-import commit (exactly its Voice and the listed playlist holding it are added, and pre-existing rows are untouched). The shared scenario and `Unexplained` rules are not edited.
  **Why:** The shared scenario has no lineage, so its rules need no new table, and #142 edits that scenario in parallel. A separate test avoids a merge conflict.
  **Issue:** #153

Story #145 (built in parallel; merged into the milestone branch):

- **Decision:** `PUT /api/v1/suno/workspaces/discovered` now takes `suno.sync` or `suno.generate` (`RequireAnyScope`).
  **Why:** The story's key link has Generate on Suno report Suno's complete workspace list, and that report is what makes n8Tracks mark a missing workspace Unavailable. A generate-only credential could not send it. The change only widens the endpoint: suno.sync callers are unaffected. The scope test now names both scopes.
  **Issue:** #145
- **Decision:** `resolvedWorkspace` on the progress PATCH sets the Song's workspace in the same transaction as the report, through `ISongWorkspaceStore.MoveAsync`, and only when the Song has none or an Unavailable one. Otherwise it answers 409 `workspace_already_set` with `workspaceId`. Resending the Song's own workspace is accepted and changes nothing (no revision bump). An unknown workspace is recorded as Available from a minimal raw project; an Unavailable one is refused with 422 `validation_failed` on `resolvedWorkspace`.
  **Why:** The story says the report is retried after a failure, never the creation, so a retry after a lost answer must not be refused. The workspace record needs a row before the Song's FK can point at it. A choice of an Unavailable workspace matches the Song PATCH rule. No migration.
  **Issue:** #145
- **Decision:** The snapshot's `workspace` carries `state` (`available` or `unavailable`). The content key is unchanged.
  **Why:** The story requires the request snapshot to carry the Song's workspace state. The workspace never made a request stale (#144), and it still does not.
  **Issue:** #145
- **Decision:** Not signed in is detected without a sign-in snapshot. The `open-workspaces` workflow's first step requires Suno's profile menu button (`data-testid="profile-menu-button"`, present on every signed-in TS-003 page snapshot). The tab also stops when it is not on /create after two loads (a sign-in redirect). The test stands in for the signed-out page by taking the button out of the selector snapshot.
  **Why:** The m4-plan risk for #145 says the not-signed-in page has no snapshot, so the story relies on fallback detection. Absence-based detection invents no page structure.
  **Issue:** #145
- **Decision:** The "Workspaces" breadcrumb that opens the list is found as `role=button` named exactly "Workspaces". When the list is already open (the "Search workspaces" box is there), nothing is pressed.
  **Why:** No TS-003 snapshot shows the closed state, only the open list. The role is unverified, so the owner's live Demo checks it. Matching the exact name never presses the workspace-name (rename) breadcrumb.
  **Issue:** #145
- **Decision:** Suno's rows carry no ID, so a workspace's row is found by `^<name>( |$)` on the row's accessible name. The name is the one Suno's complete list (`/api/project/me`) gives for the ID. If that list holds another workspace of the same name, the tab stops (`SAME_NAME`) without pressing. A prefix overlap makes `find` ambiguous, which also stops. Selection is verified by Suno's library pane requesting `/api/feed/v3` with `filters.workspace.workspaceId` equal to the ID; a workspace the pane already shows is not pressed.
  **Why:** This follows the Discretion note "the ID is matched against the IDs in Suno's workspace list response". A workspace with the same name and another ID is never selected automatically (test plan complement). The feed filter is the TS-003-documented signal of the selected workspace.
  **Issue:** #145
- **Decision:** A created workspace's name is the Song's title with whitespace collapsed and no cut ("Untitled" for a blank title). The new ID is read from the observed `POST /api/project` answer (a new observer kind, `workspace-created`). If Suno does not select the new workspace within 5 s, its row is pressed.
  **Why:** The create-row snapshot shows no `maxlength`, so there is no limit to cut to (Discretion: "cut … if the dialog shows one"). TS-003 does not say whether Suno selects a new workspace by itself.
  **Issue:** #145
- **Decision:** In the extension ESLint config, the click restriction now matches only a zero-argument `click()` (the DOM's) in the page-context folders, so a workflow can call the primitive `page.click(found)`. `test/dom-access-lint.test.ts` keeps the native-click samples failing and adds one that the primitive passes.
  **Why:** The old selector refused every `.click(...)` call, the click primitive included, so no workflow could press anything (#145 is the first that needs to). Type-aware enforcement stays in the invariant 4 source scan, which refuses DOM clicks by symbol. This is not a suppression; `check-suppressions.sh` and `check-canaries.sh` pass.
  **Issue:** #145
- **Decision:** This story ends the request in state `workspace` with step `workspace selected`; #146 continues from there. A closed generation tab reports `stopped` ("The Suno tab was closed."). The web shows `workspace` with step `choose workspace` as "Waiting for you in Suno: choose the Song's workspace in the extension's panel".
  **Why:** The filling steps are #146's. The Discretion note requires the Version page to show "Waiting for you in Suno" during the choice.
  **Issue:** #145
- **Decision:** `ADAPTER_VERSION` is 4. Four workflows are registered in `adapter/workflows/workspace.ts` (`open-workspaces`, `more-workspaces`, `select-workspace`, `create-workspace`), each with a guard recipe; `create-workspace` has `exceptions: ['create-workspace']`. The runtime guard test's timeout is raised to 60 s.
  **Why:** Selectors and an observed request changed (#133/#134 rule). The guard runs every workflow on every snapshot; with six workflows it takes about 8 s, past vitest's 5 s default. No check was weakened.
  **Issue:** #145

Story #146 (PARTIAL: D9):

- **Decision:** Simple mode's Add Lyrics and Add Styles sections, and Duration's Auto or Custom mode, are not set by the adapter. They are listed in `BLOCKED_ON_CAPTURE` in `extension/src/adapter/fill.ts`. The summary reports a Version value for them as `manual` ("add it from the + menu…" / "set Duration … by hand") and a section the Version does not add as `not_applicable`, with a note that the extension cannot see Simple's added sections. AC 1 and AC 8 stay unticked. #146 is labelled `blocked` + `needs-owner-action`, with a capture-session request.
  **Why:** This is binding decision D9: no TS-003 snapshot shows those page states, so no page structure is invented. Duration's mode is the same kind of gap, found while building: the More Options snapshot shows the slider and "0:30", but no control or reading that tells Auto from Custom. The coverage test names the three entries explicitly, and fails if one of them gains a filler without leaving the list.
  **Issue:** #146
- **Decision:** Two primitive additions in `adapter/primitives.ts`.
  - A `Region` scope (`{ around, levels, description }`): the nearest container, outwards from an anchor, that holds the target, never more than `levels` elements out.
  - A `TextAnchor` (`{ text, within? }`): the innermost visible element whose whole text is the label.
  - `Target.popup` (`aria-haspopup`) and `Reading.expanded` (`aria-expanded`).
  **Why:** More Options' Off/On and Male/Female buttons have no name or test attribute of their own, and the snapshots also keep the Speech form's Male/Female/Variety controls. A plain `find` is ambiguous, and an unbounded region found the Speech controls once a Songs control was removed (caught by the "unavailable" tests). Every level count was measured on the snapshots and is the smallest that finds the control.
  **Issue:** #146
- **Decision:** The lyrics editor is filled by a new `Page.typeText`: focus, select all, then `execCommand('insertText')` per line and `insertParagraph` between lines (`delete` for empty). `execCommand` is read with `Reflect.get`, and a browser without it (jsdom) gets a `PrimitiveError`. An editable region reads as lines (one per `<p>`, `<br>` a break). Tests stand in for Lexical (`src/testing/sunoForm.ts`).
  **Why:** #132's note: `set` refuses contenteditable. Lexical takes text only from the browser's own trusted input. A synthetic `beforeinput` is not inserted for a collapsed selection, and `execCommand` is the one way to make trusted input. It is deprecated in lib.dom, so it is read by name rather than with a lint suppression. The live Demo confirms it.
  **Issue:** #146
- **Decision:** The model is read from the menu button beside the mode tabs (its name is the label). Another model is chosen from the `role=menu` that the button's `aria-haspopup="menu"` declares. A menu that does not open, or does not offer the label, gives `unavailable`. No snapshot shows the menu open, so its read-back failure test uses a stand-in menu.
  **Why:** The Discretion line says "a model absent from Suno's dropdown is unavailable". The role comes from the page's own ARIA, not from invented structure. The menu's item names are unverified, and the owner's Demo checks them; the capture request lists the open menu.
  **Issue:** #146
- **Decision:** Both Song Title boxes are set, each found by its own place: around "Add audio" (4 levels) and around "Save to..." (2 levels).
  **Why:** TS-003 says the title is shared, and the snapshot shows both boxes visible. `find` never chooses between two matches. Setting each one shown means neither is chosen over the other.
  **Issue:** #146
- **Decision:** The workflows are `switch-form`, `fill-songs-simple`, `fill-songs-advanced`, and `check-songs-form` (`adapter/workflows/fillSongs.ts`), each with a `RUN_RECIPES` entry. The fill is one step, `fill`, with a 2-minute timeout, and it records each entry's outcome. The precondition steps are the Songs form, the Song description box, and the Lyrics, Styles, and More Options sections opened. `ADAPTER_VERSION` is 5.
  **Why:** A failed entry must not stop the run (Discretion), but the runner stops at a failed step. Only "the form as a whole" preconditions are steps. Selectors and workflows changed (#133/#134 rule).
  **Issue:** #146
- **Decision:** The read-back polls every 100 ms for up to 900 ms (`READ_BACK_MS × READ_BACK_TRIES`) through `Page.wait`, rather than three fixed 300 ms sleeps.
  **Why:** It is the same deadline as the Discretion's "300 ms, up to three times", and a value that shows sooner is taken sooner.
  **Issue:** #146
- **Decision:** A control is pressed or set only when it differs from the Version's value. Vocal Gender None presses the selected one (deselect), and is `failed` if it stays selected.
  **Why:** AC 3 requires every entry to be set, defaults included, and that is checked by reading every entry back. Pressing an already-selected toggle would deselect it.
  **Issue:** #146
- **Decision:** Source entries (audio, voice, inspiration, Simple playlist) are reported as `manual`, naming what to load by hand, until #148 loads sources. Without a source they are `not_applicable`. When the Version has a source, the Songs tab and mode are not switched.
  **Why:** The source story (#148) is not built. The Discretion says the summary lists the outcomes the source story reports. Until then, "to do by hand" is the honest outcome.
  **Issue:** #146
- **Decision:** The Suno tab now receives the snapshot's values (`GenerateJob.form`: kind, mode, entries, sources, file inputs, unsupported keys). #145's test "the job carries no values of the snapshot" was changed to assert the form instead.
  **Why:** The tab fills the form, so it needs the lyrics and settings. Nothing is logged or put in the diagnostic report, whose workflow text rule still holds.
  **Issue:** #146
- **Decision:** The PATCH takes an optional `verification` (adapterVersion ≥ 1, mode, checkedAt, at most 100 entries of `{ key, outcome, expected?, found?, note? }`). Each value is null, a number, a boolean, a string of at most 100 characters, or `{ length, sha256 }`. Any other member is refused (422 `validation_failed` on `verification`). It is validated in `Application.Suno.Generate.GenerationVerification`, stored normalised in the new nullable `suno_generation_requests.verification_json`, replaced by each later summary, kept by a report without one, and answered on both GETs. `verification` and `verificationjson` were added to the redaction names.
  **Why:** AC 7, with the key link "lyrics and prompts sent as lengths and hashes". Refusing unknown members keeps raw text out. The migration is `20261007080000_AddGenerationRequestVerification`: an in-place `ADD COLUMN`, with the Designer from the current snapshot (the diff is the one property).
  **Issue:** #146
- **Decision:** A Speech or Sound request stops at `open Songs form`, saying that filling those forms is not built yet. A request whose snapshot has no kind or mode ends the tab's part at "workspace selected", as before.
  **Why:** #147 owns Speech and Sounds. The second case keeps #145's behaviour for a malformed snapshot.
  **Issue:** #146
- **Decision:** The Voice picker is a recognised dialog (title "Voice"; Close, My Voices, and Favorites may be pressed). The Inspo picker is not added.
  **Why:** This follows #133's note. The Inspo dialog has an empty title, and recognising an untitled dialog would recognise every untitled one. #148, which presses in both pickers, decides how to recognise Inspo and adds what it presses.
  **Issue:** #146
- **Decision:** The Sounds Key and Key scale are not set by the adapter. They are in `BLOCKED_ON_CAPTURE` (D9) and reported `manual` with the Version's value as `expected`. Key scale is `not_applicable` when the Key is Any (or absent). AC 2, AC 3 and AC 4 are left unticked, and the issue is labelled `blocked` + `needs-owner-action` with a capture request.
  **Why:** TS-003's replan says the key is set through a popover of note buttons, Any, Major/Minor and Apply. No snapshot shows that popover: in `page.create-sounds-advanced-options.html` the Key button is `aria-expanded="true"`, but its `aria-controls` target is absent, and its label is redacted. Under D9, no page structure is invented.
  **Issue:** #147
- **Decision:** Type One-Shot, BPM empty (Auto) and Key Any are treated as captured page states. They appear in the hidden Sounds form inside the Speech snapshots, at its defaults. The Type and BPM fillers are built and tested on them.
  **Why:** TS-003 lists "Sound One-Shot, Auto BPM, Any key" as not exercised in a Create, but the page states themselves are in committed snapshots. D9 forbids only states that no snapshot shows. "major" appears in none, so it stays with the Key.
  **Issue:** #147
- **Decision:** Each kind gets its own workflows:
  - `switch-speech-form`, `fill-speech-simple`, `fill-speech-advanced` and `check-speech-form` in `workflows/fillSpeech.ts`.
  - `switch-sounds-form`, `fill-sounds` and `check-sounds-form` in `workflows/fillSounds.ts`.
  - All seven have `RUN_RECIPES`.
  - `FORM_WORKFLOWS` in `workflows/index.ts` is the registry keyed `<kind>.<mode>`: `song.simple`, `song.advanced`, `speech.simple`, `speech.advanced` and `sound.single`.
  - A Speech or Sound opens its tab at the reported step `choose form` (Songs keep `open Songs form`). An unknown key stops there with `NO_FORM`, which replaces `NOT_A_SONG`.
  **Why:** The Discretion says the registry key is `<kind>.<mode>` and that an unknown key and the wait for the form report "choose form". With the tab switch in its own workflow, a missing tab stops before any fill, naming its step (AC 5). The invariant-4 guard needs one recipe per workflow, so a generic switch over kinds could not be finished on each fixture.
  **Issue:** #147
- **Decision:** A Speech or Sound summary lists only its own tab's entries. The workspace entry `songs.simple.workspace` is listed for Songs only: `summaryEntries(mode, kind)`, `verifyForm` passing `job.kind`.
  **Why:** The Discretion says "A Simple Speech Version lists only its one entry", and Demo step 1 expects six Sounds entries. The workspace entry is a Songs key, and the workspace step still runs for every kind.
  **Issue:** #147
- **Decision:** The Sounds model is the menu button within 3 levels around the "Credits remaining" button: Sounds has no mode tabs to anchor on. It is chosen through the menu exactly as for Songs (`model(entry, button)`). The menu is still unverified (#146's capture item 5).
  **Why:** In the Sounds snapshot the model button ("v6-mini") sits beside the credits, and the inventory says "Same dropdown as Songs". Speech has no model button (inventory: "no model dropdown").
  **Issue:** #147
- **Decision:** These fillers follow the Songs story:
  - Speech Vocal Gender (male, female, or None) and Sounds Type (one_shot or loop) share a new `choice` builder. None presses the selected button to deselect it, as Songs' Vocal Gender does. A Type the form does not offer is `failed` with a note.
  - BPM types any whole number, so an out-of-range value is attempted and the read-back reports what Suno kept. Empty means Auto, which empties the box.
  - Speech Variety reuses the Songs step table (`wantedVariety`).
  **Why:** The Discretion says out-of-range imported values are attempted and reported `failed`, and that an empty BPM re-selects Auto. Speech's None-deselect behaviour is not captured. It is attempted and verified by read-back, so a page that will not deselect gives `failed`, not a false `set`.
  **Issue:** #147
- **Decision:** These changes go with the new kinds:
  - `ADAPTER_VERSION` is 6.
  - `FORM_GONE` now says "The form …".
  - `fillSongs.ts` exports `selected`, `expandedSection`, `pressUnless` and `fillStep` for reuse.
  - The panel's heading reads `formName(kind, mode)`, with an optional `form` on the `verification` view state.
  - The panel and the Version page name the Speech and Sounds fields. On the web, `modeLabel('single')` is "Sounds".
  - The stand-in `standInForSuno` handles `aria-pressed` groups.
  **Why:** Selectors and workflows changed (the #133/#134 rule). The rest are additive edits that keep #148's parallel changes to `fill.ts` and the guard mergeable.
  **Issue:** #147
- **Decision:** #149 goes ahead although its blocker #146 is PARTIAL. This is the orchestrator's decision.
  **Why:** #146 left three fill steps unfinished: Simple Add Lyrics and Add Styles, and the Duration mode. Recording the submitted request does not depend on them. A field the user sets by hand is observed like any other.
  **Issue:** #149
- **Decision:** The Create observation is a kind in `adapter/observed.ts`: `create`, for `POST /api/generate/v2-web`. There is no `adapter/workflows/observeCreate.ts`, although the story's artifacts list names one.
  - The observer forwards the response, and `submitted`: the values at `CREATE_REQUEST_PATHS`. Those are the import field map's `createRequest` paths plus `metadata.create_mode`, minus `user_uploaded_images_b64`. A parity test, `extension/test/observed-create-paths.test.ts`, keeps the list in step with the map.
  - The watch for further Creates lives in `content/sunoGenerate.ts`.
  **Why:** The binding #134 note puts the Create observation in `observed.ts` with a field allow-list. A Workflow has steps that act on the page, and observing presses nothing, so a workflow that clicks nothing would only weaken the registry and guard checks. An uploaded image's bytes are large, and n8Tracks keeps only a file-input note, so they are never sent.
  **Issue:** #149
- **Decision:** The observed Create is mapped by `ClipInputMapper.MapCreate`, and the field map gains two additive members.
  - **Where each option comes from:** the response (`paths.create`), else the request (`paths.createRequest`), else the requested Version, listed as assumed.
  - **New members:** `absentInCreateRequest: "default"` on vocal_gender, speech_vocal_gender and duration_mode, and `presentInCreateRequest: "custom"` on duration_mode.
  - **Absent keys:** without the new member, a key absent from the request says nothing.
  - **Never decoded from a Create:** the model, and any undecodable value, which is assumed and kept raw. For example, Male's `m` is not verified in the map.
  **Why:** The map's `values` for request-only options are descriptive text ("duration key present"), so they cannot be decoded as data. TS-003 shows that `mv` is the engine (chirp-goose), not the label. Never guessing means assuming from the Version rather than branching on a value n8Tracks cannot read. The members are additive, so import's feed reading is unchanged (`MapWith` takes a reader).
  **Issue:** #149
- **Decision:** These parts of the observed Create are decided here:
  - **Endpoint:** `POST /api/v1/suno/generation-requests/{id}/observed-create` (`suno.generate`, the claimer only) answers the request with a new `observed` list. Suno's request ID is stored in `generation_events` and `observed_json`, and never answered.
  - **The `/clips` completion route** from the planner's Discretion is not built. #154 owns completion.
  - **Migration `20261007100000_AddObservedCreates`** adds `suno_generation_requests.observed_json` (nullable). Its Designer was built from the current snapshot.
  - **Where the clips go:** to the requested Version when nothing differs, else to an earlier branch of the same request that holds the same inputs, else to a new child Version. The new Version takes the first free child number (`VersionNumberKind.Child`) and the note "Created from what was submitted to Suno". It becomes the Song's current Version.
  - **Sources:** the new Version's lineage is the requested Version's when the lineage key matches, else the clip's, resolved through `ExternalReferenceResolver.LinkAsync`.
  **Why:** The AC and the planner's Discretion ask for these. A column on a non-catalog table, like #146's `verification_json`, is the smallest store for "what happened" on the Version page. Reusing an earlier branch avoids one new Version per Create when the form stayed changed.
  **Issue:** #149
- **Decision:** These parts of how the request ends and how Creates are watched are decided here:
  - **The request** stays `waiting` (step `Create recorded`) between Creates. It becomes `done` when the tab leaves the Create page (the extension reports it), or 30 minutes after the last Create. The 30-minute rule is a settle rule in `GenerationRequestService.SettleAsync`, not a service-worker alarm.
  - **The tab's watch** polls the address every second and ends after 30 minutes (60 before the first Create).
  - **A failed send** is retried 3 times inside the service worker, and the panel then says the clips were not recorded. Failed reports are not kept in `chrome.storage.session`.
  - **After a recorded Create,** a load of the tab never fills the form again (`GenerateJob.created`).
  **Why:** The #144 note says to report `waiting` between Creates and `done` at the end. The server already settles on every read, so a server-side settle is simpler and survives a closed tab. AC 5's "marked done" is met when observation ends. n8Tracks answers a repeated Create once (by Suno's request ID), so the simpler retry is safe. A sync brings in anything still missing.
  **Issue:** #149
- **Decision:** `ADAPTER_VERSION` is 7 (the observer gained a pattern). The popup test now reads the constant.
  **Why:** By the #133/#134 rule, a change to an observed pattern raises the version. #148 also bumps it in parallel, so whichever story merges second takes 8.
  **Issue:** #149
- **Decision:** On the web, `VersionDetails` takes an optional `onRecorded`. When the request's `observed` count grows, it reloads the Version and calls `onRecorded`, and `SongVersions` then re-reads the Versions, the Song and the Generations. A branch makes the new Version current, so the e2e opens the requested Version by its number (`/v/1`), whose page lists what each Create came to.
  **Why:** Without this, the Generations would not appear "at once" until another read. A Version page opened at `/songs/<sc>` follows the Song's current Version, so it switches to the new branch. That Version carries the note.
  **Issue:** #149

Story #148 (built in parallel; merged into the milestone branch):

- **Decision:** Applied D10 to #148. Cover and Reuse Prompt are loaded from the clip's Remix menu and verified; the TS-003 snapshots back them (the menus, a loaded Cover in Advanced and Simple, and the Overwrite dialog). Extend, Mashup, Sample this song, a single Inspiration song, the Inspo playlist pick, and Voice selection are listed in the summary as to do by hand, with the source, playlist, or voice named. AC 1, 2, 4, 5 and 9 are left unticked, and the story is labelled `blocked` + `needs-owner-action` with the capture list.
  **Why:** No snapshot shows the form after those actions, a chosen voice or playlist, or an unopenable or trashed clip page. Inventing that structure would risk a false "verified".
  **Issue:** #148
- **Decision:** The source clip is opened by address at `/song/<clip id>`, the address shape of the Library's song links. Its menu button is found by the name "More options", and only when the page has exactly one; anything else stops at step 'clip menu' as "could not open source".
  **Why:** The clip page itself was not captured. Every captured Suno list names a clip's menu button "More options". Requiring exactly one, with verification of the thumbnail's clip ID afterwards, means a wrong guess can only stop the run; it never loads the wrong source. The owner's capture list includes the clip page.
  **Issue:** #148
- **Decision:** Added the outcome `verified` to the verification summary in four places: `GenerationVerification.Outcomes`, the web `VerificationOutcome` and labels, the extension's `EntryOutcome`, and its panel labels. No migration.
  **Why:** The story's source outcomes are verified / set / manual / unavailable / failed, and the summary must say a source was seen on the form. The change is additive, because the stored JSON is validated by a list.
  **Issue:** #148
- **Decision:** `switch-form` now always runs, before any source is loaded. This reverses #146's skip when the Version has sources.
  **Why:** TS-002 found that a source applies only in the mode active when its action was chosen, and the Discretion says to switch to the Version's mode first.
  **Issue:** #148
- **Decision:** The source phase is kept across page loads by the service worker, through a new tab-bound message `generate-source {source: {phase: opening|chosen, sunoId} | null}`. It resets the tab's load count. `GenerateJob.source` carries the phase to the next load.
  **Why:** Opening the clip's page is a page load, and choosing the action may load Create anew. The content script cannot survive either.
  **Issue:** #148
- **Decision:** A verification mismatch does not end the request. It stays `filling` at step `load source by hand`, with a message, and the panel shows a new `source` state with a Continue button. Continue verifies the source again, as often as needed.
  **Why:** AC 3 and the Discretion: the user may load the source by hand and continue, and a mismatch blocks again.
  **Issue:** #148
- **Decision:** In Simple mode the source is verified only by the chip thumbnail's clip ID, because Suno's chip does not name the action (`page.create-source-simple.html`).
  **Why:** The snapshot shows no action label on the chip. The action was the one the extension itself pressed.
  **Issue:** #148
- **Decision:** The Inspo dialog stays unrecognised and refused by the forbidden-control matcher.
  **Why:** Its snapshot shows no title. The orchestrator's rule is to recognise it only if a snapshot shows a title.
  **Issue:** #148
- **Decision:** The extension also checks n8Tracks' availability of every source (`trashed`, `missing`, `deleted`) before it changes the form, and stops naming each one, even though `POST .../generation-requests` already answers 422 `sources_unavailable`. It also refuses more than four Inspiration songs.
  **Why:** The Discretion says availability known to n8Tracks is checked for every source before starting. A snapshot read later could change, and five Inspiration songs are impossible by construction, which is asserted on the request.
  **Issue:** #148
- **Decision:** `ADAPTER_VERSION` is now 6. The five new workflows are appended at the end of `ADAPTER_WORKFLOWS`, and the source targets are in `adapter/sources.ts`.
  **Why:** New addresses (`/song/<id>`) and workflow steps mean a new adapter version. Appending keeps the merge with parallel #147 additive.
  **Issue:** #148
- **Decision (blocker):** #148 is PARTIAL and waits on an owner capture session. The page states needed are listed on the issue.
  **Why:** D10.
  **Issue:** #148
- **Decision:** On the merge of #148 with #147 and #149, the source skip is reconciled: the kind's `open` workflow (switch-form, switch-speech-form, or switch-sounds-form) always runs first, for every kind and whether or not the Version has sources. A source is loaded after it only when the kind loads sources. `FormWorkflows.sourcesDecideForm` is renamed `loadsSources` (Songs true; Speech and Sounds false), and `sunoGenerate.fill()` reads `workflows.loadsSources && plan.load !== null`. `fillRest` fills with the kind's `fill` workflow, with the loaded source's result. A new test in `sunoGenerate.test.ts` pins both halves: a Song with a Cover runs `switch-form` and then goes to the clip's page without filling, and a Speech Version with a source runs `switch-speech-form` and `fill-speech-advanced` in place. `ADAPTER_VERSION` is now **8**: #147 set 6, #149 set 7, and #148 set 6. Every test reads the constant.
  **Why:** TS-002 says a source applies only in the mode active when its action was chosen, so #148 needs the switch before the source. That reverses #146's skip, which #147 had narrowed to Songs. Speech and Sounds have no source step, so for them the switch always ran under #147 and still does. The rename says what the flag now decides. Each of the three stories changed the adapter, so the version takes the next number after the highest.
  **Issue:** #148, #147

Story #154:

- **Decision:** "Never been complete" is decided without a migration. A Generation is completed only when all of these hold:
  - it is listed among the Generations of this request's `observed_json`;
  - its stored status is not `complete` or `error`;
  - `declined_hash` and `kept_inputs_hash` are null;
  - its provider record has no `export_id`.
  `IGenerationStore.TryCompleteClipAsync` writes every clip column but the Suno ID in one conditional `UPDATE`. The service then replaces the provider record with the finished clip (`export_id` null). It refuses with 409 `already_complete` when the stored status is final, and with 409 `not_provisional` for a Generation that is not this request's (one made by import) or that an import review decided about.
  **Why:** n8Tracks writes a Generation's status only at attach and by this completion (`RefreshClipFieldsAsync` never writes it), so a non-final status means never complete in n8Tracks. A review that declined, kept, or applied Suno's data has decided about it, so completion must not override that (invariant 3, AC 1). A column such as `completed_utc` would need shape 7 and an upgrader for no extra guarantee.
  **Issue:** #154
- **Decision:** The route is `POST /api/v1/suno/generation-requests/{id}/clips` with one clip per call, `{ clip }`. It answers 200 `{ outcome: completed | failed, generation: { id, shortcode, sunoId, providerStatus } }` and is accepted after the request has ended (done or expired), from the claiming credential only. 404 means no request, or no live Generation holds the clip; 422 means the clip is not finished.
  **Why:** Clips finish independently (TS-001), and the test plan wants a second report refused with 409, which a batch answer could not say per clip. The user leaving the Create page ends the request (#149) while its clips are still generating.
  **Issue:** #154
- **Decision:** The cover goes through the existing `PUT /api/v1/generations/{id}/artwork` (#121), with the extension's `suno.generate` token. The service worker sends it after a completed report and reads the image with `adapter/imageReader.ts`, the only image request site. `artwork_exists` is left as it is.
  **Why:** This is the story's key link, and it is the only path that writes `artwork_asset_id` (the #121 note). Completion itself never writes artwork.
  **Issue:** #154
- **Decision:** The timers are split.
  - **The ten-minute end** is a service-worker `chrome.alarms` alarm (`n8tracks-completion`) over the watched clips in session storage. A clip whose ten minutes are up is dropped on every read as well. This adds `alarms` to the manifest allow-list (validator, `dist.test.ts`, `manifest.test.ts`, `docs/suno-integration.md`).
  - **The 15-second refresh prompts** are paced by the Suno tab's own clock. The tab also stops by itself after 11 minutes.

  **Why:** The Discretion says the timers are service-worker alarms, but Chrome's alarms fire at most every 30 seconds, so a 15-second prompt cannot be one. The prompt is a page action anyway, so it lives with the page. The alarm still bounds the watch when the service worker stops.
  **Issue:** #154
- **Decision:** The refresh prompt is a new read-only workflow, `refresh-library`, in `adapter/workflows/watchCompletion.ts`. It opens the workspace list by its breadcrumb if closed and presses the Song's workspace row, which makes the library pane ask for the workspace's songs. No library filter is toggled, so the TS-003 rule about restoring a toggled filter has nothing to restore. It runs only on the Create page and only when the workspace name is known; elsewhere the watch only listens. It is in `RUN_RECIPES`, and `ADAPTER_VERSION` is now 9.
  **Why:** No TS-003 snapshot shows the /create library pane's filters or page buttons, and inventing that structure is ruled out (D9/D10). The workspace row is in `page.workspace-selector.html`, and #145 relies on its press making the pane request the feed. Whether pressing the already-selected row requests the feed again is for the owner's Demo. If it does not, the clips arrive through the next sync, as the AC allows.
  **Issue:** #154
- **Decision:** The watch outlives the request and is bound to the tab. `generate-resume` answers `watching` (Suno IDs) even when the tab has no job. `generate-completion` is answered before the generation-tab check. Closing the tab ends that tab's watch.
  **Why:** After a recorded Create, leaving the Create page marks the request done (#149), but the clips are still generating. The completion watch reads the feed of whatever Suno page the tab shows.
  **Issue:** #154
- **Decision:** On the web, a Generation with status `error` shows a `Failed` badge (testid `generation-failed`), and deletion is the existing #124 path. A Generation still generating 10 minutes after `createdAt` shows "Still generating in Suno: sync to update" in its duration cell (`COMPLETION_WATCH_MS`, `isStillGenerating`, `isFailed` in `api/generations.ts`). The request's result on the Version page is unchanged.
  **Why:** AC 3 and AC 4 name the row. The time is the Generation's own, so no extension report is needed.
  **Issue:** #154
- **Decision:** `ProvisionalCompletionService.CompleteAsync` is in the invariant 1 guard with an API exerciser and a service exerciser. The invariant 3 guard (`ImportNeverOverwritesGuardTests`) gains `CompletionChangesOnlyAnObservedGenerationThatWasNeverComplete` and the bite test `TheGuardFailsWhenCompletionTouchesAGenerationThatWasEverComplete`. Completion does not touch the Song's updated time.
  **Why:** These are the test plan's guard and bite. A Song timestamp or revision change while the user edits the Song would surprise them, and the Generations list re-reads by itself.
  **Issue:** #154
- **Decision:** Filed #314, out of scope: a sync never updates a linked Generation's Suno status, so a clip still `submitted` after the watch stays Generating after its diff is applied.
  **Why:** #141's diff excludes `status` by design. Changing that is a reviewed-change rule for invariant 3, not this story's path.
  **Issue:** #154
- **Decision:** #314, Rule 1: a Generation's Suno status follows Suno automatically at the commit of a confirmed sync, whatever the record's choice (Skip included), and is not a reviewed diff field. It moves only from a status that is not final (`submitted`, `streaming`, or none) to Suno's final one (`complete` or `error`), by the same rule as #154's completion (`ProvisionalCompletionRules.MayComplete`). It writes that one column and nothing else: not the other clip columns (they still follow the Changed choice), the rating, state, archiver, comments, remembered hashes, raw clip, or revision. A final status never changes, and Suno's unfinished status is never written. The new step is `Application.Suno.Import.SunoStatusService` (`CountAsync`, `ApplyAsync`, both internal), run by the commit job after #141's resolutions and before #142's remote states, through `IGenerationStore.TryFinishStatusAsync` (a conditional UPDATE).
  **Why:** The status is Suno's own state of the clip, not catalog content the user chose, like #142's remote state. #154 AC 1 already makes completing a Generation that has never been complete a change no review confirms. Under the alternative (a `status` diff field following the Changed choice), a declined diff, or a clip whose only difference is its status (classed Already linked), would leave the Generation reading "Generating" for ever, so #154 AC 3 would still fail. Adding `status` to the compared fields would also change every remembered declined hash. One way only keeps "a stored status that is not final means never complete" true, so `TryCompleteClipAsync`'s conditions stay in step: once a sync gives the Generation its final status, the completion refuses it (409 `already_complete`).
  **Issue:** #314, #154
- **Decision:** #314: the status write is never silent. The review summary has a trailing `statusChanges` (`ImportReviewSummary.StatusChanges`), counted as the catalog is now. `nothingToDo` also needs `statusChanges == 0`, so an export whose only news is a finished clip can still be confirmed. The commit result has a trailing `statuses: [{sunoId, status, generation{id, shortcode}}]`. On the web, the summary shows `summary-statuses` and the result shows `commit-statuses` (`statusResultText` in `importReviewRules.ts`).
  **Why:** Invariant 3 forbids silent overwrites. Before Confirm, the user sees how many Generations will take Suno's status, and afterwards which ones did. Without the `nothingToDo` change, the Confirm button would stay disabled for a status-only sync, and the status could never arrive.
  **Issue:** #314
- **Decision:** #314: the invariant 3 guard's scenario adds three bystander Generations whose clips differ only in status, all left to Skip: `status-1` (submitted to complete; rated, commented, with a declined hash), `status-final` (complete to error), and `status-going` (submitted to streaming). The `generations` C rule gains `TakesSunosFinalStatus`, which allows only `status-1`'s `provider_status`, only from a status that is not final to `complete`, and every other column must be equal. The result must list exactly that one. The every-Skip complement now expects exactly that one change. The new bite `TheGuardFailsWhenSunosStatusChangesAFinalStatusOrAnotherColumn` uses a trigger to make the status write also fail `status-final` and retitle `status-1`, and the guard reports both. The regression tests are in `Suno/SyncStatusTests.cs`. Before the fix, the first one failed with `Expected ("complete", 143.52), Actual ("submitted", 143.52)`: the bug, an applied diff brings the duration but not the status.
  **Why:** The new write is allowed only as the rule says, and the complement and bite prove that the guard still sees everything else.
  **Issue:** #314
- **Decision:** CLAUDE.md's invariant 3 guard summary ("only rows a confirmed choice names may change") was left as it is, because the brief allows only the guard-status words. It now has two documented exceptions, both Suno's own state of a Generation that was never complete: #154's completion and #314's status. The guard's own doc comment names both.
  **Why:** The scope of the CLAUDE.md edit is fixed by the brief. The owner may want to reword the line, and whether this reading of "explicit user choice" holds is the owner's call.
  **Issue:** #314

## /n8-exec M4 fix pass — 2026-10-07

Track a1 (#325 #326 #334 #336 #337 #338 #348):

- **Decision:** The commit guard's songs/versions change rules use column allow-lists: an existing Song a choice names may change only `updated_utc`; an existing Version only `updated_utc`, `revision`, `last_generation_ordinal`, and `is_frozen` (and `is_frozen` only 0 → 1). The `settings` `suno.models` row may only be added at revision 2 or raised by exactly one. The lists were read off the real commit (a dump of the changed columns), not guessed.
  **Why:** Narrowest set the commit legitimately writes; any further column (title, name, notes, inputs) is an overwrite invariant 3 forbids. A permanent trigger-based bite test (`TheGuardFailsWhenTheCommitRetitlesATargetedSongOrRenamesItsVersion`) keeps it honest.
  **Issue:** #336
- **Decision:** The commit guard's scenario reads every catalog row before the export's first upload (`Scenario.Pristine`) and asserts, inside the scenario, that nothing changed by the time every choice (resolutions and remote-state PATCHes included) is saved; the discard complement compares against `Pristine`.
  **Why:** Anchoring all commit tests on a pre-upload snapshot catches an idempotent classify-time write in every test, not only the discard one.
  **Issue:** #337, #325, #334
- **Decision:** The staging guard snapshots before the earlier export's upload, and its run now holds a Conflict record, a Generation whose clip is in Suno's Trash, #141 resolution choices (apply/moveToNewVersion/keep), and a #142 remote-state PATCH to Skip and back.
  **Why:** #325's AC and #334's AC; #326's second AC (every class present) is met by adding Conflict rather than weakening the doc.
  **Issue:** #325, #334, #326
- **Decision:** #348's case is built through the real path (an observed Generation still `submitted`, an export retitling it, the Changed diff applied with every field accepted, committed) via a new helper `ProvisionalCompletionApi.ReviewedFromAnExportAsync`, rather than writing `provider_records.export_id` by SQL.
  **Why:** With every field accepted no hash is remembered, so only the export-sourced raw clip guards it: exactly the reachable case the bug describes.
  **Issue:** #348
- **Decision:** #338 extends the existing `AReimportRestoresTheDeletedGenerationWithItsRatingCommentsArtworkAndShortcode` (stage a red cover with the reimported record; the restored Generation keeps its own blue artwork asset) instead of adding a new test; `ImportCommitApi.StageImageAsync` gains an optional colour.
  **Why:** Same images would deduplicate to one asset and hit the no-op branch, so the staged cover must differ.
  **Issue:** #338
- **Decision:** #326's tests leave out the verifier's re-count probe (F2: a committed export is counted as cleared every day).
  **Why:** That is a separate low-severity finding, not one of the three rules; asserting it would encode a known defect or fail.
  **Issue:** #326

Track a2 (#318 #319 #320 #321 #323 #324 #333 #340 #346):

- **Decision:** An imported Song's cover comes from a fallback that is only displayed and never stored: a Song with no artwork of its own and no Selected Generation shows its newest Generation's image, active Generations first, then by created time and ordinal. The Song answer gives it `source: "newestGeneration"`. The import does not select a Generation.
  **Why:** The PRD says the user chooses the Selected Generation ("User may select one Generation"; "Selecting a Generation only marks it…"), and no story says the import selects one. Auto-selecting would also change Album entries and playback (PRD: "if the Song has no Selected Generation, n8Tracks asks the user … instead of guessing"). This narrows #121 AC 3. "Shows nothing when it has no Selected Generation" now holds only when no Generation of the Song has an image. "Shows nothing when that Generation has no image" still holds whenever a Generation is selected. The owner may want #121 AC 3 reworded, or may prefer auto-select.
  **Issue:** #318 (#121)

- **Decision:** The guard for #319 is an architecture test, not a DI test. No type in Domain, Application, Infrastructure or Api may depend on `System.Net.Http` or `System.Net.WebSockets`, except `Api.Endpoints.HealthCheckCommand`, the container self-check. Raw sockets are excluded because `ListenPortProbe` binds the server's own port.
  **Why:** The DI container holds `IHttpClientFactory` through ServiceDefaults whether or not anything uses it. The type rule catches the actual way a Suno fetch would be added.
  **Issue:** #319

- **Decision:** #324 is enforced by a database trigger `tr_generations_suno_id_never_changes` (migration `20261008000000_ProtectGenerationSunoId`, trigger only). It refuses changing a Generation's Suno ID once set. Setting it from null to a value is allowed. In the invariant 1 guard, the frozen targets of both sweeps now carry an audio source that points at a Generation with a Suno ID.
  **Why:** "Once set" is simpler than "while a frozen source points at it" and fits every current write path. Attach and import insert the ID, #141 refresh and #154 completion never write it, and moves and restore do not update it. Schema change: none, so there is no retention shape bump.
  **Issue:** #324

- **Decision:** The #340 verification rules:
  - **Text entries:** the 11 text entries (`GenerationVerification.TextKeys`, the fields the extension marks `text`) accept `expected` and `found` only as `{length, sha256}` or null.
  - **Notes:** a note that quotes a line of 8 or more characters of the Version's text, taken from the request's snapshot, is refused. That text is the text entries' values plus titles, descriptions and names.
  - **Extension:** it now sends `reportNote`, the same steps with those names given generically (for example "the source", "the Version's file note says which"). The panel still shows the named version.
  **Why:** A note cannot be checked against a template without tying the server to each adapter version. Checking it against the snapshot's own user text enforces "never the Version's text" exactly. The 8-character floor keeps short titles from colliding with the adapter's own words.
  **Issue:** #340 (#146)

- **Decision:** Kept file inputs pass through unchanged (#320). `VersionLineageRules.Errors` takes the held file inputs, and one sent back exactly as held is not checked against the mode. There is no web change.
  **Why:** The editor sends the whole list. The bug offered either a server fix or a UI fix. Fixing the server also covers API clients and keeps the hidden note stored, as #125 intends.
  **Issue:** #320 (#125)

- **Decision:** An "all" bulk workspace move requires `expectedCount`. A different count inside the transaction gives 409 `song_count_changed` with `count` and `expected`, and nothing moves. The web sends the count the confirmation stated, and on refusal it says nothing moved and reads the list again.
  **Why:** #151 AC 2 says the move is refused if the count differs. Requiring the count for `all` makes it impossible to forget. The only callers are the web and tests; the gateway does not call this route.
  **Issue:** #346 (#151)

Track c (#317 #345 #347):

- **Decision:** Select/Clear of the Selected Generation now save through the shared `useRevisionedSave` with one compared field (the selection, by Generation ID); a 409 whose only difference is elsewhere on the Song is retried silently, a changed selection opens the shared ConflictDialog (Reload / Reapply / Keep editing). Archive/Reactivate keep their one-retry (out of #317's scope).
  **Why:** docs/conventions.md "Concurrency": the client works out which fields differ; reusing the shared helper keeps one conflict UI. Request shape unchanged (still sends the Generation ID).
  **Issue:** #317
- **Decision:** `useGenerationChoices.ts` renamed to `.tsx` (it renders `ConflictValue`); the hook now returns `dialog`, rendered once in `SongVersions`. The pure rename landed in 7c953b5 by an index slip (content unchanged; not force-pushed per brief).
  **Why:** the hook supplies the dialog rows' JSX.
  **Issue:** #317
- **Decision:** After a refused workspace move, focus goes to the refusal text (`role=alert`, `tabIndex=-1`) inside the dialog; after a successful move, to the "Moved N Songs" note (`tabIndex=-1`) in the status region.
  **Why:** the Move button is disabled while loading and the trigger is disabled after success, so neither can hold focus; the reason is the most useful place to land. Mantine's focus return does not override (it only acts when focus is on BODY).
  **Issue:** #347
- **Decision:** e2e suno-workspaces-page walk gains a refused move (a third Song taken out of the workspace via API while the dialog is open, `sunoWorkspaceId: null`) with focus, Tab-containment and modal axe checks; it uses selected IDs, not "all", so it does not depend on track a2's #346 request change.
  **Why:** the bug asks for the e2e axe walk to cover the refused state.
  **Issue:** #347

Track b (#322 #327 #328 #329 #330 #331 #332 #335 #339 #341 #342 #343 #344):

- **Decision:** The manifest validator allow-lists top-level keys (`allowedManifestKeys`): manifest_version, name, version, version_name, description, icons, action, background, permissions, optional_host_permissions, options_ui. Every other key fails as "<key> must be absent", replacing the four-key deny-list.
  **Why:** a deny-list let web_accessible_resources, content_security_policy, declarative_net_request and chrome_url_overrides through; an allow-list fails closed on keys nobody thought of.
  **Issue:** #322

- **Decision:** Network refusal is a vitest `setupFiles` entry for every suite (`test/no-network.ts`), not only the adapter, panel, field-map and invariant suites. fetch, XMLHttpRequest, WebSocket and EventSource throw, and an afterEach fails a test that reached for one even if the code swallowed the error. The self-test reads the attempt log through a global symbol, not an import, so it fails if the setup file is dropped.
  **Why:** every suite already passed with it (no suite uses the real network; background tests inject fetch), so scoping it would only leave gaps.
  **Issue:** #327

- **Decision:** The invariant-4 observer check is structural: in the wraps-only file the name `fetch` may only save the original (`const x = …fetch…`) or replace it (`….fetch = …`), and the saved original may only be called as `original(...args)` with the wrapper's own rest parameter. A wrapper that edits `args` before passing them on is listed under "Not covered", with `observe.test.ts`'s behavioural tests named as its cover.
  **Why:** a computed address cannot be recognised as an address; only "passes the page's own arguments" can be checked statically.
  **Issue:** #330

- **Decision:** executeScript, insertCSS, removeCSS, userScripts and debugger are found by name (identifier, property or string) in every shipped file, not by resolved type. The one exemption is `src/popup/main.ts`, and only for `{target, files: [<own bundle constants from background/connection.ts>]}`.
  **Why:** by name, a hand-written interface over `chrome` (as connection.ts uses for scripting) cannot hide a call; the popup's existing executeScript of suno.js (tabs opened before pairing) is legitimate.
  **Issue:** #331

- **Decision:** `Page.click`, `choose` and `createWorkspaceClick` ask the forbidden-control matcher (`judge`) before the enabled check; `press` judges, then checks enabled, then dispatches.
  **Why:** a disabled forbidden control must be reported as refused so the invariant-4 guard sees it (verifier's B04 now fails the guard).
  **Issue:** #332

- **Decision:** When Suno lists another workspace with the Song's workspace's name, select() first accepts the workspace if the library pane already shows its ID; otherwise it presses neither row, shows a new panel state `select` ("Select the Song's one in Suno's workspace list") and waits up to SAME_NAME_WAIT_MS (2 min) for the library feed to ask for that ID, then accepts it; on timeout it stops with SAME_NAME (reworded to mention selecting by hand). Another ID is never accepted.
  **Why:** rows carry no ID, so pressing either is a guess (invariant-safe only by verification); the user's own selection is verifiable by ID, which makes P1 (create while the name exists) and P2 (later generations) work without a rename.
  **Issue:** #335

- **Decision:** A workflow may declare `after: '<earlier workflow id>'` (registered before it). When its needs fail, the self-check gives the new state `waiting` (panel "Waits for an earlier step: <title>", report `waiting`) instead of `not-working`. Chained: fill-songs-simple/advanced after switch-form; fill-speech-* after switch-speech-form; fill-sounds after switch-sounds-form; answer-overwrite and verify-source-* after choose-source-action.
  **Why:** these needs describe states an earlier step sets up; on a healthy Create page they showed red. Scoping needs to the start page would have removed the useful "ready" signal when the state is present.
  **Issue:** #328

- **Decision:** songs.simple.model, songs.advanced.model and sounds.single.sounds_model join BLOCKED_ON_CAPTURE (outcome manual, "Choose the model in Suno's model menu by hand", expected = the Version's model; not_applicable when none). The `model()` filler and the invented `MODEL_MENU` target are deleted; the captured model buttons stay as targets, unpressed. ADAPTER_VERSION = 10.
  **Why:** D9: no snapshot shows the model menu open.
  **Issue:** #339

- **Decision:** `SourceRoute.automated` (false on every route) gates the clip-page route. The tab keeps a new source phase `byHand`, the panel asks the user to load the source (More options › Remix › action) with Continue, and back on Create the source is verified by the thumbnail clip ID as before. A stored `opening` phase on a non-automated route stops instead of pressing on the clip page. The automated path and its tests are kept, run with `automated` flipped on inside the test.
  **Why:** D10: the clip page and its More options button are not captured; the form after Cover is, so verification stays.
  **Issue:** #341

- **Decision:** Reuse Prompt's source entry is `manual` with a note starting "Not verified: Reuse Prompt leaves no source on the form to check…", never `set`.
  **Why:** nothing on the form shows which clip's inputs were copied.
  **Issue:** #342

- **Decision:** No value of the Version goes into `expected`: sourceShown's texts are constants; choose() says "to offer the option asked for"; quoted interpolations are no longer accepted by the literal-only guard.
  **Why:** a quote inside a value splits the report's quote redaction (#343), so quoting is not a safe carrier.
  **Issue:** #343

- **Decision:** The literal-only guard (`test/diagnostics-source.test.ts`) scans all of `src/adapter/**` plus `src/content/sunoGenerate.ts`: first arguments of expected / PrimitiveError / ForbiddenControlError, findProblem's returns, target descriptions (objects with role/around/text/testId/within), and description parameters (`description` / `*Description`) whose every same-file call passes written text. Allowed spans: `.description`, `String(x.length|x.count)`; pass-throughs `x.expected` and `findProblem(...)`. Not covered (listed in the file): forbidden.ts reasons, the runner's timeout seconds, values passed through another file's function.
  **Why:** the verifier found text reaching `expected` from four files outside workflows/.
  **Issue:** #344

## Ad-hoc — 2026-10-07

- **Change:** Imported Songs, and any Song with no artwork of its own and no Selected Generation, now show their newest Generation's image, computed at read time (#318). This narrows #121 AC 3 ("shows nothing when it has no Selected Generation") to Songs whose Generations have no image.
  **Why:** Verification of #121 found that the must-have truth "a Song imported from Suno shows a cover without the user doing anything" failed. The owner's decision on the AC wording is pending.
  **Affects:** M4 #121 (closed), epic #11 AC 4; future milestones that list or display Song artwork — plans may be stale. — reconciled 2026-10-07: owner kept the fallback; #121 AC 3 amended

## /n8-exec M4 fix pass 2 — 2026-10-07

## M4 fix pass 2 decisions

- **Decision:** The design is construction, not content matching. The server no longer compares notes with the Version's text: `GenerationVerification.NotesCarryingText` and its 8-character floor are removed. A note now only has to fit a bounded shape (`GenerationVerification.IsNote`): one line of at most 1000 characters, with no control characters and no line or paragraph separators. A note with any other shape gets 422 and nothing is stored. The extension guarantees that notes carry none of the Version's text. A new source guard, `extension/test/verification-note-source.test.ts`, which sits next to the diagnostics literal-only guard (#150/#344), checks every note that can reach n8Tracks in `src/adapter/**` and `src/content/sunoGenerate.ts`:
  - each `reportNote`;
  - each `note` that has no `reportNote` beside it;
  - each assignment to `.note` or `.reportNote`;
  - each filler `unavailable` reason.

  Each of these must be text written in the source. A template may hold only the adapter's own constants: a filler's `control`, a route's `menu`, `item` or `label`, or a SCREAMING_CASE constant. It may also hold a `const` of the same file that holds such text, or the answer of a function in the same file whose every return is such text.

  **Why:** #379 AC 1 says the extension's fixed notes are accepted "whatever the Version's title, names or text entries say". Any server-side match against user text breaks that rule:
  - Matching titles and names refuses Songs titled "Duration", "Selected" or "workspace".
  - Matching only whole lyric, styles or prompt lines still refuses a fixed note whenever such a line is part of it (for example styles "Duration is Auto").
  - Checking against a server-side catalogue of note templates would tie the server to each adapter version, which #340 already rejected.

  Construction meets AC 3 ("impossible by construction"). The one-line rule still refuses a pasted block of lyrics or a prompt from any client. Trade-off: a non-extension client holding a `suno.generate` token can now store one line of free text as a note. Before, that was refused only when the line matched the Version's text. Notes are shown only on the owner's own Version page, and a token is the owner's own credential.
  **Issue:** #379 (#340)

- **Decision:** In `extension/src/adapter/sources.ts`, the panel's named steps and the generic steps n8Tracks stores are now separate functions: `byHandStep`/`namedSteps` and `reportedByHandStep`/`reportedSteps`. Before, one `stepsOf(named)` produced both. When the extension does not know a source's Suno action, the stored note now says "the extension does not know its Suno action" without the action key. The panel still names the key. `sentenceOf` capitalises the joined steps; its output is unchanged. In `fill.ts`, `unavailable()` takes the writer's `{ unavailable }` answer instead of a bare string.
  **Why:** A static guard can only show that a note is written text if the generic text is built separately from the named text. The action key comes from the API (a relationship type's mapping), not from the adapter, so it is left out of the stored note.
  **Issue:** #379

- **Decision:** The e2e verification Demo (`generate-on-suno.spec.ts`) now titles its Song "Duration". That word is part of the extension's Duration note sent in the same report.
  **Why:** It reproduces #379 against the built image. With the old check, that report got 422.
  **Issue:** #379

## /n8-exec M5 — 2026-10-07

- **Decision:** `audio_files` is a new table (migration `20261008010000_AddAudioFiles`). Its status check already allows `missing` as well as `available`. A scan writes only `available`; the domain `AudioFileStatus` declares both. The association columns (#206) and `revision` (#210) are not added here.
  **Why:** #207 writes `missing`. #206 may put a hand-written trigger on `audio_files`, and widening a CHECK later would rebuild the table and drop that trigger (orchestrator rule). The association columns depend on #206's FK/trigger design, so adding them now would guess at that story's schema.
  **Issue:** #203

- **Decision:** The scan does not follow symbolic links, whether to a file or a directory, and whether they lead inside or outside the mount. Each link counts as `skipped`. `MediaMountReader` also refuses any relative path with an empty, `.`, or `..` segment, a backslash, a NUL, or a rooted form.
  **Why:** Invariant 2: until #205 brings real-path resolution and the `skippedLinks` reasons, never following a link is the only rule that cannot read outside the mount. #205 changes "inside links" to followed.
  **Issue:** #203

- **Decision:** `POST /api/v1/media/scans` answers 202 with `{jobId, alreadyInProgress:false}` and the job as `Location` when it queues a scan. When a scan is already queued or running, it answers 200 with `{jobId, alreadyInProgress:true}`, not a 409 like backups. A token gets 403 `session_required`.
  **Why:** The AC says it "returns that job instead of starting another", so a client should treat both answers as success. The status code and the flag tell them apart.
  **Issue:** #203

- **Decision:** The last-scan summary is the `settings` row `media.lastScan` `{jobId, trigger, outcome, startedUtc, finishedUtc, counts, error}`, written on success and on failure, including interruption. A failed job carries no result (the worker drops it), so its progress message carries the counts reached ("N of M files: … new, … changed, …"). File counts move only once their batch is written.
  **Why:** The discretion asks for a summary outside the jobs table, and for a failed job to report the counts reached. The worker keeps a failed job's last message, not its result. Counting on write keeps the summary equal to what is in the table.
  **Issue:** #203

- **Decision:** The scan uses one timestamp for every record: `last_seen_utc` (and `first_seen_utc` for new files) is the time the scan started. A file that is listed but cannot be stat'ed is counted unreadable. If it is cataloged, it counts as unchanged and keeps its record. If it is new, it is cataloged with size 0 and the epoch as its modified time, so the next scan sees it as changed and reads it. A zero-byte file is never opened. This also covers FIFOs and devices, which report size 0, so opening one cannot hang.
  **Why:** One timestamp per scan lets #207 mark `missing` as "last seen before this scan began". The partition new/changed/unchanged = seen must hold for every listed file.
  **Issue:** #203

- **Decision:** Header reading uses `z440.atl.core` 7.18.0 (the latest on NuGet, published 2026-09-28) in Infrastructure, behind `IAudioMetadataReader`, given the read-only `FileStream` and the extension. Its process-wide settings are: `NullAbsentValues`, no title made up from a file name, `ReadAllMetaFrames` off, and stack traces kept off the console. Only duration, title and artist are taken. Title and artist are trimmed and cut to 500 characters, and duration is stored in milliseconds. A header counts as unreadable when there is no duration or the duration is zero.
  **Why:** This is the story's discretion. A cap keeps a damaged or hostile tag from filling the database.
  **Issue:** #203

- **Decision:** The test fixtures are seven 1.5 s tones tagged "Fixture Title" / "Fixture Artist", made with ffmpeg (Docker `linuxserver/ffmpeg`) and committed under `tests/n8Tracks.Api.Tests/Media/Fixtures/`. `scripts/check-suppressions.py` now counts `.opus` and `.aac` as binary, beside `.ogg`, `.m4a` and the others. The `config-file-not-utf8` good fixture gained a `.opus` and a `.aac` file, and fails without the change (checked).
  **Why:** The scanner refuses any tracked non-UTF-8 file whose extension is not on its binary list. These two audio formats are now real repository content. A two-entry widening of the binary list loosens no warning rule.
  **Issue:** #203

- **Decision:** The `association` filter accepts `any`, `associated`, and `none`. Until #206, `associated` answers an empty page. `status` accepts `available` and `missing`. `metadataReadable` accepts `true` and `false`. `offset` defaults to 0 and `limit` to 200 (range 1–200). Any other value, or a repeated parameter, answers 422 `validation_failed` keyed by the parameter. `audio_files` counts as catalog data in the invariant 3 guard. `POST /media/scans` is in the invariant 1 table of endpoints that touch no Version. `Application.Media` takes no catalog type, so it is not in `CatalogServiceNamespaces`.
  **Why:** These follow the story's JSON-shape discretion and the M5 orchestrator rules for new tables and namespaces.
  **Issue:** #203

- **Decision:** Migration `20261008020000_AddAudioFileAssociations` gives `audio_files` five new columns: `song_id`, `generation_id`, `association_origin` (`suno-id`/`user`), `unmatched_reason` (all four codes: `generation_deleted`, `multiple_suno_ids`, `unassociated_by_user`, `song_deleted`) and `revision`.
  - Enforcement: a check `ck_audio_files_association`, a RESTRICT FK to `songs`, and a composite FK `(generation_id, song_id) → generations (id, song_id)` with ON DELETE RESTRICT and ON UPDATE CASCADE.
  - `generations` gains only the unique index `ix_generations_id_song_id`, as the composite key's parent, with no rebuild.
  - `audio_files` is rebuilt by hand inside the migration's transaction. It has no triggers and no referrers.
  - The composite FK is not modelled in EF (the Song FK and both indexes are).
  **Why:**
  - The planner's trigger premise is stale (m5-plan 3b). A composite FK makes the database refuse a Generation of another Song.
  - The update cascade means a move (#123/#141 change `generations.song_id`) takes the Generation's files along, instead of failing.
  - EF would model the parent as an alternate key, which it refuses to change, and its SQLite rebuild switches foreign keys off outside the transaction.
  - `revision` and every reason code are declared now, so #210 and #213 need no rebuild.
  **Issue:** #206

- **Decision:** Rule 1 (minimal retention handling): a new `Application.Media.AudioFileLifecycle.ReleaseAsync` (internal) is called by the Generation, Version and Song deletion services inside their transaction, just before `RetainWithinAsync`.
  - It unassociates the files of the deleted Generations (reason `generation_deleted`) or every file of the deleted Song (reason `song_deleted`), and raises each one's revision.
  - Associations are not retained.
  - #213 adds the counts, the move warning, and the restore notes on this class.
  **Why:** With the new RESTRICT keys, `RetentionStore` refuses to delete a Song or Generation that a file still names (the test proves a 500 without the hook). The branch must never have a broken delete path (orchestrator).
  **Issue:** #206

- **Decision:** `SunoIdMatcher` (`Application.Media`) finds Suno IDs in the file name only, never in the directory. The pattern is `(?<![\p{L}\p{N}])` UUID `(?![\p{L}\p{N}])`, any case, deduplicated, lower-cased.
  - Lookup: live Generations and provider tombstones are compared with `lower(suno_id)`.
  - Exactly one live Generation → associate. Two or more → `multiple_suno_ids`. Otherwise `generation_deleted` when a found ID is tombstoned, or no reason.
  - When it runs: at the end of a completed scan, over every unassociated record except `unassociated_by_user`. Each file gets its own IMMEDIATE transaction: the Generation is read, then one conditional UPDATE (still unassociated, not user-removed) raises `revision`.
  - A recomputed reason replaces `generation_deleted` and `multiple_suno_ids`. `song_deleted` is kept unless the file now matches or carries several live IDs.
  **Why:**
  - This follows the story's discretion.
  - Suno IDs are stored as Suno sends them (lower case), but a hand-attached one could differ in case.
  - Keeping `song_deleted` honours #213's "files of a deleted Song show song_deleted": the Song's Generations are tombstoned too, so a plain recompute would turn it into `generation_deleted`. #213 owns clearing it on a Song restore.
  **Issue:** #206

- **Decision:** The scan result, the `media.lastScan` summary counts and `MediaScanCounts` gain `associated` (associated by this scan) and `unmatched` (every unassociated record after the scan, Missing and user-removed included). A summary written before reads both as 0. The API's `AudioFileResponse` fills `song`/`generation` as `{id, shortcode}` (current shortcodes, read at answer time), `associationOrigin` and `unmatchedReason`, and adds `revision`. `association=associated|none` is now real.
  **Why:** This follows the story's discretion on the API shape and the scan counts. `revision` is what #210's If-Match will use.
  **Issue:** #206

- **Decision:** The shared fixture `extension/fixtures/filenames.json` holds 14 example names, each with the Suno IDs expected in it. It covers the PRD example, browser numbering, the stream name, bare `<id>.wav` and `<id>_lyrics.mp3`, and negatives. The server's matcher test reads it, copied into the test output as `Media/Fixtures/filenames.json`.
  **Why:** #216's key link: the extension's downloader test reads the same file, so the names it produces are proven to match.
  **Issue:** #206

- **Decision:** `Application.Media` stays out of `CatalogServiceNamespaces`. `SunoIdMatcher` and `AudioFileLifecycle` take only IDs, strings and `Domain.Media` types; the deletion hook is `internal`. `audio_files` stays classified as catalog in `SunoExportStagingGuardTests`, and no new table was added.
  **Why:** This is the orchestrator rule: a namespace joins the guard only if it takes catalog types.
  **Issue:** #206

Story #204 (built in parallel; merged into the milestone branch):

- **Decision:** The scheduler is its own hosted service, `Infrastructure/Media/MediaScanScheduler`, looking every 30 s. It is not an `IDailyTask` on the once-a-minute daily scheduler. Each look calls `MediaScanScheduleService.TickAsync` (`Application/Media`), which enqueues only through `MediaScanService.StartAsync(Startup|Scheduled)`, the method `POST /media/scans` calls.
  **Why:** The story fixes a 30-second check. The daily scheduler's whole-minute pacing and "planned local time" rules do not fit an interval counted from the end of the last scan.
  **Issue:** #204
- **Decision:** Whether a scan is due is computed as `media.lastScan.finishedUtc + interval <= now`, from the stored summary of any scan, manual and failed ones included. When no scan has ever ended, a scan is due at once.
  **Why:** The Claude's Discretion line says due "from the stored last-scan summary, not from memory". With no summary yet, the only way to reach a scheduled look is that the startup scan's job vanished, so running one at once is the safe choice.
  **Issue:** #204
- **Decision:** The startup scan is queued once per process, at the first look that may queue anything: after setup is complete and outside maintenance, whatever the schedule, even with the folder unavailable. A singleton `MediaScanStartup` flag records it. If a `media-scan` job is already queued or running at that look, it counts as the startup scan.
  **Why:** This covers the discretion lines on setup, maintenance, an unavailable mount, and a leftover queued job in one place. The startup scan is therefore queued up to 30 s after setup completes, not at the instant it completes.
  **Issue:** #204
- **Decision:** The scheduler enqueues nothing while the media folder is unavailable. `MediaScanService.IsFolderAvailableAsync` lists the root within the existing listing timeout, and it is called only when a scan is otherwise due. `IMediaMount` and `MediaMountReader` are not changed, and neither is the legacy `IMediaMountProbe`.
  **Why:** This avoids a merge conflict with #205, which reworks the mount reader and folds in the probe. The check costs one root listing only when a scan is otherwise due.
  **Issue:** #204
- **Decision:** An empty startup or scheduled scan is deleted from the jobs table when the next scan finishes, whether that scan succeeds or fails. "Empty" means it succeeded with `new == 0 && changed == 0` (`MediaScanCounts.FoundNothing`, `MediaScanScheduleRules.IsForgettable`). The deletion uses a new `IJobStore.DeleteFinishedAsync(id)`, which deletes only a succeeded or failed job. Manual and recovery scans, and failed scans, are kept.
  **Why:** This is the discretion line on keeping the jobs list uncrowded. A failed unattended scan is information, so it is kept. #207 must add its `missing` count to `FoundNothing`.
  **Issue:** #204
- **Decision:** The setting has revision 0 until it is first saved. `Revisions.Read(context, allowUnsaved: true)` accepts `If-Match: "0"` on `PUT /settings/media-scan` only; every other route still answers 400 `invalid_revision` to `"0"`.
  **Why:** The discretion line requires `If-Match: "0"` for the first PUT, but the project convention, and existing tests, refuse `"0"` everywhere else.
  **Issue:** #204
- **Decision:** `PUT /settings/media-scan` binds both fields as `JsonElement` and validates them in `MediaScanSchedule.Parse`. So `1.5`, `"15"`, `null`, or a missing interval is a 422 keyed `intervalMinutes`, not a 400 from the binder. The interval is required and range-checked even when the schedule is off.
  **Why:** The test plan requires a 422 for a non-integer. The discretion line says a PUT must always carry a valid interval.
  **Issue:** #204
- **Decision:** Settings → Library is a new sidebar entry after Catalog (`/settings/library`). The page has a switch and a "Minutes between scans" number field, which is disabled while the switch is off but keeps its value. It validates 1 to 1,440 on the page before sending, and shows the API's 422 messages.
  **Why:** The discretion line puts it under Settings in the sidebar. Placing it after Catalog groups it with the other catalog settings.
  **Issue:** #204
- **Decision:** Test hosts switch the media scan scheduler off (`MediaScanSchedulerOptions { Enabled = false }` in `N8TracksApiFactory`), like the daily scheduler. One test runs the real loop at 50 ms on a `TestClock`.
  **Why:** Without this, every existing test host would get a startup scan job, which would change job counts and restore's "jobs active" check.
  **Issue:** #204
- **Decision:** The "no file-change notifications" guard is an architecture test, `NoFileWatchingTests`, with two checks. A NetArchTest check finds no dependency on `System.IO.FileSystemWatcher` in Domain, Application, Infrastructure, or Api; a test-local sample type proves the rule bites. A source scan finds no file under `src/` that names `FileSystemWatcher`. It does not cover `IFileProvider.Watch`, inotify through P/Invoke, or watchers the framework creates for itself.
  **Why:** This is the test plan's architecture test, with a complement so that an empty answer means something.
  **Issue:** #204
- **Decision:** The e2e `library-settings.spec.ts` walks the whole Demo on the fresh container, not only the settings steps. Copied files are fake `.wav` bytes. Step 3's two-minute wait is a 5-second poll that fails at once if the file is listed, not a `waitForTimeout` with a suppression. The spec is tagged `@root-only` and takes about 2.6 min.
  **Why:** The test plan asks only for the settings steps with accessibility scans. Steps 2 and 3 are this story's "must-have truth" (files show up within the interval without the user doing anything) and can only be shown on a real container.
  **Issue:** #204

Story #215 (built in parallel; merged into the milestone branch):

- **Decision:** The Download view reads only Library › Songs (`/me`) through a new `readLibrary` export of `libraryReader.ts`, which reuses the sync's private `readList` and `LIBRARY` spec and the `load-more` workflow. It does not read the workspace or Trash lists.
  **Why:** The key link requires "the same reader and observer; no second way of reading the library". The library feed already holds every workspace's clips, and each clip carries `project.{id,name}`, so the workspace list adds nothing. Suno's feed leaves trashed clips out (`trashed: "False"`), so a trashed clip cannot be selected. A clip that arrives marked `is_trashed` is shown disabled with the reason.
  **Issue:** #215
- **Decision:** Load library and Refresh always reopen `/me` and read on the next page load, like a sync leg. The service worker keeps the request and the selected Suno IDs in `chrome.storage.session` (`downloadLoad`, tab-bound, taken once, forgotten after 2 minutes). The library is not read from the observation queue of the page as it already is.
  **Why:** The observation queue is consumed by reading and limited to 200 messages, so a second read (Refresh) on the same page would see nothing. Reopening the page gives a fresh first page each time, as the sync does. Carrying the selection across the reload keeps the rule that "selections survive Refresh".
  **Issue:** #215
- **Decision:** The plan's download allowance comes from observing the page's own `GET /api/billing/info/`. This adds the observed kind `billing`, and the observer forwards only the three `download_usage` counts. The extension does not open the Download dialog itself in #215. While no reading has been seen, the summary says how to make the page read it (open any clip's More options › Download and close it), and Start is refused with that reason. `ADAPTER_VERSION` is 10 → 11.
  **Why:** TS-004 found that only opening the Download dialog makes the page request billing info. #133 reserves recognising that dialog for #216's own primitive, and the extension may never construct its own Suno request. Refusing Start when the remaining count is unknown is the safe reading of "never needs more unlocks than remain". #216's dialog primitive will produce the reading as a side effect.
  **Issue:** #215
- **Decision:** Start is always disabled in #215, and its reason line shows the most relevant refusal. The order is: no clip selected, no format chosen, unlocks unknown, too few unlocks, and then `START_NOT_YET` ("Downloading arrives in a later version …"). #216 replaces the last one.
  **Why:** The discretion line says "Until #216 lands, Start is shown disabled". Demo step 3 needs the no-selection reason.
  **Issue:** #215
- **Decision:** The lookup is the read-only `Application.Suno.SunoClipLookupService` with a new port, `ISunoClipCatalogLookup`, implemented by `Infrastructure/Persistence/SunoClipCatalogLookup.cs`. It is exposed as `POST /api/v1/suno/clips/lookup` (`RequireScope(suno.sync)`; 1 to 500 non-blank IDs, otherwise 422 `validation_failed`; distinct IDs in the order sent). `deleted` is true when the clip has a provider tombstone. An ignored clip gets `generation: null, deleted: false` with no extra read.
  **Why:** Invariant 5 requires one application-service layer. The ignore list and tombstones never overlap (#143), so reading the ignore list would change no answer. The invariant-1 guard lists the endpoint and the service method as "reads only". The scope guard has the new marker.
  **Issue:** #215
- **Decision:** A clip is not selectable while its status is submitted, queued, or streaming ("Still generating in Suno"), when its status is `error`, when it is trashed, or when it has no `media_urls[0]` ("no audio"). A hidden clip can be selected. M4A (streaming quality) is planned only for clips that have `media_urls[0]`. WAV, MP3, and M4A are always offered, and per-file failures are left to #216. The formats chosen are kept in `chrome.storage.local` (`downloadFormats`), and none is ticked the first time.
  **Why:** These follow the story's discretion lines and TS-004.
  **Issue:** #215

Story #205 (built in parallel; merged into the milestone branch):

- **Decision:** `IMediaMountProbe` is folded into `IMediaMount.Probe()`, which takes no path and probes the mount root. `HealthService` and `SetupChecks` call it through the interface, and `IMediaMountProbe` and `MediaMountProbe` are deleted. `HealthService` no longer takes `N8TracksOptions`.
  **Why:** #203's note and the planner's discretion: no second type may touch the mount. This also settles the #205/#207 difference on where the probe lives, since it now sits behind the Application-layer port #207 asked for.
  **Issue:** #205
- **Decision:** Real paths are resolved by a managed walk, segment by segment (`FileSystemInfo.LinkTarget` on each segment, 40 hops at most, which is Linux's limit). It starts from the root's own real path, so the root may itself be a link, and it runs on every `List`, `Stat`, and `OpenRead`. Paths are compared ordinally, so on a case-insensitive file system a link spelled in another case is refused as escaping, not followed. On Linux, `OpenRead` reads the handle's path back through `/proc/self/fd` and checks it again; other systems skip that check. No native interop.
  **Why:** The planner's discretion. The ordinal comparison only ever refuses more, never less.
  **Issue:** #205
- **Decision:** `MediaEntryKind.Link` is replaced:
  - A link that resolves inside the mount is listed as `File` or `Directory` with `ViaLink`.
  - The other links are listed as `EscapingLink`, `DanglingLink`, or `LoopingLink`.
  - Directories carry `RealPath`, relative to the root's real path.
  - The scan ends a branch when a directory's real path is already on its chain of ancestors. A chain of links that never resolves (ELOOP) is also counted as `cycle`.
  - `skippedLinks {escaping, cycle, dangling}` is added to the job result and the stored summary. It is an init property on `MediaScanCounts`, so a summary row written before this change reads as none skipped. Skipped links are also counted in `skipped`.
  **Why:** The AC asks for skipped links to be counted with these reasons, and for a cycle to end its branch without hanging. A link loop is literally a cycle.
  **Issue:** #205
- **Decision:** One Warning per scan for skipped links: the count and at most ten relative paths. It goes through a new Application port, `IMediaScanLog`, implemented in Infrastructure as `MediaScanLog` with `LoggerMessage`. It is logged on success and on failure.
  **Why:** The Application project has no logging package, and a port keeps it that way.
  **Issue:** #205
- **Decision:** The architecture guard `MediaMountAccessTests` scans the source line by line against exact lists, using the `EnvironmentReadGuardTests` technique:
  - The media setting is named, case-insensitively as `media_?path` or as the `"/media"` literal, only by the options loader, the options record, `MediaMountReader`, and the three backup `IsInside` lines.
  - File-system APIs appear only in 20 listed files.
  - `MediaMountReader` matches a denylist of write, move, delete, attribute, mode, and time APIs, and of non-Read mode, access, sharing, and options. Every `new FileStream(...)` statement in it must name `FileAccess.Read`.
  - Only six files name `IMediaMount`, and health and setup call nothing but `Probe`.
  - ATL is constructed only as `new Track(stream, …)`, and the media adapters never save.
  To keep the denylist strict, the reader avoids `string.Create` and `string.Replace` rather than loosening it.
  **Why:** The planner's discretion names this technique. Exact lists make every new file-system user a deliberate review.
  **Issue:** #205
- **Decision:** The "no API input is a path" check reads every endpoint: route parameters, handler parameters of plain types (query and header), and the request body type from `IAcceptsMetadata`, recursively. It flags names that contain path, file, folder, directory, dir, or mount as a camel-case word. The one allowed entry is the `/api/v1/{**path}` 404 fallback, which never reads the value. A test with deliberate path inputs proves the check bites.
  **Why:** Handlers like the audio-file list read `Request.Query` by hand, so the structural guarantee is the `IMediaMount`-users rule. The name check catches the obvious regression.
  **Issue:** #205
- **Decision:** `scripts/smoke-docker.sh`'s `mounted` section now puts a real fixture (`tone.mp3`) and a link out (`escape.mp3` -> `/etc/hostname`) in the `:ro` media folder. It then runs a manual scan through the API and expects it to succeed, with `seen` 2 (the existing fake `track.flac` counts as unreadable), `skippedLinks.escaping` 1, and no `read-only file system`, `EROFS`, or `UnauthorizedAccess` text in the log. The `api` helper moved above `mounted`. This is a manual scan because the startup scan arrives with #204, in parallel.
  **Why:** The test plan's container proof. #204 can switch it to the startup scan.
  **Issue:** #205

Step A reconcile (merging #205 after #204, #206, and #215):

- **Decision:** `MediaScanService.IsFolderAvailableAsync` (#204) asks `IMediaMount.Probe` within the listing limit instead of listing the whole root. #207 then routes it through `IMediaFolderProbe`. The #205 guard lists needed no change for #204's or #206's files: none of them names `IMediaMount` or `MediaPath`, or uses a file-system API.
  **Why:** #205 made `Probe` the one check of the mount root, and the merge brief asked to consider it. A root listing in a large library reads every entry only to answer yes or no.
  **Issue:** #205

Story #207:

- **Decision:** The mount state is the `settings` row `media.mount` `{state, sinceUtc, recoveryQueuedUtc}`, through `IMediaMountStateStore`. No row means available with `since` null. `MediaAvailability.RecordAsync` writes it only when the state changes, in an exclusive transaction. Nothing is written per file: a file reports `unavailable` while the mount is unavailable, and its stored status otherwise (`MediaAvailability.Reported`).
  **Why:** These are the planner's discretion lines. No migration is needed: `audio_files.status` already allows `missing` (#203).
  **Issue:** #207
- **Decision:** `IMediaMountProbe` is replaced by an Application port, `IMediaFolderProbe`. It is implemented once, as `Infrastructure/Media/MediaFolderProbe`, a singleton `DeadlineCheck` over `IMediaMount.Probe` with health's 2-second deadline. `HealthService`, `MediaAvailability`, the scheduler's `IsFolderAvailableAsync`, and the scan's last check all use it. `HealthService` no longer names `IMediaMount`. In `MediaMountAccessTests`, `MediaFolderProbe` replaces `HealthService` in `MediaMountUsers` and `ProbeOnlyUsers`. `SetupChecks` is unchanged.
  **Why:** The discretion line says the health service and `MediaAvailability` share the probe and its 2-second deadline. #205 had already folded the old probe into `IMediaMount.Probe`, so the port wraps that. The guard stays exact: the probe may only call `Probe`.
  **Issue:** #207
- **Decision:** The health probe changes the state at once both ways. The 60-second monitor (`MediaAvailabilityMonitor` → `MediaRecoveryService.CheckAsync`) needs two failed probes in a row (`MediaProbeStreak`, a singleton) and one readable probe. A readable probe from health also resets the streak. Health records the state only when its database check passed. Nothing is probed or recorded during maintenance or before setup is complete.
  **Why:** The discretion lines say both "the health probe flips the mount state itself" and "two failed probes in a row" for the monitor. The AC needs health, the media status, and the files to agree, so a failure that health shows has to show in the files as well. Recording needs the database. Health is polled before setup, and recording then would put a row in a settings table that holds nothing before setup.
  **Issue:** #207
- **Decision:** A recovery scan is queued only when the state goes from unavailable to available through a probe (health or the monitor). It goes through `MediaScanService.StartAsync(MediaScanTrigger.Recovery)`, after `MediaAvailability.TryClaimRecoveryAsync` records `recoveryQueuedUtc`, at most once every 5 minutes. A completed scan makes the mount available without queuing one. A scan that fails with `media folder unavailable` makes it unavailable at once.
  **Why:** #204's note asks for `StartAsync(Recovery)`, which also takes a scan already queued or running. A completed scan already did the recovery work. The 5-minute spacing is the discretion line, and it is kept in the row so a restart does not reset it.
  **Issue:** #207
- **Decision:** Missing is written as the very last step of a scan: after pass 2 and the Suno ID matcher, and after a fresh probe of the root. If that probe fails, the scan fails as `media folder unavailable` and the mount becomes unavailable. Only Available records the walk did not find are marked, and not those whose path is under a subdirectory that could not be listed. This uses one `IAudioFileStore.MarkMissingAsync` transaction, in chunks of 500 IDs. The status changes leave `revision` alone. Missing files found again become Available in the same batch writes, through `Seen(id, Available)` and `Changed(..., Available)`. A listed file that cannot be looked at, or whose changed content cannot be opened, keeps its status.
  **Why:** The AC requires that a scan that fails or is interrupted part-way marks nothing missing. The key link says Missing is written only after the root is probed again, and only for directories actually listed. #203's note says the same. "Under a directory that could not be listed" is used rather than "in a listed directory", so a folder deleted whole, or an empty mount, still marks its files Missing (the discretion line). The matcher runs before Missing, so it is unaffected: it already covers Missing records.
  **Issue:** #207
- **Decision:** `MediaScanCounts` gains `Missing` (records newly marked Missing) and `Restored` (Missing records found again) as trailing optional parameters. They are in the job result (`missing`, `restored`) and the stored summary, where a summary written before reads them as 0. `FoundNothing` is `New == 0 && Changed == 0 && Missing == 0 && Restored == 0 && Associated == 0`.
  **Why:** #204 asked for `Missing == 0`. The orchestrator added `Associated == 0`: a scan that linked files did something. `Restored == 0` is added for the same reason. #208's discretion line lists "newly Missing, restored" among the counts shown.
  **Issue:** #207
- **Decision:** The audio file API answers `status` (reported: `available`, `missing`, or `unavailable`) and `storedStatus`. The `status` filter and the total use the reported status, so `status=unavailable` is now accepted. `AudioFileService` takes an `AudioFileListRequest` and returns `ReportedAudioFile`s. The store keeps `AudioFileQuery` by stored status.
  **Why:** This is the discretion line ("filters and counts use the reported one").
  **Issue:** #207
- **Decision:** Added `GET /api/v1/media/status` (`catalog.read`, no-store), which answers `{mount: {state, since}}`. #208 extends it with counts, scans, and the warning.
  **Why:** AC 7 says "the media status in the API". #208's discretion line gives this endpoint and its `mount` shape, so this story starts it rather than adding a second one.
  **Issue:** #207
- **Decision:** AC 1's "preferred-file choice pointing at it is kept" is satisfied by construction, because no scan deletes or rewrites a record's ID or association. The preference itself arrives with #212.
  **Why:** No preference exists yet. The tests assert that row counts and associated-row counts never drop.
  **Issue:** #207

Story #216 (built in parallel; merged into the milestone branch):

- **Decision:** WAV, MP3, and M4A are prepared from the Library page (`/me`). The adapter finds the clip's row by its `/song/<id>` link (a new read-only `Target.address`), presses the row's "More options" (a region 6 levels out from the link), then the menu's Download, the format, and "Unlock & Download".
  **Why:** the clip's own page (`/song/<id>`) has no snapshot (#341, D10). `page.library-list.html` shows one link and one More options per row, `page.clip-download-menu.html` shows the menu, and `page.download-dialog.html` shows the dialog. Every page state pressed has a TS-003 or TS-004 snapshot.
  **Issue:** #216
- **Decision:** a clip whose row is not rendered in the list fails that file only, with "scroll to it there, then press Retry failed". The extension does not scroll to look for it.
  **Why:** no snapshot shows how Suno's list virtualises rows. Searching by scrolling would act on unrecorded page states. The library read has scrolled to the end, so loaded rows are normally present.
  **Issue:** #216
- **Decision:** the Download dialog's button is pressed only under its captured name, "Unlock & Download", whether or not the clip is already unlocked. A dialog that names it otherwise stops the step for that format, and no other name is pressed. The unlock is controlled in the service worker. A clip not yet unlocked (`is_download_unlocked` false and not unlocked earlier in the run) gets the page step only when the user confirmed it at Start, and once per clip.
  **Why:** TS-004 captured only the locked state. The authorize fixture's `already_unlocked` member shows that Suno answers an unlocked clip without spending. Guessing another name would invent page structure, which m4-notes rules out (BLOCKED_ON_CAPTURE). The owner's demo with already-unlocked clips will show the real name. If it differs, the WAV/MP3/M4A path for such clips needs a capture.
  **Issue:** #216
- **Decision:** the Download dialog's controls (WAV, MP3, M4A, "Unlock & Download", Close) are a second named exception in `forbidden.ts` (`download-clip`, recognised by the dialog title `/^Download\b/`). `Page.downloadDialogClick` presses them, and the static scan allows it only from `adapter/workflows/download.ts`. `RECOGNISED_DIALOGS` is unchanged. "MP4 video asset" and Manage stay forbidden. The guard's `forbiddenControls` spies the Download dialog control by control (`pressableDialog`).
  **Why:** #133 reserved the dialog for #216's own primitive. The invariant text already allows the unlock, which `docs/suno-integration.md` amended on 2026-10-05. The exception mirrors `create-workspace`.
  **Issue:** #216
- **Decision:** the #133 static scan's downloads rule is narrowed instead of adding a `NETWORK_EXEMPTIONS` entry. `chrome.downloads` and its members are refused everywhere except `src/download/downloader.ts`. That file may use only `download`, `cancel`, `search`, and `onChanged`, and `download` only with an object of `url`, `filename`, `conflictAction`, and `saveAs`. Its `url` must have the adapter's branded type `AudioAddress` (`audioAddressOf` in `addresses.ts`). Bite tests cover a header, an unchecked address, `removeFile`, and the same code in another file.
  **Why:** `downloader.ts` sends no `fetch`, so a network exemption would widen the wrong rule. This proves both "listed host" and "no credential added" statically.
  **Issue:** #216
- **Decision:** the streaming-quality M4A is named `… (suno-<id>) stream.m4a`. Every other format is `… (suno-<id>).<ext>`.
  **Why:** with only the extension to tell them apart, M4A and the stream in one run would collide into `(1)` numbering. #206 had already put this form in the shared fixture. The token stays whole, so the matcher still finds it.
  **Issue:** #216
- **Decision:** unlock confirmation is an explicit checkbox, "Use N Suno download unlocks for this run", which resets when N changes. Start sends N, and the service worker refuses a Start whose N is not the plan's count of distinct clips not yet unlocked in a paid format. The run keeps the confirmed clip IDs, not just a number.
  **Why:** the AC requires the count to be confirmed in the summary. A set of confirmed clips makes "never more unlocks than confirmed" hold by construction.
  **Issue:** #216
- **Decision:** queue mechanics. The service worker sends `download-prepare` (`chrome.tabs.sendMessage`) to the run's tab and waits for the answer, and pushes `download-progress` there. The tab reads `download-run` on each page load that has no library read. A browser restart is detected by an empty `chrome.storage.session` marker (`downloadQueueAlive`). Progress is polled every second while a file downloads, because `downloads.onChanged` reports no bytes. An expired address (`SERVER_FORBIDDEN`, `SERVER_UNAUTHORIZED`) is prepared again once.
  **Why:** these follow the story's discretion lines (the queue lives in the service worker and survives restarts, Resume after a browser restart, refetch once). Start, too, is refused while a sync or Generate on Suno runs (the page is shared).
  **Issue:** #216
- **Decision:** the page's own copy of a prepared file (`<title>.<ext>`, TS-004) is not removed or suppressed. The summary and the docs say it has no Suno ID.
  **Why:** TS-004 did not establish how the page saves the file. Cancelling downloads the extension did not start is out of scope and not covered by the story's exception.
  **Issue:** #216
- **Decision:** `ADAPTER_VERSION` is 12. The new observed kind `download-clip` forwards only `status` and `download_url`, plus `{clipId, format}` from the address, and only ready answers are queued. The workflow `Feature` gains `download` (panel group "Download from Suno"). The manifest's `permissions` gain `downloads`, and the validator's allow-list is widened explicitly with complements: the list without `downloads` fails, and an extra permission fails.
  **Why:** these are adapter pattern changes, and D4 gave the `downloads` permission to #216.
  **Issue:** #216

Story #222:

- **Decision:** Migration `20261008030000_AddDownloadRecords` adds only the new table `download_records` (EF `CreateTable`; no existing table rebuilt, no trigger touched). Its Designer was generated from the current snapshot and renamed to sort after `20261008020000`. Checks: format in the four, `suno_id` a lower-case 36-character text, `file_name` 1 to 255 characters with no `/` or `\`, `size_bytes` null or not negative, `completed_utc <= received_utc`. Index on `suno_id`; no foreign key. It is classified **catalog** in `SunoExportStagingGuardTests` (an import must never change it) and listed in `DatabaseStartupTests`.
  **Why:** The story's discretion lines give the table and its keys. Catalog is the stricter side of the invariant 3 guard.
  **Issue:** #222
- **Decision:** `DownloadRecordService` lives in `Application.Media` (an existing namespace, not a catalog namespace) and takes no catalog type. `GET /generations/{reference}/downloads` resolves the reference with `GenerationService.FindAsync` and passes the Generation's ID and Suno ID to `ListForGenerationAsync(Guid, string?)`. No change to `CatalogServiceNamespaces` was needed. `POST /api/v1/suno/downloads` is listed in the invariant 1 guard's endpoints touching no Version.
  **Why:** Taking `CatalogReference` would put a catalog type in a service outside the guarded namespaces, which the guard refuses. The endpoint stays a thin resolver.
  **Issue:** #222
- **Decision:** A report sent again under the same ID answers 200 with the stored record (201 for a new one), whatever its body says. The Suno ID is stored in lower case, and the clip lookup matches it whatever the case it is asked in. A Generation's records are read by its Suno ID; one without a Suno ID has none.
  **Why:** The discretion line makes a repeated ID a no-op; answering the stored record lets the extension treat both as recorded.
  **Issue:** #222
- **Decision:** "A scanned audio file of that name" strips the browser's ` (n)` numbering (just before the extension, digits only) from both names and compares them without regard to case. The store first narrows `audio_files` with `LIKE '<stem>%'` (escaped), then the service compares exactly. Match: `attached` when such a file's `generation_id` is this Generation, else `elsewhere` (any other or no association), else `not-found`. A Missing file counts as found, with its reported status.
  **Why:** The discretion lines give the three texts and the rules. SQLite's LIKE folds only ASCII case, so a name differing only in the case of a non-ASCII letter is not found; the extension's names always carry the ASCII `(suno-<id>)` token, so this is accepted.
  **Issue:** #222
- **Decision:** The extension reports from `background/downloadRecords.ts` (`DownloadRecorder`), called in order from the queue's `onChange(run)` in `service-worker.ts`. A saved file gets `recordId` (a fresh UUID per save) and `savedAt` in `downloader.ts`. Kept state is `chrome.storage.local` `downloadRecords {address, pending, seen, refused, unrecorded}`. A report is tried three times (waits 1 s, 4 s); 2xx is recorded, 403/422 dropped and counted, anything else (network, 5xx, 404 from an older n8Tracks) kept for later. Pending reports are sent again on a service worker start, the next saved file, and each `download-run` (a page load of the panel). Pairing with another address discards them; a revoked token keeps them for the same address.
  **Why:** The story's discretion lines (three tries, kept per address, at most 500, refused counted). `Connection.pairedAddress()` is new so the recorder knows the address without a handshake. The file is under `background/` rather than `download/`, since it calls n8Tracks through the connection (the plan comment said `download/records.ts`).
  **Issue:** #222
- **Decision:** "When the extension is not connected" means no token is held when the file is saved: nothing is queued, and the file is counted as not recorded. The run section says so, and the lookup line, when unavailable, now adds "but they are not recorded in n8Tracks". The panel's counts (refused, not recorded) are per run; the waiting count is for all runs.
  **Why:** The AC says nothing is recorded while not connected, while the discretion line keeps failed reports for later; the two are told apart by whether a token existed when the file was saved.
  **Issue:** #222
- **Decision:** In the Download view, "Not yet downloaded" is a filter checkbox, disabled until the lookup answers; "Skip files already downloaded" is ticked by default and is not remembered. Files saved in the current run count as downloaded. The summary names up to 20 skipped files, then "and N more". `unlocksNeeded()` now counts the plan's clips (after skipping), so a clip whose paid formats are all skipped needs no unlock, and the count still equals the queue's `clipsToUnlock` check.
  **Why:** The AC gives the filter rule and the default; counting unlocks from the skipped plan keeps the confirmation and the service worker's refusal in step.
  **Issue:** #222

Story #208 (built in parallel; merged into the milestone branch):

- **Decision:** `GET /api/v1/media/status` answers `{mount{state,since,path}, counts{total,available,missing,associated,unmatched}, lastScan, lastSuccessfulScan, activeScanJobId, schedule{enabled,intervalMinutes}, nextScheduledScan, majorityMissingWarning}`; each scan is `{jobId, trigger, outcome, startedAt, finishedAt, durationSeconds, counts, failure}` with `failure` one of `media_folder_unavailable|interrupted|failed`. Built by a new `Application.Media.MediaStatusService` (read-only; not a catalog namespace for the invariant 1 guard).
  **Why:** The planner's shape, plus the schedule itself (the page must say "off" and link to Settings) and a failure code so the page words known causes without parsing error text.
  **Issue:** #208
- **Decision:** The majority-missing count base is a new `MediaScanCounts.AvailableBefore` (init property): the records whose stored status was Available when pass 2 began, kept in the summary JSON (`counts.availableBefore`) and the job result. Rule: `missing > 0 && missing * 2 > availableBefore`, never while Unavailable, read from the last *successful* scan, so only the next successful scan clears it.
  **Why:** #207's `missing` counts only newly marked files; the denominator must be what was Available before. Old summaries read 0 and never warn.
  **Issue:** #208
- **Decision:** The last successful scan is kept in a second `settings` row, `media.lastSuccessfulScan`, written by `MediaScanSummaryStore.WriteAsync` whenever the summary succeeded; before it exists, a succeeded `media.lastScan` stands in. No migration.
  **Why:** Discretion asks for `lastSuccessfulScan` kept in a stored summary so pruning never empties the page, and a failed scan must show the previous counts.
  **Issue:** #208
- **Decision:** `lastScan` is the stored summary unless the newest finished `media-scan` job (new `IJobStore.FindLatestFinishedAsync`) is a different, later job; then that job (trigger and counts null, failure from its error, e.g. `interrupted by restart`).
  **Why:** Discretion says the last result is read from the newest finished job, but a scan cut off by a process stop writes no summary; the summary stays the source otherwise so pruning never empties the page.
  **Issue:** #208
- **Decision:** The page shows `mount.path`, the configured media path (`N8TracksOptions.MediaPath`, the container path), answered by the status endpoint; the one new line naming it is added to `MediaMountAccessTests.AllowedMediaPathLines` (it only answers the text; nothing touches the mount).
  **Why:** Discretion: "shows the container path from configuration"; the #205 guard requires every line naming the setting to be listed.
  **Issue:** #208
- **Decision:** Route `/library/media`; the sidebar gains a "Library" group (role=group, like Settings) between Ignored Suno items and Settings, holding Media only (#209 adds Unmatched Files there). The Unmatched count is plain text until #209 links it.
  **Why:** Discretion: Library is a new sidebar group holding Media and Unmatched Files.
  **Issue:** #208
- **Decision:** The progress bar writes its own ARIA (`withAria={false}` on Mantine's section): no `aria-valuenow` while indeterminate (queued, or the names-only listing pass), `aria-valuetext` with the job's message; completion is announced in a polite `role=status` region ("The scan has finished." / "The scan has stopped."). The state badge uses the theme's default/filled variants.
  **Why:** Mantine always states a value (wrong for an indeterminate bar) and its light green/red badge failed axe contrast in the e2e.
  **Issue:** #208
- **Decision:** Rule 1: `e2e/tests/account.spec.ts`'s exact sidebar list lacked Settings → Library (added by #204, so the spec was already failing on the milestone branch); it now lists `Media` and `Library`.
  **Why:** The sidebar list is asserted exactly; found while adding Media to it.
  **Issue:** #208
- **Decision:** The e2e walks Demo step 1 on its own fresh container (`@root-only`, like `library-settings.spec.ts`), holding the first `GET /jobs/{id}` with `page.route` until the bar has been checked and scanned with axe. Demo steps 2 and 3 (rename the host folder) are covered by `MediaStatusTests` and component tests, not e2e.
  **Why:** A 3-file scan ends faster than the bar can be observed; the shared containers' media folder path is not exposed to specs.
  **Issue:** #208

Story #217 (built in parallel; merged into the milestone branch):

- **Decision:** A record whose reported status is not `available` (stored Missing, or the media folder Unavailable) is 404 `audio_file_unavailable` without opening the file, even when the file is back on disk. The live read decides only for a record that reports Available: a file gone since the last scan, or one that cannot be opened, is 404 as well. The request never changes the record.
  **Why:** AC 3 and the orchestrator's #207 note ("serve a file only when its reported status is available") override the planner's discretion line "a file marked Missing that is in fact present is served". The next scan restores such a file.
  **Issue:** #217
- **Decision:** `IMediaMount` gains `OpenWithStat(relativePath)`, which returns `OpenedMediaFile` (the read-only stream, plus `Stat()` read from the open handle: `FileStream.Length` and `File.GetLastWriteTimeUtc(SafeFileHandle)`). `OpenRead` is unchanged, and both go through the same private `Open` (real-path check plus the `/proc/self/fd` re-check).
  **Why:** the discretion requires the length, tag and modified time to come from the opened handle. Only `MediaMountReader` may use a file-system API, and changing `OpenRead`'s return type would have touched the scan and every fake. The probe-only rule in `MediaMountAccessTests` now also names `OpenWithStat`.
  **Issue:** #217
- **Decision:** Ranges and conditionals use ASP.NET Core's own `TypedResults.Stream(..., lastModified, entityTag, enableRangeProcessing: true)`. That covers single, open-ended and suffix ranges, 416 with `Content-Range: bytes */len`, several ranges → 200 with the whole file, `If-Range`, `If-None-Match` → 304, and HEAD without a body. The endpoint runs the result itself so it can catch `AudioContentChangedException` and `Abort()` the connection. `Content-Disposition` is set by hand (`inline`, `ContentDispositionHeaderValue.SetHttpFileName`: an ASCII fallback plus RFC 5987 `filename*`), because the result's own file name would make it `attachment`.
  **Why:** this avoids hand-writing range parsing that the framework already gets right, and it matches every discretion line.
  **Issue:** #217
- **Decision:** "Other methods get 405" is an explicit `POST,PUT,PATCH,DELETE` mapping on the content route. It answers 405 `method_not_allowed` with `Allow: GET, HEAD` and is marked `RequireScope(catalog.read)` (`EndpointScopeGuardTests` lists it).
  **Why:** the `/api/v1/{**path}` fallback matches every method, so without the mapping ASP.NET Core answers 404 instead of 405.
  **Issue:** #217
- **Decision:** The content stream (`AudioContentStream`, internal to `Application/Media/AudioContentService.cs`) checks the handle's stat on every read. It throws `AudioContentChangedException` when the stat differs from the stat at open, or when a read returns 0 before the length. The endpoint then logs a Warning (ID only) and aborts the connection. The entity tag is `"<size hex>-<modified ticks hex>"`. The media type is `AudioFormats.MediaType(format)` in Domain.
  **Why:** a response is never completed with bytes that do not match its length and tag. The cost is one fstat per 64 KB read.
  **Issue:** #217
- **Decision:** Logging is done in the endpoint, not in the service, because Application services take no `ILogger`; the media code logs through ports. The "could not be opened" Warning carries `audioFileId` only, never the exception, whose message holds the absolute path (invariant 6). The access log line already holds only the URL, which carries the ID.
  **Why:** invariant 6. Tests assert that the log holds neither the file name, the folder name, nor the mount root.
  **Issue:** #217

Story #209:

- **Decision:** Unmatched Files reads the existing `GET /api/v1/audio-files` with new query parameters, not a new route: `sort=path|name|folder|firstSeen` (default `path`, so existing callers are unchanged), `direction=asc|desc` (first seen defaults to newest first, the others to ascending), `q` (trimmed, at most 200 characters, matched case-insensitively for ASCII letters against the relative path, which is the folder plus the name), and `include=suggestions`. With suggestions the limit is at most 100 (default 100), and over that is 422 keyed `limit`. Every order ends with the path, so pages never overlap. `suggestions` appears only when asked for, and is `[]` for an associated file.
  **Why:** The plan's key link names this endpoint with `include=suggestions`, and the discretion pages at 100. The `sort`/`direction`/`q` names follow the Songs and Ignored-items lists.
  **Issue:** #209
- **Decision:** `Application/Media/MatchSuggester` is a pure static scorer that follows every discretion line. Title signals: stem = title 100, embedded title 90, Generation Suno title = stem 80 (that Generation), title in the stem as whole words 60, folder = title 40. Artist +20, duration within 2 s +10. A title under 4 characters needs a second signal for a contained or folder match. The closest Generation is suggested, and an exact tie suggests none. Ties go to the most recently updated Song, then the newest by UUIDv7. The API answers reason codes with parameters (`generation`, `folder`, `artist`, `artistSource`, `differenceSeconds`), and the page writes the sentences.
  **Why:** The scoring and evidence shape are given by the discretion. The extra UUIDv7 tie-break makes equal timestamps deterministic.
  **Issue:** #209
- **Decision:** The stem is compared in two forms, with and without a leading track number (`01 `, `03 - `). Either may equal the title. Suno ID tokens (with or without `suno-` and brackets), the extension, and a trailing ` (n)` are always dropped. Apostrophes are deleted; other punctuation, `_` and `-` become spaces.
  **Why:** Dropping the number alone would stop "7 Rings.mp3" from equalling "7 Rings". Deleting apostrophes keeps "Don't" equal to "Dont".
  **Issue:** #209
- **Decision:** "Archived Songs" are Songs in the default Archived workflow state (`DefaultWorkflowStates.Archived`, by its fixed ID). Songs have no other archive flag. Archived Generations of a live Song stay candidates. A "credited Artist" is any credit, of any role. Aliases are not compared.
  **Why:** The PRD's only Song-level "Archived" is the workflow state. Narrowing Generations or roles further was not asked for.
  **Issue:** #209
- **Decision:** Candidates come from a new read-only port `IMatchCandidateStore` (`Infrastructure/Persistence/MatchCandidateStore`): three reads per request, made only when the page holds an unassociated file. The types carry IDs, shortcodes and text only, so `Application.Media` stays outside the invariant 1 guard's catalog namespaces. A deleted Generation is never a candidate, because deletion removes its row. The API test proves this.
  **Why:** The discretion says "loaded once per request and scored in memory". Keeping catalog types out of the signatures avoids widening the invariant 1 guard.
  **Issue:** #209
- **Decision:** The page (`/library/unmatched`) is a table with the File (row header, plus the unmatched reason sentence), Folder ("Top level" at the root), Format, Duration (`m:ss`, "Unknown"), Size, Status (a filled "Missing"/"Unavailable" badge), First seen, and Suggested Songs (an ordered list linking `/go/<shortcode>` for the Song and Generation, with a bulleted list of reasons). Sort is one "Sort by" select of six order/direction pairs. Search, sort and page are kept in the address. It has no hide or dismiss control. The Media page's Unmatched count is a link named "N unmatched: open Unmatched Files".
  **Why:** The AC lists the columns and orders, and #208's note asks for the link and the sidebar entry. The #210 Associate action will go in the row.
  **Issue:** #209

Story #210:

- **Decision:** The "unassociated by you" flag (`user_unassociated` in the story's discretion) is the existing `unmatched_reason = unassociated_by_user` code, which #206 declared. Only `auto_match_blocked` is new: migration `20261008040000_AddAudioFileAutoMatchBlocked` adds it as `INTEGER NOT NULL DEFAULT 0`, with a plain `ALTER TABLE ... ADD COLUMN` (and `DROP COLUMN` in Down). There is no check constraint. `DatabaseStartupTests` lists the column and still asserts #206's composite FK.
  **Why:** #206 already holds the reason, and the matcher already skips it. A plain ADD COLUMN does not rebuild `audio_files`, so the composite FK that #206 wrote by hand survives (EF Core's DropColumn would rebuild the table). The story's key link names the new column.
  **Issue:** #210
- **Decision:** The matcher skips a file whose `auto_match_blocked` is set, as well as `unassociated_by_user`. Both `MatchableAsync` and the conditional `TryAssociateBySunoIdAsync` check it. The flag is set when the user removes or replaces an association on a file whose name holds a UUID (`SunoIdMatcher.FindIds`, whether or not the UUID names a Generation). Nothing clears it except "Match by Suno ID again". It therefore survives a later re-association and a `song_deleted` release. A test proves that the block alone keeps a `song_deleted` file unmatched. A first association of an unmatched file never sets it.
  **Why:** The discretion says replacing sets it, and the user's decision outranks any scan. Keeping it across a later Song deletion stops a scan from bringing back the match the user moved the file away from.
  **Issue:** #210
- **Decision:** API: `PUT /api/v1/audio-files/{id}/association` takes `{song, generation?}`, each an ID or shortcode, as JSON text (any other JSON type is 422 `validation_failed` keyed by field). It answers 200 with the file and its revision as the ETag. `DELETE` on the same route answers 204 with the ETag. `POST /api/v1/audio-files/{id}/rematch` answers 200 with the file. All three need `songs.write` and `If-Match`. The checks run in this order: file (404) → body → Song (404 `not_found`; a deleted Song is 404 `not_found`, not `song_deleted`) → Generation (404) → `generation_not_in_song` (422) → revision (409 with `current`). A PUT naming the current association, or a DELETE on a file with none, stores nothing but still checks the revision. Rematch on an associated file is 422 `audio_file_associated`, with `current`. The files live in `Api/Endpoints/AudioFileAssociationEndpoints.cs` and `Application/Media/AudioFileAssociationService.cs`. Each runs in one exclusive transaction.
  **Why:** The order and no-op behaviour follow the selection endpoints (#120). The discretion gives the 404s and scope. Rematch refusing an associated file is my choice: it has nothing to match, and the UI never offers it there.
  **Issue:** #210
- **Decision:** "Match by Suno ID again" clears both the block and the reason, then runs `SunoIdMatcher.ResolveAsync` for that file only. On no match, the file keeps the reason a scan would give (`generation_deleted` or `multiple_suno_ids`), or no reason. Each write raises the revision, so a rematch that associates raises it twice.
  **Why:** The discretion says "stays unmatched with no reason". I read that as no user reason: a scan would compute the other two anyway, so the rematch shows them straight away.
  **Issue:** #210
- **Decision:** Audio file responses gain `autoMatchBlocked`, and `song` gains `title` (`{id, shortcode, title}`, the current title joined at read, through an optional `CatalogLink.Title`). Both are additive.
  **Why:** The dialog must name the current association, and a shortcode alone does not tell the user which Song it is.
  **Issue:** #210
- **Decision:** `AudioFileAssociationService` signatures take only `Guid`, `string`, and `int`. Its outcomes carry IDs, shortcodes, and `ReportedAudioFile`. It resolves references internally through `SongService.FindAsync(ISongStore, …)` and `GenerationService.FindAsync`. `Application.Media` therefore stays out of `CatalogServiceNamespaces`. The three endpoints are exercised by the invariant 1 API guard on a frozen Version, with the Version's inputs in the body. The reference guard lists their `id` as non-catalog.
  **Why:** This is the same approach as #209. The guard still proves that the endpoints cannot change a frozen Version.
  **Issue:** #210
- **Decision:** Web changes:
  - Each suggestion has an "Associate" button (named "Associate <file> with <Song> (<shortcode>)[, Generation <g>]"). It associates in one action, with the suggested Generation when there is one.
  - Every unmatched row has "Choose a Song…", and an associated row has "Change or remove…". Both open `AssociateFileDialog`.
  - A file with `autoMatchBlocked` has "Match by Suno ID again".
  - The page has a "Show" select (Unmatched by default, Associated, All), kept in the address as `show`.
  - After every write the list is read again, and a polite status announces what changed. The last column is now "Song", and "None" became "No suggestions".
  - The dialog searches Songs with the shared `SongSearch`, which gained optional `limit` (20 here) and `noteOf` ("Archived" by the fixed `ARCHIVED_STATE_ID`) props. It then offers "None: the Song only" (the default) and every Generation of the Song as radios, with the "a Generation is preferred" sentence.
  - On a 409 the dialog keeps `current`, so trying again uses the new revision.
  **Why:** These follow the AC and discretion. The #209 note put the action in `FileRow` and the reload after it. Reusing `SongSearch` keeps one Song finder. Radios make "Song only" an explicit choice.
  **Issue:** #210
- **Decision:** A Song's list is `GET /api/v1/songs/{reference}/audio-files` (`catalog.read`, in `MediaEndpoints`), answering `{items: AudioFileResponse[]}` without suggestions, not paged. The endpoint finds the Song with `SongService.FindAsync` (404 `song_deleted` through `MissingSongAsync`), then calls `AudioFileService.ListForSongAsync(Guid)`, so `Application.Media` still takes IDs only and stays outside the invariant 1 guard's catalog namespaces.
  **Why:** One item shape with the audio-file endpoints lets the web reuse `acceptFile` and hand a row straight to #210's `AssociateFileDialog`. Resolving the reference in the endpoint follows the #222 downloads endpoint.
  **Issue:** #211
- **Decision:** The order is computed in `AudioFileStore.ListForSongAsync`: Song-level first, then `versions.number_sort_key`, Generation ordinal, format rank (new `AudioFormats.CompareByRank`/`InRankOrder`: WAV, M4A, MP3, then the rest by name), folder, file name, path (all ordinal). The rank is a Domain rule, so #212's preferred-file and player stories can reuse it.
  **Why:** The discretion fixes the order, and the server is where the Version tree order is known.
  **Issue:** #211
- **Decision:** `GenerationSummary` gains `AudioFiles` (`AudioFileTally {Count, Missing, Unavailable, Formats}`, in Application.Media). It is filled in `GenerationRows.SummariesAsync` with one grouped read of `audio_files` plus the `media.mount` settings row (read through `MediaMountStateStore`, with the #207 rule `MediaAvailability.Reported`). So every Generation answer carries `audioFiles`, not only the Song's list. Counts include Missing and Unavailable files; while the folder is unavailable, `unavailable` = `count` and `missing` = 0.
  **Why:** The discretion asks for Generation responses to carry the counts, and they are built in many places (list, one, rate, select, move). One read path keeps them all the same. Reading the state row in Infrastructure avoids threading the mount state through 15 response call sites.
  **Issue:** #211
- **Decision:** `SongSummary.AudioFileCount` (init) and `SongResponse.audioFileCount` are on every Song answer, not only list rows. `SongSort.AudioFiles` (`sort=audioFiles`) orders by a correlated count, most first by default, with the shortcode number breaking ties. An unknown `sort` still gets the existing 400 `invalid_request`, and its message now names `audioFiles`.
  **Why:** Song answers are one record type. "Most first" matches the column's purpose (finding Songs that have audio), as Updated starts newest first. No column chooser exists, so the column is shown after Selected, per the discretion's fallback.
  **Issue:** #211
- **Decision:** Web:
  - `useSongAudioFiles` lives in `SongVersions`, which already owns the Generations and the panel. The Song's list feeds both `AudioFilesSection` (below the Versions table) and `GenerationAudioFiles` (the panel filters it by Generation ID).
  - Both lists, and the Generations (for their counts), are read again on window `focus` and after every association change. Reading again keeps what is already shown.
  - Remove association calls #210's DELETE at once, with a polite announcement and no confirmation step. Change association opens #210's dialog, which shows the current association.
  - Archived marks come from the loaded Generations and Versions.
  - Size is MB to one decimal, with 1 MB = 1,048,576 bytes. A Song-level file's Generation cell reads "Song-level".
  - The panel lists files compactly (one line of details each) rather than as a table, because the drawer is narrow.
  - `Generation.audioFiles` defaults to none when absent, and `Song.audioFileCount` is optional, as with the existing artwork and `songCount` fixture tolerance.
  **Why:**
  - Remove is reversible (associate again), and the dialog already offers it.
  - One list read serves both views.
  - The rest follows the discretion lines.
  **Issue:** #211
- **Decision:** `MediaMountAccessTests`' file-system regex matched an anonymous member named `File` (`row.File.Path`), so the member was renamed `Row`. No guard list changed. The reference guard lists the new route.
  **Why:** This is the pitfall #210 noted. The source never touches the file system.
  **Issue:** #211
- **Decision:** Rule 1: the empty state's "Open Unmatched Files" link sits inside a sentence, so it is underlined always. Axe `link-in-text-block` failed on every Song page with no files, in the existing versions-table, songs, generation-downloads and generation-evaluation e2e specs. The e2e rerun of those specs is the regression check.
  **Why:** WCAG 1.4.1: a link in a text block must not be told apart by colour alone.
  **Issue:** #211
- **Decision:** Preferences live in two new tables, `generation_preferred_audio_files (generation_id PK, audio_file_id)` and `song_preferred_audio_files (song_id PK, audio_file_id)`, not in nullable foreign-key columns on `generations` and `songs` as the planner's discretion line says. Migration `20261008050000_AddPreferredAudioFiles` writes the tables by hand. Each table has:
  - a RESTRICT key to its owner;
  - a composite key `(audio_file_id, generation_id|song_id)` to `audio_files (id, generation_id|song_id)`, RESTRICT on delete and update;
  - a unique `audio_file_id`.

  `audio_files` only gains the two unique parent-key indexes, by plain `CREATE INDEX`, so it is not rebuilt and its composite foreign key survives. The composite keys are not in the EF model, as with #206's key; `DatabaseStartupTests` asserts them.
  **Why:** This follows the orchestrator's binding #212 decision and m5-plan's drift row. Columns would bump the retained shapes (`generation` 6→7, `song` 3→4) and need upgraders. EF would also rebuild both trigger-carrying tables to add a foreign key. With the composite keys, the database itself refuses two things: a choice of a file that is not the owner's, and an association change that leaves a choice behind.
  **Issue:** #212
- **Decision:** Rule 1: `AudioFormats.CompareByRank` now ranks every format in the order of `AudioFormats.All`: WAV, M4A, MP3, FLAC, OGG, Opus, AAC. #211 ranked "WAV, M4A, MP3, then the rest by name", which put AAC before FLAC. The Song's list order, the Generation tally's formats, and the playback fallback all use this one rank. #211's unit test and its API summaries were updated.
  **Why:** #212's AC fixes the order (the user's file-type answer), and the plan says one rank serves the lists and the resolver.
  **Issue:** #212
- **Decision:** `Application/Media/PlaybackResolver` is pure. It reads a Song's files as they report now, and each file's new `AudioFile.Preferred` flag, filled by `AudioFileStore.FilesOfAsync` from the two tables. It needs no other input.
  - A Generation plays its preferred file while that file is available. Otherwise it plays the best available file: by format rank, then earliest first seen, then path (ordinal).
  - A Song plays its available Song-level preferred file. Otherwise it plays the Selected Generation's file, and when the Song's own choice is away, the reason is the Song's fallback reason.
  - With no selection, the reason is `no_selected_generation`.
  - `PlaybackService` serves the two playback reads and the marks on the Song's list (`ReportedAudioFile.Marks`).
  **Why:** One rule with one input gives the same answer everywhere. Reading the choice from the file rows avoids a second read path.
  **Issue:** #212
- **Decision:** The API:
  - `PUT`/`DELETE /generations/{reference}/preferred-audio-file` and `/songs/{reference}/preferred-audio-file` (`songs.write`, owner revision in If-Match) answer the owner (`GenerationResponse`/`SongResponse`), as `selected-generation` does. The owner's revision is checked before the file, so the reference guard can call these routes with a stale revision. An unknown file is 404 `not_found`.
  - A Generation's choice raises only its revision. A Song's choice raises its revision and sets its updated time.
  - `AudioFileResponse` gains `isPreferred` (always present) and `playsForGeneration`/`playsForSong`, which appear only on the Song's list.
  - Playback answers `{source, audioFile {id, fileName, format, durationSeconds, contentUrl}, reason}`, and the Song's answer adds `generation {id, shortcode}`. `contentUrl` is the #217 content route under the page base.
  **Why:** Answering the owner lets the web keep the Song's revision current (`onSong`) and matches the selection endpoint. `isPreferred` on every file lets #210's dialog warn on the Unmatched Files page too.
  **Issue:** #212
- **Decision:** A choice is cleared, and its owner's revision raised, inside `AudioFileAssociationService.AssociateAsync` and `RemoveAsync`, but only when the association actually changes. Naming the current association changes nothing. Deletion clears choices first in `AudioFileLifecycle.ReleaseAsync`, with no revision raise because the owners are going. A restore does not bring them back. `PreferredAudioFileService` (strings and Guids only) and `PlaybackService` are in `Application.Media`, which stays a non-catalog namespace. The invariant 1 guard exercises the four new write routes.
  **Why:** These follow the discretion lines (clearing raises the owner's revision without the caller sending it; #213 restores no choice). The database's RESTRICT keys make the order mandatory.
  **Issue:** #212
- **Decision:** Web:
  - The Audio Files table gains a "Playback" column, and the panel's file list gains a line. They show the badges "Preferred", "Plays now" (for a Generation's file, its Generation; for a Song-level file, the Song) and "Plays for the Song", plus a note when the preferred file is not the one playing ("Preferred, but Missing: X plays instead.").
  - Each row offers "Make preferred" or "Clear preferred" before the association actions.
  - Remove association on a preferred file opens #210's dialog, which now warns ("It is the preferred file of …"), instead of removing at once. Files that are not preferred are still removed at once.
  **Why:** The AC asks the dialog to say so before the user confirms. The Song page's one-click Remove has no confirmation step, so a preferred file goes through the dialog.
  **Issue:** #212
- **Decision:** `MediaMountGuardTests.NotAPath` lists the two PUT bodies' `audioFile` field. The field is a UUID, and anything else is 422. The new resolver code avoids `x.File.Y` member chains through a `Recorded(...)` helper, because `MediaMountAccessTests`' file-system regex matches them. No guard list in the arch test changed.
  **Why:** The body name is fixed by the discretion (`{ audioFile: <id> }`). This is the same pitfall #210 and #211 hit.
  **Issue:** #212
- **Decision:** Each deletion impact (Generation, Version, Song) gains `localAudioFiles {total, handAssociated, songLevel}`, counted by the new `AudioFileLifecycle.CountAsync` from the Song's file list. `total` includes Missing files. `handAssociated` counts files the user associated whose names carry no Suno ID, or whose automatic matching is off (#210), because no scan would associate those again. `songLevel` counts files with no Generation (Song deletion only). The Song impact's existing `audioFileCount` is now the real total. `SongDeletionRules.TitleRequired` does not read it, so audio files do not change which confirmation #102 requires.
  **Why:** This follows the discretion line on the impact shape. Auto-match-blocked files are counted as hand-associated because a restore would not re-attach them either. Keeping `audioFileCount` avoids changing #102's contract.
  **Issue:** #213
- **Decision:** The Song confirmation's "deleted with it" list no longer has a "N local audio files" line. Files are now described by their own lines (absent at zero), shared by the three dialogs through `media/DeletionAudioFiles.tsx` and `deletionAudioFileRules.ts`.
  **Why:** Rule 1: the old line listed audio files among what is deleted, which is false (invariant 2). The story's component tests ask for the new line to be absent at zero.
  **Issue:** #213
- **Decision:** A move needs no `AudioFileLifecycle` call. `GenerationMoveService` is unchanged. #206's composite key `(generation_id, song_id) → generations(id, song_id)` cascades on update, so the Generation's files follow it. Its Preferred Audio File choice is keyed by Generation (#212), so it moves too. The move dialog's files line comes from the Generation's own `audioFiles.count` (#211, Missing files included) and is absent at zero.
  **Why:** This deviates from the story's key link ("GenerationMoveService → AudioFileLifecycle"). The database already performs the move in the same statement, so a service call would duplicate it. The API test proves the files, the choice, and the Song-level file's place.
  **Issue:** #213
- **Decision:** The story text has `PrepareRestore` null a preference column. That is satisfied by #212's design: separate `generation_/song_preferred_audio_files` tables, removed by `ReleaseAsync` on deletion and never retained. There is no restore code for choices.
  **Why:** This was the orchestrator's instruction. The preference columns the story assumed do not exist.
  **Issue:** #213
- **Decision:** `restore-deleted` (`DeletedItemsService`) adds `AudioFileLifecycle.RestoreNote` under "Left out or changed" when the restore put back a Song or a Generation and the library holds any audio file. The note is general and counts no files. A Version group without Generations, and a library with no local files, get no note.
  **Why:** The AC says the command's output says so. Printing it on every restore in a library without media would be noise, and existing CLI tests expect no "Left out" section for a plain Song restore.
  **Issue:** #213
- **Decision:** After a Song restore, files released with `song_deleted` keep that reason (hand-associated and Song-level ones included). After a Generation restore, an ID-less file loses `generation_deleted` at the next scan, as #206's matcher already does whether or not the Generation is restored. `auto_match_blocked` is never cleared by a delete or a restore (#210).
  **Why:** This deviates from the discretion line "After a restore, a hand-associated file with no Suno ID is plainly unmatched, with no reason". Associations are not retained (planner), so nothing records which Song released a file. Clearing every `song_deleted` would mislabel files of Songs that are still deleted. A column recording the releasing owner would be a schema change the story does not ask for (Rule 4).
  **Issue:** #213

Story #218 (built in parallel; merged into the milestone branch):

- **Decision:** Rows learn playability from a new `playback {playable, reason}` on every Generation answer, decided by `PlaybackResolver.PlayabilityOf` over the statuses the list already tallies (`AudioFileTally.Playability`); `reason` is null or `nothing_available`.
  **Why:** the discretion line asks for flags computed by the resolver on the list responses, not a call per row; the resolver's `ForGeneration` names a file exactly when one file is available, which a unit test pins.
  **Issue:** #218
- **Decision:** The Play-disabled reason text is worded on the web from the Generation's tally (no file / Missing / media folder unavailable); the server's code stays `nothing_available` (the resolver's own reason).
  **Why:** keeps one set of resolver reason codes; the tally already says which case it is. #221 widens `playable` with Suno streaming.
  **Issue:** #218
- **Decision:** One `HTMLAudioElement`, made once in `PlayerProvider` (held in a ref, appended to a hidden span) and mounted inside `SignedInShell` around the `AppShell`, so route changes never unmount it; signing out unmounts the shell and stops it. The bar is `AppShell.Footer` (height 168 px narrow, 104 px from `sm`), rendered only once something was played, so Mantine reserves the space and nothing is covered.
  **Why:** the must-have key link (above the router outlet); a footer reserves space by itself. A ref, not state, because the React Compiler lint forbids mutating a `useState` value.
  **Issue:** #218
- **Decision:** The seek bar and volume are native `<input type="range">` with `aria-valuetext`; the seek bar handles Arrow (±5 s), Page Up/Down (±30 s), Home/End and Space itself (preventDefault), the volume keeps the browser's own keys (step 5 %). Mute is a toggle button with a fixed name and `aria-pressed`. No global key handler at all.
  **Why:** native sliders give names, values, and mouse dragging for free and behave the same in jsdom; handling only keys on the bar's own controls is what keeps the space bar from ever being taken from a text field or the editor.
  **Issue:** #218
- **Decision:** On an audio `error` the provider reads the session with `fetchSession` (no interceptor); if it ended, the bar goes to paused at the stored position and an `apiFetch('api/v1/session')` raises the in-place sign-in prompt; Play after signing in reloads the src and seeks back. Otherwise the error state names the file and offers Retry (reload at the stored position) and Close.
  **Why:** the discretion lines on session end; the audio element's own requests do not pass through the shared 401 interceptor.
  **Issue:** #218
- **Decision:** Cross-tab pause over `BroadcastChannel('n8tracks-player')` (a `play` posts, other tabs pause); Media Session gets `MediaMetadata({title})` and play/pause handlers only. Volume and mute in `localStorage` key `n8tracks.player.volume`.
  **Why:** discretion lines; both are feature-detected so old browsers and jsdom are unaffected.
  **Issue:** #218
- **Decision:** Play controls: a first (unlabelled "Play") column in the Versions table's Generation rows, the Audio Files section, and Unmatched Files; beside the shortcode in the Generation panel; beside the file name in the panel's file list. The two Unmatched Files component tests that listed cells/buttons by position were updated.
  **Why:** AC 1 names every one of these; putting it first keeps it visible without scrolling the wide tables.
  **Issue:** #218

Story #219 (on the milestone branch):

- **Decision:** The Song's state is one resolver function, `PlaybackResolver.StateOfSong(facts, selected, hasGenerations)` → `SongPlayability(state, reason)`: `ready` (preferred Song-level file available, or the Selected Generation has an available file), `selected-unplayable` (`nothing_available`), `needs-choice` (`no_selected_generation`: no selection, and any Song-level file is available or any Generation is playable by `PlayabilityOf`), else `none` (`no_generations` without Generations, `nothing_available` with them). `ChoiceForSong` builds the playback answer from it and from `ForSong`, and a unit table asserts they agree (ready exactly when `ForSong` names a file).
  **Why:** the must-have truth "never picks a Generation by itself" and the #212 note "never re-derive": lists and the playback read use the same function. `no_generations` is a new reason code so the disabled reason can say "no Generations and no audio files" (AC 5).
  **Issue:** #219
- **Decision:** `GET /songs/{ref}/playback` keeps #212's fields and adds `state`, `candidates` (filled only for `needs-choice`, otherwise `[]`) and `generation.sunoId`. When nothing plays, `reason` is the state's reason (so a Song with no Generations answers `no_generations`, not `no_selected_generation`). Candidates: available Song-level files first (format rank, first seen, path), then Generations that are Active and still listed by Suno, then the rest (Archived, Trashed, Remote Missing), each group in Version tree order then ordinal; each Generation candidate carries `versionNumber`, `rating`, `durationSeconds` (Suno's), `state`, `remoteState`, `playable`, `reason`.
  **Why:** the discretion lines on ordering and fields. Archived and Trash/Remote Missing are one trailing group, with a badge per state, since the lines put both "after Active ones". Generation playability comes from the Song's files through `PlayabilityOf`, so it matches the Versions table's Play.
  **Issue:** #219
- **Decision:** Song answers (list and detail), Album tracks and Playlist songs carry `playback {state, reason}`, from `Infrastructure/Persistence/SongPlaybackRows.StatesAsync`. That is one batched read per list (the Songs' files and status, their Song-level choices, which Songs have Generations, and the mount state), fed to `StateOfSong`. `SongSummary`, `AlbumTrack` and `PlaylistSong` gained an init `Playback` (default none/no_generations). No migration.
  **Why:** "Song list rows and the Song detail carry playback" (only `none` disables Play), with no per-row request. Album and Playlist rows are Song rows of their own lists, and AC 5 applies there too.
  **Issue:** #219
- **Decision:** Web: `player/SongPlayButton.tsx` and `player/ChooseGenerationDialog.tsx`; `PlayerProvider` gains `playSong(song, onChoice)` (GET the Song's playback; `needs-choice` hands the answer to the button, which opens the chooser) and `playChosen(song, candidate, {selected?, notice?})` (a Generation pick goes through `GET /generations/{id}/playback`, the one rule; a file pick plays that file). `NowPlaying` gained `songId` and `via` (`song-preferred` | `selected-generation` | `chosen`), shown in the bar as `player-via` ("The Song's choice: its Song-level file / its Selected Generation", "Chosen for this listen"). The Song's Play pauses and resumes while `songId` matches and `via` is set, and never asks again. After Close, the next Play asks again.
  **Why:** the key link (a `needs-choice` answer opens the chooser, any other answer goes to the player) and #218's note to add `playSong` beside `playGeneration`. Generation rows mirror through `generationId`.
  **Issue:** #219
- **Decision:** Ticking "Make this the Selected Generation" PUTs the #120 selection with the Song's revision: the page's own on the Song page (then `setSong`), or one read just before for Songs-table, Album and Playlist rows, which carry no revision. On success the bar says "its Selected Generation". On a conflict or a failure the pick still plays "for this listen", and the bar's notice says it was not selected.
  **Why:** the discretion line says the selection is sent with the Song's revision, a conflict is reported, and playback still starts. List rows have no revision to send.
  **Issue:** #219
- **Decision:** `selected-unplayable` puts a notice in the bar ("Nothing to play: <title>'s Selected Generation, <shortcode>, has no local audio file that can play.") with an Open in Suno link when the Generation has a Suno ID (new `PlayerState.noticeLink`). What was playing is left alone. `none` from a stale row says why in the bar.
  **Why:** the discretion line "the bar shows that the Selected Generation has no audio, with the reason and Open in Suno where there is a link".
  **Issue:** #219
- **Decision:** Placement: a first, unlabelled "Play" column in the Songs table (like #218's tables); in the Song header after the shortcode badge (a new `play` slot on `SongHeader`); first in the action group of each Album track and Playlist row. The chooser is a Mantine Modal kept mounted (`opened` toggles), so it records the Play control and gives focus back to it.
  **Why:** AC 1, and Mantine's focus return only records the trigger when `opened` changes, which a Modal mounted already open never does. The Songs-table unit and e2e tests that read cells by position were shifted by one.
  **Issue:** #219
- **Decision:** `GET /api/v1/songs/{reference}/playback-sources` (`catalog.read`, reads only, in `PlaybackEndpoints`) answers `{song{id,shortcode,title}, songFiles[{audioFile,isPlaybackFile}], generations[{generation{id,shortcode}, versionNumber, rating, revision, durationSeconds, state, remoteState, files[{audioFile,isPlaybackFile}]}]}`, built by the new `PlaybackResolver.SourcesForSong` through `PlaybackService.SourcesForSongAsync`. Only available files are listed: Missing and also Unavailable ones are left out, and so is a Generation with nothing available. Song-level files list the Song's preferred one first, then the rest in rank order. A Generation lists its `ForGeneration` file first, then its other available files in rank order. Generations of every state are included, in tree order. `SongPlaybackGeneration` gained an init `Revision`. There is no "Version label" beyond the number, so the web shows "Version <number>".
  **Why:** the key link and the discretion lines ("Missing files are omitted", nested by Generation with rating, revision, duration, Version label and a playback-file marker). Unavailable files cannot be served (#217) either. Putting the preferred Song-level file first mirrors "each Generation's playback file first".
  **Issue:** #220
- **Decision:** `PlayerProvider.switchTo(next, {keepTime})` loads the new source and seeks on `loadedmetadata`. It then waits for `seeked`, and only then plays, if the switch should play, and lets the source into the A/B pair. A pending switch (`PendingSwitch {ticket, from, at, play, back}`) is replaced by a later one, which carries the earlier one's `from`, `at` and play state. Pause, Play and the seek bar act on the pending switch. On an error the player goes back once to `from` at `at`, with the notice "<new> could not be played, so the player went back to <old>.". If going back fails too, it is the ordinary error with Retry. Any source that loads any other way also joins the pair, on `loadedmetadata`. `PlayerState.previous` is the other half of the pair (`compare.nextPair`), forgotten when the Song changes or on Close.
  **Why:** the discretion lines on seek-before-play, last request wins, failed or superseded switches never entering A/B, the pair "however it was reached", and returning to the previous source. The fake audio element now fires `seeked` on every `currentTime` set and has `failNextSeek()`. No existing test relied on its absence.
  **Issue:** #220
- **Decision:** The compare controls are a new row of the player bar (`player/CompareMenu.tsx`, shown only while the loaded source has a Song): a Mantine Menu "Compare" (read again on open and when the Song changes; group labels "Version N · shortcode · N stars/not rated"; the playing entry is marked with `aria-current` and "(playing now)"), Previous, Next, A/B, and a compact `StarRating` ("Rating of <shortcode>"). Each control has `aria-keyshortcuts`, and a visually hidden description names its shortcut. The footer height went to base 216 / sm 140. When a Generation has two files of the playing format, the bar adds "File: <name>".
  **Why:** the ACs and the artifact path. The bar must always name what plays, and the existing detail line does not tell two files of one format apart.
  **Issue:** #220
- **Decision:** The shortcuts match `KeyboardEvent.code` (`BracketLeft`, `BracketRight`, `Backslash`) on `document`. A press does nothing with auto-repeat, with any of Ctrl, Alt, Meta or Shift held, when `defaultPrevented`, or when focus is in a text-like input, a textarea, a select, a contenteditable, a `role=textbox` or a `.cm-editor`.
  **Why:** "ignore modifiers and auto-repeat" was read as: a modified press is not a shortcut. That keeps browser and OS shortcuts like Cmd+[ (Back) working.
  **Issue:** #220
- **Decision:** A switch's `via` is `chosen` when what was loaded came from a Song's Play, and null after a Generation's or a file's own Play. A/B going back to a source restores that source's own `NowPlaying`, its `via` included.
  **Why:** after a switch, "The Song's choice" would no longer be true. #219's note says `via` tells a Song's Play from a Generation's.
  **Issue:** #220
- **Decision:** The bar's rating writes `PATCH /generations/{id}` with the revision from the sources answer. A conflict is retried once with the current revision. A failure restores the old value and says so (`player-rating-problem`). A saved rating, from the bar or the Song page (`useRateGeneration`), is announced as the window event `n8tracks:generation-rated` (`generations/ratingEvents.ts`). The Song page's Generations and the bar's copy update from it, but only when the revision is newer.
  **Why:** rating "usable without stopping playback" while the Song page shows the same Generation. Without the event, either copy would show a stale rating and send a stale revision.
  **Issue:** #220
- **Decision:** The server-side Suno host list the story places "beside the image hosts" does not exist: the image hosts are kept only in the extension (`SUNO_IMAGE_HOSTS`). The new `Domain/Suno/SunoAudioHosts` is the first server-side Suno host list, and it holds audio hosts only. No server image list was made, since the server never needs one.
  **Why:** orchestrator decision ("adjust inline"). The server has no use for image hosts, so creating a list just to sit beside would add dead code.
  **Issue:** #221
- **Decision:** `SunoAudioHosts.Hosts` = `d2lwuy8qc234o3.cloudfront.net` only, the playback host seen in the fixtures' `media_urls` and confirmed by TS-004. Two hosts are left out. `studio-api.prod.suno.com` is excluded by the discretion line, and its `audio_url` answers `/api/forbidden`. `suno-data-uploads.s3.amazonaws.com`, which the adapter also lists, is left out because it serves only the signed one-hour `download_url`s that the extension's downloader uses and the server never stores. So the CSP allows exactly the server list, which is a subset of the adapter's `SUNO_AUDIO_HOSTS`, rather than every adapter host. An architecture test checks the subset, and also that the host is named in one source file only.
  **Why:** the constraint that the allowed hosts be explicit and narrow, with no wildcard. AC 9 ("exactly the Suno audio hosts the adapter lists") is read as the adapter's audio hosts that a Generation's stored address can be on.
  **Issue:** #221
- **Decision:** An address counts as a Suno stream only if it is absolute HTTPS on the default port, has no user info, and is on a listed host (case-insensitive). Anything else is no address (`nothing_available`), as the discretion line says. `SunoStream.Of` tests these in this order:
  1. No Suno ID: none.
  2. Remote state not `present`: `suno_not_present`.
  3. Provider status not `complete` (ordinal): `suno_not_complete`.
  4. Address not playable: none.

  Three reason codes are new: `suno_stream`, `suno_not_complete` and `suno_not_present`. Generation playability is `{playable: true, reason: "suno_stream"}` when it streams.
  **Why:** "Play is disabled with the reason" needs a reason for each case. A trashed clip names its trash state even when its address is also gone.
  **Issue:** #221
- **Decision:** The resolver's Suno branch is optional parameters and init properties, so every existing call and test is unchanged:
  - `ForGeneration(files, SunoStream?)`, `PlayabilityOf(statuses, SunoStream?)`, `WithStream(local, stream)` and `ForSong(files, selected, SunoStream?)`.
  - `StateOfSong(..., IReadOnlyDictionary<Guid, SunoStream>?)`: a stream-only Generation with no files counts for needs-choice.
  - `SongPlaybackGeneration.Stream`, `GenerationPlayback`/`SongPlayback.SunoAudioUrl` and `PlaybackSourceGeneration.SunoAudioUrl`. A stream-only Generation is listed with `files: []`.

  `PlaybackService.ForGenerationAsync` now takes the stream, and the endpoint passes `SunoStream.Of(generation)`. The Generation summaries (`GenerationStore`) and `SongPlaybackRows` read the stored address, status and remote state in the same batched reads.
  **Why:** the m5-notes for #218, #219 and #220. The one rule keeps a local file first: a Song follows only its Selected Generation to Suno ("Selected one, from Suno").
  **Issue:** #221
- **Decision:** The playback answers gained flat `sunoAudioUrl` (non-null only for `source: "suno"`) and `sunoPageUrl` (the clip's `https://suno.com/song/<id>` whenever there is a Suno ID, for Open in Suno on any error). The Song's answer uses its Selected Generation's. The playback sources' Generations gained both fields as well. The web parsers read an absent field as null, so older fakes still parse.
  **Why:** AC 1 ("source `suno` with the address and the Suno page link") and AC 3/complement: a local file's error offers Open in Suno when there is a page.
  **Issue:** #221
- **Decision:** `ClipReader` now stores the first `media_urls` entry by kind, MP3 then M4A (incl. `m4a-opus`) then OGG, each matched by `content_type` containing the kind or by the URL's path (without query) ending in `.kind`. With no match it falls back to `audio_url`. The data-only migration `20261008060000_RederiveGenerationAudioUrls` re-derives `generations.audio_url` from `provider_records.payload` in SQL (JSON1) with the same rule. It has no schema change, its Designer is a copy of the current snapshot, and Down is a no-op. A theory runs the migration's SQL against nine payloads (the fixtures plus edge cases) and checks it against `ClipReader`. Retained (deleted) Generations keep the address they had.
  **Why:** the discretion lines. Changing an existing row's address needs the raw clip, and an EF migration runs SQL only. The rule is short enough to state in SQL and prove equal. A sync right after the migration proposes nothing Changed, because the audio address is not a compared field (`SunoExportRules.ChangedFields`, tested). Retained documents are a different shape and are restored as they were, and the next sync of the clip refreshes the address.
  **Issue:** #221
- **Decision:** On the browser policies:
  - **CSP:** every response carries `Content-Security-Policy: media-src 'self' https://d2lwuy8qc234o3.cloudfront.net` through `Api/Frontend/BrowserPolicyMiddleware`, right after the request ID. That is the one directive the story asks for.
  - **Referrer:** the same middleware also sends `Referrer-Policy: strict-origin-when-cross-origin`, the browsers' default made explicit. The audio element sets `referrerpolicy="no-referrer"` and no `crossorigin`. Browsers do not honour `referrerpolicy` on media elements yet, so the header is what guarantees the request to Suno carries no path.
  **Why:** the CSP is AC 9 and the discretion line ("only a `media-src` directive"). The referrer header is AC 6 ("no referrer path"). The e2e test checks the request's `Referer` path and that it carries no cookie.
  **Issue:** #221
- **Decision:** Player (web):
  - **Stream source:** `NowPlaying` gained optional `source` (`local` when absent) and `sunoPageUrl`, so existing hand-built fixtures stay valid. A stream's `fileId` is `suno:<generationId>`, and its label is the Generation with format `''`. The detail line drops the format, and the new `SourceBadge` (`player-source`, `data-source`) says "Local file · WAV" or "Streaming from Suno".
  - **Watchdog:** each play request arms a 15 s timer for a `suno` source. `playing` disarms it. A `waiting` after the stream started arms 30 s. Pause, end, close and a new load disarm it. On timeout or element error, a Suno stream goes to the error state at once with no session check, and its `src` is removed so Suno is not asked again. The alert reads "The Suno audio of <shortcode> could not be played. Suno may have moved it: sync with Suno again to refresh its address." There is no Retry. Play in the bar asks again.
  - **Local errors:** keep Retry, and add Open in Suno only when `sunoPageUrl` is set.
  - **Sync hint:** the sync suggestion is always shown for a `suno` error, because a Generation Suno reports as trashed never streams (`suno_not_present`).
  **Why:** the discretion lines: 15 s to the first `playing`, 30 s stall, no retry, no fall-over, and the sync sentence not shown for trashed.
  **Issue:** #221
- **Decision:** Compare lists a stream-only Generation as one stop: key `suno:<id>`, named "<shortcode> · Suno stream", `source: 'suno'` and `data-source="suno"` on the menu item. Switching to it plays Suno's address. A Generation that has a file never lists its stream.
  **Why:** AC 8 and the #220 note. "A local file always wins" applies within each Generation.
  **Issue:** #221
- **Decision:** Rule 2: `audiourl` was added to `RedactionPolicy.SensitiveNames`, which also covers `sunoAudioUrl` and `audio_url`.
  **Why:** invariant 6. Nothing logs the address today, but a Suno audio address can carry a signature, so any future log property under these names is masked by default.
  **Issue:** #221
- **Decision:** The Suno audio host list is now a setting, `N8TRACKS_SUNO_AUDIO_HOSTS`, a comma-separated list of bare DNS host names whose default is exactly `d2lwuy8qc234o3.cloudfront.net`. It is read and validated at startup with the other `N8TRACKS_*` variables (`EnvironmentOptionsLoader` into `N8TracksOptions.SunoAudioHosts`). A value with no host, or a name with a scheme, user info, port, path, wildcard, or anything that is not a DNS host name (an IP address included), stops startup with one `Invalid configuration` line naming the variable. `SunoAudioHosts` became an instance (`Default`, `Parse`, `Playable`) registered from the options, and every reader takes the configured one: the CSP `media-src` (`BrowserPolicyMiddleware`), `SunoStream.Of`, and the stores and services that compute playability (`GenerationStore`/`GenerationRows`, `VersionStore`, `SongStore`, `AlbumStore`/`AlbumTrackStore`, `PlaylistStore`, `SongPlaybackRows`, `PlaybackService`, `PlaybackEndpoints`). The architecture test now checks that the *default* is a subset of the adapter's `SUNO_AUDIO_HOSTS`; an operator's list is outside that check. The name follows the project's `N8TRACKS_*` environment-variable pattern rather than a `Suno:AudioHosts` section, since the app reads no other configuration source. Commands run with `LoadDataPathOnly` and similar loaders keep the default list, as they keep every other default.
  **Why:** owner decision on AC 9 (2026-10-07): "CDN only for now, but make that URL easily configurable in case it changes."
  **Issue:** #221

## Ad-hoc — 2026-10-07

- **Change:** Invariant 3 in `CLAUDE.md` amended. Imports still never change existing catalog data without an explicit user choice, but it now names the one exception: Suno's own state may follow Suno without a choice, in two cases. #154 completes a Generation the user just created (an observed Create, never complete, not decided by a reviewed export), in its clip columns and raw clip only. #314 moves a Generation still generating to Suno's final status (complete or error) at a commit whatever its choice, Skip included, in that column only. Portable import has no exception. The guard reference (#140, merged) now names the rules that bound the exceptions: `TakesSunosFinalStatus` and the completion rule `CompletionUnexplained` in `ImportNeverOverwritesGuardTests`.
  **Why:** Owner decision ("Amend the wording"). Verification of #140 found the invariant's text no longer matched #154 and #314 behaviour: both change existing Generations with no import choice, and the guard already allowed exactly those changes.
  **Affects:** M8 #22 (epic AC: "a guard test proves invariant 3 for this path"), #263 (portable-import guard of invariant 3; its `CLAUDE.md` AC must keep the "Portable import has no exception" wording), #260 (conflict decisions, "never offers to overwrite") — plans may be stale. M6 and M7 open issues have no invariant 3 dependency (the "overwrite" matches in #227 and #243 are about saved views and revision checks).

## Ad-hoc — 2026-10-07

- **Change:** Artwork uploads are capped at 100,000,000 pixels (`ArtworkRules.MaximumPixels`, #305). An image over it is refused from its header, before any pixel buffer is allocated, as 422 `artwork_dimensions_exceeded` with the title "The image is W × H pixels; artwork can be at most 100 megapixels (100,000,000 pixels)." and a new `maximumPixels` extension beside `maximumSide`. The 12,000-pixel side limit is kept: it still bounds strips the pixel cap allows (100,000 × 1,000 is 100 MP) and keeps #97 AC's wording true. The 512 MiB decode memory cap and its eighth-step scaled decode are kept as a second bound, though no accepted image reaches them now (100 MP at 4 bytes is 400,000,000 bytes). This narrows #97's decision that every JPEG and WebP within 12,000 a side is accepted by scaled decoding: those over 100 MP are now refused.
  **Why:** Owner decision ("~100 MP") on #305: a 470 KB PNG could decode to ~484 MB, too much for a home-server memory profile.
  **Affects:** M3 #97 (closed; its refusal now also covers over 100 MP); M7 artwork upload through the API and MCP (`artwork.write`) inherits the same limit — no open plan names a pixel limit.

## /n8-exec M5 fix pass — 2026-10-08

- **Decision:** #385 is a test only: `AudioFileAssociationTests.AChangedFileKeepsItsAssociationItsOriginAndItsPreferenceOnTheNextScan` changes a hand-associated Song-level file and a Suno-ID file twice (bytes and mtime), first with no preferred choice and then with each its owner's preferred file, and asserts song, generation, origin, preference and reason after each rescan.
  **Why:** With a preferred choice in place the database already refuses an association change (the composite keys of #212), so the bite would only fail the scan; the first round, without a choice, is what proves the scan's Changed path keeps the association.
  **Issue:** #385

- **Decision:** #389: a second SQL function, `n8_lower(text)` (`string.ToLowerInvariant`), registered on every connection beside `n8_title_key` and mapped in the EF model as `N8TracksDbContext.Lower`; `DownloadRecordStore.AudioFilesNamedLikeAsync` filters on `n8_lower(file_name) LIKE <lowered stem>%` with the same escape, and lowers the stems itself.
  **Why:** SQLite's `LIKE` and `lower()` fold ASCII only. Lowering both sides with the same .NET rule the service's exact comparison uses keeps the prefilter a superset of the exact match, and the escaped `_` and `%` stay literal. Rule 1 fix with regression cases (Latin with a diacritic, Cyrillic, Greek) in `DownloadRecordTests`.
  **Issue:** #389

- **Decision:** #390: `Media/StreamAddressLoggingTests` stores a stream address on the listed host whose query carries a fresh signature, requests every answer that reads a stored address (Generation and Song playback, with and without a Selected Generation; playback-sources; the Generation, Song, Songs, Song's Generations, Album and Playlist answers) with the log at Trace and telemetry exported to the stub collector, and asserts the signature is in no captured line and in nothing the collector received. It joins `TelemetryCollection`, as the other collector tests do.
  **Why:** Redaction keys on property names (`audiourl`); a value-based check is what fails when the address is logged under any other name. The playback answers are asserted to carry the address, so the absence is not vacuous.
  **Issue:** #390

- **Decision:** #388: a new table `retention_released_audio_files (group_id → retention_groups ON DELETE CASCADE, audio_file_id, reason)` (migration `20261008070000_AddRetentionReleasedAudioFiles`, now the latest), written by `AudioFileLifecycle.RememberAsync` right after each Song, Version, or Generation deletion creates its group, in the same transaction. A restore clears the reason through a new `IRetentionRestoreParticipant` port that `RetentionService.RestoreWithinAsync` runs inside the restore's transaction, before the group goes; `AudioFileLifecycle` is the one participant. It clears `unmatched_reason` only where the file is still unassociated and still has the reason that deletion gave it, and raises the file's revision; `auto_match_blocked` is never touched.
  **Why:** The fix brief names this design ("record the released file IDs in the deletion's retention group"); a new table is the only place to keep them without changing `retention_groups`' shape, and it cascades with the group on restore and prune. The hook in `RetentionService` covers every restore path (the `restore-deleted` command and the Suno Reimport restore of #140) without each caller remembering it. Supersedes the #213 deviation that left `song_deleted` on a restored Song's files. Generation and Version restores now clear `generation_deleted` at once too, where before only the next scan did (for files with no Suno ID in their name). Classified catalog in `SunoExportStagingGuardTests`; schema asserted in `DatabaseStartupTests`.
  **Issue:** #388

- **Decision:** #386: `MediaMountAccessTests` reads each file's code as a whole for the file-system, write, and native-code rules, so a split call is seen; a `System.IO` file-system type named in full counts as a file-system use (aliases, `using static`); inside the reader, `FileMode`/`FileAccess`/`FileShare`/`FileOptions` may appear only as their named reading members (casts, parses, and arithmetic fail), and no `System.IO` alias or static import; nothing under `src/` may P/Invoke, load a native library, use function pointers, or use `dynamic`.
  **Why:** Each form the verifier showed passing now fails a rule, with a self-test in both directions. A regex scan stays the technique (the project's other source guards use it) rather than a Roslyn semantic check, which would add an analyzer dependency to the architecture tests.
  **Issue:** #386
- **Decision:** #386: `FileStream.Name` is closed structurally: `MediaMountReader` hands out a private read-only `Stream` over its `FileStream` (no `Name`, no handle, no write, no `SetLength`), from both `OpenRead` and `OpenWithStat`; a runtime test asserts it, and another that an open of a cataloged file that vanished fails, over HTTP too, and creates nothing.
  **Why:** No text rule can tell a `FileStream.Name` from the reader's legitimate `entry.Name`; taking the `FileStream` out of callers' reach removes the leak whatever the code says, and the vanished-file test stands behind any open mode a scan misses (a mode held in a variable and changed there, which stays in the "Not covered" list).
  **Issue:** #386

- **Decision:** #384: the AC4 guard is now structural, in `MediaMountAccessTests`: (1) the exact list of every call that hands `IMediaMount` a path (`List`, `Stat`, `OpenRead`, `OpenWithStat` with an argument), found in every user of the mount whatever the receiver or argument is called, with its argument text (`mount.OpenWithStat(file.Path)` in the content service, three listing-derived calls in the scan); (2) by reflection, the exact non-private methods of every type built with an `IMediaMount` (but the reader), none of which may take a `string` or `Uri`. `NoEndpointTakesAPath` stays as the name check.
  **Why:** A route value can reach the mount only through a type that holds it; pinning both what those types accept and every call that passes a path means a new route-to-mount flow fails whatever its parameter is named. Reflection over the endpoints alone could not see into a service; a data-flow analysis would need Roslyn.
  **Issue:** #384

- **Decision:** #387: two startup checks. `docker/entrypoint.sh` runs `refuse_overlaps` first, on both the root and the `--user` paths, before any `chown`: `/data`, `/backup`, and any `N8TRACKS_DATA_PATH`/`N8TRACKS_BACKUP_PATH` against the media folder, in both directions, by `stat -L` device:inode, by `readlink -f`, and by `/proc/self/mountinfo` (device plus the path within that filesystem); an overlap is one Error line and exit 1. The app's options loader refuses `N8TRACKS_DATA_PATH` or `N8TRACKS_BACKUP_PATH` that is the media folder or inside it (real paths, plus the mount table on Linux) through `Application.Configuration.MediaFolderOverlap`, before the data path's write probe.
  **Why:** The entrypoint is the one place that runs before `chown -R`, and `chown -R` of a folder holding the media would re-own it, so the container refuses both directions. The app refuses only "inside the media folder": it writes only its own named files in the data and backup folders, so a media folder inside the data folder (the AppHost's own `.localdata/media` layout) is safe and stays allowed. The mount table is what sees one host folder mounted twice, which no path comparison inside a container can (verified on Docker Desktop: virtiofs bind mounts share a device and carry the host path as their root). The check lives in Application, not Infrastructure, because `LayeringTests` keeps Api types outside the composition root off Infrastructure; it repeats the reader's real-path walk rather than share it, so `MediaMountReader` stays the only code that resolves paths for reading the mount.
  **Issue:** #387
- **Decision:** #387: CLAUDE.md invariant 2's guard text was rewritten in place (that line only; the invariant itself is unchanged): `MediaEndpoints` named as a reader of the setting, the structural guard of #384, the forms of #386, the startup checks, and in "Neither covers" what stays open: shell code and the Dockerfile, which no scan reads; a `FileMode`/`FileAccess` value changed in a variable; reflection into the reader's stream; overlaps the checks cannot see (two network shares of one remote folder, a mount added to a running container); and `NoEndpointTakesAPath` itself being name-based.
  **Why:** The issue's AC; the forms the fix closed are listed as covered, not as gaps.
  **Issue:** #387

## Ad-hoc — 2026-10-08

- **Change:** M5 fix pass. (1) New table `retention_released_audio_files` (migration `20261008070000_AddRetentionReleasedAudioFiles`, classified catalog), and a new retention port `IRetentionRestoreParticipant` run inside every restore (#388). (2) The container now refuses to start when the media folder overlaps `/data` or `/backup`, and the app refuses a data or backup folder inside the media folder (#387). (3) CLAUDE.md invariant 2's guard text names the strengthened guards (#384, #386, #387); the invariant itself is unchanged.
  **Why:** Fixes of verification bugs #384–#390 on `milestone/m5-fixes`.
  **Affects:** M8 portable export/import (#22 epic, #263): the table lists they plan over now include `retention_released_audio_files`; #259 (invariant 2 extended to imported audio paths): its guard text should build on the new structural lists in `MediaMountAccessTests` (`MountPathCalls`, `MountHolderMethods`) rather than the name check; any later restore path inherits the participant through `RetentionService`.

## /n8-exec M6 — 2026-10-08

- **Decision:** #223: writes are collected by SQLite triggers, not by EF change tracking. Each table holding indexed text, or saying which Songs a Tag, Album, or Playlist is on, gets triggers (30 in all, `N8TracksDbContext.SearchTables`/`SearchTriggers`) that only `INSERT OR IGNORE` the Song's ID into `search_dirty_songs`. The "save hook on the unit of work" is `ExclusiveTransaction.RunAsync`, which calls `SearchIndexer.FlushAsync` just before `CommitAsync` and re-indexes those Songs in the same transaction. A search that finds Songs still pending (a write made outside the unit of work, such as direct SQL) runs an empty unit of work first, so it indexes them before it reads.
  **Why:** Many writes are set-based or raw SQL: `ExecuteUpdate`/`ExecuteDelete` on Tags, Albums, and Playlists, `RetentionStore` deletion and restore, the import commit, and Generation moves. The EF change tracker sees none of them. Triggers see every path, so no service can forget, and the explicit `ReindexSongs` calls the planner listed for set-based changes are unnecessary. The freshness tests prove each kind of write. The triggers are added with `CREATE TRIGGER`, so no table is rebuilt. `DatabaseStartupTests` asserts the exact list, so a later table rebuild that drops them fails.
  **Issue:** #223
- **Decision:** #223: the FTS5 table `search_index` has a regular companion table, `search_rows (row_id, song_id)`, with an index on `song_id`. A Song's rows are replaced by rowid through that map. A rebuild fills `search_index_next` and `search_rows_next`, then one transaction drops the old pair and renames the new pair over it.
  **Why:** FTS5 cannot index an `UNINDEXED` column. Without the map, deleting a Song's rows on every write would scan the whole index.
  **Issue:** #223
- **Decision:** #223: the index format version lives in the `settings` row `search.index` = `{"version":n}`, and a missing row means version 1. The migration writes no row. `SearchIndexRebuild.CurrentVersion` is 1. At startup, after the app is serving, the app queues a rebuild when the stored version differs or when the index is empty while Songs exist (the case for an upgraded catalog). It also drops a next index that no running job owns. A rebuild writes the row only when the version changes.
  **Why:** A new instance needs no settings row, so tests that assert the exact settings rows stay valid, and the staging guard's catalog snapshot does not depend on when startup ran. Raising the constant, or a migration writing a lower version, still forces a rebuild, which is what the discretion line asks for.
  **Issue:** #223
- **Decision:** #223: the field mapping. Lyrics are the lyrics plus a Speech's script. Styles are the styles, the excluded styles, and a Speech's tone. Prompts are the Simple prompt, the Speech prompt, and the Sound description. The model is Suno's label, name, and version, distinct values joined into one row. Suno tags get one row per comma-separated value. A Version's label is `v<number>`, and a Generation's is `v<number>-g<ordinal>`; the reference for both is the shortcode, and for an Album or Playlist it is the ID. Not indexed: the Song's notes, release fields, Genres, and credits; Album and Playlist descriptions; the Version's model and other list choices; the Suno title input. `SearchFieldTests` walks every text property of the entity model, and each one is either indexed or excused with a reason.
  **Why:** The AC lists the fields and the API's `field` values, and none of these is on that list. Text a person wrote into a creation input (script, tone, excluded styles, prompts) is found under the nearest listed field. Song notes are a gap the AC leaves; adding a `notes` field value would change the contract that #224 builds on, so it is left for a replan if wanted.
  **Issue:** #223
- **Decision:** #223: query details. A term is a word split on whitespace or a double-quoted phrase; an unbalanced quote counts as a space. Each term becomes one FTS5 string, so no input can be an operator. `*` is appended when the term's last run of letters and digits has two characters or more. A term without a letter or digit is dropped. A complete Song, Version, or Generation shortcode matches as an exact phrase, and a shortcode row counts as a match only when the query names it exactly. Matches are ordered by tier, then `bm25`, then field order, and the top three are returned. With `search` and no `sort`, the list sorts by the new `SongSort.Relevance`; `direction=asc` reverses it. Sending `search` and `q` together is 400 `invalid_request`. `indexRebuilding` is sent whenever `search` is sent. `matches` and `matchCount` are sent only when the query has a term. The rebuild endpoint answers 202 for a new job and 200 for one already running, as the media scan endpoint does.
  **Why:** These follow the discretion lines. Where the lines leave room, the choices follow the nearest existing pattern: the media scan endpoint's start semantics and the list endpoint's 400 for parameters that are not understood.
  **Issue:** #223
- **Decision:** #223: the search tables are classified as not catalog in `SunoExportStagingGuardTests.OtherTables`: `search_index` with its five FTS5 shadow tables, `search_rows`, and `search_dirty_songs`. The services stay outside the invariant-1 catalog namespaces: `Application.Search` takes only IDs, text, and its own records. The rebuild endpoint is excused in the invariant-1 guard ("writes only the search index tables and the settings row"). `search` joins `RedactionPolicy.SensitiveNames`.
  **Why:** The index is derived data, rewritten in the same transaction as the catalog and rebuilt from it. It is never read as the catalog, so an import changing it is not an overwrite. Keeping catalog types out of the services' public surface keeps them out of the guard's namespaces without weakening the guard's complement test.
  **Issue:** #223

Story #237 (built in parallel; merged into the milestone branch):

- **Decision:** The job worker's heartbeat is beaten by the worker's own wake-ups: every poll (1 s) and every progress tick of a running job (1 s), not by an independent timer.
  **Why:** The story wants both "keeps ticking during a long job" and "stopped reporting but not exited" to mean something. An independent timer would keep beating while the loop is stuck in a call, so `stalled` for a lost heartbeat could never happen; the progress tick already wakes the worker every second during a job, so a long job keeps beating (tested at three times a shortened window).
  **Issue:** #237
- **Decision:** Heartbeat age and the 60-second starting window use the monotonic clock (`TimeProvider.GetTimestamp`); a queued job's wait uses the wall clock (`GetUtcNow`) against `jobs.created_utc` and the moment the worker last became idle.
  **Why:** Wall-clock jumps must not fake a lost heartbeat; the queue's ages are stored as wall-clock times. It also lets a test clock age the queue by an hour without touching liveness.
  **Issue:** #237
- **Decision:** Only an exception that escapes the loop counts as a fault (restart after 5 s; three within a minute → the worker exits, process alive, `jobs` unhealthy `stopped`). A failed claim stays "logged, tried again at the next poll" as before.
  **Why:** Counting claim failures would turn a minute of SQLITE_BUSY or a briefly unreachable database into a permanently stopped worker. A worker that keeps failing claims while a job waits shows as `stalled` after 10 minutes instead.
  **Issue:** #237
- **Decision:** `JobWorkerOptions` gains `RestartDelay`, `FaultLimit`, `FaultWindow`, `HeartbeatLostAfter`, `FirstHeartbeatWithin`, and a test seam `BeforePoll` (null in the app), following the `BackupTestHooks` precedent. `JobWorkerOptions` is now also `TryAdd`ed in `AddInfrastructure`, since the health check reads its thresholds.
  **Why:** The loop has no realistic fault a test can provoke without a seam; tests shorten the 30 s / 5 s windows.
  **Issue:** #237
- **Decision:** During maintenance neither new check opens the database: `migrations` reports the last result found (re-read on the first call after maintenance), `jobs` reports from the heartbeat (`idle` when idle, since nothing is claimed in maintenance by design). The overall status caps an unhealthy `migrations` at `degraded` during maintenance (#73); a stopped worker stays unhealthy.
  **Why:** `HealthService` already avoids opening a database a restore may be replacing; the discretion line says the overall status follows #73 during maintenance.
  **Issue:** #237
- **Decision:** When the database check fails, `migrations` is `unhealthy`/`unknown` (it was the startup-captured `up to date`) and `jobs` is `degraded`/`unknown` without trying the queue; three existing health tests were updated for this (and the log-once test now expects four components to fail and recover).
  **Why:** Discretion: "If the database cannot be reached, migrations keeps today's `unknown`" and "If the queue cannot be read, jobs is degraded with detail unknown".
  **Issue:** #237
- **Decision:** Healthy `jobs` details are `idle` and `running`; the web System page labels the component "Background jobs" (between Media library and Maintenance), and the `shell`/`account` e2e specs now expect six rows.
  **Why:** The AC asks for a one-word detail; the page needs a label for the new key or it shows the raw `jobs`.
  **Issue:** #237
- **Decision:** The `--healthcheck` AC is tested by chaining: the test host's `/health` answers 503 for `stopped` and 200 for `stalled`, and the command exits 1/0 against a stub answering those codes.
  **Why:** `Program.RunAsync` (the real process) cannot have its worker faulted or its claims held; the command depends only on the status code.
  **Issue:** #237

Story #224:

- **Decision:** #224 adds `GET /api/v1/songs/{reference}/matches?search=` (`catalog.read`), which answers `{matches, matchCount}` with up to 50 matches, best first. It is served by `SongSearchService.MatchesOfAsync`, which runs the whole search with a limit of 50 per Song and keeps that Song's matches. `Rank` takes the limit as an optional parameter.
  - A missing or repeated `search` is 400 `invalid_request`.
  - Text with no searchable word, or a Song that does not match, answers `{"matches":[],"matchCount":0}`. An unknown Song is 404, and a deleted one is `song_deleted`.
  - The route is listed in the scope and reference guards.
  **Why:** The discretion line expands "n more" by fetching this route, but #223 did not add it. It is a read-only addition and changes no existing contract. Running the whole search costs the same as the list request that came before it, and ranking stays in one place.
  **Issue:** #224
- **Decision:** The table moved from `SongsPage.tsx` into a new `web/src/songs/SongsTable.tsx` (`SongsTable`, `FromSongs`). `SongsPage` keeps the address state, the filters, the search box, and the empty and total texts.
  - Columns, in order: Play, Shortcode, Title, Artist, Concept, State, Kind, Versions, Genre, Tags, Created, Updated, Workspace, Selected Generation, Audio files. The first seven cell positions are unchanged.
  - Genre lists every Genre, comma-separated. Created is the date in the configured time zone.
  - Workspace shows the name, or a dash read aloud as "None". Selected Generation shows the shortcode, or "None"; it replaces #120's check column and keeps the `song-selected` testid.
  - Tags and Workspace are hidden below Mantine's `md` breakpoint.
  **Why:** The must-have names `SongsTable.tsx`, and the discretion lines name these columns and say which ones narrow screens hide first.
  **Issue:** #224
- **Decision:** The web `SongSort` gained `relevance`.
  - Relevance is the default while searching and is never sent: the API orders by relevance when `sort` is absent, and an address with `sort=relevance` reads as the plain default.
  - Starting or changing a search moves a sort that was left at its default to the new default. A sort the user chose is kept.
  - Clearing the search goes back to newest-updated first. "Clear the search and filters" resets the whole view.
  **Why:** This is the "ordinary order" in the AC, and it means a chosen title sort still applies to results.
  **Issue:** #224
- **Decision:** History works as follows.
  - Typing in the table's box replaces the current entry 300 ms after the last key. Enter adds an entry only when the text differs from the address, so pressing Enter after the debounce has fired does not create a duplicate entry.
  - The box ignores the arrival of a search it sent itself. That prevents a race where typing done during the debounced navigation was overwritten by the older address (component test).
  - The header box searches on Enter only and navigates to `/songs?search=…` with nothing else in the address.
  **Why:** These follow the discretion lines. The race was found by the address round-trip test.
  **Issue:** #224
- **Decision:** The header box (`web/src/search/HeaderSearch.tsx`) is a `role="search"` landmark named "Search", holding a plain text box named "Search Songs", not `type="search"`. On the Songs page it shows the active search.
  - `/` and Ctrl+K or ⌘K focus the table's box on the Songs page and the header box elsewhere. Below `sm`, where the header box is hidden, they open the icon's popover.
  - The shortcut is ignored with Alt, with Ctrl or ⌘ on `/`, with Shift on K, and while focus is in a field or the editor. That check reuses #220's `isTypingTarget`.
  - The header now has a second text box whose name contains "Search", so `IgnoredItemsPage.test.tsx` and `ignored-items.spec.ts` look up their own box within `main`.
  **Why:** A `searchbox` role on every page would have collided with the pages' own search boxes, which several tests look up by role.
  **Issue:** #224
- **Decision:** Highlighted words are `<mark>` elements that are bold and use a new palette pair, `highlight` (light: `#ffec99` on `#1a1b1e`; dark: `#5c4400` behind `#fff3bf`). Contrast is tested in `palette.test.ts`. After each excerpt, visually hidden text reads "(matched: “word”, …)".
  - Archived text in a match row is marked "(Archived)", and a trashed Generation's text "(In Suno’s Trash)".
  - An archived Song is marked by its State cell, which shows the Archived workflow state.
  - Album and Playlist matches, like the Song's own fields, show the field name alone and open the Song.
  **Why:** Highlighting must not rely on colour alone, and screen readers announce `<mark>` inconsistently. The AC defines where only Version and Generation matches open.
  **Issue:** #224
- **Decision:** When a search finds nothing, the page says "No Songs match “…”" (adding "with the chosen filters" when filters are set) and offers "Clear the search", plus "Clear the search and filters" when filters are set. A page whose `indexRebuilding` is true shows a one-line note. The total reads "N Songs match “…”" as a polite status.
  **Why:** These meet the AC's empty state and total. #225 adds its new filters to `filtered` and to `clearAll`.
  **Issue:** #224

Story #234 (built in parallel; merged into the milestone branch):

- **Decision:** The application's log files are written by a small rolling sink of our own (`Infrastructure/Logging/FileLogging.cs`), not by `Serilog.Sinks.File`. This deviates from the planner's discretion line. It is still one more sink of the same Serilog pipeline, sitting behind `WithRedaction()`, and it uses the same `JsonLogFormatter`. No new package was added.
  **Why:** `Serilog.Sinks.File` 7.0.0 does not fit the story's other requirements. It rolls on local time through an internal static clock, so days cannot be counted in UTC and the test plan's controllable clock cannot drive it. It has no way to tell Diagnostics that the folder cannot be written. It cannot compare the folder with the media folder (#387) before each open. It cannot change the roll size (min(20 MB, cap/4)) while running.
  **Issue:** #234
- **Decision:** The cap is enforced at every roll as well as at each sweep. At a roll, the oldest files are deleted until the closed files plus one full roll size fit under the cap. The files therefore never pass the cap by more than one line, which is stricter than the AC's "one file's worth".
  **Why:** An hourly sweep alone lets a busy hour overshoot by far more than one file.
  **Issue:** #234
- **Decision:** Nothing is deleted until the saved limits are in effect (`ILogFiles.Configure`). The "startup sweep" is the monitor's first look, which runs after it has read the setting.
  **Why:** A sweep with the defaults (14 days, 200 MB) at startup would cut short a saved 90-day / 5 GB setting.
  **Issue:** #234
- **Decision:** `confirmation_required` (409, as the discretion line says) counts only the files that the new limits delete and the current limits keep.
  **Why:** Otherwise a save that only changes the level would ask for confirmation whenever an hourly sweep is due.
  **Issue:** #234
- **Decision:** When a saved Debug level reaches its 24 hours, the setting is written back as Information with `debugUntil` null and revision + 1, and one Information line is logged. A Debug level set by `N8TRACKS_LOG_LEVEL` never ends.
  **Why:** This makes the page and the API show the level that is actually in effect, and a stale client gets a revision conflict.
  **Issue:** #234
- **Decision:** Settings → Diagnostics is a new route, `/settings/diagnostics`, with a sidebar entry after System, holding `DiagnosticsSettingsPage`. #235 replaces System and folds into this page.
  **Why:** The AC names "Settings → Diagnostics", and #235's page does not exist yet.
  **Issue:** #234
- **Decision:** Gateway log files are named `n8tracks-gateway-YYYYMMDD[_n].jsonl`. `N8TRACKS_GATEWAY_LOG_PATH` must be an absolute path. The gateway does no media-folder comparison: it has no media setting, and that is recorded in `MediaMountAccessTests`. Its lines are written by the registered JSON `ConsoleFormatter` itself, so they are byte-for-byte its console lines.
  **Why:** The gateway may share no code with the application, and an operator may point both at one folder; the app ignores other files there.
  **Issue:** #234
- **Decision:** Invariant 2 hardening: a link carrying a log file's name is never written through or deleted (the next sequence number is used). The log folder is refused when `MediaFolderOverlap` finds it, or the data folder while the log folder is absent, inside the media folder. The check runs before the folder is created and again after.
  **Why:** Defense in depth beyond #387's startup check: a `logs` folder that is a second mount of the media folder, or a planted symlink, must not let a write reach `/media`.
  **Issue:** #234
- **Decision:** `folderProblem` and the gateway Warning describe the failure in fixed words (not allowed / not writable / inside the media folder), never with the exception message.
  **Why:** .NET's I/O exception messages contain host paths, which #235 keeps out of Diagnostics.
  **Issue:** #234
- **Decision:** The log settings monitor is off in test hosts (`LoggingSettingsMonitorOptions { Enabled = false }` in `N8TracksApiFactory`), as the other schedulers are. Tests call `LoggingSettingsMonitor.LookAsync` themselves.
  **Why:** Test determinism.
  **Issue:** #234

Story #225:

- **Decision:** A bad filter value is refused with 400 `invalid_request`, naming the parameter, not `invalid_parameter`. This corrects a typo in the AC, which says "as #58 does", and #58 answers `invalid_request`. It is not a replan.
  **Why:** No `invalid_parameter` code exists. The orchestrator decided to log this as a corrected typo.
  **Issue:** #225
- **Decision:** A well-formed Genre or Tag ID that does not exist now matches nothing; before, it was a 400 (#83, #85). An unknown workflow state is still a 400, as AC 12 lists it. `GenreEndpointTests` and `TagEndpointTests` are updated to match.
  **Why:** The planner's discretion line says this replaces #83's and #85's unknown-ID refusal.
  **Issue:** #225
- **Decision:** Repeated `tag` now means every one of those Tags by default (`tagMode=all`), where before it meant any of them. `tagMode=any` restores the old reading. In the web bar, choosing "No Tags" next to another Tag switches to `any` and disables "All of these Tags". The API refuses `tag=none` with other Tags under `all`.
  **Why:** The discretion line sets the default to `all`, and the AC asks for both all-of and any-of.
  **Issue:** #225
- **Decision:** The new filters are trailing members of `SongListRequest` and `SongListQuery`, after `Search`/`MatchedIds` (#223's note). The service sets them with an object initializer. Each one is a sub-query in `SongStore.Filtered` (EXISTS on `album_songs`, `playlist_songs`, `generations`, and `audio_files`), so no filter loads the catalog. In the address, the new parameters come after the existing ones (`search` … `title`) and before `page`.
  **Why:** The key_link asks for sub-queries. Appending keeps every existing address and test unchanged.
  **Issue:** #225
- **Decision:** `audio=available` reads the reported status. While the media folder is recorded Unavailable (`MediaAvailability.CurrentAsync`), no Song has an available file and `unavailable` matches every Song that has files. Local audio counts every file associated with the Song (`audio_files.song_id`), Song-level or a Generation's.
  **Why:** This is the m6-plan's ID line (#207's reported status) and the discretion line.
  **Issue:** #225
- **Decision:** Day boundaries are `DailyTaskRules.PlannedOn(day, 00:00, TZ)`. `createdTo` is exclusive at the start of the next day, so both ends are included. Dates must be `yyyy-MM-dd` exactly.
  **Why:** The discretion line asks for whole days in the configured zone. This reuses the existing local-midnight rule, including its handling of DST gaps.
  **Issue:** #225
- **Decision:** `GET /api/v1/songs/filter-values` (`catalog.read`, `SongFilterValueService`) offers genre, tag, album, playlist, and model. It does not offer workflow states. The state chips keep `GET /workflow-states` with `statesForFilter`, which already applies "shown, plus hidden ones a Song is in". `ids` and `query` cannot be combined. At most 100 IDs.
  **Why:** States were already offered by the same rule. A second source would duplicate it.
  **Issue:** #225
- **Decision:** The values are read as ID/name projections and matched on word beginnings in memory (folded case and diacritics, then the first 50 by name). Model names are the model list's entry when `SunoModelRules.Match` finds one, and the reported text otherwise. The service is excused in the invariant-1 guard as "reads only".
  **Why:** Personal catalogs are small. Matching on word beginnings with diacritics removed cannot be expressed with SQLite LIKE. Clients still never load a catalog whole.
  **Issue:** #225
- **Decision:** This wave's migration is `20261008090000_AddSongFilterIndexes`, with indexes only: `songs (created_utc, shortcode_number)` and `generations (model_version, song_id)`. It uses `CREATE INDEX` and rebuilds no table, so the search triggers stay. The Designer was written from the current snapshot plus these two indexes.
  **Why:** The creation-date range and the model filter and picker are the new reads that no existing index covers.
  **Issue:** #225
- **Decision:** Chips are Mantine `Badge`s with a `CloseButton` named "Remove the filter …", not `Pill`.
  **Why:** `Pill`'s remove button is hidden from assistive technology (it is built for use inside an input), so a chip could not be removed by keyboard or screen reader.
  **Issue:** #225
- **Decision:** Rule 1: `DuplicateTitleEndpointTests` expected the old refusal for a repeated single-value parameter ("Only state, genre, tag, and artist may be given more than once."). Now that `model` is repeatable, the endpoint says "…tag, artist, and model…". The two InlineData expectations are updated to match.
  **Why:** The full `dotnet test` gate failed on these two cases only. The new message is correct.
  **Issue:** #225
- **Decision:** Rule 3: `SongFilterValueStore.cs` failed `dotnet format --verify-no-changes` because of the indentation of its case-block braces. It was fixed with `dotnet format whitespace` on that file alone. There was no code change.
  **Why:** The format gate must pass.
  **Issue:** #225
- **Decision:** The new title order key is a new column, `songs.title_order_key` (`SongRules.TitleOrderKey`). The existing `title_sort_key` keeps its meaning (NFC, lower case), because `q`'s substring match and the relationship and ISRC orders read it.
  **Why:** Natural digit order and folded accents would break `q`'s "title contains" match if written into the existing column.
  **Issue:** #226
- **Decision:** The key folds accents (FormD, combining marks dropped), lower-cases invariantly, and collapses white space. It strips leading **and trailing** characters that are not letters or digits, and writes each ASCII digit run as its three-digit length plus the digits without leading zeros (`Song 2` → `song 0012`). A title of nothing but punctuation keeps its folded text.
  **Why:** The discretion line names leading punctuation and quotes. Stripping trailing ones too makes `"Apple"` sort with `apple`, not after `apple pie` because of the closing quote. Length-prefixed digit runs sort naturally at any length within the 300-character title cap.
  **Issue:** #226
- **Decision:** This wave's migration is `20261008100000_AddSongOrderKeys`, written by hand:
  - `ALTER TABLE songs ADD COLUMN title_order_key TEXT NOT NULL DEFAULT ''`;
  - a back-fill through the new per-connection SQL function `n8_title_order_key`;
  - plain `CREATE INDEX` for `songs (title_order_key, shortcode_number)`, `generations (song_id, rating)`, and `generations (song_id, suno_created_utc, created_utc)`.

  The Designer is EF's own output from the current snapshot, renamed from the generated timestamp. `has-pending-model-changes` reports none.
  **Why:** The binding note forbids an EF rebuild of trigger-carrying tables. The back-fill writes no column the search triggers watch. The two Generation indexes back the rating and last-Generation sub-queries, and nothing is stored on the Song.
  **Issue:** #226
- **Decision:** The `song` retained shape goes from 3 to 4. The upgrader `SongShape3To4` computes `title_order_key` from the retained title, by the same rule. `retained-shapes.json` is updated, and `SunoWorkspaceRulesTests` now asserts shape `>= 3` instead of `== 3`.
  **Why:** `RetainedShapeGuardTests` requires a shape bump and an upgrader for any column added to a retained table. A Song retained before this change must restore with a usable key.
  **Issue:** #226
- **Decision:** The new keys are `SongSort.Created|Rating|State|LastGeneration` and `sort=created|rating|state|lastGeneration|relevance`. `updated`, `title` and `audioFiles` stay. No new member was needed on `SongListRequest` or `SongListQuery`, because the keys are enum values on the existing `Sort`.
  **Why:** The discretion line lists the keys. The plan's drift note keeps `audioFiles` as a seventh accepted key.
  **Issue:** #226
- **Decision:** The tie-break is the shortcode ascending for every key and in both directions. This replaces the old rule that broke ties in the sort's own direction. The existing `SongEndpointTests` expectation for title descending is updated, and `sort=created` in its 400 theory becomes `sort=newest`, since `created` is now valid.
  **Why:** The discretion line: "Tie-break is shortcode ascending for every key."
  **Issue:** #226
- **Decision:** Rating is the highest rating over live Generations, in any state. Last Generation date is the latest `COALESCE(suno_created_utc, created_utc)` per Generation. Both sorts order by "no value" first, so empty values come last in either direction. State sorts by `workflow_states.position`. Songs gain `highestRating` (`SongSummary.HighestRating`, `SongResponse.highestRating`, `null` when unrated) for the Rating column.
  **Why:** These follow the discretion lines. The Rating column needs the value, which no answer carried before.
  **Issue:** #226
- **Decision:** A `direction` sent with `relevance` is ignored, but an unknown direction text is still a 400. `sort=relevance` without a search is the default order, updated and newest first, in its own first direction. Relevance with a search is now always best first. Before this change, `direction=asc` reversed it.
  **Why:** The discretion line says "a `direction` sent with `relevance` is ignored". Refusing bad text keeps parameter validation uniform.
  **Issue:** #226
- **Decision:** In the web app, relevance is still never written to the address: its absence while searching means relevance. Choosing the default order (Updated, newest first) before a search cannot be told apart from choosing nothing, so a later search orders by relevance. Any other chosen sort is kept.
  **Why:** The address leaves out defaults (#59 and #224), so this keeps the existing address shape. The Demo (search → Relevance → pick Updated → reload) holds.
  **Issue:** #226
- **Decision:** The Sort control is `web/src/songs/SongSortControl.tsx`, a group named "Sort" holding a "Sort by" select and an "Order" select whose wording follows the key (for example "Newest first" or "Workflow order"). The Order select is hidden for Relevance. Its labels live in `songSortRules.ts`, because react-refresh forbids exporting constants from a component file. Headers in `SongsTable.tsx` sort by Title, State, Created, Updated, Rating (a new column after Updated), and Audio files. Last Generation date is only in the control.
  **Why:** This follows the discretion lines, and native selects are accessible without extra wiring.
  **Issue:** #226
- **Decision:** A page past the end now shows the empty table, its headers only, with "There are no Songs on this page." and a link "Go to the last page (page N)". This replaces the "Go to the first page" button, and the existing SongsPage test is updated.
  **Why:** The discretion line: "A page past the end shows an empty table with a link to the last page."
  **Issue:** #226
- **Decision:** Rule 3: the upgrader first read the title with `JsonNode.GetValue<string>()`, which tripped `EnvironmentReadGuardTests`' `.GetValue` pattern. It now uses a `(string?)` cast.
  **Why:** The architecture gate must pass, and the guard's pattern is name-based.
  **Issue:** #226

Story #228 (built in parallel; merged into the milestone branch):

- **Decision:** `DashboardService` (new namespace `n8Tracks.Application.Dashboard`) reads only through `SongService.ListAsync` and `WorkflowStateService.ListWithUsageAsync`. Recently edited is `sort=updated&archived=active&pageSize=10`. Without a Selected Generation is the same plus `selected=no&generations=some`. Each state count is the workflow list's usage count.
  **Why:** The key link requires the counts and the Songs table to agree. Reusing the list's own filters makes them agree by construction, and the tests check each count against the list total. The service takes no catalog type (it only has `GetAsync(CancellationToken)` and returns its own records), so it stays outside `CatalogServiceNamespaces`, as the M6 rule allows.
  **Issue:** #228
- **Decision:** The new `generations=some|none` Songs-list filter (`SongListRequest.Generations`, `SongListQuery.HasGenerations`) is placed after #225's `Archived`/`MediaUnavailable` members. It counts any row in `generations` (live Generations in any state, archived included). A repeated or unknown value gets 400 `invalid_request`. In the web filter bar it is a "Generations" select ("Has Generations" / "Has no Generations") after Local audio, plus a removable chip.
  **Why:** This follows the story's discretion lines. The parameter order follows the #225 note.
  **Issue:** #228
- **Decision:** `GET /api/v1/dashboard` answers each section as `{data}` or `{error:{code:"section_failed"}}`. When every section fails it answers 500 problem `section_failed`. Each failure is logged at Error in the endpoint (Application has no logger), with the exception but no request data.
  **Why:** This follows the discretion lines. Logging is kept in the Api layer to match the existing endpoints.
  **Issue:** #228
- **Decision:** The welcome is shown when the By workflow state section reports zero Songs in every state, archived included. If that section failed, the three sections are shown with their empty or failed states instead.
  **Why:** Every Song is in exactly one state, so the counts are the exact "no Songs at all" test, and this needed no extra field in the answer.
  **Issue:** #228
- **Decision:** The page heading is "Dashboard" and the sidebar entry is "Home" (first, above Songs, current only at `/`). See all links to `/songs?archived=active` (last updated first, the default sort), so the table holds the same Songs the section lists. "Show in Songs" appears only when the count is above zero.
  **Why:** The story calls the page the dashboard and names the sidebar entry Home. An archived-inclusive See all would not match the section's list.
  **Issue:** #228
- **Decision:** The focus refresh reads again only when at least 30 s have passed since the last read began. It keeps the previous data while loading. On failure it keeps that data with a "could not be refreshed" status line. Retry on a failed section re-reads the whole dashboard, with the same keep-on-failure rule.
  **Why:** This follows the discretion lines. One request serves all three sections.
  **Issue:** #228
- **Decision:** Existing e2e and unit expectations that `/` redirects to `/songs` were updated to the dashboard. These are `account.spec.ts` (the sidebar list gains Home, and the landing heading after sign-in changes), `setup.spec.ts`, the `shell.spec.ts` sub-path root, `AppShell.test.tsx`, `SessionGate.test.tsx`, and `SetupGate.test.tsx`.
  **Why:** This is the AC's intended change of the home page.
  **Issue:** #228

Story #229 (on the milestone branch):

- **Decision:** One migration, `20261008182421_AddAttentionDismissals` (EF's own timestamp, which sorts after `20261008100000`; the Designer was generated from the snapshot). It adds the table `attention_dismissals (kind, subject, dismissed_utc)`, PK (kind, subject), with a CHECK on kind. It also adds two nullable columns to `suno_exports`, `end_reason` and `failed_step`, as plain `ALTER TABLE ADD COLUMN`. `end_reason` has no CHECK.
  **Why:** The story names the dismissals table. The discard reason needs storage on the export. SQLite can add a CHECK only by rebuilding the table, and `suno_exports` is the cascade parent of the staged rows. It carries no triggers, so EF's plain add-column is safe, but a rebuild would not be. The application writes only the four names.
  **Issue:** #229
- **Decision:** Discard reasons: `POST /suno/exports/{id}/discard` takes an optional body `{reason: "cancelled"}` or `{reason: "failed", step}` (1–200 characters). A step with any other reason is 422, and a non-object body is 400. n8Tracks records its own reasons too: `replaced` for an export a newer one discarded at completion, `abandoned` for one the 24-hour sweep discarded, and `failed` at step `classifying` for a classification failure (inline, in the job, or swept). An export that has already ended keeps its first reason. The extension sends `cancelled` for Cancel sync and for an earlier receiving sync that a new one replaces, and `failed` with its stop step when a sync stops. A closed or lost tab sends no reason.
  **Why:** This follows the discretion line ("optional reason, cancelled or failed with the step"), so a failure can be told from a cancel or a replacement. Old extensions send no body, which still discards (additive).
  **Issue:** #229
- **Decision:** A failed sync is the export with `IsSyncFailure` (state `failed`, or `discarded` with reason `failed`) that ended last. It stands until any export's `ready_utc` is later than its `ended_utc`. That includes an export that returns to ready after an interrupted commit.
  **Why:** The discretion line says it "clears when a later export reaches ready". Comparing times, not creation order, is what "later" means when exports overlap.
  **Issue:** #229
- **Decision:** A failed Generate on Suno request is the request that ended `stopped` or `expired` last. A request that ended `done` later clears it. An active request that is already due by time (unclaimed for 15 s, or quiet for an hour) counts as failed at the moment it fell due. This is judged in memory, and reading the dashboard writes nothing. A request whose Version is gone is not listed. It links to `/songs/<song>/v/<number>`.
  **Why:** No background job settles requests (they settle when read), and the discretion line says "the sections read live". Dating a due request at its due moment makes it age like a stored one. Using "now" would never let it drop off.
  **Issue:** #229
- **Decision:** Suno reviews lists only `ready` exports (a classifying one has no classes yet), oldest first. Each shows `arrivedAt` (its ready time), its record total, and its Changed and Conflict class counts, which are the review page's own counts.
  **Why:** The discretion line defines "unresolved" as the Changed and Conflict class counts of an export still awaiting review. A committed, discarded, or expired export is no longer awaiting review.
  **Issue:** #229
- **Decision:** Unmatched Files counts every audio file associated with nothing, whatever its status. This is the Unmatched Files page's default list. It also answers `mediaUnavailable` from the stored mount state. The page says the folder is unavailable instead of showing the count, and still links to the page.
  **Why:** The AC says "when the media folder is unavailable it says so instead", and the count must equal the linked page's total, which keeps listing the files (reported unavailable).
  **Issue:** #229
- **Decision:** Suno problems are ordered as the failed sync and the failed request (newest first), then the Unavailable workspaces with Songs, by name. Each section lists at most 5. Suno problems' "n more" goes to `/settings/suno-workspaces` (only workspaces can overflow), and Suno reviews' goes to `/suno/imports`. A failed sync links to `/suno/imports` and a workspace to `/settings/suno-workspaces/<id>`.
  **Why:** These follow the discretion lines on links and "n more". There are at most two failures at a time.
  **Issue:** #229
- **Decision:** I added `GET /api/v1/attention` (`catalog.read`). It carries the same three sections in #228's `{data}|{error}` envelope, and is 500 `section_failed` only when all three fail. The dashboard embeds the same sections; its 500 now needs all six to fail. A bearer token gets counts only, with the `exports` and `problems` lists left out.
  **Why:** The Suno page's notice needs the failed sync without reading the whole dashboard. The artifact line says AttentionService is "shared with the badges" (#233), which will poll it. The discretion line says "a bearer token gets these sections as counts only".
  **Issue:** #229
- **Decision:** `POST /api/v1/attention/dismissals` (session-only; the session-only count is now 69) takes `{kind: failedSync|failedGenerate, subject: <export or request ID>}` and answers 204. It is idempotent and keeps the first dismissal time. An unknown export or request is 404. Any other kind, including `unavailableWorkspace`, is 422 on `kind`.
  **Why:** This follows the discretion lines: dismissals live in `attention_dismissals (kind, subject, time)`, an Unavailable workspace "cannot be dismissed", and a newer failure is a new entry because it has a new subject.
  **Issue:** #229
- **Decision:** `AttentionService` lives in `n8Tracks.Application.Dashboard` (not a catalog namespace) and takes no catalog type. The new public reads `ExportStagingService.WaitingForReviewAsync`, `SyncFailureAsync`, and `GenerationRequestService.FailureAsync` are excused in the invariant-1 guard as reads only. The discard endpoint and dismissals are listed in `ApiEndpointsTouchingNoVersion`. `attention_dismissals` is classified Other in `SunoExportStagingGuardTests`.
  **Why:** This is the M6 rule for new services. The guard enumerates every public method of a catalog namespace.
  **Issue:** #229
- **Decision:** On the dashboard, the three sections follow the catalog sections in the same `SimpleGrid`. On an empty catalog they are shown below the welcome, in a grid of their own. The failed-sync notice on `/suno/imports` replaces the "last sync was discarded" line for the same export. It says what failed and at which step, how to try again, and offers Dismiss.
  **Why:** A first sync can be waiting before any Song exists. The two lines would say the same thing twice.
  **Issue:** #229
- **Decision:** Notifications are one new table, `notifications` (migration `20261008191535_AddNotifications`, a plain CreateTable with no rebuild). Its columns are kind, severity, summary, detail, link, retry action and subject, coalescing key, topic, subject, count, first and last occurrence, read, dismissed, retried, and resolved times, and a before-restore flag. Severity has a CHECK. Kind has none, so M7 and M8 add their kinds without a migration. The table is classified Other in `SunoExportStagingGuardTests`. `RestoreApi.Fingerprint` leaves it out, as it leaves out `sessions`: it is what the work under test recorded about itself, written as that work ends.
  **Why:** The story names the table, and the AC says "the kind list is open". Without the fingerprint change, every "nothing changed" restore and upgrade assertion would race the notification of the work it checks.
  **Issue:** #231
- **Decision:** The job-finished hook is `IJobFinishedHook` (Application/Jobs). `JobWorker` calls it in a scope of its own just before it writes the job's outcome, with the payload, the unscrubbed result, and the exception. `JobNotifications` picks the `IJobNotificationProducer` for the job type: `MediaScanNotifications`, `BackupNotifications`, or `ImportCommitNotifications`. A hook failure is logged and never changes the job. Sync, restore, and migration are direct producers (`SunoSyncNotifications`, `RestoreNotifications`, `MigrationNotifications`). Every producer is also registered as `INotificationProducer`, which the kind walk test checks against `NotificationKinds.Recorded`.
  **Why:** This follows the key links. The hook is called before the outcome write, so whoever sees a job end already finds its notification, which keeps tests and #232's polling deterministic. The producers read the result for numbers only and the exception for its type only.
  **Issue:** #231
- **Decision:** Summaries and details come from fixed templates in the producers and never include job error text, exception messages, the extension's step text, archive names, or paths. Recording goes through `NotificationRecorder`. It runs in a transaction of its own, swallows and logs its own failures through the `INotificationLog` port (Application has no logger), and prunes as it records: read or dismissed rows after 90 days, and beyond the newest 500. An undismissed warning or failure is never pruned.
  **Why:** This is invariant 6 and the discretion lines ("fixed templates with numbers, never from exception text"; 90 days; 500 cap on read or dismissed only). A notification must never fail the work it reports.
  **Issue:** #231
- **Decision:** Coalescing happens only for scheduled failures (key `mediaScan:scheduled` and `backup:scheduled`, a scheduled backup's own retry included). A failure joins the newest notification of its key while that one is a failure not dismissed, retried, resolved, or from before a restore. The count goes up, the latest time, summary, and detail replace the old ones, and the notification becomes unread again. A topic is resolved by any outcome of the same topic that did not fail, whether a success or a warning (for example, a backup that fell back to the data volume). Topics are per kind for scans, backups, and syncs, and per export for imports. A routine scheduled scan that records nothing still resolves its topic (`NotificationDraft.Silent`).
  **Why:** This follows the discretion lines on the coalescing key and on "a successful run after failures resolves". A backup on the data volume did make a backup, so the earlier "no backup" failure is over. Without a silent resolve, a scheduled-scan failure followed only by routine scans would never be resolved.
  **Issue:** #231
- **Decision:** Scans: a manual scan always records what it found. A startup, scheduled, or recovery scan records only a failure, or a success when it found new unmatched files. `MediaScanCounts.NewUnmatched` and the job result's additive `newUnmatched` count the files the scan added that the Suno ID matcher left unassociated.
  **Why:** The AC ("only when they fail or when they find new unmatched files") needs "new unmatched", which the counts did not have. The field is additive, and the web's guards accept extra keys.
  **Issue:** #231
- **Decision:** Retry is `POST /notifications/{id}/retry` (session-only). It is offered for a failed scan (a manual scan), a failed backup (`BackupService.StartAsync(Manual)`, including after a scheduled failure), and an import commit that applied nothing (`ImportCommitService.CommitAsync` of the export at its current revision, only while the export is `ready`). Work already queued or running gets 409 `work_in_progress`. A notification with no retry, or one already retried, resolved, dismissed, or from before a restore, gets 409 `not_retryable`. A started retry marks the notification retried (it stays listed and stops counting as unread). The answer is 202 `{jobId}` with the job as Location.
  **Why:** This follows the AC and the discretion lines. The current revision is the user's own choices: putting a commit that applied nothing back to ready only turns records a Generation now holds into Skip (#140's reclassify). So a retry cannot apply a choice the user did not confirm (invariant 3), and `ImportNeverOverwritesGuardTests` stays green.
  **Issue:** #231
- **Decision:** "Applied nothing" is marked on the commit job's exception (`Exception.Data`, read through `ImportCommitService.AppliedNothing`) and is decided conservatively. It stays true until a Reimport is restored, a target is applied, a Changed or Conflict resolution does not fail, or the commit reaches its post-target steps (the #314 statuses, the #142 remote states, the ignore list, events, and artwork). From then on everything may write. A commit cancelled by shutdown is never marked. The export already returned to `ready` on any failure, so the failure path itself is unchanged.
  **Why:** The job's error text is scrubbed and its result is null on failure, so the job needed a side channel that changes neither the stored error nor the export's lifecycle. Erring toward "may have applied" never offers an unsafe retry. The discretion line says a commit interrupted with no result is not retry-safe.
  **Issue:** #231
- **Decision:** Mark read (`POST /notifications/read {ids}`, at most 200, session-only) changes successes only. Warnings and failures stay unread until dismissed, retried, or resolved. Dismissing one (`/{id}/dismiss`, 204, idempotent, 404 if unknown) and dismissing all (`/dismiss-all`, 200 `{dismissed}`) are session-only. `GET /notifications` (`catalog.read`) lists undismissed notifications, or every notification kept with `include=history`, 30 a page (`page`). Each list is newest occurrence first, with `{success, warning, failure, total}` unread counts. The session-only count is now 73.
  **Why:** This follows the AC ("Success notifications are marked read once shown; warnings and failures stay unread and listed until the user dismisses them") and the route and paging discretion lines. Notifications carry no revision.
  **Issue:** #231
- **Decision:** Suno sync: an export reaching ready is a success with its staged record count, and it resolves earlier sync failures. A classification failure (inline, in the job, or the 24-hour sweep) and a discard with reason `failed` are failures. A ready export that expired is a warning. A cancel, a replacement, and an export abandoned before it was completed record nothing. None offers Retry. Each export's event is recorded once (subject `<export>:<event>`).
  **Why:** This follows the discretion line on syncs. The sweep of an abandoned receiving export is neither a failure nor an expiry of something the user had in review, so it records nothing, like a cancel.
  **Issue:** #231
- **Decision:** Restore: `RestoreRunner` records the outcome after the run, in a new scope. A succeeded restore writes into the restored database after marking every archive notification read and `beforeRestore`. A rolled-back restore and one that failed before replacing anything are failures with no retry. A rollback that failed records nothing while the instance stays in maintenance. A restore put back at the next start (`StartupNotices`), or recovered at start (`MaintenanceMode.RecoveredAtStart`), is recorded by `DatabaseStartup` once the database is up to date.
  **Why:** This follows the discretion lines. While the instance is still in maintenance there is no database to write to.
  **Issue:** #231
- **Decision:** Migration: `DatabaseStartup.RunAsync` records a success ("N migrations applied after a safety backup") when this start upgraded a database that had data. A brand-new database records nothing. When the database is up to date, it also records a failure for an earlier failed upgrade whose marker `RecoverFailedUpgradeAsync` cleared at this start. Both link to `/settings/diagnostics`, and neither offers Retry. Recording runs in its own scope and catches its own failures, so it can never turn a good start into a put-back.
  **Why:** This follows the discretion lines: "recorded at the first start after an upgrade", and a failed one "at the next successful start, from #76's marker, with no retry". A failure here must not reach the upgrade's failure handling, which would restore the safety backup.
  **Issue:** #231
- **Decision:** There are no web or e2e changes. The bell, panel, and toasts are #232. No e2e spec was run: this story changes no page and no API contract an e2e spec drives (the scan result's `newUnmatched` is additive).
  **Why:** Owner rule (2026-10-07): run e2e only for specs a story adds or whose pages or endpoints it changes.
  **Issue:** #231
- **Decision:** Rule 2: a job a crash or an abandoned shutdown left running now reaches the job-finished hook when the next start fails it. `IJobStore.FailRunningAsync` returns the jobs it failed, with their payloads, instead of a count. `JobWorker.FailInterruptedAsync` tells the hook about each one as interrupted, with no exception, so an import commit gets no Retry.
  **Why:** Before this, only a job interrupted while the worker stopped gracefully was told. A scheduled backup cut off by a crash would have recorded nothing, which breaks "a failure is never lost".
  **Issue:** #231
- **Decision:** Rule 1: an interrupted scheduled backup's summary says "the next one runs at its planned time", not "n8Tracks tries again in an hour".
  **Why:** `BackupScheduleRules.RetryAt` never retries a backup a restart interrupted, so the notification would have promised a retry that never runs. `AJobLeftRunningAcrossARestartRecordsItsFailureAtTheNextStart` covers both fixes.
  **Issue:** #231
- **Decision:** Guard edits: the notification dismiss and retry routes go on `ReferenceParameterGuardTests.OtherIds`, because a notification ID is not a Song or a Version. `MediaScanService` matches an added file with positional `AudioFileWrite.Added(var addedFile)` and not `{ File.Id: ... }`. `MediaMountAccessTests`' file-system pattern reads the property pattern as `System.IO.File`, so the code was rewritten and the guard was left as it is.
  **Why:** Both guards caught the new code as it stood. Neither was weakened, and the scan file does not join the file-system list.
  **Issue:** #231
- **Decision:** The list orders by occurrence time, kept to the millisecond, then by ID. Two notifications in the same millisecond can therefore list in either order, because a UUIDv7's low bits are random. The tests that record notifications directly give each one a later millisecond. The product order is left as it is.
  **Why:** `ListingNeeds…SessionOnly` failed once under load when three notifications recorded back to back shared a millisecond. "Newest first" has no meaningful order inside one millisecond.
  **Issue:** #231

Story #230 (built in parallel; merged into the milestone branch):

- **Decision:** The dashboard arrangement is the `settings` row `ui.dashboard` = `{revision, sections:[{key,hidden}]|null}`; `GET`/`PUT`/`DELETE /api/v1/settings/dashboard`, session-only, with the usual If-Match revision (`"0"` before the first save). Reset+Save is `DELETE`, which keeps the row with `sections: null` so the revision keeps counting and a stale save from before the reset is still refused.
  **Why:** The plan names a settings row and the revision check; a deleted row would restart the revision at 0 and let a stale "0" save through.
  **Issue:** #230
- **Decision:** Unknown keys are dropped and missing keys appended (shown, in the default order) when the arrangement is read, not only when it is written; a repeated key keeps its first place. More than 100 placements, or a placement not `{key: string, hidden: bool}`, is 422.
  **Why:** A section a later version adds then appears without a write; a key a later version retires disappears the same way.
  **Issue:** #230
- **Decision:** `GET`/`PUT /api/v1/settings/last-song` (row `ui.lastSong` = `{song: <id>}`), session-only, no revision. The GET answers `{song:{id,shortcode,title}|null, deleted}`; PUT of an unknown Song is 404, of a non-ID 422. The Song is read through `SongService.FindAsync`, so archived opens and deleted (restorable or purged) answers `deleted: true`.
  **Why:** Claude's Discretion fixes the shape and "last write wins"; the GET shape lets the button name the Song and give the reason.
  **Issue:** #230
- **Decision:** The `open-sync` reply's failure reasons are `unpaired | revoked | missing_scope` as planned plus `unreachable` (the extension cannot reach n8Tracks, or could not open a tab). "Paired with another n8Tracks" and "a host permission removed" count as `unpaired`. An extension older than #230 answers `unknown_type`, which the page shows as "update the extension".
  **Why:** The extension's connection state has an `unreachable` status the three planned reasons do not cover; folding it into `unpaired` would tell the user to pair again when they need not.
  **Issue:** #230
- **Decision:** `open-sync` opens `https://suno.com/me` (the adapter's `sunoListAddress({page:'library'})`) in a new tab beside the n8Tracks tab; the service worker remembers that tab in session storage (`syncPanel`) and its first `sync-resume` answers `{session:null, open:true}` once, which opens the panel on the Sync view's choose step. Nothing is read, staged, or pressed (invariant 4).
  **Why:** Reuses the existing resume path rather than a new content-script message; the tab ID check means no other Suno tab is affected.
  **Issue:** #230
- **Decision:** dnd-kit `@dnd-kit/core` 6.3.1, `@dnd-kit/sortable` 10.0.0, `@dnd-kit/utilities` 3.2.2 (from `npm view`, 2026-10-08). Keyboard reordering is both the Move up/Move down buttons and dnd-kit's keyboard sensor on the drag handle; each move is announced (a polite live region for buttons, dnd-kit's announcements for dragging). Focus stays on the moved section's button, or its other button at an end.
  **Why:** Claude's Discretion asks for drag handles plus buttons, at the current dnd-kit.
  **Issue:** #230
- **Decision:** Quick actions are a region above the sections and not in the arrangement; the empty-catalog welcome no longer has its own New Song button (it points to the quick action). While the arrangement is read, the sections area shows a loader; when it cannot be read, the default arrangement is shown.
  **Why:** AC "Quick actions are always shown; not a hideable section"; two New Song buttons on one page would be redundant.
  **Issue:** #230
- **Decision:** Scan Library reads `GET /media/status` every 30 s while idle and every 5 s while disabled; the scan this page started counts as finished once a read made after the start no longer names it as `activeScanJobId` (queued or running). A start answered `alreadyInProgress` says a scan is already running (`startScan` now returns that flag).
  **Why:** Claude's Discretion (poll cadence; "pressing during a scan reports that one is running").
  **Issue:** #230
- **Decision:** Rule 1: the Customize dialog's close button had no accessible name (axe `button-name`, found by the e2e walk); it is now "Close", as the app's other dialogs do, with a component test.
  **Why:** Accessibility gate.
  **Issue:** #230
- **Decision:** e2e `dashboard-customize.spec.ts` runs on a fresh container (`@root-only`), like `dashboard.spec.ts`, so the saved arrangement is seen by no other spec.
  **Why:** The arrangement is instance-wide state.
  **Issue:** #230
