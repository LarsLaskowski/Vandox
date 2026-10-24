# 0124: Ingest request: one batch per POST, bearer token, acknowledged by the status alone

- **Status:** Proposed
- **Date:** 2026-10-24
- **Area:** Agent
- **Source:** Issue #39
- **Supersedes:** —

## Context

The agent's sender (#39) is built before the backend's ingest endpoint (#40), so the request and the answers both sides rely
on have to be fixed now, once. The agent must delete spooled data only after the backend committed it (record 0045, Storage,
*Writing*), must never leak its token (security area 8), must reach only the ingest port (security area 11, records 0006,
0017) and must treat what the backend answers as untrusted input (security area 10). The batch itself is the wire format
(records 0042 to 0046); the backend decodes gzip strictly (record 0076). A batch that the backend refuses as invalid and a
backend that does not accept batches at all need different reactions on the agent (record 0125), so the answer has to tell
them apart.

## Options considered

1. **Status-only acknowledgement** (chosen) — a `2xx` answer acknowledges every record of the batch; the body is never read
   beyond a small bound and never parsed. The agent parses nothing the backend chooses; the batch has no identity of its own
   anyway (record 0045), so the request is the unit of acknowledgement.
2. **An acknowledgement body listing the stored sequence ranges** — would allow partial acknowledgement of a batch, but a batch
   is valid only as a whole (record 0044), so a partial answer has no use, and the agent would gain a parser of external input.
3. **A streaming or long-lived upload** (one request carrying many batches) — fewer requests, but an acknowledgement could not
   follow a commit per batch without a framing protocol of its own, and one stalled stream would hold up current data.
4. **`Content-Encoding: gzip` on a JSON Lines body** — HTTP-conformant, but invites a server's request decompression to
   unpack the body before the strict gzip check (record 0076) and before the limit on compressed bytes; a gzip body type keeps
   the decoding in the ingest code.
5. **Token in a custom header or the URL** — a URL ends up in access logs; a custom header gains nothing over the standard
   `Authorization` scheme, which proxies and server frameworks already treat as a credential.

## Decision

Option 1 with a gzip body type and a bearer token: one wire-format batch per `POST` to a fixed path under `backend.url`,
acknowledged by a `2xx` status that the backend sends only after its commit, with `400`, `413` and `422` reserved for a batch
that is invalid as a whole. The request, the headers and the status classes are in the [Agent](../areas/agent.md) area
(*Spool and sending*) and, as the contract #40 implements, in [Backend host](../areas/backend-host.md) (*Ingest endpoint*).

## Consequences

- The agent needs no response parser; a hostile or broken backend can only choose a status.
- #40 has to answer an unsupported format version, a missing or wrong token, a full disk or a busy store with a status outside
  `400`, `413` and `422`, so the agent keeps the batch instead of giving its records up (record 0125).
- A lost answer makes the agent send the batch again; the backend's deduplication (records 0045, 0063) stores it once.
- Changing the path or the body type later needs a coordinated change of agent and backend; until the upgrade rules are settled
  (#85) the backend is upgraded first.
