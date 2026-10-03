# 0015: Mail services are checked, mail accounts are not

- **Status:** Proposed
- **Date:** 2026-10-03
- **Source:** Issue #6
- **Supersedes:** —

## Context

Mail goes down with the rest of the server when memory runs out. Monitoring could look at the mail
services or log in to individual mailboxes.

## Options considered

1. **Check individual mail accounts** (login, test mail) — end-to-end; needs mailbox credentials and
   touches customer data.
2. **Check only the mail services** (process state, ports, logs) — no credentials, no customer data;
   per-account problems are not visible.

## Decision

Option 2: Vandox checks the state of the mail services, never individual mail accounts.

## Consequences

- No mailbox credentials are stored anywhere.
- A problem that affects a single account is out of scope.
