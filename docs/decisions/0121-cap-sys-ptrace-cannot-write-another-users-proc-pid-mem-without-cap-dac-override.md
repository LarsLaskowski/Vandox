# 0121: CAP_SYS_PTRACE cannot write another user's `/proc/<pid>/mem`; the barrier is the absence of CAP_DAC_OVERRIDE

- **Status:** Proposed
- **Date:** 2026-10-23
- **Area:** Agent
- **Source:** Issue #271
- **Supersedes:** 0030 (amends it: records why the `/proc/<pid>/mem` write vector is blocked and makes the exclusion of CAP_DAC_OVERRIDE and every write-side DAC capability from the bounding set a load-bearing control; 0030 is not edited)

## Context

The security review of the process collector (#32) raised a concrete write vector against the guarantee of
[0030](0030-agent-runs-unprivileged-with-named-capabilities.md) and security area 6 of `.squad/project.md`:
that a compromised agent cannot use its capabilities to write as or run code as another user. From the kernel
source, opening `/proc/<pid>/mem` checks `PTRACE_MODE_ATTACH`, which CAP_SYS_PTRACE satisfies, and a `pwrite`
on the resulting descriptor writes into the target's address space. The agent's system-call filter denies
`ptrace`, `process_vm_readv`, `process_vm_writev`, `pidfd_getfd` and `open_by_handle_at`, but it denies
neither `openat` nor `pwrite`. The review therefore could not rule out that CAP_SYS_PTRACE alone lets the
agent open another user's `mem` read-write and write to it; the measurement was refused in the review
sandbox, so the claim was left unverified and the systemd unit (#42) was not to grant the capability until it
was settled.

The measurement was now taken (recorded in the [Agent](../areas/agent.md) area, *Rights*). It shows the
review's premise is wrong on one step: `PTRACE_MODE_ATTACH` is checked only inside `mem_open`, but the VFS
runs the ordinary DAC permission check on the file first. `/proc/<pid>/mem` is mode `0600` owned by the
target's user, so a write-capable open (`O_RDWR`/`O_WRONLY`) of another user's `mem` requires CAP_DAC_OVERRIDE.
CAP_SYS_PTRACE does not grant it, and CAP_DAC_READ_SEARCH grants read and search only, not write. Without a
write-capable descriptor there is no `pwrite`, and every alternate path to one (`ptrace` POKE,
`process_vm_writev`, stealing a descriptor with `pidfd_getfd`, `open_by_handle_at`) is a system call the filter
denies.

## Options considered

1. **Treat the review's premise as correct and add a mitigation** (e.g. `proc_mem.force_override=never` on
   kernel 6.12+, a seccomp rule on `pwrite`, or a different PSS source) — would harden a path that is already
   closed. `proc_mem.force_override` only governs `FOLL_FORCE` *after* a write-capable descriptor exists; it
   does nothing for the open gate, which is where the write is actually stopped. Unnecessary complexity and a
   false sense of where the barrier is.
2. **Correct the reasoning and record the real barrier** (chosen) — the write is prevented by the bounding set
   excluding CAP_DAC_OVERRIDE (so no write-capable open of another user's `mem`) together with the filter
   denying the syscall-based write vectors. The guarantee of 0030 holds; its stated reason was incomplete for
   this one vector and is amended here.

## Decision

The agent's capability bounding set must exclude CAP_DAC_OVERRIDE and every other write-side DAC capability,
so that a compromised agent cannot obtain a write-capable handle to another user's `/proc/<pid>/mem`; this is
a load-bearing control alongside the system-call filter, not covered by it. The guarantee of 0030 is upheld
unchanged; no additional mitigation is introduced. The measurement and the rules it establishes are in the
[Agent](../areas/agent.md) area (*Rights*).

## Consequences

- The dependency this issue guarded is cleared: the systemd unit of #42 may grant CAP_SYS_PTRACE (and, if a
  collector ever needs it, CAP_DAC_READ_SEARCH) as long as the bounding set keeps CAP_DAC_OVERRIDE and every
  write-side DAC capability out. A future change that would add CAP_DAC_OVERRIDE to the bounding set, or widen
  the system-call filter to allow a process-attach write call, breaks the 0030 guarantee and is a `security`
  change that must supersede this record.
- The read/write boundary now rests on an explicit, measured fact: with CAP_DAC_READ_SEARCH the agent can open
  another user's `mem` read-only, which is the already-accepted residual of 0030 (its read access equals
  root's, the memory and environment of every process included); it cannot open it for writing.
- `proc_mem.force_override=never` (kernel 6.12+) is available as defence in depth but is not required and is
  not adopted: it only limits `FOLL_FORCE` once a write-capable descriptor exists, which the agent cannot
  obtain. The production servers (Ubuntu 22.04, kernel 5.15) predate the configurable policy; the barrier here
  is independent of it.
- The documented reasoning in security area 6, `deploy/agent/README.md`, the Agent area and
  `docs/ARCHITECTURE.md` is corrected so that it no longer attributes the prevention of this vector to the
  system-call filter alone.
