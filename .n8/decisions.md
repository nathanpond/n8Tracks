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
