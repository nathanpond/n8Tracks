#!/bin/sh
# Tests scripts/publish-tags.sh and, at the end, scripts/published-tags.sh against a stand-in
# registry. Nothing is published and no token is needed: fake-bin/docker and fake-bin/gh beside
# this file answer from the files in $FAKE_REGISTRY and record every call in $FAKE_REGISTRY/calls
# (their layout and the faults they can be told to produce are described at the top of
# fake-bin/docker).
#
#   scripts/tests/publish-tags/run.sh
#
# PUBLISH_TAGS_SCRIPT=<path> and PUBLISHED_TAGS_SCRIPT=<path> test another copy of a script, which
# is how the test itself is shown to fail when the script does not put the floating tags back, or
# moves a released version.
#
# Prints one PASS or FAIL line per check and exits 1 if any failed.
set -u

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../../.." && pwd)
script=${PUBLISH_TAGS_SCRIPT:-$root/scripts/publish-tags.sh}
list_script=${PUBLISHED_TAGS_SCRIPT:-$root/scripts/published-tags.sh}
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

PATH="$here/fake-bin:$PATH"
export PATH

app=ghcr.io/nathanpond/n8tracks
gateway=ghcr.io/nathanpond/n8tracks-gateway

# Digests: the new build of each image, and the build the tags pointed at before.
app_new=sha256:$(printf '%064d' 11)
gateway_new=sha256:$(printf '%064d' 12)
app_old=sha256:$(printf '%064d' 21)
gateway_old=sha256:$(printf '%064d' 22)

passed=0
failed=0

pass() {
  passed=$((passed + 1))
  echo "PASS $1"
}

fail() {
  failed=$((failed + 1))
  echo "FAIL $1"
  shift
  for detail in "$@"; do
    printf '%s\n' "$detail" | sed 's/^/     /'
  done
}

context() {
  printf 'exit code %s\nstdout:\n%s\nstderr:\n%s\ncalls:\n%s\n' \
    "$code" "$(cat "$work/out")" "$(cat "$work/err")" "$(cat "$FAKE_REGISTRY/calls" 2>/dev/null)"
}

# expect <name> <wanted> <actual>
expect() {
  if [ "$2" = "$3" ]; then
    pass "$1"
  else
    fail "$1" "wanted: $2" "actual: $3" "$(context)"
  fi
}

# says <name> <text>: the text is on stderr.
says() {
  if grep -Fq -- "$2" "$work/err"; then
    pass "$1"
  else
    fail "$1" "stderr does not contain: $2" "$(context)"
  fi
}

key() {
  printf '%s' "$1" | tr '/' '_'
}

# fresh: an empty registry.
fresh() {
  rm -rf "$work/registry"
  mkdir -p "$work/registry"
  FAKE_REGISTRY="$work/registry"
  export FAKE_REGISTRY
  echo 100 >"$FAKE_REGISTRY/next_id"
  : >"$FAKE_REGISTRY/calls"
  unset FAKE_GH_REFUSE_DELETE FAKE_GH_FAIL_LIST || true
}

# pushed <repository> <digest>: the manifest is in the registry, untagged.
pushed() {
  mkdir -p "$FAKE_REGISTRY/manifests/$(key "$1")"
  next=$(($(cat "$FAKE_REGISTRY/next_id") + 1))
  echo "$next" >"$FAKE_REGISTRY/next_id"
  echo "$next" >"$FAKE_REGISTRY/manifests/$(key "$1")/$2"
}

# tagged <repository> <tag> <digest>
tagged() {
  mkdir -p "$FAKE_REGISTRY/tags/$(key "$1")"
  echo "$3" >"$FAKE_REGISTRY/tags/$(key "$1")/$2"
}

# tag <repository> <tag>: the digest the tag points at, or "absent".
tag() {
  cat "$FAKE_REGISTRY/tags/$(key "$1")/$2" 2>/dev/null || echo absent
}

# tags <repository>: every tag the repository has, on one line.
tags() {
  find "$FAKE_REGISTRY/tags/$(key "$1")" -type f -exec basename {} \; | sort | tr '\n' ' ' | sed 's/ $//'
}

