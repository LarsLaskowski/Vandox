# Reverse proxy and certificate for the web UI

The web UI of `vandoxd` speaks plain HTTP and contains no certificate code; TLS is terminated by a reverse proxy in front of the
container ([0023](decisions/0023-tls-through-a-reverse-proxy.md)). This guide shows what the proxy must do, how to tell `vandoxd` which
proxy to trust, and how to set it up with nginx, Caddy and the built-in proxies of Synology DiskStation Manager and QNAP QTS. The rules
the service follows are in the [Web login](areas/web-login.md) area.

The ingest path of the agents does not go through the proxy: it uses the tailnet address and its own port
([0017](decisions/0017-ingest-via-tailnet-address-and-published-port.md)).

> The steps for Synology and QNAP were written from the vendors' documentation and have **not been verified on a device**. The menu
> names can differ between versions. The generic settings below are what counts; the NAS menus only set them.

## What the proxy must do

1. **Terminate TLS** with a certificate for the host name users open in the browser.
2. **Forward to the web port** of the container (`127.0.0.1:8080` with the compose file's default binding, or the address set in
   `WEB_BIND_ADDRESS`), with `Host` passed on.
3. **Set `X-Forwarded-For`** to the address of the client that connected to the proxy, and **`X-Forwarded-Proto`** to `https`.
   Overwrite what the client sent, or append to it: `vandoxd` follows one hop and takes the **last** value of each header, so the value
   the proxy adds must come last.
4. **Pass WebSocket upgrades** (`Upgrade` and `Connection` headers) for `/_blazor`. Every page of the UI holds a live connection
   (Blazor Interactive Server); without WebSocket it falls back to long polling, which works but is slower and holds a request open.
5. **Not buffer** the live connection and not cut it after a short idle time (the page checks its session every 30 seconds, so a read
   timeout of 60 seconds or more is enough).
6. Keep `/healthz` reachable through it if a monitor uses it; it answers without a login.

`vandoxd` uses `X-Forwarded-For` for the client address of the sign-in limits and `X-Forwarded-Proto` for the `Secure` flag of its
cookies. It never reads `X-Forwarded-Host`, `X-Forwarded-Prefix` or `Forwarded`, and it never lets a request skip the login because of
where it came from.

## Give the UI a host name of its own

Use a host name (for example `vandox.home.example`) that no other web application shares. Browsers treat two services on the same
host name or IP address as the **same site**, whatever the port, and send the `SameSite` cookies of the UI to both; a page of another
application there could then act as the signed-in user in ways the cookie settings do not stop. A host name of its own, with its own
certificate, keeps that out. Do not mount the UI under a path of a shared host name either: the UI expects to live at `/`.

## Tell `vandoxd` which proxy to trust: `web.trusted_proxies`

In `vandoxd.yaml`:

```yaml
web:
  trusted_proxies: "172.18.0.1"
```

The value is a comma-separated list of at most 16 IP addresses or networks (IPv4 prefix 8 to 32, IPv6 prefix 16 to 128, no host bits
set, see [Configuration and secrets](areas/configuration-and-secrets.md)). Only a request whose connection comes from a listed
address has its `X-Forwarded-For` and `X-Forwarded-Proto` used. Restart the container after changing the file.

**Find the address as the container sees it**, not as the proxy sees itself:

