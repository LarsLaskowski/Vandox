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
  line exactly once. Tests: `FindLogsStopsAtTheScanLimitAndContinues`, `FindLogsWithoutTextStopsAtTheScanLimit`.
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
  `store: invalid query: `): the window rules of `RecordQueries.ValidateWindow` with `Limit` 1 to 1,000 (`limit must be between 1 and
  1000`); `scan limit must be between 1 and 100000`; `source is longer than 1024 bytes`; `program is longer than 1024 bytes`;
  `priority must be between 0 and 7`; `template ID must not be negative`; `after must be within the storable range`; `after id must be
  positive`; the `FtsQuery` rules for a non-empty text. Null strings are `ArgumentNullException`. Test: `FindLogsRefusesInvalidFilters`
  (one data row per rule, exact message).
- [ ] AC-8 Context: `LogContextAsync` returns the line and up to `Before` lines before and `After` lines after it with the same
  source, log name and host, in (capture time, ID) order, whatever their other fields; lines of other logs of the same source are
  skipped; each side examines at most 2,000 lines of the source and returns fewer when that bound or the data ends. Errors: `record ID
  must be positive`, `context lines must be between 0 and 20`, `unknown log line` (an ID that is not a stored log line). Tests:
  `LogContextReturnsNeighboursOfTheSameLog`, `LogContextStopsAtTheExamineBound`, `LogContextRefusesInvalidQueries`.
- [ ] AC-9 Sources: `LogSourcesAsync` returns the distinct sources of stored log lines in ordinal order, at most 64. Tests:
  `LogSourcesReturnsDistinctSourcesInOrder`, `LogSourcesReturnsAtMostSixtyFour`.
- [ ] AC-10 Template list: `TemplatesAsync` returns the templates with at least one line in the hours that start in the range (from the
  hour that holds `From`, before `To`), optionally of one source and one program, each with its number of lines in those hours, ordered
  by that number (highest first), then by template ID, at most `Limit` (1 to 1,000; window rules as above, source and program length
  rules as AC-7). `TemplateAsync(id)` returns the template or `null` (`template ID must be positive`). `SourceHoursAsync` returns the
  start of every hour in the range in which the source has lines, ascending (source required: `source is required`; limit 1 to
  10,000). Tests: `TemplatesCountsLinesInTheRange`, `TemplatesFiltersBySourceAndProgram`, `TemplatesRefusesInvalidQueries`,
  `TemplateReturnsTheTemplateOrNull`, `SourceHoursReturnsCoveredHours`.
- [ ] AC-11 Interruption: a cancelled token ends `FindLogsAsync` (both strategies), `LogContextAsync` and `TemplatesAsync` with
  `OperationCanceledException` and no partial result (record 0097). Test: `FindLogsIsInterruptedByACancelledToken` (data rows per
  method and strategy).

Backend (`tests/Vandox.Backend.Tests`):

- [ ] AC-12 Address (`LogsAddressTests.cs`): `LogsAddress.Parse(query)` reads `q`, `source`, `program`, `severity`, `template`, `from`
  and `to` from a query string (with or without the leading `?`) and `ToQuery()` writes them back in that order, each value through
  `Uri.EscapeDataString`, empty ones left out (`""` when none); `Parse(x.ToQuery())` equals `x` for every field. The forms and rules
  of *Address forms* below hold, each with its exact error. Tests: `ParseReadsEveryParameter`, `ParseTreatsEmptyValuesAsAbsent`,
  `ParseIgnoresUnknownParameters`, `ParseRefusesRepeatedParameters`, `ParseRefusesInvalidValues` (one data row per row of the
  table), `ParseRefusesStartNotBeforeEnd`, `ToQueryRoundTrips`, `ToQueryEscapesEveryValue` (values with `&`, `=`, `#`, `?`, `/`,
  `%`, `+`, space and non-ASCII letters: the result starts with `?q=`, holds no unescaped one of them, and `Parse` gives the values
  back).
- [ ] AC-13 Severities (`SeveritiesTests.cs`): `Severities.TryParse` accepts exactly `emerg`, `alert`, `crit`, `err`, `warning`,
  `notice`, `info`, `debug` (0 to 7) and refuses every other form (`ERR`, `Err`, `3`, ` err`, `err `, `error`, `warn`, empty);
  `Severities.Name` gives the name of 0 to 7. Tests: `TryParseAcceptsTheEightNames`, `TryParseRefusesOtherForms`, `NameNamesEveryPriority`.
