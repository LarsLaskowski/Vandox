# 0043: Wire format versioned by integer major and minor; unknown majors are rejected before parsing

- **Status:** Accepted
- **Date:** 2026-10-04
- **Area:** Wire format
- **Source:** Issue #10
- **Supersedes:** —

## Context

The wire format (0042) has to change over time while agents and the backend are not upgraded at the same moment (the backend container is
updated separately from the agent binary). Unknown major versions must be rejected cleanly, even when a future major changes the shape of
the header itself, otherwise the backend reports a confusing parse error instead of "unsupported version".

## Options considered

1. **One integer version, every change incompatible** — simplest; every added field breaks old backends.
2. **Integer major and minor, minor changes additive, unknown fields ignored** (chosen) — old backends keep reading batches with new optional fields; incompatible changes raise the major and are rejected cleanly.
3. **Version string (`"1.0"`, SemVer)** — familiar, but needs its own parser with more accepted forms and therefore more ways to bypass the check.
4. **Strict decoding within a minor, reject newer minors** — catches typos in the agent, but turns every additive change into a breaking one.

## Decision

Option 2: major and minor are JSON integers, the major is read and checked first, unknown keys are ignored, and a record kind the decoder
does not know is rejected rather than dropped. The rules are in the [Wire format](../areas/wire-format.md) area (*Versioning*).

## Consequences

- New optional fields can be rolled out agent-first without breaking an older backend.
- A new record kind is not readable by an older backend: the batch is rejected and stays in the agent's spool. Until the upgrade rules are settled (#85), the backend is upgraded before the agent.
- A misspelled optional field in the agent is silently dropped; the unit tests pin every field name and the area document is the reference.
