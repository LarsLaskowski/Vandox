namespace Vandox.Storage;

/// <summary>
/// Reads the lines of a query: raw values when the range allows it, else the rollup steps of the ladder.
/// </summary>
internal static class SeriesLineReader
{
    #region Methods

    /// <summary>
    /// Reads the lines at the resolution the range and the target allow.
    /// </summary>
    /// <param name="reader">The store reads</param>
    /// <param name="policy">The retention policy</param>
    /// <param name="series">The distinct series in query order</param>
    /// <param name="range">The range</param>
    /// <param name="cancellationToken">Cancels the reads</param>
    /// <returns>A task that returns the lines</returns>
    internal static async Task<SeriesData> ReadAsync(ITimeSeriesReader reader, RetentionPolicy policy, IReadOnlyList<SeriesKey> series, SeriesRange range, CancellationToken cancellationToken)
    {
        if (policy.Raw is null || range.From >= range.Now - policy.Raw.Value)
        {
            var raw = await TryRawAsync(reader, series, range, cancellationToken).ConfigureAwait(false);

            if (raw is not null)
            {
                return raw;
            }
        }

        return await ReadStepsAsync(reader, series, range, SeriesLadder.Choose(range, policy), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the raw values of every series, one distinct time more than the target at most, and gives up when the union of the times exceeds the target.
    /// </summary>
    /// <param name="reader">The store reads</param>
    /// <param name="series">The series</param>
    /// <param name="range">The range</param>
    /// <param name="cancellationToken">Cancels the reads</param>
    /// <returns>A task that returns the lines, or <c>null</c> when the values do not fit the target</returns>
    private static async Task<SeriesData?> TryRawAsync(ITimeSeriesReader reader, IReadOnlyList<SeriesKey> series, SeriesRange range, CancellationToken cancellationToken)
    {
        var pages = new List<(SeriesKey Key, LinePage Page)>();
        var union = new HashSet<long>();

        foreach (var key in series)
        {
            var page = await reader.RawValuesAsync(new RawValueQuery
                                                   {
                                                       Source = key.Source,
                                                       Name = key.Name,
                                                       From = range.From,
                                                       To = range.End,
                                                       MaxTimes = range.Target + 1,
                                                       MaxLines = TimeSeriesService.MaxLines
                                                   },
                                                   cancellationToken).ConfigureAwait(false);

            union.UnionWith(page.Lines.SelectMany(line => line.Points).Select(point => point.At.UtcTicks));
            pages.Add((key, page));
            cancellationToken.ThrowIfCancellationRequested();

            if (page.TimesExceeded || union.Count > range.Target)
            {
                return null;
            }
        }

        var (lines, cut) = Cap(pages);

        return new SeriesData
               {
                   Raw = true,
                   Lines = lines,
                   Cut = cut
               };
    }

    /// <summary>
    /// Reads the rollup steps of every series and the completeness time.
    /// </summary>
    /// <param name="reader">The store reads</param>
    /// <param name="series">The series</param>
    /// <param name="range">The range</param>
    /// <param name="step">The step</param>
    /// <param name="cancellationToken">Cancels the reads</param>
    /// <returns>A task that returns the lines</returns>
    private static async Task<SeriesData> ReadStepsAsync(ITimeSeriesReader reader, IReadOnlyList<SeriesKey> series, SeriesRange range, TimeSpan step, CancellationToken cancellationToken)
    {
        var tier = step < TimeSpan.FromHours(1) ? RollupTier.FiveMinutes : RollupTier.Hour;
        var pages = new List<(SeriesKey Key, LinePage Page)>();

        foreach (var key in series)
        {
            var page = await reader.RollupStepsAsync(new RollupStepQuery
                                                     {
                                                         Tier = tier,
                                                         Source = key.Source,
                                                         Name = key.Name,
                                                         From = range.From,
                                                         To = range.End,
                                                         Step = step,
                                                         MaxLines = TimeSeriesService.MaxLines
                                                     },
                                                     cancellationToken).ConfigureAwait(false);

            pages.Add((key, page));
            cancellationToken.ThrowIfCancellationRequested();
        }

        var earliest = await reader.EarliestUnfoldedAsync(cancellationToken).ConfigureAwait(false);
        var (lines, cut) = Cap(pages);

        return new SeriesData
               {
                   Step = step,
                   Axis = Axis(range, step),
                   Lines = lines,
                   Cut = cut,
                   CompleteUntil = SeriesTime.Earlier(range.Now, earliest)
               };
    }

    /// <summary>
    /// Lists the starts of the steps that overlap the range.
    /// </summary>
    /// <param name="range">The range</param>
    /// <param name="step">The step</param>
    /// <returns>The starts as UTC ticks</returns>
    private static List<long> Axis(SeriesRange range, TimeSpan step)
    {
        var first = SeriesTime.FloorDiv(range.From.UtcTicks - SeriesTime.EpochTicks, step.Ticks);
        var count = SeriesTime.Overlapping(range.From.UtcTicks - SeriesTime.EpochTicks, range.End.UtcTicks - SeriesTime.EpochTicks, step.Ticks);

        return Enumerable.Range(0, (int)count).Select(index => ((first + index) * step.Ticks) + SeriesTime.EpochTicks).ToList();
    }

    /// <summary>
    /// Keeps the first 64 lines of all series in series order and names the series that lost lines.
    /// </summary>
    /// <param name="pages">The lines of each series</param>
    /// <returns>The kept lines and the cut series</returns>
    private static (List<(SeriesKey Key, MetricLine Line)> Lines, List<SeriesKey> Cut) Cap(List<(SeriesKey Key, LinePage Page)> pages)
    {
        var lines = new List<(SeriesKey Key, MetricLine Line)>();
        var cut = new List<SeriesKey>();

        foreach (var (key, page) in pages)
        {
            var room = TimeSeriesService.MaxLines - lines.Count;

            lines.AddRange(page.Lines.Take(room).Select(line => (key, line)));

            if (page.LinesCut || page.Lines.Count > room)
            {
                cut.Add(key);
            }
        }

        return (lines, cut);
    }

    #endregion // Methods
}