# Spec: Retire the legacy monitoring script and document v0.2.0

Source: Issue #45
Status: Draft

## Problem

The maintainer replaces the `top`/`lsof` shell script on the monitored server by `vandox-agent` and releases v0.2.0
([#45](https://github.com/LarsLaskowski/Vandox/issues/45)). The Product Manager scoped this change to documentation and the changelog:
the rollout, the one-week parallel run, disabling the script on the server and the tag stay with the maintainer, and #45 stays open.
Today nothing tells the operator how to judge the parallel run or how to retire the script without losing its history, `CHANGELOG.md`
has no v0.2.0 entry, and the installation documents still defer several checks to "v0.2.0" or "#45" although the `v0.2.0-rc.1`
backend and agent already ran together ([BENCHMARKS.md](../../docs/BENCHMARKS.md), *While sending*).

## Scenarios

- S1: Given the agent runs beside the script, when the operator opens the retirement guide, then for every kind of data the script
  writes (the `top` summary, the process table, the `meminfo`, `pressure` and `vmstat` sections, the `lsof -ni` connections) the guide
  names what the agent records instead, how often, where the operator sees it, and the differences the area documents already state.
- S2: Given the comparison is satisfactory, when the operator follows the guide's retirement steps on the server, then the script's
  schedule is removed, the script stops writing, and its log files stay where they are.
- S3: Given the script is retired, when the operator follows the guide's import step, then the log written since the last import is
  imported with `vandoxd import` as INSTALL.md steps 6 and 7 describe.
- S4: Given v0.2.0 is about to be released, when the maintainer reads `CHANGELOG.md`, then it has a v0.2.0 entry in the layout of the
  0.1.0 entry with what v0.2.0 adds, changes, leaves out and its known limitations.
- S5: Given an operator installs v0.2.0, when they read INSTALL.md steps 10 and 11, the agent configuration reference and the Tailscale
  setup, then no statement defers a check to "v0.2.0", "#45" or "until v0.2.0 is released", and the documents agree with each other.

## Errors and edge cases

- E1: An item of the comparison the agent does not deliver, or delivers worse: the guide says not to retire the script and to record the
  difference in an issue.
- E2: The parallel week is in both sources: an import of the script's log after the agent started stores that week a second time under
  source `legacy-monitor` (no deduplication, [Log import](../../docs/areas/log-import.md)); the guide says so and what it affects.
- E3: The script's log has rotated copies: the guide keeps and imports all of them.
- E4: The import skips snapshots (the format was built from a description, not the real file): the guide points to the import's skip
  report and to an issue with the skipped shapes, never to a hand edit of the log.
- E5: Undoing the retirement: the guide states how to restore the schedule.

## Non-functional requirements

- N1: Every command of the guide runs as root on the monitored server one line at a time, changes nothing before the retirement step and
  names no path or schedule of the script that the Product Manager did not confirm (*Open questions*).
- N2: The documents never state the result of the parallel run or the release: those are the maintainer's (#45).
- N3: A statement that no run has shown stays marked as not verified; only what the Product Manager confirms from the `v0.2.0-rc.1`
  run is stated as verified.

## Acceptance criteria

- AC1: `CHANGELOG.md` has a `[0.2.0]` section above `[0.1.0]`, with its link line, in the 0.1.0 entry's layout, dated as
  `docs/CONTRIBUTING.md` (*Cutting a release*) prescribes, listing the user-visible changes since v0.1.0 (S4).
- AC2: A retirement guide, linked from INSTALL.md after step 11, holds the comparison checklist of S1 with E1 (S1, E1).
- AC3: The guide's retirement and import steps do S2 and S3 and cover E2 to E5 under N1 and N2 (S2, S3, E2, E3, E4, E5, N1, N2).
- AC4: INSTALL.md steps 10 and 11, `deploy/agent/README.md`, the README's agent options and the area documents hold no statement that
  defers a check to v0.2.0 or #45 other than the parallel run; each such statement is stated as the `v0.2.0-rc.1` run showed it or kept
  as not verified (S5, N3).

## Out of scope

Rolling out the agent, the parallel run itself, disabling the script on the server, the `v0.2.0` tag and release (the maintainer, #45);
the memory pressure thresholds and the peak under sustained pressure, which the parallel run decides ([Agent](../../docs/areas/agent.md),
[BENCHMARKS.md](../../docs/BENCHMARKS.md)); any change of code, scripts or configuration files.

## Open questions

- Where the script, its schedule and its log live on the server, and whether the log is rotated. Not answered: escalated.
- Which of the deferred checks the `v0.2.0-rc.1` run showed: the Synology switch to TUN mode and its boot task, the container start after
  a reboot, the `401` of the ingest probe and *Transmission to the backend* in `deploy/agent/README.md`. Not answered: escalated.
- The changelog date: Answered: `docs/CONTRIBUTING.md` lets the section say `unreleased` until the release pull request sets the date.

## Area documents

- [Backend host](../../docs/areas/backend-host.md): its *Not verified on a host* entry narrowed to what the `v0.2.0-rc.1` run did not show.
- [Agent system metrics](../../docs/areas/agent-system-metrics.md), [Agent connections](../../docs/areas/agent-connections.md) and
  [Agent](../../docs/areas/agent.md) keep their references to the parallel run of #45; the guide links them for the differences.
