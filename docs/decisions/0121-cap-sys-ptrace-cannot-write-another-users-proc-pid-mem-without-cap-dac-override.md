# 0121: CAP_SYS_PTRACE cannot write another user's process memory: no CAP_DAC_OVERRIDE for `/proc/<pid>/mem`, `process_madvise` denied

- **Status:** Proposed
- **Date:** 2026-10-23
- **Area:** Agent
- **Source:** Issue #271
- **Supersedes:** 0030 (amends it: the agent holds `CAP_SYS_PTRACE` alone instead of `CAP_SYS_PTRACE` and `CAP_DAC_READ_SEARCH`, the exclusion of CAP_DAC_OVERRIDE, every write-side DAC capability and CAP_SYS_NICE from the bounding set becomes a load-bearing control, and `process_madvise` joins the system calls the filter denies; 0030 is not edited)

## Context

The security review of the process collector (#32) raised a concrete write vector against the guarantee of
[0030](0030-agent-runs-unprivileged-with-named-capabilities.md) and security area 6 of `.squad/project.md`:
that a compromised agent cannot use its capabilities to write as or run code as another user. From the kernel
source, opening `/proc/<pid>/mem` checks `PTRACE_MODE_ATTACH`, which CAP_SYS_PTRACE satisfies, and a `pwrite`
on the resulting descriptor writes into the target's address space; the agent's system-call filter denies
neither `openat` nor `pwrite`.

Measured (recorded in the [Agent](../areas/agent.md) area, *Rights*), the `mem` vector is closed by a step the
review skipped: the VFS runs the ordinary DAC check before `mem_open`, and `mem` is mode `0600` owned by the
target's user, so a write-capable open needs CAP_DAC_OVERRIDE, which neither CAP_SYS_PTRACE nor
CAP_DAC_READ_SEARCH grants. A second vector, `pidfd_open` followed by `process_madvise`, is refused by the
kernel for the agent's capabilities (the call needs CAP_SYS_NICE for any other process since kernel 5.12, and
destructive advice on another process is refused); which advice the kernel accepts is memory-management
policy that has grown between versions.

