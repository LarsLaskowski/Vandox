# Plan: Read journal files of the COMPACT layout

Source: Issue #286
Status: Draft
Tier: security — the change widens the layout allow-list of the journal reader (`load`, `journal.go:159-162`) and adds a second decoding of every offset and size of an untrusted file, i.e. it changes the bounds checks that are the guard of security area 10.
Security areas touched: 10 Parsing of external input (`internal/journal`: `journal.go`, `list.go`, `entry.go`, `entries.go`, the layout gate and the compact decoding; consumers `internal/kernevents`, `internal/logship` unchanged); test data under `testdata/journal/` (real systemd output, anonymized). Area 6 is not touched: no new right, path or read.

## Problem / root cause

`File.load` (`internal/journal/journal.go:158-163`) accepts only the incompatible flags XZ, LZ4, KEYED-HASH and ZSTD
(`known := flagsXZ | flagsLZ4 | flagsKeyed | flagsZSTD`) and returns `ErrUnsupported` for any other bit, so a file with
`HEADER_INCOMPATIBLE_COMPACT` (`1 << 4`) is refused. systemd 252 and later write that flag by default, so after an upgrade
the kernel collector (`internal/kernevents/files.go:101`) and the log shipping collector (`internal/logship/journal.go:111`)
record every file as a gap. This is pinned by the `"compact layout"` case of `TestFile_RefusesUnsupportedLayout`
(`journal_test.go:330`). Even with the gate opened, the decoding would be wrong, because it hard-codes the non-compact widths:
- payload at 64: `dataFixed` in `walkChain`/`samePayload` (`list.go:61-86`), `readItem` (`entry.go:115-145`), `readItemWith` and
  `readCompressedItem` (`entries.go:91-142`);