# manifests <repository>: how many manifests the repository holds.
manifests() {
  find "$FAKE_REGISTRY/manifests/$(key "$1")" -type f | wc -l | tr -d ' '
}

fault() {
  echo "$*" >>"$FAKE_REGISTRY/faults"
}

# creates: the create calls so far, one per line, without the digests.
creates() {
  grep '^create ' "$FAKE_REGISTRY/calls" | cut -d' ' -f2 | tr '\n' ' ' | sed 's/ $//'
}

# first_run: both new images are pushed and no tag exists yet.
first_run() {
  fresh
  pushed "$app" "$app_new"
  pushed "$gateway" "$gateway_new"
}

# later_run: both new images are pushed; an earlier build holds edge and its own edge-<sha>.
later_run() {
  first_run
  pushed "$app" "$app_old"
  pushed "$gateway" "$gateway_old"
  tagged "$app" edge "$app_old"
  tagged "$app" edge-0ld0ld0 "$app_old"
  tagged "$gateway" edge "$gateway_old"
  tagged "$gateway" edge-0ld0ld0 "$gateway_old"
}

run() {
  "$script" "$@" >"$work/out" 2>"$work/err"
  code=$?
}

publish() {
  run --image "$app@$app_new" --image "$gateway@$gateway_new" --fixed edge-abc1234 --floating edge
}

# fixed_in_place <name>: both edge-<sha> tags of this run exist, on the new digests.
fixed_in_place() {
  expect "$1: the application's edge-<sha> is left in place" "$app_new" "$(tag "$app" edge-abc1234)"
  expect "$1: the gateway's edge-<sha> is left in place" "$gateway_new" "$(tag "$gateway" edge-abc1234)"
}

# --- A run that works ---

later_run
publish
expect "later run: exit code" 0 "$code"
expect "later run: the application's edge moved" "$app_new" "$(tag "$app" edge)"
expect "later run: the gateway's edge moved" "$gateway_new" "$(tag "$gateway" edge)"
fixed_in_place "later run"
expect "later run: the earlier build keeps its edge-<sha>" "$app_old $gateway_old" "$(tag "$app" edge-0ld0ld0) $(tag "$gateway" edge-0ld0ld0)"
expect "later run: fixed tags are created before any floating tag moves" \
  "$app:edge-abc1234 $gateway:edge-abc1234 $app:edge $gateway:edge" "$(creates)"
expect "later run: prints one line per tag" "tagged $app:edge-abc1234 $app_new
tagged $gateway:edge-abc1234 $gateway_new
tagged $app:edge $app_new
tagged $gateway:edge $gateway_new" "$(cat "$work/out")"
expect "later run: nothing is deleted" "" "$(grep '^gh ' "$FAKE_REGISTRY/calls")"

first_run
publish
expect "first run: exit code" 0 "$code"
expect "first run: both edge tags are created" "$app_new $gateway_new" "$(tag "$app" edge) $(tag "$gateway" edge)"
fixed_in_place "first run"

later_run
publish
publish
expect "the same build published twice: exit code" 0 "$code"
expect "the same build published twice: edge stays" "$app_new $gateway_new" "$(tag "$app" edge) $(tag "$gateway" edge)"

# --- A run that is no longer the head of main: no floating tag is given ---

later_run
run --image "$app@$app_new" --image "$gateway@$gateway_new" --fixed edge-abc1234 --floating ""
expect "superseded run: exit code" 0 "$code"
expect "superseded run: edge is not moved" "$app_old $gateway_old" "$(tag "$app" edge) $(tag "$gateway" edge)"
fixed_in_place "superseded run"
expect "superseded run: prints only the fixed tags" "tagged $app:edge-abc1234 $app_new
tagged $gateway:edge-abc1234 $gateway_new" "$(cat "$work/out")"

# --- The second image's edge cannot be moved: the first image's edge goes back ---

