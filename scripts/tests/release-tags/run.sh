#!/bin/sh
# Tests scripts/release-tags.sh against the cases beside this file. Nothing is published, tagged,
# or looked up: every case hands the script its four facts and checks what it prints.
#
#   scripts/tests/release-tags/run.sh
#
# Each directory under fixtures/ is one case:
#   input      four lines: tag=, version= (the contents of VERSION), in_main=, published=
#   expected   the exact output of a release that goes ahead (exit code 0), or
#   error      for a release that must be refused (exit code 1, nothing on stdout): text the
#              message must contain, one piece per line
#   stderr     (optional, with `expected`) text the note on stderr must contain, one piece per line
# A case has `expected` or `error`, never both. Reading the `expected` files shows exactly which
# tags any release would publish.
#
# Prints one PASS or FAIL line per check and exits 1 if any failed.
set -u

here=$(cd "$(dirname "$0")" && pwd)
script="$here/../../release-tags.sh"
fixtures="$here/fixtures"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# Fewer cases than this means fixtures went missing, which must not look like a pass.
minimum_cases=47

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

# field <input file> <name>: the value after `name=` (empty when the line is `name=`).
field() {
  sed -n "s/^$2=//p" "$1"
}

# has_field <input file> <name>
has_field() {
  grep -q "^$2=" "$1"
}

# contains_all <file of pieces> <file searched>: lists the pieces that are missing in $missing.
contains_all() {
  missing=
  while IFS= read -r piece; do
    [ -n "$piece" ] || continue
    if ! grep -Fq -- "$piece" "$2"; then
      missing="$missing
  missing: $piece"
    fi
  done <"$1"
  [ -z "$missing" ]
}

cases=0
for dir in "$fixtures"/*/; do
  [ -d "$dir" ] || continue
  dir=${dir%/}
  name=$(basename "$dir")
  cases=$((cases + 1))

  if [ ! -f "$dir/input" ]; then
    fail "$name" "no input file"
    continue
  fi
  incomplete=
  for key in tag version in_main published; do
    has_field "$dir/input" "$key" || incomplete="$incomplete $key"
  done
  if [ -n "$incomplete" ]; then
    fail "$name" "input has no line for:$incomplete"
    continue
  fi
  if [ -f "$dir/expected" ] && [ -f "$dir/error" ]; then
    fail "$name" "has both expected and error"
    continue
  fi

  "$script" \
    --tag "$(field "$dir/input" tag)" \
    --version "$(field "$dir/input" version)" \
    --in-main "$(field "$dir/input" in_main)" \
    --published "$(field "$dir/input" published)" \
    >"$work/out" 2>"$work/err"
  code=$?

  if [ -f "$dir/expected" ]; then
    if [ "$code" -ne 0 ]; then
      fail "$name" "exit code $code, expected 0" "$(cat "$work/err")"
    elif ! cmp -s "$dir/expected" "$work/out"; then
      fail "$name" "output differs (expected, then actual):" "$(cat "$dir/expected")" "--" "$(cat "$work/out")"
    elif [ -f "$dir/stderr" ] && ! contains_all "$dir/stderr" "$work/err"; then
      fail "$name" "stderr:" "$(cat "$work/err")" "$missing"
    elif [ ! -f "$dir/stderr" ] && [ -s "$work/err" ]; then
      fail "$name" "unexpected stderr:" "$(cat "$work/err")"
    else
      pass "$name"
    fi
  elif [ -f "$dir/error" ]; then
    if [ "$code" -ne 1 ]; then
      fail "$name" "exit code $code, expected 1" "$(cat "$work/out")" "$(cat "$work/err")"
    elif [ -s "$work/out" ]; then
      fail "$name" "a refused release printed output:" "$(cat "$work/out")"
    elif ! contains_all "$dir/error" "$work/err"; then
      fail "$name" "message:" "$(cat "$work/err")" "$missing"
    else
      pass "$name"
    fi
  else
    fail "$name" "has neither expected nor error"
  fi
done

if [ "$cases" -ge "$minimum_cases" ]; then
  pass "found $cases cases (at least $minimum_cases)"
else
  fail "found $cases cases, expected at least $minimum_cases"
fi

# usage <name> <arguments...>: a call the script cannot act on is exit code 2 with nothing on
# stdout, so a workflow that forgets an input stops instead of publishing a first release.
usage() {
  name=$1
  shift
  "$script" "$@" >"$work/out" 2>"$work/err"
  code=$?
  if [ "$code" -ne 2 ]; then
    fail "$name" "exit code $code, expected 2" "$(cat "$work/out")" "$(cat "$work/err")"
  elif [ -s "$work/out" ]; then
    fail "$name" "printed output:" "$(cat "$work/out")"
  elif ! grep -q '^usage:' "$work/err"; then
    fail "$name" "no usage line:" "$(cat "$work/err")"
  else
    pass "$name"
  fi
}

usage "usage: no arguments"
usage "usage: --published left out" --tag v1.2.3 --version 1.2.3 --in-main true
usage "usage: --in-main left out" --tag v1.2.3 --version 1.2.3 --published ""
usage "usage: --version left out" --tag v1.2.3 --in-main true --published ""
usage "usage: --tag left out" --version 1.2.3 --in-main true --published ""
usage "usage: --in-main is not true or false" --tag v1.2.3 --version 1.2.3 --in-main yes --published ""
usage "usage: --in-main is empty" --tag v1.2.3 --version 1.2.3 --in-main "" --published ""
usage "usage: unknown argument" --tag v1.2.3 --version 1.2.3 --in-main true --published "" --push
usage "usage: an option given twice" --tag v1.2.3 --tag v1.2.4 --version 1.2.3 --in-main true --published ""
usage "usage: an option without its value" --version 1.2.3 --in-main true --published "" --tag

# The inputs as a workflow passes them: VERSION read from a file (here with Windows line endings),
# and the published tags one per line.
printf '1.2.2\r\n' >"$work/VERSION"
"$script" --tag v1.2.2 --version "$(cat "$work/VERSION")" --in-main true --published "$(printf 'latest\n1.3\n1.3.0\n1.2\n1.2.1\n1.2.0\n')" >"$work/out" 2>"$work/err"
code=$?
printf 'version=1.2.2\npush_exact=true\nimage_tags=1.2.2 1.2\nprerelease=false\nlatest=false\n' >"$work/want"
if [ "$code" -eq 0 ] && cmp -s "$work/want" "$work/out" && [ ! -s "$work/err" ]; then
  pass "VERSION from a file and published tags one per line"
else
  fail "VERSION from a file and published tags one per line" "exit code $code" "$(cat "$work/out")" "$(cat "$work/err")"
fi

# The repository's own VERSION file must be releasable as it stands.
root=$(cd "$here/../../.." && pwd)
own=$(cat "$root/VERSION")
if "$script" --tag "v$own" --version "$own" --in-main true --published "" >"$work/out" 2>"$work/err" &&
  grep -Fxq "version=$own" "$work/out"; then
  pass "the repository's VERSION ($own) is a version the rules accept"
else
  fail "the repository's VERSION ($own) is a version the rules accept" "$(cat "$work/out")" "$(cat "$work/err")"
fi

echo
echo "$passed passed, $failed failed"
[ "$failed" -eq 0 ] && [ "$passed" -gt 0 ]