- 16-byte entry items: `itemSize` in `beginEntry`/`readEntry` (`entry.go:79-98`) and `readEntryWith` (`entries.go:65-71`);
- 8-byte entry-array items: `itemAfter` (`list.go:181`), `array` (`list.go:208-216`) and `listItem` (`list.go:289`).

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The reader refuses every COMPACT file as `unsupported layout`. | `journal.go:159-162`; test case `journal_test.go:330` | confirmed |
| 2 | systemd 252+ writes COMPACT by default. | systemd v255 `journal-file.c`: `SYSTEMD_JOURNAL_COMPACT` unset gives `cached = true` (lines 339-349) | confirmed |
| 3 | COMPACT layout: the data object payload is at 72, after `le32 tail_entry_array_offset` and `le32 tail_entry_array_n_entries`. Entry items are `le32 object_offset` with no hash. Entry-array items are `le32`. The other fields are unchanged: `entry_offset` 40, `entry_array_offset` 48, `n_entries` 56, entry fixed part 64, array fixed part 24. | v255 `docs/JOURNAL_FILE_FORMAT.md` (DataObject, EntryObject, EntryArrayObject). Decoding both samples with these widths: every entry item points at a data object, every array item at an entry, every payload has a name, and no item remainder is left | confirmed |
| 4 | Header of systemd 255: 272 bytes, `le32` tail_entry_array fields at 256/260, `le64 tail_entry_offset` at 264. systemd 252/253 write 264 bytes. The reader already accepts 256..4096. | `JOURNAL_FILE_FORMAT.md` ("Added in 252", "Added in 254"); sample headers: 272; `journal.go:160` | confirmed |
| 5 | Entry arrays at least double in length (`n = (hidx+1)*2` or `n*2`, at least 4), so `maxArrays` = 64 still covers every file. | v255 `journal-file.c` `link_entry_into_array` (lines 2112-2118); sample capacities 4, 8, 26 | confirmed |
| 6 | The reader needs neither the header tail fields nor the data-object tail fields. Lookup, `Seek` and `Next` use only the hash table, the data objects' `entry_offset`, `entry_array_offset` and `n_entries`, and the header's entry count and `entry_array_offset`. | `journal.go:164-180`, `list.go`, `entries.go` (no header read beyond offset 200) | confirmed |
| 7 | PM samples (systemd-journal-remote 255.4-1ubuntu8.18): kernel sample: 8 MiB, header 272, incompat `0x14` (no ZSTD, no compressed object), compat `0x2`, 100 entries, of which 95 are `_TRANSPORT=kernel` (4 arrays chained). Live sample: incompat `0x1c`, 40 entries, 4 zstd data objects, 6 kernel entries. The zstd frames have descriptor `0x60` (single segment, 2 content-size bytes, no checksum, no dictionary), the form `zstd-frames.txt` lists for the server. No byte outside header and objects is non-zero. | decoded with a scratch script; `journalctl --file=… -o export` gives 100/95 and 40/6 | confirmed |
| 8 | The samples hold entries twice. Kernel: entries 1-10 and then all 90 entries of `kernel.export`. Live: entries 1-20 of `live.export` twice (seqnums 1-20 and 21-40); its 21st entry (realtime `1792800020000000`, a 22-byte message) is missing. The 20,000-byte message is present at seqnums 15 and 35, as a zstd object (frame content size 20,008); the 700-byte message at seqnums 16 and 36, also zstd (708). So `journalctl --verify` fails on **both** samples ("Bad message", timestamps out of order), though systemd reads them. | entry dump: realtimes of seq 1 and 11 (kernel) and of seq 1 and 21 (live) are equal, with the same payloads; `journalctl --all -o json` of the live sample (lengths 20,000 at 15/35, 700 at 16/36) against the stamps of `live.export`; frame headers of the 4 zstd objects; `journalctl --verify` output. Corrected in step 6 (decide): the first version named the 20,000-byte message as the missing entry | confirmed |
| 9 | Kill lines of the kernel sample (exactly one `SYSLOG_FACILITY=0`): seqnums 4, 10, 14, 20, 25, 29, 97, 100 = PIDs 3021, 4247, 3021, 4247, 352448, 351416, 5150, 6161. The 600-byte kill line (4247) is stored plain. | `journalctl -o json _TRANSPORT=kernel` of the sample | confirmed |
| 10 | Every field value of both samples comes from the committed exports. The only foreign value is the header machine ID `2f69…` of the machine that ran journal-remote (not `/etc/machine-id` of this sandbox). File ID and seqnum ID are random, and they key the hash, so they stay. | journalctl JSON values compared with `kernel.export`/`live.export`; header decode | confirmed |
| 11 | Anonymized copies, with header bytes 40..56 replaced whole by `0badc0de0badc0de0badc0de0badc0de` (the exports' `_MACHINE_ID`): no 4-byte window of the real ID remains, raw or as hex. journalctl still reads 100/40 entries. sha256 kernel `4c8f3cdb683953489a1bf338e90627b46ef504987037f94479aed3db6e75f1cc`, live `f022f3dce65c308a372895b5f09e01ca8400a64b89a4a0a9cee842aa3eb1d819`. | scratch check on `/tmp/claude-0/-home-user-Vandox/0ebf782a-dde5-5bae-b9b0-1d2a5bfa91a4/scratchpad/anon/{kernel,live}.journal` | confirmed |
| 12 | `testdata/journal/live.export` lacks the final blank line. `ReadExport` (Go) tolerates that; journal-remote does not. | `tail -c` shows `…generated\n`; `real_test.go:52` / `export.go` flush at EOF | confirmed |
| 13 | Fixture globs: `loadFixture` (kernevents `harness_test.go:285`), `TestFixture_CommittedFilesMatchTheWriter` (`writer_test.go:491`) and `TestFixture_HoldsNoRealPrivateValue` (`real_test.go:291`) take `system*` from the top level of `testdata/journal` only. Subdirectories do not change their counts. | code read | confirmed |
| 14 | `journalctl --verify` of systemd 255 accepts the compact output of `journaltest`. | to verify: Dev runs `TestWrite_JournalctlAcceptsFixture` with the compact files (journalctl 255 is installed in this sandbox) in step 6; a failure is a writer defect (likely the data-object or header tail fields) | unverified |
| 15 | journald (not journal-remote) on the target server writes the same compact objects. | to verify: PM supplies real compact server files (option A); `TestReal_NextWithDecodesEveryEntry` with `VANDOX_REAL_JOURNAL_DIR` on a developer machine, after the PR; counts only | unverified |
| 16 | Record 0119 and 0121 are unreleased: added in #288/#293, after `v0.1.0`, the only tag (`git ls-remote --tags origin`). | `git merge-base --is-ancestor` | confirmed |

Claims of the issue: "systemd 252 and later write COMPACT by default" is confirmed (2). "32-bit entry-item offsets, compact entry-array and data object fields" is confirmed (3). "the tail-entry fields of the larger header" is refuted as a need: the reader reads none of them (6), and the 272-byte header is already accepted (4). Related finding: `live.export` lacks its last blank line (12).

## Acceptance criteria

- [ ] AC1: `Open` accepts a file with KEYED-HASH and COMPACT, alone or with any of XZ, LZ4 and ZSTD, at header sizes 264 and 272. COMPACT without KEYED-HASH, and any unknown flag (`1<<5`, `1<<31`), are still refused with `ErrUnsupported`, text `journal: unsupported layout`.
- [ ] AC2: The real kernel sample `testdata/journal/systemd-255/kernel.journal` is read through `Lookup("_TRANSPORT=kernel")`, `Seek(d, 0)` and `Next(…, ["MESSAGE", "SYSLOG_FACILITY"])`, giving 95 entries in seqnum order. The test pairs each entry with the `kernel.export` entry of the same realtime and monotonic stamps. For each pair, these match: boot ID, `MESSAGE` and the number of `SYSLOG_FACILITY` items. The entries with one facility `0` and a kill message are seqnums 4, 10, 14, 20, 25, 29, 97, 100. For every `s` in 0..100, `Seek(d, s)` stands on the first kernel entry with seqnum greater than `s`, and the list ends with `Torn() == false`.
- [ ] AC3: Both real samples are read in full through `SeekAll(0)` and `NextWith`, with the log collector's options (`Logs.readOptions`, `internal/logship/logship.go:181`: `zstdfield` decoder, `MaxPayload` 16,384 (`maxMessageBytes`), `MaxCompressed` 4, `MaxCompressedBytes` `zstdfield.MaxFrameBytes`). The reads give 100 and 40 entries (the header's entry count), `Undecoded` 0 on every entry, and exactly one `MESSAGE` per entry: for an export value of at most 16,384 bytes equal to it with `Cut` false, for a longer one its first 16,384 bytes with `Cut` true (`readCompressedItem`, `entries.go:166-170`). In the live sample, seqnums 15 and 35 carry the 20,000-byte message decoded from zstd and cut to 16,384 bytes, and seqnums 16 and 36 the 700-byte message decoded from zstd; no other entry is cut. `SeekAll(s)` stands on entry `s+1` for every `s` in 0..39.
- [ ] AC4: The bounds rules hold for COMPACT as for the old layout. Each compact bounds case ends in `ErrMalformed` (text `journal: malformed file`) for archived and online state, with no panic and no read the `refReader` flags. The cases:
  - entry size not a multiple of 4 after the fixed part;
  - array size not a multiple of 4;
  - a data object smaller than 72;
  - an item or array slot inside the header, not 8-aligned, or beyond the arena;
  - a chain loop.

  The one-byte sweep over a compact file ends only in success, `ErrMalformed` or `ErrUnsupported`. A non-compact file with the COMPACT bit set, and a compact file with it cleared, end in success or `ErrMalformed`, never a panic or a misbehaving read. A compact archived file cut inside an entry ends at the tear (`Torn()`). The same cut in an online file is `ErrMalformed`.
