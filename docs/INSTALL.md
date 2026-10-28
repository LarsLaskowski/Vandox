# Installation: from zero to the first incident

This guide installs the Vandox backend `vandoxd` on a Docker host in the home network, imports the logs of the monitored server and
opens the first reconstructed outage in the web UI. It is the path for v0.1.0, the forensics release: Vandox works from the logs the
server already has, and **no agent is needed** for it (from v0.2.0 on, step 11 installs the agent). The [README](../README.md) is the reference
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
- **Tailscale** on the Docker host and, from v0.2.0, on the monitored server, both logged in to one tailnet: the agent sends to the
  backend only over it (steps 10 and 11).
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
mkdir -p secrets import data
```

In `docker-compose.yml`, replace `networlddev/vandox:latest` with `networlddev/vandox:0.1.0`, so an upgrade happens only when you
change the tag. To check the image's origin before the first start, verify its attestation as the README shows (*Install*).

**Choose the user the container runs as.** The image runs as `65532:65532`, a user that exists on no host. That suits a Docker
volume nobody looks into, but on a NAS the files in the project directory would belong to an unknown user. Run the container as a
user of the host instead, so the files stay yours:

- Find the IDs of the user that should own the files: `id <user>` prints `uid=1026(<user>) gid=100(users) …` on a Synology NAS, for
  example. A dedicated user without login rights is the cleanest choice.
- Set them once for the commands below: `VANDOX_USER=1026:100` (or `VANDOX_USER=65532:65532` to keep the image's user and the
  named volume).
- With a host user, the database goes into `data/` of the project directory: `sudo chown -R "$VANDOX_USER" data && sudo chmod 0700 data`.
  In `docker-compose.yml`, set `user: "1026:100"` (with your IDs) and replace `vandox-data:/data` with `./data:/data`; both lines
  are prepared there as comments.

## 2. Configure

Edit `vandoxd.yaml`: uncomment `time_zone` in the existing `import:` section and set `trusted_proxies` in the existing `web:` section
(a second `import:` or `web:` block is a duplicate key and stops the start). Two settings matter for this release:

```yaml
import:
  time_zone: Europe/Berlin     # the monitored server's time zone

web:
  trusted_proxies: "172.18.0.1"  # the reverse proxy's address as the container sees it (step 5)
```

Without `import.time_zone` a traditional-format syslog file, a MariaDB, Dovecot, Plesk or web server error log and the legacy
monitoring log fail at their first line without a zone (journal exports and RFC 3339 syslog files carry their offset); it can be set later and the import run again. If the Plesk server writes its panel or web server error logs to
other paths than the defaults, set the `import.*_log` paths too (README, *Configuration*). Every other key may stay at its default.

## 3. Create the secrets

Compose passes file secrets with the host file's owner and mode, so each secret file belongs to the container's user
(`$VANDOX_USER` from step 1) and is readable by nobody else.

**Web UI password hash.** The password has 12 to 1024 characters. Read it without echo and never give the container a terminal
(`-i`, not `-t`), because a terminal shows what is typed:

```
read -rs -p 'Web UI password: ' pw && echo
printf '%s\n' "$pw" | sudo docker run --rm -i networlddev/vandox:0.1.0 hash-password > secrets/vandox_web_password_hash
unset pw
```

**Agent token.** v0.1.0 does not use it, but the compose file passes it, so the file must exist; from v0.2.0 on it is the token the
agent presents (step 10):

```
openssl rand -hex 32 > secrets/vandox_agent_token
```

Then hand both files to the container user:

```
sudo chown "$VANDOX_USER" secrets/vandox_web_password_hash secrets/vandox_agent_token
sudo chmod 0400 secrets/vandox_web_password_hash secrets/vandox_agent_token
```

## 4. Start the backend

From v0.2.0 on, the compose file publishes the ingest port on the address in `INGEST_BIND_ADDRESS` and refuses to start without it
(`set INGEST_BIND_ADDRESS to this host's tailnet address (tailscale ip -4), or 127.0.0.1 without agents`). Create `.env` in the
project directory first: `INGEST_BIND_ADDRESS=127.0.0.1` while no agent sends, the tailnet address once one does (step 10). The
v0.1.0 compose file publishes no ingest port and ignores the variable.

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

