# Tasks: Agent skeleton and collector scheduler

Plan: [plan.md](plan.md) — Status: Draft

Every documentation edit appears in exactly one row; the Lead's approval only touches status, index rows and bookkeeping.

| # | Task | Files | Tests (AC) | Owner | Done |
| - | ---- | ----- | ---------- | ----- | ---- |
| 1 | Area documents, architecture, decision record 0110 and its index row (written with the plan) | `docs/areas/agent.md`, `docs/areas/wire-format.md`, `docs/areas/configuration-and-secrets.md`, `docs/ARCHITECTURE.md`, `docs/decisions/0110-…md`, `docs/decisions/README.md` | — | Lead | [x] |
| 2 | Skeleton: every signature of *Signatures*; `cli.go` and `main.go` rewritten; mechanical adaptation of the `cli.Run` call sites in `internal/cli/cli_test.go` and the `run` calls in `cmd/vandox-agent/main_test.go` (no assertion touched); *Build* passes | `internal/model/model.go`, `internal/scheduler/{scheduler,runner,sink}.go`, `internal/selfstats/selfstats.go`, `internal/sdnotify/sdnotify.go`, `internal/agent/{agent,logger,watchdog}.go`, `internal/cli/cli.go`, `cmd/vandox-agent/main.go`, `internal/cli/cli_test.go`, `cmd/vandox-agent/main_test.go` | — | Dev | [ ] |
| 3 | Real sample: capture `testdata/proc/self/status` from a running process (`cat /proc/self/status`), check it holds no secret | `testdata/proc/self/status` | AC16, AC18, AC28 | Tester | [ ] |
| 4 | Tests first for the scheduler, sink and `ValidName` | `internal/scheduler/{scheduler,runner,sink}_test.go`, `internal/model/model_test.go` | AC1–AC15 | Tester | [ ] |
| 5 | Tests first for the self-metrics | `internal/selfstats/selfstats_test.go` | AC16–AC20 | Tester | [ ] |
| 6 | Tests first for the notification | `internal/sdnotify/sdnotify_test.go` | AC21–AC23 | Tester | [ ] |
| 7 | Tests first for the agent (watchdog loop, logger, end to end) | `internal/agent/{agent,logger,watchdog}_test.go` | AC24–AC31 | Tester | [ ] |
| 8 | Tests first for the command line (rewrite of `cli_test.go`, new test in `main_test.go`) | `internal/cli/cli_test.go`, `cmd/vandox-agent/main_test.go` | AC32–AC34 | Tester | [ ] |
| 9 | Implement the scheduler (runner loop split below gocognit 15), sink, `ValidName` | `internal/scheduler/*.go`, `internal/model/model.go` | AC1–AC15 | Dev | [ ] |
| 10 | Implement the self-metrics | `internal/selfstats/selfstats.go` | AC16–AC20 | Dev | [ ] |
| 11 | Implement the notification; scratch measurement of the full-buffer write deadline, result into `log.md` (via the orchestrator) | `internal/sdnotify/sdnotify.go` | AC21–AC23 | Dev | [ ] |
| 12 | Implement the agent, logger, watchdog loop, command line and `main` | `internal/agent/*.go`, `internal/cli/cli.go`, `cmd/vandox-agent/main.go` | AC24–AC34 | Dev | [ ] |
| 13 | Coverage pass until the *Coverage gate* passes | test files above | — | Tester | [ ] |
| 14 | README: *Agent* paragraph, binaries table, the loader sentence of the options section | `README.md` | — | Dev | [ ] |
| 15 | CONTRIBUTING: the agent's line in the quick start | `docs/CONTRIBUTING.md` | — | Dev | [ ] |
| 16 | Example configuration header comment (comment only) | `deploy/agent/agent.yaml` | `TestLoadAgent_Example` stays green | Dev | [ ] |
| 17 | `.squad/project.md` *Security areas* 8, 10, 11, 12 and the *Guarantees* pointer, as listed in the plan | `.squad/project.md` (*Security areas*, *Guarantees*) | — | Dev | [ ] |
| 18 | `.squad/project.md` *Test doubles* rows (`/proc`, notification socket, scheduler doubles, time) | `.squad/project.md` (*Test doubles*) | — | Tester | [ ] |
| 19 | `docs/UNIT_TESTS.md`: the unix datagram socket as the notification socket's double | `docs/UNIT_TESTS.md` | — | Tester | [ ] |
