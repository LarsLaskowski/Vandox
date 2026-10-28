# Contributing

<!-- project:begin getting-started -->
## Getting started

### Machine setup

Git; for the agent the Go toolchain from `go.mod` and `golangci-lint`; for the backend the .NET SDK named in
`global.json` (see `.squad/stack.md`, *Toolchain*); Python 3 for the squad tools in `.squad/tools/`. The local
.NET tools (`reihitsu-format`, `dotnet-sonarscanner`) come with `dotnet tool restore`. `govulncheck` runs through
`go tool govulncheck`. Docker is needed only to build and try the backend image.

### Cloning the repository

```shell
git clone https://github.com/LarsLaskowski/Vandox.git
```

### Building and running

```shell
go mod download
dotnet tool restore
dotnet restore Vandox.slnx
go build ./...
dotnet build Vandox.slnx
go run ./cmd/vandox-agent --version
dotnet run --project src/Vandox.Backend -- --version
```

The agent runs with a configuration file and the agent token, collects only its own metrics and sends nothing yet (`go run ./cmd/vandox-agent --version` prints its version); the backend loads its configuration, opens its database, imports logs,
rebuilds incidents and writes the report, and serves the web UI (login, logs, incidents, report) and `/healthz`. Format before building:
`gofmt -w . && reihitsu-format src tests tools`.

### Running tests

```shell
go test ./... -race
dotnet test Vandox.slnx
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

## Contributions from outside

Issues and pull requests from forks are welcome. Changes in this repository are spec-driven: what a feature does
is agreed as acceptance criteria before it is built, and the maintainers own those criteria.

- **Bug fixes and documentation** may come as a pull request directly. Describe how to reproduce the bug, or
  link an issue that does.
- **Features and changes of behavior start with an issue** (the feature request template). A maintainer answers
  with the proposed scenarios and acceptance criteria; open a pull request once you agree on them, and link the
  issue with `Closes #…`. A feature pull request without an agreed issue is put on hold until there is one.
- **Out of scope for an outside pull request:** the AI squad and agent instructions (`.squad/` except
  `stack.md` and `project.md`, `.claude/`, `AGENTS.md`, `CLAUDE.md`, `.github/copilot-instructions.md`), the
  text outside the `<!-- project:… -->` blocks of the template's marked files, and the workflows under
  `.github/workflows/`. They come from a shared template and are changed there; the *Fork scope* check fails
  on the first two. Open an issue instead.
- **Checks on a fork:** pull requests from forks run without repository secrets, so the analysis that needs one
  (e.g. SonarQube Cloud) does not run there; a maintainer runs it before merging. Run the quality gates below
  locally so the pull request arrives clean.
- Maintainers may push to your branch ("Allow edits by maintainers") or carry the change on in a branch of their
  own; you stay credited as co-author of the squashed commit.

## Quality gates

