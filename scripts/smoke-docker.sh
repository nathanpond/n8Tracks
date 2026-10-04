#!/usr/bin/env bash
# End-to-end checks of the n8Tracks application image and the MCP gateway image.
#
#   scripts/smoke-docker.sh
#
# Builds both images for this machine's platform as n8tracks:dev and n8tracks-gateway:dev, runs them
# the ways an operator would, and fails on the first thing that is not as documented. It needs
# Docker, curl, and python3, and uses host ports 18787 and 18788. Everything it creates (containers,
# a volume, a network, a temporary folder) is removed on exit; the images are kept.
#
# Environment:
#   N8TRACKS_SMOKE_IMAGE          app image tag to build and test (default n8tracks:dev)
#   N8TRACKS_SMOKE_GATEWAY_IMAGE  gateway image tag to build and test (default n8tracks-gateway:dev)
#   N8TRACKS_SMOKE_SKIP_BUILD     set to 1 to test images that are already built
#   N8TRACKS_SMOKE_PORT           host port to publish the app on (default 18787)
#   N8TRACKS_SMOKE_GATEWAY_PORT   host port to publish the gateway on (default 18788)

set -euo pipefail

readonly IMAGE="${N8TRACKS_SMOKE_IMAGE:-n8tracks:dev}"
readonly GATEWAY_IMAGE="${N8TRACKS_SMOKE_GATEWAY_IMAGE:-n8tracks-gateway:dev}"
readonly HOST_PORT="${N8TRACKS_SMOKE_PORT:-18787}"
readonly GATEWAY_HOST_PORT="${N8TRACKS_SMOKE_GATEWAY_PORT:-18788}"
readonly WAIT_SECONDS=60
readonly PREFIX="n8tracks-smoke-$$"
readonly RUN_UID=1234
readonly RUN_GID=1235
# The base image's built-in unprivileged user, which the gateway image runs as.
readonly GATEWAY_UID=1654

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly ROOT
WORK="$(mktemp -d "${TMPDIR:-/tmp}/n8tracks-smoke.XXXXXX")"
readonly WORK

checks=0

cleanup() {
    local status=$?
    local containers
    containers="$(docker ps --all --quiet --filter "name=^${PREFIX}" || true)"
    if [ -n "$containers" ]; then
        # shellcheck disable=SC2086 # one ID per word
        docker rm --force --volumes $containers >/dev/null 2>&1 || true
    fi

    docker volume rm --force "$PREFIX-data" >/dev/null 2>&1 || true
    docker network rm "$PREFIX-net" >/dev/null 2>&1 || true

    # Files written by the container belong to another user on Linux; remove them from inside one.
    docker run --rm --entrypoint rm --volume "$WORK:/work" "$IMAGE" -rf /work/. >/dev/null 2>&1 || true
    rm -rf "$WORK" 2>/dev/null || true

    if [ "$status" -eq 0 ]; then
        printf '\nSmoke test passed: %d checks.\n' "$checks"
    else
        printf '\nSmoke test FAILED (exit %d) after %d passing checks.\n' "$status" "$checks" >&2
    fi
}
trap cleanup EXIT

section() { printf '\n== %s\n' "$1"; }

pass() {
    checks=$((checks + 1))
    printf '  ok    %s\n' "$1"
}

fail() {
    printf '  FAIL  %s\n' "$1" >&2
    if [ "${2:-}" != "" ]; then
        printf -- '--- log of %s ---\n' "$2" >&2
        docker logs "$2" >&2 2>&1 || true
    fi
    exit 1
}

# expect DESCRIPTION EXPECTED ACTUAL [CONTAINER]
expect() {
    if [ "$2" = "$3" ]; then
        pass "$1: $3"
    else
        fail "$1: expected '$2', got '$3'" "${4:-}"
    fi
}

url() { printf 'http://127.0.0.1:%s%s' "$HOST_PORT" "$1"; }

http_status() { curl --silent --output /dev/null --max-time 5 --write-out '%{http_code}' "$1" || true; }

gateway_url() { printf 'http://127.0.0.1:%s%s' "$GATEWAY_HOST_PORT" "$1"; }

