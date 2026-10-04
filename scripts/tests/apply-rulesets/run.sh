#!/bin/sh
# Tests scripts/apply-rulesets.sh against a stand-in for `gh`, and checks the definitions
# committed under .github/rulesets/. Nothing reaches GitHub and no token is needed.
#
#   scripts/tests/apply-rulesets/run.sh
#
# The stand-in (fake-gh/gh beside this file) serves the rulesets found in $FAKE_GH_STATE
# (one <id>.json per ruleset) and records every write in $FAKE_GH_STATE/calls.
#
# Prints one PASS or FAIL line per check and exits 1 if any failed.
set -u

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../../.." && pwd)
script="$root/scripts/apply-rulesets.sh"
committed="$root/.github/rulesets"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

PATH="$here/fake-gh:$PATH"
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

# check <name> <condition...>: passes when the condition command succeeds.
check() {
  name=$1
  shift
  if "$@" >/dev/null 2>&1; then
    pass "$name"
  else
    fail "$name" "exit code $code" "stdout: $(cat "$work/out")" "stderr: $(cat "$work/err")" \
      "calls: $(cat "$FAKE_GH_STATE/calls" 2>/dev/null)"
  fi
}

# fresh: an empty repository state and an empty definitions directory.
fresh() {
  rm -rf "$work/state" "$work/defs"
  mkdir -p "$work/state" "$work/defs"
  FAKE_GH_STATE="$work/state"
  export FAKE_GH_STATE
  unset FAKE_GH_DROP_CHECKS FAKE_GH_REFUSE || true
}

# definition <file> <name> <approvals>: a ruleset definition with a pull_request rule and the
# required `ci` check.
definition() {
  cat >"$1" <<EOF
{
  "name": "$2",
  "target": "branch",
  "enforcement": "active",
  "conditions": {"ref_name": {"exclude": [], "include": ["~DEFAULT_BRANCH"]}},
  "rules": [
    {"type": "pull_request", "parameters": {"required_approving_review_count": $3}},
    {"type": "required_status_checks", "parameters": {"strict_required_status_checks_policy": false,
      "required_status_checks": [{"context": "ci", "integration_id": 15368}]}}
  ],
  "bypass_actors": []
}
EOF
}

# live <id> <name> <approvals>: a ruleset as the server holds it (definition plus server fields).
live() {
  definition "$work/tmp.json" "$2" "$3"
  jq --argjson id "$1" '. + {id: $id, node_id: "RRS_x", source_type: "Repository", source: "o/r",
    created_at: "2026-01-01T00:00:00Z", updated_at: "2026-01-01T00:00:00Z", _links: {}}' \
    "$work/tmp.json" >"$work/state/$1.json"
}

run() {
  code=0
  "$script" --repo o/r --dir "$work/defs" "$@" >"$work/out" 2>"$work/err" || code=$?
}

calls() {
  cat "$work/state/calls" 2>/dev/null
}

# --- an existing ruleset is updated in place -------------------------------------------------
fresh
live 7 main-pr-required 0
jq 'del(.rules[1])' "$work/state/7.json" >"$work/tmp2.json" && mv "$work/tmp2.json" "$work/state/7.json"
definition "$work/defs/main-pr-required.json" main-pr-required 0
run
check "update: exit code 0" test "$code" -eq 0
check "update: one write, a PUT to the existing id" test "$(calls)" = "PUT repos/o/r/rulesets/7"
check "update: the ruleset now has the required check" \
  jq -e '.id == 7 and (.rules | map(.type) | index("required_status_checks") != null)' "$work/state/7.json"
check "update: reported as updated" grep -q "^OK   main-pr-required: updated (id 7)" "$work/out"

# --- a missing ruleset is created ------------------------------------------------------------
fresh
live 7 something-else 0
definition "$work/defs/main-pr-required.json" main-pr-required 0
run
check "create: exit code 0" test "$code" -eq 0
check "create: one write, a POST" test "$(calls)" = "POST repos/o/r/rulesets"
check "create: the other ruleset is untouched" jq -e '.name == "something-else"' "$work/state/7.json"
check "create: reported as created" grep -q "^OK   main-pr-required: created" "$work/out"

# --- two definitions, one of each ------------------------------------------------------------
fresh
live 7 main-pr-required 1
definition "$work/defs/a.json" main-pr-required 0
definition "$work/defs/b.json" tags 0
run
check "two definitions: exit code 0" test "$code" -eq 0
check "two definitions: a PUT then a POST" test "$(calls)" = "PUT repos/o/r/rulesets/7
POST repos/o/r/rulesets"

# --- --check ---------------------------------------------------------------------------------
fresh
live 7 main-pr-required 0
definition "$work/defs/main-pr-required.json" main-pr-required 0
run --check
check "check, matching: exit code 0" test "$code" -eq 0
check "check, matching: nothing written" test -z "$(calls)"
check "check, matching: reported" grep -q "^OK   main-pr-required" "$work/out"

