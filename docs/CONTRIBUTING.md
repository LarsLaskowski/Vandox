# Contributing

<!-- project:begin getting-started -->
## Getting started

### Machine setup

Git, the Go toolchain from `go.mod`, `golangci-lint` (see `.squad/stack.md`, *Toolchain*) and Python 3 for
the squad tools in `.squad/tools/`. `govulncheck` runs through `go tool govulncheck`.

### Cloning the repository

```shell
git clone https://github.com/LarsLaskowski/Vandox.git
```

### Building and running

```shell
go mod download
go build ./...
go run ./cmd/vandoxd --version
```

Both binaries currently only print their version; configuration is added together with the first features.

### Running tests

```shell
go test ./... -race
```

For detailed rules on how unit tests are structured and named, see [`UNIT_TESTS.md`](UNIT_TESTS.md).
**Unit tests are mandatory for newly written code** — see the checklist there before opening a pull
request.
<!-- project:end getting-started -->

## Submitting a pull request

Nothing is ever committed or pushed directly to `main` — every change goes through a separate branch and
a pull request.

Pull requests are merged with **Squash and merge**: the PR title becomes the single commit subject on
`main` and the description its body, so the commits on the branch are working history and need not be
curated. Keep the branch up to date by merging the current `main` into it (no force-push needed); do not
use the plain *Create a merge commit* or *Rebase and merge* buttons (see the decision record on
squash-merging in [`decisions/`](decisions/README.md)).

For PR naming use the following convention: `[area] Description` (no period at the end).

- For the area, use one of the areas listed below, capitalized.
- For the description, do not reference an issue number in there. A clear, short summary of what
  the change entails is enough; there is room to elaborate in the description.

<!-- project:begin areas -->
Areas: `Agent`, `Backend`, `Web`, `Logs`, `Alerts`, `Security`, `Ops`, `Repo`, `Tests`, `Docker`, `CI`, `Docs`.
<!-- project:end areas -->

When a PR is related to an issue, use the `Closes #issuenumber` syntax so the issue links to the
PR automatically and closes when the PR is merged.

Follow the PR template in [`.github/pull_request_template.md`](../.github/pull_request_template.md).

## Quality gates

