# n8Tracks

A self-hosted authoring workspace and catalog for AI-assisted music: a durable system of record for lyrics, creation parameters, generated outputs, and creative lineage.

**Status:** just initialized. The repository currently holds the product requirements ([docs/PRD.md](docs/PRD.md)) and a layered ASP.NET Core backend skeleton with a single health endpoint. Nothing described in the PRD is built yet.

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

Then `GET /health`.

## Versioning

The root `VERSION` file holds the one product version, and every component takes its version from it. The build fails if the file is missing, empty, or not `major.minor.patch`. Override it for a single build with `-p:Version=<version>`.

Components (application, MCP gateway, browser extension) are compatible when their major and minor numbers match; patch numbers may differ.

## Security

See [SECURITY.md](SECURITY.md). Please report vulnerabilities privately, not in public issues.

## License and provider boundary

Apache License 2.0 — see [LICENSE](LICENSE).

n8Tracks is an independent, unofficial project. It is not affiliated with or endorsed by Suno; "Suno" is used only to describe compatibility.
