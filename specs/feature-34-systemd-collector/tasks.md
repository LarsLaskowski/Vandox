# Tasks: systemd service collector

Plan: [plan.md](plan.md) — Status: Draft

| # | Task | Files | Criteria | Depends on | Owner | Done |
| - | ---- | ---- | -------- | ---------- | ----- | ---- |
| 1 | Option `services.units`: type, default, suffix and the E1 rules, element lines in the decoder, `AgentKeys` | `internal/config/agent.go`, `internal/config/decode.go`, `internal/config/agent_test.go` | AC1, AC2 | — | Tester (tests), Dev (code) | [ ] |
| 2 | `Run.PreviousLost` set by every kind of lost sample | `internal/scheduler/scheduler.go`, `internal/scheduler/runner.go`, `internal/scheduler/runner_test.go` | AC8 | — | Tester (tests), Dev (code) | [ ] |
| 3 | Capture reader and fake bus helpers over `testdata/systemd/capture-systemd249.pcap` | `internal/sdbus/capture_test.go`, `internal/sdbus/fakebus_test.go` | AC3, AC7, AC11 | — | Tester | [ ] |
| 4 | Minimal D-Bus client: dial, auth, `Hello`, the four calls, bounded reader, skipping of unsolicited messages; the scratch `dbus-daemon` checks of plan rows 15 and 16 | `internal/sdbus/sdbus.go`, `internal/sdbus/message.go`, `internal/sdbus/sdbus_test.go` | AC3, AC7, AC11 | 3 | Tester (tests), Dev (code, measurements) | [ ] |
| 5 | Collector `systemd`: tracking, patterns and cap, change, lost-sample and hourly records, missing-unit warning, refused units as own gap, logs | `internal/svcstats/svcstats.go`, `internal/svcstats/svcstats_test.go` | AC3, AC4, AC5, AC6, AC7, AC8, AC9, AC11 | 2, 4 | Tester (tests), Dev (code) | [ ] |
| 6 | Agent wiring: `Options.Services`, schedule constants, job after `connections`; adapt `TestJobs_Schedules` and `TestRun_*` to the fake | `internal/agent/agent.go`, `internal/agent/agent_test.go` | AC1, AC10 | 1, 5 | Tester (tests), Dev (code) | [ ] |
| 7 | Example configuration and install notes | `deploy/agent/agent.yaml`, `deploy/agent/README.md`, `README.md` | AC1 | 1 | Dev | [ ] |
| 8 | Area documents, architecture, project and stack files, unit-test guide (plan, *Documentation updates*) | `docs/areas/agent.md`, `docs/areas/configuration-and-secrets.md`, `docs/areas/wire-format.md`, `docs/ARCHITECTURE.md`, `.squad/project.md`, `.squad/stack.md`, `docs/UNIT_TESTS.md` | AC1, AC5, AC6, AC7, AC11 | 5, 6 | Dev | [ ] |
