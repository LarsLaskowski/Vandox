# Plan: Keep the agent's token off the provider's network while Tailscale is down

Source: Issue #337
Status: Revised (1)
Tier: security — the change creates a security control: a guard in the agent's sender that decides through which interface the token and the batches may leave the server.
Security areas touched: 11 Outbound calls (`internal/sender` `HTTPTransport`); 2 Tailscale ACL and port binding (`deploy/agent/README.md`, *Tailscale on the monitored server*); CI configuration (`.github/scripts/check-agent-install.sh`, a new case of the `agent-install` job)

## Problem / root cause

`HTTPTransport.dialContext` (`internal/sender/transport.go:117-124`) dials with a plain `net.Dialer`: the kernel picks the interface
from the routing tables alone. While Tailscale is down its routes are gone, so a connection to the backend's `100.x` address follows the
default route onto the provider's network. The unit's `IPAddressDeny=any` / `IPAddressAllow=localhost 100.64.0.0/10 fd7a:115c:a1e0::/48`
(`deploy/agent/vandox-agent.service:50-52`) matches addresses only and lets that connection pass. Nothing in the agent or the unit
names the interface.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The sender dials with a plain dialer, no interface binding; it is the agent's only non-Unix dial | `transport.go:117-124`; `NewHTTPTransport` sets `DialContext: t.dialContext`, `Proxy: nil` (`transport.go:92-112`); other dials are Unix sockets (`.squad/project.md` area 11) | confirmed |
| 2 | Issue: `IPAddressAllow=` filters by address, not interface | `vandox-agent.service:50-52`; systemd.resource-control: address prefixes only | confirmed |
| 3 | Issue: a 2xx is acknowledged, a 400/413/422 with the refusal header is split and given up | `transport.go:65-66`, `sender.go:596-600`, `626-628`, `644-645`; the issue's line references match `eb91203` | confirmed |
| 4 | Issue: Tailscale's routes live only in table 52 (rule pref 5270) and vanish when it stops or logs out | to verify: Product Manager on the server (`ip rule`, `ip route show table 52` with Tailscale up and stopped), follow-up issue; not relied on (basis: #43's measurement; the chosen control does not rest on it) | unverified |
| 5 | Issue: networkd (v249) removes foreign routes on configure, reconfigure and carrier loss | to verify: the CI case (AC4 (b'), (c), Tester) shows the binding unaffected by `networkctl reconfigure` and a carrier flap of a networkd-managed stand-in link (basis: the issue's reading of `networkd-route.c`; decides only against options 3 and 4) | unverified |
| 6 | Go's dialer calls `Control` before `connect` with the resolved remote address, `raddr.String()`: `ip:port`, `[ipv6]:port`, `[ipv6%zone]:port`; a host name never reaches it | `$(go env GOROOT)/src/net/sock_posix.go:91-99` (go1.27.0); Security measured it in step 3 | confirmed |
| 7 | `syscall.BindToDevice(fd int, device string) error` exists in the standard library on Linux; no new dependency | `src/syscall/syscall_linux.go:844` (go1.27.0) | confirmed |
| 8 | The repository is Linux-only, so no build constraint is needed | `internal/sysstats/clock.go` uses `syscall.Adjtimex` without a build tag | confirmed |
| 9 | An unprivileged process may set `SO_BINDTODEVICE` on a socket not yet bound to a device (Linux 5.7 and later); the agent has no `CAP_NET_RAW` (`CapabilityBoundingSet=CAP_SYS_PTRACE`) | Security measured it unprivileged in step 3; AC4 repeats it under the shipped unit; the server's `uname -r` goes into the follow-up issue | confirmed |
| 10 | Binding a socket to a device that does not exist fails with `ENODEV` before any packet is sent | Security measured it in step 3; the AC2 test asserts `errors.Is(err, syscall.ENODEV)` | confirmed |
| 11 | IPv4: with `SO_BINDTODEVICE`, the route lookup skips next hops on other devices, and with no route on the device the kernel assumes the destination on-link there | Security measured it in step 3 (IPv4); AC4 (b') repeats it on systemd 249 with a provider route present | confirmed |
| 12 | A dial error, whatever its errno, reaches the sender only as `Result{Reason: "connection failed"}`; no error text leaves `Send` | `classifyFailure`, `transport.go` (not a timeout, not a TLS error → `reasonConnection`) | confirmed |
| 13 | Issue AC "while it is down, a send fails at once" | holds only when `tailscale0` is absent (tailscaled stopped, a boot before tailscaled started); while tailscaled runs without routes (before its network map, logged out) the packet goes into the TUN device and the send ends as `timeout` after 30 s; neither reaches the provider's network; the criteria state both | refuted |
| 14 | `tailscaled` removes `tailscale0` when it stops and keeps it while logged out | to verify: Product Manager on the server (`ip link show tailscale0` after `systemctl stop tailscaled`, printed by the egress line), follow-up issue; affects only which failure of fact 13 appears | unverified |
| 15 | Existing sender and agent tests dial loopback only; the agent tests with `http://100.64.0.1:8081` use a fake transport | `transport_test.go:28-86`, `agent_test.go:50, 2457-2490` | confirmed |
| 16 | The CI job `agent-install` runs on `ubuntu-22.04` with systemd 249, as root, against `https://100.64.0.1:8081` | `.github/workflows/ci.yml:271-273`; `check-agent-install.sh:22-24` | confirmed |
| 17 | The CI runner's network is managed by `systemd-networkd` | to verify: Tester, the AC4 case prints `systemctl is-active systemd-networkd` and fails unless `active` (then the Lead decides) (basis: GitHub's image uses netplan with networkd, from memory) | unverified |
| 18 | IPv6: a socket bound to a device with no route there never leaves through another device | to verify: Tester, AC4 IPv6 sub-case (provider route `fd7a:115c:a1e0::/48` on `vx-up`, none on `tailscale0`) (basis: `ip6_route_output` honours `flowi6_oif`, from memory) | unverified |
| 19 | `systemctl set-property --runtime vandox-agent IPAccounting=yes` on the running unit counts its packets from then on, and `systemctl show -P IPEgressPackets` reads the count | to verify: Tester, AC4: the count rises in (b) (positive control) and stays at 0 in (a) and (c) (basis: systemd.resource-control, from memory) | unverified |
| 20 | `backend.url` accepts host names; the unit allows localhost, so the agent reaches the `systemd-resolved` stub `127.0.0.53`, which asks the server's own (the provider's) name servers, Tailscale's DNS being off; `install.sh` does not warn about a `127.x` name server; an answer `127.0.0.1` or `::1` meets the loopback exemption | `internal/config/agent.go:509-513` (`validHost`); `vandox-agent.service:52`; `install.sh:345-366`; README step 3 (lines 94-120), line 244 ("Use the address, not a host name") | confirmed |
| 21 | `tcpdump` is not among the server's documented tools (`curl`, `sha256sum`, `tar`, `gzip`) | `deploy/agent/README.md:241`; whether it is installed: the question goes into the follow-up issue; the egress check does not use it | confirmed |

