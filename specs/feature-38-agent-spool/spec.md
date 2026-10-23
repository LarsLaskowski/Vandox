# Spec: Persistent on-disk spool (at least 7 days)

Source: Issue #38
Status: Draft

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
`spool.directory` (default `/var/lib/vandox/spool`) beside the agent's state file `agent-state.json`.

- S1: Given the agent runs with a usable spool directory, when a collector run ends and the scheduler hands its records to the sink,
  then the spool keeps them, each with a sequence number above every number the agent issued before, in the order they were handed
  over, together with the boot ID of the boot they were captured in and their clock offset estimate (unknown today); the agent no
  longer drops them.
- S2: Given the backend has been unreachable from 22:00 to 09:00 and the spool holds those 11 hours of records, when the reader reads
  from the oldest kept record onwards in pieces and acknowledges each piece, then it receives every record exactly once, in sequence
  order, each piece holding records of one boot ID and one clock offset estimate only, and once all are acknowledged the spool's
  files shrink accordingly.
- S3: Given an outage of 7 days at the reference volume (*Non-functional requirements*) with the default `spool.max_size`, when the
  backend becomes reachable again, then the spool still holds every record of the 7 days, has dropped nothing and recorded no
  `spool_dropped` gap, and S2's reading delivers all of it.
- S4: Given the spool holds unacknowledged records, when the agent is stopped with SIGTERM and started again, or the server reboots,
  then every record the spool had accepted is still there unchanged, the sequence numbers go on above the old ones, records captured
  before the reboot keep the old boot ID and records after it carry the new one, and no piece the reader receives mixes the two.
- S5: Given the agent is writing records into the spool, when it is killed with `kill -9` at any moment and started again, then every
  record whose hand-over to the spool had completed is there and intact, no damaged record is ever handed to the reader, and no
  sequence number is issued a second time.
- S6: Given the spool has reached `spool.max_size` because the backend has been away too long, when further records arrive, then the
  spool drops its oldest unacknowledged records to make room, writes a `gap` record with cause `spool_dropped` that names the dropped
  sequence numbers and the span of their capture times, logs the warning `spool data dropped` once for the episode, and its size
  never exceeds `spool.max_size`.
- S7: Given other programs have filled the server's disk so that the space available on the spool's file system is below
  `spool.min_free`, when further records arrive, then the spool does not grow: it makes room by dropping its oldest unacknowledged
  records as in S6, with a `spool_dropped` gap and the warning's `reason` `disk_space`.
- S8: Given the agent runs, when the `self` collector takes its sample (every 60 seconds), then the sample holds `agent_spool_bytes`,
  the size of the spool's files, and `agent_spool_age_seconds`, the age of the oldest unacknowledged record (0 when the spool holds
  none).
- S9: Given the spool holds a backlog, when the reader reads the newest records (from a sequence number it names) and acknowledges
  them before the older ones, then those records are never handed out again, the older ones remain readable in sequence order, and
  acknowledgements are accepted in any order.
- S10: Given the operator writes `spool.max_size: 2GiB` and `spool.min_free: 512MiB` into `/etc/vandox/agent.yaml`, when the agent
  starts, then the spool keeps at most 2 GiB and stops growing when less than 512 MiB are available; without the keys the defaults
  `1GiB` and `1GiB` apply.

## Errors and edge cases

- E1: A `spool.max_size` or `spool.min_free` value that breaks its rule (`1024`, `1 GiB`, `1.5GiB`, `1GB`, `-1GiB`, `32MiB` for the
  size, `2048GiB`, a list, `~`): the agent does not start; the error reads `config: <file>:<line>: spool.max_size: must be a whole
  number of MiB or GiB from 64MiB to 1024GiB, such as 512MiB or 2GiB` (for `spool.min_free`: `... from 0MiB to 1024GiB, such as
  0MiB or 1GiB`) and never contains the value; exit code 1, as for every configuration error.
- E2: At start the spool directory is missing, is not a directory, is a symbolic link or cannot be written by the agent's user: the
  agent logs `agent cannot start` with the error `spool: <fixed reason or system error text>` (never the path) and exits with 1
  before it notifies systemd, so `systemctl status` shows the failure instead of an agent that silently discards its data.