A third vector breaks the guarantee as 0030 wrote it. The Product Manager measured on kernels 4.4, 5.15, 6.8
and 7.0: a user holding CAP_SYS_PTRACE and CAP_DAC_READ_SEARCH opens `/proc/<pid>/fd/<n>` of a root process's
memfd read-write and a `pwrite` changes that process's `MAP_SHARED` mapping; with CAP_SYS_PTRACE alone or no
capability the open fails `EACCES`. Listing `/proc/<pid>/fd` (mode `0500`, owned by the target's user) needs
the directory-search bypass of CAP_DAC_READ_SEARCH, following the link needs the ptrace check of
CAP_SYS_PTRACE, and the opened file is then checked only against its own mode, which for a memfd is `0777`.
So, with both capabilities, every file another process holds open whose mode grants the agent write is
writable — the unsealed shared memory of every process, root's included, and the generated code of a runtime
that maps it from a memfd (an inference from the runtimes' design, not measured). CAP_DAC_READ_SEARCH alone also opens for writing every file whose own mode
allows it below a directory closed to the agent (measured). The filter cannot stop either (`openat`, `pwrite`).

No collector uses CAP_DAC_READ_SEARCH: the process collector needs CAP_SYS_PTRACE alone for `smaps_rollup`
(0113), the connection collector reads `fdinfo` with CAP_SYS_PTRACE alone and deliberately not the `fd` links
(0115), and the journal and logs come through the groups `systemd-journal` and `adm`. 0030 granted it only for
listing `/proc/<pid>/fd`, which 0115 gave up.

## Options considered

1. **Keep both capabilities and accept the write residual** — no change to the rights, but the guarantee of
   0030 would no longer hold: a compromised agent could change the shared memory of any process and possibly,
   where a runtime executes from a memfd mapping, run code as its user. A product decision against the guarantee, for
   a capability nothing uses. Rejected.
2. **Keep both capabilities and confine `/proc/<pid>/fd` with an AppArmor or SELinux profile** — a new
   artifact per distribution, kernel- and policy-dependent; whether AppArmor mediates a magic-link open by the
   `/proc` path at all was not measured. Rejected.
3. **systemd `ProtectProc=`, `ProcSubset=` or `hidepid`** — `hidepid` hides no process from a holder of
   CAP_SYS_PTRACE, and `ProcSubset=pid` hides `/proc/net`, which the connection collector reads; none changes
   the permission on `/proc/<pid>/fd`. Rejected.
4. **Drop CAP_SYS_PTRACE instead, or read the PSS from a source without it** — closes the memfd path but loses
   the PSS and the socket owners, keeps the read-everything residual and leaves CAP_DAC_READ_SEARCH's own write
   path below closed directories open. Rejected.
5. **Seccomp rules on `pwrite`/`openat` or `proc_mem.force_override=never`** — seccomp cannot inspect a path,
   and `force_override` (kernel 6.12+) acts only once a write-capable `mem` descriptor exists. Rejected.
6. **Leave `process_madvise` to the kernel** — holds today, but rests on a rule this project neither controls
   nor tests; a later grant of CAP_SYS_NICE or a kernel widening the advice set would reopen it unnoticed.
   Rejected.
7. **Drop CAP_DAC_READ_SEARCH, keep CAP_SYS_PTRACE alone, deny `process_madvise` by name and keep the
   write-side DAC capabilities and CAP_SYS_NICE out of the bounding set** (chosen) — closes the memfd path
   (measured), costs no collector and also ends the read residual of 0030 that rested on CAP_DAC_READ_SEARCH.
   The agent never calls `process_madvise`, so denying it costs nothing.

## Decision

The agent holds `CAP_SYS_PTRACE` alone, with a bounding set limited to it, so that CAP_DAC_READ_SEARCH,
CAP_DAC_OVERRIDE, every other write-side DAC capability and CAP_SYS_NICE stay out; the unit's filter also
denies `process_madvise` by name. The barriers this rests on, their measurements and the remaining residual
are in the [Agent](../areas/agent.md) area (*Rights*).

## Consequences

- The dependency this issue guarded is cleared: the systemd unit of #42 may grant CAP_SYS_PTRACE, with a
  bounding set limited to it and a filter that denies `ptrace`, `process_vm_readv`, `process_vm_writev`,
  `pidfd_getfd`, `open_by_handle_at` and `process_madvise`.
- Adding CAP_DAC_READ_SEARCH, CAP_DAC_OVERRIDE, another write-side DAC capability or CAP_SYS_NICE to the
  bounding set, or removing one of these six calls from the denied set, breaks the guarantee and is a
  `security` change that must supersede this record. A collector that needs the `/proc/<pid>/fd` links again
  reopens the memfd write and needs a new decision first.
- The read residual of 0030 ("its read access equals root's") narrows: without CAP_DAC_READ_SEARCH the agent
  cannot read `/etc/shadow`, another user's `mem` or `environ`. What remains readable and writable — the
  ptrace-checked `/proc` files and the files whose own mode allows the agent, also below a closed directory
  that is another process's working or root directory — is stated in the Agent area and area 6.
- `process_madvise` sits in systemd's `@ipc` group, which `@system-service` includes, so the unit must deny it
  by name. If the server's systemd did not know the name, the entry would be skipped and the kernel's refusal
  would remain; #42 checks that the server's systemd lists it.
- `proc_mem.force_override=never` stays unset: it is not needed for the `mem` open, and the production kernel
  (5.15) predates it.
- Security area 6, `deploy/agent/README.md`, the Agent area and `docs/ARCHITECTURE.md` name CAP_SYS_PTRACE as
  the one capability, the three barriers per vector and the narrowed residual.
