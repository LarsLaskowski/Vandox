# 0031: The Telegram bot talks only to allowlisted users in private chats

- **Status:** Accepted
- **Date:** 2026-10-04
- **Area:** Notification and remote actions
- **Source:** Issue #7
- **Supersedes:** —

## Context

`vandoxd` sends alerts and reports through a Telegram bot (0012); later, read-only commands (issue #73) and the confirmation of remote actions (issue #78, 0009) follow. A bot can be found and messaged by anyone who knows or guesses its name, and alerts and reports contain log lines and process data of the server (0021). In a group chat every member can press an inline button or send a command, so an allowlist of chat IDs would let any member of an allowed group confirm an action.

## Options considered

1. **Answer whoever writes to the bot** — anyone can read alerts and issue commands.
2. **Pairing on first contact** — the first stranger to message the bot after a reset wins.
3. **An allowlist of chat IDs, groups included** — any member of an allowed group can send commands and confirm actions.
4. **Separate allowlists for chats to send to and users to accept commands from** — safe for groups, but two values to keep consistent, and groups are not needed for a single maintainer.
5. **One allowlist of user IDs, private chats only** (chosen) — explicit, one configuration value; a private chat has the user's ID as its chat ID.

## Decision

Option 5; the rules are in the [Notification and remote actions](../areas/notification-and-remote-actions.md) area.

## Consequences

- Alerts and reports reach only the allowlisted users; neither a stranger nor a member of a group the bot was added to can trigger or confirm anything.
- The allowlist is a configuration option of the backend (README configuration table). Sending alerts to a group would need a superseding record.
- Receiving updates by outbound polling adds no inbound endpoint; a webhook would be a new internet-reachable endpoint that 0006, 0012 and 0016 do not cover and that needs its own `security`-tier decision. The choice is left to issue #60.
- Message text built from external data is escaped for the parse mode or sent as plain text.