- E3: First start in an empty spool directory, or a directory that holds only other entries such as `agent-state.json`: the spool is
  created and logged once as info `spool created`; neither the spool nor the state file logs a warning for it (the state file treats
  only a directory that is empty at its read as a first start, so the order of the two at start-up matters).
- E4: The spool's files are lost while the agent ID stays (reinstall, a wiped or replaced directory): the new spool's sequence numbers
  start above every number the lost spool can have issued, unless the wall clock now reads earlier than when those numbers were
  issued (stated residual); the backend would otherwise take the new records for duplicates of stored ones and drop them silently.
- E5: The server loses power or the kernel crashes: records accepted more than 5 seconds before may not be lost; records accepted
  later may be missing, but no damaged record is handed out and no sequence number that was issued before the crash is issued again,
  so the backend sees the missing numbers as a sequence gap (#41).
- E6: Damaged spool data, found at start or while reading (a checksum that does not match, a file cut short, bytes that are not the
  spool's format, a spool file of a format version the agent does not know): the damaged records are never handed out; the records
  before the damage in that file and all other files stay readable; the spool writes one `spool_dropped` gap naming the sequence
  numbers of the dropped records where it can know them (otherwise the backend's sequence detection finds them); the warning
  `spool data damaged` (`file`: the spool file's name, `err`: a fixed reason) is logged once per file.
- E7: Entries in the directory that are not the spool's files (`agent-state.json`, `agent-state.json.tmp`, other files,
  subdirectories, up to 100,000 of them): never read, counted, overwritten or deleted by the spool; the spool's memory bound holds.
- E8: An entry with the name of a spool file that is a symbolic link, not a regular file, owned by another user or with any group or
  other permission bit: never followed, opened for writing, renamed or deleted; it is not counted, and the data the spool expected
  there is treated as damaged (E6).
- E9: A write fails at run time (no space left, an I/O error, the file system remounted read-only): the spool refuses the records it
  could not keep, so the scheduler logs `sink failed` as today; the spool logs the warning `spool write failed` (`err`, never a path)
  once per episode and the info `spool writes again` when a write succeeds, and then writes one `gap` with cause `unknown`, source
  `agent`, covering the capture times of every record refused in the episode.
- E10: The spool stays full for weeks: every eviction writes a gap; when the records dropped include earlier gap records, the new gap
  also covers their spans, so no dropped span is ever left unrecorded; successive gaps describe adjoining spans; the size never
  exceeds `spool.max_size`, the gap records included.
- E11: The records of one hand-over do not fit even after every unacknowledged record was dropped (a cap smaller than one run's
  records): they are refused and recorded as in E9.
- E12: The reader acknowledges a sequence number the spool never issued, one already acknowledged or dropped, or one it was never
  handed: nothing changes and no other record is affected.
- E13: Many collectors hand over records at the same moment while the reader reads and acknowledges: every record gets exactly one
  sequence number, numbers increase strictly in the spool's order, and a read never returns a record twice or a record whose write
  has not completed.
- E14: SIGTERM while records are being written: every hand-over that completed is kept; the spool is closed after the scheduler has
  stopped and after the state file's last save; a close that does not finish within 5 seconds is given up with the warning
  `spool not closed` and the agent exits as usual (records accepted earlier are kept as after `kill -9`, S5).
- E15: The boot ID cannot be read from `/proc/sys/kernel/random/boot_id` at start (missing, not a lower-case UUID): the agent logs
  `agent cannot start` with `spool: boot id: <fixed reason>` and exits with 1.
- E16: The wall clock was set back, so capture times in the spool do not increase: the order stays the sequence order;
  `agent_spool_age_seconds` is 0, never negative, when the oldest record's capture time lies after the sample's start.
- E17: A record of the largest size the wire format allows (a 1 MiB process or connection snapshot) is kept and read back like any
  other.

## Non-functional requirements

- **Retention.** At the **reference volume** the default `spool.max_size` holds at least 7 days. The reference volume is 150 MB per
  day of records as the wire format encodes them (uncompressed JSON lines), made of records like the agent's own outside memory
  pressure (the process and connection snapshots, system metrics and log lines of the test data). The figure is an estimate from the
  collectors' intervals and record sizes, not a measurement; #44 measures the real spool growth on the server and reports it against
  the 7 days. Under sustained memory pressure the agent records much more (every process every 15 seconds), so less than 7 days may
  fit; the cap then drops the oldest data with a gap (the stated residual of *Memory pressure mode* in the agent area).
- **Size bound.** `agent_spool_bytes` is the total size of the spool's own files and never exceeds `spool.max_size`. Once every
  record is acknowledged it is at most one sixteenth of `spool.max_size`.
- **Disk.** The spool never grows while the space available to unprivileged users on its file system (`statfs` `f_bavail`) is below
  `spool.min_free`.
- **Durability.** A record survives `kill -9` once its hand-over returned, and an OS crash or power loss once 5 seconds have passed
  since. A sequence number is never issued twice by one agent ID (E4's residual aside).
- **Memory.** The spool's live memory stays below 4 MiB, apart from the records of a read or hand-over in progress, at every spool
  size up to `spool.max_size` and with the directory of E7; it does not grow with the number of records held.
- **Start-up time.** Opening a spool filled to the default `spool.max_size` takes at most 5 seconds on the development host
  (a benchmark, recorded in `docs/BENCHMARKS.md`); it is not measured on the server here (#44).
- **Security (security area 9).** Spool files are created with mode 0600 inside the existing `spool.directory`, which the agent never
  creates; no link is followed and nothing outside the directory is written; only the spool's own files are counted and deleted.
  Records hold no secret: the agent token is never written to the spool. Error texts and log lines name no path, only fixed spool
  file names and fixed reasons.
- **Compatibility.** Linux only. An existing configuration without the new keys keeps working with the defaults; an existing
  `spool.directory` that holds `agent-state.json` becomes the spool's directory without migration. The spool's on-disk format carries
  a version so a later agent can read or refuse it (E6). Go standard library preferred; a new dependency needs a decision record.
- **New configuration.** `spool.max_size` (default `1GiB`, 64 MiB to 1024 GiB) and `spool.min_free` (default `1GiB`, 0 to 1024 GiB,
  `0MiB` switches the check off), each one to five ASCII digits followed directly by `MiB` or `GiB`.
- **New metric.** `agent_spool_age_seconds` (unit `seconds`, no labels): from the capture time of the oldest unacknowledged record to
  the sample's start. `agent_spool_bytes` keeps its name and unit.
- **Logs.** Through the agent's logger with `component=spool`, one line per change, never a record's content: info `spool created`;
  warning `spool data dropped` (`reason`: `size_bound` or `disk_space`, `records`) at the first eviction of an episode and info `spool
  data no longer dropped` (`records`: the episode's total) when a hand-over fits again without eviction; warning `spool write failed`
  (`err`) and info `spool writes again`; warning `spool data damaged` (`file`, `err`); warning `spool not closed`.

## Acceptance criteria

- [ ] AC1: The agent hands every record that passed the scheduler's checks to the spool instead of `DiscardSink`; each kept record
  carries a sequence number strictly above every earlier one, in hand-over order, with the boot ID read at start and no clock offset
  estimate, and reads back unchanged (S1, E13, E17).
- [ ] AC2: A read from a named sequence number (0: from the oldest kept record) returns the kept, unacknowledged records after it in
  sequence order, at most the requested number of records and bytes, never two boot IDs or clock offset estimates in one result,
  never a record twice or one whose write has not completed (S2, S9, E13).
- [ ] AC3: An acknowledged record is never handed out again, also after a restart; acknowledgements are accepted in any order; an
  acknowledgement of an unknown, already acknowledged, dropped or never handed out number changes nothing; once every record is
  acknowledged `agent_spool_bytes` is at most one sixteenth of `spool.max_size` (S2, S9, E12).
- [ ] AC4: After a stop with SIGTERM and a new start, and after a simulated reboot (a new boot ID), every accepted record is kept
  unchanged with its boot ID, new numbers go on above the old ones, and no read mixes the boot IDs; a close that hangs is given up
  after 5 seconds with `spool not closed` (S4, E14).
- [ ] AC5: Hard-kill test: a process that writes records into a spool is killed with SIGKILL at varied moments, at least 20 times; after
  each kill a new spool on the same directory holds every record whose hand-over had returned, intact and in order, hands out no
  damaged record and issues no number used before (S5).
- [ ] AC6: A spool whose files are cut at arbitrary offsets, or whose newest writes are rolled back to an older state of the
  directory (a simulated power loss), hands out no damaged record and issues no number it had issued before the cut (E5).
- [ ] AC7: Simulated 7-day outage: 7 days of records at the reference volume, written with an injected clock into a spool with the
  default `spool.max_size`, are all kept, no `spool_dropped` gap is written, and reading and acknowledging from the oldest onwards
  returns every record exactly once in order and leaves the spool empty (S2, S3).
- [ ] AC8: Size cap: a spool fed beyond `spool.max_size` never measures more than it, drops only its oldest unacknowledged records,
  writes a `spool_dropped` gap (source `agent`, `first_seq` and `last_seq` the lowest and highest dropped number, `from` and `to` the
  earliest and latest dropped capture time or span of a dropped gap, `to` at least one second after `from`) that never names a
  number between them that is still kept or was acknowledged, and logs `spool data dropped` with `reason` `size_bound` once per
  episode and `spool data no longer dropped` at its end (S6, E10).
- [ ] AC9: With an injected file-system statistic whose available space is below `spool.min_free`, the spool does not grow, drops as in
  AC8 with `reason` `disk_space`, and with `0MiB` the check is off (S7).
- [ ] AC10: Damaged data — a flipped byte, a file cut short, foreign bytes, an unknown format version, and an entry of E8 — is never
  handed out, the records before it and in other files are, one `spool_dropped` gap names the dropped numbers where known, and
  `spool data damaged` is logged once per file (E6, E8).
- [ ] AC11: Foreign entries, `agent-state.json` and `agent-state.json.tmp` among them, are never read, counted, overwritten or deleted,
  and a directory with 100,000 of them opens within the memory bound (E7).
- [ ] AC12: A failing write (injected: no space, an I/O error, read-only) refuses the hand-over with an error, logs `spool write
  failed` once and `spool writes again` at the next success, then writes one `gap` with cause `unknown`, source `agent`, from the
  earliest to the latest capture time of all records refused in the episode (at least one second); a hand-over larger than the whole
  cap is refused the same way (E9, E11).
- [ ] AC13: Start-up: a missing directory, a file or a symbolic link in its place, a directory the agent cannot write, and an unreadable
  or malformed boot ID each make the agent log `agent cannot start` with a `spool:` error without a path and exit with 1 before
  `READY=1`; an empty directory, or one holding only `agent-state.json`, logs `spool created` and no warning from spool or state file
  (E2, E3, E15).
- [ ] AC14: A new spool in a directory whose earlier spool files were removed issues numbers above every number of the earlier spool
  when the injected clock moved forward between the two (E4).
- [ ] AC15: The `self` sample holds `agent_spool_bytes` (the size of the spool's files only) and `agent_spool_age_seconds` (0 for an
  empty spool and for an oldest capture time after the sample's start) (S8, E16).
- [ ] AC16: `spool.max_size` and `spool.min_free` are read with their defaults and rules, and every value of E1 is refused with its
  error text, which never contains the value (S10, E1).
- [ ] AC17: The spool's live memory stays below 4 MiB at a spool filled to its cap and with 100,000 foreign entries, and opening a spool
  filled to the default cap is measured by a benchmark recorded in `docs/BENCHMARKS.md` (NFR *Memory*, *Start-up time*).

## Out of scope

- Sending: batching, gzip, the HTTP request, retry with backoff, current data first, the bandwidth limit, the `mode` hint and
  `agent_send_lag_seconds` (#39). The spool offers reading from any sequence number and acknowledgement in any order, which the sender
  needs; it decides no order of its own.
- The ingest API and its acknowledgement (#40) and the backend's classification and gap tracking (#41).
- A clock offset estimate: no part of the agent measures one today, so every record is spooled without one (the header's "unknown");
  the spool keeps the field so a later estimate needs no format change.
- Exact log delivery across a crash (record 0121: storing the `logs` positions with the records); log lines repeat after a crash as
  today.
- A disk whose writes hang: a hand-over that blocks holds up the collectors waiting on it, the watchdog is no longer fed and systemd
  restarts the agent; isolating the spool from a stalled disk is not part of this feature.
- Creating the directory and its rights in the unit and install script (#42).
- Measuring the real volume and the footprint on the server (#44).
- A maximum age of spooled data: records stay until acknowledged or dropped for size or disk space.
- A gap pending from E9 when the agent stops while writes still fail is lost with the agent; the backend's time-gap detection (#41)
  covers that span.

## Open questions

- What does "retention of at least 7 days" mean next to the size cap? Answered: the issue makes the cap the protection of the disk
  and the gap the outcome when it is hit, and records 0028 and 0045 make dropped data a recorded gap; so the cap always wins, the 7
  days are a sizing requirement on the default (NFR *Retention*), and there is no age limit.
- Which default cap? Answered: `1GiB` (Lead decision). No document states the server's disk size or the agent's data volume; the estimate of
  150 MB per day uncompressed puts 7 days near 1 GB, so the default needs a compact on-disk form (the plan's choice) and the
  operator can raise it; #44 measures the real growth. A larger default would take more of a disk nobody has sized.
- Does "never fill the disk" need more than the size cap? Answered: yes (Lead decision). A cap alone fills a disk that has less free space than
  the cap; `spool.min_free` (default `1GiB`) keeps room for the server's other services, which write as unprivileged users too.
  The agent is unprivileged, so the file system's root reserve is never touched in any case.
- Where do the boot ID and the clock offset of a record come from? Answered: from the code, the boot ID is read from
  `/proc/sys/kernel/random/boot_id` (`procstats.ReadBootID`, used by `internal/backups`), and it cannot change while the agent runs;
  no clock offset estimate exists in the agent (`internal/sysstats` records the kernel's clock errors, not an offset), and the
  header's `clock_offset_ns` is optional, omitted meaning unknown (wire format, *Header*).
- Who reads and acknowledges the spool before #39 and #40 exist? Answered: the tests, through the spool's read and acknowledge
  operations; the running agent only writes, so without a sender its spool fills up to the cap and then drops the oldest with gaps,
  which is acceptable because the agent is not released before #39 (agent area, *Status*; record 0110).
- Does a full spool refuse records? Answered: no. The issue asks to drop the oldest data, so a full spool takes new records and evicts
  old ones; only a failed write refuses (E9). The trade-off notes of the `kernel` and `logs` collectors and record 0120 that name "a
  full spool" as a lasting refusal are corrected in the agent area.
- Must a lost spool keep the counter? Answered: the wire format area (*Batch identity*) and record 0045 say the counter must stay
  persistent or the agent needs a new ID; an agent ID is configuration, so the agent cannot change it, and E4 asks that a fresh
  spool's numbers lie above the lost ones instead.

## Area documents

- [Agent](../../docs/areas/agent.md): *Spool and sending* becomes the spool's full rules — what is kept per record, sequence
  numbers and their persistence (E4, E5), reading and acknowledgement, the size bound and `spool.min_free` with the `spool_dropped`
  gap, damaged data, write failures and their gap, start-up failures, closing at the stop, durability, memory, logging; *Status*,
  *Scheduling* ("until the spool exists"), *Self-metrics* (spool size and age), *Agent state* (no longer the only write; first start
  beside the spool), *Rights* (the spool's files), *Gaps* and the trade-off notes of *Kernel events* and *Log shipping* ("a full spool")
  are updated; *Implementation* names the new package.
- [Configuration and secrets](../../docs/areas/configuration-and-secrets.md): the options `spool.max_size` and `spool.min_free` with
  defaults, rules and error texts; `spool.directory` holds the spool's files and the state file.
- [Wire format](../../docs/areas/wire-format.md): the metric `agent_spool_age_seconds`; *Batch identity* states how a lost spool keeps
  its numbers above the old ones.
- `docs/ARCHITECTURE.md`: the status line (the agent no longer drops what it collects) and the statement that the state file is the
  agent's only file write.
