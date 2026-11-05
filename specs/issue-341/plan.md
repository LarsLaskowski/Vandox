# Plan: Socket owners of memfd and /proc/<pid>/mem holders unknown (fdinfo values rejected)

Source: Issue #341
Status: Revised (1)
Tier: standard — a parser's validation is widened to the value range the kernel prints, which changes behavior (owners found, no warning) but creates, changes or removes no security control: the socket-mount match and the probe's refusal of mount 0 stay as they are.
Security areas touched: 10. Parsing of external input (`internal/connstats` `ParseFdinfo`, the reader of `/proc/<pid>/fdinfo/<n>`, also used by `internal/sockmount` `Probe`, whose result feeds the socket-mount check of record 0115)

## Problem / root cause

`ParseFdinfo` (`internal/connstats/owners.go:36-64`) rejects two values the kernel really prints in the first four lines of an
`fdinfo` file:

- `owners.go:46` demands `allDigits` for every value, so a signed `pos:` (`%lli`) is refused: a descriptor whose position is above
  2^63, which `FMODE_UNSIGNED_OFFSET` files allow (`/proc/<pid>/mem`, `/dev/mem`), prints `pos:\t-<digits>`.
- `owners.go:56` refuses `mount == 0`, but the kernel's internal shmem mount has ID 0: every **memfd** (and any other
  `shmem_file_setup` file) prints `mnt_id:\t0`. memfds are common in system daemons.

One such descriptor makes `scanFile` return `errFormat` (`owners.go:155-158`), `scanPID` stops the process (`owners.go:127-129`) and
`scanOwners` counts it unreadable (`owners.go:92-97`); `connstats.go:291-293` then logs `socket owners partly unknown` with
`err="connstats: <pid>/fdinfo: not in the expected format"` — the issue's line — and every socket of that process loses its owner.
Which of the two values the server's two processes hold was not captured (the session has no access to the server); the memfd is the
likely one (row 6).

