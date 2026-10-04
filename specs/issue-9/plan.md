# Plan: Release pipeline for agent binary and Docker image

Source: Issue #9
Status: Draft
Tier: security — the change adds a GitHub Actions workflow that handles a registry credential, a Dockerfile and
build configuration, all of which `.squad/routing.md` places in tier `security`.

## Problem / root cause

The repository has no way to produce a release. `.github/workflows/` holds only `ci.yml` and `codeql.yml`.
Neither has a tag trigger. `deploy/backend/` and `deploy/agent/` contain only `.gitkeep`, so there is no
Dockerfile. `docs/CONTRIBUTING.md` (*Versioning and releases*) already promises that a
`v<major>.<minor>.<patch>` tag on `main` "publishes the agent binary and the backend Docker image". Today
nothing does that.

Claims of the issue, checked:

- *"Version, commit and build date injected via `-ldflags` (see internal/version)"* — **confirmed.**
  `internal/version/version.go` declares `Version = "dev"`, `Commit = "unknown"` and `Date = "unknown"` as
  package variables. `String(name)` renders `"<name> <Version> (commit <Commit>, built <Date>)"`. Both entry
  points print it through `internal/cli.Run` on `--version` (`internal/cli/cli.go`). The `-X` paths are
  `github.com/LarsLaskowski/Vandox/internal/version.{Version,Commit,Date}` (module path from `go.mod`).
- *"agent installed as a binary, backend as a Docker image on Docker Hub under `networlddev`"* —
  **confirmed** by `docs/ARCHITECTURE.md` (*Deployment*) and accepted record 0027 (`networlddev/vandox`).
  0027's consequence "the release workflow … needs Docker Hub credentials as CI secrets" is implemented here.
- *"Dependency #8 (CI workflows) is closed"* — **confirmed** by the orchestrator. Record 0036 (from #8)
  already points Dependabot's `docker` ecosystem at `/deploy/backend`. It says the Dockerfile "must live in
  `deploy/backend/`", so this change puts it there.
- *"Docker Hub token limited to pushing this repository"* — **cannot be confirmed from the code, and only
  partly possible as stated.** A Docker Hub *personal* access token's scope (Read / Read & Write /
  Read, Write & Delete) applies to every repository the user can reach. It cannot be limited to one
  repository. Only an **organization access token** can be limited to selected repositories with
  push/pull permissions. Whether one is available depends on the `networlddev` organization's Docker
  subscription. The maintainer has to check this (see *Maintainer actions*). The workflow is the same in
  every case.
- *"`vandoxd` image"*: `vandoxd` currently only prints its version or usage (`internal/cli`). The image built
  now is therefore a correct, minimal container whose entry point exits after printing usage. The serving
  backend, `HEALTHCHECK`, `EXPOSE` and compose file come with issue #13, which extends this Dockerfile
  (record 0038).

Related defects and observations found on the way:

- **Go 1.24 is out of upstream support.** `go.mod` declares `go 1.24`, and CI uses
  `go-version-file: go.mod`. Go 1.24 left support when Go 1.26 shipped in February 2026, so released
  binaries would be built with a toolchain that no longer gets security fixes. This plan does not change
  that, because it would be a toolchain bump touching all of CI. The builder image follows `go.mod`
  (`golang:1.24-…`). Proposed follow-up issue below.
- The `README.md` `-ldflags` example uses `git rev-parse --short HEAD` and the current time. The release
  uses the full commit SHA and the commit time (record 0037), so the example is aligned with that.

## Acceptance criteria

