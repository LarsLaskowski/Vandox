# Plan: Keep the token off a loopback listener reached through a forged name answer

Source: Issue #373
Tier: security — the change narrows the guard that decides where the agent token may go (which `backend.url` hosts the loader, the transport and `install.sh` accept).
Security areas touched: 2 Tailscale ACL and port binding (the agent-to-backend guarantee in `docs/areas/network-and-security-model.md`); 6 Agent privileges (`deploy/agent/install.sh`, `deploy/agent/README.md`, `deploy/agent/agent.yaml`); 8 Secrets handling (`internal/config`, `internal/sender`: where the token goes); 10 Parsing of external input (the `backend.url` rule, `install.sh` inputs); 11 Outbound calls (`internal/sender`, its destination)

## Problem / root cause

`backend.url` accepts a host name (`internal/config/agent.go:509-513`, `validHost` at `:550`), and `NewHTTPTransport`
(`internal/sender/transport.go:99-105`) accepts any host. net/http resolves the name, and the dialer's `Control` hook gets the
**resolved** address: `needsDevice` (`transport.go:152-163`) exempts it from the `tailscale0` binding when it is loopback. The
answer comes from the `systemd-resolved` stub (allowed by the unit as localhost), which asks the provider's name servers because
the documented setup turns Tailscale's DNS off. A forged `127.0.0.1`/`::1` therefore sends the bearer token and the batch, unbound,
to whatever local process listens on the port. The defect is that a name server, not the configuration, chooses the destination;
the loopback exemption is only the most direct of the destinations it can choose (record 0192, *Context*).

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | `backend.url` accepts host names (issue) | `internal/config/agent.go:509-513` calls `validHost`, which accepts `[A-Za-z0-9.-]` names (`:550-563`); `TestLoadAgent_AcceptedValues` (`agent_test.go:363`) accepts `https://nas.tailnet.ts.net`, `http://h` | confirmed |
| 2 | A forged loopback answer meets the exemption (issue) | `transport.go:128` dials with `Control: bindControl`; `needsDevice` judges the address the dialer passes, the resolved IP (`:150-163`; the test case `localhost:8081` → bound at `transport_test.go:719` shows a name never reaches it) | confirmed |
| 3 | Names resolve through the local stub, which the unit allows, and the provider's servers (issue) | `docs/areas/network-and-security-model.md:63-66`, `docs/areas/agent-rights.md:84-85`, `deploy/agent/README.md:117-125` (DNS off) (documented; the server's `resolv.conf` not re-read) | confirmed |
| 4 | The residual predates #337 and the README asks for the address (issue) | record 0134 *Consequences* (before this change); `README.md:198-199`, `deploy/agent/README.md:125` | confirmed |
| 5 | Go dials an IP-literal host without any lookup, using the same parser as the planned guard | `$GOROOT/src/net/lookup.go:310` (`netip.ParseAddr(host)` → literal returned) in go1.27.0; net/http dials `idnaASCIIFromURL(url)` = `url.Hostname()` for an ASCII host (`net/http/transport.go:3196-3201`, `request.go:801`) | confirmed |
| 6 | The guard and the dialer see the same host string, except for an IPv6 zone | the transport builds `t.url` from `u.Host` (`transport.go:105`); `http.NewRequest` re-parses it and net/http dials `Hostname()` of that URL, the same value the guard checks with `u.Hostname()` (a percent escape outside an IPv6 zone is a `url.Parse` error). With a zone, `u.Host` holds the unescaped `%eth0`, so the rebuilt `t.url` does not parse and every request fails in `NewRequestWithContext` (`transport.go:181`) — measured by Security in step 3; fail-closed, predates this change | confirmed |
| 7 | No released agent sends to `backend.url` | `git ls-tree v0.1.0` has no `internal/sender`; `docs/areas/network-and-security-model.md:11` | confirmed |
| 8 | `install.sh` accepts names and warns about name servers | `install.sh:288-290` (`_is_host_name` branch), `:336-381` (`name_server_warning`), `:981` (its call) | confirmed |
| 9 | Records 0134 and 0183 are unreleased | `git tag --contains` of each adding commit is empty; only tag `v0.1.0` (`git ls-remote --tags origin`) | confirmed |
| 10 | Under the issue's second option, a forged unspecified answer still reaches the host | `transport_test.go:754-757`: a dial of `0.0.0.0`/`::` "connects … to the host itself" | confirmed |
| 11 | Under the second option, a forged answer of the server's own tailnet address is bound to `tailscale0` and delivered locally | to verify: (reasoned from Linux's route lookup with an output interface, which matches the local route of that device; not measured) a socket bound to a stand-in device dials that device's own address and reaches a local listener — Security, step 3, only if option 2 is revived (option 1 makes no lookup, so nothing rests on it) | unverified |
| 12 | Only `deploy/agent/install_test.go` and `internal/config`/`internal/sender` tests use host names as backend URLs | `grep` over `*_test.go`, `testdata`, `cmd`, `deploy` | confirmed |
| 13 | `install.sh` accepts IPv4 parts with leading zeros that the agent refuses | `_is_ipv4` (`install.sh:226-238`) matches `[0-9]{1,3}` per part and checks only `> 255`, so `010.0.0.1` passes; `netip.ParseAddr` refuses it ("octet with leading zero", go1.27.0) — Devil's Advocate, step 2 | confirmed |
| 14 | `install.sh` refuses an IPv6 zone already | `re_v6` (`install.sh:268`) admits only `[0-9A-Fa-f:]` inside the brackets, so `%25eth0` never matches and the generic message follows | confirmed |

