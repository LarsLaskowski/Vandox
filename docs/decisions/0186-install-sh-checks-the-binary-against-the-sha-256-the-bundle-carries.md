# 0186: install.sh checks the binary against the SHA-256 the bundle carries

- **Status:** Accepted
- **Date:** 2026-10-27
- **Area:** Agent installation
- **Source:** Issue #42
- **Supersedes:** —

## Context

The issue asks for a checksum check of the downloaded binary. The release attests the build provenance of its assets (0037), and the
install bundle (record 0131) is checked by the operator before extraction. A production server should need no further tools or
logins.

## Options considered

1. **Verify the build provenance with `gh attestation verify` on the server** — needs `gh` and a GitHub login on the server.
2. **A SHA-256 check of the binary by `install.sh` before it runs** (chosen, as the issue asks) — proves integrity, not authorship; the
   README shows the provenance check for operators who want it.
3. **Check against the release's `SHA256SUMS`, downloaded at install time** — an operator who verifies the bundle's attestation still
   installs a binary pinned only by a second, unattested download, and `install.sh` must parse a file of several lines.
4. **Check against the binary's SHA-256 inside the bundle** (chosen, Security review) — verifying the bundle, by `SHA256SUMS` or by its
   attestation, covers the binary too; `install.sh` keeps a one-line rule and downloads only the binary.

## Decision

Options 2 and 4: `install.sh` runs the binary only after its SHA-256 equals the pin in the bundle. The pin's form and the messages are
in [Agent installation](../areas/agent-installation.md).

## Consequences

- `SHA256SUMS` does not prove who built the binary or the bundle; `gh attestation verify` of the bundle covers the binary too,
  through the SHA-256 the bundle carries.
- `install.sh` no longer reads `SHA256SUMS`.
- `--from DIR` takes only the binary, for offline installs and CI, and is checked against the pin like a download.
