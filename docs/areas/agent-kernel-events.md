# Agent kernel events and boots

## Scope

The agent's `kernel` collector: how it reads the kernel's OOM kills from systemd-journald's binary files, which entries count as a kill,
how a kill is timed and identified, the journal position it keeps across restarts, the gaps it records for what it cannot read, the extra
process and connection snapshots a live kill requests, and the boot check at its first run. Incidents and the report are built on these
records; scheduling and the state file are in [Agent](agent.md).

## Collector

The collector `kernel` (interval 2 seconds, timeout 2 seconds, after `clock` in the job list) records the kernel's OOM kills as
`kernel_event` records of type `oom_kill` with source `kernel` ([wire format records](wire-format-records.md)), the `boot` event of
*Boots*, and a `gap` (source and collector `kernel`, cause `unknown`) for every span of kernel messages it cannot read. It reads the binary
journal files itself through reads at offsets into fixed buffers (no `mmap`); it starts no program (no `journalctl`), links no C library
and decompresses nothing.

## Where it reads

The machine ID comes from at most 64 bytes of `/etc/machine-id`: 32 lower-case hex digits and an optional line feed. The journal directory
is `/var/log/journal/<machine-id>`, else `/run/log/journal/<machine-id>`; the first that can be listed is read. Of its regular files only
`system.journal` (the **online** file, which journald writes), `system@*.journal` (archived) and `system@*.journal~` (set aside after an
unclean close, read like an archived file) are read; the directory is listed in chunks of 256 entries and at most 1,024 files are taken.
Every listed file's header is read at every run.

## Layout (security area 10)

- Two layouts are read: the signature `LPKSHHRH`, a header of 256 to 4,096 bytes (a multiple of 8, within the file; systemd 249 writes 256,
  252 and 253 write 264, 254 and later 272), the incompatible flag `KEYED-HASH` set and no incompatible flag other than `COMPRESSED-XZ`,
  `COMPRESSED-LZ4`, `KEYED-HASH`, `COMPRESSED-ZSTD` and `COMPACT`.
- Without `COMPACT` (systemd 249 on the server) a data object's payload starts at byte 64, an entry item is 16 bytes (a 64-bit offset and a
  hash) and an entry-array item 8 bytes; with it (systemd 252 and later by default) the payload starts at byte 72, a data object is at
  least 72 bytes, and entry items and entry-array items are 32-bit offsets of 4 bytes. The tail fields the compact layout adds are not read.
- Any other file — no keyed hashes, an unknown incompatible flag, shorter than 256 bytes — is refused as `unsupported layout`; a file whose
  `COMPACT` flag differs when its header is read again is `malformed file`.
- Before anything is decoded, every offset must be 8-aligned and inside the header's arena and the file (every entry-array item and every
  link to the next array, also from an array a cursor kept across a re-read of the header), and every object at least its fixed size and
  end there. Hash chains and entry-array chains are followed only to strictly larger offsets, a hash chain for at most 65,536 objects and
  an entry-array chain for at most 64 arrays; an entry has at most 512 items. A violation is `malformed file`.
- An object beyond the end of an archived or set-aside file is a **torn tail**: the file ends there. The same in the online file is
  `malformed file`, retried once in the run after the file's size is read again, and at the next runs.
- Per entry only `_TRANSPORT`, `SYSLOG_FACILITY`, `MESSAGE` and `_SOURCE_MONOTONIC_TIMESTAMP` are read in full, up to 4,096 bytes of
  payload each; of every other item only the first 64 bytes, to see its name. At most 16 of these items and compressed items are kept per
  entry; every further one is counted. A compressed item is never decompressed, so its name is unknown.

## Finding the entries

