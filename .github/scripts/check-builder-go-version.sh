#!/usr/bin/env bash
# Checks that the builder image Go version in deploy/backend/Dockerfile equals the one in go.mod.
# Used by ci.yml and release.yml (records 0041, 0053).
set -euo pipefail
f=deploy/backend/Dockerfile
mod="$(awk '$1=="go"{print $2; exit}' go.mod | cut -d. -f1,2)"
name="$(sed -nE 's/^ARG[[:space:]]+BASE_BUILD_IMAGE="?([^"[:space:]]+)"?[[:space:]]*$/\1/p' "$f")"
tag="$(sed -nE 's/^ARG[[:space:]]+BASE_BUILD_TAG="?([^"[:space:]]+)"?[[:space:]]*$/\1/p' "$f")"
img="$(printf '%s' "$tag" | sed -nE 's/^([0-9]+\.[0-9]+)([.-].*)?$/\1/p')"
if [ "$name" != "golang" ] || ! grep -Eq '^FROM[[:space:]]+\$\{BASE_BUILD_IMAGE\}@\$\{BASE_BUILD_DIGEST\}[[:space:]]+AS[[:space:]]+build[[:space:]]*$' "$f"; then
  echo "::error::the build stage must be FROM \${BASE_BUILD_IMAGE}@\${BASE_BUILD_DIGEST} AS build with BASE_BUILD_IMAGE golang (got '$name')"
  exit 1
fi
if ! printf '%s' "$mod" | grep -Eq '^[0-9]+\.[0-9]+$' || ! printf '%s' "$img" | grep -Eq '^[0-9]+\.[0-9]+$'; then
  echo "::error::could not read Go versions (go.mod: '$mod', BASE_BUILD_TAG: '$tag')"
  exit 1
fi
if [ "$mod" != "$img" ]; then
  echo "::error::builder image Go $img (BASE_BUILD_TAG '$tag') differs from go.mod Go $mod"
  exit 1
fi
