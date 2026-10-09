# 0037: Releases: version tag only, plain tooling, tag-only token environment, secret-free attestation job, digest-pinned SBOM generator

- **Status:** Accepted
- **Date:** 2026-10-04
- **Area:** —
- **Source:** Issues #9, #104 and #119, maintainer request
- **Supersedes:** —

## Context

A release is the agent binary (`vandox-agent-linux-amd64` with `SHA256SUMS`) as GitHub release assets and the
backend image `networlddev/vandox` (0027) on Docker Hub, with version, commit and build date injected. Consumers
must be able to verify who built them, and the project's pipeline rules apply: every action pinned by commit SHA,
every base image by digest (security area 13), no `${{ }}` inside `run:` scripts.

Facts that shape the design:

- A tag push runs the workflow file *of the tagged commit*, and an environment's deployment rule matches only the
  ref name. Whoever can create a `v*` tag controls what runs and what it can read.
- A Docker Hub personal access token cannot be limited to one repository. A plain repository secret is readable by
  any workflow run on any branch.
- The repository is public, so GitHub artifact attestations are available. A workflow that runs only on tags is
  first exercised by a real release, so a broken workflow must be caught earlier by a pull-request check.

## Options considered

Tooling and build:
- **GoReleaser** — rejected: a large third-party tool with release-wide permissions and a second configuration language.
- **Docker's official actions** — rejected: four or five more third-party actions holding the registry credential, and
  build and push in one step, so the exact verified image cannot be pushed.
- **Plain `go build`, the Docker CLI and `gh` in separate jobs** (`build` verifies with read-only permissions,
  `publish-image` pushes that exact saved image, `attest` signs, `github-release` publishes) — chosen.
- **Restored Go and Docker caches** — rejected: a tag run could restore a cache saved by a default-branch run after
  third-party code ran there; a poisoned object would be linked into the release. Cold builds are chosen.
- **Build date = build time** — rejected for the commit time of the tagged commit (rebuilds are reproducible); the
  full SHA is used instead of the short one.

Trigger:
- **Release automatically on merge** — rejected: every merge could publish, the version would not be a decision, and
  the token would be reachable from branch runs.
- **Only a new `vX.Y.Z` tag** — chosen. The pull-request dry run lives in `ci.yml` (`Release build check`), not in
  `release.yml`, so release jobs never show up as skipped in pull-request checks.

Docker Hub token:
- **Plain repository secret** — rejected (any branch workflow reads it). **Secret of an environment `release` with a
  deployment rule for tags `v*.*.*`** — chosen.
- **Token scope:** an organization access token limited to `networlddev/vandox`, or a dedicated user in a team with
  access to that repository only; the maintainer's account-wide token is rejected. The user name is an environment
  variable, not a secret (masking would hide it in the image name).
- **Who may create `v*` tags:** anyone with write access — rejected (the environment would protect nothing against a
  collaborator). A tag ruleset `release-tags` restricting creation, update and deletion to the repository admin —
  chosen. A required reviewer on the environment is optional and only worth it once a second person has write access.

Provenance and SBOM:
- **cosign** and **SLSA generator workflows** — rejected: one more tool or a third-party reusable workflow, and image
  signing would need the registry credential in the signing job.
- **GitHub artifact attestations with `actions/attest`** — chosen, in a **separate job `attest`** that holds only
  `id-token: write` and `attestations: write`, no environment, no secret, no checkout. Not in `build` (it runs
  dependency and base-image code), not in `publish-image` (token and signing permission in one job).
- **Attestations stored on GitHub only** — chosen over `push-to-registry`, which would put the token and the signing
  permission in one job. The workflow verifies its own attestations before the GitHub release exists; the documented
  consumer check names the image by digest, because a tag can be re-pointed between check and pull.
- **SBOM generator:** `anchore/sbom-action` (downloads an unpinned binary), BuildKit `--sbom` (does not survive the
  save/load path and covers only the image), a Go tool dependency in `go.mod` (joins the build list), a hand-written
  generator, and the syft release binary on the runner host — all rejected. **The syft container image pinned by index
  digest from `ghcr.io`**, run without network, capabilities or access to `dist/`, in a step of `build` — chosen. Not in
  `attest`: it would put third-party code next to the signing token.
- **SBOM format and publication:** SPDX 2.3 JSON (the version pins the predicate type); published only as signed
  attestations, not as an unsigned copy next to the signed one. A small content check runs before attesting. The syft
  pin is refreshed by hand, because Dependabot cannot read an image reference in a script.

## Decision

Releases are created only by pushing a new `vX.Y.Z` tag (`vX.Y.Z-rc.N` for a pre-release); nothing else triggers
`release.yml`. The workflow is built from plain tools in four jobs: `build` verifies everything cold with read-only
permissions (including a vulnerability scan of the Go and NuGet dependencies), `publish-image` pushes exactly the verified image and is the only job that declares the `release`
environment and sees the token, `attest` creates and verifies provenance and SBOM attestations for the binary and the
image digest, and `github-release` runs only after the attestations verified. A published version is never
overwritten. The token lives in the tag-only environment `release`, and the tag ruleset `release-tags` decides who can
start a release. The step-by-step behavior, the exact checks and the maintainer setup are in `release.yml`,
`.github/scripts/` and `docs/CONTRIBUTING.md` (*Versioning and releases*).

## Consequences

- The boundary for "published only from `main`" is the tag ruleset, not the in-workflow ancestry check. An attestation
  shows which workflow file at which tag built an artifact, not that the commit is on `main`. Without the ruleset every
  collaborator with write access could publish.
- Creating the ruleset, the environment and the token is a manual step that cannot be verified from the repository
  (`gh api repos/{owner}/{repo}/rulesets` shows the ruleset). A leaked token can push only to `networlddev/vandox`;
  immutable version tags on Docker Hub are optional and recommended.
- Provenance and SBOM are workflow-controlled content: an attacker who controls the `build` job can falsify the SBOM as
  well as the binary. The consumer check proves the pulled image only when the image is pulled by the verified digest.
- A newly published vulnerability that reaches the code stops a release until it is fixed. `govulncheck` does not flag a
  toolchain that is merely out of upstream support, so the Go toolchain is moved to a supported version before the first
  stable tag (0040).
- Every release and dry run builds cold, a few minutes slower. Version, commit and date are injected in two places
  (agent: build flags in the workflow; backend: build arguments in the Dockerfile); the exact `--version` checks catch
  drift.
- The attestation path is exercised only by a real tag; generation and the SBOM content check run in every pull request
  through `Release build check`. `ghcr.io` must be reachable for both.
- `publish-image` is not idempotent: if it fails after the version tag was pushed, a re-run stops at the never-overwrite
  check, and the recovery is a new patch version. The release artifact is kept for 7 days, so re-running `attest` or
  `github-release` works within that time; a second SBOM attestation for the same subject is harmless.
- The syft pin ages until refreshed by hand (a stale syft gives a less complete SBOM, not a weaker release). Follow-up:
  report it in the weekly base-image digest workflow.
- Release notes follow the PR titles. More platforms, `push-to-registry`, other release triggers, or an account-wide
  token (if Docker Hub offers neither organization tokens nor teams) each need a new record.