- [ ] AC5: `journaltest.Write` with `Options.Compact` writes a 272-byte header with incompat `0x1c` (KEYED-HASH, ZSTD, COMPACT) and compat `0x2` (TAIL-ENTRY-BOOT-ID), data payloads at 72, 4-byte entry items and 4-byte array items. `journalctl --verify` accepts each compact fixture file, and `journalctl -o export` gives its entries (when journalctl is installed). `journal.Open` with `Next` reads every kernel entry of the compact fixture as in the export. `testdata/journal/compact/` equals the writer's output.
- [ ] AC6: The kernel collector over the compact fixture (`testdata/journal/compact/`) returns the records it returns over the non-compact fixture for the saved-position scenario of `TestKernel_ResumesAtSavedPositionAcrossBoots`:
  - the kills 3021, 352448, 351416, 5150, 6161 with the same boot IDs and capture times;
  - the same two gaps;
  - no trigger call;
  - the same journal position in the state after the next run.
- [ ] AC7: The log shipping collector over compact journal files (`journalSpec.compact`, compression on) maps the 21 entries of `live.export` to the 21 records of `live-records.jsonl`, as `TestJournal_EntriesMapAsTheImportGolden` does for the old layout.
- [ ] AC8: Every existing test stays green. The only edit to an existing assertion is the removed `"compact layout"` refusal case. `live.export` gains its final blank line, and its Go and C# readers give the same records.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestFile_RefusesUnsupportedLayout` (case `"compact layout"` replaced by `"compact without keyed hash"`); new `TestFile_ReadsEveryAcceptedCompactFlagCombination` (built compact file, header 264 and 272) | `internal/journal/journal_test.go` |
| AC2 | `TestFile_ReadsTheRealCompactKernelSample` | `internal/journal/journal_test.go` |
| AC3 | `TestEntries_NextWithReadsEveryEntryOfTheRealCompactSamples` | `internal/journal/entries_test.go` |
| AC4 | `TestFile_BoundsEveryOffsetCompact`, `TestFile_BoundsEveryOffsetSweepCompact`, `TestFile_LayoutFlagFlippedIsNeverAPanic`, `TestFile_TornTailIsEndOfFileCompact` | `internal/journal/journal_test.go` |
| AC5 | `TestWrite_CompactHeaderAndItemWidths` (new); `TestWrite_JournalctlAcceptsFixture`, `TestWrite_ReaderSeesEveryKernelEntry`, `TestFixture_CommittedFilesMatchTheWriter` and `TestFixture_UpdateGolden` extended to the compact files | `internal/journal/journaltest/writer_test.go` |
| AC6 | `TestKernel_CompactJournalGivesTheSameKillsGapsAndPosition` | `internal/kernevents/position_test.go` |
| AC7 | `TestJournal_CompactEntriesMapAsTheImportGolden` | `internal/logship/journal_test.go` |
| AC8 | the full *Test* run (Go and .NET), incl. `TestJournal_EntriesMapAsTheImportGolden`, `TestWrite_CompressedFramesMatchTheServerForm`, `JournalExportParserParsesLiveFixtureAsTheGoldenRecords` | existing files |