**Without Compose.** A single container can also be started with `docker run` (or set up the same way in a container manager's
wizard). This is the same container as the compose file, with a host user and `data/` from step 1; replace the paths and IDs with
yours:

```
cd /volume1/docker/vandox
sudo docker run -d --name vandoxd --restart unless-stopped \
  --user "$VANDOX_USER" --memory 512m --read-only --tmpfs /tmp \
  --cap-drop ALL --security-opt no-new-privileges:true --stop-timeout 30 \
  --log-driver json-file --log-opt max-size=10m --log-opt max-file=3 \
  -p 127.0.0.1:8080:8080 \
  -e VANDOX_AGENT_TOKEN_FILE=/run/secrets/vandox_agent_token \
  -e VANDOX_WEB_PASSWORD_HASH_FILE=/run/secrets/vandox_web_password_hash \
  -v "$PWD/data:/data" \
  -v "$PWD/import:/import:ro" \
  -v "$PWD/vandoxd.yaml:/etc/vandox/vandoxd.yaml:ro" \
  -v "$PWD/secrets/vandox_agent_token:/run/secrets/vandox_agent_token:ro" \
  -v "$PWD/secrets/vandox_web_password_hash:/run/secrets/vandox_web_password_hash:ro" \
  networlddev/vandox:0.1.0
```

