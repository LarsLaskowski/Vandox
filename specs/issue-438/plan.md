# Plan: Legacy lsof parser reads a dotted IPv4 address on an IPv6 socket

Source: Issue #438
Tier: standard — the address guard of the legacy lsof parser accepts one more input form, a behavior change in input handling that creates, changes or removes no security control.
Security areas touched: 10 Parsing of external input (`src/Vandox.Core/LogParsing/LegacyLsofLine.cs`)

## Problem / root cause

`LegacyLsofLine.TryParseAddress` (`src/Vandox.Core/LogParsing/LegacyLsofLine.cs:298-315`) returns from its TYPE `IPv6`
branch at line 311, which accepts only a text in brackets; a dotted quad reaches `TryParseQuad` (line 314) only with
TYPE `IPv4`. `TryParseEndpoint` (lines 266-279) hands a text without a leading `[` to `TryParseAddress` as the part
before the last colon, so `192.0.2.10:8447` with TYPE `IPv6` gives the address text `192.0.2.10`, which line 311
refuses; `Map` (line 176) leaves the reason `socket address not mapped` and the line is skipped.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | With TYPE `IPv6` only `*` or a bracketed IPv6 address is accepted; a dotted quad is read only with TYPE `IPv4` | `LegacyLsofLine.cs:302-314` | confirmed |
| 2 | The issue's line ends in `NotMapped`: it has the right shape (TYPE at index 4, NODE `TCP`, a state) and fails only at the address | traced: `TryParse` 145-159, `Map` 176-190, `TryParseEndpoint` 275-281, `TryParseAddress` 311 | confirmed |
| 3 | lsof prints an IPv4-mapped address of an `AF_INET6` socket as a dotted quad without brackets, with TYPE `IPv6` | the production line quoted in the issue (the Product Manager's real `sysmonitor.log.5.gz`, addresses replaced by documentation ones); lsof's source could not be opened (repository not reachable from the session) and the sandbox has no IPv6 to reproduce it; confirmed by the real sample | confirmed |
| 4 | The agent keeps an IPv4-mapped address of `tcp6`/`udp6` as `::ffff:a.b.c.d`, not unmapped, so `remotes` can hold one peer twice | `docs/areas/wire-format-records.md:271-273`, `docs/areas/agent-connections.md:39-40`, record 0115 *Consequences* | confirmed |
| 5 | A mapped address passes the snapshot validation and round-trips the stored JSON | `Check.Address` (`src/Vandox.Core/Model/Check.cs:262-270`) refuses only a scope; `IpJson.TryParseEndpoint` (`IpJson.cs:71-116`) reads `[::ffff:192.0.2.10]:8447`; the agent already sends such endpoints | confirmed |
| 6 | `IPAddress.MapToIPv6` turns `192.0.2.10` into `::ffff:192.0.2.10`, whose `IPEndPoint` text is `[::ffff:192.0.2.10]:8447` | documented .NET behavior; the existing test `LegacyLsofLineTryParseReadsIpv6WithEmbeddedIpv4` already compares a parsed `::ffff:192.0.2.1`; AC1's test pins the text | confirmed |
| 7 | The existing row `[DataRow(IPv6, Tcp, "1.2.3.4:22" + Listen, LegacyLsofLine.NotMapped)]` pins the old behavior | `tests/Vandox.Core.Tests/LegacyLsofLineTests.cs:211` | confirmed |
| 8 | No other test input of the parser has a dotted quad with TYPE `IPv6` (the fixture's IPv6 lines are `*` or bracketed; `UnmappedSocket` is IPv4 `127.1`) | `testdata/logs/legacy-monitor-new.log:53-67`, `LegacyMonitorParserTests.cs:36` | confirmed |
| 9 | Re-importing an already imported file does not add the rows skipped before | import reports such a file as already imported (`docs/RETIRE_LEGACY_MONITORING.md:154`) | confirmed |

## Acceptance criteria

- AC1: An lsof line with TYPE `IPv6` whose endpoint addresses are dotted quads is read: each such address becomes the
  IPv4-mapped address `::ffff:a.b.c.d`, the protocol is `tcp6` or `udp6`, and port and state are read as for any line.
  The issue's line `autoinsta 181226            root    7u  IPv6 2266160      0t0  TCP 192.0.2.10:8447->198.51.100.20:20530 (ESTABLISHED)`
  gives `tcp6`, local `[::ffff:192.0.2.10]:8447`, remote `[::ffff:198.51.100.20]:20530`, state `ESTABLISHED`, PID 181226,
  command `autoinsta`, and no reason.
- AC2: A dotted address with TYPE `IPv6` is held to the IPv4 octet rule of the guard: `127.1`, `1`, `0x7f.0.0.1`,
  `010.0.0.1`, `2130706433`, `1.2.3.256`, `1.2.3.4.5`, `1.2.3` and an unbracketed `::ffff:1.2.3.4` (as local or remote
  address) are refused with `socket address not mapped`; a bracketed `[1.2.3.4]` stays refused.
- AC3: In a legacy monitoring log, the issue's line in an `lsof` section gives no skip and a complete connection
  snapshot with state `tcp6|ESTABLISHED|1`, the remote `::ffff:198.51.100.20` counted once, and the connection
  `tcp6`, `[::ffff:192.0.2.10]:8447` → `[::ffff:198.51.100.20]:20530`, PID 181226, command `autoinsta`.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | LegacyLsofLineTryParseReadsDottedAddressOfIpv6SocketAsMapped | tests/Vandox.Core.Tests/LegacyLsofLineTests.cs |
| AC2 | LegacyLsofLineTryParseRefusesLenientDottedAddressOfIpv6Socket | tests/Vandox.Core.Tests/LegacyLsofLineTests.cs |
| AC3 | LegacyMonitorParserParseStoresDottedAddressOfIpv6Socket | tests/Vandox.Core.Tests/LegacyMonitorParserTests.cs |

Cases: AC1 is data-driven on whole lines, its first row the issue's line verbatim (runs of spaces kept, the real
sample), then a TCP listener `192.0.2.10:https (LISTEN)` (`tcp6`, `[::ffff:192.0.2.10]:443`, no remote), a UDP socket
`192.0.2.1:5000` (`udp6`, `[::ffff:192.0.2.1]:5000`), a wildcard local with a dotted remote `*:8447->198.51.100.20:20530 (ESTABLISHED)`
(`[::]:8447`, `[::ffff:198.51.100.20]:20530`), and `0.0.0.0:22 (LISTEN)` and `255.255.255.255:65535 (LISTEN)` for the octet bounds;
each row also asserts `Local.Address.IsIPv4MappedToIPv6` (false only for the wildcard row) and the address family
`InterNetworkV6`. AC2 is data-driven like `LegacyLsofLineTryParseRefusesLenientAddressForms`, every row TYPE `IPv6`:
each listed form as local address (`<form>:22 (LISTEN)`), `127.1` and `1.2.3.256` also as remote address
(`192.0.2.1:22-><form>:80 (ESTABLISHED)`), and `[1.2.3.4]:22 (LISTEN)`. AC3 uses `ConnectionSnapshotOfAsync` with the
issue's line, which asserts no skip, and asserts `Complete`, `States`, `Remotes` and `Connections` in the string
forms `LegacyMonitorParserParseBuildsConnectionSnapshot` uses.

## Approach

In `TryParseAddress`, after the `*` case: with TYPE `IPv6` a text that starts with `[` and ends with `]` goes to
`TryParseIpv6` as today; every other text, for both types, is read by `TryParseQuad`, and with TYPE `IPv6` the result is
mapped with `IPAddress.MapToIPv6`. With TYPE `IPv4` nothing changes (a bracketed text still fails `TryParseQuad`). The
XML documentation of `TryParseAddress` names the new form. No signature changes; `TryParseEndpoint` is unchanged (its
non-bracket branch already splits at the last colon).

The guard's accepted forms for the new input, enumerated: the dotted text never reaches `IPAddress.TryParse`; it is read
only by the project's own `TryParseQuad`/`TryParseOctet` (exactly four parts, 1 to 3 ASCII digits each, at most 255,
no leading zero), so the accepted set is exactly the IPv4 rule of TYPE `IPv4`, and `MapToIPv6` parses nothing. A text
with a colon (`::ffff:1.2.3.4` unbracketed) has a part that is no octet (`::ffff:1`) and is refused. A NAME
that mixes a bracketed and a dotted endpoint on an IPv6 socket is read, each endpoint by its own rule; lsof does not
print it, and both addresses are IPv6 addresses, so the snapshot stays valid.

