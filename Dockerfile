# The n8Tracks application image: the built frontend and the published backend in one runtime image,
# listening on port 8787. Build it from the repository root:
#
#   docker build -t n8tracks:dev .
#   docker buildx build --platform linux/amd64,linux/arm64 .
#
# The two build stages run on the build machine's own architecture and cross-compile for the target,
# so a two-platform build needs no emulation for them. Tests are not run here.

# --- Frontend: web/dist --------------------------------------------------------------------------
FROM --platform=$BUILDPLATFORM node:25-slim AS web
WORKDIR /src/web

# Dependencies first, so this layer is reused until the lock file changes.
COPY web/package.json web/package-lock.json ./
RUN npm ci

COPY web/ ./
RUN npm run build

# --- Backend: framework-dependent publish of n8Tracks.Api -----------------------------------------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Restore first, from the project files alone, so this layer is reused until a dependency changes.
# VERSION is needed here: Directory.Build.targets refuses to restore without it.
COPY global.json Directory.Build.props Directory.Build.targets VERSION ./
COPY src/n8Tracks.Api/n8Tracks.Api.csproj src/n8Tracks.Api/
COPY src/n8Tracks.Application/n8Tracks.Application.csproj src/n8Tracks.Application/
COPY src/n8Tracks.Domain/n8Tracks.Domain.csproj src/n8Tracks.Domain/
COPY src/n8Tracks.Infrastructure/n8Tracks.Infrastructure.csproj src/n8Tracks.Infrastructure/
COPY src/n8Tracks.ServiceDefaults/n8Tracks.ServiceDefaults.csproj src/n8Tracks.ServiceDefaults/
RUN dotnet restore src/n8Tracks.Api/n8Tracks.Api.csproj -a "$TARGETARCH"

COPY src/ src/

# Suno's Create-screen field inventory and import field map, which the Application project embeds when it is built.
COPY docs/suno-create-field-inventory.json docs/suno-import-field-map.json docs/

# The product version: the VERSION build argument, or the root VERSION file when it is empty.
# It is stamped as the informational version, which is what the health endpoint reports. It is not
# passed as -p:Version: that one must also be a valid NuGet version, and an edge version whose short
# sha is all digits with a leading zero (0.1.0-edge.0123456) is not, which would fail the build.
ARG VERSION=""
RUN version="${VERSION:-$(tr -d '[:space:]' < VERSION)}" \
    && dotnet publish src/n8Tracks.Api/n8Tracks.Api.csproj \
        --configuration Release \
        -a "$TARGETARCH" \
        --no-restore \
        --no-self-contained \
        -p:UseAppHost=false \
        -p:InformationalVersion="$version" \
        --output /app/publish

# --- Runtime --------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Time zone data for TZ. No curl or wget: the health check is the app binary itself.
# The base image ships an empty /media; it is removed so that a media folder that is not mounted is
# absent (and reported as unavailable) instead of looking like an empty library.
RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install --yes --no-install-recommends tzdata \
    && rm -rf /var/lib/apt/lists/* \
    && rmdir /media

# N8TRACKS_PORT is the only listen setting; the base image's ASPNETCORE_HTTP_PORTS is cleared.
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=

WORKDIR /app
COPY --from=build /app/publish ./
COPY --from=web /src/web/dist ./wwwroot
COPY --chmod=755 docker/entrypoint.sh /usr/local/bin/n8tracks-entrypoint
# The owner's commands in a running container: docker exec -it <container> n8tracks reset-password
COPY --chmod=755 docker/n8tracks /usr/local/bin/n8tracks

EXPOSE 8787

# The app's own data. /media and /backup are deliberately not created: one that is not mounted is absent.
VOLUME /data

# 200 (healthy or degraded) passes; 503 or no answer fails. The URL comes from N8TRACKS_PORT and the
# path of N8TRACKS_BASE_URL, as the container was started with them.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD ["dotnet", "/app/n8Tracks.Api.dll", "--healthcheck"]

# Starts as root to apply PUID and PGID, then runs the app as that user.
ENTRYPOINT ["/usr/local/bin/n8tracks-entrypoint"]
