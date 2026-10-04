#!/bin/sh
# The edge naming rules: what a build of one commit of main is called. It builds and publishes
# nothing; the caller hands it the two facts and acts on what it prints.
#
#   scripts/edge-version.sh --version VERSION --sha SHA
#
#   --version  the contents of the VERSION file at the commit
#   --sha      the full commit ID (40 or 64 lower-case hexadecimal characters)
#
# On success it prints these lines, in this order, suitable for $GITHUB_OUTPUT:
#
#   short_sha=<the first 7 characters of the commit ID>
#   version=<VERSION>-edge.<short sha>     the version the images and the extension report
#   tag=edge-<short sha>                   the image tag that names this one build
#
# The rules:
#   - The short sha is exactly the first 7 characters of the commit ID, whatever they are.
#   - VERSION X.Y.Z gives X.Y.Z-edge.<short sha>.
#   - VERSION X.Y.Z-<prerelease> gives X.Y.Z-<prerelease>.edge.<short sha>: the build stays a
#     pre-release of the same version, and there is still only one hyphen-led suffix.
#   - The floating tag, edge, is not decided here: whether a run may move it depends on whether
#     its commit is still the head of main, which the workflow asks when it is ready to tag.
#
# Exit code 0 on success, 1 when VERSION or the commit ID is not what the rules expect (the reason
# is on stderr and nothing is printed on stdout), 2 for a usage error.
# scripts/tests/edge-version/run.sh tests the rules.
set -eu
LC_ALL=C
export LC_ALL

refuse() {
  printf 'edge-version: %s\n' "$1" >&2
  exit 1
}

usage() {
  printf 'edge-version: %s\n' "$1" >&2
  printf 'usage: edge-version.sh --version VERSION --sha SHA\n' >&2
  exit 2
}

version=
sha=
seen=

while [ $# -gt 0 ]; do
  case $1 in
    --version | --sha)
      [ $# -ge 2 ] || usage "$1 needs a value"
      case " $seen " in
        *" $1 "*) usage "$1 was given twice" ;;
      esac
      seen="$seen $1"
      case $1 in
        --version) version=$2 ;;
        --sha) sha=$2 ;;
      esac
      shift 2
      ;;
    *) usage "unknown argument: $1" ;;
  esac
done

for required in --version --sha; do
  case " $seen " in
    *" $required "*) ;;
    *) usage "$required is required" ;;
  esac
done

# A VERSION file read with its line ending (of either kind) still reads as its version.
version=$(printf '%s' "$version" | tr -d '\r\n')

[ -n "$version" ] || refuse "VERSION is empty"

number='(0|[1-9][0-9]*)'
printf '%s\n' "$version" | grep -Eq "^$number\\.$number\\.$number(-[0-9a-z-]+(\\.[0-9a-z-]+)*)?\$" ||
  refuse "VERSION \"$version\" is not X.Y.Z or X.Y.Z-<prerelease> (lower-case letters, digits, dots, and hyphens)"

printf '%s\n' "$sha" | grep -Eq '^([0-9a-f]{40}|[0-9a-f]{64})$' ||
  refuse "\"$sha\" is not a full commit ID (40 or 64 lower-case hexadecimal characters)"

short_sha=$(printf '%s' "$sha" | cut -c1-7)

case $version in
  *-*) edge_version="$version.edge.$short_sha" ;;
  *) edge_version="$version-edge.$short_sha" ;;
esac

printf 'short_sha=%s\n' "$short_sha"
printf 'version=%s\n' "$edge_version"
printf 'tag=edge-%s\n' "$short_sha"
