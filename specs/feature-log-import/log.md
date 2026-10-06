# Log: Feature #15 – Import framework for log archives

One line per pipeline step: date, step, member, result. Lead decisions and escalations are quoted in full.

| Date | Step | Member | Result |
| ---- | ---- | ------ | ------ |
| 2026-10-06 | 1 Intake | Orchestrator | Issue #15 read (open, depends on #14 – merged as #130); branch ccr-a890ca78-cai7xm prescribed by session |
| 2026-10-06 | 2 Plan | squad-lead | DONE: tier security; spec/plan/tasks + decisions 0069-0072 (0072 supersedes 0058); follow-up issue to create: grown log files |
| 2026-10-06 | 2 Plan challenge | squad-devils-advocate | OBJECTIONS 0 major, 6 minor (resume vs File name/mtime; pass-2 reads past pass-1 size; docker exec -it and readability; no progress during pass 1; byte bound with empty batch; decision index) |
| 2026-10-06 | 2 Plan revise | squad-lead | DONE: 6/6 minor objections accepted (stored File for resume, pass 2 limited to pass-1 size, docker exec -it and readability, EventScanProgress, byte bound never flushes an empty batch, index scheduled for approve-pr); tier security unchanged |
| 2026-10-06 | 3 Plan security review (round 1) | squad-security | CHANGES_REQUIRED: B1 MaxFiles must stop scan early; B2 stderr JSON slog leaks U+009B/U+202E, quote or escape names; B3 README must chown to 65532 not a+rX; non-blocking N1-N4 |
| 2026-10-06 | 3 Plan revise (security round 1) | squad-lead | DONE: B1 MaxFiles enforced while scanning (AC-I7 a-c, AC-I14); B2 input-derived slog attributes strconv.Quote-d (AC-C2/AC-C5); B3 README grants read to 65532 only; N1 os.Root for all opens; N2 ErrInsecurePath+header treated as header; N3 gzip bomb fills storage documented; N4 documented in 0069; tier security unchanged |
| 2026-10-06 | 3 Plan security review (round 2) | squad-security | CHANGES_REQUIRED: B1-B3, N1-N4 resolved, os.Root design sound; one new blocking wording defect: plan.md AC-C2 (line ~198) contains a literal U+202E where the escaped text U+202E was meant |
| 2026-10-06 | 3 Plan decide | squad-lead | DECIDED: wording defect in AC-C2 (raw rune, plan.md:198) accepted and fixed with escaped text; scan of plan files and records 0069-0072 finds no other Cc/Cf/Zl/Zp characters; one security delta confirmation follows |
| 2026-10-06 | 3 Plan security delta | squad-security | APPROVED (AC-C2 wording fixed, no other change) |
| 2026-10-06 | 4 Skeleton | squad-dev | DONE: all new types/functions stubbed, build and vet green, no migration step 3 |
| 2026-10-06 | 5 Tests first | squad-tester | DONE: tasks 2-5, AC-P1..C6 covered; vet green; 140 new/updated tests fail on stubs, no other test fails; AC-I2 unreadable-file test skipped as root; summary text and log attribute names (outcome, bytes) chosen by tester, only content is asserted |
| 2026-10-06 | 6 Implement | squad-dev | DONE tasks 6-10; coverage new/changed 89.8%, overall 93.0%; 5 tests fail from a Tester helper bug (tarHeader sets Mode/ModTime on TypeXGlobalHeader); deviations: usage text lists import, openAndImport name, project.md area 12 note |
| 2026-10-06 | 5/6 Fix + coverage | squad-tester | tarHeader helper fixed (TypeXGlobalHeader); full suite green with -race; coverage gate PASS (new 90.2%, overall 93.1%); coverage pass skipped, gate already passes |
| 2026-10-06 | 7 Code check | squad-code-officer | DONE: gofmt on 4 files only; format, analyzer gate, tests -race, coverage gate verified by orchestrator (new 90.2%, overall 93.1%) |
| 2026-10-06 | 8 Security diff review (round 1) | squad-security | CHANGES_REQUIRED: B1 tar entry names over MaxPathBytes kept uncut in found (scan.go 364-374; ~1 MiB per entry, 288 MiB measured for 300 entries), needs cut name plus heap-bound test; non-blocking N1 root open race (Lstat then OpenRoot), N2 file root symlink to sibling |
| 2026-10-06 | 8 Fix round 1 | squad-tester/squad-dev | Tester added TestScan_MemoryStaysBoundedForHugeEntryNames (failed, 288 MiB); Dev fixed scan.go (cutPath cloned, found name cut); suite -race green, coverage 90.2%/93.1%; N1/N2 not changed, go to Lead; reviewer failed twice with API 529, rerun after fix |
