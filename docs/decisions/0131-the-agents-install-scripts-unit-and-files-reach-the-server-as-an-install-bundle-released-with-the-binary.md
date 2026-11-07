# 0131: The agent's install scripts, unit and files reach the server as an install bundle released with the binary

- **Status:** Accepted
- **Date:** 2026-10-27
- **Area:** Agent installation
- **Source:** Issue #42
- **Supersedes:** 0037 (amends it: a release also publishes the agent install bundle `vandox-agent-install-vX.Y.Z.tar.gz`, listed in `SHA256SUMS` beside the binary and attested for build provenance; 0037 is not edited)

## Context

v0.2.0 is the first release whose agent collects and sends, and nothing installed it yet (#42). The release workflow (0037) published
`vandox-agent-linux-amd64` with a one-line `SHA256SUMS`. The install and uninstall scripts need the unit, the MariaDB SQL files and an
example configuration on the server. On the pull request the Product Manager refused the first delivery of the scripts (a Git
checkout of the tag): a production server should not need `git` and a whole repository to install one agent.

## Options considered

1. **A Git checkout of the tag** (the first build of this record) — needs `git` and the whole repository on a production server; a
   checkout on another tag could only be warned about.
2. **An install bundle as a release asset** (chosen by the Product Manager on PR #324) — one `tar.gz` with the scripts, the unit, the
   SQL files, the example configuration, a `VERSION` file and the binary's SHA-256, built from the tagged tree by the release workflow
   and listed in the same `SHA256SUMS`; the server needs `curl`, `tar` and `sha256sum` only, and the version of the scripts and the
   binary is one value instead of an option that can disagree with the files.
3. **Each file as its own release asset**, fetched and checked by `install.sh` — more downloads and more code in the one script that
   runs as root, and the first download (`install.sh` itself) is still checked by hand.
4. **The files embedded in the agent binary** (`vandox-agent install`) — one download, but it changes the binary and adds a root code
   path to it.
5. **The binary inside the bundle** — a second copy of a large file per release, and the bundle would carry an artifact that is
   attested on its own; the script downloads the binary of the bundle's version instead.
6. **A `curl … | bash` one-liner** — runs bytes from the network before any check; never documented.
7. **`install.sh` checks its own checksum** — a script cannot vouch for the files it is part of; the check belongs before extraction.

## Decision

Option 2: a release publishes the install bundle beside the binary, both listed in `SHA256SUMS` and attested; `install.sh` takes its
version only from the bundle's `VERSION`, and the operator checks the bundle before extracting it. The bundle's content and the
documented steps are in [Agent installation](../areas/agent-installation.md) and `deploy/agent/README.md`.

## Consequences

- The operator's check of the bundle before extraction is the trust boundary for the scripts, the unit and the binary (records 0186,
  0190, 0191).
- The scripts, the unit and the binary come from one release; a run from a source checkout (no `VERSION`) is refused, and a developer
  builds a bundle with the release's packaging script.
- The release packages only the tag forms `install.sh` accepts: a release with another pre-release form fails instead of publishing a
  bundle that cannot install.
- The bundle is byte-for-byte reproducible from the tagged tree with the same `tar` and `gzip`, which the release checks by packaging
  twice; it is not reproducible across `gzip` versions.
- `SHA256SUMS` holds one line per asset and serves the operator's check of the bundle and anyone who downloads the binary by hand.
