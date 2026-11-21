# Changelog

All notable changes to Vandox are listed here, newest release first. Versions follow [Semantic Versioning](https://semver.org/); a
release is a GitHub release with a `vX.Y.Z` tag on `main` ([CONTRIBUTING.md](docs/CONTRIBUTING.md#versioning-and-releases)). The GitHub release of each
tag lists the merged pull requests in detail.

## [Unreleased]

### Added

- **Processes view** (issue #50): the new page *Processes* (`/processes`) shows, for a range (the presets *1 h* to *1 y* or *From* and
  *To*, in UTC) and one source, two stacked charts, *Memory per program* and *CPU per program*, of the 10 programs with the highest average
  and the rest as `Other programs`, drawn from the per-program rollups so that they reach back as far as `retention.rollups_1h`. A drag
  zooms into a period; a click picks a moment and shows the process snapshot of the source closest to it within 10 minutes, and a second
  click after *Compare from here* compares the programs of the two moments (memory, CPU, change, new and gone). Source, range and
  moments are in the address. The history chart gains a stacked mode, and the storage benchmark gains `--processes-view`. There is no new
  option, route or listener.
- **Per-program rollups of process snapshots** (issue #458): the retention job now folds every process snapshot, once, into 5-minute and
  hourly buckets per source and program (memory as PSS when every snapshot has it, else RSS, and CPU %, each as minimum, average and
  maximum, with the rest as *other programs*), so a program's history outlives `retention.snapshots`; a process snapshot is removed only
  after it was folded, and the buckets follow `retention.rollups_5m` and `retention.rollups_1h`. The database is migrated to schema version 16 and
  folds the stored snapshots in steps. There is no new option, route or page; the processes view draws them. The storage benchmark gains `--programs`.
- **History view** (issue #49): the new page *History* (`/history`) draws up to 8 chosen metrics over a range (the presets *1 h* to
  *1 y* or *From* and *To*, in UTC) in one chart, each metric on its own scale with the first two axes drawn, a line per label set, a
  step's minimum, average and maximum in the legend and markers for OOM kills, boots, service restarts and backup runs, and lists
  the events below it. A drag zooms into a period; a click picks a moment and shows the process and the connection snapshot closest to it
  within 10 minutes, with a link to the logs of that moment. Metrics, range and moment are in the address. There is no new option, route
  or listener.
- **Overview page** (issue #48): the start page `/` now shows, below `Updated <time>`, the data freshness (last live data, last data
  received, a running backfill and the agent's spool), the latest state of every service unit with `failed` units first, the key figures
  (memory, swap, pressure, disks, MariaDB) and the open and latest incident. A value older than 5 minutes is marked `stale`, a missing
  one `unknown`. It refreshes every 30 seconds over the live connection, and its refreshes do not extend the web session. There is no new
  option, route or listener.
- **Time-series query service** in the backend (issue #47): for a range, up to 16 series and a target number of points it picks the
  resolution (raw values or a rollup step from 5 minutes to whole days), puts all lines on one time axis, marks spans without data as
  gaps and points between another line's samples as *between samples*, and returns the gaps of the range, the markers (OOM kills, boots,
  service restarts, backup runs) and up to when the rollup points are complete. The charts draw a line through a bridged point and
  break it at a gap. Views call it in process; there is no HTTP route and no option. The storage benchmark gains `--query`.

## [0.2.0] - 2026-11-13

The live release: the agent `vandox-agent` collects on the monitored Plesk server and sends to the backend over Tailscale, so
Vandox sees an outage as it builds up and not only from the logs afterwards. The agent replaces the legacy `top`/`lsof` monitoring
script; [docs/RETIRE_LEGACY_MONITORING.md](docs/RETIRE_LEGACY_MONITORING.md) compares the two and retires the script without losing
its history. Installation: [docs/INSTALL.md](docs/INSTALL.md), steps 10 and 11.

### Added

- **Agent `vandox-agent`** (`linux/amd64`, Ubuntu 22.04 with systemd) with a scheduler, a strict YAML configuration and a gap record
  for every missed sample. Collectors: system metrics (CPU, load, memory, swap, `/proc/meminfo`, `/proc/vmstat`, pressure stall
  information, disks), file systems, the clock, processes with the PSS memory per program, network connections and listeners from the
  kernel's socket tables with the owning process, systemd services, MariaDB, kernel OOM kills, reboots and backup runs, and its own
  resource use. A memory pressure mode shortens the intervals and records full process and connection snapshots.
- **Log shipping** of the systemd journal, of files in the rsyslog format and of the Plesk panel, nginx and Apache error logs
  (`log_shipping.files`), classified in the agent; lines are shipped live and the files' rotation is followed.
- **A persistent spool and transmission** with backfill: the agent keeps its records on disk while the backend is unreachable and
  sends them afterwards at a bounded bandwidth (`backend.backfill_bandwidth`).
- **Ingest API** in the backend (port 8081, one agent token bound to the one `agent_id`), classifying records as live or backfilled
  from the data, with a free-space guard (`ingest.min_free`).
- **New findings from live data:** listeners that appeared while the agent was stopped, a changed socket owner, and the socket's UID
  and user in the new-listener findings.
- **Install bundle** `vandox-agent-install-vX.Y.Z.tar.gz` with a `SHA256SUMS`, root-run install and uninstall scripts, a hardened
  systemd unit, the example configuration and the MariaDB scripts; the release checks the install in a CI job.
- **Tailscale**: the agent binds every connection that is not to a loopback address to `tailscale0`, the unit reaches only localhost
  and the tailnet, and the documentation has an example tailnet policy with checks for the monitored server.
- **A guide to retire the legacy monitoring script** ([docs/RETIRE_LEGACY_MONITORING.md](docs/RETIRE_LEGACY_MONITORING.md)): a
  comparison with the script's output, the import of its log before and after the retirement, the retirement from root's crontab
  and how to undo it.

### Changed

- **The compose file publishes the ingest port** on the address in `INGEST_BIND_ADDRESS` and refuses to start without it; the agent
  token secret is now used (`ingest.agent_id` names the agent). The Tailscale package on the Docker host must run in TUN mode.
- **The backend runs on .NET 11.**
- The installation guide gained steps 10 and 11 (the ingest port and the agent), and describes running the backend as a host user
  and without Compose.
- The web login behind a reverse proxy that terminates TLS no longer leaves the browser on the login page, and the report page bounds its memory for external texts.

### Not in this release

- Alerting, Telegram, the nightly report, retention and the history views are planned for later releases.
- Several agents: the backend accepts the one agent named in `ingest.agent_id`.

### Known limitations

- The agent runs on `x86_64` Ubuntu 22.04 servers only, and the image on `x86_64` hosts only.
- Nothing expires yet: the database keeps every imported line and every stored record until it is deleted.
- The agent has run on the production Plesk server since v0.2.0-rc.1; this entry states no result of that run. The memory pressure
  thresholds are fixed, and the agent's peak memory under sustained pressure is not yet confirmed by a long-term measurement there;
  a finding is reported as an issue and then examined.
- Of the checks on the monitored server, the agent's reading of the Plesk and web server error logs (a read grant, tried only in a
  stand-in on Ubuntu 24.04), the route and egress checks of the Tailscale setup and the rotation of rsyslog's log have not run on a
  production server; the checks that did run are in [deploy/agent/README.md](deploy/agent/README.md), *Checks on the server*.
- The format of the Plesk panel log and the legacy monitoring log were built from their descriptions; a line in another format is
  kept raw or skipped, and the import's report names it. QNAP Container Station has not been verified on a device.

[0.2.0]: https://github.com/LarsLaskowski/Vandox/releases/tag/v0.2.0

## [0.1.0] - 2026-10-17

The forensics release: Vandox imports the existing logs of a Plesk server and reconstructs past outages, above all memory exhaustion
that ends with the OOM killer terminating MariaDB. It runs as one Docker container on a Docker host in the home network. Installation:
[docs/INSTALL.md](docs/INSTALL.md).

### Added

- **Backend `vandoxd`** as the Docker image `networlddev/vandox` (`linux/amd64`, non-root, read-only root file system, chiseled
  ASP.NET runtime base pinned by digest), with a compose file, a health check (`/healthz`) and a strict YAML configuration with
  secrets from the environment or Docker secrets only.
- **Log import** (`vandoxd import`) of directories, tar and gzip archives and single files, repeatable and resumable, from the
  systemd journal export, rsyslog files (`syslog`, `kern.log`, `mail.log`), the MariaDB error log, Dovecot's own log file, the Plesk
  panel log, the `sw-cp-server`, nginx and Apache error logs and the legacy `top`/`lsof` monitoring log. Postfix and Dovecot lines are
  classified, and multi-line kernel reports become one record.
- **Storage** in one SQLite database with full-text search over the log lines and log templates with their hourly frequency
  (`vandoxd templates` rebuilds them).
- **Detection** of known events (OOM kills, service failures, full disks, `Too many connections` and others) by a catalog of log
  signatures.
- **Outage incidents** (`vandoxd incidents`) rebuilt from the stored data: kills merged by overlapping windows, a timeline, a cause
  hypothesis from fixed rules, memory, swap, pressure and load extremes and new or unusually frequent log templates.
- **Forensics report** over all incidents as Markdown in English or German (`vandoxd report`).
- **Web UI** behind a login (one argon2id password, server-side sessions, lockout of failed sign-ins, forwarded headers trusted only
  from configured proxies, strict security headers): the logs view with bounded full-text search, context and templates; the
  incidents view with timeline, memory charts and notes; the report page with a Markdown download.
- **Release pipeline**: the image and the agent binary are built from the tag, with SPDX SBOMs and provenance attestations on GitHub.

### Not in this release

- **The agent `vandox-agent`.** The release carries the binary, but it only prints its version; collection, the spool, the ingest
  API and live data arrive with v0.2.0.
- Alerting, Telegram, the nightly report, retention and the history views are planned for later releases.

### Known limitations

- The image runs on `x86_64` hosts only.
- Nothing expires yet: the database keeps every imported line until it is deleted.
- The formats of the Plesk panel log and the legacy monitoring log were built from their descriptions and have not been checked
  against a real server; a line in another format is kept raw. The NAS steps of the installation guide have not been verified on a
  device.

[0.1.0]: https://github.com/LarsLaskowski/Vandox/releases/tag/v0.1.0
