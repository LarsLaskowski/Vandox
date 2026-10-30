# Plan: Footprint and load test of the agent

Source: Issue #44
Status: Revised (2)
Tier: trivial — the change records the Product Manager's measurements in documentation (`docs/BENCHMARKS.md`, the Agent area, `deploy/agent/README.md`) and needs a decision record, which rules out `docs`; no code, test, configuration or behavior changes.
Security areas touched: none

## Problem / root cause

#44 asks for the agent's footprint over 24 hours against the targets of under 30 MB memory and under 1 % CPU. The
Product Manager measured the agent on the production server and delivered the raw values. Nothing in the repository
records them, and several documents still say "#44 measures it". No code defect is involved.

**Closing #44.** The issue's second criterion ("targets met or deviation justified and accepted") is met: the
Product Manager accepted the three remaining deviations on 2026-10-30 (record 0133):
- 161 processes instead of several hundred;
- no sending: the backend answers `404`;
- the previous instance's stats are lost.

The PR body names them as accepted by the Product Manager and says "Closes #44".

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
| 7 | Spool, full listing read 2026-10-30 about 19:58 CET: `du -sb` 331,265,645 B. The listing has `spool-meta.json` (128 B, modified 2026-10-29 19:32:27, the restart), `agent-state.json` (496 B) and the data files. The data files are a first one of 446,931 B (2026-10-28 17:38:49); 19 sealed ones of 16.75 to 16.78 MB, modified from 2026-10-28 20:03 to 2026-10-30 18:07 CET; and the active one of 12,261,205 B (19:58:31). The files from before the restart are still there | M (the earlier 18:14 listing was cut by `head`) | confirmed |
| 8 | Daily volume while not sending: the 19 sealed files span 2026-10-28 20:03 to 2026-10-30 18:07, which is 46 h 4 min over 18 intervals, i.e. 2 h 33 min 33 s per file of about 16.76 MB. That gives **about 157 MB a day**. Cross-check: 331,265,021 B of data files over the 50 h 20 min from the first file's time to the active file's is 158.0 MB a day. That is about 70 % of the estimate of 225 MB a day (1,576,818,758 B / 7, `TestSpool_SevenDayOutageFullScale`), so 2 GiB holds about 13.7 days of it. The PM counted 22 data files, but the listed times and sizes add up to 21, and `du` matches 21; the docs state no file count | M; D | confirmed |
| 9 | The sender's `404` comes from `vandoxd` v0.1.0, which has no ingest API: commit 7228815 is not an ancestor of `v0.1.0`. The agent behaved as specified (shared backoff, records kept) | `git merge-base --is-ancestor 7228815 v0.1.0` → false; `deploy/agent/README.md` *Transmission to the backend*; agent.md *Not accepting, one shared backoff* | confirmed |
| 10 | `socket owners partly unknown processes=2 err="connstats: 528/fdinfo: not in the expected format"` at every start (also in the install run, README line 681). `ParseFdinfo` (`internal/connstats/owners.go:36-64`) requires all-digit values in the first four lines | M; code. The cause is left to #341 (hypothesis: a negative `pos:`, printed with `%lli` upstream) | confirmed |
| 11 | `collector late` for `kernel` (2.39 s) and `logs` (2.30 s) on 2026-10-29 18:50:22 belongs to the previous instance: a late start, recorded as a gap as specified | M; agent.md *Scheduling* lines 130-138 | confirmed |
| 12 | The 30 MB and 1 % targets appear only in the issue. Unit limits: `MemoryMax=256M`, `GOMEMLIMIT=192MiB`, `CPUQuota=50%` | grep of `docs/`; `deploy/agent/vandox-agent.service:22,53,54` | confirmed |

The issue's claims about the agent are confirmed (`deploy/agent/vandox-agent.service`): a static binary, a systemd
service, outbound over the tailnet only. The 24 hours are met (24 h 16 min). "Several hundred processes" is refuted for
the server (161), and that deviation is open for the PM.

The report does not show whether the memory pressure mode was on during the run: only warnings were reported, and the
mode's lines are info. The plan relies on no answer, because the pressure-cost point stays open for #45 either way.

## Acceptance criteria

