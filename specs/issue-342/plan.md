# Plan: Measure the agent's footprint and backfill rate while it sends

Source: Issue #342 | [measurements.md](measurements.md) (the Product Manager's values, the Dev's only source of numbers)
Tier: trivial — documentation only, but it changes area documents and needs a decision record (an accepted deviation from the footprint criterion), so it is not `docs`.
Security areas touched: none
Plan size: the plan names eight documents and the exact open points each one closes; the data itself is in measurements.md.

## Problem / root cause

No defect: a measurement task. #44 measured the footprint while the agent could not send (record 0133); the Product Manager
has now measured the sending path, the backfill rate, the storage write criterion on the reference host, the snapshot cost
under pressure and the spool open time on the server. The documents still say "not measured" in each of these places.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The open points naming #342 are BENCHMARKS.md lines 105-106, 366-368, 440, 487-488, agent-sending.md 116-119, agent-spool.md 236-239, agent-system-metrics.md 215-217 | `grep -rn "#342\|not measured" docs` at 31d928a | confirmed |
| 2 | `docs/areas/agent.md` (the issue's "backfill section") holds no #342 point; the backfill rate lives in agent-sending.md, *Memory and rate* | grep of agent.md; agent.md:167 names only #45 (thresholds); the issue's location is outdated | refuted |
| 3 | Stale figures outside the #342 points: BENCHMARKS.md:65 (783 ms as the SSD verdict), storage.md:145 (783 ms median), deploy/agent/README.md:682 and :767 (peak 20.4 MB "in normal mode") | read at 31d928a | confirmed |
| 4 | Record 0133 is released (in `v0.2.0-rc.1`), so it is amended by a new record, not edited | `git tag --contains` of its adding commit: `v0.2.0-rc.1`; `git ls-remote --tags origin` | confirmed |
| 5 | The raw files match the brief (readings of 117391 and 300777, journal lines, spool benchmark, docker stats) | read both raw files | confirmed |
| 6 | The derived figures of measurements.md follow from the measured ones (0.49 / 0.45 / 0.53 %, 3,678 and 3,523 records/s, 2 min 35 s, 0.42 %, 6.55 MB/h, 12 %, 34 µs, 41 B, 143 KiB/s, median 55.5 ms, 0.37 %, 28.8 MB) | recomputed by the Lead | confirmed |
| 7 | The episode's switching matches agent.md *Memory pressure mode* (on at `some avg10` ≥ 10.00 with `MemAvailable` about 52 %, off below 5.00 and ≥ 15 %) | journal values vs agent.md:163-166 | confirmed |
| 8 | The process snapshot ran over 174 processes, not the 250 the criterion and procedure name (BENCHMARKS.md:343-349) | brief; procedure text | confirmed |
| 9 | `--backfill` has no criterion of its own (BENCHMARKS.md:108-122), so its reference-host figures need no verdict | read | confirmed |
| 10 | CHANGELOG.md has no unreleased section and this change alters no behavior: no changelog entry | `grep "^## " CHANGELOG.md` | confirmed |

## Acceptance criteria

- AC1: BENCHMARKS.md *Agent footprint* gets a run "while sending" (#342) with its procedure and results (measurements.md §1–3):
  verdict peak RSS 28.8 MB **met** (1.2 MB margin), mean CPU 0.49 % **met** (0.45 % in live sending), backfill CPU about 12–14 %
  of one core an **accepted deviation** (record 0195); the paragraph "Not measured, and open for #342 …" is removed. The #44 run
  stays as it is.
- AC2: The real backfill rate is stated in BENCHMARKS.md (*Agent footprint*) and in agent-sending.md *Memory and rate*:
  3,523 records/s (clean backfill) and 3,678 records/s (first backfill, process 117391's share), a day's backlog about
  2 min 35 s (derived), the bandwidth limit not binding (derived); "the real rate is not measured yet, #342" is gone. The
  3,207 records/s bound and the test band stay.
- AC3: *Storage write throughput*: a Results row for the DS918+ SSD volume with the published program (first batch 1,438–1,580,
  later 852–974, median **906–936**, three runs), the `--warmup` run (897 ms) in the text, the outcome paragraph naming this as
  the current result (783 ms is the row before the agent timeline); lines 105-106 replace the prediction with the measurement;
  the SDK-container runs appear only as a different, slower procedure; the `--backfill` medians (1603, 1487, 1561 ms) appear as
  measured with that procedure and "not repeated with the published program". storage.md:145 names the current median.
- AC4: *Process snapshot*: a Results row for the server (174 processes, median **55.5** ms) and text saying 174 is not the
  criterion's 250, the ten values' range, the derived 0.37 % of one core every 15 s under pressure, and the 10 s episode as
  evidence only; the "not measured" and "open point of #45 and #342" wording leaves BENCHMARKS.md:366-368 and
  agent-system-metrics.md:215-217. agent.md:167 (#45 thresholds) is unchanged.
- AC5: *Spool open*: two server rows (55.1 ms, 26.1 ms); BENCHMARKS.md:440 and agent-spool.md:239 name the server figures
  instead of "not measured on the server (#342)".
- AC6: The backend's CPU and memory during and after the backfills are written as an observation, with no target or verdict.
- AC7: deploy/agent/README.md:682 and :767 give the peak while sending (28.8 MB) and that the 10 s episode left `VmHWM`
  unchanged; peak under sustained pressure stays with #45. Record 0195 (Proposed) and its index row exist; agent.md *Related
  decisions* lists it; `grep -rn "#342" docs deploy README.md` hits only records 0133 and 0195.
- AC8: Every number is taken from measurements.md exactly; each derived one is labelled derived (or "computed"); no statement
  goes beyond the data (no cause of the rate's bound, no CPU for the episode, no verdict at 250 processes).

## Verification without tests

Steps 4, 5 and the *Coverage gate* are not applicable: no production or test code.

- AC1–AC6, AC8: check: the Reviewer (step 8) reads each named passage against measurements.md, number by number.
- AC7: check: the Reviewer runs the grep of AC7 and `python3 .squad/tools/decision-check.py`.
- All: CI: *Format check* (Dev, step 7) and `scope-check.py --tier trivial`.

## Approach

Layout: the Dev follows each section's existing layout — tables extended with rows of the same columns, prose after the
table. Specifics:

- *Agent footprint*: after the existing *Results*/verdict, a subsection `### While sending (issue #342)` with: setup (bullets of
  measurements.md *Setup*), the first backfill (§2), the clean backfill (§3, with the readings table), the long reading (§1),
  a results table in the existing columns (Run: 35 h 50 min 3 s, Processes 174), the verdict (AC1), and a paragraph
  `Backend (observation)`. Kernel "5.15" (no patch level was handed over); unknown Go versions are "not recorded (built from
  `3e9611b`)"; the benchmark dates are 2026-11-09 unless the orchestrator passes the run dates.
- Wording: English; kB values as given, MB only where measurements.md gives them; "rough" kept where measurements.md says it.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| docs | `docs/BENCHMARKS.md`, `docs/areas/agent-sending.md`, `docs/areas/agent-spool.md`, `docs/areas/agent-system-metrics.md`, `docs/areas/storage.md`, `docs/areas/agent.md`, `deploy/agent/README.md` | measurements, closed points |
| decisions | `docs/decisions/0195-a-backfills-cpu-above-one-percent-of-one-core-is-an-accepted-deviation.md`, `docs/decisions/README.md` | new record, index row (Lead, done) |

## Signatures (for the Dev's skeleton)

None: the change adds no API.

## Test files

None. Existing test call sites affected: none. Files no unit test can cover: none.

## Areas

agent-sending (*Memory and rate*: measured rate), agent-spool (*Memory*: server open time), agent-system-metrics (*Memory,
hanging reads and cost*: snapshot cost on the server), storage (reference-host result), agent (*Related decisions*: 0195).
No behavior changes.

## Documentation updates

All by the Dev, as AC1–AC7 name them. Not changed: BENCHMARKS.md:88, :189, :393, :416 and agent-connections.md:199 (other
issues' points or historical comparisons); records 0113, 0133, 0141 (released); `docs/ARCHITECTURE.md` (no guarantee or flow
changes); CHANGELOG.md (fact 10).

## Architecture check

No guarantee of `.squad/project.md` changes; the footprint targets stay as 0133 defines them, and 0195 records the accepted
backfill deviation.

## Security considerations

- [x] No limits.
- [x] None.

## Decision records

- `docs/decisions/0195-a-backfills-cpu-above-one-percent-of-one-core-is-an-accepted-deviation.md` (Proposed), amends 0133.

## Out of scope / follow-ups

- The `--backfill` scenario with the published program on the DS918+: optional, a quick run by the Product Manager; without
  it the document says it was not repeated (AC3).
- Peak memory under sustained pressure and the pressure thresholds: #45.