Related defect found on the way: `docs/areas/agent.md:532-533` says an `fdinfo` without the four-line shape is "a file changed or
damaged after the start-up probe succeeded"; it was these legitimate values. The wording is corrected (*Documentation updates*).

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The issue's error comes from the owner scan with `errFormat` | `errPrefixFmt` `owners.go:25`, `errFormat` = "not in the expected format" `table.go:38`; returned by `ParseFdinfo` (`owners.go:44,47,54,58`), `parseInode` (`table.go:201`) or a non-`ReadDirFile` directory (`owners.go:121`, impossible with `os.DirFS`) | confirmed |
| 2 | The parser requires all-digit values (issue's claim) | `owners.go:46`, `allDigits` `table.go:123-132` | confirmed |
| 3 | The issue's guess "negative `pos:`" is possible | 5.15 `fs/proc/fd.c:57` (read at tag v5.15): `seq_printf(m, "pos:\t%lli\nflags:\t0%o\nmnt_id:\t%i\nino:\t%lu\n", …)`; `fs/read_write.c:39` `FMODE_UNSIGNED_OFFSET`; captured on 6.18.44: `/proc/self/mem` after `lseek(-10485760, SEEK_SET)` prints `pos:\t-10485760` (`specs/issue-341/samples/proc-mem-high-offset`); whether the server hits it: row 6 | confirmed |
| 4 | A memfd prints `mnt_id:\t0`, which the parser rejects (`owners.go:56`) | captured on 6.18.44: `memfd_create` then `/proc/self/fdinfo/<fd>` (`specs/issue-341/samples/memfd`); 5.15 `fs/namespace.c:125-131` `mnt_alloc_id` = `ida_alloc` (from 0), `mnt_init` calls `shmem_init` (`kern_mount`, `mm/shmem.c:3910`) before `init_rootfs`/`init_mount_tree` (`namespace.c:4335-4337`), `kernfs_init`/`sysfs_init` mount nothing (`fs/sysfs/mount.c:97-112`); `mm/memfd.c:307` `shmem_file_setup`; on this host 3 descriptors of running processes show `mnt_id:\t0` (6.18 measured, 5.15 read) | confirmed |
| 5 | The first four lines are printed for every open file in this order, extra lines follow | 5.15 `fs/proc/fd.c:21-71`: the `seq_printf` above, then `show_fd_locks`, then `show_fdinfo` | confirmed |
| 6 | The server's two processes fail because of a memfd (or a signed `pos:`), not a third variant | to verify: none captured (rows 3-5 leave no other value the format can print that the new parser refuses; `mnt_id` from `ida_alloc` is never negative) — the Product Manager checks after the agent of this change is installed on the server (install run and next start) that `socket owners partly unknown` is no longer logged; owner Product Manager, after step 10 (not a gate of this PR) | unverified |
| 7 | The socket mount is never 0, and the collector treats ID 0 as "not learned" | `connstats.go:38` (zero value not probed), `connstats.go:179-180` (`errMountNotSet`); sockfs is mounted by `sock_init` after `mnt_init` (agent.md *The socket mount*: measured `mnt_id: 10`) | confirmed |
| 8 | `sockmount.Probe` today refuses mount 0 only through `ParseFdinfo` | `sockmount.go:31-38`; `sockmount_test.go:149` case "mnt_id zero" | confirmed |
| 9 | Record 0115 is unreleased | `git ls-remote --tags origin`: only `v0.1.0`; `git cat-file -e v0.1.0:docs/decisions/0115-…` fails | confirmed |

## Acceptance criteria

- [ ] AC1: `ParseFdinfo` accepts the captured memfd sample and returns `{MountID: 0, Inode: 1029}`, and the captured
  `/proc/self/mem` sample and returns `{MountID: 23, Inode: 6503}`; `pos:` accepts an optional `-` followed by one or more digits (the
  `%lli` form), `mnt_id:` accepts 0 to 2147483647 in 1 to 10 digits; everything else that is refused today stays refused, plus a `pos:`
  of only `-`, with two signs, with `+`, or with a `-` after a digit.
- [ ] AC2: In the owner scan a process holding a memfd and a high-offset `/proc/<pid>/mem` descriptor besides its socket is not counted
  unreadable (`unreadable` 0, `firstErr` nil) and owns its socket; a descriptor with `mnt_id:\t0` and the inode of a tracked socket is
  never counted as a holder, also when `scanOwners` itself is called with mount 0 (it then scans nothing and counts no holder).
- [ ] AC3: The collector logs no `socket owners partly unknown` for such a process, the listener carries its PID and the aggregates are
  complete (the decision is `connstats.go:291-293`: `res.unreadable > 0`).
- [ ] AC4: `sockmount.Probe` still fails on `mnt_id:\t0` with mount 0 and the error text `sockmount: not in the expected format` (unchanged
  from today), so the collector never learns 0 as the socket mount.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestParseFdinfo_Samples`: the two new fixtures `memfd` and `proc-mem-high-offset` added to its `want` map; `TestParseFdinfo_Rejects`: case "mnt_id zero" removed from `rejects`, new reject cases "pos only a minus" (`pos:\t-`), "pos with two minus signs" (`--1`), "pos with a plus sign" (`+1`), "pos with a minus after a digit" (`1-`), "pos minus and a space" (`- 1`); new subtest "signed pos and mount 0 are accepted" with `pos:\t-9223372036854775808`, `mnt_id:\t0` → `{0, 6087}` | `internal/connstats/owners_test.go` |
| AC2 | `TestScanOwners_MemfdAndHighOffsetDescriptorsKeepTheProcessReadable`: PID 10 holds fd 3 = fixture `memfd`, fd 4 = fixture `proc-mem-high-offset` (`addRaw` with `readFdinfoSample`), fd 5 = socket inode 100 on `socketMount`; PID 7 holds `p.add(7, 3, 0, 100)`; want `unreadable` 0, error nil, owner of slot 0 = 10, counts 10 → 1, 7 → 0; `TestScanOwners_MountZeroCountsNoHolder`: PID 10 holds `p.add(10, 3, 0, 100)` and `p.add(10, 4, socketMount, 100)`, scan with mount 0: owner of slot 0 = 0, count of 10 = 0, `unreadable` 0, error nil | `internal/connstats/owners_test.go` |
| AC3 | `TestConnections_MemfdHolderKeepsItsOwners`: `ownedWorld`, PID 100 additionally holds two descriptors with the two fixtures' content (beside its sockets); one run: `wantListener(…, sshdListener, 100, …)`, `AggregatesIncomplete` false, `wantLogged(…, "socket owners partly unknown", 0)` | `internal/connstats/connstats_test.go` |
| AC4 | existing `TestProbe_MalformedFdinfoFails` case "mnt_id zero" stays; new `TestProbe_MountIDZeroFails` asserting id 0 and the exact error `sockmount: not in the expected format` | `internal/sockmount/sockmount_test.go` |

The Tester copies `specs/issue-341/samples/memfd` and `specs/issue-341/samples/proc-mem-high-offset` byte for byte to
`testdata/connections/sandbox/fdinfo/` (captured on 6.18.44 by `memfd_create` and by `lseek(-10485760)` on `/proc/self/mem`; inode and
mount numbers only, nothing private) and documents them where the fixture directory is described (*Documentation updates*).

## Approach

- `ParseFdinfo`: per line a validator instead of the one `allDigits`: line 0 (`pos:`) accepts an optional leading `-` and then
  `allDigits` (a new helper `signedDigits`); lines 1-3 stay `allDigits`. Drop the `mount == 0` rejection at `owners.go:56`
  (`ParseInt` keeps the range 0..2147483647; the 10-digit bound stays). `pos:` is still not converted. The doc comment states the
  accepted forms.
- `sockmount.Probe`: after `ParseFdinfo`, `if info.MountID == 0` return `0, errors.New("sockmount: not in the expected format")` before
  the inode check — today's text, so no failure text changes.
- `scanOwners`: when `mount == 0` it returns the empty owners (slices sized by `index`), `unreadable` 0 and nil without opening anything, so a mount-0 descriptor can never be a holder inside the package, not only through the caller's check (`connstats.go:179-180`). No change to `scanFile`, `scanPID` or the collector.
- No new user-visible output.

## Options considered

- **Chosen: accept the values the kernel's format string prints** (record 0115, option 23).
- **Skip a descriptor of an unexpected shape instead of stopping its process** — rejected: hides a future format change and
  drops owners without a warning (0115, option 24).
- **Escalate first for `head -4` of the server's files** — rejected as a precondition: both values are measured kernel output the
  parser wrongly refuses whatever the server holds, the format string leaves no third value, and the residual (row 6) is checked on
  the server after installation. A captured server sample would add a fixture, not change the fix.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| `internal/connstats` | `owners.go` (`ParseFdinfo`, new `signedDigits`, `scanOwners`) | signed `pos:`, `mnt_id:` 0 accepted; no scan with mount 0 |
| `internal/sockmount` | `sockmount.go` (`Probe`) | refuses mount ID 0 itself |
| `testdata` | `connections/sandbox/fdinfo/memfd`, `connections/sandbox/fdinfo/proc-mem-high-offset` | new fixtures (Tester) |

## Signatures (for the Dev's skeleton)

- `func ParseFdinfo(data []byte) (Fdinfo, error)` — unchanged signature, new behavior.
- `func signedDigits(b []byte) bool` (new, `internal/connstats/owners.go`) — reports whether b is an optional `-` followed by one or
  more ASCII digits.
- `func Probe(proc fs.FS) (int32, error)` — unchanged signature.
- `func scanOwners(ctx context.Context, proc fs.FS, pids []int32, index map[uint64]int32, mount int32) (o owners, unreadable int, firstErr error)` — unchanged signature, early return for mount 0.

## Test files

`internal/connstats/owners_test.go`, `internal/connstats/connstats_test.go`, `internal/sockmount/sockmount_test.go`; fixtures under
`testdata/connections/sandbox/fdinfo/`.

Existing test code a new criterion handles differently: `owners_test.go:211` reject case "mnt_id zero" (moves to the accepted cases,
Tester); `TestParseFdinfo_Samples` fixture count follows its `want` map (Tester). `sockmount_test.go:149` stays unchanged. No changed
signature.

Files no unit test can cover: none.

## Areas

`docs/areas/agent.md` (*Connections*): the accepted `fdinfo` values and the corrected failure wording.

## Documentation updates

- `docs/areas/agent.md:434-436` (*Owners*, Dev): `pos:` an optional `-` and digits (the kernel prints it signed), `flags:` digits,
  `mnt_id:` 1 to 10 digits from 0 to 2147483647 (a memfd shows 0, the kernel's internal shmem mount, which is never the socket mount),
  `ino:` unchanged.
- `docs/areas/agent.md:532-533` (*Failures and rights*, Dev): an `fdinfo` without the four-line shape is a format the kernel does not
  print today, no longer "a file changed or damaged after the start-up probe".
- `docs/areas/agent.md:474-476` (*The socket mount*, Dev): the probe refuses a mount ID 0 (`sockmount: not in the expected format`),
  since 0 means "not learned".
- Sweep result: `docs/ARCHITECTURE.md:42`, `deploy/agent/README.md:458,618` and `docs/BENCHMARKS.md:374` name `fdinfo` without its
  value rules, and `docs/areas/agent.md:2368` lists fixture directories, not files — no change. No `CHANGELOG.md` entry (it has no unreleased section; releases list merged PRs).

## Architecture check

The guarantees of `.squad/project.md` are untouched: no new right, no new file read, the read bound (256 bytes) and the socket-mount
match stay; owners are found for more processes, which is the documented intent.

## Security considerations

- [x] Limits: the 256-byte read before parsing (unchanged), `mnt_id:` at most 10 digits and int32 (unchanged), `ino:` at most 20 digits
  (unchanged); `pos:` has no own length bound and is never converted, bounded by the 256-byte read — all on raw bytes, no decoding.
- [x] Exceptions reaching a message: none new; errors stay the fixed `errFormat`, never file content.
- Accepting `mnt_id:\t0` cannot make a descriptor count as a socket holder: the collector scans only with a learned mount `!= 0`
  (`connstats.go:179-180`) and the probe refuses 0 (AC4).

## Decision records

- `docs/decisions/0115-connection-collector-proc-net-tables-owners-from-fd-regular-and-full-snapshots-bounded-listener-check.md`
  (Accepted, unreleased — extended in place: options 23 and 24, Source #341, a Decision paragraph); index row unchanged.

## Out of scope / follow-ups

- Capturing the server's own files: the residual of row 6, checked by the Product Manager after installation; if the warning persists,
  a new issue with the `head -4` output the issue describes.

## Challenge

- **Devil's Advocate 1 (minor): the fix was not checked on the server, yet the PR closes the issue** — accepted. Record 0115
  option 23 now states that the server's files were not captured, that the fix covers the two values the format string allows and
  the parser refused, and that the check on the production server happens only after merge (the Product Manager, row 6). I did not
  ask the Product Manager for the sample in parallel: it would add a fixture, not change the fix (*Options considered*). The PR body
  says the same (orchestrator, step 10).

## Security review

Security: APPROVED; the two notes that left a choice:

- **Note 1: `Probe` copies the text "not in the expected format" instead of wrapping a shared sentinel** — rejected. Sharing it means
  exporting `connstats`' `errFormat` (used across `table.go` and `owners.go`) as public API for one caller; the exact-text test
  `TestProbe_MountIDZeroFails` (AC4) catches any drift.
- **Note 2: `scanOwners` relies on its caller to refuse mount 0** — adopted: early return in `scanOwners` (*Approach*), covered by
  `TestScanOwners_MountZeroCountsNoHolder` (AC2).