later_run
fault fail create "$gateway:edge"
publish
expect "second image fails: exit code" 1 "$code"
expect "second image fails: the application's edge is restored to its previous digest" "$app_old" "$(tag "$app" edge)"
expect "second image fails: the gateway's edge is where it was" "$gateway_old" "$(tag "$gateway" edge)"
fixed_in_place "second image fails"
expect "second image fails: prints nothing on stdout" "" "$(cat "$work/out")"
says "second image fails: names the tag that failed" "could not move $gateway:edge"
says "second image fails: says what was restored" "restored $app:edge to $app_old"
says "second image fails: says the tags are back" "every floating tag is back where it started"

# --- The same on the first ever run: the first image's edge is removed ---

first_run
fault fail create "$gateway:edge"
publish
expect "first run, second image fails: exit code" 1 "$code"
expect "first run, second image fails: the application's edge is removed" absent "$(tag "$app" edge)"
expect "first run, second image fails: the gateway has no edge" absent "$(tag "$gateway" edge)"
fixed_in_place "first run, second image fails"
expect "first run, second image fails: the image itself is kept and no placeholder is left" 1 "$(manifests "$app")"
says "first run, second image fails: says what was removed" "removed $app:edge"
says "first run, second image fails: says the tags are back" "every floating tag is back where it started"

# --- The move happened but its answer was lost: the failing tag itself goes back too ---

later_run
fault fail-after create "$gateway:edge"
publish
expect "answer lost: exit code" 1 "$code"
expect "answer lost: both edge tags are restored" "$app_old $gateway_old" "$(tag "$app" edge) $(tag "$gateway" edge)"
fixed_in_place "answer lost"

first_run
fault fail-after-once create "$gateway:edge"
publish
expect "first run, answer lost: exit code" 1 "$code"
expect "first run, answer lost: both edge tags are removed" "absent absent" "$(tag "$app" edge) $(tag "$gateway" edge)"
fixed_in_place "first run, answer lost"
expect "first run, answer lost: no placeholder is left" "1 1" "$(manifests "$app") $(manifests "$gateway")"

# One image had an edge, the other is new.
first_run
pushed "$app" "$app_old"
tagged "$app" edge "$app_old"
fault fail-after-once create "$gateway:edge"
publish
expect "one image new, answer lost: exit code" 1 "$code"
expect "one image new, answer lost: one edge restored, the other removed" "$app_old absent" "$(tag "$app" edge) $(tag "$gateway" edge)"

# --- The registry says yes and does nothing: caught by reading the tag back ---

later_run
fault ignore create "$gateway:edge"
publish
expect "move not applied: exit code" 1 "$code"
expect "move not applied: the application's edge is restored" "$app_old" "$(tag "$app" edge)"
says "move not applied: says where the tag points" "$gateway:edge points at $gateway_old after being set to $gateway_new"

# --- Failures before any floating tag moved ---

later_run
fault fail create "$gateway:edge-abc1234"
publish
expect "a fixed tag fails: exit code" 1 "$code"
expect "a fixed tag fails: edge is not touched" "$app_old $gateway_old" "$(tag "$app" edge) $(tag "$gateway" edge)"
expect "a fixed tag fails: no floating tag is attempted" "$app:edge-abc1234 $gateway:edge-abc1234" "$(creates)"
says "a fixed tag fails: says nothing moved" "no floating tag had been moved"

later_run
fault fail create "$app:edge"
publish
expect "first image fails: exit code" 1 "$code"
expect "first image fails: both edge tags are where they were" "$app_old $gateway_old" "$(tag "$app" edge) $(tag "$gateway" edge)"
expect "first image fails: the second image is not attempted" "$app:edge-abc1234 $gateway:edge-abc1234 $app:edge" "$(creates)"

later_run
fault fail inspect "$gateway:edge"
publish
expect "a tag cannot be read: exit code" 1 "$code"
expect "a tag cannot be read: nothing is created" "" "$(creates)"
says "a tag cannot be read: says nothing changed" "could not read $gateway:edge. Nothing was changed."

fresh
pushed "$app" "$app_new"
publish
expect "an image that was never pushed: exit code" 1 "$code"
expect "an image that was never pushed: no edge is created" "absent absent" "$(tag "$app" edge) $(tag "$gateway" edge)"

