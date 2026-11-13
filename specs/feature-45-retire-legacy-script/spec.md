# Spec: Retire the legacy monitoring script and document v0.2.0

Source: Issue #45
Status: Approved by Product Manager

## Problem

[#45](https://github.com/LarsLaskowski/Vandox/issues/45) replaces the legacy script by `vandox-agent` and releases v0.2.0; this change
is its documentation and changelog only, and #45 stays open. Nothing tells the operator how to judge the parallel run or retire the
script without losing its history, `CHANGELOG.md` has no v0.2.0 entry, and the installation documents still defer checks to "v0.2.0"
or "#45" although the `v0.2.0-rc.1` backend and agent already ran together ([BENCHMARKS.md](../../docs/BENCHMARKS.md), *While sending*).

## Scenarios

- S1: Given the agent runs beside the script, when the operator opens the retirement guide, then for each kind of data the script
  writes (`top` summary, process table, `meminfo`, `pressure` and `vmstat` sections, `lsof -ni` connections) it names what the agent
  records instead, how often, where it is seen, and the differences the area documents state.
- S2: Given the comparison is satisfactory, when the operator follows the guide, then first `sysmonitor.log` and its rotated copies,
  and nothing else, are packed, copied into the backend's `import/` with step 6's file handling and imported with step 7's command; the
  archive is kept off the server, and the guide says not to rerun step 6 as a whole (E2).
- S3: Given that archive is imported and kept, when the operator retires the script, then only the line
  `* * * * * /usr/local/bin/syslog.sh` leaves root's crontab (the `psaadm` Plesk entries stay), and the operator waits until
  `sysmonitor.log` stops growing (cron may have started the current minute's run). Script and logs stay on the server but are not the
  kept copy (E3).
- S4: Given the script has stopped, when the operator packs and imports `sysmonitor.log` with all rotated copies again as in S2, then
  each file is reported imported, continued, contained or already imported, and the snapshots after the S2 copy up to the last run are
  stored, with or without a midnight rotation in between, except the rest of the snapshot the S2 copy cut and the `vmstat_per_second`
  rate of a continuation's first snapshot ([Legacy monitoring log parser](../../docs/areas/log-parsers-legacy-monitoring.md), *The end
  of the input*, *Sections (new format)*); the guide says both.
- S5: Given v0.2.0 is about to be released, when the maintainer reads `CHANGELOG.md`, then it has a v0.2.0 entry in the 0.1.0 entry's
  layout: what v0.2.0 adds, changes, leaves out and its known limitations.
- S6: Given an operator installs v0.2.0, when they read INSTALL.md steps 10 and 11, the agent configuration reference and the Tailscale
  setup, then nothing defers a check to "v0.2.0" or "#45", the checks of N3 are verified by the `v0.2.0-rc.1` run, and the documents
  agree.

## Errors and edge cases

- E1: A comparison item the agent does not deliver, or delivers worse: the guide says not to retire the script and to file an issue.
- E2: Data stored twice: the parallel week of the script's log is stored again under `legacy-monitor`, and importing more than that log
  (all of `/var/log`, a fresh journal export) would store again what was shipped live or imported before (no deduplication across
  sources or with live data, [Log import](../../docs/areas/log-import.md)); the guide says so.
- E3: Rotation (daily at 00:00, 14 copies) deletes copies older than about 14 days, also after the script stops: the guide copies them
  first, says why, and puts S4 within that time.
- E4: The import skips snapshots or does not recognize the log (its format was built from a description): the guide points to the
  import's report and an issue with the skipped shapes, never to a hand edit.
- E5: Undo: the guide says how to put the crontab line back.

## Non-functional requirements

- N1: The monitored server as the Product Manager confirmed it: `/usr/local/bin/syslog.sh`, run every minute by root's crontab only;
  its log `/var/log/sysmonitor.log` (root:adm 0640, about 17 MB a day) with `sysmonitor.log.1.gz` to `.14.gz`, rotated by
  `/etc/logrotate.d/sysmonitor`, of which nothing beyond its first line and that result is known or stated. Server commands are for a
  root shell, one line at a time. The only change before retirement is the S2 archive: mode `0600`, deleted once copied, or streamed
  off without being written.
- N2: The documents never state the result of the parallel run or the release (the maintainer's, #45).
- N3: Stated as verified by the `v0.2.0-rc.1` run, as the Product Manager confirmed, only: the Synology switch to TUN mode with its
  boot-up task, that the container came back after a NAS reboot (nothing on an extra `docker compose up -d`), the ingest probe's `401`
  and *Transmission to the backend*. Everything else no run showed stays not verified.

## Acceptance criteria

- AC1: `CHANGELOG.md` has a `[0.2.0]` section above `[0.1.0]` with its link line, in the 0.1.0 layout, dated `unreleased` as
  `docs/CONTRIBUTING.md` (*Cutting a release*) allows, listing the user-visible changes since v0.1.0 (S5).
- AC2: A retirement guide, linked from INSTALL.md after step 11, holds the comparison checklist of S1 with E1 (S1, E1).
- AC3: The guide's copy, import, retirement and undo steps do S2 to S4 in that order and cover E2 to E5 under N1 and N2 (S2, S3, S4,
  E2, E3, E4, E5, N1, N2).
- AC4: INSTALL.md steps 10 and 11, `deploy/agent/README.md`, the README's agent options and the area documents defer no check to v0.2.0
  or #45 but the parallel run; the checks of N3 are stated as verified by the `v0.2.0-rc.1` run, the rest not verified. Recorded results
  stay: the `v0.0.0-rc.0` cells of *Transmission to the backend* and *Tailscale checks* (`deploy/agent/README.md`, *Checks on the
  server*) are kept, their deferral marked settled, and the `v0.2.0-rc.1` result added as a separate note naming that run; INSTALL.md
  step 11 ("no release has the ingest API yet") likewise (S6, N3).

## Out of scope

Rolling out the agent, the parallel run, disabling the script on the server, the `v0.2.0` tag and release (the maintainer, #45); the
memory pressure thresholds and the peak under sustained pressure ([Agent](../../docs/areas/agent.md)); `/etc/logrotate.d/sysmonitor`;
any change of code, scripts or configuration files.

## Open questions

- The script, its schedule, its log and rotation. Answered: by the Product Manager (N1).
- Which deferred checks the `v0.2.0-rc.1` run showed. Answered: by the Product Manager, all four (N3).
- The changelog date. Answered: `unreleased` until the release pull request sets it (`docs/CONTRIBUTING.md`).

## Area documents

- [Backend host](../../docs/areas/backend-host.md): *Not verified on a host* narrowed to what the `v0.2.0-rc.1` run did not show.
- [Agent system metrics](../../docs/areas/agent-system-metrics.md), [Agent connections](../../docs/areas/agent-connections.md) and
  [Agent](../../docs/areas/agent.md) keep their references to the parallel run; the guide links them for the differences.

## Challenge

- Major 1 (S3/S4 ignore the daily rotation): accepted. Confirmed against
  [Log import](../../docs/areas/log-import.md), *A file that grew is continued*: a base is matched by the head key (first line) and must
  be a byte prefix, so a `sysmonitor.log` begun after a midnight rotation is a new file, and the grown old file sits in
  `sysmonitor.log.1.gz`. S4 now packs and imports `sysmonitor.log` with all rotated copies again (each reported as imported, continued,
  contained or already imported; a `.gz` entry of a tar archive is decompressed on its own, `Scanner.ClassifyAsync`). S3 no longer
  promises the files on the server: they are left there but are not the kept copy, which is the archives of S2 and S4; E3 says rotation
  goes on after the script stops and S4 falls within its 14 days.
- Major 2 (step 6 would store data twice): accepted. Confirmed at INSTALL.md:173-198 (whole `/var/log` plus a fresh `journalctl -o
  export`) and *Per file content* / *A file that grew is continued* (a grown journal export is imported whole; no deduplication with
  live data). S2 now archives only `sysmonitor.log*`, borrowing step 6's file handling and step 7's command, and says not to rerun step
  6 as a whole; E2 names why.
- Minor 1 (root shell, archive on the server): accepted. N1 now writes the server commands for a root shell and names the archive (mode
  `0600`, deleted after the copy, or streamed without writing it) as the only change before retirement; the mechanism is the plan's.
- Minor 2 (recorded run rewritten): accepted. Confirmed: rows at `deploy/agent/README.md`:764 and :769 are results of the
  `v0.0.0-rc.0` run (:738-752) against a `v0.1.0` backend. AC4 now keeps those cells, marks the deferral settled and adds the
  `v0.2.0-rc.1` result as a separate note naming that run; INSTALL.md:349-351 likewise.
- Minor 3 (wait for the last run; what a continuation loses): accepted. S3 now waits until `sysmonitor.log` stops growing; S4 names the
  skipped rest of the snapshot the S2 copy cut and the missing `vmstat_per_second` rate of a continuation's first snapshot
  ([Legacy monitoring log parser](../../docs/areas/log-parsers-legacy-monitoring.md), *The end of the input*, *Sections (new format)*).
