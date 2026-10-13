# Plan: Logs view

Source: Issue #26 | [spec.md](spec.md)
Status: Draft
Tier: security — the change adds a new path from a browser address to the FTS5 query parser and to SQL (security area 10,
*Parsing of external input*), shows stored external text in the web UI (area 12, *Logging and display of external data*), and makes
every stored log line readable on the web port while the login (#25, area 3) does not exist yet.

## Problem / root cause

Feature; summary of [spec.md](spec.md): two pages, `/logs` (search with filters, pages, context of a hit) and `/logs/templates`
(templates of a range with first seen, counts and the hourly chart of a chosen one, and a link to exactly its lines), every filter in
the address.

Claims of the issue, checked against the code (base `4d5dc41`):

- "Full-text search (FTS5)" — **confirmed**: `log_fts` (FTS5, external content `log_lines`, tokenizer `unicode61 remove_diacritics 2`;
  `src/Vandox.Storage/SchemaMigrator.cs` step 2), filled in the batch transaction by `BatchWriter`; the search is
  `SqliteStore.SearchLogsAsync` (`src/Vandox.Storage/SqliteStore.cs:470`) with `RecordQueries.Build(LogSearch)`
  (`src/Vandox.Storage/RecordQueries.cs`, the method after `Build(RecordQuery)`) and the literal-term guard `FtsQuery.Build`
  (`src/Vandox.Storage/FtsQuery.cs`, record 0066).
- "filters for source, severity, service and time range" — **partly present**: `LogSearch` has text, source, from, to and limit only
  (`src/Vandox.Storage/LogSearch.cs`); there is no program (service) or priority (severity) filter, no paging position, and the text is
  required (`FtsQuery.Build` refuses an empty text: "search text has no terms"). Severity is `log_lines.priority` (0–7 or `NULL`), the
  service is `log_lines.program` (journal: `SYSLOG_IDENTIFIER`, else `_COMM`, `JournalExportParser.Program`; syslog: the tag), the
  source is `records.source` (the parser type for imports, e.g. `journal`, `syslog`, `mariadb`).
- "answers in under two seconds over several million lines" — **not met by the existing query shape for common terms**:
  `RecordQueries.Build(LogSearch)` joins every FTS5 match with `log_lines` and `records` and sorts by `captured_at, id` before the
  `LIMIT`; record 0066 (Consequences) and the storage area (*Log search*) state that the cost grows with the stored lines that contain
  the terms, not with the range or the limit, and leave "narrowing by time first" to the logs view. A word in a quarter of 5,000,000
  lines means about 1,250,000 row lookups and a sort per page. Unverified by measurement here (the plan assigns the measurement,
  AC-19); the design below bounds every request instead.
- "Template view: templates with first seen and hourly counts" — **statistics confirmed, query missing**: `log_templates`
  (`first_seen`, `last_seen`, `count`) and `log_template_hours` exist (schema step 8); `ILogTemplateReader`
  (`src/Vandox.Storage/ILogTemplateReader.cs`) offers the template of a line, the hours of one template, new templates and spikes,
  but no list of the templates of a range with their counts.
- "switch from a template to its lines" — **not supported by the schema**: no table links a log line to its template; a template is
  identified only by (source, program, normalized text) (`log_templates` unique key). The lines of a template are the lines of that
  source and program whose message `LogTemplateNormalizer.Normalize` turns into that text.
- "Filters encoded in the URL" — nothing exists; the web UI has one page (`src/Vandox.Backend/Components/Pages/Home.razor`), the
  navigation one entry (`Components/Layout/MainLayout.razor`). `ILogSearcher` and `ILogTemplateReader` are already registered in the
  service's container (`src/Vandox.Backend/Hosting/ServeCommand.cs`, `Build`), the store is open while the service runs.
- Dependencies #24 (PR #229, `51370be`) and #22 (PR #211, `1aedd12`) — **confirmed** merged.

Related defects found on the way (none blocks this plan; each is handled below):

- `docs/ARCHITECTURE.md` says of the templates "nothing shows them yet" and `docs/areas/detection-and-reports.md` (*Log templates*)
  "No command or view shows them yet"; both become wrong with this change (Dev updates them, *Documentation updates*).
- The prerender of an Interactive Server page runs `OnInitializedAsync`/`OnParametersSetAsync` once while prerendering and again
  when the circuit starts; a page that queries in those methods runs every search twice. Handled by AC-14.4.
- `Home.razor` says "the logs … follow with the releases"; it becomes stale (Dev rewords the sentence; `HomePageTests` asserts only
  the heading and "The backend is running.").

## Acceptance criteria

Storage (`tests/Vandox.Storage.Tests/SqliteStoreLogExplorerTests.cs` unless named otherwise; real store in a `TempDirectory`):

- [ ] AC-1 Filters: `FindLogsAsync` returns the log lines that match every given filter — all words of the text (the rules of
  `FtsQuery`), source (exact), program (exact, ordinal), priority at most `MaxPriority`, capture time in `[From, To)` — ordered by capture
  time, then record ID. With `MaxPriority` set, lines without a priority are not returned. An empty text applies only the other
  filters. Tests: `FindLogsAppliesEveryFilter`, `FindLogsWithSeverityExcludesLinesWithoutPriority`,
  `FindLogsWithoutTextReturnsFilteredLinesInTimeOrder`.
- [ ] AC-2 Pages: at most `Limit` lines; when the page is full, `Next` is the position (capture time, ID) of its last line and
  `ScanLimitReached` is false; a call with `After = Next` continues with the next line, without a gap or a repeat, also across lines
  that share a capture time; when the range is exhausted `Next` is `null`. Tests: `FindLogsPagesThroughLinesSharingACaptureTime`,
  `FindLogsReturnsNoNextAtTheEnd`.
- [ ] AC-3 Strategies: the result does not depend on the strategy. For the same filters, the internal overload
  `FindLogsAsync(filter, selectiveMatches, token)` with `selectiveMatches = 1` (every text search scans) and with
  `selectiveMatches = 1_000_000` (every text search runs from the index) returns the same pages (lines, `Next`), for a common word, a
  rare word, two words, with and without source, program and priority filters, and with `After`. Test:
  `FindLogsReturnsTheSameLinesWithEitherStrategy` (data rows per filter set).
- [ ] AC-4 Scan bound: a call that scans examines at most `ScanLimit` log lines of the range (and of the source, when given), counted
  whether or not they match; when the bound ends the call before the page is full, `ScanLimitReached` is true and `Next` is the
  position of the last examined line (also when the page is empty); continuing from `Next` until it is `null` returns every matching
  line exactly once. Tests: `FindLogsStopsAtTheScanLimitAndContinues`, `FindLogsWithoutTextStopsAtTheScanLimit`. (The membership
  test of scan mode is the correlated `EXISTS` per examined line only, *Approach* step 2, so nothing in scan mode grows with the number
  of matches; Security B1.)
- [ ] AC-5 Template lines: with `TemplateId`, `FindLogsAsync` returns exactly the lines of the template's source and program whose
  message normalizes to the template's text (compared with a brute-force normalization of every stored line), with either strategy,
  combined with the other filters as an AND. Messages: every line of `testdata/logs/mail.log`, `kern.log-oom`, `mariadb-error.log`,
  `nginx-error.log`, `dovecot.log` and `plesk-panel.log` stored as a message (source and program set by the test), plus a 16,384-byte
  message whose template is cut at 1,024 bytes and a message without a literal word (`12345 67890`). Tests:
  `FindLogsWithTemplateReturnsExactlyItsLines`, `FindLogsWithTemplateWithoutWordsScans`, `FindLogsWithACutTemplateFindsItsLines`,
  `FindLogsWithUnknownTemplateIsRefused` (`store: invalid query: unknown template`).
- [ ] AC-6 Template words (`tests/Vandox.Storage.Tests/TemplateTermsTests.cs`): `TemplateTerms.SearchText(template)` returns the
  words of the template (split at ASCII space) that contain no `<` and no `>`, hold a letter or decimal digit and no character of
  category Cc or Cf, in template order, at most 16 and at most 1,024 UTF-8 bytes joined with single spaces; when the template is longer
  than 1,020 UTF-8 bytes (it may have been cut inside a word) its last word is never used; an empty text when no word qualifies.
  Tests: `SearchTextSkipsWordsWithPlaceholders`, `SearchTextSkipsWordsWithoutLetterOrWithFormatCharacters`,
  `SearchTextKeepsAtMostSixteenWordsWithinTheByteLimit`, `SearchTextDropsTheLastWordOfALongTemplate`,
  `SearchTextIsEmptyWithoutLiteralWords`. The format-character case uses U+202E written in the test source as `\u202e` and asserts
  that the input holds that character (the escape survived).
- [ ] AC-7 Filter rules: `FindLogsAsync` refuses a broken rule with `InvalidQueryException` and exactly these messages (prefix
  `store: invalid query: `): the window rules `from and to are required`, `from and to must be within the storable range`, `from must
  be before to` and `limit must be between 1 and 1000` (the new overload `RecordQueries.ValidateWindow(from, to, limit, maxLimit)`
  with `StorageLimits.MaxFindLimit`; the existing three-argument method delegates to it with `MaxQueryLimit` and keeps its text, so
  a call with `Limit` 1,001 says `1000`, never `10000`); `scan limit must be between 1 and 100000`; `source is longer than 1024 bytes`; `program is longer than 1024 bytes`;
  `priority must be between 0 and 7`; `template ID must not be negative`; `after must be within the storable range`; `after id must be
  positive`; the `FtsQuery` rules for a non-empty text. Null strings are `ArgumentNullException`. Test: `FindLogsRefusesInvalidFilters`
  (one data row per rule, exact message; rows for `Limit` 0 and 1,001 both expect `limit must be between 1 and 1000`).
- [ ] AC-8 Context: `LogContextAsync` returns the line and up to `Before` lines before and `After` lines after it with the same
  source, log name and host, in (capture time, ID) order, whatever their other fields; lines of other logs of the same source are
  skipped; each side examines at most 2,000 lines of the source and returns fewer when that bound or the data ends. Errors: `record ID
  must be positive`, `context lines must be between 0 and 20`, `unknown log line` (an ID that is not a stored log line). Tests:
  `LogContextReturnsNeighboursOfTheSameLog`, `LogContextStopsAtTheExamineBound`, `LogContextRefusesInvalidQueries`.
- [ ] AC-9 Sources: `LogSourcesAsync` returns the distinct sources of stored log lines in ordinal order, at most 64. Tests:
  `LogSourcesReturnsDistinctSourcesInOrder`, `LogSourcesReturnsAtMostSixtyFour`.
- [ ] AC-10 Template list: `TemplatesAsync` returns the templates with at least one line in the hours that start in the range (from the
  hour that holds `From`, before `To`), optionally of one source and one program, each with its number of lines in those hours, ordered
  by that number (highest first), then by template ID, at most `Limit` (1 to 1,000 with `StorageLimits.MaxTemplateListLimit`:
  `limit must be between 1 and 1000`; the other window rules as AC-7, source and program length rules as AC-7). The count is
  hour-granular: a `From` or `To` inside an hour counts that whole hour (Devil's Advocate 3). `TemplateAsync(id)` returns the template or `null` (`template ID must be positive`). `SourceHoursAsync` returns the
  start of every hour in the range in which the source has lines, ascending (source required: `source is required`; limit 1 to
  10,000, the existing `ValidateWindow` and its text `limit must be between 1 and 10000`). Tests: `TemplatesCountsLinesInTheRange`,
  `TemplatesCountsWholeHoursOfAMidHourRange` (lines at 10:05 and 10:50 and 12:10, `From` 10:30, `To` 12:05: both 10:xx lines and
  the 12:10 line are counted, i.e. 3), `TemplatesFiltersBySourceAndProgram`, `TemplatesRefusesInvalidQueries` (exact texts,
  including `limit must be between 1 and 1000` for 1,001), `TemplateReturnsTheTemplateOrNull`, `SourceHoursReturnsCoveredHours`.
- [ ] AC-11 Interruption: a cancelled token ends `FindLogsAsync` (both strategies), `LogContextAsync` and `TemplatesAsync` with
  `OperationCanceledException` and no partial result (record 0097). Test: `FindLogsIsInterruptedByACancelledToken` (data rows per
  method and strategy).

Backend (`tests/Vandox.Backend.Tests`):

- [ ] AC-12 Address (`LogsAddressTests.cs`): `LogsAddress.Parse(query)` reads `q`, `source`, `program`, `severity`, `template`, `from`
  and `to` from a query string (with or without the leading `?`) and `ToQuery()` writes them back in that order, each value through
  `Uri.EscapeDataString`, empty ones left out (`""` when none); `Parse(x.ToQuery())` equals `x` for every field. The forms and rules
  of *Address forms* below hold, each with its exact error. `Validate()` returns the error text the same rules give for the
  address's current values (`""` when valid); `ToQuery()` throws `InvalidOperationException` when `Validate()` is not empty, so a
  lone surrogate is never turned into U+FFFD by `Uri.EscapeDataString` (Security N2). Tests: `ParseReadsEveryParameter`,
  `ParseTreatsEmptyValuesAsAbsent`, `ParseIgnoresUnknownParameters`, `ParseRefusesRepeatedParameters` (rows `q=a&q=b`, `q=a&Q=b`,
  `q=a&%71=b`, `q=a&q`, `q=&q=x`), `ParseRefusesInvalidValues` (one data row per row of the table), `ParseRefusesStartNotBeforeEnd`,
  `ToQueryRoundTrips`, `ToQueryEscapesEveryValue` (values with `&`, `=`, `#`, `?`, `/`, `%`, `+`, space and non-ASCII letters: the
  result starts with `?q=`, holds no unescaped one of them, and `Parse` gives the values back), `ToQueryRefusesAnInvalidValue` (a
  `Text` holding a lone U+D800, written in the test source as a `\ud800` escape, asserted present: `Validate()` names `q` and
  `ToQuery()` throws). The page passes `new Uri(NavigationManager.Uri).Query` to `Parse`, so the fragment never reaches it
  (AC-14.1 uses an address with `#frag`).
- [ ] AC-13 Severities (`SeveritiesTests.cs`): `Severities.TryParse` accepts exactly `emerg`, `alert`, `crit`, `err`, `warning`,
  `notice`, `info`, `debug` (0 to 7) and refuses every other form (`ERR`, `Err`, `3`, ` err`, `err `, `error`, `warn`, empty);
  `Severities.Name` gives the name of 0 to 7. Tests: `TryParseAcceptsTheEightNames`, `TryParseRefusesOtherForms`, `NameNamesEveryPriority`.
- [ ] AC-14 Search page (`LogsPageTests.cs`, with `FakeLogExplorer`, `RecordingNavigationManager`, `FakeTimeProvider` and
  `ComponentTestRenderer`):
  1. the page passes the address filters to the store: text, source, program, `MaxPriority` from the severity, template, `From`/`To`
     (the storable range when absent: `StorableTime.Min` and `StorableTime.Max`), `Limit` 100, `ScanLimit` `LogsView.ScanRows`, no `After`
     — `LogsPagePassesTheAddressFiltersToTheStore` (the address ends in `#frag`; the text the store gets holds no `#`);
  2. it renders each line (time `yyyy-MM-dd HH:mm:ss.fff` UTC, source, host, program, pid, severity name or `-`, message) with
     external text escaped: a message `<b>x</b>` appears as text (`&lt;b&gt;`), U+202E as the visible `\u202e` inside an element of class
     `escape` — `LogsPageRendersLinesWithEscapedText` (the test asserts its input holds U+202E);
  3. an invalid address shows the error of `LogsAddress` and calls no store method — `LogsPageShowsAddressErrorWithoutQuerying`;
  4. rendered statically (`HtmlRenderer`, the prerender) it shows the filters and `Searching…` and calls no store method —
     `LogsPageDoesNotQueryWhilePrerendering`;
  5. an `InvalidQueryException` shows `The search cannot run: <rule>.` (the message without `store: invalid query: `) —
     `LogsPageShowsTheRuleOfARefusedSearch`;
  6. a search still running after `LogsView.Deadline` (10 s on the `FakeTimeProvider`) is cancelled and shows
     `The search took longer than 10 seconds and was stopped. Narrow the time range or add filters.` —
     `LogsPageStopsASearchAtTheDeadline`;
  7. any other exception of the store shows `The logs could not be read. The service log has the details.` and is logged through a
     `[LoggerMessage]` method with the exception — `LogsPageShowsAFailureWithoutDetails`;
  8. submitting the form navigates to `logs` plus `LogsAddress.ToQuery()` of the entered values; the page then searches what the new
     address says — `LogsPageSearchNavigatesToTheAddress`; entered values that `Validate()` refuses show its error and navigate
     nowhere — `LogsPageSubmitWithInvalidValuesShowsTheError`;
  9. a full page offers *Next page* (the next call has `After = Next`) and *First page*; a page with `ScanLimitReached` shows `Searched
     up to <time of Next>.` and *Continue*; *Next page*, *First page* and *Continue* call no `NavigateTo` (the position is page state,
     not part of the address; Devil's Advocate 2) — `LogsPageOffersNextPageAndContinue`;
  10. expanding a hit calls `LogContextAsync` with its ID and 5 and 5 and shows the lines before and after it; expanding another hit
      replaces it — `LogsPageShowsTheContextOfOneHit`;
  11. with `template` the page shows the template (text, source, program, first and last seen, count) above the lines; an unknown
      template shows `This template does not exist. Template numbers change when the statistics are rebuilt.` and searches nothing —
      `LogsPageWithTemplateShowsTheTemplate`, `LogsPageWithUnknownTemplateSaysSo`;
  12. a new address while a search runs cancels the running one's token; disposing the page cancels it too —
      `LogsPageCancelsTheRunningSearch`;
  13. the page never has more than one store call running: for an address with a template it calls `LogSourcesAsync`,
      `TemplateAsync` and `FindLogsAsync` one after another, and a new action (address, *Next page*, *Continue*, expanding a hit)
      cancels the running call and waits for its end before the next call starts (`FakeLogExplorer` records the highest number of
      calls in flight: 1) — `LogsPageRunsOneStoreCallAtATime` (Security B3).
- [ ] AC-15 Templates page (`LogTemplatesPageTests.cs`): lists `TemplatesAsync` of the address (source, program, from, to; limit 200)
  with text, source, program, first seen, last seen, count and count in range (column header `LogsView.CountInRangeHeader` =
  `Lines in range (whole hours)`), escaped like AC-14.2; `template` selects one, whose
  hourly counts (`ILogExplorer.TemplateHoursAsync` and `SourceHoursAsync` of the template's source, both through the gate; the page
  injects no `ILogTemplateReader`) over the hour window `from' = max(from, to - TemplateHourSeries.MaxHours hours)`, `to`, with
  `Limit = TemplateHourSeries.MaxHours + 1` for both queries (the store answers ascending and stops at the limit, so the window, not
  the limit, keeps the last hours of a long range; `TemplateHourSeries.Build(hours, covered, from', to)`) are shown as a `TimeSeriesChart` whose `Title` is the fixed `LogsView.ChartTitle` (`Lines per hour`) and whose one
  series label is the fixed `LogsView.ChartSeries` (`Lines`) — no external text goes into the chart (Security N3) — and whose link
  *Show lines* is `logs?template=<id>` plus the address's `from` and `to`; its store calls run one at a time as AC-14.13; errors as
  AC-14.3/5/6/7 with `The query took longer than 10 seconds and was stopped. Narrow the time range or choose a
  source.` and `The templates could not be read. The service log has the details.` Tests:
  `TemplatesPageListsTemplatesWithFirstSeenAndCounts`, `TemplatesPageLinksATemplateToItsLines`,
  `TemplatesPageChartsTheSelectedTemplate` (the selected template's text holds U+202E, built from its code point and asserted present; the
  chart element's `aria-label` is exactly `Lines per hour`), `TemplatesPageShowsErrors`, `TemplatesPageDoesNotQueryWhilePrerendering`,
  `TemplatesPageRunsOneStoreCallAtATime` (the template's hourly counts included), `TemplatesPageLabelsTheRangeCountAsWholeHours`,
  `TemplatesPageQueriesTheLastHoursOfALongRange` (an address of 120 days: the `TemplateHourQuery` and the `SourceHourQuery` recorded by
  `FakeLogExplorer` have template ID / source, `From` = `to` minus 2,160 hours, `To` = `to`, `Limit` 2,161; for the short range of
  `TemplatesPageChartsTheSelectedTemplate` `From` is the address's `from`).
- [ ] AC-16 Hourly series (`TemplateHourSeriesTests.cs`): `TemplateHourSeries.Build(hours, covered, from, to)` gives one point per
  hour from the first to the last hour of the range that holds a template hour or a covered hour, at most the last 2,160 hours: the
  template's count, 0 for a covered hour without the template, `null` (a gap) for an hour the source did not log. Tests:
  `BuildCountsTemplateHours`, `BuildFillsCoveredHoursWithZero`, `BuildLeavesUncoveredHoursAsGaps`, `BuildKeepsTheLastHoursOfALongRange`.
- [ ] AC-17 External text (`LogTextTests.cs`): `LogText.Runs(text, maxChars)` splits a text into plain runs and escaped runs: every
  character of category Cc except line feed and tab, Cf, Zl, Zp, Co, Cn and U+FFFD becomes `\uXXXX` (lower-case hex) or `\UXXXXXXXX`
  above U+FFFF; consecutive escapes form one run; at most 256 runs, the last holding the rest as `LogText.Escape` text;
  `LogText.Escape(text, maxChars)` is the same as one plain string. Line feed and tab stay. **Output cap** (Security B2): the run
  texts together hold at most `maxChars` UTF-16 characters; an escape is never split; when the text does not fit, the runs end at the
  last whole character or escape that fits and one more escaped run `[cut: <n> more characters]` follows, `<n>` the number of UTF-16
  characters of the input not shown. The callers pass `LogText.MaxChars` (16,384) for a message and `LogText.MaxShortChars` (2,048)
  for every other field (source, host, program, template text), so a message within the 16,384-byte model limit without escapes and a
  template within its 1,024 bytes without escapes are never cut. Cases: U+202E, U+200B, U+0000, U+000D, U+2028, U+E000, U+0378,
  U+FFFD, U+E0001 (`\U000e0001`), `<script>` (unchanged: Razor escapes markup). Tests: `RunsEscapeInvisibleCharacters`,
  `RunsKeepLineFeedAndTab`, `RunsCoalesceConsecutiveEscapes`, `RunsAreAtMostTwoHundredFiftySix`,
  `EscapeWritesSupplementaryCharactersWithEightDigits`, `RunsCutAnAllNulMessageAtTheCharacterLimit` (16,384 U+0000 with `MaxChars`:
  2,730 escapes, i.e. 16,380 characters, then the run `[cut: 13654 more characters]`; total length at most 16,384 plus the marker),
  `RunsKeepAPlainMessageOfSixteenKibibytesWhole` (16,384 ASCII letters: one plain run, no marker), `RunsNeverSplitAnEscape` (a text
  whose cap falls inside an escape ends before it). The cases are built in the test source from code points (`char.ConvertFromUtf32`; the formatter turns a `\u`
  literal into the character itself) and each test asserts its input holds the character. `LogLineViewTests.FromCapsEveryField` (`LogLineView.From(record)` builds the runs of message,
  source, host and program with the two caps and keeps no reference to the record's message). The component that renders the runs:
  `ExternalTextTests.ExternalTextRendersMarkupAsText` (`<b>` appears as `&lt;b&gt;`, an escaped run inside `<span class="escape">`).
- [ ] AC-18 Navigation and serving: the layout has a *Logs* entry with `href="logs"` that is marked on `/logs` and `/logs/templates`
  and not on `/` (`MainLayoutTests.MainLayoutMarksLogsOnTheLogPages`); `GET /logs` and `GET /logs/templates` answer 200 with the
  security headers on the web port and 404 on the ingest port with the real store wired (`ServeTests.ServeServesTheLogPagesOnlyOnWebPort`);
  the `ILogExplorer` the pages get is a `GatedLogExplorer` with `LogsView.MaxConcurrentQueries` slots over the store
  (`ServeTests.ServeGatesTheLogExplorer`, through the new hook `ServeHooks.Built`).
  `WebUiContentTests` keeps passing on the new `.razor` files and `app.css` (existing tests, no new one).
- [ ] AC-19 Performance (measurement, no unit test): `tools/Vandox.StorageBenchmark --search-lines 5000000` writes 5,000,000 generated
  lines and times the first page (100 lines, scan limit `LogsView.ScanRows`) of the cases listed under *Approach*, step 6. Criterion:
  every case under 2,000 ms on the reference host (DS918+, SSD volume). The Dev runs it on the development host and records it in
  `docs/BENCHMARKS.md`; the orchestrator asks the Product Manager to run the published tool on the reference host after task 20 (*Lead decision: benchmark misses*), before the review, and
  the Dev enters that row. A case over 2,000 ms on the reference host goes to the Lead (mode `decide`). Cases 5 and 12 (several common
  words with a rare or empty intersection) are blocking like every other case; when one misses, the fix is a cheaper probe (the
  rarest term's capped count first, or the probe under its own share of the deadline), never a wider cap (Devil's Advocate 4).
- [ ] AC-22 Index mode reads the index once (*Lead decision: benchmark misses*): the selectivity probe returns the matching record IDs
  (at most the cap), and the statement of index mode reads the lines by those IDs without the full-text table:
  `LogFinderQueries.BuildIndex(criteria, ids)` has a query plan in which no step names `log_fts` and the step on `records` is a search by
  its integer primary key (`SqliteStoreLogExplorerTests.FindLogsIndexModeReadsLinesByRecordId`, data rows: no filter; source,
  program, priority and `After`; a template). The results stay those of AC-2 to AC-6 (`FindLogsReturnsTheSameLinesWithEitherStrategy`,
  `FindLogsWithTemplateReturnsExactlyItsLines` and the other existing tests, unchanged).
- [ ] AC-23 Template list in key order (*Lead decision: benchmark misses*): the statement of `LogExplorerQueries.Build(TemplateListQuery)`
  has a query plan in which no step names `log_template_hours_hour` and none is `USE TEMP B-TREE FOR GROUP BY`
  (`SqliteStoreLogExplorerTests.TemplatesSumsTemplateHoursInKeyOrder`, data rows: no filter; source and program). The results stay those
  of AC-10 (`TemplatesCountsLinesInTheRange`, `TemplatesCountsWholeHoursOfAMidHourRange`, `TemplatesFiltersBySourceAndProgram`, unchanged).
- [ ] AC-21 Explorer gate (`GatedLogExplorerTests.cs`, Security B3): `GatedLogExplorer` passes every `ILogExplorer` call to the inner
  explorer with at most `maxConcurrent` calls in flight across all callers; a call waits for a slot with its own token (the page's
  deadline token, so the wait counts against the deadline) and ends with `OperationCanceledException` without calling the inner
  explorer when that token ends first; the slot is released when the inner call completes, fails or is cancelled. Tests:
  `GatedLogExplorerRunsAtMostTheGivenNumberOfCallsAtOnce` (data rows per method, all seven including `TemplateHours`: with 2 slots and 3 blocked calls the inner explorer
  sees 2), `GatedLogExplorerWaitEndsWithTheToken`, `GatedLogExplorerReleasesTheSlotAfterAFailure`,
  `GatedLogExplorerRefusesAnInvalidSlotCount` (`ArgumentOutOfRangeException` for 0 and for more than 3). With
  `LogsView.MaxConcurrentQueries` = 2, the view holds at most two of the store's four reader leases, so `/healthz` (whose
  `PingAsync` leases a reader from the same pool, `src/Vandox.Storage/SqliteStore.cs:103` and `:199`) always finds a slot the view
  cannot take.
- [ ] AC-20 Image: the new `.razor` files and the changed `app.css` are in the image — *Image build check* of `.squad/stack.md`
  (restore-order reproduction), run by the Dev before the review; `ls publish/wwwroot` holds `app.css` and the publish succeeds.

### Address forms

The query string is decoded by `QueryHelpers.ParseQuery` (ASP.NET Core). Checked in a scratch program against .NET 10.0.12, these
are the forms it accepts and what the address parser then does:

| Form | Decoded as | Address parser |
| ---- | ---------- | -------------- |
| `q=a+b`, `q=a%20b` | `a b` | the text `a b` |
| `Q=x` (any ASCII case of the key) | key `q` | same as `q` |
| `%71=x` (escaped key) | key `q` | same as `q` |
| `q=a&Q=b`, `q=a&q=b`, `q=a&%71=b` | two values of `q` | `The address gives the parameter q more than once.` |
| `q=a&q`, `q=&q=x` | two values of `q`, one of them empty | `The address gives the parameter q more than once.` (more than one value is repeated, even when some are empty) |
| `q`, `q=` | empty value | absent |
| `q=a#frag` | never seen: the page passes `new Uri(NavigationManager.Uri).Query`, which drops the fragment | the text `a` |
| `q=` followed by a raw lone surrogate U+D800 and `x` (unescaped in the URI string) | left unchanged | invalid: not valid UTF-16 |
| `%71%3d=x` | key `q=` | unknown, ignored |
| `ſource=x` (U+017F), `ſ`, U+212A or `ı` in any key | the key as written (`OrdinalIgnoreCase` does not fold them) | unknown, ignored |
| `q=%zz`, `q=%` | literal `%zz`, `%` | the literal text (FTS rules then apply) |
| `q=%C0%AF`, `q=%ED%A0%80` (invalid UTF-8, encoded surrogate) | left literal: `%C0%AF`, `%ED%A0%80` | the literal text |
| `q=%00x` | U+0000 then `x` | invalid: Cc |
| `q=%E2%80%AE` | U+202E | invalid: Cf |
| `q=%F0%9F%98%80` | U+1F600 (a surrogate pair) | accepted |
| `q=a=b`, `q=x;y` | `a=b`, `x;y` (`;` is no separator) | literal text |
| `;q=x` | key `;q` | unknown, ignored |

Rules of the values, each refused with `The address has an invalid value for the parameter <name>.`:

- `q`, `source`, `program`: valid UTF-16, at most 1,024 UTF-8 bytes after decoding, no character of category Cc, Cf, Zl or Zp
  (white space other than U+0020 included). `q` is then checked by the store's rules (AC-14.5).
- `severity`: exactly one of the eight names of AC-13.
- `template`: 1 to 18 ASCII digits, value at least 1; no sign, space or other digit script (`long.TryParse` with `NumberStyles.None`
  and the invariant culture, after a check that every character is `0`–`9`).
- `from`, `to`: exactly `yyyy-MM-ddTHH:mm` or `yyyy-MM-ddTHH:mm:ss` in ASCII digits, read as UTC (`DateTimeOffset.ParseExact`, invariant
  culture, `DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal`, no white space allowed), within `StorableTime`; `ToQuery` writes the seconds only when they are not 0. `from` not before `to`
  (both given): `The address has a start time that is not before its end time.`
- Any other key is ignored.

## Approach

1. **Storage: `ILogExplorer`** (new interface, implemented by `SqliteStore`; `ILogSearcher` and `SearchLogsAsync` stay unchanged for
   their existing tests). `FindLogsAsync` validates (`LogFilterRules`), leases a reader, loads the template when `TemplateId > 0`
   (unknown: AC-5 error) and calls `LogFinder.FindAsync` with `LogFinder.SelectiveMatches` (initially 20,000).
2. **Strategy** (`LogFinder`), chosen per call from the FTS index alone:
   - The search text is the user's text, plus, with a template, `TemplateTerms.SearchText(template.Template)` (joined; the combined
     text keeps the 16-term and 1,024-byte rules — the template words are cut first to fit).
   - With a search text, read the matching row IDs from the index only, capped (*Lead decision: benchmark misses*):
     `SELECT rowid FROM log_fts WHERE log_fts MATCH $match LIMIT $selective`, kept as a list of at most `selectiveMatches` IDs.
     **Fewer than `selectiveMatches`**: *index mode* — none: an empty page without a further statement; otherwise the lines with
     those record IDs (`r.id IN (SELECT value FROM json_each($ids))`, the index is not read again) with every filter, the position
     (`captured_at > $at OR (captured_at = $at AND id > $id)`) and `ORDER BY captured_at, id`, at most `selectiveMatches` candidates by
     construction (AC-22). **Otherwise, or without a search text**: *scan mode* — walk the log lines of the range in (capture time, ID) order
     through `records_kind_source_time` (with a source) or `records_kind_time`, from the position, at most `ScanLimit` lines, and keep
     those that pass the filters and, with a search text, are in the FTS match. The membership test is the correlated
     `EXISTS (SELECT 1 FROM log_fts WHERE log_fts MATCH $match AND rowid = r.id)` per examined line, nothing else: its work per line is a
     seek per term, and nothing is collected, so scan mode holds at most the page and its work grows with `ScanLimit`, not with the
     matches. (A `MATCH` restricted to the chunk's ID range and collected into a set was dropped: imported lines are not in ID order,
     so a time-ordered chunk can span nearly every row ID and the set would grow with the matches; Security B1.)
   - With a template, every candidate's message is normalized (`LogTemplateNormalizer.Normalize`) and kept only when it equals the
     template text (ordinal); in index mode the candidates are read in pages until `Limit` lines are kept or the candidates end, in scan
     mode rejected lines count as examined.
   - `Next`: the last kept line when `Limit` is reached; the last examined line when `ScanLimit` is reached first
     (`ScanLimitReached`); else `null`.
3. **Context, sources, template list, source hours** (`LogExplorerQueries`, bound SQL only): context by `records_kind_source_time`
   in both directions with row-value positions, joined to `log_lines` for log and host, examining at most `StorageLimits.ContextScanRows`
   (2,000) rows per side; sources by a loose index scan over `records_kind_source_time` (one seek per source, `LIMIT 64`); template list
   by `log_template_hours` summed per template in key order (`GROUP BY template_id` with `+hour` in the range conditions, so the
   primary key and not `log_template_hours_hour` is read) and then joined to `log_templates`, filtered and ordered as AC-10 (AC-23;
   one statement for every range, no `log_templates.count` shortcut); source hours from `log_source_hours`.
4. **Backend** (`src/Vandox.Backend/Logs/`): `LogsAddress` (parse and build, *Address forms*), `Severities`, `LogText` (escape runs),
   `TemplateHourSeries` (chart data), `LogsView` (constants and the user texts of AC-14/15). Components: `Components/Logs/ExternalText.razor`
   (renders runs: text nodes and `<span class="escape">`, wrapped in an element of class `external`: `unicode-bidi: isolate` and
   `white-space: pre-wrap` in `app.css`), `Components/Pages/LogsPage.razor` (`@page "/logs"`) and
   `Components/Pages/LogTemplatesPage.razor` (`@page "/logs/templates"`), each with a `.razor.cs` code-behind; the pages parse
   `new Uri(NavigationManager.Uri).Query` in `OnParametersSetAsync`, query only when `RendererInfo.IsInteractive`, and run the store
   calls of one action (an address, *Next page*, *Continue*, expanding a hit) **one after another** under one `CancellationTokenSource`
   with `LogsView.Deadline` on the injected `TimeProvider`; a new action or dispose cancels the running one and awaits its end before
   the next call starts (AC-14.13). The pages hold the lines as `LogLineView` (the capped runs of AC-17, not the `StoredRecord`): one
   page of lines (100) and one expanded context (at most 11 lines). Links and navigation targets are the fixed local
   paths `logs` and `logs/templates` plus `ToQuery()`. The form submit handler validates (`LogsAddress.Validate`) and navigates; the
   results always come from the address. The paging position (`After`/`Next`) is page state, not part of the address: a reloaded or
   shared address opens the first page, and *Next page*/*Continue* add no browser history entry.
   `GatedLogExplorer` (`src/Vandox.Backend/Logs/`) wraps the store for the pages: one `SemaphoreSlim` of `LogsView.MaxConcurrentQueries`
   (2) slots across all circuits, waited for with the call's token (AC-21). The chart's title and series label are the fixed texts
   `LogsView.ChartTitle` and `LogsView.ChartSeries`; the template text is shown only through `ExternalText` above the chart.
   `MainLayout` gets `<NavLink href="logs" Match="NavLinkMatch.Prefix">Logs</NavLink>`; both pages show links *Search* and *Templates*.
   `ServeCommand.Build` registers `ILogExplorer` as a factory `_ => new GatedLogExplorer(store, LogsView.MaxConcurrentQueries)` (so
   the container disposes it) and `TimeProvider` (`hooks.Clock ?? TimeProvider.System`), and calls `hooks.Built?.Invoke(app.Services)`
   after `builder.Build()`. `BackendLog`
   gets one `[LoggerMessage]` method for a failed read (`logs view: reading failed`, level Error, with the exception).
5. **Styles** in `app.css` only (no `style` attribute, CSP); the chart reuses `TimeSeriesChart` and `js/charts.js` (no new asset).
6. **Benchmark** (`tools/Vandox.StorageBenchmark/SearchBenchmark.cs`, `--search-lines N`, 1 to 10,000,000, not combinable with
   `--batches`, `--warmup`, `--log-lines`; `[ExcludeFromCodeCoverage]` like `TemplateBenchmark`): writes N generated lines (the
   generator of `TemplateBenchmark`, extended so that the words below exist with the stated shares, printed with their real counts),
   then times, each as the first page of 100 lines with scan limit `LogsView.ScanRows`: (1) a rare word (at most 1,000 lines), (2) a word
   in about 1 % of the lines, (3) a word in at least 25 %, (4) case 3 with a program and a severity filter, (5) two words of at least 25 %
   each whose combination is rare, (6) no text, a program filter only, (7) case 3 in a one-day range in the middle, (8) the lines of the
   most frequent template and of a rare one, (9) case 3 in a range where the word does not occur (the scan bound, one call), (10) the
   context (5 and 5) of a hit, (11) the template list of the whole data and of 30 days (limit 200), (12) 16 words of at least 25 % each
   whose intersection is empty (the probe walks their doclists inside one FTS5 step, which the deadline cannot interrupt; Security
   N1). It prints the mode each text case
   ran in, the time and the verdict against 2,000 ms for N = 5,000,000.
7. **Tuning**: the Dev may set `LogFinder.SelectiveMatches` (5,000 to 50,000) and `LogsView.ScanRows` (10,000 to 100,000) from the
   development-host run so that every case stays under 500 ms there (the reference host was 3 to 4 times slower in the earlier
   measurements); the final values go into the storage and web UI areas and `docs/BENCHMARKS.md`. No test depends on them (the tests
   pass explicit values).

## Options considered

How a search stays under two seconds (record 0103):

1. **Index-first only, as today, bounded by the deadline** — rare words are fast, a common word fails the criterion (cost grows with
   the matches, 0066). Lost: misses AC-19 for the most common searches.
2. **Time-ordered scan only** — every page costs at most the scan bound, but a rare word in a large range needs many *Continue* steps
   where the index would answer at once. Lost.
3. **Choose per call: index mode when the word is selective, a bounded scan otherwise** (chosen) — both modes are bounded (the
   selectivity cap, the scan limit), the choice reads only the index, and AC-3 pins that the results are identical.
4. **Order by record ID (FTS5's native rowid order, which stops at the limit)** — fast for any word, but imported lines are not in ID
   order of time (an older log imported later gets higher IDs), so pages would not be in time order. Lost.
5. **Schema change: capture time in the FTS table or a time-clustered index of matches** — FTS5 cannot range-filter an unindexed
   column, and a second index doubles the write cost the write criterion (0083) is measured on. Lost.

How to list the lines of a template:

6. **Schema step 10: `log_lines.template_id` set by the write path and the rebuild** — exact and indexable, but changes the write path
   (`BatchWriter`, the deduplication code of security area 1) and its criterion, leaves every existing line without a template until a
   rebuild rewrites every row, and a frequent template still needs the time-ordered strategy above. Lost for this feature.
7. **Normalize candidates found by the template's literal words** (chosen) — no schema or write-path change; the definition of a
   template's lines is the normalizer itself, so the result is exact; the words only narrow the candidates and never lose a line
   (AC-5, AC-6).
8. **A text search for the template's words without the exact check** — returns lines of other templates that share the words. Lost.

Template links:

9. **Template number in the address** (chosen) vs. **source, program and template text** — the text survives a rebuild only when the
   rules did not change, and a rule change (the reason to rebuild) changes the text too; the number is short and exact. A rebuilt
   database makes old links say so (AC-14.11).

Context:

10. **On demand, one hit at a time** (chosen) vs. **context for every hit** (two more queries and up to ten more lines per hit, ten times
    the circuit's render volume) vs. **a separate context page in the address** (not asked for). 

Queries and prerender:

11. **Query only when interactive** (chosen) vs. **query while prerendering and carry the result into the circuit** (puts up to 1.6 MB of
    external text into the page as persisted state) vs. **no prerender for the log pages** (the web UI area prerenders every page).

Display of invisible characters:

12. **Visible escapes** (chosen) vs. **bidirectional isolation only** (an U+202E still reverses the rest of its own line) vs.
    **removing them** (loses evidence a forensics view must show).
13. **Escaped output capped per field with a visible cut marker** (chosen; Security B2) vs. **full escapes with the real bound stated**
    (a 16 KiB message of U+0000 becomes 98,304 characters, about 192 KiB per field; roughly 20 MB for 111 lines per circuit while the
    circuits are not bounded before #25) — the cap keeps every plain message whole and only cuts the tail of a line made mostly of
    invisible characters, with the cut shown.

Reader slots:

14. **A gate of two concurrent explorer calls across all circuits, each page one call at a time** (chosen; Security B3) vs. **no limit,
    the residual stated** (four tabs keep the four reader leases busy and `/healthz` answers 503 for up to 10 s each, without end
    before #25) vs. **a separate reader pool for the view** (changes `SqliteStore` and its reader bound for one consumer; the gate gives
    the same protection in the backend).

Scan membership test:

15. **Correlated `EXISTS` per examined line** (chosen) vs. **one `MATCH` over the chunk's ID range collected into a set** (unbounded in
    the matches because a time-ordered chunk spans arbitrary IDs; Security B1).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Storage | `ILogExplorer.cs`, `LogFilter.cs`, `LogPosition.cs`, `LogPage.cs`, `LogContextQuery.cs`, `LogContext.cs`, `TemplateListQuery.cs`, `TemplateCount.cs`, `SourceHourQuery.cs` | new public types |
| Vandox.Storage | `LogFinder.cs`, `LogFilterRules.cs`, `LogExplorerQueries.cs`, `TemplateTerms.cs` | new internal types |
| Vandox.Storage | `SqliteStore.cs` | implements `ILogExplorer` (new `#region ILogExplorer`), internal overload of `FindLogsAsync` |
| Vandox.Storage | `StorageLimits.cs` | new constants (below) |
| Vandox.Storage | `RecordQueries.cs` | new overload `ValidateWindow(from, to, limit, maxLimit)`; the existing one delegates to it |
| Vandox.Backend | `Logs/LogsAddress.cs`, `Logs/Severities.cs`, `Logs/LogText.cs`, `Logs/TextRun.cs`, `Logs/LogLineView.cs`, `Logs/GatedLogExplorer.cs`, `Logs/TemplateHourSeries.cs`, `Logs/LogsView.cs` | new |
| Vandox.Backend | `Components/Logs/ExternalText.razor`, `Components/Pages/LogsPage.razor` (+ `.razor.cs`), `Components/Pages/LogTemplatesPage.razor` (+ `.razor.cs`) | new |
| Vandox.Backend | `Components/Layout/MainLayout.razor`, `Components/_Imports.razor`, `Components/Pages/Home.razor`, `Hosting/ServeCommand.cs`, `Hosting/ServeHooks.cs` (new hook `Built`), `Hosting/BackendLog.cs`, `wwwroot/app.css` | changed |
| tools | `Vandox.StorageBenchmark/SearchBenchmark.cs`, `Program.cs` (`--search-lines`), `TemplateBenchmark.cs` (generator shared) | new / changed |

Files read and found final as they are: `FtsQuery.cs`, `TemplateQueries.cs`, `ReaderQuery.cs`, `LogTemplateNormalizer.cs`,
`ChartData.cs`, `ChartSeries.cs`, `TimeSeriesChart.razor(.cs)`, `SecurityHeadersMiddleware.cs`, `App.razor`, `Routes.razor`.

## Signatures (for the Dev's skeleton)

Every type below follows the repository layout (one type per file, `#region` blocks, XML docs); properties are `{ get; set; }` with the
shown defaults unless stated.

```csharp
namespace Vandox.Storage;

public interface ILogExplorer
{
    Task<LogPage> FindLogsAsync(LogFilter filter, CancellationToken cancellationToken);
    Task<LogContext> LogContextAsync(LogContextQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> LogSourcesAsync(CancellationToken cancellationToken);
    Task<LogTemplate?> TemplateAsync(long id, CancellationToken cancellationToken);
    Task<IReadOnlyList<TemplateCount>> TemplatesAsync(TemplateListQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<DateTimeOffset>> SourceHoursAsync(SourceHourQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<TemplateHour>> TemplateHoursAsync(TemplateHourQuery query, CancellationToken cancellationToken);
}

public sealed class LogFilter
{
    public string Text { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Program { get; set; } = string.Empty;
    public byte? MaxPriority { get; set; }
    public long TemplateId { get; set; }
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public LogPosition? After { get; set; }
    public int Limit { get; set; }
    public int ScanLimit { get; set; }
}

public sealed class LogPosition
{
    public DateTimeOffset CapturedAt { get; set; }
    public long Id { get; set; }
}

public sealed class LogPage
{
    public IReadOnlyList<StoredRecord> Lines { get; set; } = [];
    public LogPosition? Next { get; set; }
    public bool ScanLimitReached { get; set; }
}

public sealed class LogContextQuery
{
    public long RecordId { get; set; }
    public int Before { get; set; }
    public int After { get; set; }
}

public sealed class LogContext
{
    public StoredRecord Line { get; set; } = new();
    public IReadOnlyList<StoredRecord> Before { get; set; } = [];
    public IReadOnlyList<StoredRecord> After { get; set; } = [];
}

public sealed class TemplateListQuery
{
    public string Source { get; set; } = string.Empty;
    public string Program { get; set; } = string.Empty;
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public int Limit { get; set; }
}

public sealed class TemplateCount
{
    public LogTemplate Template { get; set; } = new();
    public long CountInRange { get; set; }
}

public sealed class SourceHourQuery
{
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public int Limit { get; set; }
}

// StorageLimits (new constants)
public const int MaxFindLimit = 1000;
public const int MaxScanRows = 100000;
public const int MaxContextLines = 20;
public const int ContextScanRows = 2000;
public const int MaxLogSources = 64;
public const int MaxTemplateListLimit = 1000;

// SqliteStore : IRecordWriter, IRecordReader, ILogSearcher, ILogTemplateReader, ILogExplorer, IIncidentStore, IImportTracker, IAsyncDisposable
// ILogExplorer.TemplateHoursAsync is the store's existing public TemplateHoursAsync (same signature as ILogTemplateReader's, one
// method implements both; no new store code, it stays in #region ILogTemplateReader; covered by the existing SqliteStoreTemplateTests)
internal Task<LogPage> FindLogsAsync(LogFilter filter, int selectiveMatches, CancellationToken cancellationToken);

internal static class LogFinder
{
    internal const int SelectiveMatches = 20000;
    internal static Task<LogPage> FindAsync(SqliteConnection connection, LogFilter filter, LogTemplate? template, int selectiveMatches, CancellationToken cancellationToken);
}

// RecordQueries (new overload; the existing ValidateWindow(from, to, limit) delegates to it with StorageLimits.MaxQueryLimit)
internal static void ValidateWindow(DateTimeOffset from, DateTimeOffset to, int limit, int maxLimit);

internal static class LogFilterRules
{
    internal static void Validate(LogFilter filter);
    internal static void Validate(LogContextQuery query);
    internal static void Validate(TemplateListQuery query);
    internal static void Validate(SourceHourQuery query);
}

internal static class LogFinderQueries   // changed by the Lead decision on the benchmark misses
{
    internal static (string Sql, Dictionary<string, object> Parameters) BuildProbe(string match, int selectiveMatches);   // now SELECT rowid ... LIMIT $selective
    internal static (string Sql, Dictionary<string, object> Parameters) BuildIndex(LogCriteria criteria, IReadOnlyList<long> ids);   // replaces BuildIndex(LogCriteria, string match)
}
// LogFinder (private): ReadMatchesAsync(SqliteConnection, string match, int selectiveMatches, CancellationToken) : Task<List<long>> replaces
// CountMatchesAsync; FindFromIndexAsync(SqliteConnection, LogCriteria, IReadOnlyList<long> ids, CancellationToken)

internal static class LogExplorerQueries
{
    internal static Task<LogContext> ContextAsync(SqliteConnection connection, LogContextQuery query, CancellationToken cancellationToken);
    internal static (string Sql, Dictionary<string, object> Parameters) BuildSources();
    internal static (string Sql, Dictionary<string, object> Parameters) Build(TemplateListQuery query);
    internal static (string Sql, Dictionary<string, object> Parameters) Build(SourceHourQuery query);
    internal static TemplateCount ReadTemplateCount(SqliteDataReader reader);
}

internal static class TemplateTerms
{
    internal static string SearchText(string template);
}
```

```csharp
namespace Vandox.Backend.Logs;

public sealed class LogsAddress
{
    public string Text { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Program { get; set; } = string.Empty;
    public byte? Severity { get; set; }
    public long TemplateId { get; set; }
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? To { get; set; }
    public string Error { get; set; } = string.Empty;          // empty when the address is valid
    public static LogsAddress Parse(string query);
    public string Validate();                                   // the error text of the current values, "" when valid
    public string ToQuery();                                    // InvalidOperationException when Validate() is not empty
    public LogFilter ToFilter(int limit, int scanLimit, LogPosition? after);
}

public static class Severities
{
    public static bool TryParse(string name, out byte priority);
    public static string Name(byte priority);
}

public sealed class TextRun
{
    public TextRun(string text, bool escaped);
    public string Text { get; }
    public bool Escaped { get; }
}

public static class LogText
{
    public const int MaxRuns = 256;
    public const int MaxChars = 16384;
    public const int MaxShortChars = 2048;
    public static IReadOnlyList<TextRun> Runs(string text, int maxChars);
    public static string Escape(string text, int maxChars);
}

public sealed class LogLineView
{
    public long Id { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    public string Severity { get; set; } = string.Empty;       // Severities.Name or "-"
    public int Pid { get; set; }                                // LogLine.Pid
    public IReadOnlyList<TextRun> Source { get; set; } = [];
    public IReadOnlyList<TextRun> Host { get; set; } = [];
    public IReadOnlyList<TextRun> Program { get; set; } = [];
    public IReadOnlyList<TextRun> Message { get; set; } = [];
    public static LogLineView From(StoredRecord record);
}

public sealed class GatedLogExplorer : ILogExplorer, IDisposable
{
    public GatedLogExplorer(ILogExplorer inner, int maxConcurrent);   // 1 to 3, else ArgumentOutOfRangeException
    // the seven ILogExplorer methods, each: await slot (token), call inner, release in finally
    // instance fields: exactly one SemaphoreSlim and one ILogExplorer (ServeTests.ServeGatesTheLogExplorer reads them by type)
    public void Dispose();
}

public static class TemplateHourSeries
{
    public const int MaxHours = 2160;
    public static ChartData Build(IReadOnlyList<TemplateHour> hours, IReadOnlyList<DateTimeOffset> coveredHours, DateTimeOffset from, DateTimeOffset to);
}

public static class LogsView
{
    public const int PageLines = 100;
    public const int ScanRows = 50000;
    public const int ContextLines = 5;
    public const int TemplateRows = 200;
    public const int MaxConcurrentQueries = 2;
    public const string ChartTitle = "Lines per hour";
    public const string ChartSeries = "Lines";
    public const string CountInRangeHeader = "Lines in range (whole hours)";
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    // the exact user texts of AC-12, AC-14 and AC-15 as public const string fields
}
```

Components: `ExternalText` (`[Parameter, EditorRequired] public IReadOnlyList<TextRun> Runs { get; set; } = [];` — the caller builds
the runs with the cap of AC-17), `LogsPage` and
`LogTemplatesPage` (`public sealed partial class … : ComponentBase, IDisposable`, `[Inject]` `ILogExplorer`, `NavigationManager`,
`TimeProvider`, `ILogger<…>`). `ServeHooks`: `public Action<IServiceProvider>? Built { get; set; }` (called with the application's
services after the build; tests only). `BackendLog`: `internal static partial void LogsReadFailed(this ILogger logger, Exception exception);`
(next free event ID). `SearchBenchmark`: `internal static Task RunAsync(string directory, int lines, int size, CancellationToken cancellationToken)`.

## Test files

- `tests/Vandox.Storage.Tests/SqliteStoreLogExplorerTests.cs` (the store's `ILogExplorer` methods, next to `SqliteStoreQueryTests.cs`
  and `SqliteStoreTemplateTests.cs`), `tests/Vandox.Storage.Tests/TemplateTermsTests.cs`.
- `tests/Vandox.Backend.Tests/LogsAddressTests.cs`, `SeveritiesTests.cs`, `LogTextTests.cs`, `LogLineViewTests.cs`,
  `GatedLogExplorerTests.cs`, `ExternalTextTests.cs`,
  `TemplateHourSeriesTests.cs`, `LogsPageTests.cs`, `LogTemplatesPageTests.cs`; new cases in `MainLayoutTests.cs` and `ServeTests.cs`.
- New test doubles in `tests/Vandox.Backend.Tests`: `FakeLogExplorer` (`ILogExplorer`: scripted results per method, the template's
  hourly counts included (method name `FakeLogExplorer.TemplateHours`), records every query and its token, can block until the token is cancelled, can throw a scripted exception) and `RecordingNavigationManager`
  (initialized with any URI, records `NavigateTo` targets and applies them as the new URI). `ComponentTestRenderer` may get an
  interactive `RendererInfo` (Tester's change, existing tests unaffected).

Existing test code that calls a changed signature: none (`ILogExplorer` is new; `SqliteStore`, `ServeCommand.Build`, `MainLayout`
keep their signatures; `RecordingTemplateReader` is untouched because `ILogTemplateReader` does not change; `ServeHooks.Built` is a new
optional property and the three-argument `RecordQueries.ValidateWindow` keeps its signature and texts, so the existing `ServeTests`
and `RecordQueries` tests compile and pass unchanged).

Files of the change no unit test can cover: `tools/Vandox.StorageBenchmark/SearchBenchmark.cs` and the changed lines of
`tools/Vandox.StorageBenchmark/Program.cs` and `TemplateBenchmark.cs` — a measurement program run by hand on the reference host that
asserts nothing; marked `[ExcludeFromCodeCoverage]` as `Program` and `TemplateBenchmark` already are (record 0103, *Consequences*). No
browser script changes (`js/charts.js` is reused), so `sonar.coverage.exclusions` and `COVERAGE_EXCLUDES` stay as they are.

## Areas

- `docs/areas/web-ui.md` — new section *Logs view*: the two pages, the navigation entry, the address parameters with *Address forms*
  and their errors, oldest first and 100 per page, *Next page*/*First page*/*Continue*, context on demand, the template list and chart
  (zero and gap rule, 2,160 hours; the fixed chart title and series label), the template link, the range count counted in whole
  hours (a mid-hour `from` or `to` counts the whole hour; the column is labelled so), display of external text (escape rules, run
  cap, the per-field character caps 16,384 and 2,048 and the `[cut: <n> more characters]` marker), prerender without query, the
  query taken from the address without its fragment, one store call per page at a time and at most two explorer calls at once
  across the service, the 10-second deadline and the user texts; the paging position is not in the address (a reloaded or shared
  address opens the first page; *Next page* and *Continue* add no history entry, so browser back leaves the search, not the page);
  *Limits and errors*: the logs are readable by whoever reaches the web port until #25; until #25 the host has no Host-header
  allow-list (`CreateSlimBuilder`, no `AllowedHosts`), so through DNS rebinding a website the operator's browser visits can read the
  logs when the reverse proxy forwards unknown Host headers or the web port is reachable from the LAN; the memory per circuit (about
  5 MiB worst case for the search page, 111 lines at the caps, and 3.1 MiB for the template list, plus a transient render batch of
  the same order) and that the number of circuits (and the framework's retained disconnected circuits) is not bounded before #25;
  template numbers change on a rebuild, no total count, a source with an escaped character cannot be chosen; *Layout and
  navigation*: the *Logs* entry.
- `docs/areas/storage.md` — *Reading*: new subsection *Log exploration* (filters, the two modes and how one is chosen, the scan bound
  and `Next`, template lines by normalization with the template words, context, sources, template list, source hours, every limit and
  error text, the final values of `SelectiveMatches` and the page's scan limit; the scan's membership test is the per-line `EXISTS`;
  the deadline cannot interrupt FTS5 inside one step, so the selectivity probe of several common words is bounded by their doclist
  sizes, not by the deadline); *Log search* keeps its rules and says that the logs
  view searches through *Log exploration*.
- `docs/areas/detection-and-reports.md` — *Log templates*: the intro no longer says no view shows them; *Queries* gains *Templates of a
  range* (AC-10) and *Lines of a template* (pointer to the storage area).

## Documentation updates

| File | Change | Owner |
| ---- | ------ | ----- |
| `docs/areas/web-ui.md`, `docs/areas/storage.md`, `docs/areas/detection-and-reports.md` | as *Areas* | Dev |
| `docs/ARCHITECTURE.md` | intro: the templates are shown by the logs view (drop "nothing shows them yet"), the web UI has the logs view; *Components* (`src/Vandox.Backend`): the logs view; *Storage and retention*, *Log search* bullet: the bounded index-or-scan search of the logs view, link 0103 | Dev |
| `README.md` | new section *Explore the logs* (pages, filters, address, context, templates; times in UTC); *Rebuild template statistics*: template links change after a rebuild | Dev |
| `docs/BENCHMARKS.md` | new section *Log search* (criterion, procedure `--search-lines`, the cases, results: development host by the Dev, reference host from the Product Manager's run, entered by the Dev) | Dev |
| `.squad/project.md` | *Security areas* 10: the address parameters (`LogsAddress`) and the explorer's bounds (`LogFinder`, `LogFilterRules`, `TemplateTerms`: selectivity cap, scan limit, context bound, limits); 12: the logs view shows external text through `LogText`/`ExternalText` (escape rules, run cap, per-field character caps with the cut marker; fixed chart texts) and Razor's HTML escaping; 3: until #25 the logs view is readable by whoever reaches the web port, no Host allow-list (DNS rebinding), circuits not bounded; the explorer gate (`GatedLogExplorer`, two slots) that keeps reader slots for `/healthz` | Dev |
| `.squad/project.md` | *Test doubles*, row *Blazor components*: `FakeLogExplorer`, `RecordingNavigationManager` (implemented) | Tester |
| `docs/decisions/0103-…md`, `docs/decisions/0066-…md`, `docs/decisions/README.md` | the record, the sentence in 0066 *Consequences*, the index row (done with this plan) | Lead |

## Architecture check

- The three guarantees of `.squad/project.md` (no silent gaps, a hanging collector never blocks the agent, backfill never alerts) are
  not touched: the change only reads stored data and writes nothing.
- No schema step, no write-path change: `BatchWriter` and the deduplication of security area 1 stay as they are; the write criterion
  (0083) is unaffected.
- Reads stay on the query-only reader connections (at most four). `/healthz` does **not** have its own lease: `PingAsync` leases a
  reader from the same four-slot pool (`src/Vandox.Storage/SqliteStore.cs:103`, `LeaseReaderAsync` at `:199`) with a 2-second timeout
  (`src/Vandox.Backend/Hosting/ServeCommand.cs:32`). The view therefore goes through `GatedLogExplorer` with two slots across all
  circuits and each page runs one store call at a time (AC-14.13, AC-21), so at least two reader slots stay free of the view for the
  ping and the other readers; every query of the view runs under the deadline, which interrupts a running statement between FTS5
  steps (0097), and a wait for a gate slot counts against it.
- The web UI keeps its render mode, its single self-only CSP and its static assets (0081, 0098, 0099): no new file is loaded, no style
  attribute, links only to fixed local paths with escaped values.
- Record 0016/ARCHITECTURE *Network*: the web UI "requires a login" — not yet true (#25, open, same milestone v0.1.0). The logs view makes
  every stored log line readable to whoever reaches the web port until then; the compose file publishes the web port on `127.0.0.1` only
  (0060). This is a sequencing residual inside an unreleased milestone, stated in the web UI area and record 0103; v0.1.0 is not
  released without #25. No guarantee is weakened in a release.

## Security considerations

- [x] Limits and when they apply: `q`, `source`, `program` at most 1,024 UTF-8 bytes **after** URL decoding (the address parser and,
  for `q`, `FtsQuery` again); the URL itself is bounded **before** decoding by Kestrel's request line limit (8 KiB) on a page load and by
  the circuit's 32 KB message limit on an interactive navigation; the template text read from the store is at most 1,024 UTF-8 bytes;
  a message at most 16 KiB; `LogText` caps the runs per field at 256 (after decoding, on UTF-16 text); `Limit` 1 to 1,000, `ScanLimit`
  1 to 100,000, context 0 to 20 per side with 2,000 examined rows per side, sources 64, template list 1 to 1,000, source hours 1 to
  10,000, chart 2,160 points. Every query runs under the 10-second deadline; index mode is bounded by `SelectiveMatches` candidates,
  scan mode by `ScanLimit` examined rows (the per-line `EXISTS` collects nothing); the selectivity probe reads and keeps at most
  `SelectiveMatches` row IDs (about 96 KiB, the list index mode then reads by record ID), but a text of several common words makes FTS5 walk their full doclists to find the intersection inside
  one `xFilter`/`xNext` step, where the progress handler of `ReaderQuery` does not run: this work is bounded by the doclist sizes
  (index-only, at most 16 terms), **not** by the deadline, and measured as cases 5 and 12 of AC-19 (Security N1). At most two explorer
  calls run at once across the service (`GatedLogExplorer`), one per page.
  Memory per circuit: the page keeps `LogLineView`s, not the `StoredRecord`s: per line the message runs at most
  `LogText.MaxChars` (16,384) UTF-16 characters plus the cut marker and source, host and program at most `LogText.MaxShortChars`
  (2,048) each, about 45 KiB; 100 lines plus a context of 11 come to about 5 MiB worst case (a typical line is a few hundred
  bytes). The render tree references the same strings; the render batch serializes them once more, transiently, for SignalR. The
  template page holds at most 200 rows of four capped short fields (about 3.1 MiB worst case) and one chart of 2,160 points. Before
  #25 the number of circuits is not bounded (container limit 512 MB, `deploy/backend/docker-compose.yml:36`): a residual stated in the
  web UI area and 0103.
- [x] Exceptions that reach the user: `InvalidQueryException` → `The search cannot run: <rule>.` (the store's rule text, which never
  contains the input; AC-7 lists them); `OperationCanceledException` of the deadline → the fixed deadline texts; every other exception
  of a store call (`StoreException`, `SqliteException`, `InvalidOperationException`) → the fixed failure texts, the exception only in the
  service log through `[LoggerMessage]`; address errors → the fixed texts of *Address forms*, naming only the parameter, never the value.
- Guard enumeration (security area 10): the forms the query-string decoder accepts are the *Address forms* table; the values then reach
  only `FtsQuery` (existing literal-term guard, quoted terms, bound parameter), bound SQL parameters (`source`, `program`, IDs, times), or
  `long`/`DateTimeOffset.ParseExact` with fixed formats. The template words are passed through `FtsQuery` like user text, so a template
  holding `"`, `*`, `NEAR` or `:` cannot reach the FTS5 operator syntax.
- Display (security area 12): Razor's text encoding for every value, `LogText` escapes for invisible and direction-changing characters,
  `unicode-bidi: isolate` per field, no `MarkupString`; hrefs built only as `logs` or `logs/templates` plus `ToQuery()` of a validated
  address; the chart (`TimeSeriesChart`, whose `Title` goes into `aria-label` and uPlot's title) gets only the fixed texts
  `LogsView.ChartTitle` and `LogsView.ChartSeries`, never external text.
- DNS rebinding (security area 3): the host has no Host-header allow-list (`WebApplication.CreateSlimBuilder`,
  `src/Vandox.Backend/Hosting/ServeCommand.cs:141`, no `AllowedHosts`). Before #25 a website the operator's browser visits can read
  the view through a rebound name when the reverse proxy forwards unknown Host headers or the port is reachable from the LAN.
  Nothing from `main` is published (images only for `v*.*.*` tags, `.github/workflows/release.yml:9-10`); stated in 0103 and the web
  UI area, and #25 gets a comment that its reverse-proxy trust includes a Host allow-list (orchestrator).
- Forged lines (any local process on the monitored server can write log lines) appear in the view like real ones; the view is a reader,
  not a judge of origin (as records 0091–0094 state for the classification).

## Challenge

Devil's Advocate (0 major, 4 minor), each accepted:

1. *Limit message in AC-7 and AC-10* — accepted. `ValidateWindow` would say `10000`. New overload
   `RecordQueries.ValidateWindow(from, to, limit, maxLimit)`, used with `MaxFindLimit` (AC-7) and `MaxTemplateListLimit` (AC-10); the
   existing method delegates with `MaxQueryLimit`, so `SourceHoursAsync` and the existing callers keep `10000`. The test rows pin the
   exact texts (0 and 1,001). `RecordQueries.cs` is no longer in the list of final files.
2. *Paging position not in the address* — accepted. Stated in *Approach* step 4, AC-14.9 (no `NavigateTo` on *Next page*, *First page*,
   *Continue*), the web UI area and the spec; the spec's "back and forward move between searches" stays true and nothing promises
   back moves between pages.
3. *Count in range is hour-granular* — accepted. AC-10 states whole hours and gains `TemplatesCountsWholeHoursOfAMidHourRange`; the
   column is labelled `Lines in range (whole hours)` (AC-15, `TemplatesPageLabelsTheRangeCountAsWholeHours`); the web UI area
   documents it.
4. *Two common words with a rare combination* — accepted. Case 5 stays blocking, case 12 (16 common words, empty intersection) is
   added; a miss is fixed by a cheaper probe (rarest term first, or the probe under its own share of the deadline), never by a wider
   cap (AC-19, *Approach* step 7).

## Security review

Verdict CHANGES_REQUIRED (plan); every finding addressed:

- **B1** (scan option not bounded by `ScanLimit`) — accepted. The ID-range set option is dropped; the scan's membership test is only
  the per-line correlated `EXISTS`, which collects nothing (*Approach* step 2, AC-4, option 15). With the option gone no set exists,
  so no cap test is needed; AC-4's examined-line bound and AC-3's equality cover the one remaining test.
- **B2** (memory claim wrong by about 6x) — accepted with a cap. `LogText.Runs`/`Escape` take a character cap: 16,384 for a message,
  2,048 for every other field, an escape never split, a visible `[cut: <n> more characters]` run (AC-17, option 13). The pages keep
  `LogLineView`s (capped runs) instead of `StoredRecord`s. AC-17 pins a 16 KiB all-U+0000 message (`RunsCutAnAllNulMessageAtTheCharacterLimit`:
  16,380 characters plus the marker) and a 16 KiB plain message kept whole. The real bound with every holder (runs, render tree
  references, the transient render batch) is in *Security considerations*: about 5 MiB per search circuit worst case, circuits
  unbounded before #25 (residual in the web UI area and 0103).
- **B3** (`/healthz` does not have its own lease) — accepted; the claim is corrected (*Architecture check*). Each page runs its
  store calls one at a time (AC-14.13, AC-15) and `GatedLogExplorer` allows at most two explorer calls across the service, the wait
  counting against the deadline (AC-21, option 14); the registration is pinned by `ServeTests.ServeGatesTheLogExplorer`. Two of the
  four reader slots always stay free of the view.
- **N1** (deadline cannot interrupt FTS5 inside a step) — accepted. The sentence is corrected (bounded by doclist size, not by the
  deadline) and AC-19 gains case 12 (16 common words, empty intersection).
- **N2** (address form gaps) — accepted. New rows for `q=a&q` and `q=&q=x` (repeated even when some are empty), `#frag` (the page
  parses `new Uri(NavigationManager.Uri).Query`), a raw lone surrogate (invalid UTF-16), `%71%3d=x` (key `q=`, ignored), keys with
  U+017F, U+212A, U+0131 (not folded, ignored); `NumberStyles.None` and `DateTimeStyles.AssumeUniversal | AdjustToUniversal` are named.
  `ToQuery` runs only on validated values (`Validate()`, `InvalidOperationException` otherwise, `ToQueryRefusesAnInvalidValue`), and
  the form validates before it navigates (AC-14.8). The re-check of the whole list: every accepted form now ends in a value that
  either passes the value rules or is refused by them, or in an ignored key; no form reaches `ToQuery` unvalidated.
- **N3** (chart and external text) — accepted. Chart title and series label are the fixed `LogsView.ChartTitle`/`ChartSeries`;
  `TemplatesPageChartsTheSelectedTemplate` asserts the `aria-label` with a U+202E template.
- **N4** (DNS rebinding) — accepted. Named in *Security considerations*, the web UI area (*Limits and errors*, Dev) and 0103
  *Consequences*; the orchestrator comments on #25 that its reverse-proxy trust includes a Host allow-list (text in the Lead's result).

## Decision records

- `docs/decisions/0103-logs-view-address-as-the-view-bounded-search-pages-template-lines-by-normalization.md` (Proposed) — options 1–15
  above; Area: Web UI.
- `docs/decisions/0066-log-search-takes-literal-terms-only.md` — one sentence of *Consequences* now points to 0103 (unreleased, edited in
  place; its decision is unchanged).

## Out of scope / follow-ups

- Search operators, newest-first order, a total count, a host filter, a text filter on templates, export (spec *Out of scope*).
- Skipping hours without lines of a program in scan mode through the template statistics — only if AC-19 shows the program-only case
  (6) needs it; then a Lead decision.
- The login (#25) before v0.1.0 is released.

## Challenge notes

Non-blocking notes of the Security plan review, round 2 (APPROVED), verbatim for the Dev and Reviewer:

1. **Gate lifetime (most important):** Approach step 4 says `ServeCommand.Build` registers `ILogExplorer` "as a factory `_ => new GatedLogExplorer(store, …)`" but never says singleton. Registered as scoped or transient, every circuit would get its own semaphore, and the limit across the service, which is the whole point of B3, would quietly fail. As described, `ServeTests.ServeGatesTheLogExplorer` only checks the type and the slot count, so it would not catch this.
   - Register it with `AddSingleton<ILogExplorer>(…)`, matching the existing registrations at ServeCommand.cs:148-153.
   - Have that test also assert that two DI scopes resolve the same instance.
2. **Template-list bound:** "about 2.5 MiB" is low. Four short fields × 2,048 characters × 2 bytes × 200 rows is about 3.1 MiB. Correct the figure in the security considerations and in the web UI area text.

## Lead decision: template hourly counts (decide, after tests first)

Gap: the plan's `ILogExplorer` had no method for a template's hourly counts, which only `ILogTemplateReader.TemplateHoursAsync`
offers, while `LogTemplatesPage` injects only `ILogExplorer`.

Decided: **`ILogExplorer` gets `TemplateHoursAsync(TemplateHourQuery, CancellationToken)`, routed through `GatedLogExplorer`** (AC-15,
AC-21, signatures above). Rejected: the page injects `ILogTemplateReader` directly — that call would bypass the gate, so the view could
hold a third reader lease and break the guarantee of record 0103 (option 14: at most two store calls of the view across the service,
the health check keeps a reader) and Security B3; the shared counter in the test double only proves one call per page, not the bound
across circuits. No record change: 0103 already states the bound for every store call of the view, and this keeps it true.

Found on the way and fixed in AC-15: the hourly queries had no window or limit in the plan; the store answers them ascending and stops
at `Limit`, so a range longer than the limit would chart the oldest hours, contrary to AC-16 (*the last 2,160 hours*). The page now
clips `from` to `to` minus 2,160 hours and asks for 2,161 hours (`TemplatesPageQueriesTheLastHoursOfALongRange`).

Owners:
- Dev: `src/Vandox.Storage/ILogExplorer.cs` (new member with XML docs, exceptions as `ILogTemplateReader`), `src/Vandox.Backend/Logs/GatedLogExplorer.cs`
  (seventh method in the skeleton, then gated in task 5), `LogTemplatesPage.razor.cs` (no `ILogTemplateReader`; the window above).
  `SqliteStore` needs no change.
- Tester: `FakeLogExplorer.cs` (implements `TemplateHoursAsync` as `TemplateHours` through `RunAsync`, scripted result property),
  delete `FakeTemplateReader.cs`, `LogPageHarness.cs` (registers no `ILogTemplateReader`, so a page that injects one fails to render),
  `LogTemplatesPageTests.cs` (`TemplatesPageChartsTheSelectedTemplate` scripts the hours on `harness.Explorer`; new
  `TemplatesPageQueriesTheLastHoursOfALongRange`; `TemplatesPageRunsOneStoreCallAtATime` covers the hourly call),
  `GatedLogExplorerTests.cs` (data row and dispatch case `TemplateHours` in both data-row tests). `ServeTests.ServeGatesTheLogExplorer`
  stays as written: reading the gate's `SemaphoreSlim` and inner `ILogExplorer` by type is accepted, and the Dev keeps exactly one field
  of each type.
- Security: one delta confirmation of this section (the B3 gate's method set changed), per the `decide` rule for a plan fix that
  changes no scope.

UI contract the tests pin (Dev, `LogsPage.razor` and `LogTemplatesPage.razor`): form fields named `q`, `source`, `program`, `severity`,
`from`, `to`; buttons with the exact texts `Next page`, `First page`, `Continue`, `Context`; *Searched up to* written as
`yyyy-MM-dd HH:mm:ss.fff` followed by ` UTC`; on the templates page the link text `Show lines` with `href` `logs` plus
`ToQuery()`. Source rule for Dev and Tester: the formatter rewrites `\u` escapes in C# string literals into the characters themselves
(a literal U+2028 breaks the build), so a C# source builds such characters from code points (`char.ConvertFromUtf32`, `(char)0x…`), never
from a `\u` literal.

## Lead decision: benchmark misses before the reference-host run (decide, after task 6)

Situation: on the development host every AC-19 case is under 2,000 ms, but cases 12 (1,232 ms), 11 (760 ms) and 11b (732 ms; cold 0.9
to 1.2 s) are over the 500 ms tuning target of *Approach* step 7 and are not changed by the two constants; at the reference host's 3 to
4 times they would take about 3.7 to 4.9 s and 2.2 to 3.0 s (cold up to about 4.8 s). Case 9 (411 ms, about 1.2 to 1.6 s there) and case 5
(173 ms) are within reach and stay as they are.

Decided: **fix cases 11, 11b and 12 now, then ask the Product Manager for the one reference-host run.** These misses are predictable from
the plan's own factor, and the run (writing 5,000,000 lines on the NAS) is the expensive step. Waiting would most likely mean two
reference-host runs and a second decide loop anyway.

1. **Index mode reads the full-text index once (AC-22, case 12 and every index-mode case).** The probe keeps the matching record IDs it
   reads (at most `SelectiveMatches`, 12,000 IDs, about 96 KiB, plus their JSON text while the statement is bound) instead of only
   counting them. Fewer than the cap: no IDs means an empty page without a further statement; otherwise the index-mode statement reads
   the lines of those IDs (`json_each`, as `BuildFetch` already does) with every filter, the position and the order, and does not
   touch `log_fts`. The planner must drive by the IDs: if the query plan uses a `records` index on time instead, the Dev writes the range
   and source conditions with unary `+`, which AC-22's test pins either way. The doubled intersection work the Dev reported goes away, and so
   does the second walk of case 12 (about half of its 1,232 ms). The same holds for a rare but non-empty intersection, which a zero-count
   shortcut would not cover.
2. **The template list sums `log_template_hours` in key order (AC-23, cases 11 and 11b).** Sum per `template_id` over the primary key with
   `+hour` in the range conditions, then join the at most 12,914-row aggregate (here) to `log_templates`, filter by source and program,
   order and limit as AC-10. One statement for every range; the `log_templates.count` shortcut of *Approach* step 3 is dropped. Its
   cost is one ordered pass over the table whatever the range is (0.03 s on the development host's file, by the Dev's measurement),
   which is never more than the old statement's cost for the whole range. A narrow range of a large table gets slower than with
   the hour index, at most to that one pass. The storage area states it.

Options weighed:
- *Ask for the run first, fix only on a measured miss* — lost: cases 11, 11b and 12 are expected misses, and the run is the costly
  step.
- *Zero-count shortcut only* (empty page when the probe counts 0) — lost: it halves only the empty intersection of case 12. A
  16-word intersection of one line still walks the doclists twice, and no benchmark case would show it.
- *Rarest term's capped count first* (the plan's AC-19 hint) — not taken now: it decides cheaply only when one term is rare. When every
  term is common (cases 5 and 12) it either still needs the full probe or falls back to scan mode, which turns case 5 (two common
  words meeting in 5,000 lines) into about 12 hits per 12,000 examined lines and hundreds of *Continue* steps.
- *The probe under its own share of the deadline* — not possible: the work runs inside one FTS5 step, where the progress handler does not
  run (Security N1).
- *Template list from `log_templates.count` for the whole storable range* (allowed by the plan) — does not fix 11b (30 days).
- A wider cap or a smaller `ScanRows` — excluded by AC-19 (never a wider cap) and not the cause.

If case 12 still misses on the reference host after this change, the case returns to the Lead (mode `decide`). The remaining levers
are named now so that they do not come up as a surprise then: a smaller full-text index (`detail=column` or `none`, a schema step that
rebuilds `log_fts`, a separate issue) or a product decision on the number of words the view accepts. Neither one is part of this change.

Verification (Dev, before the review, logged): *Format*, *Build*, *Test*, *Analyzer gate*, *Coverage gate*; then the benchmark run on the
development host again with 5,000,000 lines and the updated rows and notes in `docs/BENCHMARKS.md` (target: cases 11, 11b and 12 under
500 ms; report any case that is not). In the same run, compare the template list of cases 11 and 11b row by row between the old and
the new statement on the benchmark file (identical IDs, counts and order) and log the result. Only then does the orchestrator ask
the Product Manager for task 13.

Owners:
- Dev: skeleton of the changed signatures first (`LogFinderQueries.BuildIndex(LogCriteria, IReadOnlyList<long>)` added as a stub
  next to the old overload, which stays until the implementation), then `src/Vandox.Storage/LogFinder.cs`, `LogFinderQueries.cs` (old
  overload removed), `LogExplorerQueries.cs`; `tools/Vandox.StorageBenchmark/SearchBenchmark.cs` if its mode probe or timing depends on
  the changed SQL; `docs/areas/storage.md` (*Log exploration*: the probe reads the IDs and index mode reads them by record ID, index read
  once; the template list's cost is one pass over the template hours whatever the range); `docs/BENCHMARKS.md` (results, *Tuning*,
  *Notes* of cases 11, 11b and 12).
- Tester: `tests/Vandox.Storage.Tests/SqliteStoreLogExplorerTests.cs`, `FindLogsIndexModeReadsLinesByRecordId` and
  `TemplatesSumsTemplateHoursInKeyOrder` (AC-22, AC-23), written first and failing on the skeleton (the template-list test on the
  current statement). The query-plan helper `PlanAsync` of `SqliteStoreTemplateTests.cs` is moved to a shared test helper or repeated;
  the Tester decides. The existing AC-2 to AC-6 and AC-10 tests stay unchanged and must stay green.
- Lead: record 0103 (options 3, 16 and 17, *Decision*), this section, `tasks.md`.
- Security: one delta confirmation of this section (index mode now holds a list of IDs bounded by the cap; the B1 finding was about an
  unbounded set).

Deviations of the Dev reports (`LocationChanged` subscription with query dedupe on both pages, public `LogsAddress.FromFields`/`FormatTime`,
the GET form fallback, `InternalsVisibleTo Vandox.StorageBenchmark`) are left to step 9.
