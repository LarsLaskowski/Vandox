<h1 align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/assets/vandox-logo-dark.svg">
    <img src="docs/assets/vandox-logo.svg" alt="Vandox" width="280">
  </picture>
</h1>

Vandox monitors a Linux server and keeps the evidence needed for forensics.
It consists of two binaries: the agent, written in Go to stay small on the monitored server, and the
backend, written in .NET 11 with a Blazor web UI. Both implement the same data model and wire format.

What the software does, area by area — formats, limits, error behavior and guarantees — is written in the
[area documents](docs/areas/README.md); this README is the user guide and links them.

## Binaries

| Binary | Runs on | Purpose |
|---|---|---|
| `vandox-agent` (Go) | the monitored Linux server | collects metrics and logs and ships them to the backend (from v0.2.0; v0.1.0 ships the backend only; what is built so far: [Agent](docs/areas/agent.md)) |
| `vandoxd` (.NET) | Docker container on any Docker host in the home network (NAS, mini PC, server) | backend with web UI and ingest API |

Both support `--version`, which prints version, commit and build date. The commands of `vandoxd`:

| Command | What it does |
| ------- | ------------ |
| `vandoxd [-config <file>]` | runs the service; the configuration file defaults to `/etc/vandox/vandoxd.yaml` |
| `vandoxd -healthcheck` | probes `/healthz` of the running service and exits 0 when it is healthy (the image's Docker `HEALTHCHECK`) |
| `vandoxd hash-password` | creates the hash of the web UI password, see *Run the backend with Docker Compose* |
| `vandoxd import <path>` | imports a directory, an archive or a log file into the database, see *Import logs* |
| `vandoxd incidents` | rebuilds the outage incidents from the stored data, see *Reconstruct incidents* |
| `vandoxd templates` | rebuilds the log template statistics, see *Rebuild template statistics* |
| `vandoxd report` | writes the forensics report as Markdown, see *Write the forensics report* |

Flags, output and exit codes of every command: [Backend host](docs/areas/backend-host.md), *Command line and exit codes*.

## Layout

```
cmd/vandox-agent/   entry point of the agent (Go)
internal/           the agent's Go packages: start-up, scheduler, collectors, log shipping, spool, sender, wire (batch encoder), model, config, cli, version
src/Vandox.Backend/ the backend vandoxd: host, command line, ingest endpoint, web login, Blazor web UI
src/Vandox.Core/    record model, wire decoder, configuration, safe file access, log parsers, log signatures and templates, incidents, forensics report
src/Vandox.Storage/ SQLite store: schema, migrations, batched writes, queries, log search and exploration
src/Vandox.Import/  log import: scanner, archives, resumable batches
tests/              one test project per project under src/
tools/              console programs for developers, e.g. the storage write benchmark (docs/BENCHMARKS.md)
Vandox.slnx         the .NET solution
deploy/agent/       deployment files for the agent
deploy/backend/     deployment files for the backend
docs/               documentation (docs/INSTALL.md: installation guide; docs/areas/: what the software does)
docs/assets/        logo, icon and favicon, see docs/BRANDING.md
testdata/           fixtures for tests, shared by both languages (testdata/wire: the golden batch of the wire format)
```

## Configuration

Each binary reads one strict YAML file for its options (the agent from v0.2.0 on; `-config <file>` names another path). The
secrets come only from the environment (or from files named by `*_FILE` variables), never from the file
([0032](docs/decisions/0032-secrets-only-from-environment-or-docker-secrets.md)). A mistake in the file or the environment stops
the binary at start-up with an error that names the file, the line and the key. The commented example files set every option.
The form, range and error text of each option are in [Configuration options](docs/areas/configuration-options.md); the rules of
the file and of the secrets in [Configuration and secrets](docs/areas/configuration-and-secrets.md).

| Binary | File | Commented example |
| ------ | ---- | ----------------- |
| `vandox-agent` | `/etc/vandox/agent.yaml` | [`deploy/agent/agent.yaml`](deploy/agent/agent.yaml) |
| `vandoxd` | `/etc/vandox/vandoxd.yaml` (mounted into the container) | [`deploy/backend/vandoxd.yaml`](deploy/backend/vandoxd.yaml) |

**Agent options** (read from v0.2.0 on)

| Key | Default | Description |
| --- | ------- | ----------- |
| `agent_id` | required | Name of this server, such as `web-1`; the backend's `ingest.agent_id` names it. |
| `backend.url` | required | Base URL of the backend's ingest endpoint, such as `http://100.64.0.1:8081` (the backend's tailnet address). |
| `backend.backfill_bandwidth` | `256KiB` | Bandwidth for sending spooled data after the backend was unreachable, such as `4MiB`. |
| `spool.directory` | `/var/lib/vandox/spool` | Directory of the local spool and the agent's state file; `deploy/agent/install.sh` creates it. |
| `spool.max_size` | `2GiB` | Size cap of the spool; when it is full, the oldest data is dropped and the gap recorded. |
| `spool.min_free` | `1GiB` | Free space kept on the spool's file system; `0MiB` turns the check off. |
| `log.level` | `info` | `debug`, `info`, `warn` or `error`. |
| `filesystem.mount_points` | `[/]` | Mount points whose size and inode use is recorded; `[]` turns the file system metrics off. |
| `processes.programs` | `[]` | Rules that name the program of a process by its command or the start of its command line. |
| `services.units` | `[sw-cp-server, sw-engine, psa, nginx, apache2, postfix, dovecot, mariadb, plesk-php*-fpm, php*-fpm, fail2ban]` | Services (unit names or patterns) whose state the `systemd` collector records; `[]` turns it off. |
| `mariadb.enabled` | `true` | Whether the `mariadb` collector asks the local MariaDB server for its status (see `deploy/agent/README.md`). |
| `mariadb.socket` | `/run/mysqld/mysqld.sock` | MariaDB's Unix socket; the agent logs in as `vandox-agent`, created by `deploy/agent/mariadb-user.sql`. |
| `backups.processes` | `[{command: pzstd}, {cmdline_prefix: "/opt/psa/admin/sbin/backup-archiver --pack "}, {cmdline_prefix: "/usr/lib/plesk-9.0/sw-tar --create "}]` | Rules that recognize the processes of a backup run; `[]` turns the `backups` collector off. |
| `log_shipping.journal` | `true` | Whether the `logs` collector ships the systemd journal. |
| `log_shipping.min_priority` | `info` | Least severe priority that is shipped, `emerg` to `debug`. |
| `log_shipping.files` | `[]` | Log files shipped besides the journal, each with `path` and `format`; the error logs need a read grant (`deploy/agent/README.md`). |
| `log_shipping.drop` | `[]` | Rules for lines that are not shipped, by log, program, priority and message pattern. |

