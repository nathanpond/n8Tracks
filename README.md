# n8Tracks

A self-hosted authoring workspace and catalog for AI-assisted music: a durable system of record for lyrics, creation parameters, generated outputs, and creative lineage.

**Status:** just initialized. The repository currently holds the product requirements ([docs/PRD.md](docs/PRD.md)) and an ASP.NET Core backend skeleton with a single health endpoint. Nothing described in the PRD is built yet.

## Build and test

Requires the .NET 10 SDK (pinned in `global.json`).

```sh
dotnet build
dotnet test
dotnet format --verify-no-changes
```

Warnings are errors, and .NET analyzers and code-style rules run as part of the build (`Directory.Build.props`).

## Run

```sh
dotnet run --project src/n8Tracks
```

Then `GET /health`.

## Security

See [SECURITY.md](SECURITY.md). Please report vulnerabilities privately, not in public issues.

## License and provider boundary

Apache License 2.0 — see [LICENSE](LICENSE).

n8Tracks is an independent, unofficial project. It is not affiliated with or endorsed by Suno; "Suno" is used only to describe compatibility.