# --- Two floating tags: both go back ---

later_run
tagged "$app" next "$app_old"
fault fail create "$gateway:next"
run --image "$app@$app_new" --image "$gateway@$gateway_new" --fixed edge-abc1234 --floating "edge next"
expect "two floating tags: exit code" 1 "$code"
expect "two floating tags: all are where they started" "$app_old $gateway_old $app_old absent" \
  "$(tag "$app" edge) $(tag "$gateway" edge) $(tag "$app" next) $(tag "$gateway" next)"

# --- Putting a tag back fails too: the run says so and gives the command ---

later_run
fault fail create "$gateway:edge"
fault fail create "$app:edge" "$app_old"
publish
expect "restore fails: exit code" 1 "$code"
says "restore fails: says which tag is not restored" "COULD NOT restore $app:edge to $app_old"
says "restore fails: gives the command to do it by hand" "docker buildx imagetools create -t $app:edge $app@$app_old"
says "restore fails: does not claim the tags are back" "NOT every floating tag is back where it started"

first_run
fault fail create "$gateway:edge"
FAKE_GH_REFUSE_DELETE=1
export FAKE_GH_REFUSE_DELETE
publish
expect "removal fails: exit code" 1 "$code"
says "removal fails: says which tag is not removed" "COULD NOT remove $app:edge"
says "removal fails: does not claim the tags are back" "NOT every floating tag is back where it started"

# Outside GHCR there is no way to remove a tag, and the script says so instead of guessing.
fresh
pushed registry.example/app "$app_new"
pushed registry.example/gateway "$gateway_new"
fault fail create registry.example/gateway:edge
run --image "registry.example/app@$app_new" --image "registry.example/gateway@$gateway_new" --fixed edge-abc1234 --floating edge
expect "removal outside GHCR: exit code" 1 "$code"
says "removal outside GHCR: says it is not implemented" "removing a tag is only implemented for ghcr.io"

# --- A release: the exact version is created once and never moved ---

# release <floating tags>: both images, the exact version 1.4.2 once-only.
release() {
  run --image "$app@$app_new" --image "$gateway@$gateway_new" --once 1.4.2 --fixed "" --floating "$1"
}

# released_before: an earlier release holds 1.4.1, 1.4, and latest.
released_before() {
  first_run
  pushed "$app" "$app_old"
  pushed "$gateway" "$gateway_old"
  for earlier in 1.4.1 1.4 latest; do
    tagged "$app" "$earlier" "$app_old"
    tagged "$gateway" "$earlier" "$gateway_old"
  done
}

released_before
release "1.4 latest"
expect "release: exit code" 0 "$code"
expect "release: the exact version is created on both images" "$app_new $gateway_new" "$(tag "$app" 1.4.2) $(tag "$gateway" 1.4.2)"
expect "release: the floating tags moved" "$app_new $gateway_new $app_new $gateway_new" \
  "$(tag "$app" 1.4) $(tag "$gateway" 1.4) $(tag "$app" latest) $(tag "$gateway" latest)"
expect "release: the earlier version is untouched" "$app_old $gateway_old" "$(tag "$app" 1.4.1) $(tag "$gateway" 1.4.1)"
expect "release: the exact version is created before any floating tag moves" \
  "$app:1.4.2 $gateway:1.4.2 $app:1.4 $gateway:1.4 $app:latest $gateway:latest" "$(creates)"
expect "release: prints one line per tag" "tagged $app:1.4.2 $app_new
tagged $gateway:1.4.2 $gateway_new
tagged $app:1.4 $app_new
tagged $gateway:1.4 $gateway_new
tagged $app:latest $app_new
tagged $gateway:latest $gateway_new" "$(cat "$work/out")"

# The same release again: every tag is where it belongs, so nothing is written.
: >"$FAKE_REGISTRY/calls"
release "1.4 latest"
expect "release run again: exit code" 0 "$code"
expect "release run again: nothing is written to the registry" "" "$(creates)"
expect "release run again: nothing is deleted" "" "$(grep '^gh ' "$FAKE_REGISTRY/calls")"
expect "release run again: every tag is reported as kept" "kept $app:1.4.2 $app_new
kept $gateway:1.4.2 $gateway_new
kept $app:1.4 $app_new
kept $gateway:1.4 $gateway_new
kept $app:latest $app_new
kept $gateway:latest $gateway_new" "$(cat "$work/out")"

