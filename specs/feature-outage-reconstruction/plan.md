# Plan: Outage reconstruction

Source: Issue #23 | [spec.md](spec.md)
Status: Draft
Tier: security — the change adds configuration options and a CLI sub-command whose input is parsed strictly (security
area 10, *CLI arguments and configuration*), prints victim commands taken from log text to the terminal (area 12), and
reads stored external text, including forged lines, under memory bounds it must keep (area 10); it also adds a schema
step.

## Problem / root cause

Feature, summarized in [spec.md](spec.md): a sub-command `vandoxd incidents` rebuilds incidents from everything stored:
OOM kills whose windows overlap form one incident, each incident gets a timeline, a structured cause hypothesis (top
three growth candidates, top three consumers, backup, boot and service activity), metric extremes, the connection peak
and template findings of its window, and is saved so that a re-run updates instead of duplicating.

Claims of the issue, checked against `main` 1aedd12:

- **Dependencies #20, #21, #22 are merged** — confirmed. #20: `LegacyMonitorParser` emits `metric`, `process_snapshot`
  and `connection_snapshot` records with source `legacy-monitor` (`docs/areas/log-import.md`, *Legacy monitoring log*,
  *Summary metrics*, *Process snapshot*, *Connection snapshot*). #21: `SignatureDetector` with the catalog
  `src/Vandox.Core/Signatures/signatures.yaml` (`oom_kill` with `victim_pid`, `victim_command`, `anon_rss_bytes`,
  `oom_score_adj`; `boot`; `service_start`, `service_stop`, `service_failed`; MariaDB, mail and disk signatures); nothing
  calls it (`grep SignatureDetector src/` finds only its own folder). #22: `ILogTemplateReader` with `NewTemplatesAsync`
  and `TemplateSpikesAsync`, implemented by `SqliteStore`.
- **"Collect boots"** — confirmed only as `boot` signature events on kernel lines: no parser emits a `kernel_event`
  record (`new KernelEvent` appears nowhere in `src/`; the type is read only by `RecordQueries`). Agent `kernel_event`
  records come with #36 and are left to #53.
- **"Process and connection history"** — confirmed, from the legacy log only (no agent yet). Snapshot interval
  (five, later one minute) is the issue #20 description, *unverified* against the real file, like the line shapes
  (`log-import.md` says so).
- **"Nightly Plesk backup (`pzstd`, ~200 MiB) … catch-up backup after a reboot"** — a statement about the server's data,
  *unverified* here; `docs/RUNBOOK_OOM_RECOVERY.md` line 11 repeats the 2 Oct 06:51/06:59 chain. It is the only evidence
  for a backup program name, so the built-in list is `pzstd` only (record 0096, option 12).
- **"Incidents are stored"** — nothing stores them: the schema is at version 8 (`StorageLimits.SchemaVersion`), no
  table holds incidents; this plan adds step 9. `docs/ARCHITECTURE.md` already lists incidents with unlimited
  retention.
- **Paging through lines (record 0094, consequences)** — confirmed: `RecordQuery` (`src/Vandox.Storage/RecordQuery.cs`)
  has no keyset and `RecordQueries.Build` orders by `captured_at, id` with `LIMIT` only, so a caller cannot page past a
  run of lines that share a capture time. This plan adds `RecordQuery.AfterId`.

Related defects found on the way (fixed in this change):

- `docs/ARCHITECTURE.md`, *Storage and retention*, *Interfaces*, named the Go-era types `store.Writer`, `store.RecordReader`,
  `store.LogSearcher`, `store.ImportTracker` and a fake in `store/storetest`, none of which exist in the C# store;
  rewritten by the Lead in this plan step.
- `tests/Vandox.Storage.Tests/SqliteStoreOpenTests.cs` line 353 pins schema version `"8"` literally and lines 361-365
  version 9 as "too new"; the migration tests at lines 164, 192, 226, 271, 312 and 344 undo the later steps by hand.
  Step 9 makes all of them fail at run time (re-running step 9 meets an existing `incidents` table); adapted by the
  Tester (*Test files*).
- `docs/areas/detection-and-reports.md`, *Not here*, points incidents at "issue #23"; replaced by the new section.
- `src/Vandox.Storage/RecordQueries.cs` line 274 (`ReadJson`) reads a payload as a UTF-16 string and lets
  `JsonDocument.Parse(string)` transcode it again: three copies of a payload that can reach ≈ 100 MiB of stored JSON
  (*Memory*); fixed in this change (read the UTF-8 bytes). The write side (`BatchWriter` line 105,
  `PayloadRegistry.Serialize` to a string) has the same shape and goes to a follow-up issue (*Out of scope*).

## Acceptance criteria

Every criterion names its tests; test classes are in *Test files*.

