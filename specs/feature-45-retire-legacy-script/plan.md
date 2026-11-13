# Plan: Retire the legacy monitoring script and document v0.2.0

Source: Issue #45 | [spec.md](spec.md)
Tier: trivial — documentation and the changelog plus Go tests that read them; no production code, no behavior changes.
Security areas touched: 2 Tailscale ACL and port binding, 6 Agent privileges (`deploy/agent/README.md`: wording of step 4 and of *Checks on the server* only; no control changes)
Plan size: the guide is a root shell procedure on the production server, so the cases of *Shell procedures* and their tests have to be written into the plan.

## Problem / root cause

See [spec.md](spec.md).

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | A grown file is continued only after a base with the same first line whose bytes it starts with | `docs/areas/log-import.md`:107-123 | confirmed |
| 2 | A `.gz` entry of a tar archive is decompressed on its own; a rotated copy and the same content in an archive count as one file | `src/Vandox.Import/Scanner.cs`:491-497, `log-import.md`:34, 78 | confirmed |
| 3 | The legacy log is line-framed, so it is continued | `docs/ARCHITECTURE.md`:443 | confirmed |
| 4 | A continuation loses the rest of the snapshot cut by the earlier copy, and its first snapshot has no `vmstat_per_second` | `docs/areas/log-parsers-legacy-monitoring.md`:137-139, 183-185 | confirmed |
| 5 | Step 6 archives all of `/var/log` plus a fresh journal export | `docs/INSTALL.md`:178-184 | confirmed |
| 6 | The legacy log needs `import.time_zone` | `docs/INSTALL.md`:74, `log-parsers-legacy-monitoring.md`:51 | confirmed |
| 7 | Deferral passages: `deploy/agent/README.md`:13-14, 149-151, 242-243, 764, 769; `docs/INSTALL.md`:254-257, 349-351; `README.md`:296-297; `docs/areas/backend-host.md`:179 | grep for `v0.2.0`, `#45`, `not verified` | confirmed |
| 8 | Two Go tests pin the deferral: step 4 must hold "not yet run" and "upgraded to v0.2.0", and the *Tailscale checks* row must hold "upgraded to v0.2.0" | `deploy/agent/tailscale_readme_test.go`:240-255, 257-286 | confirmed |
| 9 | Server facts (crontab line, log, rotation) and the four `v0.2.0-rc.1` checks | Product Manager, `log.md` 08:27; transmission also `docs/BENCHMARKS.md`:510-531 | confirmed |
| 10 | Tags `v0.1.0` and `v0.2.0-rc.1` exist, `v0.2.0` does not | `git ls-remote --tags origin` | confirmed |
| 11 | The operator's SSH login is a sudo user whose home (`getent passwd`) `scp` reads, as step 6 assumes, and the server has `runuser` (util-linux) | to verify: the Product Manager's dry run (below), before step 8 | unverified |

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | check: the Reviewer reads `CHANGELOG.md` against `git log v0.1.0..HEAD` and the 0.1.0 layout | — |
| AC2 | `TestRetireGuide_ComparisonNamesEveryKind_AC2` (a row for each kind of S1; INSTALL.md links the guide after step 11) | `deploy/agent/retire_guide_test.go` |
| AC3 | `TestRetireGuide_ArchiveHoldsOnlyTheScriptLog_AC3`, `TestRetireGuide_RetirementRemovesOnlyTheScriptLine_AC3`, `TestRetireGuide_RetirementRunTwiceKeepsTheUndo_AC3`, `TestRetireGuide_UndoRestoresTheLine_AC3`, `TestRetireGuide_StepsInOrder_AC3`; manual: Product Manager dry run of S2 | `deploy/agent/retire_guide_test.go` |
| AC4 | `TestAgentReadme_TailscaleIngestProbeRanInTheRc1Run_AC4` (replaces `TestAgentReadme_TailscaleIngestProbeWaitsForTheBackendUpgrade`), `TestAgentReadme_ChecksOnTheServerHoldNoOpenTailscaleIssue` adapted, `TestReleaseDocs_DeferNoCheckToV020_AC4` | `deploy/agent/tailscale_readme_test.go`, `deploy/agent/release_docs_test.go` |