**Backend options**

| Key | Default | Description |
| --- | ------- | ----------- |
| `web.listen` | `:8080` | Address of the web UI as `[host]:port`. |
| `web.trusted_proxies` | empty | Reverse proxies whose forwarded headers are trusted, such as `172.18.0.1` (see *Run the backend with Docker Compose*). |
| `ingest.listen` | `:8081` | Address of the ingest endpoint, on another port than `web.listen`. |
| `ingest.agent_id` | none | The `agent_id` of the agent the token belongs to; without it ingest is disabled. |
| `ingest.min_free` | `1GiB` | Free space on the data volume below which ingest stores nothing; `0MiB` turns the check off. |
| `storage.directory` | `/data` | Directory of the backend's data. |
| `log.level` | `info` | `debug`, `info`, `warn` or `error`. |
| `import.plesk_panel_log` | `/var/log/plesk/panel.log` | Server path of the Plesk panel log, for the import. |
| `import.sw_cp_server_error_log` | `/var/log/sw-cp-server/error_log` | Server path of the error log of the panel's web server `sw-cp-server`, for the import. |
| `import.nginx_error_log` | `/var/log/nginx/error.log` | Server path of the nginx error log, for the import. |
| `import.apache_error_log` | `/var/log/apache2/error.log` | Server path of the Apache error log, for the import (`/var/log/httpd/error_log` on RHEL-family servers). |
| `import.time_zone` | none | IANA time zone of the server the imported logs come from, such as `Europe/Berlin` (see *Import logs*). |
| `incidents.window_before` | `1h` | How long before an OOM kill an incident starts, such as `30m`; also the lookback of the forensics report. |
| `incidents.window_after` | `30m` | How long after an OOM kill an incident ends. |
| `retention.raw` | `30d` | How long raw metric values are kept, `1d` to `9999d` or `unlimited`; the 5-minute and hourly rollups stay. |
| `retention.rollups_5m` | `365d` | How long the 5-minute rollups are kept. |
| `retention.rollups_1h` | `1095d` | How long the hourly rollups and the log template statistics are kept. |
| `retention.logs` | `90d` | How long log lines are kept. |
| `retention.snapshots` | `30d` | How long process, connection and MariaDB status snapshots are kept. |
| `retention.events` | `1095d` | How long kernel events, service states, backup runs, gaps and listener findings are kept. |
| `retention.incidents` | `unlimited` | How long incidents (without a note) are kept. |

