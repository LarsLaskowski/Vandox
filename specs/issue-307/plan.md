# Plan: Journal reader: bound a cursor's reads from its cached entry array by the arena

Source: Issue #307
Status: Draft
Tier: standard — adds a bounds check to the journal reader's input handling (no guard deciding what an attacker may reach, `.squad/routing.md`, *Tiers*), with new `malformed file` outcomes on hostile files.
Security areas touched: 10. Parsing of external input — `internal/journal` (`File.arraySlot`, `File.position`, new `File.inArena`, `File.check`)

## Problem / root cause

A `Cursor` keeps the entry array it stands on (`arr`, `arrBase`, `arrCap`, `internal/journal/list.go:15`) across `Next` calls and
across `File.Refresh`. Two reads go to that cached array without any bounds check:

- `listItem` (`list.go:289`) calls `arraySlot` (`journal.go:138-148`), which reads `arr + 24 + slotWidth*k` with `readAt`/`readUint64`
  directly; only `k < arrCap` holds, from the size seen when the cursor reached the array.
- `position` (`list.go:305`) reads the link `c.arr + 16` with `readUint64` when the cursor moves to the next array.

Both locations were inside the arena when the array was checked by `object()` (`array()`, `list.go:204-217`). They can leave it only
when the header is read again: `load` (`journal.go:205`) takes any arena size, so a hostile or replaced online file whose header
declares a smaller arena makes the next `Next` read past `declEnd`. Every other read of the package follows an `object()`/`check()` in
the same call. The value read is validated afterwards (`array()` / `readEntry` through `object()`), so the impact is a misread, never a
panic or an unbounded read (the scratch buffer is fixed).

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The cursor caches the capacity of its current array in `arrCap` (issue). | `list.go:15` | confirmed |
| 2 | `arrCap` is set by `position()`/`Seek` from the array object's size (issue). | `list.go:138`, `list.go:302`, `list.go:319`; size from `array()`, `list.go:216` | confirmed |
| 3 | `listItem` reads slot `j-arrBase` through `arraySlot` without re-checking the array's current size (issue). | `list.go:286-289`, `journal.go:138-148` | confirmed |
| 4 | An array rewritten smaller in place makes the cursor read past the array's end (issue). | The read stays within the extent `object()` checked when the cursor reached the array (`k < arrCap`), so it reads the following object's bytes, inside the arena. | confirmed |
| 5 | The slot offset is not checked against the arena (`declEnd`) (issue). | `arraySlot` calls only `readAt`; it can lie past `declEnd` only after `Refresh` lowered the arena (`journal.go:165-175`, `205`); with the arena unchanged it is inside the extent checked before (narrowed: past the arena only after a header re-read). | confirmed |
| 6 | The read goes into the fixed scratch buffer and `object()` validates the value read; never a panic or unbounded read (issue). | `journal.go:141`, `263`; the value goes to `entryAfter`/`readEntry` → `object()` (`list.go:193`, `entry.go`) | confirmed |
| 7 | The defect is on main and not specific to COMPACT (issue). | Both branches of `arraySlot` (`journal.go:140-147`); the branch adds only `specs/issue-307` on top of main `08faedf` | confirmed |
| 8 | Related defect: `position` reads the link `c.arr + 16` of the cached array without a bounds check. | `list.go:305`; the value is checked by `array(next)` afterwards, so only the read itself leaves the arena | confirmed |
| 9 | `check()` cannot be reused as is for a slot: it demands 8-alignment, and a COMPACT slot `arr + 24 + 4k` is 4-aligned for odd `k`. | `journal.go:240`, `slotSizeCompact = 4` (`journal.go:82`) | confirmed |
| 10 | Every other read of the package is preceded by an `object()`/`check()` of the same range in the same call. | `grep readAt\|readUint64` over `internal/journal/*.go` (non-test): `entry.go:80,145`, `entries.go:66,121,187`, `list.go:47,86`, `journal.go:184,278` | confirmed |
| 11 | The existing test `TestFile_ArraySlotReportsAShortReadInBothLayouts` keeps passing: it limits the reader after `Open`, so `fileSize` and `declEnd` still cover the slot and the short read gives `errTorn` as before. | `journal_test.go:1575-1599` | confirmed |

