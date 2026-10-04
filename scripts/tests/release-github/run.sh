#!/bin/sh
# Tests scripts/release-github.sh against a stand-in for `gh` and a throwaway git repository.
# Nothing is released and no token is needed: fake-bin/gh beside this file answers from the files
# in $FAKE_GITHUB and records every call in $FAKE_GITHUB/calls (layout at the top of that file).
#
#   scripts/tests/release-github/run.sh
#
# RELEASE_GITHUB_SCRIPT=<path> tests another copy of the script, which is how the test itself is
# shown to fail when the script replaces what a release already has.
#
# Prints one PASS or FAIL line per check and exits 1 if any failed.
set -u

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../../.." && pwd)
script=${RELEASE_GITHUB_SCRIPT:-$root/scripts/release-github.sh}
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

PATH="$here/fake-bin:$PATH"
export PATH

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
    "$code" "$(cat "$work/out")" "$(cat "$work/err")" "$(cat "$FAKE_GITHUB/calls" 2>/dev/null)"
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

sha256() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d' ' -f1
  else
    shasum -a 256 "$1" | cut -d' ' -f1
  fi
}

# --- A repository with a history of tags, oldest first. v1.4.3 is a patch tagged on main after
# v1.5.0-rc.1, and v9.9.9 is on a branch that main does not contain. ---

repo="$work/repo"
git init -q "$repo"
commit() {
  git -C "$repo" -c user.name=test -c user.email=test@example.invalid commit -q --allow-empty -m "$1"
}
for version in v0.1.0-rc.1 v0.1.0-rc.2 v0.1.0-rc.10 v0.1.0 v1.4.1 v1.4.2 v1.5.0-rc.1 v1.4.3 v1.5.0; do
  commit "$version"
  git -C "$repo" tag "$version"
done
git -C "$repo" tag not-a-version
git -C "$repo" tag v2-draft
git -C "$repo" checkout -q -b side v1.4.1
commit side
git -C "$repo" tag v9.9.9
git -C "$repo" checkout -q -

# The zip built by this run, and the one an earlier run attached (other bytes, same name).
mkdir -p "$work/built" "$work/earlier"
name=n8tracks-extension-1.4.2.zip
printf 'built by this run\n' >"$work/built/$name"
printf 'attached by an earlier run\n' >"$work/earlier/$name"
built_sum="$(sha256 "$work/built/$name")  $name"
earlier_sum="$(sha256 "$work/earlier/$name")  $name"

release="$work/github/release"

# fresh: no release exists.
fresh() {
  rm -rf "$work/github"
  mkdir -p "$work/github"
  FAKE_GITHUB="$work/github"
  export FAKE_GITHUB
  : >"$FAKE_GITHUB/calls"
  unset FAKE_GH_FAIL || true
}

# existing <draft> <asset files...>: a release (or draft) with handwritten notes.
existing() {
  fresh
  mkdir -p "$release/assets"
  echo "$1" >"$release/draft"
  echo false >"$release/prerelease"
  echo true >"$release/latest"
  echo v1.4.2 >"$release/title"
  echo "notes the maintainer edited by hand" >"$release/notes"
  shift
  for file in "$@"; do
    cp "$file" "$release/assets/"
  done
}

run() {
  (cd "$repo" && "$script" "$@") >"$work/out" 2>"$work/err"
  code=$?
}

# go <tag> <prerelease> <latest>
go() {
  run --tag "$1" --prerelease "$2" --latest "$3" --zip "$work/built/$name"
}

# writes: the calls that change something, one per line, command only.
writes() {
  grep -E '^(create|upload|edit) ' "$FAKE_GITHUB/calls" | cut -d' ' -f1 | tr '\n' ' ' | sed 's/ $//'
}

assets() {
  find "$release/assets" -type f -exec basename {} \; | sort | tr '\n' ' ' | sed 's/ $//'
}

setting() {
  cat "$release/$1" 2>/dev/null || echo "no release"
}

# --- A release that does not exist yet is created ---

fresh
go v1.4.2 false true
expect "stable: exit code" 0 "$code"
expect "stable: named after the tag" v1.4.2 "$(setting title)"
expect "stable: published, not a pre-release, marked latest" "false false true" "$(setting draft) $(setting prerelease) $(setting latest)"
expect "stable: both files are attached" "$name $name.sha256" "$(assets)"
expect "stable: the checksum file is one line, <sha256>  <name>" "$built_sum" "$(cat "$release/assets/$name.sha256")"
expect "stable: notes start from the previous stable tag" "generated from v1.4.1" "$(setting notes)"
expect "stable: prints the page, created, and what was attached" "url=https://github.com/o/r/releases/tag/v1.4.2
created=true
attached=$name $name.sha256" "$(cat "$work/out")"
expect "stable: one create, nothing else" create "$(writes)"

