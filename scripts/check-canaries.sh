#!/usr/bin/env bash
# Invariant 8 guard, the effect half: warnings really are errors.
#
#   scripts/check-canaries.sh [ROOT]
#
# scripts/check-suppressions.sh reads configuration; this check does not. It puts known-bad
# source files (canaries, kept under scripts/canaries/) into every project and requires the
# project's own build, lint, and type check to fail on them. Whatever silenced a warning, by any
# route, in the repository or in the environment this runs in, the canary stops failing and this
# check fails.
#
# Everything happens in a temporary copy of ROOT (default: the repository this script lives in):
# the tracked files and the untracked files git does not ignore, as they are in the working
# tree. The copy is removed when the script ends, however it ends. Nothing under ROOT is
# created, changed, or removed, and the canaries never reach a product build.
#
# .NET: every *.csproj in the copy (outside scripts/tests/) is found, not listed, so a new
#   project is covered without a change here. For each, one at a time,
#   canaries/dotnet/N8TracksWarningCanary.cs is placed in the project's folder and
#   `dotnet build <project> --configuration Release` runs with nothing else on the command line
#   that concerns warnings. The check passes only when the build fails and the errors it reports
#   are exactly the canary's marked lines, each with its marked id: a nullable warning (CS8618)
#   and an analyzer warning (CA2200). A build that succeeds, reports them as warnings, reports
#   only one, or fails on anything else fails the check. A Visual Basic or F# project, or a tree
#   with no project at all, fails: it cannot be canaried.
#
# JavaScript: web/, extension/, and e2e/ each get `npm ci` in the copy, then one run per folder
#   under canaries/<project>/, each with only that folder's files laid over the project:
#     lint-warning/  (web, e2e) breaks only a rule set to "warn": `npm run lint` must fail
#                    anyway, which is what --max-warnings 0 is for. extension/ has no rule at
#                    "warn", so there is nothing to place.
#     lint-error/    `npm run lint` must fail and report every marked rule on its line.
#     typecheck/     `npm run typecheck` must fail and report every marked error code on its
#                    line: one line per strictness setting the projects turn on.
#   A run that exits 0, or fails without one of the marked ids, fails the check.
#
# A marker is a trailing comment on the line the tool reports:
#   // canary: CS8618                     a C# diagnostic
#   // canary: error <rule>               an ESLint rule at "error"
#   // canary: warning <rule>             an ESLint rule at "warn"
#   // canary: TS2322                     a TypeScript error
#
# What this proves, and what it does not: the marked diagnostics and rules are live, as errors,
# for a new file in the folder the canary is placed in, built the way this script builds. It
# says nothing about a rule that has no canary, a suppression scoped to other files, or a
# command line in a workflow or Dockerfile that differs from the one here. Those are the
# scanner's part (scripts/check-suppressions.py) or nobody's; the list is in its header.
#
# Prints one PASS or FAIL line per check, runs them all, and exits 1 if any failed (2 for a
# usage error or a missing tool). Needs git, tar, the .NET SDK pinned in global.json, Node, and
# npm, and the network only as far as `dotnet restore` and `npm ci` do.
set -u

here=$(cd "$(dirname "$0")" && pwd)
canaries="$here/canaries"
script_projects="web extension e2e"
dotnet_canary="N8TracksWarningCanary.cs"

if [ "$#" -gt 1 ] || [ "${1:-}" = "-h" ] || [ "${1:-}" = "--help" ]; then
  echo "usage: check-canaries.sh [ROOT]" >&2
  exit 2
fi
root=${1:-$here/..}
if [ ! -d "$root" ]; then
  echo "check-canaries: $root is not a directory" >&2
  exit 2
fi
root=$(cd "$root" && pwd)
for tool in git tar dotnet node npm; do
  if ! command -v "$tool" >/dev/null 2>&1; then
    echo "check-canaries: $tool is not installed" >&2
    exit 2
  fi
done
if ! git -C "$root" rev-parse --show-toplevel >/dev/null 2>&1; then
  echo "check-canaries: $root is not inside a git repository" >&2
  exit 2
fi

