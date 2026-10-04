# 0032: Secrets only from environment variables or Docker secrets

- **Status:** Proposed
- **Date:** 2026-10-04
- **Source:** Issue #7
- **Supersedes:** —

## Context

Vandox handles several secrets: the agent's ingest token, the web UI password (hash), the Telegram bot
token and later the command-signing key (0009). Both binaries read a configuration file and environment
variables (`docs/ARCHITECTURE.md`, *Configuration*). Configuration files are copied, shared in issues and
committed to backups; command-line arguments are visible to every user in `/proc/<pid>/cmdline`.

## Options considered

1. **Secrets in the configuration file** — one place for everything; the file must be protected as a
   secret and tends to leak.
2. **Secrets as command-line flags** — simple; readable by every local user through `/proc`.
3. **Secrets only from environment variables or Docker secrets** — the configuration file stays free of
   secrets; the backend reads Docker secrets (files under `/run/secrets/`), the agent reads environment
   variables set by its systemd unit (e.g. an `EnvironmentFile=` readable only by root).

## Decision

Option 3: secrets are read only from environment variables or Docker secrets, never from the configuration
file or the command line. They are never logged, never shown in the web UI, never written to the spool or
error messages, and compared in constant time.

## Consequences

- The configuration file can be shared without redaction.
- The configuration loading rejects a configuration file that contains a secret key, with an error that
  names the key but not its value; the README
  configuration table names the environment variable or Docker secret for each secret.
- `SECURITY.md` tells operators where secrets belong.
