#!/bin/sh
# Tests scripts/edge-version.sh: the short sha, the version an edge build reports, and its
# edge-<sha> tag. Also checks that both Dockerfiles can take such a version. Nothing is built.
#
#   scripts/tests/edge-version/run.sh
#
# Prints one PASS or FAIL line per check and exits 1 if any failed.
set -u

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../../.." && pwd)
script="$root/scripts/edge-version.sh"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

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

# gives <name> <VERSION> <sha> <short sha> <version> <tag>: the exact output of a build that goes ahead.
gives() {
  "$script" --version "$2" --sha "$3" >"$work/out" 2>"$work/err"
  code=$?
  printf 'short_sha=%s\nversion=%s\ntag=%s\n' "$4" "$5" "$6" >"$work/want"
  if [ "$code" -eq 0 ] && cmp -s "$work/want" "$work/out" && [ ! -s "$work/err" ]; then
    pass "$1"
  else
    fail "$1" "exit code $code" "wanted:" "$(cat "$work/want")" "actual:" "$(cat "$work/out")" "$(cat "$work/err")"
  fi
}

# refuses <name> <exit code> <text on stderr> <arguments...>: nothing on stdout.
refuses() {
  name=$1
  want_code=$2
  text=$3
  shift 3
  "$script" "$@" >"$work/out" 2>"$work/err"
  code=$?
  if [ "$code" -ne "$want_code" ]; then
    fail "$name" "exit code $code, expected $want_code" "$(cat "$work/out")" "$(cat "$work/err")"
  elif [ -s "$work/out" ]; then
    fail "$name" "printed output:" "$(cat "$work/out")"
  elif ! grep -Fq -- "$text" "$work/err"; then
    fail "$name" "stderr does not contain: $text" "$(cat "$work/err")"
  else
    pass "$name"
  fi
}

sha=abc1234def5678901234567890abcdef12345678

gives "a stable VERSION gets -edge.<sha>" 0.1.0 "$sha" abc1234 0.1.0-edge.abc1234 edge-abc1234
gives "a VERSION with a pre-release gets .edge.<sha>" 1.5.0-rc.1 "$sha" abc1234 1.5.0-rc.1.edge.abc1234 edge-abc1234
gives "a pre-release with a hyphen in it" 1.5.0-rc-1 "$sha" abc1234 1.5.0-rc-1.edge.abc1234 edge-abc1234
gives "the short sha is the first 7 characters, digits and a leading zero included" 2.10.3 \
  0123456789abcdef0123456789abcdef01234567 0123456 2.10.3-edge.0123456 edge-0123456
gives "a SHA-256 commit ID" 0.1.0 \
  fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210 fedcba9 0.1.0-edge.fedcba9 edge-fedcba9
gives "VERSION as read from a file, line ending included" "$(printf '0.1.0\r\n')" "$sha" abc1234 0.1.0-edge.abc1234 edge-abc1234

refuses "an abbreviated commit ID is refused" 1 "not a full commit ID" --version 0.1.0 --sha abc1234
refuses "an upper-case commit ID is refused" 1 "not a full commit ID" --version 0.1.0 --sha ABC1234DEF5678901234567890ABCDEF12345678
refuses "a branch name is refused" 1 "not a full commit ID" --version 0.1.0 --sha main
refuses "an empty VERSION is refused" 1 "VERSION is empty" --version "" --sha "$sha"
refuses "a two-part VERSION is refused" 1 "is not X.Y.Z" --version 0.1 --sha "$sha"
refuses "a VERSION with a leading v is refused" 1 "is not X.Y.Z" --version v0.1.0 --sha "$sha"
refuses "a VERSION with build metadata is refused" 1 "is not X.Y.Z" --version 0.1.0+build --sha "$sha"
refuses "a VERSION with an empty pre-release is refused" 1 "is not X.Y.Z" --version 0.1.0- --sha "$sha"
refuses "usage: no arguments" 2 "usage:"
refuses "usage: --sha left out" 2 "usage:" --version 0.1.0
refuses "usage: --version left out" 2 "usage:" --sha "$sha"
refuses "usage: unknown argument" 2 "usage:" --version 0.1.0 --sha "$sha" --short 8
refuses "usage: an option given twice" 2 "usage:" --version 0.1.0 --version 0.2.0 --sha "$sha"
refuses "usage: an option without its value" 2 "usage:" --version 0.1.0 --sha

# The repository's own VERSION file must give an edge version as it stands.
own=$(cat "$root/VERSION")
gives "the repository's VERSION ($own) gives an edge version" "$own" "$sha" abc1234 "$own-edge.abc1234" edge-abc1234

# An edge version is not always a valid NuGet version: a short sha of digits with a leading zero
# (0.1.0-edge.0123456) is refused by -p:Version, which fails the image build for that commit. The
# Dockerfiles therefore stamp it as the informational version, which /health reports.
for dockerfile in Dockerfile src/n8Tracks.Gateway/Dockerfile; do
  if grep -Fq -- '-p:InformationalVersion=' "$root/$dockerfile" &&
    ! grep -q -- '-p:Version=' "$root/$dockerfile"; then
    pass "$dockerfile stamps the version as the informational version"
  else
    fail "$dockerfile stamps the version as the informational version" \
      "expected -p:InformationalVersion=\"\$version\" and no -p:Version= in $dockerfile"
  fi
done

echo
echo "$passed passed, $failed failed"
[ "$failed" -eq 0 ] && [ "$passed" -gt 0 ]
