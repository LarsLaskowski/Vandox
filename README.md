<h1 align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/assets/vandox-logo-dark.svg">
    <img src="docs/assets/vandox-logo.svg" alt="Vandox" width="280">
  </picture>
</h1>

Vandox monitors a Linux server and keeps the evidence needed for forensics.
It consists of two binaries: the agent, written in Go to stay small on the monitored server, and the
backend, written in .NET 10 with a Blazor web UI. Both implement the same data model and wire format.

## Binaries

| Binary | Runs on | Purpose |
|---|---|---|
| `vandox-agent` (Go) | the monitored Linux server | collects data and ships it to the backend |
| `vandoxd` (.NET) | Docker container on any Docker host in the home network (NAS, mini PC, server) | backend with web UI |

Both support `--version`, which prints version, commit and build date. `vandoxd` without arguments runs the
service; `-config <file>` names its configuration file (default `/etc/vandox/vandoxd.yaml`) and
`-healthcheck` probes `/healthz` of a running service and exits 0 when it is healthy (the image's Docker
`HEALTHCHECK` uses it). `vandoxd import <path>` imports a directory, an archive or a log file into the
database, see *Import logs* below, `vandoxd incidents` rebuilds the outage incidents from the stored data, see
*Reconstruct incidents* below, and `vandoxd templates` rebuilds the log template statistics, see *Rebuild template
statistics* below.

## Layout

```
cmd/vandox-agent/   entry point of the agent (Go)
internal/           the agent's Go packages: config, model, wire (batch encoder), cli, version
src/Vandox.Backend/ the backend vandoxd: host, command line, Blazor web UI
src/Vandox.Core/    record model, wire decoder, configuration, safe file access, log parser framework, log signatures
src/Vandox.Storage/ SQLite store: schema, migrations, batched writes, queries, log search
src/Vandox.Import/  log import: scanner, archives, resumable batches
tests/              one test project per project under src/
tools/              console programs for developers, e.g. the storage write benchmark (docs/BENCHMARKS.md)
Vandox.slnx         the .NET solution
deploy/agent/       deployment files for the agent
deploy/backend/     deployment files for the backend
docs/               documentation
docs/assets/        logo, icon and favicon, see docs/BRANDING.md
testdata/           fixtures for tests, shared by both languages (testdata/wire: the golden batch of the wire format)
```

## Configuration

Each binary reads one strict YAML file for its options. The secrets come only from the environment (or
from files named by `*_FILE` variables), never from the file
([0032](docs/decisions/0032-secrets-only-from-environment-or-docker-secrets.md)).

| Binary | File | Commented example |
| ------ | ---- | ----------------- |
| `vandox-agent` | `/etc/vandox/agent.yaml` | [`deploy/agent/agent.yaml`](deploy/agent/agent.yaml) |
| `vandoxd` | `/etc/vandox/vandoxd.yaml` (mounted into the container) | [`deploy/backend/vandoxd.yaml`](deploy/backend/vandoxd.yaml) |

An unknown key, an unknown `VANDOX_` variable, a duplicate key, a second document, an anchor, a custom
tag or an invalid value is an error at start-up. Errors name the file, the line and the key, and never
the value.

**Agent options**

| Key | Default | Description |
| --- | ------- | ----------- |
| `agent_id` | required | Name of the server, 1 to 64 characters of `[A-Za-z0-9._-]`, starting with a letter or digit. |
| `backend.url` | required | Base URL of the backend ingest endpoint: `http` or `https`, a host and an optional port, no user info, path or query. |
| `spool.directory` | `/var/lib/vandox/spool` | Directory of the local spool, an absolute and clean path. |
| `log.level` | `info` | `debug`, `info`, `warn` or `error`. |

**Backend options**

