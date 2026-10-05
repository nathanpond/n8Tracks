# n8Tracks

A self-hosted authoring workspace and catalog for AI-assisted music: a durable system of record for lyrics, creation parameters, generated outputs, and creative lineage.

**Status:** just initialized. The repository currently holds the product requirements ([docs/PRD.md](docs/PRD.md)), a layered ASP.NET Core backend skeleton with a health endpoint and environment-variable configuration, a web shell that shows the version and live health, a browser extension skeleton, an MCP gateway skeleton, and an Aspire AppHost that runs them together locally. Nothing described in the PRD is built yet.

## Build and test

Requires the .NET 10 SDK (pinned in `global.json`).

```sh
dotnet build
dotnet test
dotnet format --verify-no-changes
```

Warnings are errors, and .NET analyzers and code-style rules run as part of the build (`Directory.Build.props`).

### Continuous integration

Every pull request to `main` runs one gate, `.github/workflows/ci.yml`, and the single check named `ci` is green only when all of it passed:

| Job | Runs |
| --- | --- |
| `dotnet` | `dotnet build -warnaserror` (Release), `dotnet format --verify-no-changes`, and `dotnet test` for the whole solution |
| `web` | `npm ci`, then `lint`, `typecheck`, `format:check`, `test`, and `build` in `web/` |
| `extension` | the same in `extension/`, plus `npm run package`; the zip is kept as the `extension-zip` artifact |
| `container` | builds both Docker images for the runner, runs `scripts/smoke-docker.sh` against them, then builds both for `linux/amd64` and `linux/arm64`; nothing is pushed. The smoke-tested application image is kept for one day as the `app-image` artifact (`n8tracks-image.tar.gz`, for `docker load`) |
| `e2e` | `npm ci`, then `lint`, `typecheck`, `format:check`, and the script tests in `e2e/`, then the Playwright suite against the application image the `container` job built |
| `guards` | the checks behind "warnings are errors": `scripts/check-suppressions.sh` (a suppression needs a one-line rationale, and the strictness settings may not be weakened) and `scripts/check-canaries.sh` (a known-bad file in every project must fail its build, lint, and type check), each preceded by its own fixture test; and the tests of the release and publishing scripts under `scripts/tests/` |

The canary check can be run locally: `scripts/check-canaries.sh` (about half a minute; it needs the .NET SDK, Node, and npm, and restores packages as a build does). It works in a temporary copy of the working tree, which it removes, so it changes nothing in the repository. It builds every `*.csproj` with a canary source file that must fail with a nullable warning (CS8618) and an analyzer warning (CA2200) as errors, and runs `npm run lint` and `npm run typecheck` in `web/`, `extension/`, and `e2e/` with canary files that must fail with the marked rules and error codes. What the two guard checks catch, and what neither does, is listed at the top of `scripts/check-suppressions.py` and summarised in [docs/conventions.md](docs/conventions.md).

Within a job every check runs even after an earlier one failed, so one run shows all the failures; a failed job lists its failed steps in the run summary. The workflow is also callable (`workflow_call`), so publishing runs the same gate. A job added to the workflow must be added to the `needs:` of the `ci` job.

### Publishing `edge`