fresh
go v1.4.3 false false
expect "patch on an older minor: exit code" 0 "$code"
expect "patch on an older minor: not marked latest, and not left to GitHub" false "$(setting latest)"
expect "patch on an older minor: notes start from the next lower stable tag, not the newest" "generated from v1.4.2" "$(setting notes)"

fresh
go v1.5.0 false true
expect "stable after pre-releases: notes skip the pre-release and start from the previous stable tag" "generated from v1.4.3" "$(setting notes)"

fresh
go v0.1.0 false true
expect "first stable: notes start from the beginning, not from its own pre-releases" "generated from the beginning" "$(setting notes)"

fresh
go v1.5.0-rc.1 true false
expect "pre-release: exit code" 0 "$code"
expect "pre-release: marked pre-release, not latest" "true false" "$(setting prerelease) $(setting latest)"
expect "pre-release: notes start from the previous tag of any kind" "generated from v1.4.2" "$(setting notes)"

fresh
go v0.1.0-rc.10 true false
expect "pre-release after pre-release: rc.10 follows rc.2" "generated from v0.1.0-rc.2" "$(setting notes)"

fresh
go v0.1.0-rc.1 true false
expect "first tag ever: exit code" 0 "$code"
expect "first tag ever: notes start from the beginning" "generated from the beginning" "$(setting notes)"
expect "first tag ever: marked pre-release, not latest" "true false" "$(setting prerelease) $(setting latest)"

fresh
go v9.9.9 false true
expect "tag on a branch: only tags it contains count as previous" "generated from v1.4.1" "$(setting notes)"

# --- The same run again: nothing is touched ---

fresh
go v1.4.2 false true
: >"$FAKE_GITHUB/calls"
before=$(find "$release" -type f -exec cksum {} \; | sort)
go v1.4.2 false true
expect "run again: exit code" 0 "$code"
expect "run again: nothing is created, attached, or edited" "" "$(writes)"
expect "run again: nothing is downloaded either" "" "$(grep '^download ' "$FAKE_GITHUB/calls")"
expect "run again: the release is byte for byte what it was" "$before" "$(find "$release" -type f -exec cksum {} \; | sort)"
expect "run again: prints the page and that nothing was done" "url=https://github.com/o/r/releases/tag/v1.4.2
created=false
attached=" "$(cat "$work/out")"

# --- A release that exists is completed, never changed ---

existing false "$work/earlier/$name"
printf '%s\n' "$earlier_sum" >"$release/assets/$name.sha256"
go v1.4.2 true false
expect "existing release, other flags asked: exit code" 0 "$code"
expect "existing release: its notes are untouched" "notes the maintainer edited by hand" "$(setting notes)"
expect "existing release: its flags are untouched" "false true" "$(setting prerelease) $(setting latest)"
expect "existing release: its zip is not replaced" "attached by an earlier run" "$(cat "$release/assets/$name")"
expect "existing release: nothing is written" "" "$(writes)"

existing false "$work/earlier/$name"
go v1.4.2 false true
expect "checksum missing: exit code" 0 "$code"
expect "checksum missing: it is attached" "$name $name.sha256" "$(assets)"
expect "checksum missing: it is the checksum of the zip the release has, not of this run's" "$earlier_sum" "$(cat "$release/assets/$name.sha256")"
expect "checksum missing: the zip is not replaced" "attached by an earlier run" "$(cat "$release/assets/$name")"
expect "checksum missing: the notes are untouched" "notes the maintainer edited by hand" "$(setting notes)"
expect "checksum missing: says what was attached" "attached=$name.sha256" "$(sed -n 3p "$work/out")"

existing false
go v1.4.2 false true
expect "both files missing: exit code" 0 "$code"
expect "both files missing: both are attached" "$name $name.sha256" "$(assets)"
expect "both files missing: the checksum is this run's zip's" "$built_sum" "$(cat "$release/assets/$name.sha256")"
expect "both files missing: uploads only, no create and no edit" "upload upload" "$(writes)"
expect "both files missing: the notes are untouched" "notes the maintainer edited by hand" "$(setting notes)"

existing false
printf '%s\n' "$built_sum" >"$release/assets/$name.sha256"
go v1.4.2 false true
expect "zip missing, checksum matches: exit code" 0 "$code"
expect "zip missing, checksum matches: the zip is attached" "$name $name.sha256" "$(assets)"

existing false
printf '%s\n' "$earlier_sum" >"$release/assets/$name.sha256"
go v1.4.2 false true
expect "zip missing, checksum of another build: exit code" 1 "$code"
expect "zip missing, checksum of another build: no zip is attached beside it" "$name.sha256" "$(assets)"
expect "zip missing, checksum of another build: prints nothing on stdout" "" "$(cat "$work/out")"
says "zip missing, checksum of another build: says what to do" "gh release delete-asset v1.4.2 $name.sha256 --yes"

