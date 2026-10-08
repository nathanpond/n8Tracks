#!/usr/bin/env bash
# End-to-end checks of the n8Tracks application image and the MCP gateway image.
#
#   scripts/smoke-docker.sh
#
# Builds both images for this machine's platform as n8tracks:dev and n8tracks-gateway:dev and runs
# them the ways an operator would. It needs Docker, curl, and python3, and uses host ports 18787 and
# 18788. Everything it creates (containers, a volume, a network, a temporary folder) is removed on
# exit; the images are kept.
#
# Output: one line per assertion, "PASS <name>" or "FAIL <name>". A failed assertion does not stop
# the run: every section runs, the failed lines are repeated at the end, and the exit code is 1 if
# any assertion failed. A section whose container never came up stops there (the rest of it could
# say nothing) and the next section starts. The log of a container that failed an assertion, or
# that was left behind by a stopped section, is printed before the container is removed.
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
# One line per assertion ("PASS <name>" or "FAIL <name>"), and the IDs of the containers whose log
# has been printed. Files, because sections run in subshells.
RESULTS="$(mktemp "${TMPDIR:-/tmp}/n8tracks-smoke-results.XXXXXX")"
readonly RESULTS
readonly DUMPED="$RESULTS.dumped"
: > "$DUMPED"
# The exit code of a section that stopped itself after a failed assertion.
readonly STOPPED=3

# One ordered stream: a FAIL line and a container's log stay next to the lines around them.
exec 2>&1

count() { grep -c "^$1 " "$RESULTS" || true; }

# dump_log CONTAINER: prints the container's log, once for each container.
dump_log() {
    local id
    id="$(docker inspect --format '{{.Id}}' "$1" 2>/dev/null)" || return 0
    if grep -q "^$id\$" "$DUMPED"; then
        return 0
    fi
    printf '%s\n' "$id" >> "$DUMPED"
    printf -- '--- log of %s (%s) ---\n' "$1" "$(docker inspect --format '{{.State.Status}}' "$1" 2>/dev/null || true)"
    docker logs "$1" 2>&1 || true
    printf -- '--- end of the log of %s ---\n' "$1"
}

# sweep DUMP: removes every container, the volume, and the network this run created, printing the
# containers' logs first when DUMP is 1.
sweep() {
    local container
    for container in $(docker ps --all --format '{{.Names}}' --filter "name=^${PREFIX}" 2>/dev/null || true); do
        if [ "$1" = "1" ]; then
            dump_log "$container"
        fi
        docker rm --force --volumes "$container" >/dev/null 2>&1 || true
    done

    docker volume rm --force "$PREFIX-data" >/dev/null 2>&1 || true
    docker network rm "$PREFIX-net" >/dev/null 2>&1 || true
}

cleanup() {
    local status=$? passed failed
    passed="$(count PASS)"
    failed="$(count FAIL)"
    if [ "$status" -ne 0 ] || [ "$failed" -ne 0 ]; then
        sweep 1
    else
        sweep 0
    fi

    # Files written by the container belong to another user on Linux; remove them from inside one.
    docker run --rm --entrypoint rm --volume "$WORK:/work" "$IMAGE" -rf /work/. >/dev/null 2>&1 || true
    rm -rf "$WORK" 2>/dev/null || true

    if [ "$status" -eq 0 ] && [ "$failed" -eq 0 ]; then
        printf '\nSmoke test passed: %d checks.\n' "$passed"
    else
        printf '\nSmoke test FAILED (exit %d): %d failed, %d passed.\n' "$status" "$failed" "$passed"
        grep '^FAIL ' "$RESULTS" || printf 'No assertion failed: the script itself stopped on an error (see above).\n'
    fi
    rm -f "$RESULTS" "$DUMPED"
}
trap cleanup EXIT

heading() { printf '\n== %s\n' "$1"; }

pass() { printf 'PASS %s\n' "$1" | tee -a "$RESULTS"; }

# fail NAME [CONTAINER]: records the failure, prints the container's log, and carries on.
fail() {
    printf 'FAIL %s\n' "$1" | tee -a "$RESULTS" >&2
    if [ "${2:-}" != "" ]; then
        dump_log "$2" >&2
    fi
}

# stop NAME [CONTAINER]: a failure after which the rest of the section could say nothing.
stop() {
    fail "$@"
    exit "$STOPPED"
}

# section NAME FUNCTION: runs the function in a subshell, so that a stop or an unexpected error ends
# that section only, then removes what the section left behind.
section() {
    local status
    heading "$1"
    set +e
    (
        set -e
        "$2"
    )
    status=$?
    set -e
    if [ "$status" -eq "$STOPPED" ]; then
        printf 'The section stopped at the failure above.\n'
    elif [ "$status" -ne 0 ]; then
        fail "section '$1' ran to its end (a command failed with exit code $status)"
    fi
    if [ "$status" -ne 0 ]; then
        sweep 1
    else
        sweep 0
    fi
}

# expect NAME EXPECTED ACTUAL [CONTAINER]
expect() {
    if [ "$2" = "$3" ]; then
        pass "$1: $3"
    else
        fail "$1: expected '$2', got '$3'" "${4:-}"
    fi
}

