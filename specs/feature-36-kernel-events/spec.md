# Spec: Kernel events, reboots and backup runs

Source: Issue #36
Status: Approved by Product Manager

## Problem

The operator of the monitored server (Ubuntu 22.04, Plesk 18, 2 GB RAM) has to explain outages after the fact. The issue describes 2 Oct 2026 as a
reboot at 06:51, then Plesk's missed backup, then an OOM kill of MariaDB at 06:59 (server time, UTC+2). The server's real journal
(Q3) shows that chain differently: there was no reboot that day (one boot ran from 30 Sep to 21 Oct); at 04:51:36 UTC the OOM killer
killed `pzstd`, so a backup was already running, and at 04:59:32 UTC it killed `mariadbd`. The next reboot came on 21 Oct at 16:28:27
UTC. In the days before, the same boot holds six more kills of `mariadbd`, `amavisd` and `pzstd`. Today the agent records metrics, process, connection, service and MariaDB snapshots on a fixed grid (#30 to #35), but it
records no kernel event at all: an OOM kill is only visible later, if at all, as a log line the backend imports, the process snapshot
nearest to the kill can be up to a minute (15 seconds under memory pressure) away from it, a reboot is visible only as a jump in
the data, and a backup run only as a `pzstd` process that happens to be in a snapshot. Such a chain — reboot, backup, kill — cannot be
read from the agent's data as one sequence of events with their own times; that the issue's own account of 2 Oct does not match the
journal shows how much a reconstruction from memory and scattered log lines is worth.

What exists to build on (checked in the code): the record kind `kernel_event` with the types `oom_kill` (`victim_pid`,
`victim_command`, `anon_rss_bytes`, `oom_score_adj`) and `boot` (`boot_id`, `previous_boot_id`, `booted_at`,
`previous_uptime_ns`) is in the model and the wire format, but no part of the agent produces it; there is no record for a backup run
and no field that ties a snapshot to an event.

## Scenarios

- S1: Given the agent is running, when the kernel's OOM killer kills a process, then within 5 seconds of the kill line reaching the
  journal the agent records one `kernel_event` of type `oom_kill` with the victim's PID, its command (the kernel's process name),
  its anonymous RSS in bytes (`anon-rss` in kB times 1,024), its `oom_score_adj`, the kill line as `message`, and as capture time the
  moment the kernel wrote the line: the journal's receipt time minus the time the line took to reach the journal. That delay is
  measured on the journal's monotonic clock against the kernel's stamp, less the drift of the kernel's clock as it shows on recent,
  promptly received kernel lines of the same boot (the kernel's clock runs slow by about 33 microseconds per second on the server, a
  minute after three weeks, so the kernel's stamp converted with the boot time would be early); without such lines the delay is 0. The
  capture time is never after the receipt time. The event carries an
  identity that is the same however often the same kill is read and differs between any two kills.
- S2: Given S1, when the event is recorded, then the agent at once (within 1 second, or as soon as the collector's current run ends)
  takes an additional full process snapshot (every process, as under memory pressure) and an additional full connection snapshot
  (with every connection), and each of these two records names the kill it was taken for by the kill's identity. Without memory
  pressure mode the collectors' regular runs keep their schedule: none is skipped and no gap is recorded because of the additional
  run, as long as it finishes within its timeout. Under memory pressure mode (when the regular runs are already full snapshots every
  15 seconds) the additional run takes the place of the collector's next regular run: that run is not taken and records no gap, and
  the grid continues from the additional run's start. The additional run is a sample like any other: it is the predecessor of the
  next run (CPU rates of the processes, the listener comparison of the connections), it sets and clears `Run.PreviousLost` by the same
  rules, and its failures are recorded as for any run (E11).
- S3: Given an OOM storm in which the kernel kills several processes within seconds, then every kill is its own event (S1); the
  first kill gets its additional snapshots at once (S2), and the kills that follow within 15 seconds of the start of those snapshots
  get one further pair of snapshots when the 15 seconds have passed, which names all of them (at most 64, and the number of the
  further kills it does not name). Each collector takes at most one additional snapshot per 15 seconds.
- S4: Given the server rebooted, when the agent starts in the new boot, then it records one `kernel_event` of type `boot` with the
  new boot's ID (`/proc/sys/kernel/random/boot_id`), the boot time as the kernel reports it (`booted_at`, also the capture time),
  the ID of the boot the agent ran in before (`previous_boot_id`) and how long that boot ran (`previous_uptime_ns`): at least as
  long as stated and, when the agent ran until that boot ended, at most one minute less than the real uptime.
- S5: Given the agent is restarted (an update, a crash, `systemctl restart`) within the same boot, when it starts, then it records
  no `boot` event.
- S6: Given the agent starts for the first time on the server (no saved state), then it records one `boot` event for the current
  boot without `previous_boot_id` and without `previous_uptime_ns`.
- S7: Given a configured backup rule (`backups.processes`), when a process that matches it starts, then within 15 seconds the agent
  records the **start** of a backup run: the run's start time (the earliest start time of the matching processes it sees, from
  `/proc`), the command and PID of that earliest process and the number of matching processes.
- S8: Given a running backup run, when no matching process has been seen for 60 seconds, then the agent records the **end** of the
  run: the run's start time (which pairs it with its start), the last time a matching process was seen, the time of the first check
  that saw none ("gone by"; the real end lies between the two) and the largest number of matching processes seen at once.
- S9: Given a backup whose phases run one after another with pauses of less than 60 seconds (a dump, then the compression of each
  part), then it is one run with one start and one end.
- S10: Given kills the agent did not see live — it was stopped, restarting, killed itself by the OOM killer, or the server had not
  yet started it after a boot — when the agent runs again, then every kill line the journal holds after the position the agent last
  read becomes an event with its own capture time, also for the kills of the previous boot and those in a file journald set aside
  after an unclean shutdown or its own restart (`system@...journal~`); a kill read more than 60 seconds after it happened (the time
  since the journal received it plus its delay, S1), or one from another boot, gets no additional snapshot. A run whose records the
  agent did not keep (a timeout, a refused record, a sink error) is read again from the last kept position.
- S11: Given the agent is restarted during a backup run and a matching process still runs, then the run continues: no second
  start is recorded and the end, when it comes, names the original start time.
- S12: Given the agent was stopped or the server rebooted during a backup run and no matching process runs at the agent's first
  check, then the agent records the run's end with the last time it saw a matching process before the stop (at most one minute
  before the stop) and "gone by" the time of that first check; a backup that starts in the new boot (Plesk's missed backup) is a new
  run with its own start.
- S13: Given the chain of 2 Oct as the server's journal holds it (a backup with `pzstd` running, the kill of `pzstd` at 04:51:36 UTC,
  the kill of `mariadbd` at 04:59:32 UTC, no reboot), then the agent's records hold, each with its own time, the backup run's start,
  the `oom_kill` event of `pzstd` with the two snapshots that name it, the backup run's end once no `pzstd` has been seen for 60
  seconds, and the `oom_kill` event of `mariadbd` with its own two snapshots; and given the reboot of 21 Oct 16:28:27 UTC (boot
  `f96458fd...` after `4ffeb4e7...`), the agent's first start in the new boot records its `boot` event (S4). The backend can then
  show them as one sequence (showing them is not part of this feature).

