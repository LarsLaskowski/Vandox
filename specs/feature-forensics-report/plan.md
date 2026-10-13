# Plan: Forensics report

Source: Issue #28 | [spec.md](spec.md)
Status: Draft
Tier: security — the change writes external text (victim commands, program names) into a new output format (security area 12),
adds a command-line flag whose value is checked (security area 10, CLI arguments) and reads stored external data under new memory
bounds (security area 10, the reads of `vandoxd incidents`).

## Problem / root cause

Feature, see [spec.md](spec.md): a sub-command `vandoxd report` that writes a Markdown report over the whole stored period — every
outage with victims and hypothesis, time-of-day recurrence, correlation with backups, boots and panel memory, the largest permanent
and transient memory consumers — in English or German, all times UTC. The web page is split off (PM decision 1).

Claims of the issue, checked against the code:

- *Depends on #23* — confirmed: `vandoxd incidents` stores incidents (`src/Vandox.Backend/Cli/IncidentsCommand.cs`,
  `src/Vandox.Backend/Incidents/IncidentReconstructor.cs`), each with its kills (victim command, `IncidentKill.VictimCommand`) and a
  structured hypothesis (`IncidentHypothesis`: growth, consumers, `backup_running`, `recent_boot_at`, `service_events`).
- *Number and times of outages, victims, hypotheses* — confirmed available: `IIncidentStore.IncidentsAsync`
  (`src/Vandox.Storage/IncidentRepository.cs:130`, `ORDER BY started_at, id`, no keyset, so the report pages by window end; stored
  windows never overlap because every save merges the overlapping ones, `IncidentRepository.cs:224`).
- *Correlation with backup runs* — confirmed only as program name `pzstd` (`src/Vandox.Core/Incidents/BackupPrograms.cs`, record
  0096); the report reuses it.
- *Boots* — confirmed: signature `boot` (`IncidentSignatures.Boot`), detected on stored log lines by `SignatureDetector`.
- *Panel usage (Plesk `sw-engine` memory)* — confirmed present in the process snapshots, under **two names**: the old legacy format
  cuts the command to `sw-engi` (`testdata/logs/legacy-monitor-old.log` line 15, `sw-engi+`, the `+` stripped by
  `LegacyProcessNames.Normalize`), the new format derives `sw-engine` from the path (`testdata/logs/legacy-monitor-new.log` line 14);
  record 0093 names `sw-engine-fpm`. Hence the prefix rule `sw-engi`.
