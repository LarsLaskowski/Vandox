# Plan: Tailscale on the monitored server and its tailnet policy

Source: Issue #43 | [spec.md](spec.md)
Status: Draft
Tier: security — the change rewrites the documented tailnet policy, the allow-list that decides what a compromised server may reach (it removes a grant and adds the example policy file that becomes its one source).
Security areas touched: 2 *Tailscale ACL and port binding* (the documented ACL: new `deploy/agent/tailnet-policy.hujson`, `deploy/agent/README.md`); `.squad/project.md` *Security areas* (area 2's file list extended)

## Problem / root cause

See [spec.md](spec.md), *Problem*; criteria AC1–AC6 are the spec's.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The README holds only a policy fragment and grants `autogroup:member` every port of `tag:vandox-backend` | `deploy/agent/README.md:26-36`, line 34 `"ip": ["*"]` | confirmed |
| 2 | A new file in `deploy/agent/` does not enter the install bundle | `.github/scripts/package-agent.sh:17` and `.github/scripts/check-agent-bundle.sh:12-14` list the bundle files by name (`PLAIN_FILES`, `EXECUTABLES`, `MANIFEST_NAMES`) | confirmed |
| 3 | `deploy/agent` is a test-only Go package whose tests read documents; `readRepositoryText` and `normalizeLineEnds` exist | `.squad/stack.md:45`; `deploy/agent/service_test.go:680-695` | confirmed |
| 4 | `*.hujson` is checked out with LF | `.gitattributes` (`* text=auto eol=lf`; CRLF only for `*.cs`, `*.razor`, `*.md`) | confirmed |
| 5 | No HuJSON library is a dependency | `go.mod` has no `tailscale/hujson` | confirmed |
| 6 | The ingest port is 8081 on the host side | `deploy/backend/docker-compose.yml:71` (`${INGEST_BIND_ADDRESS}:8081:8081`) | confirmed |
| 7 | `tailscale set` has `--ssh`, `--accept-routes`, `--advertise-routes`, `--advertise-exit-node`, `--exit-node`, `--advertise-connector`, `--webclient`, `--accept-dns`; `tailscale debug prefs`, `tailscale exit-node list` and `tailscale up --force-reauth` exist | Tailscale source, `cmd/tailscale/cli/set.go:77-95`, `debug.go:257`, `exitnode.go:33`, `up.go:149` (shallow clone of `main`, 1.105.0, 2026-10-29); the server's installed version is not known | confirmed |
| 8 | The admin console and GitOps accept HuJSON (`//` comments, trailing commas) as the policy file | to verify: the maintainer saves the file (follow-up issue, below) (now: tailscale.com is not reachable from the session) | unverified |
| 9 | A policy `tests` entry with a tag as `src` and `tag:host:port` in `accept`/`deny` is checked against `grants` when the policy is saved, and a failing test blocks the save | to verify: the maintainer saves the file, then once with the agent's grant changed to `tcp:*` and sees the save refused (follow-up issue) (now: not reachable from the session) | unverified |
| 10 | A tagged node has key expiry disabled by default | to verify: the server's entry in the admin console (follow-up issue) (now: README step 3 cites tailscale.com/kb/1068) | unverified |
| 11 | `tailscale status` on the server lists only peers the policy lets it reach or be reached by, and `tailscale exit-node list` only exit nodes it may use | to verify: the maintainer's run (follow-up issue); the README marks both expectations as not yet run (now: Tailscale's peer visibility is computed by the closed coordination server) | unverified |
| 12 | Tailscale comes back after a reboot with its tag and prefs (`tailscaled.service` enabled by the package, state in `/var/lib/tailscale`) | to verify: the maintainer's reboot check (follow-up issue) (now: not measured) | unverified |

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestTailnetPolicy_StripsHuJSON` (helper: line and block comments, `//` inside a string kept, trailing commas before `}` and `]`, CRLF input); `TestTailnetPolicy_HasOnlyKnownSections`; `TestTailnetPolicy_AgentHasOnlyTheIngestGrant`; `TestTailnetPolicy_SourcesAreExactlyTheKnownOnes`; `TestTailnetPolicy_TestsPinTheIngestPortOnly`; `TestAgentReadme_TailscaleStepsReferThePolicyFile` | `deploy/agent/tailnet_policy_test.go` |
| AC2 | `TestAgentReadme_TailscaleChecksNameEveryProbe` | `deploy/agent/tailnet_policy_test.go` |
| AC3 | `TestTailnetPolicy_GrantsNothingOnTheBackendButIngest`; `TestTailnetPolicy_OptInLinesAreCommentedSSHOnly`; `TestAgentReadme_TailscaleStepsHoldNoWideGrant` | `deploy/agent/tailnet_policy_test.go` |
| AC4 | `TestAgentReadme_TailscaleSettingsLineAndCheck` | `deploy/agent/tailnet_policy_test.go` |
| AC5 | `TestAgentReadme_TailscaleRebootCheck` | `deploy/agent/tailnet_policy_test.go` |
| AC6 | `TestAgentReadme_TailscaleKeyExpiry` | `deploy/agent/tailnet_policy_test.go` |

