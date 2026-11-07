# 0190: The install scripts refuse a bundle another user could change and check its manifest before sourcing

- **Status:** Accepted
- **Date:** 2026-10-27
- **Area:** Agent installation
- **Source:** Issue #42
- **Supersedes:** —

## Context

The scripts of the install bundle (record 0131) run as root and source `common.sh` and read the unit and SQL files from the extracted
directory. The operator's check of the bundle before extraction is the trust boundary, but a bundle extracted somewhere shared could be
changed by another user afterwards, and an extraction that failed part-way (a write error on one member) can leave a complete script
beside a truncated `common.sh` or unit that is still valid. A truncated `common.sh` turns a missing guard function into a false
condition and so fails open.

## Options considered

1. **Rely on the documented extraction alone** — nothing on the server notices a bundle another user can change or an incomplete
   extraction.
2. **The scripts refuse a bundle that another user could change** (chosen, Security review) — before anything is sourced, the script,
   `common.sh`, the bundle directory and every directory above it are checked for owner, links and write bits, and the bundle's other
   files before they are read; a cheap backstop that narrows what another user can swap to the time between the check and the read.
3. **A manifest of the members' SHA-256, checked by both scripts before they source anything** (chosen) — proves the extraction is
   complete and unchanged; the scripts are not listed (a script cannot hash itself), and the member order puts `install.sh` last, so a
   truncated script never runs, as bash never reaches its last line.

## Decision

Options 2 and 3. The entry check, the manifest's form and their messages are in [Agent installation](../areas/agent-installation.md).

## Consequences

- The documented extraction as root in a new private directory stays the control; the check is a backstop.
- The manifest proves completeness, not authenticity, which stays with the operator's check of the bundle.
- The manifest is read with a bounded reader before anything is sourced; its size is taken with `wc -c`, which for a regular file
  never reads the whole file (measured by Security on a 2 GiB sparse file: 128 bytes read), and `stat`, `wc` and `sha256sum` come
  from the caller's `PATH`, as `main` has not set its own yet.