# --- A draft left by a killed run is completed and published ---

existing true "$work/earlier/$name"
go v1.4.2 false false
expect "draft: exit code" 0 "$code"
expect "draft: published with the flags asked for" "false false false" "$(setting draft) $(setting prerelease) $(setting latest)"
expect "draft: the missing checksum is attached" "$name $name.sha256" "$(assets)"
expect "draft: its notes are kept" "notes the maintainer edited by hand" "$(setting notes)"
expect "draft: prints the published page, not the draft's" "url=https://github.com/o/r/releases/tag/v1.4.2" "$(sed -n 1p "$work/out")"

# --- Failures ---

fresh
FAKE_GH_FAIL=view
export FAKE_GH_FAIL
go v1.4.2 false true
expect "the release cannot be read: exit code" 1 "$code"
expect "the release cannot be read: no release is created on a guess" "" "$(writes)"
says "the release cannot be read: says so" "could not read the release v1.4.2."

fresh
FAKE_GH_FAIL=create
export FAKE_GH_FAIL
go v1.4.2 false true
expect "create fails: exit code" 1 "$code"
expect "create fails: prints nothing on stdout" "" "$(cat "$work/out")"
says "create fails: says so" "could not create the release v1.4.2."
unset FAKE_GH_FAIL
go v1.4.2 false true
expect "create fails, run again: exit code" 0 "$code"
expect "create fails, run again: both files are attached" "$name $name.sha256" "$(assets)"

existing false "$work/earlier/$name"
FAKE_GH_FAIL=upload
export FAKE_GH_FAIL
go v1.4.2 false true
expect "attach fails: exit code" 1 "$code"
says "attach fails: names the file" "could not attach $name.sha256."

existing false "$work/earlier/$name"
FAKE_GH_FAIL=download
export FAKE_GH_FAIL
go v1.4.2 false true
expect "the published zip cannot be downloaded: exit code" 1 "$code"
expect "the published zip cannot be downloaded: no checksum is attached on a guess" "$name" "$(assets)"

existing true "$work/earlier/$name"
FAKE_GH_FAIL=edit
export FAKE_GH_FAIL
go v1.4.2 false true
expect "the draft cannot be published: exit code" 1 "$code"
says "the draft cannot be published: says so" "could not publish the draft release v1.4.2."

fresh
go v7.0.0 false true
expect "a tag the clone does not have: exit code" 1 "$code"
expect "a tag the clone does not have: nothing is created" "" "$(writes)"

# --- Calls the script cannot act on: exit code 2, and GitHub is never asked ---

# usage <name> <arguments...>
usage() {
  usage_name=$1
  shift
  fresh
  run "$@"
  if [ "$code" -ne 2 ]; then
    fail "$usage_name" "exit code $code, expected 2" "$(context)"
  elif [ -s "$work/out" ] || [ -s "$FAKE_GITHUB/calls" ]; then
    fail "$usage_name" "printed output or called GitHub" "$(context)"
  elif ! grep -q '^usage:' "$work/err"; then
    fail "$usage_name" "no usage line" "$(context)"
  else
    pass "$usage_name"
  fi
}

zip="$work/built/$name"
usage "usage: no arguments"
usage "usage: --tag left out" --prerelease false --latest true --zip "$zip"
usage "usage: --zip left out" --tag v1.4.2 --prerelease false --latest true
usage "usage: --latest left out" --tag v1.4.2 --prerelease false --zip "$zip"
usage "usage: a tag without its v" --tag 1.4.2 --prerelease false --latest true --zip "$zip"
usage "usage: a tag that is not a version" --tag v2-draft --prerelease false --latest true --zip "$zip"
usage "usage: --prerelease is not true or false" --tag v1.4.2 --prerelease yes --latest true --zip "$zip"
usage "usage: --latest is empty" --tag v1.4.2 --prerelease false --latest "" --zip "$zip"
usage "usage: a pre-release marked latest" --tag v1.5.0-rc.1 --prerelease true --latest true --zip "$zip"
usage "usage: a zip that does not exist" --tag v1.4.2 --prerelease false --latest true --zip "$work/built/none.zip"
usage "usage: a file that is not a zip" --tag v1.4.2 --prerelease false --latest true --zip "$0"
usage "usage: --tag given twice" --tag v1.4.2 --tag v1.4.3 --prerelease false --latest true --zip "$zip"
usage "usage: unknown argument" --tag v1.4.2 --prerelease false --latest true --zip "$zip" --clobber

echo
echo "$passed passed, $failed failed"
[ "$failed" -eq 0 ] && [ "$passed" -gt 0 ]