| Key | Default | Description |
| --- | ------- | ----------- |
| `web.listen` | `:8080` | Address of the web UI as `[host]:port`. |
| `ingest.listen` | `:8081` | Address of the ingest endpoint, on a port other than `web.listen`. |
| `storage.directory` | `/data` | Directory of the backend's data, an absolute and clean path. |
| `log.level` | `info` | `debug`, `info`, `warn` or `error`. |
| `import.plesk_panel_log` | `/var/log/plesk/panel.log` | Server path of the Plesk panel log; a file at this path is read as that log whatever it holds. An absolute, clean path of a file in a directory. The default is not verified against a Plesk server (see *Import logs*). |
| `import.sw_cp_server_error_log` | `/var/log/sw-cp-server/error_log` | Server path of the error log of the Plesk panel web server `sw-cp-server`; recognized by this path only. Not verified against a Plesk server. |
| `import.nginx_error_log` | `/var/log/nginx/error.log` | Server path of the nginx error log. |
| `import.apache_error_log` | `/var/log/apache2/error.log` | Server path of the Apache error log (Debian and Ubuntu); on RHEL-family servers (AlmaLinux, Rocky Linux, CentOS) it is `/var/log/httpd/error_log`. |
| `import.time_zone` | none | IANA time zone of the server the imported logs come from, e.g. `Europe/Berlin` or `UTC`. Required to import traditional syslog files, MariaDB error logs, Dovecot log files, the Plesk panel, nginx and Apache error logs and the legacy `top`/`lsof` monitoring log (see *Import logs*). |
| `incidents.window_before` | `1h` | How long before an OOM kill an incident starts: one to four digits and `m` (minutes) or `h` (hours), from `1m` to `24h`, such as `30m` or `1h` (see *Reconstruct incidents*). |
| `incidents.window_after` | `30m` | How long after an OOM kill an incident ends; same format and range. |

**Secrets**

| Variable | File variant | Binary | Required | Rule |
| -------- | ------------ | ------ | -------- | ---- |
| `VANDOX_AGENT_TOKEN` | `VANDOX_AGENT_TOKEN_FILE` | agent, backend | agent: yes, backend: no | At least 32 printable ASCII characters without spaces, e.g. `openssl rand -hex 32`. |
| `VANDOX_WEB_PASSWORD_HASH` | `VANDOX_WEB_PASSWORD_HASH_FILE` | backend | no | Printable ASCII without spaces, 1 to 4096 characters. |
| `VANDOX_TELEGRAM_BOT_TOKEN` | `VANDOX_TELEGRAM_BOT_TOKEN_FILE` | backend | no | Printable ASCII without spaces, 1 to 4096 characters. |

Set either the variable or its `_FILE` form, not both. The `_FILE` value is an absolute path to a regular
file (for example a Docker secret) whose content is the secret, with at most one trailing line ending.

## Build

The agent (Go) and the backend (.NET) are built separately.

```
go build ./...
dotnet build Vandox.slnx
```

Inject version information into the agent with `-ldflags`. This is the form the release uses: the full commit
SHA, the commit time in UTC, `-trimpath` and a static binary.

```
CGO_ENABLED=0 go build -trimpath -buildvcs=false -ldflags "\
  -s -w \
  -X github.com/LarsLaskowski/Vandox/internal/version.Version=v0.1.0 \
  -X github.com/LarsLaskowski/Vandox/internal/version.Commit=$(git rev-parse HEAD) \
  -X github.com/LarsLaskowski/Vandox/internal/version.Date=$(TZ=UTC git log -1 --format=%cd --date=format-local:%Y-%m-%dT%H:%M:%SZ)" \
  -o bin/ ./cmd/...
```

The backend takes the same three values as MSBuild properties (the image build passes them as build arguments):

```
dotnet publish src/Vandox.Backend -c Release -o publish \
  -p:VandoxVersion=v0.1.0 \
  -p:VandoxCommit=$(git rev-parse HEAD) \
  -p:VandoxDate=$(TZ=UTC git log -1 --format=%cd --date=format-local:%Y-%m-%dT%H:%M:%SZ)
dotnet publish/vandoxd.dll --version
```

## Install