## Approach

1. **Reader (Dev).** `load` adds `flagsCompact` to `known` and still requires KEYED-HASH. From the flag it sets three widths on
   the `File`: `payloadOff` (64 or 72), `itemWidth` (16 or 4) and `slotWidth` (8 or 4). `Refresh` recomputes them through
   `load`. Every place listed under *Problem* uses them:
   - the minimum size of a data object is `payloadOff`;
   - the size check in `walkChain` is `payloadOff + len(payload)`;
   - entry count = `(size - entryFixed) / itemWidth`, with the remainder checked and `count <= maxItems`;
   - array capacity = `(size - arrayFixed) / slotWidth`, with the remainder checked and the minimum size `arrayFixed + slotWidth`;
   - item and slot reads go through `itemOffset` and `arraySlot`.

   A 32-bit slot 0 is an empty item, as a 64-bit 0 is today. `itemBufSize` stays (512 × 16 covers 512 × 4). Payload and value
   caps, `maxArrays`, `maxChain` and the no-decompression rule of `Next` are unchanged. The tail fields are never read
   (option 11, record 0119). No exported API changes. The package comment of `journal.go` names both layouts.
2. **Writer (Dev).** `journaltest.Options.Compact` writes the systemd 255 compact layout:
   - a header of 272 bytes, incompat KEYED-HASH, ZSTD and COMPACT, compat TAIL-ENTRY-BOOT-ID;
   - the header tail fields (offset and count of the last array of the main chain, offset of the last entry);
   - data objects with the two 32-bit tail fields of their chain and the payload at 72;
   - 4-byte entry items without hashes, and 4-byte array slots.

   Arrays still double from 4, and the compression rules are unchanged. Without `Compact`, the output is byte-identical (the
   committed old fixtures must not change). The package comment names both layouts.
