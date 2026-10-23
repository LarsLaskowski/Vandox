# Spec: Persistent on-disk spool (at least 7 days)

Source: Issue #38
Status: Approved by Product Manager

## Problem

`vandox-agent` collects metrics, snapshots, events and log lines on the monitored server, but today it drops every record after
checking it: the scheduler's sink is `scheduler.DiscardSink` (`internal/scheduler/sink.go`, `internal/agent/agent.go`
`newScheduler`), no record gets a sequence number, and nothing survives the backend host being off. The backend host is switched off
some nights (typically 22:00 to 09:00) and may be away longer; outages of the server happen exactly when nobody watches, so the data
of those hours is the data the forensics need. The operator needs the agent to keep every record on the server's disk until the
backend has acknowledged it, for at least 7 days, without ever filling the server's disk or making its memory situation worse, and
to record as a gap whatever it nevertheless has to drop (records 0028, 0045, 0046). The sender that delivers the records (#39) and
the ingest API that acknowledges them (#40) build on this spool; until they exist, the spool is read and acknowledged only by tests.

## Scenarios

The "reader" below is the agent's sender (#39); in this feature its place is taken by the tests. The spool's files live in
`spool.directory` (default `/var/lib/vandox/spool`) beside the agent's state file `agent-state.json`. "Accepted" means the hand-over
of the records to the spool returned without an error.

- S1: Given the agent runs with a usable spool directory, when a collector run ends and the scheduler hands its records to the sink,
  then the spool keeps them, each with a sequence number above every number the agent issued before, in the order they were handed
  over, together with the agent ID, the boot ID of the boot they were captured in and their clock offset estimate (unknown today);
  the agent no longer drops them.
- S2: Given the backend has been unreachable from 22:00 to 09:00 and the spool holds those 11 hours of records, when the reader reads
  from the oldest kept record onwards in pieces and acknowledges each piece, then it receives every record exactly once, in sequence
  order, each piece holding records of one boot ID and one clock offset estimate only, and once all are acknowledged the spool's
  files shrink accordingly.
- S3: Given an outage of 7 days of the reference stream (*Non-functional requirements*) with the default `spool.max_size`, when the
  backend becomes reachable again, then the spool still holds every record of the 7 days, has dropped nothing and recorded no
  `spool_dropped` gap, and S2's reading delivers all of it.
- S4: Given the spool holds unacknowledged records, when the agent is stopped with SIGTERM and started again, or the server reboots
  cleanly, then every accepted record is still there unchanged, the sequence numbers go on directly after the last one issued,
  records captured before the reboot keep the old boot ID and records after it carry the new one, and no piece the reader receives
  mixes the two.
- S5: Given the agent is writing records into the spool, when it is killed with `kill -9` at any moment and started again, then every
  accepted record is there and intact, no damaged record is ever handed to the reader, and no sequence number is issued a second
  time.
- S6: Given the spool has reached `spool.max_size` because the backend has been away too long, when further records arrive, then the
  spool drops its oldest unacknowledged records to make room, writes a `gap` record with cause `spool_dropped` per run of dropped
  sequence numbers, logs the warning `spool data dropped` once for the episode, and its size never exceeds `spool.max_size`.
- S7: Given other programs have filled the server's disk so that the space available on the spool's file system is below
  `spool.min_free`, when further records arrive, then the spool does not grow: it makes room by dropping its oldest unacknowledged
  records as in S6, with the warning's `reason` `disk_space`.
- S8: Given the agent runs, when the `self` collector takes its sample (every 60 seconds), then the sample holds `agent_spool_bytes`,
  the size of the spool's files, and `agent_spool_age_seconds`, the age of the oldest unacknowledged record (0 when the spool holds
  none).
- S9: Given the spool holds a backlog, when the reader reads the newest records (from a sequence number it names) and acknowledges
  them as a range before the older ones are acknowledged, then those records are never handed out again, the older ones remain
  readable in sequence order, and the reader goes on acknowledging the backlog from its oldest end (the two streams #39 needs:
  current data first, backfill in order).
- S10: Given the operator writes `spool.max_size: 4GiB` and `spool.min_free: 512MiB` into `/etc/vandox/agent.yaml`, when the agent
  starts, then the spool keeps at most 4 GiB and stops growing when less than 512 MiB would remain available; without the keys the
  defaults `2GiB` and `1GiB` apply.

## Errors and edge cases

- E1: A `spool.max_size` or `spool.min_free` value that breaks its rule (`1024`, `1 GiB`, `1.5GiB`, `1GB`, `-1GiB`, `32MiB` for the
  size, `65GiB` and `2048GiB` for the size, `2048GiB` for `min_free`; a list and a null value such as `~` are refused by the strict decoder as for every option, with `invalid value,
  want a string` and `has no value`): the agent does not start; the error reads `config: <file>:<line>: spool.max_size: must be a whole
  number of MiB or GiB from 64MiB to 64GiB, such as 512MiB or 2GiB` (for `spool.min_free`: `... from 0MiB to 1024GiB, such as
  0MiB or 1GiB`) and never contains the value; exit code 1, as for every configuration error.
- E2: At start the spool directory is missing, is not a directory, is itself a symbolic link (the last element of
  `spool.directory`; links in the elements above it are followed as configured; the error says `symbolic link`, not `not a
  directory`), holds a spool-named entry the spool refuses in place of `spool-meta.json` or its temporary file (E8), or cannot be written by the agent's user, also
  because its file system is read-only: the agent logs `agent cannot start` with the error `spool: <fixed reason or system error
  text>` (never the path) and exits with 1 before it notifies systemd, so `systemctl status` shows the failure instead of an agent that
  silently discards its data. A start failure is retried only by the unit's `Restart=` (#42: `Restart=on-failure` with a
  `RestartSec=` and start limits that keep retrying a failed start).