## Acceptance criteria

- [ ] AC1: `File.arraySlot(arr, k)`, in both layouts, returns `0` and `ErrMalformed` without reading when the slot's bytes
  `[arr+24+w*k, arr+24+w*k+w)` end beyond the header's arena (`declEnd`), and returns the stored offset when they end exactly at
  `declEnd`; in COMPACT a slot at an odd `k` (4- but not 8-aligned) inside the arena is read.
- [ ] AC2: In both layouts, an online file whose header is re-read (`Refresh`) with an arena ending at or before the next slot of the
  array a cursor stands on (that slot rewritten to the offset of an earlier, valid entry) makes `Next` return `false` and
  `ErrMalformed` (text `journal: malformed file`), leaves the cursor unchanged and issues no read ending beyond `declEnd`.
- [ ] AC3: In both layouts, when the cursor has read the last slot of its array and the re-read header's arena ends before that
  array's link (`arr + 16`), `Next` returns `false` and `ErrMalformed`, leaves the cursor unchanged and issues no read ending beyond
  `declEnd`.
- [ ] AC4: Files that respect their bounds read exactly as before: the existing journal tests (bounds tables, sweeps, torn tails,
  the real COMPACT sample, the read budget of `TestFile_SeekAndNextWorkDoNotGrowWithEarlierEntries`) pass unchanged.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestFile_ArraySlotOutsideTheArenaIsMalformedInBothLayouts` (table over `compact=false/true`; first array of the kernel list, `k = 1`; header arena set so `declEnd = slotAt(arr,1)+w-1` → `0, ErrMalformed`; `declEnd = slotAt(arr,1)+w` → the entry offset stored there, no error; applied through `Refresh`) | `internal/journal/journal_test.go` |
| AC2 | `TestFile_NextAfterTheArenaShrankBeforeTheNextSlotIsMalformed` (`compact=false/true`, online, `kernelEntries(30, true)`; `cursorAfter(t, f, 3)`; slot 2 of the first kernel array := value of its slot 0; arena := `slotAt(A0,2) - headerSize`; `Refresh`; reset `r.maxEnd`; `Next` → `false, ErrMalformed`, cursor equal to its copy, `r.maxEnd <= declEnd`) | `internal/journal/list_test.go` (new) |
| AC3 | `TestFile_NextAfterTheArenaShrankBeforeTheArrayLinkIsMalformed` (`compact=false/true`, online, `kernelEntries(30, true)`; `cursorAfter(t, f, 5)` so the cursor has read slot 3, the last of the first array (capacity 4); arena := `A0 + 16 - headerSize`; `Refresh`; reset `r.maxEnd`; `Next` → `false, ErrMalformed`, cursor unchanged, `r.maxEnd <= declEnd`) | `internal/journal/list_test.go` (new) |
| AC4 | existing tests of `internal/journal`, unchanged | `internal/journal/*_test.go` |

Each new test fails before the fix: AC1 reads the slot successfully, AC2 `Next` returns the earlier entry (`true, nil`), AC3 reads
`A0+16..A0+24` past `declEnd` (`r.maxEnd` too large; the error alone is the same before the fix, because `array(next)` refuses the
link's target).

## Approach

1. `journal.go`: split `check` into the alignment test and a new `inArena(off, length)` holding the rest of today's body (lower bound
   `f.hs`, overflow `end < off`, `end > f.declEnd` → `ErrMalformed`; `end > f.fileSize` → `f.short()`). `check` = alignment test, then
   `inArena`. Behavior of `check` unchanged.
2. `arraySlot`: compute `at`, call `f.inArena(at, f.slotWidth)` and return `0, err` on failure, before either read. No alignment test:
   `arr` was 8-aligned by `object()` when the cursor reached it, so `at` is always `slotWidth`-aligned (an unreachable branch would
   only cost coverage). This also covers `itemAfter` (seek), where the check always passes.
3. `position`: before `f.readUint64(c.arr + 16)`, `f.check(c.arr+16, 8)` (8-aligned by the same argument); return its error.
4. Test helper (Tester): `refReader` in `builder_test.go` gains `maxEnd int64`, the largest `off + len(p)` requested, updated in
   `ReadAt`.

No new user-visible output; the error is the existing `ErrMalformed`.

## Options considered

- **Bound the cached reads by the arena and file (chosen)** — restores the package's rule that every read is checked against the
  file's bounds, with no extra I/O.
- **Re-read the array's size before every slot (`array(c.arr, c.arrNo)` in `listItem`)** — also catches an array shrunk in place with
  the arena unchanged, but costs one more read per entry; the bytes behind a shrunk array are no more hostile than the slot itself,
  whose value `object()` checks. Rejected; recorded as option 15 of record 0119.
- **Drop the cursor's cached array after every `Refresh`** — would re-walk the chain from its head after every poll, breaking the
  constant per-poll work of the bisection design (`docs/areas/agent.md`, *Finding the entries*). Rejected.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| agent (Go) | `internal/journal/journal.go` | new `inArena`; `check` calls it; `arraySlot` bounds its read |
| agent (Go) | `internal/journal/list.go` | `position` checks the link before reading it |
| agent (Go) | `internal/journal/builder_test.go` | `refReader.maxEnd` (Tester) |
| agent (Go) | `internal/journal/journal_test.go`, `internal/journal/list_test.go` (new) | new tests (Tester) |

## Signatures (for the Dev's skeleton)

- new: `func (f *File) inArena(off, length uint64) error` — doc comment: "inArena tests that the length bytes at off lie inside the
  arena and the file."
- unchanged signatures, changed bodies: `func (f *File) check(off, length uint64) error`, `func (f *File) arraySlot(arr, k uint64)
  (uint64, error)`, `func (f *File) position(c *Cursor, j uint64) error`.

## Test files

`internal/journal/journal_test.go` (AC1, `arraySlot` lives in `journal.go`), `internal/journal/list_test.go` (new; AC2, AC3:
`listItem`/`position` live in `list.go`); helpers `cursorAfter`, `kernelList`, `newRefReader`, `buildFile` are package-level and reused.

Existing test call sites of a changed signature: none (no signature changes). `refReader` gains a field only; no call site changes.

Files of the change no unit test can cover: none.

## Areas

`docs/areas/agent.md`, *Kernel events* → **Layout (security area 10)**: the bounds rule now also names the reads a cursor makes from
the array it stands on, after the header is re-read.

## Documentation updates

- `docs/areas/agent.md` (Dev), the sentence "Before anything is decoded, every offset must be 8-aligned and inside the header's arena
  and the file, …" (around line 936): add that every item of an entry array and every link to the next array is read only inside the
  arena and the file, also from the array a cursor kept across a re-read of the header. Sweep result: `docs/ARCHITECTURE.md` line 53
  ("bounded reader") and `.squad/project.md` area 10 ("bounds checked on every offset and size") stay true and need no edit; the
  package comment of `journal.go` (line 6) stays true.

## Architecture check

The guarantee of security area 10 (malformed or hostile input yields an error, never a crash, an unbounded allocation or a hang) is
strengthened, not weakened: the change only refuses reads earlier. The constant per-poll work of the cursor is preserved (no extra
read). No `docs/ARCHITECTURE.md` change.

## Security considerations

- [ ] No limits on decoded input are added; the bound is on file offsets (raw bytes), checked before any read.
- [ ] Exceptions reaching users: none new; `ErrMalformed` keeps its fixed text without offsets (`expectMalformed`'s rule), and becomes
  a gap in `internal/kernevents` / a kept cursor in `internal/logship` as today.

## Decision records

- `docs/decisions/0119-kernel-messages-from-the-binary-journal-files-read-in-pure-go-without-decompression.md` (Accepted, unreleased:
  added by `bec2fa6`, not contained in `v0.1.0`) — extended in place: option 15 (re-read the array's size) rejected with the reason;
  issue #307 added to *Source*. Index row unchanged.

## Out of scope / follow-ups

- An entry array rewritten smaller in place with the arena unchanged is still read up to the size seen when the cursor reached it;
  accepted (record 0119, option 15).
