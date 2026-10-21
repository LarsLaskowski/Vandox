# Log: feature 35-mariadb-collector

One line per pipeline step: date and time of day (UTC), step, member, result. Lead decisions and escalations
are quoted in full.

| Date | Step | Member | Result |
| ---- | ---- | ------ | ------ |
| 2026-10-20 20:39 | 1 Intake | Orchestrator | feature: user request (issue #35, label type: feature); branch feature-35-mariadb-collector off main |
| 2026-10-20 20:48 | 2a Spec | squad-lead | DONE: spec.md, 11 scenarios, 15 criteria, plan-check --spec PASS (opus/medium · 180,381 tokens · 33 tool uses · 493 s) |
| 2026-10-20 20:51 | 2a Spec challenge | squad-devils-advocate | OBJECTIONS 3 major (M1 refused vs record 0043, M2 pre-greeting error packet, M3 protocol client/capture), 4 minor (opus/medium · 69,988 tokens · 21 tool uses · 209 s) |
| 2026-10-20 20:57 | 2a Spec revise | squad-lead | ESCALATE: source of MariaDB protocol sample; all 7 objections accepted, plan-check --spec PASS (opus/medium · 28,102 tokens · 27 tool uses · 124 s · continued) |
| 2026-10-20 20:57 | 2a Escalation | Product Manager | Protocol sample: (a) the user captures it on MariaDB 10.6 / Ubuntu 22.04; plan waits for the capture |
| 2026-10-21 05:27 | 2b Escalation follow-up | Orchestrator | Capture 1 received: server MariaDB 12.3.3 (ubu2204), but client switched to TLS after greeting, so the rest is encrypted and unusable; user asked to repeat with --skip-ssl |
| 2026-10-21 05:35 | 2b Escalation answer | Product Manager | Captures supplied (MariaDB 12.3.3-ubu2204, plaintext, --skip-ssl): status+variables+processlist, account check; hostname anonymised same-length; no captures of error cases 1698/1045/1040 available. Stored in testdata/mariadb/ |
| 2026-10-21 05:50 | 2b Plan | squad-lead | DONE: plan.md, tasks.md (7 groups), record 0118 Proposed; tier security; areas 6,7,10,11,12; spec closed (opus/medium · 139,852 tokens · 53 tool uses · 884 s · continued) |
| 2026-10-21 05:50 | 2b Capture fix | Orchestrator | Capture: replaced leaked file-name prefix 144 and server_uid by same-length placeholders (hex+text); remaining 144 are numeric values |
| 2026-10-21 05:55 | 3 Plan security review | squad-security | CHANGES_REQUIRED: B1 memory bound (short text fields x 4096 threads encoded via json.Marshal, duplicate-name memory, tests); 6 non-blocking (opus/medium · 107,771 tokens · 17 tool uses · 257 s) |
| 2026-10-21 05:56 | 2b Plan challenge | squad-devils-advocate | OBJECTIONS 2 major (M1 capability bits drop extended metadata, M2 unknown user may answer with auth switch), 5 minor (opus/medium · 94,913 tokens · 23 tool uses · 319 s) |
| 2026-10-21 06:00 | 2b Plan revise (challenge + security) | squad-lead | DONE: M1,M2,m1,m3-m5, B1 + notes accepted; m2 rejected (bytes read little-endian = 0x00100000); plan-check, decision-check PASS; plan.md 5231 words (soft limit warning) (opus/medium · 46,451 tokens · 12 tool uses · 222 s · continued) |
| 2026-10-21 06:01 | 3 Plan security delta | squad-security | APPROVED; one non-blocking note leaves the plan a choice (account check re-run) -> Lead decides (opus/medium · 22,558 tokens · 5 tool uses · 66 s · continued) |
| 2026-10-21 06:02 | 2b Plan revise (security note) | squad-lead | DONE: account check runs on every up run; plan-check, decision-check PASS (opus/medium · 4,308 tokens · 3 tool uses · 26 s · continued) |
| 2026-10-21 06:02 | 2b Plan done | Orchestrator | Plan approved by Security and Lead; plan.md >4000 words soft warning only |
| 2026-10-21 06:07 | 4 Skeleton | squad-dev | DONE: internal/mariadb stubs, model ErrorCode (Go, C#), config MariaDB, exported readers, agent job entry; Format+Build pass (sonnet/medium · 91,118 tokens · 19 tool uses · 316 s) |
| 2026-10-21 06:09 | 5 Tests first G1 (capture check) | squad-tester | DONE: both captures clean, nothing edited (sonnet/medium · 75,425 tokens · 11 tool uses · 73 s) |
| 2026-10-21 06:13 | 5 Tests first G2 | squad-tester | DONE: AC6 tests Go+C#, golden regenerated, shared cases S33-S38; refusal cases fail on skeleton (golden/pins deferred) (sonnet/medium · 120,374 tokens · 19 tool uses · 236 s) |
| 2026-10-21 06:15 | 6 Implement G2 | squad-dev | DONE: error_code checks Go+C#; deviation: NullAsAbsentConverterFactory gets ushort; model/wire/Core tests green (sonnet/medium · 71,029 tokens · 17 tool uses · 120 s) |
| 2026-10-21 06:18 | 5 Tests first G3 | squad-tester | DONE: config, MemTotal, TCPListeners tests; red on skeleton; TestLoadAgent_Example red until task 11 (sonnet/medium · 126,013 tokens · 19 tool uses · 127 s) |
| 2026-10-21 06:20 | 6 Implement G3 | squad-dev | DONE tasks 4,5; 2 test-helper failures (newline/tab socket cases expect the rule text but the shared decoder refuses control chars first) -> back to Tester (sonnet/medium · 92,281 tokens · 10 tool uses · 117 s) |
| 2026-10-21 06:20 | 6 G3 fix round (test helper) | squad-tester | DONE: badSocket.reason covers control chars; only TestLoadAgent_Example red until task 11 (sonnet/medium · 1,341 tokens · 2 tool uses · 8 s · continued from 5 Tests first G3) |
| 2026-10-21 06:34 | 5 Tests first G4 | squad-tester | DONE: capture reader, fake server, client/packet tests; red on skeleton; wrong-reason passes listed; imports test deferred to coverage mode; fact 8 correction (Sleep row db=psa, own row Info=SHOW FULL PROCESSLIST) (sonnet/medium · 245,884 tokens · 31 tool uses · 793 s) |
| 2026-10-21 06:43 | 6 Implement G4 | squad-dev | DONE: client/packet/statement; all G4 tests green except TestUserSQL (task 11); deviation: 0x01 login answer >2 bytes = continuation (authentication switch refused), shorter = unexpected reply; ping/query close the connection on failure (sonnet/medium · 179,882 tokens · 30 tool uses · 844 s) |
| 2026-10-21 07:06 | 5 Tests first G5 | squad-tester | DONE: collector/sample/derived tests (53), 51 red on stubs for the intended reason; ambiguities resolved listed (7 items), imports test not written (sonnet/medium · 376,306 tokens · 61 tool uses · 1359 s) |
| 2026-10-21 07:20 | 6 Implement G5 | squad-dev | DONE task 9; 5 collector tests red from 2 fixture defects (meminfoFile format has no space after colon; rig keeps Uptime constant while clock moves, contradicting E11) -> Tester (sonnet/medium · 261,943 tokens · 37 tool uses · 839 s) |
| 2026-10-21 07:23 | 6 G5 fix round (fixtures) | squad-tester | DONE: meminfo format and Uptime-follows-clock fixtures; only TestUserSQL red (sonnet/medium · 13,314 tokens · 5 tool uses · 112 s · continued from 5 Tests first G5) |
| 2026-10-21 07:24 | 5 Tests first G6 | squad-tester | DONE: job-order test (deferred: passes on skeleton), TestJobs_Schedules adapted, project.md Test doubles row; TestUserSQL red until task 11 (sonnet/medium · 74,141 tokens · 6 tool uses · 40 s) |
| 2026-10-21 07:26 | 6 Implement G6 | squad-dev | DONE task 11: mariadb-user.sql, agent.yaml, README options, deploy README section, project.md areas; TestUserSQL, TestLoadAgent_Example green (sonnet/medium · 104,069 tokens · 17 tool uses · 115 s) |
| 2026-10-21 07:33 | 6 Documentation G7 | squad-lead | DONE task 13: area docs, ARCHITECTURE, record 0118 as built; deviations for approve-pr: 0x01 handling, account-check error drops process list (E17), code 0; Dev passages in project.md: missing imports test, two minor wording items (opus/medium · 239,447 tokens · 60 tool uses · 409 s) |
| 2026-10-21 07:34 | 6 Doc fix project.md | squad-dev | DONE: area 10 socket sentence, area 12 log-line list (10 texts verified against code) (sonnet/medium · 59,059 tokens · 9 tool uses · 34 s) |
| 2026-10-21 07:43 | 6 Coverage pass | squad-tester | DONE: imports test (AC13), guard tests; Go coverage internal/mariadb 95.9%, changed Go lines 94.3% (tester's script) (sonnet/medium · 109,167 tokens · 36 tool uses · 566 s) |
| 2026-10-21 07:52 | 7 Code check | squad-code-officer | DONE: QF1012, revive, G115 (guard in MemTotal, judgement call); 2 gocognit findings (test structure) -> Tester (haiku/medium · 67,502 tokens · 11 tool uses · 146 s) |
| 2026-10-21 07:54 | 7 Code check: test complexity | squad-tester | DONE: gocognit findings fixed (helpers, 4 tests); MemTotal guard uncoverable -> use existing kBToBytes (sonnet/medium · 71,595 tokens · 11 tool uses · 57 s) |