Test details:
- ArchiveHoldsOnlyTheScriptLog: runs the guide's pack line in bash with stub `tar`, `runuser` and `getent` on `PATH` (stub `tar` records its arguments and working directory), from a working directory holding other files, among them `sysmonitor.log.old`, beside a log directory the test substitutes for `/var/log`; `tar` receives only the `sysmonitor.log*` names of that directory; with `SUDO_USER` unset nothing runs. Text: no `journalctl`, no `var/log` without the file name; S2 and S4 archives have different names.
- RetirementRemovesOnlyTheScriptLine: runs the guide's retirement line in bash with a stub `crontab` on `PATH` (pattern of `install_test.go`), from a working directory holding files (the pattern's `*` must not expand), with a test directory for `/root`: a crontab with the line and other entries loses only that line; one without the exact line (`... syslog.sh >/dev/null 2>&1`) installs nothing; a crontab holding only the line installs nothing; a failing `crontab -l` installs nothing.
- RetirementRunTwiceKeepsTheUndo: a second run leaves both saved files byte-identical and installs nothing.
- UndoRestoresTheLine: after a retirement, the undo line gives the crontab of before and removes the saved files; when the crontab changed since, it installs nothing.
- StepsInOrder: copy and import, retirement, wait, second copy and import, undo appear in that order.
- DeferNoCheckToV020: INSTALL.md steps 10 and 11, `deploy/agent/README.md`, `README.md` and `docs/areas/*.md` hold none of "until v0.2.0 is released", "before the release (#45)", "no release has the ingest API", "has **not yet run** there"; the two recorded rows are exempt, as is "upgraded to v0.2.0" in them.

Shell cases of the guide (*Operator environments and documented procedures*):
- (a) All lines pasted at once, or a line pasted twice: the server block that deletes the archive is a separate block after the copy on the NAS; the retirement is one `&&` line that installs only a crontab exactly one line shorter and never overwrites its saved files, so a rerun installs nothing. Tests: RetirementRemovesOnlyTheScriptLine, RetirementRunTwiceKeepsTheUndo, StepsInOrder.
- (b) One line at a time: the intended use; dry run.
- (c) A line fails: every server line chains with `&&` (the pack line under `set -o pipefail`); a failed pack may leave an incomplete archive in the login's home, which the operator does not copy and the delete block removes; a failed retirement installs nothing and leaves the saved files for the operator to read and remove before a new try. Tests: RetirementRemovesOnlyTheScriptLine, RetirementRunTwiceKeepsTheUndo.
- (d) A variable is lost: the server lines use only `${SUDO_USER:?}` and the home read from it, checked non-empty, which stop the line; the NAS lines reuse step 1's `VANDOX_USER` as step 6 does (a lost one fails `chown`, and the import cannot read the file). Test: ArchiveHoldsOnlyTheScriptLog checks the `:?` form.
- CI-only rows: none; all tests run locally.

Dry run: the Product Manager runs S2 (pack, copy, import, delete) on the production server with backend `v0.2.0-rc.1`, from the branch, before step 8, and confirms fact 11. Retirement and undo are not run (*Out of scope*); the stub tests hold them.

## Approach