- *Largest permanent and transient memory consumers* — no whole-period aggregation exists; new.
- *Available in the web UI* — refuted for now: the web UI has no login or layout (#24, #25 open); split per PM decision 1.
- *Known failure mode (2 GB, OOM kills MariaDB)* — background, not checkable in code.

Localization (PM decision 4): the repository has **no** localization mechanism — no `.resx`, no `IStringLocalizer`; every project sets
`InvariantGlobalization` (`Directory.Build.props:13`, `src/Vandox.Backend/Vandox.Backend.csproj:11`) and the backend
`SatelliteResourceLanguages` `en` (`Vandox.Backend.csproj:12`); `App.razor` declares `lang="en"`. Decided in record 0100: text tables
in code, flag `-lang en|de`, default `en`. Not user-visible-unresolved, so no escalation.

Related defect found on the way: the class comment of `CommandLine` (`src/Vandox.Backend/Cli/CommandLine.cs:5-6`) still names only
`import`, not `incidents` and `templates`; the Dev fixes it with this change (it gains `report`).

No external format is parsed: the report reads records the existing parsers stored, so no new real sample is needed; the panel names
come from the two legacy fixtures above.

## Acceptance criteria

Each criterion lists the tests the Tester writes first (step 5); test classes are named after the type under test.

- [ ] **AC1 — command line.** `report` is a sub-command with `-config`, `-lang` and `-h`/`-help`, before or after the sub-command as
  `incidents` takes `-config`; `-lang` in the forms `-lang de`, `-lang=de`, `--lang de`, `--lang=de`, last value wins, default `en`;
  exact errors: `vandoxd report: unknown language "fr" (expected en or de)` (also for `EN`, `de-DE`, `""`, and the one-character value U+202E, which the error shows through
  `Terminal.Quote` as `"\u202e"`, six ASCII characters inside the quotes), `flag needs an argument: -lang`, `vandoxd report: no argument is expected`,
  `flag provided but not defined: -lang` for `-lang` before `report` and after `incidents`, `unexpected argument "report"` with
  `-healthcheck`. Usage of `report`: `Usage of vandoxd report [-config file] [-lang en|de]:\n  -config string\n    \tpath of the
  configuration file\n  -lang string\n    \tlanguage of the report: en or de (default "en")\n`; the main usage contains
  `  vandoxd [flags] report [-config file] [-lang en|de]\n    \twrite the forensics report of the stored data as Markdown\n`.
  Tests: `CommandLineTests.CommandLineParsesReportSubCommand`, `CommandLineTests.CommandLineRefusesReportLanguagesOtherThanEnAndDe`,
  `CommandLineTests.CommandLineTakesLanguageOnlyForReport`, `CommandLineTests.CommandLineUsageNamesReportSubCommand`.
- [ ] **AC2 — command run.** `vandoxd report` loads the configuration and opens the store like `incidents`, logs `report started`
  to standard error, writes only the report to standard output and exits 0; exit 1 with `configuration invalid` (nothing on standard
  output), when the database cannot be opened, when interrupted (`report interrupted`), and when the store fails during the run
  (`report failed` with the store's text). Tests: `ReportCommandTests.ReportCommandWritesMarkdownAndExitsZero`,
  `ReportCommandTests.ReportCommandWritesGermanWithLangDe`, `ReportCommandTests.ReportCommandExitsOneOnInvalidConfiguration`,
  `ReportCommandTests.ReportCommandExitsOneWhenStorageCannotBeOpened`, `ReportCommandTests.ReportCommandExitsOneWhenInterrupted`,
  `ReportCommandTests.ReportCommandExitsOneWhenStoreFailsDuringRun`.
- [ ] **AC3 — every outage with victims and hypothesis.** For three stored incidents the report has three outage sections in window
  order, each with number, first kill (UTC), window, kill count, victims (distinct victim commands in first-seen order with counts,
  "kills not kept" when `kill_count` exceeds the kept kills, unknown for an empty victim), growth candidates with `+` MiB, largest
  consumers, the three activities and the stored service events; growth unknown when `growth_known` is false, "none" when known and
  empty, consumers unknown when the incident had no snapshot. Tests:
  `ForensicsReporterTests.ForensicsReporterListsEveryOutageWithVictimsAndHypothesis`,
  `MarkdownReportWriterTests.WriterShowsUnknownHypothesisParts`, `MarkdownReportWriterTests.WriterCountsVictimsAndKillsNotKept`.
- [ ] **AC4 — incidents read one at a time, twice, bounded.** The reporter asks the incident store with limit 1, starts each page at
  the previous incident's window end, fails with `store: reading incidents: page did not advance` when a page does not lead past it,
  and notes a different count between the passes. Tests: `ForensicsReporterTests.ForensicsReporterReadsIncidentsOneAtATime`,
  `ForensicsReporterTests.ForensicsReporterFailsWhenIncidentPageDoesNotAdvance`,
  `ForensicsReporterTests.ForensicsReporterNotesIncidentsChangedBetweenPasses`.
- [ ] **AC5 — binomial tail.** `RecurrenceRules.BinomialTail` matches, to a relative 1e-9: (3, 3, 1/24) → 7.233796296296295e-05;
  (3, 2, 1/24) → 0.005063657407407407; (5, 3, 0.06) → 0.0019702655999999995; (10, 3, 0.1) → 0.07019082640000002; (20, 5, 0.1) →
  0.04317449528446341; (1000, 30, 0.01) → 2.0599888509719412e-07; (10, 4, 1/24) → 0.0005168454002460099; and the edges (n, 0, p) → 1,
  (n, n + 1, p) → 0, (10, 2, 0) → 0, (4, 4, 1) → 1; negative counts or a probability outside [0, 1] throw
  `ArgumentOutOfRangeException`. Tests: `RecurrenceRulesTests.BinomialTailMatchesReferenceValues`,
  `RecurrenceRulesTests.BinomialTailHandlesEdges`, `RecurrenceRulesTests.BinomialTailRefusesInvalidArguments`.
- [ ] **AC6 — rule evaluation.** `RecurrenceRules.Evaluate` is unknown (`TooFewIncidents`) with fewer than 3 known, unknown
  (`NoBaseData`) with no known base hour, detected only with at least 2 matching, a share above the base share and a tail at most the
  level, not detected otherwise (2 of 3 at base 1/24 and level 0.01/24: not detected; 3 of 3: detected; 4 of 10 at 1/24 and 0.01/24:
  not detected; 5 of 10: detected; share equal to base: not detected). Tests: `RecurrenceRulesTests.EvaluateIsUnknownWithFewerThanThreeKnown`,
  `RecurrenceRulesTests.EvaluateIsUnknownWithoutBaseData`, `RecurrenceRulesTests.EvaluateDetectsUnlikelyShare`,
  `RecurrenceRulesTests.EvaluateDoesNotDetectLikelyShare`, `RecurrenceRulesTests.EvaluateNeedsTwoMatchingAndShareAboveBase`.
- [ ] **AC7 — observed hours.** Only `journal` and `syslog` lines count; distinct UTC hours are counted once each, per hour of day too,
  times before 1970 fall into the right hour (floor division), runs of consecutive hours are kept up to the cap and `Truncated` is set
  beyond it, `Covers` is true only when every hour overlapping `[from, to)` is observed, an earlier time than the previous throws
  `ArgumentException`. Tests: `ObservedHoursTests.ObservedHoursCountsDistinctHoursAndHoursOfDay`,
  `ObservedHoursTests.ObservedHoursPlacesTimesBeforeTheEpochInTheirHour`, `ObservedHoursTests.ObservedHoursCoversOnlyWhenEveryHourIsObserved`,
  `ObservedHoursTests.ObservedHoursTruncatesAfterMaxRuns`, `ObservedHoursTests.ObservedHoursRefusesEarlierTime`,
  `ForensicsAnalysisTests.ForensicsAnalysisObservesOnlySystemLogSources`.
- [ ] **AC8 — time-of-day recurrence.** With 72 evenly observed hours, three outages at 02:xx UTC are detected for hour 2 (and only
  for it); three outages at three different hours are not detected; two outages are unknown (`TooFewIncidents`); the hour bin is the
  UTC hour of the first kill whatever offset the `DateTimeOffset` carries. Tests:
  `ForensicsAnalysisTests.ForensicsAnalysisDetectsTimeOfDayCluster`, `ForensicsAnalysisTests.ForensicsAnalysisDoesNotDetectSpreadOutages`,
  `ForensicsAnalysisTests.ForensicsAnalysisTimeOfDayIsUnknownWithTwoOutages`, `ForensicsAnalysisTests.ForensicsAnalysisBinsTimeOfDayInUtc`.
- [ ] **AC9 — activities and correlations.** An outage's backup is unknown without a snapshot in its lookback, yes with a `pzstd`
  snapshot there, no with snapshots without it; boot unknown when an overlapping hour is not observed, yes (with the last boot time)
  when a boot lies in the lookback; panel high unknown without a threshold or snapshot, yes at or above the threshold. Correlations:
  outages each preceded by a backup that runs in few observed hours are detected; outages without snapshots count as unknown and never
  as no (all three unknown → result unknown, `TooFewIncidents`); boot and panel likewise detected. Tests:
  `ForensicsAnalysisTests.ForensicsAnalysisJudgesBackupInTheLookback`, `ForensicsAnalysisTests.ForensicsAnalysisJudgesBootInTheLookback`,
  `ForensicsAnalysisTests.ForensicsAnalysisJudgesPanelMemoryInTheLookback`, `ForensicsAnalysisTests.ForensicsAnalysisDetectsBackupCorrelation`,
  `ForensicsAnalysisTests.ForensicsAnalysisCountsMissingSnapshotsAsUnknown`, `ForensicsAnalysisTests.ForensicsAnalysisDetectsBootCorrelation`,
  `ForensicsAnalysisTests.ForensicsAnalysisDetectsPanelCorrelation`.
- [ ] **AC10 — panel programs and threshold.** `PanelPrograms.IsPanel` is true for `sw-engi`, `sw-engine`, `sw-engine-fpm`, false for
  `sw-cp-server`, `sw-eng`, `Sw-engine`, `xsw-engine`, `""`; panel memory sums the RSS of all panel programs of a snapshot (saturating);
  the threshold is the nearest-rank 90th percentile (10 snapshots 10…100 MiB → 90 MiB; 1 snapshot → its value) and 0 without panel
  memory. Tests: `PanelProgramsTests.PanelProgramsMatchesTheEnginePrefix`, `PanelProgramsTests.PanelProgramsSumsPanelMemory`,
  `SnapshotSeriesTests.SnapshotSeriesPanelThresholdIsNearestRankPercentile`, `SnapshotSeriesTests.SnapshotSeriesPanelThresholdIsZeroWithoutPanel`.
- [ ] **AC11 — memory consumers.** A program in 9 of 10 snapshots is permanent, in 8 of 10 transient; permanent ranked by mean RSS
  (rounded down), transient by peak RSS with the first time of the peak, ties by name, at most 5 each; names reduced like
  `MemoryAnalysis.ProgramKey` (`/usr/sbin/mariadbd` and `mariadbd` are one); beyond `maxPrograms` new programs are not tracked and
  their entries counted in `UntrackedEntries`. The 90 % and the 90th percentile are computed in integers (permanent when
  `present · 10 ≥ snapshots · 9`; nearest rank `(9 · n + 9) / 10`), so 9 of 10 is exactly 90 %. Tests: `ConsumerTrackerTests.ConsumerTrackerSplitsPermanentAndTransient`,
  `ConsumerTrackerTests.ConsumerTrackerRanksByMeanAndPeak`, `ConsumerTrackerTests.ConsumerTrackerJoinsProgramPaths`,
  `ConsumerTrackerTests.ConsumerTrackerStopsTrackingAtMaxPrograms`.
- [ ] **AC12 — bounds.** `SnapshotSeries` keeps at most its capacity and sets `Truncated`; boots beyond `maxBoots` set the boot rule
  unknown (`TooManyBoots`); more snapshots make backup and panel unknown (`TooManySnapshots`); more runs make backup, boot and panel
  unknown (`TooManyObservedRuns`) while the time of day is still decided; the notes name the cap. Tests:
  `SnapshotSeriesTests.SnapshotSeriesTruncatesAtCapacity`, `ForensicsAnalysisTests.ForensicsAnalysisMarksActivitiesUnknownOverCaps`,
  `MarkdownReportWriterTests.WriterNamesTheCapsHitInTheNotes`.
- [ ] **AC13 — external text only in code spans.** `MarkdownText.Code`: `mariadbd` → `` ` mariadbd ` ``; `a`b` → ``` `` a`b `` ```;
  `` `x` `` → fence of two with padding; `<img src=x onerror=alert(1)>`, `[a](http://x)`, `www.example.com`, `*b*`, `&amp;`, `|`
  appear unchanged inside the span; U+202E → `\u202e`, U+2028 → `\u2028`, U+0000 → `\u0000`, line feed → `\n`, tab → `\t`, U+10FFFD
  (private use) → `\U0010fffd`, a backslash → two; three spaces → `` ` \u0020\u0020\u0020 ` ``; empty → `—`. The arrange step of the
  U+202E case asserts the input is one character of category Format (so a test input that lost its backslash, six ASCII characters,
  fails there), and every case asserts the output holds no character of categories Cc, Cf, Zl, Zp. The writer puts victim commands
  and program names only into code spans: a victim `` a`b<script>`` and a program `x|y` appear only as code spans. Tests:
  `MarkdownTextTests.CodeWrapsTextInASpan`, `MarkdownTextTests.CodeUsesAFenceLongerThanTheTextsBackticks`,
  `MarkdownTextTests.CodeKeepsMarkupLiteral`, `MarkdownTextTests.CodeEscapesInvisibleCharacters`, `MarkdownTextTests.CodeWritesSpacesOnlyAsEscapes`,
  `MarkdownTextTests.CodeOfEmptyTextIsADash`, `MarkdownReportWriterTests.WriterPutsExternalTextOnlyInCodeSpans`.
- [ ] **AC14 — language and formats.** `ReportText.For("en")` and `For("de")` return the two tables, any other value `null`; the
  German report carries exactly the German headings and words of the area document's table and none of the English headings; shares
  `57.1 %` / `57,1 %`; MiB rounded half away from zero (1.5 MiB → 2); times `2026-10-02 06:59 UTC` from the UTC instant of a
  `DateTimeOffset` with offset +02:00; lookback in minutes. Tests: `ReportTextTests.ReportTextForReturnsEnglishAndGerman`,
  `ReportTextTests.ReportTextForRefusesOtherLanguages`, `MarkdownReportWriterTests.WriterUsesTheHeadingsOfTheLanguage`,
  `MarkdownReportWriterTests.WriterFormatsNumbersPerLanguage`, `MarkdownReportWriterTests.WriterWritesTimesInUtc`.
- [ ] **AC15 — summary and unknown wording.** The summary has the outage and kill counts, the first and last outage, one line per
  detected pattern, and a line naming the unknown rules; "No recurring pattern detected" only when every rule was decided, else "No
  recurring pattern detected among the decided rules"; a rule result unknown is written as `unknown` with its reason, never as
  `not detected`. Tests: `MarkdownReportWriterTests.WriterListsDetectedPatternsInTheSummary`,
  `MarkdownReportWriterTests.WriterNeverWritesUnknownAsNotDetected`.
- [ ] **AC16 — record paging extracted.** `RecordPager.PageAsync` pages through records that share a capture time (keyset on the
  record ID) and fails with `store: reading records: page did not advance`; the existing `IncidentReconstructorTests` stay green
  unchanged. Tests: `RecordPagerTests.RecordPagerPagesThroughRecordsThatShareATime`, `RecordPagerTests.RecordPagerFailsWhenPageDoesNotAdvance`.
- [ ] **AC17 — head of the report.** The head gives the generation time from the injected clock, the log line count with first and
  last capture time, the observed hours, the process snapshot count with first and last time and the lookback; with an empty store
  the report still completes (zero outages, every rule unknown, consumers unknown). Tests:
  `ForensicsReporterTests.ForensicsReporterWritesTheHead`, `ForensicsReporterTests.ForensicsReporterCompletesOnAnEmptyStore`.

Resource bound (step 6, Tester's coverage pass, not step 5): `ForensicsReporterTests.ForensicsReporterKeepsOneIncidentAtATime` — a
recording incident store asserts that no query asks for more than one incident (the bound of AC4 at the store boundary).

## Approach

Three layers, following `vandoxd incidents`:

1. **Analysis, pure** (`src/Vandox.Core/Reports`, no I/O): `ForensicsAnalysis` takes log lines (time, source), boots, process
   snapshots and incidents and produces `ForensicsSummary` and per-outage `IncidentActivities`. Its parts: `ObservedHours` (distinct
   UTC hours of system-log lines, hour-of-day counts, capped runs), `SnapshotSeries` (capped compact points: time, panel memory,
   backup present; lookback indicators; percentile), `ConsumerTracker` (per program presence, mean, peak; capped programs),
   `RecurrenceRules` (binomial tail, three-way result), `PanelPrograms`. Use order: `AddLogLine`/`AddBoot`/`AddProcessSnapshot`, then
   `Seal()` (threshold, base rates over the end of every observed hour), then `AddIncident` for each incident and `Activities` for any
   incident, then `Summarize`. Calls out of order throw `InvalidOperationException`.
2. **Reader and writer** (`src/Vandox.Backend/Reports`): `ForensicsReporter` runs the passes against `IRecordReader` and
   `IIncidentStore`: all log lines in pages of 1,000 through `RecordPager` with a `SignatureDetector` (boots only), all process
   snapshots one per page, `Seal`, incidents pass 1 (limit 1, from = previous window end, until empty or `StorableTime.Max`),
   `WriteSummaryAsync`, incidents pass 2 with `WriteIncidentAsync` each, `WriteNotesAsync`. `MarkdownReportWriter` formats with a
   `ReportText` table; `MarkdownText.Code` writes every external string.
3. **Command** (`src/Vandox.Backend/Cli`): `ReportCommand` on `StoreCommand.RunAsync`, like `IncidentsCommand`; `CommandLine` parses
   `report` and `-lang`; `BackendApp` dispatches; `BackendLog` gains three messages.

`IncidentReconstructor.PageAsync` moves unchanged into `RecordPager` (both callers use it; avoids a duplicated block for the cloud
analysis).

Rules, formats and limits: [Detection, alerts and reports](../../docs/areas/detection-and-reports.md) (*Forensics report*), command
line: [Backend host](../../docs/areas/backend-host.md). Base-rate evaluation iterates the end of every observed hour; the Dev keeps it
at O((hours + snapshots + boots) · log) per run (sweeping or binary search, not a scan per hour).

## Options considered

Weighed in the records (each with why the others lost):

- Delivery, computation, holding the outages, the series and external text: record 0098 — sub-command with Markdown on standard
  output (vs. web page now: no login, #24/#25; vs. `-o file`: a file write from a CLI path, security area 9); on demand, nothing stored
  (vs. stored by `vandoxd incidents`); two passes over the incidents (vs. collecting all: unbounded; vs. listing at most N: breaks AC
  "every outage"); capped compact series (vs. a store query per lookback: reads multiply by observed hours); code spans (vs.
  backslash escaping: GFM extended autolinks; vs. HTML entities).
- Rules: record 0099 — observed system-log hours as base (vs. calendar time: counts missing data as "no"; vs. the instantaneous
  snapshot share: not comparable with a lookback); the report's own lookback indicator (vs. the stored hypothesis: cut at the boot, no
  unknown boot); binomial tail with Bonferroni (vs. factor and minimum count: 16 % false clusters at ten outages; vs. chi-square:
  counts too small); 24 one-hour bins (vs. sliding three-hour windows); `sw-engi` prefix with 90th percentile (vs. a fixed MiB value;
  vs. a process count); 90 % presence for permanent (vs. variance).
- Language: record 0100 — text tables in code, `-lang`, default `en` (vs. `.resx` with cultures: refused under
  `InvariantGlobalization`; vs. a configuration option; vs. `LANG`; vs. German by default).
- Paging helper: move `IncidentReconstructor.PageAsync` into `RecordPager` (chosen) vs. a copy in the reporter (a duplicated block
  that SonarQube Cloud reports as duplication on new code).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Core | `Reports/Indicator.cs`, `Reports/PatternOutcome.cs`, `Reports/UnknownReason.cs` | new enums |
| Vandox.Core | `Reports/ReportLimits.cs`, `Reports/PanelPrograms.cs`, `Reports/RecurrenceRules.cs` | new static classes |
| Vandox.Core | `Reports/ObservedHours.cs`, `Reports/SnapshotSeries.cs`, `Reports/ConsumerTracker.cs`, `Reports/ForensicsAnalysis.cs` | new |
| Vandox.Core | `Reports/PatternResult.cs`, `Reports/ConsumerSummary.cs`, `Reports/IncidentActivities.cs`, `Reports/ForensicsSummary.cs` | new result types |
| Vandox.Backend | `Incidents/RecordPager.cs` | new (moved from `IncidentReconstructor.PageAsync`) |
| Vandox.Backend | `Incidents/IncidentReconstructor.cs` | rewrite: the private `PageAsync` removed, its five calls go to `RecordPager.PageAsync(_records, …)`; nothing else changes |
| Vandox.Backend | `Reports/ForensicsReporter.cs`, `Reports/ReportResult.cs`, `Reports/MarkdownReportWriter.cs`, `Reports/MarkdownText.cs`, `Reports/ReportText.cs` | new |
| Vandox.Backend | `Cli/ReportCommand.cs` | new |
| Vandox.Backend | `Cli/CommandLine.cs` | rewrite: `report`, `-lang`, usage texts, class comment |
| Vandox.Backend | `Cli/CommandOptions.cs` | add `Report`, `Language` |
| Vandox.Backend | `Hosting/BackendApp.cs` | dispatch `options.Report` after `Templates` |
| Vandox.Backend | `Hosting/BackendLog.cs` | add `ReportStarted` (38), `ReportInterrupted` (39), `ReportFailed` (40) |

No storage, schema, configuration or dependency change.

## Signatures (for the Dev's skeleton)

`src/Vandox.Core/Reports` (namespace `Vandox.Core.Reports`; one type per file):

```csharp
public enum Indicator { Unknown, No, Yes }
public enum PatternOutcome { Unknown, NotDetected, Detected }
public enum UnknownReason { None, TooFewIncidents, NoBaseData, TooManySnapshots, TooManyBoots, TooManyObservedRuns, NoPanelMemory }

public static class ReportLimits
{
    public const int MaxSnapshots = 1000000;
    public const int MaxBoots = 10000;
    public const int MaxObservedRuns = 100000;
    public const int MaxPrograms = 4096;
    public const int MaxConsumers = 5;
    public const int MinKnownIncidents = 3;
    public const int MinMatchingIncidents = 2;
    public const double SignificanceLevel = 0.01;
    public const double PermanentShare = 0.9;
    public const double PanelPercentile = 0.9;
}

public static class PanelPrograms
{
    public const string Prefix = "sw-engi";
    public static bool IsPanel(string program);
    public static ulong RssBytes(IReadOnlyDictionary<string, ProgramUsage> programs);   // saturating sum of the panel programs
}

public static class RecurrenceRules
{
    public static double BinomialTail(long trials, long successes, double probability);
    public static PatternResult Evaluate(long matching, long known, long baseMatching, long baseKnown, double level);
}

public sealed class PatternResult
{
    public PatternOutcome Outcome { get; init; }
    public UnknownReason Reason { get; init; }
    public long Matching { get; init; }
    public long Known { get; init; }
    public long BaseMatching { get; init; }
    public long BaseKnown { get; init; }
    public double? Share { get; }        // Matching / Known; null when Known is 0
    public double? BaseShare { get; }    // BaseMatching / BaseKnown; null when BaseKnown is 0
}

public sealed class ObservedHours
{
    public ObservedHours();
    public ObservedHours(int maxRuns);
    public long Count { get; }
    public IReadOnlyList<long> HoursOfDay { get; }   // 24 counts, index = UTC hour of day
    public bool Truncated { get; }
    public void Add(DateTimeOffset capturedAt);      // ArgumentException when earlier than the previous time
    public bool Covers(DateTimeOffset from, DateTimeOffset to);   // ArgumentException when from >= to
    public IEnumerable<DateTimeOffset> Ends();       // the end of every kept observed hour, ascending
    public static long HourOf(DateTimeOffset at);    // floor((at - Unix epoch) / 1 h)
}

public sealed class SnapshotSeries
{
    public SnapshotSeries();
    public SnapshotSeries(int capacity);
    public int Count { get; }
    public bool Truncated { get; }
    public void Add(DateTimeOffset capturedAt, IReadOnlyDictionary<string, ProgramUsage> programs);   // ArgumentException when earlier
    public ulong PanelThresholdBytes();
    public Indicator Backup(DateTimeOffset from, DateTimeOffset to);
    public Indicator PanelHigh(DateTimeOffset from, DateTimeOffset to, ulong thresholdBytes);   // Unknown when thresholdBytes is 0
}

public sealed class ConsumerSummary
{
    public string Program { get; init; } = string.Empty;
    public long Snapshots { get; init; }
    public double Share { get; init; }
    public ulong MeanRssBytes { get; init; }
    public ulong PeakRssBytes { get; init; }
    public DateTimeOffset PeakAt { get; init; }
}

public sealed class ConsumerTracker
{
    public ConsumerTracker();
    public ConsumerTracker(int maxPrograms);
    public long Snapshots { get; }
    public long UntrackedEntries { get; }
    public void Add(DateTimeOffset capturedAt, IReadOnlyDictionary<string, ProgramUsage> programs);
    public IReadOnlyList<ConsumerSummary> Permanent(int count);
    public IReadOnlyList<ConsumerSummary> Transient(int count);
}

public sealed class IncidentActivities
{
    public Indicator Backup { get; init; }
    public Indicator Boot { get; init; }
    public DateTimeOffset? LastBootAt { get; init; }
    public Indicator PanelHigh { get; init; }
}

public sealed class ForensicsSummary
{
    public DateTimeOffset GeneratedAt { get; init; }
    public TimeSpan Lookback { get; init; }
    public long LogLines { get; init; }
    public DateTimeOffset? FirstLogLineAt { get; init; }
    public DateTimeOffset? LastLogLineAt { get; init; }
    public long ObservedHours { get; init; }
    public long Snapshots { get; init; }
    public DateTimeOffset? FirstSnapshotAt { get; init; }
    public DateTimeOffset? LastSnapshotAt { get; init; }
    public long Incidents { get; init; }
    public long Kills { get; init; }
    public DateTimeOffset? FirstOutageAt { get; init; }
    public DateTimeOffset? LastOutageAt { get; init; }
    public IReadOnlyList<PatternResult> TimeOfDay { get; init; } = [];   // 24 entries
    public PatternResult Backup { get; init; } = new();
    public PatternResult Boot { get; init; } = new();
    public PatternResult PanelHigh { get; init; } = new();
    public ulong PanelThresholdBytes { get; init; }
    public IReadOnlyList<ConsumerSummary> Permanent { get; init; } = [];
    public IReadOnlyList<ConsumerSummary> Transient { get; init; } = [];
    public long UntrackedProgramEntries { get; init; }
    public bool SnapshotsTruncated { get; init; }
    public bool BootsTruncated { get; init; }
    public bool ObservedHoursTruncated { get; init; }
}

public sealed class ForensicsAnalysis
{
    public static IReadOnlyList<string> SystemLogSources { get; }   // "journal", "syslog"
    public ForensicsAnalysis(TimeSpan lookback);   // ArgumentOutOfRangeException when not positive
    public ForensicsAnalysis(TimeSpan lookback, int maxSnapshots, int maxBoots, int maxObservedRuns, int maxPrograms);
    public void AddLogLine(DateTimeOffset capturedAt, string source);
    public void AddBoot(DateTimeOffset at);
    public void AddProcessSnapshot(DateTimeOffset capturedAt, ProcessSnapshot snapshot);
    public void Seal();
    public IncidentActivities Activities(Incident incident);
    public void AddIncident(Incident incident);
    public ForensicsSummary Summarize(DateTimeOffset generatedAt);
}
```

`src/Vandox.Backend` (namespaces `Vandox.Backend.Incidents`, `Vandox.Backend.Reports`, `Vandox.Backend.Cli`, `Vandox.Backend.Hosting`):

```csharp
internal static class RecordPager
{
    internal static Task PageAsync(IRecordReader records, RecordQuery range, int limit, Func<StoredRecord, Task> handle, CancellationToken cancellationToken);
}

public sealed class ReportResult
{
    public long LinesRead { get; set; }
    public long SnapshotsRead { get; set; }
    public long IncidentsListed { get; set; }
}

public sealed class ForensicsReporter
{
    internal const int LinePage = 1000;
    internal const int SnapshotPage = 1;
    internal const int IncidentPage = 1;
    public ForensicsReporter(IRecordReader records, IIncidentStore incidents, SignatureCatalog catalog, TimeSpan lookback, TimeProvider clock);
    public ILogger? Logger { get; init; }
    public Task<ReportResult> RunAsync(MarkdownReportWriter writer, CancellationToken cancellationToken);
}

public sealed class ReportText
{
    public static ReportText English { get; }
    public static ReportText German { get; }
    public static IReadOnlyList<string> Languages { get; }   // "en", "de"
    public static ReportText? For(string language);           // ordinal; null for any other value
    public string Language { get; }
    public char DecimalSeparator { get; }
    // plus one public get-only string property per label of the report (names are the Dev's; the fixed headings and words are
    // in the table of the area document, the rest follows its *Content and format*); every label is set in both tables
}

internal static class MarkdownText
{
    internal static string Code(string text);
}

public sealed class MarkdownReportWriter
{
    public MarkdownReportWriter(TextWriter writer, ReportText text);
    public Task WriteSummaryAsync(ForensicsSummary summary);            // head, summary, patterns, consumers, the "Outages" heading
    public Task WriteIncidentAsync(StoredIncident stored, IncidentActivities activities);
    public Task WriteNotesAsync(ForensicsSummary summary, long listed);  // listed: incidents of the second pass
}

internal static class ReportCommand
{
    internal static Task<int> RunAsync(string configPath, string language, IEnumerable<KeyValuePair<string, string>> environment, TextWriter stdout, TextWriter stderr, ServeHooks hooks, CancellationToken cancellationToken);
}

// CommandOptions
public bool Report { get; set; }
public string Language { get; set; } = "en";

// BackendLog
[LoggerMessage(EventId = 38, Level = LogLevel.Information, Message = "report started")]
internal static partial void ReportStarted(this ILogger logger);
[LoggerMessage(EventId = 39, Level = LogLevel.Warning, Message = "report interrupted; the report on standard output is incomplete")]
internal static partial void ReportInterrupted(this ILogger logger);
[LoggerMessage(EventId = 40, Level = LogLevel.Error, Message = "report failed {Error}")]
internal static partial void ReportFailed(this ILogger logger, Exception exception, string error);
```

`CommandLine` keeps its public surface (`Parse`, `Usage`, `BinaryName`); its private `ParseSubFlags` gains the knowledge of
`-lang` for `report` only (signature the Dev's). Files the skeleton rewrites: `CommandLine.cs`, `CommandOptions.cs`, `BackendApp.cs`,
`BackendLog.cs`, `IncidentReconstructor.cs` (read; the last is final after the move of `PageAsync`).

## Test files

`tests/Vandox.Core.Tests/`: `ObservedHoursTests.cs`, `SnapshotSeriesTests.cs`, `ConsumerTrackerTests.cs`, `RecurrenceRulesTests.cs`,
`PanelProgramsTests.cs`, `ForensicsAnalysisTests.cs` (the project's flat layout, one file per type under test).

`tests/Vandox.Backend.Tests/`: `RecordPagerTests.cs`, `ForensicsReporterTests.cs`, `MarkdownReportWriterTests.cs`,
`MarkdownTextTests.cs`, `ReportTextTests.cs`, `ReportCommandTests.cs`, and new methods in the existing `CommandLineTests.cs`.
Fixtures: records through `IncidentSamples` (Backend tests) against the real store in a `TempDirectory`; incidents saved through the
store or built by `IncidentReconstructor`; `FakeTimeProvider` for the generation time; hand-written decorators for the store
failures and the page checks (the Tester names them in `.squad/project.md`, *Test doubles*).

Existing test code that calls a changed signature: none (`IncidentReconstructor`'s public surface, `CommandLine.Parse`/`Usage` and
`CommandOptions` stay compatible; the existing usage tests use `Assert.Contains` on the main usage).

## Areas

- [Detection, alerts and reports](../../docs/areas/detection-and-reports.md): new section *Forensics report*; *Scope*, *Not here*,
  *Implementation*, *Related decisions* updated. Written by the Lead with this plan.
- [Backend host](../../docs/areas/backend-host.md): *Command line and exit codes* gains `report`. Written by the Lead with this plan.

No new area.

## Documentation updates

| File | Edit | Owner |
| ---- | ---- | ----- |
| `docs/areas/detection-and-reports.md`, `docs/areas/backend-host.md` | as above (done in step 2); a deviation in step 6 is reported to the Lead, who adapts the text | Lead |
| `docs/ARCHITECTURE.md` | current state, components, data flow, records (done in step 2) | Lead |
| `README.md` | *Binaries* paragraph names `vandoxd report`; new section *Write the forensics report* after *Reconstruct incidents*: run after `vandoxd incidents`, example `docker exec vandoxd /vandoxd report -lang de > forensics.md` **without** `-t` (a terminal turns line feeds into CR LF), what the report contains, unknown is not "no", all times UTC, exit codes; the options table entry of `incidents.window_before` adds "also the lookback of the forensics report" | Dev |
| `.squad/project.md` | *Security areas* 10: the report's reads and caps (`ForensicsReporter`, `ForensicsAnalysis`, `ReportLimits`: 1,000,000 snapshot points, 10,000 boots, 100,000 runs, 4,096 programs, incidents one at a time twice); 12: `vandoxd report` writes victim commands and program names only through `MarkdownText.Code` (code spans, escapes as `Terminal.Quote`), the `-lang` value in its usage error through `Terminal.Quote`; the record list gains 0098, 0099, 0100 | Dev |
| `.squad/project.md` | *Test doubles*: the decorators the report tests add, in the database (C#) row | Tester |
| `docs/decisions/0098`–`0100`, index | Proposed now; Accepted at approval | Lead |

## Architecture check

- **No data gaps unless recorded / unknown is not normal** (guarantee 1): kept and extended — every activity and rule has an unknown
  state; hours without the system log are unknown and excluded from base rates; the summary never says "no pattern" while a rule is
  unknown.
- **Backfilled data never alerts** (guarantee 3): the report sends nothing and alerts nothing.
- **Hanging collector** (guarantee 2): agent only, untouched.
- **Deterministic detection** (0008): fixed rules, no AI.
- **Web UI behind a login** (0016): respected by the split; the web page waits for #24 and #25.
- The flow "Reports" of the architecture diagram is the nightly report via Telegram; this report is a command output, described in
  *Data flow* (updated).

## Security considerations

- [x] Limits: the caps (snapshot points, boots, runs, programs) count **decoded, stored** items, never bytes; the escaping in
  `MarkdownText.Code` works on decoded UTF-16 text by Unicode scalar value (a lone surrogate is U+FFFD there, which is escaped); the
  incidents' 8 MiB bound is the store's, on serialized JSON. No new byte limit on input.
- [x] Exceptions that reach a user-visible message: `StoreException` (store texts such as `store: reading incidents: page did not
  advance`, `store: reading incidents: decoding incident data`, `store: reading records: page did not advance`) → JSON log line
  `report failed` with that text; `OperationCanceledException` → `report interrupted`; `ConfigException` → `configuration invalid` as
  today; opening failure → `opening database failed` as today; usage errors → the exact texts of AC1 (the language value through
  `Terminal.Quote`). The analysis' `ArgumentException`/`InvalidOperationException` (order violations) cannot arise from the store's
  order and are not caught (a bug is not hidden).
- Guard on `-lang` (a value a parser consumes): the value is compared ordinally with exactly `en` and `de`; enumerated forms of the
  flag: `-lang v`, `--lang v`, `-lang=v`, `--lang=v` (any number of leading dashes, as every flag: `CommandLine.Split` trims them
  all, so `---lang de` is accepted like `-lang de`; `=` splits at the first `=`, so `-lang=de=x` has value
  `de=x` → refused), `-lang` as last argument → missing value, `-lang=` → empty value → refused, a value starting with `-` after
  `-lang ` is taken as the value (as `-config` does) → refused unless `en`/`de`, case variants and `de-DE`, `de_DE`, ` de`, `de\n`,
  full-width or Cf-bearing look-alikes → refused; the refused value is shown only through `Terminal.Quote`. Nothing else consumes it.
- Markdown injection (area 12): every external string goes through `MarkdownText.Code`; forms a renderer could interpret and that
  the code span neutralizes: inline HTML and comments, entities, links, images, autolinks `<…>`, GFM extended autolinks (`www.`,
  `http://`, e-mail), emphasis, headings (a line start cannot occur: no line feed survives), table pipes (no external text in tables),
  backtick runs (fence longer than any run), leading/trailing spaces (padding rule), bidirectional and zero-width characters (escaped).

## Decision records

- [0098](../../docs/decisions/0098-forensics-report-as-markdown-from-vandoxd-report.md) (Proposed) — sub-command and Markdown, on
  demand, two passes, capped series, code spans, web page split off.
- [0099](../../docs/decisions/0099-forensics-rules-share-of-outages-against-observed-hours-binomial-tail.md) (Proposed) — the rules.
- [0100](../../docs/decisions/0100-report-language-from-a-flag-english-default-text-tables-in-code.md) (Proposed) — the language.

## Out of scope / follow-ups

Follow-up issue for the orchestrator to create (PM decision 1):

- **Title:** `[Web] Forensics report page`
- **Body:** Context: #28 delivers the forensics report as Markdown (`vandoxd report`, records 0098–0100, area *Detection, alerts and
  reports*, *Forensics report*). Goal: the report in the web UI. Requirements: a page behind the login that renders the analysis
  (`ForensicsAnalysis`/`ForensicsSummary`, outages read one at a time, never all at once), in English or German per user choice
  (`ReportText`), with a download of the same Markdown (`MarkdownReportWriter`); all external text escaped by Razor; a deadline on the
  run (it reads every stored line). Acceptance criteria: the page shows the same content as `vandoxd report`; the Markdown download
  equals the command's output for the same data and language. Dependencies: depends on #24, #25 and #28.
- Then #29 gets "Depends on" this new issue (the orchestrator edits #29's dependency list).

Not in this change: time zones other than UTC, a language option in the configuration, a time range, the agent's records (#53),
backup programs beyond `pzstd` (#36), retention (#46).

## Challenge notes

Devil's Advocate: OBJECTIONS 0 major, 3 minor. Security plan review: APPROVED, 6 non-blocking points. None changes the tier or the acceptance criteria; Dev and Tester take them with the plan.

Devil's Advocate (minor):

1. **Standard output encoding is not pinned.** The area document says "the text is UTF-8", and the German headings (`Übersicht`) and `—` / `–` are non-ASCII. `Program.cs:43` passes the unmodified `Console.Out` to `BackendApp.RunAsync`, so its encoding comes from the locale (for example `LANG=…ISO-8859-1`). Nothing in the plan or the tests sets or checks the encoding. `incidents` output goes through `Terminal.Quote`, which is probably ASCII, so this is new here. The Dev should either write bytes as UTF-8 without a BOM in `ReportCommand`/`Program` (for example a `StreamWriter` over `Console.OpenStandardOutput()`), or state in the README next to the `> forensics.md` example that the report is UTF-8 whatever the locale. `ReportCommandTests` could then assert that a `de` report written to a stream has no `?` in place of `Ü`.
2. **A stale incident table is silent.** The report reads whatever `vandoxd incidents` last stored. If logs were imported after that run, the outage list and the rules are out of date, and only the docs ask the operator to run `incidents` first. The two-pass count check does not catch this. The Dev could add a note to the report's Notes or Head, for example the newest log line or snapshot time against the last incident's `UpdatedAt`. If the Lead rejects that, the README sentence must say it clearly.
3. **Snapshots are read one per query, with no cost statement.** `SnapshotPage = 1` means up to 1,000,000 keyset queries (the `MaxSnapshots` cap) plus a full pass over the log lines. That is consistent with `IncidentReconstructor`'s `SnapshotPage`. The Dev should still add one sentence to the README or the notes saying the run reads every stored snapshot and line and can take minutes.

Security (non-blocking):

1. **Incident counts must stay counters.** The doc comment of `ForensicsAnalysis.AddIncident` in the skeleton (plan lines 354-366) must state that it keeps only counters (24 hour counts, per-activity yes/known counts, first/last outage, kill sum, saturating); a per-incident list, which forged kills can grow, breaks the memory claim in area *Limits of the report* and record 0098 option 6.
2. **The table rule needs a test.** `WriterPutsExternalTextOnlyInCodeSpans` must also assert that no line holding external text starts with `|`, and that the writer leaves a blank line before every table.
3. **Boot ordering is unstated.** If `ForensicsAnalysis.AddBoot` assumes ascending order for a binary search, sort the boots or state their order; the analysis's `ArgumentException` is deliberately not caught.
4. **The backslash statement is wrong.** Record 0098 (lines 83-84) and the area text (lines 533-534) say a literal backslash-u202e in a name "reads like an escape in the rendered text". Backslash escapes do not apply inside code spans, so the rendered output shows two backslashes, which is distinguishable from a real U+202E. Only the wording needs fixing.
5. **Unquoted flag names in errors (existing defect).** `ParseSubFlags` and `ApplyFlag` print the unknown flag's name without quoting (`CommandLine.cs:153` and `:274`); `ParseArgument` already uses `Terminal.Quote`. This change rewrites `ParseSubFlags`, so quote there too.
6. **The retained-memory figure is incomplete.** The area document gives about 24 MB for 1,000,000 snapshot points; a list of a 32-byte struct reaches roughly 33 MiB at that capacity and about 50 MiB while it is copied to grow. State the figure including the program-name bound (4,096 programs, about 8 MiB of strings plus counters).
