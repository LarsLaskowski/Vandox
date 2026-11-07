# 0191: The documented install steps are single commands with check and extraction in one line

- **Status:** Accepted
- **Date:** 2026-10-27
- **Area:** Agent installation
- **Source:** Issue #42
- **Supersedes:** —

## Context

The operator downloads the install bundle and `SHA256SUMS` and checks the bundle before extracting it (record 0131); that check is the
trust boundary for everything the scripts install. Operators paste the documented commands into a console, line by line or as a block,
and many consoles paste a long nested block badly.

## Options considered

1. **Download, check, extract and run as separate lines** — pasted line by line, a failed check does not stop `tar` and `install.sh`.
2. **One command chained with `&&`, as root in a new `mktemp -d` directory** (Security review, then refused by the Product Manager) —
   fails closed, but a nested block of seven lines is hard to read.
3. **Short single commands, with check and extraction as one line** (chosen) — each line is one step with one sentence of explanation,
   and the sequence fails closed however the lines are pasted: `tar` runs only in the same line after a passed check; everything
   happens in a new `mktemp -d` directory; and the script is run by its full path there, so a failed step, a failed `mktemp` or
   variables lost in a new shell leave nothing to run.

## Decision

Option 3; the bundle's line of `SHA256SUMS` is selected by an exact comparison of the name field, not a pattern, because
`sha256sum --strict` alone would still take a line with one space, a tab or leading blanks. The commands are in
`deploy/agent/README.md` (*Install the agent*) and [Agent installation](../areas/agent-installation.md).

## Consequences

- Every document shows the steps as single commands, as root in a new private directory, never a pipe into a shell.
- A repeated check in the same directory after an earlier successful extraction leaves that earlier, checked extraction in place; the
  documents say to start each attempt in a new directory.
- Because the run line follows whatever `tar` did, an extraction that failed part-way needs the scripts' own manifest check
  (record 0190).
- `tar` restores the verified archive's owner (root) and modes, so it needs no extra flags.
