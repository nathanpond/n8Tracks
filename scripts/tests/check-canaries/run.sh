#!/bin/sh
# Tests scripts/check-canaries.sh against the fixture trees beside this file.
#
#   scripts/tests/check-canaries/run.sh
#
# base/ is a small tree that keeps warnings as errors: a root Directory.Build.props, one C#
# project, and the three JavaScript folders, whose "lint" and "typecheck" scripts are stand-ins
# (tools/lint.mjs, tools/typecheck.mjs) that report the canaries' marked lines the way ESLint and
# tsc do. The .NET half runs the real SDK. Against base/ the check must pass.
#
# Each directory under fixtures/ is one case, laid over a copy of base/, in which something has
# been silenced or the check cannot run:
#   overlay/   files added to, or replacing those of, the base (optional when `remove` is there)
#   remove     (optional) paths deleted from the base first, one per line
#   expected   one line per FAIL the check must print, no more, no fewer: the check's name, "|",
#              and text its reason must contain
#   passes     (optional) names of checks that must still PASS, one per line
# Every case must exit 1 and leave its tree as it found it.
#
# Prints one PASS or FAIL line per check and exits 1 if any failed.
set -u

here=$(cd "$(dirname "$0")" && pwd)
script="$here/../../check-canaries.sh"
fixtures="$here/fixtures"
base="$here/base"
work=$(mktemp -d)
work=$(cd "$work" && pwd -P)
trap 'rm -rf "$work"' EXIT
# The script under test makes its temporary copy here (a folder named check-canaries.*), so the
# test can see that it is removed. The SDK and npm leave files of their own beside it.
mkdir -p "$work/tmp"
TMPDIR="$work/tmp"
export TMPDIR

# copies: the temporary copies of the script under test that exist now.
copies() {
  find "$work/tmp" -maxdepth 1 -name 'check-canaries.*'
}

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

# build <case directory>: makes $work/tree, a git repository holding the base with the case's
# `remove` list and then its overlay applied.
build() {
  rm -rf "$work/tree"
  mkdir -p "$work/tree"
  cp -R "$base/." "$work/tree"
  if [ -n "$1" ] && [ -f "$1/remove" ]; then
    while IFS= read -r path; do
      [ -n "$path" ] && rm -rf "${work:?}/tree/$path"
    done <"$1/remove"
  fi
  if [ -n "$1" ] && [ -d "$1/overlay" ]; then
    cp -R "$1/overlay/." "$work/tree"
  fi
  git -C "$work/tree" init --quiet
  git -C "$work/tree" add --all
}

# fingerprint <directory>: every path under it with its type, size, and content hash.
fingerprint() {
  (cd "$1" && find . -path ./.git -prune -o -print | LC_ALL=C sort | while IFS= read -r path; do
    if [ -f "$path" ]; then
      printf '%s %s\n' "$path" "$(git hash-object "$path")"
    else
      printf '%s\n' "$path"
    fi
  done)
  git -C "$1" status --porcelain
}

# run <root>: leaves the exit code in $code, stdout in $work/out, stderr in $work/err, and in
# $unchanged whether the tree is as it was and the temporary folder is empty again.
run() {
  fingerprint "$1" >"$work/before"
  "$script" "$1" >"$work/out" 2>"$work/err"
  code=$?
  fingerprint "$1" >"$work/after"
  unchanged=yes
  cmp -s "$work/before" "$work/after" || unchanged="the tree changed"
  [ -z "$(copies)" ] || unchanged="the temporary copy was left behind"
}

output() {
  printf '%s\n' "exit code: $code" "$(cat "$work/out" "$work/err")"
}

# failures <expected file>: true when the last run printed exactly the expected FAIL lines, each
# followed by a reason that contains the expected text.
failures() {
  cut -d'|' -f1 "$1" | sort >"$work/expected"
  grep '^FAIL ' "$work/out" | sed 's/^FAIL //' | sort >"$work/actual"
  cmp -s "$work/expected" "$work/actual" || return 1
  while IFS='|' read -r check text; do
    awk -v name="FAIL $check" -v text="$text" \
      '$0 == name { getline; if (index($0, text)) found = 1 } END { exit !found }' \
      "$work/out" || return 1
  done <"$1"
}

build ""
run "$work/tree"
if [ "$code" -eq 0 ] && ! grep -q '^FAIL' "$work/out" && [ ! -s "$work/err" ]; then
  pass "the base tree, where warnings are errors, passes with exit 0"
else
  fail "the base tree, where warnings are errors, passes with exit 0" "$(output)"
fi
base_checks="dotnet src/Lib/Lib.csproj
web lint-warning
web lint-error
web typecheck
extension lint-error
extension typecheck
e2e lint-warning
e2e lint-error
e2e typecheck"
missing=$(printf '%s\n' "$base_checks" | while IFS= read -r name; do
  grep -q "^PASS $name: " "$work/out" || echo "$name"
done)
if [ -z "$missing" ] && [ "$(grep -c '^PASS ' "$work/out")" -eq 9 ]; then
  pass "the base tree is checked nine ways: the project, and each folder's lint and typecheck canaries"
else
  fail "the base tree is checked nine ways: the project, and each folder's lint and typecheck canaries" \
    "missing: $missing" "$(output)"
fi
if [ "$unchanged" = "yes" ]; then
  pass "a passing run leaves the tree unchanged and removes its temporary copy"
else
  fail "a passing run leaves the tree unchanged and removes its temporary copy" "$unchanged"
fi