## Acceptance criteria

- AC1: `LoadAgent` refuses a `backend.url` whose host (`url.URL.Hostname()`) is not accepted by `netip.ParseAddr` or carries a zone
  (`Zone() != ""`), with a `KeyError` on `backend.url` whose reason is exactly `host must be an IP address without a zone, not a name` (the value never quoted).
  Refused: `https://nas.tailnet.ts.net`, `http://localhost:8081`, `http://h`, `http://127.1`, `http://2130706433`,
  `http://0x7f.0.0.1`, `http://127.0.0.1.:8081`, `http://010.0.0.1`, `http://[fe80::1%25eth0]:8081`, `http://[::1%25lo]:1`.
  Accepted: `http://100.64.0.1:8081`, `HTTP://127.0.0.1:1/`, `http://[fd7a:115c:a1e0::1]:8081`, `https://[::1]`,
  `http://[::ffff:100.64.0.1]:8081`.
- AC2: `NewHTTPTransport` returns a nil transport and the error `sender: backend url: not usable` for a base URL whose host is not
  accepted by `netip.ParseAddr` or carries a zone (the refused hosts of AC1, in otherwise valid URLs), and builds a transport for the
  accepted ones.
- AC3: `install.sh`'s `validate_backend_url` refuses a host name with exit 2, nothing on standard output and exactly
  `install.sh: --backend-url is not http(s)://ip-address[:port]` on standard error — the one message for every refused value — and
  still accepts IPv4 and bracketed IPv6 literals. An IPv4 part with a leading zero is refused (`0` alone stays valid), and so is an
  IPv6 zone. Refused: `https://backend.tail1234.ts.net`, `http://h`, `http://localhost:8081`, `http://a.b-c.d`, `http://010.0.0.1`,
  `http://100.064.0.1:8081`, `http://[fe80::1%25eth0]:8081`. Accepted: `http://100.0.0.1:8081`, `http://100.64.0.1:8081`, `http://1.2.3.4`, `http://[fd7a:115c:a1e0::1]:8081/`, `http://[::1]:8081`.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestLoadAgent_BackendURLHostMustBeAnIPAddress` (subtests `AC1 refuses <url>` / `AC1 accepts <url>`, reason compared exactly) | `internal/config/agent_test.go` |
| AC2 | `TestNewHTTPTransport_RefusesHostNames` (subtests `AC2 …`; nil transport, `errors.Is(err, errBackendURL)` and the text) | `internal/sender/transport_test.go` |
| AC3 | `TestValidateBackendURL_RefusesHostNames` (subtests `AC3 …`, via the harness's `h.run(\`validate_backend_url "$1"\`, …)`) | `deploy/agent/install_test.go` |

## Approach

- **Config** (`internal/config/agent.go`): the fourth `urlRules` entry refuses the host when `netip.ParseAddr(u.Hostname())` fails or
  the address has a zone, with the reason `host must be an IP address without a zone, not a name`; `validHost` is deleted (no other caller). The rule order stays, so a value
  with a name and another defect now reports the host rule.
- **Transport** (`internal/sender/transport.go`, `NewHTTPTransport`): after the existing checks, a host `netip.ParseAddr` refuses,
  or an address with a zone, returns `nil, errBackendURL`. This keeps the property at the dial site whatever builds the transport. `needsDevice` and
  `bindControl` stay: with a literal host the dialed address is the configured one, so the loopback exemption applies to a literal
  only. Update the comments of `tailnetDevice`/`needsDevice` only if they then say something untrue.
- **install.sh**: in `validate_backend_url` the non-digit branch sets `host=""` (a name is refused), and the IPv4 branch also sets
  `host=""` when a part has a leading zero (`(^|\.)0[0-9]`); `_is_ipv4` itself stays, because its other caller,
  `tailnet_warning`, only sees values that passed this validation; the function comment at `:263` becomes `http(s)://ip-address[:port][/]`. A zone stays
  refused by `re_v6` (fact 14). `_is_host_name` and
  `name_server_warning` with its call at `:981` are deleted; the message becomes
  `install.sh: --backend-url is not http(s)://ip-address[:port]`. `tailnet_warning` stays unchanged (its `else return 0` is now
  reached only by its own unit test).