3. **Fixtures (Tester).**
   - Generated: `TestFixture_UpdateGolden` also writes the three files of `fixtureFiles` with `Compact: true` into
     `testdata/journal/compact/` (same names, same IDs).
   - Real samples: copy `/tmp/claude-0/-home-user-Vandox/0ebf782a-dde5-5bae-b9b0-1d2a5bfa91a4/scratchpad/anon/kernel.journal`
     and `…/anon/live.journal` byte for byte to `testdata/journal/systemd-255/kernel.journal` and `…/live.journal`, then check
     the sha256 of fact 11. They are committed at full size (8 MiB each, about 20 KB in Git), unmodified otherwise.
   - Append the missing `\n` to `testdata/journal/live.export`.
4. **Test helpers (Tester).**
   - `builder_test.go`: `bOptions.compact` builds compact files (header 272, flags, widths). `layout` keeps what the compact
     bounds cases need, the slot positions included. The 64-bit `mutated` cannot address a 4-byte slot, so the compact cases
     write 32-bit values.
   - kernevents `harness_test.go`: `loadCompactFixture` reads `testdata/journal/compact/` and wants 3 files. The comment
     "layout of systemd 249" names both layouts.
   - logship `world_test.go`: `journalSpec.compact` passes `Compact` to `journaltest.Options`.
   - `real_test.go`: `TestFixture_HoldsNoRealPrivateValue` also scans `testdata/journal/compact/` and
     `testdata/journal/systemd-255/`, with payloads at 72 when the COMPACT flag is set (runs only with
     `VANDOX_REAL_JOURNAL_DIR`).
5. AC2/AC3 pair sample entries with export entries by realtime and monotonic stamp, read from the committed exports, never
   hand-typed. The pinned numbers (counts, seqnums) are those of facts 7-9 for the files of fact 11.

No new user-visible output: the log lines and gap reasons stay as they are. A compact file is no longer
`unsupported layout`.

## Options considered

- Same reader, widths per file from the header flag (chosen). This is option 11 of record 0119.
- A second reader type for COMPACT. Lost because every bounds check would be duplicated (option 12).
- Reading the header and data-object tail fields to find the end of a chain. Lost because bisection is already a fixed number
  of reads and the fields add input to bound (option 13).
- Commit the samples cut after the last object. Lost because they would no longer be systemd's output (option 14).
- Ask the PM for clean samples (no duplicate entries, 21st live entry present) before planning. Lost because the samples are
  structurally sound real output, the duplicates make realtime run backwards within one seqnum ID (a case worth reading), and
  the counts are pinned per sha256. A clean regeneration later only changes the pinned numbers.
- Only generated compact fixtures, no real sample. Lost because the writer and the reader would share one reading of the
  format (the escalation rule for external formats).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| agent (Go) | `internal/journal/journal.go` | `flagsCompact`, compact constants, `File` widths, `load`, `itemOffset`, `arraySlot`, package comment |
| agent (Go) | `internal/journal/list.go` | `walkChain`, `samePayload`, `itemAfter`, `array`, `refreshList`, `listItem` use the widths |
| agent (Go) | `internal/journal/entry.go` | `readEntry`, `beginEntry`, `readItem` use the widths |
| agent (Go) | `internal/journal/entries.go` | `readEntryWith`, `readItemWith`, `readCompressedItem` use the widths |
| agent (Go) | `internal/journal/journaltest/journaltest.go`, `builder.go` | `Options.Compact`, compact writing, package comment |
| test data | `testdata/journal/compact/` (3 generated files), `testdata/journal/systemd-255/{kernel,live}.journal`, `testdata/journal/live.export` | new and fixed fixtures |