**Secrets**

| Variable | File variant | Binary | Required | Description |
| -------- | ------------ | ------ | -------- | ----------- |
| `VANDOX_AGENT_TOKEN` | `VANDOX_AGENT_TOKEN_FILE` | agent, backend | agent: yes, backend: no (ingest is disabled without it) | The token the agent presents to the ingest endpoint; create it with `openssl rand -hex 32`. |
| `VANDOX_WEB_PASSWORD_HASH` | `VANDOX_WEB_PASSWORD_HASH_FILE` | backend | no at load, but the service refuses to start without it | The hash of the web UI password, created with `vandoxd hash-password` (see *Run the backend with Docker Compose*). |
| `VANDOX_TELEGRAM_BOT_TOKEN` | `VANDOX_TELEGRAM_BOT_TOKEN_FILE` | backend | no | The token of the Telegram bot; read, but unused until the Telegram notifier (planned). |

Set either the variable or its `_FILE` form, not both; the `_FILE` form names a file that holds the secret, such as a Docker
secret.

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

**Step by step:** [`docs/INSTALL.md`](docs/INSTALL.md) installs the backend on a Docker host (with Synology and QNAP as
examples), imports the server's logs and leads to the first incident. This section is the reference behind it. What changed in each
release is in [`CHANGELOG.md`](CHANGELOG.md).