- E3: First start in an empty spool directory, or a directory that holds only other entries such as `agent-state.json`: the spool is
  created and logged once as info `spool created`; neither the spool nor the state file logs a warning for it (the state file treats
  only a directory that is empty at its read as a first start, so the order of the two at start-up matters). Stated residual: a
  start that ends after the spool was created and before the first state save (a `kill -9` in the first seconds, a later start
  error) leaves spool files without a state file, and the next start logs the state file's warning `agent state not available` once.
- E4: The spool's files are lost while the agent ID stays (reinstall, a wiped or replaced directory): the new spool's sequence numbers
  start above every number the lost spool can have issued (the rule is in NFR *Sequence numbers*), unless the wall clock now reads
  earlier than when those numbers were issued (stated residual); the backend would otherwise take the new records for duplicates of
  stored ones and drop them silently.
- E5: The server loses power or the kernel crashes: records accepted more than 5 seconds before survive; records accepted later may be
  missing, but no damaged record is handed out, no sequence number issued before the crash is issued again (NFR *Sequence numbers*),
  and the state file never holds a log or kernel journal position that covers a record the spool had not made durable: those
  positions are committed only after the spool accepted the run's records, and every state save first makes the spool durable, so
  the `kernel` and `logs` collectors read such entries again from the journal and the files. The loss is limited to records no source
  can give again (metrics and snapshots of those seconds), which the backend sees as a sequence gap (#41). Stated residual: a boot
  change (`kernel` collector) and a backup start, sighting or end (`backups` collector) are written to the state file from inside
  their run, before the run's records reach the spool; a state save in that moment followed by a power loss within the next second
  can lose the `boot` event or the `backup_run` record while the state already holds them, so they are not recorded again. The
  backend still sees the new boot in the batch headers' boot ID; the window is one sync interval after a save that fell between a
  run's state change and its hand-over. The Product Manager accepted this residual for this feature; a follow-up issue closes it
  before the agent's first release.
- E6: Damaged spool data, found at start or while reading: a checksum that does not match, a file cut short, bytes that are not the
  spool's format, a spool file of a format version the agent does not know, or values that break the spool's consistency rules
  (a file name's number that is not the number of its first record; record numbers that do not increase strictly within and across
  files; a record whose number differs from the number in its record line; a sealed file's summary that disagrees with its last
  record; a time outside the range the wire format allows). The damaged record and every later record of that file are never handed
  out; the records before the damage and all other files stay readable; the spool writes `spool_dropped` gaps for the dropped numbers
  (from the damaged record's number, or the last good number plus 1, to the file's last known number) where it can know them
  (otherwise the backend's sequence detection finds them); the warning `spool data damaged` (`file`: the spool file's name, `err`: a
  fixed reason) is logged once per file. A gap is never built from a damaged value: a time outside the wire range is not used.
- E7: Entries in the directory that are not the spool's files (`agent-state.json`, `agent-state.json.tmp`, other files,
  subdirectories, up to 100,000 of them): never read, counted, overwritten or deleted by the spool; the spool's memory bound holds.