# json_field NAME: a string field as it is; any other value as JSON (true, false, null, a number).
json_field() {
    python3 -c '
import json, sys

value = json.load(sys.stdin)[sys.argv[1]]
print(value if isinstance(value, str) else json.dumps(value))
' "$1"
}

container_state() { docker inspect --format '{{.State.Status}}' "$1"; }

container_health() { docker inspect --format '{{.State.Health.Status}}' "$1"; }

# wait_for_url CONTAINER URL: until the URL answers 200, the container stops, or time runs out.
wait_for_url() {
    local deadline=$((SECONDS + WAIT_SECONDS))
    while [ "$SECONDS" -lt "$deadline" ]; do
        if [ "$(http_status "$2")" = "200" ]; then
            return 0
        fi
        if [ "$(container_state "$1")" != "running" ]; then
            fail "$1 stopped before $2 answered 200" "$1"
        fi
        sleep 1
    done
    fail "$1 did not answer 200 at $2 within ${WAIT_SECONDS}s" "$1"
}

# wait_for_http CONTAINER PATH: the same, for a path of the app on its published port.
wait_for_http() { wait_for_url "$1" "$(url "$2")"; }

# wait_for_container_health CONTAINER: until Docker's own health check reports healthy.
wait_for_container_health() {
    local deadline=$((SECONDS + WAIT_SECONDS))
    while [ "$SECONDS" -lt "$deadline" ]; do
        if [ "$(container_health "$1")" = "healthy" ]; then
            pass "Docker reports $1 as healthy"
            return 0
        fi
        sleep 1
    done
    docker inspect --format '{{json .State.Health}}' "$1" >&2 || true
    fail "Docker did not report $1 as healthy within ${WAIT_SECONDS}s" "$1"
}

# wait_for_exit CONTAINER: prints the exit code once the container has stopped.
wait_for_exit() {
    local deadline=$((SECONDS + WAIT_SECONDS))
    while [ "$SECONDS" -lt "$deadline" ]; do
        if [ "$(container_state "$1")" = "exited" ]; then
            docker inspect --format '{{.State.ExitCode}}' "$1"
            return 0
        fi
        sleep 1
    done
    fail "$1 was still running after ${WAIT_SECONDS}s" "$1"
}

# assert_log_is_json CONTAINER: every line the container wrote, before the app and from it, is one
# JSON object whose keys are those of the application log.
assert_log_is_json() {
    local count
    if ! count="$(docker logs "$1" 2>&1 | python3 -c '
import json, sys

count = 0
for number, line in enumerate(sys.stdin, 1):
    line = line.rstrip("\n")
    try:
        entry = json.loads(line)
    except ValueError:
        sys.exit(f"line {number} is not JSON: {line!r}")
    if not isinstance(entry, dict):
        sys.exit(f"line {number} is not a JSON object: {line!r}")
    keys = list(entry)
    if keys[:4] != ["timestamp", "level", "message", "properties"] or keys[4:] not in ([], ["exception"]):
        sys.exit(f"line {number} has keys {keys}: {line!r}")
    if not isinstance(entry["properties"], dict):
        sys.exit(f"line {number} has properties that are not an object: {line!r}")
    count += 1
if count == 0:
    sys.exit("the container wrote nothing")
print(count)
')"; then
        fail "the log of $1 is not JSON lines in the application log shape" "$1"
    fi
    pass "all $count log lines of $1 are JSON objects with the application log keys"
}

# log_has CONTAINER LEVEL TEXT: succeeds when a line at that level has the text in its message.
log_has() {
    docker logs "$1" 2>&1 | python3 -c '
import json, sys

level, text = sys.argv[1], sys.argv[2]
found = any(entry["level"] == level and text in entry["message"] for entry in map(json.loads, sys.stdin))
sys.exit(0 if found else 1)
' "$2" "$3"
}

