#!/bin/sh
# Tests scripts/check-suppressions.sh against the fixture trees beside this file.
#
#   scripts/tests/check-suppressions/run.sh
#
# base/ is a small clean root: a root Directory.Build.props, a project, a workflow, a Dockerfile,
# a shell script, and the three JavaScript folders. On its own it must pass with no output.
#
# Each directory under fixtures/ is one case, laid over a copy of base/:
#   bad/       files added to, or replacing those of, the base (optional when `remove` is there)
#   remove     (optional) paths deleted from the base first, one per line
#   expected   one line per finding the script must report, no more, no fewer:
#              `file:line` or `file`, then optionally a space and text the finding must contain
#   good/      (optional) the bad files with rationales, or the legitimate form of the same thing:
#              exit 0 and no output at all
#   excused/   (optional) the bad files with a rationale comment added: for a setting no comment
#              can excuse, so the script must report exactly what it reports for bad/
#   setup      (optional) a shell fragment run in the built tree before it is scanned, for what
#              a copied file cannot hold (a file mode, a submodule entry)
# The cases named config-* are the configuration half of the guard; the others, the rationale half.
#
# Prints one PASS or FAIL line per check and exits 1 if any failed.
set -u

here=$(cd "$(dirname "$0")" && pwd)
script="$here/../../check-suppressions.sh"
fixtures="$here/fixtures"
base="$here/base"
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

# build <case directory> <overlay name>: makes $work/tree, a git repository holding the base with
# the case's `remove` list and then the overlay applied. The script reads `git ls-files`.
build() {
  rm -rf "$work/tree"
  mkdir -p "$work/tree"
  cp -R "$base/." "$work/tree"
  if [ -n "$1" ] && [ -f "$1/remove" ]; then
    while IFS= read -r path; do
      [ -n "$path" ] && rm -rf "${work:?}/tree/$path"
    done <"$1/remove"
  fi
  if [ -n "$1" ] && [ -d "$1/$2" ]; then
    cp -R "$1/$2/." "$work/tree"
  fi
  git -C "$work/tree" init --quiet
  git -C "$work/tree" add --all --force
  if [ -n "$1" ] && [ -f "$1/setup" ]; then
    (cd "$work/tree" && sh "$1/setup")
  fi
}

