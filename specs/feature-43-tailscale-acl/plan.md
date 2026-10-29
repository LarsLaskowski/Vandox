# Plan: Tailscale on the monitored server and its tailnet policy

Source: Issue #43 | [spec.md](spec.md)
Status: Revised (2)
Tier: security — the change rewrites the documented tailnet policy, the allow-list that decides what a compromised server may reach (it removes a grant and adds the example policy file that becomes its one source).
Security areas touched: 2 *Tailscale ACL and port binding* (the documented ACL: new `deploy/agent/tailnet-policy.hujson`, `deploy/agent/README.md`); `.squad/project.md` *Security areas* (area 2's file list extended)

## Problem / root cause

See [spec.md](spec.md), *Problem*; criteria AC1–AC6 are the spec's.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The README holds only a policy fragment and grants `autogroup:member` every port of `tag:vandox-backend` | `deploy/agent/README.md:26-36`, line 34 `"ip": ["*"]` | confirmed |
| 2 | A new file in `deploy/agent/` does not enter the install bundle | `.github/scripts/package-agent.sh:17`, `.github/scripts/check-agent-bundle.sh:12-14` list the bundle files by name | confirmed |
| 3 | `deploy/agent` is a test-only Go package whose tests read documents; `readRepositoryText` and `normalizeLineEnds` exist | `.squad/stack.md:45`; `deploy/agent/service_test.go:680-695` | confirmed |
| 4 | `*.hujson` is checked out with LF | `.gitattributes` (CRLF only for `*.cs`, `*.razor`, `*.md`) | confirmed |
| 5 | No HuJSON library is a dependency | `go.mod` | confirmed |
| 6 | The ingest port is 8081 on the host side | `deploy/backend/docker-compose.yml:71` | confirmed |
| 7 | `tailscale set` has `--ssh`, `--accept-routes`, `--advertise-routes`, `--advertise-exit-node`, `--exit-node`, `--advertise-connector`, `--webclient`, `--accept-dns`; `tailscale debug prefs`, `tailscale exit-node list`, `tailscale up --force-reauth` exist | Tailscale source `cmd/tailscale/cli/set.go:77-95`, `debug.go:257`, `exitnode.go:33`, `up.go:149` (`main`, 1.105.0); the server's version is unknown, and an older client rejects an unknown flag and applies nothing (Security N4) | confirmed |
| 8 | The admin console and GitOps accept HuJSON as the policy file | to verify: the maintainer saves the file (follow-up issue) (now: tailscale.com not reachable from the session) | unverified |
| 9 | A policy `tests` entry (tag `src`, `tag:host:port` in `accept`/`deny`, optional `"proto": "udp"`) is checked against `grants` on save, and a failing one blocks the save | to verify: the maintainer saves the file, then once with the agent's grant widened to `tcp:*` and once to `"8081"`, and sees both saves refused (follow-up issue) (now: not reachable) | unverified |
| 10 | A tagged node has key expiry disabled by default | to verify: the server's entry in the admin console (follow-up issue) (now: README step 3 cites kb/1068) | unverified |
| 11 | `tailscale status` lists only peers the policy relates to the server; `tailscale exit-node list` only usable exit nodes | to verify: the maintainer's run (follow-up issue) (now: computed by the closed coordination server) | unverified |
| 12 | Tailscale comes back after a reboot with its tag and prefs | to verify: the maintainer's reboot check (follow-up issue) (now: not measured) | unverified |
| 13 | Tailscale's routes live only in table 52 behind rule 5270, are deleted when it stops, and nothing makes the tailnet ranges unreachable outbound; so while it is down a `100.x` destination follows the main table's default route | Security's plan review: Tailscale 1.105.0 `wgengine/router/osrouter/router_linux.go:1577-1626`, `:386-417`, `:612-628`, and a network-namespace measurement (an `unreachable 100.64.0.0/10` in main answers at once with no route to host, table 52 still wins over it, and after table 52 is removed it fails closed) | confirmed |
| 14 | `IPAddressAllow=` filters by address, not interface | `deploy/agent/vandox-agent.service:50-51`; systemd's IP accounting/filtering is per address | confirmed |
| 15 | The agent's request carries the token on the first write and no `Expect: 100-continue` | Security's reading of `internal/sender/transport.go` | confirmed |
| 16 | A connect that fails with no route to host is logged as `connection failed` | `internal/sender/transport.go:176-193` (`classifyFailure`: not a timeout, not TLS → `reasonConnection`) | confirmed |
| 17 | `RestrictNetworkInterfaces=` needs systemd 250; the server runs 249 | to verify: in the follow-up issue that designs the control (now: Security's recollection; 249 from README:500) | unverified |
| 18 | systemd-networkd (default `ManageForeignRoutes=yes`) deletes foreign routes such as `unreachable 100.64.0.0/10` whenever a link is configured (boot), reconfigured (`networkctl reconfigure`, `netplan apply`) or loses its carrier, so a route set by a oneshot unit does not persist | Security's round-2 reading of systemd v249.11: `networkd-route.c:1153-1234`, `:1871-1885`, `networkd-link.c:1000-1024`, `:1162`, `:1262`, `:1573`, `man/networkd.conf.xml:75-82`; not measured | confirmed |

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestTailnetPolicy_StripsHuJSON`; `TestTailnetPolicy_DecodeRefusesDuplicateKeys`; `TestTailnetPolicy_HasOnlyKnownSections`; `TestTailnetPolicy_TagOwnersAreExact`; `TestTailnetPolicy_GrantsHaveOnlyKnownKeys`; `TestTailnetPolicy_AgentHasOnlyTheIngestGrant`; `TestTailnetPolicy_SourcesAreExactlyTheKnownOnes`; `TestTailnetPolicy_TestsPinTheIngestPortOnly`; `TestAgentReadme_TailscaleStepsReferThePolicyFile` | `deploy/agent/tailnet_policy_test.go` |
| AC2 | `TestAgentReadme_TailscaleChecksNameEveryProbe` | `deploy/agent/tailnet_policy_test.go` |
| AC3 | `TestTailnetPolicy_GrantsNothingOnTheBackendButIngest`; `TestTailnetPolicy_OptInLinesAreCommentedSSHOnly`; `TestAgentReadme_TailscaleStepsHoldNoWideGrant` | `deploy/agent/tailnet_policy_test.go` |
| AC4 | `TestAgentReadme_TailscaleSettingsLineAndCheck` | `deploy/agent/tailnet_policy_test.go` |
| AC5 | `TestAgentReadme_TailscaleRebootCheck`; `TestAgentReadme_TailscaleDownResidual` | `deploy/agent/tailnet_policy_test.go` |
| AC6 | `TestAgentReadme_TailscaleKeyExpiry` | `deploy/agent/tailnet_policy_test.go` |

The policy file is read by two test helpers (Security B2), never by `json.Unmarshal`:

- `stripHuJSON`: removes `//` and `/* */` comments and trailing commas outside strings; inside a string it honors escapes, so `\"`, `\\"`, `//`, `/*` and `*/` stay string content. It returns an error for a leading BOM, an unterminated block comment and an unterminated string. Its own test covers each of these cases, a trailing comma before `}` and before `]`, a trailing comma followed by a comment and then `}` or `]` (Security NB2), and CRLF input.
- `decodeStrict`: walks the result token by token (`json.Decoder.Token`) into maps and slices. It refuses a duplicate key at any level, comparing keys case-sensitively, and refuses trailing data after the top value. Its test covers a duplicate `grants` at the top level, a duplicate `src` in a grant, and a nested duplicate.

The asserts, on the decoded file and on the README section *Tailscale on the monitored server* read with `readRepositoryText`. Every key comparison is case-sensitive, so `"SRC"` or `"Src"` fails as an unknown key.

- *HasOnlyKnownSections*: the top-level keys are exactly `tagOwners`, `grants`, `tests`.
- *TagOwnersAreExact*: `{"tag:vandox-agent": ["autogroup:admin"], "tag:vandox-backend": ["autogroup:admin"]}` exactly (DA note 1).
- *GrantsHaveOnlyKnownKeys*: every grant's key set is exactly `{src, dst, ip}`.
- *AgentHasOnlyTheIngestGrant*: exactly one grant has `tag:vandox-agent` in `src`: `src` `["tag:vandox-agent"]`, `dst` `["tag:vandox-backend"]`, `ip` `["tcp:8081"]`.
- *SourcesAreExactlyTheKnownOnes*: the set of all `src` values is exactly `{tag:vandox-agent, autogroup:member}`. Tailscale's source forms are `*`, user logins, `group:`, `tag:`, `autogroup:…`, host aliases, IPs and CIDRs, and `ipset:`. Each one that can match the server fails the exact-set comparison: `*`, `autogroup:tagged`, `tag:vandox-agent` in a second grant, a covering IP or CIDR, a host alias or an ipset. `autogroup:member` matches no tagged node (kb/1068, same status as fact 10).
- *GrantsNothingOnTheBackendButIngest*: destinations are pinned as an exact set: the agent's grant has `dst` `["tag:vandox-backend"]`, and every other grant has `dst` exactly `["autogroup:self"]`, so `*`, `autogroup:internet`, `autogroup:tagged`, `autogroup:danger-all`, a tag, an IP or a CIDR fails.
- *OptInLinesAreCommentedSSHOnly*: exactly one comment line contains `"dst"`, the SSH opt-in. It names `tcp:22` and neither `8080` nor `443`.
- *TestsPinTheIngestPortOnly*: `tests` has exactly two entries:
  - key set `{src, accept, deny}`: `src` `tag:vandox-agent`, `accept` `["tag:vandox-backend:8081"]`, `deny` `["tag:vandox-backend:22", "tag:vandox-backend:8080", "tag:vandox-backend:443"]`;
  - key set `{src, proto, deny}`: `src` `tag:vandox-agent`, `proto` `udp`, `deny` `["tag:vandox-backend:8081"]` (Security N2).
- README tests, on the section:
  - *ReferThePolicyFile*: it names `tailnet-policy.hujson`.
  - *HoldNoWideGrant*: it has no `"grants"` and no `"ip": ["*"]`, and its list of forbidden destinations names `autogroup:tagged` and `autogroup:danger-all`.
  - *SettingsLineAndCheck*: the settings line of *Approach* 4 and `tailscale debug prefs`.
  - *ChecksNameEveryProbe*:
    - `tailscale exit-node list`, `tailscale status` and an IPv6 probe (`fd7a:115c:a1e0::`);
    - the accept-routes probe started with `setsid`, its one command string containing both `--accept-routes=true` and `--accept-routes=false`;
    - `tailscale debug prefs` appears after that probe.
  - *RebootCheck*: `sudo reboot`, followed by `tailscale status` and the ingest probes.
  - *DownResidual*: the section says that while Tailscale is not up a send follows the `default route`, the `token` goes in clear to a party that answers, and such an answer can make the agent drop or give up the records; it no longer says that every send fails as `connection failed`.
  - *KeyExpiry*: `--force-reauth`.
  - Each step for S2, S4, S5 and S6 contains "not yet run".

**Documented shell procedure** (*Operator environments*). The lines use placeholders (`100.64.0.1`), not shell variables, so case (d), a lost variable, cannot happen.

- (a) Pasted at once: every line is independent.
- (b) One line at a time: the intended use.
- (c) One line fails:
  - A failed check prints its result and changes nothing.
  - The accept-routes probe runs detached (`setsid -f`) with its output in `/root/vandox-route-probe.txt`. A closed SSH session therefore does not kill it before the revert. The README says this is likely, not guaranteed (DA note 5, Security N3), and the settings check after it shows a missed revert as `RouteAll` true.

Rows only CI or a real host can run: none in CI. Everything on the server is the maintainer's run (follow-up issue).

## Approach

1. **The policy file** `deploy/agent/tailnet-policy.hujson`, LF.
   - Header comment:
     - the file replaces the whole policy and works in the admin console and in GitOps alike;
     - every device the old default rule covered (subnet routers, exit nodes, other tagged devices, the maintainer's own) needs its own rule, never with a source matching the server;
     - no tag owner may be a tag;
     - a different ingest port is changed in both the agent's grant and the `tests` entries, and is never written as `tcp:*` (DA note 4).
   - Uncommented content, exactly as *Tests per criterion* pins:
     - `tagOwners`;
     - the agent's grant `tcp:8081`;
     - `{"src": ["autogroup:member"], "dst": ["autogroup:self"], "ip": ["*"]}`;
     - the commented opt-in `// {"src": ["autogroup:admin"], "dst": ["tag:vandox-backend"], "ip": ["tcp:22"]},`, with the note that it opens SSH to the backend host and that the web UI stays home-LAN only (record 0016);
     - the two `tests` entries. Their comment says they are meant to make the console refuse a later edit that widens the server, which is **not yet verified** (fact 9, Security N1).
2. **README, step 2** (policy):
   - The fragment is replaced by a reference to the file and an explanation per entry.
   - The `"*"` grant is removed. The README says what that loses over the tailnet (web UI, SSH and file services of the backend host; the home LAN is unchanged) and points to the opt-in.
   - The existing warnings stay. New warnings: no `autoApprovers`, `ssh` rule or grant with a source matching the server, and no tag as a tag owner; the forbidden destinations listed: `*`, `autogroup:internet`, `autogroup:tagged`, `autogroup:danger-all`, a subnet.
   - The optional backend-to-member grant stays as prose, marked untested, with no line in the file (DA note 2).
   - The console and GitOps are named neutrally.
3. **README, residual** (the section's opening paragraph): the sentence "every send fails as `connection failed`" (line 10) is replaced by the residual of spec E5: while Tailscale is not up, a send to the tailnet address follows the server's default route, fails as `connection failed` or `timeout`, and the records stay spooled when nothing answers; a party that answers at that address on the provider's path gets the token and the batch in clear, and its `2xx` answer makes the agent drop those records as acknowledged, a `400`/`413`/`422` refusal makes it give them up (spec E5); issue #<follow-up 2> adds a control.
4. **README, join step** (today's 3):
   - The settings line `tailscale set --ssh=false --accept-routes=false --advertise-routes= --advertise-exit-node=false --exit-node= --advertise-connector=false --webclient=false --accept-dns=false`. If it fails with an unknown flag, Tailscale is upgraded from the package repository first, rather than the flag being dropped (DA note 3, Security N4).
   - Then `tailscale debug prefs`, expecting `RunSSH` false, `RouteAll` false, `AdvertiseRoutes` empty or null, `ExitNodeID` empty, `CorpDNS` false, `RunWebClient` false, `AppConnector` `Advertise` false and `AdvertiseTags` `["tag:vandox-agent"]`. The output format is marked as not checked.
   - Key expiry, as spec S6.
5. **README, checks** (today's 4):
   - Today's lines, plus the web port and SSH on the backend's IPv6 tailnet address, another tailnet device, `tailscale status` and `tailscale exit-node list`.
   - The optional detached accept-routes probe with its caveat, then `tailscale debug prefs` again.
   - Each with the result to expect, its Tailscale page (kb/1018, kb/1019, kb/1103) and the cause of a wrong result.
6. **README, new reboot step**: `sudo reboot`, then `tailscale status`, `tailscale debug prefs`, the ingest probes of step 5, `systemctl is-active vandox-agent` and the journal. Sends fail until the tailnet is up and then go out from the spool, unless a party answered in that window (step 3's residual).
7. **README, results:** every result of these steps is marked "not yet run", and *Checks on the server* gets a row per check: "not yet run (issue #<follow-up>)".

Layout: today's numbered steps with fenced command blocks and bullets for results; the Dev words it, approved in step 9.

## Options considered

1. **Where the file lives.** `deploy/agent/tailnet-policy.hujson` (chosen) sits beside the package that pins it and stays out of the bundle (fact 2). `deploy/tailscale/` lost: the test would live in another package's folder.
2. **Reading HuJSON in the test.** The strip helper plus a strict token walk (chosen). `github.com/tailscale/hujson` lost: it is a new dependency, and its standardization still leaves duplicate keys to `encoding/json`'s last-wins rule.
3. **Policy `tests` section.** Included: it is meant to guard a later edit, and a wrong syntax fails the save visibly. Residual: fact 9.
4. **Settings check.** `tailscale debug prefs` (chosen) is in long-released versions. `tailscale get all` lost: it is too new.
5. **Keeping the token off the provider's network while Tailscale is down** (Security B1, B3): split into follow-up issue 2 (Lead decision, mode `decide`, after Security's second `CHANGES_REQUIRED`). A oneshot unit with `unreachable` routes does not survive networkd (fact 18). Each remaining candidate is unmeasured and touches the provider's network configuration: `ManageForeignRoutes=no` changes networkd for all foreign routes; `[Route] Type=unreachable` goes in a drop-in for the provider's uplink `.network`; netplan routes and nftables are the others. `RestrictNetworkInterfaces=` needs a newer systemd (fact 17) and a unit change. Settling it needs a measurement on a systemd-249 host with networkd, which the session lacks and the Product Manager will not run now. The exposure predates this change (README line 10 already assumes Tailscale is up), so this change states it instead of claiming a control.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| deploy/agent | `tailnet-policy.hujson` | new: the example policy |
| deploy/agent | `tailnet_policy_test.go` | new: the tests above (Tester) |
| deploy/agent | `README.md` | *Approach* 2–7 |

## Signatures (for the Dev's skeleton)

none

## Test files

`deploy/agent/tailnet_policy_test.go`, in package `agent` and named after the file it pins. Its helpers are the Tester's own test code.

Existing test code that calls a changed signature: none.

Files no unit test can cover: none.

## Areas

- `docs/areas/network-and-security-model.md`:
  - *Agent to backend*:
    - the example policy file as the source of the rules;
    - its `tests` entries, not yet verified to take effect;
    - E4;
    - the residual while Tailscale is down (token and batch in clear to whoever answers at that address on the provider's path) and follow-up issue 2;
    - the behavior after a reboot.
  - *Web UI*: the policy grants no source access to the backend host's tailnet address except the agent's `tcp:<ingest port>`. A subnet-route grant to the home LAN gives those devices the web UI at its LAN address, as if they were in the home LAN.
  - *Related decisions*: 0132.

## Documentation updates

Owner: Dev, after the tests are green.

- `deploy/agent/README.md`: *Approach* 2–7.
- `docs/areas/network-and-security-model.md`: *Areas*.
- `docs/INSTALL.md` step 11 (lines 310-312): name the policy file.
- `.squad/project.md` area 2: add `deploy/agent/tailnet-policy.hujson` to the file list, and record 0132 to its records.
- `.squad/stack.md` *Layout* (line 45): the `deploy/agent` tests also read the example tailnet policy.
- Sweep: `docs/ARCHITECTURE.md:266` and `:447` and `SECURITY.md:41-42` state the rule and stay true. `docs/ARCHITECTURE.md:499` (bundle contents) is unchanged.

## Architecture check

The guarantee that the monitored server reaches only the ingest port is kept, pinned by tests and by the policy's own `tests`. The token's exposure while Tailscale is down predates this change; it is now stated, and follow-up issue 2 adds a control. Record 0016 is kept. No guarantee is weakened, and `docs/ARCHITECTURE.md` needs no edit.

## Security considerations

- [x] No limits: only the repository's own file is parsed, in a test.
- [x] No user-visible exception: none.
- `IPAddressAllow=` cannot name an interface (fact 14). `RestrictNetworkInterfaces=` would, but needs systemd 250 against the server's 249 (fact 17). No control in this change: the residual of spec E5 is stated in the README and the area, and follow-up issue 2 designs the control (*Options considered* 5).

## Decision records

- `docs/decisions/0132-example-tailnet-policy-file-pinned-by-tests-no-default-maintainer-grant.md` (Proposed; records the split of the routes control)

## Challenge

Answers to the five minor *Challenge notes* of the Devil's Advocate, folded in here:

1. `tagOwners` not pinned: accepted. *TagOwnersAreExact*, plus the README sentence that no tag owner may be a tag.
2. The backend-to-member grant: kept as README prose, marked untested, with no line in the file.
3. Settings missing from the check, and older clients: accepted. `RunWebClient` and `AppConnector` were added; on an unknown flag, upgrade Tailscale rather than drop the flag.
4. Port in two places: accepted. The header comment and README step 2 say so.
5. The revert is not guaranteed: accepted. The probe runs under `setsid -f` with its output in a file, the README does not claim a guarantee, and the settings check follows it.

## Security review

- B1 and B3 (token on the provider's network while Tailscale is down; the routes do not survive networkd): not fixed in this change. After the second `CHANGES_REQUIRED`, the Lead split the control into follow-up issue 2 (*Loop limits*, Plan ↔ Security). Accept-and-fix did not apply: B3 is read from source, not measured, and its fix is a choice among options, not a supplied edit. This change states the residual (*Approach* 3, *DownResidual*, the area document). Fact 18 is corrected, and `PartOf=` is gone with the unit.
- B2 (`encoding/json` versus Tailscale's parser): resolved.
  - `stripHuJSON` handles string escapes, refuses a BOM and unterminated comments or strings, and has its own tests.
  - `decodeStrict` refuses duplicate keys at every level, compared case-sensitively.
  - Grant and `tests` key sets are exact.
- N1: accepted. The file comment and the area document say "not yet verified".
- N2: accepted. A second `tests` entry denies UDP to 8081; its syntax is fact 9.
- N3: accepted, together with DA note 5.
- N4: accepted, together with DA note 3.
- NB1 (name servers in the tailnet range): moved to follow-up issue 2, where it belongs to the precondition of a route control.
- NB2: accepted. The strip helper's test adds a trailing comma followed by a comment.

## Out of scope / follow-ups

- The PR says `Refs #43` and does not close it.
- Follow-up issue (opened by the orchestrator): "[Ops] Run the Tailscale checks of the monitored server on production". Its body asks the maintainer to:
  - save `deploy/agent/tailnet-policy.hujson`, then save it once with the grant widened to `tcp:*` and once to `"8081"`, expecting both saves refused (facts 8, 9);
  - run the settings check, the checks and the reboot check, and look at key expiry (facts 10 to 12);
  - record the results in *Checks on the server*, then close #43.
- Follow-up issue 2 (opened by the orchestrator; its number replaces `#<follow-up 2>` in the README): "[Security] Keep the agent's token off the provider's network while Tailscale is down". Body:
  - the problem: spec E5 here; fact 13 (Tailscale's routes only in table 52, removed when it stops) and fact 14 (`IPAddressAllow=` ignores the interface);
  - why a oneshot unit with `unreachable` routes fails: systemd-networkd drops foreign routes on link configure, reconfigure and carrier loss (fact 18, Security's v249.11 source references);
  - candidates to measure on a systemd-249 host with networkd: a `networkd.conf.d` drop-in `ManageForeignRoutes=no` (affects all foreign routes); `[Route] Destination=… Type=unreachable` in a drop-in for the uplink's `.network`; an nftables rule; the agent unit's `RestrictNetworkInterfaces=` (systemd 250 or later);
  - the precondition: which network manager is active (`systemctl is-active systemd-networkd NetworkManager`), no provider address, route or name server (`resolvectl dns`) in `100.64.0.0/10` or `fd7a:115c:a1e0::/48` (Security NB1);
  - the checks: the route check after a boot, after `networkctl reconfigure <uplink>` or `netplan apply`, and after a carrier flap, plus `ip route get 100.64.0.1` naming `tailscale0` while Tailscale is up.