# assert_gateway_log_is_json CONTAINER: every line the gateway container wrote is one JSON object
# with the keys of the gateway log (.NET's JSON console formatter, not the application log shape).
assert_gateway_log_is_json() {
    local count
    if ! count="$(docker logs "$1" 2>&1 | python3 -c '
import json, sys

count = 0
for number, line in enumerate(sys.stdin, 1):
    line = line.rstrip("\n")
    try:
        entry = json.loads(line)
    except ValueError:
        sys.exit(f"line {number} is not JSON: {line!r}")
    if not isinstance(entry, dict):
        sys.exit(f"line {number} is not a JSON object: {line!r}")
    missing = [key for key in ("Timestamp", "LogLevel", "Category", "Message") if key not in entry]
    if missing:
        sys.exit(f"line {number} has no {missing}: {line!r}")
    count += 1
if count == 0:
    sys.exit("the container wrote nothing")
print(count)
')"; then
        fail "the log of $1 is not JSON lines in the gateway log shape" "$1"
    fi
    pass "all $count log lines of $1 are JSON objects with the gateway log keys"
}

# gateway_log_has CONTAINER LEVEL TEXT: succeeds when a line at that level has the text in its message.
gateway_log_has() {
    docker logs "$1" 2>&1 | python3 -c '
import json, sys

level, text = sys.argv[1], sys.argv[2]
found = any(entry["LogLevel"] == level and text in entry["Message"] for entry in map(json.loads, sys.stdin))
sys.exit(0 if found else 1)
' "$2" "$3"
}

# wait_for_health_check_after CONTAINER TIME: until Docker has run a health check of the container
# that started after the given time (as docker inspect prints times); prints that check's exit code.
wait_for_health_check_after() {
    local deadline=$((SECONDS + WAIT_SECONDS)) code
    while [ "$SECONDS" -lt "$deadline" ]; do
        if code="$(docker inspect --format '{{json .State.Health.Log}}' "$1" | python3 -c '
import json, re, sys
from datetime import datetime

def instant(text):
    # Docker prints up to nine fractional digits; Python reads six.
    text = re.sub(r"(\.\d{6})\d+", r"\1", text.replace("Z", "+00:00"))
    return datetime.fromisoformat(text)

after = instant(sys.argv[1])
later = [check for check in json.load(sys.stdin) or [] if instant(check["Start"]) > after]
if not later:
    sys.exit(1)
print(later[-1]["ExitCode"])
' "$2")"; then
            printf '%s' "$code"
            return 0
        fi
        sleep 1
    done
    fail "Docker ran no health check of $1 within ${WAIT_SECONDS}s" "$1"
}

# seeded_timestamp DATA_DIR: the value of the app_metadata row written when the schema was created.
# Read from a copy (database plus write-ahead log), never from the file the container has open.
seeded_timestamp() {
    local copy="$WORK/db-copy"
    rm -rf "$copy"
    mkdir -p "$copy"
    cp "$1"/n8tracks.db* "$copy/"
    python3 -c '
import sqlite3, sys

connection = sqlite3.connect(sys.argv[1])
rows = connection.execute("SELECT value FROM app_metadata WHERE key = ?", ("schema_initialized_utc",)).fetchall()
if len(rows) != 1:
    sys.exit(f"expected one schema_initialized_utc row, found {len(rows)}")
print(rows[0][0])
' "$copy/n8tracks.db"
}

# process_id CONTAINER FIELD: the real ID on the Uid or Gid line of the app process (PID 1).
process_id() { docker exec "$1" cat /proc/1/status | awk -v field="$2:" '$1 == field { print $2 }'; }

remove() { docker rm --force --volumes "$1" >/dev/null; }

# ---------------------------------------------------------------------------------------------------

section "Build"
expected_version="$(tr -d '[:space:]' < "$ROOT/VERSION")"
if [ "${N8TRACKS_SMOKE_SKIP_BUILD:-0}" = "1" ]; then
    docker image inspect "$IMAGE" >/dev/null || fail "image $IMAGE does not exist"
    pass "using the existing image $IMAGE"
    docker image inspect "$GATEWAY_IMAGE" >/dev/null || fail "image $GATEWAY_IMAGE does not exist"
    pass "using the existing image $GATEWAY_IMAGE"
else
    docker build --tag "$IMAGE" "$ROOT"
    pass "built $IMAGE for the host platform"
    docker build --file "$ROOT/src/n8Tracks.Gateway/Dockerfile" --tag "$GATEWAY_IMAGE" "$ROOT"
    pass "built $GATEWAY_IMAGE for the host platform"
