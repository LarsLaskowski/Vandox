# Spec: Configuration loading for agent and backend

Status: Approved by Lead

Source: Issue #11. Depends on #4/#5 (`internal/model`, `internal/wire`, merged).

## Problem / motivation

Both binaries need configuration, and neither has any yet: `vandox-agent` and `vandoxd` only understand
`-version` (`internal/cli/cli.go`). The next features — the backend skeleton (#13) and the agent skeleton
(#30) — both say "configuration via the shared configuration package". Record 0032 fixed that secrets
(agent token, web password hash, Telegram bot token) come only from environment variables or Docker
secrets, never from the configuration file, and left *how* loading enforces that to this feature.

## Behavior

- A shared package `internal/config` loads one YAML file per binary: the agent's (default
  `/etc/vandox/agent.yaml`) and the backend's (default `/etc/vandox/vandoxd.yaml`, mounted into the
  container). The binaries are not wired to it yet; #13 and #30 call it at start-up.
- Parsing is strict. A key that is not a known option, a value that has the wrong type or fails its rule,
  and every YAML construct the loader does not support (duplicate keys, several documents, anchors and
  aliases, merge keys, custom tags, non-string keys) are errors. The error names the file, the line (for
  a YAML syntax error: when the parser reports one) and the full dotted key (e.g. `backend.url`). It never
  contains the offending value or other text from the file: an unknown key's name is shown only when it is
  short and made of letters, digits, `_` and `-`, and the YAML library's own messages are never passed
  through.
- Options that are missing take documented defaults. Required options without a sensible default
  (`agent_id`, `backend.url`) are errors when missing.
- Non-secret options exist only in the file. There are no environment overrides for them.
- Secrets come only from environment variables: `VANDOX_<NAME>` holds the value, or `VANDOX_<NAME>_FILE`
  names an absolute path to a file that holds it (Docker secrets, systemd credentials). Setting both is an
  error. A secret must be printable ASCII without spaces. From a file, one trailing line ending is removed.
  An error about a secret names the variable and the rule, but never the value, the `_FILE` path (a
  secret pasted into the `_FILE` variable must not reach the logs) or the file content.
- An environment variable that starts with `VANDOX_` (in any letter case) but is not a known secret
  variable of that binary is an error. For example, the Telegram token in the agent's environment is
  rejected (0012). The error shows the variable's name only when it consists of letters, digits and `_`.
- String options may not contain control, format or line/paragraph separator characters, so values are
  safe to log later.
- The agent token is required by the agent and must be at least 32 characters. The backend's secrets are
  optional at load time. The features that use them (#25 login, #40 ingest, #60 Telegram) decide what
  happens when one is missing.
- Loaded secrets print as `[redacted]` in every `fmt` verb, in `log/slog` and in text and JSON
  marshalling. Only an explicit `Value()` call returns them.
- Commented example files `deploy/agent/agent.yaml` and `deploy/backend/vandoxd.yaml` document every
  option and its default. A test loads them, and a test checks that they name every option and match the
  defaults.

## Acceptance criteria

- [x] AC1: Unknown keys, invalid values and unsupported YAML constructs produce a start-up error naming the
  file, line and key, never the value.
- [x] AC2: Secrets are taken only from environment variables or `*_FILE` files, under the rules above. The
  configuration file cannot carry one, and a secret never appears in an error message or in formatted
  output.
- [x] AC3: Example configurations exist under `deploy/agent/` and `deploy/backend/`, a test loads them, and
  a test pins that they cover every option with its default.
- [x] AC4: Missing options take their defaults, and missing required options are errors.

(The plan breaks these into testable criteria AC1–AC14.)

## Out of scope

- Calling the loader from `cmd/vandox-agent` / `cmd/vandoxd`, a `-config` flag and start-up exit codes
  (#13, #30).
- Options of later features: collector intervals and deadlines (#30–#36), spool cap and age (#38),
  backfill bandwidth (#39), retention (#46), trusted proxies (#25), Telegram allowlist and quiet hours
  (#60). Each adds its own options through the integration surface in `.squad/project.md`.
- Several agents with one token each on the backend (#89).
- Hot reload of the configuration.

## Open questions

None. The Lead decided the open design points in records 0048–0050.
