# Project

What the squad needs to know about this project that is not stack-specific. Read by the Lead, the Devil's
Advocate, Security, the Tester and the Reviewer. Not template-managed: `adopt-template` creates it once
and never overwrites it. A product PR updates it when the change makes an entry untrue
(`.squad/routing.md`, *Scope of a product PR*).

## Security areas

A change that touches one of these is tier `security` (`.squad/routing.md`). Name the concrete types,
files or endpoints.

1. **Ingest authentication** (backend ingest API, not implemented yet): the agent token, request limits and
   deduplication. *Goal:* only an agent holding a valid token can store data; the token is compared in
   constant time; request size, batch size and rate are bounded so a valid agent cannot exhaust the
   backend's memory or disk; a resent record (same agent ID
   and sequence number), and so a resent batch, is stored once and never overwrites stored data. Records
   0032, 0044, 0045.
2. **Tailscale ACL and port binding** (deployment files under `deploy/backend/` and the documented ACL):
   *Goal:* a compromised monitored server can reach only the ingest port on the backend host's tailnet
   address and nothing else in the tailnet or home LAN; the ingest port is bound only to the tailnet
   address; the web UI is not offered on the tailnet to the monitored server. Records 0010, 0016, 0017.
3. **Web UI login** (not implemented yet): password hashing, sessions, CSRF, rate limiting, reverse-proxy
   trust. *Goal:* the password is stored only as a salted, deliberately slow hash; session IDs are random,
   expire, are invalidated on logout and travel only in `HttpOnly`, `Secure`, `SameSite` cookies; every
   state-changing request is CSRF-protected; failed logins are rate-limited; forwarded headers
   (`X-Forwarded-For`, `X-Forwarded-Proto`) are trusted only from the configured reverse proxy, and the UI
   port is reachable only by that proxy. Records 0016, 0023.
4. **Command signing for remote actions** (later, not part of v0.1.0): *Goal:* the agent executes only
   commands with a valid signature that name an action from its local fixed list and were confirmed by the
   user; a forged, altered or replayed command is rejected; the signing key exists only on the backend
   host. Records 0006, 0009.