work=$(mktemp -d "${TMPDIR:-/tmp}/check-canaries.XXXXXX") || exit 2
work=$(cd "$work" && pwd -P)
cleanup() {
  rm -rf "$work"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM HUP

copy="$work/tree"
log="$work/log"
passed=0
failed=0

pass() {
  passed=$((passed + 1))
  echo "PASS $1"
}

# fail <name> <reason> [<file to show>]
fail() {
  failed=$((failed + 1))
  echo "FAIL $1"
  echo "     $2"
  if [ -n "${3:-}" ] && [ -s "$3" ]; then
    sed 's/^/     | /' "$3"
  fi
}

# The copy: what git tracks or would track, as it is in the working tree.
mkdir -p "$copy"
git -C "$root" ls-files -z --cached --others --exclude-standard >"$work/listed"
(
  cd "$root" || exit 1
  while IFS= read -r -d '' path; do
    if [ -e "$path" ] || [ -L "$path" ]; then
      printf '%s\0' "$path"
    fi
  done <"$work/listed" >"$work/present"
  tar -cf "$work/tree.tar" --null -T "$work/present"
) && tar -xf "$work/tree.tar" -C "$copy"
copied=$?
rm -f "$work/tree.tar"
if [ "$copied" -ne 0 ]; then
  echo "check-canaries: could not copy $root" >&2
  exit 2
fi
tr '\0' '\n' <"$work/present" >"$work/files"

# markers <canary file>: one "<line> <kind> <id>" per marked line; kind is error or warning.
markers() {
  awk '
    match($0, /\/\/ canary: .*$/) {
      count = split(substr($0, RSTART + 11), part, " ")
      if (count == 1) print NR, "error", part[1]
      else print NR, part[1], part[2]
    }' "$1"
}

# ---------------------------------------------------------------------------------------------
# .NET
# ---------------------------------------------------------------------------------------------

# dotnet_canary_check <project>: builds the project with the canary beside it and compares the
# errors with the canary's markers. Leaves the build output in $log and the verdict in $work/why.
dotnet_canary_check() {
  project=$1
  directory=$(dirname "$project")
  cp "$canaries/dotnet/$dotnet_canary" "$copy/$directory/$dotnet_canary"
  dotnet build "$project" --configuration Release -nologo -tl:off -clp:NoSummary >"$log" 2>&1
  code=$?
  rm -f "$copy/$directory/$dotnet_canary"

  markers "$canaries/dotnet/$dotnet_canary" | awk '{ print $1, $2, $3 }' | sort -u >"$work/expected"
  # Every diagnostic the build printed, as "<line> <severity> <id>" when it is in the canary and
  # "other <severity> <id> <where>" when it is not.
  awk -v canary="/$directory/$dotnet_canary" '
    match($0, /(error|warning) [A-Za-z]+[0-9]+:/) {
      split(substr($0, RSTART, RLENGTH - 1), found, " ")
      where = substr($0, 1, RSTART - 1)
      sub(/[: ]+$/, "", where)
      sub(/^[ \t]+/, "", where)
      at = index(where, canary "(")
      if (at > 0 && at + length(canary) == index(where, "(")) {
        line = substr(where, index(where, "(") + 1)
        sub(/,.*$/, "", line)
        print line, found[1], found[2]
      } else {
        print "other", found[1], found[2], where
      }
    }' "$log" | sort -u >"$work/diagnostics"
  grep -E '^[0-9]+ error ' "$work/diagnostics" >"$work/canary-errors"
  grep -E '^other error ' "$work/diagnostics" >"$work/other-errors"

  {
    echo "expected, in $dotnet_canary (line, severity, id):"
    sed 's/^/  /' "$work/expected"
    echo "the build exited $code and reported:"
    if [ -s "$work/diagnostics" ]; then
      sed 's/^/  /' "$work/diagnostics"
    else
      echo "  no diagnostics"
      tail -n 15 "$log" | sed 's/^/  /'
    fi
  } >"$work/why"

  if [ "$code" -eq 0 ]; then
    reason="the build succeeded with the canary in it: its warnings are not errors here, or it was not compiled"
    return 1
  fi
  if [ -s "$work/other-errors" ]; then
    reason="the build failed for another reason than the canary"
    return 1
  fi
  if ! cmp -s "$work/expected" "$work/canary-errors"; then
    reason="the build failed, but not with exactly the canary's diagnostics as errors"
    return 1
  fi
  return 0
}

grep -E '\.(vb|fs)proj$' "$work/files" | grep -v '^scripts/tests/' >"$work/unsupported"
while IFS= read -r project; do
  fail "dotnet $project" "a Visual Basic or F# project cannot be canaried: the canary is C#"
done <"$work/unsupported"

grep -E '\.csproj$' "$work/files" | grep -v '^scripts/tests/' | sort >"$work/projects"
if [ ! -s "$work/projects" ]; then
  fail "dotnet" "no *.csproj was found under $root: there is nothing to prove warnings are errors for"
fi
cd "$copy" || exit 2
while IFS= read -r project; do
  if dotnet_canary_check "$project"; then
    pass "dotnet $project: the canary's $(wc -l <"$work/expected" | tr -d ' ') warnings fail the build as errors"
  else
    fail "dotnet $project" "$reason" "$work/why"
  fi
done <"$work/projects"

# ---------------------------------------------------------------------------------------------
# JavaScript
# ---------------------------------------------------------------------------------------------

# script_canary_check <project> <phase> <script>: lays canaries/<project>/<phase>/ over the
# project, runs `npm run <script>`, takes the files away again, and looks for every marker.
script_canary_check() {
  project=$1
  phase=$2
  script=$3
  overlay="$canaries/$project/$phase"
  (cd "$overlay" && find . -type f | sed 's|^\./||' | sort) >"$work/overlay"
  while IFS= read -r relative; do
    if [ -e "$copy/$project/$relative" ]; then
      reason="$project/$relative already exists: a canary may not replace a file of the project"
      : >"$work/why"
      return 1
    fi
    mkdir -p "$(dirname "$copy/$project/$relative")"
    cp "$overlay/$relative" "$copy/$project/$relative"
  done <"$work/overlay"

  npm run "$script" >"$log" 2>&1
  code=$?

  : >"$work/missing"
  : >"$work/expected"
  while IFS= read -r relative; do
    rm -f "$copy/$project/$relative"
    markers "$overlay/$relative" >"$work/markers"
    while read -r line kind id; do
      echo "$relative:$line $kind $id" >>"$work/expected"
      if [ "$script" = "typecheck" ]; then
        # src/file.ts(12,3): error TS2322: ...
        grep -F "$relative($line," "$log" | grep -qF ": error $id:" && continue
      else
        #   12:3  error  message  rule-id     (under a line that is the file's full path)
        awk -v file="$copy/$project/$relative" -v line="$line" -v kind="$kind" -v id="$id" '
          $0 == file { inside = 1; next }
          /^[^ \t]/ || $0 == "" { inside = 0 }
          inside && $2 == kind && $NF == id && index($1, line ":") == 1 { found = 1 }
          END { exit !found }' "$log" && continue
      fi
      echo "$relative:$line $kind $id" >>"$work/missing"
    done <"$work/markers"
  done <"$work/overlay"

  {
    echo "expected (file:line, severity, id):"
    sed 's/^/  /' "$work/expected"
    echo "'npm run $script' exited $code and printed:"
    grep -v '^$' "$log" | tail -n 40 | sed 's/^/  /'
  } >"$work/why"

  if [ ! -s "$work/expected" ]; then
    reason="canaries/$project/$phase holds no marked line"
    return 1
  fi
  if [ "$code" -eq 0 ]; then
    reason="'npm run $script' passed with the canary in place"
    return 1
  fi
  if [ -s "$work/missing" ]; then
    reason="'npm run $script' failed, but did not report: $(tr '\n' ';' <"$work/missing")"
    return 1
  fi
  return 0
}

for project in $script_projects; do
  if [ ! -f "$copy/$project/package.json" ]; then
    fail "$project" "$project/package.json is missing: there is nothing to lint or type-check"
    continue
  fi
  for phase in lint-error typecheck; do
    if [ ! -d "$canaries/$project/$phase" ]; then
      fail "$project $phase" "scripts/canaries/$project/$phase is missing"
    fi
  done
  cd "$copy/$project" || exit 2
  npm ci --no-audit --no-fund >"$log" 2>&1
  code=$?
  if [ "$code" -ne 0 ]; then
    tail -n 20 "$log" >"$work/why"
    fail "$project" "'npm ci' failed (exit $code), so nothing could be checked" "$work/why"
    continue
  fi
  for phase in lint-warning lint-error typecheck; do
    [ -d "$canaries/$project/$phase" ] || continue
    script=lint
    [ "$phase" = "typecheck" ] && script=typecheck
    if script_canary_check "$project" "$phase" "$script"; then
      pass "$project $phase: 'npm run $script' fails on the canary's $(wc -l <"$work/expected" | tr -d ' ') marked line(s)"
    else
      fail "$project $phase" "$reason" "$work/why"
    fi
  done
done

echo
echo "$passed passed, $failed failed"
if [ "$failed" -gt 0 ]; then
  echo "check-canaries: a known-bad file did not fail the way it must. Something has switched a warning, an analyzer, a lint rule, or a strictness setting off, or the check could not run; see scripts/check-canaries.sh." >&2
fi
[ "$failed" -eq 0 ] && [ "$passed" -gt 0 ]
