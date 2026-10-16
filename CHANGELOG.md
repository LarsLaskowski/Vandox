# Changelog

All notable changes to Vandox are listed here, newest release first. Versions follow [Semantic Versioning](https://semver.org/); a
release is a `vX.Y.Z` tag on `main` ([CONTRIBUTING.md](docs/CONTRIBUTING.md#versioning-and-releases)). The GitHub release of each
tag lists the merged pull requests in detail.

## [0.1.0] - unreleased

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