No new user-visible output (the stored values follow the existing record fields).

## Options considered

- **IPv4-mapped IPv6 address, protocol `tcp6`/`udp6`** (chosen): the same value the agent stores for the same socket
  (verified fact 4), so imported and live snapshots of one peer agree, and the `*` wildcard of an IPv6 socket already
  becomes `::` by the socket's family.
- **Plain IPv4 address, protocol `tcp6`/`udp6`** (raised by the issue as "or as IPv4"): lost — a `tcp6` entry with an
  IPv4 address exists nowhere else in the data, and a peer would be counted under a different `remotes` key than the
  agent counts it.
- **Plain IPv4 address, protocol `tcp`/`udp`**: lost — it discards the socket's family that lsof's TYPE states and
  differs from the agent's `tcp6` counts in `states`.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Core | `LogParsing/LegacyLsofLine.cs` | `TryParseAddress` reads a dotted quad with TYPE `IPv6` as the mapped address; its XML documentation |

## Signatures (for the Dev's skeleton)

None: the change is inside the private method TryParseAddress, whose signature stays as it is. No skeleton file is
rewritten.

## Test files

- `tests/Vandox.Core.Tests/LegacyLsofLineTests.cs` — AC1, AC2.
- `tests/Vandox.Core.Tests/LegacyMonitorParserTests.cs` — AC3.

