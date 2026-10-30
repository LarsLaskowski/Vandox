# Plan: Footprint and load test of the agent

Source: Issue #44
Status: Revised (1)
Tier: trivial — the change records the Product Manager's measurements in documentation (`docs/BENCHMARKS.md`, the Agent area, `deploy/agent/README.md`) and needs a decision record, which rules out `docs`; no code, test, configuration or behavior changes.
Security areas touched: none

## Problem / root cause

#44 asks for the agent's footprint over 24 hours against the targets of under 30 MB memory and under 1 % CPU. The
Product Manager measured the agent on the production server and delivered the raw values. Nothing in the repository
records them, and several documents still say "#44 measures it". No code defect is involved.

**Closing #44.** The issue's second criterion ("targets met or deviation justified and accepted") needs the Product
Manager's acceptance of three remaining deviations:
- 161 processes instead of several hundred;
- no sending: the backend answers `404`;
- the previous instance's stats are lost.

The PM has **not** given that acceptance. The PR body states the deviations and asks the PM to confirm them. It says
"Closes #44" only once the PM has confirmed; until then it says "Refs #44".

## Verified facts

M = the Product Manager's measurement on the production server. D = arithmetic on M. The 24-hour reading was taken
2026-10-30 19:48:34 CET; the earlier reading at 18:14 CET (22 h 42 min) is the source only where marked.

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | Host at the 24-hour reading: Ubuntu 22.04, kernel 5.15.0-191, 1 core, 1,963 MB RAM, swap 477 MB used, load 0.00/0.08/0.06, 161 processes, 74 lines of `ss -tan` | M | confirmed |
| 2 | One instance (MainPID 118256, `NRestarts=0`, manual restart 2026-10-29 19:32:27 CET) ran 87,367 s = 24 h 16 min 7 s; the previous instance's own stats are lost | M; 19:32:27 to 19:48:34 next day | confirmed |
| 3 | CPU: `CPUUsageNSec` 368,052,315,000 → 368.05 s / 87,367 s = **0.42 % of one core** | D | confirmed |
| 4 | Memory: `VmHWM` 19,896 kB = **20.4 MB** (19.4 MiB) peak RSS, unchanged from the earlier reading; `VmRSS` 13,364 kB (13.7 MB); `smaps_rollup` Rss 15,440 kB, Pss 15,436 kB (15.8 MB; Pss_Anon 10,220, Pss_File 5,216, Shmem 0); 12 threads | M; D (kB · 1,024 / 10^6) | confirmed |
| 5 | cgroup `MemoryCurrent` 49,582,080 B (47.3 MiB) includes the spool's page cache; systemd's `MemoryPeak` and `IO*Bytes` are unavailable on 5.15 (value 18446744073709551615) | M | confirmed |
| 6 | `/proc/<pid>/io`, from the 22 h 42 min reading only (81,721 s): rchar 2,725,637,213; wchar 186,489,754; syscr 19,125,487; syscw 131,966; read_bytes 11,759,616; write_bytes 268,169,216 (11.8 MB/h); cancelled_write_bytes 0 | M (earlier reading) | confirmed |
| 7 | Spool, from the 22 h 42 min reading: 319,887,505 B in 17 data files of about 16 MB plus a first one of 446,931 B, the oldest listed 2026-10-28 17:38, the newest 2026-10-30 13:03 | M (earlier reading) | confirmed |
| 8 | The spool's daily volume, derived as about 160 MB a day (PM: about 157; 17 files at about one every 2.5 h). At the 24-hour reading the listing still shows no file newer than 13:03 CET. This contradicts that rate: a 16 MiB file seals about every 2.5 h, and the newest unsealed file is appended to (agent.md, *Spool and sending*, *Files*), so its modification time should be recent. Either the listing was not re-read, or the spool stopped growing at 13:03 without a `spool write failed` warning | to verify: (evidence so far: M; agent.md *Files*) the PM runs `ls -l --time-style=full-iso /var/lib/vandox/spool` and `du -sb /var/lib/vandox/spool` and sends both (owner: orchestrator, before step 6) | unverified |
| 9 | The sender's `404` comes from `vandoxd` v0.1.0, which has no ingest API: commit 7228815 is not an ancestor of `v0.1.0`. The agent behaved as specified (shared backoff, records kept) | `git merge-base --is-ancestor 7228815 v0.1.0` → false; `deploy/agent/README.md` *Transmission to the backend*; agent.md *Not accepting, one shared backoff* | confirmed |
| 10 | `socket owners partly unknown processes=2 err="connstats: 528/fdinfo: not in the expected format"` at every start (also in the install run, README line 681). `ParseFdinfo` (`internal/connstats/owners.go:36-64`) requires all-digit values in the first four lines | M; code. The cause is left to follow-up A (hypothesis: a negative `pos:`, printed with `%lli` upstream) | confirmed |
| 11 | `collector late` for `kernel` (2.39 s) and `logs` (2.30 s) on 2026-10-29 18:50:22 belongs to the previous instance: a late start, recorded as a gap as specified | M; agent.md *Scheduling* lines 130-138 | confirmed |
| 12 | The 30 MB and 1 % targets appear only in the issue. Unit limits: `MemoryMax=256M`, `GOMEMLIMIT=192MiB`, `CPUQuota=50%` | grep of `docs/`; `deploy/agent/vandox-agent.service:22,53,54` | confirmed |

