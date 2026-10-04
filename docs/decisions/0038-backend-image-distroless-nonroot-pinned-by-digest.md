# 0038: Backend image on distroless static, non-root, base images pinned by digest; version tags without "v"

- **Status:** Proposed
- **Date:** 2026-10-04
- **Source:** Issue #9
- **Supersedes:** —

## Context

Issue #9 requires the backend image `networlddev/vandox` for linux/amd64, built on a distroless or
similarly minimal base, running as non-root, with the base image pinned by digest, and tagged with the
version and `latest`. The repository has no Dockerfile yet.

Issue #13 (backend skeleton and container) also asks for a multi-stage, minimal, non-root, pinned
Dockerfile and adds a `HEALTHCHECK`. Record 0036 points Dependabot's `docker` ecosystem at `/deploy/backend`
and says the Dockerfile must live there. `vandoxd` currently only prints its version or usage.

## Options considered

1. **Runtime base**
   - `gcr.io/distroless/static-debian13:nonroot`: no shell or package manager, CA certificates and
     `/etc/passwd` included, default user 65532. Requires a static (`CGO_ENABLED=0`) binary.
   - `scratch`: smaller, but no CA bundle and no user database.
   - `alpine`: has a shell and a package manager, a larger attack surface.

   Chosen: distroless static.
2. **When the Dockerfile is created**
   - *Now, in `deploy/backend/Dockerfile`*: the release has something to publish, and #13 extends the file.
   - *Wait for #13*: the release pipeline cannot be verified until then.

   Chosen: now.
3. **Version tag**
   - `X.Y.Z` without `v`: the Docker Hub convention, and what most images use.
   - `vX.Y.Z`: identical to the Git tag.

   Chosen: `X.Y.Z`. The binary's `--version` still prints the Git tag with `v`.
4. **When `latest` moves**
   - On every tag: a pre-release or a patch for an older line would move `latest` backwards.
   - Only for the highest stable tag: `latest` is always the newest stable release.

   Chosen: only for the highest stable tag.

## Decision

`deploy/backend/Dockerfile` is a multi-stage build with the repository root as context. Its build stage
is `golang:1.24-trixie@sha256:…`, following the `go` version in `go.mod`. It copies only `go.mod`,
`go.sum`, `cmd/` and `internal/`, never `COPY . .`, and `.dockerignore` is an allow-list. The build stage
builds `vandoxd` with the flags from record 0037. The runtime stage is
`gcr.io/distroless/static-debian13:nonroot@sha256:…` with `USER 65532:65532` and `ENTRYPOINT ["/vandoxd"]`.

Every `FROM` carries the multi-arch index digest after the tag, and the release workflow fails if one does
not. Dependabot (0036) updates the digests.

Image tags:

- a stable tag `vX.Y.Z` is published as `X.Y.Z`, and also as `latest` when it is the highest stable `v*.*.*`
  tag (`sort -V`)
- a pre-release `vX.Y.Z-…` is published only as `X.Y.Z-…`
- an existing version tag on Docker Hub is never overwritten

## Consequences

- Without a shell or `curl` in the image, #13's `HEALTHCHECK` must use an exec-form command built into
  `vandoxd`, for example a `healthcheck` subcommand that calls `/healthz`. #13 adds `EXPOSE`, `HEALTHCHECK`
  and the compose file to this Dockerfile.
- The backend must stay buildable with `CGO_ENABLED=0`. A SQLite driver that needs cgo (#14) would require
  a different runtime base (`distroless/base` or `cc`) and a record superseding this one.
- Volumes mounted into the container must be writable by UID/GID 65532. #13 documents this for the
  backend host.
- The builder follows `go.mod`'s Go version. Raising it means changing the builder tag and digest in the
  same change.
