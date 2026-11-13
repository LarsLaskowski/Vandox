## Plan for issue #445: tier `docs`, no `plan.md`

**Tier: `docs`.** Justification: the diff changes one row of the *Test doubles* table in `.squad/project.md`. Under `.squad/routing.md` (*Tiers*, the paragraph after the table), an edit to `.squad/project.md` counts as documentation when it touches neither *Security areas* nor a command the pipeline runs. This edit touches neither. No production code, test code or decision record changes. The decision threshold is not met, because this only adds two existing doubles to a list.

**Security areas touched:** none.

### Claims checked against the code
- **Confirmed:** `tests/Vandox.Backend.Tests/TimerSignalingClock.cs` exists. It is `internal sealed class TimerSignalingClock : FakeTimeProvider` and signals when timers with a given due time have been armed.
- **Confirmed:** `.squad/project.md` line 186 (the *time* row) lists only "C#: `FakeTimeProvider`" on the C# side. Neither double appears anywhere in `.squad/project.md`.
- **Confirmed:** `docs/UNIT_TESTS.md` line 33–34 says a new double is added to `.squad/project.md` (*Test doubles*). Line 99 already names `TimerSignalingClock` (Backend tests), so `docs/UNIT_TESTS.md` needs no edit and nothing here depends on PR #454.
- **On the question of `CountingClock`: it belongs in the table.** `tests/Vandox.Storage.Tests/CountingClock.cs` is a hand-written `TimeProvider` double in its own file. It steps the clock forward a fixed amount on every `GetTimestamp` read, counts the reads and has an `OnRead` hook. It was added in a36d976 (#239) and is used by two tests in `SqliteStoreLogExplorerTests.cs` (lines 1604 and 1610). It stands in for the clock just like `FakeTimeProvider`, and the rule in `docs/UNIT_TESTS.md` applies to it equally. It was left out when it was added.
- **Related defect:** none found.

### Exact edit (orchestrator applies it; `.squad/project.md` is a file only the orchestrator writes)
File `/home/user/Vandox/.squad/project.md`, line 186. Replace
```
| time | Go: injected clock, `testing/synctest` (0062); C#: `FakeTimeProvider` | the clock |
```
with
```
| time | Go: injected clock, `testing/synctest` (0062); C#: `FakeTimeProvider`, `TimerSignalingClock` (Backend tests), `CountingClock` (Storage tests) | the clock |
```
The "(… tests)" suffix follows the pattern of the other C# rows.

### Acceptance criteria
- **AC1:** The *time* row of the *Test doubles* table in `.squad/project.md` names `TimerSignalingClock` with the location "(Backend tests)".
  - Verification: the Reviewer's read-only diff check against `result.md`.
- **AC2:** The same row names `CountingClock` with the location "(Storage tests)".
  - Verification: the Reviewer's read-only diff check.
- **AC3:** No other line of `.squad/project.md` changes. `scope-check.py --tier docs` and *Format check* pass. `python3 .squad/tools/config-check.py` still reports PASS with no `PROJECT_MAX_WORDS` warning (1500).
  - Verification: the orchestrator's gate run.
  - Today: PASS with no warning. The edit adds about six words, and I did not measure the exact margin.

### Other points
- **Tests and checks per edit:** none apply. The edit is documentation only; its check is AC3 and the Reviewer round.
- **Documentation updates:** only the edit above. The sweep:
  - `docs/UNIT_TESTS.md` already names `TimerSignalingClock`.
  - No area document or `docs/ARCHITECTURE.md` lists the test doubles.
  - The PR template asks for no changelog entry, since nothing changes for users.
- **Options considered:** one option. The issue asks for the *time* row, and that row is where the clock doubles belong. Leaving `CountingClock` out was the alternative the issue raises, and it loses because the rule in `docs/UNIT_TESTS.md` (lines 33–34) applies to it as well.
- **Issue author** is `OWNER`.

RESULT: DONE
