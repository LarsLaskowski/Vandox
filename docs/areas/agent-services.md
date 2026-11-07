# Agent services and backup runs

## Scope

Two agent collectors that record what runs on the server rather than how much it uses: `systemd`, which records the state of the
tracked systemd service units over the system bus, and `backups`, which records the start and end of each backup run from the process
table. The analysis and the report rely on their records; scheduling, gaps and the state file they use are in [Agent](agent.md).

## Services

The collector `systemd` (interval 30 seconds, pressure interval 10 seconds, timeout 5 seconds, after `connections` in the job list)
records the tracked units as `service_state` records with source `systemd` and the capture time of the run's start
([wire format records](wire-format-records.md)). It runs only when `services.units` holds at least one entry (default: the Plesk
services, [Configuration options](configuration-options.md)); with `services.units: []` it does not run, records nothing, logs nothing
and never connects to the system bus. It starts no program: it asks systemd over D-Bus, as `systemctl` does.

### What it reads

Each run opens its own connection to the system bus socket `/run/dbus/system_bus_socket` (a constant; no environment variable names
it), authenticates as the agent's user, says `Hello` and closes the connection at its end. It calls read methods only, all with
`NO_AUTO_START`: `ListUnitsByPatterns` (only when a pattern is configured), `ListUnitsByNames` for the tracked names in calls of at most
64 (systemd loads each named unit, resolves an alias and answers primary name, load, active and sub state in one consistent entry), and
`Properties.Get` of `ActiveEnterTimestamp` and `NRestarts` on each unit, except a unit that is `not-found` and `inactive`, which has
neither (both count as 0). About 25 round trips for the default list, about 520 at the cap. No group and no capability is needed:
systemd's bus policy lets every user make these calls.

### Which units are tracked

- An entry without a pattern character is tracked from the start; an entry with `*` or `?` is matched by systemd against its loaded
  units, and each match whose `load_state` is not `not-found` is tracked from the first run that lists it.
- A unit once tracked stays tracked until the agent restarts, even when systemd unloads it, so its stop is recorded.
- At most 256 units: the exact entries first in file order, then the matches in name order; a tracked unit is never dropped for a later
  match, and a new match beyond the cap is left out.