The data hash table (SipHash-2-4 keyed with the file ID) finds the data objects `_TRANSPORT=kernel` and
`MESSAGE_ID=e9bf28e6e834481bb6f48f548ad13606` (journald's entry about missed kernel messages); their entry lists, merged by sequence
number, are what a file holds for the collector. The place after the position is found by bisection over the entry arrays, and the cursors
at the end of the lists of the position's file are kept between runs, so a run costs a fixed number of reads plus the new entries. Sequence
number differences are never used as counts or gaps.

## Which entry counts

An entry of the `_TRANSPORT=kernel` list is **readable** when it has exactly one `_TRANSPORT` item and that is `kernel`, no compressed
item, no item beyond the 16 kept, a `__REALTIME_TIMESTAMP` above 0 and below 253,402,300,800,000,000 µs (1 January 10000 UTC), a
`__MONOTONIC_TIMESTAMP` of at most 9,223,372,036,854,775 µs, and at most one `_SOURCE_MONOTONIC_TIMESTAMP` of 1 to 20 ASCII digits (no sign,
space or line feed) within the same bound. A readable entry is **counted** when it also has exactly one `SYSLOG_FACILITY` item whose value
is exactly `0`. Only a counted entry with exactly one `MESSAGE` item can be a kill. Entries of other transports and lines written to
`/dev/kmsg` (which the kernel forces to a facility other than 0) are never a kill. An entry that is not readable is a gap, never a kill.

## The kill line

The `MESSAGE` starts with exactly one of `Out of memory: Killed process `, `Memory cgroup out of memory: Killed process ` and `Out of memory (oom_kill_allocating_task): Killed process `, followed by the PID (1 to 10 digits, 1 to 2,147,483,647) and ` (`; the victim's name runs to
the **last** `) total-vm:` of the line, and a line without one is no kill. The rest is split at `, ` and at spaces; only the tokens
`anon-rss:<1 to 20 digits>kB` (times 1,024, within 64 bits) and `oom_score_adj:<optional minus and 1 to 4 digits>` (−1000 to 1000) are
used, and one that is missing, malformed, out of range or present twice leaves its field out while the event stays. The report lines
before a kill, `Out of memory and no killable processes` and the `oom_reaper` line are no kill.

The record holds `victim_pid`, `victim_command` (the name made valid UTF-8, each invalid byte U+FFFD, cut to 1,024 bytes; `(unnamed)` when
empty), `anon_rss_bytes`, `oom_score_adj`, `boot_id` (the entry's boot ID), `kernel_time_ns` (the `_SOURCE_MONOTONIC_TIMESTAMP` in
nanoseconds, else the `__MONOTONIC_TIMESTAMP`) and the line as `message` (valid UTF-8 the same way, at most 16,384 bytes). `victim_command`
and `message` are external text that whoever displays them escapes. `boot_id`, `kernel_time_ns` and `victim_pid` are the kill's
**identity**: the same however often the kill is read.

## Capture time

A kill is timed by its receipt, corrected for the kernel clock's drift. The *lag* of a counted entry is its `__MONOTONIC_TIMESTAMP` minus
its `_SOURCE_MONOTONIC_TIMESTAMP` (0 when the source stamp is later). The *drift* is the smallest lag among the entry and the counted
entries with a source stamp of the same boot received at most 600 seconds before it, among the last 64 such entries read (kept across
runs, emptied by an entry of another boot, empty after a restart); the *delay* is lag minus drift. The capture time is the
`__REALTIME_TIMESTAMP` minus the delay, so it is never after the receipt; the delay is 0 without an earlier line and for an entry without a
source stamp. A kill of an earlier boot is timed the same way.

## Position

The position is the sequence-number ID, the sequence number and the real time of the last entry read; it is saved in the agent's state
([Agent](agent.md), *Agent state*). Without a usable saved position (a first start, no usable state, a position that does not decode) the
collector starts at the end of the newest file (the online file, else the file with the latest tail time) and reads no kill before it.
Each run reads the files in this order:

1. the files of the position's sequence-number ID in the order of their first sequence number, after the position (a file that ends at or
   before it is skipped);
2. the archived and set-aside files of other IDs whose header's first time is not before the committed position's time, by that time
   (ties by file name), from their start;
3. the archived and set-aside files of other IDs without a readable header time, by file name, from their start;
4. the online file, when its ID is another, from its start, whatever its header time.