Code-style rules are documented in [`CLAUDE.md`](../CLAUDE.md) (mirrored in `AGENTS.md` and
[`.github/copilot-instructions.md`](../.github/copilot-instructions.md)) and in
[`.squad/stack.md`](../.squad/stack.md), and are binding for all contributions. Before opening a pull
request, run the commands from `stack.md`: *Format*, *Build*, the *Analyzer gate* (no analyzer diagnostic
of any severity in a changed file) and the *Coverage gate* (at least 80 % line coverage on new or changed
production code and overall, see [`UNIT_TESTS.md`](UNIT_TESTS.md#code-coverage)). A pull request is
expected to arrive clean (see the decision record on quality gates in [`decisions/`](decisions/README.md)).

<!-- project:begin releases -->
## Versioning and releases

A release is a `v<major>.<minor>.<patch>` tag created manually on `main`; it publishes the agent binary and
the backend Docker image. A release is always created manually, and the only trigger is pushing a new tag
such as `v0.1.0`; merging a PR, pushing to `main` or a schedule never publishes one. `release.yml`
does not run on pull requests; `ci.yml` checks the release build there (*Release build check* below). The
workflow is `.github/workflows/release.yml`; the reasoning is in
[0053](decisions/0053-releases-are-manual-and-started-only-by-a-version-tag.md),
[0037](decisions/0037-release-workflow-with-plain-go-docker-and-gh.md),
[0041](decisions/0041-base-images-pinned-by-digest-through-build-arguments.md),
[0039](decisions/0039-docker-hub-token-in-a-tag-only-environment.md) and
[0054](decisions/0054-release-provenance-attestations-from-a-secret-free-job.md).

### Cutting a release

Before tagging, check that the base image digests are current (*Base image digests* below).

On an up-to-date `main`:

```bash
git tag -a vX.Y.Z -m "vX.Y.Z"
git push origin vX.Y.Z
```

Pre-release tags look like `vX.Y.Z-rc.N`. Build metadata (`+...`) is not allowed.

### What the release workflow does

1. Checks that the tag is strict SemVer and that its commit is reachable from `origin/main`.
2. Runs `go tool govulncheck ./...`; a finding stops the release.
3. Builds `vandox-agent` (linux/amd64, static, `-trimpath`) and `SHA256SUMS`, and builds the image from
   `deploy/backend/Dockerfile` with `docker build --no-cache`. Nothing is restored from a CI cache.
4. Verifies both binaries' `--version` output against the tag, the full commit SHA and the commit time, the
   checksum, that the binary is static, that every `FROM` uses a base image build argument pinned by a sha256 digest, that the builder tag's Go
   minor version equals `go.mod`'s, and that the image runs as `65532:65532`.
5. Pushes exactly the verified image as `networlddev/vandox:X.Y.Z`, and as `latest` when the tag is the
   highest stable `v*.*.*` tag. A pre-release tag publishes only its own version. If the version already
   exists on Docker Hub, or the check cannot tell, the job fails before pushing: a published version is
   never overwritten.
6. Creates SLSA build provenance attestations for the binary and the image digest (GitHub artifact
   attestations, stored on GitHub, not in Docker Hub) in the `attest` job, which holds only
   `id-token: write` and `attestations: write`, has no environment and reads no secret, and verifies them
   with the README's flags (the image by its published tag, see *Verifying a release*).
7. Creates the GitHub release, only after the attestations exist and verify, with generated notes, the image
   digest, `vandox-agent-linux-amd64` and `SHA256SUMS`; a pre-release is marked as such.

### Verifying a release

The README (*Install*) has the commands: `gh attestation verify` for the binary and, by digest, for the
image (`oci://docker.io/networlddev/vandox@sha256:<digest>`, the digest from the release notes), followed by
pulling that same digest. `--source-ref` pins the tag, `--signer-workflow` pins `release.yml`, and
`--deny-self-hosted-runners` requires a GitHub-hosted runner. The image is verified and pulled by digest
because a tag can be re-pointed (immutable tags are optional, see *One-time setup (maintainer)*, item 3). The
workflow's own check uses the tag on purpose, to catch a tag that does not resolve to the attested digest.
An attestation does not prove that the tagged commit is on `main`; the tag ruleset `release-tags` stays
that boundary.

### Base image digests

`deploy/backend/Dockerfile` names each base image in three build arguments: `BASE_<NAME>_IMAGE`,
`BASE_<NAME>_TAG` and `BASE_<NAME>_DIGEST`. `FROM` uses only the image and the digest. Dependabot cannot
read these lines, so the digests are refreshed by hand: before every release tag, and whenever a Go
patch release or a distroless update appears. Read the multi-arch index digest of exactly the tag in
`BASE_<NAME>_TAG`, for example `docker buildx imagetools inspect golang:1.27-trixie` (the `Digest:`
line), and write it to `BASE_<NAME>_DIGEST` in a pull request. A new Go minor version changes the
`go` line in `go.mod`, `BASE_BUILD_TAG` and `BASE_BUILD_DIGEST` together. The release build passes no
`BASE_*` build argument, so the pinned defaults are what it uses.

### Release build check on pull requests

`release.yml` runs only for a version tag. Pull requests are covered by the `Release build check` job in
`ci.yml`: it runs the base image pinning and builder Go version checks (`.github/scripts/`), builds the
agent and the image with the version `v0.0.0-dryrun`, and verifies `--version`, the static binary and the
image user. It uploads, pushes and releases nothing and reads no secret.

### One-time setup (maintainer)

1. **Tag ruleset.** Create the ruleset `release-tags` (Settings, Rules, Rulesets, new tag ruleset) with the
   target `refs/tags/v*`, enforcement *Active*, the rules *Restrict creations*, *Restrict updates* and
   *Restrict deletions*, and *Repository admin* as the only bypass. A tag push runs the workflow file of
   the tagged commit, so whoever can create the tag controls what runs with the Docker Hub token. Create
   the ruleset before storing the token.
2. **Environment.** Create the GitHub environment `release` with deployment branches and tags set to
   *Selected branches and tags*: the tag rule `v*.*.*` and no branch. Store the secret `DOCKERHUB_TOKEN`
   and the variable `DOCKERHUB_USERNAME` there. A required reviewer is optional; it is worth adding once
   more than one person has write access (0039).
3. **Immutable tags (optional, recommended where the Docker Hub subscription offers it).** Enable immutable
   tags on `networlddev/vandox` for version tags only, for example the rule
   `^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$`, never for `latest`, which has to move.

### Creating the Docker Hub token

Create an organization access token limited to the repository `networlddev/vandox` with push and pull, and
set `DOCKERHUB_USERNAME` to `networlddev`. If the plan offers no organization access tokens, use a dedicated
Docker Hub user that is a member of a team with *Read & Write* on `networlddev/vandox` only, plus a personal
access token of that user with the scope *Read & Write*. A personal access token of the maintainer's own
account is not acceptable, because it is not limited to one repository. To rotate the token, create the new
one, replace the environment secret, then delete the old token on Docker Hub.

### Re-running a failed release

If a job fails before the image is pushed, nothing was published: fix the cause and use "Re-run all jobs"
(or, if the tag itself was wrong, ask the repository admin to delete and recreate it).

If only `github-release` failed, use "Re-run failed jobs". It reruns just that job and works while the run's
release artifact exists, that is 7 days after the tag run.

If `attest` failed, the image is already published and no GitHub release exists. "Re-run failed jobs" reruns
`attest` and `github-release` within the 7-day artifact window; a second attestation for the same digest is
harmless. After that window, cut a new patch version. If the "Verify attestations" step reports a digest
mismatch (the version tag does not resolve to the attested digest), do not re-run it; investigate and cut a
new patch version.

If `publish-image` failed after `networlddev/vandox:<version>` was pushed (it failed while pushing `latest`
or reading the digest), a re-run stops at the never-overwrite check. Do not push by hand; cut a new patch
version. The orphaned version tag stays on Docker Hub without a GitHub release. The same applies once the
7-day window has passed.

A published image version is never replaced; a faulty release gets a new patch version.
<!-- project:end releases -->

<!-- project:begin stability -->
## Stability policy

An essential consideration in every pull request is its impact on the system. Avoid introducing
unnecessary breaking changes, performance or functional regressions, or negative impacts on usability. In
particular, preserve the guarantees listed in [`.squad/project.md`](../.squad/project.md) (*Guarantees*)
and described in [`ARCHITECTURE.md`](ARCHITECTURE.md) unless a change explicitly intends to alter one.
<!-- project:end stability -->

## Reporting security issues

Do not report security vulnerabilities through public GitHub issues. See
[`SECURITY.md`](../SECURITY.md) for the private reporting process.

## License

<!-- project:begin license -->
By contributing to this project, you agree that your contributions will be licensed under the
same license that covers the project (see `LICENSE` or `LICENSE.md` in the repository root).
<!-- project:end license -->
