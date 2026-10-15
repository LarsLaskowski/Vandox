# Plan: Forensics report page

Source: Issue #231
Status: Draft
Tier: security — the change adds a page and a download endpoint behind the web login (security area 3), shows external text of the stored data in the web UI (area 12) and reads a user-chosen value from the address (area 10).

## Problem / root cause

#28 delivered the forensics report only as Markdown on standard output (`vandoxd report`); the page was split off until the web UI had
its login. The issue asks for a page behind the login that shows the same content as the command, in English or German by the user's
choice, with a download of the same Markdown, external text escaped, a deadline on the run, and the incidents read through
`ForensicsReporter` one at a time.

Claims of the issue, checked against the code at 91f6bde:

- "#28 delivers the forensics report as Markdown (`vandoxd report`, decision records 0098–0100, area *Detection, alerts and reports*,
  section *Forensics report*)" — **partly refuted**: the report's records are 0100, 0101 and 0102; 0098 (own static assets) and 0099
  (security headers) are web UI records, which the page must follow too. Area and section confirmed
  (`docs/areas/detection-and-reports.md`, *Forensics report*).
- "The web UI has no login yet (#25)" — **outdated**: the login exists (`src/Vandox.Backend/Security`, records 0103–0105,
  `docs/areas/web-login.md`), the layout of #24 too (`Components/Layout/MainLayout.razor`); every page below `Components/Pages` is
  `[Authorize]` (`Components/Pages/_Imports.razor`). The dependencies are met.
- "`ForensicsAnalysis` / `ForensicsSummary`" — confirmed (`src/Vandox.Core/Reports`).
- "`ReportText`" with English and German — confirmed (`src/Vandox.Backend/Reports/ReportText.cs`, `For("en"|"de")`, ordinal).
- "`MarkdownReportWriter`" — confirmed (`src/Vandox.Backend/Reports/MarkdownReportWriter.cs`); it writes in three parts
  (`WriteSummaryAsync`, `WriteIncidentAsync`, `WriteNotesAsync`), and `ForensicsReporter.RunAsync` takes the concrete type
  (`ForensicsReporter.cs:81`), so another consumer needs an interface.
- "it reads every stored line" — confirmed: `ForensicsReporter.ReadLinesAsync` pages all log lines from `StorableTime.Min` to
  `StorableTime.Max` (`ForensicsReporter.cs:115-148`), then every process snapshot.
- "two passes over the incidents, one at a time" — confirmed: `PageIncidentsAsync` with `IncidentPage = 1`, called twice
  (`ForensicsReporter.cs:91` and `:102`).
- "reflects the incidents `vandoxd incidents` last stored" — confirmed: the reporter only reads `IIncidentStore.IncidentsAsync`.
- "All external text is escaped by Razor" — Razor encodes HTML; the web UI additionally makes invisible characters visible through
  `ExternalText` / `LogText` (security area 12). The page uses both.

Related defect found: `docs/areas/detection-and-reports.md` (*Limits of the report*, "A run reads every stored log line and every stored
process snapshot (one query each)") and `README.md` (*Write the forensics report*, same words) are wrong: `RecordPager.PageAsync`
(`src/Vandox.Backend/Incidents/RecordPager.cs:53-65`) sends one query per page (1,000 lines, one snapshot). Fixed in this change (Dev,
*Documentation updates*).

Other constraints found: `ActionRun.Start` fixes every page action's deadline at `LogsView.Deadline` (10 s, `Logs/ActionRun.cs:180`),
which stops any real report; `IIncidentStore` also has `SaveIncidentAsync`, which the web must not reach; a report run keeps up to about
60 MB across the run; circuits are not bounded and the container has 512 MB.

## Acceptance criteria

Fixed texts the tests compare (page chrome is English, like the rest of the web UI; record 0102):

| Name (`ReportsView`) | Text |
| -------------------- | ---- |
| `Title` | `Forensics report` |
| `Running` | `Making the report… It reads every stored log line, which can take a few minutes on a large database.` |
| `RunDeadline` | `Making the report took longer than 2 minutes and was stopped.` |
| `RunFailure` | `The report could not be made. The service log has the details.` |
| `Download` | `Download Markdown` |
| `NextOutages` | `Next outages` |
| `FirstOutages` | `First outages` |
| `OutageRange` | `Outages {0} to {1} of {2}.` |
| address errors (`ReportAddress`) | `The address gives the parameter lang more than once.` and `The address has an invalid value for the parameter lang.` |
| log (`BackendLog`) | `report page: making the report failed` (EventId 52), `report download: making the report failed` (EventId 53), exception only |

- [ ] **AC1 — the page shows the same content as `vandoxd report`.** For the same data, clock, lookback and language, the sequence of
  *pieces* of the page's report element (`article.report`: the text of every `h1`–`h3`, `p`, `th`, `td` and the own text of every `li`,
  tags removed, entities decoded, white space collapsed) equals the sequence of pieces of the Markdown (heading text, paragraph line,
  list item text without its marker, every table cell; separator rows skipped; a code span replaced by its text). Tests:
  `ReportContentTests.ReportContentShowsTheSameContentAsTheMarkdown` (DataRow `en`, `de`; one hand-built sample that has every part:
  detected hour and activity, unknown rules with reasons, the hour table and the "unknown" paragraph variant, both consumer lists and
  the "no snapshot" variant, an outage with kills not kept, unknown growth, consumers, a boot with its last time, and every note:
  the three caps, untracked programs, changed incident count) and
  `ReportPageTests.ReportPageShowsTheReportOfTheStore` (real store with `IncidentSamples`, fixed clock, compared with
  `ForensicsReporter` + `MarkdownReportWriter` over the same store). Helper `ReportPieces` (`FromMarkdown`, `FromHtml`) in the test project.
