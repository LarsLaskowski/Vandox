#!/usr/bin/env bash
# Compares each pinned base image digest in deploy/backend/Dockerfile with the current index digest of its
# tag in the registry. Run from the repository root; needs docker with buildx, and jq.
# Prints a Markdown table to standard output and ::warning::/::error:: lines to standard error.
# Exit status: 0 all current, 3 at least one digest is stale, 4 a registry lookup failed,
# 1 any other error (precedence 1 > 4 > 3 > 0). Used by base-image-digests.yml and ci.yml (record 0055).
set -euo pipefail
export LC_ALL=C
f=deploy/backend/Dockerfile
# Every value is checked as a whole string with [[ =~ ]] (not line by line with grep), because a registry
# answer can hold several lines.
image_re='^[a-z0-9][a-z0-9._/-]*$'
tag_re='^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$'
digest_re='^sha256:[0-9a-f]{64}$'
go_re='^[0-9]+\.[0-9]+(\.[0-9]+)?$'
from_re='^FROM[[:space:]]+\$\{(BASE_[A-Z]+)_IMAGE\}@'

if ! .github/scripts/check-base-image-pinning.sh; then
  exit 1
fi
for tool in docker jq; do
  if ! command -v "$tool" > /dev/null; then
    echo "::error::$tool is required" >&2
    exit 1
  fi
done

# Prints the default of a global ARG (declared once, checked by the pinning script).
arg_default() {
  sed -nE "s/^ARG[[:space:]]+${1}=\"?([^\"[:space:]]+)\"?[[:space:]]*$/\1/p" "$f"
}

names=()
while IFS= read -r line; do
  if [[ $line =~ $from_re ]]; then
    names+=("${BASH_REMATCH[1]}")
  fi
done < <(grep -E '^FROM[[:space:]]' "$f")
if [[ ${#names[@]} -eq 0 ]]; then
  echo "::error::no base image FROM line in $f" >&2
  exit 1
fi

# Validate everything before any value reaches a command.
declare -A image tag pinned
for name in "${names[@]}"; do
  image[$name]="$(arg_default "${name}_IMAGE")"
  tag[$name]="$(arg_default "${name}_TAG")"
  pinned[$name]="$(arg_default "${name}_DIGEST")"
  if ! [[ ${image[$name]} =~ $image_re ]]; then
    echo "::error::${name}_IMAGE is not a valid image name" >&2
    exit 1
  fi
  if ! [[ ${tag[$name]} =~ $tag_re ]]; then
    echo "::error::${name}_TAG is not a valid tag" >&2
    exit 1
  fi
  if ! [[ ${pinned[$name]} =~ $digest_re ]]; then
    echo "::error::${name}_DIGEST is not a sha256 digest" >&2
    exit 1
  fi
done

stale=0
lookup_failed=0
echo '| Base | Image | Pinned digest | Current digest | State |'
echo '| ---- | ----- | ------------- | -------------- | ----- |'
for name in "${names[@]}"; do
  ref="${image[$name]}:${tag[$name]}"
  if ! manifest="$(docker buildx imagetools inspect --format '{{json .Manifest}}' "$ref")"; then
    echo "::warning::lookup of $ref failed" >&2
    lookup_failed=1
    continue
  fi
  if ! current="$(jq -r .digest <<<"$manifest")" || ! [[ $current =~ $digest_re ]]; then
    echo "::warning::lookup of $ref failed" >&2
    lookup_failed=1
    continue
  fi
  state=current
  if [[ $current != "${pinned[$name]}" ]]; then
    stale=1
    state=stale
    # Informational only: the Go version the tag carries now. Never changes the exit status.
    if config="$(docker buildx imagetools inspect --format '{{json .Image}}' "$ref" 2> /dev/null)" &&
      go="$(jq -r '(."linux/amd64".config.Env // .config.Env // [])[] | select(startswith("GOLANG_VERSION=")) | ltrimstr("GOLANG_VERSION=")' <<<"$config" 2> /dev/null)" &&
      [[ $go =~ $go_re ]]; then
      state="stale (Go $go available)"
    fi
    echo "::warning::${name}_DIGEST is stale: $ref is now $current, pinned ${pinned[$name]}" >&2
  fi
  echo "| $name | $ref | ${pinned[$name]} | $current | $state |"
done

if [[ $lookup_failed -ne 0 ]]; then
  exit 4
fi
if [[ $stale -ne 0 ]]; then
  exit 3
fi
exit 0
