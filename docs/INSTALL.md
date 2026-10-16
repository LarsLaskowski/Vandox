# Installation: from zero to the first incident

This guide installs the Vandox backend `vandoxd` on a Docker host in the home network, imports the logs of the monitored server and
opens the first reconstructed outage in the web UI. It is the path for v0.1.0, the forensics release: Vandox works from the logs the
server already has, and there is **no agent to install** (the agent arrives with v0.2.0). The [README](../README.md) is the reference
for every option, command and limit named here.

The steps are the same on any Docker host. Synology Container Manager and QNAP Container Station are shown as worked examples where
they differ.

> The NAS-specific steps were written from the vendors' documentation and have **not been verified on a device**; menu names can differ
> between versions. The commands are what counts.

## What you need

- **A Docker host on `x86_64`** (`uname -m` prints `x86_64`) with Docker Engine and Docker Compose. The image is built for
  `linux/amd64` only, so an ARM-based NAS cannot run it. Give the container about 512 MiB of memory and put its data on an SSD if the
  host has one.
- **A shell on the Docker host** (SSH). Creating the secrets, setting file owners and running the import need the command line; the
  NAS interfaces cannot do these steps. On a Synology NAS enable SSH under *Control Panel → Terminal & SNMP*, on a QNAP NAS under
  *Control Panel → Network & File Services → Telnet / SSH*, and sign in as an administrator. Docker commands need `sudo` there.
- **A shell on the monitored server** with `sudo`, to collect its logs.
- **A reverse proxy with TLS** for the web UI: the NAS's built-in proxy, nginx or Caddy ([REVERSE_PROXY.md](REVERSE_PROXY.md)).
- **The IANA time zone of the monitored server**, for example `Europe/Berlin` (`timedatectl show -p Timezone --value` on the
  server). Most of its logs carry no time zone, and a wrong zone shifts every time stamp.

## 1. Create the project directory

Create one directory for Vandox on the Docker host and copy the two deployment files into it:

| Host | Directory (example) |
| ---- | ------------------- |
| Synology | `/volume1/docker/vandox` (the shared folder `docker` that Container Manager creates) |
| QNAP | `/share/Container/vandox` |
| any other | for example `/opt/vandox` |

```
sudo mkdir -p /volume1/docker/vandox && sudo chown "$USER" /volume1/docker/vandox && cd /volume1/docker/vandox
curl -fsSLO https://raw.githubusercontent.com/LarsLaskowski/Vandox/v0.1.0/deploy/backend/docker-compose.yml
curl -fsSLO https://raw.githubusercontent.com/LarsLaskowski/Vandox/v0.1.0/deploy/backend/vandoxd.yaml
mkdir -p secrets import
```

In `docker-compose.yml`, replace `networlddev/vandox:latest` with `networlddev/vandox:0.1.0`, so an upgrade happens only when you
change the tag. To check the image's origin before the first start, verify its attestation as the README shows (*Install*).

## 2. Configure

Edit `vandoxd.yaml`. Two settings matter for this release:

```yaml
import:
  time_zone: Europe/Berlin     # the monitored server's time zone

web:
  trusted_proxies: "172.18.0.1"  # the reverse proxy's address as the container sees it (step 5)
```

Without `import.time_zone` the import fails at the first syslog, MariaDB, Dovecot, Plesk or web server error log line and at the
legacy monitoring log; it can be set later and the import run again. If the Plesk server writes its panel or web server error logs to
other paths than the defaults, set the `import.*_log` paths too (README, *Configuration*). Every other key may stay at its default.

## 3. Create the secrets

The container runs as the user `65532`, and Compose passes file secrets with the host file's owner and mode, so each secret file
belongs to `65532` and is readable by nobody else.

**Web UI password hash.** The password has 12 to 1024 characters. Read it without echo and never give the container a terminal
(`-i`, not `-t`), because a terminal shows what is typed:

```
read -rs -p 'Web UI password: ' pw && echo
printf '%s\n' "$pw" | sudo docker run --rm -i networlddev/vandox:0.1.0 hash-password > secrets/vandox_web_password_hash
unset pw
```

**Agent token.** v0.1.0 does not use it, but the compose file passes it, so the file must exist:

```
openssl rand -hex 32 > secrets/vandox_agent_token
```

Then hand both files to the container user:

```
sudo chown 65532:65532 secrets/vandox_web_password_hash secrets/vandox_agent_token
sudo chmod 0400 secrets/vandox_web_password_hash secrets/vandox_agent_token
```

## 4. Start the backend

**Command line (any host):**

```
sudo docker compose up -d
sudo docker compose ps      # wait until the container is "healthy"
```

Older installations name the command `docker-compose`.

**Synology Container Manager:** *Project → Create*, choose the directory of step 1 as the path and *Use the existing
docker-compose.yml*, then *Build*. The project shows up in the list and starts the container.