This change contains no Go code. Each criterion is checked by the workflow's own verification steps,
which run on every pull request that touches the release files (the *dry run*, including this PR's CI) and
on every tag. The criteria marked **(maintainer)** need the real tag and Docker Hub, and are confirmed by the
maintainer after merge.

- [ ] AC1 — Trigger: `.github/workflows/release.yml` runs on `push` of tags matching `v*.*.*`, and as a
  dry run on `pull_request` against `main` when one of its own inputs changes (paths listed under
  *Approach*, including `cmd/**` and `internal/**`, on which AC3's `--version` comparison depends). A tag
  that does not match the strict SemVer pattern
  `^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?$` (no build metadata `+…`) fails
  the first step before anything is built. A tag whose commit is not reachable from `origin/main` fails
  before anything is published. (This ancestry check guards against a maintainer's mistake. It is not a
  security boundary, because a tag push runs the workflow file of the tagged commit; who may create a
  release tag is enforced by the tag ruleset in AC12.)
- [ ] AC2 — Agent asset: the build produces `dist/vandox-agent-linux-amd64`, a statically linked
  (`CGO_ENABLED=0`) linux/amd64 ELF executable, and `dist/SHA256SUMS` in `sha256sum` format, holding
  exactly the line for that file. A verification step runs `sha256sum -c SHA256SUMS` in `dist/` and
  `file`, and checks that the binary is `ELF 64-bit` / `x86-64` / `statically linked`.
- [ ] AC3 — Version information matches the tag: a verification step runs `dist/vandox-agent-linux-amd64
  --version`. The image check runs `docker run --rm <local image> --version`. Each output is compared
  **exactly** with
  `vandox-agent <tag> (commit <full 40-char SHA of the tagged commit>, built <commit time, UTC, %Y-%m-%dT%H:%M:%SZ>)`
  (and the same with `vandoxd`). A mismatch fails the job. On a dry run `<tag>` is `v0.0.0-dryrun`.
- [ ] AC4 — Non-root image with pinned bases: `docker image inspect --format '{{.Config.User}}'` of the
  built image equals `65532:65532`. A verification step fails if any `FROM` line in
  `deploy/backend/Dockerfile` lacks an `@sha256:<64 hex>` digest. The final stage is
  `gcr.io/distroless/static-debian13:nonroot@sha256:…`. A further verification step fails unless the
  `<major>.<minor>` of the builder tag (`FROM golang:<major>.<minor>-…`) equals the `<major>.<minor>` of the
  `go` directive in `go.mod`, so `vandoxd` (Docker builder) and `vandox-agent` (`setup-go` from `go.mod`)
  are built with the same Go minor version. `.github/dependabot.yml` ignores minor and major version updates of
  `golang` (digest updates of the same tag continue).
- [ ] AC5 — Image tags: a stable tag `vX.Y.Z` publishes `networlddev/vandox:X.Y.Z`. It also publishes `latest`
  when `vX.Y.Z` is the highest stable (`-`-free) `v*.*.*` tag in the repository by `sort -V`. A
  pre-release tag (`vX.Y.Z-…`) publishes only `X.Y.Z-…`. The image pushed is the exact image that passed AC3
  and AC4: it is loaded from the build job's artifact and not rebuilt. The publish job fails before pushing if
  `networlddev/vandox:<version>` already exists on Docker Hub, so a published version is never overwritten.
  This check fails closed: the version counts as absent only when `docker manifest inspect` exits non-zero
  **and** its error output contains `no such manifest` or `manifest unknown`. Any other error (rate limit,
  5xx, network, missing pull rights) fails the job without pushing.
- [ ] AC6 — GitHub release: created for the tag with `--verify-tag`, title `<tag>`, notes generated by
  GitHub from the merged pull requests since the previous release (`--generate-notes`), plus a line naming
  the pushed image as `networlddev/vandox:<version>@sha256:<digest>`. The assets are
  `vandox-agent-linux-amd64` and `SHA256SUMS`. A pre-release tag is marked `--prerelease`, and
  `--latest=false` is set whenever the image did not get `latest`.
- [ ] AC7 — Credentials and least privilege: the workflow-level `permissions` is `{}`. Only the GitHub
  release job has `contents: write`. The Docker Hub token is read only in the publish-image job, from the
  secret `DOCKERHUB_TOKEN` of the GitHub environment `release`, whose deployment rule allows only tags
  `v*.*.*`. The user name comes from the environment variable `vars.DOCKERHUB_USERNAME` (not a secret, so
  the image name is not masked in logs). The token reaches `docker login` only through `--password-stdin`.
  Dry runs never read a secret. No `${{ … }}` expression from event data (tag name, PR fields) appears
  inside a `run:` script; event data enters scripts only through `env:`. Every action is pinned by full
  commit SHA with a version comment, as in `ci.yml`.
- [ ] AC8 — The dry run passes on this pull request (CI), covering AC1's pattern check on the dry-run
  version and AC2–AC4.
- [ ] AC9 **(maintainer)** — After merge, a test tag on `main` (recommended: `v0.0.1-rc.1`, a
  pre-release, so `latest` is not moved) creates a GitHub pre-release with both assets, and
  `networlddev/vandox:0.0.1-rc.1`. `docker run --rm networlddev/vandox:0.0.1-rc.1 --version` prints the tag,
  and `docker image inspect` shows user `65532:65532`.
- [ ] AC10 **(maintainer)** — The Docker Hub token is created and stored as described in
  `docs/CONTRIBUTING.md` (*Versioning and releases*), scoped as narrowly as the account allows (record 0039).
- [ ] AC11 — Documentation updated as listed under *Documentation updates*.
- [ ] AC12 **(maintainer)** — A repository ruleset `release-tags` (target *tags*, enforcement *Active*,
  pattern `refs/tags/v*`) has the rules *Restrict creations*, *Restrict updates* and *Restrict deletions*,
  with only the role *Repository admin* in the bypass list. The maintainer checks it with
  `gh api repos/LarsLaskowski/Vandox/rulesets`. As of 2026-10-04 that returns only `main-Protection`
  (target *branch*). The repository is public and owned by a user account, so tag rulesets are available
  and only the owner holds the admin role.

## Approach

### New files

**`deploy/backend/Dockerfile`** (multi-stage, build context = repository root):

- Stage `build`: `FROM golang:1.24-trixie@sha256:<index digest> AS build`. `WORKDIR /src`. `COPY go.mod go.sum ./`,
  `RUN go mod download`, then `COPY cmd/ cmd/` and `COPY internal/ internal/`. Copy only these, never
  `COPY . .` (Sonar hotspot S6470, and it keeps `specs/`, `.git` and docs out of the build). Declare
  `ARG VERSION=dev`, `ARG COMMIT=unknown`, `ARG DATE=unknown`. Build:
  `RUN CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -trimpath -buildvcs=false -ldflags "-s -w -X github.com/LarsLaskowski/Vandox/internal/version.Version=${VERSION} -X github.com/LarsLaskowski/Vandox/internal/version.Commit=${COMMIT} -X github.com/LarsLaskowski/Vandox/internal/version.Date=${DATE}" -o /out/vandoxd ./cmd/vandoxd`.
- Stage runtime: `FROM gcr.io/distroless/static-debian13:nonroot@sha256:<index digest>`, `COPY --from=build
  /out/vandoxd /vandoxd`, `USER 65532:65532` (numeric and explicit, though the base already defaults to it),
  `ENTRYPOINT ["/vandoxd"]`. OCI labels from the same args: `org.opencontainers.image.source`
  (`https://github.com/LarsLaskowski/Vandox`), `.version`, `.revision`, `.created`, `.title` (`vandox`),
  `.licenses` (from `LICENSE`). No `HEALTHCHECK`/`EXPOSE`, which come with #13.
- Digests: the Dev looks up the current multi-arch **index** digest of each tag at implementation time and
  writes it as `name:tag@sha256:…`, which Dependabot's `docker` entry (0036) can update. Lookup without a
  Docker daemon, as the Lead verified on 2026-10-04:
  - golang: `tok=$(curl -sS "https://auth.docker.io/token?service=registry.docker.io&scope=repository:library/golang:pull" | python3 -c 'import sys,json;print(json.load(sys.stdin)["token"])')`, then
    `curl -sSI -H "Authorization: Bearer $tok" -H 'Accept: application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.list.v2+json' https://registry-1.docker.io/v2/library/golang/manifests/1.24-trixie`, and read `docker-content-digest`
    (value on 2026-10-04: `sha256:5835f052b784aa39f2fe9070def3568605c8bc3fcd810f10402066348b61e716`).
  - distroless: the same with token `https://gcr.io/v2/token?scope=repository:distroless/static-debian13:pull` and
    `https://gcr.io/v2/distroless/static-debian13/manifests/nonroot`
    (value on 2026-10-04: `sha256:e2e927ec666bae08560abb3c55d0659eceabb657f56b6782ab500a9fc7f555e3`).

**`.dockerignore`** (repository root, the build context): ignore everything (`*`), then re-include
`go.mod`, `go.sum`, `cmd/`, `internal/` and `LICENSE`. Exclude `**/*_test.go` and `**/testdata`.

**`.github/workflows/release.yml`**:

- `name: Release`; `on: push: tags: ['v*.*.*']` and `pull_request: branches: [main], paths:
  ['.github/workflows/release.yml', 'deploy/backend/Dockerfile', '.dockerignore', 'go.mod', 'go.sum',
  'cmd/**', 'internal/**']`.
  `permissions: {}` at the top. `concurrency: { group: release-${{ github.ref }}, cancel-in-progress: false }`.
- Job **`build`** (`runs-on: ubuntu-latest`, `permissions: contents: read`, both events):
  1. `actions/checkout` (same pinned SHA as `ci.yml`) with `fetch-depth: 0`. The full history and tags
     are needed for the `latest` decision and the ancestry check.
  2. *Resolve version*: on a tag push, `TAG` comes from `env: TAG: ${{ github.ref_name }}`. On a pull request,
     `TAG=v0.0.0-dryrun`. Then run the SemVer regex check from AC1. Write to `$GITHUB_OUTPUT`:
     `tag`, `version` (the tag without `v`), `commit` (`git rev-parse HEAD`), `date`
     (`TZ=UTC git log -1 --format=%cd --date=format-local:%Y-%m-%dT%H:%M:%SZ`), `prerelease` (`true` if
     the tag contains `-`), and `latest`. `latest` is `true` only for a stable tag equal to
     `git tag -l 'v*.*.*' | grep -v -- - | sort -V | tail -n1`.
  3. *Check tag is on main* (tag push only): `git fetch --no-tags origin main` and
     `git merge-base --is-ancestor HEAD FETCH_HEAD`, or fail with an `::error::`.
  4. `actions/setup-go` (same pinned SHA as `ci.yml`, `go-version-file: go.mod`). Build the agent with
     `CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -trimpath -buildvcs=false -ldflags "-s -w -X …Version=$TAG -X …Commit=$COMMIT -X …Date=$DATE" -o dist/vandox-agent-linux-amd64 ./cmd/vandox-agent`,
     using the same flags as the Dockerfile. Then `(cd dist && sha256sum vandox-agent-linux-amd64 > SHA256SUMS)`.
  5. *Verify agent*: AC2 (`sha256sum -c`, `file` checks) and AC3 (exact `--version` comparison).
  6. *Check base image pinning*: AC4's grep over `FROM` lines. *Check builder Go version*: read the `go`
     directive from `go.mod` (`go mod edit -json`, field `Go`, or `awk '$1=="go"{print $2}' go.mod`), cut it to
     `<major>.<minor>`, extract `<major>.<minor>` from the `FROM golang:` line of the Dockerfile, and fail with an
     `::error::` naming both values if they differ or if either is missing.
  7. *Build image*: `docker build -f deploy/backend/Dockerfile --build-arg VERSION=… --build-arg
     COMMIT=… --build-arg DATE=… -t vandox:local .`, using the plain Docker CLI on the runner (no third-party
     action).
  8. *Verify image*: AC3 for `vandoxd` (`docker run --rm vandox:local --version`) and AC4 (`.Config.User`).
  9. Tag push only: `docker save vandox:local -o dist/vandox-image.tar`, then `actions/upload-artifact`
     (pinned by SHA) with name `release`, `dist/` content, `retention-days: 1`. Job `outputs`: `tag`,
     `version`, `prerelease`, `latest`.
- Job **`publish-image`** (`needs: build`; `if: github.event_name == 'push' && startsWith(github.ref,
  'refs/tags/v')`; `environment: release`; `permissions: {}`):
  `actions/download-artifact` (pinned) → `docker load -i vandox-image.tar` →
  `docker login docker.io -u "$DOCKERHUB_USERNAME" --password-stdin` (token from `env:` via
  `secrets.DOCKERHUB_TOKEN`). Run `docker manifest inspect networlddev/vandox:$VERSION`, capturing stderr.
  Exit 0 means the version exists, so the job fails. A non-zero exit with stderr matching `no such manifest` or
  `manifest unknown` means the version is absent, so the job continues. Any other non-zero exit fails the job
  and prints the error, so the check fails closed (AC5). The Lead confirmed the not-found text on
  2026-10-04: `docker manifest inspect networlddev/vandox:0.0.0-doesnotexist` printed
  `no such manifest: docker.io/networlddev/vandox:0.0.0-doesnotexist` with exit 1. Then `docker tag` and `docker push` `networlddev/vandox:$VERSION`,
  plus `:latest` when `latest == 'true'`. Output `digest` from `docker image inspect --format
  '{{index .RepoDigests 0}}'` (the `sha256:` part). Final step `docker logout docker.io` with `if: always()`.
- Job **`github-release`** (`needs: [build, publish-image]`, same `if`, `permissions: contents: write`):
  `actions/download-artifact`, then `gh release create "$TAG" --verify-tag --title "$TAG" --generate-notes
  --notes "Docker image: networlddev/vandox:$VERSION@$DIGEST" [--prerelease] [--latest=false]
  vandox-agent-linux-amd64 SHA256SUMS` with `GH_TOKEN: ${{ github.token }}`. It uploads only those two
  files, not the image tar. (`--notes` and `--generate-notes` together append the generated notes to the
  given text. The Dev confirms this with `gh release create --help` on the runner's `gh`. If they do not
  combine, the Dev writes the generated notes with `gh api repos/{owner}/{repo}/releases/generate-notes` and
  passes the combined text with `--notes-file`.)
- Order: verify everything, then push the image, then create the release. A failure before the push publishes
  nothing. If the release job fails after the push, "Re-run failed jobs" reruns only `github-release`.

### Why this shape

Records 0037 to 0039 explain it: plain `go build`, the Docker CLI and `gh` instead of GoReleaser or the
`docker/*` actions; the image is verified once and pushed as built; the token lives in a tag-only
environment.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| repo | `.github/workflows/release.yml` | new — release workflow (build, verify, publish image, GitHub release, PR dry run) |
| backend | `deploy/backend/Dockerfile` | new — multi-stage build of `vandoxd`, distroless static `nonroot`, digest-pinned |
| backend | `deploy/backend/.gitkeep` | delete (directory now has content) |
| repo | `.dockerignore` | new — allow-list build context |
| repo | `.github/dependabot.yml` | change — in the `docker` entry, add `ignore: [{ dependency-name: "golang", update-types: ["version-update:semver-major", "version-update:semver-minor"] }]`; everything else unchanged |
| docs | `README.md`, `docs/CONTRIBUTING.md`, `docs/ARCHITECTURE.md`, `SECURITY.md`, `.squad/project.md` | see *Documentation updates* |

No Go file changes. `internal/version` is used as it is.

## Signatures (for the Dev's skeleton)

None. No Go member is added or changed. Step 4 (*Skeleton*) does not apply.

## Test files

None. The change has no Go code, so there is no unit-testable unit, and `docs/UNIT_TESTS.md`'s colocated
`_test.go` rule has nothing to attach to. Step 5 (*Tests first*) does not apply. AC1–AC8 are enforced by
the workflow's verification steps and are exercised by the dry-run job in this PR's CI. That is the
"failing test first" equivalent here: the Tester, or the Dev if the orchestrator assigns it, confirms each
check fails when its condition is broken. Examples: a `FROM` without a digest, a missing `USER`, a wrong
`-X` path, a builder tag `golang:1.26-…` against `go 1.24`, or a `docker manifest inspect` error other than
not-found (for example, simulated with a stub `docker` script on `PATH` that prints `toomanyrequests` and
exits 1). Confirm this by running the workflow's check commands locally in `bash` inside a scratch `git worktree`,
never in the working tree. The *Coverage gate* is unaffected (no Go change), and overall coverage stays
as on `main`.

Existing test code that calls a changed signature: none.

Local verification available to the squad: the image cannot be built here, because the session has a Docker
client but no daemon. `actionlint` and `hadolint` are not installed. The Dev may run them through
`go run github.com/rhysd/actionlint/cmd/actionlint@<pinned version>` in a scratch directory, without adding
a dependency to `go.mod`. The binary part of the workflow can be run locally (`go build` with the
ldflags, `sha256sum`, `--version` comparison). The dry run in PR CI is the authoritative check of the
Docker steps.

## Documentation updates

Made by the Dev, inside the `<!-- project:… -->` blocks where the file has them:

- **`README.md`**: new section *Install* (or *Releases*) with these items:
  - downloading `vandox-agent-linux-amd64` and `SHA256SUMS` from the GitHub release
  - verifying with `sha256sum -c SHA256SUMS`
  - installing with `install -m 0755` (the full agent installation is #42)
  - pulling `networlddev/vandox:<X.Y.Z>`, or by digest from the release notes
  - the tag scheme (`X.Y.Z`, `latest` = highest stable release, pre-releases only by their own tag)
  - the image running as UID/GID 65532

  Align the `-ldflags` example with the release (full `git rev-parse HEAD`, commit time in UTC, `-trimpath`,
  `CGO_ENABLED=0`). The table of binaries stays.
- **`docs/CONTRIBUTING.md`**, project block `releases`, covering these topics:
  - **Cutting a release:** tag on `main`, `git tag -a vX.Y.Z` / `git push origin vX.Y.Z`. Pre-release tags
    look like `vX.Y.Z-rc.N`.
  - **What the release workflow does:** the checks it runs and what it publishes, including the
    never-overwrite rule and how `latest` is chosen.
  - **The dry run on pull requests.**
  - **One-time setup:**
    - the tag ruleset `release-tags` from AC12 (only *Repository admin* may create, update or delete `v*`
      tags), and why it matters: a tag push runs the workflow file of the tagged commit, so the person who
      creates the tag controls what runs with the Docker Hub token
    - the GitHub environment `release` with a deployment rule for tags `v*.*.*`, the secret
      `DOCKERHUB_TOKEN` and the variable `DOCKERHUB_USERNAME`
    - optional: a required reviewer on the environment once the repository has more than one person with
      write access (record 0039)
  - **How to create the Docker Hub token (record 0039):** an organization access token limited to the
    repository `networlddev/vandox` with push and pull, and `DOCKERHUB_USERNAME` = `networlddev`. If the
    plan offers no organization access tokens, the fallback is a dedicated Docker Hub user that is a member of
    a team with *Read & Write* on `networlddev/vandox` only, plus a personal access token of that user with
    scope *Read & Write*. A personal access token of the maintainer's own account is not acceptable,
    because it is not repository-scoped. The guide also covers rotating the token.
  - **Re-running a failed release.**
- **`docs/ARCHITECTURE.md`**, project block, section *Deployment*:
  - the agent binary and `SHA256SUMS` are release assets
  - the image `networlddev/vandox` is built from `deploy/backend/Dockerfile` on a distroless static base
    pinned by digest and runs as UID 65532
  - releases are built by `.github/workflows/release.yml` only from SemVer tags on `main`. Only the repository
    admin may create these tags (tag ruleset), and the workflow checks that the tagged commit is on `main`.
  - links to 0037, 0038 and 0039

  In *Security model*, add one sentence on the release credential (0039) and cite it in that section's
  records list.
- **`.squad/project.md`**, *Security areas*: add **13. Release pipeline and published artifacts**
  (`.github/workflows/release.yml`, `deploy/backend/Dockerfile`, `.dockerignore`, the `release`
  environment). *Goal:*
  - artifacts are published only from a SemVer tag that only the repository admin can create (tag ruleset
    `release-tags`), and whose commit the workflow checks is on `main`. The ancestry check runs in code the
    tagger controls, so the ruleset is the boundary.
  - every action is pinned by commit SHA, and every base image by digest
  - the registry token is readable only by the tag-triggered publish job, enters `docker login` only via
    stdin, and is limited to pushing `networlddev/vandox`
  - event data reaches scripts only through `env:`
  - the published image runs as a non-root user
  - a published version tag is never overwritten
  - every published binary has a checksum in `SHA256SUMS`

  Records 0027, 0037, 0038, 0039. This product PR updates `.squad/project.md` because the change adds an
  attack surface the file does not yet list (`.squad/routing.md`, *Scope of a product PR*).
- **`SECURITY.md`**, *Scope*: add "the release pipeline and published artifacts (agent binary, checksums,
  Docker image)" to the in-scope list, so it stays in sync with the *Security areas*.
- `.squad/stack.md`: no change. No command or gate the squad runs changes. The `-ldflags` statement under
  *Writing code* stays true.

## Architecture check

- The guarantees in `.squad/project.md` (no silent data gaps, a hanging collector never blocks, backfill never
  alerts) concern runtime behavior. This change does not touch them.
- 0027 (Docker Hub, `networlddev/vandox`): implemented as decided.
- 0036 (Dependabot `docker` at `/deploy/backend`): the Dockerfile lands in that directory, so Dependabot
  starts updating both pinned digests. The new `ignore` rule for `golang` minor and major updates narrows
  that entry but does not contradict 0036: the entry, its directory and its schedule stay. Record 0038
  explains the reason.
- 0001 / 0035 (gates local, CI as system of record): unchanged. The release workflow adds no coverage or
  analyzer gate.
- 0032 (secrets only from environment or Docker secrets) concerns the product's runtime secrets. The CI
  credential is a GitHub environment secret and follows the same spirit: it is never in a file or on a
  command line, and it goes to `--password-stdin`.
- `docs/ARCHITECTURE.md` *Deployment* gains the release flow (see *Documentation updates*). No existing
  guarantee is weakened.
- Issue #13 builds on this Dockerfile: it adds the serving entry point, `EXPOSE`, `HEALTHCHECK` and the
  compose file. Because the runtime image has no shell or `curl`, #13's `HEALTHCHECK` needs an exec-form
  check built into `vandoxd`, for example a `healthcheck` subcommand, as recorded in 0038's consequences.
  SQLite (#14) must stay compatible with `CGO_ENABLED=0` and a static base, or supersede 0038.

## Security considerations

- **Script injection:** `github.ref_name` and other event data go into scripts only through `env:`. The tag
  is validated against the strict SemVer regex before any use. The PR dry run uses a constant version.
- **Least privilege:** the workflow-level `permissions` is `{}`. Only `build` has `contents: read`, and only
  `github-release` has `contents: write`. `publish-image` has no `GITHUB_TOKEN` permission at all.
- **Secret exposure:** the token is only available in the `release` environment (tag rule `v*.*.*`). A
  workflow on a branch or a pull request, including Dependabot's and fork PRs, cannot read it. Only one job
  reads it, and the token goes to `docker login --password-stdin`. `docker logout` runs at the end of that
  job (`if: always()`); the runner is ephemeral.
- **Who can trigger a release:** a tag push runs `release.yml` *as it is in the tagged commit*, and the
  environment's deployment rule matches only the tag name. So anyone who can create a `v*.*.*` tag could
  tag a side-branch commit with a modified workflow, and that workflow would get the token. The tag ruleset
  `release-tags` (AC12) limits creating, moving and deleting `v*` tags to the repository admin, who already
  controls the token. The in-workflow ancestry check catches only mistakes. The residual risk is recorded in
  0039: until the ruleset exists, every collaborator with write access can trigger a release.
- **Integrity:** the release is built only from tags on `main`. The image pushed is the one verified, never
  rebuilt. Actions are pinned by SHA and base images by digest, with Dependabot updating both. Binaries
  are `-trimpath`, have VCS stamping off, and use commit-time dates, so they can be rebuilt bit for bit
  from the tag and checked against `SHA256SUMS`. Published version tags are never overwritten.
- **Runtime:** the image is distroless static with no shell or package manager, runs as UID/GID 65532, and
  the binary is static.
- **Not covered:** artifact signing and provenance (cosign, GitHub artifact attestations, SBOM). They would
  strengthen "verifiable" beyond checksums and digests, but the issue does not require them; see
  *Follow-ups*.

## Decision records

- `docs/decisions/0037-release-workflow-with-plain-go-docker-and-gh.md` (Proposed)
- `docs/decisions/0038-backend-image-distroless-nonroot-pinned-by-digest.md` (Proposed)
- `docs/decisions/0039-docker-hub-token-in-a-tag-only-environment.md` (Proposed)

## Maintainer actions (outside the repository; flagged to the Product Manager)

1. Create the tag ruleset `release-tags` (AC12) **before** storing the token. Go to *Settings → Rules →
   Rulesets → New tag ruleset*. Set the target `refs/tags/v*` and the enforcement *Active*. Enable *Restrict
   creations*, *Restrict updates* and *Restrict deletions*, and add the bypass *Repository admin* with mode
   *Always*.
2. Create the GitHub environment `release`. Under *Deployment branches and tags* choose *Selected branches
   and tags*, with the tag rule `v*.*.*` and no branch. A required reviewer is optional, and recommended
   only once a second person has write access (record 0039).
3. Create the Docker Hub token (record 0039; steps in `docs/CONTRIBUTING.md`). Store it as the environment
   secret `DOCKERHUB_TOKEN`, and the login name as the environment variable `DOCKERHUB_USERNAME`. Make sure the
   repository `networlddev/vandox` exists. If the subscription offers neither an organization access token
   nor teams, AC10 ("token scoped to push for this repository only") cannot be met. The maintainer then has
   to decide whether to accept an account-wide *Read & Write* token as a recorded residual.
4. After merge, push a test tag on `main` (recommended `v0.0.1-rc.1`) and check AC9. Removing the test
   release, tag or image tag afterwards is the maintainer's call. Deleting tags or releases needs explicit
   approval under the golden rules.

## Out of scope / follow-ups

Proposed follow-up issues for the orchestrator to create:

- **"[Repo] Move to a supported Go toolchain"**: `go.mod` declares `go 1.24`, which is out of upstream
  support since February 2026. CI (`go-version-file: go.mod`) and the release builder image
  (`golang:1.24-trixie`) build with it, so released binaries miss Go security fixes. The issue would raise
  `go` in `go.mod`, the builder image tag and digest (by hand: `.github/dependabot.yml` ignores minor updates
  of `golang`, and the release workflow requires the builder minor version to match `go.mod`), and
  `.squad/stack.md` *Toolchain*.
- **"[CI] Sign release artifacts and publish provenance"**: add GitHub artifact attestations (or cosign) for
  `vandox-agent-linux-amd64` and the image `networlddev/vandox`, and optionally an SBOM. Document
  verification with `gh attestation verify`.

Not in this change:

- further platforms (arm64)
- the serving backend, `HEALTHCHECK` and compose file (#13)
- agent installation files (#42)
- the Docker Hub repository description

## Challenge

Devil's Advocate, 2026-10-04: 1 major, 3 minor objections. All four are accepted. Scope and tier are unchanged
(`security`).

1. **major — the "only from a tag on `main`" guarantee is enforced by code the tagger controls. Accepted.**
   The objection is correct. On a tag push, GitHub runs `release.yml` as it is in the tagged commit. The
   environment's deployment rule matches only the tag name. So anyone with write access could push a
   modified workflow on a side branch, tag it `v9.9.9` and get the token.
   - Revised:
     - new maintainer criterion AC12 and maintainer action 1: a tag ruleset `release-tags` on
       `refs/tags/v*` that restricts creation, update and deletion to *Repository admin*. The repository is
       public and user-owned, so the ruleset is available, and the admin is the owner, who controls the token
       anyway. Today only the branch ruleset `main-Protection` exists (`gh api …/rulesets`).
     - the token is stored only after the ruleset exists.
     - AC1 and *Security considerations* now say that the ancestry check catches mistakes and is not a
       security boundary.
     - the `.squad/project.md` goal and the `docs/ARCHITECTURE.md` wording name the ruleset as the boundary.
     - 0039 records the threat, the ruleset and the residual risk.
   - Not adopted: a required reviewer on environment `release` is documented as optional, not required. With
     a single maintainer it adds a manual approval of their own tag and no protection. Issue #9 asks for a
     release "without manual steps". 0039 recommends it once a second person has write access.
2. **minor — Dependabot will bump the `golang` builder tag away from `go.mod`. Accepted.** The repository's
   `.github/dependabot.yml` `docker` entry has no `ignore`, and Dependabot proposes newer version tags with
   the same suffix (`1.24-trixie` → `1.26-trixie`).
   - Revised:
     - an `ignore` rule for `golang` with `version-update:semver-major` and `version-update:semver-minor`.
       Digest updates of `1.24-trixie` continue.
     - a workflow check (AC4, build step 6) that fails when the builder's `<major>.<minor>` differs from
       `go.mod`'s. It also catches a hand edit. Because the dry run now also runs on Dockerfile changes, it
       catches a Dependabot PR if the ignore rule ever behaves differently than expected.
   - The single-stage alternative (build `vandoxd` on the runner, `COPY` into distroless) is rejected and
     recorded in 0038. Issue #13 asks for a multi-stage Dockerfile. A self-contained build lets anyone
     rebuild the image from the tag alone. The matching check gives the same toolchain guarantee at minor
     level. A patch-level difference between `setup-go`'s latest 1.24.x and the pinned builder digest
     remains, and is accepted in 0038.
3. **minor — the dry-run `paths` miss `cmd/**` and `internal/**`. Accepted.** Both are added (AC1, *Approach*,
   0037). Dropping the filter was not chosen: documentation-only PRs would then build an image for nothing.
4. **minor — the never-overwrite check fails open. Accepted.** The check now treats the version as absent
   only when the error output says `no such manifest` or `manifest unknown`. Any other error fails the job
   before the push (AC5, `publish-image`, 0038). The Lead confirmed the not-found message locally on
   2026-10-04, without a daemon.
