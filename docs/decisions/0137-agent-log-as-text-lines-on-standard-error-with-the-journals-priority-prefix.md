# 0137: The agent logs text lines on standard error with the journal's priority prefix, cut below the journal's line limit

- **Status:** Accepted
- **Date:** 2026-10-17
- **Area:** Agent
- **Source:** Issue #30
- **Supersedes:** —

## Context

The issue asks for journal logging. The agent runs as a systemd service whose standard error goes to the journal, and its
log is read by an operator with `journalctl`. The log carries values the agent did not choose (error texts, names), so no
value may start or forge a line. The journal splits a line longer than its `LineMax=` and makes the rest an entry of its own.

## Options considered

1. **JSON lines** — structured, but hard to read in `journalctl`.
2. **The native journal socket** — real journal fields, but a second socket for fields nobody queries.
3. **Text lines on standard error with the syslog priority prefix that systemd reads, when the journal stream is standard
   error** (chosen) — readable, one stream, the priority kept.

How the journal stream is recognized: by the variable's presence — a redirected child inherits it; **by comparing the device
and inode that `JOURNAL_STREAM` names with standard error's, as systemd prescribes** (chosen). How lines stay whole: cutting
values alone — does not bound a line with many values; **cutting values and the whole line below the journal's limit**
(chosen).

## Decision

The agent writes `key=value` text lines to standard error, with the syslog priority prefix only when standard error is the
journal stream, and cuts values and lines so the journal never splits an entry. The rules are in the
[Agent](../areas/agent.md) area (*Logging*).

## Consequences

- A library behavior the design rests on was measured (Go 1.27) and holds: `slog`'s text format keeps every entry on one
  line whatever its values hold; a test pins it.
- Revisit if the agent needs structured journal fields (then the native journal protocol).