# verify NAME CONTAINER COMMAND...: passes when the command succeeds. CONTAINER may be empty.
verify() {
    local name="$1" container="$2"
    shift 2
    if "$@"; then
        pass "$name"
    else
        fail "$name" "$container"
    fi
}

# refute NAME CONTAINER COMMAND...: passes when the command fails.
refute() {
    local name="$1" container="$2"
    shift 2
    if "$@"; then
        fail "$name" "$container"
    else
        pass "$name"
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

# wait_for_url CONTAINER URL: until the URL answers 200. Stops the section when the container stops
# or time runs out.
wait_for_url() {
    local deadline=$((SECONDS + WAIT_SECONDS))
    while [ "$SECONDS" -lt "$deadline" ]; do
        if [ "$(http_status "$2")" = "200" ]; then
            return 0
        fi
        if [ "$(container_state "$1")" != "running" ]; then
            stop "$1 answers 200 at $2 (it stopped first)" "$1"
        fi
        sleep 1
    done
    stop "$1 answers 200 at $2 within ${WAIT_SECONDS}s" "$1"
}

# wait_for_http CONTAINER PATH: the same, for a path of the app on its published port.
wait_for_http() { wait_for_url "$1" "$(url "$2")"; }

# wait_for_container_health CONTAINER: until Docker's own health check reports healthy, or
# unhealthy, or time runs out.
wait_for_container_health() {
    local deadline=$((SECONDS + WAIT_SECONDS)) health=""
    while [ "$SECONDS" -lt "$deadline" ]; do
        health="$(container_health "$1")"
        if [ "$health" = "healthy" ]; then
            pass "Docker reports $1 as healthy"
            return 0
        fi
        if [ "$health" = "unhealthy" ]; then
            break
        fi
        sleep 1
    done
    fail "Docker reports $1 as healthy within ${WAIT_SECONDS}s (its health is '$health')" "$1"
    printf 'Health checks Docker ran: '
    docker inspect --format '{{json .State.Health}}' "$1" || true
}

# wait_for_exit CONTAINER: prints the exit code once the container has stopped. Stops the section
# when it is still running after the wait.
wait_for_exit() {
    local deadline=$((SECONDS + WAIT_SECONDS))
    while [ "$SECONDS" -lt "$deadline" ]; do
        if [ "$(container_state "$1")" = "exited" ]; then
            docker inspect --format '{{.State.ExitCode}}' "$1"
            return 0
        fi
        sleep 1
    done
    stop "$1 exits within ${WAIT_SECONDS}s" "$1" >&2
}

# assert_log_is_json CONTAINER: every line the container wrote, before the app and from it, is one
# JSON object whose keys are those of the application log.
assert_log_is_json() {
    local count
    if count="$(docker logs "$1" 2>&1 | python3 -c '
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
        pass "all $count log lines of $1 are JSON objects with the application log keys"
    else
        fail "all log lines of $1 are JSON objects with the application log keys" "$1"
    fi
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
    if count="$(docker logs "$1" 2>&1 | python3 -c '
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
        pass "all $count log lines of $1 are JSON objects with the gateway log keys"
    else
        fail "all log lines of $1 are JSON objects with the gateway log keys" "$1"
    fi
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
# that started after the given time (as docker inspect prints times); prints that check's exit code,
# or "none" when Docker ran no such check within the wait.
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
    printf 'none'
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

remove_container_only() { docker rm --force "$1" >/dev/null; }

# health_field URL FIELD: a field of the health document at the URL; empty when there is no answer.
health_field() { curl --silent --max-time 10 "$1" | json_field "$2" 2>/dev/null || true; }

# api_field URL FIELD: a field of the JSON document (or Problem Details) at the URL; empty when there is no answer.
api_field() { curl --silent --max-time 10 "$1" | json_field "$2" 2>/dev/null || true; }

# The setup submission: the smoke test's fixed administrator. Not a real credential.
readonly SETUP_BODY='{"username":"smoke","password":"smoke-test-password","passwordConfirmation":"smoke-test-password"}'

# submit_setup: posts the setup submission to the app and prints the HTTP status.
submit_setup() {
    curl --silent --output /dev/null --max-time 10 --write-out '%{http_code}' \
        --header 'Content-Type: application/json' --data "$SETUP_BODY" "$(url /api/v1/setup)" || true
}

# sign_in JAR: signs in as the smoke administrator, keeping the session cookie in the file JAR, and
# prints the HTTP status. The header is the anti-forgery header every browser request carries.
sign_in() {
    curl --silent --output /dev/null --max-time 10 --write-out '%{http_code}' --cookie-jar "$1" \
        --header 'Content-Type: application/json' --header 'X-N8Tracks-Request: 1' \
        --data '{"username":"smoke","password":"smoke-test-password"}' "$(url /api/v1/session)" || true
}

# session_status JAR: the HTTP status of reading the current session with the cookie in the file JAR.
session_status() {
    curl --silent --output /dev/null --max-time 10 --write-out '%{http_code}' --cookie "$1" "$(url /api/v1/session)" || true
}

# ---------------------------------------------------------------------------------------------------
# Build: without both images nothing below can run, so a failure here ends the run.

heading "Build"
expected_version="$(tr -d '[:space:]' < "$ROOT/VERSION")"
if [ "${N8TRACKS_SMOKE_SKIP_BUILD:-0}" = "1" ]; then
    docker image inspect "$IMAGE" >/dev/null 2>&1 || stop "the image $IMAGE exists"
    pass "using the existing image $IMAGE"
    docker image inspect "$GATEWAY_IMAGE" >/dev/null 2>&1 || stop "the image $GATEWAY_IMAGE exists"
    pass "using the existing image $GATEWAY_IMAGE"
else
    docker build --tag "$IMAGE" "$ROOT" || stop "built $IMAGE for the host platform"
    pass "built $IMAGE for the host platform"
    docker build --file "$ROOT/src/n8Tracks.Gateway/Dockerfile" --tag "$GATEWAY_IMAGE" "$ROOT" \
        || stop "built $GATEWAY_IMAGE for the host platform"
    pass "built $GATEWAY_IMAGE for the host platform"
fi

media="$WORK/media"
mkdir -p "$media"
printf 'not a real track\n' > "$media/track.flac"
# A real track, and a link that leads out of the mount (inside the container, to its own /etc): the
# scan of the read-only mount catalogs the first and does not follow the second (invariant 2, #205).
cp "$ROOT/tests/n8Tracks.Api.Tests/Media/Fixtures/tone.mp3" "$media/tone.mp3"
ln -s /etc/hostname "$media/escape.mp3"

# api JAR METHOD PATH [BODY]: an API call with the session in the file JAR (and the anti-forgery
# header), printing the HTTP status on the last line after the body.
api() {
    curl --silent --max-time 10 --cookie "$1" --header 'X-N8Tracks-Request: 1' \
        --header 'Content-Type: application/json' --request "$2" ${4:+--data "$4"} \
        --write-out '\n%{http_code}' "$(url "$3")" || true
}

# job_field JSON KEY...: a value inside a job document, by its keys in turn; empty when it is not there.
job_field() {
    printf '%s' "$1" | python3 -c '
import json, sys

value = json.load(sys.stdin)
for key in sys.argv[1:]:
    value = value[key]
print(value if isinstance(value, str) else json.dumps(value))
' "${@:2}" 2>/dev/null || true
}

# scan_media JAR: starts a media scan with the session in JAR and prints its job once it has ended
# (or the last state seen, after WAIT_SECONDS).
scan_media() {
    local job document="" deadline
    job="$(api "$1" POST /api/v1/media/scans | sed '$d' | json_field jobId 2>/dev/null || true)"
    deadline=$((SECONDS + WAIT_SECONDS))
    while [ -n "$job" ] && [ "$SECONDS" -lt "$deadline" ]; do
        document="$(api "$1" GET "/api/v1/jobs/$job" | sed '$d')"
        case "$(job_field "$document" status)" in succeeded | failed) break ;; esac
        sleep 1
    done
    printf '%s' "$document"
}


# ---------------------------------------------------------------------------------------------------

image_contents() {
    local checked image files
    expect "the image declares only /data as a volume" '{"/data":{}}' \
        "$(docker image inspect --format '{{json .Config.Volumes}}' "$IMAGE")"

    # The Aspire AppHost is local development tooling: neither it nor any Aspire assembly is in an image.
    for checked in "$IMAGE:n8Tracks.Api.dll" "$GATEWAY_IMAGE:n8Tracks.Gateway.dll"; do
        image="${checked%:*}"
        files="$(docker run --rm --entrypoint find "$image" /app -type f)"
        verify "$image holds ${checked##*:}" "" grep -q "/${checked##*:}\$" <<<"$files"
        refute "$image holds no AppHost and no Aspire assembly" "" \
            grep -i -e 'n8Tracks\.AppHost' -e '/Aspire\.' <<<"$files"
    done

    expect "the gateway image's user" "$GATEWAY_UID" "$(docker image inspect --format '{{.Config.User}}' "$GATEWAY_IMAGE")"
    expect "the gateway image declares no volume" null "$(docker image inspect --format '{{json .Config.Volumes}}' "$GATEWAY_IMAGE")"
    files="$(docker run --rm --entrypoint sh "$GATEWAY_IMAGE" -c 'ls /app')"
    refute "the gateway image holds no part of the app" "" \
        grep -e 'n8Tracks\.Api' -e 'n8Tracks\.Application' -e 'n8Tracks\.Infrastructure' -e wwwroot <<<"$files"
}

section "Image contents" image_contents

# ---------------------------------------------------------------------------------------------------

mounted() {
    local name="$PREFIX-main" data="$WORK/data" health shell asset seeded scanned
    mkdir -p "$data"

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
    # The schema version and the job worker are checked when asked (#237); a fresh container has both healthy.
    expect "health's migrations component" healthy "$(printf '%s' "$health" | json_field components | json_field migrations | json_field status)" "$name"
    expect "health's migrations detail" "up to date" "$(printf '%s' "$health" | json_field components | json_field migrations | json_field detail)" "$name"
    expect "health's jobs component" healthy "$(printf '%s' "$health" | json_field components | json_field jobs | json_field status)" "$name"

    # A new instance refuses its API until the administrator is created; health and setup still answer.
    expect "setup status before setup" false "$(api_field "$(url /api/v1/setup/status)" complete)" "$name"
    expect "an API endpoint before setup" 503 "$(http_status "$(url /api/v1/songs)")" "$name"
    expect "the code of an API endpoint before setup" setup_required "$(api_field "$(url /api/v1/songs)" code)" "$name"
    expect "the setup submission" 201 "$(submit_setup)" "$name"
    expect "setup status after setup" true "$(api_field "$(url /api/v1/setup/status)" complete)" "$name"
    expect "a second setup submission" 409 "$(submit_setup)" "$name"
    expect "the API endpoint after setup asks for a session" 401 "$(http_status "$(url /api/v1/songs)")" "$name"
    expect "the code of the API endpoint without a session" not_authenticated "$(api_field "$(url /api/v1/songs)" code)" "$name"
    expect "signing in" 201 "$(sign_in "$WORK/cookies-mounted")" "$name"
    expect "the session after signing in" 200 "$(session_status "$WORK/cookies-mounted")" "$name"

    # Invariant 2 (#205): every scan step works on the read-only mount, and nothing tried to write.
    scanned="$(scan_media "$WORK/cookies-mounted")"
    expect "a scan of the read-only media mount" succeeded "$(job_field "$scanned" status)" "$name"
    expect "the audio files the scan found on the read-only mount" 2 "$(job_field "$scanned" result seen)" "$name"
    expect "the links out of the mount the scan did not follow" 1 "$(job_field "$scanned" result skippedLinks escaping)" "$name"
    refute "the log holds no write error under /media" "$name" \
        sh -c "docker logs '$name' 2>&1 | grep -i -e 'read-only file system' -e 'EROFS' -e 'UnauthorizedAccess'"

    shell="$(curl --silent --max-time 5 "$(url /)")"
    case "$shell" in
        *'<div id="root">'*'<base href="/"'* | *'<base href="/"'*'<div id="root">'*) pass "the shell page is served at / with its base href" ;;
        *) fail "the shell page is served at / with its base href (got: $shell)" "$name" ;;
    esac
    asset="$(printf '%s' "$shell" | grep -o 'assets/[^"]*\.js' | head -n 1 || true)"
    expect "the shell's script is served" 200 "$(http_status "$(url "/$asset")")" "$name"

    expect "the app process user ID" "$RUN_UID" "$(process_id "$name" Uid)" "$name"
    expect "the app process group ID" "$RUN_GID" "$(process_id "$name" Gid)" "$name"
    expect "the app process has no supplementary groups" "" "$(process_id "$name" Groups)" "$name"
    expect "the app is PID 1" "dotnet" "$(docker exec "$name" cat /proc/1/comm)" "$name"

    if [ "$(uname -s)" = "Linux" ]; then
        expect "owner of n8tracks.db on the host" "$RUN_UID:$RUN_GID" "$(stat -c '%u:%g' "$data/n8tracks.db")" "$name"
    else
        printf 'SKIP owner of n8tracks.db on the host: %s does not keep container ownership on bind mounts (see the named volume checks)\n' "$(uname -s)"
    fi

    refute "/media is read-only" "$name" docker exec "$name" sh -c 'touch /media/written 2>/dev/null'
    expect "/backup is absent when not mounted" absent "$(docker exec "$name" sh -c 'test -e /backup && echo present || echo absent')" "$name"

    wait_for_container_health "$name"
    assert_log_is_json "$name"

    seeded="$(seeded_timestamp "$data")"
    pass "seeded app_metadata row: $seeded"

    docker restart "$name" >/dev/null
    wait_for_http "$name" /health
    expect "seeded row after docker restart" "$seeded" "$(seeded_timestamp "$data")" "$name"
    expect "setup is still complete after docker restart" true "$(api_field "$(url /api/v1/setup/status)" complete)" "$name"
    expect "the session after docker restart" 200 "$(session_status "$WORK/cookies-mounted")" "$name"

    # A forgotten password, reset from inside the running container: the old session ends at once.
    verify "n8tracks reset-password --password-stdin on a set-up container" "$name" \
        sh -c "printf '%s\n' 'smoke-reset-password' | docker exec -i '$name' n8tracks reset-password --password-stdin"
    expect "the old session after the password reset" 401 "$(session_status "$WORK/cookies-mounted")" "$name"

    remove "$name"
    run_main
    wait_for_http "$name" /health
    expect "seeded row after docker rm and a fresh docker run on the same data" "$seeded" "$(seeded_timestamp "$data")" "$name"
    expect "health status after recreating the container" healthy "$(health_field "$(url /health)" status)" "$name"
    expect "setup is still complete after recreating the container" true "$(api_field "$(url /api/v1/setup/status)" complete)" "$name"
    remove "$name"
}