fi

volumes="$(docker image inspect --format '{{json .Config.Volumes}}' "$IMAGE")"
expect "the image declares only /data as a volume" '{"/data":{}}' "$volumes"

# The Aspire AppHost is local development tooling: neither it nor any Aspire assembly is in an image.
for checked in "$IMAGE:n8Tracks.Api.dll" "$GATEWAY_IMAGE:n8Tracks.Gateway.dll"; do
    image="${checked%:*}"
    files="$(docker run --rm --entrypoint find "$image" /app -type f)"
    grep -q "/${checked##*:}\$" <<<"$files" || fail "the file listing of $image does not hold ${checked##*:}"
    if stray="$(grep -i -e 'n8Tracks\.AppHost' -e '/Aspire\.' <<<"$files")"; then
        fail "$image holds the AppHost or an Aspire assembly: $stray"
    fi
    pass "$image holds no AppHost and no Aspire assembly"
done

# ---------------------------------------------------------------------------------------------------

section "Data and read-only media mounted, PUID=$RUN_UID PGID=$RUN_GID"
name="$PREFIX-main"
data="$WORK/data"
media="$WORK/media"
mkdir -p "$data" "$media"
printf 'not a real track\n' > "$media/track.flac"

run_main() {
    docker run --detach --name "$name" \
        --publish "$HOST_PORT:8787" \
        --env PUID="$RUN_UID" --env PGID="$RUN_GID" \
        --volume "$data:/data" --volume "$media:/media:ro" \
        "$IMAGE" >/dev/null
}

run_main
wait_for_http "$name" /health
health="$(curl --silent --max-time 5 "$(url /health)")"
expect "health status" healthy "$(printf '%s' "$health" | json_field status)" "$name"
expect "health reports the version in the VERSION file" "$expected_version" "$(printf '%s' "$health" | json_field version)" "$name"

shell="$(curl --silent --max-time 5 "$(url /)")"
case "$shell" in
    *'<div id="root">'*'<base href="/"'* | *'<base href="/"'*'<div id="root">'*) pass "the shell page is served at / with its base href" ;;
    *) fail "the shell page was not served at /: $shell" "$name" ;;
esac
asset="$(printf '%s' "$shell" | grep -o 'assets/[^"]*\.js' | head -n 1)"
expect "the shell's script is served" 200 "$(http_status "$(url "/$asset")")" "$name"

expect "the app process user ID" "$RUN_UID" "$(process_id "$name" Uid)" "$name"
expect "the app process group ID" "$RUN_GID" "$(process_id "$name" Gid)" "$name"
expect "the app process has no supplementary groups" "" "$(process_id "$name" Groups)" "$name"
expect "the app is PID 1" "dotnet" "$(docker exec "$name" cat /proc/1/comm)" "$name"

if [ "$(uname -s)" = "Linux" ]; then
    expect "owner of n8tracks.db on the host" "$RUN_UID:$RUN_GID" "$(stat -c '%u:%g' "$data/n8tracks.db")" "$name"
else
    printf '  skip  owner of n8tracks.db on the host: %s does not keep container ownership on bind mounts (see the named volume checks)\n' "$(uname -s)"
fi

if docker exec "$name" sh -c 'touch /media/written 2>/dev/null'; then
    fail "/media is writable" "$name"
fi
pass "/media is read-only"
expect "/backup is absent when not mounted" absent "$(docker exec "$name" sh -c 'test -e /backup && echo present || echo absent')" "$name"

wait_for_container_health "$name"
assert_log_is_json "$name"

seeded="$(seeded_timestamp "$data")"
pass "seeded app_metadata row: $seeded"

docker restart "$name" >/dev/null
wait_for_http "$name" /health
expect "seeded row after docker restart" "$seeded" "$(seeded_timestamp "$data")" "$name"

remove "$name"
run_main
wait_for_http "$name" /health
expect "seeded row after docker rm and a fresh docker run on the same data" "$seeded" "$(seeded_timestamp "$data")" "$name"
expect "health status after recreating the container" healthy "$(curl --silent --max-time 5 "$(url /health)" | json_field status)" "$name"
remove "$name"

