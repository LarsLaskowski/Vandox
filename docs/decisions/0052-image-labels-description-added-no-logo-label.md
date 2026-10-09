# 0052: Backend image gets a description label; no logo label

- **Status:** Accepted
- **Date:** 2026-10-05
- **Area:** —
- **Source:** Issue #12
- **Supersedes:** —

## Context

Issue #12 asks for OCI labels on the backend image — title, description, source and licenses — "plus the
logo URL if supported". `deploy/backend/Dockerfile` already set `org.opencontainers.image.title`,
`.source` and `.licenses` (and version, revision, created and the base-image labels from 0041);
`.description` was missing. The image is published on Docker Hub as `networlddev/vandox` (0027).

The OCI image specification's pre-defined annotation keys are `created`, `authors`, `url`,
`documentation`, `source`, `version`, `revision`, `vendor`, `licenses`, `ref.name`, `title`,
`description`, `base.digest` and `base.name`; none of them is a logo. Docker Hub shows a repository's
logo from its own settings, not from image labels.

## Options considered

1. **`io.artifacthub.package.logo-url`** — a key that Artifact Hub reads; the image is not listed on
   Artifact Hub, so nothing would display it.
2. **A self-invented key such as `org.opencontainers.image.logo`** — uses the reserved
   `org.opencontainers` namespace for a key the specification does not define; no tool reads it.
3. **No logo label** — the issue's "if supported" condition is not met; the logo stays reachable through
   `org.opencontainers.image.source` (the repository, whose README shows it).

## Decision

Option 3. The runtime stage adds the constant label
`org.opencontainers.image.description="Vandox backend: lean monitoring for a Plesk-managed Linux server that reconstructs outages from logs and metrics"`;
title, source and licenses stay as they were, and no logo label is set.

## Consequences

- `docker image inspect` and registries that show OCI labels display a description.
- If the image is ever listed on Artifact Hub, or a registry starts reading a logo label, a new record
  adds the matching key pointing to a file in `docs/assets/`.
