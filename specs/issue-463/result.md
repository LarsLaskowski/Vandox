# Result: issue 463

RESULT: DONE, tier `docs`. Security areas touched: none.

Only `.squad/project.md` changes (documentation tier: it leaves *Security areas* and *Guarantees* alone). Word count 1536 -> 1471 (limit 1500, `config-check.py`). CRLF line endings kept.

Edits:
- E1 introduction: shortened (agent/backend stack sentence, template-managed sentence).
- E2 *Test doubles*, row "retention store (C#)": `ScriptedRetentionStore`, `AgentRecords` (Backend tests) | `IRetentionStore`, agent records. Behavior stays in the class doc comment of `tests/Vandox.Backend.Tests/ScriptedRetentionStore.cs`.
- E3 *Integration surface*, wire format: dropped `VANDOX_UPDATE_GOLDEN=1 go test ./internal/wire` (stated in `docs/areas/wire-format.md:328`).
- E4 *Integration surface*, decision records: dropped the list "among them 0037, ..." (the grep beside it finds them).

Acceptance criteria:
- AC1: `config-check.py` prints no `PROJECT_MAX_WORDS` warning for `.squad/project.md`, ends with PASS.
- AC2: `git diff origin/main -- .squad/project.md` changes no line under `## Security areas` or `## Guarantees`.
- AC3: every removed fact is still stated where listed above; every name kept in E2 exists.
- AC4: `scope-check.py --tier docs` and the format check pass; the file keeps CRLF.

Not in scope: entries 9 and 11 of *Security areas* still hold behavior clauses; shortening them is a separate change.
