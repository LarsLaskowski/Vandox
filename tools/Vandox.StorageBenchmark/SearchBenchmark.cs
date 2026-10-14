using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;

using Microsoft.Data.Sqlite;

using Vandox.Core;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.StorageBenchmark;

/// <summary>
/// Measures the first page of the logs view over generated log lines (issue #26, criterion AC-19, <c>docs/BENCHMARKS.md</c>).
/// The lines are the lines of <see cref="TemplateBenchmark"/> with a few generated words added, so the words have known shares.
/// </summary>
[ExcludeFromCodeCoverage]
internal static class SearchBenchmark
{
    #region Constants

    /// <summary>
    /// The scan limit of the logs view page, kept equal to <c>LogsView.ScanRows</c> in the backend.
    /// </summary>
    private const int ScanRows = 8000;

    private const int PageLines = 100;
    private const int Runs = 3;
    private const int RarePeriod = 5000;
    private const int RareRemainder = 17;
    private const int PercentPeriod = 100;
    private const int PercentRemainder = 5;
    private const int NearCapPeriod = 300;
    private const int NearCapRemainder = 11;
    private const int CommonPeriod = 3;
    private const int PairPeriod = 1000;
    private const int PairHalf = 500;
    private const int MassWordsPerResidue = 8;
    private const int MassResidues = 2;
    private const int MassPeriod = 4;
    private const int RareProgramPeriod = 20000;
    private const int RareProgramRemainder = 7;
    private const int PriorityCount = 8;
    private const int PriorityStride = 24;
    private const int CommonFreeShare = 15;
    private const int DefaultBatchSize = 10000;
    private const long SecondsInThirtyDays = 30L * 24 * 3600;
    private const int TemplateListLimit = 200;
    private const int TemplateCandidateLimit = 1000;
    private const double CriterionMilliseconds = 2000;
    private const double TuningMilliseconds = 500;
    private const string RareWord = "rarequartz";
    private const string PercentWord = "onepercent";
    private const string NearCapWord = "nearcapword";
    private const string CommonWord = "commonword";
    private const string TogetherPrefix = "tog";
    private const string EveryLineWord = "q";
    private const int TogetherWords = 8;
    private const int CraftedParts = 511;
    private const string PairFirst = "pairalpha";
    private const string PairSecond = "pairbeta";
    private const string MassPrefix = "mass";
    private const string RareProgram = "backupd";
    private const string CommonProgram = "plesk";
    private const string SharedProgram = "dovecot";
    private const string SharedSource = "journal";

    #endregion // Constants

    #region Fields

    private static readonly TimeSpan _pageDeadline = TimeSpan.FromSeconds(10);

    private static readonly DateTimeOffset _start = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Runs the measurement.
    /// </summary>
    /// <param name="directory">The directory for the temporary database</param>
    /// <param name="lines">The number of log lines</param>
    /// <param name="size">The number of lines per batch</param>
    /// <param name="cancellationToken">Cancels the measurement</param>
    /// <returns>A task that completes when the measurement is printed</returns>
    internal static async Task RunAsync(string directory, int lines, int size, CancellationToken cancellationToken)
    {
        var work = Path.Combine(directory, $"vandox-search-benchmark-{Guid.NewGuid():N}");

        Directory.CreateDirectory(work);

        try
        {
            Print($"{RuntimeInformation.OSDescription}; {RuntimeInformation.FrameworkDescription}; {Environment.ProcessorCount} logical CPUs");
            Print($"database below {directory}; {lines} log lines in batches of {size} over 30 days; page of {PageLines} lines, scan limit {ScanRows}, selectivity cap {LogFinder.SelectiveMatches}");

            await WriteAsync(work, lines, size > 0 ? size : DefaultBatchSize, cancellationToken).ConfigureAwait(false);
            PrintShares(work);

            await using var store = await SqliteStore.OpenAsync(work, cancellationToken).ConfigureAwait(false);

            var cases = await BuildCasesAsync(store, cancellationToken).ConfigureAwait(false);
            var slowest = 0D;

            foreach (var item in cases)
            {
                slowest = Math.Max(slowest, await RunCaseAsync(store, work, item, cancellationToken).ConfigureAwait(false));
            }

            PrintVerdict(lines, slowest);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(work, true);
        }
    }