- [ ] **AC2 — the Markdown download equals the command's output** for the same data and language: the body of
  `GET /report/markdown?lang=<l>` of the running service equals, byte for byte, standard output of `vandoxd report -lang <l>` on the same
  database and clock, UTF-8 without byte order mark; the response has `Content-Type: text/markdown; charset=utf-8`,
  `Content-Disposition: attachment; filename="vandox-report-<l>.md"` and `Cache-Control: no-store`. Test:
  `ReportDownloadEndpointTests.ReportDownloadEndpointWritesTheOutputOfTheReportCommand` (DataRow `en`, `de`; `BackendFixture`,
  `LoginTestClient`).
- [ ] **AC3 — the Markdown bytes do not change** with the rewrite of the writer: the output for one rich hand-built sample equals the golden
  files `testdata/report/forensics-en.md` and `testdata/report/forensics-de.md`, **generated by the Tester from the unchanged writer at
  91f6bde in a scratch worktree** (never from the rewritten one), compared after `\r\n` → `\n`. Test:
  `MarkdownReportWriterTests.MarkdownReportWriterWritesTheGoldenReport` (DataRow `en`, `de`). The sample is the AC1 sample
  (`ReportSamples`, moved from the private helpers of `MarkdownReportWriterTests`).
- [ ] **AC4 — the language comes from the address**: `lang` absent → `en`; exactly `en` or `de` (ordinal, `ReportText.For`) selects the
  table; the key matches ignoring ASCII case and may be percent-encoded, and the value is compared after percent-decoding
  (`QueryHelpers.ParseQuery`), so `lang=%64%65` is `de`; any other value — `DE`, `de-DE`, an empty value, `lang` without `=`, `%20de`,
  `de%20`, `%E2%80%AEde`, `en%00` — is refused with the invalid-value text; a key given twice in any letter case or with an empty value (`lang=en&LANG=en`, `lang=&lang=de`) is refused with the
  repeat text; other keys are ignored; the fragment is never read. A refused address runs nothing. Tests:
  `ReportAddressTests.ReportAddressParseDefaultsToEnglish`, `ReportAddressParseReadsTheLanguage` (DataRow `lang=en`, `lang=de`,
  `LANG=de`, `%6Cang=de`, `lang=%64%65`), `ReportAddressParseRefusesOtherValues` (DataRows above), `ReportAddressParseRefusesARepeatedParameter`,
  `ReportAddressParseIgnoresOtherKeys`, `ReportAddressToQueryWritesTheLanguage`;
  `ReportPageTests.ReportPageShowsTheAddressErrorAndRunsNothing`, `ReportPageRunsAgainWhenTheLanguageChanges` (navigation to
  `report?lang=de` → the German title, a second run);
  `ReportDownloadEndpointTests.ReportDownloadEndpointRefusesAnInvalidLanguage` (`400`, no body, no store call; DataRow `lang=DE`,
  `lang=en&lang=de`).
- [ ] **AC5 — external text is shown only as escaped text**: victim commands and program names reach the page only as Razor text through
  `ExternalText` (runs of `LogText.Runs(text, LogText.MaxShortChars)`) inside a `code` element: `<script>` and `&` are encoded, U+202E
  and U+200B are visible escapes in an `escape` span, a backtick stays a backtick, a text over 2,048 displayed characters ends with
  the `[cut: <n> more characters]` marker; empty external text is the dash `—` as in the Markdown. Tests:
  `ReportContentTests.ReportContentShowsExternalTextOnlyAsEscapedText`, and the existing
  `WebUiContentTests.ComponentsContainNoInlineCodeOrExternalUrls` stays green over the new files (no `MarkupString`, no `style`
  attribute, links only from fixed paths).