## Acceptance criteria

- [ ] AC1: Every connection the transport dials to an address that is not a loopback address (`127.0.0.0/8`, `::1`, an IPv4-mapped
  loopback) is bound to the device `tailscale0` before it connects; a connection to a loopback address is not bound. An address the
  check cannot parse is bound.
- [ ] AC2: When the device does not exist, a send to a non-loopback backend fails before any packet is sent, with
  `Result{Reason: "connection failed"}`, and the dial error wraps `syscall.ENODEV`; the IPv6 sub-case is skipped with a reason when
  an `AF_INET6` socket cannot be created (`EAFNOSUPPORT`).
- [ ] AC3: A send to a loopback backend succeeds while the transport's device does not exist.
- [ ] AC4: (CI, systemd 249) the installed agent under the shipped unit, with `backend.url` `https://100.64.0.1:8081` and a provider
  stand-in route for `100.64.0.0/10` on the networkd-managed link `vx-up` (`ip route show 100.64.0.0/10` names `dev vx-up` and
  `proto static`). Each state first runs the positive controls (an unbound probe from the host namespace outside the unit reaches the
  listener the state names, its log is cleared afterwards), then restarts the agent and asserts:
  (a) no `tailscale0`: the provider listener records the unbound probe; the agent's journal shows `backend not accepting batches`
  with `reason="connection failed"` within 15 s; the provider listener accepts nothing; the unit's `IPEgressPackets` stays 0 over
  20 s;
  (b') `tailscale0` stand-in, no table-52 route or rule: an unbound probe reaches the provider listener, a probe bound to `tailscale0`
  reaches the tailnet listener; the provider listener accepts nothing from the agent; the tailnet listener's result is printed,
  not asserted; the same holds after `networkctl reconfigure vx-up` and a carrier flap of `vx-up`;
  (b) plus the table-52 route and rule pref 5270: `ip route get 100.64.0.1` names `dev tailscale0`, the unbound probe and the agent
  reach the tailnet listener, the provider's accepts nothing, `IPEgressPackets` rises;
  (c) the stand-in removed, `networkctl reconfigure vx-up` and a carrier flap: (a) holds again;
  IPv6: with `backend.url` `http://[fd7a:115c:a1e0::1]:8081`, the provider route `fd7a:115c:a1e0::/48` on `vx-up` and no route on
  `tailscale0`, the provider listener accepts nothing from the agent with and without the stand-in, after an unbound probe reached it.
- [ ] AC5: `deploy/agent/README.md` and the Network and security model area describe the binding instead of the residual, state the
  host-name residual of fact 20, and the README's *Checks on the server* carries the server rows (route while up, egress while
  tailscaled is stopped) marked "not yet run" with the follow-up issue; the documented egress check is one line that starts
  `tailscaled` again whatever failed and uses only `systemctl`, `ip`, `sleep` and `setsid`.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestNeedsDevice_FalseOnlyForLoopback` (table below); `TestHTTPTransport_BindsToTheTailnetDevice` (a new transport's `device` is `"tailscale0"`) | `internal/sender/transport_test.go` |
| AC2 | `TestHTTPTransport_FailsAtOnceWithoutTheDevice` (`device` = `"vxnodev0"`, `Send` to `http://100.64.0.1:8081` and to `http://[fd7a:115c:a1e0::1]:8081` → `connection failed`; `dialContext` error `errors.Is(err, syscall.ENODEV)`; a 1-minute context, so a timeout result fails the test; the IPv6 sub-test skips with a reason on `EAFNOSUPPORT` from `syscall.Socket(AF_INET6, …)`) | `internal/sender/transport_test.go` |
| AC3 | `TestHTTPTransport_LeavesLoopbackUnbound` (`device` = `"vxnodev0"`, `httptest` server answering `200` → `Acknowledged`, IPv4; IPv6 `[::1]` when the host has it, else skipped with a reason) | `internal/sender/transport_test.go` |
| AC4 | case "tailnet device" | `.github/scripts/check-agent-install.sh` (CI only) |
| AC5 | `TestReadme_DescribesTheTailnetDeviceBinding` (the Tailscale section names `tailscale0`, no longer holds `is not closed here`, names the host-name residual; the network area's old residual bullet is gone); `TestReadme_EgressCheckStartsTailscaleOnEveryFailure` (runs the documented line with stubs, cases below) | `deploy/agent/readme_test.go` |

`TestNeedsDevice_FalseOnlyForLoopback` cases (the forms `Control` can receive, fact 6, plus malformed ones): `127.0.0.1:8081`,
`127.255.255.254:1`, `[::1]:8081`, `[::ffff:127.0.0.1]:8081` → false; `100.64.0.1:8081`, `[fd7a:115c:a1e0::1]:8081`,
`192.0.2.1:443`, `0.0.0.0:8081`, `[::]:8081`, `[fe80::1%eth0]:8081`, `localhost:8081`, `127.0.0.1` (no port), `""` → true.
The device name `vxnodev0` must not exist on the test host; with an absent device `setsockopt` fails before `connect`, so no test
sends a packet to a non-loopback address (`docs/UNIT_TESTS.md`, loopback only).

The documented egress check (*Approach*), held by `TestReadme_EgressCheckStartsTailscaleOnEveryFailure` (the line from the README's
fenced block, run by bash with stubs for `setsid`, `ip`, `systemctl`, `sleep` on `PATH`, CRLF normalized first): (a) all lines pasted
at once and (b) one at a time are the same, it is one line; (c) a failing step — `systemctl set-property` or `systemctl stop`
failing — still runs `systemctl start tailscaled`; (d) a lost variable — `systemctl show` printing nothing or `[not set]` — writes
the failure to the output file and still starts `tailscaled`. Each case asserts the stub log ends with `systemctl start tailscaled`.
Rows that run in CI only: AC4; the Tester's local smoke substitute: `bash -n` and `shellcheck` on `check-agent-install.sh`.

## Approach

- **Transport.** `NewHTTPTransport` sets `device: tailnetDevice`. `dialContext` without a test hook dials with
  `net.Dialer{Control: bindControl(t.device)}`. `bindControl` returns a function that, when `needsDevice(address)`, calls
  `syscall.BindToDevice` on the raw descriptor inside `c.Control` and returns that error (or the `Control` error); for a loopback
  address it does nothing. `needsDevice` splits host and port with `net.SplitHostPort`, parses the host with `netip.ParseAddr`
  (zones accepted) and returns false only for `addr.Unmap().IsLoopback()`; every error returns true (fail closed). The `uintptr`
  descriptor is converted with a bounds check or a `#nosec G115` comment with its reason, as `internal/logship/safeopen.go:154` does
  (Code Officer's call). No new log line, reason or option.
- **CI case "tailnet device"** (Tester), after "first install" while the agent runs, before "uninstall". Namespaces `vx-provider`
  and `vx-tailnet`, each with `100.64.0.1/32`, `fd7a:115c:a1e0::1/128` (`nodad`) and a Python accept-and-record listener on 8081 for
  both families (`start_listener`'s pattern, `ip netns exec`). Veth `vx-up`/`vx-up-peer` to `vx-provider`, configured by a networkd
  file in `/run/systemd/network/` (an address and `Route` entries for `100.64.0.0/10` and `fd7a:115c:a1e0::/48`), `networkctl
  reload`; `vx-provider` gets a return route to `vx-up`'s address. `systemctl is-active systemd-networkd` must print `active`. The
  stand-in: veth `tailscale0`/`vx-ts-peer` to `vx-tailnet`, host side `100.100.0.1/32` and `fd7a:115c:a1e0:ffff::1/128`, return
  routes for both in `vx-tailnet`; the job prints `ip route get 100.64.0.1 oif tailscale0` and `ip route show 100.64.0.0/10`. Probes
  are a Python `socket.create_connection` script as `ipcheck.py` (`check-agent-install.sh:918-926`), run as root outside the unit,
  with an optional `SO_BINDTODEVICE` argument. `IPAccounting=yes` is set with `systemctl set-property --runtime` and reverted in the
  cleanup. States in the order (a), (b'), (b), (c), then the IPv6 sub-case with `backend.url` changed in `/etc/vandox/agent.yaml`
  and restored afterwards. If IPv6 is disabled on the runner, the case enables it for its own links and fails if it cannot (no
  skip). Everything it creates is removed in the script's cleanup. Its printed results go into the README's CI paragraph (*Unit*).
- **Documented server checks** (Dev, README *Checks on the server* and step 5 of *Tailscale on the monitored server*):
  `ip route get 100.64.0.1` with Tailscale up must name `dev tailscale0`; the egress check is one root line of the existing
  `setsid -f bash -c "…"` pattern (README step 4's route probe): `systemctl set-property --runtime vandox-agent IPAccounting=yes`,
  `systemctl stop tailscaled`, read `IPEgressPackets`, `ip link show tailscale0`, wait 60 s, read it again, `systemctl start
  tailscaled` in a position no failure skips (`;`, never `&&`), then write both counts (or the failure, when a count is not a number)
  and the link line to `/root/vandox-egress.txt` and set `IPAccounting=no` again. Expected: both counts equal, `tailscale0` absent,
  the agent's journal shows `connection failed` in that window and sends again afterwards. The count covers every interface without a
  packet capture tool (fact 21). A boot is covered by the same mechanism and the existing reboot check; `netplan apply` and a carrier
  flap are not run on production (AC4 covers them). Layout of these rows: the existing table's columns; text free for the Dev,
  approved in step 9.
- **Early draft PR:** AC4 is CI-only and facts 5, 17, 18, 19 rest on it, so the orchestrator opens the draft PR after step 7 and
  Security and the Reviewer read the CI result of the case in step 8.
- **Pull request:** it closes #337 (`Closes #337`). Its description names the deviation from the issue's criterion (fact 13: while
  tailscaled runs without routes a send ends as a timeout, not at once; neither reaches the provider's network) and the follow-up
  issue for the server checks.

## Options considered

1. `SO_BINDTODEVICE` on the sender's sockets (chosen) — no route, networkd or firewall state, fails closed, no new dependency.
2. An nftables table rejecting the agent user's packets except on `lo` and `tailscale0`, loaded by `ExecStartPre=+` — needs `nft` on
   the server, a root step in the unit, uninstall cleanup of a numeric UID, and fails open on a ruleset flush while the agent runs.
3. An `unreachable` route in a drop-in for the uplink's `.network` or in netplan — edits the provider's network configuration,
   depends on the uplink's name and the network manager, untestable here.
4. `ManageForeignRoutes=no` plus a oneshot route unit — changes networkd's handling of every foreign route on the host.
5. `RestrictNetworkInterfaces=` — systemd 250; the server runs 249 (`TestService_UsesOnlyKeysKnownToSystemd249`).
6. A configuration option for the device name — serves only a non-default TUN name; such a setup fails closed instead.

For the server's egress check: the unit's IP accounting (chosen) against `tcpdump` on the uplink, which is not a documented tool of
the server (fact 21) and needs the uplink's name. Details in record 0134.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| agent (Go) | `internal/sender/transport.go` | `tailnetDevice`, `HTTPTransport.device`, `dialContext` with `Control`, `bindControl`, `needsDevice` |
| agent tests | `internal/sender/transport_test.go`, `deploy/agent/readme_test.go` | new tests (Tester) |
| CI | `.github/scripts/check-agent-install.sh` | case "tailnet device" (Tester) |
| docs | README, areas, ARCHITECTURE, SECURITY, `.squad/project.md` | *Documentation updates* (Dev) |

## Signatures (for the Dev's skeleton)

```go
// tailnetDevice is Tailscale's network interface on Linux; every connection to an address that is not a loopback address is bound to it.
const tailnetDevice = "tailscale0"

// HTTPTransport: new field
	// device is the interface every non-loopback connection is bound to; tailnetDevice, only tests change it.
	device string

// bindControl returns the dialer's Control function: it binds the socket to device before connect unless needsDevice(address) is false.
func bindControl(device string) func(network, address string, c syscall.RawConn) error

// needsDevice reports whether a connection to address (host:port with an IP literal, as net.Dialer passes it) must be bound:
// false only for a loopback address; an address it cannot parse is bound.
func needsDevice(address string) bool
```

`dialContext` keeps its signature; its body changes. `transport.go` is the only file the skeleton rewrites.

## Test files

`internal/sender/transport_test.go`, `deploy/agent/readme_test.go`, `.github/scripts/check-agent-install.sh`.

Existing test call sites of a changed signature: none (no signature changes; `useDial` still bypasses `dialContext`).
`TestHTTPTransport_BuiltWithoutProxyAndRedirects` stays as is (no input it checks changes).

Files no unit test can cover: none (with an absent device both branches of `bindControl` run in the tests).

## Areas

- Network and security model: the bullet *Residual while Tailscale is not up* becomes the binding (what it does, the two failure
  modes of fact 13, TUN mode with the default name required) and the host-name residual (fact 20); the bullet *The agent's unit
  enforces this on the server* names the binding as the first layer and the allow-list as the second, and no longer claims that a
  host name fails when the name server is the local stub; record 0134.
- Agent: *Connection* (the binding and loopback exemption, which a forged name answer can reach), *Rights* → *Sender* (no capability
  needed, Linux 5.7 or later), *Installation* (the residual paragraph at the end replaced; still no ordering on `tailscaled`).

## Documentation updates

Sweep: `#337`, `is not closed`, `residual`, `IPAddressAllow`, `tailnet`, `host name`, `name server` in `docs/areas/`,
`docs/ARCHITECTURE.md`, `SECURITY.md`, `README.md`, `deploy/agent/README.md`, `.squad/project.md`. All Dev, against the code as built:

- `deploy/agent/README.md`: lines 9-10 and *What stays open while Tailscale is not up* (lines 15-21) → the binding, its two failure
  modes and the host-name residual (the guarantee holds only with the tailnet address, as line 244 asks); step 5's bullet
  (lines 230-233) without the residual; *The name server* (lines 402-405) and the `RestrictAddressFamilies=` bullet (line 602): a
  host name resolved through the local stub is not blocked, and a forged loopback answer reaches the exemption; the unit table row
  for `IPAddressAllow` (line 686) as the second layer; *Checks on the server*: the two rows of *Approach*, "not yet run (issue
  #<follow-up>)"; the CI paragraph of *Unit* (lines 724-727) with the AC4 results.
- `docs/areas/network-and-security-model.md` lines 53-59 and 66-70; `docs/areas/agent.md` lines 48-60, 2002-2005, 2163-2170.
- `docs/ARCHITECTURE.md` line 458: the sender binds its connections to `tailscale0` (record 0134), beside `IPAddressAllow=`.
- `SECURITY.md` line 39-40: the monitored server runs Tailscale in TUN mode with `tailscale0`; the agent sends only through it.
- `.squad/project.md` area 11: the binding with its tests (`TestNeedsDevice_FalseOnlyForLoopback`,
  `TestHTTPTransport_FailsAtOnceWithoutTheDevice`), record 0134 in its list.
- `CHANGELOG.md`: none (entries are written at release, `docs/CONTRIBUTING.md:127`; the agent is unreleased).
- Record 0132's consequence bullet already points to record 0134 (Lead, done).

## Architecture check

Guarantee "the agent's only destination is the ingest port, reached only over the tailnet, never over a public address"
(`.squad/project.md` area 11, records 0006, 0010) is strengthened: with the tailnet address in `backend.url` it now holds while
Tailscale is down. With a host name it still depends on the name answer (fact 20, pre-existing). Outbound-only and no listener:
unchanged. Provider agents and network configuration: untouched. New requirement on the monitored server: Tailscale in TUN mode
with the default interface name, which the documented install already gives.

## Security considerations

- [x] No limits: `needsDevice` parses only the address string the dialer builds.
- [x] Exceptions: every `setsockopt` or `Control` error becomes `connection failed` (fact 12); no errno or device name reaches a log.
- Residuals stated in the area: with a host name in `backend.url`, the lookup goes through the local stub to the provider's name
  servers, unbound, and a forged answer `127.0.0.1` or `::1` sends the token unbound to whatever listens on that loopback port
  (pre-existing, not a regression; the guarantee holds only with the tailnet address); a root user can create an interface named
  `tailscale0` (root already reads the token).

## Decision records

- `docs/decisions/0134-sender-binds-non-loopback-connections-to-the-tailnet-device.md` (Proposed)
- `docs/decisions/0132-…` (unreleased, edited in place: its consequence points to 0134)

## Out of scope / follow-ups

- Follow-up issue (orchestrator): title "[Ops] Run the tailnet egress checks of #337 on the production server"; body: run the two new
  rows of `deploy/agent/README.md` *Checks on the server* (`ip route get 100.64.0.1` names `tailscale0` while Tailscale is up; the
  egress line shows equal `IPEgressPackets` counts while tailscaled is stopped and the agent logs `connection failed`), record
  `uname -r`, `systemctl is-active systemd-networkd NetworkManager`, `ip link show tailscale0` after the stop (facts 4, 9, 14), and
  answer: is `tcpdump` installed on the server, and may it be? Results go into the README; Refs #337, #338.
- Follow-up issue (orchestrator): title "[Agent] Keep the token off a loopback listener reached through a forged name answer"; body:
  `backend.url` accepts host names (`internal/config/agent.go:509-513`); resolved through the `127.0.0.53` stub and the provider's
  name servers, a forged `127.0.0.1`/`::1` answer meets the sender's loopback exemption (record 0134), so the token goes unbound to a
  local listener; options: reject host names in `backend.url`, or exempt loopback only for a literal loopback host; Refs #337.
- An install-time check that `tailscale0` exists: not needed for the control (it fails closed); not added.

## Challenge

- M1 (accepted): AC4 gains state (b') — stand-in present, no table-52 route or rule, provider route present, agent restarted — before
  (b); the provider listener must accept nothing, the tailnet result is printed; repeated after `networkctl reconfigure` and a
  carrier flap.
- M2 (accepted): every state runs positive controls first (unbound probe from the host namespace, as `ipcheck.py`), clears the log,
  then asserts the agent; `ip route show 100.64.0.0/10` must name `dev vx-up` and `proto static`; in (b') the tailnet stand-in is
  checked with a probe bound to `tailscale0` (an unbound probe goes to the provider there), in (b) with an unbound one.
- M3 (accepted, no escalation): the egress check uses the unit's IP accounting through `systemctl` instead of `tcpdump`, needs no
  uplink name and counts every interface; the `tcpdump` question goes into the follow-up issue (fact 21).
- m4 (accepted): AC2's IPv6 sub-test skips with a reason on `EAFNOSUPPORT`.
- m5 (accepted): the PR closes #337 and names the fact-13 deviation and the follow-up issue (*Approach*, *Pull request*).
- m6 (accepted): host-side `tailscale0` gets `100.100.0.1/32`, `vx-tailnet` a return route, the job prints
  `ip route get 100.64.0.1 oif tailscale0`.

## Security review

- B1 (accepted): state (b') as M1; an IPv6 sub-case with `backend.url` `http://[fd7a:115c:a1e0::1]:8081`, the provider route
  `fd7a:115c:a1e0::/48` on `vx-up` and none on `tailscale0` (fact 18, unverified until the CI run; the case fails rather than
  skips); the stand-in's addresses and return routes as m6, for both families.
- B2 (accepted): the name-lookup residual is restated in plan, record 0134 and the area documents (fact 20): a forged loopback answer
  sends the token unbound to a local listener; pre-existing; the guarantee holds only with the tailnet address. A second follow-up
  issue proposes closing it.