# ---------------------------------------------------------------------------------------------------

section "No media mount"
name="$PREFIX-nomedia"
mkdir -p "$WORK/data-nomedia"
docker run --detach --name "$name" --publish "$HOST_PORT:8787" \
    --env PUID="$RUN_UID" --env PGID="$RUN_GID" \
    --volume "$WORK/data-nomedia:/data" \
    "$IMAGE" >/dev/null
wait_for_http "$name" /health
expect "health status without media" degraded "$(curl --silent --max-time 5 "$(url /health)" | json_field status)" "$name"
expect "/media is absent when not mounted" absent "$(docker exec "$name" sh -c 'test -e /media && echo present || echo absent')" "$name"
wait_for_container_health "$name"
assert_log_is_json "$name"
remove "$name"

# ---------------------------------------------------------------------------------------------------

section "Named volume: ownership follows PUID and PGID"
name="$PREFIX-volume"
volume="$PREFIX-data"
docker volume create "$volume" >/dev/null

run_on_volume() {
    docker run --detach --name "$name" --publish "$HOST_PORT:8787" \
        --env PUID="$1" --env PGID="$2" \
        --volume "$volume:/data" \
        "$IMAGE" >/dev/null
    wait_for_http "$name" /health
}

owner_in_container() { docker exec "$name" stat -c '%u:%g' "$1"; }

run_on_volume "$RUN_UID" "$RUN_GID"
expect "owner of /data" "$RUN_UID:$RUN_GID" "$(owner_in_container /data)" "$name"
expect "owner of the database the app created" "$RUN_UID:$RUN_GID" "$(owner_in_container /data/n8tracks.db)" "$name"
expect "mode of the database the app created" 644 "$(docker exec "$name" stat -c '%a' /data/n8tracks.db)" "$name"
remove_container_only() { docker rm --force "$1" >/dev/null; }
remove_container_only "$name"

# The same data under another user: the top-level owner differs, so everything is handed over.
run_on_volume 2345 2346
expect "owner of /data after changing PUID and PGID" "2345:2346" "$(owner_in_container /data)" "$name"
expect "owner of the existing database after changing PUID and PGID" "2345:2346" "$(owner_in_container /data/n8tracks.db)" "$name"
log_has "$name" Information "Changed the owner of /data from $RUN_UID:$RUN_GID to 2345:2346" || fail "no line about the owner change" "$name"
pass "an Information line reports the owner change"
remove_container_only "$name"

# Same user again: nothing to change, nothing written before the app.
run_on_volume 2345 2346
if docker logs "$name" 2>&1 | grep -q 'n8Tracks.Entrypoint'; then
    fail "the entrypoint wrote a line although the owner already matched" "$name"
fi
pass "no entrypoint line when the owner already matches"
assert_log_is_json "$name"
remove_container_only "$name"
docker volume rm "$volume" >/dev/null

# ---------------------------------------------------------------------------------------------------

section "Sub-path base URL and another port"
name="$PREFIX-subpath"
mkdir -p "$WORK/data-subpath"
docker run --detach --name "$name" --publish "$HOST_PORT:9000" \
    --env PUID="$RUN_UID" --env PGID="$RUN_GID" \
    --env N8TRACKS_PORT=9000 --env N8TRACKS_BASE_URL="http://localhost:$HOST_PORT/n8tracks/" \
    --volume "$WORK/data-subpath:/data" --volume "$media:/media:ro" \
    "$IMAGE" >/dev/null
wait_for_http "$name" /n8tracks/health
expect "health outside the sub-path" 404 "$(http_status "$(url /health)")" "$name"
case "$(curl --silent --max-time 5 "$(url /n8tracks/)")" in
    *'<base href="/n8tracks/"'*) pass "the shell page under the sub-path has base href /n8tracks/" ;;
    *) fail "the shell page under /n8tracks/ has no matching base href" "$name" ;;
esac
wait_for_container_health "$name"
assert_log_is_json "$name"
remove "$name"

# ---------------------------------------------------------------------------------------------------