- [ ] AC-14 Search page (`LogsPageTests.cs`, with `FakeLogExplorer`, `RecordingNavigationManager`, `FakeTimeProvider` and
  `ComponentTestRenderer`):
  1. the page passes the address filters to the store: text, source, program, `MaxPriority` from the severity, template, `From`/`To`
     (the storable range when absent: `StorableTime.Min` and `StorableTime.Max`), `Limit` 100, `ScanLimit` `LogsView.ScanRows`, no `After`
     — `LogsPagePassesTheAddressFiltersToTheStore`;
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
     address says — `LogsPageSearchNavigatesToTheAddress`;
  9. a full page offers *Next page* (the next call has `After = Next`) and *First page*; a page with `ScanLimitReached` shows `Searched
     up to <time of Next>.` and *Continue* — `LogsPageOffersNextPageAndContinue`;
  10. expanding a hit calls `LogContextAsync` with its ID and 5 and 5 and shows the lines before and after it; expanding another hit
      replaces it — `LogsPageShowsTheContextOfOneHit`;
  11. with `template` the page shows the template (text, source, program, first and last seen, count) above the lines; an unknown
      template shows `This template does not exist. Template numbers change when the statistics are rebuilt.` and searches nothing —
      `LogsPageWithTemplateShowsTheTemplate`, `LogsPageWithUnknownTemplateSaysSo`;
  12. a new address while a search runs cancels the running one's token; disposing the page cancels it too —
      `LogsPageCancelsTheRunningSearch`.
- [ ] AC-15 Templates page (`LogTemplatesPageTests.cs`): lists `TemplatesAsync` of the address (source, program, from, to; limit 200)
  with text, source, program, first seen, last seen, count and count in range, escaped like AC-14.2; `template` selects one, whose
  hourly counts are shown as a `TimeSeriesChart` and whose link *Show lines* is `logs?template=<id>` plus the address's `from` and
  `to`; errors as AC-14.3/5/6/7 with `The query took longer than 10 seconds and was stopped. Narrow the time range or choose a
  source.` and `The templates could not be read. The service log has the details.` Tests:
  `TemplatesPageListsTemplatesWithFirstSeenAndCounts`, `TemplatesPageLinksATemplateToItsLines`,
  `TemplatesPageChartsTheSelectedTemplate`, `TemplatesPageShowsErrors`, `TemplatesPageDoesNotQueryWhilePrerendering`.
- [ ] AC-16 Hourly series (`TemplateHourSeriesTests.cs`): `TemplateHourSeries.Build(hours, covered, from, to)` gives one point per
  hour from the first to the last hour of the range that holds a template hour or a covered hour, at most the last 2,160 hours: the
  template's count, 0 for a covered hour without the template, `null` (a gap) for an hour the source did not log. Tests:
  `BuildCountsTemplateHours`, `BuildFillsCoveredHoursWithZero`, `BuildLeavesUncoveredHoursAsGaps`, `BuildKeepsTheLastHoursOfALongRange`.
- [ ] AC-17 External text (`LogTextTests.cs`): `LogText.Runs(text)` splits a text into plain runs and escaped runs: every character of
  category Cc except line feed and tab, Cf, Zl, Zp, Co, Cn and U+FFFD becomes `\uXXXX` (lower-case hex) or `\UXXXXXXXX` above U+FFFF;
  consecutive escapes form one run; at most 256 runs, the last holding the rest as `LogText.Escape` text; `LogText.Escape(text)` is the
  same as one plain string. Line feed and tab stay. Cases: U+202E, U+200B, U+0000, U+000D, U+2028, U+E000, U+0378, U+FFFD, U+E0001
  (`\U000e0001`), `<script>` (unchanged: Razor escapes markup). Tests: `RunsEscapeInvisibleCharacters`, `RunsKeepLineFeedAndTab`,
  `RunsCoalesceConsecutiveEscapes`, `RunsAreAtMostTwoHundredFiftySix`, `EscapeWritesSupplementaryCharactersWithEightDigits`. The
  cases are written in the test source as `\u…` escapes and each test asserts its input holds the character. The component that renders
  the runs: `ExternalTextTests.ExternalTextRendersMarkupAsText` (`<b>` appears as `&lt;b&gt;`, an escaped run inside
  `<span class="escape">`).