Releases are published from `v<major>.<minor>.<patch>` tags on `main`
(see [`docs/CONTRIBUTING.md`](docs/CONTRIBUTING.md#versioning-and-releases)).

**Agent.** Download `vandox-agent-linux-amd64` and `SHA256SUMS` from the
[GitHub release](https://github.com/LarsLaskowski/Vandox/releases), verify and install the binary (the full
agent installation, with user and systemd unit, is described under `deploy/agent/` once it exists):

```
sha256sum -c SHA256SUMS
gh attestation verify vandox-agent-linux-amd64 --repo LarsLaskowski/Vandox \
  --signer-workflow LarsLaskowski/Vandox/.github/workflows/release.yml \
  --source-ref refs/tags/vX.Y.Z --deny-self-hosted-runners
sudo install -m 0755 vandox-agent-linux-amd64 /usr/local/bin/vandox-agent
vandox-agent --version
```

The checksum only shows that the download is intact. `gh attestation verify` needs the GitHub CLI logged in
(`gh auth login`) and proves that the binary was built by this repository's release workflow for that tag.

The binary also has a signed SBOM. Verify it and save it with the same flags plus the SPDX predicate type:

```
gh attestation verify vandox-agent-linux-amd64 --repo LarsLaskowski/Vandox \
  --signer-workflow LarsLaskowski/Vandox/.github/workflows/release.yml \
  --source-ref refs/tags/vX.Y.Z --deny-self-hosted-runners \
  --predicate-type https://spdx.dev/Document/v2.3 \
  --format json --jq '.[0].verificationResult.statement.predicate' > vandox-agent-linux-amd64.spdx.json
```

**Backend.** Pull the image from Docker Hub, either by version or by the digest given in the release notes
(`Docker image: networlddev/vandox:<X.Y.Z>@sha256:<digest>`):

```
docker pull networlddev/vandox:<X.Y.Z>
docker pull networlddev/vandox@sha256:<digest>
```

To check that the image was built by this repository's release workflow, verify it by digest and then pull
that same digest:

```
gh attestation verify oci://docker.io/networlddev/vandox@sha256:<digest> --repo LarsLaskowski/Vandox \
  --signer-workflow LarsLaskowski/Vandox/.github/workflows/release.yml \
  --source-ref refs/tags/v<X.Y.Z> --deny-self-hosted-runners
docker pull networlddev/vandox@sha256:<digest>
```

The check names the digest and not the tag because a tag is resolved anew on every request and anyone with
push rights to `networlddev/vandox` could re-point it between the check and the pull, while the digest names
exactly the image that was verified. The attestation is stored on GitHub, not in Docker Hub, so
`--bundle-from-oci` and registry-side tools do not find it.

The image's SBOM is verified and saved the same way, again by digest:

```
gh attestation verify oci://docker.io/networlddev/vandox@sha256:<digest> --repo LarsLaskowski/Vandox \
  --signer-workflow LarsLaskowski/Vandox/.github/workflows/release.yml \
  --source-ref refs/tags/v<X.Y.Z> --deny-self-hosted-runners \
  --predicate-type https://spdx.dev/Document/v2.3 \
  --format json --jq '.[0].verificationResult.statement.predicate' > vandox-image.spdx.json
```

Both SBOMs are SPDX 2.3 JSON documents generated by syft at release time. They are stored on GitHub with the
provenance attestations and are not published as release assets.

Image tags: `X.Y.Z` for the release `vX.Y.Z`; `latest` is the highest stable release; a pre-release
(`vX.Y.Z-rc.N`) is published only under its own tag `X.Y.Z-rc.N`. A published version tag is never
overwritten. The image runs as UID/GID 65532 (non-root) on the chiseled ASP.NET runtime image (no shell, no package manager).

### Run the backend with Docker Compose

`deploy/backend/docker-compose.yml` starts the backend with a data volume, a read-only root file system and
a memory limit of 512 MiB. Copy it and `deploy/backend/vandoxd.yaml` into one directory on the Docker host
(Synology Container Manager, QNAP Container Station or plain `docker compose`) and create:

- `vandoxd.yaml`, the configuration file (every key is optional). Its `web.listen` port must match the
  container side of the `ports` entry in the compose file (8080).
- `secrets/vandox_agent_token`, the token the agents present (at least 32 printable ASCII characters, for
  example `openssl rand -hex 32 > secrets/vandox_agent_token`). Compose sets no owner or mode on file
  secrets outside Swarm, and the container sees the host file's owner and mode, so give the file to the
  container user and never leave it world-readable:

  ```
  sudo chown 65532:65532 secrets/vandox_agent_token
  sudo chmod 0400 secrets/vandox_agent_token
  ```

- `import/`, the directory for the log import (mounted read-only).
- optionally `.env` with `WEB_BIND_ADDRESS=<address>` (default `127.0.0.1`).

Then start it:

```
docker compose up -d
docker compose ps        # the container becomes "healthy"
```

The web port is published on `127.0.0.1:8080` for a reverse proxy on the same host; point the proxy at
`127.0.0.1:8080` and set `WEB_BIND_ADDRESS` for a proxy on another host. `/healthz` answers `ok` (200) while
the database is reachable and `unavailable` (503) otherwise; use it through the proxy for monitoring. The
loopback binding is defense in depth, not the access control: depending on the Docker Engine version and the
host firewall, hosts on the LAN may reach a published container port directly. The login of the web UI is
the access control (record 0060).

The ingest port (8081) is not published yet: the ingest API arrives with issue #40, which adds the binding on
the host's tailnet address. The database lives in the named volume `vandox-data` (`/data` in the container,
owned by 65532); a bind-mounted data directory instead of the volume must be owned by 65532 as well.
Put the data directory on fast storage, an SSD if the host has one: every committed batch waits for the disk, and on a hard disk
writes take about 50 % longer in the measurement of 10,000 records per transaction (`docs/BENCHMARKS.md`); a hard disk works, too.

### Import logs

`vandoxd import <path>` reads logs the operator saved on the backend host and stores their records
(origin `import`, never live, so they never raise an alert). The compose file mounts `./import` read-only at
`/import`. Put the directory, archive or file into `import/` and run the import inside the running
container; `-it` is needed so that Ctrl-C reaches the import (without a terminal the import keeps running
when the client is closed):

```
docker exec -it vandoxd /vandoxd import /import/<name>
```

The import runs as the container's user 65532 and reads only what that user may read. A copied `/var/log`
holds files such as `auth.log` and `mail.log` (`0640 root:adm`), which are listed as failed with "permission
denied" until that user may read them. Give read access to the user 65532 only, never to everyone, because
these files must not become readable for every local user on the host:

```
sudo chown -R 65532:65532 import/<name> && sudo chmod -R u+rX import/<name>
```

or, keeping the owner and with ACL support, `sudo setfacl -R -m u:65532:rX import/<name>`. The directory
`import/` itself only needs to stay readable and searchable (as created, `0755`).

What is read: a directory (recursively, in lexical order), a `.tar`, a gzip-compressed tar (`.tar.gz`,
`.tgz`), a single gzip file (a rotated log) or a plain file. Compression and archives are recognized by their
content, not by the file name; gzip files inside a directory or a tar archive are decompressed, and a tar
archive in the directory is read as well, but an archive inside an archive is not opened. Nothing is
extracted to disk, and symbolic links, FIFOs, sockets and devices are never followed or opened. Files are
read as streams, so memory use does not grow with the file size; lines longer than 16 KiB are cut. A run
handles at most 20,000 entries (files, subdirectories and archive entries); a larger input is refused before
anything is stored and has to be split. There is no size limit: a gzip bomb of valid lines is decompressed
and its records fill `storage.directory` until the import is stopped, so watch the
progress lines and press Ctrl-C.

Every file is read twice, first to detect its type and compute the SHA-256 of its decompressed content, then
to import it. A file no parser claims is listed as not recognized with the reason (no parser, empty,
unsupported compression such as bzip2, xz, zstd or zip, nested archive, symbolic link, not a regular file);
nothing is skipped silently. Progress is logged as JSON lines on standard error (what was found, each file,
progress every 64 MiB hashed and every 100,000 lines), and a summary on standard output lists the files
imported, continued, contained, already imported, not recognized and failed, the lines read, records stored, lines skipped and lines kept raw, the
time range of the stored records and the reasons. Names from the input are printed quoted. The exit code is 0
when every file was imported, continued, contained, already imported or not recognized, 1 when a file failed, the import was
interrupted or the configuration, the database or the input could not be opened, and 2 for a usage error.

Importing is repeatable: a file whose content was imported completely is not imported again, also under
another name or compressed differently (`syslog.1` and the later `syslog.2.gz`). An import that was stopped
(Ctrl-C, a database error) continues where it stopped when the same content is imported again, and no record
is stored twice. Stopping or restarting the container kills a running import; nothing committed is lost, and
the next import resumes. Limits: a syslog, MariaDB error log, Dovecot log, Plesk or web server error log or legacy monitoring log that grew since it was imported (the same log with more lines, as in
a newer copy of `/var/log`) is continued after the part imported before (a journal export that grew is imported as a whole), an older copy
of a log imported after a newer one stores nothing (it is listed as contained), except one that ends inside a
line, one whose newer copy was imported by an earlier version (before database schema 7), and one whose newer copy is not among the 64 largest
candidates (those are imported as a whole), and lines appended to a log while it is imported
come with a later import as a continuation.

Supported sources:

- `journalctl -o export` (the text export of the systemd journal, source type `journal`). Export a binary
  journal on the server with `journalctl -o export > journal.export` (add `--since`, `--until` or `-u` to limit
  it); the binary journal files themselves are not read.
- rsyslog files (source type `syslog`): `syslog`, `kern.log`, `mail.log` and their rotations (`syslog.1`, `syslog.2.gz`,
  `kern.log-20260301`), in the traditional format (`Mar  1 12:00:00 host program[pid]: message`) and in the RFC
  3339 format (`2026-03-01T12:00:00.123456+01:00 host program[pid]: message`), with or without `<PRI>`. The
  detection also claims any other file whose first line has such a header (so `mail.log`, `mail.err` and `maillog`
  are recognized by their first line). RFC 5424 files are not read. Lines of Postfix (`postfix/<daemon>`) and Dovecot
  (`dovecot`) in any rsyslog file or journal export get the priority of their severity (Postfix `warning:` 4, `error:` 3,
  `fatal:` and `panic:` 2; Dovecot `Debug:` 7, `Warning:` 4, `Error:` 3, `Fatal:` and `Panic:` 2) and the events `postfix.fatal`, `postfix.panic`, `dovecot.fatal`,
  `dovecot.panic`, and `postfix.database_unavailable` or `dovecot.database_unavailable` for a failed or lost
  MySQL/MariaDB connection.
- The MariaDB error log (source type `mariadb`), recognized by its content under any name (`mysql/error.log`,
  `error.log.1`, `<host>.err`, inside an archive): the first non-empty line of the file is an entry header such as
  `2026-03-01 12:30:15 0 [Note] ...`, and the file is not named `syslog` or `kern.log` (these stay with the syslog
  parser). Every entry, a header line with the lines without a header after it (a crash report with its stack trace),
  becomes one record; a very long entry keeps its beginning and says how many lines were left out. Warnings and errors
  get the priorities 4 and 3 and notes 6, and the lifecycle entries (start, ready for connections, normal shutdown,
  shutdown complete, abort by a signal, crash recovery start and end) get an event such as `mariadb.start` in the
  record field `event`. The formats of MariaDB 10.x are read. A copy that starts in the middle of an entry (the
  output of `tail`) is not recognized. MariaDB sends its error log to the journal by default under systemd. In a journal export or a syslog file,
  lines of `mariadbd` and `mysqld` that start with MariaDB's time stamp are read with the same rules. An entry with
  its crash report becomes one record of at most 16,384 bytes with the priority and event of its header; lines beyond
  that are stored as plain lines, not cut. Time, host and process ID come from the journal or syslog line, and the
  source type stays `journal` or `syslog`. A journal export needs no `import.time_zone`.
- Dovecot's own log file (source type `dovecot`), when `log_path` (or `info_log_path`, `debug_log_path`) points to a
  file: recognized by its first line (`Oct 09 03:12:13 imap-login: Info: Login: ...`, Dovecot's default
  `log_timestamp`) under any name except `syslog` and `kern.log`, including their rotation suffixes (`.N`,
  `-YYYYMMDD`), which stay with the syslog parser. Lines get the priority of their label (`Debug:` 7, `Info:` 6,
  `Warning:` 4, `Error:` 3, `Fatal:` and `Panic:` 2) and the events `dovecot.fatal`, `dovecot.panic` and
  `dovecot.database_unavailable` as above; host and program are empty. Times are read in `import.time_zone` like the
  traditional syslog format. Lines without a time stamp and a Dovecot label are skipped.
- The Plesk panel log (source type `plesk-panel`) and the error logs of the web servers: `sw-cp-server-error`
  (the panel's web server), `nginx-error` and `apache-error`. Each has a path option in `vandoxd.yaml`
  (`import.plesk_panel_log`, `import.sw_cp_server_error_log`, `import.nginx_error_log`,
  `import.apache_error_log`; the defaults are the usual server paths, and on a RHEL-family server Apache writes
  `/var/log/httpd/error_log`). A file is at such a path when its parent directory and its name equal those of the
  option (the rest of the path does not matter, so a copy such as `server-1/nginx/error.log` works), also with a
  rotation suffix (`error.log.1`, `error.log-20261010`); the paths are compared as text and never opened. A file at
  a path is read as that log whatever it holds. Besides, `plesk-panel`, `nginx-error` and `apache-error` claim any
  file, under any name but a syslog name, whose first line has their format (so the per-domain logs of Plesk are
  found); `sw-cp-server-error` is claimed by its path only. If two path options match the same file, the source type
  listed first above gets it. Lines get the priority of their level (`emerg` 0 to `debug` 7; Plesk's `EMERG` to
  `DEBUG`; Apache's `trace1` to `trace8` 7) and keep the text after the time stamp as message; the times are local
  times, read in `import.time_zone` (without it the first such line fails the file as above; the lines before it are
  stored). A line that is not of the format (for example the continuation lines of a PHP error in nginx's log, or
  Apache's startup messages) is not dropped: it is stored as a record without priority, with the time of the
  record before it (for the first lines of a file the modification time of the file, when it lies in the storable
  range, else the line is skipped), and is counted in the summary as a line kept raw with its reason, and logged as a
  warning. These logs contain text of clients and web applications, so a line written by a web application can
  imitate a line of the format with any time and level; the priority classifies text and does not prove which
  program wrote it. The format of the Plesk panel log, that it is written in the server's local time, that
  `sw-cp-server` writes nginx's format and the default Plesk paths are not verified against a Plesk server; a file in
  another format is kept as raw lines, and the path options name the real files.
- The legacy monitoring log of the shell script that ran before the agent (source type `legacy-monitor`), recognized by
  its first line, a snapshot header such as `===== Fri Oct  2 09:15:01 CEST 2026 =====` (the output of `date`), under any
  name except `syslog` and `kern.log`, including their rotation suffixes (`.N`, `-YYYYMMDD`). Each snapshot holds the output of `top -b -n 1` and, in the newer format, the
  sections `meminfo`, `pressure`, `vmstat` and `lsof -ni`. A snapshot becomes metrics (load, CPU, memory, swap and, in the
  newer format, `/proc` values with the vmstat counters and their rates), a process snapshot with the sums per program and,
  with `lsof`, a connection snapshot. The header time is read in `import.time_zone` with the zone abbreviation of the header
  (`CEST`, `CET`), which tells the two passes of the repeated hour apart, and the option must be set: without it the file
  fails with "import.time_zone is not set"; a first header whose abbreviation does not belong to the zone fails the file
  too. A snapshot that is cut or mixed with other output is skipped with a reason, a single unreadable row is left out
  and marks the snapshot incomplete, and the last snapshot of a copy is marked incomplete because the log has no end marker.
  The formats were built from the description of the script, not from the real file.

The file name of a rotated log must stay as the server wrote it (`syslog.1`, `kern.log-20260301`), and the
modification time of the files matters: the traditional format has no year, and the backend takes it from a
`-YYYYMMDD` date in the file name, else from the modification time of the file, and follows the order of the lines
from there. Copy the logs with their times preserved (`cp -a`, `rsync -a` or `tar`); after a plain `cp` the
modification time is the time of the copy, and the years of the lines are wrong.

The traditional format also has no time zone. Set `import.time_zone` in `vandoxd.yaml` to the IANA time zone of the
server the logs come from (for example `Europe/Berlin`); there is no default, because a wrong zone would shift every
time stamp and an import cannot be redone. Without the option a file with year-less lines fails with
"import.time_zone is not set" (the lines before the first year-less line are stored, and the run exits with 1).
Set the option and run the import again: the file is completed. RFC 3339 files and journal exports carry their
offset and need no option. MariaDB error logs, Dovecot's log file, the Plesk panel, nginx and Apache error logs and the legacy monitoring log also write local time without a zone and are read in the same zone;
without the option such a file fails at its first entry with the same reason.

Times outside 1677-09-21 to 2262-04-11 and dates that do not exist (31 November, 29 February in a year that is not a
leap year) are skipped with a reason, as is a year-less line in a file without a usable date. A multi-line kernel
report (an OOM kill, a `cut here` warning) becomes one record. Ubuntu's rsyslog writes every kernel line to both
`syslog` and `kern.log`, so importing both stores the kernel lines twice.

### Reconstruct incidents

`vandoxd incidents` rebuilds the outage incidents from everything stored, so run it after each import (it needs no
argument; run it in the container like the import):

```
docker exec -it vandoxd /vandoxd incidents
```

An incident is built from the OOM kills in the stored log lines: kills whose windows (`incidents.window_before` before
to `incidents.window_after` after each kill) overlap form one incident. Each incident gets a timeline of the events of its
window (boots, service starts, stops and failures, the kills, a running backup), a cause hypothesis from fixed rules (the
three programs whose memory grew most since the last boot, the three largest memory users, whether a backup ran, the last
boot, the number of service events), the extremes of the memory, swap, pressure and load metrics, the peak of the
connections and the new and unusually frequent log templates. Where the data is missing, the incident says unknown
instead of none. A backup is recognized by the program name `pzstd` only.

Incidents are stored. A re-run updates the incident of the same outage instead of adding another: the oldest number is
kept, incidents that now overlap are merged into it, an unchanged incident is left untouched, and a stored incident that
no rebuilt one overlaps is kept. The command prints one line per saved incident and then the counts:

```
#1 created 2026-10-02T04:59:00Z kills=1 victim="mariadbd"
Incidents:           1
  created:           1
  updated:           0
  unchanged:         0
  merged:            0
  kept:              0
  skipped:           0
OOM kills:           1
Log lines read:      1
```

`merged=<n>` follows the line of an incident that took over `n` others, and `skipped` counts incidents the database
refused (each is logged as a warning on standard error). Exit code 0 when the run completed, 1 when the configuration is
invalid, the database cannot be opened, the store failed or the run was interrupted (run it again to complete it), 2 for a
usage error. The log goes to standard error as JSON lines. The command reads every stored log line, so it takes a while on
a large database; details and limits are in `docs/areas/detection-and-reports.md`.

### Rebuild template statistics

The log template statistics count every stored log line under its template per hour. The service keeps them while it
stores lines, so lines stored before the statistics existed (a database upgraded from before schema version 8) and lines
counted under older normalization rules are missing or outdated. `vandoxd templates` empties the statistics and counts
every stored log line again under the current rules. Run it after upgrading such a database and after a release that changes
the normalization rules, then run `vandoxd incidents` again:

```
docker exec -it vandoxd /vandoxd templates
```

The command counts the lines in batches of 10,000, each in its own transaction, logs the progress to standard error as JSON
lines and then prints the summary:

```
Log lines:           3
Batches:             1
Templates:           2
```

Best run while no import runs. Start it once: a second run started meanwhile makes the first one stop with `superseded by
another rebuild` (exit 1) and itself counts everything. Exit code 0 when the run completed, 1 when the configuration is
invalid, the database cannot be opened, the store failed, the run was superseded or interrupted, 2 for a usage error. After
an exit 1 of any kind, run it again until it exits 0: it starts over, and until a run completes the statistics are incomplete.
