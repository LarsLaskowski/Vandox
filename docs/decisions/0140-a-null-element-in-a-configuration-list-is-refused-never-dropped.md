# 0140: A null element in a list option of the agent's configuration is refused, never dropped

- **Status:** Accepted
- **Date:** 2026-10-17
- **Area:** Configuration and secrets
- **Source:** Issue #31
- **Supersedes:** —

## Context

Issue #31 adds `filesystem.mount_points`, the first list option of the agent's configuration. The pinned YAML library drops a
null element of a list on its own, so `[~]` decodes as an empty list; for `filesystem.mount_points` that would silently switch
the file system metrics off. The configuration is decoded strictly (0049): what the operator wrote either takes effect or is
refused.

## Options considered

1. **Drop the null element, as the YAML library does** — `[~]` would become an empty list and silently switch the file system
   metrics off.
2. **A check after decoding that the list kept every element** — unreachable with the pinned library, whose only silent drop is
   a null element.
3. **Refuse a null element before decoding as "has no value"** (chosen), for every list option of the agent's configuration.

## Decision

The agent refuses a null element in any list option of its configuration with `has no value` before the YAML library decodes
the list, so no element is dropped silently. The rule is in [Configuration and secrets](../areas/configuration-and-secrets.md)
(*Configuration file*) and per option in [Configuration options](../areas/configuration-options.md).

## Consequences

- Every later list option (`processes.programs`, `services.units`, `backups.processes` and the log shipping lists) inherits the
  rule without a decision of its own.
- A library update that changes how null elements decode does not weaken the rule, because the check runs before decoding.