- [ ] AC-18 Navigation and serving: the layout has a *Logs* entry with `href="logs"` that is marked on `/logs` and `/logs/templates`
  and not on `/` (`MainLayoutTests.MainLayoutMarksLogsOnTheLogPages`); `GET /logs` and `GET /logs/templates` answer 200 with the
  security headers on the web port and 404 on the ingest port with the real store wired (`ServeTests.ServeServesTheLogPagesOnlyOnWebPort`).
  `WebUiContentTests` keeps passing on the new `.razor` files and `app.css` (existing tests, no new one).
- [ ] AC-19 Performance (measurement, no unit test): `tools/Vandox.StorageBenchmark --search-lines 5000000` writes 5,000,000 generated
  lines and times the first page (100 lines, scan limit `LogsView.ScanRows`) of the cases listed under *Approach*, step 6. Criterion:
  every case under 2,000 ms on the reference host (DS918+, SSD volume). The Dev runs it on the development host and records it in
  `docs/BENCHMARKS.md`; the orchestrator asks the Product Manager to run the published tool on the reference host before step 8, and
  the Dev enters that row. A case over 2,000 ms on the reference host goes to the Lead (mode `decide`).
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
| `q`, `q=` | empty value | absent |
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
- `template`: 1 to 18 ASCII digits, value at least 1; no sign, space or other digit script.
- `from`, `to`: exactly `yyyy-MM-ddTHH:mm` or `yyyy-MM-ddTHH:mm:ss` in ASCII digits, read as UTC (`DateTimeOffset.ParseExact`, invariant
  culture, no white space allowed), within `StorableTime`; `ToQuery` writes the seconds only when they are not 0. `from` not before `to`
  (both given): `The address has a start time that is not before its end time.`
- Any other key is ignored.

## Approach

1. **Storage: `ILogExplorer`** (new interface, implemented by `SqliteStore`; `ILogSearcher` and `SearchLogsAsync` stay unchanged for
   their existing tests). `FindLogsAsync` validates (`LogFilterRules`), leases a reader, loads the template when `TemplateId > 0`
   (unknown: AC-5 error) and calls `LogFinder.FindAsync` with `LogFinder.SelectiveMatches` (initially 20,000).
