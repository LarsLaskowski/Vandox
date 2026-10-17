# Tasks: System metrics collector including PSI

Plan: [plan.md](plan.md) — Status: Draft

Every documentation edit appears in exactly one row; the Lead's approval only touches status, index rows and bookkeeping.

| # | Task | Files | Tests (AC) | Owner | Done |
| - | ---- | ----- | ---------- | ----- | ---- |
| 1 | Capture the real sample trees from the session's kernel as the plan's *Real samples* table says (verbatim copies; `self/mountinfo` reduced to the named lines, unchanged) | `testdata/proc/{stat,loadavg,meminfo,vmstat}`, `testdata/proc/pressure/{memory,cpu,io}`, `testdata/proc/self/mountinfo`, `testdata/sys/block/vda/{stat,device/uevent}`, `testdata/sys/block/loop0/stat` | AC1, AC6, AC7, AC12 | Tester | [ ] |
| 2 | Skeleton: package `internal/sysstats` with the signatures of the plan; `config.Filesystem`, `MaxMountPoints`, `MaxMountPointBytes`, `checkMountPoints`, `AgentKeys`; `agent` constants, `Options.System`, `jobs`, `newScheduler(opts, cfg, log)` | `internal/sysstats/*.go`, `internal/config/agent.go`, `internal/agent/agent.go` | — | Dev | [ ] |
| 3 | Tests first for AC1–AC12 (they compile and fail on the skeleton); add `System: fixtureSystem()` to the existing `startAgent` calls; adapt `TestAgentKeys` and `TestLoadAgent_Example` | `internal/sysstats/*_test.go`, `internal/config/agent_test.go`, `internal/config/decode_test.go`, `internal/agent/agent_test.go` | AC1–AC12 | Tester | [ ] |
| 4 | `system` collector: CPU, load, memory, vmstat, pressure (availability logging), disks, rates and state | `internal/sysstats/{sysstats,system,cpu,memory,pressure,disk}.go` | AC1–AC6, AC9, AC10 | Dev | [ ] |
| 5 | `filesystem` collector with the mount table reader and `StatFS`; `clock` collector with `ReadClock` | `internal/sysstats/{filesystem,mountinfo,clock}.go` | AC7, AC8, AC9, AC10 | Dev | [ ] |
| 6 | Option `filesystem.mount_points` with its rule and error text; null list elements refused in `setOther` | `internal/config/agent.go`, `internal/config/decode.go` | AC11 | Dev | [ ] |
| 7 | Agent wiring of the four jobs | `internal/agent/agent.go` | AC12 | Dev | [ ] |
| 8 | Example file and README row for the option | `deploy/agent/agent.yaml`, `README.md` | AC11 (`TestLoadAgent_Example`) | Dev | [ ] |
| 9 | Configuration area: option row and rule paragraph with the exact error text; *Configuration file* bullet on list elements with no value | `docs/areas/configuration-and-secrets.md` | — | Dev | [ ] |
| 10 | Architecture status sentence, `internal/` list, link to 0111 | `docs/ARCHITECTURE.md` | — | Dev | [ ] |
| 11 | Project knowledge: security areas 6 and 10, *Test doubles* rows (`/proc`, `/sys`, new *statfs and adjtimex*), *Layout* | `.squad/project.md`, `.squad/stack.md` | — | Dev | [ ] |
| 12 | Coverage pass: tests only for lines the *Coverage gate* reports uncovered | `internal/sysstats/*_test.go`, `internal/config/*_test.go` | — | Tester | [ ] |
| 13 | Agent area (*System metrics*, *Rights*, status, related decisions, implementation) and wire format *Metric names*; record 0111 and its index row | `docs/areas/agent.md`, `docs/areas/wire-format.md`, `docs/decisions/0111-…`, `docs/decisions/README.md` | — | Lead | [x] |
