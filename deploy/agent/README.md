# Installing the agent

This folder holds what installs the Vandox agent on the monitored server: the install scripts, the systemd unit and the example configuration. The
steps to install, upgrade and remove the agent come first; the reference after them explains the options, the checks and what the script does; the rights
the agent holds, each with the collector that needs it, follow under *Rights per collector*.

## Tailscale on the monitored server

The agent sends its data only over Tailscale: its unit lets it reach nothing but localhost and the tailnet ranges `100.64.0.0/10` and
`fd7a:115c:a1e0::/48`. Do these steps once, before you install the agent. They follow Tailscale's documentation and a run on a real
tailnet; where Tailscale's pages differ from this summary, follow Tailscale. The steps and checks added for issue #43 (the settings line
and its check, the extended checks, the reboot check and the key expiry) have **not yet run** on the production server; each says so, and
the maintainer's run is tracked in issue #338.

**What stays open while Tailscale is not up.** Tailscale's routes exist only while it runs. While it is not up on this server (before it has
its network map after a boot, stopped, logged out, or the node removed), a send to the backend's tailnet address follows the server's
default route. The unit's `IPAddressAllow=` filters by address, not by interface, so it does not stop this. If nothing answers, the send
fails as `connection failed` or a timeout, and the records stay in the spool until the tailnet is up. If someone answers at that address
on the hosting provider's path, it gets the agent's token and the batch in clear; the agent treats a 2xx answer as delivered and drops
those records, and a refusal answer (`400`, `413` or `422` with the refusal header) makes it give them up. This exposure predates the
policy file below and is not closed here; issue #337 adds a control.

1. **Install Tailscale** with Tailscale's guide for Linux, which covers Ubuntu 22.04: <https://tailscale.com/kb/1031/install-linux>.
   Use the package repository method that the guide describes. Check what a script does before you run it, as you would with any
   download.
