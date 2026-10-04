# 0031: The Telegram bot talks only to allowlisted chats

- **Status:** Proposed
- **Date:** 2026-10-04
- **Source:** Issue #7
- **Supersedes:** —

## Context

`vandoxd` sends alerts and reports through a Telegram bot (0012), and later Telegram commands are planned
(e.g. confirming a remote action, 0009). A Telegram bot can be found and messaged by anyone who knows or
guesses its name; alerts and reports contain log lines and process data of the server (0021).

## Options considered

1. **Answer whoever writes to the bot** — no configuration; anyone can read alerts and issue commands.
2. **Pairing on first contact** — convenient; the first stranger to message the bot after a reset wins.
3. **A configured allowlist of chat IDs** — explicit; one configuration value to maintain.

## Decision

Option 3: `vandoxd` sends messages only to chat IDs on a configured allowlist and ignores every update
(message, command, callback) from a chat that is not on it, without acting on its content. An empty
allowlist means the bot sends and accepts nothing.

## Consequences

- Alerts and reports reach only the configured chats; a stranger cannot trigger or confirm anything.
- The allowlist is a configuration option of the backend (README configuration table).
- Rejected updates may be logged only as sanitized metadata, never with their content.