    /// <summary>
    /// Prints a line with the invariant culture.
    /// </summary>
    /// <param name="text">The text</param>
    private static void Print(FormattableString text)
    {
        Console.WriteLine(text.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Prints the verdict against the criterion.
    /// </summary>
    /// <param name="lines">The number of lines</param>
    /// <param name="slowest">The slowest time of all cases in milliseconds</param>
    private static void PrintVerdict(int lines, double slowest)
    {
        var tuning = slowest < TuningMilliseconds ? $"under the {TuningMilliseconds:F0} ms of the development-host tuning target" : $"over the {TuningMilliseconds:F0} ms of the development-host tuning target";

        if (lines == 5000000)
        {
            Print($"slowest case {slowest:F0} ms ({tuning}); criterion of AC-19 (every case under {CriterionMilliseconds:F0} ms): {(slowest < CriterionMilliseconds ? "met" : "NOT met")} on this host; only a run on the reference host (DS918+, SSD volume) counts");
        }
        else
        {
            Print($"slowest case {slowest:F0} ms; no verdict: the criterion is 5000000 log lines");
        }
    }

    /// <summary>
    /// Writes the generated lines into a new database.
    /// </summary>
    /// <param name="folder">The folder of the database</param>
    /// <param name="lines">The number of lines</param>
    /// <param name="size">The number of lines per batch</param>
    /// <param name="cancellationToken">Cancels the write</param>
    /// <returns>A task that completes when the lines are written</returns>
    private static async Task WriteAsync(string folder, int lines, int size, CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();

        await using (var store = await SqliteStore.OpenAsync(folder, cancellationToken).ConfigureAwait(false))
        {
            for (var first = 0; first < lines; first += size)
            {
                await store.WriteBatchAsync(Build(first, Math.Min(size, lines - first), lines), cancellationToken).ConfigureAwait(false);
            }
        }

        total.Stop();
        Print($"wrote {lines} log lines in {total.Elapsed.TotalSeconds:F0} s ({lines / total.Elapsed.TotalSeconds:F0} lines/s); database {new FileInfo(Path.Combine(folder, StorageLimits.FileName)).Length / 1048576} MiB");
    }

    /// <summary>
    /// Prints how many lines hold each generated word and how many lines the database holds.
    /// </summary>
    /// <param name="folder">The folder of the database</param>
    private static void PrintShares(string folder)
    {
        Print($"log lines: {TemplateBenchmark.Count(folder, "SELECT count(*) FROM log_lines")}; templates: {TemplateBenchmark.Count(folder, "SELECT count(*) FROM log_templates")}");

        foreach (var word in new[] { RareWord, PercentWord, NearCapWord, CommonWord, PairFirst, PairSecond, $"{PairFirst} {PairSecond}", $"{MassPrefix}01", $"{MassPrefix}09", $"{TogetherPrefix}01" })
        {
            Print($"lines with '{word}': {TemplateBenchmark.Count(folder, $"SELECT count(*) FROM log_fts WHERE log_fts MATCH '{word.Replace(" ", " AND ", StringComparison.Ordinal)}'")}");
        }

        Print($"lines of program {RareProgram}: {TemplateBenchmark.Count(folder, $"SELECT count(*) FROM log_lines WHERE program = '{RareProgram}'")}; of program {CommonProgram}: {TemplateBenchmark.Count(folder, $"SELECT count(*) FROM log_lines WHERE program = '{CommonProgram}'")}");
    }

    /// <summary>
    /// Builds the cases 1 to 9 and 12 as filters; the cases 8, 10 and 11 need data from the database.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="cancellationToken">Cancels the queries</param>
    /// <returns>The cases in order</returns>
    private static async Task<List<SearchCase>> BuildCasesAsync(SqliteStore store, CancellationToken cancellationToken)
    {
        var start = _start;
        var lastDay = start.AddDays(29);
        var cases = new List<SearchCase>
                    {
                        new("1  rare word", PageFilter(RareWord)),
                        new("2  word in about 1 % of the lines", PageFilter(PercentWord)),
                        new("2b word in about 0.33 % of the lines", PageFilter(NearCapWord)),
                        new("3  word in 33 % of the lines", PageFilter(CommonWord)),
                        new("4  case 3 with program and severity", PageFilter(CommonWord, program: SharedProgram, maxPriority: 3)),
                        new("4b case 3 with a source (index by source)", PageFilter(CommonWord, source: SharedSource)),
                        new("5  two common words, rare combination", PageFilter($"{PairFirst} {PairSecond}")),
                        new("6  program filter only (4 % of the lines)", PageFilter(string.Empty, program: CommonProgram)),
                        new("6b program filter only (rare program)", PageFilter(string.Empty, program: RareProgram)),
                        new("6c program filter only, in one source (rare program)", PageFilter(string.Empty, program: RareProgram, source: SharedSource)),
                        new("7  case 3 in a one-day range in the middle", PageFilter(CommonWord, start.AddDays(15), start.AddDays(16))),
                        new("9  case 3 in a range without the word (scan bound)", PageFilter(CommonWord, lastDay, start.AddDays(31))),
                        new("12 16 common words, empty intersection", PageFilter(string.Join(' ', Enumerable.Range(1, MassWordsPerResidue * MassResidues).Select(index => $"{MassPrefix}{index:D2}")))),
                        new("13 eight common words together, range without them", PageFilter(string.Join(' ', TogetherList()), lastDay, start.AddDays(31))),
                        new("14 four-part term of common parts, never that phrase", PageFilter($"{MassPrefix}04.{MassPrefix}03.{MassPrefix}02.{MassPrefix}01")),
                        new("15 crafted term of 511 parts of a word every line holds", PageFilter(string.Join('.', Enumerable.Repeat(EveryLineWord, CraftedParts))))
                    };

        var templates = await store.TemplatesAsync(TemplateListQuery(TemplateCandidateLimit), cancellationToken).ConfigureAwait(false);

        if (templates.Count > 0)
        {
            cases.Insert(11, new SearchCase("8  lines of the most frequent template", PageFilter(string.Empty, templateId: templates[0].Template.Id)));
            cases.Insert(12, new SearchCase("8b lines of a rare template", PageFilter(string.Empty, templateId: templates[^1].Template.Id)));
        }

        Print($"{templates.Count} templates listed (limit {TemplateCandidateLimit}); most frequent {(templates.Count > 0 ? templates[0].CountInRange : 0)} lines, last listed {(templates.Count > 0 ? templates[^1].CountInRange : 0)} lines");

        var hit = await store.FindLogsAsync(PageFilter(CommonWord, start.AddDays(15), start.AddDays(16)), cancellationToken).ConfigureAwait(false);

        if (hit.Lines.Count > 0)
        {
            cases.Add(new SearchCase("10 context (5 and 5) of a hit", null, hit.Lines[0].Id));
        }

        cases.Add(new SearchCase("11 template list, whole data (limit 200)", null, 0, TemplateListQuery(TemplateListLimit)));
        cases.Add(new SearchCase("11b template list, 30 days (limit 200)", null, 0, TemplateListQuery(TemplateListLimit, start, start.AddDays(31))));

        return cases;
    }

    /// <summary>
    /// Creates a template list query.
    /// </summary>
    /// <param name="limit">The number of templates</param>
    /// <param name="from">The start of the range, the start of the storable range when absent</param>
    /// <param name="to">The end of the range, the end of the storable range when absent</param>
    /// <returns>The query</returns>
    private static TemplateListQuery TemplateListQuery(int limit, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        return new TemplateListQuery
               {
                   From = from ?? StorableTime.Min,
                   To = to ?? StorableTime.Max,
                   Limit = limit
               };
    }

    /// <summary>
    /// Creates the filter of the first page of a search.
    /// </summary>
    /// <param name="text">The search text</param>
    /// <param name="from">The start of the range, the start of the storable range when absent</param>
    /// <param name="to">The end of the range, the end of the storable range when absent</param>
    /// <param name="program">The program</param>
    /// <param name="maxPriority">The highest priority number</param>
    /// <param name="templateId">The template ID, 0 for none</param>
    /// <param name="source">The source</param>
    /// <returns>The filter</returns>
    private static LogFilter PageFilter(string text, DateTimeOffset? from = null, DateTimeOffset? to = null, string program = "", byte? maxPriority = null, long templateId = 0, string source = "")
    {
        return new LogFilter
               {
                   Text = text,
                   Source = source,
                   Program = program,
                   MaxPriority = maxPriority,
                   TemplateId = templateId,
                   From = from ?? StorableTime.Min,
                   To = to ?? StorableTime.Max,
                   Limit = PageLines,
                   ScanLimit = ScanRows
               };
    }

    /// <summary>
    /// Runs a case several times and prints the times.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="folder">The folder of the database</param>
    /// <param name="item">The case</param>
    /// <param name="cancellationToken">Cancels the queries</param>
    /// <returns>The slowest of the runs in milliseconds</returns>
    private static async Task<double> RunCaseAsync(SqliteStore store, string folder, SearchCase item, CancellationToken cancellationToken)
    {
        var times = new List<double>();
        var result = string.Empty;
        var stopped = false;

        for (var run = 0; run < Runs; run++)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            deadline.CancelAfter(_pageDeadline);

            var watch = Stopwatch.StartNew();

            try
            {
                (result, stopped) = await ExecuteAsync(store, item, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();

                result = "stopped by the 10 s deadline of the page";
            }

            watch.Stop();
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        var slowest = times.Max();
        var mode = "-";

        if (item.Filter is not null)
        {
            mode = stopped ? "scan, index check stopped" : await ModeAsync(store, folder, item.Filter, cancellationToken).ConfigureAwait(false);
        }

        Print($"{item.Name,-56} mode {mode,-26} {result,-40} first {times[0]:F0} ms, runs {string.Join(", ", times.Select(time => time.ToString("F0", CultureInfo.InvariantCulture)))} ms: {(slowest < CriterionMilliseconds ? "under" : "OVER")} {CriterionMilliseconds:F0} ms");

        return slowest;
    }

    /// <summary>
    /// Runs the call of a case once.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="item">The case</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A short description of the result and whether the index check of a search was stopped by its budget</returns>
    private static async Task<(string Result, bool Stopped)> ExecuteAsync(SqliteStore store, SearchCase item, CancellationToken cancellationToken)
    {
        if (item.Filter is not null)
        {
            var page = await store.FindLogsAsync(item.Filter, cancellationToken).ConfigureAwait(false);

            return ($"{page.Lines.Count} lines{(page.ScanLimitReached ? ", scan bound or page budget reached" : string.Empty)}", page.IndexCheckStopped);
        }

        if (item.Templates is not null)
        {
            return ($"{(await store.TemplatesAsync(item.Templates, cancellationToken).ConfigureAwait(false)).Count} templates", false);
        }

        var query = new LogContextQuery
                    {
                        RecordId = item.RecordId,
                        Before = 5,
                        After = 5
                    };
        var context = await store.LogContextAsync(query, cancellationToken).ConfigureAwait(false);

        return ($"{context.Before.Count} before, {context.After.Count} after", false);
    }

    /// <summary>
    /// Finds out which way the search of a filter runs: the selectivity probe counts the matching lines up to the cap.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="folder">The folder of the database</param>
    /// <param name="filter">The filter</param>
    /// <param name="cancellationToken">Cancels the probe</param>
    /// <returns><c>index</c>, <c>scan</c> or <c>scan*</c> for a search without text</returns>
    private static async Task<string> ModeAsync(SqliteStore store, string folder, LogFilter filter, CancellationToken cancellationToken)
    {
        var template = filter.TemplateId > 0 ? await store.TemplateAsync(filter.TemplateId, cancellationToken).ConfigureAwait(false) : null;
        var text = template is null ? filter.Text : $"{filter.Text} {TemplateTerms.SearchText(template.Template)}".Trim();

        if (text.Length == 0)
        {
            return "scan";
        }

        var match = FtsQuery.Build(text);
        var count = TemplateBenchmark.Count(folder, $"SELECT count(*) FROM (SELECT rowid FROM log_fts WHERE log_fts MATCH '{match.Replace("'", "''", StringComparison.Ordinal)}' LIMIT {LogFinder.SelectiveMatches})");

        return count < LogFinder.SelectiveMatches ? "index" : "scan";
    }

    /// <summary>
    /// Builds a batch of generated log lines.
    /// </summary>
    /// <param name="first">The index of the first line</param>
    /// <param name="count">The number of lines</param>
    /// <param name="lines">The number of lines of the whole run</param>
    /// <returns>The batch</returns>
    private static RecordBatch Build(int first, int count, int lines)
    {
        var records = new DataRecord[count];

        for (var offset = 0; offset < count; offset++)
        {
            var index = first + offset;
            var (source, program, message) = TemplateBenchmark.Line(index, lines);

            records[offset] = new DataRecord
                              {
                                  Origin = RecordOrigin.Import,
                                  Source = source,
                                  CapturedAt = _start.AddTicks(index * SecondsInThirtyDays / lines * TimeSpan.TicksPerSecond),
                                  Data = new LogLine
                                         {
                                             Log = source,
                                             Host = "web-1",
                                             Program = index % RareProgramPeriod == RareProgramRemainder ? RareProgram : program,
                                             Priority = (byte)(index / PriorityStride % PriorityCount),
                                             Message = $"{message}{Words(index, lines)}"
                                         }
                              };
        }

        return new RecordBatch
               {
                   ReceivedAt = _start.AddDays(31),
                   Records = records
               };
    }

    /// <summary>
    /// Creates the generated words of a line, each with its own share of the lines: a rare word, a word in 1 %, a word in a third of a
    /// percent (below the selectivity cap), a word in a third of the lines (absent in the last two of the 30 days), two words in half of
    /// the lines each that meet in one line of a thousand, and sixteen words in a quarter of the lines each of which no line holds all.
    /// </summary>
    /// <param name="index">The index of the line</param>
    /// <param name="lines">The number of lines</param>
    /// <returns>The words, each with a leading space</returns>
    private static string Words(int index, int lines)
    {
        var words = new List<string>();
        var phase = index % PairPeriod;
        var massResidue = index % MassPeriod;

        AddIf(words, index % RarePeriod == RareRemainder, RareWord);
        AddIf(words, index % PercentPeriod == PercentRemainder, PercentWord);
        AddIf(words, index % NearCapPeriod == NearCapRemainder, NearCapWord);

        var common = index % CommonPeriod == 0 && index < lines - (lines / CommonFreeShare);

        AddIf(words, common, CommonWord);

        if (common)
        {
            words.AddRange(TogetherList());
        }

        words.Add(EveryLineWord);
        AddIf(words, phase < PairHalf, PairFirst);
        AddIf(words, phase >= PairHalf - 1, PairSecond);

        if (massResidue < MassResidues)
        {
            words.AddRange(Enumerable.Range(1, MassWordsPerResidue).Select(number => string.Create(CultureInfo.InvariantCulture, $"{MassPrefix}{(massResidue * MassWordsPerResidue) + number:D2}")));
        }

        return string.Concat(words.Select(word => $" {word}"));
    }

    /// <summary>
    /// Lists the eight words that occur together in the lines of the common word.
    /// </summary>
    /// <returns>The words</returns>
    private static List<string> TogetherList()
    {
        return Enumerable.Range(1, TogetherWords).Select(number => string.Create(CultureInfo.InvariantCulture, $"{TogetherPrefix}{number:D2}")).ToList();
    }

    /// <summary>
    /// Adds a word when a condition holds.
    /// </summary>
    /// <param name="words">The words</param>
    /// <param name="condition">The condition</param>
    /// <param name="word">The word</param>
    private static void AddIf(List<string> words, bool condition, string word)
    {
        if (condition)
        {
            words.Add(word);
        }
    }

    #endregion // Methods
}