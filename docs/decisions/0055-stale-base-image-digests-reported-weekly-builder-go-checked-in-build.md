# 0055: Stale base image digests are reported by a weekly workflow as an issue; the build stage checks the builder's Go version

- **Status:** Proposed
- **Date:** 2026-10-05
- **Source:** Issue #108
- **Supersedes:** —

## Context

Record 0041 pins both base images of `deploy/backend/Dockerfile` through the build arguments
`BASE_<NAME>_IMAGE`, `BASE_<NAME>_TAG` and `BASE_<NAME>_DIGEST`, with `FROM ${…_IMAGE}@${…_DIGEST}`.
Dependabot's `docker` parser cannot read these lines, so 0041 removed the `docker` entry and chose to
refresh the digests by hand (0041, option 6), leaving automation to a follow-up issue (#108).

Two consequences of 0041 are what this record addresses:

- Between two refreshes the builder can lag behind the newest Go patch, and distroless behind its newest
  build. The release `govulncheck` scans the source with the runner's Go (`setup-go`, newest 1.27.x), so a
  standard-library fix that the builder lacks is not reported for `vandoxd`. Whether a refresh is due is
  noticed only if the maintainer checks before every tag.
- Nothing checks that `BASE_BUILD_TAG` and `BASE_BUILD_DIGEST` belong together: a digest read for another
  tag passes the release checks (`.github/scripts/check-base-image-pinning.sh`,
  `.github/scripts/check-builder-go-version.sh`), which read only the Dockerfile text.

Facts checked on 2026-10-05:

- `docker buildx imagetools inspect --format '{{json .Manifest}}' <image>:<tag>` returns the multi-arch
  index, whose `digest` is the value 0041 pins (for `gcr.io/distroless/static-debian13:nonroot` it equals
  the pinned `BASE_RUNTIME_DIGEST`). A Docker Hub lookup can fail with `429 Too Many Requests`; that is an
  error, not an answer. With `--format '{{json .Image}}'` the same command returns the image configuration
  per platform (for an index, a map keyed by `linux/amd64` and so on); the official `golang` images carry
  their Go version as `GOLANG_VERSION` in its `Env`.
- The `golang` tag is rebuilt for Debian package updates as well as for Go releases, and distroless is
  rebuilt often, so a pinned digest is usually stale within days even when the Go version has not moved.
- The repository is public: anyone can open an issue with any title.
- govulncheck v1.8.0 (`internal/vulncheck/binary.go`, `internal/buildinfo/additions_scan.go`): for a binary
  without a symbol table, which `-ldflags "-s -w"` produces, it falls back to "go.mod-level precision" and
  treats every known vulnerable symbol of an affected module as used. For the standard library that means
  every vulnerability of the builder's Go version, reachable or not, including ones no patch fixes yet.
- A `GITHUB_TOKEN` can open issues with `issues: write`. A pull request opened with the `GITHUB_TOKEN` does
  not start `pull_request` workflows, so `ci.yml` (including the *Release build check*) would not run on it.
- `go env GOVERSION` in the build stage reports the image's own toolchain as long as no `go.mod` or
  `GOTOOLCHAIN` asks for another one; the guard therefore runs before `go.mod` is copied.

## Options considered

1. **(a) Scheduled workflow, variant: opens a pull request** that writes the new digest into
   `BASE_<NAME>_DIGEST`.
   - Pros: the refresh itself needs no manual edit.
   - Cons: needs `contents: write` and `pull-requests: write` in a scheduled workflow and the repository
     setting that lets Actions create pull requests. A pull request from the `GITHUB_TOKEN` starts no CI, so
     the image build that would prove the new digest is not run until someone pushes to the branch; a
     personal or App token to avoid that is a new secret. A digest cannot be checked by reading it, so the
     review gains nothing over running the documented command.

   Rejected.
2. **(a) Scheduled workflow, variant: fails the run** when a digest is stale (as `ci.yml`'s weekly
   `govulncheck` does for new vulnerabilities).
   - Pros: needs no write permission at all.
   - Cons: GitHub notifies only the user who last changed the `cron` line; a red scheduled run is easy to
     miss, and it says nothing durable about which digest moved.

   Rejected.
3. **(a) Scheduled workflow, variant: opens or updates one issue** when a digest is stale.
   - Pros: detection without a manual step; a durable, visible reminder that names the image, the tag, the
     pinned and the current digest. Needs only `contents: read` and `issues: write`; it writes no file and
     pushes nothing. The refresh stays a reviewed pull request that runs the full CI.
   - Cons: the refresh is still a manual edit. Scheduled workflows of a public repository are disabled by
     GitHub after 60 days without repository activity.

   Chosen.
4. **(b) Renovate with a custom regex manager** for the `ARG` triplets.
   - Pros: Renovate can read the triplets and opens pull requests that run CI.
   - Cons: a second update bot next to Dependabot. The hosted Renovate App gets write access to the
     repository; self-hosted Renovate in a workflow needs a token secret with write access. Both widen the
     release pipeline's trust boundary (security area 13) for a change that happens a few times a month.

   Rejected.
5. **(c) Release-time `govulncheck -mode=binary` on the built `vandoxd`**.
   - Pros: checks the binary that is shipped, with the builder's Go.
   - Cons: because the binary is stripped, every standard-library vulnerability of the builder's Go version
     blocks the release, reachable or not, and so does one that no patch fixes yet, which no refresh can
     resolve. It says nothing about a stale distroless digest. It reports only at tag time, when the tag is
     already pushed.

   Rejected.
6. **Make a stale digest fail the release, or every pull request**.
   - Pros: no release is built on a stale base.
   - Cons: distroless is rebuilt often. A failure after the tag is pushed forces a new version (a published
     tag is not reused); a failure on every pull request blocks unrelated changes on an upstream event.

   Rejected. Pull requests and pushes to `main` show a stale digest as a warning in the *Release build
   check*; the release is not gated. For the same reason a failed registry lookup (for example a Docker
   Hub rate limit) is only a warning there; failing the check would block unrelated pull requests on a
   registry hiccup. Dropping the digest step from CI altogether was rejected: it is the only place that
   shows a stale digest on the pull request that should refresh it.
8. **What the report shows for a stale base**:
   - *Only "stale" with both digests*: rejected — because the `golang` and distroless tags are rebuilt for
     Debian updates, "stale" is the normal state and does not show whether the builder's Go patch lags,
     which is the actual risk.
   - *Also the Go version the current tag carries* (`GOLANG_VERSION` from its image configuration), as
     `stale (Go <ver> available)`: one extra registry lookup per stale base, informational only. Chosen.
   - *Also the Go version of the pinned digest*: a further lookup per run for a value the build stage
     guard already bounds to the tag's minor. Rejected.
9. **Finding the open issue to update**:
   - *By title only*: rejected — in a public repository an outsider can open an issue with that title,
     and the workflow would then edit an issue whose author it does not control.
   - *By title and author `github-actions[bot]`*, with a failed lookup ending the job before any write
     (otherwise an API error would create a duplicate issue). Chosen. A `creator=` query parameter is not
     relied on.
7. **In-build guard for the builder** (from the issue):
   - *`RUN go version` only*: prints, never fails. Rejected.
   - *Compare `go env GOVERSION` with the version at the start of `BASE_BUILD_TAG`*: a digest of another Go
     minor (or, for a tag with a patch, another patch) fails the image build. It cannot tell two variants of
     the same version apart (`1.27-trixie` and `1.27-bookworm`). Chosen.

## Decision

This extends 0041. The digests are still written by hand in a pull request (0041, option 6); what changes
is how a stale digest is noticed, and the build stage now checks the builder's Go version.

- `.github/scripts/check-base-image-digests.sh` runs `.github/scripts/check-base-image-pinning.sh` first.
  It then takes every `BASE_<NAME>` from the `FROM` lines, reads the `_IMAGE`, `_TAG` and `_DIGEST`
  defaults, and resolves `<image>:<tag>` with `docker buildx imagetools inspect` to its index digest. Image,
  tag, pinned and resolved digest are checked against fixed patterns before use. For a stale base it also
  reads `GOLANG_VERSION` from the current tag's `linux/amd64` image configuration and, if it matches
  `^[0-9]+\.[0-9]+(\.[0-9]+)?$`, shows `stale (Go <ver> available)`; otherwise plain `stale`. That read
  never changes the exit status. Exit status 0: every digest is current. Exit status 3: at least one is
  stale, with a `::warning::` per stale base and a Markdown table on standard output. Exit status 4: a
  registry lookup failed or answered with no valid digest. Exit status 1: any other error (pinning check,
  pattern check). Precedence 1 > 4 > 3 > 0.
- `.github/workflows/base-image-digests.yml` runs the script every Monday and on manual dispatch. Its only
  job holds `contents: read` and `issues: write`. On status 3 it looks for an open issue titled *Base image
  digests are stale* opened by `github-actions[bot]` and updates its body, or else opens that issue (labels
  `dependencies`, `area: docker`); a failed issue lookup fails the job before any write. On status 0 it
  does nothing; on any other status it fails. It writes no file, pushes nothing and opens no pull request.
- The *Release build check* job in `ci.yml` runs the script after the builder Go version check. Status 3
  passes with the warning, status 4 passes with a warning that freshness was not checked; any other
  non-zero status fails the job.
- The build stage of `deploy/backend/Dockerfile` re-declares `ARG BASE_BUILD_TAG` and, before `go.mod` is
  copied, fails unless `go env GOVERSION` is `go<v>` or starts with `go<v>.`, where `<v>` is
  `BASE_BUILD_TAG` up to the first `-`.
- `release.yml` and `.github/dependabot.yml` are unchanged. The release is not gated on digest freshness.

## Consequences

- A stale base image is reported within a week, and on every pull request and push to `main`, without
  anyone checking by hand. The refresh is still a pull request, and `docs/CONTRIBUTING.md` asks the
  maintainer to run the workflow, or read the release build check, before tagging.
- The gap between a Go patch release and the builder narrows to the time until the next scheduled run and
  the refresh pull request; it does not close. Source-mode `govulncheck` still does not see the builder's
  patch (0041).
- A builder digest from another Go minor than `BASE_BUILD_TAG` now fails the image build, in CI and in the
  release. Two variants of the same Go version still cannot be told apart.
- Expected noise: the stale issue will be open most of the time, because both tags are rebuilt for Debian
  updates without a Go change. The Go version in the state column is what tells a routine refresh from
  an urgent one (a newer Go patch than the builder's); a refresh is due at the latest before a release.
- The CI release build check now also makes a registry lookup per base image (two for a stale `golang`
  base). A failed lookup is only a warning there, so it does not fail unrelated pull requests; the
  scheduled workflow fails on it and reports again the following week.
- The scheduled workflow is a second workflow with a write permission (`issues: write`), in security area
  13. If GitHub disables it after 60 days without activity, it has to be re-enabled.
- To revisit: if Dependabot learns to resolve `ARG` defaults in `FROM` lines (0041), the `docker` entry can
  return and this workflow can go.
