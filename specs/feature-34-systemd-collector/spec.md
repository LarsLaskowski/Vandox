# Spec: systemd service collector

Source: Issue #34
Status: Draft

## Problem

The operator of the Plesk server wants to know the state of every important service at all times: when MariaDB, the
Plesk panel (`sw-cp-server`, `sw-engine`), the web servers or the mail services went down, failed, were restarted by
systemd or came back. Today the agent records system metrics, processes and connections (#31, #32, #33), but no
service state: whether `mariadb.service` was running at 06:59, failed or was restarted three times can only be guessed
from imported or shipped log lines, whose `service_*` events name a unit only where systemd 249 prints it
([Detection, alerts and reports](../../docs/areas/detection-and-reports.md), *Log signatures*). The wire format already
has a `service_state` record kind with source `systemd` (golden batch `testdata/wire/all-kinds.jsonl`, line 5), but no
producer writes it.

The outage Vandox is built to explain (memory exhaustion, OOM kill, MariaDB, Plesk and mail going down) is exactly the
moment the services change state, so the state must be recorded then, with the time it was seen, without making the
memory situation worse.

## Scenarios

- S1: Given the agent starts on a Plesk server with the default configuration (no `services` section in
  `/etc/vandox/agent.yaml`), when the collector `systemd` runs for the first time, then one `service_state` record with
  source `systemd` and the capture time of the run's start is recorded for every tracked unit, carrying its full unit
  name (`mariadb.service`), `load_state`, `active_state`, `sub_state`, `active_enter_at` (when systemd has one) and
  `restarts` (systemd's `NRestarts`).
- S2: Given `mariadb.service` is `active`/`running` with `restarts` 0, when the OOM killer kills it and systemd restarts
  it (`Restart=on-failure`) between two runs, then the next run records `mariadb.service` with `restarts` 1 and the new
  `active_enter_at`, and no record for any unit whose state is unchanged.
- S3: Given `dovecot.service` is running, when it fails and stays failed, then the next run records it with
  `active_state` `failed` (and `sub_state` `failed`), and the runs after that record nothing for it until it changes
  again.
- S4: Given the operator writes `services: {units: [mariadb, nginx.service, "plesk-php*-fpm"]}` into `agent.yaml`, when
  the agent starts, then exactly `mariadb.service`, `nginx.service` and the loaded units matching
  `plesk-php*-fpm.service` are tracked; the default list is replaced, not extended.
- S5: Given the default list on a server without Apache (`apache2.service` does not exist), when the first run
  succeeds, then the agent logs one warning `systemd units not found` with `collector=systemd`, `count=1` and
  `units=apache2.service`, records `apache2.service` once with `load_state` `not-found`, and logs nothing about it on
  later runs.
- S6: Given the pattern `plesk-php*-fpm` and the loaded units `plesk-php82-fpm.service` and `plesk-php83-fpm.service`,
  when the collector runs, then both are tracked and recorded; when the operator later installs and starts
  `plesk-php84-fpm.service`, the first run that sees it loaded records it.
- S7: Given the memory pressure mode is on ([Agent](../../docs/areas/agent.md), *Memory pressure mode*), when the
  collector is due, then it runs every 10 seconds instead of every 30, so a service that fails under pressure is seen
  within 10 seconds.
- S8: Given a unit whose state does not change for days, when one hour has passed since its last record, then the next
  run records it again with its unchanged state, so a unit's records are at most one hour plus one interval apart while
  the collector succeeds (an incident window shorter than that, `incidents.window_before` below about 61 minutes, may hold
  no record of a quiet unit).
- S9: Given `services: {units: []}`, when the agent starts, then the collector `systemd` does not run, records nothing,
  logs no warning and the agent does not connect to the system bus.
- S10: Given the agent is restarted, when the first run after the start succeeds, then every tracked unit is recorded
  again, as in S1, and the missing-unit warning of S5 is logged again.
- S11: Given a sample of the collector `systemd` was lost — the run failed, timed out, was abandoned or skipped while an
  earlier run was stuck, or the sink refused its records — when the next run succeeds, then it records every tracked unit
  again, as after a start (S1), so a change seen only by the lost sample is never missing from the record (the
  missing-unit warning is not repeated).

## Errors and edge cases

- E1: An invalid `services.units` value stops the agent at start-up with exit code 1 and one error line
  `config: <file>:<line>: services.units[<i>]: <reason>` (or `services.units` for the list itself) that never contains
  the value: an entry that is empty, longer than 255 bytes after the implied `.service`, holds a character other than
  `A-Z a-z 0-9 : _ . @ -` and the pattern characters `*` and `?`, names another unit type (`.socket`, `.timer`,
  `.target`, `.mount`, `.automount`, `.swap`, `.path`, `.slice`, `.scope`, `.device`), is a template without an
  instance (`foo@.service`), starts with `.` or `-`, or equals another entry after `.service` was added (`nginx` and
  `nginx.service`); more than 64 entries; a null element (`has no value`, as for the other agent lists); an element or
  a list of another YAML type.
- E2: The system bus cannot be reached (`/run/dbus/system_bus_socket` missing, connection refused, authentication
  refused, systemd not on the bus): the run fails, the sample is a gap with cause `unknown` and collector `systemd`, the
  scheduler logs `collector failed` with `collector=systemd` at the first failure and `collector recovered` when a run
  succeeds again; no record and no missing-unit warning is written until a run succeeds.
- E3: systemd or the bus daemon does not answer (stalled under memory pressure): the run is abandoned at its 5-second
  timeout, recorded as a gap with cause `collector_timeout`, not started again while it is stuck, and no other
  collector, the watchdog aside as *Scheduling* describes, is held up.
- E4: A run that sees a change but ends after its deadline (its result becomes a gap), or whose records the sink
  refuses: the collector cannot tell from inside the run whether its result was kept (it races the deadline, and
  `scheduler.Run` carries only `Start` and `Pressure`), so the scheduler tells the next run that the previous sample of
  this collector was lost, and that run records every tracked unit (S11).
- E5: systemd reports a value the wire format refuses for one unit (an `active_state` or `load_state` a newer systemd
  adds, a `sub_state` with characters outside `[a-z0-9-]` or longer than 64 bytes): the collector checks every unit's
  record against the model's rules itself, before it returns the run's records, so the scheduler never sees a refused
  record and never logs `record refused` for it. That unit's record is left out, the other units' records of the run are
  kept, the collector returns one gap with cause `unknown` and collector `systemd` with them, one warning `systemd unit
  state not recorded` (`unit`, `field`: the field's name) is logged at the first such run and an info line `systemd unit
  state recorded again` when the unit is recorded again. The validation error's text, which may quote the value, is
  never logged. The unit counts as not recorded, so
  each such run is a gap. An `active_enter_at` that is 0 (never active) or outside the storable range (after
  2262-04-11) is left out as unknown, never clamped, and fails nothing.
- E6: Patterns match more than 256 units in total (for example `*`): the first run tracks the exact names first in file
  order, then the matches in name order, up to 256 units. A unit once tracked is never dropped for a later match, even one
  whose name sorts before it; while 256 units are tracked a new match is not tracked and counts as left out. The warning
  `systemd units cut` (`collector=systemd`, `left_out=<n>`: the matches not tracked in that run) is logged once per agent
  run, at the first run that leaves a match out, and not again.
- E7: A pattern matches a unit whose name the wire format refuses (systemd's `\x2d` escapes): it is not tracked, and one
  warning `systemd units not tracked` (`count=<n>`) is logged once per agent run without the name.
- E8: The bus delivers messages the agent did not ask for (any local user may send a signal to the agent's bus
  connection), or a reply larger than 1 MiB: unsolicited messages are discarded without being kept, so a flood of them
  grows no memory; an oversized reply fails the run as in E2.
- E9: systemd re-executes (`systemctl daemon-reexec` after an upgrade), reloads (`daemon-reload`) or the bus daemon
  restarts between two runs: the next run reads the states again, connecting anew when the old connection broke; at
  most the runs while the bus is away are gaps.
- E10: Two entries name one unit (the alias `mysql` and `mariadb` for `mariadb.service`), or a pattern also matches an
  exact entry: the unit is tracked and recorded once, under systemd's primary name.
- E11: A unit changes between two runs and changes back (stopped and started by hand, restarted by systemd): the
  record shows it through a new `active_enter_at` or a higher `restarts`; a state that came and went between two runs
  without changing either (a short `activating` or `reloading`) is not seen.
- E12: A unit whose `load_state` is `masked`, `bad-setting` or `error` is tracked and recorded like any other and is
  not part of the missing-unit warning.
- E13: A unit that was not found at start-up is installed later: the first run that sees it with another state
  records it, without a log line. A unit removed later (its file deleted and systemd reloaded) is recorded with
  `load_state` `not-found`, without a warning: the warning belongs to the start-up only.
- E14: A pattern that matches no loaded unit causes no warning; a matched unit that systemd later unloads (stopped and
  no longer referenced) stays tracked by name until the agent restarts, so its stop is recorded.

## Non-functional requirements

- **Schedule.** Collector `systemd`, records with source `systemd`: interval 30 seconds, pressure interval 10 seconds,
  timeout 5 seconds; it runs only when `services.units` holds at least one entry.
- **Read only, no program.** The agent reads the units through systemd's D-Bus interface on the system bus with read
  methods only (as `systemctl show` and `systemctl status` do), never changes a unit's state and never starts
  `systemctl` or any other program (record 0019).
- **No new right.** Reading unit states needs no group and no capability beyond what the agent already holds
  (*Rights*); the agent's unit (#42) must leave `AF_UNIX` and the system bus socket reachable. The system bus is a third
  local channel besides the journal stream and the notification socket, not a network connection: the agent still
  listens on no port and opens no network connection for it.
- **Memory.** Bounded independently of the server: at most 256 tracked units, each with only its last recorded state;
  at most one bus connection; a reply read only up to 1 MiB; no queue of unsolicited messages. Records: one per tracked
  unit at start, one per change and one per unit and hour (about 11 per hour for the default list on a quiet server).
- **Configuration.** New option `services.units` in `agent.yaml`, default (in this order) `sw-cp-server`, `sw-engine`,
  `psa`, `nginx`, `apache2`, `postfix`, `dovecot`, `mariadb`, `plesk-php*-fpm`, `php*-fpm`, `fail2ban`; an entry without
  a unit type suffix means `<entry>.service`, as `systemctl` reads it; `*` and `?` are the only pattern characters. An
  existing configuration file without `services` keeps working and gets the default.
- **Logs.** Only fixed texts and counts, through the collector's logger with `collector=systemd`; unit names only as
  attribute values and only names that passed the configuration or wire rules; no line per run and no line per state
  change; no text a reply carries beyond a unit name and the fixed reasons.
- **Scheduler.** S11 needs a change of the scheduler (`internal/scheduler`): a run must learn whether the previous
  sample of its collector was lost (a gap of any cause, or records the sink refused), for example a field of `Run` or a
  confirmation of accepted results; the plan picks it and records it against record 0110, which it extends. The existing
  collectors ignore it.
- **Test data.** The D-Bus replies are an external format: the plan builds the decoder from real systemd 249 replies
  under `testdata/` (`ListUnitsByPatterns`, `LoadUnit`, `Properties.GetAll` of a loaded, a not-found and a masked unit,
  and an unsolicited signal), and the tests read them.
- **Compatibility.** Ubuntu 22.04 with systemd 249 on the server; the wire format stays 1.1 (the `service_state` fields
  are unchanged); Linux only.

## Acceptance criteria

- [ ] AC1: The tracked units come from `services.units`: without the option the default list of the non-functional
  requirements, with it exactly the entries given (each without a suffix read as `.service`), and with `[]` no
  collector, no record, no warning and no bus connection (S4, S9).
- [ ] AC2: Every invalid `services.units` value of E1 stops the agent at start-up with the error line of E1, which
  names the key path and the reason and never the value (E1).
- [ ] AC3: The first successful run after the agent starts records one `service_state` record per tracked unit, with
  source `systemd`, the capture time of the run's start, the unit's primary name, `load_state`, `active_state`,
  `sub_state`, `active_enter_at` (left out when 0 or not storable) and `restarts` (systemd's `NRestarts` as reported, 0
  for a not-found or masked unit); masked, bad-setting and error units included (S1, S10, E10, E12).
- [ ] AC4: A later run records a unit only when its `load_state`, `active_state`, `sub_state`, `active_enter_at` or
  `restarts` differs from the unit's last recorded state, or when its last record is at least one hour old (S2, S3, S8,
  E11, E13).
- [ ] AC5: The first successful run after a start logs one warning `systemd units not found` with `count` and `units`
  (the exact entries whose `load_state` is `not-found`, comma-separated in file order) when there is at least one, and no
  later run logs it again (S5, E13, E14).
- [ ] AC6: Patterns track every loaded unit whose name matches, a new match from the run that sees it, a match that
  systemd unloads until the agent restarts, at most 256 units in total with the warning of E6, and no unit whose name
  the wire format refuses, with the warning of E7 (S6, E6, E7, E14).
- [ ] AC7: A bus that cannot be reached, fails or exceeds the reply bound makes the run a gap with cause `unknown`, and
  a bus that does not answer makes it a gap with cause `collector_timeout` without holding up any other collector; the
  next run after the bus returns connects anew and records (E2, E3, E8, E9).
- [ ] AC8: After any lost sample of the collector `systemd` (a failed, timed-out, abandoned or skipped run, or records
  the sink refused) the next successful run records every tracked unit, without repeating the missing-unit warning; the
  scheduler tells the run so, and the other collectors behave as before (S11, E4).
- [ ] AC9: A unit whose state the model's rules refuse is left out of the run by the collector itself, which returns
  one gap with cause `unknown` per such run and logs the warning and recovery lines of E5 without the validation error's
  text, while the run's other records are kept and the scheduler logs no `record refused` (E5).
- [ ] AC10: The collector runs every 30 seconds with a 5-second timeout and every 10 seconds under memory pressure (S7).
- [ ] AC11: The collector only calls systemd's read methods on the system bus, starts no program and discards messages
  it did not ask for without keeping them, so a flood of unsolicited signals does not grow the agent's memory (E8).

## Out of scope

- What the backend does with the records: showing service states in the web UI, using them in incidents or the
  forensics report, alert rules on a failed unit, the service inventory.
- Self-healing (restarting a unit, #69) and the polkit rule for it; the agent's systemd unit and install scripts (#42),
  which only have to keep the system bus reachable.
- Units of other types than services, and units of user managers (`systemctl --user`).
- Following systemd's change signals for instant notice: changes are seen by the runs, at most one interval (10 or 30
  seconds) after they happen; the second of a failure is in the journal (#37). The short states of E11 are a stated
  residual.
- A new wire field for the moment of the change (systemd's `StateChangeTimestamp`) or for the time a unit became
  inactive.
- A log line per state change.

## Open questions

- What is the "event" of a state change? Answered: the existing `service_state` record kind of the wire format
  (`internal/model/service.go`; source `systemd` in the golden batch); one record per change, no new record kind.
- Which timestamp does an event carry? Answered: the capture time of the run that saw the change (at most one interval
  after it) and systemd's `ActiveEnterTimestamp` as `active_enter_at`; the exact second of a failure is in the journal
  (#37); a new field is out of scope.
- Is every state recorded every run, or only changes? Answered: only changes, plus the first run after a start and an
  hourly repeat per unit (S8), so the spool does not fill with identical records (the 2 GB budget, record 0025) while
  any hour still holds every unit's state for the incident windows, which read only the records of their window
  (`IncidentWindowReader`).
- How can a default list name the PHP-FPM units, whose names carry the PHP version (`plesk-php82-fpm.service`,
  `php8.1-fpm.service`)? Answered: entries may be patterns with `*` and `?`, matched against the units systemd has
  loaded; the default holds `plesk-php*-fpm` and `php*-fpm`.
- Does an entry need the `.service` suffix? Answered: no; as `systemctl` does, an entry without a unit type suffix gets
  `.service` (the suffix after the last dot decides: `php8.1-fpm` becomes `php8.1-fpm.service`); other unit types are
  refused because only services have `NRestarts` and the issue is about services. `restarts` is a required field
  (`internal/model/service.go`, no `omitempty`), so a not-found or masked service is recorded with the 0 systemd reports
  for it.
- What is a "missing unit"? Answered: an exact entry for which systemd reports `load_state` `not-found`; a pattern
  that matches nothing is not one, and masked or broken units are recorded, not warned about. "One warning" is one line
  naming every missing unit, logged at the first successful run after the start.
- Does reading unit states over D-Bus need a right? Answered: no: systemd's bus policy lets any local user read unit
  properties and load a unit's state, which is why `systemctl show` and `systemctl status` work for an unprivileged
  user; the plan checks this against systemd 249's policy file and records it, and a right found to be needed would
  make it a `security`-tier escalation.
- Is there a real sample of the D-Bus replies? Answered: not yet; the plan captures real systemd 249 replies into
  `testdata/` (see *Non-functional requirements*, *Test data*) or escalates for one.
- Does #30 provide what the collector plugs into? Answered: yes, #30 is closed: the scheduler with per-collector
  interval, timeout, pressure interval and gaps exists (`internal/scheduler`), and the agent builds the job list in
  `internal/agent`.

## Area documents

- [Agent](../../docs/areas/agent.md): a new section *Services* (the collector `systemd`, its schedule, what it reads and
  records, the first run, changes and the hourly repeat, the missing-unit warning, patterns and the cap, failures and
  logs, the residuals of E11); the *Status* paragraph and *Implementation* extended; *Connection* names the system bus as a third
  local channel; *Rights* states that reading unit states needs no right and what the unit must keep reachable;
  *Related decisions* lists the new record.
- [Configuration and secrets](../../docs/areas/configuration-and-secrets.md): the option `services.units` with its
  default, the suffix and pattern rules and the error texts of E1.
- [Wire format](../../docs/areas/wire-format.md): the producer of `service_state` (source `systemd`, a record per
  change, the first run, the first run after a lost sample and the hourly repeat; `active_enter_at` left out when
  unknown).
- Not area documents, but changed with them: `deploy/agent/README.md` (rights per collector: the `systemd` collector
  needs no group and no capability, only `AF_UNIX` and the system bus socket reachable from the unit) and
  `deploy/agent/agent.yaml` (a commented `services:` section with `units` at its default).

## Challenge

- M1 (record-on-change loses changes with lost samples): accepted. Confirmed in the code: a timed-out run's result is
  dropped (`internal/scheduler/runner.go` 185-187, 204-214), a failed run becomes a gap (233-237), and `Run` carries only
  `Start` and `Pressure` (`scheduler.go` 23-26), so the collector cannot know. Added S11; E4 and AC8 rewritten: after any
  lost sample, sink refusals included, the next successful run records every unit; the *Scheduler* requirement assigns
  the scheduler change to the plan, recorded against record 0110.
- Minor 1 (E5 against the scheduler's `record refused` log per run): accepted. The collector validates every unit
  itself, leaves the unit out, returns the gap itself and logs only the field name, never the validation text (E5, AC9).
- Minor 2 (install files): accepted. `deploy/agent/README.md` and `deploy/agent/agent.yaml` added under *Area
  documents*.
- Minor 3 (no real D-Bus sample): accepted. *Test data* requirement and an open question added; the plan names the
  captured replies or escalates.
- Minor 4 (`restarts` cannot be left out): accepted. AC3 and the open-question answer state `NRestarts` as reported, 0
  for not-found and masked units, and the reason for services only no longer rests on the unknown-versus-zero rule.
- Minor 5 (cap order, hourly wording): accepted. E6 states that tracked units are never dropped, a new match beyond
  the cap counts as left out and the warning is logged once per agent run; S8 now says records are at most one hour
  plus one interval apart, and that a shorter incident window may hold no record of a quiet unit.