- [ ] **AC6 — a deadline on the run**: a page run and a download run end after **2 minutes** (`ReportsView.Deadline`), the wait for the run
  slot included: the store call in flight gets a cancelled token; the page shows `RunDeadline`; the download answers `503` without a body
  when nothing was written yet. A store failure shows `RunFailure` and logs EventId 52 with the exception only; a download that fails
  before writing answers `500` without a body and logs EventId 53; one that fails after the first byte aborts the connection
  (`HttpContext.Abort`), so no truncated file is saved. Tests: `ReportPageTests.ReportPageStopsTheRunAtTheDeadline`,
  `ReportPageShowsAFailureAndLogsIt`; `ReportDownloadEndpointTests.ReportDownloadEndpointAnswers503AtTheDeadline`,
  `ReportDownloadEndpointAnswers500WhenTheStoreFailsBeforeWriting`, `ReportDownloadEndpointAbortsWhenTheStoreFailsAfterWriting`;
  `ActionRunTests.ActionRunUsesTheGivenDeadline`, `PageRunnerTests.PageRunnerUsesTheGivenDeadline`.
- [ ] **AC7 — the page reads through `ForensicsReporter`, one report at a time, through the gate of the web views**: `ReportService`
  runs at most one report across the service (page and download share it; a second waits within its own deadline and stops waiting
  when cancelled); its record and incident reads go through `WebReaderGate` (`GatedRecordReader`, `GatedIncidentStore`), every incident
  query has `Limit` 1, and `GatedIncidentStore.SaveIncidentAsync` throws `NotSupportedException` without calling the store. Tests:
  `ReportServiceTests.ReportServiceRunsOneReportAtATime`, `ReportServiceStopsWaitingForTheRunWhenCancelled`,
  `ReportServiceReadsIncidentsOneAtATime`; `GatedIncidentStoreTests.GatedIncidentStoreReadsThroughTheGate`,
  `GatedIncidentStoreRefusesToSave`.
- [ ] **AC8 — outages in pages of 50**: a run keeps only the outages of its page (`ReportViewBuilder(text, firstOutage, maxOutages)`); the
  summary and notes are those of the whole run (the "changed between passes" note compares the whole count). With more than 50 outages
  the page shows `Outages 1 to 50 of 51.`, *Next outages* runs again and shows outage 51 (`Outages 51 to 51 of 51.`), *First outages*
  goes back; the position is not in the address. Tests: `ReportViewBuilderTests.ReportViewBuilderKeepsOnlyTheOutagesOfThePage` (5 outages,
  first 2, max 2 → the 3rd and 4th, listed 5), `ReportViewBuilderWritesTheSameBlocksAsTheMarkdownWriter`;
  `ReportPageTests.ReportPageShowsTheNextOutagesAfterFifty` (51 stored incidents).
- [ ] **AC9 — behind the login, web listener only**: anonymous `GET /report` and `GET /report/markdown` → `302` to `/login`; on the ingest
  listener both → `404`. Tests: `ReportDownloadEndpointTests.ReportDownloadEndpointNeedsASession`,
  `ReportDownloadEndpointAnswers404OnTheIngestListener`, `ReportPageTests.ReportPageNeedsASession`.
- [ ] **AC10 — navigation and links**: the navigation lists *Overview*, *Logs*, *Incidents*, *Report* (`report`); *Report* is marked on
  `/report` only. The page links `report?lang=en` (*English*) and `report?lang=de` (*Deutsch*) and the download
  `report/markdown?lang=<current>` with the `download` attribute. Tests: `MainLayoutTests.MainLayoutMarksReportOnTheReportPage` (DataRows
  `report` true, `incidents` false, `` false), `ReportPageTests.ReportPageLinksTheLanguagesAndTheDownload`, `ReportsViewTests.ReportsViewHrefsUseOnlyFixedPaths`.
- [ ] **AC11 — prerendered, the page runs nothing**; leaving it cancels the run: tests
  `ReportPageTests.ReportPagePrerenderedShowsTheStatusAndRunsNothing`, `ReportPageCancelsTheRunWhenDisposed`.
- [ ] **AC12 — the consumer templates put the program first**: `ConsumerPermanent` and `ConsumerTransient` of both tables start with `{0}`
  and hold it once (the page writes the program, then the template formatted with an empty `{0}`). Test:
  `ReportTextTests.ReportTextConsumerLinesStartWithTheProgram`.
