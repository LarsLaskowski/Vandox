# 0183: The agent unit allows only localhost and the tailnet ranges as addresses

- **Status:** Accepted
- **Date:** 2026-10-27
- **Area:** Agent installation
- **Source:** Issue #42
- **Supersedes:** —

## Context

The agent connects outbound only and only over the tailnet (records 0006, 0010). Without an enforcement in the unit that guarantee
would rest on the configuration alone, and a wrong or changed `backend.url` would send the token anywhere.

## Options considered

1. **No address restriction** — the outbound-only, tailnet-only guarantee would rest on the configuration alone.
2. **`IPAddressDeny=any` with the tailnet ranges and localhost allowed** (chosen) — enforces the guarantee; a backend address outside
   these ranges fails and is documented.

## Decision

Option 2. The allowed ranges are in [Agent installation](../areas/agent-installation.md); the binding to the tailnet device that
complements the address filter is record 0134.

## Consequences

- The agent can reach only tailnet addresses and localhost; a `backend.url` elsewhere fails as `connection failed`.
- The filter is by address, not by interface, so it cannot stop a tailnet address routed through another interface while Tailscale is
  down (record 0134).
- `install.sh` warns about a backend address the filter blocks; a host name in `backend.url` is refused (record 0192).
