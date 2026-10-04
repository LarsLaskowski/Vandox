# 0036: Dependabot watches /deploy/backend for Docker before the Dockerfile exists

- **Status:** Superseded by 0041
- **Date:** 2026-10-04
- **Source:** Issue #8
- **Supersedes:** —

## Context

Issue #8 requires a Dependabot configuration that covers `gomod`, `docker` and `github-actions`.
`.github/dependabot.yml` covers `gomod` and `github-actions` only. The repository has no Dockerfile yet:
`deploy/backend/` holds only `.gitkeep`, and issue #13 (backend skeleton and container) adds a multi-stage
Dockerfile with a pinned base image. The repository layout reserves `deploy/backend` for the container
files.

## Options considered

1. **Add the `docker` entry now, for `/deploy/backend`** — meets the issue; base-image updates start the
   moment #13 lands; until then Dependabot reports that it finds no Dockerfile in that directory (an entry
   in the Dependabot status, no pull request, no effect on CI).
2. **Add the `docker` entry with #13** — no Dependabot error in the meantime, but #8's acceptance criterion
   is not met, and #13 must remember to add it.
3. **Watch several directories (`/` and `/deploy/backend`)** — covers either Dockerfile location, but both
   report missing files until then, and one of them forever.

## Decision

Option 1: a `docker` entry with `directory: "/deploy/backend"`, weekly, at most 10 open pull requests,
grouped as `docker-images`, matching the style of the other two entries.

## Consequences

- Until #13 merges, Dependabot shows a "no Dockerfile found" error for this entry; this is accepted.
- The Dockerfile from #13 must live in `deploy/backend/`, or #13 moves the `directory` in
  `.github/dependabot.yml` in the same change.
- A `docker-compose` ecosystem entry for `deploy/backend/docker-compose.yml` is not part of this decision;
  it is decided with #13, which creates that file.