New document `docs/RETIRE_LEGACY_MONITORING.md`, linked from a short paragraph after INSTALL.md step 11. Layout: H1; *Compare first* (a table: script output, agent record, interval, where seen, differences with the area link; then E1); numbered H2 steps: 1 copy and import (S2, E2, E3, E4), 2 retire (S3), 3 copy and import again (S4 with its two losses), then *Undo* (E5). Server commands for a root shell via `sudo -i`, one per line. Pack: `home=$(getent passwd "${SUDO_USER:?}" | cut -d: -f6) && [ -n "$home" ]` (under `sudo -i`, `~` is `/root`), then `set -o pipefail && (cd /var/log && tar -czf - sysmonitor.log*) | runuser -u "${SUDO_USER:?}" -- sh -c 'umask 077 && cat > "$1"' sh "$home/<name>.tar.gz"`: the glob expands in `/var/log`, and the login, not root, writes the file (mode `0600`, no `chown`, no root write into the login's directory); the delete block removes the archive, a leftover of a failed pack included; NAS commands as step 6 into `import/`, then step 7's command with the archive's name. Retirement, one `&&` line: write `/root/crontab.before-vandox` from `crontab -l` and `/root/crontab.vandox-filtered` from it with `grep -vxF` and the quoted line, each only when it does not exist yet (`set -C` in a subshell, `umask 077`); install the filtered file only when it is exactly one line shorter. A crontab holding only the script line leaves `grep` nothing, so nothing is installed: the guide says to remove the line with `crontab -e` then. Wait: `stat` of the log twice, two minutes apart. Undo: when `crontab -l` equals the filtered file (`cmp -s`), install the saved one and remove both files; else add the line with `crontab -e`.

Deferrals (fact 7): recorded cells stay; the `v0.2.0-rc.1` results go into a note after the *Checks on the server* table, which the two rows point to as settled. README.md and backend-host.md keep only what N3 leaves unverified.

## Options considered

- Guide as INSTALL.md step 12: lost, the script exists on one server only.
- Retirement with `crontab -e`: lost, it cannot be tested and depends on the operator's editor.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| docs | guide, `INSTALL.md`, `README.md`, `CHANGELOG.md`, `deploy/agent/README.md`, `backend-host.md` | text |
| deploy/agent (Go tests) | `retire_guide_test.go`, `release_docs_test.go`, `tailscale_readme_test.go` | tests |

## Signatures (for the Dev's skeleton)

none: no production code and no new API.

## Test files

`deploy/agent/retire_guide_test.go`, `deploy/agent/release_docs_test.go` (new), `deploy/agent/tailscale_readme_test.go` (two tests adapted by the Tester). Existing test code calling a changed signature: none. Files no unit test can cover: none.

## Areas

[Backend host](../../docs/areas/backend-host.md): *Not verified on a host* without #45, narrowed per N3.

## Documentation updates

All by the Dev, once the tests are written: the guide; `INSTALL.md` (link after step 11, the step 10 note, 349-351, the reboot symptom: came back in the run); `deploy/agent/README.md` (13-14, step 4, 242-243, the note after the table; keep "every other probe blocked" and the need for a backend with the ingest API, v0.1.0 answering `404`); `README.md`:296-297; `backend-host.md`:179; `CHANGELOG.md` `[0.2.0]` from `git log v0.1.0..HEAD`, its known limitations naming the parallel run without a result (N2).

## Architecture check

No guarantee touched; `docs/ARCHITECTURE.md` unchanged.

## Security considerations

- [x] no limits.
- [x] none.

## Decision records

none: a documentation procedure with no lasting architectural choice.

## Out of scope / follow-ups

As in the spec.

## Security review

- B1 (glob expanded in the working directory): accepted. The pack line runs `tar` in a subshell after `cd /var/log &&`; ArchiveHoldsOnlyTheScriptLog now runs the line with a stub `tar` from a directory holding other files and asserts the names `tar` receives.
- B2 (a second run destroys the undo): accepted. Both saved files are written only when absent (`set -C`), so a rerun fails before installing; new test RetirementRunTwiceKeepsTheUndo; the rerun is a case under (a) and (c). Undo removes both files once it restored the crontab.
- NB1 (`~` is `/root` under `sudo -i`): accepted. The home comes from `getent passwd "${SUDO_USER:?}"`, checked non-empty; fact 11 says so.
- NB2 (unquoted pattern): accepted. The pattern is quoted, and the crontab tests run from a directory with files in it.
- NB3 (root writes into the login's directory): adopted in the guide, not a follow-up. The archive is streamed to `runuser -u "${SUDO_USER:?}"`, which writes it as the login under `umask 077`; no `chown`. `runuser` joins fact 11 for the dry run. Step 6 keeps its form: it is out of this scope (documentation of #45 only), and its risk is the login's own sudoer rights; the orchestrator may file it as a `docs` issue: "INSTALL step 6: root writes the log archive into the login's home" with the streamed form as the fix.
- NB4 (`tar` leaves an output file on failure): accepted. Case (c) reworded; the delete block removes a leftover archive too.
- NB5 (crontab with only the script line): accepted. One sentence in the guide; the stub case "installs nothing" is in RetirementRemovesOnlyTheScriptLine.
- Kept in the README rewrite, as Security asks: "every other probe blocked" and the requirement of a backend with the ingest API (v0.1.0 answers `404`); *Documentation updates* says so.
