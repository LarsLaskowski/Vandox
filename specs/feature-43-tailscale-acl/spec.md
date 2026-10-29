# Spec: Tailscale on the monitored server and its tailnet policy

Source: Issue #43
Status: Approved by Product Manager

## Problem

The monitored server is internet-facing. If it is ever compromised, it must reach nothing in the home network or the tailnet except
the ingest port of the backend host ([record 0010](../../docs/decisions/0010-tailscale-with-strict-acl.md), security area 2 of
`.squad/project.md`). Setting up Tailscale on the server and the tailnet policy is a manual task of the maintainer; the repository
supplies the procedure and an example policy.

Most of it is already documented in `deploy/agent/README.md`, *Tailscale on the monitored server* (PR #324, run by the Product
Manager on a real tailnet). The gaps against the issue: no complete example policy **file** in `deploy/`, only a README fragment;
no check or statement of the server's own Tailscale settings (SSH, routes, exit node); checks that probe port 22 only, with the
ingest side never run (Q5); no reboot check for Tailscale (Q6); key expiry unchecked and without a replacement procedure (Q7); and a
README grant of **every** backend-host port to maintainer devices that disagrees with
[record 0016](../../docs/decisions/0016-web-ui-in-home-lan-with-login.md) (settled by the Product Manager, Q8).

## Scenarios

- S1: Given a tailnet whose policy still holds the default rule, when the maintainer replaces the policy with the example policy from
  `deploy/` and saves it, then
  the console accepts it, and the README explains each entry and what the default rule covered that may now need its own rule.
- S2: Given the server joined with `tag:vandox-agent`, when the maintainer runs the documented checks on the server, then the ingest
  port answers (`404` for `GET /`, `401` for a `POST /v1/batches` without a token), and every other probed target is blocked
  (`No route to host` or a timeout): another port of the backend host (SSH and the web port) on its IPv4 and on its IPv6 tailnet
  address, and another tailnet device. Checks the policy drives show the rest: `tailscale status` on the server lists the backend
  host as its only peer, and `tailscale exit-node list` shows no exit node the server may use; optionally, a deliberate
  `tailscale set --accept-routes=true` with a probe of a home-LAN address behind a subnet router, reverted right after, shows a
  subnet route refused, with its caveat (a route that overlaps the hosting provider's network would redirect the server's own
  traffic). The checks do not assume the tailnet has no other devices (Q9); each names the Tailscale page it rests on and is marked
  as not yet run.
- S3: Given a maintainer device in the tailnet and the example policy as shipped, when the maintainer opens the web UI or any other
  port of the backend host at its tailnet address, then the connection is blocked: the example grants maintainer devices nothing on
  `tag:vandox-backend`, and the web UI stays reachable in the home LAN only (record 0016). The file carries commented, opt-in lines
  (for example SSH to the backend host) that the maintainer enables on purpose, each with what it opens, and none for the web port.
- S4: Given the set-up server, when the maintainer runs the documented settings command (one `tailscale set` line that turns
  Tailscale SSH off, accepts no routes, advertises no routes and no exit node), and then the settings check, then the check shows
  Tailscale SSH off, no advertised routes, not an exit node, routes not accepted, Tailscale DNS off and the tag `tag:vandox-agent`,
  each with the value to expect; the exact output format of the read command is marked as not checked in this project.
- S5: Given the set-up server, when it reboots, then Tailscale comes up by itself without a login, the settings of S4 and the
  routes of S7 are unchanged, `ip route get 100.64.0.1` names `tailscale0`, the checks of S2 give the same results, and the agent
  sends again, records of the window from the spool.
- S7: Given a server whose provider uses nothing in `100.64.0.0/10` or `fd7a:115c:a1e0::/48` (a documented precondition check),
  when the maintainer follows the README step that comes before the agent's installation, then the main routing table holds
  `unreachable` routes for both ranges, set at every boot before `vandox-agent` starts, and a documented check prints
  `unreachable 100.64.0.0/10` (and the IPv6 line); Tailscale's own routes still win while it is up. Marked as not yet run.
- S6: Given the server's entry in the admin console, when the maintainer looks at its key expiry, then it shows expiry disabled as
  the README says, and the README states why (a tagged node, not a person's login) and how to replace the node's key deliberately:
  remove the machine and join again with the tag, which gives the server a new tailnet address and identity, so the `tailscale whois`
  check of step 3 is repeated, while the agent's `backend.url` (the backend's address) stays valid; `tailscale up --force-reauth
  --advertise-tags=tag:vandox-agent` is named as an alternative, marked as untested.