**QNAP Container Station:** *Applications → Create*, paste the content of `docker-compose.yml`, and replace the relative paths
(`./import`, `./vandoxd.yaml`, `./secrets/...`) with the absolute paths of step 1, because the application is stored in another
directory. Starting it from the command line in the directory of step 1 avoids that.

If the container does not become healthy, `sudo docker logs vandoxd` names the reason, for example a missing password hash or an
invalid option in `vandoxd.yaml`.

## 5. Put the web UI behind the reverse proxy

The web port is published on `127.0.0.1:8080`, for a proxy on the same host. Set up the proxy as
[REVERSE_PROXY.md](REVERSE_PROXY.md) shows, with the sections for Synology and QNAP, then:

1. Find the address the proxy has as seen by the container (often the gateway of the network `vandox_default`):
   `sudo docker network inspect vandox_default --format '{{ (index .IPAM.Config 0).Gateway }}'`.
2. Enter it as `web.trusted_proxies` in `vandoxd.yaml` and restart: `sudo docker compose restart`.
3. Open `https://<your host name>/` and sign in with the password of step 3.
4. Check that the port is not reachable from other hosts in the LAN (*Check the Docker Engine* in REVERSE_PROXY.md). That is your
   job as the operator: Vandox does not rely on the binding, but a port open to the LAN weakens the lockout of failed sign-ins.

## 6. Collect the logs on the monitored server

On the Plesk server, pack the logs into one archive. `tar` keeps the modification times, which the import needs to find the year of
lines that carry none:

```
sudo journalctl -o export > /tmp/journal.export
sudo tar -czf ~/vandox-logs.tar.gz --exclude=var/log/journal -C / var/log -C /tmp journal.export
sudo rm /tmp/journal.export
sudo chown "$USER" ~/vandox-logs.tar.gz
```

- The journal export can be large; limit it with `--since "2026-01-01"` if needed.
- Add the log of the earlier monitoring script (the `top`/`lsof` snapshots) to the archive if the server has one: append its
  path to the `tar` command, for example `-C / path/to/monitor.log`.
- An import reads at most 20,000 entries per run. If `/var/log` holds more, split it into several archives.
- The archive holds personal data (mail addresses, client addresses). Delete it on the server once it is copied.

Copy it into the `import/` directory of step 1 and give it to the container user:

```
scp server:vandox-logs.tar.gz /volume1/docker/vandox/import/
sudo chown 65532:65532 /volume1/docker/vandox/import/vandox-logs.tar.gz
sudo chmod 0400 /volume1/docker/vandox/import/vandox-logs.tar.gz
```

The archive is read as it is; nothing is extracted, so only the archive file itself must be readable by `65532`.

## 7. Import

```
sudo docker exec -it vandoxd dotnet /app/vandoxd.dll import /import/vandox-logs.tar.gz
```

The import prints its progress as JSON lines and ends with a summary of the files imported, not recognized and failed, the lines read
and the time range stored. A large `/var/log` takes a while on a NAS; Ctrl-C stops it, and running the same command again continues
where it stopped. A file that failed with `import.time_zone is not set` is completed by setting the option (step 2) and importing again;
the import reads `vandoxd.yaml` itself, so the container needs no restart for it. Files no parser claims are listed with the reason; that is expected for logs Vandox does not read.

## 8. Reconstruct the incidents

```
sudo docker exec -it vandoxd dotnet /app/vandoxd.dll incidents
```

It prints one line per incident, such as `#1 created 2026-10-02T04:59:00Z kills=1 victim="mariadbd"`, and the counts. Run it again
after every later import. If it reports `Incidents: 0`, the imported logs hold no OOM kill: check that `kern.log`, `syslog` or the
journal export of the outage period were in the archive.

## 9. Open the first incident

In the web UI, *Incidents* lists the outages newest first. An incident page shows the window around the kills, the timeline, the
cause hypothesis, the memory, swap and pressure charts where the data has them, and a field for your own notes. *Logs* searches every
imported line and leads from an incident's time to the lines around it. *Report* shows the forensics report over all incidents and
offers it as Markdown; the same report comes from the command line:

```
sudo docker exec vandoxd dotnet /app/vandoxd.dll report > forensics.md
```

## Next steps

- **Upgrade:** change the image tag in `docker-compose.yml` and run `sudo docker compose up -d`. The database is migrated on start;
  an older version refuses a newer database, so back up the volume first.
- **Back up:** stop the container and copy the volume `vandox_vandox-data` (`vandox.db` with its `-wal` and `-shm` files).
- **Newer logs:** copy a newer archive of the same logs and import it: a log that grew is continued after the part imported
  before, and a rotated copy that was imported already is skipped. A newer journal export is imported as a whole, so export only
  the new period (`journalctl -o export --since ...`).
- **Security:** read [SECURITY.md](../SECURITY.md), *Deployment Security Considerations*.
