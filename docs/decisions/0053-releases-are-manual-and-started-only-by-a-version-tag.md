# 0053: Releases are always created manually; the trigger is a new `vX.Y.Z` tag

- **Status:** Accepted
- **Date:** 2026-10-05
- **Source:** Maintainer request
- **Supersedes:** —

## Context

The release workflow (`.github/workflows/release.yml`, record 0037) has three jobs: `build`, `publish-image`
and `github-release`. On pull requests only `build` runs, as a dry run; `publish-image` and `github-release`
show up in the checks list as skipped because their `if:` requires a tag push. That is easy to mistake for
a release step that could run on merge.

## Options considered

1. **Release automatically on merge to `main`** (for example from conventional commit messages) — no manual
   step, but every merge could publish an image, and the version would not be a conscious decision. The
   Docker Hub token would be reachable from branch runs (record 0039).
2. **Release only when a maintainer creates a version tag** — one explicit, auditable action, protected by
   the tag ruleset and the tag-only `release` environment.

## Decision

Option 2. A release is always created manually. The only trigger is creating and pushing a new tag of the
form `vMAJOR.MINOR.PATCH`, for example `v0.1.0` (pre-release: `v0.1.0-rc.1`).

- Merging a pull request, pushing to `main`, a schedule or a manual workflow dispatch never publishes
  anything. `release.yml` has no `schedule` and no `workflow_dispatch` trigger and must not get one.
- `publish-image` and `github-release` run only for `push` events on tags `v*.*.*`. The `if:` conditions
  stay, so pull requests keep the dry run without any upload, push or release.
- A new release means a new tag. Published versions are never overwritten (record 0037).

## Consequences

- Skipped `publish-image` and `github-release` in a pull request's checks are expected.
- Adding any other release trigger needs a new record that supersedes this one.