- [ ] **AC13 — the image contains the page**: a new `.razor` file and the changed `wwwroot/app.css` are image-build inputs; verified by the
  *Image build check* of `.squad/stack.md` (restore-order reproduction), run by the **Dev** before the review, with `ReportPage` in the
  published `vandoxd.dll` (the build succeeds) and `wwwroot/app.css` in the output.

## Behavior coverage

The issue's requirements mapped to the criteria:

| Requirement | Criteria |
| ----------- | -------- |
| A page behind the login that renders the analysis | AC1, AC9, AC10 |
| Outages read one at a time, never all at once | AC7 (store), AC8 (page) |
| English or German per user choice (`ReportText`) | AC4, AC1 (DataRow `de`) |
| Download of the same Markdown (`MarkdownReportWriter`) | AC2, AC3 |
| All external text escaped by Razor | AC5 |
| A deadline on the run | AC6 |
| Reads through `ForensicsReporter`, reflects the stored incidents | AC7, AC1 (store test) |
| Acceptance: page = `vandoxd report` | AC1 |
| Acceptance: download = command output | AC2 |

## Approach

1. **Block model** (`src/Vandox.Backend/Reports`). `ReportComposer` turns the three parts of a run into `ReportBlock` lists — the code of
   `MarkdownReportWriter` today, moved: headings (level 1–3), paragraphs, list items (level 0 or 1) and tables (header row first, a
   right-alignment flag per column). Text is a list of `ReportPiece`s, plain or external; external text appears only in list items and
   paragraphs, never in a heading or table (as today). An empty external text is composed as the plain dash `—`
   (`MarkdownText.Code("")` returns it today). Consumer items are `[Code(program), Plain(Format(template, "", …))]` (AC12).
2. **`MarkdownReportWriter`** keeps its constructor and its three public methods, implements `IReportWriter`, composes and serializes:
   heading `#`×level + space + text + blank line; paragraph text + blank line; list item `"  "`×level + `- ` + text + line feed, and a blank
   line after the last item of a run of items unless it ends the document (the notes); table rows `| a | b |`, the alignment row (`---`
   or `---:`) after the header, then a blank line. Plain pieces as they are, external pieces through `MarkdownText.Code`. This reproduces
   today's bytes; AC3 pins them.
