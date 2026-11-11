# 0197: The backend moves to .NET 11; the runtime image becomes the chiseled Ubuntu 26.04 ASP.NET image

- **Status:** Accepted
- **Date:** 2026-11-11
- **Area:** —
- **Source:** Product Manager request: move the backend to .NET 11 (feature dotnet-11-upgrade)
- **Supersedes:** [0073](0073-backend-in-dotnet-10-with-blazor-agent-stays-go.md) (the .NET major of the backend; the rest of
  0073 — a .NET backend with Blazor, a Go agent — stands); amends
  [0041](0041-backend-image-chiseled-runtime-base-images-pinned-by-digest.md) (the runtime tag
  `10.0-noble-chiseled` becomes `11.0-resolute-chiseled`; chiseled and pinned by digest stay)

## Context

0073 put the backend on .NET 10 (LTS, support until 2028-11-14). .NET 11 (STS) was released with the same end of support, so the
move shortens no support window. The 11 SDK brings analyzer rules the 10 SDK did not have, and every diagnostic fails the build,
so the move and the fixes are one change. Microsoft publishes no `aspnet:11.0-noble-chiseled` image: the chiseled ASP.NET 11
images are built on Ubuntu 26.04 (`11.0-resolute-chiseled`). The reference host (Synology DS918+, Linux 4.4, a CPU without AVX)
cannot be reproduced in CI, which runs the image on a current kernel only.

## Options considered

1. **.NET 11 on `aspnet:11.0-resolute-chiseled`** (chosen by the Product Manager) — the chiseled image of the new major, no
   shell and no package manager as 0041 requires; whether it starts on Linux 4.4 is checked by hand on the reference host
   before the pull request is merged.
2. **.NET 11 on the full `aspnet:11.0-noble` image** — the distribution of today's image, but a shell and a package manager in
   the image, which 0041 rejected.
3. **A self-built chiseled Ubuntu 24.04 image with the ASP.NET 11 runtime** — keeps the distribution, but an own base image to
   build, sign and refresh, against 0041's pinned upstream images.
4. **Stay on .NET 10** — no change and the longer-tested LTS, but the Product Manager decided to move.

## Decision

Every C# project, `global.json` (SDK 11.0.100, `rollForward` `latestFeature`) and both base images are on .NET 11; the
runtime image is `mcr.microsoft.com/dotnet/aspnet:11.0-resolute-chiseled`, pinned by digest as 0041 requires. The
Microsoft.Data.Sqlite and Microsoft.Extensions packages move to their 11 releases with it.

## Consequences

- The runtime base moves from Ubuntu 24.04 to 26.04; a start on the reference host is a manual check of this change, signed
  off by the Product Manager, and on any later move of the base distribution.
- Microsoft.Data.Sqlite 11 brings SQLitePCLRaw 3 (a new major, with its own native SQLite build); the storage tests run on it.
- Dependabot keeps the SDK and the packages current within 11; the next major (12) stays one change of its own
  (`.github/dependabot.yml`).
- Measurements taken on .NET 10 (`docs/BENCHMARKS.md`) stay as measured.
