# 0043: Wire format versioned by integer major and minor; unknown majors are rejected before parsing

- **Status:** Accepted
- **Date:** 2026-10-04
- **Area:** —
- **Source:** Issue #10
- **Supersedes:** —

## Context

The wire format (0042) has to change over time while agents and the backend are not always upgraded at the
same moment (the backend host runs in a container that is updated separately from the agent binary). The
issue requires a format version in the batch header and that the backend rejects unknown major versions
cleanly. A "clean" rejection has to work even when a future major version changes the shape of the header
itself, otherwise the backend reports a confusing parse error instead of "unsupported version".

## Options considered

1. **One integer version, every change incompatible** — simplest; every added field breaks old backends.
2. **Integer major and minor, minor changes additive, unknown fields ignored** — old backends keep reading
   batches with new optional fields; incompatible changes raise the major and are rejected cleanly.
3. **Version string (`"1.0"`, SemVer)** — familiar; needs its own parser with more accepted forms (leading
   zeros, `v` prefix, pre-release suffixes) and therefore more ways to bypass the check.
4. **Strict decoding (`DisallowUnknownFields`) within a minor, reject newer minors** — catches typos in the
   agent; turns every additive change into a breaking one, like option 1.

## Decision

Option 2. The header carries `format_major` and `format_minor` as JSON integers; `format_major` is an
integer in every version, now and later. The current version is 1.0 (`wire.MajorVersion`,
`wire.MinorVersion`). The decoder first reads only `format_major` from the first line and rejects any value
other than `1` (including a missing field, `0` and negative values) with `wire.ErrUnsupportedVersion`
before it parses the rest of the header or any record. Within major 1 every minor is accepted (negative
values are invalid); unknown object keys in the header, the envelope and the payloads are ignored. A minor
version may only add optional fields or new record kinds; renaming, removing or retyping a field, changing
a field's meaning or tightening a validation rule needs a new major version. A record `kind` the decoder
does not know is rejected (`wire.ErrUnknownKind`), so an older backend refuses a batch that contains a
newer kind rather than dropping those records silently.

## Consequences

- New optional fields can be rolled out agent-first without breaking an older backend.
- A new record kind is not readable by an older backend: the batch is rejected and stays in the agent's
  spool. The rules for which agent and backend versions work together (version negotiation, upgrade order)
  are #85; until then the backend is upgraded before the agent.
- Ignoring unknown keys means a misspelled optional field in the agent is silently dropped; the unit tests
  pin every field name, and `docs/WIRE_FORMAT.md` is the reference.
