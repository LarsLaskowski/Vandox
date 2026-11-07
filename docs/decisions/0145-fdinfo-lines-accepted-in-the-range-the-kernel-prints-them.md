# 0145: The owner scan accepts the first fdinfo lines in the range the kernel prints them

- **Status:** Accepted
- **Date:** 2026-10-18
- **Area:** Agent connections
- **Source:** Issue #341
- **Supersedes:** —

## Context

The owner scan ([0144](0144-socket-owners-from-fdinfo-on-the-socket-mount-the-agent-learns-at-its-start.md)) reads the first four
lines of each `fdinfo` file. Its parser demanded unsigned digits and a non-zero `mnt_id:`, so a process holding a memfd (on the
kernel's internal shmem mount, the first mount the kernel creates, whose ID is 0) or a `/proc/<pid>/mem` positioned above 2^63
(`pos:` is signed) was counted unreadable on every run, and its sockets lost their owner: the server logged
`socket owners partly unknown` (#341). Upstream `fs/proc/fd.c` at v5.15 prints `pos:\t%lli`, `flags:\t0%o`, `mnt_id:\t%i`,
`ino:\t%lu`, and `fs/namespace.c` at v5.15 numbers mounts from 0 (`ida_alloc`) with `shmem_init` the first mount in `mnt_init`;
both values were captured on 6.18.44.

## Options considered

1. **Accept the first four `fdinfo` lines in the range the kernel prints them** (chosen) — the parser accepts what that format
   prints; the mount ID 0 is never the socket mount, and the start-up probe keeps refusing it, because 0 means "not learned" to
   the collector, and the owner scan counts nothing on mount 0.
2. **Skip a descriptor whose `fdinfo` has another shape instead of stopping its process** — would have silenced #341 without
   knowing the cause, and would let a future format change drop owners without a warning. Rejected: an unexpected shape stays
   visible as `socket owners partly unknown`.

## Decision

The owner scan reads the first four `fdinfo` lines in the range the kernel prints them and still stops a process whose file has
another shape. The rules are in [Agent connections](../areas/agent-connections.md) (*Owners*).

## Consequences

- Residual: the server's own `fdinfo` files were not captured, so its warning is attributed to these two values by the kernel
  source, not by a sample from the server; the fix was checked on the production server only after the merge, when the Product
  Manager confirms the warning is gone (a warning that remains is a new issue with the server's `head -4` output).
