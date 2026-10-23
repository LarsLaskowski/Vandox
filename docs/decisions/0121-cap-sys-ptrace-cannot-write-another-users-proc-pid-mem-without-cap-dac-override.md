# 0121: CAP_SYS_PTRACE cannot write another user's process memory: no CAP_DAC_OVERRIDE for `/proc/<pid>/mem`, `process_madvise` denied

- **Status:** Proposed
- **Date:** 2026-10-23
- **Area:** Agent
- **Source:** Issue #271
- **Supersedes:** 0030 (amends it: records which barrier blocks each write vector against another user's process memory, makes the exclusion of CAP_DAC_OVERRIDE and every write-side DAC capability from the bounding set a load-bearing control, and adds `process_madvise` to the system calls the filter denies; 0030 is not edited)

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
CAP_SYS_PTRACE does not grant it, and CAP_DAC_READ_SEARCH grants read and search only, not write.

The plan's Security review then found a vector that neither the review of #32 nor the filter covers:
`pidfd_open` on the target followed by `process_madvise` on the returned descriptor. The filter deliberately
leaves `pidfd_open` allowed (the Go runtime uses it) and did not name `process_madvise`. Measured on kernel 6.18
as user 65534 against a root process: without capabilities every advice fails `EACCES` (the ptrace read check);
with CAP_SYS_PTRACE alone the destructive advice `MADV_DONTNEED` and `MADV_FREE` fail `EINVAL` and the
non-destructive `MADV_COLD`, `MADV_PAGEOUT` and `MADV_WILLNEED` fail `EPERM`, because since kernel 5.12 the call
also requires CAP_SYS_NICE; only with CAP_SYS_PTRACE and CAP_SYS_NICE together are the three non-destructive
values accepted, and the destructive ones still fail `EINVAL`. Security's probe, run against a process of its
own user, showed the target's memory content unchanged under every accepted advice. Which advice the kernel
accepts for another process is memory-management policy that has grown between versions (`MADV_COLLAPSE` was
added later), not a permission rule.

So no single mechanism stops every vector; three distinct barriers do:

1. **DAC and the bounding set**, for opening another user's `/proc/<pid>/mem` for writing: the open needs
   CAP_DAC_OVERRIDE, which the bounding set excludes.
2. **The system-call filter**, for `ptrace` (POKE), `process_vm_writev`, `pidfd_getfd` (stealing a writable
   descriptor) and `open_by_handle_at`.
3. **For the `pidfd_open` + `process_madvise` path, the kernel itself:** the call needs CAP_SYS_NICE, which
   the bounding set excludes, and even with it the kernel refuses destructive advice on another process. The
   first is a capability rule (since 5.12, so on the production 5.15); the second is version-dependent
   memory-management policy, measured on 6.18.

## Options considered

1. **Treat the review's premise as correct and add a mitigation for the `mem` open** (e.g.
   `proc_mem.force_override=never` on kernel 6.12+, a seccomp rule on `pwrite`, or a different PSS source) —
   would harden a path that is already closed. `proc_mem.force_override` only governs `FOLL_FORCE` *after* a
   write-capable descriptor exists; it does nothing for the open gate, which is where the write is actually
   stopped. A seccomp rule cannot inspect the path of an `openat`. Unnecessary complexity and a false sense of
   where the barrier is.
2. **Correct the reasoning and leave `process_madvise` to the kernel alone** — the guarantee holds today, but
   it would rest on a kernel rule this project neither controls nor tests, and on an advice set that has
   grown between versions. Rejected: a later change that granted CAP_SYS_NICE (for example to renice
   processes) or a kernel that widened the set would reopen the vector without anything noticing.
3. **Correct the reasoning and also deny `process_madvise` in the filter** (chosen) — the agent never calls
   it (the Go runtime uses `madvise` on its own memory only, and no dependency calls `process_madvise`), so the
   denial costs nothing and puts the vector behind a control of this project, with barrier 3 as defence in
   depth. `pidfd_open` stays allowed:
   the Go runtime uses it to wait for and signal child processes, and alone it gives no access to the target's
   memory.

## Decision

The agent's capability bounding set must exclude CAP_DAC_OVERRIDE and every other write-side DAC capability,
so that a compromised agent cannot obtain a write-capable handle to another user's `/proc/<pid>/mem`; this is
a load-bearing control alongside the system-call filter, not covered by it. It also keeps CAP_SYS_NICE out,
which `process_madvise` on another process requires. The filter's denied system calls
gain `process_madvise`, so that the `pidfd_open` path to another process's memory is closed by the filter and
the kernel's refusal of destructive advice is only the second line. The guarantee of 0030 is upheld
unchanged; no other mitigation is introduced. The measurements, the vector-by-barrier table and the rules they
establish are in the [Agent](../areas/agent.md) area (*Rights*).

## Consequences

- The dependency this issue guarded is cleared: the systemd unit of #42 may grant CAP_SYS_PTRACE (and, if a
  collector ever needs it, CAP_DAC_READ_SEARCH) as long as the bounding set keeps CAP_DAC_OVERRIDE and every
  write-side DAC capability out and its filter denies `process_madvise` with the other five calls.
- A future change that adds CAP_DAC_OVERRIDE, another write-side DAC capability or CAP_SYS_NICE to the bounding set, or
  removes `ptrace`, `process_vm_writev`, `pidfd_getfd`, `open_by_handle_at` or `process_madvise` from the
  denied calls, breaks the 0030 guarantee and is a `security` change that must supersede this record.
- `process_madvise` sits in systemd's `@ipc` group, which `@system-service` includes, so an allow-list built on
  `@system-service` admits it: the unit must deny it by name. If the systemd on the server did not know the
  name, the entry would be skipped and only barrier 3 would remain; #42 checks that the server's systemd lists
  it. A kernel that let `process_madvise` apply destructive advice to another process would matter only for a
  unit that neither denies the call nor withholds CAP_SYS_NICE.
- The read/write boundary now rests on an explicit, measured fact: with CAP_DAC_READ_SEARCH the agent can open
  another user's `mem` read-only, which is the already-accepted residual of 0030 (its read access equals
  root's, the memory and environment of every process included); it cannot open it for writing.
- `proc_mem.force_override=never` (kernel 6.12+) is available as defence in depth but is not required and is
  not adopted: it only limits `FOLL_FORCE` once a write-capable descriptor exists, which the agent cannot
  obtain. The production servers (Ubuntu 22.04, kernel 5.15) predate the configurable policy; barrier 1 is
  independent of it.
- The documented reasoning in security area 6, `deploy/agent/README.md`, the Agent area and
  `docs/ARCHITECTURE.md` is corrected so that it no longer attributes the prevention of every vector to the
  system-call filter, and names `process_madvise` among the denied calls.