Code-style rules are documented in [`AGENTS.md`](../AGENTS.md) and in
[`.squad/stack.md`](../.squad/stack.md), and are binding for all contributions. Before opening a pull
request, run the commands from `stack.md`: *Format*, *Build*, the *Analyzer gate* (no analyzer diagnostic
of any severity in a changed file) and the *Coverage gate* (line coverage on new or changed production code
and overall, thresholds in [`UNIT_TESTS.md`](UNIT_TESTS.md#code-coverage)). A pull request is
expected to arrive clean (see the decision record on quality gates in [`decisions/`](decisions/README.md)).

<!-- project:begin releases -->
## Versioning and releases

A release is a GitHub release with a new `v<major>.<minor>.<patch>` tag on `main`, created on the release
page of the web interface; it publishes the agent binary and the backend Docker image. A release is always
created manually, and the only trigger is publishing such a release (`release: published`), which creates the
tag at the same time; merging a PR, pushing to `main`, pushing a tag without a release or a schedule never
publishes one. `release.yml` does not run on pull requests; `ci.yml` checks the release build there (*Release build check* below). The
workflow is `.github/workflows/release.yml`; the reasoning is in
[0037](decisions/0037-releases-version-tag-plain-tooling-and-attested-artifacts.md) and
[0041](decisions/0041-backend-image-chiseled-runtime-base-images-pinned-by-digest.md).

### Cutting a release

Before the release, check that the base image digests are current (*Base image digests* below): run the
*Base image digests* workflow (Actions, *Run workflow*) or check that the latest `Release build check` on
`main` has no stale-digest warning, and that no issue *Base image digests are stale* is open.
If the issue *SBOM generator pin is stale* is open, refresh the pin first (*SBOM generator* below); a stale pin
gives a less complete SBOM, it does not stop the release.

Give the release its entry in `CHANGELOG.md` in a pull request before the release: replace `unreleased` with the date of
the release (or add the section), and keep [`docs/INSTALL.md`](INSTALL.md) in step with what the release needs to be installed.

Then, on the repository page, *Releases → Draft a new release*:

1. *Choose a tag*: type the new tag, for example `v0.1.0`, and pick *Create new tag on publish*, with `main` as the target.
2. Title: the tag. Notes: *Generate release notes*, or leave them empty and the workflow generates them; the workflow adds
   the image digest at the end either way.
3. *Set as a pre-release* exactly for a pre-release tag (`vX.Y.Z-rc.N`); the workflow refuses a mismatch. Leave
   *Set as the latest release* as it is: the workflow sets it from the tags.
4. *Publish release*. The tag is created and the workflow starts (Actions, *Release*). The release has no assets until the
   workflow attaches `vandox-agent-linux-amd64`, `vandox-agent-install-<version>.tar.gz` and `SHA256SUMS` at the end; if the workflow fails, it turns the release
   back into a draft.

Do not *Save draft* to start a release: a draft creates no tag and starts nothing until it is published. Pre-release
tags look like `vX.Y.Z-rc.N`; the install bundle is packaged only for `vX.Y.Z` and `vX.Y.Z-rc.N`, so a release with any other pre-release suffix fails at the packaging step. Build metadata (`+...`) is not allowed.

### What the release workflow does

1. Checks that the tag is strict SemVer, that the run's ref is that tag, that the release's pre-release flag matches
   the tag, and that its commit is reachable from `origin/main`.
2. Runs `go tool govulncheck ./...` and `dotnet list package --vulnerable --include-transitive`; a finding stops
   the release.
3. Builds `vandox-agent` (Go, linux/amd64, static, `-trimpath`), packages the install bundle with `.github/scripts/package-agent.sh` and writes the two-line `SHA256SUMS` (bundle, binary), and builds the image from
   `deploy/backend/Dockerfile` (the backend, published by the .NET SDK) with `docker build --no-cache`. Nothing is restored from a CI cache.
   After the image is saved, `.github/scripts/generate-sbom.sh` generates SPDX 2.3 SBOMs for the binary and
   the image with a syft container pinned by digest (*SBOM generator* below), run without network and
   without access to `dist/`.
4. Verifies both binaries' `--version` output against the tag, the full commit SHA and the commit time, the
   checksum and the bundle (`.github/scripts/check-agent-bundle.sh`: ten files in eleven entries in a fixed order, a manifest, owner 0/0, fixed modes, `VERSION` equal to the tag, files equal to the tree, reproducible), that the binary is static, that every `FROM` uses a base image build argument pinned by a sha256 digest, that the builder and runtime tags
   name the .NET version of the target framework, and that the image runs as `65532:65532`. The SBOM script
   checks its own output (SPDX 2.3; for the agent the Go standard library and the main module, for the image
   NuGet packages; at most 16 MiB each).
5. Pushes exactly the verified image as `networlddev/vandox:X.Y.Z`, and as `latest` when the tag is the
   highest stable `v*.*.*` tag. A pre-release tag publishes only its own version. If the version already
   exists on Docker Hub, or the check cannot tell, the job fails before pushing: a published version is
   never overwritten.
6. Creates SLSA build provenance attestations and SBOM attestations (`sbom-path`, SPDX) for the binary and
   the install bundle and the image digest (GitHub artifact attestations, stored on GitHub, not in Docker Hub) in the `attest` job,
   which holds only `id-token: write` and `attestations: write`, has no environment and reads no secret, and
   verifies all of them with the README's flags, the SBOM ones with `--predicate-type
   https://spdx.dev/Document/v2.3` (the image by its published tag, see *Verifying a release*).
7. Completes the GitHub release, only after the attestations exist and verify: attaches `vandox-agent-linux-amd64`, the install bundle
   and `SHA256SUMS`, generates the notes if they are empty, appends the image digest, and marks the release as latest
   exactly when the tag is the highest stable `v*.*.*` tag.
8. If any job fails or the run is cancelled, turns the release back into a draft, so no published release lacks its
   assets. A release that is already complete from an earlier run (all three assets and the image digest in the notes)
   stays published (a republish or re-run of a
   complete release stops at the never-overwrite check).

### Verifying a release

The README (*Install*) has the commands: `gh attestation verify` for the binary and, by digest, for the
image (`oci://docker.io/networlddev/vandox@sha256:<digest>`, the digest from the release notes), followed by
pulling that same digest, and the same commands with `--predicate-type https://spdx.dev/Document/v2.3`
(and `--format json --jq` to save the SBOM) for the SBOM attestations. `--source-ref` pins the tag, `--signer-workflow` pins `release.yml`, and
`--deny-self-hosted-runners` requires a GitHub-hosted runner. The image is verified and pulled by digest
because a tag can be re-pointed (immutable tags are optional, see *One-time setup (maintainer)*, item 3). The
workflow's own check uses the tag on purpose, to catch a tag that does not resolve to the attested digest.
An attestation does not prove that the tagged commit is on `main`; the tag ruleset stays
that boundary.

### Base image digests

`deploy/backend/Dockerfile` names each base image in three build arguments: `BASE_<NAME>_IMAGE`,
`BASE_<NAME>_TAG` and `BASE_<NAME>_DIGEST`. `FROM` uses only the image and the digest. Dependabot cannot
read these lines, so the digests are refreshed by hand in a pull request: before every release tag, and
whenever a .NET patch release or a base image update appears. Read the multi-arch index digest of exactly the tag in
`BASE_<NAME>_TAG`, for example `docker buildx imagetools inspect mcr.microsoft.com/dotnet/sdk:10.0` (the `Digest:`
line), and write it to `BASE_<NAME>_DIGEST` in a pull request. A new .NET major version changes the
target framework in `Directory.Build.props`, `global.json`, `BASE_BUILD_TAG`, `BASE_RUNTIME_TAG` and both
digests together (`.github/scripts/check-builder-dotnet-version.sh` checks that they agree). The release build passes no
`BASE_*` build argument, so the pinned defaults are what it uses.

A weekly workflow, `.github/workflows/base-image-digests.yml` (Mondays, and on manual dispatch; it also checks the SBOM generator pin, see *SBOM generator*), compares
every `BASE_<NAME>_DIGEST` with the current index digest of its tag and opens or updates the issue *Base
image digests are stale*; the `Release build check` shows the same as a warning. The refresh pull request
should close that issue (`Closes #n`). A base image digest often moves without a version change
(Ubuntu package updates), so a stale report is routine; refresh at least before a release. The check can be run locally from the repository root with
`.github/scripts/check-base-image-digests.sh` (needs `docker buildx` and `jq`). If GitHub disables the
scheduled workflow after 60 days without repository activity, re-enable it under Actions. Record
[0041](decisions/0041-backend-image-chiseled-runtime-base-images-pinned-by-digest.md).

### SBOM generator

`.github/scripts/generate-sbom.sh` holds the only reference to the SBOM generator, the constant
`syft_image='ghcr.io/anchore/syft:vX.Y.Z@sha256:<index digest>'`; the script refuses any other form before it
calls Docker. Dependabot cannot read it, so it is refreshed by hand in a pull request, whose
`Release build check` proves the new pin works: take the newest syft release tag (never `latest`), read the
digest of exactly that tag with `docker buildx imagetools inspect ghcr.io/anchore/syft:vX.Y.Z` (the `Digest:`
line, the multi-arch index digest) and write both into the constant. `ghcr.io` is the only source. The script
runs the image without network, with a read-only root file system, no capabilities and the runner's UID, the
inputs mounted read-only and only a fresh empty directory writable, and prints the generator's output only
between `::stop-commands::` markers so it cannot issue workflow commands. Record
[0037](decisions/0037-releases-version-tag-plain-tooling-and-attested-artifacts.md).

A weekly job of the *Base image digests* workflow (`.github/workflows/base-image-digests.yml`, Mondays and on
manual dispatch) checks the pin. It reads the constant from the one line starting with `syft_image=` in the
literal form `syft_image='...'` and fails if there is no such line or more than one, so change the pin only on
that line and never reassign it elsewhere. The newest release is the highest `vX.Y.Z` tag of
`https://github.com/anchore/syft`, read with `git ls-remote` without credentials (pre-releases are ignored).
The job also compares the pinned tag's index digest on `ghcr.io` with the pinned digest; a newer release whose
image is not yet on `ghcr.io` is reported with `-` as its digest. It opens or updates the issue *SBOM generator
pin is stale*; the refresh pull request should close it (`Closes #n`). The check can be run locally from the
repository root with `.github/scripts/check-sbom-generator-pin.sh` (needs `git`, `docker buildx` and `jq`).

### Release build check on pull requests

`release.yml` runs only for a published release with a version tag. Pull requests are covered by the `Release build check` job in
`ci.yml`: it runs the base image pinning and builder .NET version checks (`.github/scripts/`), builds the
agent and the image with the version `v0.0.0-dryrun`, and verifies `--version`, the static binary and the
image user, and generates and checks the SBOMs from that agent binary and image (*SBOM generator*). It
also starts the image with `deploy/backend/docker-compose.yml`
(`.github/scripts/smoke-test-backend.sh`) and checks the health check, the port bindings, a graceful stop
and that the database survives re-creating the container (record
[0060](decisions/0060-compose-file-port-bindings-volumes-and-memory-limit.md)). It also
compares the base image digests with the registry (*Base image digests*); a stale
digest or a failed registry lookup is only a warning there, not a failure. It uploads, pushes and releases
nothing and reads no secret.

### One-time setup (maintainer)

1. **Tag ruleset.** Create a tag ruleset (Settings, Rules, Rulesets, new tag ruleset; the name does not matter) with the
   target `refs/tags/v*`, enforcement *Active*, the rules *Restrict creations*, *Restrict updates* and
   *Restrict deletions*, and *Repository admin* as the only bypass. A release runs the workflow file of
   the tagged commit, so whoever can create the tag controls what runs with the Docker Hub token. Publishing a
   release with a new tag on the web page creates the tag with the publisher's rights, so the ruleset is meant to
   apply there as well: only the admin can publish a release with a new tag.
   Check that once: create a temporary tag ruleset for `refs/tags/rulesetprobe-*` with *Restrict creations* and no
   bypass, try to publish a release with the new tag `rulesetprobe-1` on the web page (GitHub must refuse it), then
   delete the ruleset and anything the attempt created. Create the ruleset before storing the token.
2. **Environment.** Create the GitHub environment `release` with deployment branches and tags set to
   *Selected branches and tags*: the tag rule `v*.*.*` and no branch. Store the secret `DOCKERHUB_TOKEN`
   and the variable `DOCKERHUB_USERNAME` there. A required reviewer is optional; it is worth adding once
   more than one person has write access (0037).
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

A failed run turns the release back into a draft (job `withdraw-release`); the tag stays. A successful re-run
publishes it again with its assets.

If a job fails before the image is pushed, nothing was published: fix the cause and use "Re-run all jobs" for a
setting or a secret, or publish the draft again, which starts a new run. A fix to the release itself, such as the
pre-release flag, needs the new run: a re-run reuses the release as it was when first published. A fix in the code
needs a new patch version, because the tag cannot be moved (or, if the tag itself was wrong, ask the repository
admin to delete it together with the draft).

If only `github-release` failed, use "Re-run failed jobs". It reruns just that job and works while the run's
release artifact exists, that is 7 days after the run.

If `attest` failed, the image is already published and the release is a draft without assets. "Re-run failed jobs" reruns
`attest` and `github-release` within the 7-day artifact window; a second provenance or SBOM attestation for
the same digest is harmless. After that window, cut a new patch version. If the "Verify attestations" step reports a digest
mismatch (the version tag does not resolve to the attested digest), do not re-run it; investigate and cut a
new patch version.

If `publish-image` failed after `networlddev/vandox:<version>` was pushed (it failed while pushing `latest`
or reading the digest), a re-run stops at the never-overwrite check. Do not push by hand; cut a new patch
version. The orphaned version tag stays on Docker Hub; delete the draft release of the failed version. The same
applies once the 7-day window has passed.

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