## Errors and edge cases

- E1: A kill line that does not come from the kernel — written with `logger -t kernel`, to syslog, to the journal's native or
  stream interface, by a service whose name is `kernel`, or by root to `/dev/kmsg` — is no event. Only entries that the journal
  received through its kernel transport **and** that carry the kernel facility (0) count; any local user can write entries of the
  other transports, and a write to `/dev/kmsg` reaches the kernel transport with a facility other than 0.
- E2: A victim's process name is text the process chose (up to 15 bytes): it may contain spaces, `)`, a line feed, invalid UTF-8 or
  text that imitates the line's fields (`x) anon-rss:1kB`). The event holds the real PID, RSS and `oom_score_adj` the kernel wrote,
  the name cleaned to valid UTF-8 (each invalid byte U+FFFD), and `(unnamed)` for an empty name, so the record is always valid.
- E3: The kill line forms of the kernel count alike: `Out of memory: Killed process`, `Memory cgroup out of memory: Killed process`
  (a unit's `MemoryMax=`) and `Out of memory (oom_kill_allocating_task): Killed process`. A kill line without `anon-rss` or
  `oom_score_adj` (another kernel version) gives an event without that value, never 0. `Out of memory and no killable processes`,
  the report lines before the kill line and the `oom_reaper` line are no event.
- E4: The journal cannot be read (no journal directory, the agent is not in `systemd-journal`, a file it cannot open or decode):
  the agent logs one warning `kernel messages not readable` with the error and, when they can be read again, one info line `kernel
  messages readable again`; the other collectors run as before, and reading resumes at the saved position, so no kill is lost while
  the journal still holds it.
- E5: A read of the journal blocks (a stalled disk): the other collectors, the scheduler and the watchdog keep running, the reader is
  not started a second time while it is stuck, and the kills are read once it returns. A span of kill lines the agent can no longer
  read — rotated or vacuumed away before it read them, or in a part of a file that cannot be decoded — is recorded as a `gap` of the
  kernel event collector for that span; a span read late is not lost and needs no gap.
- E6: journald itself is stalled, killed or restarted (it can be an OOM victim too): the kernel's lines reach the journal later;
  the events then have the kernel's time of the kill (S1), and the 5 seconds count from the moment the journal holds the line. When
  the kernel's buffer overflowed meanwhile, journald writes its own entry that it missed kernel messages (its "missed kernel
  messages" message ID, written by journald itself, not on the kernel transport); such an entry is recorded as a `gap` of the kernel
  event collector from the time of the last kernel entry before it to the time of the next kernel entry after it, since kills in that
  span may be lost.
- E7: The agent was stopped for days, so a large backlog of journal entries lies after its position: it reads the backlog in bounded
  steps with memory that does not grow with the backlog or the journal, records the kills in journal order, and takes no additional
  snapshot for them (S10); the work of a run does not grow with the entries before the position, so lines a remote sender causes (a
  firewall's log lines are kernel messages) cannot push every run past its timeout. A saved position whose files are gone gives a `gap`
  from the saved position's time to the first entry after it that the journal still has, and reading goes on there.
- E8: The agent has no saved state (first start, the directory missing, the file unreadable, corrupt or not the agent's own): it logs
  one warning `agent state not available` (unless it is the first start with an empty directory), starts reading the journal at its
  current end, and records a `boot` event for the current boot at every start (each with the same `boot_id`, so a reader sees one
  boot). A state it cannot write (disk full, read-only) is logged once as `agent state not saved` with the error, and the agent goes
  on.
- E9: `/proc/sys/kernel/random/boot_id` cannot be read or is not a lower-case UUID: no `boot` event, one warning `boot ID not
  readable`, and the other features run.
- E10: The clock is set after the boot (NTP corrects it by minutes): `booted_at` is the boot time as the kernel reports it when the
  agent reads it; a kill's capture time follows the journal's receipt time of that entry (S1), also for kills of an earlier boot; and
  whether a kill is still recent enough for snapshots (S10) is judged on one monotonic clock (the time since the journal received the
  line, plus its delay), so neither a clock step nor the drift of the kernel's clock over a long uptime suppresses or triggers
  snapshots.
- E11: The process or connection collector is stuck in an abandoned run when a kill is recorded: no additional snapshot is started
  for that collector (it is never started twice) and the event is recorded anyway; the missed snapshot is a `gap` with cause
  `collector_timeout` from the moment the additional run was due to that moment plus the collector's interval of the current mode. An
  additional run that itself fails is a `gap` over the same span (cause `unknown` for an error or a panic, `collector_timeout` when it
  is abandoned), and while an abandoned additional run has not returned, every regular run then due is skipped with a
  `collector_timeout` gap as for any abandoned run; the "no gap" of S2 holds only while the additional run finishes within its
  timeout. Under memory pressure mode the additional snapshots are taken as well (S2), so the kill is named.
- E12: A local user starts a process whose command matches a rule (a process named `pzstd`): it counts as a backup run. Rules are
  literal comparisons, so `grep pzstd` (command `grep`) does not match a `command` rule.
- E13: `backups.processes: []`: no backup check runs, no backup record and no log line is written.
- E14: A backup run with thousands of matching processes: the count is recorded, no list of processes is kept beyond the earliest
  one.
- E15: A backup process that lives shorter than the time between two checks may be missed; one seen in a single check gives a run
  with its start and its end.
- E16: An event the wire format would refuse (it cannot, by construction): it is left out and its loss is a `gap` of its collector,
  as the producer size contract demands.
- E17: The agent did not run in one or more boots (it was disabled): `previous_boot_id` and `previous_uptime_ns` describe the last
  boot the agent ran in, and the boots between are not recorded.

## Non-functional requirements

- **Time.** An `oom_kill` event is handed to the agent's sink within 5 seconds of the journal holding the kill line while the agent
  runs and the reads do not block; its additional snapshots start within 1 second after that, or when the collector's current run
  ends. A backup run's start is recorded within 15 seconds of the first matching process starting, its end within 15 seconds after
  the 60 seconds without a match.
- **Memory and load.** The server must not be put under more memory pressure: reading the journal holds memory that does not grow
  with the size of the journal, the backlog or a single entry (an entry is read only up to a fixed limit and only the fields the agent
  needs): at most 4 MiB live, checked by a test at two sizes. The additional snapshots are bounded to one per collector per 15 seconds, the rate of
  memory pressure mode. Without pressure mode they come on top of the regular runs (at most one more per 15 seconds); under pressure
  mode, where an OOM kill usually happens, each takes the place of the collector's next regular run (S2), so a storm of kills costs
  no more snapshots than pressure mode already takes. The backup check reads only
  what the process collector reads to name a process. The real cost on the server is measured in #44.
- **Disk.** The agent's saved state (its last boot ID and uptime, the journal position, an open backup run) is one small file written
  atomically with owner-only permissions, at most once every 10 seconds while kill lines or a backup run are being followed and once a
  minute otherwise (a journal position that moved over other kernel lines only waits for the minute); it is the agent's first file write. It is not part of the spool: whatever the spool (#38) later counts against its
  size bound and deletes when full never includes the state file.
- **No new right, no program, static binary.** The agent reads the journal as a member of `systemd-journal`, which it already is
  (Agent area, *Rights*), `/proc` as today, and writes only below `spool.directory`. No capability, group or polkit rule is added, no
  program is started (record 0019) and the binary stays static (`CGO_ENABLED=0`), so no C library of systemd is linked.
- **The guarantees hold.** A hanging journal, `/proc` read or state write never blocks the agent (record 0029), and every span of
  kernel messages, snapshots or backup checks that is lost is recorded as a gap (record 0028).
- **Security.** Only entries of the journal's kernel transport with the kernel facility count (E1). Victim names, commands and command lines are external text: they
  reach the records only, never a log line or an error text, and whoever displays them escapes them. Every read is bounded before it is
  parsed (security area 10); the state file is a write in the agent's own directory (security area 9) and is validated when read.
- **Compatibility.** The wire format changes only by additions to the unreleased version 1.1 (only `v0.1.0` is tagged): what the plan
  adds as new optional fields (the event identity, the snapshot link) is ignored by a reader that does not know them; a new record
  kind or a new `kernel_event` type for the backup run is refused by a reader that does not know it (an unknown kind rejects the
  batch, `KernelEvent.Validate` accepts only `oom_kill` and `boot`), so both decoders accept it in the same change, which is allowed
  because 1.1 is unreleased. Everything is pinned by the golden batches (record 0075). Existing configuration files stay valid.
- **Configuration.** One new option, `backups.processes`: a list of at most 64 literal rules, each exactly one of `command` (equal to
  the process's command as the process collector derives it) and `cmdline_prefix` (the start of its command line), with the strict
  rules and error texts of `processes.programs`; `[]` switches the backup check off. The default is one rule, `command: pzstd`
  (Q7); Plesk's own backup processes are not in it until #45 captures them. The state
  uses the existing `spool.directory`; no other option.
- **Logs.** One line per change of state, fixed texts with counts and error texts as attributes, no process name, command line or
  message text: `kernel messages not readable` / `kernel messages readable again`, `agent state not available`, `agent state not
  saved`, `boot ID not readable`; no line per event, per snapshot or per backup run.

## Acceptance criteria

- [ ] AC1: For a kill line of the kernel transport and facility read live, the agent hands one `oom_kill` event to the sink within 5
  seconds, with the victim PID, command, anonymous RSS in bytes, `oom_score_adj`, the kill line as message, the receipt time minus the
  drift-corrected delay as capture time (never after the receipt time, also after 20 days of uptime), and an identity that two
  readings of the same kill share and two kills never share (S1, E6, E10).
- [ ] AC2: Each recorded live kill makes the process and the connection collector take one additional full snapshot within 1 second
  (or at the end of the current run), each naming the kill's identity; without pressure mode the regular runs keep their grid and
  record no gap while the additional run finishes in time, under pressure mode the additional run replaces the next regular run
  without a gap; the additional run is the next run's predecessor (S2).
- [ ] AC19: An abandoned or failed additional run records a gap of its collector from its due time over one interval of the current
  mode (`collector_timeout` or `unknown`), and the regular runs due while an abandoned one hangs are skipped with `collector_timeout`
  gaps; a kill while the collector is stuck starts no additional run and is recorded as an event (E11).
- [ ] AC20: journald's entry about missed kernel messages gives a gap of the kernel event collector from the last kernel entry before
  it to the next kernel entry after it (E6).
- [ ] AC3: Kills within 15 seconds after the start of the additional snapshots produce exactly one further pair after the 15
  seconds, naming them (at most 64, with the count of the rest); no collector takes more than one additional snapshot per 15 seconds
  (S3).
- [ ] AC4: A kill line not received through the kernel transport, one on the kernel transport with a facility other than 0, and the
  report, `oom_reaper` and `no killable processes` lines, give no event (E1, E3).
- [ ] AC5: A victim name with spaces, `)`, a line feed, invalid bytes, imitated fields or no characters gives an event with the
  kernel's real values and a valid, cleaned command or `(unnamed)`; a kill line without `anon-rss` or `oom_score_adj` gives an event
  without that field; the cgroup and the allocating-task kill lines give events (E2, E3).
- [ ] AC6: After a stop, a crash, the agent's own kill, a reboot, an unclean shutdown or a journald restart (`.journal~` files), every
  kill line after the saved position becomes one event, also for the previous boot; a run whose records were not kept is read again;
  kills read more than 60 seconds late (on one monotonic clock, also after 20 days of uptime) or from another boot get no additional
  snapshot (S10, E7).
- [ ] AC7: A saved position whose files are gone, and a span the agent cannot decode, give a gap of the kernel event collector for that
  span; a blocked journal read leaves the other collectors and the watchdog on time and the reader is not started twice; the work of a
  poll does not grow with the kernel entries before the position (E5, E7).
- [ ] AC8: An unreadable journal logs `kernel messages not readable` once and `kernel messages readable again` once, and resumes at
  the saved position without losing a kill (E4).
- [ ] AC9: A start in a new boot records one `boot` event with `boot_id`, `booted_at` as capture time, `previous_boot_id` and
  `previous_uptime_ns` of the last boot the agent ran in, at most one minute below that boot's uptime when the agent ran to its end
  (S4, E17).
- [ ] AC10: A restart in the same boot records no `boot` event; a first start records one without the previous fields; without a
  usable state every start records one with the current `boot_id`, and the warnings `agent state not available` and `agent state not
  saved` are logged once (S5, S6, E8).
- [ ] AC11: An unreadable or malformed boot ID gives no `boot` event and the warning `boot ID not readable` (E9).
- [ ] AC12: A process matching a backup rule gives a run start within 15 seconds with the earliest matching process's start time,
  command and PID and the number of matches; 60 seconds without a match give the end with the start time, the last seen time, the
  gone-by time and the largest count; pauses shorter than 60 seconds keep one run (S7, S8, S9, E14, E15).
- [ ] AC13: A restart during a run whose process still runs records no second start; a stop or reboot after which no match runs
  records the end with the last seen time from before the stop, and a backup in the new boot is a new run (S11, S12).
- [ ] AC14: `backups.processes` defaults to exactly one rule, `command: pzstd`, accepts at most 64 literal `command` or
  `cmdline_prefix` rules with the errors of `processes.programs`, `[]` switches the check off without records or log lines, and a
  `command` rule does not match another command whose command line mentions it (E12, E13).
- [ ] AC15: The new and changed records validate in Go and C#, and both decoders and the golden batches accept the backup run's
  record; a reader that does not know the new optional fields (event identity, snapshot link) still reads the records (E16,
  Compatibility).
- [ ] AC16: Following the journal over a journal and a backlog of two different sizes keeps the reader's memory within the plan's
  bound, independent of the size (E7, Memory).
- [ ] AC17: The state file is written with owner-only permissions and atomically, never more often than the stated rates, and a
  corrupt or foreign state file is treated as missing (E8, Disk).
- [ ] AC18: No log line of the feature holds a victim name, a command, a command line or a message text (Security, Logs).

## Out of scope

- Showing the events: the timeline, event markers and jumps from an event to its snapshots are the web UI's (#49, #53); the backend's
  incident reconstruction and forensics report keep taking kills and boots from stored log lines and backups from `pzstd` in
  snapshots. S13 is covered by AC1, AC2, AC9 and AC12.
- Sending: until the spool (#38) and the sender (#39) exist, the agent drops the records after checking them, as all its records.
- Shipping kernel lines as `log_line` records (#37) and importing them (exists).
- Kernel events other than OOM kills (panics, hung tasks, segmentation faults, disk and file system errors) and telling a clean
  reboot from a crash.
- Recovering boots and kills from before the agent's first start, and boots the agent did not run in (E17).
- Additional snapshots for a backup start or for anything other than an OOM kill.
- Comparing the `oom_kill` counter of `/proc/vmstat` with the recorded events.
- The systemd unit and the installation that create `/var/lib/vandox` and keep the journal readable (#42).

## Open questions

- Q1: What did #30 deliver that this builds on? Answered: the scheduler with a runner per collector, gaps for every missed sample,
  memory pressure mode, `Run.PreviousLost`, the self-metrics, logging and the systemd notification (Agent area, *Scheduling*); no
  journal reader, no saved state and no producer of `kernel_event` exist (`internal/`, `docs/areas/agent.md`).
- Q2: Does the wire format already describe these events? Answered: `kernel_event` with `oom_kill` and `boot` and the fields of S1
  and S4 exists since 1.0 (`internal/model/kernel.go`, wire format area); there is no backup record, no identity of an event and no
  link from a snapshot to an event. Version 1.1 is unreleased (`git ls-remote --tags origin` shows only `v0.1.0`), so additions extend
  1.1 in place (record 0043, wire format area *Versioning*).
- Q3: How does the agent follow the kernel messages "in the journal"? Answered: it reads the system journal the issue names, as a
  member of `systemd-journal`, without starting `journalctl` (record 0019, Agent area *Collecting*), without cgo (the release build
  sets `CGO_ENABLED=0`) and without a new right such as `CAP_SYSLOG` for `/dev/kmsg` (record 0030, security area 6). So the agent
  reads the binary journal files itself. The real sample (answer of the Product Manager to M1): two journal files copied from the
  server, `system.journal` (online, 2026-10-21 02:50 to 2026-10-22 17:04 UTC, boot `f96458fd...` with the last entries of boot
  `4ffeb4e7...`) and the archived `system@7b7188d0...-0000000000000001-00065cb6ddc61ac1.journal` (2026-09-30 17:45 to 2026-10-02
  08:19 UTC, boot `4ffeb4e7...`), 80 MiB each. They are **not anonymized and are never committed** or copied into the working tree.
  Checked with `journalctl --file ... --header` and the entries: written by systemd `249.11-0ubuntu3.23` on kernel
  `5.15.0-191-generic` (Ubuntu 22.04, as the spec assumes); header of 256 bytes, the layout of systemd 246 to 251; incompatible
  flags `COMPRESSED-ZSTD` and `KEYED-HASH`, **not** `COMPACT`; data hash table of 233,016 buckets; the OOM kill entries carry
  `_TRANSPORT=kernel`, `SYSLOG_FACILITY=0` and `_SOURCE_MONOTONIC_TIMESTAMP`, with the kill line of S1 (`total-vm`, `anon-rss`,
  `file-rss`, `shmem-rss`, `UID`, `pgtables`, `oom_score_adj`), and victim names as the kernel cuts them (`/usr/sbin/amavi`).
  Neither file holds journald's entry about missed kernel messages (E6), so that entry is built from the systemd 249 source, a
  residual the decision record states. The committed fixture is derived from these files: small, anonymized (every private field —
  host name, machine ID, boot IDs, addresses, user names, command lines — replaced whole and checked field by field after decoding),
  and in the server's real variant (zstd, keyed hash, not compact, the 256-byte header), with at least one OOM kill, a compressed
  field, an online and an archived file. How it is produced (a tool that rewrites selected entries of a real file, or
  `systemd-journal-remote`, whose systemd 255 here writes only the `COMPACT` variant and so serves at most as a second variant),
  its path under `testdata/`, and how developers run local checks against the real files outside the repository are the plan's
  decisions (Lead, mode `plan`).
- Q4: Can a local user forge an OOM event? Answered: not through an entry of the journal's kernel transport with the kernel facility
  (0): only the kernel's own messages carry both, a write to `/dev/kmsg` (root only) gets another facility, and every other way in is
  excluded (E1); the facility rule is checked against the kernel source in the plan. This differs from the backend's log signatures,
  which treat forged kernel lines as a limitation (record 0094).
- Q5: Where does the state that survives restarts and reboots live? Answered: in `spool.directory` (default
  `/var/lib/vandox/spool`), the agent's one write area (Agent area, *Spool and sending*; security area 9), so no new option.
- Q6: Where does "the previous uptime if known" come from? Answered: from the uptime the agent saves at least once a minute; it is a
  lower bound, at most one minute short when the agent ran until the end of that boot, and absent when the agent never ran before.
- Q7: Which processes mark a Plesk backup run, as the default of `backups.processes`? Answered: by the Product Manager (option b):
  the default is `pzstd` only (one `command` rule), matching the backend's built-in list (`BackupPrograms`), the only backup program
  seen in real data. Plesk's own backup processes were never captured; the operator adds them through the option once the parallel
  run of #45 has captured them. Until then a run is recognized only while `pzstd` runs, so a phase without it (the database dump
  before the compression) is not part of the run; the decision record and the Agent area state this gap.
- Q8: What is one backup run? Answered: the span in which at least one matching process exists, ended by 60 seconds without one
  (S8, S9); a Lead decision, so Plesk's phases do not split into many runs.
- Q9: What is "within a few seconds"? Answered: 5 seconds from the journal holding the kill line to the event in the sink, and 1
  second more to the start of the snapshots (Time).

## Area documents

- `docs/areas/agent.md`: a section *Kernel events* (the journal as source, the kernel transport only, the kill line forms and their
  fields, the position and the backlog, the gaps, the additional snapshots and their 15-second limit, the logs), a section *Boots*
  (boot ID, saved state, previous boot and uptime), a section *Backup runs* (rules, the default `pzstd` only and the stated gap that
  Plesk's own backup processes, uncaptured until #45, are not recognized by default, checks, start and end, restarts), additions to
  *Processes*, *Connections* and *Memory pressure mode* (the additional snapshots), *Rights* (the journal, the state file),
  *Status*, *Related decisions* and *Implementation*.
- `docs/areas/wire-format.md`: `kernel_event` with the event identity and its producer, the link of `process_snapshot` and
  `connection_snapshot` to the kills, the backup run record, *Versioning* (what 1.1 adds) and the golden batches.
- `docs/areas/configuration-and-secrets.md`: the option `backups.processes` with its default `[{command: pzstd}]`, its rules and
  error texts, and the use of
  `spool.directory` for the agent's state.
- `docs/areas/storage.md`: only if the plan's backup record needs a storage rule beyond the JSON payload every kind other than
  metrics and log lines gets.

## Challenge

- M1 (major, journal reader without a real sample): accepted and escalated. The Product Manager supplied two real journal files of
  the server (option a, not anonymized, never committed); Q3 records what they show (systemd 249.11, zstd, keyed hash, not compact,
  kernel entries with facility 0 and the kernel's own time stamp) and what the plan decides about the committed, anonymized fixture.
  The files also refuted the issue's account of 2 Oct (no reboot that day; `pzstd` and then `mariadbd` were killed): *Problem* and
  S13 now follow the journal, and the reboot scenarios rest on the real boot change of 21 Oct. E1, E5 and E7 stay requirements on
  behavior and are checked against the sample in the plan.
- m1 (additional snapshots double the rate under pressure): accepted. Under memory pressure mode the additional run takes the place
  of the collector's next regular run, without a gap, and the grid continues from it; without pressure mode it comes on top, at most
  one per 15 seconds. S2, *Memory and load* and AC2 say so.
- m2 (an additional run and the collectors' state): accepted. S2 states that the additional run is a sample like any other
  (predecessor for CPU rates and the listener comparison, `Run.PreviousLost` by the same rules); E11 and the new AC19 state the gap of
  a failed or abandoned additional run (from its due time over one interval of the current mode), the skipped regular runs while it
  hangs, and that the "no gap" of S2 holds only while it finishes within its timeout.
- m3 (the kernel transport alone does not prove kernel origin): accepted. An entry counts only with the kernel transport and the
  kernel facility 0; E1, Q4, *Security*, AC1 and AC4 say so, and Q4 names the check against the kernel source as the plan's.
- m4 (messages journald missed are lost without a gap): accepted. E6 and the new AC20: journald's own entry about missed kernel
  messages becomes a gap of the kernel event collector from the last kernel entry before it to the next one after it. Only journald's
  own entry counts; its message ID and fields are checked against the sample in the plan.
- m5 (capture time of kills from a previous boot): accepted in part. Each kill's time is converted with the boot time of its own
  boot (receipt time minus monotonic time of the entry), also for earlier boots (S1, E10, AC1). The fallback to the receipt time
  applies when the result lies before that boot's start or after the receipt time. The proposed lower tolerance below the receipt
  time is rejected: when journald is stalled or restarted (E6) a kill line can be received minutes after the kill, and that late
  receipt is exactly the case in which the kernel's own time is the right one.
- m6 (AC15 and a new kind or type): accepted. *Compatibility* and AC15 limit the claim about readers that do not know them to the new
  optional fields; a new record kind or `kernel_event` type for the backup run is accepted by both decoders and the golden batches in
  the same change, which is allowed because 1.1 is unreleased.
- Note for the plan (state file in `spool.directory`): taken into the spec. *Disk* says that the state file is never part of what
  the spool counts against its bound or deletes.
