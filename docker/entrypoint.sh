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
# Before anything else, either way, it refuses to start when the media folder (/media, or
# N8TRACKS_MEDIA_PATH) and /data or /backup (or N8TRACKS_DATA_PATH, N8TRACKS_BACKUP_PATH) are the same
# folder or one holds the other, however they were mounted: compared by device and inode, by real
# path, and by where each really is in /proc/self/mountinfo (#387). The owner change below would
# otherwise reach the media, and backups would be written into it.
#
# Everything it writes is one JSON object per line with the keys of the application log
# (timestamp, level, message, properties). /media is never touched: nothing here writes to it or
# changes its owner.

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

# mount_key PATH: "<major:minor> <path within that filesystem>" for the real path of PATH, by the
# longest mount point in /proc/self/mountinfo that holds it (the later of two at the same point).
mount_key() {
    real=$(readlink -f -- "$1" 2>/dev/null) || return 1
    [ -r /proc/self/mountinfo ] || return 1
    awk -v p="$real" '
        function unescape(s) { gsub(/\\040/, " ", s); gsub(/\\011/, "\t", s); gsub(/\\012/, "\n", s); gsub(/\\134/, "\\", s); return s }
        {
            point = unescape($5)
            if (p == point || point == "/" || index(p, point "/") == 1) {
                if (!found || length(point) >= length(best)) { found = 1; best = point; device = $3; root = unescape($4) }
            }
        }
        END {
            if (!found) exit 1
            rest = (best == "/") ? p : substr(p, length(best) + 1)
            path = (root == "/") ? rest : root rest
            if (path == "") path = "/"
            print device " " path
        }' /proc/self/mountinfo
}

# within INNER OUTER: whether the path INNER is OUTER or under it ("/" holds everything).
within() {
    [ "$2" = / ] && return 0
    [ "$1" = "$2" ] && return 0
    case "$1" in
        "$2"/*) return 0 ;;
    esac
    return 1
}

# overlaps A B: whether the existing folders A and B are one folder or one holds the other.
overlaps() {
    [ -d "$1" ] && [ -d "$2" ] || return 1

    [ "$(stat -L -c '%d:%i' -- "$1" 2>/dev/null)" = "$(stat -L -c '%d:%i' -- "$2" 2>/dev/null)" ] && return 0

    real_a=$(readlink -f -- "$1" 2>/dev/null) && real_b=$(readlink -f -- "$2" 2>/dev/null) || return 1
    within "$real_a" "$real_b" && return 0
    within "$real_b" "$real_a" && return 0

    key_a=$(mount_key "$1") && key_b=$(mount_key "$2") || return 1
    [ "${key_a%% *}" = "${key_b%% *}" ] || return 1
    within "${key_a#* }" "${key_b#* }" && return 0
    within "${key_b#* }" "${key_a#* }"
}

# refuse_overlaps: exits when a folder n8Tracks writes to and the media folder overlap.
refuse_overlaps() {
    media="${N8TRACKS_MEDIA_PATH:-/media}"
    for folder in /data /backup ${N8TRACKS_DATA_PATH:-} ${N8TRACKS_BACKUP_PATH:-}; do
        if overlaps "$folder" "$media"; then
            # One line of at most 200 characters (json_text): what overlaps, and what to do.
            log Error "Refusing to start: $folder and the media folder $media overlap (one folder, or one inside the other). Mount a folder outside your media at $folder; n8Tracks never writes to your media."
            exit 1
        fi
    done
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

# Before any owner change, and before the app or a command runs.
refuse_overlaps

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