- A name the wire format refuses (systemd's `\x2d` escapes) is not tracked. A pattern that matches nothing is no error. Entries or a
  pattern that name one unit (an alias, `mysql` and `mariadb`) give one unit, recorded under systemd's primary name.

### What is recorded and when

- One record per unit: the primary name, `load_state`, `active_state`, `sub_state`, `active_enter_at` and `restarts` (`NRestarts` as
  reported, 0 for a `not-found` or masked unit; masked, `bad-setting` and `error` units are recorded like any other). `active_enter_at` is
  left out when systemd reports 0 (never active) or a time the backend cannot store (after 2262-04-11), never clamped.
- The first successful run after a start records every tracked unit. A later run records a unit when any of those five fields differs
  from its last recorded state, or when its last record is at least one hour old, so a unit's records are at most one hour plus one
  interval apart while the collector succeeds (an incident window shorter than that may hold no record of a quiet unit).
- After a lost sample (the previous-sample-lost flag, [Agent](agent.md), *Scheduling*) the next successful run records every tracked
  unit again, as after a start.
- A state that came and went between two runs without a new `active_enter_at` or a higher `restarts` (a short `activating` or
  `reloading`) is not seen; a change is recorded at most one interval after it happened.
- **A refused unit state.** The collector checks every record against the wire format itself before it returns the run's records. A unit
  whose state is refused (an `active_state` or `load_state` a newer systemd adds, a `sub_state` with characters other than `[a-z0-9-]` or
  longer than 64 bytes) is left out and its last record forgotten; the other records are kept, and the run returns one gap with cause
  `unknown`, collector `systemd`, from the run's start over one interval of the run's mode — every such run, because the unit counts as
  not recorded. The scheduler therefore never sees a refused record.

### Logging

With `collector=systemd`, no line per run and none per state change; only fixed texts, counts and names that passed the configuration or
wire rules, never the value systemd sent or another text of a reply. The stored unit names and states are external text that whoever
displays them escapes.

| Line | Attributes | When |
| ---- | ---------- | ---- |
| warning `systemd units not found` | `count`, `units` (the exact entries whose `load_state` is `not-found`, comma-separated in file order) | at the first successful run after a start, when there is one; not repeated after a lost sample; not for patterns, masked or broken units; a unit installed or removed later is recorded without a line |
| warning `systemd units cut` | `left_out` | once per agent run, at the first run that leaves a match out because of the cap |
| warning `systemd units not tracked` | `count` | once per agent run, when a match has a name the wire format refuses |
| warning `systemd unit state not recorded` | `unit` (the tracked name), `field` (the field's name) | at the first run that refuses the unit's state |
| info `systemd unit state recorded again` | `unit`, `field` | when the unit is recorded again |

### Failures

A bus that cannot be reached (socket missing, connection refused), a refused authentication, an error reply to one of the agent's calls,
a reply or message the client refuses and a reply larger than 1 MiB fail the run: no record and no missing-unit warning, the sample is a
gap with cause `unknown`, and the scheduler logs `collector failed` once and `collector recovered`. The run's deadline is the connection's
deadline, so a systemd or bus daemon that does not answer ends the run at the 5-second timeout as a `collector_timeout` gap, the collector
is not started again while it is stuck, and no other collector waits. After `daemon-reexec`, `daemon-reload` or a restart of the bus
daemon the next run connects anew. The failure texts are `sdbus: connect: <os error>`, `sdbus: authentication refused`, `sdbus: message:
<fixed reason>`, `sdbus: <member>: error reply <error name>` (the name only when it follows the D-Bus error-name grammar and is at most 255
bytes), `sdbus: <member>: reply too large`, `sdbus: <member>: unexpected signature` and `sdbus: <member>: malformed body`; none contains a
string of a reply body.

### The client (security area 10)

Any local user may send a signal to the agent's bus connection (up to 32 MiB, with file descriptors), and replies are text systemd and the
bus daemon choose. The client bounds what it reads before it decodes anything:

- It only reads, writes, sets deadlines on and closes the connection and never negotiates file-descriptor passing, so the kernel closes
  any descriptor sent to it.
- Authentication is `EXTERNAL`; the answer line is cut at 512 bytes before it is compared and only `OK` and 32 hexadecimal digits are
  accepted.
- A message is refused when its byte order is unknown, its version is not 1 or it is larger than 2^27 bytes. Every message that is not a
  method return or error (signals, method calls, unknown types) is skipped by its length without a look at its header fields, in a fixed
  buffer, so a flood keeps nothing in memory. A return or error is read only with header fields of at most 64 KiB, of which only codes 1
  to 9 are decoded, each with its one fixed basic type; any other code or type fails the call. A return or error for another call is
  skipped after its header fields.
- The awaited reply's body is read only up to 1 MiB and its signature must be exactly the expected one (`s`, `a(ssssssouso)`, `v`
  holding `t` or `u`); strings must be NUL-terminated valid UTF-8 within the body, object paths are checked against the D-Bus path grammar
  before they are used in a call, and the answer of `ListUnitsByNames` must hold exactly one entry per name. An error reply's body is never
  read into a text.
- Memory is bounded independently of the server: at most 256 tracked units with their last recorded state, one connection, one reply of
  at most 1 MiB.

### Residuals

- A local user who learns the agent's unique bus name during a run can flood that connection with signals; the run discards them until
  its deadline and becomes a gap, and no memory grows (record 0117).
- Reading a unit systemd has not loaded makes systemd load and unload it, broadcasting `UnitNew` and `UnitRemoved`: one pair per missing
  unit and run.
- A local user can make systemd load a stub unit that a pattern matches; a match listed as `not-found` is not tracked.
- The unit must keep `AF_UNIX` and the bus socket reachable ([Agent rights](agent-rights.md)). Only systemd 249's replies are captured.

## Backup runs

The collector `backups` (interval 10 seconds, timeout 5 seconds, after `logs` in the job list) records the start and the end of each
backup run as `backup_run` records with source `backups` ([wire format records](wire-format-records.md)). It runs only when
`backups.processes` holds at least one rule (default: three rules, `command: pzstd` and the prefixes of `backup-archiver --pack` and
`sw-tar --create`; [Configuration options](configuration-options.md)); with `backups.processes: []` it does not run. It writes no log line
of its own.

### Matching

Each run lists `/proc` as the process collector does (its 4,096 lowest PIDs) and reads `stat` and `cmdline` of each process with the
process collector's readers ([Agent system metrics](agent-system-metrics.md), *Readers*). A process matches when a rule's `command` equals
its `command` as the process collector derives it (*Names* there), or when its cleaned command line starts with a rule's
`cmdline_prefix`; both are literal comparisons, so `grep pzstd` (command `grep`) does not match `command: pzstd`. A process that cannot be
read, ended or not, is skipped like an ended one, without a log line or a gap.

### A run

- A run starts at the first check that sees a match. The **start** record holds `started_at` (the earliest start time of the matching
  processes, also its capture time), `boot_id`, the `pid` and `command` of that earliest process and `processes`, the number of matches.
- Each further check with a match keeps the time of the check as the last sighting and the largest number of matches; only the earliest
  process is kept, never a list. The first check without a match is the **gone-by** time; a match within 60 seconds of the last sighting
  keeps the run.
- At the first check at least 60 seconds after the last sighting the **end** record is written with `started_at`, `boot_id`,
  `last_seen_at` (the last sighting, also its capture time), `gone_by` and `max_processes`; the real end lies between `last_seen_at` and
  `gone_by`. A process seen at one check only gives a run with its start and its end; one that lives shorter than the time between two
  checks may be missed.

### Restarts and reboots

The open run is kept in the agent's state file ([Agent](agent.md), *Agent state*). At the first check after the agent starts, a stored run
of the current boot with a match continues (no second start); a stored run of another boot, or one without a match, ends at once with the
stored `last_seen_at` (at most a minute before the stop) and `gone_by` that first check; a backup in the new boot is a new run. The
gone-by time is not stored: after a restart the first check without a match is the gone-by time, so it is never before the real end.

### Lost runs

A check decides from the committed state and writes nothing itself: its change (the open run, the gone-by time and the first-check flag)
takes effect only when the scheduler reports the check kept ([Agent](agent.md), *Scheduling*, the observer), right after its records
reached the spool, so a state save never holds a start, sighting or end whose record the spool has not taken. A check that was dropped,
abandoned or not reported changes nothing and the next check decides again: the start is written again while a match runs, the end again
with the gone-by time of the last kept check. After a restart a stored run whose end was dropped ends with `gone_by` the first check
without a match. The scheduler's gap covers the lost check.

### Errors

A boot ID that cannot be read or is not a lower-case UUID ([Agent kernel events and boots](agent-kernel-events.md), *Boots*), a boot time
that cannot be read, a `/proc` that cannot be listed or the run's deadline fail the run (a gap); the error texts name the file and a fixed
reason, never a command or command line. The stored `command` is the process collector's external text.

### Residuals (record 0120)

- The default rules come from the processes captured during a Plesk backup (#287): `backup-archiver --pack` runs for every part, the
  database dumps included, and `sw-tar --create` with `pzstd` for the file archives; the rules name the creating modes, so a restore
  (`--unpack`, `--extract`, assumed, not captured) does not count. `pmmcli`, `pmm-ras` and `backup_restore_helper` also run outside a
  backup and are not rules. Between two archivers the run stays open while the gap is under 60 seconds. The prefixes hold Plesk's paths;
  another install path needs its own rule.
- Any local user can start a process named `pzstd`, or one whose command line starts with a default prefix; it counts as a backup run.
- A backup process the agent cannot read is not recognized; one that becomes unreadable during a run can end the run early.

## Related decisions

- [0019](../decisions/0019-agent-reads-proc-instead-of-top-lsof.md) — no program is started; the states come from systemd over the bus.
- [0117](../decisions/0117-systemd-collector-hand-written-d-bus-client-per-run-states-recorded-on-change.md) — a minimal D-Bus client of
  its own, one connection per run, polling, records on change, after a start or a lost sample and hourly, patterns up to 256 services.
- [0120](../decisions/0120-oom-kill-events-triggered-snapshots-boots-from-saved-state-and-backup-runs.md) — backup runs as a record kind
  of their own, `pzstd` and Plesk's two creating processes as default rules, the run reaching the state only once a check is kept.

## Not here

- Scheduling, lost samples, the observer and the state file: [Agent](agent.md).
- Process names and readers: [Agent system metrics](agent-system-metrics.md), *Processes*.
- The unit's access to the bus socket: [Agent rights](agent-rights.md).
- How backup runs are used in the report: [Forensics report](forensics-report.md); the options:
  [Configuration options](configuration-options.md); the record fields: [Wire format records](wire-format-records.md).

## Implementation

`internal/svcstats` (systemd collector; `TestSvcstats_ImportsOnlyAllowedPackages`) and `internal/sdbus` (its D-Bus client; fixture
`testdata/systemd/`, a capture of systemd 249's replies; `TestSdbus_ImportsOnlyAllowedPackages` pins the connection calls it may use,
`TestSession_DiscardsUnsolicitedSignalsWithoutKeepingThem` the memory bound, `TestSession_CallsOnlyReadMethods` the method list);
`internal/backups` (backup run check; `TestBackups_LogsHoldNoProcessText`).