- No new user-visible output beyond the two error texts fixed above.

Guard forms (what the dialer treats as a literal, from `net/lookup.go:310`, is exactly what `netip.ParseAddr` accepts): dotted IPv4
without leading zeros, IPv6 with or without embedded IPv4, IPv4-mapped IPv6, and IPv6 with a zone. All but the zone are accepted and
dialed without a lookup; a zone is refused, because the transport cannot rebuild a URL from it (fact 6) and a link-local backend is
no tailnet address. Everything else `url.Parse` yields as a host (names, short or
numeric IPv4 forms such as `127.1`, `2130706433`, `0x7f.0.0.1`, octal-looking `010.0.0.1`, a trailing dot) is refused by both the loader and the transport; `url.Parse` already refuses a percent escape in a host
outside an IPv6 zone (`http://127%2E0.0.1:8081`). The refused and accepted lists of AC1 were run once through `url.Parse` and
`netip.ParseAddr` on go1.27.0 (scratch program, results as listed). `install.sh` accepts a subset (four-part IPv4 without leading zeros, bracketed hex IPv6 without a zone); the one known gap is in
*Out of scope*.

## Options considered

1. **Refuse host names** (chosen) — no lookup, nothing to forge; record 0192.
2. **Exempt loopback only for a literal loopback host** (the issue's second option) — lost: a name server still picks the
   destination; an unspecified answer reaches the host (fact 10) and the server's own tailnet address is delivered locally through
   the bound device (fact 11), so it needs further per-dial guards to keep a form the documented setup cannot use.
3. **Keep the documented residual** — lost: a hosting customer's process on the server can listen on the backend port.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Go agent | `internal/config/agent.go` | host rule of `backend.url`; `validHost` removed |
| Go agent | `internal/sender/transport.go` | `NewHTTPTransport` refuses a non-literal host |
| Agent install | `deploy/agent/install.sh` | `validate_backend_url` refuses names, message; `_is_host_name`, `name_server_warning` removed |
| Agent install | `deploy/agent/agent.yaml` | comment of `backend.url` (lines 23-26 only; `url:` stays on line 27, which `uninstall_test.go:140` and `install.sh:600` rely on) |

## Signatures (for the Dev's skeleton)

None: no member is added or changes its signature; `validHost(host string) bool` is deleted.

## Test files

`internal/config/agent_test.go`, `internal/sender/transport_test.go`, `deploy/agent/install_test.go`.

Existing tests whose inputs AC1–AC3 now handle differently, adapted by the **Tester in step 5** (no signature changes):

- `internal/config/agent_test.go:363` `TestLoadAgent_AcceptedValues`: replace the name URLs (`https://nas.tailnet.ts.net`,
  `HTTP://h:1/`, `http://h:65535`, `http://h`) with IP-literal URLs of the same shape (scheme case, port 65535, no port, `/`).
  `TestLoadAgent_InvalidValues` and the other `http://h:1` fixtures stay: they are still refused, on the key only.
- `internal/sender/transport_test.go:190` `TestHTTPTransport_BuiltWithoutProxyAndRedirects`: `https://backend.example:8081` →
  `https://100.64.0.1:8081`.
- `deploy/agent/install_test.go`: `runBackendURLCases` (`:1561-1592`) moves the name values (`https://backend.tail1234.ts.net`,
  `http://h`, `http://h:65535`, `https://h/`, `http://a.b-c.d`, `http://`+label63, `http://`+name253) to *refused*, adds
  `http://010.0.0.1` there, and expects the
  new message; `runParseArgsCases` `urlLine` (`:1441`) gets the new message; `TestNameServerWarning_ForServersOutsideLocalhostAndTheTailnet`
  (`:2137` ff.) is deleted with the function. `TestTailnetWarning_…` ("a name" case) and the `render_config` values at `:456-461`
  stay: those functions do not validate and are unchanged.

Files no unit test can cover: none.

## Areas

- `configuration-options.md` — `backend.url` row (`:36`) and *Backend URL and backfill* (`:55-58`): the host is an IP address without
  a zone, the error text of AC1, MagicDNS names are refused; with `https` the backend's certificate must name that IP address as a
  subject alternative name (a `*.ts.net` certificate fails as `tls failed`).
- `network-and-security-model.md` — the binding bullet (`:52-62`) says the address is the configured literal; the *Residual: a host
  name* bullet (`:63-67`) is replaced by the closed state (record 0192); the second-layer bullet (`:72-77`) loses the host-name sentences.
- `agent-sending.md` — `:28-43`: no MagicDNS qualification; the `https` sentence says the certificate must name the IP address
  (subject alternative name), so a `*.ts.net` certificate fails as `tls failed`; no "forged name answer can reach" clause, the host-name sentences of the
  second layer and the MagicDNS bullet replaced by "`backend.url` holds an IP address; no name is looked up"; *Decisions* list gets 0192.
- `agent-installation.md` — `:31` (residual pointer), `:46-49` (the rule and AC3's message), `:96-97` (the name-server warning removed).
- `agent-rights.md` — `:84-85`: drop the clause about port 53.

## Documentation updates

All by the Dev against the code as built, besides the areas above:

- `docs/ARCHITECTURE.md:529` — after the binding clause: `backend.url` takes only an IP address, so no name answer chooses where the
  token goes ([record 0192]).
- `.squad/project.md` *Security areas*, area 11 *Outbound calls*: after `internal/sender` insert "(the token goes only to the IP
  literal in `backend.url`; no name is looked up: the `backend.url` rule in `internal/config/agent.go`, `validate_backend_url` in
  `deploy/agent/install.sh`)". No new area and no renumbering.
- `deploy/agent/README.md` — `:28-32` and `:401-406` (residual and name-server paragraph: names are refused), `:603` (the parenthesis
  about host names and issue #373).
- `deploy/agent/agent.yaml:23-26` — "use the backend host's tailnet address (an IP address; a host name is refused)", same line count.
- `README.md:198-199` and `docs/INSTALL.md:339` — the backend URL must be the tailnet **IP address**; the agent and the script refuse
  a host name.
- `CHANGELOG.md`: none (it holds only released versions; the release notes come from PR labels).
- Test documentation (Tester): the test list of `agent-sending.md` (`:158-161`) and any test list of the other changed area documents
  name the new tests.

## Architecture check

No guarantee of `.squad/project.md` is weakened. The outbound-only, tailnet-only connection (records 0006, 0010, 0134, 0183) is
strengthened: the destination of the token is now fixed by the configuration alone. The behavior change visible to an operator —
a host name in `backend.url` or `--backend-url` is refused — affects no released sender (fact 7).

## Security considerations

- [x] No limits on parsed input are added or changed (`validHost`'s 253-byte bound goes with the name branch).
- [x] Texts that reach a user: `config: <file>:<line>: backend.url: host must be an IP address without a zone, not a name`,
  `sender: backend url: not usable`, `install.sh: --backend-url is not http(s)://ip-address[:port]`; none quotes the value or a
  runtime error.

## Decision records

- `docs/decisions/0192-backend-url-names-the-backend-by-ip-address-host-names-are-refused.md` (Proposed, indexed).
- Edited in place (unreleased, fact 9): 0134 *Consequences* (the residual bullet now points at 0192), 0183 option 2 and the
  `install.sh` consequence.

## Challenge

- **Major 1 (install.sh accepts leading-zero IPv4 the agent refuses)** — accepted: `validate_backend_url` refuses a part with a
  leading zero in its IPv4 branch only (`_is_ipv4` stays, its other caller sees validated values); AC3 refuses `http://010.0.0.1` and
  `http://100.064.0.1:8081`; facts 13 and *Guard forms* updated.
- **Minor 1 (https needs the IP in the certificate)** — accepted: *Areas* has the Dev state it in `agent-sending.md` and
  `configuration-options.md`; the `install.sh` comment at `:263` follows the new message (*Approach*).

## Security review

- **Blocking 1 (areas misnamed)** — accepted: *Security areas touched* now lists 2, 6, 8, 10 and 11; the new area 8 is dropped and
  area 11 is extended instead (*Documentation updates*).
- **Non-blocking 2 (IPv6 zone)** — taken: both Go guards refuse a zone with the same reason and error, AC1 moves both zone URLs to
  refused, AC3 pins the zone refusal of `install.sh` (already refused, fact 14); fact 6 and *Guard forms* corrected. Re-check of the
  whole accepted list: IPv4 without leading zeros, IPv6 with or without embedded IPv4, IPv4-mapped IPv6 — each dialed as a literal
  without a lookup (fact 5); zones, names and every other `url.Parse` host are refused.
- **Non-blocking 3 (leading-zero IPv4)** — taken, as Major 1.

## Out of scope / follow-ups

- Proposed issue "[Agent] install.sh accepts malformed IPv6 literals the agent refuses": `validate_backend_url` takes any bracketed
  run of hex digits and colons with two colons (`http://[1:::2]`), which `netip.ParseAddr` refuses, so the install succeeds and the
  agent fails at start. Pre-existing; fails closed. (Leading-zero IPv4 and zones are fixed here.)
- A configured literal that is the server's own or the unspecified address reaches a local listener; that is the operator's own
  configuration, not a forged answer, and `install.sh` already warns about an address outside the tailnet.