section "Data and read-only media mounted, PUID=$RUN_UID PGID=$RUN_GID" mounted

# ---------------------------------------------------------------------------------------------------

# song_titles JAR: the titles of every Song, sorted and joined with commas.
song_titles() {
    api "$1" GET /api/v1/songs | sed '$d' | python3 -c '
import json, sys

print(",".join(sorted(song["title"] for song in json.load(sys.stdin)["items"])))
' 2>/dev/null || true
}

# The Demo of disaster recovery: a backup made through the API, the container stopped, the backup
# restored by the command from the image with the same volumes, and the next start serving it.
disaster_recovery() {
    local name="$PREFIX-recovery" backup="$WORK/recovery-backup" jar="$WORK/cookies-recovery" job="" status="" result archive listed deadline
    mkdir -p "$backup"

    # /data is a named volume: the lock between the app and the command is a file lock, which a
    # bind mount through a desktop VM's file sharing need not carry between containers.
    run_app() {
        docker run --detach --name "$name" --publish "$HOST_PORT:8787" \
            --env PUID="$RUN_UID" --env PGID="$RUN_GID" \
            --volume "$PREFIX-data:/data" --volume "$backup:/backup" "$IMAGE" >/dev/null
    }

    # run_command ARGS...: n8tracks ARGS in a one-off container with the instance's volumes.
    run_command() {
        docker run --rm --env PUID="$RUN_UID" --env PGID="$RUN_GID" \
            --volume "$PREFIX-data:/data" --volume "$backup:/backup" "$IMAGE" n8tracks "$@"
    }

    run_app
    wait_for_http "$name" /health
    expect "setup of the instance to restore" 201 "$(submit_setup)" "$name"
    expect "signing in to the instance to restore" 201 "$(sign_in "$jar")" "$name"
    expect "creating the Song the backup holds" 201 "$(api "$jar" POST /api/v1/songs '{"title":"Before the backup"}' | tail -n 1)" "$name"

    job="$(api "$jar" POST /api/v1/backups | sed '$d' | json_field jobId 2>/dev/null || true)"
    deadline=$((SECONDS + WAIT_SECONDS))
    while [ "$SECONDS" -lt "$deadline" ]; do
        status="$(api "$jar" GET "/api/v1/jobs/$job" | sed '$d' | json_field status 2>/dev/null || true)"
        case "$status" in succeeded | failed) break ;; esac
        sleep 1
    done
    [ "$status" = "succeeded" ] || stop "the backup through the API succeeds (its job is '$status')" "$name"
    pass "a backup through the API"
    result="$(api "$jar" GET "/api/v1/jobs/$job" | sed '$d' | json_field result)"
    archive="$(printf '%s' "$result" | json_field name)"
    case "$(printf '%s' "$result" | json_field location)" in
        mount) archive="/backup/$archive" ;;
        *) archive="/data/backups/$archive" ;;
    esac

    expect "creating a Song after the backup" 201 "$(api "$jar" POST /api/v1/songs '{"title":"After the backup"}' | tail -n 1)" "$name"
    expect "the catalog before the restore" "After the backup,Before the backup" "$(song_titles "$jar")" "$name"

    refute "n8tracks restore refuses while the app runs on the data path" "$name" run_command restore "$archive"
    expect "the catalog after the refused restore" "After the backup,Before the backup" "$(song_titles "$jar")" "$name"

    docker stop "$name" >/dev/null
    listed="$(run_command list-backups 2>/dev/null || true)"
    case "$listed" in
        *manual*"$archive"*) pass "n8tracks list-backups lists the backup with its kind and path" ;;
        *) fail "n8tracks list-backups lists the backup with its kind and path (got: $listed)" "$name" ;;
    esac
    verify "n8tracks restore $archive with the container stopped" "$name" run_command restore "$archive"

    docker start "$name" >/dev/null
    wait_for_http "$name" /health
    expect "the old session after the restore" 401 "$(session_status "$jar")" "$name"
    expect "signing in after the restore" 201 "$(sign_in "$jar")" "$name"
    expect "the catalog after the restore is the backup's" "Before the backup" "$(song_titles "$jar")" "$name"
    expect "the database after the restore belongs to PUID:PGID" "$RUN_UID:$RUN_GID" \
        "$(docker exec "$name" stat -c '%u:%g' /data/n8tracks.db)" "$name"
    assert_log_is_json "$name"
    remove "$name"
}

