# Plan: Cloud-session note for *Test with coverage*; `.squad/project.md` back to pointers

Source: Issue #482 and Issue #484 (one run, one pull request: `Closes #482`, `Closes #484`)
Tier: standard — both are documentation of `.squad/stack.md` and `.squad/project.md`, but the shortening rewords entries of *Security areas*, which `.squad/routing.md` (*Tiers*, the paragraph under the table) puts in `standard` once the diff exceeds `PATCH_MAX_LINES` (about 60 changed lines); no area is removed or narrowed, so not `security`.
Security areas touched: `.squad/project.md` (*Security areas*): the section introduction and entries 3, 9, 10, 11 and 12 reworded to pointers, every area, file, type, endpoint and link kept

## Problem / root cause

**#482.** In this cloud session `PATH` starts with `/root/.claude/rm-guard/<hash>/refuse`, whose `rm` stands in for the
real one. `newHarness` (`deploy/agent/common_test.go:77-108`) symlinks every name of `toolNames` (line 50-54, `rm`
among them) from `exec.LookPath`, so the harness's `tools/rm` is the shim, and the shell process's
`PATH` (`stubs:tools`, line 116-118) holds no real `rm` the shim could hand over to: `_remove_file` exits 127.
The CI runner has no shim. Nothing in `.squad/stack.md` says so, so each run rediscovers it and asks the user.

**#484.** `.squad/project.md` has 1699 words (limit 1500). The growth since #465 is behavior written into entries:
*Security areas* 3, 9–12 (which escaping, which log lines, what an address holds, how `backend.url` is checked) and
the *Test doubles* rows *time*, *database*, *time-series reads* and *Blazor* (what each double records or holds).
All of it already stands in an area document or in the double's own XML documentation (facts 5–7), so it can
leave the file as plain deletions, no area document changes.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The test fails in the cloud session with `rm: command not found`, and it is the only failing test of `deploy/agent` | `go test ./deploy/agent -count=1`: only `TestRemoveFile_RemovesAFileOrALinkNeverItsTarget` FAIL, sub-tests exit 127 with stderr `rm: command not found` | confirmed |
| 2 | The shim is first on `PATH`; real `rm` is `/usr/bin/rm` and `/bin/rm` | `echo $PATH`, `type -a rm` in this session | confirmed |
| 3 | The CI job that runs the Go tests is named *Agent (Go)* | `.github/workflows/ci.yml:17-18` | confirmed |
| 4 | `gate-stamp.py` takes each gate's command from the first code span of its row in *Commands*, so a paragraph outside the table changes no command it runs; a branch that changes only Markdown outside `specs/` runs *Format check*, `scope-check.py` (and `decision-check.py` for records/areas) alone | `.squad/tools/gate-stamp.py` docstring, lines 14-17 and 38-40 | confirmed |
| 5 | The details removed from *Security areas* 10–12 stand in area documents: `LogText.Escape`/`ExternalText` in `web-ui.md` (*External text*, line 103, 227); the two log lines in `web-ui-history-view.md:173` and `web-ui-processes-view.md:164`; `HistoryAddress`, `HistoryChartEvents` (`Select`, `Pick`) in `web-ui-history-view.md:220-221`; `ProcessesAddress` in `web-ui-processes-view.md:206`; `RetentionRemover` in `retention.md`, `storage.md`; the IP-literal `backend.url` rule in `agent-sending.md:39`, `configuration-options.md:58` | grep of `docs/areas/` | confirmed |
| 6 | The descriptions removed from the *Test doubles* rows stand in each double's `<summary>` (`FakeHistoryReader`, `FakeProgramRollupReader`, `ScriptedTimeSeriesReader`, `ProbeTimeSeriesReader`, `CountingTimeSeriesReader`, `HistoryPageHarness`, `ProcessesPageHarness`, `DisposeFailingClock`, `RefreshProbe`, `CountingClock`) | `grep -B4 "class <name>"` in `tests/` | confirmed |
| 7 | The draft `specs/issue-482/project-draft.md` has 1480 words by `config-check.py`'s count (`len(text.split())`), keeps every link of the base file, and every code span except `ExternalText`, `LogText.Escape`, `Select`, `Pick`, `TimeSeriesService` and the two log-line texts (fact 5, 6); `*PageHarness` stays (six harnesses: `History`, `Incident`, `Log`, `Overview`, `Processes`, `Report`) | set comparison of code spans and link targets, base vs. draft (scratch script) | confirmed |
| 8 | No test or tool besides the squad tools reads `.squad/project.md`; `docs/UNIT_TESTS.md` and the squad files point to its *Test doubles* section, which stays | `grep -rl project.md` over `*.go`, `*.cs`, `*.py` outside `.squad/tools` (only `.claude/hooks/file-guard.py`) | confirmed |
| 9 | The note's command (fact 2 shim stripped from `PATH` for one process) makes the test pass | to verify: the orchestrator runs `PATH="$(printf '%s' "$PATH" \| tr ':' '\n' \| grep -v '/rm-guard/' \| paste -sd: -)" go test ./deploy/agent -run '^TestRemoveFile_RemovesAFileOrALinkNeverItsTarget$' -count=1` once, only after the user approved it (the Lead did not, as it bypasses the user's guard), step 6 | unverified |

