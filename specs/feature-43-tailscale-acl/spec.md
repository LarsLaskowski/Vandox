# Spec: Tailscale on the monitored server and its tailnet policy

Source: Issue #43
Status: Draft

## Problem

The monitored server is internet-facing. If it is ever compromised, it must reach nothing in the home network or the tailnet except
the ingest port of the backend host ([record 0010](../../docs/decisions/0010-tailscale-with-strict-acl.md), security area 2 of
`.squad/project.md`). Setting up Tailscale on the server and the tailnet policy is a manual task of the maintainer; the repository
supplies the procedure and an example policy.

Most of it is already documented: `deploy/agent/README.md`, *Tailscale on the monitored server*, came with PR #324 and was run by the
Product Manager on a real tailnet (install from Tailscale's package repository, tags `tag:vandox-agent` and `tag:vandox-backend`, the
replaced default rule, `tailscale up --advertise-tags`, Tailscale DNS off, the checks). Measured against the issue, these gaps remain:

- There is no example policy **file** in `deploy/`, only a fragment (`tagOwners`, three `grants`) inside the README; the maintainer
  has to assemble a complete policy by hand.
- What the server's Tailscale is still allowed to do on its own (Tailscale SSH, advertised routes, an exit node, accepted routes) is
  stated as advice ("needs neither `--ssh` nor any other option"), with no check that shows the server's settings, and no statement
  of what the policy does when a compromised root turns one of them on.
- The checks probe one other port of the backend host (22) only; not the web port, another tailnet device or a home-LAN address.
  The positive side (`404` and `401` from the ingest port) has not been run yet: the backend was `vandoxd` v0.1.0 (README, *Checks on
  the server*, row *Transmission to the backend*).
- The behavior after a reboot of the server was measured for the agent's unit only (row *Reboot*), never for Tailscale: whether it
  comes back by itself with its tag, its DNS setting and the same reachability.
- Key expiry is described by one sentence citing Tailscale's documentation, not checked on the server's entry in the admin console,
  and without what the maintainer does when the key must be replaced.
- The issue lets maintainer devices reach the web UI port. The README grants them **every** port of the backend host
  (`autogroup:member` to `tag:vandox-backend`, `"ip": ["*"]`), while [record 0016](../../docs/decisions/0016-web-ui-in-home-lan-with-login.md)
  and the network area say the web UI is reachable in the home LAN only, access from outside the home needing a separate decision.
  This is a related inconsistency of the current documentation; the Product Manager settled it (Q8): no web UI over the tailnet,
  and the README's "every port" grant goes.

## Scenarios

- S1: Given a tailnet whose policy still holds the default rule, when the maintainer replaces the policy with the example policy from
  `deploy/` (the ingest port and, where the file says so, the maintainer source filled in) and saves it in the admin console, then
  the console accepts it, and the README explains each entry and what the default rule covered that may now need its own rule.
- S2: Given the server joined with `tag:vandox-agent`, when the maintainer runs the documented checks on the server, then the ingest
  port answers (`404` for `GET /`, `401` for a `POST /v1/batches` without a token), and every other probed target is blocked
  (`No route to host` or a timeout): another port of the backend host (SSH and the web port), another tailnet device, and a home-LAN
  address behind a subnet router or an exit node where the tailnet has one (the tailnet has other devices, Q9; the checks name
  how to pick a target among them and do not assume there are none).
- S3: Given a maintainer device in the tailnet and the example policy as shipped, when the maintainer opens the web UI or any other
  port of the backend host at its tailnet address, then the connection is blocked: the example grants maintainer devices nothing on
  `tag:vandox-backend`, and the web UI stays reachable in the home LAN only (record 0016). The file carries commented, opt-in lines
  (for example SSH to the backend host) that the maintainer enables on purpose, each with what it opens, and none for the web port.
- S4: Given the set-up server, when the maintainer runs the documented settings check, then it shows Tailscale SSH off, no advertised
  routes, not an exit node, routes not accepted, Tailscale DNS off and the tag `tag:vandox-agent`, each with the value to expect.
- S5: Given the set-up server, when it reboots, then Tailscale comes up by itself without a login, the settings of S4 are unchanged,
  the checks of S2 give the same results, and the agent sends again; records produced while the tailnet was not yet up are sent
  from the spool afterwards.
