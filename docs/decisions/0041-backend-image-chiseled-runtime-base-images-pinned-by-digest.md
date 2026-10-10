# 0041: Backend image on the chiseled ASP.NET runtime; base images pinned by digest through build arguments, refreshed by hand, staleness reported weekly

- **Status:** Accepted
- **Date:** 2026-10-04
- **Area:** —
- **Source:** Issues #9, #103, #108 and #173, Product Manager request (backend in .NET)
- **Supersedes:** —

## Context

The backend image `networlddev/vandox` must run as non-root on a minimal base and pin its base images by digest
(issue #9, security area 13). Writing tag and digest into one `FROM` line made SonarQube Cloud fail the quality gate
(`docker:S8431`: "use either the version tag or the digest, not both"), and the Product Manager decided to follow the
pattern of the maintainer's other repositories: build arguments for image, tag and digest, a digest-only `FROM`, and the
tag kept alongside for readability. Dependabot's `docker` parser reads neither `ARG` lines nor `${…}` references (its
`FROM` pattern needs an image name that starts with a lower-case letter or a digit; dependabot-core,
`docker/lib/dependabot/docker/file_parser.rb`, checked on 2026-10-10), so for such a `FROM` it
proposes nothing and the digests have to be refreshed by hand. Since the backend moved to .NET (0073),
the builder is the .NET SDK image and the runtime the ASP.NET image.

Two facts shape the stale-digest report: the base tags are rebuilt for OS updates, so a pinned digest is usually stale
within days even when the framework version has not moved, and a pull request opened with the `GITHUB_TOKEN` starts no
CI.

## Options considered

Runtime base:
- `aspnet` on a full Debian/Ubuntu image — rejected: a shell and a package manager in the image.
- Self-contained trimmed publish on `runtime-deps` — rejected: Blazor Server and trimming are brittle.
- **Chiseled `aspnet` (no shell, no package manager, non-root capable)** — chosen.

Pinning form:
- Tag and digest in the `FROM` line, Sonar rule silenced — rejected: against the Product Manager's decision.
- Digest-only literal `FROM` with the tag in a comment — rejected: no tag to follow, the version is not machine-readable.
- Tag-only `FROM` — rejected: drops digest pinning, a guarantee of `docs/ARCHITECTURE.md`.
- **`BASE_<NAME>_IMAGE`, `_TAG` and `_DIGEST` build arguments, `FROM ${…_IMAGE}@${…_DIGEST}` and OCI base-image labels** —
  chosen; the Dependabot `docker` entry is removed because it finds nothing.

How strict the release pinning check is:
- Read only simple upper-case `FROM` and `ARG` lines, or parse the Dockerfile fully, or whitelist the allowed line forms
  before the first `FROM` — rejected (forms the reader cannot see pass unseen; a hand-written parser is error-prone; a
  whitelist is out of proportion for a repository-owned file).
- **Reject every form the simple reader cannot see, then read the simple form** — chosen: it fails closed.

Noticing a stale digest:
- A scheduled workflow that opens a pull request — rejected: needs write permissions, and its pull request starts no CI.
- A scheduled workflow that fails the run — rejected: easily missed and says nothing durable.
- Renovate with a custom manager — rejected: a second bot with write access widens the release pipeline's trust boundary.
- Failing the release or every pull request on a stale digest — rejected: the bases are rebuilt often and a failure after
  the tag forces a new version; stale digests are a warning in the *Release build check* instead.
- **A weekly workflow that opens or updates one issue** (`contents: read`, `issues: write`, no file written) — chosen:
  the refresh stays a reviewed pull request that runs the full CI.

## Decision

`deploy/backend/Dockerfile` builds on `mcr.microsoft.com/dotnet/sdk:10.0` and runs on
`mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled` as `65532:65532`. Both are declared once as `BASE_<NAME>_IMAGE`, `_TAG`
and `_DIGEST` arguments and used as a digest-only `FROM`; the runtime stage adds the OCI base-image labels. The release
workflow and the *Release build check* fail unless every `FROM` has that form, the digest is a `sha256` value, and the
builder and runtime tags name the .NET version of the target framework. Digests are refreshed by hand in a pull request,
tag and digest always together (`docs/CONTRIBUTING.md`, *Base image digests*), and a weekly workflow reports stale ones in
one issue. The exact checks are in `.github/scripts/` and the workflows.

## Consequences

- Digest updates are not automatic. Between two refreshes the builder and the runtime may lag behind their newest builds.
  The stale issue is open most of the time because the tags are rebuilt for OS updates; the version in its state column
  tells a routine refresh from an urgent one, and a refresh is due before every release tag.
- Nobody checks that a tag and its digest belong together beyond the .NET version check: a digest of another version than
  its tag fails the image build, a different build of the same version passes.
- Dependabot does not cover `docker`, and `.github/dependabot.yml` has no `docker` entry: one that is present finds no
  dependency, runs green and suggests coverage that does not exist. Resolving `ARG` defaults in `FROM` lines would not be
  enough: with a digest-only `FROM` the digest would be read as the version, and a refresh must change tag and digest
  together (`docs/CONTRIBUTING.md`, *Base image digests*). The entry may be reconsidered only if Dependabot learns to
  resolve and update these `ARG` values, tag and digest together, and only then is the weekly workflow's job `check`, the
  current guard against a stale digest, redundant. The weekly workflow holds a write permission (`issues: write`,
  security area 13) and GitHub disables scheduled workflows after 60 days without repository activity.
- The image is larger and starts slower than a static binary on distroless, and the memory limit matters more (0060); the
  health check's start period covers the start. Volumes mounted into the container must be writable by 65532, and without
  a shell the `HEALTHCHECK` is built into `vandoxd` (0059).
- Only the CI analysis can confirm that `docker:S8431` no longer applies.
