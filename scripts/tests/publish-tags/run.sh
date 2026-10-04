#!/bin/sh
# Tests scripts/publish-tags.sh against a stand-in registry. Nothing is published and no token is
# needed: fake-bin/docker and fake-bin/gh beside this file answer from the files in $FAKE_REGISTRY
# and record every call in $FAKE_REGISTRY/calls (their layout and the faults they can be told to
# produce are described at the top of fake-bin/docker).
#
#   scripts/tests/publish-tags/run.sh
#
# PUBLISH_TAGS_SCRIPT=<path> tests another copy of the script, which is how the test itself is
# shown to fail when the script does not put the floating tags back.
#
# Prints one PASS or FAIL line per check and exits 1 if any failed.
set -u

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../../.." && pwd)
script=${PUBLISH_TAGS_SCRIPT:-$root/scripts/publish-tags.sh}
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
  unset FAKE_GH_REFUSE_DELETE || true
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

echo
echo "$passed passed, $failed failed"
[ "$failed" -eq 0 ] && [ "$passed" -gt 0 ]