section "Started as root: PUID=0"
name="$PREFIX-root"
docker run --detach --name "$name" --publish "$HOST_PORT:8787" --env PUID=0 --env PGID=0 "$IMAGE" >/dev/null
wait_for_http "$name" /health
expect "the app process user ID" 0 "$(process_id "$name" Uid)" "$name"
log_has "$name" Warning "running as root" || fail "no warning that the app is running as root" "$name"
pass "a Warning line says the app is running as root"
assert_log_is_json "$name"
remove "$name"

section "Started through Docker's own user setting"
name="$PREFIX-user"
mkdir -p "$WORK/data-user"
chmod 777 "$WORK/data-user"
docker run --detach --name "$name" --publish "$HOST_PORT:8787" --user 4321:4322 \
    --env PUID=abc --env PGID=-1 \
    --volume "$WORK/data-user:/data" \
    "$IMAGE" >/dev/null
wait_for_http "$name" /health
expect "the app process user ID" 4321 "$(process_id "$name" Uid)" "$name"
expect "the app process group ID" 4322 "$(process_id "$name" Gid)" "$name"
pass "PUID and PGID were not read: invalid values did not stop the container"
assert_log_is_json "$name"
remove "$name"

# ---------------------------------------------------------------------------------------------------

section "Complement: invalid PUID and PGID"
# invalid_ids PUID PGID VARIABLE: the container stops with one Error line naming the variable.
invalid_ids() {
    local name="$PREFIX-invalid" label="PUID=$1 PGID=$2" code
    docker run --detach --name "$name" --env PUID="$1" --env PGID="$2" "$IMAGE" >/dev/null
    code="$(wait_for_exit "$name")"
    [ "$code" != "0" ] || fail "$label: the container exited with code 0" "$name"
    assert_log_is_json "$name"
    expect "$label: lines written" 1 "$(docker logs "$name" 2>&1 | wc -l | tr -d '[:space:]')" "$name"
    log_has "$name" Error "Invalid configuration: $3 " || fail "$label: no Error line naming $3" "$name"
    pass "$label: exit code $code with one Error line naming $3"
    remove "$name"
}

invalid_ids abc 1000 PUID
invalid_ids 1000 -1 PGID
invalid_ids 1.5 1000 PUID
invalid_ids 99999999999 1000 PUID
invalid_ids '1"\2' 1000 PUID

section "Complement: /data mounted read-only"
name="$PREFIX-readonly"
mkdir -p "$WORK/data-readonly"
docker run --detach --name "$name" --publish "$HOST_PORT:8787" \
    --env PUID="$RUN_UID" --env PGID="$RUN_GID" \
    --volume "$WORK/data-readonly:/data:ro" \
    "$IMAGE" >/dev/null
code="$(wait_for_exit "$name")"
[ "$code" != "0" ] || fail "the container exited with code 0 although /data is read-only" "$name"
pass "exit code $code"
[ "$(container_health "$name")" != "healthy" ] || fail "the container reported healthy" "$name"
if docker inspect --format '{{range .State.Health.Log}}{{.ExitCode}} {{end}}' "$name" | grep -qw 0; then
    fail "a health check passed although /data is read-only" "$name"
fi
pass "the container never reported healthy (health: $(container_health "$name"))"
log_has "$name" Error "N8TRACKS_DATA_PATH" || fail "no Error line naming N8TRACKS_DATA_PATH" "$name"
pass "an Error line names N8TRACKS_DATA_PATH"
assert_log_is_json "$name"
remove "$name"

# ---------------------------------------------------------------------------------------------------

section "Gateway: next to the app on a shared network"
network="$PREFIX-net"
app="$PREFIX-gateway-app"
name="$PREFIX-gateway"
docker network create "$network" >/dev/null

expect "the gateway image's user" "$GATEWAY_UID" "$(docker image inspect --format '{{.Config.User}}' "$GATEWAY_IMAGE")"
expect "the gateway image declares no volume" null "$(docker image inspect --format '{{json .Config.Volumes}}' "$GATEWAY_IMAGE")"
if docker run --rm --entrypoint sh "$GATEWAY_IMAGE" -c 'ls /app' | grep -q -e 'n8Tracks\.Api' -e 'n8Tracks\.Application' -e 'n8Tracks\.Infrastructure' -e wwwroot; then
    fail "the gateway image holds part of the app"