section "Disaster recovery: back up, stop, restore with the command, start" disaster_recovery

# ---------------------------------------------------------------------------------------------------

# The Demo of recovering a deleted item (#105): a Song deleted through the API is listed by
# n8tracks list-deleted and put back by n8tracks restore-deleted, run with docker exec beside the
# running app, which serves it again at once.
deleted_recovery() {
    local name="$PREFIX-deleted" jar="$WORK/cookies-deleted" created shortcode revision listed restored
    run_app() {
        docker run --detach --name "$name" --publish "$HOST_PORT:8787" \
            --env PUID="$RUN_UID" --env PGID="$RUN_GID" --volume "$PREFIX-data:/data" "$IMAGE" >/dev/null
    }

    run_app
    wait_for_http "$name" /health
    expect "setup of the instance to recover in" 201 "$(submit_setup)" "$name"
    expect "signing in to the instance to recover in" 201 "$(sign_in "$jar")" "$name"
    created="$(api "$jar" POST /api/v1/songs '{"title":"Deleted by mistake"}')"
    expect "creating the Song to delete" 201 "$(printf '%s' "$created" | tail -n 1)" "$name"
    shortcode="$(printf '%s' "$created" | sed '$d' | json_field shortcode)"
    revision="$(printf '%s' "$created" | sed '$d' | json_field revision)"
    expect "deleting the Song through the API" 204 "$(curl --silent --output /dev/null --max-time 10 --write-out '%{http_code}' \
        --cookie "$jar" --header 'X-N8Tracks-Request: 1' --header 'Content-Type: application/json' \
        --header "If-Match: \"$revision\"" --request DELETE --data '{"confirmTitle":"Deleted by mistake"}' \
        "$(url "/api/v1/songs/$shortcode")" || true)" "$name"
    expect "the deleted Song is gone from the API" 404 "$(api "$jar" GET "/api/v1/songs/$shortcode" | tail -n 1)" "$name"

    listed="$(docker exec "$name" n8tracks list-deleted 2>/dev/null || true)"
    case "$listed" in
        *Song*"$shortcode (Deleted by mistake)"*" $shortcode "*) pass "n8tracks list-deleted lists the Song with its shortcode" ;;
        *) fail "n8tracks list-deleted lists the Song with its shortcode (got: $listed)" "$name" ;;
    esac

    restored="$(docker exec "$name" n8tracks restore-deleted "$shortcode" 2>/dev/null || true)"
    case "$restored" in
        "Restored Song $shortcode (Deleted by mistake)"*"Song: 1"*) pass "n8tracks restore-deleted $shortcode reports what it put back" ;;
        *) fail "n8tracks restore-deleted $shortcode reports what it put back (got: $restored)" "$name" ;;
    esac
    expect "the restored Song is readable again without a restart" 200 "$(api "$jar" GET "/api/v1/songs/$shortcode" | tail -n 1)" "$name"
    expect "the restored Song keeps its title" "Deleted by mistake" "$(song_titles "$jar")" "$name"
    refute "n8tracks restore-deleted refuses what is no longer in retention" "$name" docker exec "$name" n8tracks restore-deleted "$shortcode"
    expect "the database after the restore still belongs to PUID:PGID" "$RUN_UID:$RUN_GID" \
        "$(docker exec "$name" stat -c '%u:%g' /data/n8tracks.db)" "$name"
    assert_log_is_json "$name"
    remove "$name"
}

