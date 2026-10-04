#!/bin/sh
# Tags images that were already pushed by digest, all or nothing for the floating tags. It builds
# and pushes no image: the caller pushes each image untagged, then hands this script the digests.
#
#   scripts/publish-tags.sh --image REPOSITORY@DIGEST [--image REPOSITORY@DIGEST ...] \
#       --fixed "TAGS" --floating "TAGS"
#
#   --image     an image pushed by digest, for example ghcr.io/nathanpond/n8tracks@sha256:<64 hex>.
#               Give it once per image; every image gets every tag.
#   --fixed     tags that name this one build (edge-<short sha>), separated by spaces. They are
#               created first and are never rolled back: a failed run leaves them in place.
#   --floating  tags that move from build to build (edge), separated by spaces. May be empty (a
#               run that must not move them) but must be given.
#
# What it does, in this order:
#   1. Reads where every floating tag points now, for every image. A tag that does not exist yet
#      is noted as absent. If a tag cannot be read for any other reason, it stops here, before
#      anything has changed.
#   2. Creates the fixed tags on every image.
#   3. Moves the floating tags on every image, one at a time, and reads each back.
#   4. If any step from 2 on fails: every floating tag it touched is put back where step 1 found
#      it, or removed if step 1 found none, and it exits 1. So the floating tags of all images
#      end where they started.
#
# On success it prints one line per tag on stdout: "tagged REPOSITORY:TAG DIGEST". Everything else
# (progress, the reason for a failure, what was restored) goes to stderr.
#
# Tags are read and moved with `docker buildx imagetools`, so the caller must be logged in to the
# registry. Removing a tag is not something a registry offers; on GHCR (the only registry it is
# implemented for) the tag is first moved to a placeholder index, a copy of the image index with
# one extra annotation and so a digest of its own, and that placeholder is then deleted through
# GitHub's packages API with `gh` (which needs GH_TOKEN with `packages: write`, and `jq`). The
# image itself, with its fixed tags, is untouched. GitHub refuses to delete the last tagged
# version of a package, which is why the fixed tags are created first.
#
# Exit code 0 when every tag is in place, 1 when the run failed (the message says whether the
# floating tags were put back, and gives the command to do it by hand if they could not be), 2 for
# a usage error. scripts/tests/publish-tags/run.sh tests it against a stand-in registry.
set -eu
LC_ALL=C
export LC_ALL

say() {
  printf 'publish-tags: %s\n' "$1" >&2
}

usage() {
  say "$1"
  printf 'usage: publish-tags.sh --image REPOSITORY@DIGEST [--image ...] --fixed "TAGS" --floating "TAGS"\n' >&2
  exit 2
}

images=
fixed=
floating=
seen=

while [ $# -gt 0 ]; do
  case $1 in
    --image | --fixed | --floating)
      [ $# -ge 2 ] || usage "$1 needs a value"
      case $1 in
        --image) images="$images $2" ;;
        *)
          case " $seen " in
            *" $1 "*) usage "$1 was given twice" ;;
          esac
          seen="$seen $1"
          case $1 in
            --fixed) fixed=$2 ;;
            --floating) floating=$2 ;;
          esac
          ;;
      esac
      shift 2
      ;;
    *) usage "unknown argument: $1" ;;
  esac
done

for required in --fixed --floating; do
  case " $seen " in
    *" $required "*) ;;
    *) usage "$required is required" ;;
  esac
done

[ -n "$images" ] || usage "--image is required"

digest_pattern='sha256:[0-9a-f]{64}'

repositories=
for image in $images; do
  printf '%s\n' "$image" | grep -Eq "^[a-z0-9][a-z0-9._/:-]*@$digest_pattern\$" ||
    usage "--image \"$image\" is not REPOSITORY@sha256:<64 hexadecimal characters>"
  repository=${image%@*}
  case " $repositories " in
    *" $repository "*) usage "--image was given twice for $repository" ;;
  esac
  repositories="$repositories $repository"
done

all_tags=
for tag in $fixed $floating; do
  printf '%s\n' "$tag" | grep -Eq '^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$' ||
    usage "\"$tag\" is not a valid image tag"
  case " $all_tags " in
    *" $tag "*) usage "tag \"$tag\" was given twice" ;;
  esac
  all_tags="$all_tags $tag"
done

[ -n "$all_tags" ] || usage "no tag was given: --fixed and --floating are both empty"

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# --- The registry ---

# read_tag REPOSITORY TAG: sets $current to the digest the tag points at, or to "absent".
# Returns 1 when the registry could not be asked (the reason is then in $work/read.err).
read_tag() {
  current=
  if docker buildx imagetools inspect "$1:$2" --format '{{.Manifest.Digest}}' >"$work/read.out" 2>"$work/read.err"; then
    current=$(tr -d '[:space:]' <"$work/read.out")
    if printf '%s\n' "$current" | grep -Eq "^$digest_pattern\$"; then
      return 0
    fi
    printf 'unexpected answer: %s\n' "$current" >"$work/read.err"
    current=
    return 1
  fi
  # Only this exact answer means "no such tag". Anything else (no network, not logged in, a
  # registry error) must not be mistaken for it: an absent tag is one a failed run would remove.
  if grep -Eq '^ERROR: .*: not found$' "$work/read.err"; then
    current=absent
    return 0
  fi
  return 1
}

# point_tag REPOSITORY TAG DIGEST: points the tag at the digest and reads it back.
point_tag() {
  if ! docker buildx imagetools create -t "$1:$2" "$1@$3" >"$work/point.out" 2>&1; then
    sed 's/^/    /' "$work/point.out" >&2
    return 1
  fi
  if ! read_tag "$1" "$2"; then
    sed 's/^/    /' "$work/read.err" >&2
    return 1
  fi
  if [ "$current" != "$3" ]; then
    say "$1:$2 points at $current after being set to $3"
    return 1
  fi
}