3. **`IReportWriter`**; `ForensicsReporter.RunAsync(IReportWriter, CancellationToken)` (the only change to the reporter).
4. **Page document**: `ReportViewBuilder : IReportWriter` keeps the summary blocks, the blocks of the outages with index in
   `[firstOutage, firstOutage + maxOutages)` and the notes blocks, and counts the outages listed, into a `ReportDocument`.
   `Components/Reports/ReportContent.razor` renders a `ReportDocument` inside `<article class="report">`: `h1`–`h3`, `p`, `ul`/`li` (a level-1
   item nested in the preceding level-0 item's `ul`), `table` with `thead`/`tbody` and class `num` on right-aligned cells; an external piece
   as `<code><ExternalText Runs="…" /></code>`. No `MarkupString`, no `style` attribute.
5. **`ReportService`** (singleton): a `SemaphoreSlim(1)` run slot; `RunAsync(writer, token)` waits for the slot with the token, then runs a
   new `ForensicsReporter` over the gated reader and gated incident store with `BuiltInSignatures.Catalog`, `incidents.window_before` and the
   clock. Wired in `ServeCommand.Build` with `new GatedRecordReader(store, gate)` and `new GatedIncidentStore(store, gate)`.
6. **Deadline for page actions**: `ActionRun`, `ActionSequence` and `PageRunner` get overloads with a `TimeSpan deadline`; the existing
   constructors keep `LogsView.Deadline`.
7. **`ReportPage`** (`/report`): parses `new Uri(Navigation.Uri).Query` with `ReportAddress.Parse`; prerendered it shows `Running` and runs
   nothing; interactive it runs `ReportService.RunAsync(new ReportViewBuilder(text, first, ReportsView.PageOutages), token)` through a
   `PageRunner` with `ReportsView.Deadline`, follows `LocationChanged` (a new language runs again from the first outage), offers *Next
   outages* / *First outages*, the language links and the download link, and shows `OutageRange` when more than 50 outages are listed.
   Disposing cancels the run.
8. **`ReportDownloadEndpoint`** (`GET /report/markdown`, no `AllowAnonymous`): parses the request's query string with `ReportAddress.Parse`
   (`400` without body on an error, before any run); sets the headers of AC2; runs `ReportService` into a `MarkdownReportWriter` over a
   `StreamWriter(Response.Body, new UTF8Encoding(false))` under a `CancellationTokenSource(ReportsView.Deadline, clock)` linked with
   `RequestAborted`; flushes. Failures as AC6: before `Response.HasStarted` → status `503` (deadline) or `500` (store failure, logged), after
   it → `Abort()` (store failure logged; deadline not logged, like the views). A cancellation by the client ends silently.
9. Navigation entry *Report* in `MainLayout.razor`; styles for `article.report` tables and `code` in `wwwroot/app.css` (no `url()`, no
   backslash).

## Options considered

The alternatives and why they lost are in record 0108 (*Options considered*): rendering (block model vs. mirrored Razor markup vs. a
Markdown library with raw HTML vs. a `pre` element), outages (pages of 50 by a new run vs. all in the circuit vs. the analysis kept per
circuit vs. a cut list), language (address vs. cookie or local storage vs. session state), download (GET stream vs. antiforgery form post
vs. a blob built in the circuit) and run limits (one run at a time under 2 minutes vs. the views' 10 s vs. a shared background result).
One more weighed here: a read-only incident interface split off `IIncidentStore` in `Vandox.Storage` instead of `GatedIncidentStore`
refusing `SaveIncidentAsync` — cleaner type-wise, but changes `SqliteStore` and four test doubles for one caller; the refusing decorator
keeps the web away from the write as well, and AC7 pins it.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Backend | `Reports/IReportWriter.cs` | new |
| Vandox.Backend | `Reports/ReportBlockKind.cs`, `Reports/ReportPiece.cs`, `Reports/ReportBlock.cs` | new |
| Vandox.Backend | `Reports/ReportComposer.cs` | new (the composing code moved out of `MarkdownReportWriter`) |
| Vandox.Backend | `Reports/MarkdownReportWriter.cs` | rewritten on the blocks, implements `IReportWriter`; public members unchanged |
| Vandox.Backend | `Reports/ForensicsReporter.cs` | `RunAsync` takes `IReportWriter` |
| Vandox.Backend | `Reports/ReportDocument.cs`, `Reports/ReportViewBuilder.cs` | new |
| Vandox.Backend | `Reports/ReportService.cs`, `Reports/GatedIncidentStore.cs` | new |
| Vandox.Backend | `Reports/ReportAddress.cs`, `Reports/ReportsView.cs`, `Reports/ReportDownloadEndpoint.cs` | new |
| Vandox.Backend | `Components/Reports/ReportContent.razor` (+ `.razor.cs`) | new |
| Vandox.Backend | `Components/Pages/ReportPage.razor` (+ `.razor.cs`) | new |
| Vandox.Backend | `Components/Layout/MainLayout.razor` | *Report* entry |
| Vandox.Backend | `Logs/ActionRun.cs`, `Logs/ActionSequence.cs`, `Logs/PageRunner.cs` | deadline overloads |
| Vandox.Backend | `Hosting/ServeCommand.cs` | register `ReportService`, map the endpoint |
| Vandox.Backend | `Hosting/BackendLog.cs` | EventIds 52, 53 |
| Vandox.Backend | `wwwroot/app.css` | report styles |

Files the skeleton must rewrite: none of the existing files is final as is except `ReportText.cs`, `MarkdownText.cs`, `ReportResult.cs`
(read, unchanged).

## Signatures (for the Dev's skeleton)

All in namespace `Vandox.Backend.Reports` unless noted.

```csharp
public interface IReportWriter
{
    Task WriteSummaryAsync(ForensicsSummary summary);
    Task WriteIncidentAsync(StoredIncident stored, IncidentActivities activities);
    Task WriteNotesAsync(ForensicsSummary summary, long listed);
}

public enum ReportBlockKind { Heading, Paragraph, Item, Table }

public sealed class ReportPiece
{
    public string Text { get; init; } = string.Empty;   // plain text, or external text as stored
    public bool External { get; init; }
    public static ReportPiece Plain(string text);
    public static ReportPiece Code(string text);         // external text; empty text gives Plain("—")
}

public sealed class ReportBlock
{
    public ReportBlockKind Kind { get; init; }
    public int Level { get; init; }                                      // Heading 1-3, Item 0-1, else 0
    public IReadOnlyList<ReportPiece> Pieces { get; init; } = [];        // Heading, Paragraph, Item
    public IReadOnlyList<bool> AlignRight { get; init; } = [];           // Table
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; init; } = []; // Table, header first
}

internal sealed class ReportComposer
{
    internal ReportComposer(ReportText text);
    internal IReadOnlyList<ReportBlock> Summary(ForensicsSummary summary);
    internal IReadOnlyList<ReportBlock> Incident(StoredIncident stored, IncidentActivities activities);
    internal IReadOnlyList<ReportBlock> Notes(ForensicsSummary summary, long listed);
}

public sealed class MarkdownReportWriter : IReportWriter   // constructor and the three methods unchanged
{
    public MarkdownReportWriter(TextWriter writer, ReportText text);
}

// ForensicsReporter (changed parameter type only)
public Task<ReportResult> RunAsync(IReportWriter writer, CancellationToken cancellationToken);

public sealed class ReportDocument
{
    public string Language { get; init; } = string.Empty;
    public IReadOnlyList<ReportBlock> Summary { get; internal set; } = [];
    public IReadOnlyList<IReadOnlyList<ReportBlock>> Outages { get; internal set; } = [];
    public IReadOnlyList<ReportBlock> Notes { get; internal set; } = [];
    public long FirstOutage { get; init; }          // 0-based index of the first outage kept
    public long OutagesListed { get; internal set; } // outages of the second pass
}

public sealed class ReportViewBuilder : IReportWriter
{
    public ReportViewBuilder(ReportText text, long firstOutage, int maxOutages);  // firstOutage >= 0, maxOutages >= 1
    public ReportDocument Document { get; }
}

public sealed class ReportService : IDisposable
{
    public ReportService(IRecordReader records, IIncidentStore incidents, SignatureCatalog catalog, TimeSpan lookback, TimeProvider clock);
    public Task<ReportResult> RunAsync(IReportWriter writer, CancellationToken cancellationToken);
}

public sealed class GatedIncidentStore : IIncidentStore
{
    public GatedIncidentStore(IIncidentStore inner, WebReaderGate gate);
    // SaveIncidentAsync throws NotSupportedException; IncidentsAsync and CountIncidentsAsync through the gate
}

public sealed class ReportAddress
{
    public string Language { get; private set; } = "en";
    public string Error { get; private set; } = string.Empty;
    public static ReportAddress Parse(string query);   // with or without '?'
    public string ToQuery();                           // "?lang=<language>"
}

public static class ReportsView
{
    public const int PageOutages = 50;
    public const string Title, Running, RunDeadline, RunFailure, Download, NextOutages, FirstOutages, OutageRange;  // texts above
    public static readonly TimeSpan Deadline = TimeSpan.FromMinutes(2);
    public static string PageHref(string language);      // "report?lang=<language>"
    public static string DownloadHref(string language);  // "report/markdown?lang=<language>"
    public static string FileName(string language);      // "vandox-report-<language>.md"
}

internal static class ReportDownloadEndpoint
{
    internal const string Pattern = "/report/markdown";
    internal static void Map(IEndpointRouteBuilder endpoints);
    internal static Task DownloadAsync(HttpContext context, ReportService reports, TimeProvider clock, ILoggerFactory loggerFactory);
}

// Vandox.Backend.Components.Reports
public sealed partial class ReportContent : ComponentBase
{
    [Parameter, EditorRequired] public ReportDocument Document { get; set; }
}

// Vandox.Backend.Components.Pages: @page "/report"
public sealed partial class ReportPage : ComponentBase, IDisposable { /* injects ReportService, NavigationManager, TimeProvider, ILogger<ReportPage> */ }

// Vandox.Backend.Logs (new overloads; the old constructors stay and use LogsView.Deadline)
internal ActionRun(TimeProvider clock, TimeSpan deadline);
internal ActionSequence(TimeProvider clock, TimeSpan deadline);
internal PageRunner(TimeProvider clock, Action<Exception> logFailure, TimeSpan deadline, string deadlineText, string failureText, Action<string> fail);

// Vandox.Backend.Hosting.BackendLog
[LoggerMessage(EventId = 52, Level = LogLevel.Error, Message = "report page: making the report failed")]
internal static partial void ReportPageFailed(this ILogger logger, Exception exception);
[LoggerMessage(EventId = 53, Level = LogLevel.Error, Message = "report download: making the report failed")]
internal static partial void ReportDownloadFailed(this ILogger logger, Exception exception);
```

## Test files

In `tests/Vandox.Backend.Tests` (mirroring the class under test):

- new: `ReportContentTests.cs`, `ReportPageTests.cs`, `ReportViewBuilderTests.cs`, `ReportServiceTests.cs`, `GatedIncidentStoreTests.cs`,
  `ReportAddressTests.cs`, `ReportsViewTests.cs`, `ReportDownloadEndpointTests.cs`;
- extended: `MarkdownReportWriterTests.cs` (golden), `ReportTextTests.cs`, `MainLayoutTests.cs`, `ActionRunTests.cs`, `PageRunnerTests.cs`;
- helpers: `ReportSamples.cs` (the hand-built summary and incidents, moved from `MarkdownReportWriterTests`), `ReportPieces.cs`,
  `ReportPageHarness.cs` (renderer, `ReportService` over a store or doubles, `FakeTimeProvider`, `ListLogger`, `RecordingNavigationManager`);
  a record reader that blocks until its token ends is `ScriptedRecordReader` with its `Before` hook;
- fixtures: `testdata/report/forensics-en.md`, `testdata/report/forensics-de.md` (AC3: generated from 91f6bde).

Existing test code that calls a changed signature: none breaks. `ForensicsReporter.RunAsync` widens its parameter (the calls in
`ForensicsReporterTests` pass a `MarkdownReportWriter` and compile); the `ActionRun`, `ActionSequence` and `PageRunner` constructors gain
overloads beside the old ones.

Files of the change no unit test can cover: none (no script; the style sheet is not part of the coverage measure).

## Areas

- **Web UI** (`docs/areas/web-ui.md`): new section *Report page* (address and language, what is shown, outage pages, download endpoint and
  its answers, deadline and the one-run limit, external text); *Layout and navigation* (four entries); *Limits and errors* (behind the
  login, memory per circuit: a document of at most 50 outages, each at most 16 victims and 3 + 3 programs of at most 1,024 bytes, plus
  the summary — a few MiB; a run keeps up to about 60 MB once across the service; cross-site opening starts a bounded run);
  *Related decisions* (0108); *Implementation*. Owner: **Dev**.
- **Detection, alerts and reports** (`docs/areas/detection-and-reports.md`), *Forensics report*: the intro sentence about the page,
  *Limits of the report* ("one query each" fix; the measured run time, see *Security considerations*), *Not here* (drop the page),
  *Implementation* (the new report types). Owner: **Dev**.

## Documentation updates

- `README.md`: a section *Read the forensics report in the web UI* after *Write the forensics report* (page, language links, download,
  2 minutes and one run at a time, `vandoxd report` for larger archives); the "one query each" fix. Owner: **Dev**.
- `.squad/project.md`, *Security areas* 3 (the report page and `GET /report/markdown` behind the login, through the gate, one run at a
  time), 10 (`ReportAddress` and its forms) and 12 (the page shows victim commands and program names only through `ExternalText`/`LogText`
  in `code`; external text in no heading or table cell), records 0108. Owner: **Dev**. *Test doubles*: `ReportPageHarness`, `ReportPieces`,
  `ReportSamples`. Owner: **Tester**.
- `docs/ARCHITECTURE.md`: done by the Lead with this plan (report page in the components and the data flow, record 0108).
- `docs/decisions/0108-…md` and its index row: done by the Lead (Proposed).

## Architecture check

No guarantee of `.squad/project.md` is touched: the page only reads (no gap, no alert, no write; `GatedIncidentStore` refuses the
save). The login stays the boundary (every page `[Authorize]`, the endpoint under the fallback policy). Record 0099 holds: no inline
code, no `MarkupString`, own style sheet only, links from fixed paths. Record 0100 holds: same passes, memory independent of the incident
count (the page keeps at most 50 outages). The gate of record 0107 keeps two reader connections free; the run slot keeps the 60 MB of a
run single across the service.

## Security considerations

- [x] Limits on parsed input: `lang` is compared ordinally with `en` and `de` after the query string is percent-decoded
  (`QueryHelpers.ParseQuery`), so no length limit is needed; the forms are listed in AC4. External text: at most 2,048 **displayed**
  UTF-16 characters (after escaping) per field (`LogText.MaxShortChars`), at most 256 runs; stored texts are at most 1,024 UTF-8 bytes
  (`ModelLimits.MaxShortTextBytes`).
- [x] Exceptions that reach the user: none raw. Page: `RunDeadline` or `RunFailure` (fixed texts) for `OperationCanceledException`,
  `StoreException` and `InvalidOperationException` (`PageRunner`); the download: status `400`, `500`, `503` without body, or an aborted
  connection. The service log gets the exception (EventIds 52, 53), as the views do.
- **Unverified, measured once by the Dev in a scratch copy before the claim goes into the area document or the README:**
  - U1: the time of a whole run on a large database — the 5,000,000-line database of `tools/Vandox.StorageBenchmark` (`docs/BENCHMARKS.md`)
    or the largest at hand, `vandoxd report` timed on the development host; the area document states the number and the host. If it
    exceeds 2 minutes, the Dev reports it and the Lead decides (the deadline or the documented limit).
  - U2: the part of a run the deadline cannot interrupt — `ForensicsAnalysis.Seal`/`Summarize` and the signature detector of one page take
    no token: time `Summarize` on the same database (Stopwatch in scratch). The area document says the run ends "within about <n> s
    after the deadline" only with that number; store statements are interrupted by record 0097 (measured there).
- Cross-site: the session cookie is `SameSite=Lax`, so another site can open `/report` or `/report/markdown` in a signed-in browser and
  start a run; it changes nothing, reveals nothing to that site (a top-level navigation's response is not readable cross-origin; the
  page cannot be framed, 0099), and is bounded by the one-run slot and the deadline (record 0108, option 13).
- Headers of the download: `Content-Disposition` carries only the fixed name of the validated language; `X-Content-Type-Options: nosniff`
  comes from the middleware, so the Markdown is never sniffed as HTML.

## Decision records

- `docs/decisions/0108-report-page-one-block-model-outage-pages-by-rerun-language-in-the-address.md` (Proposed, indexed).
- 0100 and 0102 are unreleased and stay true (0100 names the page as a separate issue and says the page renders the same analysis; 0102
  says the page passes the user's choice to the same tables); no edit.

## Out of scope / follow-ups

- A remembered language preference across visits (record 0108, options 10 and 11).
- A report run in the background with a stored result (option 17).
- An *Overview* page link to the report (the home page text is not changed by this issue).

## Challenge notes

Devil's Advocate verdict: OBJECTIONS 0 major, 2 minor.

**Minor 1: wrong line reference.** The plan says `ActionRun.Start` fixes the deadline "at `LogsView.Deadline` (`Logs/ActionRun.cs:180`)". `src/Vandox.Backend/Logs/ActionRun.cs` has only 152 lines. The deadline is set at `ActionRun.cs:109`, as `new CancellationTokenSource(LogsView.Deadline, _clock)`. The claim itself is true. The Dev should use line 109 as the place to add the deadline overload. `LogsView.Deadline` is defined at `LogsView.cs:119`.

**Minor 2: the page and the command show different content.** The page's own wording says it "shows the same content as `vandoxd report`", but AC8 limits it to 50 outages per page, while the command and the download list all of them. AC1 only compares the page with the Markdown for samples of at most 50 outages. The Dev and Reviewer should state this difference in the area document's *Report page* section, so the issue's "same content" criterion is read as "same content per outage page". The AC1/AC8 split is otherwise consistent, and the issue's "outages are read one at a time" is met by `Limit 1`.

## Security plan review notes

Security verdict: APPROVED (HEAD 10ea4ac), four non-blocking notes for the Dev.

1. **U2 times the wrong method.** The plan says "time `Summarize`", but `Summarize` is 24 rule evaluations plus two top-5 lists. The bulk of the work that cannot be cancelled is `ForensicsAnalysis.Seal` (one pass over every observed hour with binary searches into up to 1,000,000 snapshots; the panel threshold sort in `SnapshotSeries`), plus decoding each stored incident (up to 8 MiB of JSON, 0100 option 7). The Dev times `Seal` plus `Summarize` on the benchmark database before the area document states any "within about n s after the deadline".
2. **"A few MiB" per circuit becomes a computed number**, as the other views state theirs. What the circuit keeps is the `ReportPiece` strings (up to 1 KiB stored text per field) plus the rendered `ExternalText` runs (up to 2,048 UTF-16 characters each): up to 50 outages x (16 victims + 3 + 3 programs), about 6-7 MB plus render-tree overhead, before the summary. `docs/areas/web-ui.md` and `.squad/project.md` area 3 give that number.
3. **Download endpoint (approach step 8).** The writer gets no token; after the deadline the run stops at the next store call, at most one outage section later. A slow client is cut off only by Kestrel's default minimum response data rate (`ServeCommand.cs:259` sets only `RequestHeadersTimeout`, so that default must stay). Flush and dispose the `StreamWriter` asynchronously (`await using` / `FlushAsync`): synchronous I/O is disallowed and a synchronous dispose would throw `InvalidOperationException` after the response has started. On `400`/`500`/`503` set only the status code and never call `Response.Clear()`, which would drop the security headers already set.
4. **Starving the report.** A hostile site can keep sending navigations so the legitimate user's report keeps waiting, up to 2 minutes each time. Availability only; name it in the web UI area document's *Limits and errors*.
