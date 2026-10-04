#!/bin/sh
# Tags images that were already pushed by digest, all or nothing for the floating tags. It builds
# and pushes no image: the caller pushes each image untagged, then hands this script the digests.
#
#   scripts/publish-tags.sh --image REPOSITORY@DIGEST [--image REPOSITORY@DIGEST ...] \
#       [--once "TAGS"] --fixed "TAGS" --floating "TAGS"
#   scripts/publish-tags.sh --digest REPOSITORY:TAG [--digest REPOSITORY:TAG ...]
#
#   --image     an image pushed by digest, for example ghcr.io/nathanpond/n8tracks@sha256:<64 hex>.
#               Give it once per image; every image gets every tag.
#   --once      tags that are created once and never moved (a released version, 1.4.2), separated
#               by spaces. Optional. Where such a tag does not exist it is created. Where it
#               already points at the digest given for its image it is left alone, with no write
#               to the registry. Where it points at anything else the script refuses, before
#               anything has changed: a published version is never overwritten.
#   --fixed     tags that name this one build (edge-<short sha>), separated by spaces. They are
#               created first and are never rolled back: a failed run leaves them in place.
#   --floating  tags that move from build to build (edge, latest, 1.4), separated by spaces. May
#               be empty (a run that must not move them) but must be given.
#   --digest    read only: prints "REPOSITORY:TAG DIGEST" for each reference, or
#               "REPOSITORY:TAG absent" for a tag that does not exist, and changes nothing. It
#               cannot be combined with the other options. A caller asks this before building, so
#               that an image whose released version exists is not built again.
#
# What it does, in this order:
#   1. Reads where every once-only and floating tag points now, for every image. A tag that does
#      not exist yet is noted as absent. If a tag cannot be read for any other reason, or a
#      once-only tag points at another digest, it stops here, before anything has changed.
#   2. Creates the once-only tags that are absent, then the fixed tags, on every image.
#   3. Moves the floating tags on every image, one at a time, and reads each back. A floating tag
#      that already points at the digest is left alone.
#   4. If any step from 2 on fails: every floating tag it touched is put back where step 1 found
#      it, or removed if step 1 found none, and it exits 1. So the floating tags of all images
#      end where they started. Once-only and fixed tags are never rolled back.
#
# On success it prints one line per tag on stdout: "tagged REPOSITORY:TAG DIGEST" for a tag it
# created or moved, "kept REPOSITORY:TAG DIGEST" for one that was already there. Everything else
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
  printf 'usage: publish-tags.sh --image REPOSITORY@DIGEST [--image ...] [--once "TAGS"] --fixed "TAGS" --floating "TAGS"\n' >&2
  printf '       publish-tags.sh --digest REPOSITORY:TAG [--digest ...]\n' >&2
  exit 2
}

images=
once=
fixed=
floating=
references=
seen=

while [ $# -gt 0 ]; do
  case $1 in
    --image | --once | --fixed | --floating | --digest)
      [ $# -ge 2 ] || usage "$1 needs a value"
      case $1 in
        --image) images="$images $2" ;;
        --digest) references="$references $2" ;;
        *)
          case " $seen " in
            *" $1 "*) usage "$1 was given twice" ;;
          esac
          seen="$seen $1"
          case $1 in
            --once) once=$2 ;;
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

digest_pattern='sha256:[0-9a-f]{64}'
tag_pattern='[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}'

if [ -n "$references" ]; then
  [ -z "$images" ] && [ -z "$seen" ] || usage "--digest cannot be combined with the other options"
  for reference in $references; do
    printf '%s\n' "$reference" | grep -Eq "^[a-z0-9][a-z0-9._/-]*:$tag_pattern\$" ||
      usage "--digest \"$reference\" is not REPOSITORY:TAG"
  done
else
  for required in --fixed --floating; do
    case " $seen " in
      *" $required "*) ;;
      *) usage "$required is required" ;;
    esac
  done

  [ -n "$images" ] || usage "--image is required"
fi

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
for tag in $once $fixed $floating; do
  printf '%s\n' "$tag" | grep -Eq "^$tag_pattern\$" ||
    usage "\"$tag\" is not a valid image tag"
  case " $all_tags " in
    *" $tag "*) usage "tag \"$tag\" was given twice" ;;
  esac
  all_tags="$all_tags $tag"
done

[ -n "$all_tags" ] || [ -n "$references" ] ||
  usage "no tag was given: --once, --fixed, and --floating are all empty"

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

# --- Read only: where the given tags point ---

if [ -n "$references" ]; then
  : >"$work/digests"
  for reference in $references; do
    if ! read_tag "${reference%:*}" "${reference##*:}"; then
      sed 's/^/    /' "$work/read.err" >&2
      say "could not read $reference."
      exit 1
    fi
    printf '%s %s\n' "$reference" "$current" >>"$work/digests"
  done
  cat "$work/digests"
  exit 0
fi

# --- 1. Where the once-only and floating tags point now ---

# One line per once-only tag and image: "REPOSITORY TAG DIGEST create|keep".
: >"$work/once"
for tag in $once; do
  for image in $images; do
    repository=${image%@*}
    digest=${image#*@}
    if ! read_tag "$repository" "$tag"; then
      sed 's/^/    /' "$work/read.err" >&2
      say "could not read $repository:$tag. Nothing was changed."
      exit 1
    fi
    if [ "$current" = absent ]; then
      printf '%s %s %s create\n' "$repository" "$tag" "$digest" >>"$work/once"
    elif [ "$current" = "$digest" ]; then
      printf '%s %s %s keep\n' "$repository" "$tag" "$digest" >>"$work/once"
    else
      say "$repository:$tag is already published as $current and is never moved (this run was given $digest). Nothing was changed."
      exit 1
    fi
  done
done

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

# --- 2. The once-only tags that are absent, then the fixed tags ---

: >"$work/tagged"
while read -r repository tag digest action <&3; do
  if [ "$action" = keep ]; then
    printf 'kept %s:%s %s\n' "$repository" "$tag" "$digest" >>"$work/tagged"
    say "$repository:$tag is already published ($digest): left as it is"
  else
    point_tag "$repository" "$tag" "$digest" || fail "could not create $repository:$tag."
    printf 'tagged %s:%s %s\n' "$repository" "$tag" "$digest" >>"$work/tagged"
    say "created $repository:$tag ($digest)"
  fi
done 3<"$work/once"

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
    if [ "$previous" = "$digest" ]; then
      printf 'kept %s:%s %s\n' "$repository" "$tag" "$digest" >>"$work/tagged"
      say "$repository:$tag already points at $digest: left as it is"
      continue
    fi
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
