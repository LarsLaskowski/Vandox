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
  - `KillClustererTests.KillClustererClipsWindowsToStorableRange` (kills at `StorableTime.Min` and `StorableTime.Max`)
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
  - `IncidentBuilderTests.IncidentBuilderCapsTimelineAndTemplates` (1,005 events: 1,000 kept, `TimelineOmitted == 5`;
    25 new templates and 25 spikes: the first 20 of each kept)
  - `IncidentBuilderTests.IncidentBuilderRanksTopThreeGrowthCandidates` (five programs over three lookback snapshots:
    growth = last − first, a program absent from the first snapshot grows from 0, a shrinking or unchanged program is no
    candidate, ties by program name ordinal, at most three; consumers = top three RSS of the last snapshot)
  - `IncidentBuilderTests.IncidentBuilderUsesOnlyLookbackSnapshotsForGrowth` (a snapshot at the first kill's time and one
    after it do not count; `FirstSnapshotAt`, `LastSnapshotAt`, `SnapshotCount`)
  - `IncidentBuilderTests.IncidentBuilderMarksGrowthUnknownWithFewerThanTwoSnapshots` (one snapshot: `GrowthKnown`
    false, no growth, consumers from it; none: consumers empty, `BackupRunning` null)
  - `IncidentBuilderTests.IncidentBuilderRecordsBackupBootAndServiceActivity` (`pzstd` and `/usr/bin/pzstd` found,
    `BackupRunning` true with `BackupPrograms == ["pzstd"]`, `backup_first_seen` and `backup_last_seen` entries, only the
    first when one snapshot; `RecentBootAt` the last boot before the first kill; `ServiceEvents` counts start, stop and
    failed before the first kill only)
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
  - `SqliteStoreIncidentTests.SqliteStoreSaveIncidentRefusesInvalidIncident` (exact texts in *Approach*, *Storage*)
  - `SqliteStoreIncidentTests.SqliteStoreIncidentsReadsOverlappingInOrder` (range and limit rules, order by start then
    ID, the JSON read back equals the saved incident; `CountIncidentsAsync`)
  - `IncidentReconstructorTests.IncidentReconstructorRerunUpdatesInsteadOfDuplicating` (run; a second run without new
    data: all unchanged; add a second kill in the window and a third kill whose window bridges two incidents; run:
    the first incident updated with the same ID and kill count 2, the bridged pair merged into the older ID,
    `Merged == 1`, no other new incident)
  - `IncidentReconstructorTests.IncidentReconstructorKeepsStoredIncidentsWithoutMatch` (a stored incident whose window
    no rebuilt incident overlaps is kept unchanged and counted as kept)
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
  - `IncidentSummaryWriterTests.IncidentSummaryWriterWritesCounts` (exact block in *Approach*, *Command*)
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
- [ ] **AC8 — memory bound (coverage step 6, by the Tester; it cannot fail against the skeleton in a meaningful way).**
  - `IncidentBuilderTests.IncidentBuilderRetainsBoundedMemory` (100,000 events with 1,024-byte texts and 256 record IDs
    and 1,000 snapshots of 4,096 programs: retained bytes below a bound sized from the caps, measured with the
    `RetainedBytes` helper pattern of `KernelReportGrouperTests` and its `S1215` pragma)

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
   - `process_snapshot` records (page 8) → `AddProcessSnapshot`; `connection_snapshot` records (page 8) →
     `AddConnectionSnapshot`; `metric` records (page 1,000; kind-wide, no name) → `AddMetric`;
   - `NewTemplatesAsync` over the window, limit 20 → `AddNewTemplate`; `TemplateSpikesAsync` over the window clipped to
     its first 744 hours (`StorageLimits.MaxSpikeRangeHours`), default parameters, limit 20 → `AddTemplateSpike`;
   - `Build()` → `IIncidentStore.SaveIncidentAsync(incident, clock.GetUtcNow())` → the `saved` callback.
3. After pass 1: `Kept = CountIncidentsAsync() − (Created + Updated + Unchanged)`.
4. Cancellation is checked between pages and incidents (`OperationCanceledException` propagates); a `StoreException`
   propagates. Each incident is its own transaction: an interrupted run keeps the incidents it saved.

**Memory:** one page of records, two detectors (each at most 1,024 open events), one open cluster (16 kills), one
builder (at most 1,000 events, two program maps of at most 4,096 entries, one connection peak, 20 + 20 templates). It does
not grow with the number of stored lines, kills or incidents. A process or connection snapshot can be ~12 MiB when
forged at its caps, hence the page of 8. **Time** grows with all stored log lines (pass 1) plus the window data; the
spike query's cost is not bounded by its range (record 0095) — the sub-command runs off any request path.

### Incident content (`IncidentBuilder`)

