# Notification and remote actions

## Scope

How Vandox talks to the operator and how it may act on the monitored server: who sends and receives Telegram messages, and under what
conditions a command may run on the server. Both are **not implemented yet** (alerts and the bot come after the forensics release,
remote actions later still); this document fixes the rules they are held to. When alerts fire is in [Detection, alerts and
reports](detection-and-reports.md).

## Telegram

- **Only the backend talks to Telegram.** The bot token exists only on the backend host. The agent's only outbound destination is the
  backend's ingest port; it never contacts Telegram or any other service.
- While the backend host is off there are no alerts. The data is spooled, analyzed after backfill, and alerts are raised for live data
  only.
- The backend has **one allowlist of Telegram user IDs**. It sends messages only to the private chats of those users and acts on an
  update (message, command, button callback) only when the sender's user ID is on the allowlist **and** the update comes from that
  user's private chat. Every other update, including any update from a group or channel, is ignored without acting on its content. An
  empty allowlist means the bot sends and accepts nothing.
- Rejected updates are logged only as sanitized metadata (sender ID, chat type, time), never with their content. Sending alerts to a
  group needs a new decision.
- Updates are received by outbound polling (`getUpdates`); a webhook would be a new endpoint reachable from the internet, which
  records 0006, 0012 and 0016 do not cover, and needs its own `security`-tier decision (#60).
- Messages carry external data (log lines, process names, a report possibly written by the optional AI): the text is escaped for the
  parse mode used, or sent as plain text, so log content cannot change a message's formatting or links.

## Outbound calls

- Every outbound call of the backend has a timeout, goes only to its configured destination and leaves encrypted to a verified peer:
  Telegram is called over HTTPS with certificate verification, and so is the optional AI service of the nightly report (planned).
  Planned external checks verify the certificate whenever they use TLS, and a failed verification is a check result.
- Certificate verification is never switched off, and no secret is sent over an unencrypted connection.

## Remote actions

- Remote actions exist only as **signed commands that name an entry of a fixed action list configured locally on the monitored
  server**, and only after the user has confirmed them in the Telegram bot.
- The agent **pulls** commands from the backend (it never listens) and verifies the signature before it executes anything. A
  compromised backend or transport can at most trigger listed actions, and only with a valid signature.
- The agent executes only a command with a valid signature that names an action of its local list and was confirmed by the user; a
  forged, altered or replayed command is rejected.
- Adding an action requires a change on the server, by design. The signing key exists only on the backend host and is a secret
  ([Configuration and secrets](configuration-and-secrets.md)). Actions never touch agents of the hosting provider.
- Commands reach the server only at the agent's next poll.

## Related decisions

- [0012](../decisions/0012-agent-never-contacts-telegram.md) — why only the backend talks to Telegram.
- [0031](../decisions/0031-telegram-user-allowlist.md) — why a user allowlist and private chats only.
- [0009](../decisions/0009-remote-actions-as-signed-commands.md) — why signed commands from a fixed local list.
- [0006](../decisions/0006-agent-connects-outbound-only.md) — why the agent connects outbound only, so it pulls commands.

## Not here

- When an alert fires and what the report contains: [Detection, alerts and reports](detection-and-reports.md).
- Where the bot token and the signing key are supplied: [Configuration and secrets](configuration-and-secrets.md).
- The agent's outbound-only connection: [Agent](agent.md), [Agent sending](agent-sending.md).
