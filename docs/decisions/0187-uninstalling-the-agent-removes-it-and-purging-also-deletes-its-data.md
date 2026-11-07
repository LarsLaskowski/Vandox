# 0187: Uninstalling the agent removes it, and purging also deletes its data

- **Status:** Accepted
- **Date:** 2026-10-27
- **Area:** Agent installation
- **Source:** Issue #42
- **Supersedes:** —

## Context

The issue asks for an uninstall script. The agent's spool holds records not yet sent, and its token and MariaDB account are set up
once by the operator.

## Options considered

1. **Uninstall removes everything** — destroys unsent records and forces a new token and MariaDB account on reinstall.
2. **Remove and purge, as Debian packages do** (chosen) — a plain run removes the program and keeps the configuration, the token, the
   spool, the user and the MariaDB account; `--purge` removes those as well.

## Decision

Option 2. What each mode removes and keeps, and its messages, are in [Agent installation](../areas/agent-installation.md)
(*Uninstall and purge*).

## Consequences

- A reinstall after a plain uninstall resumes with the kept spool, token and account.
- A purge loses the records not yet sent, and says so before it deletes them.
