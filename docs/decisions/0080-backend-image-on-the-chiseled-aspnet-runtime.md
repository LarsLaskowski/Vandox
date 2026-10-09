# 0080: The backend image runs on the chiseled ASP.NET runtime, built by the .NET SDK image, both pinned by digest

- **Status:** Accepted
- **Date:** 2026-10-06
- **Area:** —
- **Source:** Product Manager request: move the backend to .NET 10 with Blazor, keep the agent in Go
- **Supersedes:** —

## Context

[0041](0041-base-images-pinned-by-digest-through-build-arguments.md) pins base images by digest through build
arguments and refreshes the digests by hand, because Dependabot's `docker` parser cannot read `ARG`-based `FROM`
lines (issue #108 asked for automation). Between two refreshes the builder lags behind the newest patch and the
runtime behind its newest build, and nothing checked that `BASE_BUILD_TAG` and `BASE_BUILD_DIGEST` belong
together (the release checks read only the Dockerfile text). Facts that shaped the check: the multi-arch index
digest comes from `docker buildx imagetools inspect --format '{{json .Manifest}}' <image>:<tag>`; Docker Hub
lookups can fail with `429`, which is an error, not an answer; the base tags are rebuilt for OS package updates,
so a pinned digest is usually stale within days even when the framework version has not moved; the repository is
public, so anyone can open an issue with any title; a pull request opened with the `GITHUB_TOKEN` starts no
`pull_request` workflows, so its CI would not run. The backend now needs the .NET runtime in the image.

## Options considered

1. **`aspnet` on Debian/Ubuntu** — shell and package manager in the image.
2. **Chiseled (Ubuntu, distroless-style) `aspnet`** — no shell, no package manager, non-root capable.
3. **Self-contained trimmed publish on `runtime-deps`** — smaller, but Blazor Server and trimming are brittle.

Noticing a stale digest (issue #108, decided for the Go builder, unchanged for the .NET images):

4. **Scheduled workflow that opens a pull request** with the new digest — rejected: needs `contents: write` and
   `pull-requests: write` and the setting that lets Actions create pull requests, and a pull request from the
   `GITHUB_TOKEN` starts no CI, so the image build that would prove the digest does not run.
5. **Scheduled workflow that fails the run** — rejected: GitHub notifies only the user who last changed the
   `cron` line, a red run is easy to miss and says nothing durable about which digest moved.
6. **Scheduled workflow that opens or updates one issue** — chosen: needs only `contents: read` and
   `issues: write`, writes no file, and leaves the refresh as a reviewed pull request that runs the full CI.
   Scheduled workflows of a public repository are disabled after 60 days without repository activity.
7. **Renovate with a custom regex manager** — rejected: a second update bot whose write access (hosted App or
   token secret) widens the release pipeline's trust boundary (security area 13) for a few changes a month.
8. **Release-time `govulncheck -mode=binary` on the built binary** — rejected: for a stripped Go binary every
   standard-library vulnerability of the builder's Go version blocks the release, reachable or not, and it
   says nothing about a stale runtime digest.
9. **Failing the release or every pull request on a stale digest** — rejected: the bases are rebuilt often, a
   failure after the tag is pushed forces a new version, and a failure on every pull request blocks unrelated
   work. Pull requests and pushes to `main` show a warning in the *Release build check*; so does a failed
   registry lookup.
10. **In-build guard:** compare the toolchain version the image reports with the version at the start of the
    tag (see *Decision*); `RUN … version` alone prints but never fails. Two variants of the same version
    (`1.27-trixie` and `1.27-bookworm`) cannot be told apart.
11. **Issue lookup and tokens:** find the open issue by title **and** author `github-actions[bot]` (title alone
    lets an outsider make the workflow edit a foreign issue), with a failed lookup ending the job before any
    write (otherwise a duplicate is created); run the script in a step without any token and hand the issue
    body over as a file under `$RUNNER_TEMP`, so that only the issue step gets `GH_TOKEN`; match registry
    values against their patterns as a whole string (`[[ $v =~ $re ]]` under `LC_ALL=C`), because
    `grep -Eq '^…$'` matches line by line and lets a value with an embedded newline reach the issue body.

## Decision

Option 2. `deploy/backend/Dockerfile`: stage `build` is `mcr.microsoft.com/dotnet/sdk:10.0`, the runtime is
`mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled`, both `FROM ${BASE_<NAME>_IMAGE}@${BASE_<NAME>_DIGEST}`
as in 0041. The build runs `dotnet publish` framework-dependent with `-p:VandoxVersion/VandoxCommit/VandoxDate`
(the same `--version` output as before). The image runs as 65532:65532 with the same `/data` seed, no `EXPOSE`, and
`HEALTHCHECK` through `dotnet /app/vandoxd.dll -healthcheck` (0059). The `.NET` version check replaces the earlier Go
check (a build stage that failed unless `go env GOVERSION` matched `BASE_BUILD_TAG`): `.github/scripts/check-builder-dotnet-version.sh` requires the target framework in
`Directory.Build.props`, the builder tag and the runtime tag to name the same version. Workstation, non-concurrent
GC and `DOTNET_EnableDiagnostics=0` suit the 512 MiB container limit of 0060. The SBOM check expects NuGet packages in the image.

The stale-digest report (0041 stays in force: the digests are still written by hand in a pull request):

- `.github/scripts/check-base-image-digests.sh` runs `.github/scripts/check-base-image-pinning.sh` first, takes
  every `BASE_<NAME>` from the `FROM` lines, reads the `_IMAGE`, `_TAG` and `_DIGEST` defaults and resolves
  `<image>:<tag>` to its index digest; every value is checked against a fixed pattern before use and a value
  that does not match is a lookup error and printed nowhere. Exit status 0: every digest is current; 3: at least
  one is stale (a `::warning::` per base and a Markdown table; for the builder the framework version of the
  current tag is shown as `stale (<ver> available)`, informational only); 4: a registry lookup failed; 1: any
  other error. Precedence 1 > 4 > 3 > 0.
- `.github/workflows/base-image-digests.yml` runs it every Monday and on manual dispatch with `contents: read`
  and `issues: write`. On status 3 a second step, the only one with `GH_TOKEN`, updates the open issue *Base
  image digests are stale* opened by `github-actions[bot]` or opens it (labels `dependencies`,
  `area: docker`); on 0 it does nothing, on any other status it fails. It writes no file and opens no pull
  request.
- The *Release build check* in `ci.yml` runs the script after the version check: status 3 and 4 pass with a
  warning, any other non-zero status fails. The release is not gated on digest freshness.

## Consequences

- The image is larger than the static-Go image and starts slower; the health check's start period covers it.
- Digests are refreshed by hand as before (0041); the weekly check reports stale ones within a week and on every
  pull request, but the gap between a patch release and the image only narrows. Expected noise: the stale issue is
  open most of the time because the tags are rebuilt for OS updates, so the version in the state column tells a
  routine refresh from an urgent one; a refresh is due at the latest before a release. A runtime or builder
  digest from another version than its tag now fails the image build.
- The scheduled workflow is a second workflow with a write permission (`issues: write`), in security area 13; if
  GitHub disables it after 60 days without activity it has to be re-enabled. If Dependabot learns to resolve
  `ARG` defaults in `FROM` lines, the `docker` entry can return and this workflow can go.
- The container was not built in the session that made this change (no Docker daemon); the first CI run of the
  release build check is its verification.