## Errors and edge cases

- E1: A second rule matches the server (the default rule, a source `*` or `autogroup:tagged`, or a later edit): a check of S2
  reaches a blocked target (`Connection refused`, exit `1`, or `0`); the README names the cause and the fix, and the example file
  warns next to the agent's grant.
- E2: The server joined without the tag: `tailscale whois` names a user instead of `tag:vandox-agent`; the README says to remove it
  and join again with the tag.
- E3: The ingest port written without protocol (`"8081"`) or as `tcp:*`: the example policy never uses these forms, and the README
  says that they allow more.
- E4: A compromised root on the server turns on Tailscale SSH, advertises a route or an exit node, accepts routes, or selects an
  exit node: the policy still lets the server reach only the ingest port (no `ssh` rule, no `autoApprovers` entry, no grant to a
  subnet or `autogroup:internet` matches `tag:vandox-agent`); the README says so and names the rules that must never be added.
- E5: Tailscale is not up (before its netmap after a boot, stopped, logged out, the node removed): with the routes of S7 every send
  fails at once with no route to host, logged as `connection failed`, so the token and the batch never leave the server; the records
  stay in the spool and are sent once the tailnet is up. Without them a `100.x` destination follows the default route, and whoever
  answers there gets the token and the batch in clear: the README and the network area name this residual.
- E6: After a reboot Tailscale does not come up: `tailscale status` shows it, the agent keeps spooling as in E5, and the README names
  the check and the fix.
- E7: The provider's network uses an address in one of the two ranges: the precondition check shows it, and the README says not to
  add the routes and names the residual of E5.

## Non-functional requirements

- Security: the example policy is part of security area 2 (*Tailscale ACL and port binding*, "the documented ACL"); nothing in it
  may let a source matching `tag:vandox-agent` reach more than TCP to the ingest port of `tag:vandox-backend`.
- No change to the agent, its unit file, its install scripts or the backend; no new configuration. The routes of S7 are an operator
  step of the README.
- Compatibility: Ubuntu 22.04 on the server, Tailscale from its official package repository, the policy in the `grants` form in
  HuJSON as the admin console stores it (the older `acls` form stays undocumented as tested).
- Documented procedure (*Operator environments and documented procedures*, `.squad/routing.md`): every statement no run has shown is
  marked as such; the Product Manager's run on the real server and tailnet produces the results recorded for S2, S4, S5 and S6.
- Target host and tailnet (Q9): the production server is already in the tailnet with `tag:vandox-agent`, so the procedure serves both
  a first join and the verification of a joined server; the tailnet has other devices, whose kinds are not known, so the example
  policy and the README treat subnet routers, exit nodes and other tagged devices as possibly present; the policy may be edited in
  the admin console or through GitOps, so the file is plain HuJSON usable in both and the README names neither as required.
- Verification: the extended checks, the settings check and the reboot check are not run on the production server in this change.
  They are documented with the result to expect and marked in the README as not yet run, an open verification for the maintainer.

## Acceptance criteria

- [ ] AC1: `deploy/` contains a complete example tailnet policy file that the admin console accepts; its only rule whose source
  matches `tag:vandox-agent` allows `tcp:<ingest port>` to `tag:vandox-backend`, and it has no `ssh` rule, `autoApprovers` entry
  or grant to a subnet or `autogroup:internet` that matches the server; the README's step 2 refers to it and holds no diverging copy
  (S1, E1, E3, E4). Verified by review and, as an open maintainer step, by saving it in the tailnet's policy.
- [ ] AC2: the README's checks cover the probes and the policy-driven checks of S2 (IPv4 and IPv6, the peer list, the exit-node list,
  the optional reverted subnet-route probe with its caveat), each with the result to expect, the Tailscale page it rests on and the
  cause of a wrong result; their results on the production server are marked as not yet run (S2, E1, E2, E4).
- [ ] AC3: neither the example policy nor the README grants maintainer devices (`autogroup:member` or any other source) access to
  `tag:vandox-backend` other than through commented opt-in lines, none of them for the web port; the README's `"ip": ["*"]` grant is
  removed, and the README says what the maintainer loses by that and how to opt in. The network area states what the policy
  enforces and the subnet-route case, as *Area documents* outlines; record 0016 stays unchanged (S3).
- [ ] AC4: the README has the settings command and the settings check of S4, each value with the value to expect, the read
  command's output format marked as not checked, and the result marked as not yet run on the production server (S4).
