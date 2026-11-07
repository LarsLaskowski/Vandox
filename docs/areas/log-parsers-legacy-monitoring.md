# Legacy monitoring log parser

## Scope

How the import reads the log of the shell script that monitored the server before the agent existed (parser
`legacy-monitor`): snapshots of `top`, `meminfo`, `pressure`, `vmstat` and `lsof` output become metrics, process
snapshots and connection snapshots, so the outages before the agent can be analysed like later ones. The rules shared
by all parsers are in the [Log import](log-import.md) parser contract.

## Source

- The script appends a snapshot of the monitored server every five minutes (later every minute): a header line with
  the output of `date`, the output of `top -b -n 1` and, in the **new format**, the sections `meminfo`, `pressure`,
  `vmstat` and `lsof`. The **old format** has the `top` output only.
- Both formats were built from the description in issue #20 and tested with anonymized fixtures
  (`testdata/logs/legacy-monitor-old.log`, `legacy-monitor-new.log`), not with the real file: the exact line shapes of
  `top` (the memory suffixes of the process table, that a command line cut by `-w 512` ends in `+`, that the number of
  rows equals `Tasks: N total`) and of the `pressure` lines are not verified, which is why a single bad row does not
  skip a snapshot (below).
- The parser is line-framed.

## Recognition

The parser claims a file by its first line ([Log import](log-import.md), *Claim by the first line*): a content match
when its name is no syslog name and the first line of the head is a valid header. An empty head, a first line that is
no header and a header on line 2 are not claimed. Any process that can write the file can therefore make it a legacy
log by its first line (*Forged lines*).

## Snapshots and header

- A line beginning with `===== ` is a header line: it ends the open snapshot and opens the next.
- A valid header is `===== ` + weekday (`Mon` to `Sun`) + space + month (`Jan` to `Dec`) + space + two characters of
  day (a space or a digit, then a digit) + space + `HH:MM:SS` + space + a zone of 1 to 16 characters of
  `A-Z a-z 0-9 + -` + space + a four-digit year + ` =====`, ASCII and case-sensitive, nothing before or after (the
  weekday is not compared with the date). A header line that is not valid gives the skip "invalid snapshot header" at
  its line.
- Lines before the first header line are skipped one by one as "line before the first snapshot".

## Time

- The header is the server's local time. It is read in `import.time_zone` with the offset of the zone interval **whose
  name equals the header's zone** (ordinal comparison; the time zone database names the intervals of `Europe/Berlin`
  `CEST` and `CET`, of `Etc/UTC` `UTC`, of `Asia/Istanbul` `+03`): in the repeated hour at the end of daylight saving
  time the abbreviation tells the two passes apart, and when both intervals carry the name, the earlier offset is
  taken.
- A local time in the skipped hour, a day that does not exist, hour 24, minute or second 60 give the skip "invalid
  date"; a year outside 1677 to 2262 or an instant outside the storable range gives "time outside the storable range"
  (the year is checked as a number before any date is built).
- A zone name that is not a name of an interval at that local time gives the skip "time zone does not match
  import.time_zone" at the header's line. A zone mismatch **before any header of this parse resolved** fails the parse
  with the fixed error text "the time zone of the first snapshot header does not match import.time_zone" (the option is
  probably wrong for this file); a mismatch after a header resolved skips that snapshot.
- With `import.time_zone` unset, the first valid header fails the parse ([Log import](log-import.md), *Missing time
  zone*); a file without a valid header does not need the option.
- The snapshot of a header that is invalid or cannot be resolved is skipped whole: its lines give no further skip.

## Top part

- A snapshot expects the lines of `top` in this order, empty lines ignored: `top - …` with `load average:`, `Tasks:`,
  `%Cpu(s):`, `<unit> Mem :`, `<unit> Swap:` and the table header
  `PID USER PR NI VIRT RES SHR S %CPU %MEM TIME+ COMMAND`, then the rows.
- The summary lines are split at `,` (in the swap line also at the `.` directly after `used`); each piece is a number,
  one or more spaces and a label, so a piece needs no space after the comma or the colon, and the labels are read in
  any order, each exactly once.
- **Numbers** (load, CPU, %CPU, pressure averages) are an integer part of 1 to 15 ASCII digits and an optional point
  with 1 to 6 ASCII digits and nothing else (no sign, exponent or comma), so every value is finite and below 10^15.
- The memory and swap values are scaled by their unit (`KiB` to `EiB`) in exact integer arithmetic, with the integer
  part checked against the 64-bit range before anything is multiplied, and rounded half away from zero; a value that
  does not fit 64 bits makes its line unreadable.