fresh
live 7 main-pr-required 0
jq 'del(.rules[1])' "$work/state/7.json" >"$work/tmp2.json" && mv "$work/tmp2.json" "$work/state/7.json"
definition "$work/defs/main-pr-required.json" main-pr-required 0
run --check
check "check, required check removed on the server: exit code 1" test "$code" -eq 1
check "check, required check removed on the server: nothing written" test -z "$(calls)"
check "check, required check removed on the server: the difference is shown" \
  grep -q "required_status_checks" "$work/out"

fresh
live 7 main-pr-required 2
definition "$work/defs/main-pr-required.json" main-pr-required 0
run --check
check "check, approvals changed on the server: exit code 1" test "$code" -eq 1

fresh
jq -n '{name: "main-pr-required", target: "branch", enforcement: "active", rules: []}' \
  >"$work/defs/main-pr-required.json"
live 7 main-pr-required 0
jq '.bypass_actors = [{actor_id: 5, actor_type: "RepositoryRole", bypass_mode: "always"}] | .rules = [] | del(.conditions)' \
  "$work/state/7.json" >"$work/tmp2.json" && mv "$work/tmp2.json" "$work/state/7.json"
run --check
check "check, a bypass actor added on the server: exit code 1" test "$code" -eq 1

fresh
definition "$work/defs/main-pr-required.json" main-pr-required 0
run --check
check "check, ruleset missing: exit code 1" test "$code" -eq 1
check "check, ruleset missing: nothing written" test -z "$(calls)"

# --- the server's answer is verified ---------------------------------------------------------
fresh
live 7 main-pr-required 0
definition "$work/defs/main-pr-required.json" main-pr-required 0
FAKE_GH_DROP_CHECKS=1
export FAKE_GH_DROP_CHECKS
run
check "server drops the required check: exit code 1" test "$code" -eq 1
check "server drops the required check: reported" grep -q "^FAIL main-pr-required" "$work/out"

fresh
live 7 main-pr-required 0
definition "$work/defs/main-pr-required.json" main-pr-required 0
FAKE_GH_REFUSE=1
export FAKE_GH_REFUSE
run
check "write refused: exit code 1" test "$code" -eq 1
check "write refused: reported" grep -q "^FAIL main-pr-required: the update was refused" "$work/out"

# --- refusals before anything is sent --------------------------------------------------------
fresh
live 7 main-pr-required 0
live 8 main-pr-required 0
definition "$work/defs/main-pr-required.json" main-pr-required 0
run
check "two live rulesets with one name: exit code 1" test "$code" -eq 1
check "two live rulesets with one name: nothing written" test -z "$(calls)"

fresh
definition "$work/defs/a.json" good 0
echo '{ not json' >"$work/defs/b.json"
run
check "broken definition: exit code 2" test "$code" -eq 2
check "broken definition: nothing written" test -z "$(calls)"

fresh
definition "$work/tmp.json" main-pr-required 0
jq '. + {id: 7}' "$work/tmp.json" >"$work/defs/main-pr-required.json"
run
check "definition with a server field: exit code 2" test "$code" -eq 2

fresh
definition "$work/defs/a.json" same 0
definition "$work/defs/b.json" same 0
run
check "two definitions with one name: exit code 2" test "$code" -eq 2
check "two definitions with one name: nothing written" test -z "$(calls)"

fresh
run
check "no definitions: exit code 2" test "$code" -eq 2

fresh
definition "$work/defs/a.json" good 0
code=0
"$script" --repo o/r --dir "$work/defs" --delete >"$work/out" 2>"$work/err" || code=$?
check "unknown option: exit code 2" test "$code" -eq 2
check "unknown option: nothing written" test -z "$(calls)"

# --- the committed definitions ---------------------------------------------------------------
fresh
main="$committed/main-pr-required.json"
check "committed: main-pr-required.json exists" test -f "$main"
check "committed: it targets the default branch and is active" \
  jq -e '.name == "main-pr-required" and .target == "branch" and .enforcement == "active"
         and .conditions.ref_name.include == ["~DEFAULT_BRANCH"]' "$main"
check "committed: a pull request is required, with 0 approvals" \
  jq -e '[.rules[] | select(.type == "pull_request")] | length == 1
         and .[0].parameters.required_approving_review_count == 0' "$main"
check "committed: the ci check from GitHub Actions is required" \
  jq -e '[.rules[] | select(.type == "required_status_checks")] | length == 1
         and .[0].parameters.required_status_checks == [{"context": "ci", "integration_id": 15368}]' "$main"
check "committed: branches need not be up to date" \
  jq -e '.rules[] | select(.type == "required_status_checks")
         | .parameters.strict_required_status_checks_policy == false' "$main"
check "committed: nobody can bypass" jq -e '.bypass_actors == []' "$main"
check "committed: the workflow has a job whose check is named ci" \
  grep -Eq '^    name: ci$' "$root/.github/workflows/ci.yml"
code=0
"$script" --repo o/r >"$work/out" 2>"$work/err" || code=$?
check "committed: every definition is accepted and applied to an empty repository" test "$code" -eq 0

echo
echo "$passed passed, $failed failed"
# Fewer checks than this means part of the test did not run, which must not look like a pass.
minimum_checks=42
if [ "$passed" -lt "$minimum_checks" ]; then
  echo "FAIL only $passed checks passed; at least $minimum_checks are expected"
  exit 1
fi
[ "$failed" -eq 0 ]