- A file of steps 2 and 3 read to its end is not read again while the agent runs; one cut by a limit is continued where it was cut.
- The files of steps 1 and 4 move the position, and so does a file of step 2 whose first time is not before the time the position has
  reached when it is read; the others are read without moving it. The position's time never moves back (the later time is kept).
- A saved ID of which no file is left records a gap from the position's time to the next readable kernel entry (or the run's start),
  once, and reading goes on there.
- When files of the position's ID are left but none holds the position and one starts after it (vacuumed), one gap runs from the
  position's time to the header's first time of the first such file (without one, to the next readable kernel entry, else the run's
  start), once for that position, and reading goes on in that file. Only when that file's first sequence number directly follows the
  position's is no gap recorded; otherwise a gap is recorded even when the vacuumed part held no kernel entry. A run that records the gap
  of a refused file (*Failures*) from the committed position's time records no second gap for the vacuumed position.
- A file that disappears between the listing and its read is skipped.

**Limits of a run.** A run stops after one second of reading (checked between entries), after 256 kills, or when its deadline passes, and
returns what it read; the next run continues there.

## Commit and re-read

What a run reaches — the position, the drift window, the files of other IDs read to their end or cut, and the gaps already recorded for
refused files and a lost ID — is committed as one unit at the start of the next run, only when the scheduler reports that the previous
result was kept (the previous sample was not lost, [Agent](agent.md), *Scheduling*); the position is then saved. Otherwise the next run
reads again from the committed state, so a kill whose record was dropped (a timeout, a skip, a sink error) returns with the same identity.
A dropped run that read nothing does not count. At the fourth dropped run in a row from the same committed position the span from that
position to where the last dropped run got is **given up**: the run records one gap from the committed position's time to the time of the
last readable kernel entry that run read (else the run's start), reads on after the span, and the span's kills are not returned; while
that run is dropped as well, the next one gives up the same span again. So a sink error lasting more than four runs (8 seconds: a spool
write that keeps failing or a hand-over the spool cannot place, [Agent spool](agent-spool.md)) gives up the span's kills as a gap; a full
spool is no such error (it drops its oldest records with a gap and takes the run's). A run that is abandoned never moves the saved
position.

## Gaps of the kernel collector

Each gap has source and collector `kernel` and cause `unknown`; its times come only from readable entries, the committed position (decoded
strictly from the state file) and the run's start; a gap whose end would not be after its start ends 1 µs after it. Gaps are recorded for:

- an unreadable entry of the kernel list: from the last readable kernel entry read (any facility; else the committed position's time) to
  the next readable kernel entry (else the run's start); several in a row give one gap;
- journald's entry about missed kernel messages on the `driver` transport (or with an item whose name is unknown): over the same span;
- a saved ID without files, a vacuumed position, a given-up span, and a file the reader refuses, finds torn or cannot read (*Failures*);
- a record that would fail the scheduler's check: the collector runs the same check on every record before returning it and puts a gap
  over that record's entry in its place (from the previous kernel entry, or the committed position, to the entry's time); a gap that would
  still fail becomes the gap of the last microsecond before the run's start. So a refused record never makes a span be read again, and the
  scheduler never refuses a record of this collector.

## Failures

A run never fails: it returns its records and gaps, and an unreadable journal is logged.

- No machine ID, no journal directory, a directory that cannot be listed: nothing is read and no gap recorded; reading resumes at the
  committed position once the journal can be read.
- A file whose header is refused (`unsupported layout`, `malformed file`): one gap from the committed position's time (else the run's
  start) to the run's start the first time the agent meets the file, and the other files are read on; while the online file is refused, a
  further gap from the end of the last one to the run's start at the first run at least 600 seconds after it.
- A torn tail, or `malformed file` while reading an archived or set-aside file: that file ends there, with a gap from the last readable
  kernel entry read in it (else the committed position's time, else the run's start) to the header's last time when that is later; without
  a readable last time, to the next readable kernel entry read in this run (else the run's start); when its last time is not later,
  without a gap. The file is not read again; a file that moves the position moves it past its last entry.
- A file that cannot be opened, a read that fails, and `malformed file` in the online file: the run stops at that file without a gap and
  reads it again at the next run. At the third run in a row that fails at the same file and position the file is given up: a file whose
  header could not be read gets the gap of a refused file, one being read the gap and the move past the file of a torn tail (the online
  file's header read again first), and the other files are read on. The online file's `malformed file` is not logged before it is given up.

## Extra snapshots

A kill is **live** when its entry's boot ID is the current boot's and the time since the journal received it, plus its delay, is at most
60 seconds on the monotonic clock (the first field of `/proc/uptime` minus the `__MONOTONIC_TIMESTAMP`, a negative age counting as 0);
without a readable `/proc/uptime` or boot ID no kill is live, so kills from a backlog or of another boot get no snapshot. A run queues its
live kills whose identity is not among the last 64 kills it queued; a run whose deadline has passed when it finishes queues and requests
nothing (its kills are judged again when the next run reads them again). Before a run returns, when no request was made at a run start
less than 15 seconds before, the collector requests one extra run of `processes` and one of `connections` ([Agent](agent.md),
*Scheduling*), naming the queued kills (oldest first, at most 64, the rest counted); otherwise they wait for the first run at least 15
seconds after the last request. Kills an earlier run queued are not requested a second time when read again.

## Logging and memory

One line per change: warning `kernel messages not readable` with `err` one of `journal: machine ID not readable`, `journal: no journal directory`, `journal: directory not readable` and `journal: <online|archived>: <unsupported layout|malformed file|read failed>`, and info
`kernel messages readable again`; with *Boots*, warning `boot ID not readable`. No line per event and none with a path, a field value or a
message.

The reader and the collector hold at most 4 MiB live, independent of the size of the journal, the backlog and an entry: fixed buffers, at
most 1,024 file headers, the drift window and trigger lists of 64 entries each and at most 256 kill records per run.

## Residuals (records 0119, 0120, 0153, 0154)

- The facility rule proves that the kernel wrote the line, not that the OOM killer did: kernel code that prints a line of the same form at
  facility 0 is recorded as a kill.
- A journal without keyed hashes or one that stores the kill lines compressed is not read; its kernel events are lost to recorded gaps.
  The compact layout is checked against systemd-journal-remote 255 output; files journald writes in that layout on the server are not yet
  checked.
- `/proc/uptime` counts time in suspend, the journal's monotonic clock does not; on a server that never suspends they agree (checked by
  `check journal-clock` of the install script, [Agent installation](agent-installation.md)); after a suspend every kill reads as late and
  gets no snapshots, never one too many.
- After a restart the files of another ID without a readable header time are read again from their start (their kills return with the
  same identities), and a refused file gives its gap again once per agent start.
- An archived or set-aside file of another ID whose header's first time lies before the committed position's time is not read.
- journald's entry about missed kernel messages is built from the systemd 249 source; real journals hold none. The cgroup and the
  allocating-task kill lines are built from the kernel source, not real lines; a kernel whose line differs in the prefix or before
  `) total-vm:` records no event and no gap for such a kill.
- After the clock was set back, entries of a vacuumed position can carry times after the run's start; a run that also meets a refused file
  for the first time records only the refused file's gap, ending at the run's start (or 1 µs after the position's time when later), so not
  every lost entry's time lies inside the gap.
- The listing skips symbolic links, but a file is then opened by its name, so a file replaced by a link between listing and read is
  followed; only root or journald can place one in the root-owned directory.

## Boots

At its first run the `kernel` collector reads the boot ID from `/proc/sys/kernel/random/boot_id` (at most 64 bytes: a lower-case UUID and
at most one line feed) and compares it with the boot in the agent's state ([Agent](agent.md), *Agent state*):

- the same boot (a restart of the agent): no record, and the state's boot is left alone;
- another boot: one `kernel_event` of type `boot` with `boot_id`, `booted_at` (the `btime` of `/proc/stat`), `previous_boot_id` and
  `previous_uptime_ns` (the uptime the state holds for the previous boot, a lower bound at most a minute short when the agent ran until it
  ended; boots the agent did not run in are not recorded);
- no saved boot (a first start, or no usable state): the same event without `previous_boot_id` and `previous_uptime_ns`, at every start
  while no state is usable (each with the same `boot_id`).

The capture time is `booted_at`; when the boot time cannot be read, `booted_at` is left out and the capture time is the run's start. The
state's boot becomes the current one, with the first field of `/proc/uptime` as its uptime (0 when unreadable) and its boot time, only when
the scheduler reports kept the latest run that returned the event (the observer, [Agent](agent.md), *Scheduling*); until then the state
holds the previous boot, so a restart before that records the event again with the same `boot_id`, `previous_boot_id` and
`previous_uptime_ns`. Every run returns the boot event again until such a report; a run reported not kept, or not reported, changes
nothing. The stored boot's uptime is kept current only while that boot is the current one ([Agent](agent.md), *Agent state*, *Saving*).
A boot ID that cannot be read or is not a lower-case UUID gives no boot event, leaves the state's boot alone, makes no kill live and logs
one warning `boot ID not readable`; the other features run. The agent does not start without a readable boot ID
([Agent spool](agent-spool.md)), so this warning comes only from a read that fails after a successful start.

## Related decisions

- [0028](../decisions/0028-data-gaps-are-always-recorded.md) — every span of kernel messages that cannot be read is a recorded gap.
- [0119](../decisions/0119-kernel-messages-from-the-binary-journal-files-read-in-pure-go-without-decompression.md) — the binary journal
  files read by the agent itself, set-aside files included, without decompression; only the kernel transport with facility 0 counts.
- [0154](../decisions/0154-the-journal-reader-reads-the-compact-layout-chosen-per-file-from-the-headers-flag.md) — the two layouts journald
  writes, COMPACT read by the same reader.
- [0153](../decisions/0153-a-kill-is-timed-by-its-receipt-corrected-for-the-kernel-clocks-drift.md) — a kill timed by its receipt corrected
  for the kernel clock's drift.
- [0120](../decisions/0120-oom-kill-triggers-extra-process-and-connection-snapshots.md) — a live kill requests extra snapshots and is named
  by its identity.
- [0157](../decisions/0157-boots-recognized-from-the-saved-boot-id-previous-uptime-from-the-saved-uptime.md) — boots come from the saved
  state.
- [0155](../decisions/0155-the-kernel-journal-position-is-committed-only-after-a-kept-run-with-a-re-read-cap.md) — the position is committed
  only after a kept run with at most three re-reads.
- [0159](../decisions/0159-boot-and-backup-run-changes-reach-the-state-only-once-their-run-is-kept.md) — the boot reaches the state only
  once a run is kept.

## Not here

- Scheduling, extra runs, the observer and the state file: [Agent](agent.md); what the extra snapshots hold:
  [Agent system metrics](agent-system-metrics.md) and [Agent connections](agent-connections.md).
- The journal's other log lines, shipped as `log_line` records: [Agent log shipping](agent-log-shipping.md).
- The spool and its boot ID: [Agent spool](agent-spool.md); the journal-clock check: [Agent installation](agent-installation.md).
- How kills become incidents: [Incidents](incidents.md); the `kernel_event` fields: [Wire format records](wire-format-records.md).

## Implementation

`internal/journal` (the journal file reader) and `internal/journal/journaltest` (a writer of journal files for tests; fixtures
`testdata/journal/`: an online, an archived and a set-aside file generated from `kernel.export`, the same three in the compact layout under
`testdata/journal/compact/`, and under `testdata/journal/systemd-255/` two compact files systemd-journal-remote 255 wrote from
`kernel.export` and `live.export`; every generated fixture must be accepted by `journalctl --verify` and read back by
`journalctl -o export`), `internal/kernevents` (kernel event collector and boot check; two tests assert the 4 MiB bound at
journals of 1,000 and 100,000 kernel entries, `TestKernel_LogsHoldNoMessageText` the log texts).
