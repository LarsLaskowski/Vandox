# 0050: Secrets from VANDOX_* variables or *_FILE files, strict value rules, unknown VANDOX_ variables rejected, redacted type

- **Status:** Proposed
- **Date:** 2026-10-04
- **Source:** Issue #11
- **Supersedes:** —

## Context

0032 decided that secrets come only from environment variables or Docker secrets and are never logged
or put into error messages. It left how configuration loading enforces this to the configuration
feature. Issue #11 asks for "environment variables or `*_FILE` variables pointing to files (Docker
secrets)". The secrets known today are the agent/ingest token (both binaries), the web UI password hash
(backend, #25) and the Telegram bot token (backend, #60). The agent must never hold the Telegram token
(0012). The backend's v0.1.0 use (log import, forensics) runs without any agent and without Telegram.

## Options considered

1. **Value and file variable, both allowed, file wins**. This is a common convention, but a leftover value
   is silently ignored.
2. **Value or file variable, setting both is an error**. The source is unambiguous.
3. **Trim all whitespace around a secret**. This is forgiving, but it hides a corrupted value and allows
   tokens with embedded spaces.
4. **Printable ASCII only (`0x21`–`0x7E`), with exactly one trailing line ending removed from files**. A
   BOM, CR/LF, spaces and control characters are rejected instead of silently becoming part of the secret.
   Tokens, PHC hash strings (`$argon2id$…`) and Telegram tokens all fit.
5. **Every secret required at load time**. That is simple, but the backend could not start for log import
   without a web password hash, an agent token and a Telegram token that nothing uses yet.
6. **Ignore unknown environment variables**. That is the usual behavior, but a typo such as
   `VANDOX_TELEGRAM_BOT_TOKN` silently disables a feature, and a secret of the other binary goes
   unnoticed.
7. **Show the `_FILE` path in errors** (only when it is absolute). It helps to find a wrong mount, but the
   most likely mistake is pasting the secret itself into the `_FILE` variable, a base64 secret can begin
   with `/`, and a path can hold control characters. The variable name identifies the setting, so the
   path is never shown.

## Decision

Options 2 and 4. For the agent the agent token is required, and the backend's secrets are optional at
load time. Unknown `VANDOX_` variables are rejected (option 6 rejected).

- Each secret `VANDOX_<NAME>` is read from that variable or from the file named by `VANDOX_<NAME>_FILE`.
  Setting both, including one that is set but empty, is an error. The names are `VANDOX_AGENT_TOKEN`
  (agent and backend), `VANDOX_WEB_PASSWORD_HASH` and `VANDOX_TELEGRAM_BOT_TOKEN` (backend only).
- A `_FILE` path must be absolute. It must name a regular file (symlinks followed, so Docker and
  Kubernetes secret mounts and systemd credentials work), checked before opening. The read is bounded to
  4096 bytes plus a line ending. One trailing `\n` (or `\r\n`) is removed.
- A secret value is 1–4096 bytes of printable ASCII `0x21`–`0x7E`. The agent token has at least 32
  characters on both binaries (e.g. `openssl rand -hex 32`).
- Required at load: the agent token on the agent. On the backend all three are optional at load. The
  features that use them decide what happens when one is unset: #25 refuses to start the UI without the
  password hash, #40 refuses ingest without the token, and #60 leaves Telegram off without its token.
- Every environment variable whose name starts with `VANDOX_` (in any letter case) must be one that this
  binary knows, and each may appear only once. Otherwise loading fails. The Telegram or web secrets in the
  agent's environment are therefore a start-up error.
- Secrets are held in `config.Secret`. Every `fmt` verb, `String()`, `slog` (`LogValuer`) and text/JSON
  marshalling print `[redacted]`. A verb that does not apply to a non-pointer value, such as `%p`, is not
  passed to `Format`: `fmt` prints the struct fields by reflection instead. The value is therefore stored
  behind a pointer (`value *string`), which such a path prints as an address only. Only `Value()` returns the secret. `*SecretError` names the variable
  and the rule, never the value, the file content or the `_FILE` value. An empty or relative `_FILE`
  value is rejected without any file system call. An absolute `_FILE` path is not shown either; for a
  file system error only the bare error number is kept (so "no such file or directory" still shows and
  `errors.Is(err, fs.ErrNotExist)` still works), never the `*fs.PathError` that quotes the path (option 7
  rejected).
- An unknown `VANDOX_` variable is named only if its name is 1–64 characters of letters, digits and `_`;
  any other name (it may contain any byte but `=` and NUL, including a newline) is reported as not shown.
- The configuration file cannot carry a secret. No secret field has a YAML key, and `backend.url` rejects
  user info and queries (0049).

## Consequences

- Misplaced, duplicated, malformed or misspelled secrets stop start-up with a message that is safe to
  share.
- Secret values with spaces or non-ASCII characters are impossible. A later secret that needs them (for
  example a binary key) must be encoded, e.g. base64, or this record is superseded.
- Clearing the variables from the process environment after reading is not done. Neither binary starts
  child processes. A feature that does must pass an explicit environment.
- The constant-time comparison of the token stays with #40 (0032). Several agents with one token each
  (#89) will need a new variable scheme on the backend and will supersede the single
  `VANDOX_AGENT_TOKEN` there.