- S6: Given the server's entry in the admin console, when the maintainer looks at its key expiry, then it shows expiry disabled as
  the README says, and the README states why (a tagged node, not a person's login) and how to replace the node's key deliberately
  (remove the machine, join again with the tag).

## Errors and edge cases

- E1: The default rule (or any rule with `*` or `autogroup:tagged` as source) is still in the policy: a check of S2 reaches a
  blocked target (`Connection refused`, exit `1`, or `0`); the README names this cause and the fix.
- E2: The server joined without the tag: `tailscale whois` names a user instead of `tag:vandox-agent`; the README says to remove it
  and join again with the tag.
- E3: The ingest port written without protocol (`"8081"`) or as `tcp:*`: the example policy never uses these forms, and the README
  says that they allow more.
- E4: A compromised root on the server turns on Tailscale SSH, advertises a route or an exit node, accepts routes, or selects an
  exit node: the policy still lets the server reach only the ingest port (no `ssh` rule, no `autoApprovers` entry, no grant to a
  subnet or `autogroup:internet` matches `tag:vandox-agent`); the README says so and names the rules that must never be added.
- E5: After a reboot the agent starts before Tailscale has its address: its first sends fail as `connection failed`, the records stay
  in the spool and are sent once the tailnet is up.
- E6: After a reboot Tailscale does not come up (logged out, the node removed, the service disabled): `tailscale status` shows it,
  the agent keeps spooling, and the README names the check and the fix.
- E7: The maintainer edits the policy later (another device, another tag) so that a second rule matches the server: the README's
  rule "no other rule may have a source that matches this server" and the checks of S2 are what catches it; the example file
  carries the same warning next to the agent's grant.

## Non-functional requirements

- Security: the example policy is part of security area 2 (*Tailscale ACL and port binding*, "the documented ACL"); nothing in it
  may let a source matching `tag:vandox-agent` reach more than TCP to the ingest port of `tag:vandox-backend`.
- No change to the agent, its unit, its install scripts or the backend; no new configuration.
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
  (S1, E3, E4, E7). Verified by review and, as an open maintainer step, by saving it in the tailnet's policy.
- [ ] AC2: the README's checks cover the targets of S2 with the result to expect for each and the cause of a wrong result; their
  results on the production server are marked as not yet run (S2, E1, E2).
- [ ] AC3: neither the example policy nor the README grants maintainer devices (`autogroup:member` or any other source) access to
  `tag:vandox-backend` other than through commented opt-in lines, none of them for the web port; the README's `"ip": ["*"]` grant is
  removed, the README says what the maintainer loses over the tailnet by that and how to opt in, and the network area states that
  the web UI is not offered on the tailnet (S3).
- [ ] AC4: the README has a settings check that shows each value of S4 with the value to expect, marked as not yet run on the
  production server (S4).
- [ ] AC5: the README describes the reboot check of S5 with the results to expect, including the agent's sends after the tailnet is
  up again, marked as not yet run (S5, E5, E6).
- [ ] AC6: the README states the key expiry of the tagged server, why, how to check it in the admin console and how to replace its
  key; the check on the server's entry is marked as not yet run (S6).

## Out of scope

- The actual setup of the production server and tailnet: a manual task of the maintainer (issue, *Notes*).
- Tailscale on the backend host (TUN mode, Synology): documented under issue #40 and record 0127.
- The older `acls` policy form, and a grant that lets the server reach the tailnet's name servers.
- Any change to the agent, its unit (`IPAddressAllow=`) or the backend.

## Open questions

- Q1: Must the tag be `tag:webserver` as in the issue? Answered: no, the issue says "e.g."; the documentation and the network area
  already use `tag:vandox-agent` and `tag:vandox-backend`, which stay.
- Q2: Is the install from the official apt repository documented? Answered: yes, `deploy/agent/README.md` step 1 links Tailscale's
  Linux guide and its package repository method.
- Q3: Which port is the ingest port? Answered: container port 8081, published on `INGEST_BIND_ADDRESS`
  (`deploy/backend/docker-compose.yml`); the web port is 8080, published on `127.0.0.1` or `WEB_BIND_ADDRESS`.
- Q4: Does an example ACL policy exist in `deploy/`? Answered: only a fragment inside `deploy/agent/README.md` step 2, not a policy
  file.
- Q5: Has "only the ingest port is reachable" been tested? Answered: partly; the PM's run showed port 22 blocked (`No route to host`,
  timeout), but the ingest side not, since the backend was v0.1.0 (README, *Checks on the server*).
- Q6: Has the reboot been tested? Answered: for the agent's unit only (README, row *Reboot*); never for Tailscale.
- Q7: Is key expiry off for the tagged server? Answered: the README cites Tailscale's documentation (tagged devices have key expiry
  disabled by default); not yet checked on the server's entry, so S6 records it from the run.
- Q8: What may maintainer devices reach on the backend host over the tailnet? Answered: by the Product Manager, option (C): no web UI
  over the tailnet, record 0016 stays as it is; maintainer devices get only what the maintainer adds on purpose, as commented opt-in
  lines, and the README's `autogroup:member` to `tag:vandox-backend` `"ip": ["*"]` grant is removed.
- Q9: What do the target server and the tailnet have today? Answered: by the Product Manager: the production server is already in
  the tailnet with `tag:vandox-agent`; the tailnet has other devices (which kinds was not stated, so none may be assumed absent); how
  the policy is edited was not stated (both ways are documented neutrally); the reboot test and the extended checks are not run on
  production now and stay open maintainer verification.

## Area documents

- `docs/areas/network-and-security-model.md`, *Agent to backend* and *Web UI*: the example policy file as the source of the rules,
  what the policy does against a compromised root's Tailscale settings (E4), the behavior after a reboot (S5, E5), and the maintainer
  devices' access: none to the backend host over the tailnet by default, the web UI not offered on the tailnet (Q8).
- `deploy/agent/README.md`, *Tailscale on the monitored server* (the procedure, not an area document): the policy file, the removed
  `"*"` grant and the opt-in lines, the settings check, the extended checks, the reboot check and key expiry, each marked as not yet
  run on the production server.