- A snapshot is **skipped whole**, with one skip at its header line and no record, when it ends (next header line or
  end of input) before its table header ("truncated snapshot"), or when its top part is malformed ("malformed
  snapshot"): a non-empty line before the table header that is not the next expected line, a summary line that is not
  read, a section marker before the table header, or a line after the table header that begins like a summary line
  (`top - `, `Tasks:`, `%Cpu(s):`, `<unit> Mem `, `<unit> Swap:`) or is the table header again, which are the signs of
  another run's output mixed in. The snapshots before and after are emitted.

## Summary metrics

For a snapshot whose top part is complete the parser emits metrics (source `legacy-monitor`, `captured_at` the header
instant) in the order `load1`, `load5`, `load15`, `cpu_percent` (modes `user`, `system`, `nice`, `idle`, `iowait`,
`irq`, `softirq`, `steal`), `mem_total_bytes`, `mem_free_bytes`, `mem_used_bytes`, `mem_buff_cache_bytes`,
`mem_available_bytes` (from `avail Mem`), `swap_total_bytes`, `swap_free_bytes`, `swap_used_bytes`; the names, units
and labels are in [Wire format records](wire-format-records.md), *Metric names*. The values are as `top` printed them
with its rounding; that the first frame of `top` can show averages since boot for the CPU is not verified.

## Process snapshot

- A row is `PID USER PR NI VIRT RES SHR S %CPU %MEM TIME+ COMMAND`, separated by spaces; COMMAND is the rest of the
  line. A token before COMMAND longer than 64 bytes makes the row unreadable.
- `pid` is 1 to 2,147,483,647, `state` one ASCII letter, `rss_bytes` the RES column: digits (1 to 20) with an optional
  fraction (1 to 3 digits) and an optional suffix `k` or none (KiB), `m`, `g`, `t`, `p`, `e` (the powers of 1,024),
  scaled exactly; a RES that does not fit 64 bits makes the row unreadable.
- A COMMAND ending in `+` is cut by `top`: the `+` is removed and `truncated` set.
- In the **old format** (no section marker in the snapshot) `command` is the COMMAND without the `+`, cut to 1,024
  bytes (a cut also sets `truncated`), and `cmdline` is empty. In the **new format** (at least one section marker,
  known or not) `cmdline` is the COMMAND without the `+`, cut to 2,048 bytes (a cut sets `truncated`), and `command` is
  the text inside `[...]` for a bracketed COMMAND, else the part after the last `/` of the first word with one trailing
  `:` removed, cut to 1,024 bytes. The two formats therefore name the same process differently (`apache2` against a
  name derived from the command line); a new-format snapshot cut before its first section reads as old.
- `command` and `program` are **never empty**, because the record rules refuse an empty one and would refuse the whole
  snapshot: when the derived name is empty the normalized COMMAND is used, and when that is empty too (the COMMAND
  `+`) the COMMAND as printed.
- `programs` holds one entry per program (the first space-separated word of `command`, the command itself when that
  word is empty) in order of first appearance with `count`, the sums of `cpu_percent` and `rss_bytes` (the sum
  saturates at 2^64 − 1 instead of wrapping), no `pss_bytes`, and `rss_overcounted` true exactly when `count` is above
  1.
- `total` is `Tasks: N total`; `complete` is true only when the number of rows equals `total`, no row was left out and
  the table was not ended by the end of input. A table with no row gives no process snapshot.
- **A single bad row** does not skip the snapshot: a row that does not parse is left out with the skip "process row
  not read", a row whose PID an earlier row of the snapshot has with "duplicate process id", both at the row's line,
  and `complete` is false.

## Acceptance

The top part is accepted at the first section marker after the table header or when the snapshot ends after the
table header; only then are its records emitted and its row skips reported (at most 4,097 are held until then). After
acceptance a snapshot cannot fail any more. The records of a snapshot are emitted in this order: the summary metrics,
the process snapshot, then per section in file order the metrics in line order and the connection snapshot.

## Sections (new format)

- A line `----- <name> -----` (1 to 32 lower-case ASCII letters) begins a section. An unknown name is skipped with one
  skip "unknown section" at the marker, a known section that appears a second time in the snapshot with "repeated
  section"; the lines of both are not read and give no skip. Empty lines are ignored. An unreadable line is skipped
  with "meminfo line not read", "pressure line not read" or "vmstat line not read" at its line, and the rest of the
  section is read.
- `meminfo`: `Name: N kB` is the metric `meminfo_bytes` (N × 1,024, unit `bytes`) and `Name: N` the metric `meminfo`,
  label `field` the name (1 to 64 characters of `A-Z a-z 0-9 _ ( )`). N is 1 to 20 ASCII digits within 64 bits; for
  `kB` N times 1,024 must fit as well. A repeated field is unread.
- `pressure`: a line `<res>: some avg10=F avg60=F avg300=F total=N` with an optional ` full avg10=…` part (resource
  `memory`, `cpu` or `io`), or a line `full …` directly after a resource line that had only `some`, gives
  `pressure_avg10`, `pressure_avg60`, `pressure_avg300` and `pressure_stall_microseconds_total` with the labels
  `resource` and `kind`. A line `n/a` or `<res>: n/a` gives no metric and no skip. A repeated resource and kind is
  unread.
- `vmstat`: a line `<counter> N` for `pswpin`, `pswpout`, `pgmajfault` and `oom_kill` gives `vmstat_total` (label
  `counter`) and, when the previous accepted snapshot of this parse had the counter at an earlier instant and a value
  not above N, `vmstat_per_second` = (N − previous) / seconds between the two headers; other well-formed `<name> N`
  lines are ignored. The rate **depends on where a parse starts**: the first snapshot of a parse has no rate, and a
  continued import starts with an empty history, so the first snapshot of a continuation has none either (a gap of one
  rate per continuation; the raw `vmstat_total` is complete and a rate can be derived from it later). A skipped
  snapshot is not a predecessor. A repeated counter is unread ("vmstat line not read").
- `lsof`: *Connection snapshot* below.

## Connection snapshot

- An `lsof -ni` line is split at runs of spaces: COMMAND, PID (1 to 2,147,483,647), TYPE the first token from the
  fourth on that is `IPv4` or `IPv6`, and from the end an optional `(STATE)`, NAME and NODE (`TCP` or `UDP`, after
  TYPE). A line of another shape is skipped with "lsof line not read", also a line over 1,024 bytes (counted on the
  decoded line, an invalid byte counting three). The header line of `lsof` is ignored wherever it is.
- COMMAND escapes `\xHH` are decoded to bytes and the result is read as UTF-8, cut to 1,024 bytes.
- NAME is `LOCAL` or `LOCAL->REMOTE`, an endpoint is `ADDR:PORT`. The **address guard** accepts only what `lsof`
  prints, never the wider forms a general address parser would take (`127.1`, `1`, `0x7f.0.0.1`, `010.0.0.1`,
  `2130706433`, a zone that is looked up on the host); an address is never resolved and no zone is looked up:
  - TYPE `IPv4`: `*` (0.0.0.0) or exactly four dot-separated decimal octets of 1 to 3 digits, at most 255 and without
    a leading zero, built from the four bytes.
  - TYPE `IPv6`: `*` (::) or an address in brackets followed by `:PORT`; a zone (`%` to the closing bracket) is removed
    and the address kept; the rest is 2 to 45 characters of `0-9 A-F a-f : .` with a colon; a trailing dotted quad
    (after the last colon, with no point before it) must meet the IPv4 octet rule, so `::ffff:1.2.3.04` is refused,
    not read as decimal; only then is the text parsed as an address, and the result must be an IPv6 address without a
    scope.
  - Port: `*` (0), 1 to 5 ASCII digits up to 65,535 without a leading zero, or a service name of 1 to 32 characters of
    `A-Z a-z 0-9 . _ + -` found in a built-in table for the protocol (every name and alias of the `/etc/services` of
    Ubuntu's `netbase` 6.4, never a lookup on the host; a name missing from the table only leaves its socket unmapped).
    For TCP the state is required and must be one of the record's TCP states; for UDP a state token is ignored.
  - Every other form is skipped with "socket address not mapped". The protocol is `tcp`, `tcp6`, `udp` or `udp6` from
    NODE and TYPE.
- A socket is unique by the text of its line from TYPE on, and its owner is that of its first line (a socket shared by
  two processes is counted once).
- The snapshot has `states` (per protocol and state over unique sockets, by protocol then state), `processes` (per PID
  and decoded command over every socket line, in order of first appearance), `remotes` (per remote address over unique
  connected sockets, by count descending then address text), `listeners` (unique sockets without a remote endpoint)
  and `connections` (unique sockets with one); lists without entries are left out.
- It is emitted when the section ends (next section marker, next header line or end of input). A section without a
  line gives no record; one with only the header an empty, complete snapshot. `complete` is false when a line was left
  out (unread, unmapped, too long or beyond the cap) or the section was closed by the end of input.

## The end of the input

The log has no end marker, and a copy of a growing file can be cut anywhere. A snapshot closed by the end of input
instead of a header line is therefore emitted as far as it was read: an open `lsof` section gives its connection
snapshot with `complete` false, an open process table (old format) its process snapshot with `complete` false, and the
metrics of a cut section as read; one cut before its table header is skipped whole ("truncated snapshot"). The last
snapshot of every file is thus marked incomplete in its open list. A continued import starts at a line start: the rest
of a snapshot cut by the earlier copy is skipped line by line as "line before the first snapshot", and that rest is
lost.

## Bounds

- A line that is empty or holds only spaces counts as empty everywhere in a snapshot: it is ignored and counts toward
  no cap (a line with a tab or another character is not empty).
- Every non-empty line counts toward the cap of its part, read or not, and at the first line beyond a cap one skip is
  reported and every later line of that part is dropped unseen and without a skip: 4,096 row lines per snapshot ("too
  many processes in a snapshot", `complete` false, the aggregates are over the rows kept), 4,096 `lsof` lines ("too
  many sockets in a snapshot", `complete` false), and per section 128 `meminfo`, 16 `pressure` and 64 `vmstat` lines
  ("too many lines in a section").
- The only skips held back are the row skips of a top part not yet accepted (at most 4,097); every section skip is
  reported at once.
- Memory does not grow with the number of snapshots or the number of lines in a snapshot; the worst case is a constant
  of about 40 MiB for one snapshot (4,096 rows with a 2,048-byte command line and a 1,024-byte command, and 4,096
  socket keys and commands, counted in UTF-16), plus one line.
- A process or connection snapshot whose stored JSON would be larger than 16,777,216 bytes (possible only for forged
  text, because these caps allow about 105 MB of escaped JSON, while a legitimate snapshot stays far below) is refused
  by the importer ([Log import](log-import.md), *Parsing*) and not stored; the file goes on with the next record.

## Errors

The parse never fails for input. It fails for the time zone (*Time*), on cancellation and with what the emitter
reports; the open snapshot is not finished then, so the records emitted are a prefix of those of a complete parse.

## Forged lines (limitation)

Any process that may write the file can forge snapshots with any time, process or connection, and the first-line
claim makes any file whose first line has the shape of a header a legacy log. Command and user texts are stored as
they are and escaped where they are displayed.

## Related decisions

- [0093](../decisions/0093-legacy-monitoring-log-snapshots-by-header-zone-and-whole-snapshot-validity.md) — why the legacy monitoring log is read snapshot by snapshot, timed by the zone abbreviation of its header within `import.time_zone`, and why a snapshot with a broken top part is skipped whole while a single bad row is not.
- [0085](../decisions/0085-syslog-time-zone-from-import-time-zone-with-embedded-tzdb.md) — why local times are read in `import.time_zone` with an embedded time zone database.

## Not here

- The rules shared by every parser (claims, syslog names, record fields, line reader, storable range, missing time
  zone, record validation): [Log import](log-import.md), *Parser contract*.
- Metric names, process and connection snapshot fields: [Wire format records](wire-format-records.md).
- The agent's own collectors of the same data: [Agent system metrics](agent-system-metrics.md),
  [Agent connections](agent-connections.md).
- The other parsers: [System and mail log parsers](log-parsers-system.md),
  [MariaDB error log parser](log-parsers-mariadb.md), [Plesk and web server error log parsers](log-parsers-web.md).

## Implementation

`Vandox.Core.LogParsing`: `LegacyMonitorParser`, `LegacyMonitorHeader`, `LegacyMonitorSnapshot`, `LegacyProcessTable`,
`LegacyConnectionTally`, `LegacyMetrics`, `LegacyTopLine`, `LegacyTopRow`, `LegacyProcessNames`, `LegacyProcLine`,
`LegacyPressure`, `LegacyLsofLine` (the address guard in front of `IPAddress.TryParse`), `LegacyServiceNames`,
`LegacyCounter`, `LegacyParseState`; the memory scaling compares with `ulong.MaxValue >> (10 × n)` and uses `UInt128`;
the zone names come from NodaTime's TZDB; the payload limit is `StorageLimits.MaxPayloadBytes`. The parse throws
`InvalidOperationException` for the time zone and `OperationCanceledException` on cancellation.
