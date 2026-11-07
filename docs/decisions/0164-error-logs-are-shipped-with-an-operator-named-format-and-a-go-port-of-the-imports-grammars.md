# 0164: Error logs are shipped with an operator-named format and a Go port of the import's grammars

- **Status:** Accepted
- **Date:** 2026-10-31
- **Area:** Agent log shipping
- **Source:** Issue #289
- **Supersedes:** —

## Context

#289 adds the Plesk panel log and the `sw-cp-server`, nginx and Apache error logs, which exist on the server only as files, so that the
web signatures (record 0094) read live lines. The import parses them (record 0092: three line grammars, raw records for unreadable
lines); the agent does not reuse the .NET parsers (0073). No line of the server's logs is available, so the formats stay as unverified
as record 0092 states. A live record should equal the import's record of the same line, so that live and imported data read alike.

## Options considered

1. **Detect the format by content, as the import does** — no new option values, but a file's first line would choose its source, and
   `sw-cp-server`'s log cannot be told from nginx's by content.
2. **The operator names the format per file** (chosen), as `syslog` already is.
3. **Put the grammars into the syslog line package, or into a package with a clock of its own** — the first mixes grammars under a
   package about syslog headers, the second holds the skipped- and repeated-hour rules twice. A package of its own that resolves its
   times through the syslog clock (chosen) holds them once.
4. **A raw line without a predecessor takes the file's modification time, as the import does** — live, the modification time is that
   of the file's newest line, not of the raw line; the file's last time (chosen) is the rule `syslog` raw lines already follow.
5. **Prove parity with hand-written Go expectations** — they would pass a Go port that misreads the grammar as the test's author did.
6. **Golden records written by the C# import and read by the Go tests** (chosen) — bind both languages, as the journal's
   `live-records.jsonl` does, without `log`, which differs by design (the configured path against the listed name).

## Decision

The configured files also take the formats `plesk-panel`, `sw-cp-server-error`, `nginx-error` and `apache-error`, named per file by
the operator and parsed by a Go port of the import's three grammars with the import's mapping and raw lines; parity is bound by golden
records the C# import writes. The rules are in [Agent log shipping](../areas/agent-log-shipping.md), the option in
[Configuration options](../areas/configuration-options.md) (`log_shipping`).

## Consequences

- The C# and Go grammars must change together: the golden records fail when one changes alone. This is the second port of an import
  grammar to Go, after the syslog line parser.
- Error log records equal the import's except `log` for a line of valid UTF-8 read after its predecessor in the same zone. They differ
  where the agent cannot know what the import knows (a raw line without a predecessor, the previous instant carried across a
  rotation, an invalid byte replaced one by one) and where a raw line's borrowed old time makes the record backfilled (record 0130);
  the area names each.
- MariaDB's and Dovecot's own files remain import-only.
