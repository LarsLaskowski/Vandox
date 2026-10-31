# Tasks: Bound how long a failing renamed log file holds back its newer lines

Plan: [plan.md](plan.md) — Status: Draft

| # | Group | Task | Files | Criteria | Depends on | Owner | Done |
| - | ----- | ---- | ----- | -------- | ---------- | ----- | ---- |
| 1 | G1 | Skeleton: `readAt`, `countRenamedRead`, `giveUpRenamed`, `renamedFile` fields `fails` and `last`, `run.renamedGivenUp`, `renamedFailsToGiveUp`, `msgRenamedGivenUp`, `SwapReadAt` | `internal/logship/files.go`, `state.go`, `run.go`, `logship.go`, `export_test.go` | AC1–AC9 | — | Dev | [x] |
| 2 | G1 | Tests first: the tests of *Tests per criterion* and the helper `failRenamedReads` | `internal/logship/files_test.go` | AC1–AC9 | 1 | Tester | [x] |
| 3 | G1 | Count, renamed part's last time, give-up and its warning (plan *Approach* 1–7) until the tests pass and the *Coverage gate* passes | `internal/logship/files.go`, `state.go`, `positions.go`, `run.go`, `logship.go` | AC1–AC9 | 2 | Dev | [ ] |
| 4 | G2 | Agent area: *Rotation*, *A file's last time*, *Gaps of the log collector*, the no-gap paragraph, *Logging* and the paragraph after it, *When a group closes*, *Residuals (record 0121)* | `docs/areas/agent.md` | AC1–AC9 | 3 | Dev | [ ] |
| 5 | G2 | *What it logs*: the new warning | `deploy/agent/README.md` | AC9 | 3 | Dev | [ ] |
| 6 | G2 | Security area 12: the new fixed text in the collector's list | `.squad/project.md` | AC9 | 3 | Dev | [ ] |
