# Tasks: Retire the legacy monitoring script and document v0.2.0

Plan: [plan.md](plan.md)

| # | Group | Task | Files | Criteria | Depends on | Owner | Done |
| - | ----- | ---- | ----- | -------- | ---------- | ----- | ---- |
| 1 | G1 | Skeleton: no new API | — | AC2, AC3, AC4 | — | Dev | [ ] |
| 2 | G1 | Tests of the guide | `deploy/agent/retire_guide_test.go` | AC2, AC3 | 1 | Tester | [ ] |
| 3 | G1 | Release document tests; adapt the two Tailscale tests | `deploy/agent/release_docs_test.go`, `deploy/agent/tailscale_readme_test.go` | AC4 | 1 | Tester | [ ] |
| 4 | G1 | Documentation updates of the plan: guide, INSTALL.md, README.md, `deploy/agent/README.md`, backend-host.md, CHANGELOG.md | `docs/RETIRE_LEGACY_MONITORING.md`, `docs/INSTALL.md`, `README.md`, `deploy/agent/README.md`, `docs/areas/backend-host.md`, `CHANGELOG.md` | AC1, AC2, AC3, AC4 | 2, 3 | Dev | [ ] |
| 5 | PM | Dry run of S2 from the branch; confirm fact 11 | — | AC3 | 4 | Product Manager | [ ] |
