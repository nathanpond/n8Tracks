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