section "Recovering a deleted Song with the container commands" deleted_recovery

# ---------------------------------------------------------------------------------------------------

no_media() {
    local name="$PREFIX-nomedia"
    mkdir -p "$WORK/data-nomedia"
    docker run --detach --name "$name" --publish "$HOST_PORT:8787" \
        --env PUID="$RUN_UID" --env PGID="$RUN_GID" \
        --volume "$WORK/data-nomedia:/data" \
        "$IMAGE" >/dev/null
    wait_for_http "$name" /health
    expect "health status without media" degraded "$(health_field "$(url /health)" status)" "$name"
    expect "/media is absent when not mounted" absent "$(docker exec "$name" sh -c 'test -e /media && echo present || echo absent')" "$name"
    wait_for_container_health "$name"
    assert_log_is_json "$name"
    remove "$name"
}

section "No media mount" no_media

# ---------------------------------------------------------------------------------------------------

named_volume() {
    local name="$PREFIX-volume" volume="$PREFIX-data"
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
    remove_container_only "$name"

    # The same data under another user: the top-level owner differs, so everything is handed over.
    run_on_volume 2345 2346
    expect "owner of /data after changing PUID and PGID" "2345:2346" "$(owner_in_container /data)" "$name"
    expect "owner of the existing database after changing PUID and PGID" "2345:2346" "$(owner_in_container /data/n8tracks.db)" "$name"
    verify "an Information line reports the owner change" "$name" \
        log_has "$name" Information "Changed the owner of /data from $RUN_UID:$RUN_GID to 2345:2346"
    remove_container_only "$name"

    # Same user again: nothing to change, nothing written before the app.
    run_on_volume 2345 2346
    refute "no entrypoint line when the owner already matches" "$name" \
        grep -q 'n8Tracks.Entrypoint' <<<"$(docker logs "$name" 2>&1)"
    assert_log_is_json "$name"
    remove_container_only "$name"
    docker volume rm "$volume" >/dev/null
}