Releases are published from GitHub releases with `v<major>.<minor>.<patch>` tags on `main`
(see [`docs/CONTRIBUTING.md`](docs/CONTRIBUTING.md#versioning-and-releases)).

**Agent.** The agent collects on the monitored server and sends to the backend (from v0.2.0). Each release publishes an install bundle,
`vandox-agent-install-vX.Y.Z.tar.gz` (the install and uninstall scripts, the systemd unit, the example configuration, the two MariaDB scripts, a
`VERSION` file, the binary's checksum and a manifest of the files), and a `SHA256SUMS` that lists the bundle and the binary. A server needs no `git`.
The server must first be in the backend host's tailnet (Tailscale installed and logged in; see
[*Tailscale on the monitored server*](deploy/agent/README.md#tailscale-on-the-monitored-server)).
Run these lines as root on the monitored server (Ubuntu 22.04, systemd, `x86_64`), one at a time:

1. Become root (run this line on its own: it may ask for your password).
2. Set the version to install (replace `vX.Y.Z` by the release, for example `v0.2.0`).
3. Set the bundle's file name.
4. Set the release's download address.
5. Create a new private directory and change into it (start every attempt here, with a new directory).
6. Download the bundle and the checksum file of the release.
7. Check the bundle against its line in `SHA256SUMS`, and only if it matches, unpack it; if this prints anything but `...: OK`, stop and start again at step 5.
8. Run the install script from the unpacked bundle; it asks for the agent token. Replace `web-1` by the name of this server (its `agent_id`) and the address by the backend's tailnet address and ingest port.

```
sudo -i
v=vX.Y.Z
f=vandox-agent-install-$v.tar.gz
u=https://github.com/LarsLaskowski/Vandox/releases/download/$v
d=$(mktemp -d) && cd "$d"
curl --proto '=https' --proto-redir '=https' -fsSL -O "$u/$f" -O "$u/SHA256SUMS"
awk -v f="$f" '$2 == f' SHA256SUMS | sha256sum -c --strict - && tar -xzf "$f"
"$d/vandox-agent-install-$v/install.sh" --agent-id web-1 --backend-url http://100.64.0.1:8081
```

The variables live only in this shell; in a new shell, start again at step 1. If the script says
`the bundle is incomplete or changed; extract it again in a new directory`, start again at step 5.

The script downloads the agent binary `vandox-agent-linux-amd64` from the GitHub release of the bundle's version, so the server needs
HTTPS access to `github.com` and a working name server (turn off Tailscale's DNS on it, see
[*Tailscale on the monitored server*](deploy/agent/README.md#tailscale-on-the-monitored-server)). The script asks
`Agent token:` and shows nothing while you type or paste; press Enter after the token. If your console cannot paste into a hidden
prompt, see *The token prompt* in [`deploy/agent/README.md`](deploy/agent/README.md).

To remove the agent, run the bundle's `uninstall.sh` (`--purge` also deletes the configuration, token and spool). Upgrade, offline install, the options and the checks are in [`deploy/agent/README.md`](deploy/agent/README.md); the step-by-step path is step 11 of
[`docs/INSTALL.md`](docs/INSTALL.md). The unit lets the agent reach only localhost and the tailnet, so the backend URL names the
backend's tailnet **IP address**; the agent and the script refuse a host name, a MagicDNS name included. What the scripts check and change: [Agent installation](docs/areas/agent-installation.md).

A server that still runs the legacy monitoring script keeps its history and retires the script as
[`docs/RETIRE_LEGACY_MONITORING.md`](docs/RETIRE_LEGACY_MONITORING.md) shows.

**Backend.** Pull the image from Docker Hub, either by version or by the digest given in the release notes
(`Docker image: networlddev/vandox:<X.Y.Z>@sha256:<digest>`):

```
docker pull networlddev/vandox:<X.Y.Z>
docker pull networlddev/vandox@sha256:<digest>
```

To check that the image was built by this repository's release workflow, verify it by digest and then pull
that same digest (by digest, because a tag can be re-pointed between the check and the pull; see
[*Verifying a release*](docs/CONTRIBUTING.md#verifying-a-release)):

```
gh attestation verify oci://docker.io/networlddev/vandox@sha256:<digest> --repo LarsLaskowski/Vandox \
  --signer-workflow LarsLaskowski/Vandox/.github/workflows/release.yml \
  --source-ref refs/tags/v<X.Y.Z> --deny-self-hosted-runners
docker pull networlddev/vandox@sha256:<digest>
```

The attestations are stored on GitHub, not in Docker Hub, so `--bundle-from-oci` and registry-side tools do not find them.
The image's SBOM (SPDX 2.3 JSON, stored on GitHub with the attestations, not published as a release asset) is verified and saved
the same way, again by digest:

```
gh attestation verify oci://docker.io/networlddev/vandox@sha256:<digest> --repo LarsLaskowski/Vandox \
  --signer-workflow LarsLaskowski/Vandox/.github/workflows/release.yml \
  --source-ref refs/tags/v<X.Y.Z> --deny-self-hosted-runners \
  --predicate-type https://spdx.dev/Document/v2.3 \
  --format json --jq '.[0].verificationResult.statement.predicate' > vandox-image.spdx.json
```

Image tags: `X.Y.Z` for the release `vX.Y.Z`; `latest` is the highest stable release; a pre-release
(`vX.Y.Z-rc.N`) is published only under its own tag `X.Y.Z-rc.N`. The image is built for `linux/amd64` only, so an ARM-based NAS
cannot run it. It runs as UID/GID 65532 (non-root) without a shell ([Backend host](docs/areas/backend-host.md), *Container image*).

### Run the backend with Docker Compose

`deploy/backend/docker-compose.yml` starts the backend with a data volume, a read-only root file system and
a memory limit of 512 MiB ([Backend host](docs/areas/backend-host.md), *Compose file*). Copy it and `deploy/backend/vandoxd.yaml`
into one directory on the Docker host (Synology Container Manager, QNAP Container Station or plain `docker compose`) and create:

- `vandoxd.yaml`, the configuration file (every key is optional). Its `web.listen` port must match the
  container side of the `ports` entry in the compose file (8080). For agents, set `ingest.agent_id` to the agent's `agent_id`
  (uncomment `agent_id: web-1` under `ingest:` and change the name).
- `secrets/vandox_agent_token`, the token the agents present (for example
  `openssl rand -hex 32 > secrets/vandox_agent_token`). Compose sets no owner or mode on file
  secrets outside Swarm, and the container sees the host file's owner and mode, so give the file to the
  container user and never leave it world-readable:

  ```
  sudo chown 65532:65532 secrets/vandox_agent_token
  sudo chmod 0400 secrets/vandox_agent_token
  ```

- `secrets/vandox_web_password_hash`, the hash of the web UI password. Create it with `vandoxd hash-password`, which
  reads the password from the first line of standard input and prints the hash. The command does not switch off a terminal's echo, so
  read the password silently with `read -rs` and pipe it in; start the container with `-i` and **never with `-t`**, because a
  terminal shows what is typed:

  ```
  read -rs -p 'Web UI password: ' pw && echo
  printf '%s\n' "$pw" | docker run --rm -i networlddev/vandox hash-password > secrets/vandox_web_password_hash
  unset pw
  sudo chown 65532:65532 secrets/vandox_web_password_hash
  sudo chmod 0400 secrets/vandox_web_password_hash
  ```

  To change the password, create the file again and recreate the container. The password rules are in
  [Web login](docs/areas/web-login.md), *Creating the hash*.

- `import/`, the directory for the log import (mounted read-only).
- `.env` with `INGEST_BIND_ADDRESS=<this host's tailnet address>` (required, no default; `tailscale ip -4` prints it; `127.0.0.1`
  for an import-only setup without agents) and optionally `WEB_BIND_ADDRESS=<address>` (default `127.0.0.1`).

Then start it:

```
docker compose up -d
docker compose ps        # the container becomes "healthy"
```

The web port is published on `127.0.0.1:8080` for a reverse proxy on the same host; point the proxy at
`127.0.0.1:8080` and set `WEB_BIND_ADDRESS` for a proxy on another host. Monitor `/healthz` through the proxy
([Backend host](docs/areas/backend-host.md), *Health*). The loopback binding is defense in depth, not the access control: the
login of the web UI is ([Backend host](docs/areas/backend-host.md), *Exposure and trust*).

The web UI asks for the administrator password at `/login`; sessions and the lockout after wrong passwords are described in
[Web login](docs/areas/web-login.md). TLS and the certificate are the reverse proxy's job: `docs/REVERSE_PROXY.md` explains the
proxy settings, the certificate and the built-in proxies of Synology and QNAP. Set `web.trusted_proxies` in `vandoxd.yaml` to the
proxy's address as the container sees it; without it, the cookies are never `Secure` and every client behind the proxy shares one
lockout.

**Ingest port and Tailscale.** The ingest port 8081 is published on `INGEST_BIND_ADDRESS` only, the address the agents reach at
`http://<tailnet address>:8081` (their `backend.url`). This needs **Tailscale in TUN mode** on the Docker host; userspace-networking
mode is not supported ([Network and security model](docs/areas/network-and-security-model.md)). The Synology package runs in
userspace mode by default; `docs/INSTALL.md` shows how to switch it to TUN mode. What to look for (the `v0.2.0-rc.1` run verified the Synology
switch to TUN mode with its boot-up task and that the container came back after a NAS reboot; the rest of the Synology and userspace
behavior below is reported, not verified on a host):

- `docker compose up` stops with `set INGEST_BIND_ADDRESS to this host's tailnet address ...`: the variable is missing from `.env`.
- The container does not start and Docker reports a port binding error: the host does
  not have the address in `INGEST_BIND_ADDRESS`, because Tailscale is down or runs in userspace mode. Switch to TUN mode or fix the
  address, then `docker compose up -d`.
- After a reboot the container is not running although Tailscale is up: Docker may have started it before Tailscale brought up its
  address. Run `docker compose up -d` once Tailscale is up. (On the Synology host of the `v0.2.0-rc.1` run the container came back by
  itself after a NAS reboot.)
- The agent logs `token refused`: check the backend's log for `ingest agent refused` (the presented `agent_id` differs from
  `ingest.agent_id`) and `ingest token refused` (wrong token); `ingest disabled` at start names what is missing
  ([Ingest endpoint](docs/areas/ingest-endpoint.md)).

As for the web port, the binding is defense in depth: hosts on the LAN may reach the ingest port too, and the token is the boundary
(records 0126 and 0127).

The database lives in the named volume `vandox-data` (Docker lists it as `vandox_vandox-data`; `/data` in the container,
owned by 65532); a bind-mounted data directory instead of the volume must be owned by 65532 as well. To keep the files readable for
a user of the host (on a NAS, for example), run the container as that user's IDs (`user:` in the compose file, `--user` for
`docker run`) with a bind-mounted data directory, and give the secret and import files to that user instead of 65532;
`docs/INSTALL.md` shows both, and a `docker run` command for setups without Compose.
Put the data directory on fast storage, an SSD if the host has one; a hard disk works, too, with slower writes (`docs/BENCHMARKS.md`).

### Import logs

`vandoxd import <path>` reads logs the operator saved on the backend host and stores their records as imported, never
live data, so they never raise an alert. The compose file mounts `./import` read-only at `/import`. Put the directory, archive or
file into `import/` and run the import inside the running container; `-it` is needed so that Ctrl-C reaches the import (without a
terminal the import keeps running when the client is closed):

```
docker exec -it vandoxd dotnet /app/vandoxd.dll import /import/<name>
```

The import runs as the container's user (65532, or the IDs set with `user:` or `--user`) and reads only what that user may read. A copied `/var/log`
holds files such as `auth.log` and `mail.log` (`0640 root:adm`), which are listed as failed with "permission
denied" until that user may read them. Give read access to that user only, never to everyone, because
these files must not become readable for every local user on the host:

```
sudo chown -R 65532:65532 import/<name> && sudo chmod -R u+rX import/<name>
```

or, keeping the owner and with ACL support, `sudo setfacl -R -m u:65532:rX import/<name>`. The directory
`import/` itself only needs to stay readable and searchable (as created, `0755`).

The input is a directory, a `.tar` or `.tar.gz` archive, a gzip file (a rotated log) or a plain file; formats are recognized by
their content. The import has no size limit, so watch its progress lines and press Ctrl-C if an archive decompresses into more
than the disk holds. Importing is repeatable: a content imported before is not stored again, a stopped import continues where it
stopped, and a log that grew since its last import is continued. Progress goes to standard error, a summary of every file and its
outcome to standard output. What is read and refused, the limits, the repeatable import, the summary and the exit codes:
[Log import](docs/areas/log-import.md).

Supported sources:

- `journalctl -o export` (the text export of the systemd journal, source type `journal`). Export a binary
  journal on the server with `journalctl -o export > journal.export` (add `--since`, `--until` or `-u` to limit
  it); the binary journal files themselves are not read.
- rsyslog files (source type `syslog`): `syslog`, `kern.log`, `mail.log` and their rotations (`syslog.1`, `syslog.2.gz`,
  `kern.log-20260301`) in the traditional and the RFC 3339 format, also under other names; lines of Postfix and Dovecot get their
  priority and events. Ubuntu's rsyslog writes every kernel line to both `syslog` and `kern.log`, so importing both stores the
  kernel lines twice. See [System and mail log parsers](docs/areas/log-parsers-system.md).
- Dovecot's own log file (source type `dovecot`), when Dovecot's `log_path` points to a file. See
  [System and mail log parsers](docs/areas/log-parsers-system.md).
- The MariaDB error log (source type `mariadb`) under any name, and MariaDB's lines in a journal export or a syslog file. See
  [MariaDB error log parser](docs/areas/log-parsers-mariadb.md).
- The Plesk panel log (source type `plesk-panel`) and the error logs of the web servers (`sw-cp-server-error`, `nginx-error`,
  `apache-error`), found by the path options `import.*_log` and by their first line. Their format, the server's local time and the
  default Plesk paths are not verified against a Plesk server. See
  [Plesk and web server error log parsers](docs/areas/log-parsers-web.md).
- The legacy monitoring log of the shell script that ran before the agent (source type `legacy-monitor`: `top`, `meminfo`,
  `pressure`, `vmstat` and `lsof -ni` snapshots). Its formats were built from the description of the script, not from the real
  file. See [Legacy monitoring log parser](docs/areas/log-parsers-legacy-monitoring.md).

Keep the file names of rotated logs as the server wrote them (`syslog.1`, `kern.log-20260301`) and copy the logs with their times
preserved (`cp -a`, `rsync -a` or `tar`): the traditional syslog format has no year, and the import takes it from a `-YYYYMMDD`
date in the file name, else from the modification time of the file.

Set `import.time_zone` in `vandoxd.yaml` to the IANA time zone of the server the logs come from (for example `Europe/Berlin`)
before importing traditional syslog files, MariaDB error logs, Dovecot's log file, the Plesk and web server error logs or the
legacy monitoring log, which all write local time without a zone. There is no default, because a wrong zone shifts every time
stamp and an import cannot be redone. Without it such a file fails with "import.time_zone is not set"; set the option and run the
import again, and the file is completed. Journal exports and RFC 3339 files carry their offset and need no option.

### Explore the logs

The web UI shows the stored log lines under *Logs* (`/logs`) and the log templates under *Templates* (`/logs/templates`). All
times are **UTC**. The search filters by text, source, program, severity and a time range and lists the lines oldest first;
*Context* on a line shows the lines around it from the same log. The template page lists the message patterns of a range with
their counts and charts a template's lines per hour. Every filter is in the address, so a search can be bookmarked and shared.
Template numbers change when the template statistics are rebuilt (*Rebuild template statistics* below). The pages, their limits
and their messages: [Logs view](docs/areas/web-ui-logs-view.md).

### Reconstruct incidents

`vandoxd incidents` rebuilds the outage incidents from everything stored, so run it after each import (it needs no
argument; run it in the container like the import):

```
docker exec -it vandoxd dotnet /app/vandoxd.dll incidents
```

An incident is built from the OOM kills in the stored log lines, within `incidents.window_before` and
`incidents.window_after` around each kill: a timeline, a cause hypothesis, metric extremes, the connection peak and the new and
unusually frequent log templates. Where the data is missing, the incident says unknown instead of none. A re-run updates the
stored incidents instead of adding new ones. The command prints one line per saved incident and then the counts:

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

`merged=<n>` follows the line of an incident that took over `n` others. An interrupted run is completed by running it again. The
command reads every stored log line, so it takes a while on a large database. What an incident holds, its rules and limits:
[Incidents](docs/areas/incidents.md).

### Look at incidents

After `vandoxd incidents` the web UI shows the stored incidents under *Incidents* (`/incidents`), newest first. All times are
**UTC**. An incident's page (`/incidents/<number>`) shows its window, its kills, the hypothesis, the timeline, the connection peak,
the metric extremes, the log templates and charts of memory and connections over the window. Every time on it opens *Logs* at
that moment. A note per incident is saved with *Save note* and stays with its incident when `vandoxd incidents` runs again. The
pages and their limits: [Incidents view](docs/areas/web-ui-incidents-view.md).

### Write the forensics report

`vandoxd report` writes a forensics report over the whole stored period as Markdown to standard output. Run it after
`vandoxd incidents`, and run `vandoxd incidents` again after every import: the report lists the incidents as they were
last stored. Run it in the container without `-t` (a terminal turns the line feeds into CR LF) and redirect the output:

```
docker exec vandoxd dotnet /app/vandoxd.dll report > forensics.md
docker exec vandoxd dotnet /app/vandoxd.dll report -lang de > forensics.md
```

The report is in English; `-lang de` writes it in German. It holds a summary of the outages, recurring patterns (time of day, a
running backup, a boot, high memory use of the Plesk panel), the memory consumers, new listeners and every outage with its victims
and hypothesis. All times are UTC, and "unknown" is never "no": a rule without enough data is written as unknown with its reason.
On a large database the command can take minutes, and it logs no progress while it reads. If it exits with 1, standard output holds an incomplete report: discard it. The
rules, the content and the limits: [Forensics report](docs/areas/forensics-report.md) and
[Listener changes](docs/areas/listener-changes.md).

### Read the forensics report in the web UI

The web UI shows the same report under *Report* (`/report`); *Deutsch* (`report?lang=de`) shows it in German. The report is made
when the page opens and lists the outages 50 at a time; *Download Markdown* saves the whole report, the same bytes as
`vandoxd report` writes. The page stops a run after 2 minutes; on a very large database use `vandoxd report`, which has no limit.
The page and its limits: [Report page](docs/areas/web-ui-report-page.md).

### Rebuild template statistics

The log template statistics count every stored log line under its template per hour. The service keeps them while it stores
lines, but lines stored before the statistics existed (a database upgraded from before schema version 8) and lines counted under
older normalization rules are missing or outdated. `vandoxd templates` counts every stored log line again under the current
rules. Run it after upgrading such a database and after a release that changes the normalization rules, then run
`vandoxd incidents` again. Template numbers belong to the statistics: after a rebuild, saved links to a template may show
another template or none.

```
docker exec -it vandoxd dotnet /app/vandoxd.dll templates
```

The command logs its progress to standard error and then prints the summary:

```
Log lines:           3
Batches:             1
Templates:           2
```

Best run while no import runs, and start it once. Until a run exits 0 the statistics are incomplete: after an exit 1, run it
again. The rules: [Log templates](docs/areas/log-templates.md) and [Storage](docs/areas/storage.md).
