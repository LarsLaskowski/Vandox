# Tasks: systemd service collector

Plan: [plan.md](plan.md) — Status: Draft (revised with the plan)

| # | Task | Files | Criteria | Depends on | Owner | Done |
| - | ---- | ---- | -------- | ---------- | ----- | ---- |
| 1 | Option `services.units`: type, default, suffix and the E1 rules, element lines in the decoder, `AgentKeys` | `internal/config/agent.go`, `internal/config/decode.go`, `internal/config/agent_test.go` | AC1, AC2 | — | Tester (tests), Dev (code) | [x] |
| 2 | `Run.PreviousLost` set by every kind of lost sample | `internal/scheduler/scheduler.go`, `internal/scheduler/runner.go`, `internal/scheduler/runner_test.go` | AC8 | — | Tester (tests), Dev (code) | [x] |
| 3 | Capture reader and fake bus helpers over `testdata/systemd/capture-systemd249.pcap` and the measured auth and `Hello` bytes of plan row 16 (stored as `testdata/systemd/hello-dbus-1.14.hex`) | `internal/sdbus/capture_test.go`, `internal/sdbus/fakebus_test.go`, `testdata/systemd/hello-dbus-1.14.hex` | AC3, AC7, AC11 | — | Tester | [x] |
| 4 | Minimal D-Bus client: dial, auth, `Hello`, `ListUnitsByPatterns`, `ListUnitsByNames` in chunks of 64, the two `Get`s, `NO_AUTO_START`, bounded reader with fixed header-field types, skipping of every unsolicited or unknown message, only `net.Conn` methods | `internal/sdbus/sdbus.go`, `internal/sdbus/message.go`, `internal/sdbus/sdbus_test.go` | AC3, AC7, AC11 | 3 | Tester (tests), Dev (code) | [x] |
| 5 | Collector `systemd`: tracking, patterns and cap, change, lost-sample and hourly records, missing-unit warning, refused units as own gap, logs | `internal/svcstats/svcstats.go`, `internal/svcstats/svcstats_test.go` | AC3, AC4, AC5, AC6, AC7, AC8, AC9, AC11 | 2, 4 | Tester (tests), Dev (code) | [ ] |
| 6 | Agent wiring: `Options.Services`, schedule constants, job after `connections`; adapt `TestJobs_Schedules` and `TestRun_*` to the fake | `internal/agent/agent.go`, `internal/agent/agent_test.go` | AC1, AC10 | 1, 5 | Tester (tests), Dev (code) | [ ] |
| 7 | Example configuration and install notes | `deploy/agent/agent.yaml`, `deploy/agent/README.md`, `README.md` | AC1 | 1 | Dev | [ ] |
| 8 | Area documents, architecture, project and stack files, unit-test guide (plan, *Documentation updates*) | `docs/areas/agent.md`, `docs/areas/configuration-and-secrets.md`, `docs/areas/wire-format.md`, `docs/ARCHITECTURE.md`, `.squad/project.md`, `.squad/stack.md`, `docs/UNIT_TESTS.md` | AC1, AC5, AC6, AC7, AC11 | 5, 6 | Dev | [ ] |