What each asserts, on `deploy/agent/tailnet-policy.hujson` parsed with `encoding/json` after the strip helper, and on the README section *Tailscale on the monitored server* read with `readRepositoryText`:

- *HasOnlyKnownSections*: the top-level keys are exactly `tagOwners`, `grants`, `tests` (so no `acls`, `ssh`, `autoApprovers`, `nodeAttrs`, `hosts`, `ipsets`, `postures`).
- *AgentHasOnlyTheIngestGrant*: exactly one grant has `tag:vandox-agent` in `src`, and it is `src` `["tag:vandox-agent"]`, `dst` `["tag:vandox-backend"]`, `ip` `["tcp:8081"]`, no `app`/`via`/`srcPosture` key.
- *SourcesAreExactlyTheKnownOnes*: the set of all `src` values of all grants is exactly `{tag:vandox-agent, autogroup:member}`. This is the guard against a second rule matching the server: Tailscale's source selectors are `*`, user logins, `group:`, `tag:`, `autogroup:` (`member`, `tagged`, `admin`, …), host aliases, IP addresses and CIDRs, and `ipset:`; of these, `*`, `autogroup:tagged`, `tag:vandox-agent`, a CIDR or IP covering `100.64.0.0/10` or `fd7a:115c:a1e0::/48`, a host alias or an ipset can match the server, and every one of them fails the exact-set comparison. `autogroup:member` does not match a tagged node (README step 2, Tailscale kb/1068; same status as fact 10).
- *GrantsNothingOnTheBackendButIngest*: no grant other than the agent's has `tag:vandox-backend` in `dst`; no `dst` is `*`, `autogroup:internet`, an IP or a CIDR.
- *OptInLinesAreCommentedSSHOnly*: the comment lines that contain `"dst"` are exactly one, the SSH opt-in of *Approach*; it names `tcp:22` and neither `8080` nor `443`.
- *TestsPinTheIngestPortOnly*: `tests` has one entry, `src` `tag:vandox-agent`, `accept` `["tag:vandox-backend:8081"]`, `deny` containing `tag:vandox-backend:22`, `tag:vandox-backend:8080` and `tag:vandox-backend:443`.
- README tests: the section names `tailnet-policy.hujson`; holds no `"grants"` and no `"ip": ["*"]`; names `tailscale debug prefs`, the settings line of *Approach*, `tailscale exit-node list`, `tailscale status`, an IPv6 probe (`fd7a:115c:a1e0::`), the accept-routes probe as **one line** containing both `--accept-routes=true` and `--accept-routes=false`, `sudo reboot`, `--force-reauth`, and the words "not yet run" in each of the steps for S2, S4, S5 and S6.

Documented shell procedure (*Operator environments and documented procedures*): the lines use placeholders (`100.64.0.1`), no shell variables, so (d) a lost variable cannot happen. (a) Pasted at once: each line is independent and read-only except the settings line and the accept-routes probe; both are safe pasted with the others. (b) One at a time: the intended use. (c) One line fails: a failed check prints its result and changes nothing; the accept-routes probe is one line `tailscale set --accept-routes=true; timeout 5 bash -c '</dev/tcp/<LAN address>/<port>'; echo $?; tailscale set --accept-routes=false`, so the revert runs whatever the probe did, also when an overlapping route has cut the maintainer's own SSH session — held by the one-line assertion above. Rows only CI or a real host can run: none in CI; everything on the server is the maintainer's run (follow-up issue).

