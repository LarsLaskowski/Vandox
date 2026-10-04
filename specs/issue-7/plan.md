# Plan: Fill .squad/project.md and SECURITY.md

Source: Issue #7
Status: Draft
Tier: security — the change defines the *Security areas*, guarantees and secrets policy against which every
later `security`-tier change is reviewed, so Security must review the plan and the diff (when in doubt, the
higher tier); it changes no production or test code.

Why not `docs`: the change edits `.squad/project.md`, which tier `docs` excludes, and it needs decision
records (0028–0032). `trivial` would fit the "documentation that needs a decision record" definition but
skips Security, which is the role this content serves.

## Problem / root cause

`.squad/project.md` still has the seeded content: the *Security areas* are generic code-level areas without
protection goals, *Guarantees* and *Test doubles* say "None", and *Integration surface* lists only internal
change patterns, not the external surfaces the agent and backend talk to. Security, Tester and Reviewer have
nothing concrete to check against.

Claims of the issue, checked:

- "Security role and reviewers rely on `.squad/project.md`" — confirmed (`.squad/routing.md`, *Tiers*:
  tier `security` is defined by *Security areas* in `.squad/project.md`; `docs/UNIT_TESTS.md` points to
  *Test doubles* there).
- "Depends on #5" — confirmed and satisfied: #5 (adopt the template) is closed; `.squad/project.md` and
  `SECURITY.md` exist and `python3 .squad/tools/config-check.py` passes.
