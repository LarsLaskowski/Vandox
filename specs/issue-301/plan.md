# Plan: Harden the spool's free-space check and size accounting

Source: Issue #301
Status: Draft
Tier: security — the free-space check is moved from a path lookup onto the directory descriptor, extending the spool's
descriptor-only file access rule (a guard of security area 9 against a path swapped after `Open`), and the size bound's sum is made
to fail closed against planted files; the issue asks for Security's diff review.
Security areas touched: 9 (agent file access: `internal/spool`)

## Problem / root cause

1. `internal/spool/spool.go:198-201` `statAvailable(dir string)` calls `syscall.Statfs(dir, …)` on `s.opts.Dir`
   (`spool.go:194`), the configured path. Every other spool operation goes through `dirFS.fd` (`fsys.go:120-124`, opened in
   `openDir`, `fsys.go:127-150`). If the path is renamed away or replaced by another directory or mount after `Open`, the
   `spool.min_free` check (`put.go:195-206`) reads another file system, or fails with `ENOENT` and refuses every hand-over, while the
   spool goes on writing into the directory it holds.
2. `spool.go:179-185` `diskUsed` sums `segFile.size` with plain `+=`. A size found at `Open` is `st.Size` of a trusted file
   (`open.go:249`), unbounded; a sealed file passes `quick` (`segment.go:604-628`) on its header, trailer and first record, whatever
   its size, and is kept (`settle.go:72-75`). Two planted sparse files near 2^62 bytes each make the sum wrap negative, so
   `shortage` (`put.go:192`, `s.diskUsed()+metaSlack+need > s.opts.MaxBytes`) passes and the size bound no longer drops anything.
   Related defect found on the way: even a single file whose size is near `math.MaxInt64` overflows `diskUsed()+metaSlack+need` in
   `put.go:192` and `diskUsed() + metaSize` in `SpoolBytes` (`spool.go:175`), which then reports a negative `agent_spool_bytes`.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | ------ | ------ |
| 1 | Issue: `statAvailable` calls `Statfs` on the configured path | `spool.go:194` passes `s.opts.Dir`, `spool.go:200` `syscall.Statfs(dir, &st)` | confirmed |
| 2 | Issue: the directory descriptor is `dirFS.fd` and stays open for the spool's life | `fsys.go:120-124`, `Open` sets `s.dir` (`open.go:109-113`) before `recoverState`; closed only in `abandon`/`Close` | confirmed |
| 3 | `syscall.Fstatfs(fd int, buf *Statfs_t) error` exists on linux/amd64, the only build target | `go doc syscall.Fstatfs` (go1.27.0); CI and release build `GOOS=linux GOARCH=amd64` (`ci.yml:213`, `release.yml:120`) | confirmed |
| 4 | The agent unit's system-call filter allows `fstatfs` | `deploy/agent/vandox-agent.service:44` `@system-service`; `systemd-analyze syscall-filter @system-service` lists `@file-system`, which lists `fstatfs` and `fstatfs64` (systemd 255) | confirmed |
| 5 | Issue: `Options.Available` is a seam called with the directory path, and a test asserts the path | `spool.go:66-67`, `spool.go:190-191`; `limits_test.go:526` expects `r.dir()` | confirmed |
| 6 | Issue: `cover_test.go` has the `statAvailable` cases | `cover_test.go:17-36`: temp dir, missing path, default without injection | confirmed |
| 7 | Issue: `diskUsed` sums trusted sizes without an upper bound | `spool.go:179-185`; sizes from `st.Size` (`open.go:249`), from writes (`put.go:433`), cuts (`settle.go:113,132`) and trailers (`settle.go:145`) | confirmed |
| 8 | Issue: the quick start-up path accepts a sealed data file of any size | `segment.go:607` checks only a lower size bound (`size-hdr < frameHeadLen+minPayload+trailerLen`) | confirmed |
| 9 | A legitimate data file is bounded only by the `spool.max_size` of the run that wrote it: a first hand-over into a fresh file has no size limit but `MaxBytes - metaSlack` (`makeRoom`, `put.go:211`), and `rollNeeded` lets a file grow past `fileMax` by one hand-over (`put.go:168-170`) | `put.go:168-170`, `put.go:209-213`; `spool.max_size` may be lowered between runs (`internal/config/agent.go:407`, 64 MiB to 64 GiB) | confirmed |
| 10 | `metaSize` is written by the spool itself and is at most `maxMetaBytes` | `meta.go:74`, `meta.go:275` | confirmed |
| 11 | A sum that wraps today makes `shortage` return `""` (fits) | `put.go:192`: a negative left side is never `> MaxBytes` | confirmed |
| 12 | A planted file is dropped by `dropOldest` through its identity, not its size | `removeFile` via `removeTrusted` with `want` (`fsys.go:200-213`) | confirmed |
| 13 | Renaming the spool directory away after `Open` makes `Statfs(path)` fail with `ENOENT` while `Fstatfs(fd)` succeeds | Security's measurement (rename: `Statfs(path)` fails with ENOENT, `Fstatfs(fd)` succeeds; a symlink planted at the old path makes `Statfs` report another file system, `Fstatfs` the held one); `TestStatAvailable` cases "the default check reads the opened directory after its path moved" and "the default check ignores a full file system planted at the old path" | confirmed |