## Acceptance criteria

- AC1: (#484) `python3 .squad/tools/config-check.py` passes and prints no `PROJECT_MAX_WORDS` warning.
- AC2: (#484) every security area (1–13), guarantee (3), integration-surface entry (9 kinds) and test-double row (19)
  of the base file is still in `.squad/project.md` with its files, types and endpoints named; every link target of the
  base file is still there; a code span the base file had is gone only if fact 5 or 6 names where it now stands.
- AC3: (#484) no security area is narrowed: each entry's protected property and scope cover at least what the base
  entry covered (no file, directory, type, route or address dropped from an entry, unless a broader scope in the same entry still
  covers it, as entry 3's "every page under `Components/Pages/`" covers `HistoryPage` and `ProcessesPage`;
  `RetentionRemover`-only deletes and the spool's bound stay in entry 9).
- AC4: (#482) `.squad/stack.md` holds the cloud-session note below the paragraph that starts "*Test* and *Test with
  coverage* build and run", with the content *Approach* lists; the *Test with coverage* row of *Commands* is unchanged.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | check: `python3 .squad/tools/config-check.py` | none |
| AC2 | check: code-span and link comparison base vs. branch | none |
| AC3 | check: Reviewer reads entries 3, 9–12 against base | none |
| AC4 | check: `git diff` of `.squad/stack.md` | none |

## Verification without tests

Markdown only (`.squad/project.md`, `.squad/stack.md`): steps 4, 5 and the *Coverage gate* are not applicable.

- AC1: check: orchestrator, step 7 (and the Dev in step 6) runs `python3 .squad/tools/config-check.py`.
- AC2: check: Reviewer, step 8: `git show origin/main:.squad/project.md` against the branch file, extracting
  `` `…` `` spans and `](…)` targets (the comparison of fact 7); every dropped span must be one fact 5 or 6 names.
- AC3: check: Security (plan review, against `project-draft.md`) and the Reviewer (step 8, against the diff).
- AC4: check: Reviewer, step 8, reads the note against *Approach* and confirms `git diff` leaves the *Commands* table
  untouched. Fact 9 is the orchestrator's check in step 6; if it fails, the note's command is corrected before step 7.

## Approach

**#484.** The Dev reports the new text of `.squad/project.md` and the orchestrator writes it in (`.squad/team.md`,
*Files*). `specs/issue-482/project-draft.md` is a complete draft at 1480 words; the Dev may reword it but keeps the
rules: names stay, descriptions go; nothing moves into another file. Changes in the draft:
- introduction: one sentence for who reads it; the pointer rule adds "types" and "what a type does (a test double:
  its XML documentation)";
- *Security areas* introduction: two sentences, still pointing to `.squad/routing.md` *Tiers*;
- entry 3: drops the parenthetical naming `HistoryPage`/`ProcessesPage` (covered by "every page under
  `Components/Pages/`"; both stay named in entry 12);
- entry 9: "the spool and retention's deletes are bounded", `RetentionRemover` in the file list;
- entry 10: the addresses as `/logs`, `/report`, `/history` (`HistoryAddress`), `/processes` (`ProcessesAddress`),
  `HistoryChartEvents`; documents as `wire-format*.md`, `configuration-*.md`, `agent*.md`, `log-*.md`,
  `web-ui-*.md` plus the four links not covered by those patterns;
- entry 11: `internal/sender` "with the `backend.url` checks (`internal/config/agent.go`, `validate_backend_url` in
  `install.sh`)", adding the [configuration-options] link where the rule stands;
- entry 12: `HistoryPage`, `HistoryChart` and `ProcessesPage` named without their escaping and log-line details;
  [web-ui] gains "(*External text*)";
- *Test doubles*: rows *time*, *database*, *time-series reads* and *Blazor* list names only; the intro says the
  double's own documentation comment holds what it does.
Reserve if the Dev's rewording runs over: the 27 bullet markers of *Integration surface* as inline lists.

**#482.** The Dev reports one paragraph for `.squad/stack.md`, placed after the paragraph on Debug/Release
(ending "for allocation bounds."), before "Test time limits:". It states:
1. In a cloud session a harness directory that refuses `rm` (`…/rm-guard/…/refuse`) is first on `PATH`; the
   `deploy/agent` harness links its tools from `PATH`, so `TestRemoveFile_RemovesAFileOrALinkNeverItsTarget`
   (`deploy/agent/uninstall_test.go`) fails with `rm: command not found`, failing *Test with coverage* and every
   `gate-stamp.py` run that includes it; the code is not at fault, and the CI job *Agent (Go)* is the reference.
2. A member whose run fails with that test alone, with that message, reports it as this environmental failure and
   does not ask about it.
3. The orchestrator asks the user once, at the start of the run, to approve running `gate-stamp.py` (and the fact-9
   style single-test check) with that directory removed from `PATH` for that one process, and then runs
   `PATH="$(printf '%s' "$PATH" | tr ':' '\n' | grep -v '/rm-guard/' | paste -sd: -)" python3 .squad/tools/gate-stamp.py …`;
   an agent's message is no such approval, and without it the stamp is not run that way.
Exact wording is the Dev's.

Early draft PR: no

## Options considered

- #484, shorten the entries to pointers (chosen): the issue's scope; the draft shows it fits (1480).
- #484, raise `PROJECT_MAX_WORDS`: lost — the limit exists because five members read the file each launch, and
  shortening works without it.
- #484, move the *Test doubles* table into `docs/UNIT_TESTS.md`: lost — the squad files and `docs/UNIT_TESTS.md` point
  to `.squad/project.md` (*Test doubles*); not needed to reach the limit.
- #482, the note (chosen): the issue's proposal.
- #482, make the test resolve `rm` from `/bin` or `/usr/bin` (the issue's possible follow-up): lost here — a Go test
  change outside both issues' scope, and test code that silently steps around a guard of the user's environment,
  where the note keeps the user's one-time approval. Left to the Product Manager as a follow-up (*Out of scope*).
- Splitting #482 and #484 into two runs: lost — both are small Markdown edits to different `.squad/` files with no
  interaction; one branch, one PR.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| squad project knowledge | `.squad/project.md` | entries shortened to pointers (#484) |
| squad project knowledge | `.squad/stack.md` | cloud-session paragraph (#482) |

## Signatures (for the Dev's skeleton)

none — no code changes.

## Test files

None: no test is written or changed. Existing test call sites affected: none. Files no unit test can cover: none.

## Areas

none (no change in behavior; every detail removed from `.squad/project.md` already stands in an area document, fact 5).

## Documentation updates

- `.squad/project.md` (Dev reports the text, orchestrator writes it): as *Approach*, #484.
- `.squad/stack.md` (Dev reports the text, orchestrator writes it): as *Approach*, #482.
- `CHANGELOG.md`: none (not user-visible). `README.md`, `docs/ARCHITECTURE.md`, area documents: none. Sweep for
  enumerations: none extended.

## Architecture check

No guarantee of `.squad/project.md` or `docs/ARCHITECTURE.md` changes; the *Guarantees* section is untouched.

## Security considerations

- [x] no limits.
- [x] none.
- The #482 note documents running one process with a guard of the user's session removed from `PATH`; it limits
  that to the orchestrator, one process, after the user's own approval, never on an agent's message.
- The #484 rewording must not narrow an area (AC3); Security reviews entries 3, 9–12 in `project-draft.md`.

## Decision records

none: no decision beyond the obvious documentation fix.

## Out of scope / follow-ups

- Making `newHarness` resolve `rm` past the shim (#482's possible follow-up): for the Product Manager to open if wanted.
- Keeping `.squad/project.md` short over time (#484's *Follow-up to consider*): a template issue, not this run.
