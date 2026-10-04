# 0021: No pseudonymization of log data

- **Status:** Accepted
- **Date:** 2026-10-03
- **Source:** Issue #6
- **Supersedes:** —

## Context

The server's logs contain IP addresses, mail addresses, domain names and user names. Pseudonymizing them
would protect that data but makes forensics harder (correlating an attacker IP or a mailbox across logs).
The server belongs to the operator, and the data is stored only on the backend host in the home network.

## Options considered

1. **Pseudonymize personal data before storage** — less sensitive data at rest; harder correlation, extra
   code on every parser.
2. **Store log data unchanged** — full forensic value; the store holds personal data and must be
   protected accordingly.

## Decision

Option 2: log data is stored as it is, without pseudonymization, because it is the operator's own server
and the data stays in the home network.

## Consequences

- Access control of the web UI (0016), the network boundary (0010) and the retention of 90 days for
  logs (0007) are what protect the data.
- Sending data to external services (for example an optional AI for the report, 0008) has to take into
  account that it is not pseudonymized.
