# Project conventions

Shared build conventions the planned stories refer to as "the M2 conventions" or "the project conventions". A story's own acceptance criteria and discretion lines take precedence where they differ.

- API: versioned under `/api/v1`, JSON in camelCase, IDs are UUIDv7 strings. Errors are Problem Details (`application/problem+json`) with a stable machine-readable `code`.
- Concurrency: every editable record has an integer `revision`, starting at 1 and incremented on each successful write. A write sends the revision it read (`If-Match: "<revision>"`). A stale write returns 409 with `code: revision_conflict` and `current`, the full current record; the client works out which fields differ from what it loaded. `If-Match` is a quoted integer; anything else is 400 `invalid_revision`. A write without a revision returns 428.
- Authentication: the web UI uses a cookie session; unsafe methods from the browser also require an anti-forgery header. Non-browser clients use `Authorization: Bearer <token>`. Until the sign-in story lands, endpoints are open on the milestone branch.
- Authorization: every `/api/v1` endpoint declares the scope it needs. A cookie session holds every scope.
- Persistence: EF Core entities and snake_case tables in Infrastructure, application services in Application, endpoints in Api, per the layering guard. Each schema change is one EF Core migration.
- Frontend: Mantine components, React Router routes, data access through small hooks over `fetch` resolved against the page base URL. New screens are reachable from the sidebar of the signed-in shell.
- Tests each story adds: unit tests for domain rules, API integration tests (including the refusal cases), component tests for new UI states, and at least one end-to-end test in `e2e/` that walks the story's Demo and scans each visited state with the accessibility helper.
- Logging: any new field holding lyrics, styles, prompts, tokens, or passwords is added to the redaction policy's name list in the same story.
- Time: stored and returned as UTC ISO 8601; shown in the UI in the configured time zone.
