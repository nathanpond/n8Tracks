# n8Tracks

A self-hosted authoring workspace and catalog for AI-assisted music: a durable system of record for lyrics, creation parameters, generated outputs, and creative lineage.

**Status:** just initialized. The repository currently holds the product requirements ([docs/PRD.md](docs/PRD.md)) and a layered ASP.NET Core backend skeleton with a health endpoint and environment-variable configuration. Nothing described in the PRD is built yet.

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
