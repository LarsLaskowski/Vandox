# 0122: CAP_SYS_PTRACE alone cannot write another user's process memory through `/proc/<pid>/mem` or `/proc/<pid>/fd`: CAP_DAC_READ_SEARCH dropped, `process_madvise` denied, a file-mode residual accepted

- **Status:** Accepted
- **Date:** 2026-10-23
- **Area:** Agent rights
- **Source:** Issue #271
- **Supersedes:** 0030 (amends it: the agent holds `CAP_SYS_PTRACE` alone instead of `CAP_SYS_PTRACE` and `CAP_DAC_READ_SEARCH`, a bounding set holding only that capability becomes a load-bearing control, `process_madvise` joins the system calls the filter denies, and the write guarantee is restated with an accepted residual; 0030 is not edited)

## Context

The security review of the process collector (#32) raised a concrete write vector against the guarantee of
[0030](0030-agent-runs-unprivileged-with-named-capabilities.md) and security area 6 of `.squad/project.md`: that a compromised agent
cannot use its capabilities to write as or run code as another user. Opening `/proc/<pid>/mem` checks `PTRACE_MODE_ATTACH`, which
CAP_SYS_PTRACE satisfies, and the agent's system-call filter denies neither `openat` nor `pwrite`.

Measured, the `mem` vector is closed by the ordinary DAC check that runs before `mem_open`: the file is mode `0600` and owned by the
target's user, so a write-capable open needs CAP_DAC_OVERRIDE. A second vector, `pidfd_open` followed by `process_madvise`, is refused
by the kernel for the agent's capabilities, but which advice the kernel accepts is memory-management policy that has grown between
versions. A third vector breaks the guarantee as 0030 wrote it: the Product Manager measured on kernels 4.4, 5.15, 6.8 and 7.0 that a
user holding CAP_SYS_PTRACE and CAP_DAC_READ_SEARCH opens `/proc/<pid>/fd/<n>` of a root process's memfd read-write and changes that
process's `MAP_SHARED` mapping with `pwrite`; with CAP_SYS_PTRACE alone the open fails. With both capabilities every file another
process holds open whose own mode grants the agent write is writable, and CAP_DAC_READ_SEARCH alone also opens such files below
directories closed to the agent. The measurements are in [Agent rights](../areas/agent-rights.md).

The process collector needs CAP_SYS_PTRACE alone for `smaps_rollup` (0113), the connection collector reads `fdinfo` with
CAP_SYS_PTRACE alone and deliberately not the `fd` links (0144), and the journal and logs come through the groups `systemd-journal`
and `adm`. 0030 granted CAP_DAC_READ_SEARCH only for listing `/proc/<pid>/fd`, which 0144 gave up; the log shipping collector would use
it only for a configured file that neither its user nor its groups can read.

## Options considered

1. **Keep both capabilities and accept the write residual** — no change to the rights, but a compromised agent could change the
   shared memory of any process and possibly, where a runtime executes from a memfd mapping, run code as its user: a product decision
   against the guarantee, for a capability only the log shipping collector would use.
2. **Keep both capabilities and confine `/proc/<pid>/fd` with an AppArmor or SELinux profile** — a new artifact per distribution,
   kernel- and policy-dependent; whether AppArmor mediates a magic-link open by the `/proc` path at all was not measured.
3. **systemd `ProtectProc=`, `ProcSubset=` or `hidepid`** — `hidepid` hides no process from a holder of CAP_SYS_PTRACE, and
   `ProcSubset=pid` hides `/proc/net`, which the connection collector reads; none changes the permission on `/proc/<pid>/fd`.
4. **Drop CAP_SYS_PTRACE instead, or read the PSS from a source without it** — closes the memfd path but loses the PSS and the socket
   owners, keeps the read-everything residual and leaves CAP_DAC_READ_SEARCH's own write path below closed directories open.
5. **Seccomp rules on `pwrite`/`openat` or `proc_mem.force_override=never`** — seccomp cannot inspect a path, and `force_override`
   (kernel 6.12+) acts only once a write-capable `mem` descriptor exists.
6. **Leave `process_madvise` to the kernel** — holds today, but rests on a rule this project neither controls nor tests; a later
   grant of CAP_SYS_NICE or a kernel widening the advice set would reopen it unnoticed.
7. **Drop CAP_DAC_READ_SEARCH, keep CAP_SYS_PTRACE alone with a bounding set holding only it, and deny `process_madvise` by name**
   (chosen by the Product Manager) — closes the memfd path (measured), costs the log shipping collector only the configured files its
   user and groups cannot read, and narrows the read residual of 0030 that rested on CAP_DAC_READ_SEARCH. The agent never calls
   `process_madvise`, so denying it costs nothing. It leaves the residual of files whose own mode grants the agent write.

## Decision

The agent holds `CAP_SYS_PTRACE` alone, with a bounding set that holds only that capability, and the unit's filter also denies
`process_madvise` by name. The Product Manager accepted the residual this leaves and, after the log shipping collector merged (#293),
its cost there, so the write guarantee holds in the qualified form of [Agent rights](../areas/agent-rights.md), not in 0030's absolute
one; the barriers per vector, their measurements and the residual are stated there.

## Consequences

- The systemd unit of #42 may grant CAP_SYS_PTRACE, with a bounding set limited to it and a filter that denies the six calls the area
  lists.
- 0030's absolute "cannot write as or run code as another user" does not hold: through a file whose own mode grants the agent write,
  reached along another process's `cwd`, `root` or `exe` link, the agent can still write another user's process memory where that file
  is mapped `MAP_SHARED`, and run code as another user who executes or reads it. The title's "through `/proc/<pid>/mem` or
  `/proc/<pid>/fd`" is therefore deliberate. A separate small helper that holds the capability is the way to reduce it.
- Adding any capability to the bounding set, or removing one of the denied calls, is a `security` change that must supersede this
  record; a collector that needs the `/proc/<pid>/fd` links again reopens the memfd write and needs a new decision first.
- The log shipping collector ships only the configured files its user and groups can read (record 0165).
- The read residual of 0030 ("its read access equals root's") narrows: without CAP_DAC_READ_SEARCH the agent cannot read
  `/etc/shadow` or another user's `mem` or `environ`.
- `process_madvise` sits in systemd's `@ipc` group, which `@system-service` includes, so the unit must deny it by name; if the
  server's systemd did not know the name, the entry would be skipped and only the kernel's refusal would remain, so the installation
  checks it on the server.
- `proc_mem.force_override=never` stays unset: it is not needed for the `mem` open, and the production kernel (5.15) predates it.
