# Spec: Outage reconstruction

Status: Approved by Lead

Source: issue #23 (milestone v0.1.0 Forensics; depends on #20, #21, #22, all closed and merged on `main` 1aedd12).

## Problem / motivation

v0.1.0 is the forensics release: Vandox imports the old logs of the production server and explains past
outages. The known failure mode is memory exhaustion on a 2 GB server that ends with the OOM killer terminating
MariaDB, after which Plesk and mail go down. The building blocks exist: the importer stores log lines, the legacy
`top`/`lsof` monitoring log becomes metrics and process and connection snapshots (#20), the signature detector
recognizes OOM kills, boots, service starts, stops and failures and MariaDB and mail events on stored lines (#21),
and the write path keeps log template statistics with queries for new and unusually frequent templates (#22).
Nothing turns them into outages yet: the detector is called by nobody, and no incident is stored anywhere.

## Behavior

- A new sub-command `vandoxd incidents [-config file]` reconstructs the incidents from everything stored and
  prints what it did. It is run after an import (and again after every further import); the service and
  `vandoxd import` do not run it.
- **Incidents from OOM kills.** Every OOM kill the signature detector finds in the stored log lines (copies of one
  kill in `syslog`, `kern.log` and the journal are already one kill there) gets a window from *before* ahead of
  it to *after* behind it. Kills whose windows overlap form one incident, whose window runs from the first
  kill's window start to the last kill's window end. *Before* and *after* are configured in the backend
  configuration file (`incidents.window_before`, default `1h`; `incidents.window_after`, default `30m`).
- **What an incident holds** (all deterministic, computed from stored data only):
  - the kills (time, host, victim pid and command, anon-RSS, `oom_score_adj`, linked log lines) and their number;
  - a **timeline**: every event the signature catalog detects in the window (OOM kills, boots, service
    starts, stops and failures, MariaDB crash and recovery, *Too many connections*, a full disk, Postfix and
    Dovecot fatal errors) and the first and last process snapshot in which a backup program ran, in time order;
  - a **cause hypothesis**, structured, not prose: the (up to) **three programs with the strongest memory
    growth** in the *before* period of the first kill, the three **largest memory consumers** in the last
    process snapshot before it, and the **correlated activities** before it: whether a backup program ran,
    the last boot, the number of service starts, stops and failures;
  - **system metrics** of the window: lowest available memory, highest swap use, highest memory pressure and
    highest load, each with its time;
  - **connection history** of the window: the connection snapshot with the most sockets and its three busiest
    processes;
  - **log templates**: templates first seen in the window and hours of the window in which a template was well
    above its usual level.
- Where the data needed for a statement is missing, the incident says **unknown**, never "nothing happened":
  growth is unknown with fewer than two process snapshots before the kill, a running backup is unknown without
  any process snapshot there, a metric without samples is absent.
- **Stored and re-computable.** Incidents are stored in the database. Running the sub-command again updates
  them: a recomputed incident whose window overlaps a stored one keeps that incident's number and replaces its
  content (or leaves it untouched when nothing changed); stored incidents that a recomputed one joins are merged
  into the oldest of them; a recomputed incident that overlaps none is added; a stored incident that no
  recomputed one overlaps is kept as it is (incidents outlive the 90-day log retention).
- The output lists one line per incident (number, created, updated or unchanged, first kill time, number of
  kills, the first victim's command) and a summary of the counts. Victim commands are log text and are printed
  escaped.

## Acceptance criteria

- [ ] AC1: Every OOM kill in the stored data produces exactly one incident, or is part of the incident whose
  window overlaps its own; copies of one kill in several log files count as one kill.
- [ ] AC2: Each incident has a timeline of the events in its window and up to three growth candidates (the
  programs whose memory grew most before the first kill), or growth marked unknown when fewer than two process
  snapshots exist before the kill.
- [ ] AC3: Running the reconstruction again after a new import updates the stored incidents instead of
  duplicating them; a run without new data changes nothing.
- [ ] AC4: The window before and after a kill is configurable in the backend configuration file and is validated
  strictly.
- [ ] AC5: `vandoxd incidents` runs the reconstruction, prints the incidents and the counts with log text
  escaped, and exits 0 on success, 1 on a configuration, database or storage error or an interruption, 2 on a
  usage error.

## Out of scope

- Showing incidents in the web UI and notes per incident (#27), the forensics report across incidents (#28).
- Live and backfilled incidents, OOM kills and boots from the agent's `kernel_event` records, recorded data gaps
  in the incident window (#53; no import parser writes `kernel_event` or `gap` records).
- A configurable list of backup programs (the agent's backup detection with configurable patterns is #36).
- Running the reconstruction automatically after an import or inside the service.
- Prose or AI-written hypotheses (record 0008).

## Open questions

None; the defaults (window, sub-command, backup program list) are Lead decisions in record 0096 and can be
changed by the Product Manager without touching the design.
