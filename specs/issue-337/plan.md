# Plan: Keep the agent's token off the provider's network while Tailscale is down

Source: Issue #337
Status: Draft
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
| 3 | Issue: a 2xx is acknowledged, a 400/413/422 with the refusal header is split and given up | `transport.go:65-66`, `sender.go:596-600` (`acknowledge`, `split`), `626-628` (`giveUp`), `644-645` (`Ack`); the issue's line references match `eb91203` | confirmed |
| 4 | Issue: Tailscale's routes live only in table 52 (rule pref 5270) and vanish when it stops or logs out | to verify: Product Manager on the server (`ip rule`, `ip route show table 52` with Tailscale up and stopped), follow-up issue; not relied on (basis: #43's measurement and Security's reading; not checkable in the session. The chosen control does not rest on it) | unverified |
| 5 | Issue: networkd (v249) removes foreign routes on configure, reconfigure and carrier loss | to verify: the CI case (AC4, Tester) shows the binding unaffected by `networkctl reconfigure` and a carrier flap of a networkd-managed stand-in link (basis: issue's reading of `networkd-route.c`, `networkd-link.c`; decides only against options 3 and 4; not relied on by the chosen option) | unverified |
| 6 | Go's dialer calls `Control` before `connect` with the resolved remote address, `raddr.String()`: `ip:port`, `[ipv6]:port`, `[ipv6%zone]:port`; a host name never reaches it | `$(go env GOROOT)/src/net/sock_posix.go:91-99` (go1.27.0) | confirmed |
| 7 | `syscall.BindToDevice(fd int, device string) error` exists in the standard library on Linux; no new dependency | `src/syscall/syscall_linux.go:844` (go1.27.0) | confirmed |
| 8 | The repository is Linux-only, so no build constraint is needed | `internal/sysstats/clock.go` uses `syscall.Adjtimex` without a build tag | confirmed |
| 9 | An unprivileged process may set `SO_BINDTODEVICE` on a socket not yet bound to a device (Linux 5.7 and later); the agent has no `CAP_NET_RAW` (`CapabilityBoundingSet=CAP_SYS_PTRACE`) | to verify: Tester, the CI case of AC4 (the installed agent reaches the `tailscale0` stand-in under the shipped unit), Early draft PR before step 8; the server's kernel (`uname -r`, Ubuntu 22.04: 5.15 or later) in the follow-up issue (basis: kernel commit "net: core: enable SO_BINDTODEVICE for non-root users" (5.7), from memory; the session runs as root and cannot show it) | unverified |
| 10 | Binding a socket to a device that does not exist fails with `ENODEV` before any packet is sent | to verify: Tester, step 5, the AC2 test asserts `errors.Is(err, syscall.ENODEV)` (basis: kernel `sock_setbindtodevice` → `dev_get_by_name`, from memory) | unverified |
| 11 | With `SO_BINDTODEVICE`, the route lookup skips next hops on other devices, and with no route on the device the kernel assumes the destination on-link there; a packet never leaves through another interface | to verify: Tester, the CI case of AC4 (a more specific route for `100.64.0.0/10` via the provider stand-in exists, and its listener accepts nothing) (basis: kernel `fib_table_lookup` oif check, `ip_route_output_key_hash_rcu` on-link fallback, from memory) | unverified |
| 12 | A dial error, whatever its errno, reaches the sender only as `Result{Reason: "connection failed"}`; no error text leaves `Send` | `classifyFailure`, `transport.go` (not a timeout, not a TLS error → `reasonConnection`) | confirmed |
| 13 | Issue AC "while it is down, a send fails at once" | holds with the chosen control only when `tailscale0` is absent (tailscaled stopped, after a boot before tailscaled started); while tailscaled runs without routes (before its network map, logged out) the packet goes into the TUN device and the send ends as `timeout` after 30 s; the plan's criteria state both cases; neither reaches the provider's network | refuted |
| 14 | `tailscaled` removes `tailscale0` when it stops and keeps it while logged out | to verify: Product Manager on the server (`ip link show tailscale0` after `systemctl stop tailscaled`), follow-up issue; affects only which of the two failures of fact 13 appears (basis: Tailscale's Linux TUN handling, from memory) | unverified |
| 15 | Existing sender and agent tests dial loopback only (`httptest`, `rawServer` on `127.0.0.1`); the agent tests with `http://100.64.0.1:8081` use a fake transport | `transport_test.go:28-86`, `agent_test.go:50, 2457-2490` | confirmed |
| 16 | The CI job `agent-install` runs on `ubuntu-22.04` with systemd 249, as root, against `https://100.64.0.1:8081` | `.github/workflows/ci.yml:271-273`; `check-agent-install.sh:22-24` and its case "verify on 249" | confirmed |
| 17 | The CI runner's network is managed by `systemd-networkd` | to verify: Tester, the AC4 case prints `systemctl is-active systemd-networkd` and fails if it is not `active` (then the Lead decides) (basis: GitHub's `ubuntu-22.04` image uses netplan with networkd, from memory) | unverified |

## Acceptance criteria

- [ ] AC1: Every connection the transport dials to an address that is not a loopback address (`127.0.0.0/8`, `::1`, an IPv4-mapped
  loopback) is bound to the device `tailscale0` before it connects; a connection to a loopback address is not bound. An address the
  check cannot parse is bound.
- [ ] AC2: When the device does not exist, a send to a non-loopback backend fails before any packet is sent, with
  `Result{Reason: "connection failed"}`, and the dial error wraps `syscall.ENODEV`.
- [ ] AC3: A send to a loopback backend succeeds while the transport's device does not exist.
- [ ] AC4: (CI, systemd 249) the installed agent, with `backend.url` `https://100.64.0.1:8081` and a provider stand-in route for
  `100.64.0.0/10` on a networkd-managed link: (a) without `tailscale0` the journal shows `backend not accepting batches` with
  `reason="connection failed"` within 15 s of the start and the provider stand-in's listener accepts nothing; (b) with a `tailscale0`
  stand-in (table 52 route, rule pref 5270) `ip route get 100.64.0.1` names `dev tailscale0`, the tailnet stand-in's listener accepts
  the agent's connection and the provider's accepts nothing; (c) after removing the `tailscale0` stand-in, `networkctl reconfigure` of
  the provider stand-in link and a carrier flap of it (down, up), (a) holds again.
- [ ] AC5: `deploy/agent/README.md` and the Network and security model area describe the binding instead of the residual, and the
  README's *Checks on the server* carries the server rows (route while up, egress while tailscaled is stopped) marked
  "not yet run" with the follow-up issue; the documented egress check is one line that starts `tailscaled` again whatever failed.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestNeedsDevice_FalseOnlyForLoopback` (table below); `TestHTTPTransport_BindsToTheTailnetDevice` (a new transport's `device` is `"tailscale0"`) | `internal/sender/transport_test.go` |
| AC2 | `TestHTTPTransport_FailsAtOnceWithoutTheDevice` (`device` = `"vxnodev0"`, `Send` to `http://100.64.0.1:8081` and to `http://[fd7a:115c:a1e0::1]:8081` → `connection failed`; `dialContext` error `errors.Is(err, syscall.ENODEV)`; a 1-minute context, so a timeout result fails the test) | `internal/sender/transport_test.go` |
| AC3 | `TestHTTPTransport_LeavesLoopbackUnbound` (`device` = `"vxnodev0"`, `httptest` server answering `200` → `Acknowledged`, IPv4; IPv6 `[::1]` when the host has it, else skipped with a reason) | `internal/sender/transport_test.go` |
| AC4 | case "tailnet device" | `.github/scripts/check-agent-install.sh` (CI only) |
| AC5 | `TestReadme_DescribesTheTailnetDeviceBinding` (the Tailscale section names `tailscale0` and no longer holds `is not closed here`; the network area's residual bullet is gone); `TestReadme_EgressCheckStartsTailscaleOnEveryFailure` (runs the documented line with stubs, cases below) | `deploy/agent/readme_test.go` |

`TestNeedsDevice_FalseOnlyForLoopback` cases (the forms `Control` can receive, fact 6, plus malformed ones): `127.0.0.1:8081`,
`127.255.255.254:1`, `[::1]:8081`, `[::ffff:127.0.0.1]:8081` → false; `100.64.0.1:8081`, `[fd7a:115c:a1e0::1]:8081`,
`192.0.2.1:443`, `0.0.0.0:8081`, `[::]:8081`, `[fe80::1%eth0]:8081`, `localhost:8081`, `127.0.0.1` (no port), `""` → true.
The device name `vxnodev0` (8 bytes, under `IFNAMSIZ`) must not exist on the test host; with an absent device `setsockopt` fails
before `connect`, so no test sends a packet to a non-loopback address (`docs/UNIT_TESTS.md`, loopback only).

The documented egress check (*Approach*) and its cases, held by `TestReadme_EgressCheckStartsTailscaleOnEveryFailure` (the line
from the README's fenced block, run by bash with stubs for `setsid`, `ip`, `tcpdump`, `systemctl`, `sleep` on `PATH`, CRLF
normalized first): (a) all lines pasted at once and (b) one at a time are the same, it is one line; (c) a failing step — `tcpdump`
missing or exiting non-zero, `systemctl stop` failing — still runs `systemctl start tailscaled`; (d) a lost variable — `ip` printing
no default route, so the uplink is empty — writes the failure to the output file and still starts `tailscaled`. Each case asserts the
stub log ends with `systemctl start tailscaled`. Rows that run in CI only: AC4. The Tester's local smoke substitute for it: `bash -n`
and `shellcheck` on `check-agent-install.sh`.

## Approach

- **Transport.** `NewHTTPTransport` sets `device: tailnetDevice`. `dialContext` without a test hook dials with
  `net.Dialer{Control: bindControl(t.device)}`. `bindControl` returns a function that, when `needsDevice(address)`, calls
  `syscall.BindToDevice` on the raw descriptor inside `c.Control` and returns that error (or the `Control` error); for a loopback
  address it does nothing. `needsDevice` splits host and port with `net.SplitHostPort`, parses the host with `netip.ParseAddr`
  (zones accepted) and returns false only for `addr.Unmap().IsLoopback()`; every error returns true (fail closed). The `uintptr`
  descriptor is converted with a bounds check or a `#nosec G115` comment with its reason, as `internal/logship/safeopen.go:154` does
  (Code Officer's call). No new log line, reason or option: a missing device is `connection failed` in the existing warning
  `backend not accepting batches`.
- **CI case "tailnet device"** (Tester), after "first install" while the agent runs, before "uninstall": two network namespaces,
  `vx-provider` and `vx-tailnet`, each with `100.64.0.1/32` and a Python accept-and-record listener on port 8081 (the pattern of
  `start_listener`, run with `ip netns exec`). A veth pair `vx-up`/`vx-up-peer` to `vx-provider`, configured by a networkd file in
  `/run/systemd/network/` (address and `Route` for `100.64.0.0/10`), `networkctl reload`; `systemctl is-active systemd-networkd` must
  print `active`. Then (a); then a veth pair named `tailscale0` to `vx-tailnet`, `ip route add 100.64.0.0/10 dev tailscale0 table 52`,
  `ip rule add pref 5270 lookup 52`, `systemctl restart vandox-agent` and (b), reading the listeners' logs and `ip route get`; then the
  stand-in removed, `networkctl reconfigure vx-up`, `ip link set vx-up down`, `up`, a restart and (c). Everything it creates is
  removed in the script's cleanup. Its results (the lines it printed) go into the README's CI paragraph (*Unit*).
- **Documented server checks** (Dev, README *Checks on the server* and step 5 of *Tailscale on the monitored server*):
  `ip route get 100.64.0.1` with Tailscale up must name `dev tailscale0`; the egress check is one root line of the existing
  `setsid -f bash -c "…"` pattern (README step 4's route probe): find the uplink from the default route, start
  `timeout 75 tcpdump -ni <uplink> -c 1 'dst net 100.64.0.0/10'` in the background with its output in `/root/vandox-egress.txt`,
  `systemctl stop tailscaled`, wait 60 s, `systemctl start tailscaled` in a position no failure skips (`;`, never `&&`), then wait for
  `tcpdump`. Expected: `0 packets captured` in the file, the agent's journal shows `connection failed` in that window and sends again
  afterwards. A boot is covered by the same mechanism (no device until `tailscaled` creates it) and the existing reboot check;
  `netplan apply` and a carrier flap are not run on production (AC4 covers them on the stand-in). Layout of these rows: the existing
  table's columns; text free for the Dev, approved in step 9.
- **Early draft PR:** AC4 is CI-only and fact 9 rests on it, so the orchestrator opens the draft PR after step 7 and Security and the
  Reviewer read the CI result of the case in step 8.

## Options considered

1. `SO_BINDTODEVICE` on the sender's sockets (chosen) — no route, networkd or firewall state, fails closed, no new dependency.
2. An nftables table rejecting the agent user's packets except on `lo` and `tailscale0`, loaded by `ExecStartPre=+` — needs `nft` on
   the server, a root step in the unit, uninstall cleanup of a numeric UID, and fails open on a ruleset flush while the agent runs.
3. An `unreachable` route in a drop-in for the uplink's `.network` or in netplan — edits the provider's network configuration,
   depends on the uplink's name and the network manager, untestable here.
4. `ManageForeignRoutes=no` plus a oneshot route unit — changes networkd's handling of every foreign route on the host.
5. `RestrictNetworkInterfaces=` — systemd 250; the server runs 249 (`TestService_UsesOnlyKeysKnownToSystemd249`).
6. A configuration option for the device name — serves only a non-default TUN name; such a setup fails closed instead.

Details in record 0134.

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
  modes of fact 13, TUN mode with the default name required, unbound name-server queries); the bullet *The agent's unit enforces this
  on the server* names the binding as the first layer and the allow-list as the second; record 0134.
- Agent: *Connection* (the binding and loopback exemption), *Rights* → *Sender* (no capability needed, Linux 5.7 or later),
  *Installation* (the residual paragraph at the end replaced; still no ordering on `tailscaled`).

## Documentation updates

Sweep: `#337`, `is not closed`, `residual`, `IPAddressAllow`, `tailnet` in `docs/areas/`, `docs/ARCHITECTURE.md`, `SECURITY.md`,
`README.md`, `deploy/agent/README.md`, `.squad/project.md`. All Dev, against the code as built:

- `deploy/agent/README.md`: line 9-10 and the paragraph *What stays open while Tailscale is not up* (lines 15-21) → what the binding
  does and its two failure modes; step 5's bullet (lines 230-233) without the residual; the unit table row for `IPAddressAllow`
  (line 686) as the second layer; *Checks on the server*: the two rows of *Approach*, "not yet run (issue #<follow-up>)"; the CI
  paragraph of *Unit* (lines 724-727) with the AC4 results.
- `docs/areas/network-and-security-model.md` lines 53-59 and 66-70; `docs/areas/agent.md` lines 48-60, 2002-2005, 2163-2170.
- `docs/ARCHITECTURE.md` line 458: the sender binds its connections to `tailscale0` (record 0134), beside `IPAddressAllow=`.
- `SECURITY.md` line 39-40: the monitored server runs Tailscale in TUN mode with `tailscale0`; the agent sends only through it.
- `.squad/project.md` area 11: the binding with its tests (`TestNeedsDevice_FalseOnlyForLoopback`,
  `TestHTTPTransport_FailsAtOnceWithoutTheDevice`), record 0134 in its list.
- `CHANGELOG.md`: none (entries are written at release, `docs/CONTRIBUTING.md:127`; the agent is unreleased).
- Record 0132's consequence bullet already points to record 0134 (Lead, done).

## Architecture check

Guarantee "the agent's only destination is the ingest port, reached only over the tailnet, never over a public address"
(`.squad/project.md` area 11, records 0006, 0010) is strengthened: it now holds while Tailscale is down. Outbound-only and no listener:
unchanged. Provider agents and network configuration: untouched (no route, networkd or firewall change). New requirement on the
monitored server: Tailscale in TUN mode with the default interface name, which the documented install already gives.

## Security considerations

- [x] No limits: `needsDevice` parses only the address string the dialer builds.
- [x] Exceptions: every `setsockopt` or `Control` error becomes `connection failed` (fact 12); no errno or device name reaches a log.
- Residuals stated in the area: name-server queries for a host name in `backend.url` are not bound (the name leaks, not the token); a
  root user can create an interface named `tailscale0` (root already reads the token).

## Decision records

- `docs/decisions/0134-sender-binds-non-loopback-connections-to-the-tailnet-device.md` (Proposed)
- `docs/decisions/0132-…` (unreleased, edited in place: its consequence points to 0134)

## Out of scope / follow-ups

- Follow-up issue (orchestrator): title "[Ops] Run the tailnet egress checks of #337 on the production server"; body: run the two new
  rows of `deploy/agent/README.md` *Checks on the server* (`ip route get 100.64.0.1` names `tailscale0` while Tailscale is up; the
  egress line shows `0 packets captured` while tailscaled is stopped and the agent logs `connection failed`), record `uname -r`,
  `systemctl is-active systemd-networkd NetworkManager`, `ip link show tailscale0` after the stop (facts 4, 9, 14), and the results in
  the README; Refs #337, #338.
- An install-time check that `tailscale0` exists: not needed for the control (it fails closed); not added.