## Approach

1. **The policy file** `deploy/agent/tailnet-policy.hujson`, LF, a header comment (what the file is, that it replaces the whole policy, works in the admin console and in a GitOps repository alike, and that every other device the old default rule covered — subnet routers, exit nodes, other tagged devices, the maintainer's own — needs its own rule added here, never with a source that matches the server). Uncommented content, exactly:
   - `tagOwners`: `tag:vandox-agent` and `tag:vandox-backend`, each `["autogroup:admin"]`;
   - `grants`: `{"src": ["tag:vandox-agent"], "dst": ["tag:vandox-backend"], "ip": ["tcp:8081"]}` with the comment that it is the agent's only rule and the port must stay `tcp:<port>`; `{"src": ["autogroup:member"], "dst": ["autogroup:self"], "ip": ["*"]}` (own devices reach each other); then the commented opt-in `// {"src": ["autogroup:admin"], "dst": ["tag:vandox-backend"], "ip": ["tcp:22"]},` with the comment that it opens SSH to the backend host from admins' devices over the tailnet, and that the web UI stays home-LAN only (record 0016), so no line opens its port;
   - `tests`: the one entry of *TestsPinTheIngestPortOnly*, commented as what makes the console refuse a later edit that widens the server.
2. **README, step 2** (*Prepare the tailnet policy*): the fragment is replaced by a reference to the file and an explanation per entry; the `"*"` grant is gone, with what the maintainer loses over the tailnet (web UI, SSH, file services of the backend host; the home LAN as before) and the opt-in line; the warnings of today's step 2 stay (default rule, `tcp:<port>`, no other source matching the server), plus: no `autoApprovers`, `ssh` rule, grant to a subnet or `autogroup:internet` with such a source. Editing in the console or GitOps is named neutrally.
3. **README, step 3**: after joining, the settings line `tailscale set --ssh=false --accept-routes=false --advertise-routes= --advertise-exit-node=false --exit-node= --advertise-connector=false --webclient=false --accept-dns=false`, then `tailscale debug prefs` with the fields to expect (`RunSSH` false, `RouteAll` false, `AdvertiseRoutes` empty or null, `ExitNodeID` empty, `CorpDNS` false, `AdvertiseTags` `["tag:vandox-agent"]`), its output format marked as not checked. The key-expiry paragraph: why disabled, where the console shows it, replacing the key (remove the machine, join with the tag again: new address and identity, repeat the `whois` check, `backend.url` unaffected; `tailscale up --force-reauth --advertise-tags=tag:vandox-agent` as untested alternative, not over a session that runs through Tailscale).
4. **README, step 4** (checks): today's lines plus the web port and SSH on the backend's IPv6 tailnet address, a probe of another tailnet device, `tailscale status` (backend host the only peer), `tailscale exit-node list` (no usable exit node), the optional one-line accept-routes probe with its caveat; each with the expected result, the Tailscale page it rests on (kb/1018, kb/1019, kb/1103) and the cause of a wrong one.
5. **README, new step 5** (after a reboot): `sudo reboot`, then `tailscale status`, `tailscale debug prefs`, the checks of step 4, `systemctl is-active vandox-agent` and the agent's journal (sends fail as a failed connection or a timeout until the tailnet is up, then succeed from the spool).
6. Every result of steps 2–5 on the production server is marked "not yet run"; the *Checks on the server* table gets a row per check with "not yet run (issue #<follow-up>)".

Layout of new README text: the steps keep today's numbered list with fenced command blocks and bullets for results; the Dev words it, the Reviewer checks it against this list, approved in step 9.

## Options considered

1. **Where the file lives.** `deploy/agent/tailnet-policy.hujson` (chosen): colocated with the test-only package that pins it and the README that explains it; kept out of the bundle by the explicit file lists (fact 2). `deploy/tailscale/policy.hujson` lost: a new directory whose test would live in another package's folder (`../tailscale`), against *Layout*.
2. **Reading HuJSON in the test.** A small strip helper plus `encoding/json` (chosen), itself tested. `github.com/tailscale/hujson` lost: a new dependency (a security area of its own, govulncheck surface) for one test.
3. **Policy `tests` section.** Included (chosen): the console then refuses a later edit that widens the server (E1, "a later edit"); if the syntax is wrong, the save fails visibly, which fails closed. Leaving it out lost: the only guard against a later edit would be the maintainer's manual checks. Residual: fact 9 unverified.
4. **Settings check command.** `tailscale debug prefs` (chosen): present in long-released versions. `tailscale get all` lost: new in current `main`, likely absent on the server's version.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| deploy/agent | `tailnet-policy.hujson` | new: the example policy |
| deploy/agent | `tailnet_policy_test.go` | new: the tests above (Tester) |
| deploy/agent | `README.md` | steps 2–5, key expiry, *Checks on the server* rows |

## Signatures (for the Dev's skeleton)

none

## Test files

`deploy/agent/tailnet_policy_test.go` (package `agent`, named after the file it pins, as `service_test.go` for the unit); its strip helper is the Tester's own test code, no production API.

Existing test code that calls a changed signature: none.

Files no unit test can cover: none (the policy file and README are pinned by the tests; no production Go code, so the *Coverage gate* has no new lines).

## Areas

- `docs/areas/network-and-security-model.md`, *Agent to backend*: the example policy file as the source of the rules; its `tests` entry; what the policy does when a compromised root changes the server's Tailscale settings (E4); behavior after a reboot (E5, E6). *Web UI*: the policy grants no source access to the backend host's tailnet address except the agent's `tcp:<ingest port>`; a grant to the home-LAN subnet through a subnet router gives those tailnet devices the web UI at its LAN address as if in the home LAN. *Related decisions*: record 0132.

## Documentation updates

Owner: Dev, after the tests are green.

- `deploy/agent/README.md`: *Approach* 2–6.
- `docs/areas/network-and-security-model.md`: *Areas*.
- `docs/INSTALL.md` step 11 (line 310-312): names `deploy/agent/tailnet-policy.hujson` beside the README link.
- `.squad/project.md` area 2: the heading's file list adds `deploy/agent/tailnet-policy.hujson`, and record 0132 joins its record list.
- `.squad/stack.md` *Layout* (line 45): the `deploy/agent` tests also read the example tailnet policy.
- Sweep result: `docs/ARCHITECTURE.md:266` and `:447`, `SECURITY.md:41-42` state the rule ("only the ingest port") and stay true; `docs/ARCHITECTURE.md:499` lists the bundle, which does not change; no other enumeration names the policy.

## Architecture check

The guarantee "the monitored server reaches only the ingest port" (`.squad/project.md` area 2, ARCHITECTURE *Security model*) is kept and now pinned by tests and the policy's own `tests`. Record 0016 (web UI home-LAN only) is kept; the removed `"*"` grant brings the README back in line with it. No guarantee is weakened; no ARCHITECTURE edit is needed.

## Security considerations

- [x] No limits: the change parses only the repository's own file in a test.
- [x] No user-visible exception: none.
- For Security's plan review (spec E5): while Tailscale is down, a plain-`http` request to `100.x` with the token may follow the default route onto the provider's network (the unit allows `100.64.0.0/10` on any interface). This plan documents the window only; whether it needs a control (for example `IPAddressAllow=` bound to `tailscale0`, which systemd's IP filter cannot express, or a firewall rule) is for Security to rule on.

## Decision records

- `docs/decisions/0132-example-tailnet-policy-file-pinned-by-tests-no-default-maintainer-grant.md` (Proposed)

## Out of scope / follow-ups

- The PR references #43 without closing it (`Refs #43`).
- Follow-up issue (the orchestrator opens it): title "[Ops] Run the Tailscale checks of the monitored server on production"; body: save `deploy/agent/tailnet-policy.hujson` in the tailnet (facts 8, 9: also once with the agent's grant widened, expecting the save refused), run the settings check, the checks of step 4, the reboot check and the key-expiry look (facts 10–12), record the results in `deploy/agent/README.md` *Checks on the server*, then close #43.
