# 0031: The Telegram bot talks only to allowlisted users in private chats

- **Status:** Proposed
- **Date:** 2026-10-04
- **Source:** Issue #7
- **Supersedes:** —

## Context

`vandoxd` sends alerts and reports through a Telegram bot (0012); later, read-only commands (issue #73) and
the confirmation of remote actions (issue #78, 0009) follow. A Telegram bot can be found and messaged by
anyone who knows or guesses its name; alerts and reports contain log lines and process data of the server
(0021). Issue #60 asks for an allowlist of Telegram **user** IDs. In a group chat every member can press
an inline button or send a command, so an allowlist of chat IDs would let any member of an allowed group
confirm an action.

## Options considered

1. **Answer whoever writes to the bot** — no configuration; anyone can read alerts and issue commands.
2. **Pairing on first contact** — convenient; the first stranger to message the bot after a reset wins.
3. **An allowlist of chat IDs, groups included** — alerts can go to a shared group; any member of an
   allowed group can send commands and confirm actions, which issues #73 and #78 rule out.
4. **Separate allowlists: chats to send to, users to accept commands from** — supports groups safely, but
   two values to keep consistent, and groups are not needed for a single maintainer.
5. **One allowlist of user IDs, private chats only** — explicit; one configuration value; a private chat
   with a user has that user's ID as its chat ID.

## Decision

Option 5: `vandoxd` has one configured allowlist of Telegram user IDs. It sends messages only to the private
chats of those users. It acts on an update (message, command, callback) only when the sender's user ID is on
the allowlist and the update comes from that user's private chat; every other update, including any update
from a group or channel, is ignored without acting on its content. An empty allowlist means the bot sends
and accepts nothing.

## Consequences

- Alerts and reports reach only the allowlisted users; neither a stranger nor a member of a group the bot
  was added to can trigger or confirm anything.
- The allowlist is a configuration option of the backend (README configuration table).
- Rejected updates are logged (issue #73) only as sanitized metadata (sender ID, chat type, time), never
  with their content.
- Sending alerts to a group would need a superseding record.
