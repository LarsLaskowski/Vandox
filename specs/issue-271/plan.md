# Plan: Settle whether CAP_SYS_PTRACE can write another process's memory before the agent unit grants it

Source: Issue #271
Status: Draft
Tier: standard — corrects the documented reasoning of a security area (area 6, *Agent privileges*) and adds a
load-bearing control across four product documents plus a new decision record; no production or test code, so
beyond the `patch` budget, but it removes/narrows no security area (`.squad/routing.md`, *Tiers*: an edit to
*Security areas* beyond patch budget that does not narrow an area is `standard`).
Security areas touched: area 6 (Agent privileges) of `.squad/project.md`.

## Problem / root cause

Not a code bug: a security claim to settle. The process collector (#32, record 0113) needs `CAP_SYS_PTRACE`
to read `smaps_rollup` of other users' processes, and the agent unit (#42) must not grant the capability
until it is proven that the capability cannot be turned into a write into another user's process memory.

The review of #32 reasoned from kernel source that opening `/proc/<pid>/mem` checks `PTRACE_MODE_ATTACH`
(satisfied by `CAP_SYS_PTRACE`) and that a `pwrite` on it writes into the target, and that the agent's
system-call filter denies neither `openat` nor `pwrite` — leaving open that a compromised agent could write as
another user. The measurement was refused in the review sandbox, so the claim was `unverified`.

**Root finding (measured here):** the review's premise skips the VFS DAC check that runs *before* `mem_open`.
`/proc/<pid>/mem` is mode `0600` owned by the target's user, so a write-capable open (`O_RDWR`/`O_WRONLY`) of
another user's `mem` requires `CAP_DAC_OVERRIDE`. `CAP_SYS_PTRACE` does not grant it; `CAP_DAC_READ_SEARCH`
grants read/search only, not write. The agent's bounding set is limited to exactly `CAP_SYS_PTRACE` and
`CAP_DAC_READ_SEARCH` (record 0030, area 6), so no write-capable descriptor to another user's `mem` can be
obtained, and every syscall-based write vector (`ptrace` POKE, `process_vm_writev`, `pidfd_getfd`,
`open_by_handle_at`) is denied by the filter. The 0030 guarantee holds; its stated *reason* was incomplete for
this vector and is corrected.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | `/proc/<pid>/mem` of a root process is mode `0600`, owner root. | `stat -c %A %U /proc/<pid>/mem` on a root-owned `sleep`: `-rw------- root`. | confirmed |
| 2 | As uid 65534 with `CAP_SYS_PTRACE` effective, opening a root process's `/proc/<pid>/mem` with `O_RDONLY`, `O_RDWR` or `O_WRONLY` fails `EACCES`. | Measured: capsh `--user=nobody --inh=cap_sys_ptrace --addamb=cap_sys_ptrace` running a C probe that opens-and-closes only (`CapEff=0x80000` confirmed via `/proc/self/status`); all three modes `EACCES` (errno 13). | confirmed |
| 3 | As uid 65534 with `CAP_SYS_PTRACE`+`CAP_DAC_READ_SEARCH`, `O_RDONLY` succeeds; `O_RDWR`/`O_WRONLY` fail `EACCES`. | Same probe with both caps ambient: `O_RDONLY` ok, `O_RDWR`/`O_WRONLY` `EACCES`. | confirmed |
| 4 | As uid 65534 with `CAP_SYS_PTRACE`+`CAP_DAC_OVERRIDE`, all three modes succeed. | Same probe with `CAP_DAC_OVERRIDE` ambient: all ok. Control showing `CAP_DAC_OVERRIDE` is the capability that unlocks a write-capable open. | confirmed |
| 5 | The agent's capability bounding set is limited to exactly `CAP_SYS_PTRACE` and `CAP_DAC_READ_SEARCH`; `CAP_DAC_OVERRIDE` is not in it, and `NoNewPrivileges=yes` prevents gaining it. | `.squad/project.md` area 6; record 0030 (*Decision*, option 4); `docs/areas/agent.md` *Rights*; `deploy/agent/README.md`. | confirmed |
| 6 | The system-call filter denies `ptrace`, `process_vm_readv`, `process_vm_writev`, `pidfd_getfd`, `open_by_handle_at`, and does not deny `openat` or `pwrite`. | `.squad/project.md` area 6; `docs/areas/agent.md` *Rights*; `deploy/agent/README.md` lines 19–20; issue #271. | confirmed |
| 7 | A `pwrite` into another user's memory requires a write-capable descriptor (`O_RDWR`/`O_WRONLY`), which (facts 2–4) cannot be obtained with the agent's capability set; the alternate paths to one are the five syscalls the filter denies (fact 6). | Reasoning from facts 2–6: a read-only descriptor cannot `pwrite`; no other route to a writable `mem` fd exists that the filter leaves open. | confirmed |
| 8 | The measurement generalizes to the production kernel (Ubuntu 22.04, kernel 5.15) although taken on kernel 6.18.44 in the same user namespace without Yama/LSM. | By established DAC semantics: DAC file-permission checks and the meaning of `CAP_DAC_OVERRIDE`/`CAP_DAC_READ_SEARCH` are stable, long-standing kernel behavior independent of version and namespace; the barrier is the `0600` ownership check, not a version-specific path. | confirmed |
| 9 | `proc_mem.force_override` (kernel 6.12+) governs `FOLL_FORCE` only after a write-capable descriptor exists and is irrelevant to the open gate; the sandbox kernel is `CONFIG_PROC_MEM_ALWAYS_FORCE=y`, the production kernel (5.15) predates the configurable policy. | `zcat /proc/config.gz`: `CONFIG_PROC_MEM_ALWAYS_FORCE=y`; `/proc/cmdline` has no `proc_mem.*`. | confirmed |

## Acceptance criteria

(From the issue; each verified without a test — see *Verification without tests*.)

- [ ] AC1: The measurement is recorded — the reproducible capability matrix for opening another user's
  `/proc/<pid>/mem` (modes × capability sets, with observed result) and the method, in `docs/areas/agent.md`
  (*Rights*) and summarized in record 0121.
- [ ] AC2: Area 6 of `.squad/project.md`, `deploy/agent/README.md`, `docs/areas/agent.md` (*Rights*) and
  `docs/ARCHITECTURE.md` state what holds: a write-capable open of another user's `/proc/<pid>/mem` is refused
  without `CAP_DAC_OVERRIDE`, which the bounding set excludes; the system-call filter does not deny
  `openat`/`pwrite`, so this vector is blocked by DAC (the bounding-set exclusion), not by the filter; a
  read-only open succeeds with `CAP_DAC_READ_SEARCH`, which is the already-accepted read residual of 0030.
- [ ] AC3: A mitigation exists if the write works — the write does **not** work with the agent's capability
  set, so no new mitigation code is introduced; the control that prevents it (the bounding set excludes
  `CAP_DAC_OVERRIDE` and every write-side DAC capability) is documented as a required, load-bearing control in
  area 6 and the Agent area, and record 0121 states that adding such a capability, or widening the filter to
  allow a process-attach write call, is a `security` change that must supersede it. `proc_mem.force_override`
  is documented as non-required defence in depth.

## Tests per criterion

Not applicable — the change touches no production or test code (*Changes without production or test code* in
`.squad/routing.md`). Every acceptance criterion is verified under *Verification without tests*.

## Verification without tests

Steps 4 (*Skeleton*), 5 (*Tests first*) and the *Coverage gate* are not applicable: the diff is documentation
and one decision record, no production or test code.

- **AC1** — Reviewer (step 8) and Security (step 3) confirm the capability matrix and method as written in
  `docs/areas/agent.md` (*Rights*) and record 0121 match the measurement reported here; the measurement is
  reproducible with `capsh --user=nobody --inh=<caps> --addamb=<caps> -- -c <open-and-close probe>` against a
  root-owned process. No runtime artifact is shipped.
- **AC2** — Reviewer checks the four documents against record 0121 and this plan; Security (step 3) checks that
  the corrected reasoning in area 6 is accurate and complete (write blocked by DAC + bounding-set exclusion,
  not the filter; read-only residual acknowledged).
- **AC3** — Reviewer and Security confirm no code mitigation is added, that the bounding-set exclusion of
  `CAP_DAC_OVERRIDE`/write-side DAC capabilities is stated as required in area 6 and the Agent area, and that
  record 0121 names the conditions under which the guarantee would break.

## Approach

No user-visible output. The change (a) adds the measured capability matrix and its conclusion to the Agent
area's *Rights* section, (b) corrects the one-line "why the write cannot happen" reasoning in area 6,
`deploy/agent/README.md` and `docs/ARCHITECTURE.md` so it names the bounding-set exclusion of
`CAP_DAC_OVERRIDE` as the barrier for the `/proc/<pid>/mem` vector (the filter covers the syscall vectors),
and (c) records the decision and the load-bearing control in record 0121 (amends released record 0030).

## Options considered

One option for the documentation fix: the measurement is conclusive, so the only reasonable action is to
correct the reasoning and record the real barrier. The mitigation alternatives the issue proposed were
weighed and rejected as unnecessary, because the write is already impossible with the agent's capability set:

1. `proc_mem.force_override=never` (kernel 6.12+) — governs `FOLL_FORCE` only after a write-capable descriptor
   exists; does nothing for the open gate where the write is actually stopped, and the production kernel (5.15)
   predates it. Documented as non-required defence in depth.
2. A seccomp rule on `pwrite`/`openat` of `mem` — would harden a path already closed by DAC; adds filter
   complexity and would need argument inspection seccomp cannot do by path. Rejected.
3. A different PSS source avoiding `CAP_SYS_PTRACE` — out of scope (0113 settled the PSS source); unnecessary
   because the capability does not enable the write.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| docs | `docs/areas/agent.md` (*Rights*) | Add the measured capability matrix for opening another user's `/proc/<pid>/mem` and the rule that the bounding set must exclude `CAP_DAC_OVERRIDE`/write-side DAC capabilities; state the read/write boundary. |
| docs | `docs/ARCHITECTURE.md` | Correct the agent-privileges guarantee sentence to name the bounding-set exclusion as the barrier for the `mem` write vector. |
| docs | `docs/decisions/0121-…md` + `docs/decisions/README.md` index | New record (Proposed), amends 0030; index row. |
| squad/project doc | `.squad/project.md` area 6 | Correct the "So a compromised agent cannot … write as … another user" reasoning to include the bounding-set exclusion of `CAP_DAC_OVERRIDE`, and state it as a required control. |
| deploy doc | `deploy/agent/README.md` | Correct the confinement paragraph to name the exclusion of `CAP_DAC_OVERRIDE`/write-side DAC capabilities and note the `mem` read/write asymmetry. |

## Signatures (for the Dev's skeleton)

None — no code.

## Test files

None — no production or test code. No existing test call sites affected. No file a unit test cannot cover
(there is no code file at all).

## Areas

- **Agent** (`docs/areas/agent.md`, *Rights*) — the measured capability matrix for `/proc/<pid>/mem`, the
  required exclusion of `CAP_DAC_OVERRIDE` and write-side DAC capabilities from the bounding set, and the
  read/write boundary. Owner: Lead.

## Documentation updates

Every edit has exactly one owner.

- `docs/areas/agent.md` (*Rights*) — **Lead**.
- `docs/ARCHITECTURE.md` (agent-privileges guarantee sentence) — **Lead**.
- `docs/decisions/0121-…md` and the `docs/decisions/README.md` index row — **Lead** (already written in this
  step; status `Proposed`).
- `.squad/project.md` area 6 — **Dev** (outside the Lead's writable scope; it is a product-doc correction that
  the measurement makes necessary, allowed by `.squad/routing.md`, *Scope of a product PR*).
- `deploy/agent/README.md` (Capabilities / confinement) — **Dev**.

## Architecture check

Touches the agent-privileges guarantee in `docs/ARCHITECTURE.md` and `.squad/project.md` area 6: a compromised
agent cannot use its capabilities to write as or run code as another user. The guarantee is **preserved** —
the measurement confirms it holds. Only its stated reasoning is corrected (the `/proc/<pid>/mem` vector is
blocked by the bounding-set exclusion of `CAP_DAC_OVERRIDE`, not by the system-call filter). No guarantee in
`.squad/project.md` is weakened; this is not a Product-Manager decision.

## Security considerations

- [x] No parsed input and no limits — "no limits".
- [x] No exception reaches a user-visible message — "none".
- Residual (restated from 0030, confirmed): with `CAP_DAC_READ_SEARCH` the agent can open another user's `mem`
  read-only, i.e. its read access equals root's; this is the accepted residual, not a regression. The new
  control is only that write-side DAC capabilities stay out of the bounding set.

## Decision records

- `docs/decisions/0121-cap-sys-ptrace-cannot-write-another-users-proc-pid-mem-without-cap-dac-override.md`
  (Proposed) — amends released record 0030; 0030 is not edited.

## Out of scope / follow-ups

- The agent systemd unit itself (#42): this issue only clears the dependency; the unit is added there and must
  keep `CAP_DAC_OVERRIDE` and write-side DAC capabilities out of the bounding set (0121).
