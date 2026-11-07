# 0167: Live log lines are classified by a Go port of the import's stages in the agent

- **Status:** Accepted
- **Date:** 2026-10-31
- **Area:** Agent log shipping
- **Source:** Issue #290
- **Supersedes:** —

## Context

#290 gives live lines the import's three stages: the mail classification, MariaDB entries and kernel reports
([System and mail log parsers](../areas/log-parsers-system.md)). A live record must equal the import's record of the same line (spec of
#290). The stages hold lines for up to 60 seconds of capture time, and live input never ends. No held line may be lost silently, at an
agent crash or a backend crash. The filter keeps judging the mapped line. Storage deduplicates and finds sequence gaps by the agent's
sequence numbers (security area 1, record 0130).

## Options considered

1. **Classify at ingest, in the backend, with the C# stages** — one implementation, so parity holds by construction. But a MariaDB
   entry or kernel report merges several agent records, each with its own sequence number, into one stored record; the merged members
   would read as missing and every later record would be stored backfilled. The backend also acknowledges a batch before an entry it
   holds is complete, so it would have to persist the open entries in a new table to survive a crash.
2. **Classify each line at ingest without grouping** — no state, but it fails the spec's equality and cuts crash reports into single
   lines, which have no `mariadb.abort` message to match.
3. **Port the stages to Go and run them in the `logs` collector after the filter** (chosen) — each grouped record gets its own
   sequence number when it is sent, so storage and the timeline do not change; parity is bound by golden records that the C# import
   writes and the Go tests read (record 0164).
4. **One grouping state for the whole journal, in shipping order** — close to the time-merged export, but stages for every user ID
   would grow with the number of web-space users.
5. **Stages per seqnum ID, kept only while a group is open, with every entry of a user file given the mail classification alone**
   (chosen) — the rule decides by the file, not the ID, so it does not depend on how a systemd version assigns IDs; journald writes the
   MariaDB server's and the kernel's lines to the system files.
6. **Keep the MariaDB recovery state in the state file** — survives a restart in the middle of a recovery, but adds a field for a case
   that needs a restart and a crash recovery at once, and the spec accepts the loss.
7. **Expire a group 60 seconds after its first capture time, as the import counts** — a `syslog` line carries a time its writer may
   choose, and a group dated in the future would never expire. **The earlier of the first capture time and the start of the run that
   read it** (chosen), the rule of record 0166.

## Decision

The `logs` collector runs a Go port of the import's three stages after its filter, per journal seqnum ID and per configured `syslog`
file, gives entries of user journal files the mail classification alone, and dates a group by the earlier of its first capture time
and its run's start. When a group closes is in [Agent log shipping](../areas/agent-log-shipping.md).

## Consequences

- Live lines get the import's classification. This is the third port of an import grammar to Go; the C# and Go stages must change
  together, and the golden records of `testdata/journal/classify.export` and `testdata/logs/classify-syslog.log` fail when one changes
  alone.
- A live record differs from the import's at the edges the area names: when an entry closes, times capped at the run's start, user
  journal files not grouped, the recovery state lost at a restart. Its record identity orders it at its close, not at its header.
- Where the import skips a line it cannot date (an impossible or unstorable syslog time), the collector ships it raw, because live a
  skipped line would be a loss without a gap; live and imported records of such a line differ.
- Forged mail, MariaDB and kernel lines now make events live, not only on import; later alert rules (#56, #61) must weigh that
  ([Log signatures](../areas/log-signatures.md), *Limits and errors*).
- A MariaDB line now waits up to 60 seconds, plus the shipping delay, unless the next MariaDB line ends its entry earlier; that is well
  inside the live allowance of log lines (record 0130).
