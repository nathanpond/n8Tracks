#!/bin/sh
# The release rules: decides what a pushed version tag publishes. It publishes nothing itself and
# looks nothing up; the caller hands it the four facts and acts on what it prints.
#
#   scripts/release-tags.sh --tag TAG --version VERSION --in-main true|false --published "TAGS"
#
#   --tag        the pushed git tag, for example v1.4.2 or v1.5.0-rc.1
#   --version    the contents of the VERSION file at the tagged commit
#   --in-main    whether the tagged commit is contained in main
#   --published  the tags the application image already has, separated by spaces, commas, or
#                newlines. May be empty (nothing published yet) but must be given. Only exact
#                versions (X.Y.Z or X.Y.Z-<prerelease>, with or without a leading v) are read;
#                anything else in the list (latest, edge, 1.4, ...) is ignored.
#
# On success it prints these lines, in this order, suitable for $GITHUB_OUTPUT:
#
#   version=X.Y.Z[-<prerelease>]   the tag without its v
#   push_exact=true|false          false when that exact version is already published: skip its push
#   image_tags=<tags>              the image tags to push, separated by spaces; may be empty
#   prerelease=true|false          whether the GitHub release is a pre-release
#   latest=true|false              whether the GitHub release is marked latest
#
# The rules:
#   - The tag is exactly vX.Y.Z or vX.Y.Z-<prerelease>. X, Y, and Z are numbers without leading
#     zeros. A pre-release is dot-separated identifiers of lower-case letters, digits, and hyphens,
#     none empty, and none a number with a leading zero. Build metadata (+...) is refused.
#   - The tag without its v equals VERSION, and the tagged commit is contained in main.
#   - vX.Y.Z publishes X.Y.Z; X.Y as well unless a higher X.Y.* stable version is already
#     published; and latest as well unless a higher stable version is already published.
#     Pre-releases already published never count as "higher".
#   - vX.Y.Z-<prerelease> publishes only X.Y.Z-<prerelease>, as a pre-release, never latest.
#   - When the exact version is already published (a re-run), it is left out of image_tags and
#     push_exact is false; the floating tags it is still entitled to are listed, so a re-run
#     completes them.
# One decision serves both images: the gateway image gets the same tags as the application image.
#
# Exit code 0 on success, 1 when the release is refused (the reason is on stderr and nothing is
# printed on stdout), 2 for a usage error. scripts/tests/release-tags/run.sh tests the rules.
set -eu
LC_ALL=C
export LC_ALL

refuse() {
  printf 'release-tags: %s\n' "$1" >&2
  exit 1
}

usage() {
  printf 'release-tags: %s\n' "$1" >&2
  printf 'usage: release-tags.sh --tag TAG --version VERSION --in-main true|false --published "TAGS"\n' >&2
  exit 2
}

tag=
version=
in_main=
published=
seen=

while [ $# -gt 0 ]; do
  case $1 in
    --tag | --version | --in-main | --published)
      [ $# -ge 2 ] || usage "$1 needs a value"
      case " $seen " in
        *" $1 "*) usage "$1 was given twice" ;;
      esac
      seen="$seen $1"
      case $1 in
        --tag) tag=$2 ;;
        --version) version=$2 ;;
        --in-main) in_main=$2 ;;
        --published) published=$2 ;;
      esac
      shift 2
      ;;
    *) usage "unknown argument: $1" ;;
  esac
done

for required in --tag --version --in-main --published; do
  case " $seen " in
    *" $required "*) ;;
    *) usage "$required is required" ;;
  esac
done

case $in_main in
  true | false) ;;
  *) usage "--in-main must be true or false, not \"$in_main\"" ;;
esac

# --- The tag is exactly vX.Y.Z or vX.Y.Z-<prerelease> ---

shape='it must be exactly vX.Y.Z or vX.Y.Z-<prerelease>'

[ -n "$tag" ] || refuse "the tag is empty: $shape"

case $tag in
  *+*) refuse "tag \"$tag\" carries build metadata (+...), which is not allowed: $shape" ;;
esac

case $tag in
  *[!0-9A-Za-z.-]*)
    refuse "tag \"$tag\" has a character other than letters, digits, dots, and hyphens: $shape"
    ;;
esac

