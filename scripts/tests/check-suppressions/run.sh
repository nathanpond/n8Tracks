#!/bin/sh
# Tests scripts/check-suppressions.sh against the fixture trees beside this file.
#
#   scripts/tests/check-suppressions/run.sh
#
# Each directory under fixtures/ is one case:
#   bad/       a small root tree with suppressions that have no rationale
#   expected   the `file:line` of every finding the script must report for bad/, no more, no less
#   good/      (optional) the same tree with rationales: exit 0 and no output at all
# The script reads `git ls-files`, so a new fixture is seen only once it is staged (`git add`).
#
# Prints one PASS or FAIL line per check and exits 1 if any failed.
set -u

here=$(cd "$(dirname "$0")" && pwd)
script="$here/../../check-suppressions.sh"
fixtures="$here/fixtures"
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

# run <root>: leaves the exit code in $code, stdout in $work/out, stderr in $work/err.
run() {
  "$script" "$1" >"$work/out" 2>"$work/err"
  code=$?
}

cases=0
for case_dir in "$fixtures"/*/; do
  name=$(basename "$case_dir")
  cases=$((cases + 1))

  if [ ! -d "$case_dir/bad" ] || [ ! -s "$case_dir/expected" ]; then
    fail "$name: has a bad/ tree and a non-empty expected list"
    continue
  fi

  run "$case_dir/bad"
  sort "$case_dir/expected" >"$work/expected"
  # "file:line: what" -> "file:line"
  sed 's/^\([^:]*:[0-9]*\): .*$/\1/' "$work/out" | sort >"$work/actual"
  if [ "$code" -eq 1 ] && cmp -s "$work/expected" "$work/actual"; then
    pass "$name: bad/ is reported at $(wc -l <"$work/expected" | tr -d ' ') expected line(s), exit 1"
  else
    fail "$name: bad/ is reported at exactly the expected lines, exit 1" \
      "exit code: $code" \
      "expected (<) against reported (>):" \
      "$(diff "$work/expected" "$work/actual")" \
      "output:" "$(cat "$work/out" "$work/err")"
  fi

  if [ -d "$case_dir/good" ]; then
    run "$case_dir/good"
    if [ "$code" -eq 0 ] && [ ! -s "$work/out" ] && [ ! -s "$work/err" ]; then
      pass "$name: good/ passes with exit 0 and no output"
    else
      fail "$name: good/ passes with exit 0 and no output" \
        "exit code: $code" "$(cat "$work/out" "$work/err")"
    fi
  fi
done

# A glob that matched nothing, or a fixture directory that went missing, must not read as success.
minimum_cases=17
if [ "$cases" -ge "$minimum_cases" ]; then
  pass "found $cases fixture cases (at least $minimum_cases)"
else
  fail "found $cases fixture cases (at least $minimum_cases)"
fi

# What the scan leaves out, in a throwaway repository: build and dependency folders, untracked
# files, and this test's own fixtures when the tree above them is scanned.
repo="$work/repo"
mkdir -p "$repo"
git -C "$repo" init --quiet
unexplained='#pragma warning disable CS0618'
for directory in bin obj node_modules dist src/bin src/obj web/node_modules web/dist \
  scripts/tests/check-suppressions/fixtures/any/bad; do
  mkdir -p "$repo/$directory"
  echo "$unexplained" >"$repo/$directory/Skipped.cs"
done
git -C "$repo" add --force .
echo "$unexplained" >"$repo/Untracked.cs"

run "$repo"
if [ "$code" -eq 0 ] && [ ! -s "$work/out" ]; then
  pass "bin/, obj/, node_modules/, dist/, the fixtures, and untracked files are not scanned"
else
  fail "bin/, obj/, node_modules/, dist/, the fixtures, and untracked files are not scanned" \
    "exit code: $code" "$(cat "$work/out" "$work/err")"
fi

# The same files are found once they are tracked and outside those folders.
mkdir -p "$repo/src/binary" "$repo/scripts/tests/other"
echo "$unexplained" >"$repo/src/binary/Found.cs"
echo "$unexplained" >"$repo/scripts/tests/other/Found.cs"
git -C "$repo" add Untracked.cs src/binary scripts/tests/other
run "$repo"
printf '%s\n' 'Untracked.cs:1' 'scripts/tests/other/Found.cs:1' 'src/binary/Found.cs:1' | sort >"$work/expected"
sed 's/^\([^:]*:[0-9]*\): .*$/\1/' "$work/out" | sort >"$work/actual"
if [ "$code" -eq 1 ] && cmp -s "$work/expected" "$work/actual"; then
  pass "a tracked file outside those folders is scanned"
else
  fail "a tracked file outside those folders is scanned" \
    "exit code: $code" "$(cat "$work/out" "$work/err")"
fi

# The fixtures are scanned when the root is inside them (which is how every case above runs).
run "$repo/scripts/tests/check-suppressions/fixtures/any/bad"
if [ "$code" -eq 1 ] && grep -q '^Skipped.cs:1: ' "$work/out"; then
  pass "a root inside the fixtures folder is scanned"
else
  fail "a root inside the fixtures folder is scanned" \
    "exit code: $code" "$(cat "$work/out" "$work/err")"
fi

run "$work/does-not-exist"
if [ "$code" -eq 2 ]; then
  pass "a root that is not a directory is a usage error (exit 2)"
else
  fail "a root that is not a directory is a usage error (exit 2)" "exit code: $code"
fi

mkdir -p "$work/not-a-repository"
(
  cd "$work/not-a-repository" || exit 1
  GIT_CEILING_DIRECTORIES="$work" "$script" . >"$work/out" 2>"$work/err"
)
code=$?
if [ "$code" -eq 2 ]; then
  pass "a root outside any git repository is an error (exit 2), not a pass"
else
  fail "a root outside any git repository is an error (exit 2), not a pass" "exit code: $code"
fi

echo
echo "$passed passed, $failed failed"
[ "$failed" -eq 0 ] && [ "$passed" -gt 0 ]