- E8: An entry with the name of a spool file that is a symbolic link (also one whose target is `agent-state.json` or another file
  of the agent's user in the same directory), not a regular file, a regular file with more than one hard link, owned by another user
  or with any group or other permission bit: never followed, opened for writing, renamed or deleted, and its target never changes; it is not counted, and the data the spool expected
  there is treated as damaged (E6). The spool's own temporary file `spool-meta.json.tmp` is removed and `spool-meta.json` is
  replaced only when each is absent or a regular file of the agent's user without group or other bits; otherwise the meta file
  cannot be written (E2 at start; at run time an acknowledgement fails with an error).
- E9: A write fails at run time (no space left, an I/O error, the file system remounted read-only): unlike at start (E2) the agent
  keeps running, because the failure may pass and its collectors' gaps should then reach the backend. The spool refuses the records it
  could not keep, so the scheduler logs `sink failed` as today; such a refusal returns at once and never stops the watchdog. The spool
  logs the warning `spool write failed` (`err`, never a path) once per episode and the info `spool writes again` when a write succeeds,
  and then writes one `gap` with cause `unknown`, source `agent`, covering the capture times of every record refused in the episode.
- E10: The spool stays full for weeks: every eviction writes its gaps; when the records dropped include earlier gap records, the new
  gap's time span also covers their spans, so no dropped span is ever left unrecorded; the size never exceeds `spool.max_size`, the
  gap records included. A gap's `first_seq` and `last_seq` are exact; its time span is the union of the dropped capture times and
  dropped gap spans and may therefore include moments of which other records were kept (stated residual: the sequence numbers, not
  the time span, say which records are missing).
- E11: The records of one hand-over do not fit even after every unacknowledged record was dropped (a cap smaller than one run's
  records, or available space below `spool.min_free` with an empty spool): they are refused and recorded as in E9; the episode's gap
  is written by the first write that fits again.
- E12: The reader acknowledges a sequence number the spool never issued, one already acknowledged or dropped, or one it was never
  handed: nothing changes and no other record is affected. An acknowledgement that would make the spool track more than 64 disjoint
  acknowledged ranges above its oldest unacknowledged record is refused with an error and changes nothing; the records stay readable
  and are acknowledged again later (a resend is idempotent at the backend).
- E13: Many collectors hand over records at the same moment while the reader reads and acknowledges: every record gets exactly one
  sequence number, numbers increase strictly in the spool's order, and a read never returns a record twice or a record whose write
  has not completed.
- E14: SIGTERM while records are being written: every accepted hand-over is kept; after the scheduler has stopped, the spool makes its
  records durable, then the state file is saved for the last time, then the spool is closed; a step that does not finish within 5
  seconds is given up with the warning `spool not closed` (for the spool's steps) and the agent exits as usual.
- E15: The boot ID cannot be read from `/proc/sys/kernel/random/boot_id` at start (missing, not a lower-case UUID): the agent logs
  `agent cannot start` with `spool: boot id: <fixed reason>` and exits with 1.
- E16: The wall clock was set back, so capture times in the spool do not increase: the order stays the sequence order;
  `agent_spool_age_seconds` is 0, never negative, when the oldest record's capture time lies after the sample's start.
- E17: A record of the largest size the wire format allows (a 1 MiB process or connection snapshot) is kept and read back like any
  other.
- E18: The operator changes `agent_id` while records of the old ID are spooled: the wire header carries the agent ID, and the token
  belongs to the new ID, so the old records cannot be sent as theirs. At start the spool drops every record of another agent ID,
  writes no gap for them (a gap sent under the new ID would describe another agent's data) and logs the warning `spool records of
  another agent dropped` (`records`: the count); the new ID's numbers start as on a fresh spool (E4).
- E19: Many reboots or restarts leave many small spool files (a crash loop with reboots): the spool holds at most 1,024 files; at the
  limit it drops its oldest unacknowledged records as for the size bound, with `spool_dropped` gaps and the warning's `reason`
  `file_limit`. A restart in the same boot goes on writing into the newest file, so restarts alone do not add files. A directory
  that nevertheless holds more than 1,026 data files (placed there from outside): the spool keeps the 1,026 newest by name; the
  others are damaged (E6), never read, counted or deleted, and `spool data damaged` is logged once per start with `err` `too many
  files`.
- E20: The meta file breaks its consistency rules (acknowledged ranges not sorted, overlapping, with a first number above the last,
  or at or above the next number; a next number at or above 2^63 - 1): it is damaged (E6, `file` `spool-meta.json`); its
  acknowledgements are discarded, so those records are handed out again (a resend is idempotent), and the next number is taken as on
  a fresh spool (NFR *Sequence numbers*).
- E21: The next sequence number would reach 2^63 - 1, the largest the backend stores: the hand-over is refused as in E9 with `err`
  `spool: sequence numbers exhausted`; no number at or above it is issued.

## Non-functional requirements

- **Reference stream and retention.** The default `spool.max_size` holds 7 days of the **reference stream**, which the test generates
  reproducibly from a fixed seed, every record with varying content (numbers, PIDs, times and the variable parts of log lines drawn
  anew, never a record repeated verbatim): per day 2,880 `system` samples of 110 metrics; 1,440 `filesystem` samples of 7 metrics,
  `clock` samples of 3 and `self` samples of 5; 1,440 process snapshots of 50 processes and 100 programs with command lines of
  60 to 200 bytes; 1,440 regular connection snapshots of 30 listeners, 40 owning processes and 25 remotes; 2,880 MariaDB status
  records; 400 service states; 200,000 log lines with messages of 40 to 400 bytes drawn from the line shapes of `testdata/logs/` and
  `testdata/journal/`. Its encoded size per day (uncompressed JSON lines) is measured by the test and written into the agent area;
  the estimate is about 170 MB, so 7 days are about 1.2 GB, which the 2 GiB default holds with room even without compression. The
  stream is an estimate of the server, not a measurement; #44 measures the real growth. Under sustained memory pressure the agent
  records much more, so less than 7 days may fit; the cap then drops the oldest data with gaps (the stated residual of *Memory
  pressure mode* in the agent area).
- **Size bound.** `agent_spool_bytes` is the total size of the spool's own files on disk (its data files, `spool-meta.json` and
  `spool-meta.json.tmp` while it exists), headers and summaries included, and never exceeds `spool.max_size`. Once every
  record is acknowledged it is at most one sixteenth of `spool.max_size`.
- **Disk.** A write is made only when the space available to unprivileged users on the spool's file system (`statfs` `f_bavail`)
  is at least the write's size and what remains after the write is at least `spool.min_free` (compared without overflow, so an
  available space smaller than the write never passes); otherwise the spool first drops its oldest unacknowledged records, and refuses
  (E11) when nothing is left to drop. `0MiB` and `0GiB` switch the check off.
- **Durability.** A record survives `kill -9` once accepted, and an OS crash or power loss once 5 seconds have passed since. Before
  the state file saves a change, every record accepted until then is durable, so no saved position or state ever covers a record a
  crash can still lose, apart from the residual of E5 (E5, E14).
- **Sequence numbers.** Numbers are issued strictly increasing and never twice by one agent ID. After a clean stop, and after `kill -9`
  in the same boot, the next number follows the last issued one. On a fresh spool (E3, E4, E18), and after an unclean stop in an
  earlier boot (a crash or power loss may have rolled the stored counter back, E5), the next number is the larger of the stored
  counter plus 1 and the **clock bound**: the wall clock's microseconds since the Unix epoch plus 1,000,000. Because the agent issues
  far fewer than 1,000,000 numbers per second, a number is never more than 1,000,000 ahead of the clock's microseconds when it is
  issued, so a later fresh start lies above it unless the clock was set back by more than the time between them. Numbers stay below
  2^63 - 1, which the backend stores, for about 292,000 years. An agent's first number is therefore large, and a fresh start or a
  crash leaves a jump: the backend's sequence detection (#41) does not report numbers below an agent's first number, and reports a
  jump as `sequence_missing` (wire format, *Batch identity*).
- **Memory.** The spool's live memory stays below 4 MiB, apart from the records of a read or hand-over in progress, at every
  `spool.max_size` up to its largest value (64 GiB), with the largest number of files (1,024, E19), 64 acknowledged ranges and the
  directory of E7; it does not grow with the number of records held. Its persisted acknowledgement state is bounded the same way.
- **Start-up time.** Opening a spool filled to the default `spool.max_size`, and one holding 1,024 files, each takes at most 5
  seconds on the development host (a benchmark, recorded in `docs/BENCHMARKS.md`); it is not measured on the server here (#44).
- **Security (security area 9).** Spool files are created with mode 0600 inside the existing `spool.directory`, which the agent never
  creates; no link is followed, also one inside the directory, no file with a second hard link is written, and nothing outside the
  spool's own files is written; only the spool's own files are counted and deleted.
  Records hold no secret: the agent token is never written to the spool. Error texts and log lines name no path, only fixed spool
  file names and fixed reasons.
- **Compatibility.** Linux only. An existing configuration without the new keys keeps working with the defaults; an existing
  `spool.directory` that holds `agent-state.json` becomes the spool's directory without migration. The spool's on-disk format carries
  a version so a later agent can read or refuse it (E6). Go standard library preferred; a new dependency needs a decision record.
- **New configuration.** `spool.max_size` (default `2GiB`, 64 MiB to 64 GiB) and `spool.min_free` (default `1GiB`, 0 to 1024 GiB,
  zero switches the check off), each one to five ASCII digits followed directly by `MiB` or `GiB`.
- **New metric.** `agent_spool_age_seconds` (unit `seconds`, no labels): from the capture time of the oldest unacknowledged record to
  the sample's start. `agent_spool_bytes` keeps its name and unit.
- **Logs.** Through the agent's logger with `component=spool`, one line per change, never a record's content: info `spool created`;
  warning `spool data dropped` (`reason`: `size_bound`, `disk_space` or `file_limit`, `records`) at the first eviction of an episode and info `spool
  data no longer dropped` (`records`: the episode's total) when a hand-over fits again without eviction; warning `spool write failed`
  (`err`) and info `spool writes again`; warning `spool data damaged` (`file`, `err`); warning `spool records of another agent
  dropped` (`records`); warning `spool not closed`.

## Acceptance criteria

- [ ] AC1: The agent hands every record that passed the scheduler's checks to the spool instead of `DiscardSink`; each kept record
  carries a sequence number strictly above every earlier one, in hand-over order, with the agent ID, the boot ID read at start and no
  clock offset estimate, and reads back unchanged (S1, E13, E17).
- [ ] AC2: A read from a named sequence number (0: from the oldest kept record) returns the kept, unacknowledged records after it in
  sequence order, at most the requested number of records and bytes, never two boot IDs or clock offset estimates in one result,
  never a record twice or one whose write has not completed (S2, S9, E13).
- [ ] AC3: An acknowledged range is never handed out again, also after a restart; the newest records can be acknowledged before the
  older ones; an acknowledgement of an unknown, already acknowledged, dropped or never handed out number changes nothing; one that
  would make a 65th disjoint acknowledged range is refused with an error and changes nothing; once every record is acknowledged
  `agent_spool_bytes` is at most one sixteenth of `spool.max_size` (S2, S9, E12).
- [ ] AC4: After a stop with SIGTERM and a new start, and after a simulated clean reboot (a new boot ID), every accepted record is kept
  unchanged with its boot ID, the next number follows the last issued one, and no read mixes the boot IDs; at the stop the spool is
  made durable before the state file's last save and closed after it, and a spool step that hangs is given up after 5 seconds with
  `spool not closed` (S4, E14).
- [ ] AC5: Hard-kill test: a process that writes records into a spool is killed with SIGKILL at varied moments, at least 20 times; after
  each kill a new spool on the same directory holds every accepted record, intact and in order, hands out no damaged record and
  issues no number used before (S5).
- [ ] AC6: A spool whose files are cut at arbitrary offsets, or whose newest writes are rolled back to an older state of the
  directory with a new boot ID (a simulated power loss), hands out no damaged record and issues no number it had issued before the
  cut; and a state save waits until the records accepted before it are durable, checked with a spool whose sync is held back (E5).
- [ ] AC7: Simulated 7-day outage: 7 days of the reference stream at one sixteenth of every daily count, written with an injected
  clock into a spool of one sixteenth of the default `spool.max_size` (128 MiB), are all kept, no `spool_dropped` gap is written, and
  reading and acknowledging from the oldest onwards returns every record exactly once in order and leaves the spool empty; the test
  reports the stream's encoded bytes per day at full scale. The same test at full scale (the default 2 GiB) runs when the
  environment variable `VANDOX_LONG_TESTS` is `1`; it is run once before the pull request and its result is recorded (S2, S3).
- [ ] AC8: Size cap: a spool fed beyond `spool.max_size` never holds more than it, measured as the sum of the sizes of its files on
  disk after every hand-over, drops only its oldest unacknowledged records,
  writes one `spool_dropped` gap per run of dropped numbers that contains no kept or acknowledged number (source `agent`,
  `first_seq` and `last_seq` the run's lowest and highest number, `from` and `to` the earliest and latest dropped capture time or
  span of a dropped gap, `to` at least one second after `from`), and logs `spool data dropped` with `reason` `size_bound` once per
  episode and `spool data no longer dropped` at its end; with 1,024 files it drops the same way with `reason` `file_limit`, and a
  restart in the same boot adds no file (S6, E10, E19).
- [ ] AC9: With an injected file-system statistic, a write that would leave less than `spool.min_free` available drops as in AC8 with
  `reason` `disk_space` first; with an empty spool and too little space the hand-over is refused and its gap written by the next write
  that fits; an available space smaller than the write itself, with a non-zero `spool.min_free`, never lets the write through; with
  `0MiB` or `0GiB` the check is off (S7, E11).
- [ ] AC10: Damaged data (a flipped byte, a file cut short, foreign bytes, an unknown format version, a file name's number that is
  not its first record's, numbers that do not increase within or across files, a record number that differs from its line's, a
  summary that disagrees with the last record, a time outside the wire range, an entry of E8 — among them a symbolic and a hard
  link to `agent-state.json`, whose bytes, size and modification time stay unchanged — and the data files beyond the 1,026 newest
  of E19) is never handed out, at start and
  when found during a read: the records before it and in other files are, `spool_dropped` gaps name the dropped numbers where
  known and never carry a damaged value, and `spool data damaged` is logged once per file (E6, E8).
- [ ] AC11: Foreign entries, `agent-state.json` and `agent-state.json.tmp` among them, are never read, counted, overwritten or deleted,
  and a directory with 100,000 of them opens within the memory bound (E7).
- [ ] AC12: A failing write (injected: no space, an I/O error, read-only) refuses the hand-over with an error at once, logs `spool write
  failed` once and `spool writes again` at the next success, then writes one `gap` with cause `unknown`, source `agent`, from the
  earliest to the latest capture time of all records refused in the episode (at least one second); a hand-over larger than the whole
  cap is refused the same way (E9, E11).
- [ ] AC13: Start-up: a missing directory, a file or a symbolic link in its place (the link with the reason `symbolic link`), a
  directory path that is not absolute and clean, a refused entry in place of `spool-meta.json` or its temporary file, a directory the agent cannot write, and an unreadable
  or malformed boot ID each make the agent log `agent cannot start` with a `spool:` error without a path and exit with 1 before
  `READY=1`; an empty directory, or one holding only `agent-state.json`, logs `spool created` and no warning from spool or state file
  (E2, E3, E15).
- [ ] AC14: Sequence numbers with an injected clock: a fresh spool starts at the clock bound; a new spool in a directory whose earlier
  spool files were removed issues numbers above every number of the earlier one, also when the earlier one issued a burst of 100,000
  numbers within one microsecond of the clock and the clock then moved on by one second; an unclean stop in an earlier boot with a
  rolled-back counter continues above the clock bound; after a `kill -9` in the same boot with every sealed file acknowledged and
  deleted and an empty newest file, the next number lies above that file's name; a meta file that breaks a consistency rule of E20
  is damaged, its acknowledgements discarded and the next number taken as on a fresh spool; a hand-over whose numbers would reach
  2^63 - 1 is refused with `spool: sequence numbers exhausted` (E4, E5, E20, E21).
- [ ] AC15: The `self` sample holds `agent_spool_bytes` (the size of the spool's files only) and `agent_spool_age_seconds` (0 for an
  empty spool and for an oldest capture time after the sample's start) (S8, E16).
- [ ] AC16: `spool.max_size` and `spool.min_free` are read with their defaults and rules, and every value of E1 is refused with its
  error text, which never contains the value (S10, E1).
- [ ] AC17: The spool's live memory stays below 4 MiB with `spool.max_size` 64 GiB and 1,024
  files left by simulated reboots, 64 acknowledged ranges, reads from both ends and 100,000 foreign entries; opening a spool filled to
  the default cap and one holding 1,024 files is measured by a benchmark recorded in `docs/BENCHMARKS.md` (NFR *Memory*, *Start-up
  time*).
- [ ] AC18: A spool holding records of another agent ID, opened under a new ID, hands none of them out, writes no gap for them, logs
  `spool records of another agent dropped` with their count, and issues the new ID's numbers from the clock bound (E18).
- [ ] AC19: After the agent ran with an agent token set, no spool file holds the token's bytes (NFR *Security*).

## Out of scope

- Sending: batching, gzip, the HTTP request, retry with backoff, current data first, the bandwidth limit, the `mode` hint and
  `agent_send_lag_seconds` (#39). The spool offers reading from any sequence number and acknowledgement of ranges, which the sender
  needs; it decides no order of its own.
- The ingest API and its acknowledgement (#40) and the backend's classification and gap tracking (#41), which must follow NFR
  *Sequence numbers* for an agent's first number and its jumps.
- A clock offset estimate: no part of the agent measures one today, so every record is spooled without one (the header's "unknown");
  the spool keeps the field so a later estimate needs no format change.
- Exact log delivery across a crash (record 0121: storing the `logs` positions with the records). Log lines read after the last
  saved position repeat after a crash as today; the spool never makes one get lost (E5).
- A disk whose writes hang: a hand-over that blocks holds up the collectors waiting on it, the watchdog is no longer fed and systemd
  restarts the agent; isolating the spool from a stalled disk is not part of this feature.
- Creating the directory and its rights, and the unit's `Restart=` (E2), in the unit and install script (#42).
- Measuring the real volume and the footprint on the server (#44).
- A maximum age of spooled data: records stay until acknowledged or dropped for size or disk space.
- A gap pending from E9 when the agent stops while writes still fail is lost with the agent; the backend's time-gap detection (#41)
  covers that span.
- Saving the boot and backup state only after the run's records are kept (the E5 residual): the follow-up issue "[Agent] Save boot
  and backup state only after the run's records are kept", to be merged before the agent's first release.

## Open questions

- What does "retention of at least 7 days" mean next to the size cap? Answered: the issue makes the cap the protection of the disk
  and the gap the outcome when it is hit, and records 0028 and 0045 make dropped data a recorded gap; so the cap always wins, the 7
  days are a sizing requirement on the default (NFR *Reference stream and retention*), and there is no age limit.
- Which default cap? Answered: `2GiB` (Lead decision, raised from 1 GiB after the challenge). No document states the server's disk
  size or the agent's data volume; the reference stream's estimate puts 7 days near 1.2 GB uncompressed, and `spool.min_free` now
  guards the disk, so the larger default costs no safety and holds 7 days without relying on compression.
- Does "never fill the disk" need more than the size cap? Answered: yes (Lead decision). A cap alone fills a disk that has less free
  space than the cap; `spool.min_free` (default `1GiB`) keeps room for the server's other services, which write as unprivileged users
  too. The agent is unprivileged, so the file system's root reserve is never touched in any case.
- Where do the boot ID and the clock offset of a record come from? Answered: from the code, the boot ID is read from
  `/proc/sys/kernel/random/boot_id` (`procstats.ReadBootID`, used by `internal/backups`), and it cannot change while the agent runs;
  no clock offset estimate exists in the agent (`internal/sysstats` records the kernel's clock errors, not an offset), and the
  header's `clock_offset_ns` is optional, omitted meaning unknown (wire format, *Header*).
- Who reads and acknowledges the spool before #39 and #40 exist? Answered: the tests, through the spool's read and acknowledge
  operations; the running agent only writes, so without a sender its spool fills up to the cap and then drops the oldest with gaps,
  which is acceptable because the agent is not released before #39 (agent area, *Status*; record 0110).
- Does a full spool refuse records? Answered: no. The issue asks to drop the oldest data, so a full spool takes new records and evicts
  old ones; only a failed write or E11 refuses. The trade-off notes of the `kernel` and `logs` collectors and record 0120 that name "a
  full spool" as a lasting refusal are corrected.
- Must a lost spool keep the counter? Answered: the wire format area (*Batch identity*) and record 0045 say the counter must stay
  persistent or the agent needs a new ID; an agent ID is configuration, so the agent cannot change it, and NFR *Sequence numbers*
  puts a fresh spool's numbers above the lost ones instead.

## Area documents

- [Agent](../../docs/areas/agent.md): *Spool and sending* becomes the spool's full rules (what is kept per record, sequence numbers
  and the clock bound, reading and acknowledgement of ranges, the size bound and `spool.min_free` with the `spool_dropped` gaps,
  damaged data, write failures and their gap, another agent ID, start-up failures, the stop order, durability before a state save,
  memory, logging, the reference stream's measured bytes per day); *Status*, *Scheduling* ("until the spool exists"),
  *Self-metrics* (spool size and age), *Agent state* (no longer the only write; first start beside the spool; a save waits for the
  spool's durability; the "missing directory" case can no longer occur at start), *Kernel events* (the warning `boot ID not
  readable` can no longer occur at start, since the start already read it), *Rights* (the spool's files), *Gaps* and the trade-off
  notes of *Kernel events* and *Log shipping* ("a full spool") are updated; *Implementation* names the new package.
- [Configuration and secrets](../../docs/areas/configuration-and-secrets.md): the options `spool.max_size` and `spool.min_free` with
  defaults, rules and error texts; `spool.directory` holds the spool's files and the state file.
- [Wire format](../../docs/areas/wire-format.md): the metric `agent_spool_age_seconds`; *Batch identity* states the clock bound, the
  large first number and the jumps after a fresh start or a crash.
- `docs/ARCHITECTURE.md`: the status line (the agent no longer drops what it collects) and the statement that the state file is the
  agent's only file write.

## Challenge

- M1 (state positions durable before the records they cover). Accepted. NFR *Durability* now requires every accepted record to be
  durable before the state file saves a change; E5 says positions, kills, boots and backup changes are never saved ahead of the
  records, so the `kernel` and `logs` collectors read those entries again after a crash; E14 and AC4 put the stop order as spool
  durable, last state save, spool closed; AC6 checks a state save held back by the spool's sync; the out-of-scope line now says lines
  after the last saved position repeat and the spool never loses one.
- M2 (acknowledgements in any order). Accepted, narrowed. Acknowledgements are ranges; the spool tracks at most 64 disjoint
  acknowledged ranges above its oldest unacknowledged record, enough for #39's two streams, and refuses one more with an error and no
  effect (E12, AC3); NFR *Memory* and AC17 include the 64 ranges. A `spool_dropped` gap is written per run of dropped numbers, so one
  eviction writes at most 65 gaps (S6, AC8).
- M3 (7-day capacity rests on an unreproducible estimate). Accepted. The reference stream is now defined reproducibly (records per day
  by kind, varying content from a fixed seed); the test reports its encoded bytes per day, which go into the agent area. The default
  is raised to `2GiB`: about 1.2 GB for 7 days fits uncompressed, and `spool.min_free` guards the disk.
- m1 (E2 against E9). Accepted: E9 says why the agent keeps running and that a refusal returns at once, so the watchdog keeps being
  fed; E2 names what #42's unit must do and that "symbolic link" means the last element only; the agent area marks the two rules that
  can no longer occur at start.
- m2 (seed rules). Accepted: NFR *Sequence numbers* states the clock bound (microseconds plus 1,000,000), when it applies (fresh
  spool, unclean stop in an earlier boot), the rate it relies on, the 2^63 - 1 limit and what #41 must do with the first number and
  the jumps; AC14 tests the burst, the rollback and the limit.
- m3 (changed `agent_id`). Accepted: E18 and AC18. The records of another ID are dropped with a warning and without a gap, because a
  gap sent under the new ID would describe another agent's data; the option of refusing to start was rejected, since a deliberate
  rename would then need the operator to wipe the directory.
- m4 (`spool.min_free` arithmetic). Accepted: NFR *Disk* checks available space minus the write; an empty spool with too little space
  refuses (E11, AC9) and the episode's gap is written by the next write that fits; `0MiB` and `0GiB` both switch it off.
- m5 (union time spans). Accepted as a stated residual in E10: gaps are split per run of dropped numbers, so the sequence range is
  exact, while the time span may include moments of which other records were kept; writing one gap per contiguous time span is
  not possible, because capture times of different collectors interleave.
- Lead, while planning: E1 claimed the size rule's text also for a list and for `~`; the strict decoder (`internal/config/decode.go`
  `setLeaf`) answers those before any option rule, with `invalid value, want a string` and `has no value`. E1 now says so.
- Plan review (Devil's Advocate and Security, answered in plan.md *Challenge* and *Security review*): E5 now states the boot and backup
  residual; E6 names the consistency rules and the read-time behavior; E8 names the meta file's own names; E19 (file limit), E20
  (meta consistency) and E21 (numbers exhausted) are new; E3 states the residual state-file warning; NFR *Size bound*, *Disk*,
  *Memory*, *Start-up time* and the `spool.max_size` maximum (now 64 GiB) changed; AC7 runs scaled with a full-scale run on demand;
  AC8, AC9, AC10, AC13, AC14 and AC17 cover the new cases; AC19 is new.
- Security, plan review round 2 (answered in plan.md *Security review*, R1 and N7): E8 now names a link to a file inside the
  directory and a second hard link, NFR *Security* says the same, E19 bounds the data files a start holds, and AC10 covers both.
- Product Manager, on the escalated plan review note M1: the boot and backup window of E5 stays a stated residual in this feature,
  the guarantee *No data gaps unless explicitly recorded* keeps its text, and the follow-up issue closes the window before the agent's
  first release (E5 and *Out of scope* say so).