2. **Prepare the tailnet policy, before this server joins.** Both machines carry a tag: this server `tag:vandox-agent`, the backend host
   `tag:vandox-backend`. The policy must allow this server only the ingest port on the backend
   host ([SECURITY.md](../../SECURITY.md)). That is possible only when the server joins with a tag: a device without a tag acts as
   the user who logged it in, so every rule for that user would apply to the server too. Rules only add permissions, and `*`
   includes tagged devices, so **the tailnet's default rule must be replaced first**: in the policy file it is a grant with `*` as
   source, destination and IP (in the older form an `accept` rule with `*` as source and `*:*` as destination). Tag the
   backend host as Tailscale's guide to tags describes (<https://tailscale.com/kb/1068/acl-tags>).

   Then replace the whole policy (<https://tailscale.com/kb/1018/acls>) with the example file
   [`tailnet-policy.hujson`](tailnet-policy.hujson) of this folder. It is plain HuJSON, so the admin console accepts it as the policy
   file and GitOps can use it as it is; it is not part of the install bundle, so take it from the repository at the release's tag.
   Whether the console accepts the file has **not yet run**; the maintainer saves it in the tailnet (issue #338). The file holds:

   - `tagOwners`: both tags are owned by `autogroup:admin`. No tag may own a tag.
   - The agent's only rule: TCP to the ingest port of the backend host. Write it exactly as `tcp:<port>`. A port without a protocol
     (`"8081"`) allows every protocol on that port, and `tcp:*` allows every TCP port. The port appears twice in the file, in this rule
     and in the `tests` entries; if your backend uses another port, change both.
   - A rule for your own devices (`autogroup:member` to `autogroup:self`), which keeps them reaching each other. A tagged host is no
     longer part of its user's `autogroup:self`.
   - Two `tests` entries: the agent reaches the ingest port and not ports 22, 8080 and 443 of the backend host, and not the ingest port
     over UDP. They are meant to make the console refuse a later edit that lets the server reach more. Whether the console checks them
     against the rules on save has **not yet run** (issue #338).
   - One commented line, off by default: SSH from the tailnet admins to the backend host.

   What the file changes against the default rule:

   - The rule that gave your devices every port of the backend host over the tailnet, which earlier versions of this README had, is gone.
     Your devices no longer reach
     the backend host's web UI, SSH or file services at its tailnet address. In the home LAN nothing changes: the web UI is reachable
     there only, with its login (record 0016). To open SSH over the tailnet on purpose, remove the comment marks from the one
     commented line, which opens `tcp:22` of the backend host and nothing else. There is no line for the web port.
   - Whoever replaces the default rule must also think of exit nodes, DNS servers, subnet routers and other tagged devices that
     it had covered, and of your own devices that need more than each other: each needs its own rule, written with a source that cannot
     match this server. A grant to a subnet route of the home LAN gives those devices the web UI at its LAN address, as if they were in
     the home LAN.
   - Optional, and not tested in a run: if the backend host itself opens connections to your devices (for example backups), it needs a
     rule with `tag:vandox-backend` as source and `autogroup:member` as destination. The file has no line for it.

   Rules that must never be added:

   - No other rule may have a source that matches this server: not `*`, not `autogroup:tagged`, not `tag:vandox-agent` with another
     destination, and not an address, a CIDR, a host alias or an ipset that covers it.
   - No `autoApprovers` entry, no `ssh` rule and no grant to a subnet or to `autogroup:internet` with such a source. Then a compromised
     root on this server that turns on Tailscale SSH, advertises a route or an exit node, accepts routes or selects an exit node still
     reaches only the ingest port. The settings line of step 3 keeps these off anyway.
   - No tag as a tag owner.
   - No grant of your devices (or anyone) with `autogroup:tagged` or `autogroup:danger-all` as destination: both include the backend host.
   - The agent itself never listens on a port, so it needs no rule with this server as destination. If you also manage the server over
     Tailscale (SSH or anything else over the tailnet, not over the normal network), that needs an extra rule with your devices as
     source and `tag:vandox-agent` as destination; no rule but the agent's one may have `tag:vandox-agent` as source.

   Tailscale now recommends the `grants` form, which the file uses. Our understanding is that the older `acls` form still works, with
   `{"action": "accept", "src": ["tag:vandox-agent"], "dst": ["tag:vandox-backend:8081"]}` as the agent's rule, but this project
   has not tested it.
3. **Join the tailnet with the tag.** Run as root:

   ```
   tailscale up --advertise-tags=tag:vandox-agent
   ```

   It prints a URL; open it in a browser and sign in, as an admin of the tailnet, to the tailnet of the backend host. The request
   fails if the policy of step 2 does not name the tag. If it also prints that some peers are advertising routes but
   `--accept-routes` is false, leave it so: do not add `--accept-routes`. The unit lets the agent reach only localhost and the
   tailnet ranges, so routes into other networks would only carry traffic the unit blocks. The agent needs neither Tailscale SSH
   (`--ssh`) nor any other option.

   **Turn off what the agent does not need, and check it.** One line sets every option that could give this server more reach than the
   ingest port: Tailscale SSH, accepted and advertised routes, an exit node (offered or selected), the app connector, the web client and
   Tailscale's DNS. Run as root:

   ```
   tailscale set --ssh=false --accept-routes=false --advertise-routes= --advertise-exit-node=false --exit-node= --advertise-connector=false --webclient=false --accept-dns=false
   ```

   If it fails with an unknown flag, the installed Tailscale is too old: upgrade it from the package repository (step 1) and run the
   line again. Do not drop the flag, because an old client applies nothing when it rejects one. Then show the settings:

   ```
   tailscale debug prefs
   ```

   Expect `RunSSH` false, `RouteAll` false, `AdvertiseRoutes` empty or null, `ExitNodeID` empty, `CorpDNS` false, `RunWebClient` false,
   `AppConnector` with `Advertise` false, and `AdvertiseTags` holding `tag:vandox-agent` only. The exact output format of this command
   has not been checked in this project. The settings line and this check have **not yet run** on the production server (issue #338).

   **Tailscale's DNS is off after that line.** On Linux, Tailscale takes over the server's name resolution by default and resolves names through the tailnet's DNS settings. With the narrow policy of step 2, lookups can then fail: the install lines cannot reach `github.com` (`curl: (6) Could not resolve host`), and nothing else on the server can resolve a name either. The line above keeps the server's own name server; check it:

   ```
   tailscale dns status
   getent hosts github.com
   ```

   The first line must no longer show Tailscale's DNS as enabled. The second must print an address. MagicDNS names then no longer
   resolve on this server, so give the agent the backend's tailnet address (`http://100.x.y.z:8081`), never a MagicDNS name.
   In a run on a real tailnet, `getent hosts github.com` printed nothing while Tailscale's DNS was on, and the server's own name
   server answered once it was off. The other way, a grant that lets this server reach the tailnet's name servers on port 53,
   opens more than the ingest port and has not been tested in this project.

   **Check that the server carries the tag.** In the admin console's list of machines, the server shows `tag:vandox-agent` and no
   user. Or run this on the backend host, with this server's tailnet address in place of `100.64.0.2`:

   ```
   tailscale whois 100.64.0.2
   ```

   It should name `tag:vandox-agent`, not a user (the exact wording of the output has not been checked in this project). A server
   without the tag acts as the user who logged it in: remove it from the tailnet and join again with the tag.

   **Key expiry.** Tailscale documents that tagged devices have key expiry turned off by default (<https://tailscale.com/kb/1068/acl-tags>),
   because the node belongs to a tag and not to a person's login, so the server stays in the tailnet. To check, look at the server's
   entry in the admin console: it should show no expiry (the console's wording has not been checked). That check has **not yet run**
   (issue #338). To replace the node's key on purpose, remove the machine in the admin console and run the join line above again with
   the tag. The server then has a new tailnet address and identity, so repeat the `tailscale whois` check with the new address; the
   agent's `backend.url` is the backend's address and stays valid. `tailscale up --force-reauth --advertise-tags=tag:vandox-agent`
   is an alternative that has not been tested.
4. **Check the connection and the policy.** On this server, with the backend host's tailnet address in place of `100.64.0.1`, its IPv6
   tailnet address in place of `fd7a:115c:a1e0::1`, another tailnet device's address in place of `100.64.0.9`, and the backend's ingest
   port in place of `8081`. Each line stands alone and changes nothing. Only the fifth line, SSH on the backend host's IPv4 tailnet
   address, has a result from the production server (below); the results of the other lines, the two `curl` lines and the lines added
   for issue #43 among them, have **not yet run** there (issue #338):

   ```
   tailscale ip -4
   tailscale ping 100.64.0.1
   curl -s -o /dev/null -w '%{http_code}\n' http://100.64.0.1:8081/
   curl -s -o /dev/null -w '%{http_code}\n' -X POST http://100.64.0.1:8081/v1/batches
   timeout 5 bash -c '</dev/tcp/100.64.0.1/22'; echo $?
   timeout 5 bash -c '</dev/tcp/100.64.0.1/8080'; echo $?
   timeout 5 bash -c '</dev/tcp/fd7a:115c:a1e0::1/8080'; echo $?
   timeout 5 bash -c '</dev/tcp/fd7a:115c:a1e0::1/22'; echo $?
   timeout 5 bash -c '</dev/tcp/100.64.0.9/22'; echo $?
   tailscale status
   tailscale exit-node list
   ```

   - The first line prints this server's tailnet address (a `100.x.y.z` address).
   - The second must get a pong from the backend host. It checks the tailnet path, not the policy.
   - The third must print `404`. Only the ingest port answers like that: it answers every request except the agent's own batches
     with `404`, so a `404` shows that the policy lets this server reach the ingest port. This `404` proves only that the ingest port is reachable, not that the backend has the ingest API: that is the fourth line. The other answers:
     - `302` (a redirect to `/login`) means the web listener answered, not the ingest port. The address or port is wrong.
     - `000` means no connection: check the rule of step 2, and that the backend host carries `tag:vandox-backend` (without it
       the rule matches nothing).
     - In both cases, check on the backend host that `.env` sets `INGEST_BIND_ADDRESS` to its tailnet address, and run
       `docker port vandoxd 8081`. The ingest port is container port 8081 and the web port 8080 (step 10 of
       [`docs/INSTALL.md`](../../docs/INSTALL.md)).
   - The fourth must print `401`: the ingest endpoint refuses a batch without a token, so the backend has the ingest API and ingest
     is enabled. The backend logs an `ingest token refused` warning for it (at most one a minute).
     - `503` means ingest is disabled: set `ingest.agent_id` and the token on the backend.
     - `404` means the backend has no ingest API (v0.1.0 or older), a proxy is in front of the port, or the port is the wrong one.
   - The next five lines try other targets: SSH (22) and the web port (8080) of the backend host on its IPv4 tailnet address, the web
     port and SSH on its IPv6 tailnet address, and SSH on another tailnet device. They mean something only after the third line printed
     `404` and the fourth `401`. Each must be blocked (Tailscale's access control page: <https://tailscale.com/kb/1018/acls>).
     - Blocked by the policy, as it should be: `No route to host`, or `124` (a timeout). In the Product Manager's server run the blocked attempt printed `No route to host` in one policy state and timed out (`124`) in
       another, and the allowed one, before the default rule was replaced, printed `Connection refused`.
     - The policy lets this server reach that port or device: `Connection refused` with `1`, or `0` (connected). The policy is too
       wide, usually because the default rule is still there, a second rule matches the server, or the server has no tag; correct
       step 2 or 3.
   - `tailscale status` must list the backend host as its only peer, because Tailscale shows a server only the peers the policy
     relates to it (Tailscale's access control page: <https://tailscale.com/kb/1018/acls>). Another peer means a rule has this server
     as source or as destination: a rule that gives the server more than the ingest port is too wide and must go; one with your devices
     as source and `tag:vandox-agent` as destination, added on purpose to manage the server (step 2), explains that peer.
   - `tailscale exit-node list` must show no exit node this server may use (Tailscale's exit node page:
     <https://tailscale.com/kb/1103/exit-nodes>). One in the list means a rule or an exit node's `autogroup:internet` grant matches this
     server; remove that match.
   - Optional, and an extra check of the policy: a deliberate `--accept-routes=true` with a probe of a home-LAN address behind a subnet
     router shows that the policy refuses the route (Tailscale's subnet router page: <https://tailscale.com/kb/1019/subnets>). **Caveat:**
     a route that overlaps the hosting provider's network would redirect the server's own traffic while the setting is on, so the
     probe runs detached, as root, and reverts the setting itself. Replace `192.168.1.10` by a home-LAN address behind a subnet router:

     ```
     setsid -f bash -c "tailscale set --accept-routes=true; sleep 10; timeout 5 bash -c '</dev/tcp/192.168.1.10/22'; echo \$?; tailscale set --accept-routes=false" > /root/vandox-route-probe.txt 2>&1
     ```

     After about 20 seconds, `cat /root/vandox-route-probe.txt` must show `No route to host` or `124`, not `0` or `1`. The detached start
     makes it likely, not certain, that the revert runs even if your SSH session closes. Run the settings check right after it:

     ```
     tailscale debug prefs
     ```

     `RouteAll` must be false again. If it is true, run `tailscale set --accept-routes=false` yourself.

   Use the backend's tailnet address, not its MagicDNS name: with Tailscale's DNS turned off (step 3), MagicDNS names do not
   resolve on this server.
5. **Check the reboot, after the agent is installed.** Tailscale should come up by itself, without a login, with its tag and its
   settings. The check has **not yet run** on the production server (issue #338). Run as root:

   ```
   sudo reboot
   ```

   When the server answers again, run:

   ```
   tailscale status
   tailscale debug prefs
   curl -s -o /dev/null -w '%{http_code}\n' http://100.64.0.1:8081/
   curl -s -o /dev/null -w '%{http_code}\n' -X POST http://100.64.0.1:8081/v1/batches
   systemctl is-active vandox-agent
   journalctl -u vandox-agent --since '10 min ago' --no-pager
   ```

   - `tailscale status` shows the server logged in and the backend host as its peer; `tailscale debug prefs` shows the values of step 3
     unchanged; the two `curl` lines print `404` and `401`.
   - `systemctl is-active vandox-agent` prints `active`. The journal shows the agent sending again, with the records of the window from
     its spool, and no further `connection failed` after the tailnet is up. Until the tailnet is up, the residual at the top of this
     section applies in that window: if nothing answers, sends fail and the records stay spooled; an answering party on the provider's
     path gets the token and the batch and, with a `2xx` or a refusal answer, makes the agent drop or give up those records.
   - If `tailscale status` says Tailscale is stopped or logged out, check `systemctl status tailscaled` and run
     `systemctl enable --now tailscaled`; if it asks for a login, join again with step 3. The agent keeps spooling meanwhile.

## Install the agent

**What you need**

- The monitored server: Ubuntu 22.04 with systemd on `x86_64`, `root` access (`sudo`), and `curl`, `sha256sum`, `tar` and `gzip` (no `git`).
- Tailscale on this server, logged in to the same tailnet as the backend host (see *Tailscale on the monitored server* below).
- The backend, already running with its ingest port on its tailnet address (step 10 of [`docs/INSTALL.md`](../../docs/INSTALL.md)): you
  need that address and port, for example `http://100.64.0.1:8081`. Use the address, not a host name: the agent can reach nothing but localhost and the tailnet, and with Tailscale's DNS turned off on
this server (*Tailscale on the monitored server*, step 3) MagicDNS names do not resolve.
- A backend with the ingest API: `vandoxd` v0.2.0 or later. v0.1.0 has no ingest endpoint and answers `404`; until v0.2.0 is
  released, use an image built from `main` since PR #316. Ingest must also be enabled: `ingest.agent_id` and the agent token set
  on the backend (`deploy/backend/vandoxd.yaml`, step 10 of [`docs/INSTALL.md`](../../docs/INSTALL.md)). Until both are set, it
  answers every batch with `503`. An agent sending to a backend without the ingest API logs
  `backend not accepting batches reason=status status=404` and keeps its records in the spool.
- The agent token. You create it on the backend host (step 3 of [`docs/INSTALL.md`](../../docs/INSTALL.md): `openssl rand -hex 32 > secrets/vandox_agent_token`)
  and the backend accepts it only for the `agent_id` set under `ingest` (step 10). Have the same token value ready; the installer asks for it and never takes it as an option.
- The release version, for example `v0.2.0`: find it on <https://github.com/LarsLaskowski/Vandox/releases> (use the same version as the backend where you can).

**Step 1 - choose the version and the names.** Replace `vX.Y.Z` in the first block with the release version, `web-1` with the name of this server (the
`agent_id`, the one the backend expects) and `http://100.64.0.1:8081` with the backend's tailnet address and ingest port.

**Step 2 - run the lines as root, one at a time.** They download the install bundle and its checksum list over HTTPS, check the bundle, unpack it only if the
check passes, and start the installer. The installer asks for the token (it shows nothing while you type; see *The token prompt* below), downloads the binary and checks it before it runs it, then creates the user, the files, the unit and the MariaDB account, and starts the agent:

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

The variables live only in this shell; in a new shell, start again at step 1. The script first checks the other files of the bundle against `MANIFEST.sha256` and stops with `the bundle is incomplete or changed; extract it again in a new directory` if one is different or missing, for example after an interrupted unpacking; a missing `common.sh` or `MANIFEST.sha256` gets the same message.

**The binary comes from the release.** The bundle does not contain the agent binary, only its SHA-256. The install script downloads
`vandox-agent-linux-amd64` from the GitHub release of the bundle's version and installs it only if it matches. So the server needs
HTTPS access to `github.com`, with a working name server (*Tailscale on the monitored server*, step 3). A bundle without a GitHub
release, such as a test build, cannot be installed this way; its download ends with `install.sh: download failed: vandox-agent-linux-amd64`.
Use the offline variant below, with the binary beside the bundle and `--from .`.

**The token prompt.** The script asks `Agent token:` and then shows nothing while you type or paste: no characters, no asterisks.
That is deliberate. Paste or type the token and press Enter. The token has 32 to 4096 characters of `0x21` to `0x7E` (a token from
`openssl rand -hex 32` has 64); otherwise the script stops with `install.sh: token rejected: <rule>` and changes nothing. If your
console cannot paste into a hidden prompt, give the token on standard input instead:
1. Create a file only root can read with `install -m 0600 /dev/null /root/agent-token`.
2. Write the token into it as its one line, with an editor such as `nano /root/agent-token`.
3. Add ` < /root/agent-token` to the end of the last line of the block.
4. Delete the file afterwards with `rm /root/agent-token`.

Write the file with Unix line endings: a carriage return is a character outside `0x21` to `0x7E`, and the token is refused. The token
is asked for only while `/etc/vandox/agent-token` does not exist, so an upgrade does not ask for it.

Without internet access on the server, download the release files on a machine that has it:

```
v=vX.Y.Z
f=vandox-agent-install-$v.tar.gz
u=https://github.com/LarsLaskowski/Vandox/releases/download/$v
curl --proto '=https' --proto-redir '=https' -fsSL -O "$u/$f" -O "$u/SHA256SUMS"
curl --proto '=https' --proto-redir '=https' -fsSLO "$u/vandox-agent-linux-amd64"
```

Copy the three files to the server into `/root/vandox-files`. Then check their names: `ls /root/vandox-files` must show exactly
`vandox-agent-install-vX.Y.Z.tar.gz`, `SHA256SUMS` and `vandox-agent-linux-amd64`, with this spelling and case. Some upload tools
change a name, for example to lower case. A file `sha256sums` makes the check below fail: `awk` reports that it cannot open `SHA256SUMS` (gawk: `awk: fatal: cannot open file 'SHA256SUMS'`; mawk, Ubuntu's default `awk`, words it differently), then `sha256sum: 'standard input': no properly formatted SHA256 checksum lines found`, and nothing is unpacked. Fix
the name with `mv /root/vandox-files/sha256sums /root/vandox-files/SHA256SUMS`. The check does not accept other spellings on
purpose: it selects the bundle's line by its exact name. On the server run lines 1, 2, 3 and 5 of the block above (`sudo -i`,
`v=`, `f=` and the `mktemp` line), then (`web` stands for the `agent_id` of this server):

```
cp /root/vandox-files/* .
awk -v f="$f" '$2 == f' SHA256SUMS | sha256sum -c --strict - && tar -xzf "$f"
"$d/vandox-agent-install-$v/install.sh" --from . --agent-id web --backend-url http://100.64.0.1:8081
```

Here the binary comes from the directory (`--from .`) and is not downloaded; it is checked against the SHA-256 inside the checked
bundle. Without `--from .` the script would download it from the release of the bundle's version.

**Step 3 - check that it runs.** The installer ends with `vandox-agent vX.Y.Z installed and running` and prints four lines
`check journal-group`, `check journal-clock`, `check ipc-filter` and `check capabilities`, each `ok` or `WARNING <what to do>`. A warning does not fail the
installation; it names something to look at (see *Checks on the server*). Then:

```
systemctl status vandox-agent
journalctl -u vandox-agent -n 50
```

If the installer printed `vandox-agent did not start`, the journal shows why (a wrong `agent.yaml` value, an unreadable token, the spool directory); fix it and
run `systemctl restart vandox-agent`, the unit keeps retrying by itself. The backend shows the server once the first batch arrives.

**Step 4 - upgrade.** Run the lines of step 2 again with the new `v`, in a new shell and a new directory; the last line is just
`"$d/vandox-agent-install-$v/install.sh"` (the kept `agent.yaml` holds the ID and URL). Only the binary and the unit are replaced; the configuration, token, spool
and state stay, and the token is not asked for again.

**Step 5 - uninstall.** Run lines 1 to 7 of step 2 with the installed version (they unpack the same bundle into a new directory), then one of:

```
"$d/vandox-agent-install-$v/uninstall.sh"
"$d/vandox-agent-install-$v/uninstall.sh" --purge
```

The first removes the unit and the binary and keeps the user, `agent.yaml`, the token, the spool and the MariaDB account; `--purge` deletes them too, unsent
records included.

The options, what the script checks and in which order, the messages and the provenance check are in *Install reference* below.

## Install reference: options, checks and what the script does

The release publishes the install bundle `vandox-agent-install-vX.Y.Z.tar.gz` and a `SHA256SUMS` with two lines, the bundle's and the binary's. The
bundle has one directory and ten files (eleven archive entries): `install.sh`, `uninstall.sh`, `common.sh`, `vandox-agent.service`, `mariadb-user.sql`,
`mariadb-user-remove.sql`, `agent.yaml`, `VERSION` (the tag and one line feed), `vandox-agent-linux-amd64.sha256` (the binary's checksum line) and
`MANIFEST.sha256` (the checksums of the seven files the scripts read before they run anything).
It is byte-reproducible from the tagged source. The commands are in *Install the agent*: single lines, of which only the check line unpacks, and only after a passed check, so after a failed step the script's full path does not exist and nothing runs.

- **The check.** `awk` selects the bundle's line of `SHA256SUMS` by exact comparison of the name field, and `sha256sum --check --strict -` checks that
  file against it; with no such line it fails on empty input and `tar` never runs. The check proves the bundle is the file the release published; for the
  provenance too, see *Provenance* below. A new directory per attempt (`d=$(mktemp -d) && cd "$d"`) means a failed check leaves nothing to run: the script's full path does not exist.
- **Extraction as root.** `tar` run by root restores the owner and modes of the archive, which the release check pins to owner 0/0, 0755 for the scripts and 0644 for the rest,
  without special bits; the entry check verifies them. A tar made by someone else or a mode changed afterwards makes the entry check refuse.
- **The entry check.** Before anything is sourced or read, `install.sh` and `uninstall.sh` check the physical directory of the script, each of its
  ancestors, the script and `common.sh`: owned by uid 0, no link, and not writable by group or others (an ancestor with the sticky bit, like `/tmp`, may be;
  the directory, the script and `common.sh` may carry no set-user-ID, set-group-ID or sticky bit). A failure prints
  `<script>: <path> is not owned by root or is writable by others; extract the bundle as root in a private directory`, exit 1, nothing changed. The
  scripts also check `MANIFEST.sha256` and the other bundle files they read the same way before reading them. Running a script from a source checkout, owned by a user,
  therefore fails; use the bundle. Then the manifest: a regular file of at most 4096 bytes with exactly seven `sha256sum` lines for `VERSION`, `agent.yaml`, `common.sh`, `mariadb-user-remove.sql`, `mariadb-user.sql`, `vandox-agent-linux-amd64.sha256` and `vandox-agent.service`, in that order, and `sha256sum --check --strict` of them must pass; else `<script>: the bundle is incomplete or changed; extract it again in a new directory`, exit 1, before `common.sh` is sourced. The manifest proves the unpacking is complete and unchanged, not who made the bundle (that is the operator's check line). What is left is the time between a check and the read of the file.
- **Options.** `--agent-id` and `--backend-url` (required while `/etc/vandox/agent.yaml` does not exist, ignored when it does), `--from DIR` (take
  `vandox-agent-linux-amd64` from a directory instead of downloading it), `--no-mariadb` (skip the MariaDB account without a warning). There is no
  `--version`: the version is the first line of the bundle's `VERSION` (a file that is missing, a link, over 64 bytes or not one `vX.Y.Z` or `vX.Y.Z-rc.N` line
  gives `install.sh: no valid VERSION file next to install.sh; use the install bundle of a release`, exit 2). The token is read from the terminal without echo, or
  from one line of standard input; `--token` is refused. It is asked for only when `/etc/vandox/agent-token` is missing.
- **What it does, in order,** and every message and exit code: the Agent area, *Installation* (`docs/areas/agent.md`). In short: it runs the entry
  check, reads `VERSION`, checks root, systemd and `x86_64`, validates the configuration values and the spool directory, downloads only the binary
  over HTTPS (`curl --proto =https --proto-redir =https --fail`) into a private temporary directory, requires the bundle's `vandox-agent-linux-amd64.sha256` (a regular
  file, no link, one line, at most 4096 bytes) to match the binary (a link, a non-regular file or more than 64 MiB is refused), runs the verified binary once as
  `nobody` with `-version` and requires it to equal `VERSION`, and only then changes the server: user and groups, `/etc/vandox`, `agent.yaml`, the token (root:root 0600), the
  spool directory, the binary (written beside the old one and renamed over it), the unit, the MariaDB account, enable and restart.
- **Upgrade.** Run the command again with the new version. Only the binary and the unit are replaced; configuration, token, spool and state stay.
- **Offline.** The variant of *Install the agent* with `--from .` (the new directory the lines changed into).
- **Uninstall.** The commands are in *Install the agent*, step 5.
  The plain run stops and disables the agent (it saves its state), removes the unit and the binary, and keeps the user, `agent.yaml`, the token, the spool and the
  MariaDB account so a later install continues. `--purge` also removes them (it says that unsent records are lost before it deletes `/var/lib/vandox`) and drops
  the MariaDB account with `mariadb-user-remove.sql`; when that fails it prints the command to run with the bundle's path, for example
  `uninstall.sh: WARNING MariaDB account not removed; run: mariadb < /path/to/vandox-agent-install-vX.Y.Z/mariadb-user-remove.sql`. A home directory of an existing
  `vandox-agent` user is named and left in place. Both succeed on an absent or partial installation.
- **MariaDB.** When the account cannot be created, `install.sh` continues with `install.sh: WARNING MariaDB account not created; run: <plesk db | mariadb> < <path>`,
  where `<path>` is `mariadb-user.sql` in the extracted bundle.
- **Server checks.** After the start it prints `check journal-group`, `check journal-clock`, `check ipc-filter` and `check capabilities`, each `ok` or
  `WARNING <what to do>`; see *Checks on the server*.
- **Drop-ins.** A drop-in directory `/etc/systemd/system/vandox-agent.service.d/` or `/run/systemd/system/vandox-agent.service.d/` can change the unit
  after the script has installed it; the script names each one it finds and does not read it. Check it against *Unit*.
- **Provenance.** The two checks above prove integrity, not who built the files. At a release the bundle carries a build-provenance attestation, and
  because the bundle holds the binary's checksum, verifying the bundle also covers the binary:
  `gh attestation verify vandox-agent-install-vX.Y.Z.tar.gz --repo LarsLaskowski/Vandox` (and the same for `vandox-agent-linux-amd64`).
- **The name server.** With Tailscale's DNS turned off (*Tailscale on the monitored server*, step 3), the server keeps its own name
  server and MagicDNS names do not resolve, so `backend.url` uses the backend's tailnet address. A host name would be resolved by
  the name server of `/etc/resolv.conf`, and the unit blocks every name server outside localhost and the tailnet (`install.sh:
  WARNING name server <ip> is blocked by the unit; use the tailnet address or MagicDNS`).
- **The server run from the CI artifact.** For the Product Manager's run of a pull request's head, download the artifact
  `agent-install-files` of the `agent-install` run of the approved head SHA (the run ID and the SHA are given with the request).
  Put its three files into `/root/vandox-files`, check their names as in the offline variant, and run
  `sha256sum --check --strict SHA256SUMS` there (it checks both lines). Its bundle has the version `v0.0.0-rc.0`, which has no
  GitHub release, so the online lines cannot install it: the script would try to download the binary and end with `install.sh:
  download failed: vandox-agent-linux-amd64`. Follow the offline install of step 2 with `v=v0.0.0-rc.0` and `--from .`; the
  binary in the artifact is checked against the bundle's SHA-256.

## Rights per collector

`vandox-agent` runs as the dedicated system user `vandox-agent`, **never as root**, without a login shell. The rights it holds are
listed here, each with the collector that needs it, as those collectors are implemented; the complete set is the one the Agent area names (*Rights*: `CAP_SYS_PTRACE` and the groups `adm` and `systemd-journal`), and nothing beyond it is granted. The rules behind them are in the Agent area
(`docs/areas/agent.md`, *Rights*) and the security area 6 of `.squad/project.md`. Adding a right, a capability or a group is a
`security`-tier change. The example configuration is `agent.yaml` in this folder; the unit is `vandox-agent.service`, installed by
`install.sh` and removed by `uninstall.sh` (both in this folder, with `common.sh`); see *Install the agent*, *Install reference* and *Unit*.

## Capabilities

| Capability | Collector | What it is for |
| ---------- | --------- | -------------- |
| `CAP_SYS_PTRACE` | `processes` | Reading `/proc/<pid>/smaps_rollup` of **another user's** process, which gives the proportional set size (PSS). |
| `CAP_SYS_PTRACE` | `connections` | Listing and reading `/proc/<pid>/fdinfo/<n>` of **another user's** process, which names the socket (`ino:`) a descriptor holds and its mount (`mnt_id:`), so a socket gets its owner. |

`CAP_DAC_READ_SEARCH` is **not granted**: with `CAP_SYS_PTRACE` it opens other processes' open files, memfds included, for writing
(record 0122). Only the `logs` collector would use it, for configured files the agent's user and groups cannot read, and it does without it; the connection collector deliberately does not use it (see below). The `logs` collector adds no
capability: it opens each file of `log_shipping.files` through a walk from `/` that refuses a symbolic link in any element and a file with
more than one link (against an attacker who races the check, the hard-link refusal relies on the distribution default
`fs.protected_hardlinks=1`, Ubuntu's default), so a service user who replaces its log file with a link to another file (for example
`/etc/shadow`) never gets that file shipped. It reads the files the agent's user and groups (`adm`, `systemd-journal`) can read and logs
`log file not readable` with the reason `permission denied` for the others.

`CAP_SYS_PTRACE` is granted as an ambient capability, with a bounding set that holds `CAP_SYS_PTRACE` only; adding any capability to it is a
`security` change that supersedes record 0122. For example, `CAP_DAC_READ_SEARCH` reopens the write through `/proc/<pid>/fd/<n>` into other
processes' open files, memfds included, and below closed directories; `CAP_DAC_OVERRIDE` and the other write-side DAC capabilities the write
open of `mem`; `CAP_SYS_NICE` `process_madvise` on another process; `CAP_SYS_ADMIN` or `CAP_CHECKPOINT_RESTORE`, together with directory
search, the links in `/proc/<pid>/map_files/`. The unit is
confined as soon as it grants any (`NoNewPrivileges=yes`, a system-call filter that denies `ptrace`, `process_vm_readv`,
`process_vm_writev`, `pidfd_getfd`, `open_by_handle_at` and `process_madvise`, no write-side capability). `process_madvise` is named
explicitly because `@system-service` includes it; `pidfd_open` stays allowed.

Three barriers keep the agent from writing into another user's process or file beyond what the file's own mode grants it, and none of
them alone covers every vector examined. (a) DAC plus the bounding set: opening another user's `/proc/<pid>/mem` for writing needs
`CAP_DAC_OVERRIDE` (the file is mode `0600`, owned by the target's user), which `CAP_SYS_PTRACE` does not grant, and looking up an entry in
`/proc/<pid>/fd` (mode `0500`, owned by the target's user) needs `CAP_DAC_READ_SEARCH`, so the bounding set must keep both out, and every
write-side DAC capability too. (b) The system-call filter, for `ptrace`, `process_vm_writev`, `pidfd_getfd`, `open_by_handle_at` and
`process_madvise`; it does not deny `openat` or `pwrite`, so it is not what stops a write open. (c) The kernel, behind the filter, for
`pidfd_open` plus `process_madvise`: the call needs `CAP_SYS_NICE` (since kernel 5.12), which the bounding set must also keep out, and the
kernel refuses destructive advice on another process; the accepted advice set is version-dependent, so this is defence in depth only.
A compromised agent cannot use its capability to write into another user's process or file, or to run code as another user, except through a file
whose own mode grants the agent's user, its groups (`adm`, `systemd-journal`) or others write (accepted residual, record 0122).
Measurement and reasoning: record 0122 and the Agent area.

**Accepted residual.** With `CAP_SYS_PTRACE` the agent follows another process's `/proc/<pid>/cwd` and `/proc/<pid>/root` links and their
`/proc/<pid>/task/<tid>/cwd` and `…/root` variants, and the `/proc/<pid>/exe` and `/proc/<pid>/task/<tid>/exe` links, so a file whose own mode grants the agent's user, its groups or others read or write is
reachable even below a closed directory that contains another process's working or root directory, and the `exe` link reaches that process's executable file, readable or writable as its own mode allows (a file is reachable only along paths from
some process's `cwd` or `root`, or that process's executable through `exe`; the path components after the link are still checked against their own modes), and the `root` link reaches the
file tree of another mount namespace (a container's). A write into such a file reaches another user's process memory where that process maps the
file `MAP_SHARED`, and runs code as another user where that user executes the file or reads it as a script or configuration. Readable besides are
the ptrace-checked `/proc` files whose mode allows reading (`maps`, `smaps_rollup`, `fdinfo`, …), not another user's `mem` or `environ` and not
`/etc/shadow`; credentials read that way may still lead to root through other services (password reuse, Plesk or MariaDB administration).

### What the log collector reads and what it needs

- **The journal.** The system files (`system.journal`, `system@*.journal`, `system@*.journal~`) **and** the user files (`user-<UID>.journal`,
  `user-<UID>@*.journal`, `user-<UID>@*.journal~`) below `/var/log/journal/<machine-id>`, else `/run/log/journal/<machine-id>`, are read with the
  group `systemd-journal`; journald's default `SplitMode=uid` puts the entries of users with a UID of 1000 or more (on Plesk the web-space
  users) into the user files. The agent reads the machine ID from `/etc/machine-id` and decodes zstd-compressed items itself (no `journalctl`).
- **Configured files.** Each file of `log_shipping.files` is read with the group `adm`, or because its mode lets any user read it; a file that is
  readable by neither, or below a directory the agent has no search right on, is logged `log file not readable` with the reason
  `permission denied` and not shipped, because `CAP_DAC_READ_SEARCH` is not granted (record 0122). The path is walked from `/` without following a link; the directory of a rotated file is reached the same way when the
  agent looks for it after a rotation that happened while it was stopped (`<name>.1` and `<name>-YYYYMMDD`).
- **The time zone.** `/etc/localtime` (readable by anyone) gives the zone of the year-less times of `syslog` files. When it cannot be read the
  agent logs `time zone not readable` once, reads no `syslog` file until it can, keeps their positions and records no gap; the journal is
  not affected.
- **What it logs.** Fixed texts only, one per change: `journal not readable` (with a reason such as `journal: no journal directory`),
  `log file not readable` (with the index of the file in `log_shipping.files` and a reason: `missing`, `not a regular file`, `link refused`,
  `permission denied`, `read failed`), `time zone not readable` and their `... readable again`. A path, a field value or a message never
  reaches the agent's own log.
- **Duplicates.** A file that holds what the journal ships (rsyslog's `/var/log/syslog` on a server that forwards the journal to rsyslog) is shipped
  twice, once per source. The agent does not remove them.

### What the process collector reads and what it needs

- `/proc/<pid>/stat`, `status` and `cmdline` of every process are readable by any user, so the collector needs **no right** for them,
  unless `/proc` is mounted with `hidepid` (then it sees only its own processes, and the snapshot is incomplete for that reason).
- `/proc/<pid>/smaps_rollup` of **another user's** process needs `CAP_SYS_PTRACE`: the kernel checks ptrace access when the file is
  opened. `CAP_DAC_READ_SEARCH` does **not** replace it. Measured on the development host (kernel 6.18, no Yama restriction, no
  `hidepid`), as user 65534 against a process of root in the same namespace: `EACCES` without any capability and with
  `CAP_DAC_READ_SEARCH` alone, readable with `CAP_SYS_PTRACE`.
- Without the capability the collector still runs: it records the resident set size (RSS) from `status` and leaves the PSS of those
  processes (and the PSS sum of their programs) out, never 0. The agent logs the number of processes without PSS once.
- The read can be refused even with the capability: a process in another user namespace, a Yama or other security-module policy.
  Those cases were not measured; the effect is the same as without the capability, for those processes only.
- A kernel thread or a zombie has no address space; the kernel answers `ESRCH` for its `smaps_rollup`, and the collector records 0.
- `/proc/stat`, `/etc/passwd` and the other files the system collectors read are readable by any user and need nothing.

### What the connection collector reads and what it needs

- `/proc/net/tcp`, `tcp6`, `udp` and `udp6` are readable by any user and need no right. `tcp6` and `udp6` are missing on a kernel without IPv6;
  the collector then leaves those protocols out.
- `/etc/passwd` is world-readable and needs no right. The collector reads it once per run, with the process collector's bounds (1 MiB, lines of
  4 KiB), to name the UID the kernel prints for each socket. Without it the sample still succeeds and every `user` is the decimal UID; the agent
  logs `user names unavailable` once and `user names available` when the file reads again.
- `/proc/<pid>/fdinfo/<n>` of **another user's** process needs `CAP_SYS_PTRACE`: the kernel checks ptrace access when the directory is listed
  and the file opened. The links `/proc/<pid>/fd/<n>` (`socket:[N]`) would need `CAP_DAC_READ_SEARCH` to list and both capabilities to read,
  and `CAP_DAC_READ_SEARCH` lets the agent read every file on the server (`/etc/shadow`, Plesk's credentials) and, with `CAP_SYS_PTRACE`, open
  other processes' open files, memfds included, for writing (record 0122), so the collector does not read them (record 0115) and the agent does not hold it
  (record 0122). Measured on the development host (kernel 6.18.44, no Yama restriction, no `hidepid`), as user 65534 against a process of root:

  | Capabilities | list `fd/` | readlink `fd/3` | list `fdinfo/` | read `fdinfo/3` | read `/etc/shadow` |
  | ------------ | ---------- | --------------- | -------------- | --------------- | ------------------ |
  | none | `EACCES` | `EACCES` | `EACCES` | `EACCES` | `EACCES` |
  | `CAP_DAC_READ_SEARCH` | ok | `EACCES` | `EACCES` | `EACCES` | ok |
  | `CAP_SYS_PTRACE` | `EACCES` | `EACCES` | ok | ok (`mnt_id: 10`, `ino:` the socket's inode) | `EACCES` |
  | both | ok | `socket:[N]` | ok | ok | ok |

- With `CAP_SYS_PTRACE`, `hidepid=2` hides no process (measured in a scratch mount namespace with `proc` mounted with `hidepid=2`); without
  it other users' processes are invisible, and their sockets then carry no owner **without a warning**.
- The `ino:` line of `fdinfo` exists from Linux 5.14 on (the server's Ubuntu 22.04 has 5.15 or a newer kernel). On an older kernel the start-up probe of the socket mount fails, no socket
  gets an owner and the agent logs `socket owners unknown`.
- Without the capability the collector still runs: the sockets of other users' processes carry no owner, the sample succeeds and the agent logs
  `socket owners partly unknown` once with the number of processes it could not read.
- **The socket probe needs no right but a unit setting.** At its start the agent opens one Unix datagram socket (never bound, connected or
  sent on) and reads its own `/proc/self/fdinfo/<fd>` to learn the mount that holds the kernel's sockets; only descriptors on that mount count as
  sockets. If the unit refuses the socket, no socket gets an owner and the agent logs `socket owners unknown`.

### Unit settings that must stay as they are

- `RestrictAddressFamilies=` must keep **`AF_UNIX`** (the probe above, the notification socket of systemd and the system bus of the systemd collector) and **`AF_INET`** and **`AF_INET6`** (the sender's connection to the backend; the unit's `IPAddressDeny=any` with `IPAddressAllow=localhost 100.64.0.0/10 fd7a:115c:a1e0::/48` lets the agent reach only localhost and the tailnet, so `backend.url` names a tailnet address or a MagicDNS name; a host name that is resolved by a name server outside localhost and the tailnet fails as `connection failed`, and `install.sh` warns about such a name server).
- No own network namespace: no `PrivateNetwork=` and no `NetworkNamespacePath=`, or `/proc/net` shows another namespace's sockets.
- `/run/dbus/system_bus_socket` stays reachable: no `InaccessiblePaths=` or `TemporaryFileSystem=` over `/run/dbus`.
- No `ProcSubset=pid`: it hides `/proc/net`.
- `ProtectProc=` stays at `default`: a stricter value hides other users' processes whenever the agent runs without `CAP_SYS_PTRACE`.
- `/run/mysqld` stays reachable (the MariaDB collector connects to `/run/mysqld/mysqld.sock`): no `InaccessiblePaths=` or `TemporaryFileSystem=` over `/run/mysqld`, and `AF_UNIX` in `RestrictAddressFamilies=`.

### What the systemd collector reads and what it needs

- It asks systemd for the state of the configured units over the system bus socket `/run/dbus/system_bus_socket`. Reading unit states is
  allowed to any user, so the collector needs **no group and no capability**.
- It needs the socket to be reachable: the unit must not hide `/run/dbus` (no `InaccessiblePaths=` or `TemporaryFileSystem=` over it), and
  `RestrictAddressFamilies=` must keep `AF_UNIX`, also for this bus connection.
- With `services.units: []` the collector is off and the bus is never opened.

### What the MariaDB collector reads and what it needs

- It connects to the local MariaDB server over its Unix socket (`mariadb.socket`, default `/run/mysqld/mysqld.sock`) and sends only fixed,
  read-only statements: the server status and variables, the process list when many threads run or the server is under memory pressure, and
  two questions about its own account. It needs **no group, no capability and no password**; with `mariadb.enabled: false` it does not run and
  never opens the socket.
- **The account.** The agent logs in as `vandox-agent`@`localhost`, authenticated by the operating system user of the connecting process
  (`unix_socket`), so no database password exists. `deploy/agent/mariadb-user.sql` creates it: `CREATE OR REPLACE USER 'vandox-agent'@'localhost'
  IDENTIFIED VIA unix_socket WITH MAX_USER_CONNECTIONS 2` and `GRANT PROCESS ON *.* TO 'vandox-agent'@'localhost'`, nothing else.
- **Run it once as the database administrator.** The file may be run again, for example after a Plesk update dropped the user; it resets the
  account to exactly this state. As root on a plain MariaDB server: `mariadb < mariadb-user.sql`. On a Plesk server: `plesk db < mariadb-user.sql`.
- **What `PROCESS` lets the agent see.** The process list of all connections: user, host, database, command, state, time and the **statement
  text** of every running or sleeping connection, and the server status counters. It does not let the agent read a table or its data, change
  anything or see a password hash.
- **The statement texts can hold customer data and credentials** (`IDENTIFIED BY '...'`, the values of an `INSERT`). While the process list is
  recorded (more than 10 running threads, or memory pressure), the agent cuts each statement to 16 KiB, keeps the first 64, and sends them to
  the backend, which stores and shows them in the web UI. Treat the backend and its web UI like the database for that reason.
- **Without the user** the collector records the server as `not_answering` with the error number the server sent (for example 1698) and logs
  how to fix it; a missing socket is recorded as `down`.

## Groups

The group `systemd-journal` is used by the `kernel` collector and the `logs` collector: they read the binary journal files below
`/var/log/journal` and `/run/log/journal` themselves (pure Go, no `journalctl`). The `kernel` collector reads the system files for the kernel
messages that report an out-of-memory kill, without decompression; the `logs` collector reads the system and the user files and decodes
zstd-compressed items. The group `adm` (log files) is for the files named in `log_shipping.files`; the process and `backups` collectors need
neither group, and neither do the connection, systemd and MariaDB collectors.

## Kernel events, log shipping, backups and the state file

- **The unit keeps the journal readable**: no `InaccessiblePaths=` or `TemporaryFileSystem=` over `/var/log/journal` or `/run/log/journal`, and
  it starts after `systemd-journal-flush.service`, so the persistent journal is in place when the `kernel` collector first reads it. A server
  without a persistent journal is read from `/run/log/journal`; if neither can be read, the collector logs
  `kernel messages not readable` once, records no gap and resumes at the saved position once the journal is readable again.
- **The spool** lives in `spool.directory` too and holds the records the backend has not yet acknowledged. The agent writes segment
  files `segment-<number>.spool` (mode 0600) and the bookkeeping file `spool-meta.json` (mode 0600, written through `spool-meta.json.tmp`:
  the agent ID, the boot ID, the next sequence number, whether the spool was closed cleanly and the acknowledged ranges), and deletes a segment file once the backend has acknowledged all its records,
  when it is damaged, or when the oldest data has to give way to new records (`spool.max_size`, `spool.min_free`). Other entries in the directory,
  `agent-state.json` among them, are never read, changed or deleted by the spool. No spool file holds the agent token.
- **The state file** `agent-state.json` lives in `spool.directory` and holds the boot, the position in the journal, the open backup run and the
  positions of the `logs` collector (a position per journal sequence number ID, at most 256, and per configured file, at most 16, with the
  SHA-256 of its path instead of the path; up to 64 KiB in all), so a restart neither repeats nor loses a kill or a log line, and a backup
  that ended while the agent was down is closed. The agent saves it within 10 seconds after a position changed (the next run of its
  10-second state job) and once more when it stops; after a crash it ships the lines of the last seconds again. The agent writes this file and the spool's files
  (mode 0600) and **never creates the directory**: the installation creates `spool.directory` and makes it writable for `vandox-agent`
  (for example `install -d -o vandox-agent -g vandox-agent -m 0700 /var/lib/vandox/spool`). When the file cannot be read or written the agent
  logs `agent state not available` or `agent state not saved` once and runs on without it.
- **The `backups` collector** reads `/proc` like the process collector and needs no further right; its rules are `backups.processes` in
  `agent.yaml`.

## Unit

`vandox-agent.service` is installed as `/etc/systemd/system/vandox-agent.service` (root, 0644). Every setting and why:

| Setting | Why |
| ------- | --- |
| `Description=`, `Documentation=` | Names the unit and links this file. |
| `After=systemd-journal-flush.service` | The persistent journal is in place when the `kernel` collector first reads it. |
| `StartLimitIntervalSec=0`, `Restart=on-failure`, `RestartSec=30s` | A failed or watchdog-stopped agent is restarted at most twice a minute for ever, never giving up (`start-limit-hit`), so it comes back by itself once a cause such as a missing spool directory is fixed. |
| `Type=notify`, `NotifyAccess=main`, `WatchdogSec=2min` | The agent sends `READY=1` and `WATCHDOG=1`; it pings every minute while its runners are on time (the longest runner timeout is 10 s), so a short stall under pressure does not restart it, a stuck runner does. |
| `ExecStart=/usr/local/bin/vandox-agent`, `User=`, `Group=vandox-agent` | The dedicated user, never root. |
| `SupplementaryGroups=adm systemd-journal` | Log files and the journal (*Groups*). |
| `AmbientCapabilities=CAP_SYS_PTRACE`, `CapabilityBoundingSet=CAP_SYS_PTRACE`, `NoNewPrivileges=yes` | The one capability the process and connection collectors need, and nothing else (records 0030, 0122). |
| `LoadCredential=agent-token:/etc/vandox/agent-token`, `Environment=VANDOX_AGENT_TOKEN_FILE=/run/credentials/vandox-agent.service/agent-token` | The token reaches the agent as a credential file; the path is written out because the `%d` specifier is not known to systemd 249. It is never an argument or an environment value (record 0032). |
| `Environment=GOMEMLIMIT=192MiB` | The Go runtime does not know the cgroup limit; with the default garbage collector a heap can reach about twice its live size and the kernel would kill the agent for garbage. A soft limit below `MemoryMax=` makes the runtime collect harder only near it. A unit setting, not an agent option. |
| `ProtectSystem=strict`, `ReadWritePaths=/var/lib/vandox`, `ProtectHome=yes`, `PrivateTmp=yes`, `PrivateDevices=yes`, `ProtectKernelTunables=yes`, `ProtectKernelModules=yes`, `ProtectKernelLogs=yes`, `ProtectControlGroups=yes`, `ProtectHostname=yes`, `InaccessiblePaths=/etc/shadow /etc/gshadow` | The agent writes only below `/var/lib/vandox` (spool and state) and reads little else. `ProtectKernelLogs=yes` hides `/dev/kmsg` and `syslog(2)`; the kernel collector reads the journal instead. |
| `RestrictNamespaces=yes`, `RestrictRealtime=yes`, `RestrictSUIDSGID=yes`, `LockPersonality=yes`, `MemoryDenyWriteExecute=yes`, `RemoveIPC=yes`, `UMask=0077`, `KeyringMode=private` | Further confinement; none is used by the collectors. |
| `RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6` | The socket probe, the bus and the MariaDB socket need `AF_UNIX`; the sender needs the two internet families. |
| `SystemCallArchitectures=native`, `SystemCallFilter=@system-service adjtimex`, `SystemCallFilter=~ptrace process_vm_readv process_vm_writev pidfd_getfd open_by_handle_at process_madvise`, `SystemCallErrorNumber=EPERM` | `adjtimex` is read by the `clock` collector; the six denied calls are barrier (b) of record 0122. `process_madvise` is named because `@system-service` includes it. |
| `IPAddressDeny=any`, `IPAddressAllow=localhost 100.64.0.0/10 fd7a:115c:a1e0::/48` | The agent connects only over the tailnet (records 0006, 0010); the unit enforces it. `localhost` allows loopback TCP and UDP (a backend on the same host, which is also the target of the CI check); Unix sockets (MariaDB, the system bus, the journal) are not subject to `IPAddressAllow=` at all. |
| `MemoryMax=256M` | The documented worst cases (a process run 48 MiB, MariaDB 32 MiB, logs 24 MiB, kernel 4 MiB, the sender one 1 MiB batch per stream) sum to about 112 MiB if all peak at once under memory pressure; with `GOMEMLIMIT=192MiB` this leaves headroom for the runtime and page cache. The memory pressure mode reads the system's `MemAvailable`, not the cgroup's. The agent's real peak RSS is **to be measured** (the parallel run of #45); until then the values rest on the documented bounds. |
| `CPUQuota=50%` | Bounds a runaway loop to half a core; collector timeouts are at most 10 s, so an idle server never reaches it. |
| `Nice=5`, `IOWeight=50` | The agent yields to the monitored services without starving while an outage builds. |
| `[Install] WantedBy=multi-user.target` | Starts at boot. |

Not set, on purpose (the *Unit settings that must stay as they are* section): `ProtectClock`, `PrivateNetwork`, `NetworkNamespacePath`, `ProcSubset`,
a `ProtectProc=` other than `default`, `PrivateUsers`, `TemporaryFileSystem`, `DynamicUser`, any specifier, `VANDOX_AGENT_TOKEN=`, and any
`InaccessiblePaths=` over `/run/dbus`, `/run/mysqld`, `/var/log/journal` or `/run/log/journal`.

**`systemd-analyze security vandox-agent`.** Required: the overall exposure level `OK` or better (record 0131). A draft with these settings scored 2.2
`OK` offline on systemd 255 in a development session; the shipped unit was measured on 249 as below.

| Where | systemd | Overall exposure level |
| ----- | ------- | ---------------------- |
| CI job `agent-install`, Ubuntu 22.04 runner (PR #324, head `a0d92a6`, run 40480331990) | 249 (`249.11-0ubuntu3.22`) | `Overall exposure level for vandox-agent.service: 2.2 OK` |
| the monitored server (the Product Manager's server run, the artifact described in *Checks on the server*) | 249 (Ubuntu 22.04.5 LTS) | `Overall exposure level for vandox-agent.service: 2.2 OK` in both runs |

Items the CI run and the server run both still flag (the same 15 items; the exposure each adds), with the reason the unit keeps them open:

| Item | Exposure | Reason |
| ---- | -------- | ------ |
| `PrivateNetwork=` | 0.5 | The sender needs the network, and the connection collector reads the host's `/proc/net` (see *Unit settings that must stay as they are*). |
| `CapabilityBoundingSet=~CAP_SYS_PTRACE` | 0.3 | `CAP_SYS_PTRACE` is the one capability the process and connection collectors need (record 0122); the bounding set holds nothing else. |
| `RestrictAddressFamilies=~AF_(INET\|INET6)` | 0.3 | The sender's connection to the backend needs Internet sockets; `IPAddressAllow=` limits where they may go. |
| `PrivateUsers=` | 0.2 | Not set on purpose: the collectors read other users' processes, which a private user namespace would hide or change (see the forbidden settings above). |
| `ProtectClock=` | 0.2 | The `clock` collector calls `adjtimex` (record 0111), which `ProtectClock=` would deny. |
| `ProtectProc=` | 0.2 | Must stay `default`: a stricter value hides other users' processes whenever the agent runs without `CAP_SYS_PTRACE`. |
| `SystemCallFilter=~@clock` | 0.2 | The `clock` collector calls `adjtimex`, which `@clock` holds. |
| `SystemCallFilter=~@privileged` | 0.2 | `@privileged` holds `@clock`, so `adjtimex` (record 0131, option 9). |
| `SystemCallFilter=~@resources` | 0.2 | `@resources` holds calls the Go runtime may make at start (record 0131, option 9). |
| `IPAddressDeny=` | 0.1 | Flagged because the allow-list has entries other than localhost: the tailnet ranges are the point of the setting. |
| `AmbientCapabilities=` | 0.1 | The unprivileged process holds `CAP_SYS_PTRACE` through it. |
| `RootDirectory=` / `RootImage=` | 0.1 | Not applicable to this unit: the agent reads the host's `/proc`, journal and sockets. |
| `SupplementaryGroups=` | 0.1 | The groups `adm` and `systemd-journal` give the log and journal reads. |
| `RestrictAddressFamilies=~AF_UNIX` | 0.1 | `AF_UNIX` is needed by the socket probe, the notification socket, the system bus and the MariaDB socket. |
| `ProcSubset=` | 0.1 | `ProcSubset=pid` hides `/proc/net`, which the connection collector reads. |

**What the CI run measured on that runner** (not the server: the runner has no real journal or rsyslog setup, so its check results say nothing about
the server): `ptrace(PTRACE_ATTACH)` under the unit's confinement printed `EPERM`, the running agent's `CapEff` and `CapBnd` were `0000000000080000`;
the address allow-list blocked the runner's non-tailnet address and reached loopback; the four install-time checks printed `ok`; and install,
repeat install, restart after a removed spool directory, uninstall, reinstall and purge all passed.

## Checks on the server

The install script prints these four checks (a warning never fails the installation); the rest are run by hand. The Product Manager
ran them on a monitored server (Ubuntu 22.04.5 LTS, version `v0.0.0-rc.0`) with the artifact `agent-install-files` of the `agent-install`
run of PR #324, twice. The binary is rebuilt in every CI run and is not bit-identical between runs, so the SHA-256 values below
identify the exact files of each run.

- **First run (reboot measured).** The run number of the artifact was not recorded.
  - bundle `vandox-agent-install-v0.0.0-rc.0.tar.gz`: `e2a7b07e9c42f129598b8c41b81ec9c3f2a92006c8052470ff62dee7af572c4f`
  - binary `vandox-agent-linux-amd64`: `1af5983910c8a01a154ea898dc52f0e6d9726ba0762f52c4730557ee28676065`
- **Re-run with the bundle of `d9de44b` (`install.sh` with the three Sonar fixes; no reboot).** CI run 40929022861. The agent was
  uninstalled without `--purge` and installed again from that artifact on the production server.
  - bundle `vandox-agent-install-v0.0.0-rc.0.tar.gz`: `451e18ade63231e5d050ecd034c3807dfc568e48b5dab472d3c99d8062758fff`
  - binary `vandox-agent-linux-amd64`: `4dc16fc732c24275672e621508efcfe5893981b306db64fbc1acbb4e37171a58`

The results are in the last column; they are the first run's, with a note where the re-run was checked too. In the first run the user
`vandox-agent` already existed: the script kept its primary group `vandox-agent`, set its supplementary groups to `adm` and
`systemd-journal`, kept its home directory in place, and ended with `vandox-agent v0.0.0-rc.0 installed and running`. The re-run printed
`install.sh: keeping existing /etc/vandox/agent.yaml`, `home directory /home/vandox-agent is kept in place`, the four checks
`ok` and `vandox-agent v0.0.0-rc.0 installed and running`; the service was `enabled` and `active (running)` with 14.2M of
256.0M memory, and the agent logged `socket owners partly unknown` and `backend not accepting batches` (the backend was
`vandoxd` v0.1.0 without the ingest API, as before).

| Check | Command or line | Result to expect | Result of the server run |
| ----- | --------------- | ---------------- | ------------------------ |
| Journal files readable by the group | `check journal-group: ok` | `ok`; the `WARNING` means a file below `/var/log/journal` is not group-readable by `systemd-journal` (or there is none: a volatile journal only under `/run/log/journal`; then the `kernel` and `logs` collectors read that one, check it by hand with `ls -l /run/log/journal/*/`) | `check journal-group: ok` (re-run: `ok`) |
| Clock of the journal | `check journal-clock: ok` | `/proc/uptime` and the journal's monotonic clock differ by less than 1 s; `WARNING ... differ by <n> ms` means every live kill reads as late and gets no extra snapshots; `WARNING could not read the journal timestamp` means `logger` or `journalctl` failed | `check journal-clock: ok` (re-run: `ok`) |
| `process_madvise` in `@ipc` | `check ipc-filter: ok` | systemd lists the name, so the system-call filter entry takes effect | `check ipc-filter: ok` (re-run: `ok`) |
| Capabilities of the running agent | `check capabilities: ok` | `CapEff` is `0000000000080000` (`CAP_SYS_PTRACE` alone) | `check capabilities: ok` (re-run: `ok`) |
| Security score | `systemd-analyze security vandox-agent` | `OK` or better (see *Unit*) | `2.2 OK`, the same items flagged as in the CI run (*Unit*) (re-run: `2.2 OK`) |
| `ptrace` attach is refused | run `sleep 300 &` as root, then `sudo systemd-run --wait --pipe -p User=vandox-agent -p AmbientCapabilities=CAP_SYS_PTRACE -p CapabilityBoundingSet=CAP_SYS_PTRACE -p NoNewPrivileges=yes -p 'SystemCallFilter=@system-service adjtimex' -p 'SystemCallFilter=~ptrace process_vm_readv process_vm_writev pidfd_getfd open_by_handle_at process_madvise' -p SystemCallErrorNumber=EPERM -p SystemCallArchitectures=native python3 -c 'import ctypes,sys; l=ctypes.CDLL(None,use_errno=True); r=l.ptrace(16,int(sys.argv[1]),0,0); print("EPERM" if r==-1 and ctypes.get_errno()==1 else "NOT REFUSED")' <PID of the sleep>` | prints `EPERM` | `EPERM` (re-run: `EPERM`) |
| Reboot | `sudo reboot`, then `systemctl is-active vandox-agent` | `active`, started by itself after the journal flush | `active` and enabled, started by itself after the reboot; memory 25.8M of `MemoryMax=256M`; measured in the first run only: the unit file is unchanged since, and the re-run on the production server did not reboot |
| Files and modes | `stat -c '%U:%G %a %n' /etc/vandox/agent-token /etc/vandox/agent.yaml /var/lib/vandox/spool` | `root:root 600`, `root:vandox-agent 640`, `vandox-agent:vandox-agent 700` | as expected |
| Transmission to the backend | the agent's log (`journalctl -u vandox-agent`) after the start; the fourth line of *Tailscale on the monitored server*, step 4 | the line prints `401`; the agent logs no `backend not accepting batches`, and the backend stores its batches | not part of this run (part of the v0.2.0 release check): the backend was `vandoxd` v0.1.0, which has no ingest API (it came with PR #316 and will first be released in v0.2.0), so the agent logged `backend not accepting batches reason=status status=404` and kept its records in the spool. The other log lines seen in the run were `mariadb down`, then `mariadb answering again`, and `socket owners partly unknown`. |
| rsyslog's logrotate | manual: the configuration renames the log before its single `HUP` | the `logs` collector reads the renamed file until the writer has moved on (Agent area, *Log shipping*) | to be measured |
| Peak memory | the agent's own RSS self-metric during the #45 parallel run | below `GOMEMLIMIT=192MiB` | to be measured |
| Tailscale policy file | save `deploy/agent/tailnet-policy.hujson` as the tailnet's policy (step 2 of *Tailscale on the monitored server*); then save it once with the agent's rule widened to `tcp:*` and once to `"8081"` | the console accepts the file; both widened saves are refused by the `tests` entries | not yet run (issue #338) |
| Tailscale checks | the probes of step 4 of *Tailscale on the monitored server*, with `tailscale status` and `tailscale exit-node list` | `404` and `401` from the ingest port; every other probe blocked; the backend host the only peer; no exit node | not yet run (issue #338) |
| Tailscale settings | the `tailscale set` line and `tailscale debug prefs` (step 3) | SSH, accepted routes, advertised routes, exit node, app connector, web client and Tailscale's DNS off; tag `tag:vandox-agent` | not yet run (issue #338) |
| Tailscale after a reboot | `sudo reboot`, then step 5 | Tailscale up by itself with the same settings; the ingest probes as before; `active`; the agent sends again from its spool | not yet run (issue #338) |
| Key expiry of the tagged server | the server's entry in the admin console (step 3) | expiry disabled | not yet run (issue #338) |

The same cases, on a throw-away runner with systemd 249, run in the CI job `agent-install` (`.github/scripts/check-agent-install.sh`); its results
are in *Unit*. They are the runner's, not the server's; the server's results are in the table above.