## Acceptance criteria

- [ ] AC1: Without an injected `Options.Available`, the free-space check reads the file system of the directory descriptor the spool
  opened (`syscall.Fstatfs` on `dirFS.fd`): after the spool directory is renamed to another name following `Open`, a `Put` with
  `MinFreeBytes > 0` succeeds and `available()` returns a positive number; `statAvailable` on an invalid descriptor returns `0` and
  an error. `Options.Available`, when set, is still called with `Options.Dir`.
- [ ] AC2: `diskUsed` adds the data file sizes saturating at `math.MaxInt64` (never negative, never wrapping), and `SpoolBytes`
  returns the data files plus the meta file saturated the same way.
- [ ] AC3: The size bound in `shortage` counts a sum (data files + `metaSlack` + the write) that reaches `math.MaxInt64` as no room
  (reason `size_bound`), so planted oversized files are dropped oldest first like any other file and the hand-over is written.
  Sums below `math.MaxInt64` behave as today.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestStatAvailable`, adapted: "an open directory has space" (`statAvailable` on a descriptor opened with `syscall.Open(t.TempDir(), O_RDONLY\|O_DIRECTORY\|O_CLOEXEC, 0)` > 0), "an invalid descriptor is an error" (`statAvailable(-1)` = 0 and an error), "the default check is used without an injected one" (kept), new "the default check reads the opened directory after its path moved" (rig with `Available = nil`, `MinFreeBytes = 1`; open; `os.Rename(r.dir(), r.dir()+"-moved")`, renamed back in `t.Cleanup` before the rig's close; `mustPut` succeeds and `sp.available()` > 0). The `limits_test.go:526` assertion stays unchanged | `internal/spool/cover_test.go` |
| AC2 | `TestAddSize` (table: 0+0, 1+2, `MaxInt64`+0, `MaxInt64`+1, `MaxInt64`+`MaxInt64`, `MaxInt64-1`+1); `TestSpool_DiskUsedSaturates` (a `Spool{files: …}` literal with sizes `MaxInt64`, `MaxInt64`, 1 gives `MaxInt64`; sizes 1, 2 give 3); `TestSpoolBytes_Saturates` (open a rig, put one record, set `sp.files[0].size = math.MaxInt64` under `sp.mu`, `SpoolBytes` = `MaxInt64`, nil; the size restored before the rig closes the spool) | `internal/spool/spool_test.go` |
| AC3 | `TestPut_SizeBoundCountsAnUnrepresentableSumAsFull`: rig at 64 MiB cap, enough hand-overs for at least three data files; under `sp.mu` set the sizes of the two oldest (sealed, not active) files to `math.MaxInt64`; the next `Put` succeeds, the two files are gone from the directory, the warning `spool data dropped` has `reason` `size_bound`, `readAll` holds the `spool_dropped` gaps for their numbers, and `SpoolBytes` equals `diskBytes` afterwards. Against today's code the sum wraps and nothing is dropped | `internal/spool/limits_test.go` |

## Approach

1. `spool.go`: `statAvailable` takes the descriptor and calls `syscall.Fstatfs(fd, &st)` through `retry` (EINTR, as every other
   descriptor call in `fsys.go`); the `Bsize <= 0` and `bits.Mul64` overflow handling stays. `available()` keeps the seam
   (`s.opts.Available(s.opts.Dir)`) and otherwise calls `statAvailable(s.dir.fd)`. The doc comment of `Options.Available` says
   "nil means fstatfs on the spool directory's descriptor, f_bavail times f_bsize".
2. `spool.go`: new `addSize(a, b int64) int64` — the sum of two non-negative sizes, `math.MaxInt64` when it would exceed it
   (`if b > math.MaxInt64-a { return math.MaxInt64 }`). `diskUsed` folds with it; `SpoolBytes` returns
   `addSize(s.diskUsed(), s.metaSize)`.
3. `put.go` `shortage`: `total := addSize(addSize(s.diskUsed(), metaSlack), need)`; `size_bound` when
   `total == math.MaxInt64 || total > s.opts.MaxBytes`. `need` is non-negative (`h.bytes` and the gap reserve are sums of
   in-memory sizes). `makeRoom`'s pre-check (`put.go:211`) sums only in-memory sizes and stays.
4. Start-up stays as it is: a sealed file of any size is still kept by `quick`; its size is now counted saturating and the size
   bound drops it at the first write (record 0123, *Size accounting*). No new damage reason, no new log line, no new configuration.

No new user-visible output.

## Options considered

- Free-space check: one option — `Fstatfs` on the held descriptor is the issue's fix and the only way to read the file system of
  the directory the spool writes into; `Options.Available` keeps its path argument because tests assert it and the path names the
  directory to an injected function, which never reaches the file system.
- Size accounting (record 0123, *Size accounting*): (a) treat a data file larger than the sealing size plus the largest hand-over as
  damage at start — lost: the largest legitimate file depends on the `spool.max_size` of the run that wrote it (fact 9), so a bound
  from the current cap deletes good records after the cap is lowered, and a fixed bound needs a new ceiling on `Options.MaxBytes` in
  the spool package; it also still leaves the `put.go:192` addition to be made safe. (b) saturate the sums and count a saturated
  sum as no room — chosen: today's behavior for every file a real spool writes, and it fails closed. (c) both — the cost of (a)
  without a further protection, since (b) already bounds the arithmetic.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Go agent | `internal/spool/spool.go` | `statAvailable` on a descriptor via `Fstatfs`; `available`; `addSize`; `diskUsed`; `SpoolBytes`; `Options.Available` doc comment |
| Go agent | `internal/spool/put.go` | `shortage` size-bound comparison |
| Go agent | `internal/spool/cover_test.go`, `spool_test.go`, `limits_test.go` | tests above |

## Signatures (for the Dev's skeleton)

- `func statAvailable(fd int) (uint64, error)` (changed: was `dir string`) — `spool.go`
- `func addSize(a, b int64) int64` (new) — `spool.go`
- unchanged signatures, changed bodies: `func (s *Spool) available() (uint64, error)`, `func (s *Spool) diskUsed() int64`,
  `func (s *Spool) SpoolBytes(ctx context.Context) (int64, error)` (`spool.go`), `func (s *Spool) shortage(h *handover) (string, error)`
  (`put.go`).

## Test files

`internal/spool/cover_test.go` (existing `TestStatAvailable`, adapted), `internal/spool/spool_test.go` (AC2), and
`internal/spool/limits_test.go` (AC3, beside `TestPut_SizeBoundDropsOldest`).

Existing test call sites of a changed signature: `cover_test.go:19` and `cover_test.go:24` call `statAvailable` with a path; the
Tester adapts them in step 5 (AC1 row). Tests kept unchanged: every user of `Options.Available` (`helpers_test.go:67-100`,
`open_test.go:436`, `outage_test.go:137`, `limits_test.go:469,526`) — the seam's signature and argument do not change; the size-bound
tests (`limits_test.go`) never reach a sum near `math.MaxInt64`, so the new comparison decides them as today.

Files no unit test can cover: none.

## Areas

Agent (`docs/areas/agent.md`): *File access* and *Size bound, free space and file limit* — the free-space check reads the directory
descriptor's file system, and the size sum saturates.

## Documentation updates

Owned by the Dev, written once the code is green:

- `docs/areas/agent.md` *File access* (line 1704): the list of descriptor-relative calls (`openat`, `unlinkat`, `renameat`) gains
  `fstatfs` for the free-space check.
- `docs/areas/agent.md` *Size bound, free space and file limit* (line 1768): `statfs` becomes `fstatfs` on the directory's
  descriptor; add that the sizes are added saturating at 2^63 - 1 and that a sum that reaches it counts as no room, so an oversized
  file is dropped like any other oldest file (and `agent_spool_bytes` saturates the same way).
- `.squad/project.md` security area 9 (line 156): "every operation goes through `syscall.Openat`, `Unlinkat` and `Renameat`" adds
  `Fstatfs` for the `spool.min_free` check; "the `spool.min_free` free-space check is compared without overflow" adds "and the size
  bound's sum saturates, a saturated sum counting as no room".
- Sweep done: `docs/ARCHITECTURE.md:70` names the bounds without the call or the sum — no change; `deploy/agent/README.md:685`
  (system-call filter) needs none (fact 4); the `statfs` mentions in `docs/areas/agent.md:167,235,237,2080,2286` are the system
  metrics collector, not the spool.

## Architecture check

The spool's guarantees in `.squad/project.md` area 9 and `docs/ARCHITECTURE.md:70` (within `spool.max_size` and `spool.min_free`,
oldest dropped with a gap, every operation relative to the directory's descriptor) are strengthened, not weakened: the reserve now
reads the descriptor too, and the size bound can no longer be defeated by wrap-around. No `ARCHITECTURE.md` change.

## Security considerations

- [ ] No limits on parsed input change; the sizes are file sizes from `fstat`, not decoded values.
- [ ] Exceptions reaching a user-visible text: an `Fstatfs` failure reaches `spool write failed` / the `Put` error as
  `spool: write: <errno text>` through `writeError` and `errText` (`put.go:200`), as the `Statfs` failure did; it carries no path.

## Decision records

- `docs/decisions/0123-agent-spool-segment-files-clock-bound-sequence-numbers-range-acknowledgements.md` (Accepted, unreleased:
  not in `v0.1.0`) extended in place: *File access* option 3 names `Fstatfs`, a new *Size accounting* option list, and the decision
  sentence.

## Out of scope / follow-ups

- Refusing an oversized sealed file at start (option a) — not planned.

## Challenge notes

Devil's Advocate, mode plan (0 major, 1 minor), appended verbatim:

1. **minor** (test hygiene: a descriptor leak the Tester can avoid without a plan change). In `TestStatAvailable` "an open directory has space" (`internal/spool/cover_test.go`), close the descriptor opened with `syscall.Open(t.TempDir(), O_RDONLY|O_DIRECTORY|O_CLOEXEC, 0)` in a `t.Cleanup` that ignores no error silently: report a failed `syscall.Close` with `t.Errorf`. Fail the test with `t.Fatalf` if the open itself fails, before calling `statAvailable`.

## Security notes

Security, mode plan, APPROVED, non-blocking findings appended verbatim:

1. Under option (b), a planted oversized sealed file that has the *highest* file number is dropped only after every older legitimate file. Each `size_bound` round drops `files[0]` until the planted file is reached, so all older unacknowledged records turn into `spool_dropped` gaps. This doesn't add any capability: a file only passes `trusted()` if it belongs to the agent's own user, and that user can delete spool files directly. Still, the area document text saying "an oversized file is dropped like any other oldest file" could say plainly that the oldest-first order stays, so newer planted files cost the older real data. The Dev can add this in the `docs/areas/agent.md` *Size bound* update, or the Lead in record 0123 *Size accounting*. It needs no code change.
2. Fact 13 in the plan's table can be marked confirmed with the measurement above (rename: `Statfs(path)` fails with ENOENT, `Fstatfs(fd)` succeeds; a symlink planted at the old path makes `Statfs` report another file system, `Fstatfs` the held one), as a record update only.
