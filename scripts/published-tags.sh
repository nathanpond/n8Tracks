#!/bin/sh
# Lists the tags an image on GHCR already has. It is the "published" fact that
# scripts/release-tags.sh is handed; it decides nothing and changes nothing.
#
#   scripts/published-tags.sh ghcr.io/OWNER/NAME
#
# Prints one tag per line on stdout, in no particular order, and nothing when the image has no
# tags or the package does not exist yet (the first release ever).
#
# The list comes from GitHub's packages API through `gh` (GH_TOKEN with `packages: read`, and
# `jq`): a registry itself offers no listing that `docker` can ask for.
#
# GitHub answers 404 both for a package that does not exist and for one the token may not see.
# Both are read as "nothing published". That is safe for a release: the tags of a package the
# token may not see cannot be created or moved by that token either, so the run fails at its
# first push instead of publishing on a wrong answer, and an exact version that exists is never
# overwritten whatever this list says (scripts/publish-tags.sh --once reads the registry itself).
# Any other failure (no network, a server error, a bad token) is exit code 1.
#
# Exit code 0 on success, 1 when the list could not be read (the reason is on stderr and nothing
# is printed on stdout), 2 for a usage error. scripts/tests/publish-tags/run.sh tests it against
# the stand-in `gh` beside that file.
set -eu
LC_ALL=C
export LC_ALL

say() {
  printf 'published-tags: %s\n' "$1" >&2
}

usage() {
  say "$1"
  printf 'usage: published-tags.sh ghcr.io/OWNER/NAME\n' >&2
  exit 2
}

[ $# -eq 1 ] || usage "exactly one image is expected"

printf '%s\n' "$1" | grep -Eq '^ghcr\.io/[A-Za-z0-9-]+/[a-z0-9][a-z0-9._/-]*$' ||
  usage "\"$1\" is not ghcr.io/OWNER/NAME"

path=${1#ghcr.io/}
owner=${path%%/*}
package=$(printf '%s' "${path#*/}" | sed 's|/|%2F|g')

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

if ! gh api "users/$owner" --jq .type >"$work/type" 2>"$work/err"; then
  sed 's/^/    /' "$work/err" >&2
  say "could not look up the owner \"$owner\"."
  exit 1
fi
case $(cat "$work/type") in
  Organization) base="orgs/$owner/packages/container/$package" ;;
  *) base="users/$owner/packages/container/$package" ;;
esac

# On a failure `gh` prints the error body on stdout, so stdout is used only after a success.
if gh api --paginate "$base/versions?per_page=100" \
  --jq '.[].metadata.container.tags[]' >"$work/tags" 2>"$work/err"; then
  cat "$work/tags"
elif grep -Fq '(HTTP 404)' "$work/err"; then
  say "$1 has no package yet (or none this token can see): nothing is published."
else
  sed 's/^/    /' "$work/err" >&2
  say "could not list the tags of $1."
  exit 1
fi