# A pre-release: only the exact version, no floating tag.
first_run
run --image "$app@$app_new" --image "$gateway@$gateway_new" --once 0.1.0-rc.1 --fixed "" --floating ""
expect "pre-release: exit code" 0 "$code"
expect "pre-release: only the exact version exists" "0.1.0-rc.1 0.1.0-rc.1" \
  "$(tags "$app") $(tags "$gateway")"

# An earlier run stopped after the application's exact version: the caller hands back the digest
# that tag already has, and the run completes the rest without writing that tag again.
released_before
tagged "$app" 1.4.2 "$app_new"
release "1.4 latest"
expect "interrupted release completed: exit code" 0 "$code"
expect "interrupted release completed: only what was missing is written" \
  "$gateway:1.4.2 $app:1.4 $gateway:1.4 $app:latest $gateway:latest" "$(creates)"
expect "interrupted release completed: the first line says kept" "kept $app:1.4.2 $app_new" "$(sed -n 1p "$work/out")"

# The exact version exists and points at another build: refused before anything changes.
released_before
tagged "$gateway" 1.4.2 "$gateway_old"
release "1.4 latest"
expect "published version would be overwritten: exit code" 1 "$code"
expect "published version would be overwritten: it is not moved" "$gateway_old" "$(tag "$gateway" 1.4.2)"
expect "published version would be overwritten: nothing is written" "" "$(creates)"
expect "published version would be overwritten: the other image gets no tag" absent "$(tag "$app" 1.4.2)"
expect "published version would be overwritten: prints nothing on stdout" "" "$(cat "$work/out")"
says "published version would be overwritten: says why" "$gateway:1.4.2 is already published as $gateway_old and is never moved"

released_before
fault fail inspect "$gateway:1.4.2"
release "1.4 latest"
expect "a once-only tag cannot be read: exit code" 1 "$code"
expect "a once-only tag cannot be read: nothing is written" "" "$(creates)"
says "a once-only tag cannot be read: says nothing changed" "could not read $gateway:1.4.2. Nothing was changed."

# A floating tag fails after the exact version was created: the floating tags go back, the exact
# version stays, and the next run completes the release.
released_before
fault fail-once create "$gateway:latest"
release "1.4 latest"
expect "release, a floating tag fails: exit code" 1 "$code"
expect "release, a floating tag fails: the exact version stays on both images" "$app_new $gateway_new" "$(tag "$app" 1.4.2) $(tag "$gateway" 1.4.2)"
expect "release, a floating tag fails: every floating tag is back" "$app_old $gateway_old $app_old $gateway_old" \
  "$(tag "$app" 1.4) $(tag "$gateway" 1.4) $(tag "$app" latest) $(tag "$gateway" latest)"
release "1.4 latest"
expect "release, run again after the failure: exit code" 0 "$code"
expect "release, run again after the failure: the floating tags moved" "$app_new $gateway_new $app_new $gateway_new" \
  "$(tag "$app" 1.4) $(tag "$gateway" 1.4) $(tag "$app" latest) $(tag "$gateway" latest)"

# --- Read only: where a tag points ---

released_before
run --digest "$app:1.4.1" --digest "$gateway:1.4.2"
expect "read: exit code" 0 "$code"
expect "read: prints the digest, or absent" "$app:1.4.1 $app_old
$gateway:1.4.2 absent" "$(cat "$work/out")"
expect "read: nothing is written" "" "$(creates)"

released_before
fault fail inspect "$gateway:1.4.2"
run --digest "$app:1.4.1" --digest "$gateway:1.4.2"
expect "read, the registry cannot be asked: exit code" 1 "$code"
expect "read, the registry cannot be asked: prints nothing on stdout" "" "$(cat "$work/out")"
says "read, the registry cannot be asked: names the tag" "could not read $gateway:1.4.2."