- [ ] AC1: `docs/BENCHMARKS.md` has a section `## Agent footprint` (after *Spool open*), shaped like the others, with these parts:
  - **Criterion:** issue #44, as defined by record 0133. Peak RSS (`VmHWM`) under 30 MB, and cgroup CPU time over elapsed time under 1 % of one core.
  - **Procedure:** the source of every value (facts 2-7). Write it as "the values were read from", since no script is kept.
  - **Results:** a table with one row and the columns Host | Kernel | CPU | RAM | Date | Run | Processes | CPU (% of one core) | Peak RSS | RSS / PSS.
  - **Notes:**
    - the I/O figures labelled with their own reading (22 h 42 min), and the spool figures with theirs (19:58 CET): size, the daily volume of fact 8 with its derivation;
    - cgroup memory includes page cache and is not the criterion;
    - systemd's peak and I/O are unavailable on 5.15.
  - **Verdict:** "both targets met". It is followed by the three deviations (161 processes, no sending because of the `404` of v0.1.0, the previous instance's stats lost), accepted by the Product Manager on 2026-10-30 (record 0133).
  - The intro (lines 5-9) adds that the agent's reference host is the monitored server.
- [ ] AC2: every "#44" open point is rewritten as *Documentation updates* lists. A point closes only where facts 3-8 support it. None still names #44 as a pending measurement.
- [ ] AC3: `deploy/agent/README.md` states the measured peak RSS in normal mode. The peak under memory pressure is left to #45.
- [ ] AC4: record 0133 exists with its index row; status `Proposed` until step 9. It states the deviations and their acceptance by the Product Manager on 2026-10-30.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1–AC4 | none: verified without tests (below) | — |

## Verification without tests

There is no production or test code, so steps 4, 5 and the *Coverage gate* are not applicable.
- AC1, AC3: the Reviewer (step 8) compares every figure with facts 1-8, and that the acceptance is attributed to the Product Manager with its date. Rounding: MB = 10^6 B, one decimal; CPU two decimals.
- AC2: the Reviewer runs `grep -n "#44" docs/ deploy/agent/README.md`. Hits may remain only in decision records and in the *Agent footprint* section.
- AC4: `decision-check.py` passes (orchestrator, step 7).
- All criteria: *Format check* and `scope-check.py` pass (orchestrator, step 7).

## Approach

The Dev writes the documentation from the facts above. Nothing is waiting: the measurements, the PM's acceptance and
the follow-up issues (#341, #342) are all in this plan.

Step list:
1. Step 6, Dev: write every item of *Documentation updates*, then run *Format* on the changed files. The *Agent footprint* section takes the layout of *Spool open*: criterion paragraph, `### Procedure`, `### Results` with the table, then the notes as paragraphs.
2. Step 7, orchestrator: *Format check*, `scope-check.py`, `decision-check.py`.
3. Step 8, Reviewer: one round, against *Verification without tests*.
4. Step 9: record 0133 set to `Accepted`. The PR body lists the three deviations as accepted by the Product Manager on 2026-10-30 and says "Closes #44".

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

All by the Dev.
- `docs/BENCHMARKS.md`:
  - Lines 5-9 (intro): add that the agent's reference host is the monitored server.
  - Line 103: "it stays with #44" → the reference host is not measured; #342.
  - Lines 364-365: per-snapshot time not measured on the server. The agent's total CPU there is 0.42 % of one core over 24 hours (*Agent footprint*). The `smaps_rollup`-under-pressure cost stays open for #45 and #342.
  - Line 390: same as above, for the connection snapshot.
  - Line 436 (*Spool open*): the server is not measured; #342.
  - New section per AC1.
- `docs/areas/agent.md`:
  - Lines 369-371: as the BENCHMARKS change at 364-365 (open point "for #45 and #342").
  - Line 550: drop "(#44)", point to the total CPU.
  - Line 1715: "the server is not measured (#44)" → not measured on the server; #342.
  - Lines 1727-1729 *Capacity*: replace "The real volume is measured on the server in #44" with the server figure. On the production server, without sending (nothing acknowledged), the spool grew by about 157 MB a day, derived from the sealed files' spacing (one file of about 16.76 MB every 2 h 33 min, 2026-10-28 to 2026-10-30; cross-check 158 MB from the total). That is about 70 % of the reference stream's 225 MB, so the default 2 GiB holds about 13.7 days. Link BENCHMARKS *Agent footprint*. The pressure sentence stays unchanged.
  - Line 1800: "measured in #44" → not measured yet; #342.
- `deploy/agent/README.md`:
  - Line 619: replace "to be measured (the parallel run of #45)" with the peak RSS of 20.4 MB in normal mode over 24 hours on the server (BENCHMARKS, *Agent footprint*). The peak under pressure is still to be measured in #45.
  - Line 696, *Peak memory* row: result "20.4 MB peak RSS (`VmHWM`) over 24 h in normal mode (BENCHMARKS, *Agent footprint*); under memory pressure to be measured (#45)".
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

- #341 — `[Agent] Socket owners of two processes unknown on kernel 5.15 (fdinfo format)` (created).
- #342 — `[Tests] Measure the agent's footprint and backfill rate while it sends` (created). It takes:
  - the backfill rate;
  - the backend reference host's storage write criterion;
  - the `smaps_rollup`-under-pressure cost (with #45);
  - the spool open time on the server.
- No issue for the sender's `404`. It is the documented v0.1.0 backend, and the agent behaved as specified. No issue for `collector late` either: a late start of the previous instance, recorded as a gap.