# reported <expected file>: true when the last run exited 1 and reported exactly the expected
# places, each finding containing the text the expected line asks for.
reported() {
  [ "$code" -eq 1 ] || return 1
  cut -d' ' -f1 "$1" | sort >"$work/expected"
  # "file:line: what" or "file: what" -> the part before the first ": "
  sed 's/^\([^ ]*\): .*$/\1/' "$work/out" | sort >"$work/actual"
  cmp -s "$work/expected" "$work/actual" || return 1
  while IFS= read -r line; do
    place=${line%% *}
    text=${line#"$place"}
    text=${text# }
    awk -v place="$place: " -v text="$text" \
      'index($0, place) == 1 && (text == "" || index($0, text)) { found = 1 } END { exit !found }' \
      "$work/out" || return 1
  done <"$1"
}

details() {
  printf '%s\n' "exit code: $code" "expected:" "$(cat "$1")" "reported:" "$(cat "$work/out" "$work/err")"
}

if [ ! -f "$base/Directory.Build.props" ]; then
  fail "the base tree exists"
fi
build "" ""
run "$work/tree"
if [ "$code" -eq 0 ] && [ ! -s "$work/out" ] && [ ! -s "$work/err" ]; then
  pass "the clean base tree passes with exit 0 and no output"
else
  fail "the clean base tree passes with exit 0 and no output" \
    "exit code: $code" "$(cat "$work/out" "$work/err")"
fi

cases=0
config_cases=0
excused_cases=0
for case_dir in "$fixtures"/*/; do
  case_dir=${case_dir%/}
  name=$(basename "$case_dir")
  cases=$((cases + 1))
  case $name in config-*) config_cases=$((config_cases + 1)) ;; esac

  if { [ ! -d "$case_dir/bad" ] && [ ! -s "$case_dir/remove" ]; } || [ ! -s "$case_dir/expected" ]; then
    fail "$name: has a bad/ tree or a remove list, and a non-empty expected list"
    continue
  fi

  build "$case_dir" bad
  run "$work/tree"
  if reported "$case_dir/expected"; then
    pass "$name: bad/ is reported at $(wc -l <"$case_dir/expected" | tr -d ' ') expected place(s), exit 1"
  else
    fail "$name: bad/ is reported at exactly the expected places, exit 1" "$(details "$case_dir/expected")"
  fi

  if [ -d "$case_dir/good" ]; then
    build "$case_dir" good
    run "$work/tree"
    if [ "$code" -eq 0 ] && [ ! -s "$work/out" ] && [ ! -s "$work/err" ]; then
      pass "$name: good/ passes with exit 0 and no output"
    else
      fail "$name: good/ passes with exit 0 and no output" \
        "exit code: $code" "$(cat "$work/out" "$work/err")"
    fi
  fi

  if [ -d "$case_dir/excused" ]; then
    excused_cases=$((excused_cases + 1))
    build "$case_dir" excused
    run "$work/tree"
    if reported "$case_dir/expected"; then
      pass "$name: excused/ (a rationale comment added) still fails at the same place(s)"
    else
      fail "$name: excused/ (a rationale comment added) still fails at the same place(s)" \
        "$(details "$case_dir/expected")"
    fi
    case $name in
      config-*) ;;
      *) fail "$name: only a config-* case may have an excused/ tree" ;;
    esac
  fi
done

# A glob that matched nothing, or fixture directories that went missing, must not read as success.
minimum_cases=121
minimum_config_cases=91
minimum_excused_cases=84
counted="found $cases fixture cases (at least $minimum_cases), $config_cases of them config-*"
counted="$counted (at least $minimum_config_cases), $excused_cases with excused/"
counted="$counted (at least $minimum_excused_cases)"
if [ "$cases" -ge "$minimum_cases" ] && [ "$config_cases" -ge "$minimum_config_cases" ] &&
  [ "$excused_cases" -ge "$minimum_excused_cases" ]; then
  pass "$counted"
else
  fail "$counted"
fi

# What the scan leaves out, in a throwaway repository: untracked files, and this test's own
# fixtures when the tree above them is scanned. Nothing else: a folder's name hides nothing.
repo="$work/repo"
mkdir -p "$repo"
cp -R "$base/." "$repo"
git -C "$repo" init --quiet
unexplained='#pragma warning disable CS0618'
mkdir -p "$repo/scripts/tests/check-suppressions/fixtures/any/bad"
echo "$unexplained" >"$repo/scripts/tests/check-suppressions/fixtures/any/bad/Skipped.cs"
git -C "$repo" add --force .
echo "$unexplained" >"$repo/Untracked.cs"
mkdir -p "$repo/bin" "$repo/src/obj"
echo "$unexplained" >"$repo/bin/Untracked.cs"
echo "$unexplained" >"$repo/src/obj/Untracked.cs"

run "$repo"
if [ "$code" -eq 0 ] && [ ! -s "$work/out" ]; then
  pass "the fixtures and untracked files are not scanned"
else
  fail "the fixtures and untracked files are not scanned" \
    "exit code: $code" "$(cat "$work/out" "$work/err")"
fi

# A tracked file is found wherever it is: build and dependency folder names are not skipped.
: >"$work/expected"
for directory in bin obj node_modules dist src/bin src/obj web/node_modules web/dist \
  src/binary scripts/tests/other; do
  mkdir -p "$repo/$directory"
  echo "$unexplained" >"$repo/$directory/Found.cs"
  echo "$directory/Found.cs:1" >>"$work/expected"
done
echo 'Untracked.cs:1' >>"$work/expected"
git -C "$repo" add --force Untracked.cs '*/Found.cs'
run "$repo"
sort -o "$work/expected" "$work/expected"
sed 's/^\([^:]*:[0-9]*\): .*$/\1/' "$work/out" | sort >"$work/actual"
if [ "$code" -eq 1 ] && cmp -s "$work/expected" "$work/actual"; then
  pass "a tracked file is scanned whatever its folder is called (bin, obj, node_modules, dist)"
else
  fail "a tracked file is scanned whatever its folder is called (bin, obj, node_modules, dist)" \
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