2. **Strategy** (`LogFinder`), chosen per call from the FTS index alone:
   - The search text is the user's text, plus, with a template, `TemplateTerms.SearchText(template.Template)` (joined; the combined
     text keeps the 16-term and 1,024-byte rules — the template words are cut first to fit).
   - With a search text, count the matching rows from the index only, capped:
     `SELECT count(*) FROM (SELECT rowid FROM log_fts WHERE log_fts MATCH $match LIMIT $selective)`.
     **Fewer than `selectiveMatches`**: *index mode* — the existing join from `log_fts` with every filter, the position
     (`captured_at > $at OR (captured_at = $at AND id > $id)`) and `ORDER BY captured_at, id`, at most `selectiveMatches` candidates by
     construction. **Otherwise, or without a search text**: *scan mode* — walk the log lines of the range in (capture time, ID) order
     through `records_kind_source_time` (with a source) or `records_kind_time`, from the position, at most `ScanLimit` lines, and keep
     those that pass the filters and, with a search text, are in the FTS match (the Dev picks the membership test by measurement: a
     correlated `EXISTS (SELECT 1 FROM log_fts WHERE log_fts MATCH $match AND rowid = r.id)` per line, or one `MATCH` restricted to the
     chunk's ID range collected into a set; the results must be identical, AC-3).
   - With a template, every candidate's message is normalized (`LogTemplateNormalizer.Normalize`) and kept only when it equals the
     template text (ordinal); in index mode the candidates are read in pages until `Limit` lines are kept or the candidates end, in scan
     mode rejected lines count as examined.
   - `Next`: the last kept line when `Limit` is reached; the last examined line when `ScanLimit` is reached first
     (`ScanLimitReached`); else `null`.
3. **Context, sources, template list, source hours** (`LogExplorerQueries`, bound SQL only): context by `records_kind_source_time`
   in both directions with row-value positions, joined to `log_lines` for log and host, examining at most `StorageLimits.ContextScanRows`
   (2,000) rows per side; sources by a loose index scan over `records_kind_source_time` (one seek per source, `LIMIT 64`); template list
   by `log_template_hours` of the range joined to `log_templates`, grouped and ordered as AC-10 (when the range covers the whole
   storable range the Dev may read `log_templates.count` instead, same result); source hours from `log_source_hours`.
4. **Backend** (`src/Vandox.Backend/Logs/`): `LogsAddress` (parse and build, *Address forms*), `Severities`, `LogText` (escape runs),
   `TemplateHourSeries` (chart data), `LogsView` (constants and the user texts of AC-14/15). Components: `Components/Logs/ExternalText.razor`
   (renders runs: text nodes and `<span class="escape">`, wrapped in an element of class `external`: `unicode-bidi: isolate` and
   `white-space: pre-wrap` in `app.css`), `Components/Pages/LogsPage.razor` (`@page "/logs"`) and
   `Components/Pages/LogTemplatesPage.razor` (`@page "/logs/templates"`), each with a `.razor.cs` code-behind; the pages read
   `NavigationManager.Uri` in `OnParametersSetAsync`, query only when `RendererInfo.IsInteractive`, run each query under a
   `CancellationTokenSource` with `LogsView.Deadline` on the injected `TimeProvider`, cancel the previous one on a new address and on
   dispose, hold one page of lines (100) and one expanded context (at most 11 lines). Links and navigation targets are the fixed local
   paths `logs` and `logs/templates` plus `ToQuery()`. The form submit handler navigates; the results always come from the address.
   `MainLayout` gets `<NavLink href="logs" Match="NavLinkMatch.Prefix">Logs</NavLink>`; both pages show links *Search* and *Templates*.
   `ServeCommand.Build` registers `ILogExplorer` (the store) and `TimeProvider` (`hooks.Clock ?? TimeProvider.System`). `BackendLog`
   gets one `[LoggerMessage]` method for a failed read (`logs view: reading failed`, level Error, with the exception).
5. **Styles** in `app.css` only (no `style` attribute, CSP); the chart reuses `TimeSeriesChart` and `js/charts.js` (no new asset).
6. **Benchmark** (`tools/Vandox.StorageBenchmark/SearchBenchmark.cs`, `--search-lines N`, 1 to 10,000,000, not combinable with
   `--batches`, `--warmup`, `--log-lines`; `[ExcludeFromCodeCoverage]` like `TemplateBenchmark`): writes N generated lines (the
   generator of `TemplateBenchmark`, extended so that the words below exist with the stated shares, printed with their real counts),
   then times, each as the first page of 100 lines with scan limit `LogsView.ScanRows`: (1) a rare word (at most 1,000 lines), (2) a word
   in about 1 % of the lines, (3) a word in at least 25 %, (4) case 3 with a program and a severity filter, (5) two words of at least 25 %
   each whose combination is rare, (6) no text, a program filter only, (7) case 3 in a one-day range in the middle, (8) the lines of the
   most frequent template and of a rare one, (9) case 3 in a range where the word does not occur (the scan bound, one call), (10) the
   context (5 and 5) of a hit, (11) the template list of the whole data and of 30 days (limit 200). It prints the mode each text case
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

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Storage | `ILogExplorer.cs`, `LogFilter.cs`, `LogPosition.cs`, `LogPage.cs`, `LogContextQuery.cs`, `LogContext.cs`, `TemplateListQuery.cs`, `TemplateCount.cs`, `SourceHourQuery.cs` | new public types |
| Vandox.Storage | `LogFinder.cs`, `LogFilterRules.cs`, `LogExplorerQueries.cs`, `TemplateTerms.cs` | new internal types |
| Vandox.Storage | `SqliteStore.cs` | implements `ILogExplorer` (new `#region ILogExplorer`), internal overload of `FindLogsAsync` |
| Vandox.Storage | `StorageLimits.cs` | new constants (below) |
| Vandox.Backend | `Logs/LogsAddress.cs`, `Logs/Severities.cs`, `Logs/LogText.cs`, `Logs/TextRun.cs`, `Logs/TemplateHourSeries.cs`, `Logs/LogsView.cs` | new |
| Vandox.Backend | `Components/Logs/ExternalText.razor`, `Components/Pages/LogsPage.razor` (+ `.razor.cs`), `Components/Pages/LogTemplatesPage.razor` (+ `.razor.cs`) | new |
| Vandox.Backend | `Components/Layout/MainLayout.razor`, `Components/_Imports.razor`, `Components/Pages/Home.razor`, `Hosting/ServeCommand.cs`, `Hosting/BackendLog.cs`, `wwwroot/app.css` | changed |
| tools | `Vandox.StorageBenchmark/SearchBenchmark.cs`, `Program.cs` (`--search-lines`), `TemplateBenchmark.cs` (generator shared) | new / changed |

Files read and found final as they are: `FtsQuery.cs`, `RecordQueries.cs`, `TemplateQueries.cs`, `ReaderQuery.cs`, `LogTemplateNormalizer.cs`,
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
internal Task<LogPage> FindLogsAsync(LogFilter filter, int selectiveMatches, CancellationToken cancellationToken);

internal static class LogFinder
{
    internal const int SelectiveMatches = 20000;
    internal static Task<LogPage> FindAsync(SqliteConnection connection, LogFilter filter, LogTemplate? template, int selectiveMatches, CancellationToken cancellationToken);
}

internal static class LogFilterRules
{
    internal static void Validate(LogFilter filter);
    internal static void Validate(LogContextQuery query);
    internal static void Validate(TemplateListQuery query);
    internal static void Validate(SourceHourQuery query);
}

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
    public string ToQuery();
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
    public static IReadOnlyList<TextRun> Runs(string text);
    public static string Escape(string text);
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
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    // the exact user texts of AC-12, AC-14 and AC-15 as public const string fields
}
```

Components: `ExternalText` (`[Parameter, EditorRequired] public string Value { get; set; } = string.Empty;`), `LogsPage` and
`LogTemplatesPage` (`public sealed partial class … : ComponentBase, IDisposable`, `[Inject]` `ILogExplorer`, `NavigationManager`,
`TimeProvider`, `ILogger<…>`). `BackendLog`: `internal static partial void LogsReadFailed(this ILogger logger, Exception exception);`
(next free event ID). `SearchBenchmark`: `internal static Task RunAsync(string directory, int lines, int size, CancellationToken cancellationToken)`.

## Test files

- `tests/Vandox.Storage.Tests/SqliteStoreLogExplorerTests.cs` (the store's `ILogExplorer` methods, next to `SqliteStoreQueryTests.cs`
  and `SqliteStoreTemplateTests.cs`), `tests/Vandox.Storage.Tests/TemplateTermsTests.cs`.
- `tests/Vandox.Backend.Tests/LogsAddressTests.cs`, `SeveritiesTests.cs`, `LogTextTests.cs`, `ExternalTextTests.cs`,
  `TemplateHourSeriesTests.cs`, `LogsPageTests.cs`, `LogTemplatesPageTests.cs`; new cases in `MainLayoutTests.cs` and `ServeTests.cs`.
- New test doubles in `tests/Vandox.Backend.Tests`: `FakeLogExplorer` (`ILogExplorer`: scripted results per method, records every
  query and its token, can block until the token is cancelled, can throw a scripted exception) and `RecordingNavigationManager`
  (initialized with any URI, records `NavigateTo` targets and applies them as the new URI). `ComponentTestRenderer` may get an
  interactive `RendererInfo` (Tester's change, existing tests unaffected).

Existing test code that calls a changed signature: none (`ILogExplorer` is new; `SqliteStore`, `ServeCommand.Build`, `MainLayout`
keep their signatures; `RecordingTemplateReader` is untouched because `ILogTemplateReader` does not change).

Files of the change no unit test can cover: `tools/Vandox.StorageBenchmark/SearchBenchmark.cs` and the changed lines of
`tools/Vandox.StorageBenchmark/Program.cs` and `TemplateBenchmark.cs` — a measurement program run by hand on the reference host that
asserts nothing; marked `[ExcludeFromCodeCoverage]` as `Program` and `TemplateBenchmark` already are (record 0103, *Consequences*). No
browser script changes (`js/charts.js` is reused), so `sonar.coverage.exclusions` and `COVERAGE_EXCLUDES` stay as they are.

## Areas

- `docs/areas/web-ui.md` — new section *Logs view*: the two pages, the navigation entry, the address parameters with *Address forms*
  and their errors, oldest first and 100 per page, *Next page*/*First page*/*Continue*, context on demand, the template list and chart
  (zero and gap rule, 2,160 hours), the template link, display of external text (escape rules, run cap), prerender without query,
  the 10-second deadline and the user texts; *Limits and errors*: the logs are readable by whoever reaches the web port until #25,
  template numbers change on a rebuild, no total count, a source with an escaped character cannot be chosen; *Layout and navigation*:
  the *Logs* entry.
- `docs/areas/storage.md` — *Reading*: new subsection *Log exploration* (filters, the two modes and how one is chosen, the scan bound
  and `Next`, template lines by normalization with the template words, context, sources, template list, source hours, every limit and
  error text, the final values of `SelectiveMatches` and the page's scan limit); *Log search* keeps its rules and says that the logs
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
| `.squad/project.md` | *Security areas* 10: the address parameters (`LogsAddress`) and the explorer's bounds (`LogFinder`, `LogFilterRules`, `TemplateTerms`: selectivity cap, scan limit, context bound, limits); 12: the logs view shows external text through `LogText`/`ExternalText` (escape rules, run cap) and Razor's HTML escaping; 3: until #25 the logs view is readable by whoever reaches the web port | Dev |
| `.squad/project.md` | *Test doubles*, row *Blazor components*: `FakeLogExplorer`, `RecordingNavigationManager` (implemented) | Tester |
| `docs/decisions/0103-…md`, `docs/decisions/0066-…md`, `docs/decisions/README.md` | the record, the sentence in 0066 *Consequences*, the index row (done with this plan) | Lead |

## Architecture check

- The three guarantees of `.squad/project.md` (no silent gaps, a hanging collector never blocks the agent, backfill never alerts) are
  not touched: the change only reads stored data and writes nothing.
- No schema step, no write-path change: `BatchWriter` and the deduplication of security area 1 stay as they are; the write criterion
  (0083) is unaffected.
- Reads stay on the query-only reader connections (at most four); every query of the view runs under a deadline, which interrupts a
  running statement (0097), so a heavy search cannot block `/healthz` (it uses its own lease) beyond the pool's four slots for at most
  10 seconds each.
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
  scan mode by `ScanLimit` examined rows; the selectivity probe reads at most `SelectiveMatches` row IDs, but a text of several common
  words makes FTS5 merge their full doclists to find the intersection (index-only work, bounded by the deadline; measured as case 5).
  Memory per open page: one page of at most 100 lines (16 KiB each) and one context of at most 11 lines.
- [x] Exceptions that reach the user: `InvalidQueryException` → `The search cannot run: <rule>.` (the store's rule text, which never
  contains the input; AC-7 lists them); `OperationCanceledException` of the deadline → the fixed deadline texts; every other exception
  of a store call (`StoreException`, `SqliteException`, `InvalidOperationException`) → the fixed failure texts, the exception only in the
  service log through `[LoggerMessage]`; address errors → the fixed texts of *Address forms*, naming only the parameter, never the value.
- Guard enumeration (security area 10): the forms the query-string decoder accepts are the *Address forms* table; the values then reach
  only `FtsQuery` (existing literal-term guard, quoted terms, bound parameter), bound SQL parameters (`source`, `program`, IDs, times), or
  `long`/`DateTimeOffset.ParseExact` with fixed formats. The template words are passed through `FtsQuery` like user text, so a template
  holding `"`, `*`, `NEAR` or `:` cannot reach the FTS5 operator syntax.
- Display (security area 12): Razor's text encoding for every value, `LogText` escapes for invisible and direction-changing characters,
  `unicode-bidi: isolate` per field, no `MarkupString`; hrefs built only as `logs` or `logs/templates` plus `ToQuery()`.
- Forged lines (any local process on the monitored server can write log lines) appear in the view like real ones; the view is a reader,
  not a judge of origin (as records 0091–0094 state for the classification).

## Decision records

- `docs/decisions/0103-logs-view-address-as-the-view-bounded-search-pages-template-lines-by-normalization.md` (Proposed) — options 1–12
  above; Area: Web UI.
- `docs/decisions/0066-log-search-takes-literal-terms-only.md` — one sentence of *Consequences* now points to 0103 (unreleased, edited in
  place; its decision is unchanged).

## Out of scope / follow-ups

- Search operators, newest-first order, a total count, a host filter, a text filter on templates, export (spec *Out of scope*).
- Skipping hours without lines of a program in scan mode through the template statistics — only if AC-19 shows the program-only case
  (6) needs it; then a Lead decision.
- The login (#25) before v0.1.0 is released.