- A proxy **on the Docker host** (the default `127.0.0.1:8080` binding) connects through Docker's userland proxy, so the container sees
  the **gateway address of its network**:

  ```
  docker network inspect vandox_default --format '{{ (index .IPAM.Config 0).Gateway }}'
  ```

  (`vandox_default` is the compose project's default network; the address is often `172.18.0.1`.)
- A proxy **on another host** (`WEB_BIND_ADDRESS` set to the Docker host's LAN address) is seen with its own LAN address.
- To check, sign in with a wrong password once and read the `client` attribute of the log line `login failed`
  (`docker logs vandoxd`). With the right entry it shows the browser's address; with the proxy's address, the entry is missing or wrong.

**Trusting an address means trusting everyone who connects from it.** A connection through the userland proxy always arrives from the
gateway address, whoever opened it: the reverse proxy, but also any other host that can reach the published port. Such a client can
choose its own `X-Forwarded-For` and so dodge the per-client lockout; only the overall limit of 100 failed sign-ins per hour still
applies to it. That is why the port must not be reachable for others (see *Check the Docker Engine*), and why the entry should be the
proxy's address and not a whole network you do not need.

**Without `web.trusted_proxies`** (the default) every header is ignored and the client is the peer address, which behind a proxy is the
proxy's own. Then:

- all users share one client address, so **five wrong passwords from anyone lock everyone out for 15 minutes**, again and again;
- the cookies are never `Secure`, because the service cannot know that the browser used HTTPS.

If the log shows `forwarded header ignored` together with `login locked`, a request carried `X-Forwarded-For` from an address that is
not in the option: the proxy's address is missing from `web.trusted_proxies`.

## nginx

```nginx
map $http_upgrade $connection_upgrade {
    default upgrade;
    ''      close;
}

server {
    listen 443 ssl;
    http2 on;
    server_name vandox.home.example;

    ssl_certificate     /etc/ssl/vandox/fullchain.pem;
    ssl_certificate_key /etc/ssl/vandox/privkey.pem;

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;

        proxy_set_header Host              $host;
        proxy_set_header X-Forwarded-For   $remote_addr;
        proxy_set_header X-Forwarded-Proto $scheme;

        proxy_set_header Upgrade    $http_upgrade;
        proxy_set_header Connection $connection_upgrade;

        proxy_buffering    off;
        proxy_read_timeout 100s;
    }
}
```

`X-Forwarded-For $remote_addr` replaces whatever the client sent. nginx on the Docker host reaches the container from the network's
gateway address, so that address goes into `web.trusted_proxies`.

## Caddy

```
vandox.home.example {
    reverse_proxy 127.0.0.1:8080
}
```

Caddy passes WebSocket upgrades, sets `X-Forwarded-Proto` and sets `X-Forwarded-For` to the client address (it discards a value sent by
a client it does not trust). For a certificate on a home network use `tls internal` (Caddy's own CA, which every browser has to be
told to trust) or a certificate from a public CA through the DNS challenge for a name you own.

## Synology DiskStation Manager 7

1. **Certificate:** *Control Panel → Security → Certificate → Add*. Import a certificate for the host name, or request one from
   Let's Encrypt (needs a name that resolves to the NAS and, for the standard challenge, reachability from the internet). Assign it to
   the reverse proxy entry below with *Settings*.
2. **Reverse proxy:** *Control Panel → Login Portal → Advanced → Reverse Proxy → Create*.
   - Source: protocol HTTPS, host name `vandox.home.example`, port 443.
   - Destination: protocol HTTP, host name `localhost`, port 8080 (the published port of the container).
3. **WebSocket:** edit the entry, *Custom Header → Create → WebSocket*. This adds the `Upgrade` and `Connection` headers.
4. Synology's proxy is nginx, which sets `X-Forwarded-For` and `X-Forwarded-Proto` for the entries. Set `web.trusted_proxies` to the
   gateway address of the container network (see above) and restart the container. If the log still shows the proxy's address as
   `client`, read that address from the log line and enter it instead.

## QNAP QTS 5

1. **Certificate:** *Control Panel → Security → SSL Certificate & Private Key*. Import a certificate for the host name or request one
   from Let's Encrypt.
2. **Reverse proxy:** *Control Panel → Network & File Services → Network Access → Reverse Proxy → Add*.
   - Source: protocol HTTPS, host name `vandox.home.example`, port 443.
   - Destination: protocol HTTP, host `127.0.0.1` (or `localhost`), port 8080.
   - Enable *WebSocket* support for the rule (a checkbox or a custom header, depending on the version).
3. Assign the certificate to the rule in the *Certificate* settings of the reverse proxy.
4. If your QTS version does not add `X-Forwarded-For` and `X-Forwarded-Proto`, the cookies are not `Secure` and the client address is
   the proxy's even with `web.trusted_proxies` set. In that case run a proxy that does (nginx or Caddy as a container, for example).

## After setup: check list

- `https://vandox.home.example/healthz` answers `ok` through the proxy.
- `https://vandox.home.example/` redirects to `/login`; after signing in the pages load and the page does not show its *Connection to the
  server lost* dialog (a dialog that does not go away points to a failing WebSocket upgrade).
- In the browser's developer tools, the cookie `vandox_session` is `HttpOnly`, `SameSite=Lax` and **`Secure`**. If `Secure` is
  missing, `X-Forwarded-Proto` does not arrive or the proxy's address is not in `web.trusted_proxies`.
- A wrong password shows *The password is not correct.*, and the log line `login failed` carries the **browser's address** in `client`,
  not the proxy's.
- *Sign out* returns to the sign-in page, and a page still open in another tab goes to the sign-in page within 30 seconds.
- The web port is not reachable from another LAN host (next section).

## Check the Docker Engine

The compose file publishes the web port on `127.0.0.1`. That is defense in depth only: the login protects every route, whatever the
network does. Still, an engine that lets other hosts reach the port weakens the lockout (see *trusted proxies* above) and exposes the
login page to the whole LAN.

What is known (from Docker's release notes, [0105](decisions/0105-forwarded-headers-only-from-trusted-proxies-failed-logins-locked-per-client-and-capped.md)):

- Before **Docker Engine 28.0.0**, neighbor hosts on the same network could connect to ports published on a loopback address, and
  remote hosts could connect directly to a container on its published ports.
- **28.0.0** fixed that; **28.3.3** fixed CVE-2025-54388, where reloading firewalld made ports published on loopback reachable from the
  local network again. Run **28.3.3 or later**.
- Which engine version Synology Container Manager and QNAP Container Station ship has **not been verified on a device**, and they are
  believed to be older than 28.0.0. Check yours; do not assume the loopback binding protects you.

Procedure, with two LAN hosts: the Docker host (`NAS`, LAN address `<nas-ip>`) and a second host (`H2`) that is not the NAS.

1. On the NAS, note the engine version, the address of the container and the network's subnet:

   ```
   docker version --format '{{ .Server.Version }}'
   docker inspect vandoxd --format '{{ range .NetworkSettings.Networks }}{{ .IPAddress }} {{ end }}'
   docker network inspect vandox_default --format '{{ (index .IPAM.Config 0).Subnet }}'
   ```

2. On `H2`, with the container address `<container-ip>` and the subnet `<subnet>` from step 1, send the traffic through the NAS and try
   both ways in:

   ```
   sudo ip route add <subnet> via <nas-ip>
   curl -sS -m 5 http://<container-ip>:8080/healthz
   curl -sS -m 5 http://<nas-ip>:8080/healthz
   sudo ip route del <subnet> via <nas-ip>
   ```

   To test the loopback publication the way the CVEs describe it, additionally try to send packets for `127.0.0.1` to the NAS: the
   destination of the packets must be `127.0.0.1` and the next hop the NAS (a route for `127.0.0.1/32 via <nas-ip>` on `H2`, if its
   system accepts one, since most systems treat loopback addresses as local), followed by
   `curl -sS -m 5 http://127.0.0.1:8080/healthz`; remove the route afterwards. A refused route on `H2` means this part of the test could
   not be made, not that the port is safe; say so when reporting the result.

3. **Expected result: every `curl` fails** (connection refused or timeout). An answer `ok` means the port is reachable from the LAN:
   update Docker Engine to 28.3.3 or later, or block the port with the NAS firewall for everything but the proxy, and run the check
   again. The login still protects the UI in that case, but the lockout for a trusted gateway address is then shared with whoever
   connects.
4. Note the engine version and the result, and report them on the issue tracker so that record 0105 can be amended.

## The overall lock and how to recover

Besides the lock of a single client (5 failures in 15 minutes lock that client for 15 minutes), the service locks the sign-in for
**everyone** after 100 failed attempts of all clients within one hour. The lock lasts until the oldest of those failures is one hour
old. This is the only bound on the guesses of a client that can change its address, and the price is that **anyone who can reach the
login page can lock the owner out by failing 100 times**; while attempts keep arriving, each one may also wait up to 5 seconds for the
single password check. On a home network with one user that is accepted
([0105](decisions/0105-forwarded-headers-only-from-trusted-proxies-failed-logins-locked-per-client-and-capped.md)).

The counts live only in memory, so **restarting the container clears every lock** (and signs everyone out):

```
docker compose restart vandoxd
```

If the locks come back, find the source in the log (`login failed` and `login locked` carry the `client` address) and block it at the
proxy or the firewall.
