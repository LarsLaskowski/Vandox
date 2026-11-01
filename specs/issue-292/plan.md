# Plan: Check log shipping against a non-empty user journal from the server

Source: Issue #292
Status: Draft
Tier: trivial — documentation only, but in `docs/areas/`, which tier `docs` excludes (`.squad/routing.md`, *Tiers*); no decision record.
Security areas touched: none

## Problem / root cause

The server sample of #37 had an empty `user-10000.journal`, so `docs/areas/agent.md` (*Residuals (record 0121)*, line 1480)
states that no real user-journal entry was checked. The Orchestrator has since run `TestReal_NextWithDecodesEveryEntry` with
`VANDOX_REAL_JOURNAL_DIR` on a copy with a non-empty user journal; it passed. The residual is now settled and is removed; the
check is recorded in *Checked against the server*. The residual bullet at line 1480 also runs into an unrelated residual (rsyslog's
logrotate and its single `HUP`), which must stay as a bullet of its own.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The real check passed: no read error, 0 undecoded entries in all 24 files (`system.journal` 54,360 entries, `user-10000.journal` 152, 21 archives 38 to 444 each) | Orchestrator's run of `TestReal_NextWithDecodesEveryEntry` with `VANDOX_REAL_JOURNAL_DIR` on a copy of the server's journal | confirmed |
| 2 | The user files' seqnum IDs differ from the system file's; seqnums rise strictly per file and across the archives; the active user file lies inside the system range 31,249,816..31,304,291; 0 collisions with system seqnums | Orchestrator's temporary check (not committed) | confirmed |
| 3 | The copy held only the current `system.journal`, no archived system files | Orchestrator's report | confirmed |
| 4 | The residual's only occurrence is `docs/areas/agent.md:1480`; record 0121 states no such residual | `grep -rn -i` over `docs/` (records included) for the residual text, "empty user", `user-*.journal`, `#292` | confirmed |
| 5 | Line 1480's bullet joins the user-journal residual and the rsyslog logrotate residual (lines 1480 to 1482) | Read `docs/areas/agent.md:1476-1484` | confirmed |
| 6 | Record 0121 is unreleased and needs no change: its context (lines 13 to 18) already assumes the own seqnum ID and continued counter the check confirmed | `git cat-file -e v0.1.0:docs/decisions/0121-...` fails; read of the record | confirmed |
| 7 | Nothing of the real journal files is in the repository | Orchestrator's report | confirmed |

## Acceptance criteria

- [ ] AC1: The text "The user files of the server's sample were empty" appears nowhere in `docs/`.
- [ ] AC2: The rsyslog logrotate / `HUP` residual remains under *Residuals (record 0121)* as its own bullet, its wording unchanged
  apart from line wrapping.
- [ ] AC3: *Checked against the server* states the non-empty user-journal check: 24 files, no unreadable item, and the seqnum-ID
  grouping of record 0121 confirmed.
- [ ] AC4: The diff contains nothing of the real journal files; it touches only `docs/areas/agent.md` besides `specs/issue-292/`.

## Tests per criterion

None: the change has no production or test code; every criterion is listed under *Verification without tests*.

## Verification without tests

The change touches no production or test code: steps 4 (*Skeleton*), 5 (*Tests first*) and the *Coverage gate* are not applicable
(*Changes without production or test code* in `.squad/routing.md`). Step 6 is the Dev's edits alone; step 7 runs every gate but the
*Coverage gate*.

| AC | Verification | Who, when |
| -- | ------------ | --------- |
| AC1 | `grep -rn "user files of the server's sample" docs/` returns nothing | Reviewer, step 8 (read-only check) |
| AC2 | Read *Residuals (record 0121)* in the diff: the logrotate sentence is a bullet starting `- That rsyslog's logrotate configuration`, words identical to the old text | Reviewer, step 8 |
| AC3 | Read the inserted sentence of *Checked against the server* against Verified facts 1 and 2 | Reviewer, step 8 |
| AC4 | `git diff --stat origin/main...HEAD` lists only `docs/areas/agent.md` and files under `specs/issue-292/`; no journal file or seqnum ID value in the diff | Reviewer, step 8 |

## Approach

Two edits in `docs/areas/agent.md`, applied by the Dev as written (see *Documentation updates*). Lines wrap at the document's
width (about 135 columns). No hex seqnum IDs or other values of the real files beyond the counts below.

## Options considered

One option: the issue names the edit, and the check it depends on has already passed.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| docs | `docs/areas/agent.md` | residual removed, check recorded |

## Signatures (for the Dev's skeleton)

None.

## Test files

None.

Existing test code that calls a changed signature: none.

Files of the change no unit test can cover: none (documentation only, verified as above).

## Areas

`docs/areas/agent.md` (Agent area): the residual of the empty user journal is removed and the real user-journal check is recorded.
No change in behavior.

## Documentation updates

Owner: Dev. File `docs/areas/agent.md`.

Edit 1, *Residuals (record 0121)*, lines 1480 to 1482. Replace

```
- The user files of the server's sample were empty: user-journal entries are read like system entries without a real one checked. That
  rsyslog's logrotate configuration on the server renames before its single `HUP` is not checked by the installation (a manual step in `deploy/agent/README.md`); the renamed file
  is read until the writer has moved whatever that window is.
```

with

```
- That rsyslog's logrotate configuration on the server renames before its single `HUP` is not checked by the installation (a manual
  step in `deploy/agent/README.md`); the renamed file is read until the writer has moved whatever that window is.
```

Edit 2, *Checked against the server* (about line 1456): after the sentence ending `nothing of the files is committed).` and before
`CI reads generated files:`, insert

```
A later copy with a non-empty user journal (`system.journal`, `user-10000.journal` and its 21 archived files) read the same way gave
no unreadable item in any of its 24 files (54,360 system entries, 38 to 444 per user file); the user files' seqnum IDs differ from the
system file's, and their sequence numbers rise strictly within each file and across the archives and continue the system counter
without colliding with a system sequence number, as record 0121 assumes.
```

`docs/ARCHITECTURE.md`: none (lines 58 and the agent area's lines 2094 and 2318 describe the reading in general and claim no
verification).

## Architecture check

No guarantee of `docs/ARCHITECTURE.md` is touched; the documentation now states a verification that confirms existing behavior.

## Security considerations

- [x] No limits.
- [x] No exceptions reach a user: none.

## Decision records

None: no decision; record 0121 stays as it is (Verified fact 6).

## Out of scope / follow-ups

Archived system files were not in the copy; the earlier check (two system files, one archived) already covers them. No follow-up
issue.
