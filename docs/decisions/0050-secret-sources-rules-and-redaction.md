# 0050: Secrets from VANDOX_* variables or *_FILE files, strict value rules, unknown VANDOX_ variables rejected, redacted type

- **Status:** Accepted
- **Date:** 2026-10-04
- **Area:** Configuration and secrets
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

Options 2 and 4: a secret comes from its variable or from its `_FILE` file, never both, and is printable ASCII;
unknown `VANDOX_` variables are rejected (option 6 rejected); secrets are held in a type that redacts itself, and
no error shows a value or a path (option 7 rejected). Only the agent token is required at load, and only on the
agent; on the backend the feature that needs a secret decides (option 5 rejected). The exact rules are in
[Configuration and secrets](../areas/configuration-and-secrets.md), *Secrets*.

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