- [ ] **AC1 — one incident per kill, overlapping windows merged.** A kill at `t` has the window
  `[t − before, t + after)`, both ends clipped to the storable range. Kills arrive in time order; a kill joins the open
  incident when its window start is before the incident's end, which then becomes the kill's window end; otherwise the
  open incident is closed and the kill opens the next. Windows that only touch (`t₂ − before == t₁ + after`) are two
  incidents. Copies of one kill line (syslog, kern.log, journal within 60 s) are one kill (the detector's merge).
  An incident keeps its first 16 kills and counts all.
  - `KillClustererTests.KillClustererMergesKillsWhoseWindowsOverlap` (kills at 00:00, 01:20 and 03:31 with before 1h,
    after 30m: two clusters, `[−01:00, 01:50)` with 2 kills and `[02:31, 04:01)` with 1; first and last kill times)
  - `KillClustererTests.KillClustererSeparatesWindowsThatOnlyTouch` (kills at 00:00 and 01:30, before 1h, after 30m)
  - `KillClustererTests.KillClustererClipsWindowsToStorableRange` (a kill at `StorableTime.Min`: `Start == Min`; a kill
    one tick before `StorableTime.Max`: `End == Max` and the kill lies in `[Start, End)`; a kill at `StorableTime.Max`
    is ignored: `Add` returns null and neither opens nor extends a cluster, `Complete` then returns the earlier cluster
    unchanged)
  - `KillClustererTests.KillClustererKeepsSixteenKillsAndCountsAll` (20 kills one minute apart: `Kills.Count == 16`,
    `KillCount == 20`, the kept ones are the first 16)
  - `KillClustererTests.KillClustererRefusesWrongInput` (an event whose `SignatureId` is not `oom_kill`:
    `ArgumentException`; a kill earlier than the previous: `InvalidOperationException` with
    `incidents: kills must be added in time order`; `Add` or `Complete` after `Complete`: `InvalidOperationException` with
    `incidents: already complete`)
  - `KillClustererTests.KillClustererMapsKillFields` (`IncidentKill.From`: victim pid, command, anon-RSS,
    `oom_score_adj`, host, first 16 record IDs, line count; absent fields stay null / empty)
  - `IncidentReconstructorTests.IncidentReconstructorCreatesOneIncidentPerKill` (one kill stored as a `syslog`, a
    `kern.log` and a `journal` line within 60 s and a second kill 3 h later: two stored incidents, kill count 1 each)
- [ ] **AC2 — timeline and top three growth candidates.** Built by `IncidentBuilder` from the records of the window
  (rules in *Approach*, *Incident content*).
  - `IncidentBuilderTests.IncidentBuilderOrdersTimeline` (events, backup entries; order by time, type, first record ID;
    events outside the window ignored)
  - `IncidentBuilderTests.IncidentBuilderCapsTimelineAndTemplates` (1,005 small events: 1,000 kept, `TimelineOmitted == 5`;
    25 new templates and 25 spikes: the first 20 of each kept)
  - `IncidentBuilderTests.IncidentBuilderCapsTimelineByteBudget` (events whose entries serialize to about 20 KB each
    (two 1,024-byte texts and a 1,024-byte host of `<`): the kept entries are a prefix in arrival order, the sum of
    their `IncidentJson.Utf8Length` is at most `IncidentLimits.MaxTimelineBytes` and adding the next one would exceed
    it; a later small event after the first one that did not fit is omitted too; `TimelineOmitted` counts every omitted
    event)
  - `IncidentBuilderTests.IncidentBuilderRanksTopThreeGrowthCandidates` (five programs over three lookback snapshots, no
    boot: growth = last − first, a program absent from the first snapshot grows from 0, a shrinking or unchanged program
    is no candidate, ties by program name ordinal, at most three; consumers = top three RSS of the last snapshot)
  - `IncidentBuilderTests.IncidentBuilderStartsGrowthAfterLastBoot` (boots at −40m and −15m before the first kill,
    snapshots at −50m, −20m, −15m (exactly at the boot), −10m and −2m, the −50m and −20m ones holding large programs
    absent later: growth from −10m to −2m only, `FirstSnapshotAt` −10m, `SnapshotCount == 2`, `RecentBootAt` −15m,
    consumers from −2m; with the −2m snapshot removed: `GrowthKnown` false, consumers from −10m; with only the −50m and
    −20m snapshots: `SnapshotCount == 0`, consumers empty, `BackupRunning` null)
  - `IncidentBuilderTests.IncidentBuilderRefusesEventsAfterProcessSnapshots` (`AddEvent` after `AddProcessSnapshot`:
    `InvalidOperationException` with `incidents: events must be added before process snapshots`)
  - `IncidentBuilderTests.IncidentBuilderUsesOnlyLookbackSnapshotsForGrowth` (a snapshot at the first kill's time and one
    after it do not count; `FirstSnapshotAt`, `LastSnapshotAt`, `SnapshotCount`)
  - `IncidentBuilderTests.IncidentBuilderMarksGrowthUnknownWithFewerThanTwoSnapshots` (one snapshot: `GrowthKnown`
    false, no growth, consumers from it; none: consumers empty, `BackupRunning` null)
  - `IncidentBuilderTests.IncidentBuilderRecordsBackupBootAndServiceActivity` (`pzstd` and `/usr/bin/pzstd` found,
    `BackupRunning` true with `BackupPrograms == ["pzstd"]`, `backup_first_seen` and `backup_last_seen` entries, only the
    first when one snapshot; a backup seen only in a snapshot before the last boot: `BackupRunning` false, but the
    timeline still has its `backup_first_seen`; `RecentBootAt` the last boot before the first kill; `ServiceEvents`
    counts start, stop and failed before the first kill only)
  - `IncidentBuilderTests.IncidentBuilderKeepsMetricExtremes` (the five definitions; labels must match exactly; ties keep
    the earliest; a non-finite value is not counted; a definition without samples is absent)
  - `IncidentBuilderTests.IncidentBuilderKeepsConnectionPeak` (sockets = sum of `states` counts; ties keep the earliest;
    top three processes by summed count, then command; no snapshot: `Connections` null)
  - `IncidentBuilderTests.IncidentBuilderBuildsOnce` (`Build` twice: `InvalidOperationException` with
    `incidents: already built`)
  - `MemoryAnalysisTests.MemoryAnalysisNormalizesProgramKeys` (`/usr/sbin/mariadbd` → `mariadbd`; `mariadbd`,
    `[kworker/0:1]`, `php-fpm: pool`, `/`, `/usr/bin/` unchanged)
  - `MemoryAnalysisTests.MemoryAnalysisSumsProgramsAndFallsBackToProcesses` (two programs with one key are summed;
    a snapshot without `programs` is aggregated from `processes` by the first word of `command`; sums saturate at
    `ulong.MaxValue`)
  - `MemoryAnalysisTests.MemoryAnalysisRanksGrowthAndConsumers`
  - `BackupProgramsTests.BackupProgramsFindsPzstdOnly` (`pzstd` found; `zstd`, `pzstd2`, `PZSTD` not)
  - `IncidentSignaturesTests.IncidentSignaturesNameBuiltInSignatures` (every constant is the `Id` of a signature in
    `BuiltInSignatures.Catalog`)
  - `IncidentReconstructorTests.IncidentReconstructorBuildsIncidentFromStoredRecords` (stored kill line, boot line,
    systemd lines, legacy-style process snapshots with `pzstd`, metrics, a connection snapshot and template statistics:
    the stored incident has the timeline entries, growth top three, consumers, backup, boot, metric extremes, peak and
    template lists)
- [ ] **AC3 — re-running updates instead of duplicating.**
  - `SqliteStoreIncidentTests.SqliteStoreSaveIncidentCreatesUpdatesAndLeavesUnchanged` (first save `Created`; equal
    incident `Unchanged` with `updated_at` untouched; changed content `Updated` with the same ID)
  - `SqliteStoreIncidentTests.SqliteStoreSaveIncidentMergesOverlappingIntoOldest` (stored #1 and #2 overlapped by one
    incident: kept ID 1, `Merged == 1`, #2 gone; a stored incident that only touches is not overlapped)
  - `SqliteStoreIncidentTests.SqliteStoreSaveIncidentNeverReusesNumbers` (the highest ID merged away, the next new
    incident gets a higher ID)
  - `SqliteStoreIncidentTests.SqliteStoreSaveIncidentRefusesInvalidIncident` (exact texts in *Approach*, *Storage*; among
    them a first kill equal to `End`, e.g. `End == FirstKillAt == StorableTime.Max`: `the first kill must lie in the
    window`)
  - `SqliteStoreIncidentTests.SqliteStoreSaveIncidentAcceptsWorstCaseIncident` (pins the size bound against escaping;
    the incident is built by `IncidentBuilder`, so its caps apply: 1,100 events with two 1,024-byte texts of `<`, a
    1,024-byte host of `<` and 256 record IDs of `long.MaxValue`; a cluster of 16 kept kills (20 counted) with
    1,024-byte victims and hosts of `<` and 16 record IDs of `long.MaxValue`; two process snapshots after the last boot
    with 4,096 programs each named by 1,024 bytes of `<` plus a distinct suffix, so growth and consumers are full, and
    `pzstd`; a connection snapshot with three process commands of 1,024 bytes of `<`; samples for all five metric
    definitions with their labels and a 128-byte source; 25 new templates and 25 spikes with 1,024-byte template and
    program texts and a 128-byte source of `<`: `SaveIncidentAsync` returns `Created`, the stored `data` is at most
    `StorageLimits.MaxIncidentDataBytes` UTF-8 bytes; the test first asserts that the serialized JSON holds the six
    characters backslash, `u003C` (in C# `"\\u003C"`), so an encoder that stops escaping does not let it pass with a
    smaller document than the one it is meant to pin)
  - `SqliteStoreIncidentTests.SqliteStoreIncidentsReadsOverlappingInOrder` (range and limit rules, order by start then
    ID, the JSON read back equals the saved incident; `CountIncidentsAsync`)
  - `IncidentReconstructorTests.IncidentReconstructorRerunUpdatesInsteadOfDuplicating` (run; a second run without new
    data: all unchanged; add a second kill in the window and a third kill whose window bridges two incidents; run:
    the first incident updated with the same ID and kill count 2, the bridged pair merged into the older ID,
    `Merged == 1`, no other new incident)
  - `IncidentReconstructorTests.IncidentReconstructorKeepsStoredIncidentsWithoutMatch` (a stored incident whose window
    no rebuilt incident overlaps is kept unchanged and counted as kept)
  - `IncidentReconstructorTests.IncidentReconstructorSkipsInvalidIncidentAndContinues` (three kills 3 h apart; a
    hand-written `IIncidentStore` decorator around the real store throws `InvalidIncidentException` for the second
    save: the run completes, `Skipped == 1`, the first and third incidents are stored and passed to `saved`, the second
    is not; warning `IncidentSkipped` logged with its first kill time)
  - `IncidentReconstructorTests.IncidentReconstructorFailsWhenPageDoesNotAdvance` (a hand-written `IRecordReader` that
    returns the same full page twice: `StoreException` with `store: reading records: page did not advance`)
  - `IncidentReconstructorTests.IncidentReconstructorClipsSpikeRangeToMaxHours` (kills chained 40 h apart with before
    and after 24 h, 20 kills: one incident whose window exceeds 744 h; a recording `ILogTemplateReader` decorator around
    the real store: the spike query's `From` is the incident start and its `To` is the start plus 744 h, the new
    template query covers the whole window)
- [ ] **AC4 — configurable, strictly validated window.**
  - `ConfigDurationTests.ConfigDurationAcceptsMinutesAndHours` (`1m`, `30m`, `0090m`, `1h`, `24h`, `9999h`)
  - `ConfigDurationTests.ConfigDurationRefusesOtherForms` (`""`, `m`, `60`, `1`, `1.5h`, `+1h`, `-1h`, `1H`, `1M`,
    `1 h`, ` 1h`, `1h `, `1h30m`, `1d`, `1s`, `12345m`, `1hh`, `h1`, `0x1h`, `1_0m`)
  - `ConfigDurationTests.ConfigDurationRefusesNonAsciiDigits` (two data rows built with C# escapes: `"\u0661h"` (Arabic-Indic
    digit one, U+0661) and `"\uFF11h"` (fullwidth digit one, U+FF11); the test first asserts `input.Length == 2` and that
    `input[0]` is above U+007F, so an input that lost its backslash fails the precondition, not the guard)
  - `IncidentConfigTests.IncidentConfigParsesWindows` (defaults `1h` and `30m` give 60 and 30 minutes; an unvalidated
    value makes `GetWindowBefore` throw `InvalidOperationException`)
  - `BackendConfigLoaderTests.BackendConfigLoaderReadsIncidentWindows` (`incidents:` with `window_before: 2h` and
    `window_after: 15m`; quoted `"1h"`; `incidents:` null keeps the defaults)
  - `BackendConfigLoaderTests.BackendConfigLoaderRefusesInvalidIncidentWindows` (refused: `0m`, `25h`, `1441m`, `60`,
    `1.5h`, `window_after: 0m`; accepted at the bounds: `1m`, `24h`, `1440m`; exact text
    `config: <file>:<line>: incidents.window_before: must be a whole number of minutes or hours from 1m to 24h, such as 30m or 1h`
    and the same with `incidents.window_after`; a null value: `has no value`)
- [ ] **AC5 — the sub-command.**
  - `CommandLineTests.CommandLineParsesIncidentsSubCommand` (`incidents`, `-config x incidents`, `incidents -config x`,
    `incidents -config=x`, `incidents -h`, `incidents extra` → `vandoxd incidents: no argument is expected`,
    `incidents -verbose` → `flag provided but not defined: -verbose`, `-healthcheck incidents` →
    `unexpected argument "incidents"`, `incidents -config` → `flag needs an argument: -config`)
  - `IncidentSummaryWriterTests.IncidentSummaryWriterFormatsIncidentLines` (exact lines; a victim with `\n`, `\u001b` and
    `\u202e` (U+202E) is quoted by `Terminal.Quote` (the test first asserts that its victim text holds U+001B and U+202E); no kill victim: `victim=-`; `merged=` only when above 0)
  - `IncidentSummaryWriterTests.IncidentSummaryWriterWritesCounts` (exact block in *Approach*, *Command*, `skipped`
    included)
  - `IncidentsCommandTests.IncidentsCommandPrintsIncidentsAndExitsZero` (through `BackendApp.RunAsync` with a
    temporary configuration and database holding one kill)
  - `IncidentsCommandTests.IncidentsCommandExitsOneOnInvalidConfiguration`
  - `IncidentsCommandTests.IncidentsCommandExitsOneWhenInterrupted` (cancelled token: exit 1, warning logged)
- [ ] **AC6 — paging by record ID (record 0094's open point).**
  - `SqliteStoreQueryTests.SqliteStoreRecordsPagesAfterId` (five log lines with one capture time and two after it,
    limit 2: pages with `From` = last capture time and `AfterId` = last ID return every record once, in order; the
    metric-series form likewise)
  - `SqliteStoreQueryTests.SqliteStoreRecordsRefusesNegativeAfterId` (exact text
    `store: invalid query: after id must not be negative`)
- [ ] **AC7 — schema step 9.**
  - `SqliteStoreOpenTests.SqliteStoreOpenMigratesVersionEightAndAddsIncidentTable` (table `incidents`, index
    `incidents_started_at`, no rows; a stored line is kept; version 10 refused with
    `store: schema version 10 is not supported, this build uses 9`)
- [ ] **AC8 — memory bound.**
  - `IncidentReconstructorTests.IncidentReconstructorReadsSnapshotsOnePerPage` (step 5; a recording `IRecordReader`
    decorator around the real store, two incidents with three process and two connection snapshots each: every
    `process_snapshot` and `connection_snapshot` query has `Limit == 1`, every `log_line` and `metric` query
    `Limit == 1000`, and each snapshot is read exactly once per incident window — the reconstructor holds at most one
    decoded snapshot page)
  - `SqliteStoreQueryTests.SqliteStoreRecordsRoundTripsEscapedPayloadText` (step 5; a process snapshot whose command and
    cmdline hold `<`, `ä`, U+202E and U+1F600 (built with C# escapes; the test first asserts the cmdline holds a
    surrogate pair) is read back equal through the UTF-8 byte path of `ReadJson`; a payload-less kind and a stored
    JSON `null` behave as before)
  - `IncidentBuilderTests.IncidentBuilderRetainsBoundedMemory` (coverage step 6, by the Tester; it cannot fail against
    the skeleton in a meaningful way: 100,000 events with 1,024-byte texts and 256 record IDs and 1,000 snapshots of
    4,096 programs: retained bytes below a bound sized from the caps, measured with the `RetainedBytes` helper pattern
    of `KernelReportGrouperTests` and its `S1215` pragma)

## Approach

### Configuration

- New section `incidents` with `window_before` (default `1h`) and `window_after` (default `30m`), string options of
  the existing strict decoder (no decoder change). `ConfigDuration.TryParse` accepts exactly **1 to 4 ASCII digits
  `0`–`9` followed by one `m` (minutes) or `h` (hours)**, nothing before or after; leading zeros are allowed. It scans
  ordinally and calls no parsing API, so no culture, sign, white space, other digit script, fraction or second unit
  reaches a conversion. `BackendConfigLoader.Validate` then requires 1 minute to 24 hours for each option, else
  `ConfigException.ForKey` with the reason `must be a whole number of minutes or hours from 1m to 24h, such as 30m or 1h`.
- Forms the YAML layer can hand over, and what the guard does with the decoded text: a plain scalar (`1h`), a single-
  or double-quoted one (`"1h"`; escapes such as `"\x31h"` are decoded by YAML to `1h` first and accepted, which is
  harmless), a folded or literal block scalar (`>-` gives `1h`; `|` keeps a line feed, which the decoder refuses as a
  control character before the guard), an integer (`60`, refused for the missing unit), null (decoder: `has no value`),
  a sequence or mapping (decoder: `invalid value, want a string`). The guard sees only the decoded string.
- `BackendConfig.Keys()` appends `incidents.window_before` and `incidents.window_after` (after `import.time_zone`).

### Reconstruction (`IncidentReconstructor.RunAsync`)

1. **Pass 1:** read every log line (`RecordQuery` kind `log_line`, no source, `From = StorableTime.Min`,
   `To = StorableTime.Max`, `Limit = 1000`, then `From` = the last record's capture time and `AfterId` = its ID) and
   feed it to one `SignatureDetector` over `BuiltInSignatures.Catalog`; count the lines. Each closed event with
   `SignatureId == "oom_kill"` goes to a `KillClusterer`; a cluster it closes (and the last one, after `Complete`) is
   built and saved at once (step 2), so one open incident exists at a time.
2. **Per cluster:** a fresh `IncidentBuilder(cluster, before, after)`; then, all over `[cluster.Start, cluster.End)`
   with the same keyset paging:
   - log lines (page 1,000) through a fresh `SignatureDetector`; every closed event → `AddEvent`;
   - all log lines first (the builder refuses an event after a process snapshot, because the last boot decides which
     snapshots count);
   - then `process_snapshot` records (page **1**) → `AddProcessSnapshot`; `connection_snapshot` records (page **1**) →
     `AddConnectionSnapshot`; `metric` records (page 1,000; kind-wide, no name) → `AddMetric`;
   - `NewTemplatesAsync` over the window, limit 20 → `AddNewTemplate`; `TemplateSpikesAsync` from the window start to
     `min(End, Start + 744 h)` (`StorageLimits.MaxSpikeRangeHours`), default parameters, limit 20 → `AddTemplateSpike`;
   - `Build()` → `IIncidentStore.SaveIncidentAsync(incident, clock.GetUtcNow())` → the `saved` callback. An
     `InvalidIncidentException` is not fatal: the incident is skipped, `Skipped` counted, `IncidentSkipped` (Warning,
     EventId 33, `incident skipped {FirstKillAt} {Error}`, the store's text, no input text) logged, and the run goes on;
     the stored incidents it would have overlapped stay as they are and count as kept.
3. After pass 1: `Kept = CountIncidentsAsync() − (Created + Updated + Unchanged)`.
4. **Progress guard** for every keyset loop: after a full page, the next `(From, AfterId)` must be strictly after the
   previous one in the order (capture time, ID); otherwise `StoreException` `store: reading records: page did not
   advance`. It catches a store whose capture times are not whole ticks (the keyset would repeat a page forever).
5. Cancellation is checked between pages and incidents (`OperationCanceledException` propagates); any other
   `StoreException` propagates. Each incident is its own transaction: an interrupted run keeps the incidents it saved.

**Memory:** one page of records, two detectors (each at most 1,024 open events), one open cluster (16 kills), one
builder (at most 1,000 events within a 4 MiB serialized budget, two program maps of at most 4,096 entries, one
connection peak, 20 + 20 templates). It does not grow with the number of stored lines, kills or incidents.

The largest single read is one process snapshot. Its worst case, for the only writer today (the legacy parser: 4,096
rows with `command` ≤ 1,024 bytes, `cmdline` ≤ 2,048 bytes, `user` and `state` ≤ 64 bytes, and 4,096 programs of
≤ 1,024 bytes), when every text is `<` and the default encoder writes each as six bytes (backslash, `u003C`): about
4,096 × 19.5 KB + 4,096 × 6.3 KB ≈ 105 MB (≈ 100 MiB) of stored JSON. (An agent snapshot is bounded by the 16 MiB wire
batch; re-escaped on storing it reaches the same order.) Reading it as the code does on `main` (`RecordQueries.ReadJson`:
`GetString` gives a UTF-16 copy of ≈ 200 MiB, `JsonDocument.Parse(string)` a UTF-8 copy of ≈ 100 MiB) on top of SQLite's
native copy of the column (≈ 100 MiB) and the decoded objects (≈ 36 MiB in UTF-16) peaks at ≈ 440 MiB, which in the
512 MiB container (`deploy/backend/docker-compose.yml` line 36, shared with the running service, since `docker exec`
joins its cgroup) is not safe. This plan therefore changes `ReadJson` to read the column's UTF-8 bytes
(`reader.GetFieldValue<byte[]>(10)`, which returns the stored UTF-8 bytes of a `TEXT` column — checked in a scratch
program against Microsoft.Data.Sqlite 10.0.12) and parse with `JsonDocument.Parse(ReadOnlyMemory<byte>)`, which does
not copy. The peak becomes native ≈ 100 MiB + bytes ≈ 100 MiB + document metadata ≈ 2 MiB + objects ≈ 36 MiB
≈ **240 MiB** for one forged maximum snapshot, with a page of 1; a connection snapshot is smaller (≈ 60 MB of stored
JSON at most). The residual — a forged maximum snapshot leaves the service about half the container's memory while it
is read — is stated in record 0096 and *Limits of incidents*; it needs a forged legacy monitoring log at every cap. The
import that stores such a snapshot already peaks higher (`PayloadRegistry.Serialize` builds a UTF-16 string of
≈ 200 MiB); that is pre-existing and goes to a follow-up issue (*Out of scope*).

**Time** grows with all stored log lines (pass 1) plus, per incident, the data of its window, so a flood of forged kills
far enough apart to form many incidents multiplies the window reads; the spike query's cost is not bounded by its range
(record 0095) — the sub-command runs off any request path.

### Incident content (`IncidentBuilder`)

- **Lookback** = `[Start, FirstKillAt)` (Start is the first kill's window start). Boots and service events before the
  first kill are judged on the lookback; the timeline covers the whole window.
- **Snapshot period** = the lookback after its last boot: `(RecentBootAt, FirstKillAt)` when the lookback holds a
  `boot` event, else the whole lookback. A snapshot captured at or before the last boot describes processes that the
  reboot ended, so it never serves as the growth baseline (the 2 Oct chain of `docs/RUNBOOK_OOM_RECOVERY.md`: reboot
  06:51, kill 06:59, both inside the default one-hour lookback). Agent `kernel_event` boots are not read (#53); a reboot
  without a stored boot line is not seen (*Limits of incidents*).
- **Timeline:** every detected event of the window (`TimelineEntry.From`: time of its first line, signature ID as
  `type`, host, texts, numbers, first 16 record IDs, line count), a prefix in arrival order that holds at most 1,000
  entries and at most `IncidentLimits.MaxTimelineBytes` (4 MiB) counted as `IncidentJson.Utf8Length` of each entry:
  the first entry that would exceed either cap and every later one are counted in `TimelineOmitted` and not kept;
  plus `backup_first_seen` and `backup_last_seen` (outside the budget, fixed size; the first and last process snapshot of the window
  holding a backup program; only the first when it is the same snapshot; `texts.program` = the first backup name found,
  `numbers.rss_bytes` = the backup programs' summed RSS, the snapshot's record ID). Sorted by time, then `type`
  ordinal, then first record ID.
- **Programs of a snapshot** (`MemoryAnalysis.Programs`): its `programs` entries, or, when it has none, its processes
  grouped by the first space-separated word of `command` (the command itself when that word is empty), keyed by
  `ProgramKey` (a name starting with `/` becomes the part after its last `/`, unless that part is empty), RSS and
  counts summed (saturating).
- **Hypothesis:** the first and last process snapshot of the **snapshot period** (by capture time, then record ID) and
  their number. Growth candidates (`GrowthKnown` = at least two snapshots there): programs of the last snapshot whose
  RSS is above their RSS in the first (0 when absent there), by growth descending, then key ordinal, top three.
  Consumers: the last snapshot's programs by RSS descending, then key ordinal, top three (empty without snapshot).
  `BackupRunning`: true when a snapshot of the period holds a backup program, false when snapshots exist there and none
  does, null without snapshot; `BackupPrograms` the names seen there in list order. `RecentBootAt`: the time of the
  last `boot` event in the lookback, else null. `ServiceEvents`: the number of `service_start`, `service_stop` and
  `service_failed` events in the lookback. Because the period depends on the last boot, every `AddEvent` precedes the
  first `AddProcessSnapshot` (else `InvalidOperationException` `incidents: events must be added before process
  snapshots`); among snapshots the order does not matter.
- **Metrics** (fixed definitions, in this order; labels must equal exactly; the strictly lower/higher value replaces,
  so ties keep the earliest; non-finite values are skipped; a definition without samples is absent):
  `mem_available_bytes` min; `swap_used_bytes` max; `pressure_avg10` {`resource: memory`, `kind: some`} max;
  `pressure_avg10` {`resource: memory`, `kind: full`} max; `load1` max. Each with value, time, source and sample count.
- **Connections:** the snapshot of the window with the most sockets (sum of its `states` counts; strictly more
  replaces), its time and record ID, the number of snapshots, and its top three processes by count summed per
  `command`, then command ordinal; null without a snapshot.
- **Templates:** new templates (source, program, template, first seen, count) and spikes (source, program, template,
  hour, count, usual level), the first 20 of each as the queries return them.
- **Kills at the end of the storable range:** every query range is half-open with `To` at most `StorableTime.Max`, so a
  line captured at exactly `StorableTime.Max` is never read; `KillClusterer.Add` ignores such a kill (returns null,
  counts nothing), so no window `[t − before, Max)` can hold a kill at its own end. A kill one tick earlier gets
  `End = Max` and lies inside.
- **Size bound.** The incident is serialized with `IncidentJson.Options` (the `System.Text.Json` default encoder, as
  for payloads, no indentation) in the builder's budget and in `SaveIncidentAsync` alike. Worst case with every text
  six bytes per character after escaping and every field at its model cap (host, victim, program, command, template
  ≤ 1,024 bytes: `Check.Short`; source ≤ 128): timeline ≤ 4 MiB + 1,000 separators; 16 kills × ≈ 12.9 KB ≈ 0.21 MB;
  growth and consumers 6 × ≈ 6.3 KB; connections 3 × ≈ 6.2 KB; metrics 5 × ≈ 1 KB; 40 templates × ≈ 13.2 KB ≈ 0.53 MB;
  two backup entries < 1 KB — together below 5 MiB, so under `MaxIncidentDataBytes` (8 MiB) with margin.
  `SqliteStoreSaveIncidentAcceptsWorstCaseIncident` pins it.

### Storage

- Schema step 9, in one transaction:
  `CREATE TABLE incidents (id INTEGER PRIMARY KEY AUTOINCREMENT, started_at INTEGER NOT NULL, ended_at INTEGER NOT NULL CHECK (ended_at > started_at), first_kill_at INTEGER NOT NULL, kill_count INTEGER NOT NULL CHECK (kill_count > 0), created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, data TEXT NOT NULL) STRICT`
  and `CREATE INDEX incidents_started_at ON incidents(started_at)`. `AUTOINCREMENT` so that a number merged away is never
  given to a later incident. `StorageLimits.SchemaVersion = 9`.
- `SaveIncidentAsync` (writer lock, one immediate transaction): validate; serialize the incident with
  `IncidentJson.Options` (the `JsonPropertyName` attributes, no indentation); select the stored rows with
  `started_at < $end AND ended_at > $start ORDER BY id`; none → insert (`created_at = updated_at = savedAt`),
  `Created`; else keep the lowest ID, delete the others (`Merged` = their number), and if nothing was deleted and
  `started_at`, `ended_at`, `first_kill_at`, `kill_count` and `data` are equal, `Unchanged` without a write, otherwise
  update every column but `id` and `created_at` (`updated_at = savedAt`), `Updated`.
- Validation (`InvalidIncidentException : StoreException`, exact texts): `store: invalid incident: start and end must be
  within the storable range`; `store: invalid incident: start must be before end`; `store: invalid incident: the first kill
  must lie in the window`; `store: invalid incident: kill count must be at least 1`; `store: invalid incident: saved at
  must be within the storable range`; `store: invalid incident: data is larger than 8388608 bytes` (UTF-8 bytes of the
  serialized JSON, `StorageLimits.MaxIncidentDataBytes`). A null incident: `ArgumentNullException`.
- `IncidentsAsync`: rows with `started_at < $to AND ended_at > $from`, `ORDER BY started_at, id LIMIT $limit`, the
  window rules of `RecordQueries.ValidateWindow`; a `data` that does not deserialize: `StoreException`
  `store: reading incidents: decoding incident data`. `CountIncidentsAsync`: `SELECT count(*) FROM incidents`.
- SQLite errors: `StoreException` with `store: saving incident: <message>` / `store: reading incidents: <message>`.
- `RecordQuery.AfterId` (default 0): when above 0, records whose capture time equals `From` and whose ID is at most
  `AfterId` are skipped: `AND (r.captured_at > $from OR r.id > $after)` (metric-series form with `m.captured_at` and
  `m.record_id`). Negative: `store: invalid query: after id must not be negative`. Every writer stores capture times in
  whole 100 ns (`DateTimeOffset` ticks), so the capture time a reader returns equals the stored one; the reconstructor's
  progress guard turns a violation into an error instead of an endless loop.
- `RecordQueries.ReadJson` reads the payload column as UTF-8 bytes (`GetFieldValue<byte[]>(10)`) and parses it with
  `JsonDocument.Parse(ReadOnlyMemory<byte>)`; `NULL` keeps parsing `null`. Results are unchanged for every reader; the
  UTF-16 copy and the transcoded copy of the old path go away (*Memory* above).

### Command

- `vandoxd [flags] incidents [-config file]`; flags after `incidents`: `-config`, `-h`/`-help`; any positional argument:
  `vandoxd incidents: no argument is expected`. `CommandLine.Usage(string usageFor)` replaces `Usage(bool forImport)`:
  `"import"` gives the import usage unchanged, `"incidents"` gives
  `Usage of vandoxd incidents [-config file]:\n  -config string\n    \tpath of the configuration file\n`, anything else the
  general usage, which gains after the import lines
  `  vandoxd [flags] incidents [-config file]\n    \trebuild the incidents from the stored data\n`.
- `CommandOptions.Incidents` is set and `UsageFor` is `"incidents"` once the positional `incidents` is parsed;
  `BackendApp.RunAsync` prints `CommandLine.Usage(options.UsageFor)` for help and usage errors and routes
  `options.Incidents` to `IncidentsCommand.RunAsync` after the version check, like the import.
- `IncidentsCommand.RunAsync`: load the configuration (`ConfigurationInvalid`, exit 1), open the store
  (`OpeningDatabaseFailed`, exit 1), log `IncidentsStarted`, run the reconstructor writing
  `IncidentSummaryWriter.FormatIncident(saved)` plus `\n` per saved incident to standard output, then the summary;
  exit 0. A `StoreException` during the run: `IncidentsFailed`, exit 1. Cancellation: `IncidentsInterrupted`, exit 1,
  no summary.
- Incident line (exact): `#<id> <created|updated|unchanged> <first kill, UTC, yyyy-MM-ddTHH:mm:ssZ, invariant> kills=<kill count> victim=<Terminal.Quote(first kill's victim command) or ->`
  followed by ` merged=<n>` only when `n > 0`; e.g. `#3 created 2026-10-02T04:59:00Z kills=1 victim="mariadbd"`.
- Summary (exact, each line ending in `\n`, the label padded to 21 characters like the import summary):

  ```text
  Incidents:           <incidents>
    created:           <created>
    updated:           <updated>
    unchanged:         <unchanged>
    merged:            <merged>
    kept:              <kept>
    skipped:           <skipped>
  OOM kills:           <kills>
  Log lines read:      <lines read>
  ```

- Logging (`BackendLog`, `[LoggerMessage]`): EventId 30 Information `incident reconstruction started`; 31 Warning
  `incident reconstruction interrupted; run it again to complete it`; 32 Error `incident reconstruction failed {Error}`
  (with the exception); 33 Warning `incident skipped {FirstKillAt} {Error}` (the `InvalidIncidentException` text and
  the first kill time, invariant `O` format). No input-derived text in a message. A skipped incident gets no line on
  standard output (it has no number); the summary counts it.

## Options considered

All in record 0096 with the reasons: trigger (sub-command / after every import / in the service), re-run identity
(match by overlap / delete and insert / cursor), merging (overlap / after-window only), hypothesis (fixed rules from
first and last lookback snapshot / slope or max−min / prose or AI), backup recognition (`pzstd` built in / configurable
list / Plesk log lines), memory and atomicity (stream with one transaction per incident / one transaction for all),
paging (keyset on the record ID / offset / one long statement). Further choices with only one reasonable option: the
window defaults `1h`/`30m` (twelve legacy snapshots before a kill, the 06:51 → 06:59 chain inside, recovery after it)
and the count caps. Added in the revision (record 0096, options 20 to 26): the growth baseline after the last boot
(vs. the first lookback snapshot, or growth unknown whenever a boot lies in the lookback), the timeline's serialized
byte budget (vs. count caps sized for six-fold escaping, or a relaxed encoder), skipping an incident the store refuses
(vs. aborting the run), a snapshot page of 1 with the payload read as UTF-8 bytes (vs. a page of 8, or a projection
query for snapshots), and no guard against a poorer rebuild after retention (vs. refusing a lower kill count).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Core | `Configuration/IncidentConfig.cs`, `Configuration/ConfigDuration.cs` | new |
| Vandox.Core | `Configuration/BackendConfig.cs`, `Configuration/BackendConfigLoader.cs` | `Incidents` section, keys, validation |
| Vandox.Core | `Incidents/` (`Incident`, `IncidentKill`, `TimelineEntry`, `IncidentHypothesis`, `ProgramGrowth`, `ProgramUsage`, `MetricExtreme`, `ConnectionPeak`, `ConnectionProcess`, `IncidentNewTemplate`, `IncidentTemplateSpike`, `IncidentLimits`, `IncidentJson`, `IncidentSignatures`, `KillCluster`, `KillClusterer`, `MemoryAnalysis`, `BackupPrograms`, `IncidentBuilder`) | new, one file per type |
| Vandox.Storage | `IIncidentStore.cs`, `StoredIncident.cs`, `IncidentQuery.cs`, `IncidentSaveResult.cs`, `IncidentSaveOutcome.cs`, `InvalidIncidentException.cs`, `IncidentRepository.cs` | new |
| Vandox.Storage | `SqliteStore.cs` (implements `IIncidentStore`), `SchemaMigrator.cs` (step 9), `StorageLimits.cs`, `RecordQuery.cs`, `RecordQueries.cs` (`AfterId`; `ReadJson` from UTF-8 bytes) | changed |
| Vandox.Backend | `Incidents/IncidentReconstructor.cs`, `Incidents/ReconstructionResult.cs`, `Incidents/SavedIncident.cs`, `Cli/IncidentsCommand.cs`, `Cli/IncidentSummaryWriter.cs` | new |
| Vandox.Backend | `Cli/CommandLine.cs`, `Cli/CommandOptions.cs`, `Hosting/BackendApp.cs`, `Hosting/BackendLog.cs` | changed |
| deploy | `deploy/backend/vandoxd.yaml` | `incidents` section |

No new project, package or Dockerfile change: `Vandox.Backend` holds analysis (CLAUDE.md, *Architecture*) and already
references Core and Storage.

## Signatures (for the Dev's skeleton)

All public types get XML docs and `#region` blocks; JSON names are given in brackets (`[JsonPropertyName]`).

```csharp
// Vandox.Core.Configuration
public sealed class IncidentConfig
{
    [ConfigKey("window_before")] public string WindowBefore { get; set; } = "1h";
    [ConfigKey("window_after")] public string WindowAfter { get; set; } = "30m";
    public TimeSpan GetWindowBefore();   // InvalidOperationException("config: incidents.window_before is not valid") when it does not parse
    public TimeSpan GetWindowAfter();    // same with incidents.window_after
}
public static class ConfigDuration
{
    public const int MaxDigits = 4;
    public static bool TryParse(string text, out TimeSpan value);
}
// BackendConfig: [ConfigKey("incidents")] public IncidentConfig Incidents { get; } = new();

// Vandox.Core.Incidents
public static class IncidentSignatures { public const string OomKill = "oom_kill"; public const string Boot = "boot"; public const string ServiceStart = "service_start"; public const string ServiceStop = "service_stop"; public const string ServiceFailed = "service_failed"; }
public static class IncidentLimits { public const int MaxKills = 16; public const int MaxRecordIds = 16; public const int MaxTimelineEvents = 1000; public const int MaxTimelineBytes = 4194304; public const int MaxCandidates = 3; public const int MaxTemplates = 20; }
public static class IncidentJson
{
    public static JsonSerializerOptions Options { get; }      // default encoder, WriteIndented false; used by IncidentBuilder and IncidentRepository
    public static int Utf8Length(TimelineEntry entry);        // JsonSerializer.SerializeToUtf8Bytes(entry, Options).Length
}
public sealed class Incident
{
    DateTimeOffset Start [start]; DateTimeOffset End [end]; long WindowBeforeSeconds [window_before_seconds]; long WindowAfterSeconds [window_after_seconds];
    DateTimeOffset FirstKillAt [first_kill_at]; DateTimeOffset LastKillAt [last_kill_at]; long KillCount [kill_count]; List<IncidentKill> Kills [kills];
    List<TimelineEntry> Timeline [timeline]; long TimelineOmitted [timeline_omitted]; IncidentHypothesis Hypothesis [hypothesis];
    List<MetricExtreme> Metrics [metrics]; ConnectionPeak? Connections [connections]; List<IncidentNewTemplate> NewTemplates [new_templates];
    List<IncidentTemplateSpike> TemplateSpikes [template_spikes];          // all { get; set; }, lists default []
}
public sealed class IncidentKill
{
    DateTimeOffset At [at]; string Host [host]; long? VictimPid [victim_pid]; string VictimCommand [victim_command]; long? AnonRssBytes [anon_rss_bytes];
    long? OomScoreAdj [oom_score_adj]; List<long> RecordIds [record_ids]; long LineCount [line_count];
    public static IncidentKill From(DetectedEvent detected);
}
public sealed class TimelineEntry
{
    DateTimeOffset At [at]; string Type [type]; string Host [host]; Dictionary<string, string> Texts [texts]; Dictionary<string, long> Numbers [numbers];
    List<long> RecordIds [record_ids]; long LineCount [line_count];
    public static TimelineEntry From(DetectedEvent detected);
}
public sealed class IncidentHypothesis
{
    long SnapshotCount [snapshot_count]; DateTimeOffset? FirstSnapshotAt [first_snapshot_at]; DateTimeOffset? LastSnapshotAt [last_snapshot_at];
    bool GrowthKnown [growth_known]; List<ProgramGrowth> Growth [growth]; List<ProgramUsage> Consumers [consumers];
    bool? BackupRunning [backup_running]; List<string> BackupPrograms [backup_programs]; DateTimeOffset? RecentBootAt [recent_boot_at]; long ServiceEvents [service_events];
}
public sealed class ProgramGrowth { string Program [program]; ulong StartRssBytes [start_rss_bytes]; ulong EndRssBytes [end_rss_bytes]; ulong GrowthBytes [growth_bytes]; }
public sealed class ProgramUsage { string Program [program]; ulong RssBytes [rss_bytes]; long Count [count]; }
public sealed class MetricExtreme { string Name [name]; Dictionary<string, string>? Labels [labels]; string Extreme [extreme]; string Source [source]; DateTimeOffset At [at]; double Value [value]; long Samples [samples]; }
public sealed class ConnectionPeak { long Snapshots [snapshots]; DateTimeOffset At [at]; long RecordId [record_id]; long Sockets [sockets]; List<ConnectionProcess> Processes [processes]; }
public sealed class ConnectionProcess { string Command [command]; long Count [count]; }
public sealed class IncidentNewTemplate { string Source [source]; string Program [program]; string Template [template]; DateTimeOffset FirstSeen [first_seen]; long Count [count]; }
public sealed class IncidentTemplateSpike { string Source [source]; string Program [program]; string Template [template]; DateTimeOffset Hour [hour]; long Count [count]; double UsualLevel [usual_level]; }
public sealed class KillCluster { DateTimeOffset Start; DateTimeOffset End; DateTimeOffset FirstKillAt; DateTimeOffset LastKillAt; long KillCount; List<IncidentKill> Kills; }  // { get; set; }
public sealed class KillClusterer
{
    public KillClusterer(TimeSpan before, TimeSpan after);   // ArgumentOutOfRangeException unless both are positive
    public KillCluster? Add(DetectedEvent kill);              // a kill at StorableTime.Max is ignored (null)
    public KillCluster? Complete();
}
public static class MemoryAnalysis
{
    public static string ProgramKey(string program);
    public static IReadOnlyDictionary<string, ProgramUsage> Programs(ProcessSnapshot snapshot);
    public static IReadOnlyList<ProgramGrowth> Growth(IReadOnlyDictionary<string, ProgramUsage> first, IReadOnlyDictionary<string, ProgramUsage> last, int count);
    public static IReadOnlyList<ProgramUsage> Largest(IReadOnlyDictionary<string, ProgramUsage> programs, int count);
}
public static class BackupPrograms
{
    public static IReadOnlyList<string> Names { get; }   // ["pzstd"]
    public static IReadOnlyList<string> Find(IReadOnlyDictionary<string, ProgramUsage> programs);
}
public sealed class IncidentBuilder
{
    public IncidentBuilder(KillCluster cluster, TimeSpan before, TimeSpan after);
    public void AddEvent(DetectedEvent detected);              // InvalidOperationException after AddProcessSnapshot
    public void AddProcessSnapshot(long recordId, DateTimeOffset capturedAt, ProcessSnapshot snapshot);
    public void AddConnectionSnapshot(long recordId, DateTimeOffset capturedAt, ConnectionSnapshot snapshot);
    public void AddMetric(string source, DateTimeOffset capturedAt, MetricPoint metric);
    public void AddNewTemplate(IncidentNewTemplate template);
    public void AddTemplateSpike(IncidentTemplateSpike spike);
    public Incident Build();
}

// Vandox.Storage
public sealed class RecordQuery { /* existing */ public long AfterId { get; set; } }
public static class StorageLimits { public const int SchemaVersion = 9; public const int MaxIncidentDataBytes = 8388608; /* existing */ }
public interface IIncidentStore
{
    Task<IncidentSaveResult> SaveIncidentAsync(Incident incident, DateTimeOffset savedAt, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredIncident>> IncidentsAsync(IncidentQuery query, CancellationToken cancellationToken);
    Task<long> CountIncidentsAsync(CancellationToken cancellationToken);
}
public enum IncidentSaveOutcome { Created, Updated, Unchanged }
public sealed class IncidentSaveResult { long Id; IncidentSaveOutcome Outcome; long Merged; }                    // { get; set; }
public sealed class StoredIncident { long Id; Incident Incident; DateTimeOffset CreatedAt; DateTimeOffset UpdatedAt; }  // { get; set; }
public sealed class IncidentQuery { DateTimeOffset From; DateTimeOffset To; int Limit; }                           // { get; set; }
public sealed class InvalidIncidentException : StoreException { (string message); (string message, Exception innerException); }
internal static class IncidentRepository
{
    internal static string Validate(Incident incident, DateTimeOffset savedAt);   // returns the serialized JSON
    internal static Task<IncidentSaveResult> SaveAsync(SqliteConnection connection, SqliteTransaction transaction, Incident incident, string data, DateTimeOffset savedAt, CancellationToken cancellationToken);
    internal static (string Sql, Dictionary<string, object> Parameters) Build(IncidentQuery query);
    internal static StoredIncident Read(SqliteDataReader reader);
}
// SqliteStore : ..., IIncidentStore (new #region IIncidentStore)

// Vandox.Backend.Incidents
public sealed class IncidentReconstructor
{
    internal const int LinePage = 1000; internal const int MetricPage = 1000; internal const int SnapshotPage = 1;
    public IncidentReconstructor(IRecordReader records, ILogTemplateReader templates, IIncidentStore incidents, SignatureCatalog catalog, TimeSpan before, TimeSpan after, TimeProvider clock);
    public Task<ReconstructionResult> RunAsync(Func<SavedIncident, Task> saved, CancellationToken cancellationToken);
}
public sealed class ReconstructionResult { long LinesRead; long Kills; long Incidents; long Created; long Updated; long Unchanged; long Merged; long Kept; long Skipped; }  // { get; set; }; Incidents counts the built ones, skipped included
public sealed class SavedIncident { IncidentSaveResult Result; Incident Incident; }                                                           // { get; set; }

// Vandox.Backend.Cli
public static string CommandLine.Usage(string usageFor);                // replaces Usage(bool forImport)
public bool CommandOptions.Incidents { get; set; }
internal static class IncidentsCommand
{
    internal static Task<int> RunAsync(string configPath, IEnumerable<KeyValuePair<string, string>> environment, TextWriter stdout, TextWriter stderr, ServeHooks hooks, CancellationToken cancellationToken);
}
internal static class IncidentSummaryWriter
{
    internal static string FormatIncident(SavedIncident saved);
    internal static Task WriteAsync(TextWriter writer, ReconstructionResult result);
}
// BackendLog: IncidentsStarted(this ILogger logger) [30]; IncidentsInterrupted(this ILogger logger) [31]; IncidentsFailed(this ILogger logger, Exception exception, string error) [32]; IncidentSkipped(this ILogger logger, string firstKillAt, string error) [33]
```

Files the skeleton rewrites (read and final apart from the listed change): `BackendConfig.cs`, `BackendConfigLoader.cs`,
`RecordQuery.cs`, `RecordQueries.cs`, `StorageLimits.cs`, `SchemaMigrator.cs`, `SqliteStore.cs`, `CommandLine.cs`,
`CommandOptions.cs`, `BackendApp.cs`, `BackendLog.cs`.

## Test files

- `tests/Vandox.Core.Tests/ConfigDurationTests.cs`, `IncidentConfigTests.cs`, `KillClustererTests.cs`,
  `MemoryAnalysisTests.cs`, `BackupProgramsTests.cs`, `IncidentSignaturesTests.cs`, `IncidentBuilderTests.cs` (new);
  `BackendConfigLoaderTests.cs` (existing, new tests); helper `tests/Vandox.Core.Tests/IncidentSamples.cs` (detected
  events, clusters, process and connection snapshots, metrics).
- `tests/Vandox.Storage.Tests/SqliteStoreIncidentTests.cs` (new); `SqliteStoreQueryTests.cs` and
  `SqliteStoreOpenTests.cs` (existing, new tests).
- `tests/Vandox.Backend.Tests/IncidentReconstructorTests.cs`, `IncidentsCommandTests.cs`,
  `IncidentSummaryWriterTests.cs` (new); `CommandLineTests.cs` (existing, new test); helper
  `tests/Vandox.Backend.Tests/IncidentSamples.cs` (log line, snapshot and metric records and batches written through the
  real `SqliteStore` in a `TempDirectory`); hand-written decorators around the real store, one file each, as the
  folder's `CancellingParser.cs` and `StubHandler.cs`: `RecordingRecordReader.cs` (records every `RecordQuery`),
  `RepeatingRecordReader.cs` (returns its first full page again), `RecordingTemplateReader.cs` (records the spike and
  new-template queries), `RefusingIncidentStore.cs` (throws `InvalidIncidentException` for the n-th save). They are
  hand-written test doubles of `.squad/project.md` *Test doubles*, row *database (C#)* (Tester, task 10).

Existing test code affected:

- `tests/Vandox.Backend.Tests/CommandLineTests.cs` lines 111-112 call `CommandLine.Usage(false)` and `Usage(true)`:
  the old signature goes away, so the **Dev** adapts them in step 4 mechanically to `Usage(string.Empty)` and
  `Usage("import")`.
- `tests/Vandox.Storage.Tests/SqliteStoreOpenTests.cs` (runtime, not a signature): the migration tests at lines 164,
  192, 226, 271, 312 and 344 get a constant `UndoStepNine = "DROP TABLE incidents"` in front of their undo chain; in
  `SqliteStoreOpenMigratesVersionSevenAndAddsTemplateTables` the literal `"8"` becomes the current version and the
  "too new" check moves to version 10. **Tester**, step 5.
- `tests/Vandox.Core.Tests/BackendConfigLoaderTests.cs`: `BackendConfigKeysListTimeZone` (asserts the time zone is the
  last key) and `BackendConfigKeysListErrorLogPathsBeforeTimeZone` (exact key sequence) follow the two appended keys;
  `BackendConfigLoaderLoadsRepositoryExample` passes once the example file has the section. **Tester**, step 5.

## Areas

- `docs/areas/detection-and-reports.md` (**Dev**): *Scope* names incident reconstruction as implemented; new section
  **Incidents** after *Log templates* with: *When incidents are built* (the sub-command, full rebuild, not automatic);
  *Kills and windows* (AC1 rules, defaults and option names, clipping, a kill at the last storable instant ignored, 16
  kills kept); *Content* (timeline with its 1,000-entry and 4 MiB caps, lookback, snapshot period after the last boot,
  programs and keys, hypothesis, metrics, connections, templates, caps — the rules of *Incident content* above, values
  stated once); *Unknown is not none* (growth, backup, metrics, connections); *Re-running* (overlap matching, oldest
  number kept, merge, unchanged, kept, skipped, never reused; one transaction per incident; interruption);
  *Limits of incidents* (memory with the page of 1 and the peak of ≈ 240 MiB for one forged maximum process snapshot
  against the container's 512 MiB, time growing with stored lines and with the number of incidents times their window
  data, the spike query cost, a reboot without a stored boot line is not seen and its snapshots count as growth
  baseline, copies more than 60 s apart (wrong `import.time_zone`) are two kills, old/new legacy names only partly
  joined, backup only `pzstd`, `rss_overcounted` sums, forged lines make forged incidents and — kills spaced more than
  before + after apart — many of them, each up to 8 MiB and kept without limit, so disk use from forged lines outlives
  their retention (#46), victim commands and templates are external text for whoever shows them, agent
  `kernel_event`/`gap` records not read, retention follow-up); *Related decisions* gets 0096; *Not here* drops the #23
  line and points the web view to #27 and the report to #28; *Implementation* lists the types.
- `docs/areas/storage.md` (**Dev**): schema version 9 and the table `incidents` (columns, `AUTOINCREMENT`, index);
  *Writing* gains **Incidents** (save rules, validation texts, one transaction); *Reading* gains the incident query and
  count, the record query's `AfterId` keyset with the 100 ns note, and that payloads are read from the column's UTF-8
  bytes; *Retention* adds that incidents are kept without limit and that a stored incident no rebuilt one overlaps is
  kept, and states the open limit for #46: once retention deletes lines, a re-run rebuilds an incident whose window
  lines have partly expired from what is left and replaces the richer stored one (fewer kills, a shorter timeline);
  retention must prevent that (keep incident windows' data, or stop rebuilding incidents older than the log
  retention), and forged incidents (up to 8 MiB each) outlive the lines they came from; *Related decisions* gets 0096;
  *Implementation* lists `IncidentRepository`.
- `docs/areas/configuration-and-secrets.md` (**Dev**): the two rows in the backend options table and a bullet with the
  duration rule and the error text.
- `docs/areas/backend-host.md` (**Dev**): *Command line and exit codes*: the positional `incidents`, its flags, output
  channels and exit codes (as AC5), link to the detection area.
- `docs/areas/README.md` index row of *Detection, alerts and reports* (**Dev**): scope adds "incident reconstruction",
  drops nothing.

## Documentation updates

- `docs/ARCHITECTURE.md` — **Lead, done in this step**: status paragraph, Components, Data flow paragraph and record
  list, Storage *Migrations* (version 9), new *Incidents* bullet, *Interfaces* bullet rewritten (related defect above).
- `README.md` (**Dev**): *Configuration*, backend options table: `incidents.window_before` (`1h`) and
  `incidents.window_after` (`30m`) with the rule; a new section *Reconstruct incidents* after *Import logs*
  (`docker exec -it vandoxd /vandoxd incidents` in the form the import section uses, what it prints, that it is run
  again after each import, exit codes); *Layout* if it lists the backend's folders.
- `deploy/backend/vandoxd.yaml` (**Dev**): `incidents:` section with both keys at their defaults and a comment each.
- `.squad/project.md` (**Dev**): security area 10 names `incidents.window_before`/`incidents.window_after`
  (`BackendConfigLoader`, `ConfigDuration`) and the reconstruction over stored external text with its bounds
  (`Vandox.Core.Incidents`: `IncidentBuilder` caps and the 4 MiB timeline budget measured with `IncidentJson`,
  `KillClusterer`; `IncidentReconstructor` page sizes, snapshot page of 1 and progress guard; `RecordQueries.ReadJson`
  reading UTF-8 bytes; `IncidentRepository` 8 MiB data bound); area 12 names `vandoxd incidents` printing victim commands through
  `Terminal.Quote` (`IncidentSummaryWriter`) and the incident texts as external text; record 0096 in both lists.
- `.squad/project.md` *Test doubles*, row *database (C#)* (**Tester**): the reconstruction is tested against the real
  store with `IncidentSamples` (`tests/Vandox.Backend.Tests`).
- `docs/decisions/0096-…md` and its index row — **Lead, done in this step** (`Proposed`).

## Architecture check

- *Deterministic detection* (0008): every statement of an incident comes from fixed rules over stored records; no AI.
- *No data gaps unless explicitly recorded* (0028): missing snapshots or metrics make growth, backup, metrics and
  connections **unknown** (null / absent / `GrowthKnown` false), never "no growth" or "no backup". No import record kind
  carries gaps; reading agent `gap` records is left to #53 with live incidents.
- *Backfilled data never raises an alert* (0022): nothing alerts; imported data is only analyzed.
- *A hanging collector never blocks the agent*: not touched.
- Storage rules (0063, 0077): a new table through a new migration step; stored records are never updated; the writer
  lock and one immediate transaction per incident as for batches. No guarantee is weakened; no Product Manager
  decision is needed.

## Security considerations

- [x] Limits and where they apply: `ConfigDuration` counts ASCII characters of the decoded YAML string (no byte
  decoding involved; any non-ASCII character fails); text fields in incidents come from the detector, already cut to
  1,024 UTF-8 bytes; the 8 MiB bound and the 4 MiB timeline budget count the UTF-8 bytes of the serialized JSON after
  `System.Text.Json` escaping (up to six bytes per input byte), with one options object for both; the record caps
  (16 IDs, 16 kills, 1,000 events, 20 templates, 3 candidates) are counts. An incident the store still refuses is
  skipped and counted, never fatal for the run.
- [x] Exceptions that reach the user: configuration errors (exact texts above, never the value); on standard error as
  JSON log attributes `store: …` texts of `StoreException` and `InvalidIncidentException` (`IncidentsFailed`,
  `OpeningDatabaseFailed`); usage errors (exact texts above). Standard output holds only the incident lines and the
  summary; victim commands there pass through `Terminal.Quote`. No raw runtime exception text reaches standard output.
- Hostile input: forged kill lines create incidents (documented, not prevented: 0094); a flood of kills merges into
  one long incident whose kept parts are capped; kills spaced apart make many incidents, kept without limit (disk,
  *Limits of incidents*, #46); snapshot pages of 1 and the byte read bound one forged maximum process snapshot to a
  peak of ≈ 240 MiB (residual, record 0096); the keyset loops cannot spin (progress guard); `AfterId` and all query
  values are bound parameters.

## Decision records

- `docs/decisions/0096-outage-incidents-rebuilt-from-stored-data-merged-by-overlapping-windows.md` (Proposed, indexed).

## Challenge

1. **Major — the growth baseline can span a reboot: accepted.** Confirmed: the default lookback (1 h) holds the 06:51
   reboot before the 06:59 kill (`docs/RUNBOOK_OOM_RECOVERY.md` line 11), and the first lookback snapshot would then be
   a pre-reboot one, so every service started after boot would "grow from 0". The plan now judges snapshots on the
   **snapshot period** after the last boot in the lookback (*Incident content*): baseline, last snapshot, count,
   growth, consumers and `BackupRunning`; fewer than two snapshots there make growth unknown. Of the two fixes offered,
   the baseline after the boot wins over "growth unknown whenever snapshots straddle a boot", because the post-boot
   growth (catch-up backup plus starting services) is exactly what the runbook's chain needs explained; with only one
   post-boot snapshot it still ends as unknown. The builder now requires events before process snapshots (the
   reconstructor reads lines first anyway). Tests `IncidentBuilderStartsGrowthAfterLastBoot` and
   `IncidentBuilderRefusesEventsAfterProcessSnapshots`; a backup only before the boot added to
   `IncidentBuilderRecordsBackupBootAndServiceActivity`. A reboot without a stored boot line stays unseen (*Limits of
   incidents*). Spec and record 0096 (option 9, new options 20 and 21) updated.
2. **Minor — a re-run after retention replaces a richer incident with a poorer one: accepted in part.** The limit is
   real once retention exists, but retention deletes nothing today (`docs/areas/storage.md`, *Retention (planned)*:
   "not implemented yet"), so no run can degrade an incident in this change. A guard "refuse a lower kill count" is
   rejected: it would also freeze legitimate corrections (a catalog change in a release, a re-import after fixing
   `import.time_zone` that turns two kills into one) and would not catch a shorter timeline or lost snapshots with the
   same kill count. The limit is written in the storage area's *Retention* (Dev, task 8) and in record 0096 as the
   requirement on #46; the spec's "incidents outlive the 90-day log retention" is reworded to what this change does.
3. **Minor — the metric-series form of `AfterId` is never called: rejected, kept.** `RecordQuery` is the public query
   of `IRecordReader`; a keyset field that one form silently ignores would return duplicate or endless pages to the
   next caller that pages a metric series (the charts of #27), and refusing it there is as much code as honoring it.
   Cost: one SQL fragment and the existing test row in `SqliteStoreRecordsPagesAfterId`.
4. **Minor — window reads per incident; no test for a window above 744 h: accepted.** The cost per incident is now
   stated (*Memory* / *Time*, *Limits of incidents*), and the clip is pinned by
   `IncidentReconstructorClipsSpikeRangeToMaxHours` (20 kills 40 h apart with 24 h windows; a recording template reader
   checks `To = Start + 744 h`).

## Security review

- **B1 (blocking) — the 8 MiB bound can be exceeded by escaped forged text: resolved.** Confirmed against the code:
  payloads and incidents use the default encoder, `service_start`/`service_stop`/`service_failed` carry two text fields,
  and a timeline entry's host is a log line host of up to 1,024 bytes (`LogLine.Validate`: `Check.Short`), not 128, so
  the worst entry is about 19 KB and 1,000 of them about 19 MB. The timeline now has a serialized byte budget
  (`IncidentLimits.MaxTimelineBytes` = 4 MiB, measured with `IncidentJson.Utf8Length` under the same options the store
  serializes with; the first entry that does not fit and all later ones are counted in `TimelineOmitted`). The
  remaining parts are bounded by counts at ≤ 6 bytes per input byte, below 1 MiB together (*Incident content*, *Size
  bound*); the sizing sentence is replaced in *Options considered* and the detection area's *Content* (Dev). A relaxed
  encoder was rejected because control characters (6×) and supplementary characters still expand, so it would not
  give a bound by itself. `SqliteStoreSaveIncidentAcceptsWorstCaseIncident` builds the worst case through
  `IncidentBuilder` (hosts at 1,024 bytes rather than the review's 128) and asserts the save. And an
  `InvalidIncidentException` is now reported (`IncidentSkipped`, EventId 33) and counted as skipped instead of ending
  the run (`IncidentReconstructorSkipsInvalidIncidentAndContinues`, summary line `skipped`).
- **B2 (blocking) — a page of 8 snapshots does not bound memory: resolved, with a stated residual.** The snapshot page
  is 1. The 12 MiB figure was wrong; the real worst case of one legacy process snapshot is ≈ 105 MB of stored JSON (all
  texts `<`), and reading it the current way peaks at ≈ 440 MiB (native copy, UTF-16 string, transcoded UTF-8,
  objects), which does not fit next to the service in 512 MiB. `RecordQueries.ReadJson` now reads the UTF-8 bytes and
  parses them without a copy (checked: `GetFieldValue<byte[]>` returns the UTF-8 bytes of a `TEXT` column in
  Microsoft.Data.Sqlite 10.0.12), which brings one snapshot to ≈ 240 MiB. The residual (a forged legacy log at every
  cap leaves the service about half the container while one snapshot is read) is stated in record 0096 and the
  detection area's *Limits of incidents*. `IncidentReconstructorReadsSnapshotsOnePerPage` pins that the reconstructor
  holds one snapshot page; `SqliteStoreRecordsRoundTripsEscapedPayloadText` pins the unchanged results of the new read
  path; the import side's same pattern goes to a follow-up issue (*Out of scope*).
- **N1 — a kill at `StorableTime.Max`: resolved.** Every query range is half-open with `To ≤ Max`, so a line at `Max`
  is never read by pass 1 anyway; `KillClusterer.Add` ignores such a kill, so a window never ends at its own kill.
  Pinned in `KillClustererClipsWindowsToStorableRange` and in `SqliteStoreSaveIncidentRefusesInvalidIncident` (first
  kill equal to `End`); a refused incident is skipped, not fatal (B1).
- **N2 — keyset paging without a progress guard: resolved.** *Reconstruction*, step 4; test
  `IncidentReconstructorFailsWhenPageDoesNotAdvance`.
- **N3 — incidents kept without limit amplify disk use from forged lines: resolved as documentation.** Stated in the
  detection area's *Limits of incidents*, the storage area's *Retention* and record 0096 as a requirement on #46;
  bounding the number of incidents here would drop real ones under a flood of forged kills, which is a product
  decision for retention, not for reconstruction.

## Out of scope / follow-ups

- #27 (incidents view, notes: must follow merges), #28 (forensics report), #53 (live incidents: agent `kernel_event`
  kills and boots, `gap` records in the window), #36 (backup detection by configurable patterns), #46 (retention must
  not degrade incidents on re-run, and the disk use of forged incidents; noted in 0096 and the storage area).
- New follow-up issue (the orchestrator opens it): **title** `[Backend] Import holds a forged maximum legacy snapshot
  several times in memory`; **body**: "`BatchWriter` stores a payload as `PayloadRegistry.Serialize(payload)`, a
  `string`. A legacy process snapshot at its caps (4,096 rows with 1,024-byte commands and 2,048-byte command lines,
  4,096 programs) whose texts are all `<` serializes to about 105 MB of JSON, because the default encoder writes `<` as
  six bytes; as a UTF-16 string that is about 200 MiB, next to the parsed snapshot (about 40 MiB) and SQLite's UTF-8
  copy when the parameter is bound (about 100 MiB). `vandoxd import` runs in the backend container (512 MiB, shared
  with the service). Serialize to UTF-8 bytes (`JsonSerializer.SerializeToUtf8Bytes`) and bind them, or cap the stored
  payload size; state the bound in `docs/areas/log-import.md` and `docs/areas/storage.md`. Found while planning #23,
  whose reader of the same column was changed to read UTF-8 bytes."
