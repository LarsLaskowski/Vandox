# 0053: Releases are always created manually; the trigger is a new `vX.Y.Z` tag

- **Status:** Accepted
- **Date:** 2026-10-05
- **Area:** —
- **Source:** Maintainer request
- **Supersedes:** —

## Context

The release workflow (`.github/workflows/release.yml`, record 0037) had two triggers: a version tag, and a
dry run of its `build` job on pull requests that change a release input. In a pull request's checks the
release jobs `publish-image` and `github-release` then appeared as skipped, which is easy to mistake for a
release step that could run on merge. Pull-request checks belong in `ci.yml`.

## Options considered

1. **Release automatically on merge to `main`** (for example from conventional commit messages) — no manual
   step, but every merge could publish an image, and the version would not be a conscious decision. The
   Docker Hub token would be reachable from branch runs (record 0039).
2. **Release only when a maintainer creates a version tag** — one explicit, auditable action, protected by
   the tag ruleset and the tag-only `release` environment.

## Decision

Option 2. A release is always created manually. The only trigger is creating and pushing a new tag of the
form `vMAJOR.MINOR.PATCH`, for example `v0.1.0` (pre-release: `v0.1.0-rc.1`).

- `release.yml` triggers only on `push` of tags `v*.*.*`. Merging a pull request, pushing to `main`, a
  schedule, a pull request or a manual workflow dispatch never starts it, and it must not get such a
  trigger.
- The pull-request dry run moves to `ci.yml` as the job `Release build check`. It builds the agent and the
  image with the version `v0.0.0-dryrun` and runs the same pinning and builder-Go-version checks, which
  live in `.github/scripts/` and are shared with `release.yml`. It uploads, pushes and releases nothing
  and reads no secret.
- A new release means a new tag. Published versions are never overwritten (record 0037).

This amends record 0037, Decision, *Trigger*: the dry run on pull requests is no longer part of
`release.yml`. The rest of 0037 stands.

## Consequences

- Release jobs no longer show up in pull-request checks, skipped or otherwise.
- A broken Dockerfile or base-image bump still fails in pull-request CI, now in `ci.yml`.
- The release `build` job additionally checks the tag's ancestry, runs `govulncheck` and builds cold; the
  CI check does not (`ci.yml` already runs `govulncheck` and may use caches), so a cache-related
  difference is only caught on the tag run.
- Adding any other release trigger needs a new record that supersedes this one.
