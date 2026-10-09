# 0052: Backend image gets a description label; no logo label

- **Status:** Accepted
- **Date:** 2026-10-05
- **Area:** —
- **Source:** Issue #12
- **Supersedes:** —

## Context

Issue #12 asks for OCI labels on the backend image (title, description, source, licenses) "plus the logo URL if supported".
The Dockerfile already set title, source, licenses, version, revision, created and the base-image labels; the description
was missing. The OCI specification defines no logo annotation, and Docker Hub takes a repository's logo from its own
settings, not from image labels.

## Options considered

- `io.artifacthub.package.logo-url` — rejected: only Artifact Hub reads it, and the image is not listed there.
- A self-invented `org.opencontainers.image.logo` — rejected: uses the reserved namespace for a key the specification does
  not define; no tool reads it.
- **No logo label** — chosen: the logo stays reachable through `org.opencontainers.image.source` (the repository README).

## Decision

Option 3: the runtime stage adds the constant label `org.opencontainers.image.description` (a one-sentence product
description); title, source and licenses stay as they were, and no logo label is set.

## Consequences

- `docker image inspect` and registries that show OCI labels display a description.
- If the image is ever listed on Artifact Hub, or a registry starts reading a logo label, a new record
  adds the matching key pointing to a file in `docs/assets/`.
