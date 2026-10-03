# Project

What the squad needs to know about this project that is not stack-specific. Read by the Lead, the Devil's
Advocate, Security, the Tester and the Reviewer. Not template-managed: `adopt-template` creates it once
and never overwrites it. A product PR updates it when the change makes an entry untrue
(`.squad/routing.md`, *Scope of a product PR*).

## Security areas

A change that touches one of these is tier `security` (`.squad/routing.md`). Name the concrete types,
files or endpoints.

- Secrets and tokens (backend login, Telegram bot token, agent/ingest tokens, later command-signing keys): read from environment variables or files, never logged, compared in constant time.
- Authentication and sessions of the web UI and the agent-to-backend ingest API.
- File writes and paths derived from external input: log import, the agent's on-disk spool, database backups.
- Parsing of external input: log files (journal, syslog, MariaDB, mail, Plesk, web server), the ingest wire format, CLI arguments and configuration, later Telegram commands.
- Outbound calls: Telegram, external checks, the agent's connection to the backend (TLS or Tailscale, timeouts).
- Logging of external data: log lines and process names from the monitored server must not inject into log output or the UI.

## Guarantees

Deliberate behavior that must not change without the Product Manager. Each one is described in
`docs/ARCHITECTURE.md` and, where it was a real choice, has a decision record.

_None recorded yet._ Guarantees (for example gapless collection with backfill) are added here together with their decision record as the features land.

## Integration surface

What the Reviewer checks when the diff introduces or changes a thing of this kind: every place that must
change with it.

**A new or changed configuration option** touches:
- the configuration type and its loading for the agent and the backend (not implemented yet)
- the default configuration file under `deploy/agent/` or `deploy/backend/`
- the configuration table in `README.md` (key, environment variable, default)
- the test that pins the configuration loading

**A new or changed service / module** touches:
- its exported interface and the package that owns it under `internal/`
- where it is wired up in `cmd/vandox-agent` or `cmd/vandoxd`
- the test double used by the tests of its callers
- the component list in `docs/ARCHITECTURE.md`

**A new external API call or DTO** touches:
- the client and its types — the external shape must not leak past it
- the fake/stub in the tests

## Test doubles

The hand-written fakes and stubs the tests reuse (no mocking library unless `docs/UNIT_TESTS.md` says
otherwise):

_None yet._ Hand-written fakes are listed here when the first ones are introduced.
