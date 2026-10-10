#!/usr/bin/env bash
# Compares the syft image pinned in .github/scripts/generate-sbom.sh with the newest syft release (highest
# vX.Y.Z tag of https://github.com/anchore/syft, read with git ls-remote without credentials) and with the
# current index digest of the pinned tag on ghcr.io. Run from the repository root; takes no argument; needs
# git, docker with buildx, and jq.
# Prints a Markdown table to standard output and ::warning::/::error:: lines to standard error.
# Exit status: 0 the pin is the newest release and its digest is current, 3 a newer release exists or the
# digest has moved, 4 the tag lookup or the pinned tag's digest lookup failed, 1 any other error
# (precedence 1 > 4 > 3 > 0). Used by base-image-digests.yml (record 0037).
set -euo pipefail
export LC_ALL=C

generate_script=.github/scripts/generate-sbom.sh
syft_repo=ghcr.io/anchore/syft
syft_url=https://github.com/anchore/syft.git

# Every value is checked as a whole string with [[ =~ ]] before it reaches a command or the table.
assign_re="^syft_image='([^']*)'\$"
syft_re='^ghcr\.io/anchore/syft:v[0-9]+\.[0-9]+\.[0-9]+@sha256:[0-9a-f]{64}$'
tag_re='^v(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})$'
ref_re='^refs/tags/(v(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8}))$'
digest_re='^sha256:[0-9a-f]{64}$'

fail() {
  echo "::error::$1" >&2
  exit 1
}

if [[ $# -ne 0 ]]; then
  fail "usage: check-sbom-generator-pin.sh (no arguments)"
fi

# Reads the pin before any network access: exactly one line starting with syft_image=, in the literal form
# syft_image='<value>'. Every other line is ignored.
if ! [[ -f $generate_script && ! -L $generate_script ]]; then
  fail "$generate_script is missing or not a regular file"
fi
assignments=()
while IFS= read -r line || [[ -n $line ]]; do
  if [[ $line == syft_image=* ]]; then
    assignments+=("$line")
  fi
done < "$generate_script"
if [[ ${#assignments[@]} -ne 1 ]]; then
  fail "$generate_script must have exactly one line starting with syft_image="
fi
if ! [[ ${assignments[0]} =~ $assign_re ]]; then
  fail "the syft_image line is not of the form syft_image='<reference>'"
fi
pin="${BASH_REMATCH[1]}"
if ! [[ $pin =~ $syft_re ]]; then
  fail "the syft pin is not ghcr.io/anchore/syft:vX.Y.Z@sha256:<digest>"
fi
pinned_ref="${pin%@*}"
pinned_digest="${pin#*@}"
pinned_tag="${pinned_ref#*:}"
if ! [[ $pinned_tag =~ $tag_re ]]; then
  fail "the syft pin's tag is not a strict vMAJOR.MINOR.PATCH tag"
fi
pinned_major="${BASH_REMATCH[1]}"
pinned_minor="${BASH_REMATCH[2]}"
pinned_patch="${BASH_REMATCH[3]}"

for tool in git docker jq; do
  if ! command -v "$tool" > /dev/null; then
    fail "$tool is required"
  fi
done

# Prints the index digest of a syft tag; fails when the lookup fails or the answer is not one digest.
index_digest() {
  local tag=$1 manifest digest
  manifest="$(docker buildx imagetools inspect --format '{{json .Manifest}}' "$syft_repo:$tag")" || return 1
  digest="$(jq -r .digest <<<"$manifest")" || return 1
  [[ $digest =~ $digest_re ]] || return 1
  printf '%s' "$digest"
}

# Lists the tags of the public syft repository without any credential: no prompt, no system or global Git
# configuration, no credential helper, and no home directory (so no ~/.netrc), outside any repository (so no
# checkout's http.extraheader).
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
list_tags() {
  (
    cd "$scratch"
    HOME="$scratch" XDG_CONFIG_HOME="$scratch" GIT_TERMINAL_PROMPT=0 GIT_CONFIG_NOSYSTEM=1 \
      GIT_CONFIG_GLOBAL=/dev/null GIT_CEILING_DIRECTORIES="$(dirname "$scratch")" \
      git -c credential.helper= ls-remote --tags --refs "$syft_url"
  )
}

lookup_failed=0
stale=0
rows=()

newest_tag=
if tags_output="$(list_tags)"; then
  newest_major=-1
  newest_minor=-1
  newest_patch=-1
  while IFS= read -r line; do
    if [[ $line != *$'\t'* ]]; then
      continue
    fi
    ref="${line#*$'\t'}"
    if ! [[ $ref =~ $ref_re ]]; then
      continue
    fi
    candidate="${BASH_REMATCH[1]}"
    major="${BASH_REMATCH[2]}"
    minor="${BASH_REMATCH[3]}"
    patch="${BASH_REMATCH[4]}"
    if ((major > newest_major || (major == newest_major && (minor > newest_minor || (minor == newest_minor && patch > newest_patch))))); then
      newest_tag="$candidate"
      newest_major=$major
      newest_minor=$minor
      newest_patch=$patch
    fi
  done <<<"$tags_output"
  if [[ -z $newest_tag ]]; then
    echo "::warning::lookup of the syft release tags failed: no vX.Y.Z tag found" >&2
    lookup_failed=1
  fi
else
  echo "::warning::lookup of the syft release tags failed" >&2
  lookup_failed=1
fi

if current="$(index_digest "$pinned_tag")"; then
  state=current
  if [[ $current != "$pinned_digest" ]]; then
    stale=1
    state=stale
    echo "::warning::the syft pin's digest is stale: $syft_repo:$pinned_tag is now $current, pinned $pinned_digest" >&2
  fi
  rows+=("| $syft_repo | $pinned_tag | pinned | $pinned_digest | $current | $state |")
else
  echo "::warning::lookup of $syft_repo:$pinned_tag failed" >&2
  lookup_failed=1
fi

if [[ -n $newest_tag ]] && ((newest_major > pinned_major || (newest_major == pinned_major && (newest_minor > pinned_minor || (newest_minor == pinned_minor && newest_patch > pinned_patch))))); then
  stale=1
  echo "::warning::a newer syft release exists: $newest_tag, pinned $pinned_tag" >&2
  if newest_digest="$(index_digest "$newest_tag")"; then
    :
  else
    echo "::warning::digest lookup failed for $syft_repo:$newest_tag" >&2
    newest_digest=-
  fi
  rows+=("| $syft_repo | $newest_tag | newest release | - | $newest_digest | newer |")
fi

echo '| Image | Tag | Role | Pinned digest | Current digest | State |'
echo '| ----- | --- | ---- | ------------- | -------------- | ----- |'
for row in "${rows[@]}"; do
  echo "$row"
done

if [[ $lookup_failed -ne 0 ]]; then
  exit 4
fi
if [[ $stale -ne 0 ]]; then
  exit 3
fi
exit 0
