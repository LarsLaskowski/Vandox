# Plan: Settle whether CAP_SYS_PTRACE can write another process's memory before the agent unit grants it

Source: Issue #271
Status: Draft — mitigation pending the Product Manager's answer to the escalation in *Security review* (E1); the
plan is written for the recommended option (drop `CAP_DAC_READ_SEARCH`).
Tier: security — changes sandbox controls: the agent's capability set shrinks to `CAP_SYS_PTRACE` alone,
`process_madvise` joins the system calls the agent unit's filter must deny, and the exclusion of
`CAP_DAC_READ_SEARCH`, `CAP_DAC_OVERRIDE`, the other write-side DAC capabilities and `CAP_SYS_NICE` from the
bounding set becomes a stated load-bearing control (documentation and one decision record only, no production
or test code).
Security areas touched: area 6 (Agent privileges) of `.squad/project.md`.

## Problem / root cause

Not a code bug: a security claim to settle. The process collector (#32, record 0113) needs `CAP_SYS_PTRACE`
to read `smaps_rollup` of other users' processes, and the agent unit (#42) must not grant the capability
until it is proven that the capability cannot be turned into a write into another user's process memory.

**`/proc/<pid>/mem` (the issue's vector) — closed.** The review's premise skips the VFS DAC check that runs
*before* `mem_open`: `mem` is mode `0600` owned by the target's user, so a write-capable open needs
`CAP_DAC_OVERRIDE`, which neither `CAP_SYS_PTRACE` nor `CAP_DAC_READ_SEARCH` grants (facts 1–5).

**`pidfd_open` + `process_madvise` — closed by the kernel, denied by the filter after this change** (facts
10–14).

**`/proc/<pid>/fd/<n>` of another process — open with the planned capability set (root finding of this
revision).** The Product Manager measured on kernels 4.4, 5.15, 6.8 and 7.0 (fact 16) that a user holding
`CAP_SYS_PTRACE` and `CAP_DAC_READ_SEARCH` opens a root process's memfd through `/proc/<pid>/fd/<n>` read-write
and a `pwrite` changes the target's `MAP_SHARED` mapping. The mechanism (facts 17–18): `/proc/<pid>/fd` is mode
`0500` owned by the target's user, so looking up an entry needs the search bypass of `CAP_DAC_READ_SEARCH`;
following the link needs the ptrace check that `CAP_SYS_PTRACE` passes; the file opened is then checked only
against its own mode, which for a memfd is `0777`. The documented guarantee ("cannot use its capabilities to
write as or run code as another user", area 6, `deploy/agent/README.md`, `docs/ARCHITECTURE.md`, record 0030) is
therefore **false as written** for every file another process holds open whose mode grants the agent write —
the unsealed shared memory of every process, root's included, and possibly a runtime's generated code mapped
from a memfd (fact 21). `CAP_DAC_READ_SEARCH` alone adds a second write path: every file whose own mode grants
the agent write below a directory closed to it (fact 19). The filter cannot stop either (`openat`, `pwrite`).

**No collector uses `CAP_DAC_READ_SEARCH`** (fact 20): 0030 granted it for listing `/proc/<pid>/fd`, which the
connection collector gave up in 0115. With `CAP_SYS_PTRACE` alone the memfd open fails `EACCES` (facts 16, 18),
so dropping `CAP_DAC_READ_SEARCH` closes the vector at no functional cost, and also ends 0030's read residual
"read access equals root's" (fact 22). What remains with `CAP_SYS_PTRACE` alone (fact 23): a file whose own mode
grants the agent's user, groups or others write or read stays reachable through another process's
`/proc/<pid>/cwd` or `root` link even below a directory closed to the agent.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | `/proc/<pid>/mem` of a root process is mode `0600`, owner root. | `stat -c %A %U /proc/<pid>/mem` on a root-owned `sleep`: `-rw------- root`. | confirmed |
| 2 | As uid 65534 with `CAP_SYS_PTRACE` effective, opening a root process's `/proc/<pid>/mem` with `O_RDONLY`, `O_RDWR` or `O_WRONLY` fails `EACCES`. | Measured: `capsh --user=nobody` with ambient `cap_sys_ptrace` (`CapEff=0x80000`), open-and-close probe. | confirmed |
| 3 | As uid 65534 with `CAP_SYS_PTRACE`+`CAP_DAC_READ_SEARCH`, `O_RDONLY` succeeds; `O_RDWR`/`O_WRONLY` fail `EACCES`. | Same probe with both caps ambient: `O_RDONLY` ok, `O_RDWR`/`O_WRONLY` `EACCES`. | confirmed |
| 4 | As uid 65534 with `CAP_SYS_PTRACE`+`CAP_DAC_OVERRIDE`, all three modes succeed. | Same probe with `CAP_DAC_OVERRIDE` ambient (control). | confirmed |
| 5 | Today's documented capability bounding set is exactly `CAP_SYS_PTRACE` and `CAP_DAC_READ_SEARCH`; `CAP_DAC_OVERRIDE` is not in it, and `NoNewPrivileges=yes` prevents gaining it. | `.squad/project.md` area 6; record 0030 (*Decision*, option 4); `docs/areas/agent.md` *Rights*; `deploy/agent/README.md`. This plan narrows the set to `CAP_SYS_PTRACE` (fact 20). | confirmed |
| 6 | The system-call filter denies `ptrace`, `process_vm_readv`, `process_vm_writev`, `pidfd_getfd`, `open_by_handle_at`, and does not deny `openat`, `pwrite`, `pidfd_open` or `process_madvise`. | `.squad/project.md` area 6 before this change; `docs/areas/agent.md` *Rights*; `deploy/agent/README.md`; issue #271. | confirmed |
| 7 | A `pwrite` into another user's memory through `/proc/<pid>/mem` requires a write-capable descriptor, which (facts 2–4) cannot be obtained with either capability set; `ptrace` POKE, `process_vm_writev`, `pidfd_getfd` and `open_by_handle_at` are denied by the filter (fact 6). These are not all paths: `pidfd_open` + `process_madvise` (facts 10–11) and `/proc/<pid>/fd/<n>` (fact 16) are further ones the filter leaves open. | Reasoning from facts 2–6 and 16. | confirmed |
| 8 | The DAC result (facts 2–4) generalizes to the production kernel (Ubuntu 22.04, kernel 5.15). It does not cover `process_madvise` (facts 10–11), whose advice set is version-dependent. | Stable DAC semantics; fact 16 shows the DAC-based memfd result identical on 4.4 to 7.0. | confirmed |
| 9 | `proc_mem.force_override` (kernel 6.12+) governs `FOLL_FORCE` only after a write-capable descriptor exists and is irrelevant to the open gate; the production kernel (5.15) predates the configurable policy. | `zcat /proc/config.gz`; `/proc/cmdline`. | confirmed |
| 10 | As uid 65534 against a root process (kernel 6.18.44), `process_madvise` on a `pidfd_open` descriptor: no capability → every advice `EACCES`; `CAP_SYS_PTRACE` alone and `CAP_SYS_PTRACE`+`CAP_DAC_READ_SEARCH` → `MADV_COLD`, `MADV_PAGEOUT`, `MADV_WILLNEED` `EPERM`, `MADV_DONTNEED`, `MADV_FREE` `EINVAL`; `CAP_SYS_NICE` alone → all `EACCES`; `CAP_SYS_PTRACE`+`CAP_SYS_NICE` → the three non-destructive values accepted, `MADV_DONTNEED`/`MADV_FREE` `EINVAL`. | Lead probe `xpm.c` under `capsh` (CapEff checked) against a root `sleep`; the agent's set by Security (N1). | confirmed |
| 11 | The accepted advice changes no content; the `CAP_SYS_NICE` requirement exists since kernel 5.12; the accepted advice set is version-dependent. | Security's probe `pm2.c` on 6.18.44; kernel history of `process_madvise`. | confirmed |
| 12 | The agent never calls `process_madvise`. | `grep` over the Go runtime and module cache: only syscall-number tables. | confirmed |
| 13 | systemd places `process_madvise` in `@ipc` (part of `@system-service`), so the unit must deny it by name; `pidfd_open` is in `@process`. | `systemd-analyze syscall-filter` on systemd 255 (sandbox). | confirmed |
| 14 | The production server's systemd (249) knows the name `process_madvise`, so a `SystemCallFilter=~process_madvise` entry takes effect. | to verify: `systemd-analyze syscall-filter @ipc` on the server lists `process_madvise`; owner: the Dev of #42 when the unit is written. If skipped, the kernel's refusal (facts 10–11) remains. | unverified |
| 15 | Issue #271 names `process_madvise`. | The issue names `/proc/<pid>/mem`, `open` and `pwrite` only. | refuted |
| 16 | As `nobody` against a root process holding a `MAP_SHARED` memfd: no capability and `CAP_SYS_PTRACE` alone → opening `/proc/<pid>/fd/<n>` `O_RDWR` fails `EACCES`; `CAP_SYS_PTRACE`+`CAP_DAC_READ_SEARCH` (CapEff `0x80004`) → open ok, `pwrite` of 4 bytes ok, the target reads the new bytes — on kernels 4.4.302, 5.15.0-198, 6.8.0-147 and 7.0.0-38. | Product Manager's measurement, `specs/issue-271/memfd-measurement.md`. Reproduced by the Lead on 6.18.44 for the open only (no write): `/proc/<pid>/fd/<n>` and `/proc/<pid>/task/<tid>/fd/<n>` `EACCES` with `CAP_SYS_PTRACE`, `O_RDWR` ok with both. | confirmed |
| 17 | A memfd's inode is mode `0777`; `/proc/<pid>/fd` is mode `0500` owned by the target's user. | `os.stat` on `/proc/self/fd/<memfd>` → `0o100777`; `/proc/self/fd` → `0o40500` (kernel 6.18.44). | confirmed |
| 18 | The memfd open needs both capabilities: `CAP_DAC_READ_SEARCH` for the lookup in the `0500` directory, `CAP_SYS_PTRACE` for following the link; the target file is then checked against its own mode only. | The `fd` table in `docs/areas/agent.md` *Rights* (list `fd/` ok with `CAP_DAC_READ_SEARCH`, readlink only with both) and fact 16 (neither `CAP_SYS_PTRACE` alone nor, by the table, `CAP_DAC_READ_SEARCH` alone reaches the link). | confirmed |
| 19 | With `CAP_DAC_READ_SEARCH` alone, uid 65534 opens for writing a `0666` file inside a `0700` root directory; a `0644` file there and every case without the capability, or with `CAP_SYS_PTRACE` alone, fail `EACCES`. | Measured by the Lead (open and close only, no write), kernel 6.18.44, `capsh --user=nobody`. | confirmed |
| 20 | No collector uses `CAP_DAC_READ_SEARCH`: the process collector needs `CAP_SYS_PTRACE` alone (0113), the connection collector reads `fdinfo` with `CAP_SYS_PTRACE` alone and not the `fd` links (0115), journal and logs come through `systemd-journal` and `adm`; the agent's code opens no `/proc/<pid>/fd` link. | `deploy/agent/README.md` ("`CAP_DAC_READ_SEARCH` is not needed by any collector so far"); `.squad/project.md` area 6 ("which no collector uses"); `docs/areas/agent.md` *Rights*; `grep` over `internal/` and `cmd/`: only `fdinfo` paths, no `readlink`. Issue #42's "what `/proc/<pid>/fd` … require" predates 0115. | confirmed |
| 21 | A runtime may map its generated code from a memfd (double mapping for W^X JITs, for example .NET's), so the memfd write could reach code of a process, not only data. Whether such a process runs as another user on the production server is unknown. | to verify: only if the Product Manager chooses option 2 of E1 (an inference from the runtimes' design until then) — Security lists the root processes on the server holding an executable memfd mapping (`grep memfd /proc/*/maps` as root, permissions with `x`), in the revision after the answer. | unverified |
| 22 | With `CAP_SYS_PTRACE` alone, uid 65534 cannot open a root process's `environ` (`0400`) or `mem`, nor `/etc/shadow` directly or through `/proc/<pid>/root`; it can open the root process's `maps` and `smaps_rollup`. | Measured by the Lead, kernel 6.18.44 (open and close only). | confirmed |
| 23 | With `CAP_SYS_PTRACE` alone, uid 65534 opens for writing a `0666` file in a `0755` directory below a `0700` root directory through `/proc/<pid>/cwd/` of a root process whose working directory it is; without a capability `EACCES`. | Measured by the Lead (open and close only), kernel 6.18.44. The same holds by construction for `/proc/<pid>/root`. | confirmed |

## Acceptance criteria

(From the issue; each verified without a test — see *Verification without tests*.)

- [ ] AC1: The measurements are recorded — the capability matrix for opening another user's `/proc/<pid>/mem`, the
  `process_madvise` matrix, and the `/proc/<pid>/fd/<n>` memfd matrix of fact 16 (capability sets × kernels, with
  observed result) with the method — in `docs/areas/agent.md` (*Rights*), and summarized in record 0121.
- [ ] AC2: Area 6 of `.squad/project.md`, `deploy/agent/README.md`, `docs/areas/agent.md` (*Rights*) and
  `docs/ARCHITECTURE.md` state what holds: the agent holds `CAP_SYS_PTRACE` alone (bounding set limited to it);
  with it a write-capable open of another user's `mem` is refused (no `CAP_DAC_OVERRIDE`) and the
  `/proc/<pid>/fd/<n>` open of another user's process is refused (no `CAP_DAC_READ_SEARCH`), while with
  `CAP_DAC_READ_SEARCH` added the memfd write works (fact 16); the remaining residual of fact 23 and the narrowed
  read residual of fact 22 replace "read access equals root's"; none of the four documents says any more that
  the agent holds `CAP_DAC_READ_SEARCH` or that it is planned from #42.
- [ ] AC3: The mitigation exists: `CAP_DAC_READ_SEARCH` is removed from the agent's rights in all four documents
  and in the per-collector table of `deploy/agent/README.md`; the bounding-set exclusion of `CAP_DAC_READ_SEARCH`,
  `CAP_DAC_OVERRIDE`, the other write-side DAC capabilities and `CAP_SYS_NICE` is stated as a required,
  load-bearing control in area 6 and the Agent area; record 0121 states that adding one of them, or removing one
  of the six denied calls, is a `security` change that must supersede it. `proc_mem.force_override` stays
  documented as not required.
- [ ] AC4: The `pidfd_open` + `process_madvise` vector is recorded with its measured results (facts 10–11) in the
  Agent area's vector table and record 0121, and `process_madvise` is among the calls the unit's filter must deny
  — by name (fact 13) — in area 6, the Agent area (*Rights*), `deploy/agent/README.md` and `docs/ARCHITECTURE.md`;
  `pidfd_open` stays allowed. Each of these documents names the three barriers — (a) DAC + bounding set (now also
  for `/proc/<pid>/fd`), (b) filter, (c) the kernel: its `CAP_SYS_NICE` requirement **and its refusal of
  destructive advice on another process**, version-dependent, defence in depth — instead of attributing every
  vector to one. (Reviewer round 1, blocking: `deploy/agent/README.md` line 24–27 and `docs/ARCHITECTURE.md`
  lines 410–415 lack barrier (c) as the kernel's refusal.)

## Tests per criterion

Not applicable — the change touches no production or test code (*Changes without production or test code* in
`.squad/routing.md`). Every acceptance criterion is verified under *Verification without tests*.

## Verification without tests

Steps 4 (*Skeleton*), 5 (*Tests first*) and the *Coverage gate* are not applicable: the diff is documentation
and one decision record, no production or test code.

- **AC1** — Reviewer (step 8) and Security (diff review) confirm the three matrices and the method in
  `docs/areas/agent.md` (*Rights*) and record 0121 match facts 2–4, 10 and 16 and
  `specs/issue-271/memfd-measurement.md`.
- **AC2** — Reviewer checks the four documents against record 0121 and facts 16–23: `grep -rn
  CAP_DAC_READ_SEARCH .squad/project.md deploy/agent/README.md docs/areas/agent.md docs/ARCHITECTURE.md` shows
  only statements that the agent does not hold it and why (the `fd` table and the memfd matrix included); Security
  checks the stated residual is complete for the vectors examined.
- **AC3** — Reviewer and Security confirm the capability is removed from every rights list, the bounding-set
  exclusions are stated as required, and record 0121 names the conditions under which the guarantee breaks.
- **AC4** — Reviewer and Security confirm the four documents list `process_madvise` among the denied calls and
  keep `pidfd_open` allowed, and that each names barrier (c) with both parts (the `CAP_SYS_NICE` requirement and
  the kernel's refusal of destructive advice on another process). Fact 14 is carried into the Agent area as a
  check for #42.

## Approach

No user-visible output. The change:

1. **Agent area, *Rights*** (Lead): the rights bullet names `CAP_SYS_PTRACE` alone (bounding set limited to it)
   and says why `CAP_DAC_READ_SEARCH` is not granted (fact 16, with its matrix per kernel); the connection
   collector bullet keeps its `fd` table and states that the `fd` links stay unread because reading them needs
   `CAP_DAC_READ_SEARCH`, which also enables the memfd write; the vector table gains the row
   "`/proc/<pid>/fd/<n>` of another user's process, then `pwrite` → (a) DAC and the bounding set: the `0500`
   directory needs `CAP_DAC_READ_SEARCH`" and the row "a file whose own mode grants write, below a closed
   directory → (a) without `CAP_DAC_READ_SEARCH`, except through another process's `cwd`/`root` (residual)";
   the bounding-set rule lists `CAP_DAC_READ_SEARCH` beside `CAP_DAC_OVERRIDE`, the write-side DAC capabilities
   and `CAP_SYS_NICE`; the "Read and write differ for `mem`" sentence goes (with `CAP_SYS_PTRACE` alone the read
   open fails too, fact 2); the *Accepted residual* bullet is replaced by facts 22–23: readable are the
   ptrace-checked `/proc` files whose mode allows reading (`maps`, `smaps_rollup`, `fdinfo`, …), not `mem` or
   `environ`; readable and writable are the files whose own mode allows the agent's user, groups or others, also
   below a closed directory that is another process's working or root directory.
2. **`docs/ARCHITECTURE.md`** (Dev, lines 408–418): "The two capabilities it needs … (`CAP_SYS_PTRACE`,
   `CAP_DAC_READ_SEARCH`) give it root's read access" becomes the one capability `CAP_SYS_PTRACE`; the guarantee
   sentence names the three barriers including (c) the kernel's `CAP_SYS_NICE` requirement and its refusal of
   destructive advice on another process (Reviewer blocking); "can read everything on the server" becomes the
   narrowed residual; links 0030 and 0121 stay.
3. **`.squad/project.md` area 6** (Dev): the goal names `CAP_SYS_PTRACE` alone from #42; the bounding-set rule
   adds `CAP_DAC_READ_SEARCH`; barrier (a) adds the `/proc/<pid>/fd` lookup; the "Read and write differ for `mem`"
   sentence goes; the *Accepted residual* is replaced by the narrowed one; "which no collector uses" for
   `CAP_DAC_READ_SEARCH` becomes "which the agent does not hold (record 0121)".
4. **`deploy/agent/README.md`** (Dev): the intro's complete set becomes `CAP_SYS_PTRACE` and the two groups; the
   line "`CAP_DAC_READ_SEARCH` is not needed by any collector so far" becomes "is not granted: with
   `CAP_SYS_PTRACE` it opens other processes' open files, memfds included, for writing (record 0121)"; the barrier
   paragraph (line 24–30) names (c) as the kernel's `CAP_SYS_NICE` requirement **and** its refusal of
   destructive advice on another process (Reviewer blocking), adds the `fd` lookup to (a), and drops the `mem`
   read sentence; the connection collector's `fd` bullet keeps the table and adds the memfd reason.
5. **Record 0121** (Lead, done in this revision): rewritten for the memfd finding and option 7; the Reviewer's
   non-blocking findings are applied (all six denied calls in *Consequences*, *Decision* in two sentences).

## Options considered

The question for the Product Manager (E1, *Security review*) chooses among these; the plan is written for 1.

1. **Drop `CAP_DAC_READ_SEARCH`, keep `CAP_SYS_PTRACE` alone** (recommended) — closes the memfd write (fact 16:
   `EACCES` with `CAP_SYS_PTRACE` alone, on every kernel measured) and the write below closed directories (fact
   19); costs no collector (fact 20); narrows the read residual (fact 22). Remaining residual: fact 23.
2. **Keep both and accept the write residual** — the guarantee of 0030 and area 6 would have to be rewritten to
   "can write into every file another process holds open whose mode allows it, the shared memory of every
   process included, and every file whose own mode allows it anywhere", possibly code (fact 21); a product
   decision against a stated guarantee for a capability nothing uses. Not recommended.
3. **Keep both and add an AppArmor/SELinux profile** denying writes through `/proc/*/fd/*` — a new artifact per
   distribution; AppArmor mediates the resolved path of an opened file, so whether a rule on the `/proc` path
   catches a magic-link open is unknown (not measured); does not close fact 19. Not recommended.
4. **systemd `ProtectProc=`/`ProcSubset=`/`hidepid`** — `hidepid` hides no process from a holder of
   `CAP_SYS_PTRACE` (Agent area, measured), `ProcSubset=pid` hides `/proc/net` that the connection collector
   reads, and none changes the permission on `/proc/<pid>/fd`. Rejected.
5. **Drop `CAP_SYS_PTRACE` instead / read PSS from a source without it** — no PSS source avoids the ptrace check
   (0113); loses PSS and socket owners, keeps the read-everything residual and fact 19. Rejected.
6. **Seccomp rule on `openat`/`pwrite`, `proc_mem.force_override=never`** — seccomp cannot inspect a path;
   `force_override` acts only on `mem` after a write-capable descriptor exists. Rejected.

For `process_madvise`: **deny it in the filter** (chosen; the agent never calls it, fact 12) against **leave it
to the kernel** (rejected: a later grant of `CAP_SYS_NICE` or a kernel widening the advice set would reopen it
unnoticed); `pidfd_open` stays allowed (the Go runtime uses it; alone it gives no memory access).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| docs | `docs/areas/agent.md` (*Rights*) | Approach 1: `CAP_SYS_PTRACE` alone, memfd matrix, vector-table rows, bounding-set rule with `CAP_DAC_READ_SEARCH`, narrowed residual, `mem` read sentence removed. |
| docs | `docs/ARCHITECTURE.md` | Approach 2: one capability, three barriers incl. the kernel's refusal of destructive advice, narrowed residual. |
| docs | `docs/decisions/0121-…md` + `docs/decisions/README.md` index | Rewritten in this revision (Proposed, amends 0030); index row unchanged. |
| squad/project doc | `.squad/project.md` area 6 | Approach 3. |
| deploy doc | `deploy/agent/README.md` | Approach 4, incl. the Reviewer's blocking AC4 fix. |

## Signatures (for the Dev's skeleton)

None — no code.

## Test files

None — no production or test code. No existing test call sites affected. No file a unit test cannot cover
(there is no code file at all).

## Areas

- **Agent** (`docs/areas/agent.md`, *Rights*) — the agent's one capability, the three measured matrices, the
  vector-by-barrier table, the bounding-set rule, the narrowed residual. Owner: Lead.

## Documentation updates

Sweep done for every statement the change touches: `grep -rn "CAP_DAC_READ_SEARCH"` over `docs/`, `deploy/`,
`.squad/project.md` (hits: `docs/areas/agent.md` lines 395, 1171, 1173, 1175, 1180, 1187, 1237, 1244, 1250,
1260; `docs/ARCHITECTURE.md` line 410; `.squad/project.md` lines 73, 84, 90, 99; `deploy/agent/README.md` lines 4,
16, 25, 28, 36, 38, 51–52, 58; records 0030, 0113, 0115 — released or settled, not edited); `grep -rn "equals
root's\|read everything\|root's read"` (hits: agent.md 1288, project.md 94, ARCHITECTURE.md 410 and 414, record
0030 — not edited); `grep -rn pidfd_getfd` for the denied-call lists (agent.md 1214, README 20, project.md 76–77,
already carry `process_madvise`). The measurement mentions in agent.md 395, 1173–1175, 1187 and README 36–38, 51–58
describe what the capability does and stay true; each owner checks them while editing.

- `docs/areas/agent.md` (*Rights*, the *Processes*/*Connections* mentions at 395) — **Lead**.
- `docs/ARCHITECTURE.md` (agent-privileges sentence) — **Dev**.
- `docs/decisions/0121-…md` — **Lead** (done in this revision).
- `.squad/project.md` area 6 — **Dev** (allowed by `.squad/routing.md`, *Scope of a product PR*).
- `deploy/agent/README.md` — **Dev**.

## Architecture check

Touches the agent-privileges guarantee in `docs/ARCHITECTURE.md` and `.squad/project.md` area 6. As written
today the write guarantee is **false** for the planned capability set (fact 16). With option 1 it holds again,
stated precisely: a compromised agent cannot use its capability to write into another user's process memory or
to a file whose mode does not grant it write; and the read residual narrows from "equals root's" to fact 22–23.
Area 6's goal changes (the capability list): the guarantee gets stronger, not weaker, but it reverses a released
decision (0030 granted the capability), so the Product Manager decides (E1).

## Security considerations

- [x] No parsed input and no limits — "no limits".
- [x] No exception reaches a user-visible message — "none".
- Residual with option 1 (fact 23): files whose own mode grants write (or read) to the agent's user, its groups
  (`adm`, `systemd-journal`) or others stay reachable through another process's `/proc/<pid>/cwd` or `root` link
  even below a directory closed to the agent. It is DAC as the file's owner set it, reached past a closed parent.
- Runtime detail no one can verify in this change (fact 14); fail-closed: if skipped, the kernel's refusal
  remains; #42 checks it on the server.

## Decision records

- `docs/decisions/0121-cap-sys-ptrace-cannot-write-another-users-proc-pid-mem-without-cap-dac-override.md`
  (Proposed) — amends released record 0030 (capability set narrowed, controls added); 0030 is not edited.

## Challenge

- **Devil's Advocate (minor): record 0121 says *Supersedes* while plan and index say amends.** Rejected: the
  form is the one `docs/decisions/README.md` (*Rules*) and `_template.md` prescribe for an amendment — "names the
  older one under *Supersedes* and says there, in one sentence, what it amends. The older record is not edited";
  0030 stays `Accepted` in the index.

## Security review

- **BLOCKING (plan review) — `process_madvise`/`pidfd_open` vector omitted, exhaustiveness claim false.**
  Accepted: facts 6–7, 10–11, AC4 and the three barriers; `CAP_SYS_PTRACE` alone gets `EPERM` because the call
  also needs `CAP_SYS_NICE` (fact 10).
- **Fact 8 too broad.** Accepted.
- **Deny `process_madvise`?** Decided: deny it by name.
- **Security note N1** (reason `pm2.c` missed the `EPERM`; agent's set behaves as `CAP_SYS_PTRACE` alone).
  Accepted: fact 10 and the Agent area say so.
- **Security note N2** ("three barriers stop every vector" reads as a completeness claim). Accepted: "the
  vectors examined" in the documents and record, and `clear_refs` under barrier (a) in the vector table.
- **New finding (Product Manager's memfd measurement, fact 16; the Security diff review of it did not complete).**
  Accepted: the guarantee is false as written with `CAP_DAC_READ_SEARCH`. Mitigation chosen for the plan: option 1
  (drop the capability), pending **E1**.
- **E1 (escalation to the Product Manager).** Drop `CAP_DAC_READ_SEARCH` from the agent's rights, reversing that
  part of released record 0030 (option 1, recommended), or keep it and accept the write residual in writing
  (option 2), or keep it behind an LSM profile (option 3)? With option 1 the residual of fact 23 is accepted as
  part of the answer. If the answer is 2 or 3, the Lead revises AC2–AC3, *Approach* and record 0121.
- **Reviewer round 1.** Blocking AC4 (barrier (c) as the kernel's refusal missing in `deploy/agent/README.md` and
  `docs/ARCHITECTURE.md`): folded into AC4 and *Approach* 2 and 4, owner Dev. Non-blocking (0121 *Consequences*
  list all six denied calls incl. `process_vm_readv`; 0121 *Decision* in one to three sentences): done by the Lead
  in this revision.
- A Security diff review of the final diff is required again.

## Out of scope / follow-ups

- The agent systemd unit itself (#42): grants `CAP_SYS_PTRACE` alone with a bounding set limited to it, denies
  the six calls (`process_madvise` by name), and checks fact 14 on the server (0121). #42's requirement text
  "only what `/proc/<pid>/fd`, `smaps_rollup` and the journal require" is outdated by 0115 and 0121; proposed
  issue comment on #42 (orchestrator): "Per records 0115 and 0121 the agent does not read `/proc/<pid>/fd` and
  holds `CAP_SYS_PTRACE` alone; `CAP_DAC_READ_SEARCH` must not be granted (with it, other processes' open files,
  memfds included, become writable)."
