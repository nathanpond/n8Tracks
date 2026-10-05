#!/bin/sh
# Entry point of the n8Tracks image.
#
# Started as root (the default), it applies PUID and PGID (default 1000:1000): it makes /data, and
# /backup when mounted, belong to that user if the top-level owner differs, drops to that user with
# no supplementary groups, and replaces itself with the app. Started as another user (Docker's
# --user / Compose's user:), it skips all of that and just runs the app.
#
# Given "n8tracks <command>" as the container's command (docker run --rm ... <image> n8tracks
# restore /backup/<file>), it does the same, then runs that command through the n8tracks wrapper
# instead of starting the app, so the files a restore writes belong to PUID:PGID too. Any other
# arguments are ignored: the app is configured by its environment only.
#
# Everything it writes is one JSON object per line with the keys of the application log
# (timestamp, level, message, properties). /media is never touched.

set -eu
umask 022

# The chosen user usually has no passwd entry and so no home directory.
export HOME=/tmp

# Printable ASCII only, at most 200 characters, escaped for a JSON string.
json_text() {
    printf '%s' "$1" | LC_ALL=C tr -cd '\040-\176' | cut -c1-200 | sed 's/\\/\\\\/g; s/"/\\"/g'
}

# log LEVEL MESSAGE [VARIABLE REASON]
log() {
    properties='"sourceContext":"n8Tracks.Entrypoint"'
    if [ "$#" -ge 4 ]; then
        properties="$properties,\"variable\":\"$(json_text "$3")\",\"reason\":\"$(json_text "$4")\""
    fi

    printf '{"timestamp":"%s","level":"%s","message":"%s","properties":{%s}}\n' \
        "$(date -u +%Y-%m-%dT%H:%M:%S.%3NZ)" "$1" "$(json_text "$2")" "$properties"
}

# is_id VALUE: a whole number from 0 to 4294967294, written with digits only.
is_id() {
    case "$1" in
        '' | *[!0-9]*) return 1 ;;
    esac

    [ "${#1}" -le 10 ] && [ "$1" -le 4294967294 ]
}

# read_id NAME VALUE: prints the ID without leading zeros, or reports the variable and fails.
read_id() {
    value=$(printf '%s' "$2" | sed 's/^0*\([0-9]\)/\1/')
    if is_id "$value"; then
        printf '%s' "$value"
        return 0
    fi

    reason="must be a whole number from 0 to 4294967294, but was '$2'."
    log Error "Invalid configuration: $1 $reason" "$1" "$reason"
    return 1
}

# fix_owner DIRECTORY: gives the directory and everything in it to the chosen user when its
# top-level owner differs. Symbolic links are changed themselves, never followed. A failure
# (a read-only or root-squashed mount) is a warning: the app's own startup check decides.
fix_owner() {
    [ -d "$1" ] || return 0

    owner=$(stat -c '%u:%g' "$1" 2>/dev/null) || return 0
    [ "$owner" != "$puid:$pgid" ] || return 0

    if chown -R -h "$puid:$pgid" "$1" 2>/dev/null; then
        log Information "Changed the owner of $1 from $owner to $puid:$pgid."
    else
        log Warning "Could not change the owner of $1 from $owner to $puid:$pgid (a read-only or root-squashed mount?). Continuing: n8Tracks will stop if it cannot write there."
    fi
}

# app [PREFIX...]: replaces this script with the app, so the app is PID 1 and receives the stop signal.
# Arguments given to the container are not passed on: the app is configured by its environment only.
app() {
    exec "$@" dotnet /app/n8Tracks.Api.dll
}

# A container command ("n8tracks <command> ..."), or nothing: the app.
command_given=false
if [ "$#" -gt 0 ] && [ "$1" = n8tracks ]; then
    command_given=true
    shift
fi

# Not root: Docker was told which user to run as, so PUID and PGID do not apply.
if [ "$(id -u)" != 0 ]; then
    if [ "$command_given" = true ]; then
        exec /usr/local/bin/n8tracks "$@"
    fi

    app
fi

valid=true
puid=$(read_id PUID "${PUID:-1000}") || { printf '%s\n' "$puid"; valid=false; }
pgid=$(read_id PGID "${PGID:-1000}") || { printf '%s\n' "$pgid"; valid=false; }
if [ "$valid" != true ]; then
    exit 1
fi

fix_owner /data
fix_owner /backup

if [ "$puid" = 0 ]; then
    log Warning "PUID is 0: n8Tracks is running as root, and the files it creates belong to root. Set PUID and PGID to an unprivileged user unless you need this."
fi

if ! setpriv --reuid="$puid" --regid="$pgid" --clear-groups true 2>/dev/null; then
    log Error "Could not switch to user $puid:$pgid. The container needs the SETUID and SETGID capabilities to apply PUID and PGID; otherwise start it as that user with Docker's own user setting."
    exit 1
fi

# The container command runs as the chosen user through the wrapper, which accepts only its own commands.
if [ "$command_given" = true ]; then
    exec setpriv --reuid="$puid" --regid="$pgid" --clear-groups /usr/local/bin/n8tracks "$@"
fi

app setpriv --reuid="$puid" --regid="$pgid" --clear-groups
