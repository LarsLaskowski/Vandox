# 0037: Release workflow built from plain go build, the Docker CLI and gh; verified once, published as built

- **Status:** Accepted
- **Date:** 2026-10-04
- **Source:** Issue #9
- **Supersedes:** — (the pull-request dry run in *Trigger* is amended by [0053](0053-releases-are-manual-and-started-only-by-a-version-tag.md))

## Context

Issue #9 asks for a SemVer tag (`v*.*.*`) to produce a complete, verifiable release without manual steps.
The release has these parts:

- the `vandox-agent` binary for linux/amd64 and a `SHA256SUMS` file as GitHub release assets
- the backend image `networlddev/vandox` (record 0027)
- version, commit and build date injected through `-ldflags` into `internal/version`
- release notes generated from merged pull requests

`docs/CONTRIBUTING.md` already says a release tag is created manually on `main`. CI (`ci.yml`) pins every
action by commit SHA, and Dependabot updates them. A release workflow only runs when a tag is pushed, so a
broken workflow would otherwise first show up on a real release.

## Options considered

1. **GoReleaser (action and `.goreleaser.yaml`)**
   - Pros: builds, checksums, Docker images and changelogs in one tool.
   - Cons: a large third-party tool with release-wide permissions, a second configuration language, and a
     changelog from commit messages unless configured otherwise. With squash merges, PR titles are what
     reach `main` anyway. Far more than one binary and one image need.
2. **Docker's official actions (`setup-buildx`, `login`, `metadata`, `build-push`) plus a release action**
   - Pros: familiar, with BuildKit features.
   - Cons: four or five more third-party actions holding the registry credential. Building and pushing in one
     step makes it awkward to test the exact image before it is pushed.
3. **Plain `go build`, the Docker CLI and `gh` on the GitHub runner, in three jobs**
   - `build` builds and verifies everything with read-only permissions and hands the files and the saved
     image to the next jobs as an artifact.
   - `publish-image` loads and pushes that exact image.
   - `github-release` creates the release with `gh release create --generate-notes`.
   - A dry run of `build` runs on pull requests that change the release inputs.
   - Pros: the only new actions are GitHub's own `upload-artifact` and `download-artifact`; the
     credential never leaves one job; the published image is the verified one.
   - Cons: a longer YAML file the project maintains itself.

For build caches, two options were considered:

- *Restore the Go build and module cache (`setup-go` `cache: true`) and a Docker layer cache
  (`--cache-from`, the `type=gha` backend)*: faster releases. Rejected: a tag run can restore caches saved
  on the default branch, and `ci.yml` saves the Go cache there after third-party actions and dependency code
  have run in the same job. A poisoned object in that cache would be linked into the release binary, and
  neither `--version` nor `file` would notice.
- *Build cold*: `setup-go` with `cache: false`, no `actions/cache`, plain `docker build --no-cache` without a
  cache backend. Modules come from the Go module proxy and are checked against `go.sum`. Costs a few minutes
  per run.

For the build date, the time of the build and the commit time of the tagged commit were both considered. The
build time makes every rebuild produce a different binary. The commit time makes the build reproducible, so
anyone can rebuild from the tag and compare against `SHA256SUMS`. For the commit, the short SHA was rejected
because its length varies; the full SHA is unambiguous.

## Decision

Option 3, in `.github/workflows/release.yml`:

- **Trigger:** on `push` of tags `v*.*.*`. Also as a dry run on pull requests to `main` that change the
  workflow, `deploy/backend/Dockerfile`, `.dockerignore`, `go.mod`, `go.sum`, `cmd/**` or `internal/**`. The
  `cmd/**` and `internal/**` paths are included because the `--version` checks depend on them. The filter is
  kept so that documentation-only pull requests do not build an image.
- **Tag checks:** the tag must match strict SemVer, `^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?$`,
  and its commit must be reachable from `origin/main`. The ancestry check catches a maintainer's mistake
  but is not a security boundary. A tag push runs the workflow file of the tagged commit, so who may start a
  release is decided by who may create the tag (record 0039).
- **Build flags:** both binaries are built with `CGO_ENABLED=0 -trimpath -buildvcs=false -ldflags "-s -w -X …"`:
  - `Version` = the tag (with `v`)
  - `Commit` = the full SHA of the tagged commit
  - `Date` = the commit time in UTC as `%Y-%m-%dT%H:%M:%SZ`
- **No restored caches:** the `build` job (tag run and dry run) uses `setup-go` with `cache: false`, no
  `actions/cache`, and `docker build --no-cache` without `--cache-from`, `--cache-to` or a cache backend.
- **Verification before publishing:** before anything is published, `go tool govulncheck ./...` must pass,
  and the workflow checks that each binary's `--version` output equals the expected line exactly.
- **Script hygiene:** no `${{ }}` expression of any kind appears inside a `run:` script; event data, step
  and job outputs, `vars` and `github.*` reach scripts only through `env:`. `actions/checkout` runs with
  `persist-credentials: false`. The `github-release` job has no checkout and passes `GH_REPO` to `gh`.
- **Agent asset:** `vandox-agent-linux-amd64`, a raw binary, not an archive, listed in `SHA256SUMS`.
- **Order:** the image is pushed first. Then the GitHub release is created with `--verify-tag` and
  GitHub-generated notes from the merged pull requests, with the image digest added to the notes.
- **Pre-releases:** a tag with a pre-release suffix creates a GitHub pre-release.

## Consequences

- A broken release workflow, Dockerfile or base-image bump fails in pull-request CI, not on the release tag.
- Every release and dry run builds cold, which takes a few minutes longer. This is accepted in exchange for
  binaries that do not depend on a cache another workflow wrote.
- A newly published vulnerability that reaches the code stops a release until it is fixed. `govulncheck`
  does not flag a Go version that is merely out of upstream support, so the Go toolchain is moved to a
  supported version before the first stable tag (`v0.1.0`), in a separate change.
- The ldflags string exists twice, in the workflow (agent) and in the Dockerfile (backend). The exact
  `--version` checks catch any drift between them.
- Release notes follow the PR titles. Their quality depends on the `[area] Description` titles from
  `docs/CONTRIBUTING.md`.
- The `release` artifact is kept for 7 days, so "Re-run failed jobs" on `publish-image` or
  `github-release` works for a week after the tag run. After that, or for a run that is not re-runnable,
  the release gets a new patch version.
- `publish-image` is not idempotent: if it fails after `networlddev/vandox:$VERSION` was pushed (while
  pushing `latest` or reading the digest), a re-run stops at the never-overwrite check, and neither
  `latest` nor the GitHub release is created. The recovery is a new patch version, not a manual push:
  pushing by hand would need the Docker Hub token outside the `release` environment (record 0039). Making
  the job resume when the published digest equals the built image was considered and left out, because it
  weakens the never-overwrite check for a rare failure.
- Signing and provenance (cosign, GitHub artifact attestations, SBOM) are not part of this decision. Adding
  them is a new record.
- More platforms (e.g. arm64) mean a build matrix and more asset names. That needs a new record if the
  naming changes.