section "Named volume: ownership follows PUID and PGID" named_volume

# ---------------------------------------------------------------------------------------------------

sub_path() {
    local name="$PREFIX-subpath"
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
        *) fail "the shell page under the sub-path has base href /n8tracks/" "$name" ;;
    esac
    wait_for_container_health "$name"
    assert_log_is_json "$name"
    remove "$name"
}

section "Sub-path base URL and another port" sub_path

# ---------------------------------------------------------------------------------------------------

as_root() {
    local name="$PREFIX-root"
    docker run --detach --name "$name" --publish "$HOST_PORT:8787" --env PUID=0 --env PGID=0 "$IMAGE" >/dev/null
    wait_for_http "$name" /health
    expect "the app process user ID" 0 "$(process_id "$name" Uid)" "$name"
    verify "a Warning line says the app is running as root" "$name" log_has "$name" Warning "running as root"
    assert_log_is_json "$name"
    remove "$name"
}

section "Started as root: PUID=0" as_root

docker_user() {
    local name="$PREFIX-user"
    mkdir -p "$WORK/data-user"
    chmod 777 "$WORK/data-user"
    docker run --detach --name "$name" --publish "$HOST_PORT:8787" --user 4321:4322 \
        --env PUID=abc --env PGID=-1 \
        --volume "$WORK/data-user:/data" \
        "$IMAGE" >/dev/null
    wait_for_http "$name" /health
    pass "PUID and PGID were not read: invalid values did not stop the container"
    expect "the app process user ID" 4321 "$(process_id "$name" Uid)" "$name"
    expect "the app process group ID" 4322 "$(process_id "$name" Gid)" "$name"
    assert_log_is_json "$name"
    remove "$name"
}

