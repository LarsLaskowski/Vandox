# 0134: The sender binds every non-loopback connection to the tailnet device, so the token never leaves through another interface

- **Status:** Proposed
- **Date:** 2026-11-04
- **Area:** Network and security model
- **Source:** Issue #337
- **Supersedes:** —

## Context

The agent sends its token and its batches in plain `http` to the backend's tailnet address. Tailscale's routes exist only while it
runs; while it is down, a connection to a `100.x` address follows the server's default route onto the hosting provider's network, and
the unit's `IPAddressAllow=` cannot stop it because it filters by address, not by interface (record 0131). Whoever answers there gets
the token and the batch and can make the agent drop or give up those records. Record 0132 split this exposure off into issue #337.
The monitored server runs systemd 249 with `systemd-networkd`, which removes foreign routes on every link configure, reconfigure and
carrier loss, and `RestrictNetworkInterfaces=` needs systemd 250. The provider's network configuration is not ours to change
(record 0026 for the provider's agents, by the same reasoning).

## Options considered

1. **Bind the sender's sockets to `tailscale0` with `SO_BINDTODEVICE`** (chosen) — the kernel then routes the connection only through
   that device, whatever the routing tables hold, so no route, networkd or firewall state is involved; a missing device fails the
   connect at once. Needs no capability from Linux 5.7 on and no new dependency (`syscall.BindToDevice`). Loopback stays unbound for
   a backend on the same host. A non-default TUN name or Tailscale's userspace networking on the server makes every send fail.
2. **An nftables table that rejects the agent user's packets on any interface but `lo` and `tailscale0`, loaded by the unit** — no
   code change, but it needs the `nft` tool on the server, a root `ExecStartPre=+` in the unit, cleanup on uninstall (the rule names a
   numeric UID that a purge frees), and a ruleset flush by another tool while the agent runs removes it without notice.
3. **An `unreachable` route for the tailnet ranges in a drop-in for the uplink's `.network`, or in netplan** — survives networkd's
   cleanup, but edits the provider's network configuration, depends on the uplink's name and the network manager, and cannot be
   tested without the server.
4. **`ManageForeignRoutes=no` with a oneshot unit that adds the routes** — changes how networkd treats every foreign route on the
   host, the provider's included.
5. **`RestrictNetworkInterfaces=lo tailscale0`** — the server's systemd 249 ignores the key.
6. **A configuration option for the device name** — serves only a non-default TUN name, which the documented setup does not create;
   left out, and such a setup fails closed with `connection failed`.

## Decision

Option 1: `HTTPTransport` binds every connection whose resolved address is not a loopback address to `tailscale0` before it connects,
and the unit's address allow-list stays as a second layer. The behavior, the failure modes and the server checks are in the
[Network and security model](../areas/network-and-security-model.md) area and the [Agent](../areas/agent.md) area (*Connection*).

## Consequences

- While Tailscale is down, a send never leaves through the provider's network: without the device it fails at once as
  `connection failed`; with the device but without routes (before the network map, logged out) the packets go into Tailscale and the
  send ends as a timeout. Either way the records stay spooled.
- The monitored server must run Tailscale in TUN mode with the default interface name; Tailscale's userspace networking is not
  supported there.
- The guarantee holds only with the tailnet address in `backend.url`, which the documented setup asks for. A host name is resolved
  through the local `systemd-resolved` stub, which the unit's `localhost` allows and which asks the provider's name servers; a forged
  answer `127.0.0.1` or `::1` meets the loopback exemption and sends the token unbound to whatever listens on that local port. This
  predates the binding; a follow-up issue proposes closing it.
- The control was measured on the CI runner's systemd 249 with stand-in links, for IPv4 and IPv6, including a stand-in without
  routes while a provider route covers the tailnet range; the run on the production server is the maintainer's (follow-up issue).
- The server's egress check reads the unit's IP accounting (`IPEgressPackets`) while `tailscaled` is stopped, not a packet capture
  on the uplink: `tcpdump` is not a documented tool of the server, and the count covers every interface without the uplink's name.
