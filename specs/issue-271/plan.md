# Plan: Settle whether CAP_SYS_PTRACE can write another process's memory before the agent unit grants it

Source: Issue #271
Status: Draft
Tier: security — changes a sandbox control: `process_madvise` joins the system calls the agent unit's filter must
deny, and the exclusion of `CAP_DAC_OVERRIDE` and `CAP_SYS_NICE` from the bounding set becomes a stated
load-bearing control (raised from `standard` in the revision after Security's plan review; documentation and one
decision record only, no production or test code).
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
obtained.

**Second vector (found in Security's plan review):** `pidfd_open(target)` followed by `process_madvise(pidfd, …)`;
the filter denies neither call (`pidfd_open` is deliberately allowed, `docs/areas/agent.md` *Rights*, line 1214).
Measured here (fact 10): with `CAP_SYS_PTRACE` alone, the agent's set, every advice on another user's process
fails — non-destructive advice `EPERM` (the call needs `CAP_SYS_NICE` since kernel 5.12), destructive advice
`EINVAL`; with `CAP_SYS_NICE` added only the non-destructive `MADV_COLD`, `MADV_PAGEOUT`, `MADV_WILLNEED` are
accepted (fact 11).

So three distinct barriers stop the write vectors: (a) DAC plus the bounding-set exclusion of `CAP_DAC_OVERRIDE`
for the `mem` open; (b) the filter for `ptrace` POKE, `process_vm_writev`, `pidfd_getfd` and `open_by_handle_at`;
(c) the kernel for `process_madvise`: its `CAP_SYS_NICE` requirement (the bounding set excludes it) and its refusal
of destructive advice on another process (version-dependent mm policy). The plan adds `process_madvise` to the
denied calls so that (c) becomes defence in depth behind the filter. The 0030 guarantee holds; its stated
*reason* was incomplete and is corrected.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | `/proc/<pid>/mem` of a root process is mode `0600`, owner root. | `stat -c %A %U /proc/<pid>/mem` on a root-owned `sleep`: `-rw------- root`. | confirmed |
| 2 | As uid 65534 with `CAP_SYS_PTRACE` effective, opening a root process's `/proc/<pid>/mem` with `O_RDONLY`, `O_RDWR` or `O_WRONLY` fails `EACCES`. | Measured: capsh `--user=nobody --inh=cap_sys_ptrace --addamb=cap_sys_ptrace` running a C probe that opens-and-closes only (`CapEff=0x80000` confirmed via `/proc/self/status`); all three modes `EACCES` (errno 13). | confirmed |
| 3 | As uid 65534 with `CAP_SYS_PTRACE`+`CAP_DAC_READ_SEARCH`, `O_RDONLY` succeeds; `O_RDWR`/`O_WRONLY` fail `EACCES`. | Same probe with both caps ambient: `O_RDONLY` ok, `O_RDWR`/`O_WRONLY` `EACCES`. | confirmed |
| 4 | As uid 65534 with `CAP_SYS_PTRACE`+`CAP_DAC_OVERRIDE`, all three modes succeed. | Same probe with `CAP_DAC_OVERRIDE` ambient: all ok. Control showing `CAP_DAC_OVERRIDE` is the capability that unlocks a write-capable open. | confirmed |
| 5 | The agent's capability bounding set is limited to exactly `CAP_SYS_PTRACE` and `CAP_DAC_READ_SEARCH`; `CAP_DAC_OVERRIDE` is not in it, and `NoNewPrivileges=yes` prevents gaining it. | `.squad/project.md` area 6; record 0030 (*Decision*, option 4); `docs/areas/agent.md` *Rights*; `deploy/agent/README.md`. | confirmed |
| 6 | The system-call filter denies `ptrace`, `process_vm_readv`, `process_vm_writev`, `pidfd_getfd`, `open_by_handle_at`, and does not deny `openat`, `pwrite`, `pidfd_open` or `process_madvise`. | `.squad/project.md` area 6; `docs/areas/agent.md` *Rights* (line 1214: "and not `pidfd_open` or `pidfd_send_signal`"); `deploy/agent/README.md` lines 19–20; issue #271. | confirmed |
| 7 | A `pwrite` into another user's memory through `/proc/<pid>/mem` requires a write-capable descriptor (`O_RDWR`/`O_WRONLY`), which (facts 2–4) cannot be obtained with the agent's capability set; `ptrace` POKE, `process_vm_writev`, `pidfd_getfd` and `open_by_handle_at` are denied by the filter (fact 6). These are not all paths: `pidfd_open` + `process_madvise` is a further one the filter leaves open (facts 10–11). | Reasoning from facts 2–6; the earlier "the alternate paths are the five syscalls the filter denies" was refuted by Security's plan review, so the claim is narrowed to these four. | confirmed |
| 8 | The DAC result (facts 2–4) generalizes to the production kernel (Ubuntu 22.04, kernel 5.15) although taken on kernel 6.18.44 in the same user namespace without Yama/LSM. The argument covers DAC file-permission semantics only; it does not cover `process_madvise` (facts 10–11), whose advice set is version-dependent. | DAC file-permission checks and the meaning of `CAP_DAC_OVERRIDE`/`CAP_DAC_READ_SEARCH` are stable, long-standing kernel behavior independent of version and namespace; the barrier is the `0600` ownership check, not a version-specific path. | confirmed |
| 9 | `proc_mem.force_override` (kernel 6.12+) governs `FOLL_FORCE` only after a write-capable descriptor exists and is irrelevant to the open gate; the sandbox kernel is `CONFIG_PROC_MEM_ALWAYS_FORCE=y`, the production kernel (5.15) predates the configurable policy. | `zcat /proc/config.gz`: `CONFIG_PROC_MEM_ALWAYS_FORCE=y`; `/proc/cmdline` has no `proc_mem.*`. | confirmed |
| 10 | As uid 65534 against a root process (kernel 6.18.44), `process_madvise` on a `pidfd_open` descriptor: no capability → every advice `EACCES`; `CAP_SYS_PTRACE` alone → `MADV_COLD`, `MADV_PAGEOUT`, `MADV_WILLNEED` `EPERM`, `MADV_DONTNEED`, `MADV_FREE` `EINVAL`; `CAP_SYS_NICE` alone → all `EACCES`; `CAP_SYS_PTRACE`+`CAP_SYS_NICE` → the three non-destructive values accepted, `MADV_DONTNEED`/`MADV_FREE` `EINVAL`. `pidfd_open` itself succeeds without a capability. | Measured by the Lead in this revision: probe `lead/xpm.c` in the session scratchpad, run under `capsh --user=nobody --inh=<caps> --addamb=<caps>` (CapEff checked) against `sleep` owned by root, on its first read-only mapping. Security's probe `pm2.c` targeted a child of its own user, so it did not exercise the cross-user permission check; its claim that `CAP_SYS_PTRACE` suffices is refuted for the agent's set. | confirmed |
| 11 | The accepted advice changes no content: under `MADV_COLD`, `MADV_PAGEOUT`, `MADV_WILLNEED` (and `MADV_COLLAPSE`, `EINVAL` in the probe) the target's page content stayed unchanged. The `CAP_SYS_NICE` requirement exists since kernel 5.12, so also on the production 5.15; the set of accepted advice has grown between versions (`MADV_COLLAPSE` added later), so it is version-dependent. | Security's probe `pm2.c` on 6.18.44 (content check); 5.12 from the kernel history of `process_madvise` (the change that replaced the ptrace-attach requirement with ptrace-read plus `CAP_SYS_NICE`), consistent with fact 10's `EPERM`. Content and 6.18 measured; 5.15 from the kernel history. | confirmed |
| 12 | The agent never calls `process_madvise`: the Go runtime uses `madvise` on its own memory only, and no module in `go.mod` wraps or calls it. | `grep -rn process_madvise` over `$(go env GOROOT)/src/{runtime,os,syscall}` and the module cache: only syscall-number tables (`zsysnum_*`) name it. | confirmed |
| 13 | systemd places `process_madvise` in the group `@ipc` (part of `@system-service`), so an allow-list based on `@system-service` admits it and the unit must deny it by name; `pidfd_open` is in `@process`. | `systemd-analyze syscall-filter` on systemd 255 (sandbox). | confirmed |
| 14 | The production server's systemd (Ubuntu 22.04, systemd 249) knows the name `process_madvise`, so a `SystemCallFilter=~process_madvise` entry takes effect rather than being skipped. | to verify: `systemd-analyze syscall-filter @ipc` on the server lists `process_madvise`; owner: the Dev of #42 when the unit is written (this change ships no unit). If it were skipped, barrier (c) (facts 10–11) still holds on 5.15, so the design degrades to the kernel's refusal, not to an open write. | unverified |
| 15 | Issue #271 names `process_madvise`. | Issue body: it names `/proc/<pid>/mem`, `open` and `pwrite` only; the vector came from Security's plan review and is real regardless. | refuted |

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
  area 6 and the Agent area, and record 0121 states that adding such a capability or `CAP_SYS_NICE`, or removing
  a call from the denied set, is a `security` change that must supersede it. `proc_mem.force_override` is
  documented as non-required defence in depth.
- [ ] AC4: The `pidfd_open` + `process_madvise` vector is recorded with its measured results (facts 10–11) in the
  Agent area's vector table and record 0121, and `process_madvise` is added to the system calls the unit's filter
  must deny — by name, since `@system-service` includes it (fact 13) — in area 6, the Agent area (*Rights*),
  `deploy/agent/README.md` and `docs/ARCHITECTURE.md`; `pidfd_open` stays allowed. Each of these documents
  names the three barriers — (a) DAC + bounding set, (b) filter, (c) the kernel (`CAP_SYS_NICE` requirement and
  refusal of destructive advice, version-dependent, now defence in depth) — instead of attributing every vector
  to one.

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
- **AC4** — Reviewer and Security confirm that area 6, the Agent area (*Rights*), `deploy/agent/README.md` and
  `docs/ARCHITECTURE.md` list `process_madvise` among the denied calls and keep `pidfd_open` allowed, and that the
  Agent area's vector table gives each vector its barrier (a), (b) or (c) with the measured result of facts 2–4
  and 10–11; the measurement is reproducible with the same `capsh` invocation and a probe that calls
  `pidfd_open` and `process_madvise` with each advice on a mapping of a root-owned process. Fact 14 is carried
  into the Agent area as a check for #42.

## Approach

No user-visible output. The change (a) adds to the Agent area's *Rights* section the measured capability matrix
for the `mem` open, the `process_madvise` matrix of fact 10, and a table of every write vector against another
process's memory with its barrier — `/proc/<pid>/mem` write open → (a); `ptrace` POKE, `process_vm_writev`,
`pidfd_getfd`, `open_by_handle_at` → (b); `pidfd_open` + `process_madvise` → (b) after this change, (c) behind it;
(b) adds `process_madvise` to the denied calls wherever they are listed (Agent area line 1214, area 6,
`deploy/agent/README.md`, `docs/ARCHITECTURE.md`) with the note that it must be named because `@system-service`
includes it, and states the #42 check of fact 14; (c) corrects the "why the write cannot happen" reasoning in
those documents to name the three barriers and the required exclusion of `CAP_DAC_OVERRIDE`, write-side DAC
capabilities and `CAP_SYS_NICE`; and (d) records the decision in record 0121 (amends released record 0030).
There is no unit or filter file yet (#42), so no Dev change to code or a unit file follows.

## Options considered

For `process_madvise` two options were weighed:

- **Deny `process_madvise` in the filter** (chosen) — the agent never calls it (fact 12), so the denial costs one
  name; it puts the vector behind a control of this project instead of a kernel rule this project neither
  controls nor tests.
- **Leave it to the kernel** (`CAP_SYS_NICE` requirement and refusal of destructive advice, facts 10–11) — holds
  today on 6.18 and 5.15, but a later change granting `CAP_SYS_NICE` (for example to renice processes) or a kernel
  widening the advice set would reopen the vector with nothing noticing. Rejected.
- Denying `pidfd_open` too was not taken: the Go runtime uses it for child processes, the Agent area keeps it
  allowed deliberately, and alone it gives no access to memory.

For the `/proc/<pid>/mem` vector the measurement is conclusive, so the only reasonable action is to correct the
reasoning and record the real barrier. The mitigation alternatives the issue proposed were weighed and
rejected as unnecessary, because the write is already impossible with the agent's capability set:

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
| docs | `docs/areas/agent.md` (*Rights*) | Add the measured capability matrices for opening another user's `/proc/<pid>/mem` and for `process_madvise`, the vector-by-barrier table, the rule that the bounding set must exclude `CAP_DAC_OVERRIDE`/write-side DAC capabilities and `CAP_SYS_NICE`, `process_madvise` named in the denied calls of the confinement bullet (line 1214), the #42 check of fact 14; state the read/write boundary. |
| docs | `docs/ARCHITECTURE.md` | Correct the agent-privileges guarantee sentence: the unit denies the process-attach calls and `process_madvise` and keeps write-side DAC capabilities out of the bounding set; link 0121. |
| docs | `docs/decisions/0121-…md` + `docs/decisions/README.md` index | New record (Proposed), amends 0030; index row. |
| squad/project doc | `.squad/project.md` area 6 | Add `process_madvise` to the `SystemCallFilter=` denied list; correct the "So a compromised agent cannot … write as … another user" reasoning to name the three barriers, with the bounding-set exclusion of `CAP_DAC_OVERRIDE` (and `CAP_SYS_NICE`) as a required control; add 0121 to the record list. |
| deploy doc | `deploy/agent/README.md` | Add `process_madvise` to the denied calls of the confinement paragraph; name the exclusion of `CAP_DAC_OVERRIDE`/write-side DAC capabilities and note the `mem` read/write asymmetry. |

## Signatures (for the Dev's skeleton)

None — no code.

## Test files

None — no production or test code. No existing test call sites affected. No file a unit test cannot cover
(there is no code file at all).

## Areas

- **Agent** (`docs/areas/agent.md`, *Rights*) — the measured capability matrices for `/proc/<pid>/mem` and
  `process_madvise`, the vector-by-barrier table, `process_madvise` among the denied calls, the required exclusion of `CAP_DAC_OVERRIDE` and write-side DAC capabilities from the bounding set, and the
  read/write boundary. Owner: Lead.

## Documentation updates

Every edit has exactly one owner. Sweep for lists of the denied calls (`grep -rn pidfd_getfd` over `docs/`,
`deploy/`, `.squad/project.md`): hits are `docs/areas/agent.md` line 1214, `deploy/agent/README.md` line 20 and
`.squad/project.md` lines 76–77 — each gains `process_madvise`; `docs/ARCHITECTURE.md` (line 411) names the calls
only as "the process-attach system calls" and gets `process_madvise` added there. Record 0121 already lists it.

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
blocked by the bounding-set exclusion of `CAP_DAC_OVERRIDE`, not by the system-call filter; the
`process_madvise` vector is blocked by the kernel today and by the filter after this change). Adding a denied
call strengthens the confinement; no guarantee in `.squad/project.md` is weakened; this is not a Product-Manager
decision.

## Security considerations

- [x] No parsed input and no limits — "no limits".
- [x] No exception reaches a user-visible message — "none".
- Residual (restated from 0030, confirmed): with `CAP_DAC_READ_SEARCH` the agent can open another user's `mem`
  read-only, i.e. its read access equals root's; this is the accepted residual, not a regression. The new
  controls are that write-side DAC capabilities and `CAP_SYS_NICE` stay out of the bounding set and that the
  filter denies `process_madvise`.
- Runtime detail no one can verify in this change (fact 14): whether the server's systemd 249 knows the name
  `process_madvise`. Fail-closed reading: if it did not, the entry would be skipped and barrier (c) would still
  stop the write; #42 checks it on the server.

## Decision records

- `docs/decisions/0121-cap-sys-ptrace-cannot-write-another-users-proc-pid-mem-without-cap-dac-override.md`
  (Proposed) — amends released record 0030; 0030 is not edited.

## Security review

- **BLOCKING — `process_madvise`/`pidfd_open` vector omitted, exhaustiveness claim false.** Accepted. Facts 6–7
  corrected (the filter does not deny `pidfd_open` or `process_madvise`; the five denied calls are not all the
  paths), facts 10–11 add the vector, AC4 adds it to the Agent area's vector table, area 6 and record 0121
  (*Context*) with three distinct barriers. One correction to the finding, measured in this revision (fact 10):
  `CAP_SYS_PTRACE` alone does **not** reach the accepted advice on another user's process — it fails `EPERM`
  because the call also needs `CAP_SYS_NICE` (since 5.12); the probe `pm2.c` targeted a same-user child and did
  not exercise that check. Barrier (c) is therefore the kernel's `CAP_SYS_NICE` requirement (a capability rule,
  the bounding set excludes it) plus its refusal of destructive advice (version-dependent mm policy). Fact 15
  notes that the issue does not itself name `process_madvise`; the vector is handled regardless.
