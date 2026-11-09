# 0194: install.sh accepts only the IPv6 literals the agent parses

- **Status:** Accepted
- **Date:** 2026-11-09
- **Area:** Agent installation
- **Source:** Issue #385 / PR #395, Issue #397
- **Supersedes:** 0192 — amends what `install.sh` accepts as an IPv6 literal in `--backend-url`; the rest of 0192 stands.

## Context

Record 0192 refuses host names in `backend.url` and lets `install.sh` accept IPv4 literals and bracketed IPv6 literals,
noting that a malformed IPv6 literal the script lets through fails at the agent's start. The script accepted any bracketed
run of hex digits and colons with at least two colons, such as `http://[1:::2]` or `http://[::1::]`; the agent refuses both
through `netip.ParseAddr`, so the install succeeded and the agent then failed at start (#385). #395 narrowed the script's
check and rewrote 0192's *Decision* to match, after 0192 had been released in `v0.2.0-rc.1`; a released record is
append-only, so the sentence went back to its released text and the change is recorded here (#397).

## Options considered

1. **Check in `install.sh` with the agent's grammar** (chosen) — the install fails with the `--backend-url` message on every
   literal the agent would refuse, so a broken configuration is never installed; the script carries a second implementation of
   the grammar of `netip.ParseAddr`, which its tests pin against the parser.
2. **Keep the loose check and let the agent refuse at start** — one parser only, but the operator learns of the mistake only
   after the install, from the service failing.

## Decision

Option 1. `install.sh` accepts in `--backend-url` only the bracketed IPv6 literals the agent's parser takes, without a zone or
an embedded IPv4 part, and refuses every other form with the `--backend-url` message; the grammar is stated in
[Agent installation](../areas/agent-installation.md).

## Consequences

- A value the script accepts loads in the agent; the set of accepted values only shrank, so no working configuration is refused.
- The script's grammar and `netip.ParseAddr` must change together: `deploy/agent/install_test.go` asserts for each refused form
  that the parser refuses it too.
