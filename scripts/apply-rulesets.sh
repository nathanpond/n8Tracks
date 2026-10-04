#!/bin/sh
# Applies the repository rulesets committed under .github/rulesets/ to GitHub, or checks that
# GitHub still matches them.
#
#   scripts/apply-rulesets.sh [--check] [--repo OWNER/NAME] [--dir DIR]
#
#   --check   change nothing: compare each committed definition with the live ruleset of the same
#             name and report any difference
#   --repo    the repository (default: the one `gh` resolves for the current directory)
#   --dir     where the definitions are (default: .github/rulesets beside this script's parent)
#
# Each *.json file is one ruleset, in the shape the REST API takes (name, target, enforcement,
# conditions, rules, bypass_actors) with no server fields, so it is sent as it is. A ruleset is
# matched by its name: one that exists is updated in place (PUT, keeping its id), one that does
# not is created (POST). After each write the answer is compared with the file, so a field GitHub
# dropped or changed is reported instead of passing silently. A ruleset on GitHub that has no file
# here is left alone: nothing is ever deleted.
#
# Needs `gh` signed in as someone who administers the repository, and `jq`.
# Exit code 0 when everything was applied (or, with --check, matches), 1 when something differs
# or a write failed, 2 for a usage error or an unreadable definition.
# scripts/tests/apply-rulesets/run.sh tests it against a stand-in for `gh`.
set -eu

usage() {
  echo "usage: scripts/apply-rulesets.sh [--check] [--repo OWNER/NAME] [--dir DIR]" >&2
  exit 2
}

check=false
repo=
dir=$(cd "$(dirname "$0")/.." && pwd)/.github/rulesets

while [ $# -gt 0 ]; do
  case "$1" in
    --check) check=true ;;
    --repo)
      [ $# -ge 2 ] || usage
      repo=$2
      shift
      ;;
    --dir)
      [ $# -ge 2 ] || usage
      dir=$2
      shift
      ;;
    *) usage ;;
  esac
  shift
done

for tool in gh jq; do
  if ! command -v "$tool" >/dev/null 2>&1; then
    echo "apply-rulesets: $tool is required" >&2
    exit 2
  fi
done

if [ ! -d "$dir" ]; then
  echo "apply-rulesets: no such directory: $dir" >&2
  exit 2
fi

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# The parts of a ruleset that a definition states, with keys and rules in a fixed order, so a
# committed file and an API answer can be compared.
normalize() {
  jq -S '{name, target, enforcement, conditions, bypass_actors: (.bypass_actors // []),
          rules: ((.rules // []) | sort_by(.type))}' "$1"
}

# Every definition is read before anything is sent: a broken file stops the run with no change made.
count=0
for file in "$dir"/*.json; do
  [ -f "$file" ] || continue
  count=$((count + 1))
  if ! jq -e 'type == "object"
              and (.name | type == "string" and length > 0)
              and (.target | type == "string")
              and (.enforcement | type == "string")
              and (.rules | type == "array")
              and (has("id") or has("node_id") or has("created_at") or has("updated_at")
                   or has("_links") or has("source") or has("source_type") | not)' \
    "$file" >/dev/null 2>&1; then
    echo "apply-rulesets: $file is not a ruleset definition (it needs name, target, enforcement, and rules, and no server fields such as id)" >&2
    exit 2
  fi
done
if [ "$count" -eq 0 ]; then
  echo "apply-rulesets: no *.json definitions in $dir" >&2
  exit 2
fi
names=$(cat "$dir"/*.json | jq -r '.name' | sort)
if [ -n "$(printf '%s\n' "$names" | uniq -d)" ]; then
  echo "apply-rulesets: two definitions in $dir have the same name" >&2
  exit 2
fi

if [ -z "$repo" ]; then
  repo=$(gh repo view --json nameWithOwner --jq .nameWithOwner)
fi

gh api "repos/$repo/rulesets?per_page=100&includes_parents=false" >"$work/list.json"

failed=0
for file in "$dir"/*.json; do
  name=$(jq -r '.name' "$file")
  ids=$(jq -r --arg name "$name" '.[] | select(.name == $name) | .id' "$work/list.json")
  if [ "$(printf '%s\n' "$ids" | grep -c .)" -gt 1 ]; then
    echo "FAIL $name: more than one ruleset on $repo has this name; remove the extra one by hand"
    failed=1
    continue
  fi
  normalize "$file" >"$work/wanted.json"

  if [ "$check" = true ]; then
    if [ -z "$ids" ]; then
      echo "FAIL $name: not on $repo"
      failed=1
      continue
    fi
    gh api "repos/$repo/rulesets/$ids" >"$work/live.json"
    normalize "$work/live.json" >"$work/got.json"
    if cmp -s "$work/wanted.json" "$work/got.json"; then
      echo "OK   $name: $repo matches $(basename "$file")"
    else
      echo "FAIL $name: $repo differs from $(basename "$file") (< committed, > live)"
      diff "$work/wanted.json" "$work/got.json" || true
      failed=1
    fi
    continue
  fi

  if [ -n "$ids" ]; then
    action="updated (id $ids)"
    if ! gh api --method PUT "repos/$repo/rulesets/$ids" --input "$file" >"$work/live.json"; then
      echo "FAIL $name: the update was refused"
      failed=1
      continue
    fi
  else
    action=created
    if ! gh api --method POST "repos/$repo/rulesets" --input "$file" >"$work/live.json"; then
      echo "FAIL $name: the create was refused"
      failed=1
      continue
    fi
  fi
  normalize "$work/live.json" >"$work/got.json"
  if cmp -s "$work/wanted.json" "$work/got.json"; then
    echo "OK   $name: $action"
  else
    echo "FAIL $name: $action, but GitHub's answer differs from $(basename "$file") (< committed, > live)"
    diff "$work/wanted.json" "$work/got.json" || true
    failed=1
  fi
done

exit "$failed"