Existing test code that calls a changed signature: none. Existing test whose input a criterion handles differently:
the row `[DataRow(IPv6, Tcp, "1.2.3.4:22" + Listen, LegacyLsofLine.NotMapped)]` of
`LegacyLsofLineTryParseRefusesLenientAddressForms` (`LegacyLsofLineTests.cs:211`) is removed by the Tester in step 5
(AC1 now reads it); the other rows of that test keep their result.

Files of the change no unit test can cover: none.

## Areas

- `docs/areas/log-parsers-legacy-monitoring.md`, *Connection snapshot*: the TYPE `IPv6` rule gains the dotted form.
- `docs/areas/wire-format-records.md`, *Producer mapping for connection endpoints*: the legacy parser's mapping of a
  dotted address on an IPv6 socket.

## Documentation updates

Written by the Dev once the code is green:

- `docs/areas/log-parsers-legacy-monitoring.md` lines 156-160 (TYPE `IPv6` bullet): add that a dotted quad without
  brackets, as `lsof` prints the IPv4-mapped address of an IPv6 socket, is read by the TYPE `IPv4` octet rule and kept
  as the IPv4-mapped address `::ffff:a.b.c.d` (not unmapped, as the agent keeps it), with the protocol `tcp6`/`udp6`.
- `docs/areas/wire-format-records.md` lines 263-273: one bullet — in `lsof`, a dotted IPv4 address on an IPv6 socket
  becomes the IPv4-mapped address `::ffff:a.b.c.d`, the value the connection collector keeps for the same socket.
- Sweep: `docs/ARCHITECTURE.md` (lines 10, 149, 166) names the legacy parser without its address rules — no change;
  `docs/RETIRE_LEGACY_MONITORING.md` states no address rule — no change; `CHANGELOG.md` gets entries per release
  (`docs/CONTRIBUTING.md`, *Cutting a release*) and the PR template's checklist asks for none — no change.

## Architecture check

The guarantee of security area 10 (hostile input yields a skipped record, never a crash, unbounded allocation or hang)
holds: the new input is read by the existing bounded octet reader, `MapToIPv6` cannot throw on an IPv4 address, and no
limit changes. No guarantee of `.squad/project.md` is weakened; `docs/ARCHITECTURE.md` needs no update.

## Security considerations

- [x] No limits: the line stays capped at 1,024 decoded bytes before it is split; no new limit.
- [x] None: no new exception path; a refused address still ends in the existing reason `socket address not mapped`.

## Decision records

- none: no decision beyond the obvious fix — the mapping applies the existing producer rule that an IPv4-mapped address
  of an IPv6 socket is kept, not unmapped (`docs/areas/wire-format-records.md`, record 0115 *Consequences*).

## Out of scope / follow-ups

- The 6 rows already skipped in the Product Manager's database stay missing: a file already imported is not read again
  (verified fact 9); restoring them would need an import into a fresh database. No follow-up issue: 6 of 7.4 million lines.
