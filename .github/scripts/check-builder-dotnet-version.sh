#!/usr/bin/env bash
# Checks that the builder image SDK version in deploy/backend/Dockerfile equals the target framework in
# Directory.Build.props, and that the runtime image tag carries the same version.
# Used by ci.yml and release.yml (records 0037, 0041).
set -euo pipefail
f=deploy/backend/Dockerfile
tfm="$(sed -nE 's|^[[:space:]]*<TargetFramework>net([0-9]+\.[0-9]+)</TargetFramework>[[:space:]]*$|\1|p' Directory.Build.props)"
build_name="$(sed -nE 's/^ARG[[:space:]]+BASE_BUILD_IMAGE="?([^"[:space:]]+)"?[[:space:]]*$/\1/p' "$f")"
build_tag="$(sed -nE 's/^ARG[[:space:]]+BASE_BUILD_TAG="?([^"[:space:]]+)"?[[:space:]]*$/\1/p' "$f")"
run_name="$(sed -nE 's/^ARG[[:space:]]+BASE_RUNTIME_IMAGE="?([^"[:space:]]+)"?[[:space:]]*$/\1/p' "$f")"
run_tag="$(sed -nE 's/^ARG[[:space:]]+BASE_RUNTIME_TAG="?([^"[:space:]]+)"?[[:space:]]*$/\1/p' "$f")"
if [[ "$build_name" != "mcr.microsoft.com/dotnet/sdk" ]] || ! grep -Eq '^FROM[[:space:]]+\$\{BASE_BUILD_IMAGE\}@\$\{BASE_BUILD_DIGEST\}[[:space:]]+AS[[:space:]]+build[[:space:]]*$' "$f"; then
  echo "::error::the build stage must be FROM \${BASE_BUILD_IMAGE}@\${BASE_BUILD_DIGEST} AS build with BASE_BUILD_IMAGE mcr.microsoft.com/dotnet/sdk (got '$build_name')" >&2
  exit 1
fi
if [[ "$run_name" != "mcr.microsoft.com/dotnet/aspnet" ]]; then
  echo "::error::BASE_RUNTIME_IMAGE must be mcr.microsoft.com/dotnet/aspnet (got '$run_name')" >&2
  exit 1
fi
build="$(printf '%s' "$build_tag" | sed -nE 's/^([0-9]+\.[0-9]+)([.-].*)?$/\1/p')"
run="$(printf '%s' "$run_tag" | sed -nE 's/^([0-9]+\.[0-9]+)([.-].*)?$/\1/p')"
if ! printf '%s' "$tfm" | grep -Eq '^[0-9]+\.[0-9]+$' || ! printf '%s' "$build" | grep -Eq '^[0-9]+\.[0-9]+$' || ! printf '%s' "$run" | grep -Eq '^[0-9]+\.[0-9]+$'; then
  echo "::error::could not read .NET versions (framework: '$tfm', BASE_BUILD_TAG: '$build_tag', BASE_RUNTIME_TAG: '$run_tag')" >&2
  exit 1
fi
if [[ "$tfm" != "$build" || "$tfm" != "$run" ]]; then
  echo "::error::target framework net$tfm, builder tag '$build_tag' and runtime tag '$run_tag' must name the same .NET version" >&2
  exit 1
fi
