#!/usr/bin/env bash
# Builds the install bundle of a release from the tagged tree and the built agent binary, and writes the
# release's SHA256SUMS (record 0131).
# Usage: package-agent.sh <tag> <dist>
#   <dist> holds vandox-agent-linux-amd64; it receives vandox-agent-install-<tag>.tar.gz and SHA256SUMS (two
#   lines: the bundle, then the binary). The bundle has one top directory and ten files (eleven archive entries, MANIFEST.sha256 before the scripts); the archive is
#   byte-reproducible from the tree (fixed order, owner, modes and time).
set -euo pipefail
umask 022
export LC_ALL=C

readonly BINARY="vandox-agent-linux-amd64"
readonly PIN="vandox-agent-linux-amd64.sha256"
readonly EXECUTABLES=(install.sh uninstall.sh)
readonly MANIFEST="MANIFEST.sha256"
readonly MANIFEST_NAMES=(VERSION agent.yaml common.sh mariadb-user-remove.sql mariadb-user.sql "$PIN" vandox-agent.service)
readonly PLAIN_FILES=(common.sh vandox-agent.service mariadb-user.sql mariadb-user-remove.sql agent.yaml)

fail() {
  echo "::error::$*" >&2
  exit 1
}

[[ $# -eq 2 ]] || fail "usage: package-agent.sh <tag> <dist>"

tag=$1
dist=$2
readonly tag dist
readonly tag_re='^v[0-9]+\.[0-9]+\.[0-9]+(-rc\.[0-9]+)?$'

[[ $tag =~ $tag_re ]] || fail "the tag '$tag' is not vX.Y.Z or vX.Y.Z-rc.N, the forms install.sh accepts"
[[ -f "$dist/$BINARY" && ! -L "$dist/$BINARY" ]] || fail "$dist holds no regular file $BINARY"

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
readonly repo
readonly source_dir="$repo/deploy/agent"
readonly top="vandox-agent-install-$tag"

stage="$(mktemp -d)"
readonly stage
trap 'rm -rf -- "$stage"' EXIT

mkdir -m 0755 "$stage/$top"

for name in "${EXECUTABLES[@]}"; do
  install -m 0755 -- "$source_dir/$name" "$stage/$top/$name"
done

for name in "${PLAIN_FILES[@]}"; do
  install -m 0644 -- "$source_dir/$name" "$stage/$top/$name"
done

printf '%s\n' "$tag" >"$stage/$top/VERSION"
chmod 0644 -- "$stage/$top/VERSION"

(cd "$dist" && sha256sum "$BINARY") >"$stage/$top/$PIN"
chmod 0644 -- "$stage/$top/$PIN"

(cd "$stage/$top" && sha256sum -- "${MANIFEST_NAMES[@]}") >"$stage/$top/$MANIFEST"
chmod 0644 -- "$stage/$top/$MANIFEST"

mtime="$(git -C "$repo" log -1 --format=%ct HEAD)"
readonly mtime

# The member order is fixed: the manifest sits before both scripts, so a script that exists was extracted
# after an attempted manifest (record 0131, option 34).
tar --create --format=ustar --no-recursion --owner=0 --group=0 --numeric-owner --mtime="@$mtime" \
  -C "$stage" "$top" "$top/VERSION" "$top/agent.yaml" "$top/common.sh" "$top/mariadb-user-remove.sql" \
  "$top/mariadb-user.sql" "$top/$PIN" "$top/vandox-agent.service" "$top/$MANIFEST" \
  "$top/uninstall.sh" "$top/install.sh" | gzip -9 -n >"$dist/$top.tar.gz"

(cd "$dist" && sha256sum "$top.tar.gz" "$BINARY" >SHA256SUMS)

echo "packaged $dist/$top.tar.gz"