cases=0
for case_dir in "$fixtures"/*/; do
  case_dir=${case_dir%/}
  name=$(basename "$case_dir")
  cases=$((cases + 1))

  if { [ ! -d "$case_dir/overlay" ] && [ ! -s "$case_dir/remove" ]; } || [ ! -s "$case_dir/expected" ]; then
    fail "$name: has an overlay/ tree or a remove list, and a non-empty expected list"
    continue
  fi

  build "$case_dir"
  run "$work/tree"
  if [ "$code" -eq 1 ] && failures "$case_dir/expected"; then
    pass "$name: fails with exactly the $(wc -l <"$case_dir/expected" | tr -d ' ') expected FAIL line(s), exit 1"
  else
    fail "$name: fails with exactly the expected FAIL line(s), exit 1" \
      "expected:" "$(cat "$case_dir/expected")" "$(output)"
  fi
  if [ -f "$case_dir/passes" ]; then
    missing=$(while IFS= read -r check; do
      grep -q "^PASS $check: " "$work/out" || echo "$check"
    done <"$case_dir/passes")
    if [ -z "$missing" ]; then
      pass "$name: the other checks named in passes still pass"
    else
      fail "$name: the other checks named in passes still pass" "missing: $missing" "$(output)"
    fi
  fi
  if [ "$unchanged" = "yes" ]; then
    pass "$name: the failing run leaves the tree unchanged and removes its temporary copy"
  else
    fail "$name: the failing run leaves the tree unchanged and removes its temporary copy" "$unchanged"
  fi
done

# A glob that matched nothing, or fixture directories that went missing, must not read as success.
minimum_cases=20
if [ "$cases" -ge "$minimum_cases" ]; then
  pass "found $cases fixture cases (at least $minimum_cases)"
else
  fail "found $cases fixture cases (at least $minimum_cases)"
fi

# What is copied: tracked files and untracked files git does not ignore, as they are on disk.
build ""
mkdir -p "$work/tree/src/Lib/ignored"
echo 'this is not C#' >"$work/tree/src/Lib/ignored/Ignored.cs"
run "$work/tree"
if [ "$code" -eq 0 ]; then
  pass "a file git ignores is not part of the copy (a broken ignored source file changes nothing)"
else
  fail "a file git ignores is not part of the copy (a broken ignored source file changes nothing)" "$(output)"
fi
build ""
echo 'this is not C#' >"$work/tree/src/Lib/Untracked.cs"
run "$work/tree"
if [ "$code" -eq 1 ] && grep -q '^FAIL dotnet src/Lib/Lib.csproj' "$work/out"; then
  pass "an untracked file git does not ignore is part of the copy (a broken one fails the build)"
else
  fail "an untracked file git does not ignore is part of the copy (a broken one fails the build)" "$(output)"
fi
build ""
cat >"$work/tree/Directory.Build.props" <<'PROPS'
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
PROPS
run "$work/tree"
if [ "$code" -eq 1 ] && grep -q '^FAIL dotnet src/Lib/Lib.csproj' "$work/out"; then
  pass "an uncommitted change to a tracked file is what gets checked"
else
  fail "an uncommitted change to a tracked file is what gets checked" "$(output)"
fi

# A setting from outside the repository is seen too: MSBuild reads properties from the environment.
build ""
fingerprint "$work/tree" >"$work/before"
NoWarn='CS8618;CA2200' "$script" "$work/tree" >"$work/out" 2>"$work/err"
code=$?
if [ "$code" -eq 1 ] && grep -q '^FAIL dotnet src/Lib/Lib.csproj' "$work/out"; then
  pass "a suppression that comes from the environment, not from a file, fails the check"
else
  fail "a suppression that comes from the environment, not from a file, fails the check" "$(output)"
fi

# Interrupted part-way: the temporary copy is still removed and the tree is untouched.
build ""
fingerprint "$work/tree" >"$work/before"
"$script" "$work/tree" >"$work/out" 2>"$work/err" &
pid=$!
waited=0
while [ -z "$(copies)" ] && [ "$waited" -lt 100 ]; do
  sleep 0.1
  waited=$((waited + 1))
done
sleep 1
started=$(copies)
kill -TERM "$pid"
wait "$pid"
code=$?
waited=0
while [ -n "$(copies)" ] && [ "$waited" -lt 100 ]; do
  sleep 0.1
  waited=$((waited + 1))
done
fingerprint "$work/tree" >"$work/after"
if [ -n "$started" ] && [ "$code" -ne 0 ] && [ -z "$(copies)" ] && cmp -s "$work/before" "$work/after"; then
  pass "an interrupted run (SIGTERM) exits non-zero, removes its temporary copy, and leaves the tree unchanged"
else
  fail "an interrupted run (SIGTERM) exits non-zero, removes its temporary copy, and leaves the tree unchanged" \
    "exit code: $code" "left behind: $(copies)"
fi

"$script" "$work/does-not-exist" >"$work/out" 2>"$work/err"
code=$?
if [ "$code" -eq 2 ]; then
  pass "a root that is not a directory is a usage error (exit 2)"
else
  fail "a root that is not a directory is a usage error (exit 2)" "exit code: $code"
fi

"$script" "$work/tree" extra >"$work/out" 2>"$work/err"
code=$?
if [ "$code" -eq 2 ]; then
  pass "a second argument is a usage error (exit 2): nothing narrows what is checked"
else
  fail "a second argument is a usage error (exit 2): nothing narrows what is checked" "exit code: $code"
fi

mkdir -p "$work/not-a-repository"
GIT_CEILING_DIRECTORIES="$work" "$script" "$work/not-a-repository" >"$work/out" 2>"$work/err"
code=$?
if [ "$code" -eq 2 ]; then
  pass "a root outside any git repository is an error (exit 2), not a pass"
else
  fail "a root outside any git repository is an error (exit 2), not a pass" "exit code: $code"
fi

echo
echo "$passed passed, $failed failed"
[ "$failed" -eq 0 ] && [ "$passed" -gt 0 ]
