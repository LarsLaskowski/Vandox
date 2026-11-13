# Spec: Retire the legacy monitoring script and document v0.2.0

Source: Issue #45
Status: Draft

## Problem

[#45](https://github.com/LarsLaskowski/Vandox/issues/45) replaces the legacy script by `vandox-agent` and releases v0.2.0; this change
is its documentation and changelog only (*Out of scope*), and #45 stays open. Today nothing tells the operator how to judge the parallel run or how to retire the script without losing its history, `CHANGELOG.md`
has no v0.2.0 entry, and the installation documents still defer several checks to "v0.2.0" or "#45" although the `v0.2.0-rc.1`
backend and agent already ran together ([BENCHMARKS.md](../../docs/BENCHMARKS.md), *While sending*).

## Scenarios

- S1: Given the agent runs beside the script, when the operator opens the retirement guide, then for every kind of data the script
  writes (the `top` summary, the process table, the `meminfo`, `pressure` and `vmstat` sections, the `lsof -ni` connections) the guide
  names what the agent records instead, how often, where the operator sees it, and the differences the area documents already state.
- S2: Given the comparison is satisfactory, when the operator follows the guide, then first `/var/log/sysmonitor.log` and its rotated
  copies `sysmonitor.log.1.gz` to `sysmonitor.log.14.gz` are copied off the server and imported with `vandoxd import` as INSTALL.md
  steps 6 and 7 describe, and a copy is kept.
- S3: Given the logs are kept, when the operator follows the retirement step, then the line `* * * * * /usr/local/bin/syslog.sh` is
  removed from root's crontab, every other crontab entry (the `psaadm` Plesk entries among them) is left as it was, the script stops
  writing, and `/usr/local/bin/syslog.sh` and the log files stay on the server.
- S4: Given the script is retired, when the operator imports the current `sysmonitor.log` once more, then the import continues after
  the part it imported before, so the snapshots up to the last run of the script are stored.
- S5: Given v0.2.0 is about to be released, when the maintainer reads `CHANGELOG.md`, then it has a v0.2.0 entry in the layout of the
  0.1.0 entry with what v0.2.0 adds, changes, leaves out and its known limitations.
- S6: Given an operator installs v0.2.0, when they read INSTALL.md steps 10 and 11, the agent configuration reference and the Tailscale
  setup, then no statement defers a check to "v0.2.0", "#45" or "until v0.2.0 is released", the four checks the `v0.2.0-rc.1` run
  showed are stated as verified by that run, and the documents agree with each other.

## Errors and edge cases

- E1: An item of the comparison the agent does not deliver, or delivers worse: the guide says not to retire the script and to record the
  difference in an issue.
- E2: The parallel week is in both sources: the script's log of that week stores it a second time under source `legacy-monitor` (no
  deduplication, [Log import](../../docs/areas/log-import.md)); the guide says so and what it affects.
- E3: Rotation removes copies: logrotate rotates the log daily at 00:00 and keeps 14 compressed copies (as the server's listing shows),
  so a copy older than about 14 days is gone; the guide copies them all before anything else and says why.
- E4: The import skips snapshots or does not recognize a file as the legacy log (the format was built from a description, not the real
  file): the guide points to the import's report and to an issue with the shapes it skipped, never to a hand edit of the log.
- E5: Undoing the retirement: the guide states how to put the crontab line back.

## Non-functional requirements

- N1: The target host is the monitored server as the Product Manager confirmed it: the script `/usr/local/bin/syslog.sh` (root), run
  every minute by root's crontab only (no file in `/etc/cron.d`, `/etc/cron.hourly` or `/etc/cron.daily`, no systemd timer); its log
  `/var/log/sysmonitor.log` (root:adm, 0640, about 17 MB a day); rotation by `/etc/logrotate.d/sysmonitor`, of which only its first line
  and the listing's result (daily at 00:00, 14 compressed copies, root:adm 0640) are known. The guide states nothing about that file
  beyond this. Every command runs as root, one line at a time, and nothing changes the server before the retirement step.
- N2: The documents never state the result of the parallel run or the release: those are the maintainer's (#45).
- N3: Only what the Product Manager confirmed from the `v0.2.0-rc.1` run is stated as verified: the Synology switch to TUN mode with its
  boot-up task, that the container came back after a NAS reboot (not whether an extra `docker compose up -d` was needed), the `401` of
  the ingest probe and *Transmission to the backend*. Every other statement no run has shown stays marked as not verified.

## Acceptance criteria

- AC1: `CHANGELOG.md` has a `[0.2.0]` section above `[0.1.0]`, with its link line, in the 0.1.0 entry's layout, dated `unreleased` as
  `docs/CONTRIBUTING.md` (*Cutting a release*) allows, listing the user-visible changes since v0.1.0 (S5).
- AC2: A retirement guide, linked from INSTALL.md after step 11, holds the comparison checklist of S1 with E1 (S1, E1).
- AC3: The guide's copy, import, retirement and undo steps do S2 to S4 in that order and cover E2 to E5 under N1 and N2 (S2, S3, S4,
  E2, E3, E4, E5, N1, N2).
- AC4: INSTALL.md steps 10 and 11, `deploy/agent/README.md`, the README's agent options and the area documents hold no statement that
  defers a check to v0.2.0 or #45 other than the parallel run; the four checks of N3 are stated as verified by the `v0.2.0-rc.1` run, and
  the rest stays not verified (S6, N3).

## Out of scope

Rolling out the agent, the parallel run itself, disabling the script on the server, the `v0.2.0` tag and release (the maintainer, #45);
the memory pressure thresholds and the peak under sustained pressure, which the parallel run decides ([Agent](../../docs/areas/agent.md),
[BENCHMARKS.md](../../docs/BENCHMARKS.md)); a change to `/etc/logrotate.d/sysmonitor`; any change of code, scripts or configuration
files of the repository.

## Open questions

- Where the script, its schedule and its log live, and whether the log is rotated. Answered: by the Product Manager (N1).
- Which deferred checks the `v0.2.0-rc.1` run showed. Answered: by the Product Manager, all four (N3); whether the container came back
  without an extra `docker compose up -d` was not given, so the documents claim neither.
- The changelog date. Answered: `docs/CONTRIBUTING.md` lets the section say `unreleased` until the release pull request sets the date.

## Area documents

- [Backend host](../../docs/areas/backend-host.md): its *Not verified on a host* entry narrowed to what the `v0.2.0-rc.1` run did not show.
- [Agent system metrics](../../docs/areas/agent-system-metrics.md), [Agent connections](../../docs/areas/agent-connections.md) and
  [Agent](../../docs/areas/agent.md) keep their references to the parallel run of #45; the guide links them for the differences.
