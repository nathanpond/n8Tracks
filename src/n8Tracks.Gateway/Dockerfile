# The n8Tracks MCP gateway image: the published gateway alone, listening on port 8788. It holds no
# part of the app. Build it from the repository root:
#
#   docker build -f src/n8Tracks.Gateway/Dockerfile -t n8tracks-gateway:dev .
#   docker buildx build -f src/n8Tracks.Gateway/Dockerfile --platform linux/amd64,linux/arm64 .
#
# The build context is cut down by Dockerfile.dockerignore next to this file, which takes the place
# of the root .dockerignore for this build. The build stage runs on the build machine's own
# architecture and cross-compiles for the target. Tests are not run here.

# --- Build: framework-dependent publish of n8Tracks.Gateway ---------------------------------------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Restore first, from the project files alone, so this layer is reused until a dependency changes.
# VERSION is needed here: Directory.Build.targets refuses to restore without it.
COPY global.json Directory.Build.props Directory.Build.targets VERSION ./
COPY src/n8Tracks.Gateway/n8Tracks.Gateway.csproj src/n8Tracks.Gateway/
COPY src/n8Tracks.ServiceDefaults/n8Tracks.ServiceDefaults.csproj src/n8Tracks.ServiceDefaults/
RUN dotnet restore src/n8Tracks.Gateway/n8Tracks.Gateway.csproj -a "$TARGETARCH"

COPY src/n8Tracks.Gateway/ src/n8Tracks.Gateway/
COPY src/n8Tracks.ServiceDefaults/ src/n8Tracks.ServiceDefaults/

# The product version: the VERSION build argument, or the root VERSION file when it is empty.
# It is stamped as the informational version, which is what the health endpoint reports. It is not
# passed as -p:Version: that one must also be a valid NuGet version, and an edge version whose short
# sha is all digits with a leading zero (0.1.0-edge.0123456) is not, which would fail the build.
ARG VERSION=""
RUN version="${VERSION:-$(tr -d '[:space:]' < VERSION)}" \
    && dotnet publish src/n8Tracks.Gateway/n8Tracks.Gateway.csproj \
        --configuration Release \
        -a "$TARGETARCH" \
        --no-restore \
        --no-self-contained \
        -p:UseAppHost=false \
        -p:InformationalVersion="$version" \
        --output /app/publish

# --- Runtime --------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Nothing is installed: no curl or wget (the health check is the gateway binary itself), and no time
# zone data (the gateway shows no local times; its log is in UTC).

# N8TRACKS_GATEWAY_PORT is the only listen setting; the base image's ASPNETCORE_HTTP_PORTS is cleared.
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=

WORKDIR /app
COPY --from=build /app/publish ./

EXPOSE 8788

# The base image's built-in unprivileged user, "app" (UID and GID 1654). The gateway writes no file,
# so there is nothing to own and no PUID or PGID.
USER $APP_UID

# 200 passes, and the gateway answers 200 whether it is healthy or degraded: n8Tracks being away does
# not make this container unhealthy. No answer fails. The port comes from N8TRACKS_GATEWAY_PORT, as
# the container was started with it.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD ["dotnet", "/app/n8Tracks.Gateway.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "/app/n8Tracks.Gateway.dll"]
