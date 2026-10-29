#!/usr/bin/env bash
# Checks the install bundle that package-agent.sh wrote (record 0131): the two-line SHA256SUMS, the ten files
# of the archive in their fixed order with their owner and modes, VERSION, the manifest, the files against the tree, the binary's pin, and that
# packaging again gives the same bytes.
# Usage: check-agent-bundle.sh <dist> <tag>
set -euo pipefail
export LC_ALL=C

readonly BINARY="vandox-agent-linux-amd64"
readonly PIN="vandox-agent-linux-amd64.sha256"
readonly EXECUTABLES=(install.sh uninstall.sh)
readonly PLAIN_FILES=(common.sh vandox-agent.service mariadb-user.sql mariadb-user-remove.sql agent.yaml)
readonly MANIFEST="MANIFEST.sha256"
readonly MANIFEST_NAMES=(VERSION agent.yaml common.sh mariadb-user-remove.sql mariadb-user.sql "$PIN" vandox-agent.service)

fail() {
  echo "::error::$*" >&2
  exit 1
}

[[ $# -eq 2 ]] || fail "usage: check-agent-bundle.sh <dist> <tag>"

dist=$1
tag=$2
readonly tag
readonly tag_re='^v[0-9]+\.[0-9]+\.[0-9]+(-rc\.[0-9]+)?$'

[[ $tag =~ $tag_re ]] || fail "the tag '$tag' is not vX.Y.Z or vX.Y.Z-rc.N"
dist="$(cd "$dist" && pwd)"
readonly dist

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
readonly repo
readonly source_dir="$repo/deploy/agent"
readonly top="vandox-agent-install-$tag"
readonly bundle="$top.tar.gz"

work="$(mktemp -d)"
readonly work
trap 'rm -rf -- "$work"' EXIT

# SHA256SUMS: exactly two lines, the bundle then the binary, and sha256sum agrees.
sums_lines="$(wc -l <"$dist/SHA256SUMS")"
[[ ${sums_lines// /} == 2 ]] || fail "SHA256SUMS must hold exactly two lines"
[[ "$(sed -n 1p "$dist/SHA256SUMS")" =~ ^[0-9a-f]{64}\ \ vandox-agent-install-.*\.tar\.gz$ ]] || fail "the first line of SHA256SUMS is not the bundle's"
[[ "$(sed -n 1p "$dist/SHA256SUMS" | cut -c67-)" == "$bundle" ]] || fail "the first line of SHA256SUMS does not name $bundle"
[[ "$(sed -n 2p "$dist/SHA256SUMS" | cut -c65-)" == "  $BINARY" ]] || fail "the second line of SHA256SUMS does not name $BINARY"
[[ "$(sed -n 2p "$dist/SHA256SUMS")" =~ ^[0-9a-f]{64}\ \ vandox-agent-linux-amd64$ ]] || fail "the second line of SHA256SUMS is not well formed"
(cd "$dist" && sha256sum --check --strict SHA256SUMS)

# The archive: exactly the directory and ten files in this order, owner 0/0, fixed modes, no other entry type.
expected="$work/expected"
{
  printf 'drwxr-xr-x 0/0 %s/\n' "$top"
  for name in "${MANIFEST_NAMES[@]}"; do printf -- '-rw-r--r-- 0/0 %s/%s\n' "$top" "$name"; done
  printf -- '-rw-r--r-- 0/0 %s/%s\n' "$top" "$MANIFEST"
  printf -- '-rwxr-xr-x 0/0 %s/uninstall.sh\n' "$top"
  printf -- '-rwxr-xr-x 0/0 %s/install.sh\n' "$top"
} >"$expected"

tar -tvzf "$dist/$bundle" --numeric-owner | awk '{ print $1, $2, $NF }' >"$work/actual"

if ! diff -u "$expected" "$work/actual"; then
  fail "the archive does not hold exactly the eleven entries (directory and ten files) in the order and with the modes above"
fi

# Extract unprivileged into a scratch directory, then compare with the tree.
mkdir "$work/extract"
tar -xzf "$dist/$bundle" --no-same-owner --no-same-permissions -C "$work/extract"
readonly root="$work/extract/$top"

[[ "$(<"$root/VERSION")" == "$tag" ]] || fail "VERSION is not $tag"
[[ "$(wc -c <"$root/VERSION")" -eq $((${#tag} + 1)) ]] || fail "VERSION is not the tag and one line feed"

for name in "${EXECUTABLES[@]}" "${PLAIN_FILES[@]}"; do
  cmp -s -- "$source_dir/$name" "$root/$name" || fail "$name of the bundle differs from deploy/agent/$name"
done

# The manifest: seven lines in order, each the sha256sum of the source file, and the check passes in the extracted directory.
manifest_expected="$work/manifest.expected"
for name in "${MANIFEST_NAMES[@]}"; do
  if [[ $name == "$PIN" ]]; then
    printf '%s  %s\n' "$(sha256sum <"$root/$PIN" | cut -d' ' -f1)" "$name"
  elif [[ $name == VERSION ]]; then
    printf '%s  %s\n' "$(printf '%s\n' "$tag" | sha256sum | cut -d' ' -f1)" "$name"
  else
    printf '%s  %s\n' "$(sha256sum <"$source_dir/$name" | cut -d' ' -f1)" "$name"
  fi
done >"$manifest_expected"
cmp -s -- "$root/$MANIFEST" "$manifest_expected" || fail "$MANIFEST does not hold the seven expected lines in order"
(cd "$root" && sha256sum --check --strict --quiet "$MANIFEST") || fail "$MANIFEST does not match the extracted files"

# The pin equals the binary's line of SHA256SUMS and the binary's real hash.
cmp -s -- "$root/$PIN" <(sed -n 2p "$dist/SHA256SUMS") || fail "the pin differs from the binary line of SHA256SUMS"
cmp -s -- "$root/$PIN" <(cd "$dist" && sha256sum "$BINARY") || fail "the pin is not the hash of the binary"

# Reproducible: packaging again gives the same bundle and the same SHA256SUMS.
mkdir "$work/again"
install -m 0755 -- "$dist/$BINARY" "$work/again/$BINARY"
"$repo/.github/scripts/package-agent.sh" "$tag" "$work/again" >/dev/null
cmp -s -- "$dist/$bundle" "$work/again/$bundle" || fail "packaging again gives another bundle"
cmp -s -- "$dist/SHA256SUMS" "$work/again/SHA256SUMS" || fail "packaging again gives another SHA256SUMS"

echo "install bundle $bundle is complete and reproducible"