- **Lookback** = `[Start, FirstKillAt)` (Start is the first kill's window start). Snapshots, boots and service events
  before the first kill are judged on the lookback; the timeline covers the whole window.
- **Timeline:** every detected event of the window (`TimelineEntry.From`: time of its first line, signature ID as
  `type`, host, texts, numbers, first 16 record IDs, line count), the first 1,000 in arrival order, the rest counted in
  `TimelineOmitted`; plus `backup_first_seen` and `backup_last_seen` (the first and last process snapshot of the window
  holding a backup program; only the first when it is the same snapshot; `texts.program` = the first backup name found,
  `numbers.rss_bytes` = the backup programs' summed RSS, the snapshot's record ID). Sorted by time, then `type`
  ordinal, then first record ID.
- **Programs of a snapshot** (`MemoryAnalysis.Programs`): its `programs` entries, or, when it has none, its processes
  grouped by the first space-separated word of `command` (the command itself when that word is empty), keyed by
  `ProgramKey` (a name starting with `/` becomes the part after its last `/`, unless that part is empty), RSS and
  counts summed (saturating).
- **Hypothesis:** the first and last process snapshot of the lookback (by capture time, then record ID) and their
  number. Growth candidates (`GrowthKnown` = at least two snapshots): programs of the last snapshot whose RSS is above
  their RSS in the first (0 when absent there), by growth descending, then key ordinal, top three. Consumers: the last
  snapshot's programs by RSS descending, then key ordinal, top three (empty without snapshot). `BackupRunning`: true when
  a lookback snapshot holds a backup program, false when snapshots exist and none does, null without snapshot;
  `BackupPrograms` the names seen there in list order. `RecentBootAt`: the time of the last `boot` event in the
  lookback, else null. `ServiceEvents`: the number of `service_start`, `service_stop` and `service_failed` events in the
  lookback.
- **Metrics** (fixed definitions, in this order; labels must equal exactly; the strictly lower/higher value replaces,
  so ties keep the earliest; non-finite values are skipped; a definition without samples is absent):
  `mem_available_bytes` min; `swap_used_bytes` max; `pressure_avg10` {`resource: memory`, `kind: some`} max;
  `pressure_avg10` {`resource: memory`, `kind: full`} max; `load1` max. Each with value, time, source and sample count.
- **Connections:** the snapshot of the window with the most sockets (sum of its `states` counts; strictly more
  replaces), its time and record ID, the number of snapshots, and its top three processes by count summed per
  `command`, then command ordinal; null without a snapshot.
- **Templates:** new templates (source, program, template, first seen, count) and spikes (source, program, template,
  hour, count, usual level), the first 20 of each as the queries return them.

### Storage

- Schema step 9, in one transaction:
  `CREATE TABLE incidents (id INTEGER PRIMARY KEY AUTOINCREMENT, started_at INTEGER NOT NULL, ended_at INTEGER NOT NULL CHECK (ended_at > started_at), first_kill_at INTEGER NOT NULL, kill_count INTEGER NOT NULL CHECK (kill_count > 0), created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, data TEXT NOT NULL) STRICT`
  and `CREATE INDEX incidents_started_at ON incidents(started_at)`. `AUTOINCREMENT` so that a number merged away is never
  given to a later incident. `StorageLimits.SchemaVersion = 9`.
- `SaveIncidentAsync` (writer lock, one immediate transaction): validate; serialize the incident with
  `System.Text.Json` (the `JsonPropertyName` attributes, no indentation); select the stored rows with
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
  whole 100 ns (`DateTimeOffset` ticks), so the capture time a reader returns equals the stored one.

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
  OOM kills:           <kills>
  Log lines read:      <lines read>
  ```

- Logging (`BackendLog`, `[LoggerMessage]`): EventId 30 Information `incident reconstruction started`; 31 Warning
  `incident reconstruction interrupted; run it again to complete it`; 32 Error `incident reconstruction failed {Error}`
  (with the exception). No input-derived text in a message.

## Options considered

All in record 0096 with the reasons: trigger (sub-command / after every import / in the service), re-run identity
(match by overlap / delete and insert / cursor), merging (overlap / after-window only), hypothesis (fixed rules from
first and last lookback snapshot / slope or max−min / prose or AI), backup recognition (`pzstd` built in / configurable
list / Plesk log lines), memory and atomicity (stream with one transaction per incident / one transaction for all),
paging (keyset on the record ID / offset / one long statement). Further choices with only one reasonable option: the
window defaults `1h`/`30m` (twelve legacy snapshots before a kill, the 06:51 → 06:59 chain inside, recovery after it)
and the caps (sized so an incident's JSON stays far below the 8 MiB storage bound: about 1,000 × (16 IDs + two 1 KiB
texts) + 40 templates of 1 KiB).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Core | `Configuration/IncidentConfig.cs`, `Configuration/ConfigDuration.cs` | new |
| Vandox.Core | `Configuration/BackendConfig.cs`, `Configuration/BackendConfigLoader.cs` | `Incidents` section, keys, validation |
| Vandox.Core | `Incidents/` (`Incident`, `IncidentKill`, `TimelineEntry`, `IncidentHypothesis`, `ProgramGrowth`, `ProgramUsage`, `MetricExtreme`, `ConnectionPeak`, `ConnectionProcess`, `IncidentNewTemplate`, `IncidentTemplateSpike`, `IncidentLimits`, `IncidentSignatures`, `KillCluster`, `KillClusterer`, `MemoryAnalysis`, `BackupPrograms`, `IncidentBuilder`) | new, one file per type |
| Vandox.Storage | `IIncidentStore.cs`, `StoredIncident.cs`, `IncidentQuery.cs`, `IncidentSaveResult.cs`, `IncidentSaveOutcome.cs`, `InvalidIncidentException.cs`, `IncidentRepository.cs` | new |
| Vandox.Storage | `SqliteStore.cs` (implements `IIncidentStore`), `SchemaMigrator.cs` (step 9), `StorageLimits.cs`, `RecordQuery.cs`, `RecordQueries.cs` | changed |
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
public static class IncidentLimits { public const int MaxKills = 16; public const int MaxRecordIds = 16; public const int MaxTimelineEvents = 1000; public const int MaxCandidates = 3; public const int MaxTemplates = 20; }
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
    public KillCluster? Add(DetectedEvent kill);
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
    public void AddEvent(DetectedEvent detected);
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
    internal const int LinePage = 1000; internal const int MetricPage = 1000; internal const int SnapshotPage = 8;
    public IncidentReconstructor(IRecordReader records, ILogTemplateReader templates, IIncidentStore incidents, SignatureCatalog catalog, TimeSpan before, TimeSpan after, TimeProvider clock);
    public Task<ReconstructionResult> RunAsync(Func<SavedIncident, Task> saved, CancellationToken cancellationToken);
}
public sealed class ReconstructionResult { long LinesRead; long Kills; long Incidents; long Created; long Updated; long Unchanged; long Merged; long Kept; }  // { get; set; }
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
// BackendLog: IncidentsStarted(this ILogger logger) [30]; IncidentsInterrupted(this ILogger logger) [31]; IncidentsFailed(this ILogger logger, Exception exception, string error) [32]
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
  real `SqliteStore` in a `TempDirectory`).

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
  *Kills and windows* (AC1 rules, defaults and option names, clipping, 16 kills kept); *Content* (timeline, lookback,
  programs and keys, hypothesis, metrics, connections, templates, caps — the rules of *Incident content* above, values
  stated once); *Unknown is not none* (growth, backup, metrics, connections); *Re-running* (overlap matching, oldest
  number kept, merge, unchanged, kept, never reused; one transaction per incident; interruption); *Limits of incidents*
  (memory and time, the spike query cost, copies more than 60 s apart (wrong `import.time_zone`) are two kills, old/new
  legacy names only partly joined, backup only `pzstd`, `rss_overcounted` sums, forged lines make forged incidents,
  victim commands and templates are external text for whoever shows them, agent `kernel_event`/`gap` records not read,
  retention follow-up); *Related decisions* gets 0096; *Not here* drops the #23 line and points the web view to #27 and
  the report to #28; *Implementation* lists the types.
- `docs/areas/storage.md` (**Dev**): schema version 9 and the table `incidents` (columns, `AUTOINCREMENT`, index);
  *Writing* gains **Incidents** (save rules, validation texts, one transaction); *Reading* gains the incident query and
  count, and the record query's `AfterId` keyset with the 100 ns note; *Retention* adds that incidents outlive log lines
  and the open point for #46; *Related decisions* gets 0096; *Implementation* lists `IncidentRepository`.
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
  (`Vandox.Core.Incidents`: `IncidentBuilder` caps, `KillClusterer`; `IncidentReconstructor` page sizes;
  `IncidentRepository` 8 MiB data bound); area 12 names `vandoxd incidents` printing victim commands through
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
  1,024 UTF-8 bytes; the 8 MiB bound counts the UTF-8 bytes of the serialized JSON after `System.Text.Json` escaping;
  the record caps (16 IDs, 16 kills, 1,000 events, 20 templates, 3 candidates) are counts.
- [x] Exceptions that reach the user: configuration errors (exact texts above, never the value); on standard error as
  JSON log attributes `store: …` texts of `StoreException` and `InvalidIncidentException` (`IncidentsFailed`,
  `OpeningDatabaseFailed`); usage errors (exact texts above). Standard output holds only the incident lines and the
  summary; victim commands there pass through `Terminal.Quote`. No raw runtime exception text reaches standard output.
- Hostile input: forged kill lines create incidents (documented, not prevented: 0094); a flood of kills merges into
  one long incident whose kept parts are capped; snapshot pages of 8 bound the memory of forged maximum-size snapshots;
  `AfterId` and all query values are bound parameters.

## Decision records

- `docs/decisions/0096-outage-incidents-rebuilt-from-stored-data-merged-by-overlapping-windows.md` (Proposed, indexed).

## Out of scope / follow-ups

- #27 (incidents view, notes: must follow merges), #28 (forensics report), #53 (live incidents: agent `kernel_event`
  kills and boots, `gap` records in the window), #36 (backup detection by configurable patterns), #46 (retention must
  not degrade incidents on re-run; noted in 0096 and the storage area). No new follow-up issue is needed.