- `SECURITY.md` needs a reporting channel and supported versions — **refuted as a gap**: `SECURITY.md`
  already names the reporting channel (e-mail to `Development@e-networld.de`, no public issues,
  acknowledgement within 5 business days) and a supported-versions table. No escalation is needed. It is
  only stale: no release exists yet (`git tag` empty, no GitHub release), its secrets advice ("files readable
  only by the service user") and its *Scope* list do not match the requirements of this issue.
- Guarantees: "backfilled data never raises an alert by itself" — confirmed as already decided (0022,
  `docs/ARCHITECTURE.md` *Offline behavior and backfill*). "No data gaps unless explicitly recorded" — only
  partly covered: 0018 promises a gapless spool and gap *detection*, but nothing says that unavoidable loss
  (agent killed, spool full, collector timeout) is recorded → new record 0028. "A hanging collector or
  database never blocks the agent" — not documented anywhere → new record 0029.
- Security areas: ingest token/limits/deduplication, web UI login, command signing (0009), Tailscale ACL
  (0010, 0017), MariaDB user (0013) — consistent with existing records. Agent privileges (dedicated user,
  capabilities, polkit rule), Telegram allowlist and "secrets only from environment variables / Docker
  secrets" are not recorded anywhere → new records 0030, 0031, 0032. Checked against the issues that
  implement them: the read-side rights are groups `adm` and `systemd-journal` plus documented
  `AmbientCapabilities` (#42); polkit is needed only for restarting configured units (#70, depends on #69),
  not for reading unit state over D-Bus; the Telegram allowlist holds **user** IDs (#60, #73).

Related defect found on the way: `docs/ARCHITECTURE.md` *Security model* says it is "kept in sync with
`SECURITY.md` and the *Security areas* in `.squad/project.md`", but none of the three mention agent
privileges, the Telegram allowlist or the secrets policy; fixed by the documentation updates below.

## Acceptance criteria

No unit test applies (no production or test code changes). Each criterion is checked by the Reviewer and
Security against the diff:

- [ ] AC1: `.squad/project.md` *Security areas* lists all eight areas of the issue (ingest authentication;
  Tailscale ACL; web UI login; command signing for remote actions (later); Telegram allowlist; agent
  privileges; MariaDB monitoring user; secrets handling), each with a **protection goal** and the decision
  records it rests on — and keeps the four existing code-level areas (file writes, parsing of external
  input, outbound calls, logging of external data), each also with a protection goal. Area 6 names both
  capabilities, the confinement and the read-everything residual without exception (every file, every
  process's memory and environment), and labels `ProtectHome=`/`InaccessiblePaths=` as defence in depth
  against accidental reads only, bypassable via `/proc/<pid>/root` (0030); area 11 keeps transport security
  (tailnet only for the agent, HTTPS with certificate verification for Telegram and the AI service,
  certificate verification for TLS checks, never skipped); area 12 covers Telegram messages.
- [ ] AC2: *Guarantees* states the three guarantees (no data gaps unless explicitly recorded; a hanging
  collector or database never blocks the agent; backfilled data never raises an alert by itself), each with
  its record (0028, 0029, 0022) and its `docs/ARCHITECTURE.md` section; "_None recorded yet._" is gone.
- [ ] AC3: *Integration surface* lists the seven external surfaces (`/proc`, `/sys`, systemd D-Bus,
  journald, MariaDB socket, Telegram Bot API, Tailscale network) and *Test doubles* names a double for
  **every** one of them plus the injectable clock; none is left without a double, and each agent-side
  source (`/proc`, `/sys`, D-Bus, journald, MariaDB) also has a hanging variant for 0029 — for `/proc` and
  `/sys` a blocking `fs.FS` wrapper, not only a static fixture tree. "_None yet._" is gone.
- [ ] AC4: `SECURITY.md` contains the reporting channel (unchanged e-mail address and no-public-issue rule),
  supported versions that are true before the first release, deployment advice matching 0030, 0031 and
  0032, a *Scope* list matching the new *Security areas*, and an out-of-scope list that excludes only
  attacks requiring root or physical access (not unprivileged local access to the monitored server).
- [ ] AC5: `docs/ARCHITECTURE.md` describes both new guarantees and the three new security-model points and
  links records 0028–0032.
- [ ] AC6: `python3 .squad/tools/config-check.py` passes and *Format check* passes; no file outside the
  list under *Affected files* changes.

## Approach

The Dev edits three files with the content below (wording may be tightened, content may not be dropped).
No placeholder text, no type or file names that do not exist marked as if they did: everything not yet in
code is marked "(not implemented yet)" or "planned".

### `.squad/project.md`

Keep the title, the intro paragraph and the intro sentence of every section. Replace the section bodies:

**Security areas** (bullet per area: name — what it covers — *Goal:* … — records):

1. **Ingest authentication** (backend ingest API, not implemented yet) — the agent token, request
   limits and deduplication. *Goal:* only an agent holding a valid token can store data; the token is
   compared in constant time; request size, batch size and rate are bounded so a valid agent cannot
   exhaust the backend's memory or disk; a resent batch (same identity and sequence number) is stored once
   and never overwrites stored data. Records 0018, 0032.
2. **Tailscale ACL and port binding** (deployment files under `deploy/backend/` and the documented ACL)
   — *Goal:* a compromised monitored server can reach only the ingest port on the backend host's tailnet
   address and nothing else in the tailnet or home LAN; the ingest port is bound only to the tailnet
   address; the web UI is not offered on the tailnet to the monitored server. Records 0010, 0016, 0017.
3. **Web UI login** (not implemented yet) — password hashing, sessions, CSRF, rate limiting,
   reverse-proxy trust. *Goal:* the password is stored only as a salted, deliberately slow hash; session IDs
   are random, expire, are invalidated on logout and travel only in `HttpOnly`, `Secure`, `SameSite`
   cookies; every state-changing request is CSRF-protected; failed logins are rate-limited; forwarded
   headers (`X-Forwarded-For`, `X-Forwarded-Proto`) are trusted only from the configured reverse proxy, and
   the UI port is reachable only by that proxy. Records 0016, 0023.
4. **Command signing for remote actions (later, not part of v0.1.0)** — *Goal:* the agent executes only
   commands with a valid signature that name an action from its local fixed list and were confirmed by the
   user; a forged, altered or replayed command is rejected; the signing key exists only on the backend host.
   Records 0006, 0009.
5. **Telegram allowlist** (not implemented yet) — *Goal:* the bot sends only to the private chats of
   allowlisted Telegram user IDs and acts on an update (message, command, callback) only when its sender is
   allowlisted and it comes from that user's private chat; every other update, including any from a group,
   is ignored without acting on its content and logged only as sanitized metadata. Updates arrive by
   outbound polling (`getUpdates`) or through a webhook; a webhook is a new inbound endpoint reachable from
   the internet that 0006, 0012 and 0016 do not cover and needs its own decision (choice left to #60).
   Records 0012, 0031.
6. **Agent privileges** (`deploy/agent/`: user, systemd unit; from v0.6.0 the polkit rule) — *Goal:* the
   agent never runs as root; it holds only the group memberships (`adm`, `systemd-journal`), from #42 the
   capabilities `CAP_SYS_PTRACE` and `CAP_DAC_READ_SEARCH` (bounding set limited to exactly these), and —
   once self-healing exists — a polkit rule that allows restarting only the configured units, each listed
   and justified in `deploy/agent/`. While any capability is granted, the unit is confined:
   `NoNewPrivileges=yes`; `SystemCallFilter=` denies at least `ptrace`, `process_vm_readv`,
   `process_vm_writev`, `pidfd_getfd` and `open_by_handle_at`, with `SystemCallArchitectures=native`; no
   write-side capability. So a compromised agent cannot write as or run code as another user. Accepted
   residual, stated openly: its read access equals root's — every file on the server, and the memory and
   environment of every process. `ProtectHome=yes` and `InaccessiblePaths=` (at least `/etc/shadow`,
   `/etc/gshadow`) are defence in depth against accidental reads only, not a limit on a compromised agent,
   which bypasses them through `/proc/<pid>/root`. Records 0013, 0030.
7. **MariaDB monitoring user** — *Goal:* the agent connects over the local socket as `vandox-agent`,
   authenticated by `unix_socket`, with only the `PROCESS` privilege: no database password exists and no
   table data is readable; the agent issues read-only status queries only. Record 0013.
8. **Secrets handling** (backend login, Telegram bot token, agent/ingest token, later the command-signing
   key) — *Goal:* secrets are read only from environment variables or Docker secrets, never from the
   configuration file or the command line; never logged, shown in the UI, written to the spool or put into
   error messages; a secret checked against input (ingest token, later TOTP codes) is compared in constant
   time, the UI password through its hash function's comparison. Record 0032.
9. **File writes and paths derived from external input** (log import, the agent's on-disk spool, database
   backups) — *Goal:* no write outside the configured directories (no path traversal), the spool is
   size-bounded, files are created with restrictive permissions. Record 0018.
10. **Parsing of external input** (log files: journal, syslog, MariaDB, mail, Plesk, web server; the ingest
    wire format; CLI arguments and configuration; later Telegram commands) — *Goal:* malformed or hostile
    input yields an error or a skipped record, never a crash, an unbounded allocation or a hang.
11. **Outbound calls** (Telegram, external checks, the optional AI service of the nightly report, the
    agent's connection to the backend) — *Goal:* every call has a timeout, goes only to its configured
    destination and leaves encrypted to a verified peer. The agent's only destination is the ingest port,
    reached only over the tailnet (WireGuard encryption and node authentication by Tailscale), never over a
    public address. Telegram and the AI service are called over HTTPS with certificate verification;
    external checks verify the certificate whenever they use TLS, and a failed verification is a check
    result. Certificate verification is never switched off (no `InsecureSkipVerify`), and no secret is sent
    over an unencrypted connection. Records 0006, 0008, 0010, 0012, 0017, 0023.
12. **Logging and display of external data** (log lines, process names and text derived from them, e.g. an
    AI-written report) — *Goal:* they cannot inject into log output (control characters, newlines), into
    the web UI (HTML is escaped) or into Telegram messages (escaped for the parse mode used, or sent as plain
    text without a parse mode). Record 0031 for Telegram; none decides log output and UI escaping yet.

**Guarantees** (each: statement, `docs/ARCHITECTURE.md` section, record):

- **No data gaps unless explicitly recorded** — collection is gapless across backend downtime (spool and
  backfill); data that is nevertheless missing (agent stopped, spool full, collector timed out, sequence
  numbers missing) is recorded as a gap and treated as "unknown", never as "normal". *Offline behavior and
  backfill*; 0018, 0028.
- **A hanging collector or database never blocks the agent** — every collector runs in its own goroutine
  under a deadline and is abandoned when the deadline passes (a blocked `/proc` or `/sys` read cannot be
  cancelled, only abandoned; sources that take a context also get it); a hung source (`/proc`, `/sys`,
  D-Bus, journald, MariaDB) loses only its own sample, recorded as a gap, while the other collectors, the
  spool and the sender keep running; while a collector is still stuck it is not started again.
  *Components*; 0029.
- **Backfilled data never raises an alert by itself** — the backend classifies every record as live or
  backfilled from the data; alert rules run on live data only; backfill is stored and analyzed but never
  alerts. *Offline behavior and backfill*; 0022.

**Integration surface**: keep the three existing change patterns unchanged and add a fourth:

**A new or changed external source or destination** (`/proc`, `/sys`, systemd D-Bus, journald, the MariaDB
socket, the Telegram Bot API, the Tailscale network) touches:
- the small interface the code reads it through, and its fake in *Test doubles*
- the deadline that keeps it from blocking the agent (0029) and the gap it records on timeout (0028)
- the privilege it needs in `deploy/agent/` (0030) or the ACL and port binding (0010, 0017)
- the *Security areas* entry it falls under

**Test doubles** — a table, one row per surface, column "status" = "planned (not implemented yet)" for all
rows; the first feature that introduces a surface adds its double under this name and changes the status:

| Surface | Test double |
| ------- | ----------- |
| `/proc` | collectors read through an `io/fs.FS` (production `os.DirFS("/proc")`; symlink targets such as `/proc/<pid>/fd/*` through a link-reading method on the same small interface, since Go 1.24's `io/fs` has none); tests use fixtures under `testdata/proc/` (via `os.DirFS`) or `fstest.MapFS`, plus a **blocking filesystem wrapper** whose reads block until the test releases them (in `t.Cleanup`), for 0029 |
| `/sys` | same filesystem interface and blocking wrapper; fixtures under `testdata/sys/` |
| systemd D-Bus | fake systemd reader (scripted unit states and errors, and a reader that blocks until its context is cancelled, for 0029) |
| journald | fake journal reader (scripted entries, cursors and errors, and a reader that blocks until its context is cancelled, for 0029) |
| MariaDB socket | fake MariaDB status source (status variables, process list, errors, and a source that hangs until its context is cancelled, for 0029) |
| Telegram Bot API | fake Telegram client (records sent messages, returns scripted updates; no network) |
| Tailscale network | none in code (0017: `vandoxd` embeds no Tailscale); the agent's sender is tested against a `net/http/httptest` server standing in for the ingest port; the ACL itself is deployment configuration, reviewed, not unit-tested |
| time | injectable clock (spool age, live/backfill classification, deadlines) |

### `SECURITY.md`

- *Supported Versions*: state that no release has been published yet, so fixes go to `main`; from the first
  release (v0.1.0) on, only the latest release receives security fixes. Keep the table (latest: Yes,
  older: No).
- *Reporting a Vulnerability*: unchanged (the reporting channel stays e-mail to
  `Development@e-networld.de`, no public issues, 5 business days / 30 days).
- *Deployment Security Considerations*: keep the paragraph, but replace "in environment variables or files
  readable only by the service user" with "only in environment variables or Docker secrets, never in the
  configuration file or on the command line" (0032); "run the agent with minimal privileges" becomes "run
  the agent as the dedicated user `vandox-agent`, never as root, with only the rights listed in
  `deploy/agent/`, and keep the unit's confinement (system-call filter, no further capabilities); its
  capabilities give it read access to everything on the server, so treat the server's credentials as
  readable by the agent" (0030); add: the Tailscale ACL must allow the monitored server only the ingest port on
  the backend host (0010), and the Telegram user allowlist must be set (0031).
- *Scope*, in scope: replace the inline list with the area names from `.squad/project.md` *Security areas*
  (ingest authentication, Tailscale ACL and port binding as documented, web UI login, command signing for
  remote actions once released, Telegram allowlist, agent privileges, MariaDB monitoring user, secrets
  handling, file writes, parsing of external input, outbound calls including their transport security,
  logging and display of external data in log output, the web UI and Telegram messages).
  Add explicitly: attacks by an unprivileged local user of the monitored server against the agent or its
  secrets (e.g. reading a secret from `/proc/<pid>/cmdline` or the environment, abusing the agent's rights,
  escaping the agent unit's confinement to write or run code as another user) are in scope.
- *Scope*, out of scope: replace "Attacks that require local system access or physical access to the host"
  with "Attacks that require root or physical access to the monitored server or to the backend host" — the
  old wording would exclude exactly the local-access threats that the agent-privilege (0030) and secrets
  (0032) areas exist for. "Issues arising from misconfiguration of the deployment environment" stays.

### `docs/ARCHITECTURE.md` (inside the `project:… architecture` block only)

- *Components*, after the agent bullet or as a sentence after the list: each collector runs in its own
  goroutine under a deadline and is abandoned when the deadline passes, so a hanging collector or database
  never blocks the agent; a missed sample is recorded as a gap, and a collector still stuck is not started
  again. Add 0029 to that section's *Records* line.
- *Offline behavior and backfill*: add that data which is nevertheless lost (agent stopped, spool full,
  collector timed out) is recorded as a gap, so there are no data gaps unless explicitly recorded. Add 0028
  to *Records*.
- *Configuration*: add that secrets are read only from environment variables or Docker secrets, never
  from the configuration file (0032).
- *Security model*: add the agent runs as the dedicated user `vandox-agent`, never as root, with only the
  groups and capabilities listed in `deploy/agent/`, and from v0.6.0 a polkit rule that allows restarting
  only the configured units; the two capabilities it needs to read other users' processes
  (`CAP_SYS_PTRACE`, `CAP_DAC_READ_SEARCH`) give it root's read access, so its systemd unit denies the
  process-attach system calls and grants no write-side capability — a compromised agent can read
  everything on the server but cannot write as or run code as another user (0030); the Telegram bot sends to and accepts updates only from allowlisted
  users in their private chats (0031); secrets come only from environment variables or Docker secrets
  (0032). Add 0030, 0031, 0032 to *Records*.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| squad project knowledge (seeded, owned by this repository) | `.squad/project.md` | sections filled as above |
| docs | `SECURITY.md` | versions, deployment advice, scope |
| docs | `docs/ARCHITECTURE.md` | guarantees and security model, record links |
| docs | `docs/decisions/0028`–`0032` | new records, written by the Lead (Proposed) |

`.squad/project.md` is the one `.squad/` file a product PR may change (`.squad/routing.md`, *Scope of a
product PR*); the Reviewer must not flag it. No other `.squad/` file, no instruction file, no code, no
`deploy/` file changes.

## Signatures (for the Dev's skeleton)

None. Step 4 (skeleton) does not apply.

## Test files

None: no production or test code changes, so steps 5 and 6 have no tests to write and coverage is
unaffected. The acceptance criteria are checked by the Reviewer and Security against the diff and by
`python3 .squad/tools/config-check.py`. Existing test code affected: none.

## Documentation updates

The three files above, made by the Dev. `README.md`: none (no configuration exists yet; the allowlist and
secret variables enter the configuration table with the features that add them, as records 0031 and 0032
say).

## Architecture check

No guarantee is weakened: 0022 is restated unchanged; 0028 and 0029 add guarantees and are documented in
`docs/ARCHITECTURE.md` as `.squad/project.md` *Guarantees* requires. 0028 extends 0018 (gapless spool)
without contradicting it; 0030 extends 0013 (the agent runs as OS user `vandox-agent`); 0031 extends 0012;
0032 narrows the earlier, unrecorded "environment variables or files" wording in `.squad/project.md` and
`SECURITY.md` — no accepted record says otherwise.

## Security considerations

The content is the security model itself, hence tier `security`. Points for Security to check: that no
existing area was dropped while restructuring; that the protection goals are verifiable by later reviews;
that `SECURITY.md` keeps the private reporting channel and does not invite public disclosure; that no
secret, internal hostname, tailnet address or Telegram user or chat ID is written into any file; that 0030
describes the capabilities, the confinement and the residual accurately enough for the #42 diff review to
check the unit against it.

## Decision records

- `docs/decisions/0028-data-gaps-are-always-recorded.md` (Proposed)
- `docs/decisions/0029-hanging-collector-never-blocks-the-agent.md` (Proposed)
- `docs/decisions/0030-agent-runs-unprivileged-with-named-capabilities.md` (Proposed)
- `docs/decisions/0031-telegram-user-allowlist.md` (Proposed; renamed from `0031-telegram-chat-allowlist.md`
  in the revision, the old file is deleted)
- `docs/decisions/0032-secrets-only-from-environment-or-docker-secrets.md` (Proposed)

Index entries in `docs/decisions/README.md` are added by the Lead at approval.

## Challenge

Devil's Advocate, round 1 (2 major, 5 minor). All seven objections were checked against the code, the
records and issues #42, #43, #60, #69, #70, #73, #78, and accepted. Scope and tier are unchanged.

1. **Major — the `/proc`/`/sys` doubles cannot test 0029, and 0029 promised cancellation Go cannot
   deliver.** Accepted. Confirmed: `os.ReadFile` takes no context, and a static `testdata/proc` tree
   cannot hang. 0029 now says the collector is *abandoned* at its deadline (context passed only where the
   API takes one), and a collector that is still stuck is not started again; "cancel every call" is listed
   under *Options considered* as rejected, with the reason. The *Test doubles* rows for `/proc` and `/sys`
   now name an `io/fs.FS` interface (production `os.DirFS`, tests `testdata/` fixtures or `fstest.MapFS`)
   plus a blocking wrapper; D-Bus and journald got a blocking variant too. AC3 requires the hanging
   variants; the guarantee text and the `docs/ARCHITECTURE.md` *Components* edit use the new wording.
   Fixtures under `testdata/` remain, so 0019 still holds.
2. **Major — `SECURITY.md` would exclude the local-access threats its new areas cover.** Accepted. The
   out-of-scope line becomes "Attacks that require root or physical access to the monitored server or to the
   backend host", and in scope gains attacks by an unprivileged local user against the agent or its
   secrets. AC4 checks this.
3. **Minor — 0030 justifies polkit wrongly.** Accepted. Reading unit state over D-Bus needs no polkit;
   0030 now names groups `adm` and `systemd-journal` plus documented `AmbientCapabilities` for reading
   (#42) and ties the polkit rule to restarting configured units only (#70, after #69, v0.6.0). Area 6 and
   the *Security model* edit follow; the record's title no longer mentions polkit.
4. **Minor — chat-ID allowlist vs. user IDs.** Accepted, decided explicitly: one allowlist of Telegram
   **user** IDs, private chats only — messages go only to those users' private chats; updates are acted on
   only when the sender is allowlisted and the update comes from that private chat; groups are ignored.
   Chat-ID and dual-allowlist options are recorded as rejected. The record is renamed
   `0031-telegram-user-allowlist.md`; area 5, `SECURITY.md` and the *Security model* edit follow.
5. **Minor — "web UI not offered on the tailnet" overstates 0016.** Accepted: area 2 now uses 0016's
   wording "not offered on the tailnet to the monitored server". Whether maintainer devices reach the UI
   over the tailnet (#43) is left to #43 (listed under follow-ups).
6. **Minor — 0032 over-commits.** Accepted. Constant-time comparison now applies only to secrets checked
   against input (ingest token, later TOTP codes; the UI password through its hash's comparison). The
   configuration-file rejection is no longer a consequence; the enforcement mechanism is left to the
   configuration feature, stated as such in 0032 and under follow-ups.
7. **Minor — area 12 cites 0021.** Accepted: area 12 cites no record, since none decides output escaping
   yet.

## Security review

Security, plan review round 1: CHANGES_REQUIRED (3 blocking, 1 non-blocking). All four accepted; scope and
tier unchanged.

1. **B1 — "not root" was false for the capabilities #42 needs.** Accepted, option (a). 0030 now names
   `CAP_SYS_PTRACE` (ptrace access check for `smaps_rollup` and `fd` link targets; also attach to any
   process) and `CAP_DAC_READ_SEARCH` (listing `/proc/<pid>/fd`; also read every file, other processes'
   `environ`/`mem`, `open_by_handle_at`). The decision is now option 4: the two capabilities with a
   mandatory confinement — `NoNewPrivileges=yes`, `SystemCallFilter=` denying at least `ptrace`,
   `process_vm_readv`, `process_vm_writev`, `pidfd_getfd` and `open_by_handle_at` with
   `SystemCallArchitectures=native`, no write-side capability, `ProtectHome=yes` and `InaccessiblePaths=`
   for `/etc/shadow` and `/etc/gshadow` (the latter two re-labelled after round 2, see B4). Checked as asked: the filter is consistent with the reads, because
   procfs applies the ptrace access check (`ptrace_may_access`) inside `open`/`readlink`/`read`, not through
   `ptrace(2)`; the record says so, and that `pidfd_open`/`pidfd_send_signal` (used by Go's `os/exec`) must
   stay allowed. The read-everything residual (files, process memory and environment — the latter are
   plain reads no filter can block) is stated openly as accepted, including that credentials read this way
   may lead to root through other services. Unconfined capabilities (option 3) and a privileged helper
   (option 5) are recorded as rejected with reasons. Area 6's goal, the `docs/ARCHITECTURE.md`
   *Security model* edit, the `SECURITY.md` deployment advice and scope, and AC1 follow. The #42 review must
   verify on the target system that the reads work under the filter and `PTRACE_SEIZE` fails with `EPERM`.
   No escalation: the guarantee "never runs as root" holds; what is narrowed is a claim no accepted record
   or `docs/ARCHITECTURE.md` made ("gains nothing beyond them"), and it is replaced by the true statement.
2. **B2 — transport security dropped from outbound calls.** Accepted. Area 11 now requires every call to
   leave encrypted to a verified peer: the agent only over the tailnet (0010, 0017, 0023), Telegram and the
   optional AI service of the nightly report (0008, found on the way: also an outbound call) over HTTPS with
   certificate verification, TLS checks with verification, never `InsecureSkipVerify`, no secret over an
   unencrypted connection. For external checks the wording is "whenever they use TLS", since a check
   may probe a plain-text port (e.g. a mail service's banner) where no certificate exists; a failed verification is a check result.
3. **B3 — Telegram missing from area 12.** Accepted. Area 12 now covers Telegram messages (external data
   escaped for the parse mode used, or sent as plain text), including AI-written text; 0031 gains the same
   consequence; the `SECURITY.md` scope names log output, web UI and Telegram messages.
4. **N1 — polling vs. webhook.** Accepted. 0031 *Consequences* and area 5 state that `getUpdates` polling
   adds no inbound endpoint, a webhook would be a new internet-facing endpoint not covered by 0006, 0012 and
   0016 and needs its own decision; the choice stays with #60.

Security, plan review round 2: CHANGES_REQUIRED (1 blocking). Second rejection, so the Lead decided
(mode `decide`): a pure wording defect, accepted and fixed; scope and tier unchanged, no split, no
escalation.

5. **B4 — `ProtectHome=`/`InaccessiblePaths=` presented as a limit they are not.** Accepted. Confirmed:
   both only hide paths inside the unit's private mount namespace; with `CAP_SYS_PTRACE` (passes the
   ptrace access check on `/proc/<pid>/root`) and `CAP_DAC_READ_SEARCH` the agent opens
   `/proc/1/root/etc/shadow` with a plain `open`/`read`, which no system-call filter blocks; hiding other
   processes (`ProtectProc=invisible`) would break the reads #42 needs. Kept, labelled as defence in depth
   against accidental reads only, in 0030 *Decision*, area 6 and AC1. The residual in 0030 *Consequences*
   and area 6 is now "every file on the server, and the memory and environment of every process" without
   the "not made inaccessible" exception; "narrows this" is removed. The #42 on-target check (0030
   *Consequences*, follow-ups) now includes the `/proc/<pid>/root` bypass. The `SECURITY.md` advice
   ("treat the server's credentials as readable by the agent") and the `docs/ARCHITECTURE.md` *Security
   model* edit ("can read everything on the server") were already correct and are unchanged.

## Out of scope / follow-ups

- Choosing the password-hash algorithm, session lifetime and rate-limit values: with the web UI login
  feature (its own record).
- The unit file with the two capabilities and the confinement 0030 requires, further `InaccessiblePaths=`
  entries for credential stores on the target system (defence in depth against accidental reads only), and
  the on-target check that reads work, `PTRACE_SEIZE` fails, and a path in `InaccessiblePaths=` is still
  readable through `/proc/1/root/<path>` so that no deployment file presents the list as protection
  against a compromised agent: with #42 (0030); the polkit rule: with the restart feature (#70). Moving the
  capabilities into a separate helper (0030 option 5) would need a superseding record.
- Telegram update delivery (polling or webhook; a webhook needs its own decision): with #60 (0031).
- How configuration loading enforces "no secrets in the configuration file": with the configuration
  feature (0032).
- Whether maintainer devices may reach the web UI over the tailnet (#43) next to 0016's "home LAN only":
  decided in #43, not here; this plan only restates 0016.
- The ACL text and the agent's systemd unit in `deploy/`: with the deployment work.
- Enabling GitHub private vulnerability reporting as a second channel: a repository setting, not a file
  change; not requested.