fi
pass "the gateway image holds no part of the app"

docker run --detach --name "$app" --network "$network" --publish "$HOST_PORT:8787" \
    --env PUID="$RUN_UID" --env PGID="$RUN_GID" \
    --volume "$media:/media:ro" \
    "$IMAGE" >/dev/null
wait_for_http "$app" /health

# The gateway reaches the app by its name on the network, as in the Compose example.
docker run --detach --name "$name" --network "$network" --publish "$GATEWAY_HOST_PORT:8788" \
    --env N8TRACKS_API_URL="http://$app:8787" \
    "$GATEWAY_IMAGE" >/dev/null
wait_for_url "$name" "$(gateway_url /health)"

gateway_health() { curl --silent --max-time 10 "$(gateway_url /health)"; }

health="$(gateway_health)"
expect "gateway health status" healthy "$(printf '%s' "$health" | json_field status)" "$name"
expect "gateway health upstream" reachable "$(printf '%s' "$health" | json_field upstream)" "$name"
expect "gateway health compatible" true "$(printf '%s' "$health" | json_field compatible)" "$name"
expect "gateway health reports the version in the VERSION file" "$expected_version" "$(printf '%s' "$health" | json_field version)" "$name"

expect "the gateway process user ID" "$GATEWAY_UID" "$(process_id "$name" Uid)" "$name"
expect "the gateway process group ID" "$GATEWAY_UID" "$(process_id "$name" Gid)" "$name"
expect "the gateway is PID 1" "dotnet" "$(docker exec "$name" cat /proc/1/comm)" "$name"
wait_for_container_health "$name"

section "Gateway complement: the app stops"
docker stop "$app" >/dev/null
stopped="$(docker inspect --format '{{.State.FinishedAt}}' "$app")"
health="$(gateway_health)"
expect "gateway health status with the app stopped" degraded "$(printf '%s' "$health" | json_field status)" "$name"
expect "gateway health upstream with the app stopped" unreachable "$(printf '%s' "$health" | json_field upstream)" "$name"
expect "gateway health compatible with the app stopped" null "$(printf '%s' "$health" | json_field compatible)" "$name"

# The image's own health check, run by Docker after the app went away.
expect "exit code of Docker's next health check of the degraded gateway" 0 "$(wait_for_health_check_after "$name" "$stopped")" "$name"
expect "Docker's health status of the degraded gateway" healthy "$(container_health "$name")" "$name"
gateway_log_has "$name" Warning "n8Tracks is unreachable" || fail "no Warning line that n8Tracks is unreachable" "$name"
pass "a Warning line says n8Tracks is unreachable"

docker start "$app" >/dev/null
wait_for_http "$app" /health
expect "gateway health status with the app back" healthy "$(gateway_health | json_field status)" "$name"
assert_gateway_log_is_json "$name"
remove "$name"
remove "$app"

section "Gateway: another port"
name="$PREFIX-gateway-port"
docker run --detach --name "$name" --network "$network" --publish "$GATEWAY_HOST_PORT:9001" \
    --env N8TRACKS_API_URL="http://$app:8787" --env N8TRACKS_GATEWAY_PORT=9001 \
    "$GATEWAY_IMAGE" >/dev/null
wait_for_url "$name" "$(gateway_url /health)"
wait_for_container_health "$name"
remove "$name"
docker network rm "$network" >/dev/null

section "Gateway complement: no N8TRACKS_API_URL"
name="$PREFIX-gateway-nourl"
docker run --detach --name "$name" "$GATEWAY_IMAGE" >/dev/null
code="$(wait_for_exit "$name")"
[ "$code" != "0" ] || fail "the gateway exited with code 0 without N8TRACKS_API_URL" "$name"
pass "exit code $code"
expect "lines written" 1 "$(docker logs "$name" 2>&1 | wc -l | tr -d '[:space:]')" "$name"
gateway_log_has "$name" Error "N8TRACKS_API_URL is required" || fail "no Error line naming N8TRACKS_API_URL" "$name"
pass "one Error line names N8TRACKS_API_URL"
assert_gateway_log_is_json "$name"
remove "$name"