Every push to `main` runs `.github/workflows/publish-edge.yml`: the same gate first, and only if it is green, the [`edge` images](#edge-images) of that commit are pushed to GHCR and the extension zip of the same commit, stamped with the same version, is kept for 14 days as the `extension-zip-edge` artifact of the run. Nobody builds or pushes images by hand. The run's summary lists the tags, their digests, and the commands to pull them.

- **Nothing is half-published.** Both images are pushed by digest, untagged, before any tag exists; `scripts/publish-tags.sh` then tags both. If that fails part-way, it puts the `edge` tag of both images back where it started (on the very first run, it removes it) and the run fails. The `edge-<sha>` tags and untagged digests of a failed run are left in place.
- **One publish at a time.** A publish in progress is never cancelled by a newer push. A run still waiting for its turn is replaced by a newer one, so not every commit is guaranteed an `edge-<sha>` tag.
- **`edge` never moves backwards.** If a newer commit has reached `main` by the time a run is ready to tag, the run still pushes its `edge-<sha>` tags, leaves `edge` alone, and says so in its summary.
- **By hand.** The workflow can be started from the Actions tab (or `gh workflow run publish-edge.yml --ref main`) to publish `main` again. Started from any other ref it runs the gate and builds both images, and pushes, tags, and uploads nothing.

The names come from `scripts/edge-version.sh` (the short sha is the first 7 characters of the commit ID; a `VERSION` that already has a pre-release suffix gets `.edge.<sha>` appended instead of `-edge.<sha>`). Both scripts have tests that publish nothing and run in the gate: `scripts/tests/edge-version/run.sh` and `scripts/tests/publish-tags/run.sh`, the second against a stand-in registry.

### Releasing

Pushing a version tag (`v1.4.2`, or `v1.5.0-rc.1` for a pre-release) runs `.github/workflows/release.yml`: the release rules first, then the same gate, and only if both pass, the versioned images of both components are pushed to GHCR and a GitHub release is created with the extension zip and its checksum attached. A published version is never overwritten, and re-running a failed run completes it. How to cut a release, what a failed run leaves behind, and how to roll back are in [docs/releasing.md](docs/releasing.md).

The workflow holds no rules of its own: `scripts/release-tags.sh` decides what a tag publishes, `scripts/published-tags.sh` lists what is already published, `scripts/publish-tags.sh` tags the images, and `scripts/release-github.sh` creates or completes the GitHub release. Their tests publish nothing and run in the gate (`scripts/tests/release-tags/run.sh`, `scripts/tests/publish-tags/run.sh`, `scripts/tests/release-github/run.sh`).

### Branch and tag rules

Changes reach `main` only through a pull request whose `ci` check is green. Nobody can push to `main` directly or merge while `ci` is red or still running, and that includes the repository owner: the rule has no bypass. No approval is required, and a branch does not have to be up to date with `main` to merge.

The rule is the `main-pr-required` ruleset. Its definition is committed as `.github/rulesets/main-pr-required.json`, in the shape GitHub's REST API takes, and GitHub is brought in line with the committed files by:

```sh
scripts/apply-rulesets.sh           # create or update each ruleset, matched by name
scripts/apply-rulesets.sh --check   # change nothing; report where GitHub differs from the files
```

Both need `gh` signed in as an administrator of the repository, and `jq`. To change a rule, edit the JSON, merge it, then run the script. A ruleset that has no file is left alone; nothing is deleted. `scripts/tests/apply-rulesets/run.sh` tests the script without touching GitHub.

Release tags have two rulesets of their own, defined beside it and applied by the same script: `release-tags-create` lets only the repository admin role create a `v*` tag, and `release-tags-immutable` stops everyone, the owner included, from moving or deleting one. What that means for a release is in [docs/releasing.md](docs/releasing.md#who-can-release).

### Dependency updates

Dependabot (`.github/dependabot.yml`) checks GitHub Actions, NuGet packages, the .NET SDK version in `global.json`, the npm projects (`web/`, `extension/`, `e2e/`), and the base images of both Dockerfiles every week. Minor and patch updates arrive as one pull request per ecosystem, each major update as its own, with at most five open per ecosystem, titled `chore(deps): ...`. Security updates arrive separately as they are needed. All of them go through the same `ci` gate. Secret scanning and push protection are on for the repository.

### Backend layout

| Project | Holds | May reference |
| --- | --- | --- |
| `src/n8Tracks.Domain` | Domain types and rules | nothing (no project, no NuGet package) |
| `src/n8Tracks.Application` | Application services: the one place business rules are applied | Domain |
| `src/n8Tracks.Infrastructure` | Persistence and other adapters | Application, Domain |
| `src/n8Tracks.Api` | HTTP endpoints and the composition root | Application, Infrastructure, ServiceDefaults |
| `src/n8Tracks.Gateway` | The MCP gateway: a separate service that reaches n8Tracks over HTTP only | ServiceDefaults |
| `src/n8Tracks.ServiceDefaults` | OpenTelemetry wiring shared by the app and the gateway (see [Telemetry](#telemetry)); no business logic | nothing |
| `src/n8Tracks.AppHost` | Local development only: the Aspire AppHost that runs the app, the gateway, and the frontend together (see [Run](#run)). In neither Docker image | Api, Gateway (started as processes, not referenced as code) |

Endpoints go through the application layer. Only the Api's composition root (`Program.cs` and the `n8Tracks.Api.DependencyInjection` namespace) may touch Infrastructure or Entity Framework Core. `tests/n8Tracks.Architecture.Tests` fails the build's test run when a project reference or a type dependency breaks these rules; a new project under `src/` must be added to the table in `ProjectReferenceTests`.

## Run

One command starts the whole stack for local development: the app, the MCP gateway, and the frontend dev server, with a dashboard for their logs and traces. Install the frontend's dependencies once, then run the Aspire AppHost from the repository root:

```sh
(cd web && npm install)
dotnet run --project src/n8Tracks.AppHost
```

| Started | Address |
| --- | --- |
| The app (`api`) | `http://localhost:8787` |
| The MCP gateway (`gateway`), pointed at the app | `http://localhost:8788` |
| The frontend dev server (`frontend`), with hot reload | `http://localhost:5173` |
| The Aspire dashboard | `http://localhost:15187` |

The console prints a line starting `Login to the dashboard at`: open that URL (it carries the dashboard's login token; no browser is opened for you). The dashboard lists the three resources and their state, and shows each one's console output, structured logs, and traces. Stop everything with Ctrl+C.

- **Data.** The AppHost creates `src/n8Tracks.AppHost/.localdata` and `.localdata/media` (git-ignored) and passes them as `N8TRACKS_DATA_PATH` and `N8TRACKS_MEDIA_PATH`, so a fresh clone starts healthy.
- **Settings.** The app and the gateway are configured the way a container is, only through the `N8TRACKS_*` variables under [Configuration](#configuration) and [Gateway settings](#gateway-settings) (and `TZ`). Whatever you set in your own environment replaces the AppHost's default, with no code change: `N8TRACKS_LOG_LEVEL=Debug dotnet run --project src/n8Tracks.AppHost`. A relative path you set is taken relative to the directory you run the command in.
- **Ports.** 8787, 8788, and 5173 are fixed (the first two follow `N8TRACKS_PORT` and `N8TRACKS_GATEWAY_PORT`), and nothing sits in front of them. If one is in use, that resource fails to start and says so; there are no fallback ports.
- **Telemetry.** The AppHost sets `OTEL_EXPORTER_OTLP_ENDPOINT` of the app and the gateway to the dashboard, which is what turns their [telemetry](#telemetry) on. The app's log records are redacted before they are exported, as always, and query-string values in traces show as `Redacted` on the dashboard too: the AppHost removes the two variables Aspire sets to turn that redaction off (`OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION` and `OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION`), and the services ignore them anyway.
- **Frontend.** The AppHost never runs `npm install`. If `web/node_modules` is missing, the `frontend` resource fails with a message saying to run `npm install` in `web/`; the app and the gateway still start. After installing, start the resource from the dashboard or restart the AppHost.
- **No HTTPS.** The dashboard and its collector listen on plain HTTP on localhost, so no development certificate has to be trusted. The warning `No trusted Aspire development certificate was found` at startup can be ignored.

The AppHost (`src/n8Tracks.AppHost`, Aspire) is development tooling only. It is not part of either Docker image: both build contexts leave it out, and `tests/n8Tracks.AppHost.Tests` and `scripts/smoke-docker.sh` fail if that changes.

### Run one component

Each part also runs on its own.

The app:

```sh
dotnet run --project src/n8Tracks.Api
```

Then `GET http://localhost:8787/health`. The launch profile sets `N8TRACKS_DATA_PATH=./.localdata` (created by a Debug build under `src/n8Tracks.Api`, git-ignored; a different folder from the AppHost's) and `ASPNETCORE_ENVIRONMENT=Development`, which adds the OpenAPI document at `/openapi/v1.json` (see [Configuration](#configuration)).

The gateway, next to a running app: `dotnet run --project src/n8Tracks.Gateway` (see [MCP gateway](#mcp-gateway)).

The frontend dev server, next to a running app: `npm run dev` in `web/` (see [Frontend project](#frontend-project)).

## Run with Docker

n8Tracks runs from one image: the app and its web interface, on port 8787. The image is built from this repository for `linux/amd64` and `linux/arm64`. Released versions are published under their version (`ghcr.io/nathanpond/n8tracks:1.4.2`; see [docs/releasing.md](docs/releasing.md) for which tags a release gets); until the first release the only published images are the [`edge` images](#edge-images), the newest build of `main`. The MCP gateway is a second, separate image: see [The gateway image](#the-gateway-image).

### Edge images

The newest build of `main` is published after every merge, for `linux/amd64` and `linux/arm64`, and can be pulled without logging in:

```sh
docker pull ghcr.io/nathanpond/n8tracks:edge
docker pull ghcr.io/nathanpond/n8tracks-gateway:edge
```

| Tag | Is |
| --- | --- |
| `edge` | The newest commit of `main` that passed the gate. It moves with every merge. |
| `edge-<short sha>` | The build of one commit (the first 7 characters of its ID), for pinning or going back. Not every commit has one: when merges come faster than publishing, a run that is still waiting is replaced by a newer one. Old ones are not cleaned up yet. |

**`edge` is not a release.** It is whatever `main` holds: it has passed the automated gate and nothing else, it can change several times a day, and nothing promises that data written by one `edge` build is readable by the next. Use it to try what is coming, not to keep a library you care about.

An `edge` build reports its version as `<VERSION>-edge.<short sha>` (for example `0.1.0-edge.abc1234`) on the page, from `/health`, and from the gateway's `/health`, so you can tell which commit is running. The application and the gateway are published together from the same commit; use the same tag for both. To use an `edge` image with the Compose example, replace its `build:` line with `image: ghcr.io/nathanpond/n8tracks:edge`.

The images carry build provenance and a software bill of materials (SBOM) as attestations stored beside them. That is why `docker buildx imagetools inspect ghcr.io/nathanpond/n8tracks:edge` lists two extra entries with the platform `unknown/unknown` next to `linux/amd64` and `linux/arm64`: those are the attestations, not images, and `docker pull` ignores them. Read them with:

```sh
docker buildx imagetools inspect ghcr.io/nathanpond/n8tracks:edge --format '{{ json .Provenance }}'
docker buildx imagetools inspect ghcr.io/nathanpond/n8tracks:edge --format '{{ json .SBOM }}'
```

The images are not signed. How they are published is under [Publishing `edge`](#publishing-edge).

### Quick start with Compose

[`docker-compose.example.yml`](docker-compose.example.yml) builds the image and starts it:

```sh
docker compose -f docker-compose.example.yml up -d --build
```

Then open `http://localhost:8787/`: the page shows the version and the health of the instance, and `docker ps` shows the container as `healthy`. The example keeps the app's data in `./data` and reads media from `./media`, next to the Compose file. To make it your own, copy it to `docker-compose.yml`, set `PUID`, `PGID`, `TZ`, and the two host folders, and run `docker compose up -d --build`.

Removing the container and creating it again (`docker compose down`, then `up -d`) keeps everything: the database lives in the data folder, not in the container.

With `restart: unless-stopped`, a container that cannot start (an invalid setting, a data folder it cannot write to, a database from a newer version) shows as restarting, and `docker compose logs` repeats the same error line each time.

Without Compose:

```sh
docker build -t n8tracks:dev .
docker run -d --name n8tracks -p 8787:8787 --stop-timeout 45 \
  -e PUID=1000 -e PGID=1000 -e TZ=Etc/UTC \
  -v "$PWD/data:/data" -v "$PWD/media:/media:ro" \
  n8tracks:dev
```

`--stop-timeout 45` (`stop_grace_period: 45s` in the Compose example) gives a running background job, such as a backup, the 30 seconds it is allowed to finish when the container stops. With Docker's default of 10 seconds the job is cut off; it is then marked failed, "interrupted by restart", when the app next starts.

### The user the app runs as: `PUID` and `PGID`

| Variable | Default | Meaning |
| --- | --- | --- |
| `PUID` | `1000` | Numeric ID of the user the app runs as. Files it creates belong to this user. |
| `PGID` | `1000` | Numeric ID of that user's group. |

Use the IDs of the account that owns your data folder on the host (`id -u` and `id -g`). The container starts as root only to apply them: if the top-level owner of `/data` (or of `/backup`, when mounted) is someone else, it hands that folder and everything in it to `PUID:PGID`, then drops to that user, with no other groups, and starts the app. Files the app creates are readable by others and writable only by their owner (umask 022). `/media` is never touched.

- A value that is not a whole number from 0 upwards stops the container with exit code 1 and one error line naming the variable.
- `0` is accepted and logs a warning: the app then runs as root.
- If the owner cannot be changed (a read-only or root-squashed share), the container logs a warning and carries on; the app stops with its own error if it cannot write to `/data`.
- If you start the container as a user yourself (`user: "1000:1000"` in Compose, `--user` with `docker run`), `PUID` and `PGID` are not read and nothing is changed: that user must already be able to write to `/data`.

Only `/data` and `/backup` get this treatment. If you point `N8TRACKS_DATA_PATH` somewhere else inside the container, make that folder writable for `PUID:PGID` yourself.

### Mounts

| Path in the container | Purpose | Notes |
| --- | --- | --- |
| `/data` | The app's own data, the database included. | Required, writable. Declared as a volume: without a mount Docker gives the container an anonymous volume, which is lost when the container is removed with its volumes. Mount a host folder or a named volume. |
| `/media` | Your media files. | Mount it read-only (`:ro`); n8Tracks never writes there. Not mounted or not reachable: the app still runs, health is `degraded`, and the container stays `healthy`. |
| `/backup` | Backups. | Optional, writable. |

The image does not contain `/media` or `/backup`: one that is not mounted does not exist in the container.

### Settings

The app reads the variables under [Configuration](#configuration); the Compose example lists each with its default. In a container, leave `N8TRACKS_DATA_PATH`, `N8TRACKS_MEDIA_PATH`, and `N8TRACKS_BACKUP_PATH` alone and change what is mounted there. If you change `N8TRACKS_PORT`, change the container side of the port mapping too (`"8787:9000"` for port 9000). `ASPNETCORE_ENVIRONMENT` is `Production` in the image.

### Under a sub-path

Behind a reverse proxy that serves n8Tracks at, say, `https://nas.example/n8tracks`, set:

```yaml
    environment:
      N8TRACKS_BASE_URL: https://nas.example/n8tracks
```

and have the proxy forward the path unchanged to port 8787. The app then answers only under `/n8tracks` (the page at `/n8tracks/`, health at `/n8tracks/health`); anything outside it is 404. The container health check follows the setting by itself.

### Signing in, HTTP, and HTTPS

Everything but the sign-in page, first-run setup, and `/health` needs the administrator to be signed in. A session lasts 30 days from its last use and survives restarts; "Sign out everywhere" in the user menu ends every session. After five wrong passwords within 15 minutes, sign-in is refused for 15 minutes. Settings → Account changes the password (the current one is required, and wrong guesses count toward the same limit); changing it ends every other session. If a session ends while a page is open, a sign-in prompt appears over the page, and once you sign in again the action you took goes through and the page stays as it was.

The app works the same over plain HTTP on a trusted network and behind an HTTPS reverse proxy. Have the proxy set `X-Forwarded-Proto` (and `X-Forwarded-Host` if it changes the host name): the session cookie is then marked `Secure`. The app takes these headers from any address, because the address of your proxy is not known in advance, so **publish port 8787 only to the proxy or to a trusted network**, never directly to the internet: anyone who can reach the port can claim the request came over HTTPS.

### Forgot your password

There is no email reset. With a shell on the Docker host, set a new password from inside the running container (the name is `n8tracks` in the Compose example):

```sh
docker exec -it n8tracks n8tracks reset-password
```

It asks for the new password twice without showing it, holds it to the same rule as setup (12 to 256 characters), and names the administrator it changed. The app can keep running: every browser session ends, so a signed-in browser goes to the sign-in page at its next request, and a sign-in lockout from wrong passwords is cleared. API, extension, and gateway credentials keep working. Nothing else changes.

For a script, give the password on standard input instead; the first line is the password, without its line ending:

```sh
printf '%s\n' "$NEW_PASSWORD" | docker exec -i n8tracks n8tracks reset-password --password-stdin
```

Without a terminal and without `--password-stdin` the command refuses. It also refuses, changing nothing, when setup has never been completed, when the database schema does not match the image's version (start the app first, so it upgrades the database; the command never applies a migration), or while an upgrade holds the migration lock. It runs as the `PUID`/`PGID` user, as the app does. Prompts and messages go to standard error, the password is never printed or logged, and the exit code is 0 on success and 1 otherwise. The command writes one Information line recording the reset (without the password), and the app logs the reset at the next sign-in.

### Container health check

The image's health check runs the app binary in a second mode, `dotnet /app/n8Tracks.Api.dll --healthcheck`, so the image needs no `curl`. It requests `<base path>/health` on the loopback interface at `N8TRACKS_PORT`, and passes on 200 (`healthy` or `degraded`); 503, no answer within 4 seconds, or an invalid port or base URL fails it. It runs every 30 seconds (every 5 while starting, on Docker 25 or later), and three failures in a row mark the container `unhealthy`.

### Log lines before the app starts

What the container writes before the app starts (the `PUID`/`PGID` errors and warnings, an owner change) has the keys of the application log, one JSON object per line:

```json
{"timestamp":"2026-10-04T05:50:21.118Z","level":"Error","message":"Invalid configuration: PUID must be a whole number from 0 to 4294967294, but was 'abc'.","properties":{"sourceContext":"n8Tracks.Entrypoint","variable":"PUID","reason":"must be a whole number from 0 to 4294967294, but was 'abc'."}}
```

### Building the image

```sh
docker build -t n8tracks:dev .
docker buildx build --platform linux/amd64,linux/arm64 .
```

The version comes from the root `VERSION` file; `--build-arg VERSION=<version>` overrides it, and `/health` reports whichever was used. The two-platform build needs a builder that supports it (the containerd image store, or `docker buildx create --driver docker-container`). Tests are not run in the image build.

`scripts/smoke-docker.sh` builds both images (the app and the gateway) for your machine and checks them end to end. For the app: health, the page, the user the app runs as, file ownership, a read-only media mount, no media mount, a sub-path, a restart and a re-creation on the same data, and the refusals (`PUID=abc`, a read-only `/data`). For the gateway: health next to the app on a shared network, the user it runs as, the container staying `healthy` while the app is stopped, another port, and the refusal to start without `N8TRACKS_API_URL`. It needs Docker, `curl`, and `python3`, uses host ports 18787 and 18788, and removes what it created.

It prints one `PASS <name>` or `FAIL <name>` line per assertion and runs them all: a failure does not stop the run, the failed lines are repeated at the end, the log of a container that failed an assertion is printed, and the exit code is 1. `N8TRACKS_SMOKE_SKIP_BUILD=1` tests the `n8tracks:dev` and `n8tracks-gateway:dev` images that already exist instead of building them, which is how CI runs it.

### The gateway image

The [MCP gateway](#mcp-gateway) has its own image, built from `src/n8Tracks.Gateway/Dockerfile` with the repository root as the build context:

```sh
docker build -f src/n8Tracks.Gateway/Dockerfile -t n8tracks-gateway:dev .
docker buildx build -f src/n8Tracks.Gateway/Dockerfile --platform linux/amd64,linux/arm64 .
```

It holds the gateway alone, for `linux/amd64` and `linux/arm64`, and listens on port 8788. The version comes from the root `VERSION` file, or from `--build-arg VERSION=<version>`, exactly as for the app image, and the gateway's `/health` reports it. Build both images from the same version: the gateway is `degraded` when its major and minor numbers differ from the app's. The build reads `src/n8Tracks.Gateway/Dockerfile.dockerignore` instead of the root `.dockerignore`, so only the gateway's sources are sent to Docker.

With Compose: in [`docker-compose.example.yml`](docker-compose.example.yml), remove the `# ` in front of the `n8tracks-gateway` service and its lines, then start it the same way:

```sh
docker compose -f docker-compose.example.yml up -d --build
```

`docker ps` then shows both containers as `healthy`, and `http://localhost:8788/health` answers:

```json
{ "status": "healthy", "upstream": "reachable", "version": "0.1.0", "compatible": true }
```

Without Compose, put both containers on one Docker network and give the gateway the app's address there:

```sh
docker network create n8tracks
docker run -d --name n8tracks --network n8tracks -p 8787:8787 \
  -e PUID=1000 -e PGID=1000 \
  -v "$PWD/data:/data" -v "$PWD/media:/media:ro" \
  n8tracks:dev
docker run -d --name n8tracks-gateway --network n8tracks -p 8788:8788 \
  -e N8TRACKS_API_URL=http://n8tracks:8787 \
  n8tracks-gateway:dev
```

- `N8TRACKS_API_URL` is required: the URL of n8Tracks as the gateway container reaches it, which is the app's container or service name and the port it listens on inside its container, not `localhost` and not the published host port. If the app runs under a sub-path, include the path (`http://n8tracks:8787/n8tracks`). Without the variable the container stops with exit code 1 and one error line naming it; under `restart: unless-stopped` it shows as restarting.
- The other settings are under [Gateway settings](#gateway-settings). If you change `N8TRACKS_GATEWAY_PORT`, change the container side of the port mapping too.
- The gateway runs as the image's fixed unprivileged user (`app`, UID and GID 1654). It writes no file, so the image has no volume, no mount, and no `PUID` or `PGID`. It has no time zone setting either: its log is in UTC.
- The health check runs the gateway binary in a second mode, `dotnet /app/n8Tracks.Gateway.dll --healthcheck`, with the timing of the app image's check. It requests `/health` on the loopback interface at `N8TRACKS_GATEWAY_PORT` and passes on 200. The gateway answers 200 whether it is `healthy` or `degraded`, so the container stays `healthy` while n8Tracks is stopped, unreachable, or of another version; only a gateway that does not answer within 4 seconds becomes `unhealthy`. To see whether n8Tracks is reachable, read the gateway's `/health`.

## Health

`GET /health` (under the base URL path, if there is one) reports the instance in one JSON document. It needs no sign-in, is never cached (`Cache-Control: no-store`), answers `HEAD` too, and is checked afresh on every request.

```json
{
  "status": "degraded",
  "version": "0.1.0",
  "timeZone": "Europe/Oslo",
  "components": {
    "application": { "status": "healthy", "detail": "running" },
    "database": { "status": "healthy", "detail": "reachable" },
    "migrations": { "status": "healthy", "detail": "up to date", "lastApplied": "20261004042959_InitialCreate" },
    "media": { "status": "degraded", "detail": "unavailable" }
  }
}
```

| Component | Healthy when | Otherwise |
| --- | --- | --- |
| `application` | The app answers. | |
| `database` | A trivial query succeeds within 2 seconds. | `unhealthy`, detail `unreachable`. |
| `migrations` | Always, once the app has started: `lastApplied` is the newest migration applied at startup. | |
| `media` | `N8TRACKS_MEDIA_PATH` is a directory that can be listed within 2 seconds. A read-only mount is fine. | `degraded`, detail `unavailable`. |

The overall `status` is the worst of the components. The HTTP status is 200 for `healthy` and `degraded` (the app keeps working without the media mount) and 503 for `unhealthy`, so a container health check can use the status code alone. The response never contains paths, connection strings, or error text: the cause of a failure is written to the log once, as a Warning, when a component stops being healthy, and its recovery as an Information line.

## Frontend

The backend serves a built frontend from its web root, the `wwwroot` folder under its working directory: `src/n8Tracks.Api/wwwroot` in a local run (git-ignored; the .NET build never runs npm) and `/app/wwwroot` in the image. Everything is under the base URL path, if there is one:

- `GET <base>/` and any unknown path (a deep link) return `index.html`, the shell, with `<base href="<base>/">` put in place of the `<!--n8tracks-base-->` comment in its `<head>`, so one build works at the root and under any sub-path. The shell is read once at startup and served with `Cache-Control: no-cache`.
- Only `GET` and `HEAD` get the shell, and never a path whose first segment is `api`, `health`, `openapi`, or `assets`, or whose last segment has a file extension: those are 404 when nothing is there.
- Files under `assets/` are served as `public, max-age=31536000, immutable`; other files as `no-cache`.
- `GET <base>` without the trailing slash redirects (308) to `<base>/`.
- With no `index.html` in the web root, the shell routes answer a plain-text 404 saying the frontend is not built; the API and `/health` work as usual.

### Frontend project

The frontend is `web/`: React, TypeScript, Vite, and Mantine. It needs Node 24 (pinned in `.nvmrc`; `nvm use` picks it up) and npm. Run these in `web/`:

| Command | Does |
| --- | --- |
| `npm ci` | Installs the dependencies from the lockfile. |
| `npm run dev` | Serves the app at `http://localhost:5173/` with hot reload, proxying `/api` and `/health` to the backend at `N8TRACKS_API_URL`, by default `http://localhost:8787` (start it with `dotnet run --project src/n8Tracks.Api`). The dev server runs at the root only. The AppHost runs this for you (see [Run](#run)). |
| `npm run build` | Typechecks, then builds into `web/dist`. |
| `npm run lint` | ESLint, with typed rules, React hooks rules, and accessibility rules. No warnings allowed. |
| `npm run typecheck` | Strict TypeScript check. |
| `npm run format:check` | Checks formatting with Prettier; `npm run format` fixes it. |
| `npm test` | Runs the component and unit tests once (Vitest, Testing Library, jsdom). |

To have the backend serve the built frontend, copy the build into its web root (from the repository root), then start the backend and open `http://localhost:8787/`:

```sh
(cd web && npm ci && npm run build)
rm -rf src/n8Tracks.Api/wwwroot && cp -R web/dist src/n8Tracks.Api/wwwroot
```

The build uses relative URLs and resolves every request against the page's base URL, so the same `web/dist` works at the root of a hostname and under a sub-path.

Settings → System shows the version and the health report, refreshed every 30 seconds while the tab is visible. The colour scheme (light, dark, or auto, which follows the system) is chosen in the header and remembered in the browser. Every colour pair that carries text is in `web/src/theme/palette.ts`, and a test holds each to WCAG 2.1 AA contrast.

## End-to-end tests

`e2e/` is a Playwright suite that drives the real application image in Chromium and scans every state it reaches with axe for WCAG 2.1 A and AA violations, once in light and once in dark. It needs Docker, Node 24, and the image `n8tracks:dev`. From the repository root:

```sh
docker build -t n8tracks:dev .
cd e2e
npm ci
npx playwright install chromium
npm test
```

`npx playwright install chromium` downloads the browser and is needed once per Playwright version. Rebuild the image after changing `web/` or the backend: the suite tests the image, not the working tree.

Each run starts three containers from the image, waits up to 60 seconds for them, and removes them and their temporary data directories afterwards:

| Container | Host port | Is |
| --- | --- | --- |
| `n8tracks-e2e-root` | 18787 | healthy, at the root of the hostname |
| `n8tracks-e2e-subpath` | 18788 | healthy, under the sub-path `/n8tracks` |
| `n8tracks-e2e-nomedia` | 18789 | no media mounted, so it reports `degraded` |

Every test runs twice, as the projects `root` and `subpath` (`npm test -- --project root` runs one). Containers left by an aborted run are removed first. The run stops at once with a message if Docker is not running, if the image does not exist, or if one of the ports is taken. `scripts/smoke-docker.sh` uses 18787 and 18788 as well, so do not run the two at the same time. Set `N8TRACKS_E2E_IMAGE` to test another image.

A failed test keeps a trace under `e2e/test-results/`; the failure message has the `npx playwright show-trace` command for it. Tests do not retry locally and run in one worker.

| Command (in `e2e/`) | Does |
| --- | --- |
| `npm test` | Starts the containers and runs the suite in both projects. |
| `npm run lint` | ESLint, with typed rules and the Playwright rules. No warnings allowed. |
| `npm run typecheck` | Strict TypeScript check. |
| `npm run format:check` | Checks formatting with Prettier; `npm run format` fixes it. |

The pattern for later features: one spec file per area in `e2e/tests/`, shared helpers in `e2e/support/`, and `expectAccessibleInLightAndDark(page)` (or `expectNoA11yViolations(page)` for one scan) after each state a test reaches. A test tagged `@root-only` or `@subpath-only` runs in that project alone. The suite covers Chromium only; it does not cover Firefox, Safari, or screen-reader behaviour.

## Browser extension

The extension is `extension/`: a Chrome Manifest V3 project in TypeScript, built with Vite. For now it is a skeleton: a popup that shows the name, the version, and "Not connected to n8Tracks", and a service worker that does nothing. It needs Node 24 (the same `.nvmrc` as `web/`) and npm, and it is not part of the .NET solution. Run these in `extension/`:

| Command | Does |
| --- | --- |
| `npm ci` | Installs the dependencies from the lockfile. |
| `npm run build` | Typechecks, empties `extension/dist`, and builds the extension into it. |
| `npm run package` | Builds, then zips the contents of `dist` as `extension/n8tracks-extension-<version>.zip`, deleting the zips of other versions. |
| `npm run lint` | ESLint with typed rules. No warnings allowed. |
| `npm run typecheck` | Strict TypeScript check. |
| `npm run format:check` | Checks formatting with Prettier; `npm run format` fixes it. |
| `npm test` | Builds into `dist` (overwriting it), then runs the unit tests and the checks on the built manifest once (Vitest). |

To load it unpacked:

1. Run `npm ci` and `npm run build` in `extension/` (or unzip a packaged zip into a folder).
2. In Chrome, open `chrome://extensions` and turn on Developer mode.
3. Choose Load unpacked and select `extension/dist` (or the unzipped folder).
4. Click the n8Tracks icon in the toolbar; the popup shows the name and version. After a rebuild, press the reload button on the extension's card.

The source manifest is `extension/manifest.json`; the build writes `dist/manifest.json` from it, replacing source paths with built ones and filling in the version. The extension asks for the `storage` permission only, and for `https://suno.com/*` only as an optional host, which Chrome grants when the user agrees. The build and the tests fail if the manifest gains another permission or host, a content script, or `externally_connectable`.

The version is the root `VERSION` file, or the `N8TRACKS_VERSION` environment variable when it is set (for example `N8TRACKS_VERSION=0.1.0-edge.abc1234 npm run package`). Chrome accepts only numbers in a manifest `version`, so a version with a pre-release suffix is written as `version` `0.1.0` and `version_name` `0.1.0-edge.abc1234`; the popup and the zip file name use the full string. The build also keeps the version in `extension/package.json` and its lockfile equal to the `VERSION` file.

`extension/fixtures/` holds sanitized examples of Suno responses for later adapter tests.

## MCP gateway

The gateway is `src/n8Tracks.Gateway`: a separate ASP.NET Core service that will expose n8Tracks to MCP clients. For now it is a skeleton with no MCP endpoint and no tools: it starts, checks that it can reach n8Tracks, and reports that on its own health URL. It talks to n8Tracks only over HTTP, at `N8TRACKS_API_URL`, and holds no business logic and no catalog data.

Build and test it with the rest of the solution (`dotnet build`, `dotnet test`), or on its own:

```sh
dotnet build src/n8Tracks.Gateway
dotnet test tests/n8Tracks.Gateway.Tests
```

Run it next to the app (start that first with `dotnet run --project src/n8Tracks.Api`):

```sh
dotnet run --project src/n8Tracks.Gateway
```

Then `GET http://localhost:8788/health`. The launch profile sets `N8TRACKS_API_URL=http://localhost:8787`; anywhere else, set it yourself:

```sh
N8TRACKS_API_URL=http://localhost:8787 dotnet src/n8Tracks.Gateway/bin/Debug/net10.0/n8Tracks.Gateway.dll
```

### Gateway settings

The gateway is configured only through environment variables, read once at startup. An empty or whitespace-only value counts as unset.

| Variable | Default | Example | Meaning |
| --- | --- | --- | --- |
| `N8TRACKS_API_URL` | none (required) | `https://nas.example/n8tracks` | URL of n8Tracks as the gateway reaches it. An absolute `http` or `https` URL without query string, fragment, or user info. A path is kept: the example is checked at `https://nas.example/n8tracks/health`. |
| `N8TRACKS_GATEWAY_PORT` | `8788` | `9001` | Port the gateway listens on: plain HTTP, all interfaces. A whole number from 1 to 65535. |
| `N8TRACKS_LOG_LEVEL` | `Information` | `Debug` | Minimum log level: `Trace`, `Debug`, `Information`, `Warning`, `Error`, or `Critical` (any letter case). |

These variables, and the optional `OTEL_` variables under [Telemetry](#telemetry), are all the gateway reads. It takes no setting from anywhere else .NET would look: not from a command-line argument (`--healthcheck`, under [Gateway health](#gateway-health), is the only argument with a meaning), not from any other environment variable (with an `ASPNETCORE_` or `DOTNET_` prefix or without one), and not from an `appsettings.json`. So no setting of .NET or ASP.NET Core changes what the gateway does, whichever of those it comes from: not `ASPNETCORE_URLS`, `ASPNETCORE_HTTP_PORTS`, `--urls`, or an entry under `Kestrel:Endpoints` (`N8TRACKS_GATEWAY_PORT` is the only way to set the listen address, and nothing adds a second one), not a `Logging` section, not `AllowedHosts`, not `ASPNETCORE_ENVIRONMENT`. The gateway ignores every other `N8TRACKS_` variable, the app's `N8TRACKS_PORT` included. Variables the .NET runtime reads for itself before the gateway's code runs (its garbage collector and diagnostics switches, such as `DOTNET_gcServer`) are outside this rule: they tune the runtime, not the gateway.

A missing or invalid value stops the gateway before it listens, with exit code 1 and one line per problem naming the variable, at any log level. The value of `N8TRACKS_API_URL` is never written. The gateway also exits with code 1 and one such line when its port is already in use.

The log is one JSON object per line on standard output, in the shape of .NET's JSON console formatter (not the app's shape):

```json
{"Timestamp":"2026-10-04T05:18:28.639Z","EventId":1,"LogLevel":"Error","Category":"n8Tracks.Gateway.Startup","Message":"Invalid configuration: N8TRACKS_API_URL is required: set it to the URL of n8Tracks, such as http://n8tracks:8787.","State":{"Variable":"N8TRACKS_API_URL","Reason":"is required: set it to the URL of n8Tracks, such as http://n8tracks:8787.","{OriginalFormat}":"Invalid configuration: {Variable} {Reason}"}}
```

Framework categories (`Microsoft`, `System`) are held at Warning unless the level is set higher.

### Gateway health

Started with `--healthcheck`, the gateway binary starts nothing: it requests `/health` from the gateway already running on the loopback interface at `N8TRACKS_GATEWAY_PORT`, writes one log line, and exits with code 0 for a 200 answer and 1 otherwise. The [gateway image](#the-gateway-image) uses this as its container health check.

`GET /health` on the gateway asks `<N8TRACKS_API_URL>/health` afresh on every request and answers 200 with `Cache-Control: no-store`, whatever it finds:

```json
{ "status": "healthy", "upstream": "reachable", "version": "0.1.0", "compatible": true }
```

| Field | Values |
| --- | --- |
| `upstream` | `reachable` when n8Tracks returned any complete HTTP response within 3 seconds, a 503 included; otherwise `unreachable`. Redirects are not followed, and at most 64 KB of the response is read. |
| `version` | The gateway's own version. |
| `compatible` | `true` when the `version` in n8Tracks' health response has the same major and minor numbers as the gateway's, `false` when it does not, and `null` when n8Tracks is unreachable or its answer has no readable version. |
| `status` | `healthy` only when `upstream` is `reachable` and `compatible` is `true`; otherwise `degraded`. |

The response never says why n8Tracks is unreachable. The gateway writes one Warning line when n8Tracks becomes unreachable, mismatched, or of unknown version (a short reason, never the URL), nothing while that lasts, and one Information line when it is reachable and compatible again.

### Gateway isolation

`tests/n8Tracks.Gateway.Tests/GatewayIsolationGuardTests.cs` fails if the gateway references another n8Tracks project, Entity Framework Core, or SQLite, directly or through another package or project. The one exemption is `n8Tracks.ServiceDefaults` (telemetry wiring), which is held to the same rule itself. It reads the gateway's project file, the dependency graph NuGet resolved for it, and the assemblies the built gateway references. It cannot see business rules written by hand inside the gateway; that is for review.

## Configuration

The app is configured only through environment variables, read once at startup. An empty or whitespace-only value counts as unset.

| Variable | Default | Example | Meaning |
| --- | --- | --- | --- |
| `N8TRACKS_PORT` | `8787` | `9000` | Port the app listens on: plain HTTP, all interfaces. A whole number from 1 to 65535. |
| `N8TRACKS_BASE_URL` | `http://localhost:<port>` | `https://nas.example/n8tracks` | Public URL of the app. An absolute `http` or `https` URL without query string, fragment, or user info. If it has a path, every route (including `/health`) is served under that path and anything outside it returns 404. |
| `TZ` | `UTC` | `Europe/Oslo` | Time zone that times are shown in: an IANA time zone ID. |
| `N8TRACKS_LOG_LEVEL` | `Information` | `Debug` | Minimum log level: `Trace`, `Debug`, `Information`, `Warning`, `Error`, or `Critical` (any letter case). |
| `N8TRACKS_DATA_PATH` | `/data` | `/srv/n8tracks/data` | Directory for the app's own data. It must exist and be writable. |
| `N8TRACKS_MEDIA_PATH` | `/media` | `/mnt/music` | Directory of your media files. It may be missing at startup. |
| `N8TRACKS_BACKUP_PATH` | `/backup` | `/mnt/backup` | Directory for backups. It may be missing at startup. |

Behind a reverse proxy on a sub-path, set `N8TRACKS_BASE_URL` to the public URL and have the proxy forward the path unchanged: with `https://nas.example/n8tracks`, the health check is `/n8tracks/health` and the prefix matches in any letter case. A single trailing slash is ignored. Path segments may contain only letters, digits, `.`, `_`, `~`, and `-`.

Relative paths resolve against the working directory.

The variables in the table, the optional `OTEL_` variables under [Telemetry](#telemetry), and `ASPNETCORE_ENVIRONMENT` (below) are all the app reads; the image's entrypoint also reads `PUID` and `PGID`. The app takes no setting from anywhere else .NET would look: not from a command-line argument (`--healthcheck` is the only argument with a meaning: the image's [container health check](#container-health-check)), not from any other environment variable (with an `ASPNETCORE_` or `DOTNET_` prefix or without one), and not from an `appsettings.json`. So no setting of .NET or ASP.NET Core changes what the app does, whichever of those it comes from: not `ASPNETCORE_URLS`, `ASPNETCORE_HTTP_PORTS`, `--urls`, or an entry under `Kestrel:Endpoints` (`N8TRACKS_PORT` is the only way to set the listen address, and nothing adds a second one), not a `Logging` section, not `AllowedHosts`, not `--environment` or `--contentRoot`. Variables the .NET runtime reads for itself before the app's code runs (its garbage collector and diagnostics switches, such as `DOTNET_gcServer`) are outside this rule: they tune the runtime, not n8Tracks.

`ASPNETCORE_ENVIRONMENT` is the one variable of .NET's own that the app honours, and it is for development: with the value `Development` the app also serves its OpenAPI document at `/openapi/v1.json` (under the base URL path, if there is one). Unset, blank, or any other value, there is no such document; the image sets `Production`. Only the environment variable of exactly that name counts.

An invalid value stops the app before it listens, with exit code 1 and one line per problem naming the variable and the reason, for example:

```json
{"timestamp":"2026-10-04T04:06:19.515361Z","level":"Error","message":"Invalid configuration: TZ must be a time zone ID this system knows, such as UTC or Europe/Oslo, but was 'Mars/Olympus'.","properties":{"variable":"TZ","reason":"must be a time zone ID this system knows, such as UTC or Europe/Oslo, but was 'Mars/Olympus'."}}
```

The app also exits with code 1 and one such line when the port is already in use or it is not permitted to bind it. A variable that starts with `N8TRACKS_` but is not in the table gets one warning line and is otherwise ignored. The optional `OTEL_EXPORTER_OTLP_ENDPOINT` is described under [Telemetry](#telemetry).

## Telemetry

Neither the app nor the gateway sends telemetry anywhere unless you tell it where. Both read one optional setting, the standard OpenTelemetry variable:

| Variable | Default | Example | Meaning |
| --- | --- | --- | --- |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | unset: nothing is sent | `http://collector:4317` | URL of an OpenTelemetry collector you run. When set, traces, metrics, and logs are exported to it over OTLP. |

When the variable is unset or blank, nothing is sent: OpenTelemetry is not set up at all (no tracing, no metrics, no log export, no exporter), and the process opens no telemetry connection. There is no other switch and no built-in destination. Only the environment variable of exactly that name counts: the same key as a command-line argument (`--OTEL_EXPORTER_OTLP_ENDPOINT=...`), with an `ASPNETCORE_` or `DOTNET_` prefix, or in an `appsettings.json` turns nothing on.

When it is set:

- Traces and metrics cover incoming requests (ASP.NET Core) and outgoing HTTP requests. The app reports as service `n8tracks`, the gateway as `n8tracks-gateway`.
- The app's log records are exported after the same redaction as its standard-output log, so credentials, tokens, cookies, lyrics, prompts, and raw provider payloads are masked before they leave. Traces carry the request path; query-string values are replaced with `Redacted`, and no headers or bodies are recorded.
- The gateway's traces redact query-string values the same way for the requests it receives; for a request either service sends, the whole query string shows as `*`. The gateway's log records are the same lines it writes to standard output, at the level `N8TRACKS_LOG_LEVEL` sets: no `Logging` setting of .NET, from any source, widens, narrows, or stops them. Its traces of the health check do include the URL in `N8TRACKS_API_URL`, which its log never does.
- Standard output is unchanged; export is in addition to it.

The companion variables are honoured as the OpenTelemetry SDK defines them, for example `OTEL_EXPORTER_OTLP_PROTOCOL` (`grpc`, the default, or `http/protobuf`) and `OTEL_EXPORTER_OTLP_HEADERS`. They too are read from the environment only: neither service reads the command line, a prefixed variable, or a settings file at all (see [Configuration](#configuration)), so nothing from those, an `OTEL_` key or any other, can change where telemetry goes, change what is exported, or stop it. One kind of setting is ignored even as an environment variable: nothing turns query-string redaction off, so `OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION` and `OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION` have no effect in the app or the gateway, and neither has the .NET runtime's `DOTNET_SYSTEM_NET_HTTP_DISABLEURIREDACTION`. With `http/protobuf`, give the collector's base URL (such as `http://collector:4318`); `/v1/traces`, `/v1/metrics`, and `/v1/logs` are appended. The service names are fixed: `OTEL_SERVICE_NAME` does not change them. There are no n8Tracks-specific telemetry settings.

The wiring is `src/n8Tracks.ServiceDefaults`, shared by the app and the gateway. It maps no endpoints (each service keeps its own `/health`) and references no other n8Tracks project.

## Versioning

The root `VERSION` file holds the one product version, and every component takes its version from it. The build fails if the file is missing, empty, or not `major.minor.patch` with an optional lower-case pre-release suffix (`0.1.0-rc.1`, which is how a release candidate is cut: see [docs/releasing.md](docs/releasing.md)). Override it for a single build with `-p:Version=<version>`. The Docker image build takes the same file, or `--build-arg VERSION=<version>`, which it stamps as the informational version (the one `/health` reports) so that any `edge` version is accepted. An `edge` build is `<VERSION>-edge.<short sha>`: see [Edge images](#edge-images).

Components (application, MCP gateway, browser extension) are compatible when their major and minor numbers match; patch numbers may differ.

## Security

See [SECURITY.md](SECURITY.md). Please report vulnerabilities privately, not in public issues.

## License and provider boundary

Apache License 2.0 — see [LICENSE](LICENSE).

n8Tracks is an independent, unofficial project. It is not affiliated with or endorsed by Suno; "Suno" is used only to describe compatibility.
