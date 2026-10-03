# 0027: Project name Vandox; images on Docker Hub as networlddev/vandox

- **Status:** Proposed
- **Date:** 2026-10-03
- **Source:** Issue #6
- **Supersedes:** —

## Context

The project needs a name for the repository, the binaries and the backend image, and a registry from
which the NAS pulls the image.

## Options considered

1. **GitHub Container Registry** — next to the code; a second account context on the NAS.
2. **Docker Hub under the existing `networlddev` organization** — the Synology container manager pulls
   from Docker Hub by default; the organization is already used (e.g. for SonarQube Cloud,
   `sonar-project.properties`).

## Decision

The project is named Vandox (binaries `vandox-agent` and `vandoxd`). The backend image is published on
Docker Hub as `networlddev/vandox`.

## Consequences

- The release workflow publishes to Docker Hub and needs Docker Hub credentials as CI secrets.
- Changing the registry later means a new record and new pull instructions for the NAS.