section "Started through Docker's own user setting" docker_user

# ---------------------------------------------------------------------------------------------------

# invalid_ids PUID PGID VARIABLE: the container stops with one Error line naming the variable.
invalid_ids() {
    local name="$PREFIX-invalid" label="PUID=$1 PGID=$2" code
    docker run --detach --name "$name" --env PUID="$1" --env PGID="$2" "$IMAGE" >/dev/null
    code="$(wait_for_exit "$name")"
    refute "$label: the exit code is not 0 (it is $code)" "$name" [ "$code" = "0" ]
    assert_log_is_json "$name"
    expect "$label: lines written" 1 "$(docker logs "$name" 2>&1 | wc -l | tr -d '[:space:]')" "$name"
    verify "$label: an Error line names $3" "$name" log_has "$name" Error "Invalid configuration: $3 "
    remove "$name"
}

invalid_puid_and_pgid() {
    invalid_ids abc 1000 PUID
    invalid_ids 1000 -1 PGID
    invalid_ids 1.5 1000 PUID
    invalid_ids 99999999999 1000 PUID
    invalid_ids '1"\2' 1000 PUID
}

section "Complement: invalid PUID and PGID" invalid_puid_and_pgid

read_only_data() {
    local name="$PREFIX-readonly" code
    mkdir -p "$WORK/data-readonly"
    docker run --detach --name "$name" --publish "$HOST_PORT:8787" \
        --env PUID="$RUN_UID" --env PGID="$RUN_GID" \
        --volume "$WORK/data-readonly:/data:ro" \
        "$IMAGE" >/dev/null
    code="$(wait_for_exit "$name")"
    refute "the exit code is not 0 with /data read-only (it is $code)" "$name" [ "$code" = "0" ]
    refute "no health check passed with /data read-only (health: $(container_health "$name"))" "$name" \
        grep -qw 0 <<<"$(docker inspect --format '{{range .State.Health.Log}}{{.ExitCode}} {{end}}' "$name")"
    verify "an Error line names N8TRACKS_DATA_PATH" "$name" log_has "$name" Error "N8TRACKS_DATA_PATH"
    assert_log_is_json "$name"
    remove "$name"
}

section "Complement: /data mounted read-only" read_only_data

# host_listing FOLDER: every entry under FOLDER on the host, with its size, modification time, owner,
# and mode, one per line, sorted: what must not change when the container refuses to start.
host_listing() {
    python3 -c '
import os, sys

root = sys.argv[1]
for folder, directories, files in os.walk(root):
    for name in sorted(directories + files):
        path = os.path.join(folder, name)
        info = os.lstat(path)
        print(os.path.relpath(path, root), info.st_size, info.st_mtime_ns, f"{info.st_uid}:{info.st_gid}", oct(info.st_mode))
' "$1" | sort
}

# Invariant 2 (#387): the media folder, or a folder inside it or holding it, mounted at /data or
# /backup as well. The container refuses to start before any owner change, with one Error line, and
# the host media folder gains no file and changes no owner, mode, or time.
overlapping_mounts() {
    local name="$PREFIX-overlap" music="$WORK/overlap/music" data="$WORK/overlap/data" before

    mkdir -p "$music/sub" "$data"
    cp "$media/tone.mp3" "$music/tone.mp3"
    cp "$media/tone.mp3" "$music/sub/tone.mp3"
    before="$(host_listing "$WORK/overlap")"

    # refused LABEL FOLDER DOCKER-RUN-ARGUMENTS...: the container with those mounts stops at once.
    refused() {
        local label="$1" folder="$2" code
        shift 2
        docker run --detach --name "$name" --env PUID="$RUN_UID" --env PGID="$RUN_GID" "$@" "$IMAGE" >/dev/null
        code="$(wait_for_exit "$name")"
        refute "$label: the exit code is not 0 (it is $code)" "$name" [ "$code" = "0" ]
        assert_log_is_json "$name"
        verify "$label: an Error line names $folder and the media folder" "$name" \
            log_has "$name" Error "Refusing to start: $folder and the media folder /media overlap"
        refute "$label: no owner was changed" "$name" grep -q 'Changed the owner' <<<"$(docker logs "$name" 2>&1)"
        expect "$label: lines written" 1 "$(docker logs "$name" 2>&1 | wc -l | tr -d '[:space:]')" "$name"
        expect "$label: the host media folder is as it was" "$before" "$(host_listing "$WORK/overlap")" "$name"
        remove "$name"
    }

    refused "a folder inside the media folder at /backup" /backup \
        --volume "$music:/media:ro" --volume "$music/sub:/backup" --volume "$data:/data"
    refused "a folder inside the media folder at /data" /data \
        --volume "$music:/media:ro" --volume "$music/sub:/data"
    refused "the media folder itself at /data" /data \
        --volume "$music:/media:ro" --volume "$music:/data"
    refused "the folder holding the media folder at /backup" /backup \
        --volume "$music:/media:ro" --volume "$WORK/overlap:/backup" --volume "$data:/data"
    refused "a folder inside the media folder at /backup, with Docker's own user setting" /backup \
        --user "$RUN_UID:$RUN_GID" --volume "$music:/media:ro" --volume "$music/sub:/backup" --volume "$data:/data"
}