The issue's claims about the agent are confirmed (`deploy/agent/vandox-agent.service`): a static binary, a systemd
service, outbound over the tailnet only. The 24 hours are met (24 h 16 min). "Several hundred processes" is refuted for
the server (161), and that deviation is open for the PM.

The report does not show whether the memory pressure mode was on during the run: only warnings were reported, and the
mode's lines are info. The plan relies on no answer, because the pressure-cost point stays open for #45 either way.

**Spool, by the outcome of fact 8** (fail closed):
- **Confirmed growth:** the measured volume closes the *Capacity* point, as written under *Documentation updates*.
- **No answer:** BENCHMARKS gives the spool's size and listing with their reading time and no daily rate. The *Capacity* point stays open, pointing to follow-up B.
- **The spool really stopped at 13:03:** a possible defect. The Lead is launched in mode `decide` before step 6 (new issue C, and the escalation it needs).

## Acceptance criteria

- [ ] AC1: `docs/BENCHMARKS.md` has a section `## Agent footprint` (after *Spool open*), shaped like the others, with these parts:
  - **Criterion:** issue #44, as defined by record 0133. Peak RSS (`VmHWM`) under 30 MB, and cgroup CPU time over elapsed time under 1 % of one core.
  - **Procedure:** the source of every value (facts 2-7). Write it as "the values were read from", since no script is kept.
  - **Results:** a table with one row and the columns Host | Kernel | CPU | RAM | Date | Run | Processes | CPU (% of one core) | Peak RSS | RSS / PSS.
  - **Notes:**
    - the I/O and spool figures, each labelled with its own reading (22 h 42 min);
    - cgroup memory includes page cache and is not the criterion;
    - systemd's peak and I/O are unavailable on 5.15.
  - **Verdict:** "both targets met". It is followed by the three open deviations (161 processes, no sending because of the `404` of v0.1.0, the previous instance's stats lost), stated as awaiting the Product Manager's acceptance. Nothing says they are accepted.
  - The intro (lines 5-9) adds that the agent's reference host is the monitored server.
- [ ] AC2: every "#44" open point is rewritten as *Documentation updates* lists. A point closes only where facts 3-8 support it. None still names #44 as a pending measurement.
- [ ] AC3: `deploy/agent/README.md` states the measured peak RSS in normal mode. The peak under memory pressure is left to #45.
- [ ] AC4: record 0133 exists with its index row; status `Proposed` until step 9. It states the deviations as open, not accepted.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1–AC4 | none: verified without tests (below) | — |

## Verification without tests

There is no production or test code, so steps 4, 5 and the *Coverage gate* are not applicable.
- AC1, AC3: the Reviewer (step 8) compares every figure with facts 1-8, and checks that the text nowhere claims PM acceptance. Rounding: MB = 10^6 B, one decimal; CPU two decimals.
- AC2: the Reviewer runs `grep -n "#44" docs/ deploy/agent/README.md`. Hits may remain only in decision records and in the *Agent footprint* section.
- AC4: `decision-check.py` passes (orchestrator, step 7).
- All criteria: *Format check* and `scope-check.py` pass (orchestrator, step 7).

## Approach

The Dev writes the documentation from the facts above.
- Layout of the new section: as the existing *Spool open* section — criterion paragraph, `### Procedure`, `### Results` with the table, then the notes as paragraphs.
- The orchestrator creates the follow-up issues (below) before step 6, and the Dev uses their numbers.
- The PR body lists the three open deviations and asks the PM to accept them. "Closes #44" is used only after the PM's confirmation, which the orchestrator logs.

## Options considered

- Measure on the server and state the remaining deviations for the PM (chosen).
- Measure again while sending, after #43.
- A test system with synthetic load.

The reasons are in record 0133. Separately, tier `docs` with only `docs/BENCHMARKS.md` was rejected: it would leave the
Agent area and the README stating "#44 measures it".

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| docs | `docs/BENCHMARKS.md` | new *Agent footprint* section, intro sentence, open points |
| docs | `docs/areas/agent.md` | open points |
| deploy | `deploy/agent/README.md` | *Unit* `MemoryMax=` row, *Peak memory* check row |
| docs | `docs/decisions/0133-agent-footprint-measured-on-the-server-as-peak-rss-and-cpu-of-one-core.md`, `docs/decisions/README.md` | new record (Lead, done) |

## Signatures (for the Dev's skeleton)

none

## Test files

none. Existing test call sites affected: none. Files no unit test can cover: none (no code).

## Areas

Agent: the measured figures replace the "#44" pointers. No behavior changes.

## Documentation updates

All by the Dev. "A" and "B" are the follow-up issues below.
- `docs/BENCHMARKS.md`:
  - Lines 5-9 (intro): add that the agent's reference host is the monitored server.
  - Line 103: "it stays with #44" → the reference host is not measured; follow-up B.
  - Lines 364-365: per-snapshot time not measured on the server. The agent's total CPU there is 0.42 % of one core over 24 hours (*Agent footprint*). The `smaps_rollup`-under-pressure cost remains an open point of #45 only.
  - Line 390: same as above, for the connection snapshot.
  - New section per AC1.
- `docs/areas/agent.md`:
  - Lines 369-371: as the BENCHMARKS change at 364-365 (open point "for #45").
  - Line 550: drop "(#44)", point to the total CPU.
  - Line 1715: drop "(#44)", since the start-up time was not measured.
  - Lines 1727-1729 *Capacity*: drop "#44". Only if fact 8 is confirmed: the real normal-mode volume on the server (the confirmed figure; at about 160 MB a day that is about 70 % of the estimate of 225 MB, and 2 GiB holds about 13 days). Otherwise: not measured yet, follow-up B. The pressure sentence stays.
  - Line 1800: "measured in #44" → not measured yet, follow-up B.
- `deploy/agent/README.md`:
  - Line 619: replace "to be measured (the parallel run of #45)" with the peak RSS of 20.4 MB in normal mode over 24 hours on the server (BENCHMARKS, *Agent footprint*). The peak under pressure is still to be measured in #45.
  - Line 696, *Peak memory* row: the normal-mode result, with pressure still open for #45.
- `docs/ARCHITECTURE.md`: none.

## Architecture check

No guarantee is touched. "Never makes the memory situation worse" gets its first figure. The unit limits stay as they are:
the measured peak is about 8 % of `MemoryMax=256M`.

## Security considerations

- [ ] no limits (documentation only)
- [ ] none

## Decision records

- `docs/decisions/0133-agent-footprint-measured-on-the-server-as-peak-rss-and-cpu-of-one-core.md` (Proposed)

## Out of scope / follow-ups

- **A — `[Agent] Socket owners of two processes unknown on kernel 5.15 (fdinfo format)`.** Body:
  - On the production server (Ubuntu 22.04, 5.15.0-191) the connection collector logs `socket owners partly unknown processes=2 err="connstats: 528/fdinfo: not in the expected format"` at every start (install run and the #44 run), so the sockets of two processes have no owner.
  - `ParseFdinfo` (`internal/connstats/owners.go`) requires all-digit values in the first four lines. Upstream prints `pos:` with `%lli`, so a negative position is a candidate cause.
  - Task: capture `head -4` of each `/proc/<pid>/fdinfo/*` of the two processes (anonymized), add the variant as a fixture, and fix or document it.
- **B — `[Tests] Measure the agent's footprint and backfill rate while it sends`.** Body:
  - The #44 run (record 0133) did not send: `vandoxd` v0.1.0 answers `404`, because it has no ingest API.
  - Once #43 is done and a backend with the ingest API runs, measure on the server, as in BENCHMARKS *Agent footprint*: CPU and peak RSS while the backlog is sent; the real backfill rate (agent.md *Sending*); and the storage write criterion on the backend reference host (BENCHMARKS line 103). If fact 8 stays unconfirmed, add the daily spool volume as well.
- No issue for the sender's `404`. It is the documented v0.1.0 backend, and the agent behaved as specified. No issue for `collector late` either: a late start of the previous instance, recorded as a gap.