# --- Calls the script cannot act on: exit code 2, and the registry is never asked ---

# usage <name> <arguments...>
usage() {
  name=$1
  shift
  fresh
  run "$@"
  if [ "$code" -ne 2 ]; then
    fail "$name" "exit code $code, expected 2" "$(context)"
  elif [ -s "$work/out" ] || [ -s "$FAKE_REGISTRY/calls" ]; then
    fail "$name" "printed output or called the registry" "$(context)"
  elif ! grep -q '^usage:' "$work/err"; then
    fail "$name" "no usage line" "$(context)"
  else
    pass "$name"
  fi
}

usage "usage: no arguments"
usage "usage: no image" --fixed edge-abc1234 --floating edge
usage "usage: --fixed left out" --image "$app@$app_new" --floating edge
usage "usage: --floating left out" --image "$app@$app_new" --fixed edge-abc1234
usage "usage: no tag at all" --image "$app@$app_new" --fixed "" --floating ""
usage "usage: an image given by tag" --image "$app:edge" --fixed edge-abc1234 --floating edge
usage "usage: a short digest" --image "$app@sha256:abc" --fixed edge-abc1234 --floating edge
usage "usage: the same repository twice" --image "$app@$app_new" --image "$app@$app_old" --fixed edge-abc1234 --floating edge
usage "usage: a tag that is not a tag" --image "$app@$app_new" --fixed "edge/abc" --floating edge
usage "usage: a tag both fixed and floating" --image "$app@$app_new" --fixed edge --floating edge
usage "usage: unknown argument" --image "$app@$app_new" --fixed edge-abc1234 --floating edge --push
usage "usage: an option without its value" --image "$app@$app_new" --fixed edge-abc1234 --floating
usage "usage: a tag both once-only and floating" --image "$app@$app_new" --once 1.4.2 --fixed "" --floating 1.4.2
usage "usage: --once given twice" --image "$app@$app_new" --once 1.4.2 --once 1.4.3 --fixed "" --floating ""
usage "usage: --digest with an image" --digest "$app:1.4.2" --image "$app@$app_new"
usage "usage: --digest with tags" --digest "$app:1.4.2" --fixed "" --floating ""
usage "usage: --digest of a digest" --digest "$app@$app_new"
usage "usage: --digest without a tag" --digest "$app"

# --- scripts/published-tags.sh: the tags an image already has ---

list() {
  "$list_script" "$@" >"$work/out" 2>"$work/err"
  code=$?
}

released_before
tagged "$app" edge "$app_new"
list "$app"
expect "list: exit code" 0 "$code"
expect "list: prints every tag of the image" "1.4 1.4.1 edge latest" "$(sort "$work/out" | tr '\n' ' ' | sed 's/ $//')"
expect "list: changes nothing" "" "$(grep -E '^(create|gh DELETE) ' "$FAKE_REGISTRY/calls")"

first_run
list "$app"
expect "list, pushed but never tagged: exit code" 0 "$code"
expect "list, pushed but never tagged: prints nothing" "" "$(cat "$work/out")"

fresh
list "$app"
expect "list, no package yet: exit code" 0 "$code"
expect "list, no package yet: prints nothing, not the error body" "" "$(cat "$work/out")"
says "list, no package yet: says so" "nothing is published"

released_before
FAKE_GH_FAIL_LIST=1
export FAKE_GH_FAIL_LIST
list "$app"
expect "list, GitHub cannot be asked: exit code" 1 "$code"
expect "list, GitHub cannot be asked: prints nothing on stdout" "" "$(cat "$work/out")"
says "list, GitHub cannot be asked: says so" "could not list the tags of $app."

fresh
list
expect "list, no argument: exit code" 2 "$code"
list registry.example/app
expect "list, not on GHCR: exit code" 2 "$code"
list "$app:edge"
expect "list, a tag instead of an image: exit code" 2 "$code"
expect "list, usage errors never ask GitHub" "" "$(cat "$FAKE_REGISTRY/calls")"

echo
echo "$passed passed, $failed failed"
[ "$failed" -eq 0 ] && [ "$passed" -gt 0 ]