# remove_tag REPOSITORY TAG: removes the tag and nothing else. GHCR only (see the top of the file).
remove_tag() {
  case $1 in
    ghcr.io/*/*) ;;
    *)
      say "removing a tag is only implemented for ghcr.io, not for $1"
      return 1
      ;;
  esac
  path=${1#ghcr.io/}
  owner=${path%%/*}
  package=$(printf '%s' "${path#*/}" | sed 's|/|%2F|g')

  read_tag "$1" "$2" || return 1
  [ "$current" != absent ] || return 0
  image_digest=$current

  # The placeholder: the same index plus one annotation, so it has a digest of its own and the
  # tag leaves the real image. The annotation's value makes each placeholder different.
  if ! docker buildx imagetools create \
    --annotation "index:dev.n8tracks.removed-tag=$2 $(date -u +%Y%m%dT%H%M%SZ) $$" \
    -t "$1:$2" "$1@$image_digest" >"$work/point.out" 2>&1; then
    sed 's/^/    /' "$work/point.out" >&2
    return 1
  fi
  read_tag "$1" "$2" || return 1
  placeholder=$current
  if [ "$placeholder" = absent ] || [ "$placeholder" = "$image_digest" ]; then
    say "the placeholder for $1:$2 did not get a digest of its own"
    return 1
  fi

  owner_type=$(gh api "users/$owner" --jq .type) || return 1
  case $owner_type in
    Organization) base="orgs/$owner/packages/container/$package" ;;
    *) base="users/$owner/packages/container/$package" ;;
  esac
  # $placeholder is a digest (checked by read_tag), so it is safe inside the jq filter.
  version_id=$(gh api --paginate "$base/versions?per_page=100" \
    --jq ".[] | select(.name == \"$placeholder\") | .id" | head -n 1) || return 1
  if [ -z "$version_id" ]; then
    say "no package version named $placeholder was found under $base"
    return 1
  fi
  gh api --method DELETE "$base/versions/$version_id" >/dev/null || return 1

  read_tag "$1" "$2" || return 1
  if [ "$current" != absent ]; then
    say "$1:$2 still points at $current after its placeholder was deleted"
    return 1
  fi
}

# --- Putting the floating tags back ---

# One line per floating tag this run has touched, newest first: "REPOSITORY TAG PREVIOUS".
: >"$work/touched"

# put_back: every touched floating tag goes back where it was. Returns 1 if any could not.
put_back() {
  result=0
  # Read on descriptor 3, so that nothing run inside the loop can swallow the list.
  while read -r repository tag previous <&3; do
    if ! read_tag "$repository" "$tag"; then
      sed 's/^/    /' "$work/read.err" >&2
      current=unknown
    fi
    if [ "$current" = "$previous" ]; then
      say "$repository:$tag is where it started ($previous)"
    elif [ "$previous" = absent ]; then
      if remove_tag "$repository" "$tag"; then
        say "removed $repository:$tag (it did not exist before this run)"
      else
        result=1
        say "COULD NOT remove $repository:$tag, which did not exist before this run."
        say "  By hand: delete the package version that carries only that tag, in the package's settings on GitHub."
      fi
    elif point_tag "$repository" "$tag" "$previous"; then
      say "restored $repository:$tag to $previous"
    else
      result=1
      say "COULD NOT restore $repository:$tag to $previous."
      say "  By hand: docker buildx imagetools create -t $repository:$tag $repository@$previous"
    fi
  done 3<"$work/touched"
  return "$result"
}

fail() {
  say "$1"
  if [ ! -s "$work/touched" ]; then
    say "no floating tag had been moved."
  elif put_back; then
    say "every floating tag is back where it started."
  else
    say "NOT every floating tag is back where it started: see above."
  fi
  exit 1
}

# --- 1. Where the floating tags point now ---

: >"$work/previous"
for tag in $floating; do
  for image in $images; do
    repository=${image%@*}
    if ! read_tag "$repository" "$tag"; then
      sed 's/^/    /' "$work/read.err" >&2
      say "could not read $repository:$tag. Nothing was changed."
      exit 1
    fi
    printf '%s %s %s\n' "$repository" "$tag" "$current" >>"$work/previous"
  done
done

# --- 2. The fixed tags ---

: >"$work/tagged"
for tag in $fixed; do
  for image in $images; do
    repository=${image%@*}
    digest=${image#*@}
    point_tag "$repository" "$tag" "$digest" || fail "could not create $repository:$tag."
    printf 'tagged %s:%s %s\n' "$repository" "$tag" "$digest" >>"$work/tagged"
    say "created $repository:$tag ($digest)"
  done
done

# --- 3. The floating tags ---

for tag in $floating; do
  for image in $images; do
    repository=${image%@*}
    digest=${image#*@}
    previous=$(awk -v r="$repository" -v t="$tag" '$1 == r && $2 == t { print $3 }' "$work/previous")
    # Noted before the attempt: a move that failed may still have happened.
    {
      printf '%s %s %s\n' "$repository" "$tag" "$previous"
      cat "$work/touched"
    } >"$work/touched.new"
    mv "$work/touched.new" "$work/touched"
    point_tag "$repository" "$tag" "$digest" || fail "could not move $repository:$tag."
    printf 'tagged %s:%s %s\n' "$repository" "$tag" "$digest" >>"$work/tagged"
    say "moved $repository:$tag from $previous to $digest"
  done
done

cat "$work/tagged"
