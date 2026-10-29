# Plan: Tailscale on the monitored server and its tailnet policy

Source: Issue #43 | [spec.md](spec.md)
Status: Revised (1)
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
| 17 | `RestrictNetworkInterfaces=` needs systemd 250; the server runs 249 | to verify: none needed, the option is not used (now: Security's recollection; 249 from README:500) | unverified |
| 18 | systemd-networkd may remove foreign routes when it starts (`ManageForeignRoutes=` default) and so drop the unreachable routes; `After=` and `PartOf=systemd-networkd.service` re-add them | to verify: the maintainer's reboot check and one `systemctl restart systemd-networkd` followed by the route check (follow-up issue) (now: not measured) | unverified |

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestTailnetPolicy_StripsHuJSON`; `TestTailnetPolicy_DecodeRefusesDuplicateKeys`; `TestTailnetPolicy_HasOnlyKnownSections`; `TestTailnetPolicy_TagOwnersAreExact`; `TestTailnetPolicy_GrantsHaveOnlyKnownKeys`; `TestTailnetPolicy_AgentHasOnlyTheIngestGrant`; `TestTailnetPolicy_SourcesAreExactlyTheKnownOnes`; `TestTailnetPolicy_TestsPinTheIngestPortOnly`; `TestAgentReadme_TailscaleStepsReferThePolicyFile` | `deploy/agent/tailnet_policy_test.go` |
| AC2 | `TestAgentReadme_TailscaleChecksNameEveryProbe` | `deploy/agent/tailnet_policy_test.go` |
| AC3 | `TestTailnetPolicy_GrantsNothingOnTheBackendButIngest`; `TestTailnetPolicy_OptInLinesAreCommentedSSHOnly`; `TestAgentReadme_TailscaleStepsHoldNoWideGrant` | `deploy/agent/tailnet_policy_test.go` |
| AC4 | `TestAgentReadme_TailscaleSettingsLineAndCheck` | `deploy/agent/tailnet_policy_test.go` |
| AC5 | `TestAgentReadme_TailscaleRebootCheck` | `deploy/agent/tailnet_policy_test.go` |
| AC6 | `TestAgentReadme_TailscaleKeyExpiry` | `deploy/agent/tailnet_policy_test.go` |
| AC7 | `TestAgentReadme_TailnetRoutesUnit` | `deploy/agent/tailnet_policy_test.go` |

The policy file is read by two test helpers (Security B2), never by `json.Unmarshal`:

- `stripHuJSON`: removes `//` and `/* */` comments and trailing commas outside strings; inside a string it honors escapes, so `\"`, `\\"`, `//`, `/*` and `*/` stay string content. It returns an error for a leading BOM, an unterminated block comment and an unterminated string. Its own test covers each of these cases, a trailing comma before `}` and before `]`, and CRLF input.
- `decodeStrict`: walks the result token by token (`json.Decoder.Token`) into maps and slices. It refuses a duplicate key at any level, comparing keys case-sensitively, and refuses trailing data after the top value. Its test covers a duplicate `grants` at the top level, a duplicate `src` in a grant, and a nested duplicate.

The asserts, on the decoded file and on the README section *Tailscale on the monitored server* read with `readRepositoryText`. Every key comparison is case-sensitive, so `"SRC"` or `"Src"` fails as an unknown key.

- *HasOnlyKnownSections*: the top-level keys are exactly `tagOwners`, `grants`, `tests`.
- *TagOwnersAreExact*: `{"tag:vandox-agent": ["autogroup:admin"], "tag:vandox-backend": ["autogroup:admin"]}` exactly (DA note 1).
- *GrantsHaveOnlyKnownKeys*: every grant's key set is exactly `{src, dst, ip}`.
- *AgentHasOnlyTheIngestGrant*: exactly one grant has `tag:vandox-agent` in `src`: `src` `["tag:vandox-agent"]`, `dst` `["tag:vandox-backend"]`, `ip` `["tcp:8081"]`.
- *SourcesAreExactlyTheKnownOnes*: the set of all `src` values is exactly `{tag:vandox-agent, autogroup:member}`. Tailscale's source forms are `*`, user logins, `group:`, `tag:`, `autogroup:…`, host aliases, IPs and CIDRs, and `ipset:`. Each one that can match the server fails the exact-set comparison: `*`, `autogroup:tagged`, `tag:vandox-agent` in a second grant, a covering IP or CIDR, a host alias or an ipset. `autogroup:member` matches no tagged node (kb/1068, same status as fact 10).
- *GrantsNothingOnTheBackendButIngest*: no other grant has `tag:vandox-backend` in `dst`. No `dst` is `*`, `autogroup:internet`, an IP or a CIDR.
- *OptInLinesAreCommentedSSHOnly*: exactly one comment line contains `"dst"`, the SSH opt-in. It names `tcp:22` and neither `8080` nor `443`.
- *TestsPinTheIngestPortOnly*: `tests` has exactly two entries:
  - key set `{src, accept, deny}`: `src` `tag:vandox-agent`, `accept` `["tag:vandox-backend:8081"]`, `deny` `["tag:vandox-backend:22", "tag:vandox-backend:8080", "tag:vandox-backend:443"]`;
  - key set `{src, proto, deny}`: `src` `tag:vandox-agent`, `proto` `udp`, `deny` `["tag:vandox-backend:8081"]` (Security N2).
- README tests, on the section:
  - *ReferThePolicyFile*: it names `tailnet-policy.hujson`.
  - *HoldNoWideGrant*: it has no `"grants"` and no `"ip": ["*"]`.
  - *SettingsLineAndCheck*: the settings line of *Approach* 4 and `tailscale debug prefs`.
  - *ChecksNameEveryProbe*:
    - `tailscale exit-node list`, `tailscale status` and an IPv6 probe (`fd7a:115c:a1e0::`);
    - the accept-routes probe started with `setsid`, its one command string containing both `--accept-routes=true` and `--accept-routes=false`;
    - `tailscale debug prefs` appears after that probe.
  - *RebootCheck*: `sudo reboot`, followed by the route check and `ip route get 100.64.0.1`.
  - *KeyExpiry*: `--force-reauth`.
  - *TailnetRoutesUnit*: the unit's lines `ExecStart=/usr/sbin/ip route replace unreachable 100.64.0.0/10`, `ExecStart=/usr/sbin/ip -6 route replace unreachable fd7a:115c:a1e0::/48`, `Before=vandox-agent.service` and `RequiredBy=vandox-agent.service`, plus the checks `ip route show table main 100.64.0.0/10` and `ip -6 route show table main fd7a:115c:a1e0::/48`. The step comes before the section's join step.
  - Each step for S2, S4, S5, S6 and S7 contains "not yet run".

**Documented shell procedure** (*Operator environments*). The lines use placeholders (`100.64.0.1`), not shell variables, so case (d), a lost variable, cannot happen.

- (a) Pasted at once: every line is independent. The routes unit is one heredoc block, so pasting it whole is its intended use.
- (b) One line at a time: the intended use for everything else. The README says the heredoc block goes in as one paste.
- (c) One line fails:
  - A failed check prints its result and changes nothing.
  - If `systemctl enable --now` of the routes unit fails, the route check shows no `unreachable` line, and the README says to stop before the agent's install. Because of `RequiredBy=`, the agent cannot start without the unit (fails closed).
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
   - The existing warnings stay. New warnings: no `autoApprovers`, `ssh` rule or subnet/`autogroup:internet` grant with a source matching the server, and no tag as a tag owner.
   - The optional backend-to-member grant stays as prose, marked untested, with no line in the file (DA note 2).
   - The console and GitOps are named neutrally.
3. **README, new step before the join** (*Keep the tailnet ranges off the default route*):
   - **Precondition:** `ip -4 addr`, `ip -6 addr`, `ip route`, `ip -6 route`. Nothing in `100.64.0.0/10` or `fd7a:115c:a1e0::/48` may appear outside `tailscale0`. Otherwise stop, do not add the routes, and accept the residual (E7).
   - **The unit:** written with `cat > /etc/systemd/system/vandox-tailnet-routes.service <<'EOF'`:
     - `[Unit]`: `Description=`, `After=systemd-networkd.service`, `PartOf=systemd-networkd.service`, `Before=vandox-agent.service`;
     - `[Service]`: `Type=oneshot`, `RemainAfterExit=yes`, and the two `ExecStart=` lines of *Tests per criterion*. There is no `ExecStop=`: the routes stay when the unit stops, so it fails closed;
     - `[Install]`: `WantedBy=multi-user.target` and `RequiredBy=vandox-agent.service`.
   - **Enable:** `systemctl daemon-reload` and `systemctl enable --now vandox-tailnet-routes.service`.
   - **The check:** `ip route show table main 100.64.0.0/10` prints `unreachable 100.64.0.0/10`. `ip -6 route show table main fd7a:115c:a1e0::/48` prints a line that starts with `unreachable fd7a:115c:a1e0::/48`. With Tailscale up, `ip route get 100.64.0.1` names `dev tailscale0`.
   - The README explains why: while Tailscale is down, the token and the batch would otherwise go in clear to whoever answers on the provider's path (fact 13). It also says the step applies to the already-joined production server too, and that the agent's restart follows a networkd restart because of `PartOf=` and `RequiredBy=`. Not yet run.
4. **README, join step** (today's 3):
   - The settings line `tailscale set --ssh=false --accept-routes=false --advertise-routes= --advertise-exit-node=false --exit-node= --advertise-connector=false --webclient=false --accept-dns=false`. If it fails with an unknown flag, Tailscale is upgraded from the package repository first, rather than the flag being dropped (DA note 3, Security N4).
   - Then `tailscale debug prefs`, expecting `RunSSH` false, `RouteAll` false, `AdvertiseRoutes` empty or null, `ExitNodeID` empty, `CorpDNS` false, `RunWebClient` false, `AppConnector` `Advertise` false and `AdvertiseTags` `["tag:vandox-agent"]`. The output format is marked as not checked.
   - Key expiry, as spec S6.
5. **README, checks** (today's 4):
   - Today's lines, plus the web port and SSH on the backend's IPv6 tailnet address, another tailnet device, `tailscale status` and `tailscale exit-node list`.
   - The optional detached accept-routes probe with its caveat, then `tailscale debug prefs` again.
   - Each with the result to expect, its Tailscale page (kb/1018, kb/1019, kb/1103) and the cause of a wrong result.
6. **README, new reboot step**: `sudo reboot`, then the route check, `ip route get 100.64.0.1`, `tailscale status`, `tailscale debug prefs`, the ingest probes of step 5, `systemctl is-active vandox-agent` and the journal. Sends fail at once as `connection failed` until the tailnet is up, then go out from the spool.
7. **README, existing text:** the sentence "every send fails as `connection failed`" (line 10) is tied to step 3's routes. Every result of these steps is marked "not yet run", and *Checks on the server* gets a row per check: "not yet run (issue #<follow-up>)".

Layout: today's numbered steps with fenced command blocks and bullets for results; the Dev words it, approved in step 9.

## Options considered

1. **Where the file lives.** `deploy/agent/tailnet-policy.hujson` (chosen) sits beside the package that pins it and stays out of the bundle (fact 2). `deploy/tailscale/` lost: the test would live in another package's folder.
2. **Reading HuJSON in the test.** The strip helper plus a strict token walk (chosen). `github.com/tailscale/hujson` lost: it is a new dependency, and its standardization still leaves duplicate keys to `encoding/json`'s last-wins rule.
3. **Policy `tests` section.** Included: it is meant to guard a later edit, and a wrong syntax fails the save visibly. Residual: fact 9.
4. **Settings check.** `tailscale debug prefs` (chosen) is in long-released versions. `tailscale get all` lost: it is too new.
5. **Keeping the token off the provider's network while Tailscale is down** (Security B1):
   - A oneshot unit with unreachable routes in the main table (chosen): independent of the provider's network files, ordered before the agent, and fail-closed through `RequiredBy=`.
   - netplan `type: unreachable` routes lost: they edit provider- or cloud-init-managed files, and a mistake there can cut the server off at `netplan apply`.
   - An nftables rule refusing tailnet destinations outside `tailscale0` lost: it interacts with Tailscale's own netfilter chains and the provider's firewall.
   - `RestrictNetworkInterfaces=` in the agent's unit lost: it needs systemd 250 (fact 17), and the spec forbids changing the unit.

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
    - the unreachable routes as the control that keeps a send from leaving while Tailscale is down, and the residual without them (token and batch in clear to whoever answers at that address on the provider's path);
    - the behavior after a reboot.
  - *Web UI*: the policy grants no source access to the backend host's tailnet address except the agent's `tcp:<ingest port>`. A subnet-route grant to the home LAN gives those devices the web UI at its LAN address, as if they were in the home LAN.
  - *Related decisions*: 0132.

## Documentation updates

Owner: Dev, after the tests are green.

- `deploy/agent/README.md`: *Approach* 2–7.
- `docs/areas/network-and-security-model.md`: *Areas*.
- `docs/INSTALL.md` step 11 (lines 310-312): name the policy file, and the routes step among the steps done before the agent.
- `.squad/project.md` area 2: add `deploy/agent/tailnet-policy.hujson` and the README's routes unit to the file list, and record 0132 to its records.
- `.squad/stack.md` *Layout* (line 45): the `deploy/agent` tests also read the example tailnet policy.
- Sweep: `docs/ARCHITECTURE.md:266` and `:447` and `SECURITY.md:41-42` state the rule and stay true. `docs/ARCHITECTURE.md:499` (bundle contents) is unchanged.

## Architecture check

The guarantee that the monitored server reaches only the ingest port is kept, pinned by tests and by the policy's own `tests`. It is extended on the server's side: the token does not leave the server while Tailscale is down. Record 0016 is kept. No guarantee is weakened, and `docs/ARCHITECTURE.md` needs no edit.

## Security considerations

- [x] No limits: only the repository's own file is parsed, in a test.
- [x] No user-visible exception: none.
- `IPAddressAllow=` cannot name an interface (fact 14). `RestrictNetworkInterfaces=` would, but needs systemd 250 against the server's 249 (fact 17, not measured), and the spec keeps the unit unchanged. The routes of *Approach* 3 are the control. How they interact with a real tailscaled and with networkd is the follow-up run's (facts 13, 18).

## Decision records

- `docs/decisions/0132-example-tailnet-policy-file-pinned-by-tests-no-default-maintainer-grant.md` (Proposed, extended with the routes)

## Challenge

Answers to the five minor *Challenge notes* of the Devil's Advocate, folded in here:

1. `tagOwners` not pinned: accepted. *TagOwnersAreExact*, plus the README sentence that no tag owner may be a tag.
2. The backend-to-member grant: kept as README prose, marked untested, with no line in the file.
3. Settings missing from the check, and older clients: accepted. `RunWebClient` and `AppConnector` were added; on an unknown flag, upgrade Tailscale rather than drop the flag.
4. Port in two places: accepted. The header comment and README step 2 say so.
5. The revert is not guaranteed: accepted. The probe runs under `setsid -f` with its output in a file, the README does not claim a guarantee, and the settings check follows it.

## Security review

- B1 (token on the provider's network while Tailscale is down): resolved.
  - *Approach* 3 adds the precondition, the routes unit (ordered before the agent and required by it) and the check.
  - *Approach* 6 repeats the check and `ip route get` after the reboot.
  - Spec S7, E5 to E7 and AC7 replace the "timeout via the default route" wording.
  - The area document states the control and the residual.
  - *TailnetRoutesUnit* pins the lines, and the follow-up issue lists the checks.
  - The mechanism is mine to decide: a oneshot unit (*Options considered* 5).
- B2 (`encoding/json` versus Tailscale's parser): resolved.
  - `stripHuJSON` handles string escapes, refuses a BOM and unterminated comments or strings, and has its own tests.
  - `decodeStrict` refuses duplicate keys at every level, compared case-sensitively.
  - Grant and `tests` key sets are exact.
- N1: accepted. The file comment and the area document say "not yet verified".
- N2: accepted. A second `tests` entry denies UDP to 8081; its syntax is fact 9.
- N3: accepted, together with DA note 5.
- N4: accepted, together with DA note 3.

## Out of scope / follow-ups

- The PR says `Refs #43` and does not close it.
- Follow-up issue (opened by the orchestrator): "[Ops] Run the Tailscale checks of the monitored server on production". Its body asks the maintainer to:
  - save `deploy/agent/tailnet-policy.hujson`, then save it once with the grant widened to `tcp:*` and once to `"8081"`, expecting both saves refused (facts 8, 9);
  - run the precondition, routes unit and route checks, plus one networkd restart (fact 18);
  - run the settings check, the checks, the reboot check with `ip route get 100.64.0.1`, and look at key expiry (facts 10 to 12);
  - record the results in *Checks on the server*, then close #43.
