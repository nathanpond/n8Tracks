#!/bin/sh
# Invariant 8 guard: fails when a warning suppression has no one-line rationale, and when
# warnings-as-errors, analyzers, zero-warning linting, or strict type checking is switched off or
# weakened (which no rationale excuses).
#
#   scripts/check-suppressions.sh [ROOT]
#
# Scans the git-tracked files under ROOT (default: this repository) and prints `file:line: what`
# (or `file: what`) for each finding. Exit code 0 and no output when there is none, 1 when there
# is one, 2 for a usage error. The rules are described at the top of
# scripts/check-suppressions.py; scripts/tests/check-suppressions/run.sh tests them.
set -eu

if ! command -v python3 >/dev/null 2>&1; then
  echo "check-suppressions: python3 is required" >&2
  exit 2
fi

exec python3 "$(dirname "$0")/check-suppressions.py" "$@"