- [ ] AC5: the README describes the reboot check of S5 with the results to expect, including the agent's sends after the tailnet is
  up again, marked as not yet run (S5, E5, E6).
- [ ] AC6: the README states the key expiry of the tagged server, why, how to check it in the admin console and how to replace its
  key with the consequences of S6 (new address, repeated `whois` check, `backend.url` unaffected, the untested `--force-reauth`
  alternative); the check on the server's entry is marked as not yet run (S6).
- [ ] AC7: the README has the step of S7 before the agent's installation: the precondition check, the persistent routes for both
  ranges ordered before `vandox-agent`, and the check, all marked as not yet run; the reboot step repeats the check and
  `ip route get 100.64.0.1`; the network area states the control and the residual without it (S7, S5, E5, E6, E7).

## Out of scope

- The actual setup of the production server and tailnet: a manual task of the maintainer (issue, *Notes*).
- Tailscale on the backend host (TUN mode, Synology): documented under issue #40 and record 0127.
- The older `acls` policy form, and a grant that lets the server reach the tailnet's name servers.
- Any change to the agent, its unit (`IPAddressAllow=`) or the backend.

## Open questions

- Q1: Must the tag be `tag:webserver`? Answered: no ("e.g."); `tag:vandox-agent` stays. The apt install is README step 1.
- Q3: Which ports? Answered: ingest 8081, web 8080 (compose file).
- Q4: Does an example policy exist in `deploy/`? Answered: only a fragment in README step 2.
- Q5: Was "only the ingest port" tested? Answered: port 22 blocked only; the ingest side not (backend v0.1.0, README).
- Q6: Was the reboot tested? Answered: for the agent's unit only (README, row *Reboot*).
- Q7: Is key expiry off for the tagged server? Answered: by Tailscale's documentation (README step 3); not checked on the server.
- Q8: What may maintainer devices reach on the backend host over the tailnet? Answered: by the Product Manager, option (C): only
  what the maintainer adds on purpose (commented opt-in lines), no web UI over the tailnet, record 0016 unchanged.
- Q9: What do the target server and the tailnet have today? Answered: by the Product Manager, as the target-host requirement above
  records.

## Area documents

- `docs/areas/network-and-security-model.md`, *Agent to backend* and *Web UI*: the example policy file as the source of the rules,
  what the policy does against a compromised root's Tailscale settings (E4), the behavior after a reboot (S5, E5), and the maintainer
  devices' access: none to the backend host's tailnet address by default (only the agent's `tcp:<ingest port>`), and a subnet-route
  grant to the home LAN gives those tailnet devices the web UI at its LAN address as if in the home LAN (Q8, M2); the unreachable
  routes of S7 and the residual without them (E5).
- `deploy/agent/README.md`, *Tailscale on the monitored server* (the procedure): every step above, marked as not yet run.

## Challenge

- M1 (major, the home-LAN probe passes with a wrong policy): accepted. S2 and AC2 replace it with checks the policy drives (the peer
  list, `tailscale exit-node list`, an optional reverted `--accept-routes=true` probe with its caveat), each naming its Tailscale
  page and marked as not yet run; AC2 now covers E4.
- M2 (major, "not offered on the tailnet" untrue in general): accepted by narrowing, no new decision. AC3 and the area outline state
  only what the policy enforces, and that a subnet-route grant to the home LAN puts those devices in the home LAN's position, where
  record 0016 already allows the web UI with its login. Record 0016 stays unchanged (Q8).
- Minor 1 (issue closure): accepted for the plan: the PR references #43 without closing it, and the plan names a follow-up issue
  for the maintainer's run of S2, S4, S5 and S6.
- Minor 2 (set as well as check): accepted; S4 and AC4 add the `tailscale set` line, the read output format marked as not checked.
- Minor 3 (pin the file with a test): accepted for the plan, which names a Go test reading the file as HuJSON and asserting AC1.
- Minor 4 (path, security area list, bundle): accepted for the plan: exact path, the file added to security area 2 in
  `.squad/project.md`, and, under `deploy/agent/`, not in the install bundle (`MANIFEST_NAMES`, `check-agent-bundle.sh`).
- Minor 5 (E5/E6 failure and the token): accepted; Security's plan review then required a control (B1): S7, E5–E7 and AC7.
- Minor 6 (IPv6): accepted; S2 and AC2 add a blocked probe to the backend host's IPv6 tailnet address.
- Minor 7 (key replacement): accepted; S6 and AC6 name the new address, the repeated `whois` check, the unaffected `backend.url`
  and the untested `--force-reauth` alternative.