section "Complement: the media folder overlaps /data or /backup" overlapping_mounts

# ---------------------------------------------------------------------------------------------------

gateway_with_app() {
    local network="$PREFIX-net" app="$PREFIX-gateway-app" name="$PREFIX-gateway" health stopped
    docker network create "$network" >/dev/null

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

    health="$(curl --silent --max-time 10 "$(gateway_url /health)")"
    expect "gateway health status" healthy "$(printf '%s' "$health" | json_field status)" "$name"
    expect "gateway health upstream" reachable "$(printf '%s' "$health" | json_field upstream)" "$name"
    expect "gateway health compatible" true "$(printf '%s' "$health" | json_field compatible)" "$name"
    expect "gateway health reports the version in the VERSION file" "$expected_version" "$(printf '%s' "$health" | json_field version)" "$name"

    expect "the gateway process user ID" "$GATEWAY_UID" "$(process_id "$name" Uid)" "$name"
    expect "the gateway process group ID" "$GATEWAY_UID" "$(process_id "$name" Gid)" "$name"
    expect "the gateway is PID 1" "dotnet" "$(docker exec "$name" cat /proc/1/comm)" "$name"
    wait_for_container_health "$name"

    heading "Gateway complement: the app stops"
    docker stop "$app" >/dev/null
    stopped="$(docker inspect --format '{{.State.FinishedAt}}' "$app")"
    health="$(curl --silent --max-time 10 "$(gateway_url /health)")"
    expect "gateway health status with the app stopped" degraded "$(printf '%s' "$health" | json_field status)" "$name"
    expect "gateway health upstream with the app stopped" unreachable "$(printf '%s' "$health" | json_field upstream)" "$name"
    expect "gateway health compatible with the app stopped" null "$(printf '%s' "$health" | json_field compatible)" "$name"

    # The image's own health check, run by Docker after the app went away.
    expect "exit code of Docker's next health check of the degraded gateway" 0 "$(wait_for_health_check_after "$name" "$stopped")" "$name"
    expect "Docker's health status of the degraded gateway" healthy "$(container_health "$name")" "$name"
    verify "a Warning line says n8Tracks is unreachable" "$name" gateway_log_has "$name" Warning "n8Tracks is unreachable"

    docker start "$app" >/dev/null
    wait_for_http "$app" /health
    expect "gateway health status with the app back" healthy "$(health_field "$(gateway_url /health)" status)" "$name"
    assert_gateway_log_is_json "$name"
    remove "$name"
    remove "$app"
    docker network rm "$network" >/dev/null
}

section "Gateway: next to the app on a shared network" gateway_with_app

gateway_other_port() {
    local name="$PREFIX-gateway-port"
    docker run --detach --name "$name" --publish "$GATEWAY_HOST_PORT:9001" \
        --env N8TRACKS_API_URL="http://$PREFIX-gateway-app:8787" --env N8TRACKS_GATEWAY_PORT=9001 \
        "$GATEWAY_IMAGE" >/dev/null
    wait_for_url "$name" "$(gateway_url /health)"
    pass "the gateway answers on port 9001"
    wait_for_container_health "$name"
    remove "$name"
}

section "Gateway: another port" gateway_other_port

gateway_without_url() {
    local name="$PREFIX-gateway-nourl" code
    docker run --detach --name "$name" "$GATEWAY_IMAGE" >/dev/null
    code="$(wait_for_exit "$name")"
    refute "the exit code is not 0 without N8TRACKS_API_URL (it is $code)" "$name" [ "$code" = "0" ]
    expect "lines written" 1 "$(docker logs "$name" 2>&1 | wc -l | tr -d '[:space:]')" "$name"
    verify "one Error line names N8TRACKS_API_URL" "$name" gateway_log_has "$name" Error "N8TRACKS_API_URL is required"
    assert_gateway_log_is_json "$name"
    remove "$name"
}

section "Gateway complement: no N8TRACKS_API_URL" gateway_without_url

# The exit trap prints the summary and removes what is left.
if [ "$(count FAIL)" -ne 0 ]; then
    exit 1
fi