## Signatures (for the Dev's skeleton)

```go
// internal/journal/journal.go
const (
	flagsCompact     = 1 << 4
	dataFixedCompact = 72 // data object of COMPACT: the fixed part plus two 32-bit tail fields
	itemSizeCompact  = 4  // entry item of COMPACT: a 32-bit offset, no hash
	slotSize         = 8  // entry-array item of the non-compact layout
	slotSizeCompact  = 4
)

type File struct {
	// … existing fields …
	payloadOff uint64 // offset of the payload in a data object: dataFixed or dataFixedCompact
	itemWidth  uint64 // bytes of an entry item: itemSize or itemSizeCompact
	slotWidth  uint64 // bytes of an entry-array item: slotSize or slotSizeCompact
}

// itemOffset returns the data object offset in item i of the item table of an entry.
func (f *File) itemOffset(table []byte, i uint64) uint64

// arraySlot reads the entry offset in item k of the entry array at arr.
func (f *File) arraySlot(arr, k uint64) (uint64, error)
```

```go
// internal/journal/journaltest/journaltest.go
type Options struct {
	FileID, MachineID, SeqnumID [16]byte
	Online                      bool
	// Compress, when set, compresses the payloads of 512 bytes and more; nil keeps the raw-block frames.
	Compress func(payload []byte) ([]byte, error)
	// Compact writes the COMPACT layout of systemd 255 (272-byte header, 32-bit items) instead of the server's systemd 249 layout.
	Compact bool
}
```

Existing files the skeleton rewrites: none beyond adding these declarations. The bodies listed under *Affected projects* change
in step 6.

## Test files

`internal/journal/journal_test.go`, `internal/journal/entries_test.go`, `internal/journal/journaltest/writer_test.go`,
`internal/kernevents/position_test.go`, `internal/logship/journal_test.go`. Helpers adapted by the Tester:
`internal/journal/builder_test.go`, `internal/journal/real_test.go`, `internal/kernevents/harness_test.go`,
`internal/logship/world_test.go`.

Existing test call sites of a changed signature: none. `Options` gains a field and no exported signature changes, so the
existing composite literals compile unchanged.

Files no unit test can cover: none.

## Areas

- `docs/areas/agent.md` (owner Lead):
  - *Kernel events*, *Layout* (lines 897-906): both layouts, the compact widths, the 72-byte minimum data object, the header
    flag as the layout switch.
  - *Residuals (records 0119, 0120)* (line 1042): the COMPACT clause is dropped.
  - *Residuals (record 0121)* (line 1341): the COMPACT clause is dropped.
  - The 0119 summary in the record list (line 1922).
  - The test data enumeration of `internal/journal` (line 1957): the compact fixtures and the systemd 255 samples.

## Documentation updates

- `docs/areas/agent.md`: as above (Lead).
- `docs/ARCHITECTURE.md` line 54, "a bounded reader of the server's layout": becomes "of the two layouts journald writes" (Lead).
- `.squad/project.md` line 443, *Test doubles*, journald row (Tester): the writer of the non-compact **and** compact layouts,
  the fixtures `testdata/journal/compact/`, and the samples `testdata/journal/systemd-255/` with their origin
  (systemd-journal-remote 255 from the committed exports, header machine ID replaced). Security area 10 names no layout and
  stays.
- Package comments of `internal/journal/journal.go` and `journaltest/journaltest.go` (Dev).
- Sweep done: `agent.md` 889, 1004, 1105, 1279 and 1303 name the layout rules or reasons generically and stay. The
  systemd 249 statements in `detection-and-reports.md`, `log-import.md` and `agent.md` 658/1051/1954 are about other
  things. `deploy/agent/README.md` and `README.md` have no layout statement.

## Architecture check