5. **Telegram allowlist** (not implemented yet): *Goal:* the bot sends only to the private chats of
   allowlisted Telegram user IDs and acts on an update (message, command, callback) only when its sender is
   allowlisted and it comes from that user's private chat; every other update, including any from a group,
   is ignored without acting on its content and logged only as sanitized metadata. Updates arrive by
   outbound polling (`getUpdates`) or through a webhook; a webhook is a new inbound endpoint reachable from
   the internet that 0006, 0012 and 0016 do not cover and needs its own decision (choice left to #60).
   Records 0012, 0031.
6. **Agent privileges** (`deploy/agent/`: user, systemd unit; from v0.6.0 the polkit rule): *Goal:* the
   agent never runs as root; it holds only the group memberships (`adm`, `systemd-journal`), from #42 the
   capabilities `CAP_SYS_PTRACE` and `CAP_DAC_READ_SEARCH` (bounding set limited to exactly these), and,
   once self-healing exists, a polkit rule that allows restarting only the configured units, each listed
   and justified in `deploy/agent/`. While any capability is granted, the unit is confined:
   `NoNewPrivileges=yes`; `SystemCallFilter=` denies at least `ptrace`, `process_vm_readv`,
   `process_vm_writev`, `pidfd_getfd` and `open_by_handle_at` (with `SystemCallErrorNumber=EPERM`), and
   `SystemCallArchitectures=native`; no write-side capability. So a compromised agent cannot use its
   capabilities to write as or run code as another user. Accepted residual, stated openly: its read access
   equals root's, i.e. every file on the server, and the memory and environment of every process, and
   credentials read that way may still lead to root through other services (password reuse, Plesk or
   MariaDB administration). `ProtectHome=yes` and `InaccessiblePaths=` (at least `/etc/shadow`,
   `/etc/gshadow`) are defence in depth against accidental reads only, not a limit on a compromised agent,
   which bypasses them through `/proc/<pid>/root`. Records 0013, 0030.
7. **MariaDB monitoring user**: *Goal:* the agent connects over the local socket as `vandox-agent`,
   authenticated by `unix_socket`, with only the `PROCESS` privilege: no database password exists and no
   table data is readable; the agent issues read-only status queries only. Record 0013.
8. **Secrets handling** (backend login, Telegram bot token, agent/ingest token, later the command-signing
   key): *Goal:* secrets are read only from environment variables or Docker secrets, never from the
   configuration file or the command line; never logged, shown in the UI, written to the spool or put into
   error messages; a secret checked against input (ingest token, later TOTP codes) is compared in constant
   time, the UI password through its hash function's comparison. Implemented in `internal/config`
   (`readSecret`, `checkEnviron`, `Secret`). Record 0032, 0050.
9. **File writes and paths derived from external input** (log import, the agent's on-disk spool, database
   backups): *Goal:* no write outside the configured directories (no path traversal), the spool is
   size-bounded, files are created with restrictive permissions. Record 0045.
10. **Parsing of external input** (log files: journal, syslog, MariaDB, mail, Plesk, web server; the ingest
    wire format (`internal/wire`: `NewDecoder`, `Decoder.Next`, `wire.Limits`); CLI arguments and configuration; later Telegram commands): *Goal:* malformed or hostile
    input yields an error or a skipped record, never a crash, an unbounded allocation or a hang. The
    configuration file is read by `internal/config` (`decodeStrict`, `readFile`). Records 0048, 0049.
11. **Outbound calls** (Telegram, external checks, the optional AI service of the nightly report, the
    agent's connection to the backend): *Goal:* every call has a timeout, goes only to its configured
    destination and leaves encrypted to a verified peer. The agent's only destination is the ingest port,
    reached only over the tailnet (WireGuard encryption and node authentication by Tailscale), never over a
    public address. Telegram and the AI service are called over HTTPS with certificate verification;
    external checks verify the certificate whenever they use TLS, and a failed verification is a check
    result. Certificate verification is never switched off (no `InsecureSkipVerify`), and no secret is sent
    over an unencrypted connection. Records 0006, 0008, 0010, 0012, 0017, 0023.
12. **Logging and display of external data** (log lines, process names and text derived from them, e.g. an
    AI-written report): *Goal:* they cannot inject into log output (control characters, newlines), into the
    web UI (HTML is escaped) or into Telegram messages (escaped for the parse mode used, or sent as plain
    text without a parse mode). Record 0031 for Telegram; none decides log output and UI escaping yet.
    Configuration error texts never echo document text other than schema or safe key names
    (`decodeStrict`), never a `_FILE` value or path or an unsafe variable name (`readSecret`,
    `checkEnviron`). Configuration values are free of Cc, Cf, Zl and Zp characters but are still logged only
    as `slog` attributes, never concatenated into a message.
13. **Release pipeline and published artifacts** (`.github/workflows/release.yml`,
    `deploy/backend/Dockerfile`, `.dockerignore`, the GitHub environment `release`): *Goal:* artifacts are
    published only from a SemVer tag that only the repository admin can create (tag ruleset `release-tags`)
    and whose commit the workflow checks is on `main`; the ancestry check runs in code the tagger controls,
    so the ruleset is the boundary. Every action is pinned by commit SHA and every base image by
    digest (`FROM ${BASE_<NAME>_IMAGE}@${BASE_<NAME>_DIGEST}` with build-argument defaults that the release
    build never overrides; the release workflow checks the form).
    Release binaries are built without restored CI caches (`setup-go` `cache: false`, plain
    `docker build --no-cache`, no cache backend) and only after `govulncheck` passes. The registry token is
    readable only by the tag-triggered publish job, enters `docker login` only via stdin, and is limited to
    pushing `networlddev/vandox`. No `${{ }}` expression of any kind appears inside a `run:` script; every
    value goes through `env:`, and checkout does not persist the job token. The published image runs as a
    non-root user, a published version tag is never overwritten, and every published binary has a checksum
    in `SHA256SUMS`. Records 0027, 0037, 0039, 0041.

## Guarantees

Deliberate behavior that must not change without the Product Manager. Each one is described in
`docs/ARCHITECTURE.md` and, where it was a real choice, has a decision record.

- **No data gaps unless explicitly recorded**: collection is gapless across backend downtime (spool and
  backfill); data that is nevertheless missing (agent stopped, spool full, collector timed out, sequence
  numbers missing) is recorded as a gap and treated as "unknown", never as "normal". `docs/ARCHITECTURE.md`
  section *Offline behavior and backfill*; records 0028, 0045.
- **A hanging collector or database never blocks the agent**: every collector runs in its own goroutine
  under a deadline and is abandoned when the deadline passes (a blocked `/proc` or `/sys` read cannot be
  cancelled, only abandoned; sources that take a context also get it); a hung source (`/proc`, `/sys`,
  D-Bus, journald, MariaDB) loses only its own sample, recorded as a gap, while the other collectors, the
  spool and the sender keep running; while a collector is still stuck it is not started again.
  `docs/ARCHITECTURE.md` section *Components*; record 0029.
- **Backfilled data never raises an alert by itself**: the backend classifies every record as live or
  backfilled from the data; alert rules run on live data only; backfill is stored and analyzed but never
  alerts. `docs/ARCHITECTURE.md` section *Offline behavior and backfill*; record 0022.

## Integration surface

What the Reviewer checks when the diff introduces or changes a thing of this kind: every place that must
change with it.

**A new or changed configuration option** touches:
- the configuration type and its loading for the agent and the backend: `internal/config` (`Agent`/`Backend`, `LoadAgent`/`LoadBackend`, `AgentKeys`/`BackendKeys`)
- the commented example file `deploy/agent/agent.yaml` or `deploy/backend/vandoxd.yaml`
- the configuration table in `README.md` (key, environment variable, default)
- the tests that pin the configuration loading: `internal/config/agent_test.go` and `internal/config/backend_test.go`

**A new secret** touches: its `Env*` constant, the known-variable list of each binary that reads it, the
secrets table in `README.md`

**A new or changed service / module** touches:
- its exported interface and the package that owns it under `internal/`
- where it is wired up in `cmd/vandox-agent` or `cmd/vandoxd`
- the test double used by the tests of its callers
- the component list in `docs/ARCHITECTURE.md`

**A new external API call or DTO** touches:
- the client and its types — the external shape must not leak past it
- the fake/stub in the tests

**A new or changed external source or destination** (`/proc`, `/sys`, systemd D-Bus, journald, the MariaDB
socket, the Telegram Bot API, the Tailscale network) touches:
- the small interface the code reads it through, and its fake in *Test doubles*
- the deadline that keeps it from blocking the agent (0029) and the gap it records on timeout (0028)
- the privilege it needs in `deploy/agent/` (0030) or the ACL and port binding (0010, 0017)
- the *Security areas* entry it falls under

## Test doubles

The hand-written fakes and stubs the tests reuse (no mocking library unless `docs/UNIT_TESTS.md` says
otherwise):

Status of every row: planned (not implemented yet). The first feature that introduces a surface adds its
double under this name and changes the status.

| Surface | Test double |
| ------- | ----------- |
| `/proc` | collectors read through an `io/fs.FS` (production `os.DirFS("/proc")`; symlink targets such as `/proc/<pid>/fd/*` through `io/fs.ReadLinkFS` (Go 1.25 and later; implemented by `os.DirFS` and `fstest.MapFS`, the blocking wrapper must implement `fs.ReadLinkFS` (`ReadLink` and `Lstat`) as well)); tests use fixtures under `testdata/proc/` (via `os.DirFS`) or `fstest.MapFS`, plus a **blocking filesystem wrapper** whose reads block until the test releases them (in `t.Cleanup`), for 0029 |
| `/sys` | same filesystem interface and blocking wrapper; fixtures under `testdata/sys/` |
| systemd D-Bus | fake systemd reader (scripted unit states and errors, and a reader that blocks until its context is cancelled, for 0029) |
| journald | fake journal reader (scripted entries, cursors and errors, and a reader that blocks until its context is cancelled, for 0029) |
| MariaDB socket | fake MariaDB status source (status variables, process list, errors, and a source that hangs until its context is cancelled, for 0029) |
| Telegram Bot API | fake Telegram client (records sent messages, returns scripted updates; no network) |
| Tailscale network | none in code (0017: `vandoxd` embeds no Tailscale); the agent's sender is tested against a `net/http/httptest` server standing in for the ingest port; the ACL itself is deployment configuration, reviewed, not unit-tested |
| time | injectable clock (spool age, live/backfill classification, deadlines) |
