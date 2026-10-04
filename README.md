# n8Tracks

A self-hosted authoring workspace and catalog for AI-assisted music: a durable system of record for lyrics, creation parameters, generated outputs, and creative lineage.

**Status:** just initialized. The repository currently holds the product requirements ([docs/PRD.md](docs/PRD.md)), a layered ASP.NET Core backend skeleton with a health endpoint and environment-variable configuration, a web shell that shows the version and live health, a browser extension skeleton, and an MCP gateway skeleton. Nothing described in the PRD is built yet.

## Build and test

Requires the .NET 10 SDK (pinned in `global.json`).

```sh
dotnet build
dotnet test
dotnet format --verify-no-changes
```

Warnings are errors, and .NET analyzers and code-style rules run as part of the build (`Directory.Build.props`).

### Backend layout

| Project | Holds | May reference |
| --- | --- | --- |
| `src/n8Tracks.Domain` | Domain types and rules | nothing (no project, no NuGet package) |
| `src/n8Tracks.Application` | Application services: the one place business rules are applied | Domain |
| `src/n8Tracks.Infrastructure` | Persistence and other adapters | Application, Domain |
| `src/n8Tracks.Api` | HTTP endpoints and the composition root | Application, Infrastructure |
| `src/n8Tracks.Gateway` | The MCP gateway: a separate service that reaches n8Tracks over HTTP only | nothing |

Endpoints go through the application layer. Only the Api's composition root (`Program.cs` and the `n8Tracks.Api.DependencyInjection` namespace) may touch Infrastructure or Entity Framework Core. `tests/n8Tracks.Architecture.Tests` fails the build's test run when a project reference or a type dependency breaks these rules; a new project under `src/` must be added to the table in `ProjectReferenceTests`.

## Run

```sh
dotnet run --project src/n8Tracks.Api
```

Then `GET http://localhost:8787/health`. The launch profile sets `N8TRACKS_DATA_PATH=./.localdata` (created by a Debug build under `src/n8Tracks.Api`, git-ignored).

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

The backend serves a built frontend from its web root, `src/n8Tracks.Api/wwwroot` (git-ignored; the .NET build never runs npm). Everything is under the base URL path, if there is one:

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
| `npm run dev` | Serves the app at `http://localhost:5173/` with hot reload, proxying `/api` and `/health` to the backend on port 8787 (start it with `dotnet run --project src/n8Tracks.Api`). The dev server runs at the root only. |
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

The shell page shows the version and the health report, refreshed every 30 seconds while the tab is visible. The colour scheme (light, dark, or auto, which follows the system) is chosen in the header and remembered in the browser. Every colour pair that carries text is in `web/src/theme/palette.ts`, and a test holds each to WCAG 2.1 AA contrast.

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

`N8TRACKS_GATEWAY_PORT` is the only way to set the listen address: `ASPNETCORE_URLS`, `ASPNETCORE_HTTP_PORTS`, `--urls`, and launch settings are ignored. The gateway ignores every other variable, the app's `N8TRACKS_PORT` included.

A missing or invalid value stops the gateway before it listens, with exit code 1 and one line per problem naming the variable, at any log level. The value of `N8TRACKS_API_URL` is never written. The gateway also exits with code 1 and one such line when its port is already in use.

The log is one JSON object per line on standard output, in the shape of .NET's JSON console formatter (not the app's shape):

```json
{"Timestamp":"2026-10-04T05:18:28.639Z","EventId":1,"LogLevel":"Error","Category":"n8Tracks.Gateway.Startup","Message":"Invalid configuration: N8TRACKS_API_URL is required: set it to the URL of n8Tracks, such as http://n8tracks:8787.","State":{"Variable":"N8TRACKS_API_URL","Reason":"is required: set it to the URL of n8Tracks, such as http://n8tracks:8787.","{OriginalFormat}":"Invalid configuration: {Variable} {Reason}"}}
```

Framework categories (`Microsoft`, `System`) are held at Warning unless the level is set higher.

### Gateway health

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

`tests/n8Tracks.Gateway.Tests/GatewayIsolationGuardTests.cs` fails if the gateway references another n8Tracks project, Entity Framework Core, or SQLite, directly or through another package or project. It reads the gateway's project file, the dependency graph NuGet resolved for it, and the assemblies the built gateway references. It cannot see business rules written by hand inside the gateway; that is for review.

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

`N8TRACKS_PORT` is the only way to set the listen address: `ASPNETCORE_URLS`, `ASPNETCORE_HTTP_PORTS`, `--urls`, and launch settings are ignored.

An invalid value stops the app before it listens, with exit code 1 and one line per problem naming the variable and the reason, for example:

```json
{"timestamp":"2026-10-04T04:06:19.515361Z","level":"Error","message":"Invalid configuration: TZ must be a time zone ID this system knows, such as UTC or Europe/Oslo, but was 'Mars/Olympus'.","properties":{"variable":"TZ","reason":"must be a time zone ID this system knows, such as UTC or Europe/Oslo, but was 'Mars/Olympus'."}}
```

The app also exits with code 1 and one such line when the port is already in use or it is not permitted to bind it. A variable that starts with `N8TRACKS_` but is not in the table gets one warning line and is otherwise ignored.

## Versioning

The root `VERSION` file holds the one product version, and every component takes its version from it. The build fails if the file is missing, empty, or not `major.minor.patch`. Override it for a single build with `-p:Version=<version>`.

Components (application, MCP gateway, browser extension) are compatible when their major and minor numbers match; patch numbers may differ.

## Security

See [SECURITY.md](SECURITY.md). Please report vulnerabilities privately, not in public issues.

## License and provider boundary

Apache License 2.0 — see [LICENSE](LICENSE).

n8Tracks is an independent, unofficial project. It is not affiliated with or endorsed by Suno; "Suno" is used only to describe compatibility.