The guarantees of `.squad/project.md` are unchanged: pure Go, no cgo, no program started, no `mmap`, memory independent of
the journal (fixed buffers; compact items are smaller), no decompression in `Next`, the zstd bound of `NextWith`, chains
only forward, and fail-closed for any other layout. `docs/ARCHITECTURE.md` gets only the wording update above.

## Security considerations

- [ ] Limits: 4,096 bytes of payload (`Next`) and `MaxPayload` (`NextWith`) count raw payload bytes, and for zstd items they
  count after decoding, as today. `maxItems` 512 counts items before reading their objects, `maxArrays` 64 counts arrays per
  chain, and `maxChain` 65,536 counts hash-chain steps. All are independent of the layout. Every compact offset, being 32-bit,
  passes the same `check` (aligned, `>= header size`, inside arena and file).
- [ ] Errors that reach a user-visible reason: `ErrUnsupported`, `ErrMalformed`, `ErrRead` with their fixed texts (agent.md
  *Logs*). No new error and no offset or value in a text.
- [ ] The layout flag is untrusted. A flipped flag reads the file with the other widths, which only changes which in-bounds
  bytes are read (AC4).

## Decision records

- `docs/decisions/0119-kernel-messages-from-the-binary-journal-files-read-in-pure-go-without-decompression.md` (Accepted,
  unreleased, extended in place): context, options 10-14, decision and consequences for COMPACT.
- `docs/decisions/0121-log-shipping-journal-and-syslog-files-positions-per-seqnum-id-zstd-commit-when-kept.md` (Accepted,
  unreleased): the COMPACT consequence updated.

## Out of scope / follow-ups

- Real compact files of the server (option A): the PM supplies them later. The check is `TestReal_NextWithDecodesEveryEntry`
  with `VANDOX_REAL_JOURNAL_DIR` on a developer machine (fact 15). `TestFile_RealServerJournals` pins the 249 files' counts and
  stays for them.
- Journals without keyed hashes, XZ/LZ4 items, and the 4 GiB size cap of compact files (no reader change needed).
- Optional, not blocking: regenerate the samples into fresh output files after the `live.export` fix, which changes only the
  pinned numbers and the sha256.

## Challenge notes

- minor: The plan gives the minimum size and the payload position of a data object, but not how many bytes are read to find the field name. `readItem` (`internal/journal/entry.go:115`) and `readItemWith` (`internal/journal/entries.go:91`) read `dataFixed+namePrefix` (64+64) bytes and take `head := obj[dataFixed:]`. If only the slice start moves to `payloadOff` (72), a compact file shows just 56 name bytes. A wanted field whose name is 56 to 64 bytes long (systemd allows 64) would then have no `=` in `head` and would be dropped without a sign. Instruction for the Dev: read `f.payloadOff+namePrefix` bytes in both functions (128 + 8 still fits `scratchSize` 256). Instruction for the Tester: add a compact case to the AC4/AC5 tests with a wanted field whose name is 64 bytes long, read through both `Next` and `NextWith`.

## Security notes

- non-blocking (plan, step 1, "`Refresh` recomputes them through `load`"): the widths are re-derived on every `Refresh`, but a `Cursor` keeps `arrCap`, which `array()` computed with the old `slotWidth`. If the COMPACT bit of an online file changes between `Open` and `Refresh`, `listItem` and `position()` reuse the stale `arrCap`. That happens with a hostile or replaced file; journald never does it. Example: capacity counted with 4-byte slots, then read with 8-byte slots. `arr + 24 + 8*k` can then reach up to about twice the array's size past its start. That is an unchecked offset, possibly beyond `declEnd`. The impact is low. The read goes into the fixed scratch buffer, and the value is then checked by `object()`. Rewriting an array's size in place can already cause a similar stale-capacity read today. Suggested fix: `Refresh` refuses a COMPACT bit that differs from the one seen at `Open` (`ErrMalformed`, keeping the previous state, as `Refresh` already does on error), with one case in AC4. Then a cursor never mixes widths. AC4's "flag flipped" case covers only a static file at `Open`, not a flip across `Refresh`.