In a container manager's wizard the same settings are: the image `networlddev/vandox:0.1.0`; the port `127.0.0.1:8080` → `8080`
(or the host's LAN address, see step 5); the five folder and file mappings above, `import`, `vandoxd.yaml` and the secrets
read-only; the two environment variables; automatic restart; a memory limit of 512 MB. Where the wizard offers them, also set the
user, a read-only root file system, `/tmp` as tmpfs and no added capabilities; the container runs without them as well, only less
confined. The image sets `ASPNETCORE_URLS` to an empty value on purpose: leave it empty, because the ports come from `web.listen`
in `vandoxd.yaml`, not from that variable.

If the container does not become healthy, `sudo docker logs vandoxd` names the reason, for example a missing password hash, an
invalid option in `vandoxd.yaml`, or a data directory the container's user cannot write.

## 5. Put the web UI behind the reverse proxy

The web port is published on `127.0.0.1:8080`, for a proxy on the same host. Set up the proxy as
[REVERSE_PROXY.md](REVERSE_PROXY.md) shows, with the sections for Synology and QNAP, then:

1. Find the address the proxy has as seen by the container (often the gateway of the container's network):
   `sudo docker network inspect vandox_default --format '{{ (index .IPAM.Config 0).Gateway }}'` with Compose, or the same with
   `bridge` instead of `vandox_default` for a container started with `docker run`.
2. Enter it as `web.trusted_proxies` in `vandoxd.yaml` and restart: `sudo docker compose restart` (or `sudo docker restart vandoxd`).
3. Open `https://<your host name>/` and sign in with the password of step 3.
4. Check that the port is not reachable from other hosts in the LAN (*Check the Docker Engine* in REVERSE_PROXY.md). That is your
   job as the operator: Vandox does not rely on the binding, but a port open to the LAN weakens the lockout of failed sign-ins.

## 6. Collect the logs on the monitored server

On the Plesk server, pack the logs into one archive. `umask 077` keeps the export and the archive readable for you alone. `tar` keeps the modification times, which the import needs to find the year of
lines that carry none:

```
umask 077
sudo journalctl -o export > ~/journal.export
sudo tar -czf ~/vandox-logs.tar.gz --exclude=var/log/journal -C / var/log -C ~ journal.export
rm ~/journal.export
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
sudo chown "$VANDOX_USER" /volume1/docker/vandox/import/vandox-logs.tar.gz
sudo chmod 0400 /volume1/docker/vandox/import/vandox-logs.tar.gz
```

The archive is read as it is; nothing is extracted, so only the archive file itself must be readable by the container's user.

## 7. Import

```
sudo docker exec -it vandoxd dotnet /app/vandoxd.dll import /import/vandox-logs.tar.gz
```

The import prints its progress as JSON lines and ends with a summary of the files imported, not recognized and failed, the lines read
and the time range stored. A large `/var/log` takes a while on a NAS; Ctrl-C stops it, and running the same command again continues
where it stopped. A file that failed with `import.time_zone is not set` is completed by setting the option (step 2),
restarting the container (`sudo docker compose restart` or
`sudo docker restart vandoxd`) and importing again: the container keeps seeing the file it was started
with when an editor saves the change as a new file. Files no parser claims are listed with the reason; that is expected for logs Vandox does not read.

## 8. Reconstruct the incidents

```
sudo docker exec -it vandoxd dotnet /app/vandoxd.dll incidents
```

It prints one line per incident, such as `#1 created 2026-10-02T04:59:00Z kills=1 victim="mariadbd"`, and the counts. Run it again
after every later import. If it reports `Incidents: 0`, the imported logs hold no OOM kill: check that `kern.log`, `syslog` or the
journal export of the outage period were in the archive.

## 9. Open the first incident

In the web UI, *Incidents* lists the outages newest first. An incident page shows the window around the kills, the timeline, the
cause hypothesis, the extremes of memory, swap, pressure and load, charts of the memory per program, the available memory and swap
and the connections where the data has them, and a field for your own notes. *Logs* searches every
imported line and leads from an incident's time to the lines around it. *Report* shows the forensics report over all incidents and
offers it as Markdown; the same report comes from the command line:

```
sudo docker exec vandoxd dotnet /app/vandoxd.dll report > forensics.md
```

## Next steps

- **Upgrade:** change the image tag in `docker-compose.yml` and run `sudo docker compose up -d` (without Compose:
  `sudo docker stop vandoxd && sudo docker rm vandoxd`, then the `docker run` command of step 4 with the new tag). The database is
  migrated on start; an older version refuses a newer database, so back up the data first.
- **Back up:** stop the container and copy the volume `vandox_vandox-data`, or the directory `data/` when the container runs as
  a host user (`vandox.db` with its `-wal` and `-shm` files).
- **Newer logs:** copy a newer archive of the same logs and import it: a log that grew is continued after the part imported
  before, and a rotated copy that was imported already is skipped. A newer journal export is imported as a whole, so export only
  the new period (`journalctl -o export --since ...`).
- **Security:** read [SECURITY.md](../SECURITY.md), *Deployment Security Considerations*.

## 10. Prepare the ingest port for the agent (v0.2.0)

From v0.2.0 on, the agent on the monitored server sends its data to the backend over Tailscale, to
`http://<tailnet address of the Docker host>:8081`. The backend side needs three things.

> The Synology steps and the userspace-mode behavior in this section come from Tailscale's documentation and the project's notes and
> have **not been verified on a device**; they are to be checked on a Synology host before the release (#45). The source of the
> Synology steps is Tailscale's Synology guide, <https://tailscale.com/kb/1131/synology>; follow it where it differs from the
> commands below.

**Tailscale in TUN mode on the Docker host.** Docker can publish a port only on an address of a host interface. In Tailscale's TUN
mode the tailnet address is on the interface `tailscale0`; in userspace-networking mode it is on no interface, so the binding fails,
and Tailscale forwards inbound tailnet connections to `127.0.0.1` instead, where the web port is published, which would put the web
UI on the tailnet behind nothing but the ACL. Userspace mode is therefore not supported. Check the mode with
`ip -4 addr show tailscale0`: in TUN mode it lists the address that `tailscale ip -4` prints.

**Synology:** the Tailscale package runs in userspace mode by default. Over SSH, switch it to TUN mode and restart the package:

```
sudo tailscale configure-host
sudo synosystemctl restart pkgctl-Tailscale.service
```

The setting does not survive a reboot, so repeat it at every boot with a triggered task: *Control Panel → Task Scheduler → Create → Triggered Task →
User-defined script*, user `root`, event *Boot-up*, script
`/var/packages/Tailscale/target/bin/tailscale configure-host; synosystemctl restart pkgctl-Tailscale.service`.

**The bind address and the agent ID.** Set `INGEST_BIND_ADDRESS` in `.env` to the address `tailscale ip -4` prints (for example
`INGEST_BIND_ADDRESS=100.101.102.103`, beside a `WEB_BIND_ADDRESS` line if you have one), and the agent's `agent_id` in
`vandoxd.yaml` (uncomment `agent_id: web-1` in the existing `ingest:` section and change the name; a second `ingest:` block stops the
start). Then recreate the container:

```
sudo docker compose up -d
```

Without `docker compose`, add `-p <tailnet address>:8081:8081` to the `docker run` command of step 4. The token of step 3 is the
one the agent presents; `ingest.min_free` (default `1GiB`) is the free space below which the backend stops writing agent data.

**Symptoms:**

- `docker compose up` stops and names `INGEST_BIND_ADDRESS`: `.env` lacks the variable.
- The container does not start and Docker reports a port binding error: the host does not have the address in `.env` (Tailscale
  down, or in userspace mode). Switch to TUN mode or correct the address, then `sudo docker compose up -d`.
- After a reboot the container is not running although Tailscale is up: Docker may have started before Tailscale brought up its
  address, and the restart policy may not retry a failed binding (expected from Docker's behavior, not verified). Run
  `sudo docker compose up -d` once Tailscale is up; if it happens at every boot, add that command to the end of the triggered task.
- The backend logs `ingest disabled` at start: the token or `ingest.agent_id` is missing; ingest answers `503` until both are set.
- The agent logs `token refused`: look in `sudo docker logs vandoxd` for `ingest agent refused` (the agent's `agent_id` differs from
  `ingest.agent_id`, compared exactly) or `ingest token refused` (the agent has another token).
- `ingest paused: free space low`: the data volume has less than `ingest.min_free` free; the agent keeps its data until space is freed.

**What the binding does not do.** Docker matches a published port by its destination address only, so a host in the LAN that routes
packets to the tailnet address may reach the ingest port, as it may the web port. The agent token is the boundary: the backend checks
it before it reads a request, accepts only the agent named in `ingest.agent_id` and bounds what one request may cost. Keep the
Tailscale ACL strict, so the monitored server reaches only this port ([SECURITY.md](../SECURITY.md)).

## 11. Install the agent on the monitored server

From v0.2.0 on, the agent collects on the monitored server and sends to the backend of step 10. The release publishes an install bundle,
`vandox-agent-install-vX.Y.Z.tar.gz`, with the scripts, the unit, the example configuration and a manifest, and a `SHA256SUMS` that lists the bundle and the binary.
First put the monitored server into the backend host's tailnet: install Tailscale there, log in, limit the server to the ingest port
in the tailnet policy, and check the connection, as
[`deploy/agent/README.md`](../deploy/agent/README.md#tailscale-on-the-monitored-server) (*Tailscale on the monitored server*) describes.

The server needs `bash`, `curl`, `sha256sum`, `tar` and `gzip`, not `git`. Run these lines **as root on the monitored server**, one at a time, with `vX.Y.Z`
the release you install:

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

The variables live only in this shell; in a new shell, start again at step 1. The script first checks every file of the bundle against
`MANIFEST.sha256` and stops with `the bundle is incomplete or changed; extract it again in a new directory` if one is missing or different, for example after
an interrupted unpacking.

The `--agent-id` must be the `agent_id` of step 10, and the backend URL a tailnet address or MagicDNS name: the unit lets the agent reach only localhost and
the tailnet. The script reads the version from the bundle's `VERSION` file, asks for the agent token of step 3 (typed without echo, or one line of standard input;
never an option), downloads the binary and checks it against the checksum in the bundle before it runs it, creates the user `vandox-agent`, `/etc/vandox`, the
spool directory, the unit and the MariaDB account (`plesk db` on Plesk, else `mariadb`), starts the agent and prints a line per server check.

Remove the agent with the bundle's `uninstall.sh` (`--purge` also deletes the configuration, token and spool). Upgrade and an install without internet access on the server are in [`deploy/agent/README.md`](../deploy/agent/README.md), which also lists every option,
message, check and unit setting. The unit and the scripts have not yet run on the server (the Product Manager's server run is to be done).
