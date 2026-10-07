# 0080: The backend image runs on the chiseled ASP.NET runtime, built by the .NET SDK image, both pinned by digest

- **Status:** Accepted
- **Date:** 2026-10-06
- **Source:** Product Manager request: move the backend to .NET 10 with Blazor, keep the agent in Go
- **Supersedes:** [0055](0055-stale-base-image-digests-reported-weekly-builder-go-checked-in-build.md)

## Context

[0041](0041-base-images-pinned-by-digest-through-build-arguments.md) pins base images by digest through build
arguments; [0055](0055-stale-base-image-digests-reported-weekly-builder-go-checked-in-build.md) added a check
that the builder's Go version matches its tag. The backend now needs the .NET runtime in the image.

## Options considered

1. **`aspnet` on Debian/Ubuntu** — shell and package manager in the image.
2. **Chiseled (Ubuntu, distroless-style) `aspnet`** — no shell, no package manager, non-root capable.
3. **Self-contained trimmed publish on `runtime-deps`** — smaller, but Blazor Server and trimming are brittle.

## Decision

Option 2. `deploy/backend/Dockerfile`: stage `build` is `mcr.microsoft.com/dotnet/sdk:10.0`, the runtime is
`mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled`, both `FROM ${BASE_<NAME>_IMAGE}@${BASE_<NAME>_DIGEST}`
as in 0041. The build runs `dotnet publish` framework-dependent with `-p:VandoxVersion/VandoxCommit/VandoxDate`
(the same `--version` output as before). The image runs as 65532:65532 with the same `/data` seed, no `EXPOSE`, and
`HEALTHCHECK` through `dotnet /app/vandoxd.dll -healthcheck` (0059). The `.NET` version check replaces the Go
check: `.github/scripts/check-builder-dotnet-version.sh` requires the target framework in
`Directory.Build.props`, the builder tag and the runtime tag to name the same version. Workstation, non-concurrent
GC and `DOTNET_EnableDiagnostics=0` suit the 512 MiB container limit of 0060. The SBOM check expects NuGet packages in the image.

## Consequences

- The image is larger than the static-Go image and starts slower; the health check's start period covers it.
- Digests are refreshed by hand as before (0041); the weekly check reports stale ones.
- The container was not built in the session that made this change (no Docker daemon); the first CI run of the
  release build check is its verification.