case $tag in
  v*) ;;
  *) refuse "tag \"$tag\" does not start with a lower-case v: $shape" ;;
esac

release=${tag#v}
core=${release%%-*}
case $release in
  *-*)
    is_prerelease=true
    suffix=${release#*-}
    ;;
  *)
    is_prerelease=false
    suffix=
    ;;
esac

# A number without a leading zero, short enough for the shell to compare exactly.
number='(0|[1-9][0-9]{0,8})'
stable_pattern="^$number\\.$number\\.$number\$"

printf '%s\n' "$core" | grep -Eq "$stable_pattern" ||
  refuse "tag \"$tag\": \"$core\" is not three numbers X.Y.Z (no leading zeros, at most nine digits each): $shape"

if [ "$is_prerelease" = true ]; then
  [ -n "$suffix" ] || refuse "tag \"$tag\" has an empty pre-release after the hyphen: $shape"
  case $suffix in
    *[A-Z]*)
      refuse "tag \"$tag\": pre-release \"$suffix\" has an upper-case letter; identifiers are lower-case letters, digits, and hyphens"
      ;;
  esac
  case $suffix in
    .* | *. | *..*)
      refuse "tag \"$tag\": pre-release \"$suffix\" has an empty identifier (a leading, trailing, or doubled dot)"
      ;;
  esac
  rest=$suffix
  while [ -n "$rest" ]; do
    identifier=${rest%%.*}
    case $rest in
      *.*) rest=${rest#*.} ;;
      *) rest= ;;
    esac
    case $identifier in
      *[!0-9]*) ;;
      0?*)
        refuse "tag \"$tag\": pre-release identifier \"$identifier\" is a number with a leading zero"
        ;;
    esac
  done
fi

# --- The tag equals VERSION, and the commit is in main ---

# A VERSION file saved with Windows line endings still reads as its version.
version=$(printf '%s' "$version" | tr -d '\r')

[ "$release" = "$version" ] ||
  refuse "tag \"$tag\" is version \"$release\" but VERSION is \"$version\": the tag without its v must equal VERSION"

[ "$in_main" = true ] ||
  refuse "the commit tagged \"$tag\" is not contained in main: releases are cut only from commits in main"

# --- What is already published ---

# One tag per line, without a leading v.
published_list=$(printf '%s\n' "$published" | tr ', \t\r' '[\n*]' | sed -e 's/^v//' -e '/^$/d')

push_exact=true
if printf '%s\n' "$published_list" | grep -Fxq -- "$release"; then
  push_exact=false
fi

# higher A B: true when stable version A is higher than stable version B.
higher() {
  a=$1
  b=$2
  a_major=${a%%.*}
  a=${a#*.}
  a_minor=${a%%.*}
  a_patch=${a#*.}
  b_major=${b%%.*}
  b=${b#*.}
  b_minor=${b%%.*}
  b_patch=${b#*.}
  if [ "$a_major" -ne "$b_major" ]; then
    [ "$a_major" -gt "$b_major" ]
  elif [ "$a_minor" -ne "$b_minor" ]; then
    [ "$a_minor" -gt "$b_minor" ]
  else
    [ "$a_patch" -gt "$b_patch" ]
  fi
}

image_tags=
latest=false

if [ "$push_exact" = true ]; then
  image_tags=$release
fi

if [ "$is_prerelease" = false ]; then
  minor=${core%.*}
  minor_superseded=false
  latest_superseded=false
  for other in $(printf '%s\n' "$published_list" | grep -E "$stable_pattern" || true); do
    if higher "$other" "$core"; then
      latest_superseded=true
      if [ "${other%.*}" = "$minor" ]; then
        minor_superseded=true
      fi
    fi
  done
  if [ "$minor_superseded" = false ]; then
    image_tags="$image_tags $minor"
  fi
  if [ "$latest_superseded" = false ]; then
    image_tags="$image_tags latest"
    latest=true
  fi
  image_tags=${image_tags# }
fi

if [ "$push_exact" = false ]; then
  printf 'release-tags: %s is already published, skip push\n' "$release" >&2
fi

printf 'version=%s\n' "$release"
printf 'push_exact=%s\n' "$push_exact"
printf 'image_tags=%s\n' "$image_tags"
printf 'prerelease=%s\n' "$is_prerelease"
printf 'latest=%s\n' "$latest"