- **Fact 8 too broad.** Accepted: fact 8 now covers DAC file-permission semantics only and flags the
  `process_madvise` advice set as version-dependent.
- **Forward-looking control / deny `process_madvise`?** Decided: deny it (*Options considered*). It costs nothing
  (fact 12), must be named because `@system-service` includes it (fact 13), and turns (c) into defence in depth.
  Record 0121 states that removing any of the six denied calls, or adding a write-side DAC capability or
  `CAP_SYS_NICE`, is a `security` change that must supersede it. There is no unit yet, so the change is the
  required deny list in the four documents (Lead and Dev as listed), and the server-side check of fact 14
  belongs to #42. The tier is raised to `security` because the deny list is a sandbox control.
- **Non-blocking:** same-uid `/proc/<pid>/mem` writes are inside the trust boundary and out of scope; the other
  `/proc/<pid>/` files Security checked are no bypass. No change.

## Out of scope / follow-ups

- The agent systemd unit itself (#42): this issue only clears the dependency; the unit is added there and must
  keep `CAP_DAC_OVERRIDE`, write-side DAC capabilities and `CAP_SYS_NICE` out of the bounding set, deny
  `process_madvise` by name with the other five calls, and check fact 14 on the server (0121).

## Challenge notes

Minor objection of the Devil's Advocate (verbatim):

1. Record 0121 calls itself a supersession, while the plan and the index call it an amendment. The record's header says `**Supersedes:** 0030 (amends it: … 0030 is not edited)`. The plan (*Decision records*: "amends released record 0030; 0030 is not edited") and the index row in `docs/decisions/README.md` (line 150, "(amends 0030)") both say amend. Meanwhile 0030 stays `Accepted` in the index (line 80). A `Supersedes:` field implies 0030 becomes `Superseded`, which the plan does not intend, since 0030's guarantee is upheld and only its reasoning is completed. Instruction for the Lead: replace the `Supersedes:` field in record 0121 with the field the decision-record template uses for an amendment (or a plain `Amends: 0030` line), so that 0030's status stays `Accepted` and the header matches the plan and the index. If the template has no such field, say so in the record's *Context* and leave 0030's status unchanged.

## Security notes

Non-blocking findings of Security's plan review (verbatim):

- N1, fact 10 and the plan's Security review section: the stated reason `pm2.c` missed the EPERM is wrong. The CAP_SYS_NICE check is not a cross-user check; it applies to any process other than the caller. A same-user control (uid 65534 against its own `sleep`, no capabilities) also gives EPERM on COLD, PAGEOUT and WILLNEED, and EINVAL on DONTNEED and FREE. `pm2.c` simply ran with CAP_SYS_NICE (as root). Suggested wording: "pm2.c ran with CAP_SYS_NICE, so it did not exercise the CAP_SYS_NICE requirement". Record 0121 does not repeat the wrong reason, so it needs no change for this. Also: the agent's real set, `CAP_SYS_PTRACE` + `CAP_DAC_READ_SEARCH` (CapEff 0x80004), behaves as `CAP_SYS_PTRACE` alone (EPERM / EINVAL); worth adding to fact 10.
- N2, record 0121 Context ("no single mechanism stops every vector; three distinct barriers do") and plan line 36: this reads as a completeness claim again. Suggest "the vectors examined" and, in the Agent area's vector table, the other owner-only writable `/proc/<pid>/` files (for example `clear_refs`) under barrier (a). DAC covers them the same way as `mem`. Wording fix only.
