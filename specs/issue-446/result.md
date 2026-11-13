## Lead plan for issue #446 (work folder /home/user/Vandox/specs/issue-446)

**Tier: `patch`.** The fix is a single edit that restores two lines of one Markdown file, with no logic change. It is not `docs`, because that tier excludes `docs/decisions/`. It is not `patch/security`: the file is not in a security area and is not a CI or build file. I wrote no `plan.md`.

**Security areas touched:** none.

### Verified facts
- **Confirmed: the check fails on main.** I ran `python3 .squad/tools/decision-check.py` on 44f954e and got the error the issue quotes, ending `Checked 181 decision records: FAIL`.
- **Confirmed: v0.2.0 contains bf7a808, 9a09748 and a1f6bee.** All three are in both v0.2.0 and v0.2.0-rc.1 (`git tag --contains`). So they are part of the released text, and the issue's suspicion about a1f6bee is refuted.
- **Confirmed: only 4fdb4e7 (#442) changed the record after the release.** No tag contains 4fdb4e7. `git diff v0.2.0 HEAD -- docs/decisions/0134-*.md` shows a single hunk in *Consequences*. That hunk changes content, not the status, which is still `Accepted`. The check is right, so the check needs no fix.
- **The edit records a measured result, not a changed decision.** It replaces "the run on the production server is the maintainer's (issue #372)" with the passed production run: the route named `tailscale0`, and `IPEgressPackets` stayed 0.
- **That result is already documented elsewhere.** The same commit recorded it in `docs/areas/network-and-security-model.md` (line 62) and in `deploy/agent/README.md` (*Checks on the server* table, two rows dated 2026-11-13). The records README says records keep the why and area documents hold what is true today, so the result is already where it belongs.
- **No Go, C# or Python code outside `docs/decisions/` references record 0134's text.** `deploy/agent/readme_test.go` checks only the README.
- **Related defect: CI does not run `decision-check.py`.** Only the squad skills and `decision-consolidate` run it, which is how #442 could merge while the check failed.

### Exact edit (owner: orchestrator, under the `patch` tier)
File: `/home/user/Vandox/docs/decisions/0134-sender-binds-non-loopback-connections-to-the-tailnet-device.md`. Restore the v0.2.0 text, which is equivalent to `git checkout v0.2.0 -- <file>` because the hunk below is the only difference. Replace

```
  routes while a provider route covers the tailnet range. The run on the production server (issue #372) passed: the route named
  `tailscale0`, and the unit's `IPEgressPackets` stayed 0 while `tailscaled` was stopped.
```
with
```
  routes while a provider route covers the tailnet range; the run on the production server is the maintainer's (issue #372).
```
The status stays `Accepted`, and no other file changes.

### Options considered
1. **Restore the released text only (chosen).** No decision changed, and the new fact is already in the area document and the deploy README.
2. **A new record that supersedes 0134 (the issue's suggestion).** Rejected: the records README allows superseding only for a changed decision. Here the decision (bind to `tailscale0`, fail closed) is unchanged. Superseding would also turn every link to 0134 into a link to a superseded record and copy the whole record just to add one measurement.
3. **A new record that amends 0134 (records README lines 40–41).** Rejected: a measurement result does not meet the decision threshold, and restating it there would duplicate the area document.
4. **Change the check.** Rejected: the edit was a content change, which the check correctly refuses.

### Acceptance criteria and the check covering each
- **AC1:** `git diff v0.2.0 HEAD -- docs/decisions/0134-sender-binds-non-loopback-connections-to-the-tailnet-device.md` prints nothing. Check: that command.
- **AC2:** `python3 .squad/tools/decision-check.py` exits 0, with no error for 0134 or any other record. Check: that command, as the check this result names for the edit.
- **AC3:** The production-run result stays documented in `docs/areas/network-and-security-model.md` and `deploy/agent/README.md`, neither of which is edited. Check: `scope-check.py --tier patch` shows the diff outside `specs/` is that one file.

Gate for this patch: *Format check* (the documentation-only row of *Gates of a patch*), plus `decision-check.py` as above.

### Documentation updates
None beyond the edit. The area document and the deploy README already state the passed run. I swept `docs/areas/` and `docs/ARCHITECTURE.md`: neither lists anything this change extends. The PR template checklist asks for no changelog entry, because nothing changes for users. I created or changed no decision record.

### Proposed follow-up issue (the orchestrator creates it)
- **Title:** "[CI] Run decision-check.py in CI so a changed released record cannot merge"
- **Body:** "`.squad/tools/decision-check.py` runs only inside the squad skills. #442 was merged with a content change to released record 0134, and main failed the check until #446. Add the check to the CI workflow, with the tags fetched (`fetch-depth: 0` or `git fetch --tags`) and `--strict` semantics so a missing tag set fails instead of skipping. This is a CI change, so it goes through Security's review."

RESULT: DONE
