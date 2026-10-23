# Tasks: Persistent on-disk spool (at least 7 days)

Plan: [plan.md](plan.md) — Status: Draft

Build order: the wire codec and the configuration first (the spool uses both), then the spool, then its consumers (self-metrics,
state file, agent), then the documentation. Each group runs Skeleton (Dev), tests first (Tester), implementation (Dev).

| # | Group | Task | Files | Criteria | Depends on | Owner | Done |
| - | ----- | ---- | ----- | -------- | ---------- | ----- | ---- |
| 1 | G1 | Skeleton: MarshalRecord, UnmarshalRecord | internal/wire/encode.go, internal/wire/decode.go | AC1 | — | Dev | [ ] |
| 2 | G1 | Tests: round trip of every record kind, damaged and oversized lines refused | internal/wire/encode_test.go | AC1 | 1 | Tester | [ ] |
| 3 | G1 | Implement the single-line codec on the existing marshalLine, keyCheck and decodeRecord | internal/wire/encode.go, internal/wire/decode.go | AC1 | 2 | Dev | [ ] |
| 4 | G2 | Skeleton: Spool fields MaxSize, MinFree, MaxBytes, MinFreeBytes; parseSize | internal/config/agent.go, internal/config/config.go | AC16 | — | Dev | [ ] |
| 5 | G2 | Tests: defaults, valid values, every E1 value with its exact text; adapt TestAgentKeys and TestDefaultAgent | internal/config/agent_test.go | AC16 | 4 | Tester | [ ] |
| 6 | G2 | Implement defaults (2GiB, 1GiB), AgentKeys, validation | internal/config/agent.go, internal/config/config.go | AC16 | 5 | Dev | [ ] |
| 7 | G3 | Skeleton: Spool, Options, Batch, MaxAckRanges, ErrTooManyRanges, Open, Put, Read, Ack, LastSeq, Sync, Close, SpoolBytes, OldestCapture, ClockBound, writeFile seam | internal/spool/spool.go, internal/spool/segment.go, internal/spool/meta.go, internal/spool/export_test.go | AC1–AC14, AC17, AC18 | 3, 6 | Dev | [ ] |
| 8 | G3 | Tests first, incl. the hard-kill helper, the reference stream generator, the memory bound and BenchmarkOpen_Full; report facts 14–16 and 18 | internal/spool/spool_test.go, internal/spool/segment_test.go, internal/spool/meta_test.go | AC1–AC14, AC17, AC18 | 7 | Tester | [ ] |
| 9 | G3 | Implement the spool: files and guards, segments and trailer, meta, open and cut, numbers and clock bound, Put with drops and episodes, Read, Ack, sync loop, Close; run BenchmarkOpen_Full (fact 17) | internal/spool/spool.go, internal/spool/segment.go, internal/spool/meta.go | AC1–AC14, AC17, AC18 | 8 | Dev | [ ] |
| 10 | G4 | Skeleton: SpoolAgeReporter, SpoolAge, MetricSpoolAge | internal/selfstats/selfstats.go | AC15 | — | Dev | [ ] |
| 11 | G4 | Tests: age, 0 when empty or in the future, provider error fails the sample | internal/selfstats/selfstats_test.go | AC15 | 10 | Tester | [ ] |
| 12 | G4 | Implement the age metric | internal/selfstats/selfstats.go | AC15 | 11 | Dev | [ ] |
| 13 | G5 | Skeleton: SetBarrier | internal/agentstate/agentstate.go | AC4, AC6 | — | Dev | [ ] |
| 14 | G5 | Tests: barrier before every save (job and Flush), failing barrier skips the save with the fixed text | internal/agentstate/agentstate_test.go | AC4, AC6 | 13 | Tester | [ ] |
| 15 | G5 | Implement the barrier | internal/agentstate/agentstate.go | AC4, AC6 | 14 | Dev | [ ] |
| 16 | G6 | Skeleton: openStorage, jobs, newScheduler, closeSpool | internal/agent/agent.go | AC1, AC4, AC13 | 9, 12, 15 | Dev | [ ] |
| 17 | G6 | Tests: records reach the spool, stop order, close give-up, start failures, first start without warning; adapt the jobs and newScheduler call sites | internal/agent/agent_test.go | AC1, AC4, AC13 | 16 | Tester | [ ] |
| 18 | G6 | Implement the wiring and stop order; DiscardSink doc | internal/agent/agent.go, internal/scheduler/sink.go | AC1, AC4, AC13 | 17 | Dev | [ ] |
| 19 | G7 | no new API: README option rows for spool.max_size and spool.min_free, the directory row | README.md | AC16 | 6 | Dev | [ ] |
| 20 | G7 | Example keys spool.max_size and spool.min_free with comments; the directory's contents and the agent's writes | deploy/agent/agent.yaml, deploy/agent/README.md | AC16, AC13 | 6, 18 | Dev | [ ] |
| 21 | G7 | Security area 9: the spool's files, guards and functions; Layout package list gains spool | .squad/project.md, .squad/stack.md | AC10, AC11 | 9 | Dev | [ ] |
| 22 | G7 | Benchmark *Spool open* with host and figures | docs/BENCHMARKS.md | AC17 | 9 | Dev | [ ] |
| 23 | G8 | Agent area: *Spool and sending* rules, the reference stream's measured bytes per day, and every section the plan names | docs/areas/agent.md | AC1–AC15, AC17, AC18 | 18 | Lead | [ ] |
| 24 | G8 | Options spool.max_size and spool.min_free | docs/areas/configuration-and-secrets.md | AC16 | 6 | Lead | [ ] |
| 25 | G8 | Metric agent_spool_age_seconds; *Batch identity* | docs/areas/wire-format.md | AC14, AC15 | 9 | Lead | [ ] |
| 26 | G8 | Status line and the only-file-write statement, record 0123 linked | docs/ARCHITECTURE.md | AC1 | 18 | Lead | [ ] |
