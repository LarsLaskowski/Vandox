# 0158: Backup runs are a record kind of their own, recognized by process rules taken from a captured Plesk backup

- **Status:** Accepted
- **Date:** 2026-10-22
- **Area:** Agent services and backup runs
- **Source:** Issue #36, Issue #287
- **Supersedes:** —

## Context

#36 wants backup runs from configurable process patterns, so that a chain such as 2 Oct 2026 (a backup with `pzstd`, the kill of
`pzstd`, then of `mariadbd`) reads as one sequence. Plesk runs its backups as ordinary processes; there is no event to listen for. The
Product Manager set the default rule to `pzstd` only until Plesk's own processes were captured (#287). Wire format 1.1 was unreleased.

## Options considered

1. **Backup runs as a new record kind `backup_run`** (chosen) — a minor-version addition while 1.1 is unreleased (0043).
2. **As `kernel_event` types** — no new kind, but the kernel reports no backup.
3. **As `log_line` events** — not a log line.
4. **Default rules from a capture of a Plesk backup** (chosen, #287) — `pzstd` plus the command-line prefixes of the creating modes of
   Plesk's archiver and tar; a match on the program alone would also count a restore, so the prefixes name the creating modes.
5. **`pzstd` only** (the Product Manager's default until the capture) — the one backup program seen in real data before #287, misses
   the database dumps.
6. **Plesk's documented tool names** — never captured; a wrong rule fails silently.

## Decision

A backup run is the span in which a process matches a `backups.processes` rule, recorded as a `backup_run` start and end, and the
default rules are those of the processes captured during a Plesk backup. The rules are in
[Agent services and backup runs](../areas/agent-services.md) (*Backup runs*), the option in
[Configuration options](../areas/configuration-options.md) and the record in
[Wire format records](../areas/wire-format-records.md) (*`backup_run`*).

## Consequences

- The database dumps run in Plesk's archiver, so they are part of the run; the creating modes keep a restore out.
- Any local user can start a process that matches a rule and so make a backup run appear.
- A process the agent cannot read is skipped like an ended one, without a log line, so a backup process the agent cannot read is
  not recognized.
